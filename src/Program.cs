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
			config.AddCommand<CheckCommand>("check")
				.WithDescription("Check a Fluke source file for errors without generating code.");
			config.AddCommand<TestCommand>("test")
				.WithDescription("Run tests for a file or folder.");
			config.SetExceptionHandler((exception, _) => HandleException(exception));
		});
		return app.Run(args);
	}

	// Exceptions the commands don't handle themselves; see ExitCode
	private static int HandleException(Exception exception) {
		switch (exception) {
			case CommandAppException:
				Console.Error.WriteLine($"error: {exception.Message}");
				return ExitCode.Usage;
			case IOException or UnauthorizedAccessException:
				Console.Error.WriteLine($"error: {exception.Message}");
				return ExitCode.Io;
			default:
				Console.Error.WriteLine($"internal compiler error: {exception}");
				return ExitCode.InternalError;
		}
	}
}