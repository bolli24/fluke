using System.Diagnostics;

namespace fluke.Hir;

internal class Scope {
	private readonly Dictionary<string, Definition> symbols = new();
	public bool IsFunctionBoundary { get; init; }

	public Definition? Get(string name) => symbols.GetValueOrDefault(name);

	public bool Declare(string name, Definition symbol) => symbols.TryAdd(name, symbol);

	public void Bind(string name, Definition symbol) => symbols[name] = symbol;

	public Ty? TypeFromName(string name) {
		var type = name switch {
			"int" => Ty.Int,
			"bool" => Ty.Bool,
			"string" => Ty.String,
			"unit" => Ty.Unit,
			_ => null
		};

		if (type is not null) return type;

		return Get(name) switch {
			StructDef structDef => new Ty.Struct(structDef),
			EnumDef enumDef => new Ty.Enum(enumDef),
			_ => null
		};
	}
}

public class Checker {
	private readonly List<Diagnostic> diagnostics = [];
	private readonly Stack<Scope> scopes = new();
	private FnDef currentFunction;
	private HirProgram program;
	private readonly Stack<string> nameStack = new();
	private readonly HashSet<string> mangledNames = ["Main"];

	// Reset to 0 before checking each function (params first), so local ids are per function
	private int nextLocalId;

	public Checker() {
		currentFunction = new FnDef("Main", "Main", null, default);
		program = new HirProgram(currentFunction);
		scopes.Push(new Scope());
	}

	private HirExpression.Error Error(string msg, Span span) {
		diagnostics.Add(new Diagnostic(msg, span));
		return new HirExpression.Error(span);
	}

	// Unique across the program, since functions with the same name can be declared in different
	// blocks. Only repeated names get a counter suffix, e.g. `outer$h` and `outer$h$1`.
	private string MangledName(string name) {
		var baseName = nameStack.Count == 0 ? name : nameStack.Peek() + "$" + name;
		var mangled = baseName;
		for (var i = 1; !mangledNames.Add(mangled); i++)
			mangled = $"{baseName}${i}";
		return mangled;
	}

	private static string Plural(int count, string word) => count == 1 ? $"{count} {word}" : $"{count} {word}s";

