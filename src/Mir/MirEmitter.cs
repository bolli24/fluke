using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using fluke.Hir;

namespace fluke.Mir;

// A compiled program loaded into the current process
public class FlukeProgram(Type programType) {
	private readonly Type programType = programType;

	public void Run() {
		programType.GetMethod("Main")!.Invoke(null, null);
	}
}

// Emits a MirProgram as a CLR assembly
internal sealed class MirEmitter(MirProgram program) {
	private readonly MirProgram program = program;
	private readonly Dictionary<FnDef, (MirBody, MethodBuilder)> methods = new();
	private readonly Dictionary<StructDef, TypeBuilder> structTypes = new();
	private readonly Dictionary<EnumDef, TypeBuilder> enumTypes = new();

	// Payload struct of each variant that has fields
	private readonly Dictionary<VariantDef, TypeBuilder> payloadTypes = new();

	// CLR field of each struct or payload field that has storage
	private readonly Dictionary<FieldDef, FieldBuilder> fieldBuilders = new();

	// The enum's field at offset 4 that holds the variant's payload, for each variant that has one
	private readonly Dictionary<VariantDef, FieldBuilder> payloadFields = new();
	private readonly Dictionary<EnumDef, FieldBuilder> tagFields = new();

	// `$` can't appear in a Fluke identifier, so the tag can't clash with a variant name
	private const string TagFieldName = "$tag";
	// State of the method being emitted, set by EmitMethod
	private ILGenerator il = null!;
	private Dictionary<MirLocal, Slot?> slots = null!;
	private Dictionary<BasicBlock, Label> labels = null!;

	// Holds the value of a Switch while it is compared against each case; shared by all switches in a method
	private LocalBuilder? switchTemp;

	private abstract record Slot {
		public sealed record Local(LocalBuilder builder) : Slot;

		public sealed record Arg(short index) : Slot;
	}

	// Builds the assembly and loads it into the current process
	internal FlukeProgram EmitInMemory() {
		var asmName = new AssemblyName("Fluke");
		var builder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
		var mod = builder.DefineDynamicModule("Fluke");
		var (type, mainMethod) = EmitCore(mod);

		var peBytes = BuildPeBytes(builder, mainMethod);
		ILVerifier.Verify(peBytes);

		var assembly = Assembly.Load(peBytes);
		return new FlukeProgram(assembly.GetType("Fluke.Program")!);
	}

	// Builds the assembly and writes it to outputPath
	internal void EmitToFile(string outputPath) {
		var asmName = new AssemblyName(Path.GetFileNameWithoutExtension(outputPath));
		var builder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
		var mod = builder.DefineDynamicModule(asmName.Name!);
		var (type, mainMethod) = EmitCore(mod);

		var peBytes = BuildPeBytes(builder, mainMethod);
		ILVerifier.Verify(peBytes);

		File.WriteAllBytes(outputPath, peBytes);
	}


	private (Type, MethodBuilder) EmitCore(ModuleBuilder mod) {
		DefineTypes(mod);

		var type = mod.DefineType("Fluke.Program", TypeAttributes.Public | TypeAttributes.Class);
		var mainMethod = type.DefineMethod("Main", MethodAttributes.Public | MethodAttributes.Static, typeof(void),
			Type.EmptyTypes);
		methods[program.Main.Fn] = (program.Main, mainMethod);

		foreach (var body in program.Functions) {
			var methodBuilder = type.DefineMethod(body.Fn.MangledName,
				MethodAttributes.Public | MethodAttributes.Static,
				ReturnClrType(body.Fn.ReturnType),
				body.Fn.Params.Select(p => StorageClrType(p.Type)).OfType<Type>().ToArray()
			);

			methods[body.Fn] = (body, methodBuilder);
		}

		foreach (var (_, (body, methodBuilder)) in methods) {
			il = methodBuilder.GetILGenerator();
			EmitMethod(body);
		}

		return (type.CreateType(), mainMethod);
	}

