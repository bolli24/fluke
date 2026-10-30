using Spectre.Console;

namespace fluke;

public static class DiagnosticRenderer {
	public static readonly IAnsiConsole ErrorConsole = CreateConsole(Console.Error);

	// Colors are dropped automatically when the writer is redirected or NO_COLOR is set.
	public static IAnsiConsole CreateConsole(TextWriter writer) {
		var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer) });
		// Never wrap: source lines must stay intact for the carets to line up
		console.Profile.Width = int.MaxValue;
		return console;
	}

	public static void Print(this Diagnostic diagnostic, IAnsiConsole console, string source, string sourcePath) =>
		console.MarkupLine(diagnostic.FormatMessage(source, sourcePath));

	// Returns Spectre markup using rustc's colors: red error and carets, blue gutter.
	public static string FormatMessage(this Diagnostic diagnostic, string source, string sourcePath) {
		var span = diagnostic.Span;
		var lines = source.Split(["\r\n", "\n"], StringSplitOptions.None);
		// A trailing newline yields an empty last element; don't count it as a real line.
		var lineCount = lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
		var digits = (span.End.Line + 1).ToString().Length + 1;

		string Gutter(string label) => $"[blue]{label.PadLeft(digits)} │[/]";

		string SourceLine(int lineNumber) =>
			$"{Gutter(lineNumber.ToString())} {Markup.Escape(lines[lineNumber - 1])}\n";

		// Editors count a tab as one column, unlike Pidgin
		var col = SourceColumns.ColToIndex(lines[span.Start.Line - 1], span.Start.Col) + 1;
		var formatted = $"[bold red]error:[/] [bold]{Markup.Escape(diagnostic.Message)}[/]\n";
		formatted +=
			$"[blue]{new string(' ', digits)} ╭───┨[/] {Markup.Escape(sourcePath)}:{span.Start.Line}:{col}\n";
		formatted += $"{Gutter(span.Start.Line > 2 ? "⋮" : "")}\n";

		if (span.Start.Line > 1)
			formatted += SourceLine(span.Start.Line - 1);

		var lastStart = span.Start;
		int i = span.Start.Line;
		for (; i <= span.End.Line; i++) {
			var line = lines[i - 1];
			formatted += SourceLine(i);
			var startIndex = SourceColumns.ColToIndex(line, lastStart.Col);
			var endIndex = lastStart.Line == span.End.Line
				? SourceColumns.ColToIndex(line, span.End.Col)
				: line.Length;
			var length = Math.Max(endIndex - startIndex, 1);
			// Mirror tabs from the source line so the carets line up with what was printed.
			var pad = string.Concat(line.Take(startIndex).Select(c => c == '\t' ? '\t' : ' '));
			formatted += $"{Gutter("")} {pad}[bold red]{new string('^', length)}[/]\n";
			lastStart = lastStart.NewLine();
		}

		if (lineCount >= i)
			formatted += SourceLine(i);

		if (lineCount > i)
			formatted += $"{Gutter("⋮")}\n";

		return formatted;
	}
}