	private void CollectDeclarations(IEnumerable<Statement> statements) {
		var typeDefs = new List<(Statement, Definition)>();
		var fnDefs = new List<(Statement, FnDef)>();

		foreach (var statement in statements) {
			switch (statement) {
				case Statement.StructDeclaration(var ident, _): {
					var def = new StructDef(ident.Name, statement.Span);
					if (!scopes.Peek().Declare(ident.Name, def)) {
						Error($"duplicate declaration of struct type '{ident.Name}'", ident.Span);
					}

					typeDefs.Add((statement, def));
					break;
				}
				case Statement.EnumDeclaration(var ident, _): {
					var def = new EnumDef(ident.Name, statement.Span);
					if (!scopes.Peek().Declare(ident.Name, def)) {
						Error($"duplicate declaration of enum type '{ident.Name}'", ident.Span);
					}

					typeDefs.Add((statement, def));
					break;
				}
				case Statement.FunctionDeclaration(var ident, _, _, _): {
					var def = new FnDef(ident.Name, MangledName(ident.Name), currentFunction, statement.Span);
					if (!scopes.Peek().Declare(ident.Name, def)) {
						Error($"duplicate declaration of function '{ident.Name}'", ident.Span);
					}

					fnDefs.Add((statement, def));
					break;
				}
			}
		}

		// Fills the def created for each declaration
		foreach (var (statement, def) in typeDefs) {
			switch (statement, def) {
				case (Statement.StructDeclaration(_, var fields), StructDef structDef):
					structDef.Fields.AddRange(CollectFields(fields, $"struct '{structDef.Name}'"));
					program.Structs.Add(structDef);
					break;
				case (Statement.EnumDeclaration(_, var variants), EnumDef enumDef):
					foreach (var enumVariant in variants) {
						var (variantName, variantSpan) = enumVariant.name;
						if (enumDef.Variants.Exists(v => v.Name == variantName)) {
							Error($"variant '{variantName}' is declared more than once in enum '{enumDef.Name}'",
								variantSpan);
							continue;
						}

						var variant = new VariantDef(enumDef, variantName, enumDef.Variants.Count,
							enumVariant is EnumVariant.Bare, variantSpan);
						if (enumVariant is EnumVariant.Struct(_, var fields))
							variant.Fields.AddRange(CollectFields(fields, $"variant '{variant}'"));
						enumDef.Variants.Add(variant);
					}

					program.Enums.Add(enumDef);
					break;
				default:
					throw new UnreachableException($"Unexpected declaration kind '{statement.GetType().Name}'.");
			}
		}

		// Both checks look through struct fields, so they run once all types in this scope are filled.
		// Cycles can only form within one scope, since outer types cannot name inner ones.
		var finishedStructs = new HashSet<StructDef>();
		foreach (var (_, def) in typeDefs) {
			switch (def) {
				case StructDef structDef:
					CheckStructCycle(structDef, finishedStructs, []);
					break;
				case EnumDef enumDef:
					foreach (var variant in enumDef.Variants)
					foreach (var field in variant.Fields)
						CheckVariantFieldType(field.Type, variant, field.Name, field.TypeSpan, []);
					break;
			}
		}

		// Signatures after types, so they can name any type in scope. Parameter ids restart at 0 for
		// each function, so the enclosing function's counter is restored afterwards.
		var enclosingLocalId = nextLocalId;
		foreach (var (statement, fn) in fnDefs) {
			var (_, parameters, _, returnType) = (Statement.FunctionDeclaration)statement;

			nextLocalId = 0;
			foreach (var param in parameters) {
				var (paramName, paramSpan) = param.Name;
				// Duplicates are still added, so the arity stays as written and calls report no follow-up errors.
				// The later one shadows the earlier one in the body.
				if (fn.Params.Exists(p => p.Name == paramName))
					Error($"parameter '{paramName}' is declared more than once in '{fn.Name}'", paramSpan);
				CheckLocalName(paramName, LocalKind.Param, paramSpan);

				// Typed as the inner type, so uses of the parameter don't report follow-up errors
				var type = ResolveTypeOrError(param.TypeName);
				if (param.IsRef)
					Error("'ref' parameters are not supported yet", param.Span);
				fn.Params.Add(NewLocal(paramName, type, LocalKind.Param, param.Span));
			}

			fn.ReturnType = returnType is not null ? ResolveTypeOrError(returnType) : Ty.Unit;
			program.Functions.Add(fn);
		}

		nextLocalId = enclosingLocalId;

		// Bodies after all signatures, so calls can refer to functions declared later. Checking them
		// here instead of in source order is fine, since functions cannot see the enclosing locals anyway.
		foreach (var (statement, fn) in fnDefs) {
			var body = ((Statement.FunctionDeclaration)statement).body;

			var outerFunction = currentFunction;
			var outerLocalId = nextLocalId;
			currentFunction = fn;
			nextLocalId = fn.Params.Count;
			nameStack.Push(fn.MangledName);
			scopes.Push(new Scope { IsFunctionBoundary = true });
			// Parameters named like a type were reported with the signature
			foreach (var param in fn.Params.Where(p => !IsTypeName(p.Name)))
				scopes.Peek().Bind(param.Name, param);

			var bodyHir = CheckBlock(body);
			if (!bodyHir.Type.CoercesTo(fn.ReturnType))
				Error($"mismatched return type: expected '{fn.ReturnType}', found '{bodyHir.Type}'",
					bodyHir.tail is HirExpression.Unit ? bodyHir.Span : bodyHir.tail.Span);
			fn.Body = bodyHir;

			scopes.Pop();
			nameStack.Pop();
			currentFunction = outerFunction;
			nextLocalId = outerLocalId;
		}
	}

	// Structs hold their fields by value, so a struct that contains itself would have infinite size.
	// Reports each cycle once, at the field that closes it. path is the chain of structs being visited.
	private void CheckStructCycle(StructDef def, HashSet<StructDef> finished, List<StructDef> path) {
		if (finished.Contains(def))
			return;

		path.Add(def);
		foreach (var field in def.Fields) {
			if (field.Type is not Ty.Struct(var inner))
				continue;

			var cycleStart = path.IndexOf(inner);
			if (cycleStart >= 0) {
				var cycle = path.Skip(cycleStart).Append(inner).Select(s => s.Name);
				Error($"cyclic struct dependency: {string.Join(" -> ", cycle)}", field.TypeSpan);
				continue;
			}

			CheckStructCycle(inner, finished, path);
		}

		path.RemoveAt(path.Count - 1);
		finished.Add(def);
	}

