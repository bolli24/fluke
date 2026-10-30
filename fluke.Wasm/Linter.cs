using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using fluke.Hir;

namespace fluke.Wasm;

// Offsets are UTF-16 indices into the source, as used by CodeMirror
public record LintDiagnostic(int From, int To, string Message, string Severity);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<LintDiagnostic>))]
internal partial class LintJsonContext : JsonSerializerContext;

[SupportedOSPlatform("browser")]
public static partial class Linter {
	// Returns the diagnostics as a JSON array, empty when the source checks cleanly
	[JSExport]
	public static string Check(string source) {
		List<LintDiagnostic> diagnostics;
		try {
			var (tokens, eof) = Lexer.Lex(source);
			new Checker().Check(Parser.Parse(tokens, eof));
			diagnostics = [];
		}
		catch (CompileException e) {
			var lineStarts = LineStarts(source);
			diagnostics = e.Diagnostics
				.Select(d => {
					var from = ToOffset(source, lineStarts, d.Span.Start);
					var to = Math.Max(from, ToOffset(source, lineStarts, d.Span.End));
					return new LintDiagnostic(from, to, d.Message, "error");
				})
				.ToList();
		}
		// A crash in the checker should not take the editor down with it
		catch (Exception e) {
			diagnostics = [new LintDiagnostic(0, 0, $"internal compiler error: {e.Message}", "error")];
		}

		return JsonSerializer.Serialize(diagnostics, LintJsonContext.Default.ListLintDiagnostic);
	}

	private static List<int> LineStarts(string source) {
		var starts = new List<int> { 0 };
		for (var i = 0; i < source.Length; i++) {
			if (source[i] == '\n') starts.Add(i + 1);
		}

		return starts;
	}

	private static int ToOffset(string source, List<int> lineStarts, Pidgin.SourcePos pos) {
		if (pos.Line > lineStarts.Count) return source.Length;

		var start = lineStarts[pos.Line - 1];
		var end = pos.Line < lineStarts.Count ? lineStarts[pos.Line] - 1 : source.Length;
		var line = source[start..end].TrimEnd('\r');
		return start + SourceColumns.ColToIndex(line, pos.Col);
	}
}