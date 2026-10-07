using Spectre.Console.Cli;

namespace fluke.Commands;

internal sealed class BuildCommand : Command<BuildCommand.Settings> {
	internal sealed class Settings : CommandSettings {
		[CommandArgument(0, "<file>")] public string SourceFile { get; init; } = "";
		[CommandOption("--verbose")] public bool Verbose { get; init; }
	}

	protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken) {
		try {
			var ast = CompilerPipeline.LexAndParse(settings.SourceFile, settings.Verbose);

			var basename = Path.GetFileNameWithoutExtension(settings.SourceFile);
			var targetDir = Path.Combine(Directory.GetCurrentDirectory(), "target");
			Directory.CreateDirectory(targetDir);

			var dllPath = Path.Combine(targetDir, basename + ".dll");
			var runtimeConfigPath = Path.Combine(targetDir, basename + ".runtimeconfig.json");

			var start = DateTime.Now;
			new Generator().GenerateToFile(ast, dllPath);
			Console.WriteLine($"Built {dllPath} in {(DateTime.Now - start).Milliseconds} ms");

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

			return 0;
		}
		catch (CompileException e) {
			var source = File.ReadAllText(settings.SourceFile);
			foreach (var diagnostic in e.Diagnostics) {
				Console.Error.WriteLine(diagnostic.FormatMessage(source, settings.SourceFile));
			}
			return 1;
		}
	}
}