	// Union enums overlap their variants' payloads, which the CLR only allows for unmanaged data, so
	// payloads may only contain value-type primitives, also through nested structs. fieldPath is the
	// dotted path from the variant field, e.g. "w.s", for diagnostics. Stops at the first offending
	// field, so each variant field reports at most one error.
	private bool CheckVariantFieldType(Ty type, VariantDef variant, string fieldPath, Span typeSpan,
		HashSet<StructDef> visiting) {
		switch (type) {
			case Ty.Struct(var structDef):
				// Recursive structs are not checked here
				if (!visiting.Add(structDef))
					return true;
				var ok = structDef.Fields.All(field =>
					CheckVariantFieldType(field.Type, variant, $"{fieldPath}.{field.Name}", typeSpan, visiting));
				visiting.Remove(structDef);
				return ok;
			case Ty.Enum:
				Error(
					$"field '{fieldPath}' of variant '{variant}' is an enum, but nested enum payloads are not supported",
					typeSpan);
				return false;
			default:
				// Error: already reported where the type was resolved
				if (type == Ty.Int || type == Ty.Bool || type == Ty.Error)
					return true;
				Error(
					$"field '{fieldPath}' of variant '{variant}' has type '{type}', but reference types are not allowed in variants",
					typeSpan);
				return false;
		}
	}

	// Typed Error when unknown, so uses of the value don't report follow-up errors
	private Ty ResolveTypeOrError(Ident ident) {
		var type = ResolveType(ident.Name);
		if (type is not null) return type;

		Error($"cannot find type '{ident.Name}' in this scope", ident.Span);
		return Ty.Error;
	}

	// Owner is only used in diagnostics, e.g. "struct 'Point'"
	private List<FieldDef> CollectFields((Ident, Ident)[] fields, string owner) {
		var result = new List<FieldDef>();
		foreach (var ((fieldName, fieldSpan), (typeName, typeSpan)) in fields) {
			if (result.Exists(f => f.Name == fieldName)) {
				Error($"field '{fieldName}' is declared more than once in {owner}", fieldSpan);
				continue;
			}

			var type = ResolveTypeOrError(new Ident(typeName, typeSpan));
			result.Add(new FieldDef(fieldName, type, result.Count, fieldSpan, typeSpan));
		}

		return result;
	}

	// Types are visible across function boundaries, unlike locals
	private Ty? ResolveType(string name) =>
		scopes.Select(s => s.TypeFromName(name)).FirstOrDefault(t => t is not null);

	internal HirProgram Check(Ast ast) {
		CollectDeclarations(ast.statements);

		// The top-level statements become the body of Main, in the global scope
		var diverges = false;
		var statements = new List<HirStatement>();
		foreach (var statement in ast.statements) {
			if (CheckStatement(statement) is not { } hirStmt)
				continue;
			statements.Add(hirStmt);
			diverges |= hirStmt.Diverges;
		}

		var span = ast.statements.Count > 0
			? ast.statements.Select(s => s.Span).Aggregate((a, b) => a | b)
			: default;
		var tail = new HirExpression.Unit(new Span(span.End, span.End));
		program.Main.Body = new HirExpression.Block(statements.ToArray(), tail, diverges ? Ty.Never : Ty.Unit, span);

		ThrowIfErrors();
		return program;
	}

