using Spectre.Console.Cli;

namespace fluke.Commands;

internal sealed class TestCommand : Command<TestCommand.Settings> {
	internal sealed class Settings : CommandSettings {
		[CommandArgument(0, "<path>")] public string Path { get; init; } = "";
		[CommandOption("--output")] public bool Output { get; init; }
		[CommandOption("--verbose")] public bool Verbose { get; init; }
	}

	protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken) {
		var files = GetFiles(settings.Path, ".fl");
		var passed = 0;
		var failed = 0;

		foreach (var file in files) {
			var originalOut = Console.Out;
			var source = CompilerPipeline.ReadSource(file);
			try {
				var ast = CompilerPipeline.LexAndParse(source, settings.Verbose);
				var hir = CompilerPipeline.Check(ast, settings.Verbose);
				var mir = CompilerPipeline.Lower(hir, settings.Verbose);
				var program = CompilerPipeline.Generate(mir);
				if (!settings.Output) Console.SetOut(TextWriter.Null);
				program.Run();
				Console.SetOut(originalOut);
				Console.WriteLine($"PASS {file}");
				passed++;
			}
			catch (CompileException e) {
				Console.SetOut(originalOut);
				Console.WriteLine($"FAIL {file}");

				foreach (var diagnostic in e.Diagnostics) {
					diagnostic.Print(DiagnosticRenderer.CreateConsole(Console.Out), source, file);
				}

				failed++;
			}
			catch (Exception e) {
				Console.SetOut(originalOut);
				Console.WriteLine($"FAIL {file}");
				Console.WriteLine(e.ToString());
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