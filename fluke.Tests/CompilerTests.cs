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
	public void Run(string path) {
		var ast = CompilerPipeline.LexAndParse(path);
		var program = new Generator().GenerateInMemory(ast);
		program.Run();
	}

	[Fact]
	public void RejectsUnterminatedBlockCommentDuringLexing() {
		var ex = Assert.Throws<ParseException<char>>(() => Lexer.Lex("let a = 1; /* unterminated"));
		Assert.Contains("unterminated block comment", ex.Message);
	}
	
	[Theory]
	[InlineData("enum Bad { X { s: string } }", "s", 19)]
	[InlineData("struct W { s: string } enum Bad2 { X { w: W } }", "w.s", 43)]
	public void RejectsReferenceTypesInVariants(string source, string expectedField, int expectedCol) {
		var ast = Parse(source);
		var ex = Assert.Throws<CompileException>(() => new Generator().GenerateInMemory(ast));
		var d = Assert.Single(ex.Diagnostics, d => d.Message.Contains("reference types are not allowed"));
		Assert.Contains(expectedField, d.Message);
		// Points at the variant field's type
		Assert.Equal(expectedCol, d.Span.Start.Col);
	}

	[Theory]
	[InlineData("enum E { A { v: int } } let e = E.A { v: 1 }; if let E.B { v } = e { :print(v); }",
		"No variant named 'B'")]
	[InlineData("enum E { A { v: int } } let e = E.A { v: 1 }; if let E.A { w } = e { :print(w); }",
		"no field 'w'")]
	[InlineData("enum E { A } let e = E.A; if let E.A { v } = e { :print(v); }", "cannot bind fields")]
	public void RejectsInvalidIfLet(string source, string expectedFragment) {
		var ast = Parse(source);
		var ex = Assert.Throws<CompileException>(() => new Generator().GenerateInMemory(ast));
		Assert.Single(ex.Diagnostics, d => d.Message.Contains(expectedFragment));
	}

	[Theory]
	[InlineData("enum E { A } let x = E.A { v: 1 };", "Bare variant")]
	[InlineData("enum E { A { v: int } } let x = E.A { };", "missing fields: v")]
	[InlineData("enum E { A { v: int } } let x = E.A { v: 1, w: 2 };", "no field 'w'")]
	[InlineData("enum E { A { v: int } } let x = E.A { v: true };", "expected 'int'")]
	public void RejectsInvalidEnumVariantInit(string source, string expectedFragment) {
		var ast = Parse(source);
		var ex = Assert.Throws<CompileException>(() => new Generator().GenerateInMemory(ast));
		Assert.Single(ex.Diagnostics, d => d.Message.Contains(expectedFragment));
	}

	private static Ast Parse(string source) {
		var (tokens, eof) = Lexer.Lex(source);
		return Parser.Parse(tokens, eof);
	}
}
