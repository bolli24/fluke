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

		[CommandOption("--emit <VIEWS>")]
		[Description("Write intermediate representations to --emit-dir, comma-separated: hir, mir.")]
		public string? Emit { get; init; }

		[CommandOption("--emit-dir <DIR>")]
		[Description("Directory for --emit, one <view>.txt per view.")]
		[DefaultValue("target")]
		public string EmitDir { get; init; } = "target";

		internal static readonly string[] KnownViews = ["hir", "mir"];

		// Empty when --emit is missing or empty, so scripts can always pass it
		public HashSet<string> Views =>
			Emit?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet() ?? [];

		public override ValidationResult Validate() {
			if (Iterations < 1)
				return ValidationResult.Error("--iterations must be at least 1");
			if (Views.FirstOrDefault(v => !KnownViews.Contains(v)) is { } unknown)
				return ValidationResult.Error(
					$"unknown view '{unknown}' for --emit, expected {string.Join(", ", KnownViews)}");
			if (Timings && Views.Count > 0)
				return ValidationResult.Error("--emit can't be combined with --timings");
			return ValidationResult.Success();
		}
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
				var views = settings.Views;
				string ViewPath(string view) => Path.Combine(settings.EmitDir, view + ".txt");
				if (views.Count > 0) Directory.CreateDirectory(settings.EmitDir);
				// Views are written as soon as their stage succeeds, so a failed build must not leave
				// those of an earlier build behind
				foreach (var view in views) File.Delete(ViewPath(view));

				var ast = CompilerPipeline.LexAndParse(source, settings.Verbose);
				var hir = CompilerPipeline.Check(ast, settings.Verbose);
				if (views.Contains("hir")) File.WriteAllText(ViewPath("hir"), CompilerPipeline.FormatHir(hir) + "\n");
				var mir = CompilerPipeline.Lower(hir, settings.Verbose);
				if (views.Contains("mir")) File.WriteAllText(ViewPath("mir"), CompilerPipeline.FormatMir(mir) + "\n");

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