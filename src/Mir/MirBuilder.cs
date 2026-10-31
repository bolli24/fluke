using System.Diagnostics;
using fluke.Hir;

namespace fluke.Mir;

internal sealed class MirBuilder {
	private readonly HirProgram hirProgram;
	private readonly MirProgram program;

	private MirBody currentBody = null!;
	private BasicBlock currentBlock = null!;
	private BasicBlock returnBlock = null!;

	// Locals of the body being lowered, by the HIR binding they were created for
	private readonly Dictionary<LocalDef, MirLocal> locals = new();

	internal MirBuilder(HirProgram hirProgram) {
		this.hirProgram = hirProgram;
		program = new MirProgram(new MirBody(hirProgram.Main));
		program.Structs.AddRange(hirProgram.Structs);
		program.Enums.AddRange(hirProgram.Enums);
	}

	internal MirProgram Lower() {
		LowerBodies();

		// Invalid MIR is a compiler bug, not a user error, so it is an exception rather than a diagnostic.
		// The message includes the MIR, since the errors are hard to follow without it.
		var errors = program.Verify();
		if (errors.Count > 0) {
			throw new InvalidOperationException(
				$"invalid MIR:\n{string.Join("\n", errors)}\n\n{string.Join("\n\n", program.Bodies)}");
		}

		return program;
	}

	private void LowerBodies() {
		LowerFn(hirProgram.Main, program.Main);

		foreach (var fnDef in hirProgram.Functions) {
			var body = new MirBody(fnDef);
			program.Functions.Add(body);
			LowerFn(fnDef, body);
		}
	}

	private void LowerFn(FnDef fn, MirBody body) {
		currentBody = body;
		locals.Clear();

		var entryBlock = currentBody.NewBlock();
		returnBlock = currentBody.NewBlock();
		currentBlock = entryBlock;

		foreach (var paramLocal in body.Params)
			locals.Add(paramLocal.Source!, paramLocal);

		LowerInto(new Place(currentBody.ReturnLocal), fn.Body!);
		currentBlock.Terminator = new Terminator.Goto(returnBlock);
		returnBlock.Terminator = new Terminator.Return();

		RemoveUnreachableBlocks(body);
	}

	// Creates the MIR local for a HIR binding and registers it, so later uses resolve to it
	private MirLocal DeclareLocal(LocalDef def) {
		var local = currentBody.NewLocal(def.Type, def);
		locals.Add(def, local);
		return local;
	}

	private Place NewTemp(Ty type) => new(currentBody.NewLocal(type, null));

	private void Assign(Place dest, Rvalue value) =>
		currentBlock.Statements.Add(new MirStatement.Assign(dest, value));

	// Ends the current block with terminator and continues lowering in next, so the current block
	// never has a terminator yet
	private void Terminate(Terminator terminator, BasicBlock next) {
		currentBlock.Terminator = terminator;
		currentBlock = next;
	}

	private static void RemoveUnreachableBlocks(MirBody body) {
		var visited = new HashSet<BasicBlock>();
		var remaining = new Stack<BasicBlock>();
		remaining.Push(body.Entry);
		while (remaining.TryPop(out var block)) {
			if (visited.Contains(block)) continue;
			foreach (var successor in block.Terminator!.Successors) {
				remaining.Push(successor);
			}

			visited.Add(block);
		}

		body.Blocks.RemoveAll(b => !visited.Contains(b));
	}

	private void LowerStatement(HirStatement statement) {
		switch (statement) {
			case HirStatement.Let(var localDef, var value, _):
				LowerInto(new Place(DeclareLocal(localDef)), value);
				break;
			case HirStatement.Assign(var place, var value, _):
				LowerInto(LowerPlace(place), value);
				break;
			case HirStatement.Expr(var expr, _):
				LowerInto(NewTemp(expr.Type), expr);
				break;
			case HirStatement.Return(var value, _):
				LowerInto(new Place(currentBody.ReturnLocal), value);
				// Code after the return goes into a block nothing jumps to
				Terminate(new Terminator.Goto(returnBlock), currentBody.NewBlock());
				break;
			default:
				throw new UnreachableException();
		}
	}