	// Null for declarations, which produce no statement
	private HirStatement? CheckStatement(Statement statement) {
		switch (statement) {
			case Statement.ExpressionStatement(var expr):
				return new HirStatement.Expr(CheckExpression(expr), expr.Span);
			case Statement.VariableBinding(var ident, var expr): {
				var value = CheckExpression(expr);
				var local = NewLocal(ident.Name, value.Type, LocalKind.Let, ident.Span);
				BindLocal(local);
				return new HirStatement.Let(local, value, statement.Span);
			}
			case Statement.Assignment(var lhs, var rhs): {
				var value = CheckExpression(rhs);
				switch (lhs) {
					case Expression.Identifier(var (name, span)):
						switch (Resolve(name)) {
							case null:
								return new HirStatement.Assign(Error($"cannot find value '{name}' in this scope", span),
									value, statement.Span);
							case LocalDef local:
								// A Never local's binding diverged, so this assignment is unreachable
								if (local.Type != Ty.Never && !value.Type.CoercesTo(local.Type))
									Error($"cannot assign '{value.Type}' to variable '{name}' of type '{local.Type}'",
										span);
								return new HirStatement.Assign(new HirExpression.Local(local, span), value,
									statement.Span);
							default:
								return new HirStatement.Assign(Error($"cannot assign to '{name}'", span), value,
									statement.Span);
						}
					case Expression.FieldAccess(var container, var (field, span)): {
						var @base = CheckExpression(container);
						if (!IsPlace(@base))
							return new HirStatement.Assign(Error("invalid assignment target", lhs.Span), value,
								statement.Span);

						var fieldDef = ResolveField(@base, field, span);
						if (fieldDef is null)
							return new HirStatement.Assign(new HirExpression.Error(lhs.Span), value,
								statement.Span);

						if (!value.Type.CoercesTo(fieldDef.Type))
							Error($"cannot assign '{value.Type}' to field '{field}' of type '{fieldDef.Type}'", span);

						return new HirStatement.Assign(new HirExpression.Field(@base, fieldDef, lhs.Span), value,
							statement.Span);
					}
					default:
						return new HirStatement.Assign(Error("invalid assignment target", lhs.Span), value,
							statement.Span);
				}
			}
			// Handled by CollectDeclarations when the enclosing block is entered
			case Statement.FunctionDeclaration or Statement.StructDeclaration or Statement.EnumDeclaration:
				return null;
			case Statement.Return(var expr): {
				var value = expr != null
					? CheckExpression(expr)
					: new HirExpression.Unit(new Span(statement.Span.End, statement.Span.End));

				if (!value.Type.CoercesTo(currentFunction.ReturnType))
					Error($"mismatched return type: expected '{currentFunction.ReturnType}', found '{value.Type}'",
						value.Span);

				return new HirStatement.Return(value, statement.Span);
			}
			default:
				throw new ArgumentOutOfRangeException(nameof(statement));
		}
	}

