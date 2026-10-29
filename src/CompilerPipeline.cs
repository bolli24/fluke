using fluke.Hir;
using fluke.Mir;
using Pidgin;

namespace fluke;

internal static class CompilerPipeline {
	internal static string ReadSource(string path) {
		try {
			using var reader = new StreamReader(path);
			return reader.ReadToEnd();
		}
		catch (IOException e) {
			Console.Error.WriteLine($"Could not read file: {e.Message}");
			throw;
		}
	}

	internal static Ast LexAndParse(string text, bool verbose = false) {
		if (verbose) Console.WriteLine();

		var start = DateTime.Now;
		var (tokens, eof) = Lexer.Lex(text);

		if (verbose) {
			Console.WriteLine($"Lexed input in {(DateTime.Now - start).TotalMilliseconds:F0} ms");
			foreach (var token in tokens) Console.WriteLine(token);
			Console.WriteLine();
		}

		start = DateTime.Now;
		var ast = Parser.Parse(tokens, eof);

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

	internal static MirProgram Lower(HirProgram hir, bool verbose = false) {
		var start = DateTime.Now;
		var mir = new MirBuilder(hir).Lower();

		if (verbose) {
			Console.WriteLine($"Lowered input in {(DateTime.Now - start).TotalMilliseconds:F0} ms");
			foreach (var body in mir.Bodies) Console.WriteLine(body);
			Console.WriteLine();
		}

		return mir;
	}

	internal static FlukeProgram Generate(MirProgram mir) => new MirEmitter(mir).EmitInMemory();

	internal static void GenerateToFile(MirProgram mir, string outputPath) =>
		new MirEmitter(mir).EmitToFile(outputPath);
}