using Antlr4.Runtime;

namespace fluke;

public class ParseException(string message) : Exception(message);

public static class Parser {
	public static FlukeProgram Parse(string input) {
		var stream = CharStreams.fromString(input);
		var lexer = new FlukeLexer(stream);
		var tokens = new CommonTokenStream(lexer);
		var parser = new FlukeParser(tokens);
		var tree = parser.program();

		var statements = tree.statement()
			.Select(s => (Statement)new Statement.IntrinsicCall(s.IDENTIFIER().GetText()))
			.ToList();

		return new FlukeProgram(statements);
	}
}
