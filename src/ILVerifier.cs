using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ILVerify;

namespace fluke;

internal static class ILVerifier {
	public static void Verify(byte[] peBytes) {
		using var resolver = new RuntimeResolver();
		var verifier = new Verifier(resolver);
		verifier.SetSystemModuleName(AssemblyNameInfo.Parse("System.Private.CoreLib"));

		using var pe = new PEReader(new MemoryStream(peBytes));
		var reader = pe.GetMetadataReader();
		var results = verifier.Verify(pe).ToList();

		if (results.Count > 0) {
			var messages = results.Select(r =>
				$"  {MethodName(reader, r.Method)}: {r.Message}");
			throw new CompileException("IL verification failed:\n" + string.Join("\n", messages));
		}
	}

	private static string MethodName(MetadataReader reader, MethodDefinitionHandle handle) =>
		handle.IsNil ? "?" : reader.GetString(reader.GetMethodDefinition(handle).Name);
}

internal class RuntimeResolver : IResolver, IDisposable {
	private readonly string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
	private readonly Dictionary<string, PEReader> cache = new();
	private bool disposed;

	public PEReader ResolveAssembly(AssemblyNameInfo assemblyName) {
		var name = assemblyName.Name!;
		if (cache.TryGetValue(name, out var cached))
			return cached;

		var path = Path.Combine(runtimeDir, name + ".dll");
		if (!File.Exists(path))
			throw new FileNotFoundException($"Cannot resolve assembly: {name}");

		var reader = new PEReader(File.OpenRead(path));
		cache[name] = reader;
		return reader;
	}

	public PEReader ResolveModule(AssemblyNameInfo referencingAssembly, string fileName) {
		throw new NotSupportedException(
			$"Multi-module assemblies are not supported (module '{fileName}' referenced by '{referencingAssembly.Name}')");
	}

	public void Dispose() {
		if (disposed) return;
		disposed = true;
		foreach (var reader in cache.Values)
			reader.Dispose();
		cache.Clear();
	}
}