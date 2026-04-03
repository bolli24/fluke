using System.Diagnostics;

namespace fluke;

public record FlukeProgram(List<Statement> Statements);

public abstract record Statement {
	public record IntrinsicCall(string Name) : Statement;

	public sealed override string ToString() {
		return this switch {
			IntrinsicCall(var name) => $"Intrincic({name})",
			_ => throw new UnreachableException()
		};
	}
}
