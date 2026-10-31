using System.Diagnostics;
using fluke.Hir;

namespace fluke.Mir;

// Checks the invariants lowering must uphold for one body, adding a message per violation to errors.
// The Verify* helpers return the type of what they checked, or null if it is invalid, so a broken
// operand is reported once and not again by every check that uses its type.
internal sealed class MirVerifier(MirBody body, List<string> errors) {
	private readonly HashSet<MirLocal> locals = body.Locals.ToHashSet();
	private readonly HashSet<BasicBlock> blocks = body.Blocks.ToHashSet();

	// Block being checked, for error messages; null for checks about the whole body
	private BasicBlock? current;

	private void Error(string message) =>
		errors.Add(current is null ? $"{body.Fn}: {message}" : $"{body.Fn}: {current}: {message}");

	public void Verify() {
		if (body.Blocks.Count == 0) {
			Error("has no blocks");
			return;
		}

		var returnBlocks = body.Blocks.Count(b => b.Terminator is Terminator.Return);
		if (returnBlocks != 1)
			Error($"has {returnBlocks} blocks ending in return, expected exactly 1");

		foreach (var block in body.Blocks) {
			current = block;
			VerifyBlock(block);
		}

		current = null;
	}

	private void VerifyBlock(BasicBlock block) {
		foreach (var statement in block.Statements) {
			switch (statement) {
				case MirStatement.Assign(var dest, var value): {
					var destType = VerifyPlace(dest);
					var valueType = VerifyRvalue(value);
					if (destType is not null && valueType is not null && destType != valueType)
						Error($"`{statement}` assigns '{valueType}' to a place of type '{destType}'");
					break;
				}
				case MirStatement.Intrinsic(var kind, var args): {
					var types = args.Select(VerifyOperand).ToArray();
					switch (kind) {
						case MirIntrinsic.Print:
							if (args.Length > 1)
								Error($"`{statement}` takes 0 or 1 arguments, found {args.Length}");
							break;
						case MirIntrinsic.Assert:
							if (args.Length != 1)
								Error($"`{statement}` takes 1 argument, found {args.Length}");
							else
								ExpectType(types[0], Ty.Bool, $"argument of `{statement}`");
							break;
						case MirIntrinsic.Exit:
							if (args.Length != 1)
								Error($"`{statement}` takes 1 argument, found {args.Length}");
							else
								ExpectType(types[0], Ty.Int, $"argument of `{statement}`");
							break;
						default:
							throw new UnreachableException();
					}

					break;
				}
				default:
					throw new UnreachableException();
			}
		}

		var terminator = block.Terminator;
		if (terminator is null) {
			Error("has no terminator");
			return;
		}

		switch (terminator) {
			case Terminator.Goto:
				break;
			case Terminator.Branch(var condition, _, _):
				ExpectType(VerifyOperand(condition), Ty.Bool, $"condition of `{terminator}`");
				break;
			case Terminator.Switch(var value, var cases, _): {
				ExpectType(VerifyOperand(value), Ty.Int, $"value of `{terminator}`");
				var duplicates = cases.GroupBy(c => c.value).Where(g => g.Count() > 1).Select(g => g.Key);
				foreach (var duplicate in duplicates)
					Error($"`{terminator}` has more than one case for {duplicate}");
				break;
			}
			case Terminator.Return or Terminator.Unreachable:
				break;
			default:
				throw new UnreachableException();
		}

		foreach (var target in terminator.Successors.Where(t => !blocks.Contains(t)))
			Error($"jumps to {target}, which belongs to another body");
	}