	private HirExpression CheckExpression(Expression expr) {
		switch (expr) {
			case Expression.StringLiteral(var value):
				return new HirExpression.StringLiteral(value, expr.Span);
			case Expression.IntLiteral(var value):
				return new HirExpression.IntLiteral(value, expr.Span);
			case Expression.BoolLiteral(var value):
				return new HirExpression.BoolLiteral(value, expr.Span);
			case Expression.Identifier(var ident): {
				var name = ident.Name;
				var symbol = Resolve(name);
				if (symbol is null)
					return Error($"cannot find value '{name}' in this scope", expr.Span);

				return symbol switch {
					LocalDef localDef => new HirExpression.Local(localDef, expr.Span),
					FnDef => Error($"'{name}' is a function, not a value; call it with '{name}(...)'", expr.Span),
					StructDef => Error($"'{name}' is a struct type, not a value", expr.Span),
					EnumDef => Error($"'{name}' is an enum type, not a value", expr.Span),
					_ => throw new UnreachableException($"Unexpected definition kind '{symbol.GetType().Name}'.")
				};
			}
			case Expression.BinaryOp(var left, var op, var right): {
				var leftHir = CheckExpression(left);
				var rightHir = CheckExpression(right);

				var expectedType = op switch {
					BinOp.And or BinOp.Or => Ty.Bool,
					_ => Ty.Int,
				};
				var isEquality = op is BinOp.Eq or BinOp.Neq;
				var resultType = isEquality ? Ty.Bool : expectedType;

				if (leftHir.Type == Ty.Error || rightHir.Type == Ty.Error)
					return new HirExpression.Binary(op, leftHir, rightHir, Ty.Error, expr.Span);

				// CoercesTo rather than equality, so Never operands (e.g. a block that returns) are accepted
				if (isEquality && !leftHir.Type.CoercesTo(rightHir.Type) && !rightHir.Type.CoercesTo(leftHir.Type)) {
					Error(
						$"cannot compare '{leftHir.Type}' with '{rightHir.Type}' using '{op.Symbol()}'",
						expr.Span
					);
					return new HirExpression.Binary(op, leftHir, rightHir, Ty.Error, expr.Span);
				}

				// Only primitives can be compared for now. The non-Never side decides, so `return` operands are fine.
				var comparedType = leftHir.Type == Ty.Never ? rightHir.Type : leftHir.Type;
				if (isEquality && comparedType != Ty.Int && comparedType != Ty.Bool && comparedType != Ty.String &&
				    comparedType != Ty.Never) {
					Error(
						$"cannot compare values of type '{comparedType}' using '{op.Symbol()}'; only 'int', 'bool' and 'string' can be compared",
						expr.Span
					);
					return new HirExpression.Binary(op, leftHir, rightHir, Ty.Error, expr.Span);
				}

				if (!isEquality && (!leftHir.Type.CoercesTo(expectedType) || !rightHir.Type.CoercesTo(expectedType))) {
					Error(
						$"operator '{op.Symbol()}' expects '{expectedType}' operands, found '{leftHir.Type}' and '{rightHir.Type}'",
						expr.Span
					);
					return new HirExpression.Binary(op, leftHir, rightHir, Ty.Error, expr.Span);
				}

				return new HirExpression.Binary(op, leftHir, rightHir, resultType, expr.Span);
			}
			case Expression.UnaryOp(var op, var operand): {
				var hirExpr = CheckExpression(operand);
				switch (op) {
					case UnOp.Neg:
						if (!hirExpr.Type.CoercesTo(Ty.Int)) {
							Error($"operator '{op.Symbol()}' expects an 'int' operand, found '{hirExpr.Type}'",
								expr.Span);
						}

						return new HirExpression.Unary(op, hirExpr, Ty.Int, expr.Span);
					case UnOp.Not:
						if (!hirExpr.Type.CoercesTo(Ty.Bool)) {
							Error($"operator '{op.Symbol()}' expects a 'bool' operand, found '{hirExpr.Type}'",
								expr.Span);
						}

						return new HirExpression.Unary(op, hirExpr, Ty.Bool, expr.Span);
					default:
						throw new UnreachableException($"Operator '{op}' is not a valid unary operator.");
				}
			}
			case Expression.Block block:
				return CheckBlock(block);
			case Expression.FunctionCall(var ident, var args): {
				var symbol = Resolve(ident.Name);
				// Checked up front so errors inside the arguments are reported even if the call is invalid
				var argExpr = args.Select(CheckExpression).ToArray();

				if (symbol is null)
					return Error($"cannot find function '{ident.Name}' in this scope", ident.Span);

				if (symbol is not FnDef fn)
					return Error($"'{ident.Name}' is not a function", ident.Span);

				if (args.Length != fn.Params.Count)
					return Error($"'{fn.Name}' takes {Plural(fn.Params.Count, "argument")}, found {args.Length}",
						expr.Span);

				for (var i = 0; i < args.Length; i++) {
					var argType = argExpr[i].Type;
					var paramType = fn.Params[i].Type;
					if (!argType.CoercesTo(fn.Params[i].Type))
						Error(
							$"mismatched types for argument {i + 1} of '{fn.Name}': expected '{paramType}', found '{argType}'",
							args[i].Span
						);
				}

				return new HirExpression.Call(fn, argExpr, expr.Span);
			}
			case Expression.StructInit(var ident, var fields): {
				var name = ident.Name;
				var symbol = Resolve(name);
				if (symbol is null)
					return Error($"cannot find struct '{name}' in this scope", expr.Span);

				if (symbol is not StructDef structDef)
					return Error($"'{name}' is not a struct type", expr.Span);

				var hirFields = new List<(FieldDef field, HirExpression value)>();
				foreach (var ((fieldName, fieldSpan), fieldExpr) in fields) {
					// Checked even if the field is invalid, so errors inside the value are still reported
					var value = CheckExpression(fieldExpr);

					var field = structDef.Fields.Find(f => f.Name == fieldName);
					if (field is null) {
						Error($"no field '{fieldName}' on type '{name}'", fieldSpan);
						continue;
					}

					if (hirFields.Exists(f => f.field == field)) {
						Error($"field '{fieldName}' is initialized more than once", fieldSpan);
						continue;
					}

					if (!value.Type.CoercesTo(field.Type))
						Error(
							$"mismatched types for field '{fieldName}': expected '{field.Type}', found '{value.Type}'",
							fieldExpr.Span
						);

					hirFields.Add((field, value));
				}

				var missingFields = structDef.Fields.Where(f => !hirFields.Exists(h => h.field == f)).ToList();
				if (missingFields.Count > 0)
					Error(
						$"missing fields in initializer of '{name}': {string.Join(", ", missingFields)}",
						expr.Span
					);

				// Still typed as the struct when fields are wrong, so uses of the value don't report follow-up errors
				return new HirExpression.StructInit(structDef, hirFields.ToArray(), expr.Span);
			}
			case Expression.EnumVariantInit(var (enumName, enumSpan), var (variantName, variantSpan), var fields): {
				var symbol = Resolve(enumName);
				if (symbol is null)
					return Error($"cannot find enum '{enumName}' in this scope", enumSpan);

				if (symbol is not EnumDef enumDef)
					return Error($"'{enumName}' is not an enum type", enumSpan);

				// Checked up front so errors inside the values are reported even if the variant is invalid
				var values = fields.Select(f => CheckExpression(f.Item2)).ToArray();

				var variant = enumDef.Variants.Find(v => v.Name == variantName);
				if (variant is null)
					return Error($"no variant '{variantName}' on enum '{enumName}'", variantSpan);

				if (variant.IsBare) {
					Error(
						$"variant '{variant}' has no fields and cannot be initialized with '{{ ... }}'",
						expr.Span
					);
					return new HirExpression.VariantInit(variant, [], expr.Span);
				}

				var hirFields = new List<(FieldDef field, HirExpression value)>();
				for (var i = 0; i < fields.Length; i++) {
					var ((fieldName, fieldSpan), fieldExpr) = fields[i];
					var value = values[i];

					var field = variant.Fields.Find(f => f.Name == fieldName);
					if (field is null) {
						Error($"no field '{fieldName}' on variant '{variant}'", fieldSpan);
						continue;
					}

					if (hirFields.Exists(f => f.field == field)) {
						Error($"field '{fieldName}' is initialized more than once", fieldSpan);
						continue;
					}

					if (!value.Type.CoercesTo(field.Type))
						Error(
							$"mismatched types for field '{fieldName}': expected '{field.Type}', found '{value.Type}'",
							fieldExpr.Span
						);

					hirFields.Add((field, value));
				}

				var missingFields = variant.Fields.Where(f => !hirFields.Exists(h => h.field == f)).ToList();
				if (missingFields.Count > 0)
					Error(
						$"missing fields in initializer of '{variant}': {string.Join(", ", missingFields)}",
						expr.Span
					);

				// Still typed as the enum when fields are wrong, so uses of the value don't report follow-up errors
				return new HirExpression.VariantInit(variant, hirFields.ToArray(), expr.Span);
			}
			case Expression.FieldAccess(var @base, var ident): {
				if (
					@base is Expression.Identifier(var (typeName, _))
					&& Resolve(typeName) is EnumDef enumDef
				) {
					var idx = enumDef.Variants.Select(v => v.Name).ToList().IndexOf(ident.Name);
					if (idx == -1)
						return Error($"no variant '{ident.Name}' on enum '{typeName}'", expr.Span);
					if (!enumDef.Variants[idx].IsBare)
						return Error(
							$"variant '{typeName}.{ident.Name}' has fields and must be initialized with '{typeName}.{ident.Name} {{ ... }}'",
							expr.Span);
					return new HirExpression.VariantInit(enumDef.Variants[idx], [], expr.Span);
				}

				var baseExpr = CheckExpression(@base);
				var fieldDef = ResolveField(baseExpr, ident.Name, expr.Span);
				if (fieldDef is not null) {
					return new HirExpression.Field(baseExpr, fieldDef, expr.Span);
				}

				return new HirExpression.Error(expr.Span);
			}
			case Expression.If(var condition, var thenBlock, var elseBlock): {
				var conditionHir = CheckExpression(condition);
				if (!conditionHir.Type.CoercesTo(Ty.Bool))
					Error($"'if' condition must be 'bool', found '{conditionHir.Type}'", condition.Span);

				// Branches are checked even if the condition is wrong, so errors inside them are still reported
				var thenHir = CheckBlock(thenBlock);
				var elseHir = elseBlock is not null ? CheckBlock(elseBlock) : null;

				var resultType = BranchType("if", thenHir, elseHir, expr.Span);
				return new HirExpression.If(conditionHir, thenHir, elseHir ?? EmptyBlock(thenHir.Span), resultType,
					expr.Span);
			}
			case Expression.IfLet(var (enumName, enumSpan), var (variantName, variantSpan), var bindings,
				var scrutinee, var thenBlock, var elseBlock): {
				var scrutineeHir = CheckExpression(scrutinee);

				VariantDef? variant = null;
				var symbol = Resolve(enumName);
				if (symbol is null)
					Error($"cannot find enum '{enumName}' in this scope", enumSpan);
				else if (symbol is not EnumDef enumDef)
					Error($"'{enumName}' is not an enum type", enumSpan);
				else if ((variant = enumDef.Variants.Find(v => v.Name == variantName)) is null)
					Error($"no variant '{variantName}' on enum '{enumName}'", variantSpan);
				else if (!scrutineeHir.Type.CoercesTo(new Ty.Enum(enumDef)))
					Error(
						$"mismatched types for 'if let' scrutinee: expected '{enumName}', found '{scrutineeHir.Type}'",
						scrutinee.Span
					);

				if (variant is { IsBare: true } && bindings.Length > 0)
					Error(
						$"variant '{variant}' has no fields to bind",
						bindings.Select(b => b.Span).Aggregate((a, b) => a | b)
					);

				// Bindings live in their own scope around the then block, so they are not visible in else
				scopes.Push(new Scope());
				var hirBindings = new List<(FieldDef field, LocalDef local)>();
				foreach (var (bindName, bindSpan) in bindings) {
					var field = variant?.Fields.Find(f => f.Name == bindName);
					if (variant is { IsBare: false } && field is null)
						Error($"no field '{bindName}' on variant '{variant}'", bindSpan);

					var duplicate = field is not null && hirBindings.Exists(b => b.field == field);
					if (duplicate)
						Error($"field '{bindName}' is bound more than once", bindSpan);

					// Defined even when invalid, typed Error, so uses in the body don't report follow-up errors
					var local = NewLocal(bindName, field?.Type ?? Ty.Error, LocalKind.PatternBinding, bindSpan);
					BindLocal(local);
					if (field is not null && !duplicate)
						hirBindings.Add((field, local));
				}

				// Branches are checked even if the pattern is wrong, so errors inside them are still reported
				var thenHir = CheckBlock(thenBlock);
				scopes.Pop();
				var elseHir = elseBlock is not null ? CheckBlock(elseBlock) : null;

				var resultType = BranchType("if let", thenHir, elseHir, expr.Span);
				if (variant is null)
					return new HirExpression.Error(expr.Span);

				return new HirExpression.IfLet(scrutineeHir, variant, hirBindings.ToArray(), thenHir,
					elseHir ?? EmptyBlock(thenHir.Span), resultType, expr.Span);
			}
			case Expression.IntrinsicCall(var (name, nameSpan), var args): {
				// Checked up front so errors inside the arguments are reported even if the call is invalid
				var argExprs = args.Select(CheckExpression).ToArray();

				switch (name) {
					case "print": {
						if (argExprs.Length > 1)
							return Error($"'print' takes 0 or 1 arguments, found {args.Length}", expr.Span);
						if (argExprs is [{ Type: var argType }] && argType == Ty.Unit)
							Error($"cannot print a value of type '{argType}'", args[0].Span);
						return new HirExpression.Intrinsic(IntrinsicKind.Print, argExprs, Ty.Unit, expr.Span);
					}
					case "assert": {
						if (argExprs.Length != 1)
							return Error($"'assert' takes 1 argument, found {args.Length}", expr.Span);
						if (!argExprs[0].Type.CoercesTo(Ty.Bool))
							Error($"'assert' argument must be 'bool', found '{argExprs[0].Type}'", args[0].Span);
						return new HirExpression.Intrinsic(IntrinsicKind.Assert, argExprs, Ty.Unit, expr.Span);
					}
					case "enum_to_int": {
						if (argExprs.Length != 1)
							return Error($"'enum_to_int' takes 1 argument, found {args.Length}", expr.Span);
						var argType = argExprs[0].Type;
						if (argType != Ty.Error && argType is not Ty.Enum)
							Error($"'enum_to_int' argument must be an enum, found '{argType}'", args[0].Span);
						return new HirExpression.Intrinsic(IntrinsicKind.EnumToInt, argExprs, Ty.Int, expr.Span);
					}
					case "exit": {
						if (argExprs.Length != 1)
							return Error($"'exit' takes 1 argument, found {args.Length}", expr.Span);
						if (!argExprs[0].Type.CoercesTo(Ty.Int))
							Error($"'exit' argument must be 'int', found '{argExprs[0].Type}'", args[0].Span);
						return new HirExpression.Intrinsic(IntrinsicKind.Exit, argExprs, Ty.Unit, expr.Span);
					}
					default:
						return Error($"unknown intrinsic '{name}'", nameSpan);
				}
			}
			default:
				throw new ArgumentOutOfRangeException(nameof(expr));
		}

		throw new UnreachableException();
	}

