using System.Diagnostics;
using Pidgin;
using static Pidgin.Parser<fluke.Token>;

namespace fluke;

public record FlukeProgram(List<Statement> Statements);

public abstract record Statement {
	public record IntrinsicCall(string Name, string? arg) : Statement;

	public sealed override string ToString() {
		return this switch {
			IntrinsicCall(var name, null) => $"Intrincic({name})",
			IntrinsicCall(var name, var arg) => $"Intrincic({name}, {arg})",
			_ => throw new UnreachableException()
		};
	}
}

public static class Parser {
	private static readonly Parser<Token, Token> Colon =
		Any.Where(t => t is Token.Colon);

	private static readonly Parser<Token, string> String =
		from token in Any.Where(t => t is Token.String)
		select ((Token.String)token).value;
	
	private static readonly Parser<Token, string> Identifier =
		from t in Any.Where(t => t is Token.Identifier)
		select ((Token.Identifier)t).name;

	private static readonly Parser<Token, Statement> InstrinsicCall =
		from _ in Colon
		from name in Identifier
		from arg in String.Optional()
		select (Statement)new Statement.IntrinsicCall(name, arg.HasValue ? arg.Value : null);

	private static readonly Parser<Token, Statement> Statement =
		InstrinsicCall;

	private static readonly Parser<Token, FlukeProgram> Program =
		from stmts in Statement.Many()
		from _end in End
		select new FlukeProgram(stmts.ToList());

	public static FlukeProgram Parse(List<Token> tokens) =>
		Program.ParseOrThrow(tokens);
}