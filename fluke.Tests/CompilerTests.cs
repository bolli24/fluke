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
}