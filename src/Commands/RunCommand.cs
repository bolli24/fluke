using Spectre.Console.Cli;

namespace fluke.Commands;

internal sealed class RunCommand : Command<RunCommand.Settings> {
	internal sealed class Settings : CommandSettings {
		[CommandArgument(0, "<file>")] public string SourceFile { get; init; } = "";
		[CommandOption("--verbose")] public bool Verbose { get; init; }
	}

	protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken) {
		var source = CompilerPipeline.ReadSource(settings.SourceFile);
		try {
			var ast = CompilerPipeline.LexAndParse(source, settings.Verbose);
			var hir = CompilerPipeline.Check(ast, settings.Verbose);
			var mir = CompilerPipeline.Lower(hir, settings.Verbose);
			var start = DateTime.Now;
			var program = CompilerPipeline.Generate(mir);
			Console.WriteLine($"Built executable in {(DateTime.Now - start).TotalMilliseconds:F0} ms\n");
			program.Run();
			return 0;
		}
		catch (CompileException e) {
			foreach (var diagnostic in e.Diagnostics) {
				diagnostic.Print(DiagnosticRenderer.ErrorConsole, source, settings.SourceFile);
			}

			return 1;
		}
		catch (Exception e) {
			Console.Error.WriteLine(e);
			return 1;
		}
	}
}