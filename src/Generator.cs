using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;

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

internal class Scope {
	private readonly Dictionary<string, (ExpressionType type, LocalBuilder local)> variables = new();

	public (ExpressionType type, LocalBuilder local)? GetVar(string name) {
		return variables.TryGetValue(name, out var value) ? value : null;
	}

	public void SetVar(string name, (ExpressionType type, LocalBuilder local) var) {
		variables[name] = var;
	}
}

public class Generator {
	private MethodBuilder currentMethod;
	private Scope currentScope = new();

	public FlukeProgram Generate(Ast ast) {
		var asmName = new AssemblyName("Fluke");
		var builder = AssemblyBuilder.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.Run);

		var mod = builder.DefineDynamicModule("Fluke");
		var type = mod.DefineType("Fluke.Program", TypeAttributes.Public | TypeAttributes.Class);
		currentMethod = type.DefineMethod("Main", MethodAttributes.Public | MethodAttributes.Static);

		var il = currentMethod.GetILGenerator();

		foreach (var statement in ast.Statements) {
			BuildStatement(statement);
		}

		il.Emit(OpCodes.Ret);

		var t = type.CreateType();
		return new FlukeProgram(t);
	}

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
				throw new Exception($"Too many arguments: {args.Length}. Intrinsic 'print' requires 0 or 1 arguments.");
			}
			case Statement.IntrinsicCall("assert", [var arg]): {
				var type = BuildExpression(arg);
				if (type != ExpressionType.Bool)
					throw new Exception($"Invalid type '{type}'. Expected: 'Bool'");

				var okLabel = il.DefineLabel();
				il.Emit(OpCodes.Brtrue, okLabel);
				il.Emit(OpCodes.Ldstr, "Assertion failed");
				il.Emit(OpCodes.Newobj, typeof(Exception).GetConstructor([typeof(string)])!);
				il.Emit(OpCodes.Throw);
				il.MarkLabel(okLabel);

				break;
			}
			case Statement.IntrinsicCall("assert", var args): {
				throw new Exception(
					$"Wrong number of arguments: {args.Length}. Intrinsic 'assert' requires 1 argument.");
			}
			case Statement.VariableBinding(var name, var expression): {
				var type = BuildExpression(expression);
				var local = il.DeclareLocal(typeof(int));
				il.Emit(OpCodes.Stloc, local);

				currentScope.SetVar(name, (type, local));
				break;
			}
			default:
				throw new Exception($"Statement not supported: {statement}");
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
				var left_type = BuildExpression(left);
				var right_type = BuildExpression(right);

				var expectedType = op switch {
					BinOp.And or BinOp.Or => ExpressionType.Bool,
					_ => ExpressionType.Int
				};
				if (op is BinOp.Eq or BinOp.Neq) {
					if (left_type != right_type)
						throw new Exception(
							$"Type mismatch. Can't call operator '{op}' on operands of different types '{left_type}' and '{right_type}'");
				}
				else if (left_type != expectedType || right_type != expectedType) {
					throw new Exception(
						$"Type mismatch. Can't call operator '{op}' on operands of different types '{left_type}' and '{right_type}'");
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

				return left_type;
			}
			case Expression.UnaryOp(var op, var operand): {
				var type = BuildExpression(operand);

				switch (op) {
					case UnOp.Neg:
						if (type != ExpressionType.Int) {
							throw new Exception(
								$"Invalid Operator. Can't call operator '{op}' on operand of type '{type}'");
						}

						il.Emit(OpCodes.Neg);
						return type;
					case UnOp.Not:
						if (type != ExpressionType.Bool) {
							throw new Exception(
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
				var variable = currentScope.GetVar(name);
				if (variable is null) {
					throw new Exception($"Use of undeclared identifier '{name}'");
				}

				var (type, local) = variable.Value;
				il.Emit(OpCodes.Ldloc, local);

				return type;
			}
			default:
				throw new Exception($"Expression not supported: {expression}");
		}
	}
}