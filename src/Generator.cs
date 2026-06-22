using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace fluke;

public class FlukeProgram(Type programType) {
	private readonly Type programType = programType;

	public void Run() {
		programType.GetMethod("Main")!.Invoke(null, null);
	}
}

internal record EnumVariantInfo(string name, ExpressionType.Struct? payload);

internal abstract record ExpressionType {
	private record IntType : ExpressionType;

	private record BoolType : ExpressionType;

	private record StringType : ExpressionType;

	private record UnitType : ExpressionType;

	private record NeverType : ExpressionType;

	public record Struct(Type clrType, IReadOnlyDictionary<string, ExpressionType> fields) : ExpressionType;

	public record Enum(Type initialClrType, IReadOnlyList<EnumVariantInfo> initialVariants) : ExpressionType {
		public Type clrType { get; private set; } = initialClrType;
		public IReadOnlyList<EnumVariantInfo> variants { get; private set; } = initialVariants;

		public void Complete(Type completedClrType, IReadOnlyList<EnumVariantInfo> completedVariants) {
			clrType = completedClrType;
			variants = completedVariants;
		}
	}

	public static readonly ExpressionType Int = new IntType();
	public static readonly ExpressionType Bool = new BoolType();
	public static readonly ExpressionType String = new StringType();
	public static readonly ExpressionType Unit = new UnitType();
	public static readonly ExpressionType Never = new NeverType();

	public sealed override string ToString() =>
		this switch {
			IntType => "int",
			BoolType => "bool",
			StringType => "string",
			UnitType => "unit",
			NeverType => "never",
			Struct(var t, _) => t.Name,
			Enum enumType => enumType.clrType.Name,
			_ => throw new UnreachableException(),
		};

	public Type AsType() =>
		this switch {
			IntType => typeof(int),
			BoolType => typeof(bool),
			StringType => typeof(string),
			UnitType => typeof(void),
			Struct(var t, _) => t,
			Enum enumType => enumType.clrType,
			_ => throw new UnreachableException(),
		};
}

internal abstract record Symbol {
	public record Variable(ExpressionType type, LocalBuilder? local) : Symbol;

	public record Parameter(int index, ExpressionType type) : Symbol;

	public record Function(MethodBuilder method, ExpressionType returnType, ExpressionType[] parameterTypes) : Symbol;

	public record StructType(ExpressionType.Struct type) : Symbol;

	public record EnumType(ExpressionType.Enum type) : Symbol;
}

internal class Scope {
	private readonly Dictionary<string, Symbol> symbols = new();
	public bool IsFunctionBoundary { get; init; }

	public Symbol? Get(string name) =>
		symbols.GetValueOrDefault(name);

	public void Define(string name, Symbol symbol) =>
		symbols[name] = symbol;

	public bool Has(string name) =>
		symbols.ContainsKey(name);
}

public class Generator {
	private MethodBuilder currentMethod;
	private ExpressionType currentReturnType = ExpressionType.Unit;
	private readonly Stack<Scope> scopes = new();
	private readonly Dictionary<string, Symbol.Function> functions = new();
	private readonly Dictionary<string, ExpressionType.Struct> structTypes = new();
	private readonly Dictionary<string, ExpressionType.Enum> enumTypes = new();
	private readonly Stack<string> nameStack = new();

	public FlukeProgram GenerateInMemory(Ast ast) {
		var asmName = new AssemblyName("Fluke");
		var builder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
		var mod = builder.DefineDynamicModule("Fluke");
		var (_, mainMethod) = GenerateCore(mod, ast);

		var peBytes = BuildPeBytes(builder, mainMethod);
		ILVerifier.Verify(peBytes);

		var assembly = Assembly.Load(peBytes);
		return new FlukeProgram(assembly.GetType("Fluke.Program")!);
	}

	public void GenerateToFile(Ast ast, string outputPath) {
		var asmName = new AssemblyName(Path.GetFileNameWithoutExtension(outputPath));
		var builder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
		var mod = builder.DefineDynamicModule(asmName.Name!);
		var (_, mainMethod) = GenerateCore(mod, ast);

		var peBytes = BuildPeBytes(builder, mainMethod);
		ILVerifier.Verify(peBytes);

		File.WriteAllBytes(outputPath, peBytes);
	}

	private byte[] BuildPeBytes(PersistedAssemblyBuilder builder, MethodBuilder entryPoint) {
		var metadataBuilder = builder.GenerateMetadata(out var ilStream, out var fieldData);
		var entryPointToken = MetadataTokens.MethodDefinitionHandle(entryPoint.MetadataToken);

		var peHeaderBuilder = new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage);
		var peBuilder = new ManagedPEBuilder(
			header: peHeaderBuilder,
			metadataRootBuilder: new MetadataRootBuilder(metadataBuilder),
			ilStream: ilStream,
			mappedFieldData: fieldData,
			entryPoint: entryPointToken);

		var peBlob = new BlobBuilder();
		peBuilder.Serialize(peBlob);

