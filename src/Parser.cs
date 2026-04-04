using System.Diagnostics;
using Pidgin;
using static Pidgin.Parser<fluke.Token>;

namespace fluke;

public record Ast(List<Statement> Statements);

public abstract record Statement {
    public record IntrinsicCall(string name, Expression? expr) : Statement;
    public record ExpressionStatement(Expression expr) : Statement;

    public sealed override string ToString() {
       return this switch {
          IntrinsicCall(var name, null) => $"Intrinsic({name})",
          IntrinsicCall(var name, var arg) => $"Intrinsic({name}, \"{arg}\")",
          ExpressionStatement(var expr) => $"Expr({expr})",
          _ => throw new UnreachableException()
       };
    }
}

public enum Op {
    Add, Sub, Mul, Div, Neg, Not
}

public abstract record Expression {
    public record StringLiteral(string value) : Expression;
    public record IntLiteral(int value) : Expression;
    public record Identifier(string name) : Expression;
    public record BinaryOp(Expression left, Op op, Expression right) : Expression;
    public record UnaryOp(Op op, Expression operand) : Expression;

    public sealed override string ToString() {
        return this switch {
            IntLiteral(var v) => v.ToString(),
            StringLiteral(var v) => $"\"{v}\"",
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

    // --- Expression parsers (manual precedence climbing) ---

    // Atoms: literals, identifiers, parenthesized expressions
    private static readonly Parser<Token, Expression> Atom =
        (from t in Token(t => t is Token.Int)
         select (Expression)new Expression.IntLiteral(((Token.Int)t).value))
        .Or(from s in String
            select (Expression)new Expression.StringLiteral(s))
        .Or(from n in IdentifierName
            select (Expression)new Expression.Identifier(n))
        .Or(from _l in Token(new Token.LParen())
            from e in Pidgin.Parser.Rec(() => Additive)
            from _r in Token(new Token.RParen())
            select e);

    // Unary: -x, !x
    private static readonly Parser<Token, Expression> Unary =
        (from _ in Token(new Token.Minus())
         from e in Pidgin.Parser.Rec(() => Unary)
         select (Expression)new Expression.UnaryOp(Op.Neg, e))
        .Or(from _ in Token(new Token.Bang())
            from e in Pidgin.Parser.Rec(() => Unary)
            select (Expression)new Expression.UnaryOp(Op.Not, e))
        .Or(Atom);

    // Multiplicative: *, /
    private static readonly Parser<Token, Expression> Multiplicative =
        from left in Unary
        from ops in (
            from op in Token(new Token.Star()).ThenReturn(Op.Mul)
                        .Or(Token(new Token.Slash()).ThenReturn(Op.Div))
            from right in Unary
            select (op, right)
        ).Many()
        select ops.Aggregate(left, (l, pair) => (Expression)new Expression.BinaryOp(l, pair.op, pair.right));

    // Additive: +, -
    private static readonly Parser<Token, Expression> Additive =
        from left in Multiplicative
        from ops in (
            from op in Token(new Token.Plus()).ThenReturn(Op.Add)
                        .Or(Token(new Token.Minus()).ThenReturn(Op.Sub))
            from right in Multiplicative
            select (op, right)
        ).Many()
        select ops.Aggregate(left, (l, pair) => (Expression)new Expression.BinaryOp(l, pair.op, pair.right));

    private static readonly Parser<Token, Expression> Expression = Additive;
    
    
    private static readonly Parser<Token, Statement> IntrinsicCall =
       from _ in Token(new Token.Colon())
       from name in IdentifierName
       from arg in Expression.Optional()
       select (Statement)new Statement.IntrinsicCall(name, arg.HasValue ? arg.Value : null);

    private static readonly Parser<Token, Statement> ExpressionStatement =
       from expr in Expression
       select (Statement)new Statement.ExpressionStatement(expr);

    private static readonly Parser<Token, Statement> Statement =
       from statement in IntrinsicCall.Or(ExpressionStatement)
       from _ in Token(new Token.Semicolon())
       select statement;

    private static readonly Parser<Token, Ast> Program =
       from stmts in Statement.Many()
       from _end in End
       select new Ast(stmts.ToList());

    public static Ast Parse(List<Token> tokens) =>
       Program.ParseOrThrow(tokens);
}