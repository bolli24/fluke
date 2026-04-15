using Spectre.Console.Cli;

namespace fluke.Commands;

internal sealed class TestCommand : Command<TestCommand.Settings> {
	internal sealed class Settings : CommandSettings {
		[CommandArgument(0, "<path>")] public string Path { get; init; } = "";
		[CommandOption("--output")] public bool Output { get; init; }
	}

	protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken) {
		var files = GetFiles(settings.Path, ".fl");
		var passed = 0;
		var failed = 0;

		foreach (var file in files) {
			var expectError = System.IO.Path.GetFileNameWithoutExtension(file).EndsWith("_error");
			var originalOut = Console.Out;
			if (!settings.Output) Console.SetOut(TextWriter.Null);
			try {
				var ast = CompilerPipeline.LexAndParse(file);
				var program = new Generator().GenerateInMemory(ast);
				program.Run();
				Console.SetOut(originalOut);
				if (expectError) {
					Console.WriteLine($"FAIL {file}");
					Console.WriteLine($"     Expected a compile error but succeeded");
					failed++;
				} else {
					Console.WriteLine($"PASS {file}");
					passed++;
				}
			} catch (CompileException e) {
				Console.SetOut(originalOut);
				if (expectError) {
					Console.WriteLine($"PASS {file}");
					Console.WriteLine($"     {e.Message}");
					passed++;
				} else {
					Console.WriteLine($"FAIL {file}");
					Console.WriteLine($"     {e.Message}");
					failed++;
				}
			} catch (Exception e) {
				Console.SetOut(originalOut);
				Console.WriteLine($"FAIL {file}");
				Console.WriteLine($"     {e.Message}");
				failed++;
			}
		}

		Console.WriteLine();
		Console.WriteLine($"{passed} passed, {failed} failed.");
		return failed > 0 ? 1 : 0;
	}

	private static IEnumerable<string> GetFiles(string path, string extension) {
		if (File.Exists(path)) {
			if (Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase)) {
				return [path];
			}
			return [];
		}
		if (Directory.Exists(path)) {
			return Directory.EnumerateFiles(path, $"*{extension}", SearchOption.AllDirectories);
		}
		throw new ArgumentException($"Path does not exist: {path}");
	}
}