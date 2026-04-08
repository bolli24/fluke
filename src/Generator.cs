using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace fluke;

public class FlukeProgram(Type programType) {
	private Type programType = programType;

	public void Run() {
		programType.GetMethod("Main")!.Invoke(null, null);
	}
}

public enum ExpressionType {
	Int,
	String,
	Bool,
	Unit,
}

static class ExpressionTypeExt {
	public static Type AsType(this ExpressionType type) {
		return type switch {
			ExpressionType.Int => typeof(int),
			ExpressionType.String => typeof(string),
			ExpressionType.Unit => typeof(void),
			ExpressionType.Bool => typeof(bool),
			_ => throw new UnreachableException(),
		};
	}
}

internal abstract record Symbol {
	public record Variable(ExpressionType type, LocalBuilder? local) : Symbol;

	public record Parameter(int index, ExpressionType type) : Symbol;

	public record Function(MethodBuilder method, ExpressionType returnType, ExpressionType[] parameterTypes) : Symbol;
}

internal class Scope {
	private readonly Dictionary<string, Symbol> symbols = new();

	public Symbol? Get(string name) =>
		symbols.TryGetValue(name, out var s) ? s : null;

	public void Define(string name, Symbol symbol) =>
		symbols[name] = symbol;

	public bool Has(string name) =>
		symbols.ContainsKey(name);
}

public class Generator {
	private MethodBuilder currentMethod;
	private readonly Stack<Scope> scopes = new();
	private readonly Dictionary<string, Symbol.Function> functions = new();
	private readonly Stack<string> nameStack = new();

