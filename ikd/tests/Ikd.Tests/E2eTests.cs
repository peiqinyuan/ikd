using System.Text;
using Ikd.Compiler;
using Ikd.Runtime;

namespace Ikd.Tests;

/// <summary>端到端：源码 → 编译 → VM 运行，捕获标准输出。</summary>
public static class E2eTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "ikd-e2e-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { /* 忽略 */ }
        }
    }

    /// <summary>编译并运行源码；返回 (stdout+stderr 归一化, 退出码)。</summary>
    private static (string Stdout, int Code) Run(string source, params string[] progArgs)
    {
        using var dir = new TempDir();
        string file = System.IO.Path.Combine(dir.Path, "main.ikd");
        File.WriteAllText(file, source, new UTF8Encoding(false));

        var result = Compilation.CompileFile(file);
        if (!result.Success)
        {
            var sb = new StringBuilder();
            foreach (var d in result.Diagnostics.Items)
                sb.AppendLine(d.ToString());
            return (Normalize(sb.ToString()), -1);
        }

        var oldOut = Console.Out;
        var oldErr = Console.Error;
        var buf = new StringWriter();
        var err = new StringWriter();
        Console.SetOut(buf);
        Console.SetError(err);
        try
        {
            var vm = new Vm(result.Image!);
            int code = vm.Run(progArgs);
            return (Normalize(buf.ToString() + err.ToString()), code);
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    private static void AssertOut(string expected, string source, params string[] progArgs)
    {
        var (stdout, code) = Run(source, progArgs);
        Assert.Equal(0, code, "退出码非 0，输出:\n" + stdout);
        Assert.Equal(expected, stdout);
    }

    [Test]
    public static void Hello()
        => AssertOut("Hello, ikd!\n",
            "fn main(args) { println(\"Hello, ikd!\"); return 0; }");

    [Test]
    public static void ProgramArgs()
        => AssertOut("2\n",
            "fn main(args) { println(toStr(len(args))); return 0; }", "a", "b");

    [Test]
    public static void LocalCall()
        => AssertOut("6\n",
            """
            fn add(a, b) { return a + b; }
            fn main(args) { println(toStr(add(2, 4))); return 0; }
            """);

    [Test]
    public static void ClosureCaptures()
        => AssertOut("42\n",
            """
            fn make(n) { return |x| x + n; }
            fn main(args) { let f = make(37); println(toStr(f(5))); return 0; }
            """);

    [Test]
    public static void Recursion()
        => AssertOut("120\n",
            """
            fn fact(n) { if (n <= 1) { return 1; } return n * fact(n - 1); }
            fn main(args) { println(toStr(fact(5))); return 0; }
            """);

    [Test]
    public static void ClassInheritanceOverride()
        => AssertOut("woof\nrex: woof\n",
            """
            class Animal {
                let name: Str;
                init(name) { this.name = name; }
                fn speak(): Str { return "..."; }
            }
            class Dog : Animal {
                init(name) { super.init(name); }
                override fn speak(): Str { return "woof"; }
            }
            fn main(args) {
                let d = Dog("rex");
                println(d.speak());
                println(d.name + ": " + d.speak());
                return 0;
            }
            """);

    [Test]
    public static void StaticMembers()
        => AssertOut("3\n",
            """
            class Counter {
                static var total = 0;
                static fn bump(n) { Counter.total = Counter.total + n; return Counter.total; }
            }
            fn main(args) { println(toStr(Counter.bump(3))); return 0; }
            """);

    [Test]
    public static void EnumMatch()
        => AssertOut("6\n",
            """
            enum Tree { Leaf(Int), Node(Tree, Tree) }
            fn sum(t) {
                return match t {
                    Tree.Leaf(v) => v,
                    Tree.Node(l, r) => sum(l) + sum(r),
                    _ => 0,
                };
            }
            fn main(args) {
                let t = Tree.Node(Tree.Leaf(1), Tree.Node(Tree.Leaf(2), Tree.Leaf(3)));
                println(toStr(sum(t)));
                return 0;
            }
            """);

    [Test]
    public static void MatchGuardAndTypePattern()
        => AssertOut("big\nmid\n",
            """
            fn f(n) {
                return match n {
                    0 => "zero",
                    is Int when n > 5 => "big",
                    is Int => "small",
                    _ => "mid",
                };
            }
            fn main(args) { println(f(9)); println(f("s")); return 0; }
            """);

    [Test]
    public static void MatchExhaustiveRuntimeError()
    {
        var (stdout, code) = Run(
            """
            fn f(n) { return match n { 0 => "zero" }; }
            fn main(args) { println(f(1)); return 0; }
            """);
        Assert.Equal(1, code, "非穷尽 match 应以非 0 退出，输出: " + stdout);
        Assert.Contains("match 没有匹配任何分支", stdout);
    }

    [Test]
    public static void TryCatchFinally()
        => AssertOut("caught: boom\nafter\n",
            """
            fn main(args) {
                try { throw "boom"; } catch e { println("caught: " + toStr(e)); }
                println("after");
                return 0;
            }
            """);

    [Test]
    public static void FinallyRunsOnThrow()
        => AssertOut("fin\ncaught\n",
            """
            fn main(args) {
                try {
                    try { throw 1; } finally { println("fin"); }
                } catch e { println("caught"); }
                return 0;
            }
            """);

    [Test]
    public static void GenericErasure()
        => AssertOut("42\ntext\n",
            """
            class Box<T> {
                let value: T;
                init(value) { this.value = value; }
                fn get(): T { return this.value; }
            }
            fn firstOr<T>(list, fallback) {
                if (len(list) > 0) { return list[0]; }
                return fallback;
            }
            fn main(args) {
                println(toStr(Box(42).get()));
                println(Box("text").get());
                return 0;
            }
            """);

    [Test]
    public static void NullSafeMember()
        => AssertOut("null\n7\n",
            """
            class P { var v: Int = 7; }
            fn main(args) {
                let x = null;
                println(toStr(x?.v));
                let p = P();
                println(toStr(p?.v));
                return 0;
            }
            """);

    [Test]
    public static void ShortCircuitAndNullCoalesce()
        =>             AssertOut("true\n2\n",
            """
            fn main(args) {
                println(toStr(true && true));
                let a = null;
                println(toStr(a ?? 2));
                return 0;
            }
            """);

    [Test]
    public static void ForLoopAndList()
        => AssertOut("6\n[1, 2, 3]\n",
            """
            fn main(args) {
                var s = 0;
                let xs = [1, 2, 3];
                for x in xs { s = s + x; }
                println(toStr(s));
                println(toStr(xs));
                return 0;
            }
            """);

    [Test]
    public static void MapAndIndex()
        => AssertOut("2\n",
            """
            fn main(args) {
                var m = {"a": 1, "b": 2};
                println(toStr(m["b"]));
                return 0;
            }
            """);

    [Test]
    public static void ExitCodeFromMain()
    {
        var (_, code) = Run("fn main(args) { return 3; }");
        Assert.Equal(3, code);
    }

    [Test]
    public static void CompileErrorReported()
    {
        var (stdout, _) = Run("fn main(args) { return undefinedThing; }");
        Assert.Contains("E", stdout);
    }

    [Test]
    public static void CrossModuleImport()
    {
        using var dir = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "lib.ikd"),
            "pub fn twice(n) { return n * 2; }\npub class Adder { init() {} fn add(a) { return a + 1; } }\n",
            new UTF8Encoding(false));
        string main = System.IO.Path.Combine(dir.Path, "main.ikd");
        File.WriteAllText(main,
            "import \"lib\";\nfn main(args) { println(toStr(lib.twice(21))); return 0; }\n",
            new UTF8Encoding(false));

        var result = Compilation.CompileFile(main);
        Assert.True(result.Success,
            "应编译成功:\n" + string.Join("\n", result.Diagnostics.Items));

        var old = Console.Out;
        var oldErr = Console.Error;
        var buf = new StringWriter();
        var err = new StringWriter();
        Console.SetOut(buf);
        Console.SetError(err);
        try
        {
            int code = new Vm(result.Image!).Run(Array.Empty<string>());
            Assert.Equal(0, code, buf.ToString() + err.ToString());
            Assert.Equal("42\n", Normalize(buf.ToString()));
        }
        finally { Console.SetOut(old); Console.SetError(oldErr); }
    }

    [Test]
    public static void MissingImportReported()
    {
        using var dir = new TempDir();
        string main = System.IO.Path.Combine(dir.Path, "main.ikd");
        File.WriteAllText(main, "import \"nope\";\nfn main(args) { return 0; }\n",
            new UTF8Encoding(false));
        var result = Compilation.CompileFile(main);
        Assert.False(result.Success, "缺失导入应编译失败");
        Assert.Contains("E4001", string.Join("\n", result.Diagnostics.Items));
    }

    [Test]
    public static void ImportCycleReported()
    {
        using var dir = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "a.ikd"),
            "import \"b\";\npub fn f() { return 1; }\n", new UTF8Encoding(false));
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "b.ikd"),
            "import \"a\";\npub fn g() { return 2; }\n", new UTF8Encoding(false));
        string main = System.IO.Path.Combine(dir.Path, "main.ikd");
        File.WriteAllText(main, "import \"a\";\nfn main(args) { return 0; }\n",
            new UTF8Encoding(false));

        var result = Compilation.CompileFile(main);
        Assert.False(result.Success, "循环导入应编译失败");
        Assert.Contains("E4002", string.Join("\n", result.Diagnostics.Items));
    }
}
