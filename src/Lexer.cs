using System.Diagnostics;
using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

namespace fluke;

public abstract record Token {
	public record Colon : Token;

	public record Semicolon : Token;

	public record Identifier(string name) : Token;

	public record String(string value) : Token;

	public sealed override string ToString() {
		return this switch {
			Colon => "Colon",
			Semicolon => "Semicolon",
			Identifier(var name) => $"Identifier({name})",
			String(var value) => $"String({value})",
			_ => throw new UnreachableException()
		};
	}
}

public static class Lexer {
	private static readonly Parser<char, Token> Colon =
		from _ in Char(':')
		select (Token)new Token.Colon();
	
	private static readonly Parser<char, Token> Semicolon =
		from _ in Char(';')
		select (Token)new Token.Semicolon();

	private static readonly Parser<char, Token> String =
		from _ in Char('"')
		from value in Try(Any.Where(c => c != '"' && c != '\r' && c != '\n')).ManyString()
		from __ in Char('"').Labelled("closing '\"' (unterminated string)")
		select (Token)new Token.String(value);

	private static readonly Parser<char, Token> Identifier =
		from first in Letter.Or(Char('_'))
		from rest in LetterOrDigit.Or(Char('_')).ManyString()
		select (Token)new Token.Identifier(first + rest);

	private static readonly Parser<char, List<Token>> Tokens =
		from _leading in Whitespace.SkipMany()
		from tokens in OneOf(Colon, Semicolon, Identifier, String).Before(Whitespace.SkipMany()).Many()
		from _end in End
		select tokens.ToList();

	public static List<Token> Lex(string input) =>
		Tokens.ParseOrThrow(input);
}