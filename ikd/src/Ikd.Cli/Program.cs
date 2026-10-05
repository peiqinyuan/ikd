using Ikd.Compiler;
using Ikd.Compiler.Diagnostics;
using Ikd.Runtime;

namespace Ikd.Cli;

public static class Program
{
    public const string Version = "0.1.0";

    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (IkdException ex)
        {
            Console.Error.WriteLine($"ikd: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ikd: 内部错误: {ex}");
            return 70;
        }
    }

    private static int Run(string[] args)
    {
        bool embedded = ImageCodec.TryReadFromExe(Environment.ProcessPath ?? "", out var image);

        if (args.Length == 0)
        {
            // 内嵌了程序 → 直接运行（无参数）
            if (embedded) return RunEmbedded(image, Array.Empty<string>());
            PrintHelp();
            return 0;
        }

        var command = args[0];
        bool wantsMeta = command is "version" or "--version" or "-v"
            or "help" or "--help" or "-h";

        // 自身尾部带字节码 → 直接运行（ikd build 产物）
        if (embedded && !wantsMeta)
            return RunEmbedded(image, args);

        switch (command)
        {
            case "--version":
            case "-v":
            case "version":
                Console.WriteLine($"ikd {Version}");
                return 0;
            case "--help":
            case "-h":
            case "help":
                PrintHelp();
                return 0;
            case "run":
                return RunProgram(args.Skip(1).ToArray());
            case "check":
                return Check(args.Skip(1).ToArray());
            case "build":
                return Build(args.Skip(1).ToArray());
            case "disasm":
                return Disasm(args.Skip(1).ToArray());
            case "repl":
                return Repl.Run();
            case "new":
                return NewProject.Run(args.Skip(1).ToArray());
            default:
                if (command.EndsWith(".ikd", StringComparison.OrdinalIgnoreCase))
                    return RunProgram(args);
                Console.Error.WriteLine($"ikd: 未知命令 '{command}'，使用 'ikd help' 查看帮助。");
                return 2;
        }
    }

    // ----------------------------------------------------------------
    //  run
    // ----------------------------------------------------------------

    /// <summary>运行内嵌在本可执行文件里的程序；参数原样传给 main。</summary>
    private static int RunEmbedded(ProgramImage image, string[] args)
    {
        try
        {
            return new Vm(image).Run(args);
        }
        catch (IkdException ex)
        {
            Console.Error.WriteLine($"ikd: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ikd: 内部错误: {ex}");
            return 70;
        }
    }

    private static int RunProgram(string[] args)
    {
        string? file = null;
        var progArgs = new List<string>();
        bool afterDash = false;
        foreach (var a in args)
        {
            if (afterDash) { progArgs.Add(a); continue; }
            if (a == "--") { afterDash = true; continue; }
            if (file is null) file = a;
            else progArgs.Add(a);
        }

        if (file is null)
        {
            Console.Error.WriteLine("ikd: run 需要一个 .ikd 文件");
            return 2;
        }

        var result = Compilation.CompileFile(file);
        if (!PrintDiagnostics(result.Diagnostics)) return 1;
        if (result.Image is null) return 1;

        var vm = new Vm(result.Image);
        return vm.Run(progArgs.ToArray());
    }

    // ----------------------------------------------------------------
    //  build
    // ----------------------------------------------------------------

    /// <summary>把程序编译成可执行文件：自身副本 + 尾部追加压缩字节码。</summary>
    private static int Build(string[] args)
    {
        string? file = null, output = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-o" || args[i] == "--output")
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("ikd: -o 需要一个输出路径");
                    return 2;
                }
                output = args[++i];
            }
            else if (file is null) file = args[i];
            else
            {
                Console.Error.WriteLine($"ikd: 未知参数 '{args[i]}'");
                return 2;
            }
        }

        if (file is null)
        {
            Console.Error.WriteLine("ikd: build 需要一个 .ikd 文件");
            return 2;
        }

        var result = Compilation.CompileFile(file);
        if (!PrintDiagnostics(result.Diagnostics) || result.Image is null) return 1;

        output ??= Path.ChangeExtension(Path.GetFileNameWithoutExtension(file), ".exe");

        string? host = FindHostExecutable();
        if (host is null)
        {
            Console.Error.WriteLine("ikd: 找不到可作为宿主的 ikd 可执行文件（请先发布 ikd.exe）");
            return 1;
        }

        try
        {
            byte[] hostBytes = File.ReadAllBytes(host);
            ImageCodec.AppendToExe(output, result.Image, hostBytes);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ikd: 生成可执行文件失败: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"已生成 {output}");
        return 0;
    }

    /// <summary>定位承载 VM 的可执行文件：优先当前进程，其次同目录的 ikd.exe。</summary>
    private static string? FindHostExecutable()
    {
        var self = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(self))
        {
            string name = Path.GetFileNameWithoutExtension(self);
            // 以 `dotnet xxx.dll` 方式运行时不认 dotnet 本体
            if (!name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("testhost", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(self))
                return self;
        }

        string dir = AppContext.BaseDirectory;
        foreach (var candidate in new[] { "ikd.exe", "ikd" })
        {
            string p = Path.Combine(dir, candidate);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // ----------------------------------------------------------------
    //  check / disasm
    // ----------------------------------------------------------------

    private static int Check(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("ikd: check 需要一个 .ikd 文件");
            return 2;
        }
        var result = Compilation.CompileFile(args[0]);
        bool ok = PrintDiagnostics(result.Diagnostics);
        if (ok && result.Image is not null)
            Console.WriteLine("检查通过");
        return ok && result.Image is not null ? 0 : 1;
    }

    private static int Disasm(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("ikd: disasm 需要一个 .ikd 文件");
            return 2;
        }
        var result = Compilation.CompileFile(args[0]);
        if (!PrintDiagnostics(result.Diagnostics) || result.Image is null) return 1;

        foreach (var m in result.Image.Modules)
            Console.WriteLine(Disassembler.DisassembleModule(m));
        return 0;
    }

    /// <summary>打印诊断；返回 true 表示没有错误。</summary>
    private static bool PrintDiagnostics(DiagnosticBag bag)
    {
        if (bag.Items.Count == 0) return true;
        bool color = !Console.IsOutputRedirected &&
                     Environment.GetEnvironmentVariable("NO_COLOR") is null;
        foreach (var d in bag.Items)
            Console.Error.WriteLine(DiagnosticRenderer.Render(d, color));
        if (bag.HasErrors)
            Console.Error.WriteLine($"编译失败：{bag.ErrorCount} 个错误，{bag.WarningCount} 个警告");
        return !bag.HasErrors;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
$"""
ikd {Version} — ikd 编译器 / 虚拟机

用法:
  ikd run <file.ikd> [-- args...]   运行程序
  ikd build <file.ikd> [-o out.exe] 生成可执行文件
  ikd check <file.ikd>              只做编译检查
  ikd disasm <file.ikd>             反汇编字节码
  ikd new <dir>                     生成示例项目
  ikd repl                          交互式求值
  ikd version                       显示版本
  ikd help                          显示本帮助
""");
    }
}
