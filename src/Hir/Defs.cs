namespace fluke.Hir;

// Definitions are classes, not records: they are compared by identity, and their member lists are
// filled in during collection so that types can refer to definitions declared later in the source.

internal abstract class Definition(string name, Span span) {
	public string Name { get; } = name;
	public Span Span { get; } = span;

	public override string ToString() => Name;
}

internal sealed class StructDef(string name, Span span) : Definition(name, span) {
	public List<FieldDef> Fields { get; } = [];
}

internal sealed class EnumDef(string name, Span span) : Definition(name, span) {
	public List<VariantDef> Variants { get; } = [];
}

internal sealed class VariantDef(EnumDef parent, string name, int tag, bool isBare, Span span) {
	public EnumDef Parent { get; } = parent;
	public string Name { get; } = name;
	public int Tag { get; } = tag;

	// Declared without braces. `V {}` is not bare, even though it has no fields.
	public bool IsBare { get; } = isBare;
	public Span Span { get; } = span;

	// Empty for bare variants
	public List<FieldDef> Fields { get; } = [];

	public override string ToString() => $"{Parent.Name}.{Name}";
}

internal sealed class FieldDef(string name, Ty type, int index, Span span, Span typeSpan) {
	public string Name { get; } = name;
	public Ty Type { get; } = type;

	// Position in declaration order
	public int Index { get; } = index;
	public Span Span { get; } = span;

	// Span of the type name, for diagnostics about the field's type
	public Span TypeSpan { get; } = typeSpan;

	public override string ToString() => Name;
}

internal enum LocalKind {
	Param,
	Let,
	PatternBinding,
}

// Parameters and `let` bindings are both locals. Every binding gets its own LocalDef, so shadowing
// is already resolved in the HIR.
internal sealed class LocalDef(string name, int id, Ty type, LocalKind kind, Span span) : Definition(name, span) {
	// Unique within the enclosing function, in binding order, so shadowed locals can be told apart
	public int Id { get; } = id;
	public Ty Type { get; } = type;
	public LocalKind Kind { get; } = kind;

	// For HIR dumps; ToString stays the source name so diagnostics read naturally
	public string UniqueName => $"{Name}#{Id}";
}

internal sealed class FnDef(string name, string mangledName, FnDef? parent, Span span) : Definition(name, span) {
	public string MangledName { get; } = mangledName;

	// Enclosing function: Main for top-level functions, null only for Main itself. Nested functions
	// cannot capture, so this only matters for naming.
	public FnDef? Parent { get; } = parent;

	public List<LocalDef> Params { get; } = [];
	public Ty ReturnType { get; set; } = Ty.Unit;

	// Set once the body has been checked
	public HirExpression.Block? Body { get; set; }

	// For HIR dumps, e.g. `fn add(a#0: int, b#1: int): int`
	public string Signature =>
		$"fn {MangledName}({string.Join(", ", Params.Select(p => $"{p.UniqueName}: {p.Type}"))}): {ReturnType}";

	public override string ToString() => MangledName;
}

internal sealed class HirProgram(FnDef main) {
	public List<StructDef> Structs { get; } = [];
	public List<EnumDef> Enums { get; } = [];

	// Every function, nested ones included. Does not contain Main.
	public List<FnDef> Functions { get; } = [];

	// Synthesized function holding the top-level statements
	public FnDef Main { get; } = main;
}