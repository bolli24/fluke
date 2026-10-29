using Spectre.Console.Cli;

namespace fluke.Commands;

internal sealed class CheckCommand : Command<CheckCommand.Settings> {
	internal sealed class Settings : CommandSettings {
		[CommandArgument(0, "<file>")] public string SourceFile { get; init; } = "";
		[CommandOption("--verbose")] public bool Verbose { get; init; }
	}

	protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken) {
		var source = CompilerPipeline.ReadSource(settings.SourceFile);
		try {
			var start = DateTime.Now;
			var ast = CompilerPipeline.LexAndParse(source, settings.Verbose);
			CompilerPipeline.Check(ast, settings.Verbose);
			Console.WriteLine(
				$"Checked {settings.SourceFile} in {(DateTime.Now - start).TotalMilliseconds:F0} ms, no errors found");
			return 0;
		}
		catch (CompileException e) {
			foreach (var diagnostic in e.Diagnostics) {
				diagnostic.Print(Diagnostic.ErrorConsole, source, settings.SourceFile);
			}

			return 1;
		}
	}
}