using System.Diagnostics;

namespace fluke.Hir;

// Typed, name-resolved tree. Control flow is still structured; making it explicit (and deciding
// moves, copies and drops) is the job of the MIR.

internal enum IntrinsicKind {
	Print,
	Assert,
	EnumToInt,
}

internal abstract record HirExpression(Ty Type, Span Span) {
	public record IntLiteral(int value, Span Span) : HirExpression(Ty.Int, Span);

	public record BoolLiteral(bool value, Span Span) : HirExpression(Ty.Bool, Span);

	public record StringLiteral(string value, Span Span) : HirExpression(Ty.String, Span);

	public record Unit(Span Span) : HirExpression(Ty.Unit, Span);

	public record Local(LocalDef def, Span Span) : HirExpression(def.Type, Span);

	public record Call(FnDef fn, HirExpression[] args, Span Span) : HirExpression(fn.ReturnType, Span);

	public record Intrinsic(IntrinsicKind kind, HirExpression[] args, Ty Type, Span Span) : HirExpression(Type, Span);

	// && and || stay binary operators here; the MIR lowers them to branches
	public record Binary(BinOp op, HirExpression left, HirExpression right, Ty Type, Span Span)
		: HirExpression(Type, Span);

	public record Unary(UnOp op, HirExpression operand, Ty Type, Span Span) : HirExpression(Type, Span);

	public record Field(HirExpression @base, FieldDef field, Span Span) : HirExpression(field.Type, Span);

	// Fields are in source order, which is evaluation order
	public record StructInit(StructDef def, (FieldDef field, HirExpression value)[] fields, Span Span)
		: HirExpression(new Ty.Struct(def), Span);

	// Fields are in source order, which is evaluation order. Empty for bare variants.
	public record VariantInit(VariantDef variant, (FieldDef field, HirExpression value)[] fields, Span Span)
		: HirExpression(new Ty.Enum(variant.Parent), Span);

	// The tail is Unit when the source block has none
	public record Block(HirStatement[] stmts, HirExpression tail, Ty Type, Span Span) : HirExpression(Type, Span) {
		public bool IsEmpty => stmts.Length == 0 && tail is Unit;
	}

	// The else block is an empty block when the source has no else
	public record If(HirExpression condition, Block thenBlock, Block elseBlock, Ty Type, Span Span)
		: HirExpression(Type, Span);

	public record IfLet(
		HirExpression scrutinee,
		VariantDef variant,
		(FieldDef field, LocalDef local)[] bindings,
		Block thenBlock,
		Block elseBlock,
		Ty Type,
		Span Span) : HirExpression(Type, Span);

	// Stands in for an expression that failed to check. Its type is Error, so CoercesTo accepts it
	// everywhere and the surrounding code reports no follow-up errors.
	public record Error(Span Span) : HirExpression(Ty.Error, Span);

	public sealed override string ToString() {
		return this switch {
			IntLiteral(var value, _) => value.ToString(),
			BoolLiteral(var value, _) => value.ToString(),
			StringLiteral(var value, _) => $"\"{value}\"",
			Unit(_) => "()",
			Local(var def, _) => def.UniqueName,
			Call(var fn, var args, _) => $"{fn.MangledName}({string.Join(", ", args)})",
			Intrinsic(var kind, var args, var type, _) =>
				$":{kind.ToString().ToLowerInvariant()}({string.Join(", ", args)}) : {type}",
			Binary(var op, var left, var right, var type, _) => $"({left} {op.Symbol()} {right} : {type})",
			Unary(var op, var operand, var type, _) => $"{op.Symbol()}{operand} : {type}",
			Field(var hirExpr, var field, _) => $"{hirExpr}.{field}",
			StructInit(var def, var fields, _) =>
				$"{def.Name}{FormatFields(fields.Select(f => $"{f.field} = {f.value}"))}",
			VariantInit(var variant, var fields, _) =>
				$"{variant}{FormatFields(fields.Select(f => $"{f.field} = {f.value}"))}",
			Block(var stmts, var tail, var type, _) => FormatBlock(stmts, tail, type),
			If(var condition, var then, var @else, _, _) =>
				$"if {condition} {then}{FormatElse(@else)}",
			IfLet(var scrutinee, var variant, var bindings, var then, var @else, _, _) =>
				$"if let {variant}{FormatFields(bindings.Select(b => $"{b.field}: {b.local.UniqueName}"))} = {scrutinee} {then}"
				+ FormatElse(@else),
			Error(_) => "ERROR",
			_ => throw new UnreachableException(),
		};
	}

	// Unit tails are omitted, so blocks print like the source
	private static string FormatBlock(HirStatement[] stmts, HirExpression tail, Ty type) {
		var lines = stmts.Select(s => $"{s};").Concat(tail is not Unit ? [tail.ToString()] : []).ToArray();
		if (lines.Length == 0) return $"{{}} : {type}";
		return $"{{\n{string.Join("\n", lines.Select(Indent))}\n}} : {type}";
	}

	// Empty else blocks are omitted, so a missing else prints like the source
	private static string FormatElse(Block @else) => @else.IsEmpty ? "" : $" else {@else}";

	// Empty for bare variants and struct inits without fields
	private static string FormatFields(IEnumerable<string> fields) {
		var joined = string.Join(", ", fields);
		return joined.Length == 0 ? "" : $" {{ {joined} }}";
	}

	// Indents every line, so nested blocks line up
	private static string Indent(string s) => "\t" + s.Replace("\n", "\n\t");
}

internal abstract record HirStatement(Span Span) {
	public record Let(LocalDef local, HirExpression init, Span Span) : HirStatement(Span);

	// The checker guarantees that place is a Local or a chain of Field accesses ending in one
	public record Assign(HirExpression place, HirExpression value, Span Span) : HirStatement(Span);

	public record Expr(HirExpression Expression, Span Span) : HirStatement(Span);

	// Always diverges; a block containing one has type Never, even if it has a tail
	public record Return(HirExpression value, Span Span) : HirStatement(Span);

	// Whether control never continues past this statement, e.g. an if whose branches both return
	public bool Diverges =>
		this switch {
			Return => true,
			Expr(var expr, _) => expr.Type == Ty.Never,
			Let(_, var init, _) => init.Type == Ty.Never,
			Assign(_, var value, _) => value.Type == Ty.Never,
			_ => throw new UnreachableException(),
		};

	public sealed override string ToString() =>
		this switch {
			Let(var local, var init, _) => $"let {local.UniqueName} : {local.Type} = {init}",
			Assign(var place, var value, _) => $"{place} = {value}",
			Expr(var expr, _) => expr.ToString(),
			Return(var value, _) => value is not HirExpression.Unit ? $"return {value}" : "return",
			_ => throw new UnreachableException(),
		};
}