using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace fluke.Tests;

public class ILVerifierTests {
	[Fact]
	public void Verify_RejectsBadIL() {
		var asmName = new AssemblyName("BadILTest");
		var builder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
		var mod = builder.DefineDynamicModule("BadILTest");
		var type = mod.DefineType("Bad", TypeAttributes.Public | TypeAttributes.Class);
		var method = type.DefineMethod("Broken", MethodAttributes.Public | MethodAttributes.Static,
			typeof(int), Type.EmptyTypes);

		// Method is declared to return int but emits no value before ret
		var il = method.GetILGenerator();
		il.Emit(OpCodes.Ret);

		type.CreateType();

		var peBytes = BuildPeBytes(builder);

		var ex = Assert.Throws<ILVerificationException>(() => ILVerifier.Verify(peBytes));
		Assert.Contains("IL verification failed", ex.Message);
		Assert.Contains("Broken", ex.Message);
	}

	private static byte[] BuildPeBytes(PersistedAssemblyBuilder builder) {
		var metadata = builder.GenerateMetadata(out var ilStream, out var fieldData);
		var peBuilder = new ManagedPEBuilder(
			header: new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage),
			metadataRootBuilder: new MetadataRootBuilder(metadata),
			ilStream: ilStream,
			mappedFieldData: fieldData);
		var blob = new BlobBuilder();
		peBuilder.Serialize(blob);
		return blob.ToArray();
	}
}