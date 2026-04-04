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
	String
}

static class ExpressionTypeExt {
	public static Type AsType(this ExpressionType type) {
		return type switch {
			ExpressionType.Int => typeof(int),
			ExpressionType.String => typeof(string),
			_ => throw new UnreachableException(),
		};
	}
}

public class Generator {
	private MethodBuilder currentMethod;

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
			case Statement.IntrinsicCall("print", null): {
				il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", Type.EmptyTypes));
				break;
			}
			case Statement.IntrinsicCall("print", var arg): {
				var type = BuildExpression(arg);
				il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", new[] { type.AsType() }));
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
			case Expression.BinaryOp(var left, var op, var right): {
				var left_type = BuildExpression(left);
				var right_type = BuildExpression(right);

				if (left_type != ExpressionType.Int || right_type != ExpressionType.Int)
					throw new Exception(
						$"Type mismatch. Can't call operator '{op}' on operands of type '{left_type}' and '{right_type}'");

				switch (op) {
					case Op.Add: il.Emit(OpCodes.Add); break;
					case Op.Sub: il.Emit(OpCodes.Sub); break;
					case Op.Mul: il.Emit(OpCodes.Mul); break;
					case Op.Div: il.Emit(OpCodes.Div); break;
					default: throw new Exception($"Operator not supported: {op}");
				}
				
				return left_type;
			}
			default:
				throw new Exception($"Expression not supported: {expression}");
		}
	}
}