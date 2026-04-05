using Spectre.Console.Cli;

namespace fluke.Commands;

internal sealed class TestCommand : Command<TestCommand.Settings> {
	internal sealed class Settings : CommandSettings {
		[CommandArgument(0, "<path>")]
		public string Path { get; init; } = "";
	}

	protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken) {
		Console.WriteLine("Test not yet implemented.");
		return 0;
	}
}