	public FlukeProgram GenerateInMemory(Ast ast) {
		var asmName = new AssemblyName("Fluke");
		var builder = AssemblyBuilder.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.Run);
		var mod = builder.DefineDynamicModule("Fluke");
		var t = GenerateCore(mod, ast);
		return new FlukeProgram(t);
	}

	public void GenerateToFile(Ast ast, string outputPath) {
		var asmName = new AssemblyName(Path.GetFileNameWithoutExtension(outputPath));
		var builder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
		var mod = builder.DefineDynamicModule(asmName.Name!);
		GenerateCore(mod, ast);

		var metadataBuilder = builder.GenerateMetadata(out var ilStream, out var fieldData);
		var entryPointToken = MetadataTokens.MethodDefinitionHandle(currentMethod.MetadataToken);

		var peHeaderBuilder = new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage);
		var peBuilder = new ManagedPEBuilder(
			header: peHeaderBuilder,
			metadataRootBuilder: new MetadataRootBuilder(metadataBuilder),
			ilStream: ilStream,
			mappedFieldData: fieldData,
			entryPoint: entryPointToken);

		var peBlob = new BlobBuilder();
		peBuilder.Serialize(peBlob);

		using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
		peBlob.WriteContentTo(fileStream);
	}

	private Type GenerateCore(ModuleBuilder mod, Ast ast) {
		var type = mod.DefineType("Fluke.Program", TypeAttributes.Public | TypeAttributes.Class);
		currentMethod = type.DefineMethod("Main", MethodAttributes.Public | MethodAttributes.Static, typeof(void),
			Type.EmptyTypes);

		scopes.Push(new Scope());

		// Pass 1: register all function signatures (recursive)
		CollectFunctions(ast.Statements, type, "");

		// Pass 2: generate code
		var il = currentMethod.GetILGenerator();
		foreach (var statement in ast.Statements) {
			BuildStatement(statement);
		}

		il.Emit(OpCodes.Ret);

		return type.CreateType();
	}

	private void CollectFunctions(IEnumerable<Statement> statements, TypeBuilder type, string prefix) {
		foreach (var stmt in statements) {
			switch (stmt) {
				case Statement.FunctionDeclaration decl: {
					var mangledName = prefix == "" ? decl.name : prefix + "$" + decl.name;
					var paramTypes = decl.parameters.Select(p => ParseType(p.Item2).AsType()).ToArray();
					var returnType = ParseType(decl.returnType);
					var mb = type.DefineMethod(mangledName,
						MethodAttributes.Public | MethodAttributes.Static,
						returnType.AsType(), paramTypes);
					var parameterTypes = decl.parameters.Select(p => ParseType(p.Item2)).ToArray();
					functions[mangledName] = new Symbol.Function(mb, returnType, parameterTypes);
					CollectFunctions(decl.body.body, type, mangledName);
					break;
				}
				case Statement.ExpressionStatement(Expression.Block(var body, _)):
					CollectFunctions(body, type, prefix);
					break;
				case Statement.VariableBinding(_, Expression.Block(var body, _)):
					CollectFunctions(body, type, prefix);
					break;
			}
		}
	}

	private string MangledName(string name) {
		return nameStack.Count == 0 ? name : nameStack.Peek() + "$" + name;
	}

	private static ExpressionType ParseType(string name) => name switch {
		"int" => ExpressionType.Int,
		"bool" => ExpressionType.Bool,
		"string" => ExpressionType.String,
		"unit" => ExpressionType.Unit,
		_ => throw new CompileException($"Unknown type '{name}'")
	};

	private void BuildStatement(Statement statement) {
		var il = currentMethod.GetILGenerator();
		switch (statement) {
			case Statement.IntrinsicCall("print", []): {
				il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", Type.EmptyTypes));
				break;
			}
			case Statement.IntrinsicCall("print", [var arg]): {
				var type = BuildExpression(arg);
				il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", new[] { type.AsType() }));
				break;
			}
			case Statement.IntrinsicCall("print", var args): {
				throw new CompileException($"Too many arguments: {args.Length}. Intrinsic 'print' requires 0 or 1 arguments.");
			}
			case Statement.IntrinsicCall("assert", [var arg]): {
				var type = BuildExpression(arg);
				if (type != ExpressionType.Bool)
					throw new CompileException($"Invalid type '{type}'. Expected: 'Bool'");

				var okLabel = il.DefineLabel();
				il.Emit(OpCodes.Brtrue, okLabel);
				il.Emit(OpCodes.Ldstr, "Assertion failed");
				il.Emit(OpCodes.Newobj, typeof(Exception).GetConstructor([typeof(string)])!);
				il.Emit(OpCodes.Throw);
				il.MarkLabel(okLabel);

				break;
			}
			case Statement.IntrinsicCall("assert", var args): {
				throw new CompileException(
					$"Wrong number of arguments: {args.Length}. Intrinsic 'assert' requires 1 argument.");
			}
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
				nameStack.Push(mangledName);

				scopes.Push(new Scope());
				for (var i = 0; i < parameters.Length; i++) {
					var (pName, pType) = parameters[i];
					scopes.Peek().Define(pName, new Symbol.Parameter(i, ParseType(pType)));
				}

				var fnIl = currentMethod.GetILGenerator();
				foreach (var stmt in body.body)
					BuildStatement(stmt);
				if (body.tail != null)
					BuildExpression(body.tail);
				fnIl.Emit(OpCodes.Ret);

				scopes.Pop();
				nameStack.Pop();
				currentMethod = savedMethod;
				break;
			}
			case Statement.ExpressionStatement(var expr): {
				var type = BuildExpression(expr);
				if (type != ExpressionType.Unit)
					il.Emit(OpCodes.Pop);
				break;
			}
			default:
				throw new CompileException($"Statement not supported: {statement}");
		}
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
					_ => ExpressionType.Int
				};
				if (op is BinOp.Eq or BinOp.Neq) {
					if (leftType != rightType)
						throw new CompileException(
							$"Type mismatch. Can't call operator '{op}' on operands of different types '{leftType}' and '{rightType}'");
				}
				else if (leftType != expectedType || rightType != expectedType) {
					throw new CompileException(
						$"Type mismatch. Can't call operator '{op}' on operands of different types '{leftType}' and '{rightType}'");
				}

				switch (op) {
					case BinOp.Add: il.Emit(OpCodes.Add); break;
					case BinOp.Sub: il.Emit(OpCodes.Sub); break;
					case BinOp.Mul: il.Emit(OpCodes.Mul); break;
					case BinOp.Div: il.Emit(OpCodes.Div); break;
					case BinOp.Eq:
						il.Emit(OpCodes.Ceq);
						return ExpressionType.Bool;
					case BinOp.Neq:
						il.Emit(OpCodes.Ceq);
						il.Emit(OpCodes.Ldc_I4_0);
						il.Emit(OpCodes.Ceq);
						return ExpressionType.Bool;
					case BinOp.And: il.Emit(OpCodes.And); break;
					case BinOp.Or: il.Emit(OpCodes.Or); break;
					default: throw new UnreachableException($"Operator '{op}' is not a valid binary operator.");
				}

				return leftType;
			}
			case Expression.UnaryOp(var op, var operand): {
				var type = BuildExpression(operand);

				switch (op) {
					case UnOp.Neg:
						if (type != ExpressionType.Int) {
							throw new CompileException(
								$"Invalid Operator. Can't call operator '{op}' on operand of type '{type}'");
						}

						il.Emit(OpCodes.Neg);
						return type;
					case UnOp.Not:
						if (type != ExpressionType.Bool) {
							throw new CompileException(
								$"Invalid Operator. Can't call operator '{op}' on operand of type '{type}'");
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
				if (symbol is not Symbol.Function function)
					throw new CompileException($"'{name}' is not a function");

				var paramTypes = function.parameterTypes;
				if (args.Length != paramTypes.Length)
					throw new CompileException($"'{name}' expects {paramTypes.Length} argument(s), got {args.Length}");

				for (var i = 0; i < args.Length; i++) {
					var argType = BuildExpression(args[i]);
					if (argType != paramTypes[i])
						throw new CompileException($"'{name}' argument {i + 1}: expected '{paramTypes[i]}', got '{argType}'");
				}
				il.Emit(OpCodes.Call, function.method);
				return function.returnType;
			}
			case Expression.Block(var body, var tail): {
				scopes.Push(new Scope());
				foreach (var stmt in body)
					BuildStatement(stmt);
				var returnType = tail != null ? BuildExpression(tail) : ExpressionType.Unit;
				scopes.Pop();
				return returnType;
			}
			default:
				throw new CompileException($"Expression not supported: {expression}");
		}
	}

	private Symbol? Resolve(string name) {
		foreach (var scope in scopes) {
			var symbol = scope.Get(name);
			if (symbol != null) return symbol;
		}

		return null;
	}

	private void Define(string name, Symbol symbol) =>
		scopes.Peek().Define(name, symbol);
}