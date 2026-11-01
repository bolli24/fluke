using System.Diagnostics;
using fluke.Hir;
using fluke.Mir;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using Pidgin;

namespace fluke;

internal static class CompilerPipeline {
	internal static string ReadSource(string path) {
		try {
			using var reader = new StreamReader(path);
			return reader.ReadToEnd();
		}
		catch (IOException e) {
			// Reported by Program.HandleException
			throw new IOException($"Could not read file: {e.Message}", e);
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
			Console.WriteLine(FormatHir(hir));
			Console.WriteLine();
		}

		return hir;
	}

	internal static MirProgram Lower(HirProgram hir, bool verbose = false) {
		var start = DateTime.Now;
		var mir = new MirBuilder(hir).Lower();

		if (verbose) {
			Console.WriteLine($"Lowered input in {(DateTime.Now - start).TotalMilliseconds:F0} ms");
			Console.WriteLine(FormatMir(mir));
			Console.WriteLine();
		}

		return mir;
	}

	// Main first, then the other functions, separated by blank lines
	internal static string FormatHir(HirProgram hir) =>
		string.Join("\n\n", hir.Functions.Prepend(hir.Main).Select(fn => $"{fn.Signature} {fn.Body}"));

	internal static string FormatMir(MirProgram mir) => string.Join("\n\n", mir.Bodies);

	internal static FlukeProgram Generate(MirProgram mir) => new MirEmitter(mir).EmitInMemory();

	internal static void GenerateToFile(MirProgram mir, string outputPath) =>
		new MirEmitter(mir).EmitToFile(outputPath);

	// ildasm-style text of the types in the assembly at path. Nested types are printed inside the
	// type that declares them.
	internal static string FormatCil(string path) {
		using var file = new PEFile(path);
		var reader = file.Metadata;
		var output = new PlainTextOutput { IndentationString = "\t" };
		var disassembler = new ReflectionDisassembler(output, CancellationToken.None);
		foreach (var handle in reader.TypeDefinitions) {
			var type = reader.GetTypeDefinition(handle);
			if (!type.GetDeclaringType().IsNil || reader.GetString(type.Name) == "<Module>") continue;
			disassembler.DisassembleType(file, handle);
			output.WriteLine();
		}

		return output.ToString().TrimEnd();
	}

	private static readonly string[] StageNames = ["Lex", "Parse", "Check", "Lower", "Emit"];

	// Compiles source 1 + warmIterations times in this process, writing the assembly to outputPath
	// each time, and prints how long each stage took. The first run is reported on its own because it
	// includes JIT compilation; the warm runs are summarised by median and minimum.
	internal static void TimeStages(string source, string outputPath, int warmIterations) {
		var runs = warmIterations + 1;
		var samples = new double[StageNames.Length + 1][]; // Last row is the total of each run
		for (var i = 0; i < samples.Length; i++) samples[i] = new double[runs];

		var stopwatch = new Stopwatch();
		for (var run = 0; run < runs; run++) {
			// Keep garbage from the previous run from being collected during this one
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();

			stopwatch.Restart();
			var (tokens, eof) = Lexer.Lex(source);
			samples[0][run] = Lap(stopwatch);
			var ast = Parser.Parse(tokens, eof);
			samples[1][run] = Lap(stopwatch);
			var hir = new Checker().Check(ast);
			samples[2][run] = Lap(stopwatch);
			var mir = new MirBuilder(hir).Lower();
			samples[3][run] = Lap(stopwatch);
			new MirEmitter(mir).EmitToFile(outputPath);
			samples[4][run] = Lap(stopwatch);

			for (var stage = 0; stage < StageNames.Length; stage++) samples[^1][run] += samples[stage][run];
		}

		Console.WriteLine($"1 cold run, {warmIterations} warm runs");
		Console.WriteLine($"{"Stage",-8}{"Cold",12}{"Warm median",14}{"Warm min",12}");
		for (var i = 0; i < samples.Length; i++) {
			var name = i < StageNames.Length ? StageNames[i] : "Total";
			var warm = samples[i][1..];
			Console.WriteLine(
				$"{name,-8}{Ms(samples[i][0]),12}{Ms(Median(warm)),14}{Ms(warm.Min()),12}");
		}

		return;

		static double Lap(Stopwatch stopwatch) {
			var ms = stopwatch.Elapsed.TotalMilliseconds;
			stopwatch.Restart();
			return ms;
		}

		static string Ms(double ms) => $"{ms:F2} ms";

		static double Median(double[] values) {
			var sorted = values.Order().ToArray();
			var mid = sorted.Length / 2;
			return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
		}
	}
}