using Spectre.Console.Cli;
using fluke.Commands;

namespace fluke;

internal static class Program {
	private static int Main(string[] args) {
		var app = new CommandApp();
		app.Configure(config => {
			config.AddCommand<RunCommand>("run")
			      .WithDescription("Compile and run a Fluke source file.");
			config.AddCommand<BuildCommand>("build")
			      .WithDescription("Compile a Fluke source file to target/<name>.dll.");
			config.AddCommand<TestCommand>("test")
			      .WithDescription("Run tests for a file or folder.");
		});
		return app.Run(args);
	}
}