	private void EmitMethod(MirBody body) {
		slots = new Dictionary<MirLocal, Slot?>();
		labels = new Dictionary<BasicBlock, Label>();
		switchTemp = null;

		// Parameters live in IL arguments. Those without storage are not in the signature, so they take no index.
		var paramLocals = body.Params.ToHashSet();
		short argIndex = 0;

		foreach (var localDef in body.Locals) {
			var clrType = StorageClrType(localDef.Type);
			if (clrType is null)
				slots[localDef] = null;
			else if (paramLocals.Contains(localDef))
				slots[localDef] = new Slot.Arg(argIndex++);
			else
				slots[localDef] = new Slot.Local(il.DeclareLocal(clrType));
		}

		foreach (var block in body.Blocks) {
			labels[block] = il.DefineLabel();
		}

		foreach (var block in body.Blocks) {
			EmitBlock(block, body);
		}
	}

	private void EmitBlock(BasicBlock block, MirBody body) {
		il.MarkLabel(labels[block]);

		foreach (var statement in block.Statements) {
			switch (statement) {
				case MirStatement.Assign(var place, var value):
					EmitStorePlace(place, value);
					break;
				case MirStatement.Intrinsic(var kind, var args):
					EmitIntrinsic(kind, args);
					break;
				default:
					throw new UnreachableException();
			}
		}

		switch (block.Terminator) {
			case null:
				throw new UnreachableException();
			case Terminator.Branch(var condition, var thenBlock, var elseBlock):
				EmitOperand(condition);
				il.Emit(OpCodes.Brtrue, labels[thenBlock]);
				il.Emit(OpCodes.Br, labels[elseBlock]);
				break;
			case Terminator.Goto(var target):
				il.Emit(OpCodes.Br, labels[target]);
				break;
			case Terminator.Return:
				EmitLoadSlot(body.ReturnLocal);
				il.Emit(OpCodes.Ret);
				break;
			case Terminator.Switch(var value, var cases, var otherwise):
				// The verifier guarantees the value is an int
				var temp = switchTemp ??= il.DeclareLocal(typeof(int));
				EmitOperand(value);
				il.Emit(OpCodes.Stloc, temp);
				foreach (var (i, target) in cases) {
					il.Emit(OpCodes.Ldloc, temp);
					il.Emit(OpCodes.Ldc_I4, i);
					il.Emit(OpCodes.Beq, labels[target]);
				}

				il.Emit(OpCodes.Br, labels[otherwise]);
				break;
			case Terminator.Unreachable:
				var ctor = typeof(UnreachableException).GetConstructor([typeof(string)])!;
				il.Emit(OpCodes.Ldstr, "trap: unreachable");
				il.Emit(OpCodes.Newobj, ctor);
				il.Emit(OpCodes.Throw);
				break;
			default:
				throw new UnreachableException();
		}
	}

	private void EmitIntrinsic(MirIntrinsic kind, Operand[] args) {
		switch (kind, args) {
			case (MirIntrinsic.Print, []):
				il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", Type.EmptyTypes)!);
				break;
			// Enums print their tag
			case (MirIntrinsic.Print, [{ Type: Ty.Enum } arg]):
				EmitValue(new Rvalue.Discriminant(PlaceOf(arg)));
				il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", [typeof(int)])!);
				break;
			case (MirIntrinsic.Print, [{ Type: Ty.IntTy or Ty.BoolTy or Ty.StringTy } arg]):
				EmitOperand(arg);
				il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", [StorageClrType(arg.Type)!])!);
				break;
			case (MirIntrinsic.Print, [{ Type: Ty.Struct }]):
				throw new NotImplementedException("printing structs");
			case (MirIntrinsic.Assert, [var condition]): {
				var ok = il.DefineLabel();
				EmitOperand(condition);
				il.Emit(OpCodes.Brtrue, ok);
				il.Emit(OpCodes.Ldstr, "Assertion failed");
				il.Emit(OpCodes.Newobj, typeof(Exception).GetConstructor([typeof(string)])!);
				il.Emit(OpCodes.Throw);
				il.MarkLabel(ok);
				break;
			}
			default:
				throw new UnreachableException($"invalid intrinsic `{new MirStatement.Intrinsic(kind, args)}`");
		}
	}

