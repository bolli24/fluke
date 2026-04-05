using System.Diagnostics;
using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<fluke.Token>;

namespace fluke;

public record Ast(List<Statement> Statements);

public abstract record Statement {
	public record IntrinsicCall(string name, Expression? expr) : Statement;

	public record ExpressionStatement(Expression expr) : Statement;

	public record VariableBinding(string name, Expression expr) : Statement;

	public sealed override string ToString() {
		return this switch {
			IntrinsicCall(var name, null) => $"Intrinsic({name})",
			IntrinsicCall(var name, var arg) => $"Intrinsic({name}, {arg})",
			ExpressionStatement(var expr) => $"Expr({expr})",
			VariableBinding(var name, var expr) => $"Binding({name}, {expr})",
			_ => throw new UnreachableException()
		};
	}
}

public enum BinOp {
	Add,
	Sub,
	Mul,
	Div,
	And,
	Or,
	Eq,
	Neq
}

public enum UnOp {
	Neg,
	Not
}

public abstract record Expression {
	public record StringLiteral(string value) : Expression;

	public record IntLiteral(int value) : Expression;

	public record BoolLiteral(bool value) : Expression;

	public record Identifier(string name) : Expression;

	public record BinaryOp(Expression left, BinOp op, Expression right) : Expression;

	public record UnaryOp(UnOp op, Expression operand) : Expression;

	public sealed override string ToString() {
		return this switch {
			StringLiteral(var v) => $"\"{v}\"",
			IntLiteral(var v) => v.ToString(),
			BoolLiteral(var v) => v.ToString(),
			Identifier(var n) => n,
			BinaryOp(var l, var op, var r) => $"({l} {op} {r})",
			UnaryOp(var op, var e) => $"({op}{e})",
			_ => throw new UnreachableException()
		};
	}
}

public static class Parser {
	private static readonly Parser<Token, string> String =
		from token in Token(t => t is Token.String)
		select ((Token.String)token).value;
	
	private static readonly Parser<Token, string> IdentifierName =
		from t in Token(t => t is Token.Identifier)
		select ((Token.Identifier)t).name;

	// Atoms: literals, identifiers, parenthesized expressions
	private static readonly Parser<Token, Expression> Atom =
		(from t in Token(t => t is Token.Int)
			select (Expression)new Expression.IntLiteral(((Token.Int)t).value))
		.Or(from t in Token(t => t is Token.Bool)
			select (Expression)new Expression.BoolLiteral(((Token.Bool)t).value))
		.Or(from s in String
			select (Expression)new Expression.StringLiteral(s))
		.Or(from n in IdentifierName
			select (Expression)new Expression.Identifier(n))
		.Or(from _l in Token(new Token.LParen())
			from e in Rec(() => LogicalOr)
			from _r in Token(new Token.RParen())
			select e);

	// Unary: -x, !x
	private static readonly Parser<Token, Expression> Unary =
		(from _ in Token(new Token.Minus())
			from e in Rec(() => Unary)
			select (Expression)new Expression.UnaryOp(UnOp.Neg, e))
		.Or(from _ in Token(new Token.Bang())
			from e in Rec(() => Unary)
			select (Expression)new Expression.UnaryOp(UnOp.Not, e))
		.Or(Atom);

	// Multiplicative: *, /
	private static readonly Parser<Token, Expression> Multiplicative =
		from left in Unary
		from ops in (
			from op in Token(new Token.Star()).ThenReturn(BinOp.Mul)
				.Or(Token(new Token.Slash()).ThenReturn(BinOp.Div))
			from right in Unary
			select (op, right)
		).Many()
		select ops.Aggregate(left, (l, pair) => new Expression.BinaryOp(l, pair.op, pair.right));

	// Additive: +, -
	private static readonly Parser<Token, Expression> Additive =
		from left in Multiplicative
		from ops in (
			from op in Token(new Token.Plus()).ThenReturn(BinOp.Add)
				.Or(Token(new Token.Minus()).ThenReturn(BinOp.Sub))
			from right in Multiplicative
			select (op, right)
		).Many()
		select ops.Aggregate(left, (l, pair) => new Expression.BinaryOp(l, pair.op, pair.right));

	// Equality: ==, !=
	private static readonly Parser<Token, Expression> Equality =
		from left in Additive
		from ops in (
			from op in Token(new Token.EqualEqual()).ThenReturn(BinOp.Eq)
				.Or(Token(new Token.BangEqual()).ThenReturn(BinOp.Neq))
			from right in Additive
			select (op, right)
		).Many()
		select ops.Aggregate(left, (l, pair) => new Expression.BinaryOp(l, pair.op, pair.right));

	// LogicalAnd: &&
	private static readonly Parser<Token, Expression> LogicalAnd =
		from left in Equality
		from ops in (
			from op in Token(new Token.AmpAmp()).ThenReturn(BinOp.And)
			from right in Equality
			select (op, right)
		).Many()
		select ops.Aggregate(left, (l, pair) => new Expression.BinaryOp(l, pair.op, pair.right));

	// LogicalOr: ||
	private static readonly Parser<Token, Expression> LogicalOr =
		from left in LogicalAnd
		from ops in (
			from op in Token(new Token.PipePipe()).ThenReturn(BinOp.Or)
			from right in LogicalAnd
			select (op, right)
		).Many()
		select ops.Aggregate(left, (l, pair) => new Expression.BinaryOp(l, pair.op, pair.right));

	private static readonly Parser<Token, Expression> Expression = LogicalOr;

	private static readonly Parser<Token, Statement> IntrinsicCall =
		from _ in Token(new Token.Colon())
		from name in IdentifierName
		from arg in Expression.Optional()
		select (Statement)new Statement.IntrinsicCall(name, arg.HasValue ? arg.Value : null);

	private static readonly Parser<Token, Statement> ExpressionStatement =
		from expr in Expression
		select (Statement)new Statement.ExpressionStatement(expr);

	private static readonly Parser<Token, Statement> VariableBinding =
		from _ in Token(new Token.Keyword("let"))
		from name in IdentifierName
		from __ in Token(new Token.Equal())
		from expression in Expression
		select (Statement)new Statement.VariableBinding(name, expression);

	private static readonly Parser<Token, Statement> Statement =
		from statement in OneOf(IntrinsicCall, ExpressionStatement, VariableBinding)
		from _ in Token(new Token.Semicolon())
		select statement;

	private static readonly Parser<Token, Ast> Program =
		from stmts in Statement.Many()
		from _end in End
		select new Ast(stmts.ToList());

	public static Ast Parse(List<Token> tokens) =>
		Program.ParseOrThrow(tokens);
}