	private HirExpression.Block CheckBlock(Expression.Block block) {
		scopes.Push(new Scope());
		CollectDeclarations(block.body);
		var diverges = false;
		var blockStatements = new List<HirStatement>();
		foreach (var stmt in block.body) {
			if (CheckStatement(stmt) is not { } hirStmt)
				continue;
			blockStatements.Add(hirStmt);
			diverges |= hirStmt.Diverges;
		}

		// A block without a tail evaluates to unit
		var tail = block.tail != null
			? CheckExpression(block.tail)
			: new HirExpression.Unit(new Span(block.Span.End, block.Span.End));
		var type = diverges ? Ty.Never : tail.Type;

		scopes.Pop();
		return new HirExpression.Block(blockStatements.ToArray(), tail, type, block.Span);
	}

	// Stands in for a missing `else`, so if and if let always have both branches
	private static HirExpression.Block EmptyBlock(Span after) {
		var span = new Span(after.End, after.End);
		return new HirExpression.Block([], new HirExpression.Unit(span), Ty.Unit, span);
	}

	// Result type of an `if` or `if let`; keyword is only used in diagnostics. Takes the else block
	// as written (null if absent), so a missing else gets its own diagnostic.
	private Ty BranchType(string keyword, HirExpression.Block thenHir, HirExpression.Block? elseHir, Span span) {
		var thenType = thenHir.Type;
		if (elseHir is not null) {
			var elseType = elseHir.Type;
			if (!elseType.CoercesTo(thenType) && !thenType.CoercesTo(elseType)) {
				Error($"'{keyword}' and 'else' have incompatible types: '{thenType}' and '{elseType}'", span);
				return Ty.Error;
			}

			return thenType == Ty.Never ? elseType : thenType;
		}

		if (!thenType.CoercesTo(Ty.Unit)) {
			Error($"'{keyword}' without 'else' must have type 'unit', found '{thenType}'", thenHir.Span);
			return Ty.Error;
		}

		return Ty.Unit;
	}

