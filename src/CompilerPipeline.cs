using fluke.Hir;
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
			Console.WriteLine($"Lexed input in {(DateTime.Now - start).TotalMilliseconds:F0} ms");
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
			Console.WriteLine($"Parsed input in {(DateTime.Now - start).TotalMilliseconds:F0} ms");
			foreach (var statement in ast.statements) Console.WriteLine(statement);
			Console.WriteLine();
		}

		return ast;
	}

	internal static HirProgram Check(Ast ast, bool verbose = false) {
		var start = DateTime.Now;
		var hir = new Checker().Check(ast);

		if (verbose) {
			Console.WriteLine($"Checked input in {(DateTime.Now - start).TotalMilliseconds:F0} ms");
			foreach (var fn in hir.Functions.Prepend(hir.Main)) Console.WriteLine($"{fn.Signature} {fn.Body}");
			Console.WriteLine();
		}

		return hir;
	}

	// Code generation is being rebuilt on top of HIR and MIR. The old AST-based Generator is kept
	// for reference only.
	internal static FlukeProgram Generate(HirProgram hir) =>
		throw new NotImplementedException("code generation is not implemented yet (pending MIR)");

	internal static void GenerateToFile(HirProgram hir, string outputPath) =>
		throw new NotImplementedException("code generation is not implemented yet (pending MIR)");
}