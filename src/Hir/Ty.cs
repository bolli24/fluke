using System.Diagnostics;

namespace fluke.Hir;

// Semantic types. These know nothing about the CLR; the backend maps definitions to CLR types.
internal abstract record Ty {
	internal sealed record IntTy : Ty;

	internal sealed record BoolTy : Ty;

	internal sealed record StringTy : Ty;

	internal sealed record UnitTy : Ty;

	internal sealed record NeverTy : Ty;

	internal sealed record ErrorTy : Ty;

	// Equality is reference equality on the definition
	public sealed record Struct(StructDef def) : Ty;

	public sealed record Enum(EnumDef def) : Ty;

	public sealed record Ref(Ty inner) : Ty;

	public static readonly Ty Int = new IntTy();
	public static readonly Ty Bool = new BoolTy();
	public static readonly Ty String = new StringTy();
	public static readonly Ty Unit = new UnitTy();
	public static readonly Ty Never = new NeverTy();
	public static readonly Ty Error = new ErrorTy();

	public bool CoercesTo(Ty target) => this == target || this == Error || this == Never || target == Error;

	public sealed override string ToString() =>
		this switch {
			IntTy => "int",
			BoolTy => "bool",
			StringTy => "string",
			UnitTy => "unit",
			NeverTy => "never",
			ErrorTy => "ERROR",
			Struct(var def) => def.Name,
			Enum(var def) => def.Name,
			Ref(var inner) => $"ref {inner}",
			_ => throw new UnreachableException(),
		};
}