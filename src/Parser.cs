using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<fluke.Token>;

namespace fluke;

public record Ast(List<Statement> Statements);

public abstract record Statement {
	public record IntrinsicCall(string name, Expression[] args) : Statement;

	public record ExpressionStatement(Expression expr) : Statement;

	public record VariableBinding(string name, Expression expr) : Statement;

	public record FunctionDeclaration(
		string name,
		(string, string)[] parameters,
		Expression.Block body,
		string returnType) : Statement;

	public override string ToString() {
		return this switch {
			IntrinsicCall(var name, var args) => $"Intrinsic({name}, {string.Join(", ", args)})",
			ExpressionStatement(var expr) => $"Expr({expr})",
			VariableBinding(var name, var expr) => $"Binding({name}, {expr})",
			FunctionDeclaration(var name, var parameters, var body, var returnType) =>
				$"Fn({name}({string.Join(", ", parameters.Select(p => $"{p.Item1}: {p.Item2}"))}) -> {returnType} {body})",
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

	public record Block(Statement[] body, Expression? tail) : Expression;

	public record FunctionCall(string name, Expression[] args) : Expression;

	public sealed override string ToString() {
		return this switch {
			StringLiteral(var v) => $"\"{v}\"",
			IntLiteral(var v) => v.ToString(),
			BoolLiteral(var v) => v.ToString(),
			Identifier(var n) => n,
			BinaryOp(var l, var op, var r) => $"({l} {op} {r})",
			UnaryOp(var op, var e) => $"({op}{e})",
			Block(var body, var tail) =>
				"Block {\n" + string.Join("\n", body.Select(s => "\t" + s)) +
				(tail != null ? "\n\t" + tail : "") + "\n}",
			FunctionCall(var n, var args) => $"{n}({string.Join(", ", args.Select(a => a.ToString()))})",
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

	private static readonly Parser<Token, Expression.Block> Block = from _l in Token(new Token.LBrace())
		from stmts in Rec(() => Statement).Many()
		from tail in Rec(() => Expression).Optional()
		from _r in Token(new Token.RBrace())
		select new Expression.Block(stmts.ToArray(), tail.GetValueOrDefault());

	// Atoms: literals, identifiers, parenthesized expressions, blocks
	private static readonly Parser<Token, Expression> Atom =
		(from t in Token(t => t is Token.Int)
			select (Expression)new Expression.IntLiteral(((Token.Int)t).value))
		.Or(from t in Token(t => t is Token.Bool)
			select (Expression)new Expression.BoolLiteral(((Token.Bool)t).value))
		.Or(from s in String
			select (Expression)new Expression.StringLiteral(s))
		.Or(Try(
			from n in IdentifierName
			from args in CallArgs
			select (Expression)new Expression.FunctionCall(n, args.ToArray())))
		.Or(from n in IdentifierName
			select (Expression)new Expression.Identifier(n))
		.Or(from _l in Token(new Token.LParen())
			from e in Rec(() => LogicalOr)
			from _r in Token(new Token.RParen())
			select e)
		.Or(from block in Block select (Expression)block);

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

	private static readonly Parser<Token, IEnumerable<Expression>> CallArgs =
		from _lparen in Token(new Token.LParen())
		from args in Expression.Separated(Token(new Token.Comma()))
		from _ in Token(new Token.Comma()).Optional()
		from _rparen in Token(new Token.RParen())
		select args;

	private static readonly Parser<Token, Statement> IntrinsicCall =
		from _colon in Token(new Token.Colon())
		from name in IdentifierName
		from args in CallArgs
		select (Statement)new Statement.IntrinsicCall(name, args.ToArray());

	private static readonly Parser<Token, Statement> ExpressionStatement =
		from expr in Expression
		select (Statement)new Statement.ExpressionStatement(expr);

	private static readonly Parser<Token, Statement> VariableBinding =
		from _ in Token(new Token.Keyword("let"))
		from name in IdentifierName
		from __ in Token(new Token.Equal())
		from expression in Expression
		select (Statement)new Statement.VariableBinding(name, expression);

	private static readonly Parser<Token, (string, string)> TypedIdentifier =
		from param_name in IdentifierName
		from _colon in Token(new Token.Colon())
		from param_type in IdentifierName
		select (param_name, param_type);

	private static readonly Parser<Token, Statement> FunctionDecl =
		from _fn in Token(new Token.Keyword("fn"))
		from name in IdentifierName
		from _lparen in Token(new Token.LParen())
		from parameters in TypedIdentifier.Separated(Token(new Token.Comma()))
		from _ in Token(new Token.Comma()).Optional()
		from _rparen in Token(new Token.RParen())
		from returnType in Try(
				from _colon in Token(new Token.Colon())
				from t in IdentifierName
				select t)
			.Optional()
		from body in Block
		select (Statement)new Statement.FunctionDeclaration(name, parameters.ToArray(), body,
			returnType.GetValueOrDefault("unit"));

	private static readonly Parser<Token, Statement> Statement =
		Try(from statement in OneOf(IntrinsicCall, ExpressionStatement, VariableBinding)
				from _ in Token(new Token.Semicolon())
				select statement)
			.Or(FunctionDecl)
			.Or(Try(
				from block in Block
				from _ in Not(Token(new Token.RBrace()))
				select (Statement)new Statement.ExpressionStatement(block)));

	private static readonly Parser<Token, Ast> Program =
		from stmts in Statement.Many()
		from _end in End
		select new Ast(stmts.ToList());

	public static Ast Parse(List<Token> tokens) =>
		Program.ParseOrThrow(tokens);
}