using System.Diagnostics;
using Pidgin;
using static Pidgin.Parser<fluke.Token>;

namespace fluke;

public record Ast(List<Statement> Statements);

public abstract record Statement {
	public record IntrinsicCall(string Name, string? arg) : Statement;

	public sealed override string ToString() {
		return this switch {
			IntrinsicCall(var name, null) => $"Intrincic({name})",
			IntrinsicCall(var name, var arg) => $"Intrincic({name}, \"{arg}\")",
			_ => throw new UnreachableException()
		};
	}
}

public static class Parser {
	private static readonly Parser<Token, string> String =
		from token in Token(t => t is Token.String)
		select ((Token.String)token).value;
	
	private static readonly Parser<Token, string> Identifier =
		from t in Token(t => t is Token.Identifier)
		select ((Token.Identifier)t).name;

	private static readonly Parser<Token, Statement> IntrinsicCall =
		from _ in Token(new Token.Colon())
		from name in Identifier
		from arg in String.Optional()
		select (Statement)new Statement.IntrinsicCall(name, arg.HasValue ? arg.Value : null);

	private static readonly Parser<Token, Statement> Statement =
		from statement in IntrinsicCall
		from _ in Token(new Token.Semicolon())
		select statement;
		
	private static readonly Parser<Token, Ast> Program =
		from stmts in Statement.Many()
		from _end in End
		select new Ast(stmts.ToList());

	public static Ast Parse(List<Token> tokens) =>
		Program.ParseOrThrow(tokens);
}