using Ikd.Compiler.Diagnostics;
using Ikd.Compiler.Emit;
using Ikd.Compiler.Syntax;
using Ikd.Runtime;

namespace Ikd.Compiler;

/// <summary>一次编译的结果。</summary>
public sealed class CompilationResult
{
    public ProgramImage? Image { get; init; }
    public required DiagnosticBag Diagnostics { get; init; }
    public bool Success => Image is not null;
}

/// <summary>
/// 编译驱动：读入口文件 → 解析 → 递归解析依赖 → 逐模块声明/发射 → 组装 <see cref="ProgramImage"/>。
/// 模块名统一为绝对路径（"std.*" 原样保留），与 VM 的按名查找保持一致。
/// </summary>
public static class Compilation
{
    /// <summary>从内存源码编译（REPL / 测试用，写入临时文件以复用模块解析）。</summary>
    public static CompilationResult CompileSource(string source, string path = "<memory>")
    {
        string dir = Path.Combine(Path.GetTempPath(), "ikd-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, Path.GetFileName(path.EndsWith(".ikd", StringComparison.OrdinalIgnoreCase) ? path : "main.ikd"));
        try
        {
            File.WriteAllText(file, source);
            return CompileFile(file);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 忽略 */ }
        }
    }

    public static CompilationResult CompileFile(string entryPath)
    {
        var bag = new DiagnosticBag();

        string full;
        try
        {
            full = Path.GetFullPath(entryPath);
        }
        catch
        {
            full = entryPath;
        }

        if (!File.Exists(full))
        {
            var stub = SourceText.From("", full);
            bag.SetSource(stub);
            bag.Report(ErrorCode.FileNotFound, TextSpan.Empty,
                $"找不到入口文件 '{entryPath}'", "请检查路径是否正确");
            return new CompilationResult { Diagnostics = bag };
        }

        var state = new ProgramState();
        var units = new Dictionary<string, CompilationUnit>(StringComparer.Ordinal);
        var importMaps = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var order = new List<string>();
        var onPath = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var chain = new List<string>();

        void Visit(string name, SourceText? fromSource, TextSpan importSpan)
        {
            if (done.Contains(name)) return;

            if (!onPath.Add(name))
            {
                if (fromSource is not null)
                {
                    bag.SetSource(fromSource);
                    bag.Report(ErrorCode.ImportCycle, importSpan,
                        "模块导入存在循环引用: " +
                        string.Join(" → ", chain.Select(Short)) + " → " + Short(name),
                        "请拆分循环依赖");
                }
                return;
            }
            chain.Add(name);

            SourceText source;
            try
            {
                source = SourceText.FromFile(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                bag.SetSource(fromSource ?? SourceText.From("", name));
                bag.Report(ErrorCode.FileNotFound,
                    fromSource is not null ? importSpan : TextSpan.Empty,
                    $"无法读取模块 '{Short(name)}': {ex.Message}");
                chain.RemoveAt(chain.Count - 1);
                onPath.Remove(name);
                return;
            }

            var unit = Parser.Parse(source, bag);
            units[name] = unit;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            importMaps[name] = map;

            foreach (var stmt in unit.Statements)
            {
                if (stmt is not ImportStmt im) continue;
                string target = ResolveImport(name, im.Path);
                map[im.Path] = target;
                if (target.StartsWith("std.", StringComparison.Ordinal)) continue;

                bag.SetSource(source);
                if (!File.Exists(target))
                {
                    bag.Report(ErrorCode.FileNotFound, im.PathSpan,
                        $"找不到导入的文件 '{im.Path}'", "请检查路径或是否遗漏 .ikd 后缀");
                    continue;
                }
                Visit(target, source, im.PathSpan);
            }

            bag.SetSource(source);
            chain.RemoveAt(chain.Count - 1);
            onPath.Remove(name);
            done.Add(name);
            order.Add(name);
        }

        Visit(full, null, default);

        if (bag.HasErrors) return new CompilationResult { Diagnostics = bag };

        var modules = new List<ModuleImage>();
        foreach (var name in order)
        {
            var unit = units[name];
            bag.SetSource(unit.Source);
            var emitter = new Emitter(state, bag, unit, name, name);
            emitter.Declare();
            var img = emitter.Emit();

            // 导入表：源码里的写法 → 规范化模块名（VM 按名字查找）
            var map = importMaps[name];
            for (int i = 0; i < img.Imports.Length; i++)
                if (map.TryGetValue(img.Imports[i], out var norm))
                    img.Imports[i] = norm;

            modules.Add(img);
        }

        if (bag.HasErrors) return new CompilationResult { Diagnostics = bag };

        var image = new ProgramImage
        {
            EntryPath = entryPath,
            EntryName = full,
            Modules = modules.ToArray(),
            Interfaces = state.InterfaceList.ToArray(),
            NativeNames = state.NativeNames.ToArray(),
            Version = 1,
        };
        return new CompilationResult { Image = image, Diagnostics = bag };
    }

    /// <summary>把 import 路径解析为规范模块名（"std.*" 原样返回；其余为绝对路径）。</summary>
    public static string ResolveImport(string fromModule, string importPath)
    {
        if (importPath.StartsWith("std.", StringComparison.Ordinal))
            return importPath;

        string dir = "";
        try { dir = Path.GetDirectoryName(Path.GetFullPath(fromModule)) ?? ""; }
        catch { /* 保持为空 */ }

        string combined = Path.IsPathRooted(importPath)
            ? importPath
            : Path.Combine(dir, importPath);
        if (!Path.HasExtension(combined)) combined += ".ikd";

        try { return Path.GetFullPath(combined); }
        catch { return combined; }
    }

    private static string Short(string path)
        => string.IsNullOrEmpty(path) ? path : Path.GetFileName(path);
}
