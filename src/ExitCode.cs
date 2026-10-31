namespace fluke;

// Exit codes of the fluke CLI, so callers like the playground can tell a broken program from a broken compiler
internal static class ExitCode {
	public const int Success = 0;

	// The source couldn't be read or the output couldn't be written
	public const int Io = 1;

	// Invalid command-line arguments
	public const int Usage = 2;

	// The program has lexing, parsing or type errors. Used by build, check and run.
	public const int CompileError = 3;

	// A crash in the compiler itself, e.g. invalid MIR or IL
	public const int InternalError = 4;

	// `run`: the program threw, e.g. a failed :assert. A program calling :exit ends with its own code.
	public const int ProgramFailed = 5;

	// `test`: some tests failed or didn't compile
	public const int TestsFailed = 6;
}
