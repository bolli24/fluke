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

	private record ErrorType : ExpressionType;

	public record Struct(Type clrType, IReadOnlyDictionary<string, ExpressionType> fields) : ExpressionType;

	public record Ref(ExpressionType type) : ExpressionType;

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
	public static readonly ExpressionType Error = new ErrorType();

	public sealed override string ToString() =>
		this switch {
			IntType => "int",
			BoolType => "bool",
			StringType => "string",
			UnitType => "unit",
			NeverType => "never",
			ErrorType => "ERROR",
			Struct(var t, _) => t.Name,
			Enum enumType => enumType.clrType.Name,
			Ref(var t) => $"ref {t}",
			_ => throw new UnreachableException(),
		};

	public Type AsType() =>
		this switch {
			IntType => typeof(int),
			BoolType => typeof(bool),
			StringType => typeof(string),
			UnitType => typeof(void),
			ErrorType => throw new UnreachableException("Error type must not be used in codegen"),
			Struct(var t, _) => t,
			Enum enumType => enumType.clrType,
			_ => throw new UnreachableException(),
		};

	public bool CoercesTo(ExpressionType target) => this == target || this == Error || this == Never || target == Error;
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

	private readonly List<Diagnostic> diagnostics = [];

	// Values the enclosing expressions have left on the IL stack; `return` is only valid when this is 0.
	private int pendingStackValues;

	private ExpressionType Error(string msg, Span span) {
		diagnostics.Add(new Diagnostic(msg, span));
		return ExpressionType.Error;
	}

	public FlukeProgram GenerateInMemory(Ast ast) {
		var asmName = new AssemblyName("Fluke");
		var builder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
		var mod = builder.DefineDynamicModule("Fluke");
		var (_, mainMethod) = GenerateCore(mod, ast);

		if (diagnostics.Count > 0)
			throw new CompileException(diagnostics);

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
		
		if (diagnostics.Count > 0)
			throw new CompileException(diagnostics);

		var peBytes = BuildPeBytes(builder, mainMethod);
		ILVerifier.Verify(peBytes);

		File.WriteAllBytes(outputPath, peBytes);
	}

	private static byte[] BuildPeBytes(PersistedAssemblyBuilder builder, MethodBuilder entryPoint) {
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
		var pendingStructs =
			new Dictionary<string, (TypeBuilder builder, Ident ident, (Ident name, Ident type)[] fields)>();
		foreach (var stmt in stmtList) {
			if (stmt is not Statement.StructDeclaration(var sIdent, var sFields))
				continue;
			var sName = sIdent.Name;
			var sb = mod.DefineType(
				sName,
				TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout,
				typeof(ValueType)
			);
			structTypes[sName] = new ExpressionType.Struct(sb, new Dictionary<string, ExpressionType>());
			pendingStructs[sName] = (sb, sIdent, sFields.Select(f => (f.Item1, f.Item2)).ToArray());
		}

		// Sub-pass 1b: pre-register enum TypeBuilders and per-variant payload struct TypeBuilders.
		// Placeholder ExpressionType.Enum entries allow ParseType to resolve enum names while
		// structs are being finalized.
		var pendingEnums =
			new Dictionary<string, (TypeBuilder builder, Ident ident, EnumVariant[] variants,
				Dictionary<string, TypeBuilder> payloads)>();
		foreach (var stmt in stmtList) {
			if (stmt is not Statement.EnumDeclaration(var eIdent, var eVariants))
				continue;
			var eName = eIdent.Name;
			var eb = mod.DefineType(
				eName,
				TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.ExplicitLayout,
				typeof(ValueType)
			);
			var payloads = new Dictionary<string, TypeBuilder>();
			foreach (var variant in eVariants) {
				if (variant is not EnumVariant.Struct(var (vName, _), _))
					continue;
				payloads[vName] = mod.DefineType(
					eName + "$" + vName,
					TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout,
					typeof(ValueType)
				);
			}

			enumTypes[eName] = new ExpressionType.Enum(eb, Array.Empty<EnumVariantInfo>());
			pendingEnums[eName] = (eb, eIdent, eVariants, payloads);
		}

		// Sub-pass 2: define fields and finalize structs in dependency order
		var finalized = new HashSet<string>();
		var visitingSet = new HashSet<string>();
		var visitingPath = new List<string>();

		foreach (var (_, (_, sIdent, _)) in pendingStructs)
			FinalizeStruct(sIdent);

		// Sub-pass 3: finalize enum payload structs (with ref-type validation), then enum types.
		foreach (var (eName, (eb, eIdent, variants, payloads)) in pendingEnums) {
			var variantInfos = new List<EnumVariantInfo>(variants.Length);
			foreach (var variant in variants) {
				if (variant is EnumVariant.Struct(var vIdent, var vFields)) {
					var vName = vIdent.Name;
					var pb = payloads[vName];
					var fieldTypes = new Dictionary<string, ExpressionType>();
					foreach (var ((fName, _), fTypeName) in vFields) {
						var fieldType = ParseType(fTypeName);
						if (!CheckVariantFieldType(fieldType, eIdent, vIdent, fName, fTypeName.Span, new HashSet<string>()))
							fieldType = ExpressionType.Error;
						fieldTypes[fName] = fieldType;
						if (fieldType != ExpressionType.Error)
							pb.DefineField(fName, fieldType.AsType(), FieldAttributes.Public);
					}

					var payloadType = pb.CreateType();
					variantInfos.Add(new EnumVariantInfo(vName,
						new ExpressionType.Struct(payloadType, fieldTypes)));
				}
				else {
					variantInfos.Add(new EnumVariantInfo(variant.name.Name, null));
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
					var mangledName = prefix == "" ? decl.ident.Name : prefix + "$" + decl.ident.Name;
					var parameterTypes = decl.parameters.Select(p => {
						if (!p.IsRef)
							return ParseType(p.TypeName);
						Error("'ref' parameters are not supported yet.", p.Span);
						return ExpressionType.Error;
					}).ToArray();
					var returnType = decl.returnType is not null ? ParseType(decl.returnType) : ExpressionType.Unit;
					var mb = type.DefineMethod(
						mangledName,
						MethodAttributes.Public | MethodAttributes.Static,
						SignatureType(returnType),
						parameterTypes.Select(SignatureType).ToArray()
					);
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

		// Returns false if `ident` closes a cycle; the caller gives that field the Error type.
		bool FinalizeStruct(Ident ident) {
			var name = ident.Name;
			if (finalized.Contains(name))
				return true;
			if (!visitingSet.Add(name)) {
				var cycle = visitingPath.Skip(visitingPath.IndexOf(name)).Append(name);
				Error($"Cyclic struct dependency: {string.Join(" -> ", cycle)}", ident.Span);
				return false;
			}

			visitingPath.Add(name);
			var (sb, _, fields) = pendingStructs[name];
			var fieldTypes = new Dictionary<string, ExpressionType>();
			foreach (var (fieldNameIdent, fieldTypeIdent) in fields) {
				var fieldType = pendingStructs.ContainsKey(fieldTypeIdent.Name) && !FinalizeStruct(fieldTypeIdent)
					? ExpressionType.Error
					: ParseType(fieldTypeIdent);
				fieldTypes[fieldNameIdent.Name] = fieldType;
				// Fields of type Error (unknown type or cycle-closing) get no CLR field
				if (fieldType != ExpressionType.Error)
					sb.DefineField(fieldNameIdent.Name, fieldType.AsType(), FieldAttributes.Public);
			}

			structTypes[name] = new ExpressionType.Struct(sb.CreateType(), fieldTypes);
			visitingPath.RemoveAt(visitingPath.Count - 1);
			visitingSet.Remove(name);
			finalized.Add(name);
			return true;
		}
	}

	private bool CheckVariantFieldType(ExpressionType t, Ident enumIdent, Ident variantIdent, string fieldPath,
		Span typeSpan, HashSet<string> visiting) {
		switch (t) {
			case ExpressionType.Struct(var clrType, var fields): {
				var key = clrType.FullName ?? clrType.Name;
				if (!visiting.Add(key))
					return true;
				foreach (var (fName, fType) in fields) {
					if (!CheckVariantFieldType(fType, enumIdent, variantIdent, fieldPath + "." + fName, typeSpan,
						    visiting))
						return false;
				}

				visiting.Remove(key);
				return true;
			}
			case ExpressionType.Enum:
				Error(
					$"Enum variant '{enumIdent.Name}.{variantIdent.Name}' field '{fieldPath}': nested enum payloads are not supported",
					typeSpan);
				return false;
			default:
				// Error: already reported where the type was resolved
				if (t == ExpressionType.Int || t == ExpressionType.Bool || t == ExpressionType.Error)
					return true;
				Error(
					$"Enum variant '{enumIdent.Name}.{variantIdent.Name}' field '{fieldPath}' has disallowed type '{t}' (reference types are not allowed in variants)",
					typeSpan);
				return false;
		}
	}

	// Placeholder for signatures whose types failed to resolve; the method body is never serialized.
	private static Type SignatureType(ExpressionType t) =>
		t == ExpressionType.Error ? typeof(object) : t.AsType();

	private string MangledName(string name) {
		return nameStack.Count == 0 ? name : nameStack.Peek() + "$" + name;
	}

	// Pre-populate the current scope with all functions and structs declared directly in
	// `statements` so that forward references within the same body work.
	private void PreDefineSymbols(IEnumerable<Statement> statements) {
		foreach (var stmt in statements) {
			switch (stmt) {
				case Statement.FunctionDeclaration(var (name, _), _, _, _):
					if (functions.TryGetValue(MangledName(name), out var fn))
						scopes.Peek().Define(name, fn);
					break;
				case Statement.StructDeclaration(var (name, _), _):
					if (structTypes.TryGetValue(name, out var st))
						scopes.Peek().Define(name, new Symbol.StructType(st));
					break;
				case Statement.EnumDeclaration(var (name, _), _):
					if (enumTypes.TryGetValue(name, out var en))
						scopes.Peek().Define(name, new Symbol.EnumType(en));
					break;
			}
		}
	}

	private ExpressionType ParseType(Ident ident) =>
		ident.Name switch {
			"int" => ExpressionType.Int,
			"bool" => ExpressionType.Bool,
			"string" => ExpressionType.String,
			"unit" => ExpressionType.Unit,
			_ => structTypes.TryGetValue(ident.Name, out var st) ? st
				: enumTypes.TryGetValue(ident.Name, out var en) ? en
				: Error($"Unknown type '{ident.Name}'", ident.Span),
		};

	private bool BuildStatement(Statement statement) {
		var il = currentMethod.GetILGenerator();
		switch (statement) {
			case Statement.VariableBinding(var (name, _), var expression): {
				var type = BuildExpression(expression);
				LocalBuilder? local = null;
				// Unit, Never and Error variables have no storage
				if (type != ExpressionType.Unit && type != ExpressionType.Never && type != ExpressionType.Error) {
					local = il.DeclareLocal(type.AsType());
					il.Emit(OpCodes.Stloc, local);
				}

				Define(name, new Symbol.Variable(type, local));
				break;
			}
			case Statement.FunctionDeclaration(var (name, _), var parameters, var body, _): {
				var mangledName = MangledName(name);
				var fn = functions[mangledName];
				Define(name, fn);

				var savedMethod = currentMethod;
				currentMethod = fn.method;
				var savedReturnType = currentReturnType;
				currentReturnType = fn.returnType;
				var savedPendingStackValues = pendingStackValues;
				pendingStackValues = 0;
				nameStack.Push(mangledName);

				scopes.Push(new Scope { IsFunctionBoundary = true });
				for (var i = 0; i < parameters.Length; i++)
					scopes.Peek().Define(parameters[i].Name.Name, new Symbol.Parameter(i, fn.parameterTypes[i]));

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
				pendingStackValues = savedPendingStackValues;
				break;
			}
			case Statement.Assignment(var left, var expression): {
				switch (left) {
					case Expression.Identifier(var (name, span)): {
						var type = BuildExpression(expression);
						switch (Resolve(name)) {
							case null:
								Error($"Use of undeclared identifier '{name}'", span);
								break;
							case Symbol.Variable var:
								if (!type.CoercesTo(var.type))
									Error(
										$"Type mismatch. Can't assign value of type '{type}' to variable of type '{var.type}'",
										span
									);
								// Unit, Never and Error variables have no storage
								if (var.local != null)
									il.Emit(OpCodes.Stloc, var.local);
								break;
							default:
								Error($"Cannot assign to '{name}'. Must be a variable.", span);
								break;
						}

						break;
					}
					case Expression.FieldAccess(var container, var (field, span)): {
						var (targetType, clrField) = ResolveField(EmitAddressOf(container), field, span);
						var valueType = BuildOperand(expression, 1); // ..., field_address
						if (!valueType.CoercesTo(targetType))
							Error(
								$"Type mismatch. Can't assign value of type '{valueType}' to field '{field}' of type '{targetType}'",
								span
							);
						if (clrField != null)
							il.Emit(OpCodes.Stfld, clrField);
						break;
					}
					default:
						Error($"Invalid assignment target: {left}", left.Span);
						BuildExpression(expression); // still report errors in the value
						break;
				}

				break;
			}
			case Statement.StructDeclaration(var (name, _), _): {
				Define(name, new Symbol.StructType(structTypes[name]));
				break;
			}
			case Statement.EnumDeclaration(var (name, _), _): {
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
				var span = expr?.Span ?? statement.Span;
				if (!type.CoercesTo(currentReturnType))
					Error($"Return type mismatch: expected '{currentReturnType}', got '{type}'", span);
				// `ret` requires the stack to hold only the return value; supporting this needs MIR lowering
				if (pendingStackValues > 0)
					throw new NotImplementedException(
						$"return with values on the evaluation stack is not supported until MIR lowering @ {statement.Span}");
				il.Emit(OpCodes.Ret);
				return true;
			}
			default:
				throw new NotImplementedException($"Statement not supported: {statement} @ {statement.Span}");
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
				var rightType = BuildOperand(right, 1); // ..., left

				var expectedType = op switch {
					BinOp.And or BinOp.Or => ExpressionType.Bool,
					_ => ExpressionType.Int,
				};
				var isEquality = op is BinOp.Eq or BinOp.Neq;
				var resultType = isEquality ? ExpressionType.Bool : expectedType;

				if (leftType == ExpressionType.Error || rightType == ExpressionType.Error)
					return resultType; // already reported

				if (isEquality && leftType != rightType) {
					Error(
						$"Type mismatch. Can't call operator '{op}' on operands of different types '{leftType}' and '{rightType}'",
						expression.Span
					);
					return resultType;
				}

				if (!isEquality && (leftType != expectedType || rightType != expectedType)) {
					Error(
						$"Type mismatch. Operator '{op}' expects '{expectedType}' operands, got '{leftType}' and '{rightType}'",
						expression.Span
					);
					return resultType;
				}

				if (isEquality) {
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

				return resultType;
			}
			case Expression.UnaryOp(var op, var operand): {
				var type = BuildExpression(operand);

				switch (op) {
					case UnOp.Neg:
						if (!type.CoercesTo(ExpressionType.Int)) {
							Error(
								$"Invalid Operator. Can't call operator '{op}' on operand of type '{type}'",
								expression.Span
							);
							return ExpressionType.Int;
						}

						il.Emit(OpCodes.Neg);
						return type;
					case UnOp.Not:
						if (!type.CoercesTo(ExpressionType.Bool)) {
							Error(
								$"Invalid Operator. Can't call operator '{op}' on operand of type '{type}'",
								expression.Span
							);
							return ExpressionType.Bool;
						}

						il.Emit(OpCodes.Ldc_I4_0);
						il.Emit(OpCodes.Ceq);
						return type;
					default:
						throw new UnreachableException($"Operator '{op}' is not a valid unary operator.");
				}
			}
			case Expression.Identifier(var (name, span)): {
				var symbol = Resolve(name);
				if (symbol is null)
					return Error($"Use of undeclared identifier '{name}'", span);

				switch (symbol) {
					case Symbol.Variable(var type, var local): {
						Debug.Assert(type == ExpressionType.Unit || type == ExpressionType.Never ||
						             type == ExpressionType.Error || local != null);
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
						return Error($"'{name}' is a function and cannot be used as a value", span);
					}
					default:
						throw new UnreachableException();
				}
			}
			case Expression.FunctionCall(var (name, span), var args): {
				var symbol = Resolve(name);

				var argExpr = args.Select((arg, i) => BuildOperand(arg, i)).ToArray(); // ..., arg_0 .. arg_i-1

				if (symbol is null)
					return Error($"Use of undefined function '{name}'", span);
				if (symbol is not Symbol.Function(var method, var returnType, var paramTypes))
					return Error($"'{name}' is not a function", span);

				if (args.Length != paramTypes.Length) {
					Error($"'{name}' expects {paramTypes.Length} argument(s), got {args.Length}",
						expression.Span);
					return returnType;
				}

				for (var i = 0; i < args.Length; i++) {
					var argType = argExpr[i];
					if (!argType.CoercesTo(paramTypes[i]))
						Error(
							$"'{name}' argument {i + 1}: expected '{paramTypes[i]}', got '{argType}'", args[i].Span
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
			case Expression.EnumVariantInit(var (enumName, enumSpan), var (variantName, variantSpan), var
				fieldExpressions): {
				var symbol = Resolve(enumName);
				if (symbol is null)
					return Error($"Use of undefined enum '{enumName}'", enumSpan);

				if (symbol is not Symbol.EnumType(var enumType))
					return Error($"'{enumName}' is not an enum type", enumSpan);

				var idx = enumType.variants.Select(v => v.name).ToList().IndexOf(variantName);
				if (idx == -1) {
					Error($"No variant named '{variantName}' on enum '{enumName}'", variantSpan);
					return enumType;
				}

				var (_, payload) = enumType.variants[idx];
				if (payload is null) {
					Error(
						$"Bare variant '{enumName}.{variantName}' cannot be constructed with field initializers",
						expression.Span);
					return enumType;
				}

				var providedFields = fieldExpressions.Select(f => f.Item1.Name).ToHashSet();
				var missingFields = payload.fields.Keys.Where(f => !providedFields.Contains(f)).ToList();
				if (missingFields.Count > 0) {
					Error(
						$"Variant '{enumName}.{variantName}' initialization is missing fields: {string.Join(", ", missingFields)}",
						expression.Span
					);
					return enumType;
				}

				var local = il.DeclareLocal(enumType.clrType);
				il.Emit(OpCodes.Ldloca, local);
				il.Emit(OpCodes.Initobj, enumType.clrType);

				il.Emit(OpCodes.Ldloca, local);
				il.Emit(OpCodes.Ldc_I4, idx);
				il.Emit(OpCodes.Stfld, enumType.clrType.GetField(ENUM_TAG_FIELD_NAME)!);

				var payloadField = enumType.clrType.GetField(variantName)!;
				foreach (var ((fieldName, span), fieldExpr) in fieldExpressions) {
					il.Emit(OpCodes.Ldloca, local);
					il.Emit(OpCodes.Ldflda, payloadField);
					var actualType = BuildOperand(fieldExpr, 1); // ..., payload_address

					if (!payload.fields.TryGetValue(fieldName, out var expectedType)) {
						Error(
							$"Variant '{enumName}.{variantName}' has no field '{fieldName}'", span);
						continue;
					}

					if (!actualType.CoercesTo(expectedType))
						Error(
							$"Field '{fieldName}': expected '{expectedType}', got '{actualType}'", fieldExpr.Span
						);
					// Fields of type Error were never defined on the CLR type
					var clrField = payload.clrType.GetField(fieldName);
					if (clrField != null)
						il.Emit(OpCodes.Stfld, clrField);
				}

				il.Emit(OpCodes.Ldloc, local);
				return enumType;
			}
			case Expression.StructInit(var (name, span), var fieldExpressions): {
				var symbol = Resolve(name);
				if (symbol is null)
					return Error($"Use of undefined struct type '{name}'", span);
				if (symbol is not Symbol.StructType(var structType))
					return Error($"'{name}' is not a struct type", span);

				var local = il.DeclareLocal(structType.clrType);
				il.Emit(OpCodes.Ldloca, local);
				il.Emit(OpCodes.Initobj, structType.clrType);

				var providedFields = fieldExpressions.Select(f => f.Item1.Name).ToHashSet();
				var missingFields = structType.fields.Keys.Where(f => !providedFields.Contains(f)).ToList();
				if (missingFields.Count > 0)
					Error(
						$"Struct initialization of '{name}' is missing fields: {string.Join(", ", missingFields)}",
						expression.Span
					);

				foreach (var ((fieldName, fieldSpan), fieldExpr) in fieldExpressions) {
					il.Emit(OpCodes.Ldloca, local);
					var actualType = BuildOperand(fieldExpr, 1); // ..., local_adress, expression_result

					if (!structType.fields.TryGetValue(fieldName, out var expectedType)) {
						Error($"Struct '{name}' has no field '{fieldName}'", fieldSpan);
						continue;
					}

					if (!actualType.CoercesTo(expectedType))
						Error(
							$"Field '{fieldName}': expected '{expectedType}', got '{actualType}'", fieldExpr.Span
						);
					// Fields of type Error were never defined on the CLR type
					var clrField = structType.clrType.GetField(fieldName);
					if (clrField != null)
						il.Emit(OpCodes.Stfld, clrField);
				}

				il.Emit(OpCodes.Ldloc, local);
				return structType;
			}
			case Expression.FieldAccess(var expr, var (name, span)): {
				if (
					expr is Expression.Identifier(var (typeName, _))
					&& Resolve(typeName) is Symbol.EnumType(var enumExprType)
				) {
					var idx = enumExprType.variants.Select(v => v.name).ToList().IndexOf(name);
					if (idx == -1)
						return Error($"No variant named '{name}' on enum '{typeName}'", span);
					if (enumExprType.variants[idx].payload is not null)
						return Error(
							$"Variant '{typeName}.{name}' has payload fields and must be constructed with '{typeName}.{name} {{ ... }}'",
							expression.Span);
					EmitBareVariant(il, enumExprType, idx);
					return enumExprType;
				}

				var (fieldType, clrField) = ResolveField(BuildExpression(expr), name, span);
				if (clrField != null)
					il.Emit(OpCodes.Ldfld, clrField);
				return fieldType;
			}
			case Expression.IfLet(var (enumName, enumNameSpan), var (variantName, variantNameSpan), var bindings, var
				scrutinee, var thenBlock,
				var elseBlock): {
				var symbol = Resolve(enumName);
				if (symbol is null)
					return Error($"Use of undefined enum '{enumName}'", enumNameSpan);
				if (symbol is not Symbol.EnumType(var enumType))
					return Error($"'{enumName}' is not an enum type", enumNameSpan);

				var idx = enumType.variants.Select(v => v.name).ToList().IndexOf(variantName);
				if (idx == -1)
					return Error($"No variant named '{variantName}' on enum '{enumName}'",
						variantNameSpan);

				var info = enumType.variants[idx];
				if (info.payload is null && bindings.Length > 0)
					return Error(
						$"Bare variant '{enumName}.{variantName}' cannot bind fields",
						bindings.Select(b => b.Span).Aggregate((a, b) => a | b));
				if (info.payload is not null)
					foreach (var b in bindings)
						if (!info.payload.fields.ContainsKey(b.Name))
							return Error(
								$"Variant '{enumName}.{variantName}' has no field '{b.Name}'", b.Span);

				var scrutType = BuildExpression(scrutinee);
				if (!scrutType.CoercesTo(enumType))
					return Error(
						$"if let scrutinee: expected '{enumType}', got '{scrutType}'", scrutinee.Span);

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
					foreach (var (b, _) in bindings) {
						var fieldType = info.payload.fields[b];
						if (fieldType == ExpressionType.Error) {
							Define(b, new Symbol.Variable(fieldType, null)); // already reported, no CLR field
							continue;
						}

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
					if (!elseTypeL.CoercesTo(thenTypeL) && !thenTypeL.CoercesTo(elseTypeL))
						return Error("if let then and else have incompatible types.", expression.Span);
					resultTypeL = thenTypeL == ExpressionType.Never ? elseTypeL : thenTypeL;
				}
				else {
					if (!thenTypeL.CoercesTo(ExpressionType.Unit))
						return Error(
							$"Inner expression type of if let without else arm must be unit. Got '{thenTypeL}'",
							expression.Span);
					resultTypeL = ExpressionType.Unit;
				}

				il.MarkLabel(endLabelL);
				return resultTypeL;
			}
			case Expression.If(var condition, var thenBlock, var elseBlock): {
				var conditionType = BuildExpression(condition);

				if (!conditionType.CoercesTo(ExpressionType.Bool)) {
					return Error($"If condition type must be bool. Got '{conditionType}'",
						condition.Span);
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
					if (!elseType.CoercesTo(thenType) && !thenType.CoercesTo(elseType)) {
						return Error("If and else have incompatible types.", expression.Span);
					}

					resultType = thenType == ExpressionType.Never ? elseType : thenType;
				}
				else {
					if (!thenType.CoercesTo(ExpressionType.Unit)) {
						return Error(
							$"Inner expression type of if expression without else arm must be unit. Got '{thenType}'",
							thenBlock.Span
						);
					}

					resultType = ExpressionType.Unit;
				}

				il.MarkLabel(endLabel);
				return resultType;
			}
			case Expression.IntrinsicCall(("print", _), []): {
				il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", Type.EmptyTypes));
				return ExpressionType.Unit;
			}
			case Expression.IntrinsicCall(("print", _), [var arg]): {
				var type = BuildExpression(arg);
				if (type == ExpressionType.Error)
					return ExpressionType.Unit; // already reported
				if (type == ExpressionType.Unit) {
					Error($"Can't print value of type '{type}'", arg.Span);
					return ExpressionType.Unit;
				}

				if (type is ExpressionType.Enum enumType) {
					il.Emit(OpCodes.Ldfld, enumType.clrType.GetField(ENUM_TAG_FIELD_NAME)!);
					il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", [typeof(int)])!);
					return ExpressionType.Unit;
				}

				il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", [type.AsType()])!);
				return ExpressionType.Unit;
			}
			case Expression.IntrinsicCall(("print", _), var args):
				return Error(
					$"Too many arguments: {args.Length}. Intrinsic 'print' requires 0 or 1 arguments.",
					args.Select(a => a.Span).Aggregate((a, b) => a | b)
				);
			case Expression.IntrinsicCall(("assert", _), [var arg]): {
				var type = BuildExpression(arg);
				if (!type.CoercesTo(ExpressionType.Bool))
					return Error($"Invalid type '{type}'. Expected: 'bool'", arg.Span);

				var okLabel = il.DefineLabel();
				il.Emit(OpCodes.Brtrue, okLabel);
				il.Emit(OpCodes.Ldstr, "Assertion failed");
				il.Emit(OpCodes.Newobj, typeof(Exception).GetConstructor([typeof(string)])!);
				il.Emit(OpCodes.Throw);
				il.MarkLabel(okLabel);
				return ExpressionType.Unit;
			}
			case Expression.IntrinsicCall(("assert", _), var args):
				return Error(
					$"Wrong number of arguments: {args.Length}. Intrinsic 'assert' requires 1 argument.",
					expression.Span
				);
			case Expression.IntrinsicCall(("enum_to_int", _), [var arg]): {
				var type = BuildExpression(arg);
				if (type == ExpressionType.Error)
					return ExpressionType.Int; // already reported
				if (type is not ExpressionType.Enum enumType)
					return Error($"'enum_to_int' expects an enum argument, got '{type}'", arg.Span);
				il.Emit(OpCodes.Ldfld, enumType.clrType.GetField(ENUM_TAG_FIELD_NAME)!);
				return ExpressionType.Int;
			}
			case Expression.IntrinsicCall(("enum_to_int", _), var args): {
				return Error(
					$"Wrong number of arguments: {args.Length}. 'enum_to_int' requires 1 argument.", expression.Span
				);
			}
			case Expression.IntrinsicCall(var (name, span), _):
				return Error($"Unknown intrinsic '{name}'", span);
			default:
				throw new NotImplementedException($"Expression not supported: {expression} at {expression.Span}");
		}
	}

	// Builds a sub-expression while the enclosing expression has `pushed` values below it on the IL stack.
	private ExpressionType BuildOperand(Expression expression, int pushed) {
		pendingStackValues += pushed;
		var type = BuildExpression(expression);
		pendingStackValues -= pushed;
		return type;
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
			if (fieldType == ExpressionType.Error)
				continue; // already reported, no CLR field
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

	private (ExpressionType type, FieldInfo? field)
		ResolveField(ExpressionType containerType, string field, Span span) {
		if (containerType == ExpressionType.Error)
			return (ExpressionType.Error, null);
		if (containerType is not ExpressionType.Struct structType) {
			Error($"Cannot access field '{field}' on non-struct type '{containerType}'", span);
			return (ExpressionType.Error, null);
		}

		if (!structType.fields.TryGetValue(field, out var fieldType)) {
			Error($"No field named '{field}' on type '{structType}'", span);
			return (ExpressionType.Error, null);
		}

		// Fields of type Error were never defined on the CLR type
		return (fieldType, structType.clrType.GetField(field));
	}

	private ExpressionType EmitAddressOf(Expression expr) {
		var il = currentMethod.GetILGenerator();
		switch (expr) {
			case Expression.Identifier(var (name, span)):
				switch (Resolve(name)) {
					case null:
						return Error($"Use of undeclared identifier '{name}'", span);
					case Symbol.Variable var:
						if (var.local != null)
							il.Emit(OpCodes.Ldloca, var.local);
						return var.type;
					default:
						return Error($"Cannot take address of '{name}'", span);
				}
			case Expression.FieldAccess(var inner, var (field, span)): {
				var (fieldType, clrField) = ResolveField(EmitAddressOf(inner), field, span);
				if (clrField != null)
					il.Emit(OpCodes.Ldflda, clrField);
				return fieldType;
			}
			default:
				return Error($"Invalid assignment target: {expr}", expr.Span);
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