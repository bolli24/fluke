using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace fluke.Commands;

internal sealed class BuildCommand : Command<BuildCommand.Settings> {
	internal sealed class Settings : CommandSettings {
		[CommandArgument(0, "<file>")] public string SourceFile { get; init; } = "";
		[CommandOption("--verbose")] public bool Verbose { get; init; }

		[CommandOption("--timings")]
		[Description("Compile repeatedly and print per-stage times, cold and warm.")]
		public bool Timings { get; init; }

		[CommandOption("--iterations <N>")]
		[Description("Number of warm runs for --timings.")]
		[DefaultValue(20)]
		public int Iterations { get; init; }

		public override ValidationResult Validate() =>
			Iterations < 1 ? ValidationResult.Error("--iterations must be at least 1") : ValidationResult.Success();
	}

	protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken) {
		var source = CompilerPipeline.ReadSource(settings.SourceFile);
		try {
			var basename = Path.GetFileNameWithoutExtension(settings.SourceFile);
			var targetDir = Path.Combine(Directory.GetCurrentDirectory(), "target");
			Directory.CreateDirectory(targetDir);

			var dllPath = Path.Combine(targetDir, basename + ".dll");
			var runtimeConfigPath = Path.Combine(targetDir, basename + ".runtimeconfig.json");

			if (settings.Timings) {
				// Ignores --verbose: dumping every stage on every run would swamp the timings
				CompilerPipeline.TimeStages(source, dllPath, settings.Iterations);
				Console.WriteLine($"Built {dllPath}");
			}
			else {
				var ast = CompilerPipeline.LexAndParse(source, settings.Verbose);
				var hir = CompilerPipeline.Check(ast, settings.Verbose);
				var mir = CompilerPipeline.Lower(hir, settings.Verbose);

				var start = DateTime.Now;
				CompilerPipeline.GenerateToFile(mir, dllPath);
				Console.WriteLine($"Built {dllPath} in {(DateTime.Now - start).TotalMilliseconds:F0} ms");
			}

			File.WriteAllText(runtimeConfigPath, """
			                                     {
			                                       "runtimeOptions": {
			                                         "tfm": "net10.0",
			                                         "framework": {
			                                           "name": "Microsoft.NETCore.App",
			                                           "version": "10.0.0"
			                                         }
			                                       }
			                                     }
			                                     """);
			Console.WriteLine($"Wrote {runtimeConfigPath}");

			return ExitCode.Success;
		}
		catch (CompileException e) {
			foreach (var diagnostic in e.Diagnostics) {
				diagnostic.Print(DiagnosticRenderer.ErrorConsole, source, settings.SourceFile);
			}

			return ExitCode.CompileError;
		}
	}
}