	// Lowers any expression, writing its value to dest. Block, If, IfLet, && and || and Intrinsic are
	// handled here directly; everything else becomes `dest = LowerRvalue(expr)`.
	private void LowerInto(Place dest, HirExpression expr) {
		switch (expr) {
			case HirExpression.Intrinsic(var kind, var args, _, _): {
				if (kind == IntrinsicKind.EnumToInt) {
					Assign(dest, new Rvalue.Discriminant(LowerPlace(args[0])));
					break;
				}

				var mirKind = kind switch {
					IntrinsicKind.Print => MirIntrinsic.Print,
					IntrinsicKind.Assert => MirIntrinsic.Assert,
					IntrinsicKind.Exit => MirIntrinsic.Exit,
					_ => throw new UnreachableException(),
				};
				var argOperands = LowerOperands(args);

				// Print, assert and exit return unit, which has no storage, so dest is not written
				currentBlock.Statements.Add(new MirStatement.Intrinsic(mirKind, argOperands));
				break;
			}
			case HirExpression.Block(var statements, var tail, _, _): {
				foreach (var statement in statements)
					LowerStatement(statement);

				LowerInto(dest, tail);
				break;
			}
			case HirExpression.If(var condition, var thenHir, var elseHir, _, _): {
				var conditionOperand = LowerOperand(condition);
				var thenBlock = currentBody.NewBlock();
				var elseBlock = currentBody.NewBlock();
				var joinBlock = currentBody.NewBlock();
				Terminate(new Terminator.Branch(conditionOperand, thenBlock, elseBlock), thenBlock);
				LowerInto(dest, thenHir);
				Terminate(new Terminator.Goto(joinBlock), elseBlock);
				LowerInto(dest, elseHir);
				Terminate(new Terminator.Goto(joinBlock), joinBlock);
				break;
			}
			case HirExpression.Binary((BinOp.And or BinOp.Or) and var op, var left, var right, _, _): {
				var leftOperand = LowerOperand(left);
				var thenBlock = currentBody.NewBlock();
				var elseBlock = currentBody.NewBlock();
				var joinBlock = currentBody.NewBlock();
				Terminate(new Terminator.Branch(leftOperand, thenBlock, elseBlock), thenBlock);
				if (op == BinOp.And) {
					LowerInto(dest, right);
				}
				else {
					Assign(dest, new Rvalue.Use(new Operand.Const(new Constant.Bool(true))));
				}

				Terminate(new Terminator.Goto(joinBlock), elseBlock);
				if (op == BinOp.And) {
					Assign(dest, new Rvalue.Use(new Operand.Const(new Constant.Bool(false))));
				}
				else {
					LowerInto(dest, right);
				}

				Terminate(new Terminator.Goto(joinBlock), joinBlock);
				break;
			}
			case HirExpression.IfLet(var scrutineeHir, var variant, var bindings, var thenHir, var elseHir, _, _): {
				var scrutinee = LowerPlace(scrutineeHir);
				var tag = NewTemp(Ty.Int);
				Assign(tag, new Rvalue.Discriminant(scrutinee));
				var thenBlock = currentBody.NewBlock();
				var elseBlock = currentBody.NewBlock();
				var joinBlock = currentBody.NewBlock();
				Terminate(new Terminator.Switch(new Operand.Copy(tag), [(variant.Tag, thenBlock)], elseBlock),
					thenBlock);

				// Bindings are read only here, where the tag guarantees the downcast is valid
				var payload = scrutinee.Project(new Projection.Downcast(variant));
				foreach (var (fieldDef, localDef) in bindings) {
					var field = payload.Project(new Projection.Field(fieldDef));
					Assign(new Place(DeclareLocal(localDef)), new Rvalue.Use(new Operand.Copy(field)));
				}

				LowerInto(dest, thenHir);
				Terminate(new Terminator.Goto(joinBlock), elseBlock);
				LowerInto(dest, elseHir);
				Terminate(new Terminator.Goto(joinBlock), joinBlock);
				break;
			}
			default:
				Assign(dest, LowerRvalue(expr));
				break;
		}
	}

	// The storage an expression denotes: a Local, or a Field chain on one. Anything else is lowered into
	// a new temp first (e.g. reading `make().x`). Assignment targets never need that, since the checker
	// only allows places there.
	private Place LowerPlace(HirExpression expr) {
		switch (expr) {
			case HirExpression.Local(var def, _):
				return new Place(locals[def]);
			case HirExpression.Field(var @base, var def, _):
				return LowerPlace(@base).Project(new Projection.Field(def));
			default: {
				var temp = NewTemp(expr.Type);
				LowerInto(temp, expr);
				return temp;
			}
		}
	}

