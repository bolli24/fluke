using Spectre.Console;

namespace fluke;

public class CompileException(string message, Span span) : Exception($"{span}: {message}") {
	public Span Span { get; } = span;

	public string FormatMessage(string source, string sourcePath) {
		var lines = source.Split(["\r\n", "\n"], StringSplitOptions.None);
		// A trailing newline yields an empty last element; don't count it as a real line.
		var lineCount = lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
		var digits = (Span.End.Line + 1).ToString().Length + 1;
		var formatted = $"ERROR: {message}\n";
		formatted += $"{new string(' ', digits)} ╭───┨ {sourcePath}:{Span.Start.Line}:{Span.Start.Col}\n";
		formatted += $"{(Span.Start.Line > 2 ? "⋮" : "").PadLeft(digits)} │\n";

		if (Span.Start.Line > 1)
			formatted += $"{(Span.Start.Line - 1).ToString().PadLeft(digits)} │ {lines[Span.Start.Line - 2]}\n";

		var lastStart = Span.Start;
		int i = Span.Start.Line;
		for (; i <= Span.End.Line; i++) {
			var line = lines[i - 1];
			formatted += $"{i.ToString().PadLeft(digits)} │ {line}\n";
			var startIndex = ColToIndex(line, lastStart.Col);
			var endIndex = lastStart.Line == Span.End.Line ? ColToIndex(line, Span.End.Col) : line.Length;
			var length = Math.Max(endIndex - startIndex, 1);
			// Mirror tabs from the source line so the carets line up with what was printed.
			var pad = string.Concat(line.Take(startIndex).Select(c => c == '\t' ? '\t' : ' '));
			formatted += $"{new string(' ', digits)} │ {pad}{new string('^', length)}\n";
			lastStart = lastStart.NewLine();
		}

		if (lineCount >= i)
			formatted += $"{i.ToString().PadLeft(digits)} │ {lines[i - 1]}\n";

		if (lineCount > i)
			formatted += $"{"⋮".PadLeft(digits)} │\n";

		return formatted;
	}

	// Pidgin counts a tab as 4 columns, so map its 1-based column back to a character index.
	private static int ColToIndex(string line, int col) {
		var current = 1;
		for (var index = 0; index < line.Length; index++) {
			if (current >= col) return index;
			current += line[index] == '\t' ? 4 : 1;
		}

		return line.Length;
	}
}