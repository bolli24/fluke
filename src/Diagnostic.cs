using Pidgin;

namespace fluke;

public record Diagnostic(string Message, Span Span);

public static class SourceColumns {
	// Pidgin counts a tab as 4 columns, so map its 1-based column back to a character index.
	public static int ColToIndex(string line, int col) {
		var current = 1;
		for (var index = 0; index < line.Length; index++) {
			if (current >= col) return index;
			current += line[index] == '\t' ? 4 : 1;
		}

		return line.Length;
	}
}

public class CompileException(IReadOnlyList<Diagnostic> diagnostics)
	: Exception(string.Join("\n", diagnostics.Select(d => $"{d.Span}: {d.Message}"))) {
	public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;

	public CompileException(string message, Span span) : this([new Diagnostic(message, span)]) { }

	// spell renders a token sequence as it would appear in source
	internal static CompileException FromParseError<T>(ParseError<T> err, Span span,
		Func<IEnumerable<T>, string> spell) {
		var unexpected = err.Unexpected.HasValue ? spell([err.Unexpected.Value]) : "end of input";
		var expected = string.Join(", ", err.Expected
			.Select(e => e.Label ?? (e.IsEof ? "end of input" : spell(e.Tokens)))
			.Distinct());
		var message = expected == "" ? $"unexpected {unexpected}" : $"unexpected {unexpected}, expected {expected}";

		return new CompileException(message, span);
	}
}