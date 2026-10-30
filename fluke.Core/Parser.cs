using System.Diagnostics;
using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<fluke.Token>;

namespace fluke;

public record Ast(List<Statement> statements);

public record Ident(string Name, Span Span) {
	public override string ToString() => Name;

	public override int GetHashCode() => Name.GetHashCode();
	public virtual bool Equals(Ident? other) => other is not null && Name == other.Name;
}

public struct Param {
	public Ident Name;
	public Ident TypeName;
	public bool IsRef;
	public Span Span;
}

public abstract record Statement {
	public Span Span { get; init; }
	public virtual bool Equals(Statement? other) => other is not null && EqualityContract == other.EqualityContract;

	public override int GetHashCode() => EqualityContract.GetHashCode();

	public record ExpressionStatement(Expression expr) : Statement;

	public record VariableBinding(Ident ident, Expression expr) : Statement;

	public record Assignment(Expression lhs, Expression rhs) : Statement;

	public record FunctionDeclaration(
		Ident ident,
		Param[] parameters,
		Expression.Block body,
		Ident? returnType) : Statement;

	public record StructDeclaration(Ident ident, (Ident, Ident)[] fields) : Statement;

	public record EnumDeclaration(Ident ident, EnumVariant[] variants) : Statement;

	public record Return(Expression? expression) : Statement;

	public sealed override string ToString() {
		return this switch {
			ExpressionStatement(var expr) => $"Expr({expr})",
			VariableBinding(var name, var expr) => $"Binding({name}, {expr})",
			Assignment(var lhs, var rhs) => $"Assign({lhs}, {rhs})",
			FunctionDeclaration(var name, var parameters, var body, var returnType) =>
				$"Fn({name}({string.Join(", ", parameters.Select(p => $"{p.Name}:{(p.IsRef ? "ref" : "")} {p.TypeName}"))}) -> {returnType} {body})",
			StructDeclaration(var name, var fields) =>
				$"Struct({name} {{ {string.Join(", ", fields.Select(f => $"{f.Item1}: {f.Item2}"))} }})",
			EnumDeclaration(var name, var variants) =>
				$"Enum({name} {{ {string.Join(", ", variants.Select(v => v.ToString()))}}})",
			Return(var expr) => expr != null ? $"Return({expr})" : "Return",
			_ => throw new UnreachableException(),
		};
	}
}

public abstract record EnumVariant(Ident name) {
	public record Bare(Ident name) : EnumVariant(name);

	public record Struct(Ident name, (Ident, Ident)[] fields) : EnumVariant(name);

	public sealed override string ToString() =>
		this switch {
			Bare(var n) => n.Name,
			Struct(var n, var fields) =>
				$"{n} {{ {string.Join(", ", fields.Select(f => $"{f.Item1}: {f.Item2}"))} }}",
			_ => throw new UnreachableException(),
		};
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

public static class OperatorExtensions {
	// Source spelling, for diagnostics
	public static string Symbol(this BinOp op) =>
		op switch {
			BinOp.Add => "+",
			BinOp.Sub => "-",
			BinOp.Mul => "*",
			BinOp.Div => "/",
			BinOp.And => "&&",
			BinOp.Or => "||",
			BinOp.Eq => "==",
			BinOp.Neq => "!=",
			_ => throw new UnreachableException(),
		};

	public static string Symbol(this UnOp op) =>
		op switch {
			UnOp.Neg => "-",
			UnOp.Not => "!",
			_ => throw new UnreachableException(),
		};
}

public abstract record Expression {
	public Span Span { get; init; }

	public virtual bool Equals(Expression? other) => other is not null && EqualityContract == other.EqualityContract;

	public override int GetHashCode() => EqualityContract.GetHashCode();

	public record StringLiteral(string value) : Expression;

	public record IntLiteral(int value) : Expression;

	public record BoolLiteral(bool value) : Expression;

	public record Identifier(Ident ident) : Expression;

	public record BinaryOp(Expression left, BinOp op, Expression right) : Expression;

	public record UnaryOp(UnOp op, Expression operand) : Expression;

	public record Block(Statement[] body, Expression? tail) : Expression;

	public record FunctionCall(Ident ident, Expression[] args) : Expression;

