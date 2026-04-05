// Token + Lexer

using System.Diagnostics;
using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

namespace fluke;

public abstract record Token {
	public record Colon : Token;

	public record Semicolon : Token;

	public record Comma : Token;

	public record LParen : Token;

	public record RParen : Token;

	public record Plus : Token;

	public record Minus : Token;

	public record Star : Token;

	public record Slash : Token;

	public record Bang : Token;

	public record Equal : Token;

	public record EqualEqual : Token;

	public record BangEqual : Token;

	public record PipePipe : Token;

	public record AmpAmp : Token;

	public record Identifier(string name) : Token;

	public record String(string value) : Token;

	public record Int(int value) : Token;

	public record Bool(bool value) : Token;

	public sealed override string ToString() {
		return this switch {
			Colon => "Colon",
			Semicolon => "Semicolon",
			Comma => "Comma",
			LParen => "LParen",
			RParen => "RParen",
			Plus => "Plus",
			Minus => "Minus",
			Star => "Star",
			Slash => "Slash",
			Bang => "Bang",
			Equal => "Equal",
			EqualEqual => "EqualEqual",
			BangEqual => "BangEqual",
			PipePipe => "PipePipe",
			AmpAmp => "AmpAmp",
			Identifier(var name) => $"Identifier({name})",
			String(var value) => $"String({value})",
			Int(var value) => $"Int({value})",
			Bool(var value) => $"Bool({value})",
			Keyword(var name) => $"Keyword({name})",
			_ => throw new UnreachableException()
		};
	}

	public record Keyword(string name) : Token;
}

public static class Lexer {
	private static readonly Parser<char, Token> Colon =
		Char(':').ThenReturn((Token)new Token.Colon());

	private static readonly Parser<char, Token> Semicolon =
		Char(';').ThenReturn((Token)new Token.Semicolon());
	
	private static readonly Parser<char, Token> Comma =
		Char(',').ThenReturn((Token)new Token.Comma());

	private static readonly Parser<char, Token> LParen =
		Char('(').ThenReturn((Token)new Token.LParen());

	private static readonly Parser<char, Token> RParen =
		Char(')').ThenReturn((Token)new Token.RParen());

	private static readonly Parser<char, Token> Plus =
		Char('+').ThenReturn((Token)new Token.Plus());

	private static readonly Parser<char, Token> Minus =
		Char('-').ThenReturn((Token)new Token.Minus());

	private static readonly Parser<char, Token> Star =
		Char('*').ThenReturn((Token)new Token.Star());

	private static readonly Parser<char, Token> Slash =
		Char('/').ThenReturn((Token)new Token.Slash());

	private static readonly Parser<char, Token> Bang =
		Char('!').ThenReturn((Token)new Token.Bang());

	private static readonly Parser<char, Token> Equal =
		Char('=').ThenReturn((Token)new Token.Equal());

	private static readonly Parser<char, Token> EqualEqual =
		String("==").ThenReturn((Token)new Token.EqualEqual());

	private static readonly Parser<char, Token> BangEqual =
		String("!=").ThenReturn((Token)new Token.BangEqual());

	private static readonly Parser<char, Token> PipePipe =
		String("||").ThenReturn((Token)new Token.PipePipe());

	private static readonly Parser<char, Token> AmpAmp =
		String("&&").ThenReturn((Token)new Token.AmpAmp());

	private static readonly Parser<char, Token> String =
		from _ in Char('"')
		from value in Try(Any.Where(c => c != '"' && c != '\r' && c != '\n')).ManyString()
		from __ in Char('"').Labelled("closing '\"' (unterminated string)")
		select (Token)new Token.String(value);

	private static readonly Parser<char, Token> IntLiteral =
		from digits in Digit.AtLeastOnceString()
		select (Token)new Token.Int(int.Parse(digits));

	private static readonly Parser<char, Token> BoolLiteral =
		from value in Letter.ManyString().Where(s => s is "true" or "false")
		select (Token)new Token.Bool(bool.Parse(value));

	private static readonly Parser<char, Token> Keyword =
		from name in Letter.ManyString().Where(name => name switch {
			"let" => true,
			_ => false
		})
		select (Token)new Token.Keyword(name);

	private static readonly Parser<char, Token> Identifier =
		from first in Letter.Or(Char('_'))
		from rest in LetterOrDigit.Or(Char('_')).ManyString()
		select (Token)new Token.Identifier(first + rest);

	private static readonly Parser<char, List<Token>> Tokens =
		from _leading in Whitespace.SkipMany()
		from tokens in OneOf(
			Colon, Semicolon, Comma, LParen, RParen,
			Plus, Minus, Star, Slash, Try(BangEqual), Bang, Try(EqualEqual), Equal,
			Try(PipePipe), Try(AmpAmp),
			String, Try(IntLiteral), Try(Keyword), Try(BoolLiteral), Identifier
		).Before(Whitespace.SkipMany()).Many()
		from _end in End
		select tokens.ToList();

	public static List<Token> Lex(string input) =>
		Tokens.ParseOrThrow(input);
}