using System.Diagnostics;
using fluke.Hir;

namespace fluke.Mir;

// Statements, places and values of the MIR. There is no unwinding: a failing assert ends the
// program, so calls are ordinary rvalues and blocks have no cleanup edges.

// A memory location: a local, optionally followed by projections into it, e.g. `_1.position.x`
internal sealed record Place(MirLocal Local, Projection[] Projections) {
	public Place(MirLocal local) : this(local, []) { }

	public Place Project(Projection projection) => this with { Projections = [.. Projections, projection] };

	// Type of the location. A Downcast does not change the type; it only selects which variant's
	// fields the following Field projection reads.
	public Ty Type =>
		Projections.Aggregate(Local.Type, (type, projection) => projection switch {
			Projection.Field(var def) => def.Type,
			Projection.Downcast => type,
			_ => throw new UnreachableException(),
		});

	// e.g. `_1.position.x`, or `(_2 as Circle).radius` for a variant field
	public override string ToString() =>
		Projections.Aggregate(Local.ToString(), (text, projection) => projection switch {
			Projection.Field(var def) => $"{text}.{def.Name}",
			Projection.Downcast(var variant) => $"({text} as {variant.Name})",
			_ => throw new UnreachableException(),
		});
}

internal abstract record Projection {
	// A struct field, or a variant field when preceded by Downcast
	public sealed record Field(FieldDef def) : Projection;

	// Views an enum as the payload of one variant. Only valid when the tag says it is that variant.
	public sealed record Downcast(VariantDef variant) : Projection;
}

internal abstract record Constant(Ty Type) {
	public sealed record Int(int value) : Constant(Ty.Int);

	public sealed record Bool(bool value) : Constant(Ty.Bool);

	public sealed record String(string value) : Constant(Ty.String);

	public sealed record Unit() : Constant(Ty.Unit);

	public sealed override string ToString() =>
		this switch {
			Int(var value) => value.ToString(),
			Bool(var value) => value ? "true" : "false",
			String(var value) => $"\"{value}\"",
			Unit => "()",
			_ => throw new UnreachableException(),
		};
}

internal abstract record Operand {
	public sealed record Copy(Place place) : Operand;

	// Lowering only produces Copy; a later pass turns last uses of reference-counted values into Move
	public sealed record Move(Place place) : Operand;

	public sealed record Const(Constant value) : Operand;

	public Ty Type =>
		this switch {
			Copy(var place) => place.Type,
			Move(var place) => place.Type,
			Const(var value) => value.Type,
			_ => throw new UnreachableException(),
		};

	public sealed override string ToString() =>
		this switch {
			Copy(var place) => $"copy {place}",
			Move(var place) => $"move {place}",
			Const(var value) => $"const {value}",
			_ => throw new UnreachableException(),
		};
}

internal abstract record Rvalue {
	public sealed record Use(Operand operand) : Rvalue;

	// Never And or Or, which are lowered to branches. Eq and Neq work on any type, including
	// structs and enums.
	public sealed record Binary(BinOp op, Operand left, Operand right) : Rvalue;

	public sealed record Unary(UnOp op, Operand operand) : Rvalue;

	// Field values in declaration order
	public sealed record StructAggregate(StructDef def, Operand[] fields) : Rvalue;

	// Field values in declaration order; empty for bare variants
	public sealed record VariantAggregate(VariantDef variant, Operand[] fields) : Rvalue;

	// The tag of the enum at place, as an int
	public sealed record Discriminant(Place place) : Rvalue;

	public sealed record Call(FnDef fn, Operand[] args) : Rvalue;

	// Never And or Or (see Binary), so those have no type here
	public Ty Type =>
		this switch {
			Use(var operand) => operand.Type,
			Binary(BinOp.Add or BinOp.Sub or BinOp.Mul or BinOp.Div, _, _) => Ty.Int,
			Binary(BinOp.Eq or BinOp.Neq, _, _) => Ty.Bool,
			Unary(UnOp.Neg, _) => Ty.Int,
			Unary(UnOp.Not, _) => Ty.Bool,
			StructAggregate(var def, _) => new Ty.Struct(def),
			VariantAggregate(var variant, _) => new Ty.Enum(variant.Parent),
			Discriminant => Ty.Int,
			Call(var fn, _) => fn.ReturnType,
			_ => throw new UnreachableException(),
		};

	public sealed override string ToString() =>
		this switch {
			Use(var operand) => operand.ToString(),
			Binary(var op, var left, var right) => $"{left} {op.Symbol()} {right}",
			Unary(var op, var operand) => $"{op.Symbol()}{operand}",
			StructAggregate(var def, var fields) => $"{def.Name}{FormatFields(def.Fields, fields)}",
			VariantAggregate(var variant, var fields) => $"{variant}{FormatFields(variant.Fields, fields)}",
			Discriminant(var place) => $"discriminant({place})",
			Call(var fn, var args) => $"{fn.MangledName}({string.Join(", ", args.AsEnumerable())})",
			_ => throw new UnreachableException(),
		};

	// Empty for bare variants
	private static string FormatFields(List<FieldDef> defs, Operand[] values) =>
		values.Length == 0 ? "" : $" {{ {string.Join(", ", defs.Zip(values, (d, v) => $"{d.Name}: {v}"))} }}";
}

internal enum MirIntrinsic {
	Print,
	Assert,
	Exit,
}

internal abstract record MirStatement {
	// The whole rvalue is evaluated before dest is written, so its operands may read dest, e.g.
	// `_1 = P { x: copy _1.y, y: copy _1.x }`. Code generation must not build an aggregate in place.
	public sealed record Assign(Place dest, Rvalue value) : MirStatement;

	// :enum_to_int is not an intrinsic here; it lowers to Rvalue.Discriminant
	public sealed record Intrinsic(MirIntrinsic kind, Operand[] args) : MirStatement;

	public sealed override string ToString() =>
		this switch {
			Assign(var dest, var value) => $"{dest} = {value}",
			Intrinsic(var kind, var args) =>
				$":{kind.ToString().ToLowerInvariant()}({string.Join(", ", args.AsEnumerable())})",
			_ => throw new UnreachableException(),
		};
}

internal abstract record Terminator {
	public sealed record Goto(BasicBlock target) : Terminator;

	public sealed record Branch(Operand condition, BasicBlock then, BasicBlock @else) : Terminator;

	// Jumps to the block of the first case equal to value, otherwise to otherwise
	public sealed record Switch(Operand value, (int value, BasicBlock target)[] cases, BasicBlock otherwise)
		: Terminator;

	// Returns _0. Only the body's single return block ends in this.
	public sealed record Return : Terminator;

	// Control never reaches the end of this block, e.g. the block following a `return`
	public sealed record Unreachable : Terminator;

	public IEnumerable<BasicBlock> Successors =>
		this switch {
			Goto(var target) => [target],
			Branch(_, var then, var @else) => [then, @else],
			Switch(_, var cases, var otherwise) => cases.Select(c => c.target).Append(otherwise),
			Return or Unreachable => [],
			_ => throw new UnreachableException(),
		};

	public sealed override string ToString() =>
		this switch {
			Goto(var target) => $"goto -> {target}",
			Branch(var condition, var then, var @else) => $"branch {condition} -> [true: {then}, false: {@else}]",
			Switch(var value, var cases, var otherwise) =>
				$"switch {value} -> [{string.Join(", ", cases.Select(c => $"{c.value}: {c.target}"))}, otherwise: {otherwise}]",
			Return => "return",
			Unreachable => "unreachable",
			_ => throw new UnreachableException(),
		};
}