	// Pops the value into the local's slot. Locals without storage have no slot and no value on the stack.
	private void EmitStoreSlot(MirLocal local) {
		switch (slots[local]) {
			case null:
				break;
			case Slot.Arg(var index):
				il.Emit(OpCodes.Starg, index);
				break;
			case Slot.Local(var ilLocal):
				il.Emit(OpCodes.Stloc, ilLocal);
				break;
			default:
				throw new UnreachableException();
		}
	}

	// Pushes the local's value. Locals without storage have no slot and push nothing.
	private void EmitLoadSlot(MirLocal local) {
		switch (slots[local]) {
			case null:
				break;
			case Slot.Arg(var index):
				il.Emit(OpCodes.Ldarg, index);
				break;
			case Slot.Local(var ilLocal):
				il.Emit(OpCodes.Ldloc, ilLocal);
				break;
			default:
				throw new UnreachableException();
		}
	}

	private void EmitLoadAddr(MirLocal local) {
		switch (slots[local]) {
			case null:
				break;
			case Slot.Arg(var index):
				il.Emit(OpCodes.Ldarga, index);
				break;
			case Slot.Local(var ilLocal):
				il.Emit(OpCodes.Ldloca, ilLocal);
				break;
			default:
				throw new UnreachableException();
		}
	}

	private void EmitLoadPlace(Place place) {
		if (StorageClrType(place.Type) is null) return;

		if (place.Projections.Length == 0) {
			EmitLoadSlot(place.Local);
			return;
		}

		EmitPlaceAddr(place with { Projections = place.Projections[..^1] });
		il.Emit(OpCodes.Ldfld, ClrFields([place.Projections[^1]]).Single());
	}

	// Evaluates value and stores it into place. stfld needs the address below the value, so the address is pushed
	// first. That is safe even when value reads place: the address is only a pointer, and the write comes last.
	private void EmitStorePlace(Place place, Rvalue value) {
		// The value pushes nothing, and the field has no CLR field to store into
		if (StorageClrType(place.Type) is null) {
			EmitValue(value);
			return;
		}

		if (place.Projections.Length == 0) {
			EmitValue(value);
			EmitStoreSlot(place.Local);
			return;
		}

		EmitPlaceAddr(place with { Projections = place.Projections[..^1] });
		EmitValue(value);
		il.Emit(OpCodes.Stfld, ClrFields([place.Projections[^1]]).Single());
	}

	// Pushes the address of the value at place: the root's address, then ldflda for every projection
	private void EmitPlaceAddr(Place place) {
		EmitLoadAddr(place.Local);
		foreach (var field in ClrFields(place.Projections))
			il.Emit(OpCodes.Ldflda, field);
	}

	// The CLR field each projection steps into, in order. Downcast steps into the enum's payload field.
	private IEnumerable<FieldInfo> ClrFields(IEnumerable<Projection> projections) =>
		projections.Select(projection => projection switch {
			Projection.Field(var def) => fieldBuilders[def],
			Projection.Downcast(var variant) => payloadFields[variant],
			_ => throw new UnreachableException(),
		});