	private Definition? Resolve(string name) {
		var crossedBoundary = false;
		foreach (var scope in scopes) {
			var symbol = scope.Get(name);
			// Locals of enclosing functions are not visible, but definitions further out still are
			if (symbol != null && !(crossedBoundary && symbol is LocalDef))
				return symbol;

			if (scope.IsFunctionBoundary)
				crossedBoundary = true;
		}

		return null;
	}

	// Whether an expression denotes storage that can be assigned to: a local, or a field chain ending
	// in one. Error counts as a place, since its error has already been reported.
	private static bool IsPlace(HirExpression expr) =>
		expr switch {
			HirExpression.Local or HirExpression.Error => true,
			HirExpression.Field(var @base, _, _) => IsPlace(@base),
			_ => false,
		};

	private FieldDef? ResolveField(HirExpression container, string field, Span span) {
		// Error has already been reported, either for the expression or for its type
		if (container.Type == Ty.Error) return null;
		if (container.Type is not Ty.Struct({ } structDef)) {
			Error($"cannot access field '{field}' on non-struct type '{container.Type}'", span);
			return null;
		}

		if (structDef.Fields.Find(f => f.Name == field) is { } fieldDef) return fieldDef;

		Error($"no field '{field}' on type '{structDef.Name}'", span);
		return null;
	}

