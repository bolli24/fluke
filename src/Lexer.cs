// Token + Lexer

using System.Diagnostics;
using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

namespace fluke;

public readonly record struct Span(SourcePos Start, SourcePos End) {
	public override string ToString() => $"{Start.Line}:{Start.Col}";

	public static Span operator |(Span left, Span right) =>
		new(left.Start < right.Start ? left.Start : right.Start,
			left.End > right.End ? left.End : right.End);
}

public abstract record Token {
	public Span Span { get; init; }

	public virtual bool Equals(Token? other) => other is not null && EqualityContract == other.EqualityContract;

	public override int GetHashCode() => EqualityContract.GetHashCode();

	public record Dot : Token;

	public record Colon : Token;

	public record Semicolon : Token;

	public record Comma : Token;

	public record LParen : Token;

	public record RParen : Token;

	public record LBrace : Token;

	public record RBrace : Token;

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

	public record Keyword(string name) : Token;

	public sealed override string ToString() {
		return this switch {
			Dot => "Dot",
			Colon => "Colon",
			Semicolon => "Semicolon",
			Comma => "Comma",
			LParen => "LParen",
			RParen => "RParen",
			LBrace => "LBrace",
			RBrace => "RBrace",
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

	// Source spelling, for diagnostics
	public string Spelling() =>
		this switch {
			Dot => "'.'",
			Colon => "':'",
			Semicolon => "';'",
			Comma => "','",
			LParen => "'('",
			RParen => "')'",
			LBrace => "'{'",
			RBrace => "'}'",
			Plus => "'+'",
			Minus => "'-'",
			Star => "'*'",
			Slash => "'/'",
			Bang => "'!'",
			Equal => "'='",
			EqualEqual => "'=='",
			BangEqual => "'!='",
			PipePipe => "'||'",
			AmpAmp => "'&&'",
			Identifier(var name) => $"identifier '{name}'",
			String(var value) => $"string \"{value}\"",
			Int(var value) => $"integer {value}",
			Bool(var value) => value ? "'true'" : "'false'",
			Keyword(var name) => $"'{name}'",
			_ => throw new UnreachableException()
		};
}

public static class Lexer {
	private static readonly Parser<char, Token> Dot =
		Char('.').ThenReturn<Token>(new Token.Dot());

	private static readonly Parser<char, Token> Colon =
		Char(':').ThenReturn<Token>(new Token.Colon());

	private static readonly Parser<char, Token> Semicolon =
		Char(';').ThenReturn<Token>(new Token.Semicolon());

	private static readonly Parser<char, Token> Comma =
		Char(',').ThenReturn<Token>(new Token.Comma());

	private static readonly Parser<char, Token> LParen =
		Char('(').ThenReturn<Token>(new Token.LParen());

	private static readonly Parser<char, Token> RParen =
		Char(')').ThenReturn<Token>(new Token.RParen());

	private static readonly Parser<char, Token> LBrace =
		Char('{').ThenReturn<Token>(new Token.LBrace());

	private static readonly Parser<char, Token> RBrace =
		Char('}').ThenReturn<Token>(new Token.RBrace());

	private static readonly Parser<char, Token> Plus =
		Char('+').ThenReturn<Token>(new Token.Plus());

	private static readonly Parser<char, Token> Minus =
		Char('-').ThenReturn<Token>(new Token.Minus());

	private static readonly Parser<char, Token> Star =
		Char('*').ThenReturn<Token>(new Token.Star());

	private static readonly Parser<char, Token> Slash =
		Char('/').ThenReturn<Token>(new Token.Slash());

	private static readonly Parser<char, Token> Bang =
		Char('!').ThenReturn<Token>(new Token.Bang());

	private static readonly Parser<char, Token> Equal =
		Char('=').ThenReturn<Token>(new Token.Equal());

	private static readonly Parser<char, Token> EqualEqual =
		String("==").ThenReturn<Token>(new Token.EqualEqual());

	private static readonly Parser<char, Token> BangEqual =
		String("!=").ThenReturn<Token>(new Token.BangEqual());

	private static readonly Parser<char, Token> PipePipe =
		String("||").ThenReturn<Token>(new Token.PipePipe());

	private static readonly Parser<char, Token> AmpAmp =
		String("&&").ThenReturn<Token>(new Token.AmpAmp());

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
		from _ in Not(Lookahead(LetterOrDigit.Or(Char('_'))))
		select (Token)new Token.Bool(bool.Parse(value));

	private static readonly Parser<char, Token> Keyword =
		from name in Letter.ManyString().Where(name =>
			name is "let" or "fn" or "struct" or "enum" or "if" or "then" or "else" or "return" or "ref")
		from _ in Not(Lookahead(LetterOrDigit.Or(Char('_'))))
		select (Token)new Token.Keyword(name);

	private static readonly Parser<char, Token> Identifier =
		from first in Letter.Or(Char('_'))
		from rest in LetterOrDigit.Or(Char('_')).ManyString()
		select (Token)new Token.Identifier(first + rest);

	private static readonly Parser<char, Unit> LineComment =
		String("//").Then(AnyCharExcept('\r', '\n').SkipMany());

	private static readonly Parser<char, Unit> BlockComment =
		Try(String("/*")).Then(Any.SkipUntil(Try(String("*/")))
			.Labelled("closing '*/' (unterminated block comment)"));

	private static readonly Parser<char, Unit> Ignored =
		OneOf(Whitespace.ThenReturn(Unit.Value), Try(LineComment), BlockComment).SkipMany();

	private static Parser<char, Token> WithSpan(Parser<char, Token> p) =>
		from start in CurrentPos
		from token in p
		from end in CurrentPos
		select token with { Span = new Span(start, end) };

	private static readonly Parser<char, (List<Token>, SourcePos)> Tokens =
		from _leading in Ignored
		from tokens in WithSpan(OneOf(
			Dot, Colon, Semicolon, Comma, LParen, RParen, LBrace, RBrace,
			Plus, Minus, Star, Slash, Try(BangEqual), Bang, Try(EqualEqual), Equal,
			Try(PipePipe), Try(AmpAmp),
			String, Try(IntLiteral), Try(Keyword), Try(BoolLiteral), Identifier
		)).Before(Ignored).Many()
		from _end in End
		from eof in CurrentPos
		select (tokens.ToList(), eof);

	/// Returns parsed tokens and end of input source position
	public static (List<Token>, SourcePos) Lex(string input) {
		var result = Tokens.Parse(input);

		if (result.Success) return result.Value;

		var err = result.Error!;
		var span = err.Unexpected.HasValue
			? new Span(err.ErrorPos, err.ErrorPos.IncrementCol())
			: new Span(err.ErrorPos, err.ErrorPos);

		throw CompileException.FromParseError(err, span, SpellChars);
	}

	// Whitespace would break the message apart, so it is named instead
	private static string SpellChars(IEnumerable<char> chars) =>
		string.Concat(chars) switch {
			"\n" or "\r" => "newline",
			"\t" => "tab",
			var s => $"'{s}'"
		};
}