	private Ty? VerifyRvalue(Rvalue value) {
		switch (value) {
			case Rvalue.Use(var operand):
				return VerifyOperand(operand);
			case Rvalue.Binary(var op, var left, var right): {
				var leftType = VerifyOperand(left);
				var rightType = VerifyOperand(right);
				switch (op) {
					case BinOp.And or BinOp.Or:
						Error($"`{value}` uses '{op.Symbol()}', which must be lowered to branches");
						return null;
					case BinOp.Eq or BinOp.Neq:
						if (leftType is not null && rightType is not null && leftType != rightType)
							Error($"`{value}` compares '{leftType}' with '{rightType}'");
						return Ty.Bool;
					default:
						ExpectType(leftType, Ty.Int, $"left operand of `{value}`");
						ExpectType(rightType, Ty.Int, $"right operand of `{value}`");
						return Ty.Int;
				}
			}
			case Rvalue.Unary(var op, var operand): {
				var type = op switch {
					UnOp.Neg => Ty.Int,
					UnOp.Not => Ty.Bool,
					_ => throw new UnreachableException(),
				};
				ExpectType(VerifyOperand(operand), type, $"operand of `{value}`");
				return type;
			}
			case Rvalue.StructAggregate(var def, var fields):
				VerifyArguments(fields, def.Fields.Select(f => f.Type).ToList(), "field", value);
				return new Ty.Struct(def);
			case Rvalue.VariantAggregate(var variant, var fields):
				VerifyArguments(fields, variant.Fields.Select(f => f.Type).ToList(), "field", value);
				return new Ty.Enum(variant.Parent);
			case Rvalue.Discriminant(var place): {
				var type = VerifyPlace(place);
				if (type is not null and not Ty.Enum)
					Error($"`{value}` reads the discriminant of non-enum type '{type}'");
				return Ty.Int;
			}
			case Rvalue.Call(var fn, var args):
				VerifyArguments(args, fn.Params.Select(p => p.Type).ToList(), "argument", value);
				return fn.ReturnType;
			default:
				throw new UnreachableException();
		}
	}

	// Checks the number and types of an aggregate's fields or a call's arguments
	private void VerifyArguments(Operand[] actual, List<Ty> expected, string what, Rvalue value) {
		var types = actual.Select(VerifyOperand).ToArray();
		if (types.Length != expected.Count) {
			Error($"`{value}` has {types.Length} {what}s, expected {expected.Count}");
			return;
		}

		for (var i = 0; i < types.Length; i++)
			ExpectType(types[i], expected[i], $"{what} {i + 1} of `{value}`");
	}

	private Ty? VerifyOperand(Operand operand) =>
		operand switch {
			Operand.Copy(var place) => VerifyPlace(place),
			Operand.Move(var place) => VerifyPlace(place),
			Operand.Const(var constant) => constant.Type,
			_ => throw new UnreachableException(),
		};

	// A Field must belong to the struct being projected, or to the variant selected by a directly
	// preceding Downcast. A Downcast only applies to an enum and must be followed by a Field, so no
	// place ends in one.
	private Ty? VerifyPlace(Place place) {
		if (!locals.Contains(place.Local)) {
			Error($"{place} refers to a local of another body");
			return null;
		}

		var type = place.Local.Type;
		VariantDef? variant = null;
		for (var i = 0; i < place.Projections.Length; i++) {
			switch (place.Projections[i]) {
				case Projection.Field(var field): {
					var fields = variant?.Fields ?? (type is Ty.Struct(var structDef) ? structDef.Fields : null);
					if (fields is null || !fields.Contains(field)) {
						Error(
							$"{place} projects field '{field}', which does not belong to '{variant?.ToString() ?? type.ToString()}'");
						return null;
					}

					type = field.Type;
					variant = null;
					break;
				}
				case Projection.Downcast(var target):
					if (variant is not null || type is not Ty.Enum(var enumDef) || enumDef != target.Parent) {
						Error($"{place} downcasts '{type}' to variant '{target}'");
						return null;
					}

					if (i == place.Projections.Length - 1) {
						Error($"{place} ends in a downcast; it must be followed by a field");
						return null;
					}

					variant = target;
					break;
				default:
					throw new UnreachableException();
			}
		}

		return type;
	}

	// actual is null when it was invalid, which has already been reported
	private void ExpectType(Ty? actual, Ty expected, string what) {
		if (actual is not null && actual != expected)
			Error($"{what} has type '{actual}', expected '{expected}'");
	}
}