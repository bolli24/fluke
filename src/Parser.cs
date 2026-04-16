using System.Diagnostics;
using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<fluke.Token>;

namespace fluke;

public record Ast(List<Statement> Statements);

public abstract record Statement {
	public record ExpressionStatement(Expression expr) : Statement;

	public record VariableBinding(string name, Expression expr) : Statement;

	public record Assignment(Expression lhs, Expression rhs) : Statement;

	public record FunctionDeclaration(
		string name,
		(string, string)[] parameters,
		Expression.Block body,
		string returnType) : Statement;

	public record StructDeclaration(string name, (string, string)[] fields) : Statement;

	public record EnumDeclaration(string name, string[] variants) : Statement;

	public record Return(Expression? Expression) : Statement;

	public sealed override string ToString() {
		return this switch {
			ExpressionStatement(var expr) => $"Expr({expr})",
			VariableBinding(var name, var expr) => $"Binding({name}, {expr})",
			Assignment(var lhs, var rhs) => $"Assign({lhs}, {rhs})",
			FunctionDeclaration(var name, var parameters, var body, var returnType) =>
				$"Fn({name}({string.Join(", ", parameters.Select(p => $"{p.Item1}: {p.Item2}"))}) -> {returnType} {body})",
			StructDeclaration(var name, var fields) =>
				$"Struct({name} {{ {string.Join(", ", fields.Select(f => $"{f.Item1}: {f.Item2}"))} }})",
			// EnumDeclaration(var name, var variants) =>
			//	$"Enum({name} {{ {string.Join(", ", variants.Select(f => f.Item2 != null ? $"{f.Item1}: {f.Item2}" : $"{f.Item2}"))} }})",
			EnumDeclaration(var name, var variants) => $"Enum({name} {{ {string.Join(", ", variants)}}})",
			Return(var expr) => expr != null ? $"Return({expr})" : "Return",
			_ => throw new UnreachableException(),
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

	public record StructInit(string name, (string, Expression)[] fields) : Expression;

	public record FieldAccess(Expression expression, string name) : Expression;

	public record If(Expression condition, Block thenBlock, Block? elseBlock) : Expression;

	public record IntrinsicCall(string name, Expression[] args) : Expression;

	public sealed override string ToString() {
		return this switch {
			StringLiteral(var v) => $"\"{v}\"",
			IntLiteral(var v) => v.ToString(),
			BoolLiteral(var v) => v.ToString(),
			Identifier(var n) => n,
			BinaryOp(var l, var op, var r) => $"({l} {op} {r})",
			UnaryOp(var op, var e) => $"({op}{e})",
			Block(var body, var tail) => "Block {\n"
			                             + string.Join("\n", body.Select(s => "\t" + s))
			                             + (tail != null ? "\n\t" + tail : "")
			                             + "\n}",
			FunctionCall(var n, var args) => $"{n}({string.Join(", ", args.Select(a => a.ToString()))})",
			StructInit(var n, var fields) =>
				$"{n} {{ {string.Join(", ", fields.Select(f => $"{f.Item1} = {f.Item2}"))} }}",
			FieldAccess(var expr, var name) => $"Field({expr}, {name})",
			If(var cond, var then, var @else) => $"If({cond}, {then}{(@else != null ? $", {@else}" : "")})",
			IntrinsicCall(var n, var args) => $":{n}({string.Join(", ", args.Select(a => a.ToString()))})",
			_ => throw new UnreachableException(),
		};
	}
}

public static class Parser {
	// Parses zero or more comma-separated items, allowing an optional trailing comma.
	private static Parser<Token, IEnumerable<T>> CommaSeparated<T>(Parser<Token, T> p) =>
		from items in Try(from item in p from _ in Token(new Token.Comma()) select item).Many()
		from last in p.Optional()
		select last.HasValue ? items.Append(last.Value) : items;

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

	private static readonly Parser<Token, Expression> StructInit =
		from name in IdentifierName
		from _lbrace in Token(new Token.LBrace())
		from fields in CommaSeparated(
			from name in IdentifierName
			from _colon in Token(new Token.Colon())
			from expr in Expression
			select (name, expr)
		)
		from _rbrace in Token(new Token.RBrace())
		select (Expression)new Expression.StructInit(name, fields.ToArray());

	// Atoms: literals, identifiers, parenthesized expressions, blocks
	private static readonly Parser<Token, Expression> Atom = (
			from t in Token(t => t is Token.Int)
			select (Expression)new Expression.IntLiteral(((Token.Int)t).value)
		)
		.Or(from t in Token(t => t is Token.Bool) select (Expression)new Expression.BoolLiteral(((Token.Bool)t).value))
		.Or(from s in String select (Expression)new Expression.StringLiteral(s))
		.Or(
			Try(
				from _colon in Token(new Token.Colon())
				from name in IdentifierName
				from args in CallArgs
				select (Expression)new Expression.IntrinsicCall(name, args.ToArray())
			)
		)
		.Or(Try(StructInit))
		.Or(
			Try(
				from n in IdentifierName
				from args in CallArgs
				select (Expression)new Expression.FunctionCall(n, args.ToArray())
			)
		)
		.Or(from n in IdentifierName select (Expression)new Expression.Identifier(n))
		.Or(
			from _l in Token(new Token.LParen())
			from e in Rec(() => LogicalOr)
			from _r in Token(new Token.RParen())
			select e
		)
		.Or(Rec(() => If))
		.Or(from block in Block select (Expression)block);

	private static readonly Parser<Token, Expression> FieldAccess =
		from expr in Atom
		from accesses in (from _dot in Token(new Token.Dot()) from name in IdentifierName select name).Many()
		select accesses.Aggregate(expr, (e, name) => new Expression.FieldAccess(e, name));

	// Unary: -x, !x
	private static readonly Parser<Token, Expression> Unary =
		(from _ in Token(new Token.Minus())
			from e in Rec(() => Unary)
			select (Expression)new Expression.UnaryOp(UnOp.Neg, e))
		.Or(from _ in Token(new Token.Bang())
			from e in Rec(() => Unary)
			select (Expression)new Expression.UnaryOp(UnOp.Not, e))
		.Or(FieldAccess);

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
		from args in CommaSeparated(Expression)
		from _rparen in Token(new Token.RParen())
		select args;

	private static readonly Parser<Token, Expression> If =
		from _if in Token(new Token.Keyword("if"))
		from condition in Expression
		from _then in Token(new Token.Keyword("then"))
		from thenBlock in Block
		from elseBlock in Try(
			from _else in Token(new Token.Keyword("else"))
			from block in Rec(() => If).Select(e => new Expression.Block([], e))
				.Or(Block)
			select block
		).Optional()
		select (Expression)new Expression.If(condition, thenBlock, elseBlock.GetValueOrDefault());

	private static readonly Parser<Token, Statement> ExpressionStatement =
		from expr in Expression
		select (Statement)new Statement.ExpressionStatement(expr);

	private static readonly Parser<Token, Statement> Return =
		from _return in Token(new Token.Keyword("return"))
		from expression in Try(Expression).Optional()
		select (Statement)new Statement.Return(expression.GetValueOrDefault());

	private static readonly Parser<Token, Statement> VariableBinding =
		from _ in Token(new Token.Keyword("let"))
		from name in IdentifierName
		from __ in Token(new Token.Equal())
		from expression in Expression
		select (Statement)new Statement.VariableBinding(name, expression);

	private static readonly Parser<Token, Statement> Assignment =
		from lhs in FieldAccess
		from _ in Token(new Token.Equal())
		from rhs in Expression
		select (Statement)new Statement.Assignment(lhs, rhs);

	private static readonly Parser<Token, (string, string)> TypedIdentifier =
		from param_name in IdentifierName
		from _colon in Token(new Token.Colon())
		from param_type in IdentifierName
		select (param_name, param_type);

	private static readonly Parser<Token, Statement> FunctionDecl =
		from _fn in Token(new Token.Keyword("fn"))
		from name in IdentifierName
		from _lparen in Token(new Token.LParen())
		from parameters in CommaSeparated(TypedIdentifier)
		from _rparen in Token(new Token.RParen())
		from returnType in Try(
				from _colon in Token(new Token.Colon())
				from t in IdentifierName
				select t)
			.Optional()
		from body in Block
		select (Statement)new Statement.FunctionDeclaration(name, parameters.ToArray(), body,
			returnType.GetValueOrDefault("unit"));

	private static readonly Parser<Token, Statement> StructDecl =
		from _struct in Token(new Token.Keyword("struct"))
		from name in IdentifierName
		from _lbrace in Token(new Token.LBrace())
		from fields in CommaSeparated(TypedIdentifier)
		from _rbrace in Token(new Token.RBrace())
		select (Statement)new Statement.StructDeclaration(name, fields.ToArray());

	private static readonly Parser<Token, Statement> EnumDecl =
		from _enum in Token(new Token.Keyword("enum"))
		from name in IdentifierName
		from _brace in Token(new Token.LBrace())
		from variants in CommaSeparated(IdentifierName)
		from _rbrace in Token(new Token.RBrace())
		select (Statement)new Statement.EnumDeclaration(name, variants.ToArray());

	private static readonly Parser<Token, Statement> Statement = Try(
			from statement in OneOf(Try(Assignment), VariableBinding, Return, ExpressionStatement)
			from _ in Token(new Token.Semicolon())
			select statement
		)
		.Or(OneOf(FunctionDecl, StructDecl, EnumDecl))
		.Or(
			Try(
				from expr in Rec(() => If).Or(from b in Block select (Expression)b)
				from _ in Not(Token(new Token.RBrace()))
				select (Statement)new Statement.ExpressionStatement(expr)
			)
		);

	private static readonly Parser<Token, Ast> Program =
		from stmts in Statement.Many()
		from _end in End
		select new Ast(stmts.ToList());

	public static Ast Parse(List<Token> tokens) =>
		Program.ParseOrThrow(tokens);
}