	public record StructInit(Ident ident, (Ident, Expression)[] fields) : Expression;

	public record EnumVariantInit(Ident enumIdent, Ident variantIdent, (Ident, Expression)[] fields) : Expression;

	public record FieldAccess(Expression expression, Ident ident) : Expression;

	public record If(Expression condition, Block thenBlock, Block? elseBlock) : Expression;

	public record IfLet(
		Ident enumIdent,
		Ident variantIdent,
		Ident[] bindings,
		Expression scrutinee,
		Block thenBlock,
		Block? elseBlock) : Expression;

	public record IntrinsicCall(Ident ident, Expression[] args) : Expression;

	public sealed override string ToString() {
		return this switch {
			StringLiteral(var v) => $"\"{v}\"",
			IntLiteral(var v) => v.ToString(),
			BoolLiteral(var v) => v.ToString(),
			Identifier(var i) => i.Name,
			BinaryOp(var l, var op, var r) => $"({l} {op} {r})",
			UnaryOp(var op, var e) => $"({op}{e})",
			Block(var body, var tail) => "Block {\n"
			                             + string.Join("\n", body.Select(s => "\t" + s))
			                             + (tail != null ? "\n\t" + tail : "")
			                             + "\n}",
			FunctionCall(var n, var args) => $"{n}({string.Join(", ", args.Select(a => a.ToString()))})",
			StructInit(var n, var fields) =>
				$"{n} {{ {string.Join(", ", fields.Select(f => $"{f.Item1} = {f.Item2}"))} }}",
			EnumVariantInit(var en, var vn, var fields) =>
				$"{en}.{vn} {{ {string.Join(", ", fields.Select(f => $"{f.Item1} = {f.Item2}"))} }}",
			FieldAccess(var expr, var name) => $"Field({expr}, {name})",
			If(var cond, var then, var @else) => $"If({cond}, {then}{(@else != null ? $", {@else}" : "")})",
			IfLet(var en, var vn, var binds, var scrutinee, var then, var @else) =>
				$"IfLet({en}.{vn}{{{string.Join(", ", binds)}}} = {scrutinee}, {then}{(@else != null ? $", {@else}" : "")})",
			IntrinsicCall(var n, var args) => $":{n}({string.Join(", ", args.Select(a => a.ToString()))})",
			_ => throw new UnreachableException(),
		};
	}
}

public static class Parser {
	private static Parser<Token, T> Spanned<T>(Parser<Token, T> p) where T : Expression =>
		p.MapWithInput((input, node) => (T)((Expression)node with { Span = input[0].Span | input[^1].Span }));

	// Parses zero or more comma-separated items, allowing an optional trailing comma.
	private static Parser<Token, IEnumerable<T>> CommaSeparated<T>(Parser<Token, T> p) =>
		from items in Try(from item in p from _ in Token(new Token.Comma()) select item).Many()
		from last in p.Optional()
		select last.HasValue ? items.Append(last.Value) : items;

	private static readonly Parser<Token, (string value, Span span)> String =
		from token in Token(t => t is Token.String).Labelled("string")
		select (((Token.String)token).value, token.Span);

	private static readonly Parser<Token, Ident> IdentifierName =
		from token in Token(t => t is Token.Identifier).Labelled("identifier")
		select new Ident(((Token.Identifier)token).name, token.Span);

	// Exactly one of the two is set; a tail can only be the last item, as it must be followed by '}'
	private readonly record struct BlockItem(Statement? Statement, Expression? Tail);

	private static readonly Parser<Token, Expression.Block> Block = Spanned(from _l in Token(new Token.LBrace())
		from items in Rec(() => BlockItemParser!).Many()
		from _r in Token(new Token.RBrace())
		select new Expression.Block(
			items.Where(i => i.Statement != null).Select(i => i.Statement!).ToArray(),
			items.LastOrDefault().Tail));

	private static readonly Parser<Token, IEnumerable<(Ident, Expression)>> InitFields =
		from _lbrace in Token(new Token.LBrace())
		from fields in CommaSeparated(
			from ident in IdentifierName
			from _colon in Token(new Token.Colon())
			from expr in Expression
			select (ident, expr)
		)
		from _rbrace in Token(new Token.RBrace())
		select fields;

