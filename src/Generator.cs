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

internal abstract record ExpressionType {
	private record IntType : ExpressionType;

	private record BoolType : ExpressionType;

	private record StringType : ExpressionType;

	private record UnitType : ExpressionType;

	private record NeverType : ExpressionType;

	public record Struct(Type clrType, IReadOnlyDictionary<string, ExpressionType> fields) : ExpressionType;

	public record Enum(Type clrType, IReadOnlyList<string> variants) : ExpressionType;

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
			Enum(var t, _) => t.Name,
			_ => throw new UnreachableException(),
		};

	public Type AsType() =>
		this switch {
			IntType => typeof(int),
			BoolType => typeof(bool),
			StringType => typeof(string),
			UnitType => typeof(void),
			Struct(var t, _) => t,
			Enum(var t, _) => t,
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
		symbols.TryGetValue(name, out var s) ? s : null;

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
		CollectSymbols(ast.Statements, type, "", mod);
		PreDefineSymbols(ast.Statements);

		// Pass 2: generate code
		var il = currentMethod.GetILGenerator();
		foreach (var statement in ast.Statements) {
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

		// Sub-pass 2: define fields and finalize in dependency order
		var finalized = new HashSet<string>();
		var visitingSet = new HashSet<string>();
		var visitingPath = new List<string>();

		foreach (var name in pendingStructs.Keys)
			FinalizeStruct(name);

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
					break; // handled in sub-passes above
				case Statement.EnumDeclaration decl: {
					enumTypes[decl.name] = new ExpressionType.Enum(typeof(int), decl.variants.AsReadOnly());
					break;
				}
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
				if (left is Expression.Identifier(var name)) {
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
				}
				else if (left is Expression.FieldAccess(var container, var field)) {
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
				}
				else {
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
					case BinOp.Eq:
						il.Emit(OpCodes.Ceq);
						return ExpressionType.Bool;
					case BinOp.Neq:
						il.Emit(OpCodes.Ceq);
						il.Emit(OpCodes.Ldc_I4_0);
						il.Emit(OpCodes.Ceq);
						return ExpressionType.Bool;
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
			case Expression.StructInit(var name, var fieldExpressions): {
				var symbol = Resolve(name);
				if (symbol is null)
					throw new CompileException($"Use of undefined struct type '{name}'");
				if (symbol is not Symbol.StructType(var structType))
					throw new CompileException($"'{name}' is not a struct type");

				var local = il.DeclareLocal(structType.clrType);
				il.Emit(OpCodes.Ldloca, local);
				il.Emit(OpCodes.Initobj, structType.clrType);

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
					var idx = enumExprType.variants.IndexOf(name);
					if (idx == -1)
						throw new CompileException($"No variant named '{name}' on enum '{typeName}'");
					il.Emit(OpCodes.Ldc_I4, idx);
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
					case ExpressionType.Enum(var enumType, var variants): {
						var index = variants.IndexOf(name);
						if (index == -1)
							throw new CompileException($"No variant named '{name}' on type '{type}'");

						il.Emit(OpCodes.Ldc_I4, index);
						return type;
					}
					default:
						throw new CompileException($"Can't access field on type '{type}'");
				}
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
				il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", new[] { type.AsType() }));
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
				if (type is not ExpressionType.Enum)
					throw new CompileException($"'enum_to_int' expects an enum argument, got '{type}'");
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