	private void EmitValue(Rvalue value) {
		switch (value) {
			case Rvalue.Binary(var op, var left, var right):
				if (op is BinOp.Eq or BinOp.Neq) {
					EmitEquality(left, right);
					if (op == BinOp.Neq) {
						il.Emit(OpCodes.Ldc_I4_0);
						il.Emit(OpCodes.Ceq);
					}

					break;
				}

				EmitOperand(left);
				EmitOperand(right);

				switch (op) {
					case BinOp.Add:
						il.Emit(OpCodes.Add);
						break;
					case BinOp.Sub:
						il.Emit(OpCodes.Sub);
						break;
					case BinOp.Mul:
						il.Emit(OpCodes.Mul);
						break;
					case BinOp.Div:
						il.Emit(OpCodes.Div);
						break;
					case BinOp.And:
					case BinOp.Or:
					default:
						throw new UnreachableException();
				}

				break;
			case Rvalue.Call(var fnDef, var args):
				foreach (var arg in args) {
					EmitOperand(arg);
				}

				il.Emit(OpCodes.Call, methods[fnDef].Item2);
				break;
			case Rvalue.Discriminant(var place): {
				var def = ((Ty.Enum)place.Type).def;
				EmitPlaceAddr(place);
				il.Emit(OpCodes.Ldfld, tagFields[def]);
				break;
			}
			case Rvalue.StructAggregate(var def, var fields): {
				// Built in a fresh temp, never in dest: the operands may read dest (see MirStatement.Assign)
				var structType = structTypes[def];
				var temp = il.DeclareLocal(structType);
				il.Emit(OpCodes.Ldloca, temp);
				il.Emit(OpCodes.Initobj, structType);

				foreach (var (operand, fieldDef) in fields.Zip(def.Fields)) {
					// Fields without storage have no CLR field, and their operands push nothing
					if (!fieldBuilders.TryGetValue(fieldDef, out var field)) continue;

					il.Emit(OpCodes.Ldloca, temp);
					EmitOperand(operand);
					il.Emit(OpCodes.Stfld, field);
				}

				il.Emit(OpCodes.Ldloc, temp);
				break;
			}
			case Rvalue.Unary(var op, var operand):
				EmitOperand(operand);
				switch (op) {
					case UnOp.Neg:
						il.Emit(OpCodes.Neg);
						break;
					case UnOp.Not:
						il.Emit(OpCodes.Ldc_I4_0);
						il.Emit(OpCodes.Ceq);
						break;
					default:
						throw new UnreachableException();
				}

				break;
			case Rvalue.Use(var operand):
				EmitOperand(operand);
				break;
			case Rvalue.VariantAggregate(var variant, var fields): {
				var def = variant.Parent;
				var enumType = enumTypes[def];
				var temp = il.DeclareLocal(enumType);
				il.Emit(OpCodes.Ldloca, temp);
				il.Emit(OpCodes.Initobj, enumType);

				il.Emit(OpCodes.Ldloca, temp);
				il.Emit(OpCodes.Ldc_I4, variant.Tag);
				il.Emit(OpCodes.Stfld, tagFields[def]);

				foreach (var (operand, fieldDef) in fields.Zip(variant.Fields)) {
					if (!fieldBuilders.TryGetValue(fieldDef, out var field)) continue;

					il.Emit(OpCodes.Ldloca, temp);
					il.Emit(OpCodes.Ldflda, payloadFields[variant]);
					EmitOperand(operand);
					il.Emit(OpCodes.Stfld, field);
				}

				il.Emit(OpCodes.Ldloc, temp);
				break;
			}
			default:
				throw new UnreachableException();
		}
	}

	// Pushes whether left == right. Both have the same type (checked by MirVerifier). Structs and enums are
	// compared field by field through projected places, so no temps are needed.
	private void EmitEquality(Operand left, Operand right) {
		switch (left.Type) {
			case Ty.IntTy or Ty.BoolTy:
				EmitOperand(left);
				EmitOperand(right);
				il.Emit(OpCodes.Ceq);
				break;
			case Ty.StringTy:
				EmitOperand(left);
				EmitOperand(right);
				il.Emit(OpCodes.Call, typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!);
				break;
			// Operands have no side effects, so there is nothing to evaluate
			case Ty.UnitTy or Ty.NeverTy:
				il.Emit(OpCodes.Ldc_I4_1);
				break;
			case Ty.Struct(var def):
				EmitFieldsEquality(def.Fields, PlaceOf(left), PlaceOf(right));
				break;
			case Ty.Enum(var def):
				EmitEnumEquality(def, PlaceOf(left), PlaceOf(right));
				break;
			case Ty.Ref:
				throw new NotImplementedException();
			default:
				throw new UnreachableException();
		}
	}

