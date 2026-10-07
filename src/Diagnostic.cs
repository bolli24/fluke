using Spectre.Console;

namespace fluke;

public record Diagnostic(string Message, Span Span) {
	public static readonly IAnsiConsole ErrorConsole = CreateConsole(Console.Error);

	// Colors are dropped automatically when the writer is redirected or NO_COLOR is set.
	public static IAnsiConsole CreateConsole(TextWriter writer) {
		var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer) });
		// Never wrap: source lines must stay intact for the carets to line up
		console.Profile.Width = int.MaxValue;
		return console;
	}

	public void Print(IAnsiConsole console, string source, string sourcePath) =>
		console.MarkupLine(FormatMessage(source, sourcePath));

	// Returns Spectre markup using rustc's colors: red error and carets, blue gutter.
	public string FormatMessage(string source, string sourcePath) {
		var lines = source.Split(["\r\n", "\n"], StringSplitOptions.None);
		// A trailing newline yields an empty last element; don't count it as a real line.
		var lineCount = lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
		var digits = (Span.End.Line + 1).ToString().Length + 1;

		string Gutter(string label) => $"[blue]{label.PadLeft(digits)} │[/]";

		string SourceLine(int lineNumber) =>
			$"{Gutter(lineNumber.ToString())} {Markup.Escape(lines[lineNumber - 1])}\n";

		// Editors count a tab as one column, unlike Pidgin
		var col = ColToIndex(lines[Span.Start.Line - 1], Span.Start.Col) + 1;
		var formatted = $"[bold red]ERROR:[/] [bold]{Markup.Escape(Message)}[/]\n";
		formatted +=
			$"[blue]{new string(' ', digits)} ╭───┨[/] {Markup.Escape(sourcePath)}:{Span.Start.Line}:{col}\n";
		formatted += $"{Gutter(Span.Start.Line > 2 ? "⋮" : "")}\n";

		if (Span.Start.Line > 1)
			formatted += SourceLine(Span.Start.Line - 1);

		var lastStart = Span.Start;
		int i = Span.Start.Line;
		for (; i <= Span.End.Line; i++) {
			var line = lines[i - 1];
			formatted += SourceLine(i);
			var startIndex = ColToIndex(line, lastStart.Col);
			var endIndex = lastStart.Line == Span.End.Line ? ColToIndex(line, Span.End.Col) : line.Length;
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

public class CompileException(IReadOnlyList<Diagnostic> diagnostics)
	: Exception(string.Join("\n", diagnostics.Select(d => $"{d.Span}: {d.Message}"))) {
	public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;

	public CompileException(string message, Span span) : this([new Diagnostic(message, span)]) { }
}