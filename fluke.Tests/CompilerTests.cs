using fluke.Hir;
using fluke.Mir;
using Pidgin;

namespace fluke.Tests;

public class CompilerTests {
	public static TheoryData<string> TestFiles() {
		var data = new TheoryData<string>();
		foreach (var file in Directory.EnumerateFiles("test", "*.fl", SearchOption.AllDirectories))
			data.Add(file);
		return data;
	}

	[Theory]
	[MemberData(nameof(TestFiles))]
	public void Checks(string path) {
		var ast = CompilerPipeline.LexAndParse(CompilerPipeline.ReadSource(path));
		new Checker().Check(ast);
	}

	// Lowering verifies the MIR and throws on invalid MIR
	[Theory]
	[MemberData(nameof(TestFiles))]
	public void Lowers(string path) {
		var hir = new Checker().Check(CompilerPipeline.LexAndParse(CompilerPipeline.ReadSource(path)));
		new MirBuilder(hir).Lower();
	}

	// Emits, verifies and runs the program; a failing :assert throws
	[Theory]
	[MemberData(nameof(TestFiles))]
	public void Run(string path) {
		var hir = new Checker().Check(CompilerPipeline.LexAndParse(CompilerPipeline.ReadSource(path)));
		var mir = new MirBuilder(hir).Lower();
		new MirEmitter(mir).EmitInMemory().Run();
	}

	[Fact]
	public void RejectsUnterminatedBlockCommentDuringLexing() {
		var ex = Assert.Throws<CompileException>(() => Lexer.Lex("let a = 1; /* unterminated"));
		var d = Assert.Single(ex.Diagnostics);
		Assert.Contains("unterminated block comment", d.Message);
		// Points at the end of input
		Assert.Equal(new SourcePos(1, 27), d.Span.Start);
	}

	[Fact]
	public void ReportsUnexpectedCharacterDuringLexing() {
		var ex = Assert.Throws<CompileException>(() => Lexer.Lex("let a = 1 $ 2;"));
		var d = Assert.Single(ex.Diagnostics);
		Assert.Contains("unexpected '$'", d.Message);
		Assert.Equal(new SourcePos(1, 11), d.Span.Start);
		Assert.Equal(new SourcePos(1, 12), d.Span.End);
	}

	[Theory]
	[InlineData("enum Bad { X { s: string } }", "s", 19)]
	[InlineData("struct W { s: string } enum Bad2 { X { w: W } }", "w.s", 43)]
	public void RejectsReferenceTypesInVariants(string source, string expectedField, int expectedCol) {
		var ast = Parse(source);
		var ex = Assert.Throws<CompileException>(() => new Checker().Check(ast));
		var d = Assert.Single(ex.Diagnostics, d => d.Message.Contains("reference types are not allowed"));
		Assert.Contains(expectedField, d.Message);
		// Points at the variant field's type
		Assert.Equal(expectedCol, d.Span.Start.Col);
	}

	[Theory]
	[InlineData("enum E { A { v: int } } let e = E.A { v: 1 }; if let E.B { v } = e { :print(v); }",
		"no variant 'B'")]
	[InlineData("enum E { A { v: int } } let e = E.A { v: 1 }; if let E.A { w } = e { :print(w); }",
		"no field 'w'")]
	[InlineData("enum E { A } let e = E.A; if let E.A { v } = e { :print(v); }", "has no fields to bind")]
	public void RejectsInvalidIfLet(string source, string expectedFragment) {
		var ast = Parse(source);
		var ex = Assert.Throws<CompileException>(() => new Checker().Check(ast));
		Assert.Single(ex.Diagnostics, d => d.Message.Contains(expectedFragment));
	}

	[Theory]
	[InlineData("enum E { A } let x = E.A { v: 1 };", "has no fields and cannot be initialized")]
	[InlineData("enum E { A { v: int } } let x = E.A { };", "missing fields in initializer of 'E.A': v")]
	[InlineData("enum E { A { v: int } } let x = E.A { v: 1, w: 2 };", "no field 'w'")]
	[InlineData("enum E { A { v: int } } let x = E.A { v: true };", "expected 'int'")]
	public void RejectsInvalidEnumVariantInit(string source, string expectedFragment) {
		var ast = Parse(source);
		var ex = Assert.Throws<CompileException>(() => new Checker().Check(ast));
		Assert.Single(ex.Diagnostics, d => d.Message.Contains(expectedFragment));
	}

	[Theory]
	[InlineData("struct P { x: int } let P = 1;", "variable 'P' has the same name as a type")]
	[InlineData("enum E { A } { let E = 1; }", "variable 'E' has the same name as a type")]
	[InlineData("struct P { x: int } fn f(P: int) {}", "parameter 'P' has the same name as a type")]
	[InlineData("struct v { x: int } enum E { A { v: int } } let e = E.A { v: 1 }; if let E.A { v } = e { :print(1); }",
		"binding 'v' has the same name as a type")]
	public void RejectsLocalsNamedLikeTypes(string source, string expectedFragment) {
		var ast = Parse(source);
		var ex = Assert.Throws<CompileException>(() => new Checker().Check(ast));
		var d = Assert.Single(ex.Diagnostics);
		Assert.Contains(expectedFragment, d.Message);
	}

	// The rejected local is left unbound, so later uses of the type report no follow-up errors
	[Fact]
	public void RejectedLocalLeavesTypeUsable() {
		var ast = Parse("struct P { x: int } let P = 1; let p = P { x: 1 }; { fn f(q: P): int { q.x } }");
		var ex = Assert.Throws<CompileException>(() => new Checker().Check(ast));
		Assert.Single(ex.Diagnostics);
	}

	// An enclosing function's local is skipped, so the outer function is found
	[Fact]
	public void ResolvesPastEnclosingLocals() {
		new Checker().Check(Parse("fn helper(): int { 1 } { let helper = 5; { fn g(): int { helper() } } }"));
	}

	[Fact]
	public void UnresolvedParamTypeReportsNoFollowUpErrors() {
		var ast = Parse("fn f(p: Unknown): int { p.x }");
		var ex = Assert.Throws<CompileException>(() => new Checker().Check(ast));
		var d = Assert.Single(ex.Diagnostics);
		Assert.Contains("cannot find type 'Unknown'", d.Message);
	}

	[Theory]
	[InlineData("struct P { x: int } let a = P { x: 1 }; let b = a == a;", "type 'P'")]
	[InlineData("enum E { A } let b = E.A != E.A;", "type 'E'")]
	[InlineData("let b = {} == {};", "type 'unit'")]
	public void RejectsEqualityOnNonPrimitives(string source, string expectedFragment) {
		var ast = Parse(source);
		var ex = Assert.Throws<CompileException>(() => new Checker().Check(ast));
		var d = Assert.Single(ex.Diagnostics);
		Assert.Contains("cannot compare values of " + expectedFragment, d.Message);
	}

	[Theory]
	[InlineData("let b = 1 == 2;")]
	[InlineData("let b = true != false;")]
	[InlineData("let b = \"a\" == \"b\";")]
	[InlineData("fn f(): bool { let x = { return true; }; x == 1 }")]
	public void AcceptsEqualityOnPrimitives(string source) {
		new Checker().Check(Parse(source));
	}

	// The binding diverged, so the assignment is unreachable and not checked
	[Fact]
	public void AcceptsAssignmentToNeverLocal() {
		new Checker().Check(Parse("fn f(): int { let x = { return 1; }; x = 5; 2 }"));
	}

	private static Ast Parse(string source) {
		var (tokens, eof) = Lexer.Lex(source);
		return Parser.Parse(tokens, eof);
	}
}