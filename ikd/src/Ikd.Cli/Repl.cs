using System.Text;
using Ikd.Compiler;
using Ikd.Compiler.Diagnostics;
using Ikd.Runtime;

namespace Ikd.Cli;

/// <summary>交互式求值：声明会累积；表达式会打印结果。</summary>
internal static class Repl
{
    public static int Run()
    {
        Console.WriteLine($"ikd {Program.Version} 交互环境。输入 :help 查看命令，:quit 退出。");
        var history = new StringBuilder();
        var buffer = new StringBuilder();
        int depth = 0;

        while (true)
        {
            Console.Write(buffer.Length == 0 ? ">>> " : "... ");
            string? line = Console.ReadLine();
            if (line is null) break;

            if (buffer.Length == 0 && line.StartsWith(':'))
            {
                if (!HandleCommand(line, history)) break;
                continue;
            }

            if (buffer.Length > 0) buffer.AppendLine();
            buffer.Append(line);
            depth += DeltaDepth(line);
            if (depth > 0) continue;
            if (depth < 0)
            {
                Console.Error.WriteLine("ikd: 括号不匹配");
                buffer.Clear();
                depth = 0;
                continue;
            }

            string snippet = buffer.ToString().Trim();
            buffer.Clear();
            depth = 0;
            if (snippet.Length == 0) continue;
            Evaluate(snippet, history);
        }

        return 0;
    }

    private static bool HandleCommand(string line, StringBuilder history)
    {
        string cmd = line.Trim();
        switch (cmd)
        {
            case ":q":
            case ":quit":
            case ":exit":
                return false;
            case ":help":
                Console.WriteLine(
"""
:help     显示本帮助
:quit     退出
:reset    清空已定义的声明
:src      显示当前累积源码
表达式会打印值；fn / class / let 等声明会保留到后续输入。
""");
                return true;
            case ":reset":
                history.Clear();
                Console.WriteLine("已清空");
                return true;
            case ":src":
                Console.WriteLine(history.Length == 0 ? "(空)" : history.ToString());
                return true;
            default:
                Console.WriteLine("未知命令，输入 :help");
                return true;
        }
    }

    private static void Evaluate(string snippet, StringBuilder history)
    {
        bool decl = LooksLikeDeclaration(snippet);
        string source;
        if (decl)
        {
            source = history + snippet + "\nfn main(args) { return 0; }\n";
        }
        else if (LooksLikePrintCall(snippet))
        {
            source = history + "fn main(args) {\n" + snippet + "\nreturn 0;\n}\n";
        }
        else
        {
            source = history + "fn main(args) {\nprintln(toStr((" + StripSemi(snippet) + ")));\nreturn 0;\n}\n";
        }

        var result = Compilation.CompileSource(source, "<repl>");
        if (result.Diagnostics.Items.Count > 0)
        {
            bool color = !Console.IsOutputRedirected &&
                         Environment.GetEnvironmentVariable("NO_COLOR") is null;
            foreach (var d in result.Diagnostics.Items)
                Console.Error.WriteLine(DiagnosticRenderer.Render(d, color));
        }
        if (!result.Success || result.Image is null) return;

        try
        {
            int code = new Vm(result.Image).Run(Array.Empty<string>());
            if (code != 0) Console.Error.WriteLine($"退出码 {code}");
            if (decl) { history.AppendLine(snippet); history.AppendLine(); }
            else if (IsPersistentBinding(snippet))
            {
                history.AppendLine(snippet);
                history.AppendLine();
            }
        }
        catch (IkdException ex)
        {
            Console.Error.WriteLine($"ikd: {ex.Message}");
        }
    }

    private static bool IsPersistentBinding(string snippet)
    {
        string t = snippet.TrimStart();
        return t.StartsWith("let ", StringComparison.Ordinal)
            || t.StartsWith("var ", StringComparison.Ordinal)
            || t.StartsWith("const ", StringComparison.Ordinal);
    }

    private static bool LooksLikeDeclaration(string snippet)
    {
        string t = snippet.TrimStart();
        return t.StartsWith("fn ", StringComparison.Ordinal)
            || t.StartsWith("class ", StringComparison.Ordinal)
            || t.StartsWith("interface ", StringComparison.Ordinal)
            || t.StartsWith("enum ", StringComparison.Ordinal)
            || t.StartsWith("import ", StringComparison.Ordinal)
            || t.StartsWith("pub ", StringComparison.Ordinal)
            || t.StartsWith("abstract ", StringComparison.Ordinal)
            || t.StartsWith("static ", StringComparison.Ordinal)
            || t.StartsWith("let ", StringComparison.Ordinal)
            || t.StartsWith("var ", StringComparison.Ordinal)
            || t.StartsWith("const ", StringComparison.Ordinal);
    }

    private static bool LooksLikePrintCall(string snippet)
    {
        string t = snippet.TrimStart();
        return t.StartsWith("println", StringComparison.Ordinal)
            || t.StartsWith("print(", StringComparison.Ordinal)
            || t.StartsWith("print ", StringComparison.Ordinal);
    }

    private static string StripSemi(string s)
    {
        s = s.Trim();
        return s.EndsWith(';') ? s[..^1] : s;
    }

    internal static int DeltaDepth(string line)
    {
        int d = 0;
        bool inStr = false, escape = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inStr)
            {
                if (escape) { escape = false; continue; }
                if (c == '\\') { escape = true; continue; }
                if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') { inStr = true; continue; }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
            if (c is '(' or '{' or '[') d++;
            else if (c is ')' or '}' or ']') d--;
        }
        return d;
    }
}