	private LocalDef NewLocal(string name, Ty type, LocalKind kind, Span span) =>
		new(name, nextLocalId++, type, kind, span);

	// Types and values share one namespace, so a local may not reuse the name of a struct or enum in
	// scope. Built-in type names are resolved before any scope, so locals can't hide them.
	private bool IsTypeName(string name) => scopes.Any(s => s.Get(name) is StructDef or EnumDef);

	// Locals that would hide a type are reported and left unbound, so the type stays usable and
	// later uses of it report no follow-up errors
	private bool CheckLocalName(string name, LocalKind kind, Span span) {
		if (!IsTypeName(name))
			return true;

		var what = kind switch {
			LocalKind.Param => "parameter",
			LocalKind.Let => "variable",
			LocalKind.PatternBinding => "binding",
			_ => throw new UnreachableException($"Unexpected local kind '{kind}'."),
		};
		Error($"{what} '{name}' has the same name as a type in scope", span);
		return false;
	}

	private void BindLocal(LocalDef local) {
		if (CheckLocalName(local.Name, local.Kind, local.Span))
			scopes.Peek().Bind(local.Name, local);
	}

	private void ThrowIfErrors() {
		if (diagnostics.Count == 0)
			return;
		// Passes report out of source order (signatures before bodies), so sort by position; OrderBy is stable
		throw new CompileException(
			diagnostics.OrderBy(d => d.Span.Start.Line).ThenBy(d => d.Span.Start.Col).ToList());
	}
}