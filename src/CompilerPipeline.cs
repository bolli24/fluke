using Pidgin;

namespace fluke;

internal static class CompilerPipeline {
	internal static Ast LexAndParse(string path, bool verbose = false) {
		string text;
		try {
			using var reader = new StreamReader(path);
			text = reader.ReadToEnd();
		}
		catch (IOException e) {
			Console.Error.WriteLine($"Could not read file: {e.Message}");
			throw;
		}

		if (verbose) Console.WriteLine();

		var start = DateTime.Now;
		List<Token> tokens;
		SourcePos eof;
		try {
			(tokens, eof) = Lexer.Lex(text);
		}
		catch (ParseException<char> e) {
			Console.Error.WriteLine($"Lex error: {e.Message}");
			throw;
		}

		if (verbose) {
			Console.WriteLine($"Lexed input in {(DateTime.Now - start).Milliseconds} ms");
			foreach (var token in tokens) Console.WriteLine(token);
			Console.WriteLine();
		}

		start = DateTime.Now;
		Ast ast;
		try {
			ast = Parser.Parse(tokens, eof);
		}
		catch (ParseException<Token> e) {
			Console.Error.WriteLine($"Parse error: {e.Message}");
			throw;
		}

		if (verbose) {
			Console.WriteLine($"Parsed input in {(DateTime.Now - start).Milliseconds} ms");
			foreach (var statement in ast.statements) Console.WriteLine(statement);
			Console.WriteLine();
		}

		return ast;
	}
}