		return peBlob.ToArray();
	}

	private (Type type, MethodBuilder mainMethod) GenerateCore(ModuleBuilder mod, Ast ast) {
		var type = mod.DefineType("Fluke.Program", TypeAttributes.Public | TypeAttributes.Class);
		var mainMethod = type.DefineMethod("Main", MethodAttributes.Public | MethodAttributes.Static, typeof(void),
			Type.EmptyTypes);
		currentMethod = mainMethod;

		scopes.Push(new Scope());

		// Pass 1: register all function and struct signatures (recursive)
		CollectSymbols(ast.statements, type, "", mod);
		PreDefineSymbols(ast.statements);

		// Pass 2: generate code
		var il = currentMethod.GetILGenerator();
		foreach (var statement in ast.statements) {
			BuildStatement(statement);
		}

		il.Emit(OpCodes.Ret);

		return (type.CreateType(), mainMethod);
	}

	private void CollectSymbols(IEnumerable<Statement> statements, TypeBuilder type, string prefix, ModuleBuilder mod) {
		var stmtList = statements.ToList();

		// Sub-pass 1: register all struct TypeBuilders so names resolve for forward references
		var pendingStructs = new Dictionary<string, (TypeBuilder builder, (string name, string type)[] fields)>();
		foreach (var stmt in stmtList) {
			if (stmt is not Statement.StructDeclaration(var sName, var sFields))
				continue;
			var sb = mod.DefineType(
				sName,
				TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout,
				typeof(ValueType)
			);
			structTypes[sName] = new ExpressionType.Struct(sb, new Dictionary<string, ExpressionType>());
			pendingStructs[sName] = (sb, sFields.Select(f => (f.Item1, f.Item2)).ToArray());
		}

		// Sub-pass 1b: pre-register enum TypeBuilders and per-variant payload struct TypeBuilders.
		// Placeholder ExpressionType.Enum entries allow ParseType to resolve enum names while
		// structs are being finalized.
		var pendingEnums =
			new Dictionary<string, (TypeBuilder builder, EnumVariant[] variants,
				Dictionary<string, TypeBuilder> payloads)>();
		foreach (var stmt in stmtList) {
			if (stmt is not Statement.EnumDeclaration(var eName, var eVariants))
				continue;
			var eb = mod.DefineType(
				eName,
				TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.ExplicitLayout,
				typeof(ValueType)
			);
			var payloads = new Dictionary<string, TypeBuilder>();
			foreach (var variant in eVariants) {
				if (variant is not EnumVariant.Struct(var vName, _))
					continue;
				payloads[vName] = mod.DefineType(
					eName + "$" + vName,
					TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout,
					typeof(ValueType)
				);
			}

			enumTypes[eName] = new ExpressionType.Enum(eb, Array.Empty<EnumVariantInfo>());
			pendingEnums[eName] = (eb, eVariants, payloads);
		}

		// Sub-pass 2: define fields and finalize structs in dependency order
		var finalized = new HashSet<string>();
		var visitingSet = new HashSet<string>();
		var visitingPath = new List<string>();

		foreach (var name in pendingStructs.Keys)
			FinalizeStruct(name);

		// Sub-pass 3: finalize enum payload structs (with ref-type validation), then enum types.
		foreach (var (eName, (eb, variants, payloads)) in pendingEnums) {
			var variantInfos = new List<EnumVariantInfo>(variants.Length);
			foreach (var variant in variants) {
				if (variant is EnumVariant.Struct(var vName, var vFields)) {
					var pb = payloads[vName];
					var fieldTypes = new Dictionary<string, ExpressionType>();
					foreach (var (fName, fTypeName) in vFields) {
						var fieldType = ParseType(fTypeName);
						AssertNoReferenceTypes(fieldType, eName, vName, fName, new HashSet<string>());
						pb.DefineField(fName, fieldType.AsType(), FieldAttributes.Public);
						fieldTypes[fName] = fieldType;
					}

					var payloadType = pb.CreateType();
					variantInfos.Add(new EnumVariantInfo(vName,
						new ExpressionType.Struct(payloadType, fieldTypes)));
				}
				else {
					variantInfos.Add(new EnumVariantInfo(variant.name, null));
				}
			}

			var tagField = eb.DefineField(ENUM_TAG_FIELD_NAME, typeof(int), FieldAttributes.Public);
			tagField.SetOffset(0);
			foreach (var info in variantInfos) {
				if (info.payload is null)
					continue;
				var payloadField = eb.DefineField(info.name, info.payload.clrType, FieldAttributes.Public);
				payloadField.SetOffset(4);
			}

			var createdEnum = eb.CreateType();
			enumTypes[eName].Complete(createdEnum, variantInfos);
		}

		// Main pass: register functions and recurse into blocks
		foreach (var stmt in stmtList) {
			switch (stmt) {
				case Statement.FunctionDeclaration decl: {
					var mangledName = prefix == "" ? decl.name : prefix + "$" + decl.name;
					var paramTypes = decl.parameters.Select(p => ParseType(p.Item2).AsType()).ToArray();
					var returnType = ParseType(decl.returnType);
					var mb = type.DefineMethod(
						mangledName,
						MethodAttributes.Public | MethodAttributes.Static,
						returnType.AsType(),
						paramTypes
					);
					var parameterTypes = decl.parameters.Select(p => ParseType(p.Item2)).ToArray();
					functions[mangledName] = new Symbol.Function(mb, returnType, parameterTypes);
					CollectSymbols(decl.body.body, type, mangledName, mod);
					break;
				}
				case Statement.StructDeclaration:
				case Statement.EnumDeclaration:
					break; // handled in sub-passes above
				case Statement.ExpressionStatement(Expression.Block(var body, _)):
					CollectSymbols(body, type, prefix, mod);
					break;
				case Statement.VariableBinding(_, Expression.Block(var body, _)):
					CollectSymbols(body, type, prefix, mod);
					break;
			}
		}

		return;

		void FinalizeStruct(string name) {
			if (finalized.Contains(name))
				return;
			if (!visitingSet.Add(name)) {
				var cycleStart = visitingPath.IndexOf(name);
				var cycle = visitingPath.Skip(cycleStart).Append(name);
				throw new CompileException($"Cyclic struct dependency: {string.Join(" -> ", cycle)}");
			}

			visitingPath.Add(name);
			var (sb, fields) = pendingStructs[name];
			foreach (var (_, fieldTypeName) in fields)
				if (pendingStructs.ContainsKey(fieldTypeName))
					FinalizeStruct(fieldTypeName);
			var fieldTypes = new Dictionary<string, ExpressionType>();
			foreach (var (fieldName, fieldTypeName) in fields) {
				var fieldType = ParseType(fieldTypeName);
				sb.DefineField(fieldName, fieldType.AsType(), FieldAttributes.Public);
				fieldTypes[fieldName] = fieldType;
			}

			var createdType = sb.CreateType();
			structTypes[name] = new ExpressionType.Struct(createdType, fieldTypes);
			visitingPath.RemoveAt(visitingPath.Count - 1);
			visitingSet.Remove(name);
			finalized.Add(name);
		}
	}

	private static void AssertNoReferenceTypes(ExpressionType t, string enumName, string variantName,
		string fieldPath, HashSet<string> visiting) {
		switch (t) {
			case ExpressionType.Struct(var clrType, var fields): {
				var key = clrType.FullName ?? clrType.Name;
				if (!visiting.Add(key))
					return;
				foreach (var (fName, fType) in fields)
					AssertNoReferenceTypes(fType, enumName, variantName, fieldPath + "." + fName, visiting);
				visiting.Remove(key);
				return;
			}
			case ExpressionType.Enum:
				throw new CompileException(
					$"Enum variant '{enumName}.{variantName}' field '{fieldPath}': nested enum payloads are not supported");
			default:
				if (t == ExpressionType.Int || t == ExpressionType.Bool)
					return;
				throw new CompileException(
					$"Enum variant '{enumName}.{variantName}' field '{fieldPath}' has disallowed type '{t}' (reference types are not allowed in variants)");
		}
	}

	private string MangledName(string name) {
		return nameStack.Count == 0 ? name : nameStack.Peek() + "$" + name;
	}

	// Pre-populate the current scope with all functions and structs declared directly in
	// `statements` so that forward references within the same body work.
	private void PreDefineSymbols(IEnumerable<Statement> statements) {
		foreach (var stmt in statements) {
			switch (stmt) {
				case Statement.FunctionDeclaration(var name, _, _, _):
					if (functions.TryGetValue(MangledName(name), out var fn))
						scopes.Peek().Define(name, fn);
					break;
				case Statement.StructDeclaration(var name, _):
					if (structTypes.TryGetValue(name, out var st))
						scopes.Peek().Define(name, new Symbol.StructType(st));
					break;
				case Statement.EnumDeclaration(var name, _):
					if (enumTypes.TryGetValue(name, out var en))
						scopes.Peek().Define(name, new Symbol.EnumType(en));
					break;
			}
		}
	}

	private ExpressionType ParseType(string name) =>
		name switch {
			"int" => ExpressionType.Int,
			"bool" => ExpressionType.Bool,
			"string" => ExpressionType.String,
			"unit" => ExpressionType.Unit,
			_ => structTypes.TryGetValue(name, out var st) ? st
				: enumTypes.TryGetValue(name, out var en) ? en
				: throw new CompileException($"Unknown type '{name}'"),
		};

	private bool BuildStatement(Statement statement) {
		var il = currentMethod.GetILGenerator();
		switch (statement) {
			case Statement.VariableBinding(var name, var expression): {
				var type = BuildExpression(expression);
				LocalBuilder? local = null;
				if (type != ExpressionType.Unit) {
					local = il.DeclareLocal(type.AsType());
					il.Emit(OpCodes.Stloc, local);
				}

				Debug.Assert(type == ExpressionType.Unit || local != null);
				Define(name, new Symbol.Variable(type, local));
				break;
			}
			case Statement.FunctionDeclaration(var name, var parameters, var body, _): {
				var mangledName = MangledName(name);
				var fn = functions[mangledName];
				Define(name, fn);

				var savedMethod = currentMethod;
				currentMethod = fn.method;
				var savedReturnType = currentReturnType;
				currentReturnType = fn.returnType;
				nameStack.Push(mangledName);

				scopes.Push(new Scope { IsFunctionBoundary = true });
				for (var i = 0; i < parameters.Length; i++) {
					var (pName, pType) = parameters[i];
					scopes.Peek().Define(pName, new Symbol.Parameter(i, ParseType(pType)));
				}

				PreDefineSymbols(body.body);

				var fnIl = currentMethod.GetILGenerator();
				var returned = false;
				foreach (var stmt in body.body) {
					if (returned)
						break;
					returned = BuildStatement(stmt);
				}

				if (!returned) {
					if (body.tail != null)
						BuildExpression(body.tail);
					fnIl.Emit(OpCodes.Ret);
				}

				scopes.Pop();
				nameStack.Pop();
				currentMethod = savedMethod;
				currentReturnType = savedReturnType;
				break;
			}
			case Statement.Assignment(var left, var expression): {
				switch (left) {
					case Expression.Identifier(var name): {
						var type = BuildExpression(expression);
						var symbol = Resolve(name);

						if (symbol is null)
							throw new CompileException($"Use of undeclared identifier '{name}'");

						if (symbol is not Symbol.Variable var)
							throw new CompileException($"Cannot assign to '{name}'. Must be a variable.");

						if (var.type != type)
							throw new CompileException(
								$"Type mismatch. Can't assign value of type '{type}' to variable of type '{var.type}'"
							);

						Debug.Assert(type == ExpressionType.Unit || var.local != null);
						if (type != ExpressionType.Unit)
							il.Emit(OpCodes.Stloc, var.local);
						break;
					}
					case Expression.FieldAccess(var container, var field): {
						var containerType = EmitAddressOf(container);

						if (containerType is not ExpressionType.Struct structType)
							throw new CompileException(
								$"Cannot access field '{field}' on non-struct type '{containerType}'"
							);

						if (!structType.fields.TryGetValue(field, out var targetType))
							throw new CompileException($"No field named '{field}' on type '{structType}'");

						var valueType = BuildExpression(expression);
						if (valueType != targetType)
							throw new CompileException(
								$"Type mismatch. Can't assign value of type '{valueType}' to field '{field}' of type '{targetType}'"
							);

						il.Emit(OpCodes.Stfld, structType.clrType.GetField(field)!);
						break;
					}
					default:
						throw new CompileException($"Invalid assignment target: {left}");
				}

				break;
			}
			case Statement.StructDeclaration(var name, _): {
				Define(name, new Symbol.StructType(structTypes[name]));
				break;
			}
			case Statement.EnumDeclaration(var name, _): {
				Define(name, new Symbol.EnumType(enumTypes[name]));
				break;
			}
			case Statement.ExpressionStatement(var expr): {
				var type = BuildExpression(expr);
				if (type != ExpressionType.Unit && type != ExpressionType.Never)
					il.Emit(OpCodes.Pop);
				return type == ExpressionType.Never;
			}
			case Statement.Return(var expr): {
				var type = expr != null ? BuildExpression(expr) : ExpressionType.Unit;
				if (type != currentReturnType)
					throw new CompileException($"Return type mismatch: expected '{currentReturnType}', got '{type}'");
				il.Emit(OpCodes.Ret);
				return true;
			}
			default:
				throw new CompileException($"Statement not supported: {statement}");
		}

		return false;
	}

	private ExpressionType BuildExpression(Expression expression) {
		var il = currentMethod.GetILGenerator();

		switch (expression) {
			case Expression.StringLiteral(var value): {
				il.Emit(OpCodes.Ldstr, value);
				return ExpressionType.String;
			}
			case Expression.IntLiteral(var value): {
				il.Emit(OpCodes.Ldc_I4, value);
				return ExpressionType.Int;
			}
			case Expression.BoolLiteral(var value): {
				il.Emit(value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
				return ExpressionType.Bool;
			}
			case Expression.BinaryOp(var left, var op, var right): {
				var leftType = BuildExpression(left);
				var rightType = BuildExpression(right);

				var expectedType = op switch {
					BinOp.And or BinOp.Or => ExpressionType.Bool,
					_ => ExpressionType.Int,
				};
				if (op is BinOp.Eq or BinOp.Neq) {
					if (leftType != rightType)
						throw new CompileException(
							$"Type mismatch. Can't call operator '{op}' on operands of different types '{leftType}' and '{rightType}'"
						);
				}
				else if (leftType != expectedType || rightType != expectedType) {
					throw new CompileException(
						$"Type mismatch. Can't call operator '{op}' on operands of different types '{leftType}' and '{rightType}'"
					);
				}

				if (op is BinOp.Eq or BinOp.Neq) {
					EmitEqualityFromStack(leftType);
					if (op == BinOp.Neq) {
						il.Emit(OpCodes.Ldc_I4_0);
						il.Emit(OpCodes.Ceq);
					}

					return ExpressionType.Bool;
				}

				switch (op) {
					case BinOp.Add:
						il.Emit(OpCodes.Add);
						break;
					case BinOp.Sub:
						il.Emit(OpCodes.Sub);
						break;
					case BinOp.Mul:
						il.Emit(OpCodes.Mul);
						break;
					case BinOp.Div:
						il.Emit(OpCodes.Div);
						break;
					case BinOp.And:
						il.Emit(OpCodes.And);
						break;
					case BinOp.Or:
						il.Emit(OpCodes.Or);
						break;
					default:
						throw new UnreachableException($"Operator '{op}' is not a valid binary operator.");
				}

				return leftType;
			}
			case Expression.UnaryOp(var op, var operand): {
				var type = BuildExpression(operand);

				switch (op) {
					case UnOp.Neg:
						if (type != ExpressionType.Int) {
							throw new CompileException(
								$"Invalid Operator. Can't call operator '{op}' on operand of type '{type}'"
							);
						}

						il.Emit(OpCodes.Neg);
						return type;
					case UnOp.Not:
						if (type != ExpressionType.Bool) {
							throw new CompileException(
								$"Invalid Operator. Can't call operator '{op}' on operand of type '{type}'"
							);
						}

						il.Emit(OpCodes.Ldc_I4_0);
						il.Emit(OpCodes.Ceq);
						return type;
					default:
						throw new UnreachableException($"Operator '{op}' is not a valid unary operator.");
				}
			}
			case Expression.Identifier(var name): {
				var symbol = Resolve(name);
				if (symbol is null)
					throw new CompileException($"Use of undeclared identifier '{name}'");

				switch (symbol) {
					case Symbol.Variable(var type, var local): {
						Debug.Assert(type == ExpressionType.Unit || local != null);
						if (local != null)
							il.Emit(OpCodes.Ldloc, local);
						return type;
					}
					case Symbol.Parameter(var index, var type): {
						il.Emit(OpCodes.Ldarg, index);
						return type;
					}
					// Defensive: FunctionCall is parsed before Identifier so a bare function name shouldn't reach here
					case Symbol.Function: {
						throw new CompileException($"'{name}' is a function and cannot be used as a value");
					}
					default:
						throw new UnreachableException();
				}
			}
			case Expression.FunctionCall(var name, var args): {
				var symbol = Resolve(name);
				if (symbol is null)
					throw new CompileException($"Use of undefined function '{name}'");
				if (symbol is not Symbol.Function(var method, var returnType, var paramTypes))
					throw new CompileException($"'{name}' is not a function");

				if (args.Length != paramTypes.Length)
					throw new CompileException($"'{name}' expects {paramTypes.Length} argument(s), got {args.Length}");

				for (var i = 0; i < args.Length; i++) {
					var argType = BuildExpression(args[i]);
					if (argType != paramTypes[i])
						throw new CompileException(
							$"'{name}' argument {i + 1}: expected '{paramTypes[i]}', got '{argType}'"
						);
				}

				il.Emit(OpCodes.Call, method);
				return returnType;
			}
			case Expression.Block(var body, var tail): {
				scopes.Push(new Scope());
				var returned = false;
				foreach (var stmt in body) {
					if (returned)
						break;
					returned = BuildStatement(stmt);
				}

				var returnType = !returned && tail != null ? BuildExpression(tail) : ExpressionType.Unit;
				scopes.Pop();
				return returned ? ExpressionType.Never : returnType;
			}
			case Expression.EnumVariantInit(var enumName, var variantName, var fieldExpressions): {
				var symbol = Resolve(enumName);
				if (symbol is null)
					throw new CompileException($"Use of undefined enum '{enumName}'");
				if (symbol is not Symbol.EnumType(var enumType))
					throw new CompileException($"'{enumName}' is not an enum type");

				var idx = enumType.variants.Select(v => v.name).ToList().IndexOf(variantName);
				if (idx == -1)
					throw new CompileException($"No variant named '{variantName}' on enum '{enumName}'");

				var info = enumType.variants[idx];
				if (info.payload is null)
					throw new CompileException(
						$"Bare variant '{enumName}.{variantName}' cannot be constructed with field initializers");

				var payload = info.payload;
				var providedFields = fieldExpressions.Select(f => f.Item1).ToHashSet();
				var missingFields = payload.fields.Keys.Where(f => !providedFields.Contains(f)).ToList();
				if (missingFields.Count > 0)
					throw new CompileException(
						$"Variant '{enumName}.{variantName}' initialization is missing fields: {string.Join(", ", missingFields)}"
					);

				var local = il.DeclareLocal(enumType.clrType);
				il.Emit(OpCodes.Ldloca, local);
				il.Emit(OpCodes.Initobj, enumType.clrType);

				il.Emit(OpCodes.Ldloca, local);
				il.Emit(OpCodes.Ldc_I4, idx);
				il.Emit(OpCodes.Stfld, enumType.clrType.GetField(ENUM_TAG_FIELD_NAME)!);

				var payloadField = enumType.clrType.GetField(variantName)!;
				foreach (var (fieldName, fieldExpr) in fieldExpressions) {
					if (!payload.fields.TryGetValue(fieldName, out var expectedType))
						throw new CompileException(
							$"Variant '{enumName}.{variantName}' has no field '{fieldName}'");

					il.Emit(OpCodes.Ldloca, local);
					il.Emit(OpCodes.Ldflda, payloadField);
					var actualType = BuildExpression(fieldExpr);
					if (actualType != expectedType)
						throw new CompileException(
							$"Field '{fieldName}': expected '{expectedType}', got '{actualType}'"
						);
					il.Emit(OpCodes.Stfld, payload.clrType.GetField(fieldName)!);
				}

				il.Emit(OpCodes.Ldloc, local);
				return enumType;
			}
			case Expression.StructInit(var name, var fieldExpressions): {
				var symbol = Resolve(name);
				if (symbol is null)
					throw new CompileException($"Use of undefined struct type '{name}'");
				if (symbol is not Symbol.StructType(var structType))
					throw new CompileException($"'{name}' is not a struct type");

				var local = il.DeclareLocal(structType.clrType);
				il.Emit(OpCodes.Ldloca, local);
				il.Emit(OpCodes.Initobj, structType.clrType);

				var providedFields = fieldExpressions.Select(f => f.Item1).ToHashSet();
				var missingFields = structType.fields.Keys.Where(f => !providedFields.Contains(f)).ToList();
				if (missingFields.Count > 0)
					throw new CompileException(
						$"Struct initialization of '{name}' is missing fields: {string.Join(", ", missingFields)}"
					);

				foreach (var (fieldName, fieldExpr) in fieldExpressions) {
					if (!structType.fields.TryGetValue(fieldName, out var expectedType))
						throw new CompileException($"Struct '{name}' has no field '{fieldName}'");

					il.Emit(OpCodes.Ldloca, local);
					var actualType = BuildExpression(fieldExpr); // ..., local_adress, expression_result
					if (actualType != expectedType)
						throw new CompileException(
							$"Field '{fieldName}': expected '{expectedType}', got '{actualType}'"
						);
					il.Emit(OpCodes.Stfld, structType.clrType.GetField(fieldName)!);
				}

				il.Emit(OpCodes.Ldloc, local);
				return structType;
			}
			case Expression.FieldAccess(var expr, var name): {
				if (
					expr is Expression.Identifier(var typeName)
					&& Resolve(typeName) is Symbol.EnumType(var enumExprType)
				) {
					var idx = enumExprType.variants.Select(v => v.name).ToList().IndexOf(name);
					if (idx == -1)
						throw new CompileException($"No variant named '{name}' on enum '{typeName}'");
					if (enumExprType.variants[idx].payload is not null)
						throw new CompileException(
							$"Variant '{typeName}.{name}' has payload fields and must be constructed with '{typeName}.{name} {{ ... }}'");
					EmitBareVariant(il, enumExprType, idx);
					return enumExprType;
				}

				var type = BuildExpression(expr);

				switch (type) {
					case ExpressionType.Struct(var structType, var fields): {
						if (!fields.TryGetValue(name, out var fieldType))
							throw new CompileException($"No field named '{name}' on type '{type}'");
						il.Emit(OpCodes.Ldfld, structType.GetField(name)!);
						return fieldType;
					}
					default:
						throw new CompileException($"Can't access field on type '{type}'");
				}
			}
			case Expression.IfLet(var enumName, var variantName, var bindings, var scrutinee, var thenBlock,
				var elseBlock): {
				var symbol = Resolve(enumName);
				if (symbol is null)
					throw new CompileException($"Use of undefined enum '{enumName}'");
				if (symbol is not Symbol.EnumType(var enumType))
					throw new CompileException($"'{enumName}' is not an enum type");

				var idx = enumType.variants.Select(v => v.name).ToList().IndexOf(variantName);
				if (idx == -1)
					throw new CompileException($"No variant named '{variantName}' on enum '{enumName}'");

				var info = enumType.variants[idx];
				if (info.payload is null && bindings.Length > 0)
					throw new CompileException(
						$"Bare variant '{enumName}.{variantName}' cannot bind fields");
				if (info.payload is not null)
					foreach (var b in bindings)
						if (!info.payload.fields.ContainsKey(b))
							throw new CompileException(
								$"Variant '{enumName}.{variantName}' has no field '{b}'");

				var scrutType = BuildExpression(scrutinee);
				if (scrutType != enumType)
					throw new CompileException(
						$"if let scrutinee: expected '{enumType}', got '{scrutType}'");

				var scrutLocal = il.DeclareLocal(enumType.clrType);
				il.Emit(OpCodes.Stloc, scrutLocal);

				il.Emit(OpCodes.Ldloc, scrutLocal);
				il.Emit(OpCodes.Ldfld, enumType.clrType.GetField(ENUM_TAG_FIELD_NAME)!);
				il.Emit(OpCodes.Ldc_I4, idx);
				var elseLabelL = il.DefineLabel();
				var endLabelL = il.DefineLabel();
				il.Emit(OpCodes.Bne_Un, elseLabelL);

				scopes.Push(new Scope());
				if (info.payload is not null) {
					var payloadField = enumType.clrType.GetField(variantName)!;
					foreach (var b in bindings) {
						var fieldType = info.payload.fields[b];
						il.Emit(OpCodes.Ldloc, scrutLocal);
						il.Emit(OpCodes.Ldfld, payloadField);
						il.Emit(OpCodes.Ldfld, info.payload.clrType.GetField(b)!);
						var bindLocal = il.DeclareLocal(fieldType.AsType());
						il.Emit(OpCodes.Stloc, bindLocal);
						Define(b, new Symbol.Variable(fieldType, bindLocal));
					}
				}

				var thenTypeL = BuildExpression(thenBlock);
				scopes.Pop();
				if (thenTypeL != ExpressionType.Never)
					il.Emit(OpCodes.Br, endLabelL);
				il.MarkLabel(elseLabelL);

				ExpressionType resultTypeL;
				if (elseBlock is not null) {
					var elseTypeL = BuildExpression(elseBlock);
					if (elseTypeL != thenTypeL && elseTypeL != ExpressionType.Never &&
					    thenTypeL != ExpressionType.Never)
						throw new CompileException("if let then and else have incompatible types.");
					resultTypeL = thenTypeL == ExpressionType.Never ? elseTypeL : thenTypeL;
				}
				else {
					if (thenTypeL != ExpressionType.Unit && thenTypeL != ExpressionType.Never)
						throw new CompileException(
							$"Inner expression type of if let without else arm must be unit. Got '{thenTypeL}'");
					resultTypeL = ExpressionType.Unit;
				}

				il.MarkLabel(endLabelL);
				return resultTypeL;
			}
			case Expression.If(var condition, var thenBlock, var elseBlock): {
				var conditionType = BuildExpression(condition);

				if (conditionType != ExpressionType.Bool) {
					throw new CompileException($"If condition type must be bool. Got '{conditionType}'");
				}

				var elseLabel = il.DefineLabel();
				var endLabel = il.DefineLabel();

				il.Emit(OpCodes.Brfalse, elseLabel);

				var thenType = BuildExpression(thenBlock);
				if (thenType != ExpressionType.Never)
					il.Emit(OpCodes.Br, endLabel);
				il.MarkLabel(elseLabel);

				ExpressionType resultType;
				if (elseBlock is not null) {
					var elseType = BuildExpression(elseBlock);
					if (elseType != thenType && elseType != ExpressionType.Never && thenType != ExpressionType.Never) {
						throw new CompileException("If and else have incompatible types.");
					}

					resultType = thenType == ExpressionType.Never ? elseType : thenType;
				}
				else {
					if (thenType != ExpressionType.Unit && thenType != ExpressionType.Never) {
						throw new CompileException(
							$"Inner expression type of if expression without else arm must be unit. Got '{thenType}'"
						);
					}

					resultType = ExpressionType.Unit;
				}

				il.MarkLabel(endLabel);
				return resultType;
			}
			case Expression.IntrinsicCall("print", []): {
				il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", Type.EmptyTypes));
				return ExpressionType.Unit;
			}
			case Expression.IntrinsicCall("print", [var arg]): {
				var type = BuildExpression(arg);
				if (type is ExpressionType.Enum enumType) {
					il.Emit(OpCodes.Ldfld, enumType.clrType.GetField(ENUM_TAG_FIELD_NAME)!);
					il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", new[] { typeof(int) })!);
					return ExpressionType.Unit;
				}

				il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", new[] { type.AsType() })!);
				return ExpressionType.Unit;
			}
			case Expression.IntrinsicCall("print", var args):
				throw new CompileException(
					$"Too many arguments: {args.Length}. Intrinsic 'print' requires 0 or 1 arguments."
				);
			case Expression.IntrinsicCall("assert", [var arg]): {
				var type = BuildExpression(arg);
				if (type != ExpressionType.Bool)
					throw new CompileException($"Invalid type '{type}'. Expected: 'bool'");

				var okLabel = il.DefineLabel();
				il.Emit(OpCodes.Brtrue, okLabel);
				il.Emit(OpCodes.Ldstr, "Assertion failed");
				il.Emit(OpCodes.Newobj, typeof(Exception).GetConstructor([typeof(string)])!);
				il.Emit(OpCodes.Throw);
				il.MarkLabel(okLabel);
				return ExpressionType.Unit;
			}
			case Expression.IntrinsicCall("assert", var args):
				throw new CompileException(
					$"Wrong number of arguments: {args.Length}. Intrinsic 'assert' requires 1 argument."
				);
			case Expression.IntrinsicCall("enum_to_int", [var arg]): {
				var type = BuildExpression(arg);
				if (type is not ExpressionType.Enum enumType)
					throw new CompileException($"'enum_to_int' expects an enum argument, got '{type}'");
				il.Emit(OpCodes.Ldfld, enumType.clrType.GetField(ENUM_TAG_FIELD_NAME)!);
				return ExpressionType.Int;
			}
			case Expression.IntrinsicCall("enum_to_int", var args): {
				throw new CompileException(
					$"Wrong number of arguments: {args.Length}. 'enum_to_int' requires 1 argument."
				);
			}
			case Expression.IntrinsicCall(var name, _):
				throw new CompileException($"Unknown intrinsic '{name}'");
			default:
				throw new CompileException($"Expression not supported: {expression}");
		}
	}

	private void EmitEqualityFromStack(ExpressionType type) {
		var il = currentMethod.GetILGenerator();
		switch (type) {
			case ExpressionType.Struct:
			case ExpressionType.Enum: {
				var right = il.DeclareLocal(type.AsType());
				var left = il.DeclareLocal(type.AsType());
				il.Emit(OpCodes.Stloc, right);
				il.Emit(OpCodes.Stloc, left);
				EmitEqualityFromLocals(type, left, right);
				return;
			}
			default:
				il.Emit(OpCodes.Ceq);
				return;
		}
	}

	private void EmitEqualityFromLocals(ExpressionType type, LocalBuilder left, LocalBuilder right) {
		switch (type) {
			case ExpressionType.Struct structType:
				EmitStructEquality(structType, left, right);
				return;
			case ExpressionType.Enum enumType:
				EmitEnumEquality(enumType, left, right);
				return;
			default:
				throw new UnreachableException($"No local equality emitter for type '{type}'");
		}
	}

	private void EmitStructEquality(ExpressionType.Struct structType, LocalBuilder left, LocalBuilder right) {
		var il = currentMethod.GetILGenerator();
		il.Emit(OpCodes.Ldc_I4_1);
		foreach (var (fieldName, fieldType) in structType.fields) {
			il.Emit(OpCodes.Ldloc, left);
			il.Emit(OpCodes.Ldfld, structType.clrType.GetField(fieldName)!);
			il.Emit(OpCodes.Ldloc, right);
			il.Emit(OpCodes.Ldfld, structType.clrType.GetField(fieldName)!);
			EmitEqualityFromStack(fieldType);
			il.Emit(OpCodes.And);
		}
	}

	private void EmitEnumEquality(ExpressionType.Enum enumType, LocalBuilder left, LocalBuilder right) {
		var il = currentMethod.GetILGenerator();
		var falseLabel = il.DefineLabel();
		var endLabel = il.DefineLabel();

		il.Emit(OpCodes.Ldloc, left);
		il.Emit(OpCodes.Ldfld, enumType.clrType.GetField(ENUM_TAG_FIELD_NAME)!);
		il.Emit(OpCodes.Ldloc, right);
		il.Emit(OpCodes.Ldfld, enumType.clrType.GetField(ENUM_TAG_FIELD_NAME)!);
		il.Emit(OpCodes.Ceq);
		il.Emit(OpCodes.Brfalse, falseLabel);

		for (var i = 0; i < enumType.variants.Count; i++) {
			var info = enumType.variants[i];
			if (info.payload is null)
				continue;

			var nextVariant = il.DefineLabel();
			il.Emit(OpCodes.Ldloc, left);
			il.Emit(OpCodes.Ldfld, enumType.clrType.GetField(ENUM_TAG_FIELD_NAME)!);
			il.Emit(OpCodes.Ldc_I4, i);
			il.Emit(OpCodes.Ceq);
			il.Emit(OpCodes.Brfalse, nextVariant);

			il.Emit(OpCodes.Ldloc, left);
			il.Emit(OpCodes.Ldfld, enumType.clrType.GetField(info.name)!);
			il.Emit(OpCodes.Ldloc, right);
			il.Emit(OpCodes.Ldfld, enumType.clrType.GetField(info.name)!);
			EmitEqualityFromStack(info.payload);
			il.Emit(OpCodes.Br, endLabel);
			il.MarkLabel(nextVariant);
		}

		il.Emit(OpCodes.Ldc_I4_1);
		il.Emit(OpCodes.Br, endLabel);
		il.MarkLabel(falseLabel);
		il.Emit(OpCodes.Ldc_I4_0);
		il.MarkLabel(endLabel);
	}

	private static void EmitBareVariant(ILGenerator il, ExpressionType.Enum enumType, int tagIndex) {
		var local = il.DeclareLocal(enumType.clrType);
		il.Emit(OpCodes.Ldloca, local);
		il.Emit(OpCodes.Initobj, enumType.clrType);
		il.Emit(OpCodes.Ldloca, local);
		il.Emit(OpCodes.Ldc_I4, tagIndex);
		il.Emit(OpCodes.Stfld, enumType.clrType.GetField(ENUM_TAG_FIELD_NAME)!);
		il.Emit(OpCodes.Ldloc, local);
	}

	private const string ENUM_TAG_FIELD_NAME = "$tag";

	private ExpressionType EmitAddressOf(Expression expr) {
		var il = currentMethod.GetILGenerator();
		switch (expr) {
			case Expression.Identifier(var name): {
				var symbol = Resolve(name);
				if (symbol is not Symbol.Variable var)
					throw new CompileException($"Cannot take address of '{name}'");
				il.Emit(OpCodes.Ldloca, var.local!);
				return var.type;
			}
			case Expression.FieldAccess(var inner, var field): {
				var type = EmitAddressOf(inner);
				if (type is not ExpressionType.Struct structType)
					throw new CompileException($"Cannot access field '{field}' on non-struct type '{type}'");
				if (!structType.fields.TryGetValue(field, out var fieldType))
					throw new CompileException($"No field named '{field}' on type '{structType}'");
				il.Emit(OpCodes.Ldflda, structType.clrType.GetField(field)!);
				return fieldType;
			}
			default:
				throw new CompileException($"Invalid assignment target: {expr}");
		}
	}

	private Symbol? Resolve(string name) {
		var crossedBoundary = false;
		foreach (var scope in scopes) {
			var symbol = scope.Get(name);
			if (symbol != null) {
				if (crossedBoundary && symbol is Symbol.Variable or Symbol.Parameter)
					return null;
				return symbol;
			}

			if (scope.IsFunctionBoundary)
				crossedBoundary = true;
		}

		return null;
	}

	private void Define(string name, Symbol symbol) => scopes.Peek().Define(name, symbol);
}