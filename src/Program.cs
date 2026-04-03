using Pidgin;

namespace fluke;

internal static class Program {
	private static void Main(string[] args) {
		if (args.Length != 1) {
			Console.WriteLine("Usage: fluke <path>");
			return;
		}

		var path = args[0];
		string text;

		try {
			using StreamReader reader = new(path);
			text = reader.ReadToEnd();
		}
		catch (IOException e) {
			Console.WriteLine("The file could not be read:");
			Console.WriteLine(e.Message);
			return;
		}

		Console.WriteLine(text);
		Console.WriteLine();

		var start = DateTime.Now;
		List<Token> tokens;
		try {
			tokens = Lexer.Lex(text);
		} catch (ParseException<char> e) {
			Console.Error.WriteLine($"Lex error: {e.Message}");
			return;
		}
		Console.WriteLine($"Lexed input in {(DateTime.Now - start).Milliseconds} ms");
		foreach (var token in tokens) {
			Console.WriteLine(token);
		}

		Console.WriteLine();

		start = DateTime.Now;
		FlukeProgram ast;
		try {
			ast = Parser.Parse(tokens);
		} catch (ParseException<Token> e) {
			Console.Error.WriteLine($"Parse error: {e.Message}");
			return;
		}
		Console.WriteLine($"Parsed input in {(DateTime.Now - start).Milliseconds} ms");
		foreach (var statement in ast.Statements) {
			Console.WriteLine(statement);
		}
	}
}