using Ikd.Compiler;
using Ikd.Runtime;

namespace Ikd.Tests;

public static class ExampleSmokeTests
{
    private static string ExamplesDir()
    {
        // tests/Ikd.Tests/bin/Debug/net10.0 → ikd/examples
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples"));
        return dir;
    }

    [Test]
    public static void AllExamplesCompileAndRun()
    {
        string dir = ExamplesDir();
        Assert.True(Directory.Exists(dir), "找不到 examples: " + dir);

        string[] mains =
        {
            "hello.ikd", "shapes.ikd", "generics.ikd", "oo.ikd",
            "app.ikd", "closures.ikd", "match.ikd", "errors.ikd",
        };

        foreach (var name in mains)
        {
            string file = Path.Combine(dir, name);
            Assert.True(File.Exists(file), "缺少示例 " + file);
            var result = Compilation.CompileFile(file);
            Assert.True(result.Success,
                name + " 编译失败:\n" + string.Join("\n", result.Diagnostics.Items));

            var oldOut = Console.Out;
            var oldErr = Console.Error;
            var buf = new StringWriter();
            Console.SetOut(buf);
            Console.SetError(buf);
            try
            {
                int code = new Vm(result.Image!).Run(Array.Empty<string>());
                Assert.Equal(0, code, name + " 退出码非 0，输出:\n" + buf);
            }
            finally
            {
                Console.SetOut(oldOut);
                Console.SetError(oldErr);
            }
        }
    }
}
