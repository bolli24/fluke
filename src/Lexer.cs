using System.Diagnostics;
using Antlr4.Runtime;

namespace fluke;

public abstract record Token {
	public record Colon : Token;

	public record Identifier(string name) : Token;

	public sealed override string ToString() {
		return this switch {
			Colon => "Colon",
			Identifier(var name) => $"Identifier({name})",
			_ => throw new UnreachableException()
		};
	}
}

public class Lexer {
	public static List<Token> Lex(string input) {
		var stream = CharStreams.fromString(input);
		var antlrLexer = new FlukeLexer(stream);
		var tokens = new List<Token>();

		while (true) {
			var token = antlrLexer.NextToken();
			if (token.Type == -1) break; // EOF

			tokens.Add(token.Type switch {
				FlukeLexer.COLON      => new Token.Colon(),
				FlukeLexer.IDENTIFIER => new Token.Identifier(token.Text),
				_                     => throw new UnreachableException()
			});
		}

		return tokens;
	}
}