	private static readonly Parser<Token, Expression> StructInit =
		from ident in IdentifierName
		from fields in InitFields
		select (Expression)new Expression.StructInit(ident, fields.ToArray());

	private static readonly Parser<Token, Expression> EnumVariantInit =
		from enumIdent in IdentifierName
		from _dot in Token(new Token.Dot())
		from variantIdent in IdentifierName
		from fields in InitFields
		select (Expression)new Expression.EnumVariantInit(enumIdent, variantIdent, fields.ToArray());

	// Atoms: literals, identifiers, parenthesized expressions, blocks
	private static readonly Parser<Token, Expression> Atom = Spanned((
			from t in Token(t => t is Token.Int).Labelled("integer")
			select (Expression)new Expression.IntLiteral(((Token.Int)t).value)
		)
		.Or(from t in Token(t => t is Token.Bool).Labelled("bool")
			select (Expression)new Expression.BoolLiteral(((Token.Bool)t).value))
		.Or(from s in String select (Expression)new Expression.StringLiteral(s.value))
		.Or(
			Try(
				from _colon in Token(new Token.Colon())
				from ident in IdentifierName
				from args in CallArgs
				select (Expression)new Expression.IntrinsicCall(ident, args.ToArray())
			)
		)
		.Or(Try(EnumVariantInit))
		.Or(Try(StructInit))
		.Or(
			Try(
				from ident in IdentifierName
				from args in CallArgs
				select (Expression)new Expression.FunctionCall(ident, args.ToArray())
			)
		)
		.Or(from ident in IdentifierName select (Expression)new Expression.Identifier(ident))
		.Or(
			from _l in Token(new Token.LParen())
			from e in Rec(() => LogicalOr!)
			from _r in Token(new Token.RParen())
			select e
		)
		.Or(Try(Rec(() => IfLet!)))
		.Or(Rec(() => If!))
		.Or(from block in Block select (Expression)block));

	private static readonly Parser<Token, Expression> FieldAccess =
		from expr in Atom
		from accesses in (from _dot in Token(new Token.Dot()) from ident in IdentifierName select ident).Many()
		select accesses.Aggregate(expr,
			(e, ident) => new Expression.FieldAccess(e, ident) { Span = e.Span | ident.Span });

	// Unary: -x, !x
	private static readonly Parser<Token, Expression> Unary = Spanned(
		(from _ in Token(new Token.Minus())
			from e in Rec(() => Unary!)
			select (Expression)new Expression.UnaryOp(UnOp.Neg, e))
		.Or(from _ in Token(new Token.Bang())
			from e in Rec(() => Unary!)
			select (Expression)new Expression.UnaryOp(UnOp.Not, e))
		.Or(FieldAccess));

	// Multiplicative: *, /
	private static readonly Parser<Token, Expression> Multiplicative =
		from left in Unary
		from ops in (
			from op in Token(new Token.Star()).ThenReturn(BinOp.Mul)
				.Or(Token(new Token.Slash()).ThenReturn(BinOp.Div))
			from right in Unary
			select (op, right)
		).Many()
		select ops.Aggregate(left,
			(l, pair) => new Expression.BinaryOp(l, pair.op, pair.right) { Span = l.Span | pair.right.Span });

	// Additive: +, -
	private static readonly Parser<Token, Expression> Additive =
		from left in Multiplicative
		from ops in (
			from op in Token(new Token.Plus()).ThenReturn(BinOp.Add)
				.Or(Token(new Token.Minus()).ThenReturn(BinOp.Sub))
			from right in Multiplicative
			select (op, right)
		).Many()
		select ops.Aggregate(left,
			(l, pair) => new Expression.BinaryOp(l, pair.op, pair.right) { Span = l.Span | pair.right.Span });

	// Equality: ==, !=
	private static readonly Parser<Token, Expression> Equality =
		from left in Additive
		from ops in (
			from op in Token(new Token.EqualEqual()).ThenReturn(BinOp.Eq)
				.Or(Token(new Token.BangEqual()).ThenReturn(BinOp.Neq))
			from right in Additive
			select (op, right)
		).Many()
		select ops.Aggregate(left,
			(l, pair) => new Expression.BinaryOp(l, pair.op, pair.right) { Span = l.Span | pair.right.Span });

