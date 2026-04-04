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
			case Statement.IntrinsicCall("print", var arg): {
				var value = arg ?? "";
				il.Emit(OpCodes.Ldstr, value);
				il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", new[] { typeof(string) }));
				break;
			}
			default:
				throw new Exception($"Statement not supported: {statement}");
		}
	}
}