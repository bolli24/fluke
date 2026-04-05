using Spectre.Console.Cli;

namespace fluke.Commands;

internal sealed class RunCommand : Command<RunCommand.Settings> {
	internal sealed class Settings : CommandSettings {
		[CommandArgument(0, "<file>")]
		public string SourceFile { get; init; } = "";
	}

	protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken) {
		try {
			var ast = CompilerPipeline.LexAndParse(settings.SourceFile);
			var start = DateTime.Now;
			var program = new Generator().GenerateInMemory(ast);
			Console.WriteLine($"Built executable in {(DateTime.Now - start).Milliseconds} ms\n");
			program.Run();
			return 0;
		} catch (Exception e) {
			Console.Error.WriteLine(e.Message);
			return 1;
		}
	}
}