	// LogicalAnd: &&
	private static readonly Parser<Token, Expression> LogicalAnd =
		from left in Equality
		from ops in (
			from op in Token(new Token.AmpAmp()).ThenReturn(BinOp.And)
			from right in Equality
			select (op, right)
		).Many()
		select ops.Aggregate(left,
			(l, pair) => new Expression.BinaryOp(l, pair.op, pair.right) { Span = l.Span | pair.right.Span });

	// LogicalOr: ||
	private static readonly Parser<Token, Expression> LogicalOr =
		from left in LogicalAnd
		from ops in (
			from op in Token(new Token.PipePipe()).ThenReturn(BinOp.Or)
			from right in LogicalAnd
			select (op, right)
		).Many()
		select ops.Aggregate(left,
			(l, pair) => new Expression.BinaryOp(l, pair.op, pair.right) { Span = l.Span | pair.right.Span });

	private static readonly Parser<Token, Expression> Expression = LogicalOr.Labelled("expression");

	private static readonly Parser<Token, IEnumerable<Expression>> CallArgs =
		from _lparen in Token(new Token.LParen())
		from args in CommaSeparated(Expression)
		from _rparen in Token(new Token.RParen())
		select args;

	private static readonly Parser<Token, Expression> IfLet = Spanned(
		from _if in Token(new Token.Keyword("if"))
		from _let in Token(new Token.Keyword("let"))
		from enumName in IdentifierName
		from _dot in Token(new Token.Dot())
		from variantName in IdentifierName
		from bindings in Try(
			from _l in Token(new Token.LBrace())
			from names in CommaSeparated(IdentifierName)
			from _r in Token(new Token.RBrace())
			select names.ToArray()
		).Optional()
		from _eq in Token(new Token.Equal())
		from scrutinee in Rec(() => Expression)
		from thenBlock in Block
		from elseBlock in Try(
			from _else in Token(new Token.Keyword("else"))
			from block in Block
			select block
		).Optional()
		select (Expression)new Expression.IfLet(
			enumName, variantName,
			bindings.GetValueOrDefault(Array.Empty<Ident>()),
			scrutinee, thenBlock, elseBlock.GetValueOrDefault()));

	private static readonly Parser<Token, Expression> If = Spanned(
		from _if in Token(new Token.Keyword("if"))
		from condition in Expression
		from _then in Token(new Token.Keyword("then"))
		from thenBlock in Block
		from elseBlock in Try(
			from _else in Token(new Token.Keyword("else"))
			from block in Rec(() => If!).Select(e => new Expression.Block([], e) { Span = e.Span })
				.Or(Block)
			select block
		).Optional()
		select (Expression)new Expression.If(condition, thenBlock, elseBlock.GetValueOrDefault()));

	private static readonly Parser<Token, Statement> Return =
		from _return in Token(new Token.Keyword("return"))
		from expression in Expression.Optional()
		select (Statement)new Statement.Return(expression.GetValueOrDefault());

	private static readonly Parser<Token, Statement> VariableBinding =
		from _ in Token(new Token.Keyword("let"))
		from ident in IdentifierName
		from __ in Token(new Token.Equal())
		from expression in Expression
		select (Statement)new Statement.VariableBinding(ident, expression);

	// Only the `lhs =` prefix backtracks, so errors in the right-hand side are reported where they occur
	private static readonly Parser<Token, Statement> Assignment =
		from lhs in Try(FieldAccess.Before(Token(new Token.Equal())))
		from rhs in Expression
		select (Statement)new Statement.Assignment(lhs, rhs);

	private static readonly Parser<Token, (Ident, Ident)> TypedIdentifier =
		from nameIdent in IdentifierName
		from _colon in Token(new Token.Colon())
		from typeIdent in IdentifierName
		select (nameIdent, typeIdent);

	private static readonly Parser<Token, Param> Parameter =
		from nameIdent in IdentifierName
		from _colon in Token(new Token.Colon())
		from isRef in Token(new Token.Keyword("ref")).Optional()
		from typeIdent in IdentifierName
		select new Param
			{ Name = nameIdent, TypeName = typeIdent, IsRef = isRef.HasValue, Span = nameIdent.Span | typeIdent.Span };

