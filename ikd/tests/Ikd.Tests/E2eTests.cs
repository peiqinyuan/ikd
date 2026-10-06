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

    // ---------------- if 表达式 / 值块 / 构造器 ----------------

    [Test]
    public static void IfExpressionElseIfChain()
        => AssertOut("pos\nzero\nneg\n",
            "fn cls(n) { return if (n > 0) \"pos\" else if (n == 0) \"zero\" else \"neg\"; }\n" +
            "fn main(args) { println(cls(3)); println(cls(0)); println(cls(-1)); return 0; }");

    [Test]
    public static void IfExpressionWithoutElseReported()
    {
        var (stdout, _) = Run("fn main(args) { return if (true) 1; }");
        Assert.Contains("E2005", stdout);
        Assert.Contains("else", stdout);
    }

    [Test]
    public static void IfExpressionValueBlock()
        => AssertOut("42\n0\n",
            "fn main(args) {\n" +
            "    let a = if (true) { let t = 41; t + 1 } else { 0 };\n" +
            "    let b = if (false) { 1 } else { 0 };\n" +
            "    println(toStr(a)); println(toStr(b));\n" +
            "    return 0;\n}");

    [Test]
    public static void MatchArmBlockBody()
        => AssertOut("2\n",
            "enum Op { Add(Int), Div(Int) }\n" +
            "fn ev(op, a, b) {\n" +
            "    return match op {\n" +
            "        Op.Add(_) => a + b,\n" +
            "        Op.Div(_) => {\n" +
            "            if (b == 0) { throw \"div0\"; }\n" +
            "            a / b\n" +
            "        },\n" +
            "    };\n" +
            "}\n" +
            "fn main(args) { println(toStr(ev(Op.Div(Op), 5, 2))); return 0; }");

    [Test]
    public static void MatchArmBlockBodyPropagatesThrow()
    {
        var (stdout, code) = Run(
            "enum Op { Div(Int) }\n" +
            "fn ev(op, a, b) {\n" +
            "    return match op {\n" +
            "        Op.Div(_) => {\n" +
            "            if (b == 0) { throw \"div0\"; }\n" +
            "            a / b\n" +
            "        },\n" +
            "    };\n" +
            "}\n" +
            "fn main(args) { println(toStr(ev(Op.Div(Op), 1, 0))); return 0; }");
        Assert.Contains("div0", stdout);
        Assert.True(code != 0, "未捕获的 throw 应非 0 退出");
    }

    [Test]
    public static void MapAndListConstructors()
        => AssertOut("[1, 2, 3]\n[]\n7\n0\n",
            "fn main(args) {\n" +
            "    let xs = List(1, 2, 3);\n" +
            "    let empty = List();\n" +
            "    let m = Map(\"k\", 7);\n" +
            "    println(toStr(xs));\n" +
            "    println(toStr(empty));\n" +
            "    println(toStr(m.get(\"k\")));\n" +
            "    println(toStr(len(Map())));\n" +
            "    return 0;\n}");

    [Test]
    public static void MapOddArgsThrows()
    {
        var (stdout, code) = Run("fn main(args) { let m = Map(\"k\"); return 0; }");
        Assert.Contains("偶数", stdout);
        Assert.True(code != 0, "Map 参数个数为奇数应报运行时错误");
    }

    [Test]
    public static void IfExpressionAsArgumentAndReturnValue()
        => AssertOut("10\n",
            "fn pick(n) { return if (n > 5) 10 else 20; }\n" +
            "fn main(args) { println(toStr(pick(9))); return 0; }");

    // ---------------- 宏 ----------------

    [Test]
    public static void ConstMacroExpansion()
        => AssertOut("3.14\nA=7\n",
            "macro PI = 3.14;\n" +
            "macro TAG = \"A\";\n" +
            "fn main(args) { println(toStr(PI)); println(TAG + \"=7\"); return 0; }");

    [Test]
    public static void FunctionMacroExpansion()
        => AssertOut("10\n10\n4\n",
            "macro MAX(a, b) { if (a > b) { a } else { b } }\n" +
            "macro TWICE(x) { x + x }\n" +
            "fn main(args) {\n" +
            "    println(toStr(MAX(3, 10)));\n" +
            "    println(toStr(MAX(10, 3)));\n" +
            "    println(toStr(TWICE(2)));\n" +
            "    return 0;\n}");

    [Test]
    public static void MacroArgumentEvaluatedOnce()
        => AssertOut("2\n1\n",
            "var g = 0;\n" +
            "macro TWICE(x) { x + x }\n" +
            "fn bump() { g = g + 1; return g; }\n" +
            "fn main(args) {\n" +
            "    println(toStr(TWICE(bump())));\n" +
            "    println(toStr(g));\n" +
            "    return 0;\n}");

    [Test]
    public static void MacroParamShadowedByLocal()
        => AssertOut("100\n",
            "macro SHADOW(a) { let a = 99; a + 1 }\n" +
            "fn main(args) { println(toStr(SHADOW(1))); return 0; }");

    [Test]
    public static void NestedMacroExpansion()
        => AssertOut("4\n",
            "macro ADD(a, b) { a + b }\n" +
            "macro FOUR(x) { ADD(x, x) }\n" +
            "fn main(args) { println(toStr(FOUR(2))); return 0; }");

    [Test]
    public static void MacroReturnLeaksToCaller()
        => AssertOut("pos\nneg\n",
            "macro EARLY(x) { if (x > 0) { return \"pos\"; } \"neg\" }\n" +
            "fn cls(n) { return EARLY(n); }\n" +
            "fn main(args) { println(cls(5)); println(cls(-1)); return 0; }");

    [Test]
    public static void MacroBreakLeavesCallerLoop()
        => AssertOut("1\n",
            "var g = 0;\n" +
            "macro STOP() { break }\n" +
            "fn main(args) {\n" +
            "    for i in range(0, 10) { g = g + 1; STOP(); }\n" +
            "    println(toStr(g));\n" +
            "    return 0;\n}");

    [Test]
    public static void MacroUsedBeforeTextualDeclaration()
        => AssertOut("7\n",
            "fn main(args) { println(toStr(THREE() + 4)); return 0; }\n" +
            "macro THREE() { 3 }");

    [Test]
    public static void MacroArityMismatchReported()
    {
        var (stdout, _) = Run("macro HALF(x) { x / 2 }\nfn main(args) { return HALF(1, 2); }");
        Assert.Contains("E3004", stdout);
        Assert.Contains("HALF", stdout);
    }

    [Test]
    public static void MacroDuplicateReported()
    {
        var (stdout, _) = Run("macro A = 1;\nmacro A = 2;\nfn main(args) { return 0; }");
        Assert.Contains("E3008", stdout);
    }

    [Test]
    public static void MacroInsideFunctionReported()
    {
        var (stdout, _) = Run(
            "fn main(args) { macro B = 3; println(toStr(B)); return 0; }");
        Assert.Contains("E2011", stdout);
        Assert.Contains("顶层", stdout);
    }

    [Test]
    public static void MacroSelfReferenceReported()
    {
        var (stdout, _) = Run(
            "macro REC(x) { x + REC(x) }\nfn main(args) { return REC(1); }");
        Assert.Contains("E3037", stdout);
    }

    [Test]
    public static void ConstantMacroCannotBeCalled()
    {
        var (stdout, _) = Run("macro A = 1;\nfn main(args) { return A(1); }");
        Assert.Contains("E3003", stdout);
    }

    [Test]
    public static void FunctionMacroUsedAsValueReported()
    {
        var (stdout, _) = Run(
            "macro TWICE(x) { x + x }\nfn main(args) { let f = TWICE; return 0; }");
        Assert.Contains("E3003", stdout);
    }

    [Test]
    public static void MacroCollidingWithGlobalReported()
    {
        var (stdout, _) = Run(
            "fn main(args) { return 0; }\nclass Box { }\nmacro Box = 1;");
        Assert.Contains("E3008", stdout);
    }

    // ---------------- sizeof ----------------

    [Test]
    public static void SizeOfBuiltinTypes()
        => AssertOut("8\n8\n1\n16\n16\n16\n48\n",
            "fn main(args) {\n" +
            "    println(toStr(sizeof(Int)));\n" +
            "    println(toStr(sizeof(Float)));\n" +
            "    println(toStr(sizeof(Bool)));\n" +
            "    println(toStr(sizeof(Str)));\n" +
            "    println(toStr(sizeof(List)));\n" +
            "    println(toStr(sizeof(Map)));\n" +
            "    println(toStr(sizeof(Fn)));\n" +
            "    return 0;\n}");

    [Test]
    public static void SizeOfValues()
        => AssertOut("0\n1\n8\n20\n80\n64\n",
            "fn main(args) {\n" +
            "    println(toStr(sizeof(null)));\n" +
            "    println(toStr(sizeof(true)));\n" +
            "    println(toStr(sizeof(42)));\n" +
            "    println(toStr(sizeof(\"hi\")));\n" +
            "    println(toStr(sizeof([1, 2, 3, 4])));\n" +
            "    println(toStr(sizeof(Map(\"k\", 1))));\n" +
            "    return 0;\n}");

    [Test]
    public static void SizeOfInstanceCountsFields()
        => AssertOut("48\n80\n",
            "class Point { let x = 0; let y = 0; }\n" +
            "fn main(args) {\n" +
            "    let p = Point();\n" +
            "    println(toStr(sizeof(p)));\n" +
            "    println(toStr(sizeof(Point)));\n" +
            "    return 0;\n}");
}
