using fluke.Hir;

namespace fluke.Mir;

// Control-flow graph form of a function. Locals and blocks are classes compared by identity;
// their index only exists for printing (`_3`, `bb2`).

internal sealed class MirLocal(int index, Ty type, LocalDef? source) {
	public int Index { get; } = index;
	public Ty Type { get; } = type;

	// The HIR binding this local was created for; null for temporaries and the return local
	public LocalDef? Source { get; } = source;

	public override string ToString() => $"_{Index}";
}

internal sealed class BasicBlock(int index) {
	public int Index { get; } = index;
	public List<MirStatement> Statements { get; } = [];

	// Null only while the block is being built; every finished block has one
	public Terminator? Terminator { get; set; }

	public override string ToString() => $"bb{Index}";
}

internal sealed class MirBody {
	public FnDef Fn { get; }
	public List<MirLocal> Locals { get; } = [];
	public List<BasicBlock> Blocks { get; } = [];

	// _0 holds the return value. `return` assigns it and jumps to the single block ending in Return.
	public MirLocal ReturnLocal { get; }

	// _1.._n, in parameter order
	public IEnumerable<MirLocal> Params => Locals.Skip(1).Take(Fn.Params.Count);

	public BasicBlock Entry => Blocks[0];

	public MirBody(FnDef fn) {
		Fn = fn;
		ReturnLocal = NewLocal(fn.ReturnType, null);
		foreach (var param in fn.Params)
			NewLocal(param.Type, param);
	}

	public MirLocal NewLocal(Ty type, LocalDef? source) {
		var local = new MirLocal(Locals.Count, type, source);
		Locals.Add(local);
		return local;
	}

	public BasicBlock NewBlock() {
		var block = new BasicBlock(Blocks.Count);
		Blocks.Add(block);
		return block;
	}

	// e.g.
	//   fn add(_1: int, _2: int): int {
	//   	let _0: int;
	//   	let _1: int; // a#0
	//   	...
	//   	bb0: {
	//   		_0 = copy _1 + copy _2;
	//   		goto -> bb1;
	//   	}
	//   }
	public override string ToString() {
		var text = new System.Text.StringBuilder();
		var parameters = string.Join(", ", Params.Select(p => $"{p}: {p.Type}"));
		text.Append($"fn {Fn.MangledName}({parameters}): {Fn.ReturnType} {{\n");

		foreach (var local in Locals) {
			var source = local.Source is not null ? $" // {local.Source.UniqueName}" : "";
			text.Append($"\tlet {local}: {local.Type};{source}\n");
		}

		foreach (var block in Blocks) {
			text.Append($"\n\t{block}: {{\n");
			foreach (var statement in block.Statements)
				text.Append($"\t\t{statement};\n");
			// Missing while the body is being built, which is when dumps are most useful
			text.Append(block.Terminator is not null ? $"\t\t{block.Terminator};\n" : "\t\t<no terminator>\n");
			text.Append("\t}\n");
		}

		text.Append('}');
		return text.ToString();
	}
}

internal sealed class MirProgram(MirBody main) {
	// Type definitions are shared with the HIR
	public List<StructDef> Structs { get; } = [];
	public List<EnumDef> Enums { get; } = [];

	// Every function except Main, nested ones included
	public List<MirBody> Functions { get; } = [];

	public IEnumerable<MirBody> Bodies => Functions.Prepend(Main);

	public MirBody Main { get; } = main;

	// Checks invariants lowering must uphold. Returns one message per violation; empty when valid.
	public List<string> Verify() {
		var errors = new List<string>();
		foreach (var body in Bodies)
			new MirVerifier(body, errors).Verify();
		return errors;
	}
}