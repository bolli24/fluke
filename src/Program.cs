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

		var tokens = Lexer.Lex(text);
		foreach (var token in tokens) {
			Console.WriteLine(token);
		}
		Console.WriteLine();
		
		var ast = Parser.Parse(text);
		foreach (var statement in ast.Statements) {
			Console.WriteLine(statement);
		}
	}
}