	// Pushes whether every field that has storage is equal. leftPlace and rightPlace are already projected to
	// the struct or variant payload that owns the fields.
	private void EmitFieldsEquality(List<FieldDef> fields, Place leftPlace, Place rightPlace) {
		il.Emit(OpCodes.Ldc_I4_1);
		foreach (var field in fields) {
			if (StorageClrType(field.Type) is null) continue;

			var projection = new Projection.Field(field);
			EmitEquality(new Operand.Copy(leftPlace.Project(projection)),
				new Operand.Copy(rightPlace.Project(projection)));
			il.Emit(OpCodes.And);
		}
	}

	// Compares the tags, then switches on the tag to compare the payload of that variant
	private void EmitEnumEquality(EnumDef def, Place leftPlace, Place rightPlace) {
		var notEqual = il.DefineLabel();
		var equal = il.DefineLabel();
		var end = il.DefineLabel();

		EmitValue(new Rvalue.Discriminant(leftPlace));
		EmitValue(new Rvalue.Discriminant(rightPlace));
		il.Emit(OpCodes.Bne_Un, notEqual);

		// Tags are the variant's index, so they index the switch table. Variants without a payload are
		// equal once the tags are.
		var targets = def.Variants
			.Select(variant => payloadTypes.ContainsKey(variant) ? il.DefineLabel() : equal)
			.ToArray();
		EmitValue(new Rvalue.Discriminant(leftPlace));
		il.Emit(OpCodes.Switch, targets);
		il.Emit(OpCodes.Br, equal);

		foreach (var variant in def.Variants) {
			if (!payloadTypes.ContainsKey(variant)) continue;

			il.MarkLabel(targets[variant.Tag]);
			var downcast = new Projection.Downcast(variant);
			EmitFieldsEquality(variant.Fields, leftPlace.Project(downcast), rightPlace.Project(downcast));
			il.Emit(OpCodes.Br, end);
		}

		il.MarkLabel(equal);
		il.Emit(OpCodes.Ldc_I4_1);
		il.Emit(OpCodes.Br, end);
		il.MarkLabel(notEqual);
		il.Emit(OpCodes.Ldc_I4_0);
		il.MarkLabel(end);
	}

	// Struct and enum operands are always places, since there are no struct or enum constants
	private static Place PlaceOf(Operand operand) =>
		operand switch {
			Operand.Copy(var place) => place,
			Operand.Move(var place) => place,
			_ => throw new UnreachableException($"`{operand}` is not a place"),
		};