	// Literals and Unit become constants. Anything else is a `copy` of its place, which for anything
	// other than a Local or Field is a new temp holding the value.
	private Operand LowerOperand(HirExpression expr) =>
		expr switch {
			HirExpression.IntLiteral(var value, _) => new Operand.Const(new Constant.Int(value)),
			HirExpression.BoolLiteral(var value, _) => new Operand.Const(new Constant.Bool(value)),
			HirExpression.StringLiteral(var value, _) => new Operand.Const(new Constant.String(value)),
			HirExpression.Unit => new Operand.Const(new Constant.Unit()),
			_ => new Operand.Copy(LowerPlace(expr)),
		};

	// Lowers the operands of one rvalue, left to right. A `copy` of a place only reads it when the
	// rvalue's statement runs, after all later operands have been lowered. So a Local or Field operand is
	// read into a temp right away when a later operand might assign to it, e.g. `x + { x = 5; 1 }`.
	//
	// TODO: this is quadratic in two ways, harmless for realistic code but worth fixing:
	// - Within one list, each operand rescans all later ones.
	// - MayAssign walks the whole subtree, and nested operands walk their subtrees again
	private Operand[] LowerOperands(IReadOnlyList<HirExpression> exprs) {
		var operands = new Operand[exprs.Count];
		for (var i = 0; i < exprs.Count; i++) {
			var expr = exprs[i];
			if (expr is HirExpression.Local or HirExpression.Field && exprs.Skip(i + 1).Any(MayAssign)) {
				var temp = NewTemp(expr.Type);
				Assign(temp, new Rvalue.Use(LowerOperand(expr)));
				operands[i] = new Operand.Copy(temp);
			}
			else {
				operands[i] = LowerOperand(expr);
			}
		}

		return operands;
	}

	// Lowers initializer values in source order, which is evaluation order, and returns them in
	// declaration order, as aggregates expect
	private Operand[] LowerFields((FieldDef field, HirExpression value)[] fields) {
		var values = LowerOperands(fields.Select(f => f.value).ToArray());
		var operands = new Operand[fields.Length];
		for (var i = 0; i < fields.Length; i++)
			operands[fields[i].field.Index] = values[i];
		return operands;
	}

	// Whether lowering expr might assign to a local. Conservative: only statements can assign, so any
	// Block, If or IfLet counts. Calls cannot assign to the caller's locals, since there are no closures
	// or ref parameters.
	private static bool MayAssign(HirExpression expr) =>
		expr switch {
			HirExpression.Block or HirExpression.If or HirExpression.IfLet => true,
			HirExpression.IntLiteral or HirExpression.BoolLiteral or HirExpression.StringLiteral
				or HirExpression.Unit or HirExpression.Local => false,
			HirExpression.Field(var @base, _, _) => MayAssign(@base),
			HirExpression.Unary(_, var operand, _, _) => MayAssign(operand),
			HirExpression.Binary(_, var left, var right, _, _) => MayAssign(left) || MayAssign(right),
			HirExpression.Call(_, var args, _) => args.Any(MayAssign),
			HirExpression.Intrinsic(_, var args, _, _) => args.Any(MayAssign),
			HirExpression.StructInit(_, var fields, _) => fields.Any(f => MayAssign(f.value)),
			HirExpression.VariantInit(_, var fields, _) => fields.Any(f => MayAssign(f.value)),
			_ => throw new UnreachableException(),
		};

	// Only expressions that are a single rvalue: Binary, Unary, Call, StructInit, VariantInit, and
	// literals, Unit, Local and Field as `Use`
	private Rvalue LowerRvalue(HirExpression expr) {
		switch (expr) {
			case HirExpression.IntLiteral:
			case HirExpression.BoolLiteral:
			case HirExpression.StringLiteral:
			case HirExpression.Unit:
			case HirExpression.Local:
			case HirExpression.Field:
				return new Rvalue.Use(LowerOperand(expr));
			case HirExpression.Call(var fnDef, var args, _):
				return new Rvalue.Call(fnDef, LowerOperands(args));
			case HirExpression.Binary(var op, var left, var right, _, _): {
				var operands = LowerOperands([left, right]);
				return new Rvalue.Binary(op, operands[0], operands[1]);
			}
			case HirExpression.Unary(var op, var operand, _, _):
				return new Rvalue.Unary(op, LowerOperand(operand));
			case HirExpression.StructInit(var structDef, var fields, _):
				return new Rvalue.StructAggregate(structDef, LowerFields(fields));
			case HirExpression.VariantInit(var variant, var fields, _):
				return new Rvalue.VariantAggregate(variant, LowerFields(fields));
			case HirExpression.Error:
				throw new UnreachableException("must never try to lower error expressions");
			default:
				throw new UnreachableException("handled by LowerInto");
		}
	}
}