	private static readonly Parser<Token, Statement> FunctionDecl =
		from _fn in Token(new Token.Keyword("fn"))
		from name in IdentifierName
		from _lparen in Token(new Token.LParen())
		from parameters in CommaSeparated(Parameter)
		from _rparen in Token(new Token.RParen())
		from returnType in Try(
				from _colon in Token(new Token.Colon())
				from t in IdentifierName
				select t)
			.Optional()
		from body in Block
		select (Statement)new Statement.FunctionDeclaration(name, parameters.ToArray(), body,
			returnType.HasValue ? returnType.Value : null);

	private static readonly Parser<Token, Statement> StructDecl =
		from _struct in Token(new Token.Keyword("struct"))
		from name in IdentifierName
		from _lbrace in Token(new Token.LBrace())
		from fields in CommaSeparated(TypedIdentifier)
		from _rbrace in Token(new Token.RBrace())
		select (Statement)new Statement.StructDeclaration(name, fields.ToArray());

	private static readonly Parser<Token, EnumVariant> EnumVariantParser =
		from variantName in IdentifierName
		from fields in Try(
			from _l in Token(new Token.LBrace())
			from fs in CommaSeparated(TypedIdentifier)
			from _r in Token(new Token.RBrace())
			select fs
		).Optional()
		select fields.HasValue
			? (EnumVariant)new EnumVariant.Struct(variantName, fields.Value.ToArray())
			: new EnumVariant.Bare(variantName);

	private static readonly Parser<Token, Statement> EnumDecl =
		from _enum in Token(new Token.Keyword("enum"))
		from name in IdentifierName
		from _brace in Token(new Token.LBrace())
		from variants in CommaSeparated(EnumVariantParser)
		from _rbrace in Token(new Token.RBrace())
		select (Statement)new Statement.EnumDeclaration(name, variants.ToArray());

	private static bool IsBlockLike(fluke.Expression expr) =>
		expr is fluke.Expression.If or fluke.Expression.IfLet or fluke.Expression.Block;

	// An expression statement and a block's tail expression share their prefix, so the expression is parsed
	// once and what follows decides: ';' makes it a statement, '}' makes it the tail. Block-like expressions
	// may also stand as statements without ';'. A tail is only allowed inside a block.
	private static Parser<Token, BlockItem> ExpressionItem(bool inBlock) {
		var semicolon = Token(new Token.Semicolon()).ThenReturn(true);
		var end = inBlock ? semicolon.Or(Lookahead(Token(new Token.RBrace())).ThenReturn(false)) : semicolon;
		return SpannedItem(
			from expr in Expression
			from isStatement in IsBlockLike(expr) ? end.Or(Parser<Token>.Return(true)) : end
			select isStatement
				? new BlockItem(new Statement.ExpressionStatement(expr), null)
				: new BlockItem(null, expr));
	}

	private static Parser<Token, BlockItem> Item(bool inBlock) =>
		SpannedItem(
			OneOf(VariableBinding, Return, Assignment).Before(Token(new Token.Semicolon()))
				.Or(OneOf(FunctionDecl, StructDecl, EnumDecl))
				.Select(s => new BlockItem(s, null))
		).Or(ExpressionItem(inBlock));

	private static Parser<Token, BlockItem> SpannedItem(Parser<Token, BlockItem> p) =>
		p.MapWithInput((input, item) => item.Statement is { } s
			? item with { Statement = s with { Span = input[0].Span | input[^1].Span } }
			: item);

	private static readonly Parser<Token, BlockItem> BlockItemParser = Item(inBlock: true);

	private static readonly Parser<Token, Ast> Program =
		from items in Item(inBlock: false).Many()
		from _end in End
		select new Ast(items.Select(i => i.Statement!).ToList());

	public static Ast Parse(List<Token> tokens, SourcePos eof) {
		var result = Program.Parse(tokens);

		if (result.Success) return result.Value;

		var err = result.Error!;
		var span = err.Unexpected.HasValue ? err.Unexpected.Value.Span : new Span(eof, eof);

		throw CompileException.FromParseError(err, span,
			tokens => string.Join(" ", tokens.Select(t => t.Spelling())));
	}
}