	private void EmitOperand(Operand operand) {
		switch (operand) {
			case Operand.Const(var constant):
				switch (constant) {
					case Constant.Bool(var value):
						il.Emit(value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
						break;
					case Constant.Int(var value):
						il.Emit(OpCodes.Ldc_I4, value);
						break;
					case Constant.String(var value):
						il.Emit(OpCodes.Ldstr, value);
						break;
					case Constant.Unit:
						break;
					default:
						throw new UnreachableException();
				}

				break;
			case Operand.Copy(var place):
				EmitLoadPlace(place);
				break;
			case Operand.Move(var place):
				throw new NotImplementedException();
			// EmitLoadPlace(place);
			// break;
			default:
				throw new UnreachableException();
		}
	}

	// Creates a CLR value type for every struct, enum and variant payload, in dependency order
	private void DefineTypes(ModuleBuilder mod) {
		var finalized = new HashSet<Definition>();
		var visiting = new HashSet<Definition>();

		// Structs and enums can contain each other, so both are created in one dependency order: a type's
		// value-type fields are created before it, which the CLR needs to lay it out
		void FinalizeDeps(Ty type) {
			switch (type) {
				case Ty.Struct(var def):
					FinalizeStruct(def);
					break;
				case Ty.Enum(var def):
					FinalizeEnum(def);
					break;
			}
		}

		// Returns false if the definition is already finalized
		bool BeginFinalize(Definition def) {
			if (finalized.Contains(def)) return false;
			if (!visiting.Add(def))
				throw new UnreachableException("Must never allow cyclic dependencies");
			return true;
		}

		void EndFinalize(Definition def) {
			visiting.Remove(def);
			finalized.Add(def);
		}

		void DefineFields(TypeBuilder builder, List<FieldDef> fields) {
			foreach (var fieldDef in fields) {
				FinalizeDeps(fieldDef.Type);

				// Fields that need no storage (unit, never) get no CLR field
				if (StorageClrType(fieldDef.Type) is { } fieldType)
					fieldBuilders[fieldDef] = builder.DefineField(fieldDef.Name, fieldType, FieldAttributes.Public);
			}
		}

		void FinalizeStruct(StructDef def) {
			if (!BeginFinalize(def)) return;

			var builder = structTypes[def];
			DefineFields(builder, def.Fields);
			builder.CreateType();

			EndFinalize(def);
		}

		// The tag sits at offset 0 and every variant's payload struct overlaps at offset 4
		void FinalizeEnum(EnumDef def) {
			if (!BeginFinalize(def)) return;

			var builder = enumTypes[def];
			var tagField = builder.DefineField(TagFieldName, typeof(int), FieldAttributes.Public);
			tagField.SetOffset(0);
			tagFields[def] = tagField;

			foreach (var variant in def.Variants) {
				if (!payloadTypes.TryGetValue(variant, out var payload)) continue;

				DefineFields(payload, variant.Fields);
				payload.CreateType();
				var payloadField = builder.DefineField(variant.Name, payload, FieldAttributes.Public);
				payloadField.SetOffset(4);
				payloadFields[variant] = payloadField;
			}

			builder.CreateType();

			EndFinalize(def);
		}

		foreach (var structDef in program.Structs) {
			var typeBuilder = mod.DefineType(
				structDef.Name,
				TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout,
				typeof(ValueType)
			);

			structTypes[structDef] = typeBuilder;
		}

		foreach (var enumDef in program.Enums) {
			enumTypes[enumDef] = mod.DefineType(
				enumDef.Name,
				TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.ExplicitLayout,
				typeof(ValueType)
			);

			// Bare variants and variants without fields carry no payload
			// TODO: also drop the payload when no field needs storage (all unit/never); it's an empty struct now
			foreach (var variant in enumDef.Variants) {
				if (variant.Fields.Count == 0) continue;

				payloadTypes[variant] = mod.DefineType(
					$"{enumDef.Name}${variant.Name}",
					TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout,
					typeof(ValueType)
				);
			}
		}

		foreach (var def in program.Structs) FinalizeStruct(def);
		foreach (var def in program.Enums) FinalizeEnum(def);
	}

	// The CLR type a value of this type is stored as, or null if it needs no storage (unit, never)
	// TODO: treat structs whose fields all need no storage like unit too
	private Type? StorageClrType(Ty type) =>
		type switch {
			Ty.IntTy => typeof(int),
			Ty.BoolTy => typeof(bool),
			Ty.StringTy => typeof(string),
			Ty.UnitTy or Ty.NeverTy => null,
			Ty.Struct(var def) => structTypes[def],
			Ty.Enum(var def) => enumTypes[def],
			Ty.Ref => throw new NotImplementedException(),
			Ty.ErrorTy => throw new UnreachableException("Error type must not reach codegen"),
			_ => throw new UnreachableException(),
		};

	private Type ReturnClrType(Ty type) => StorageClrType(type) ?? typeof(void);

	private static byte[] BuildPeBytes(PersistedAssemblyBuilder builder, MethodBuilder entryPoint) {
		var metadataBuilder = builder.GenerateMetadata(out var ilStream, out var fieldData);
		var entryPointToken = MetadataTokens.MethodDefinitionHandle(entryPoint.MetadataToken);

		var peHeaderBuilder = new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage);
		var peBuilder = new ManagedPEBuilder(
			header: peHeaderBuilder,
			metadataRootBuilder: new MetadataRootBuilder(metadataBuilder),
			ilStream: ilStream,
			mappedFieldData: fieldData,
			entryPoint: entryPointToken);

		var peBlob = new BlobBuilder();
		peBuilder.Serialize(peBlob);

		return peBlob.ToArray();
	}
}