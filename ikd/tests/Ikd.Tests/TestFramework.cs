using System.Diagnostics;
using System.Reflection;

namespace Ikd.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute
{
    public string? Name { get; set; }
}

public sealed class AssertFailedException : Exception
{
    public AssertFailedException(string message) : base(message) { }
}

public static class Assert
{
    public static void Equal<T>(T expected, T actual, string? context = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertFailedException(
                $"{context ?? "值不相等"}\n  期望: {Fmt(expected)}\n  实际: {Fmt(actual)}");
    }

    public static void True(bool condition, string? context = null)
    {
        if (!condition) throw new AssertFailedException(context ?? "期望为 true");
    }

    public static void False(bool condition, string? context = null)
    {
        if (condition) throw new AssertFailedException(context ?? "期望为 false");
    }

    public static void Null(object? value, string? context = null)
    {
        if (value is not null) throw new AssertFailedException(context ?? $"期望 null，实际: {Fmt(value)}");
    }

    public static void NotNull(object? value, string? context = null)
    {
        if (value is null) throw new AssertFailedException(context ?? "期望非 null");
    }

    public static void Contains(string expectedSubstring, string? actual, string? context = null)
    {
        if (actual is null || !actual.Contains(expectedSubstring, StringComparison.Ordinal))
            throw new AssertFailedException(
                $"{context ?? "未找到子串"}\n  期望包含: {expectedSubstring}\n  实际: {Fmt(actual)}");
    }

    public static void DoesNotContain(string substring, string? actual, string? context = null)
    {
        if (actual is not null && actual.Contains(substring, StringComparison.Ordinal))
            throw new AssertFailedException($"{context ?? "不应包含子串"}: {substring}\n  实际: {Fmt(actual)}");
    }

    public static TException Throws<TException>(Action action, string? context = null)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            throw new AssertFailedException(
                $"{context ?? "异常类型不符"}\n  期望: {typeof(TException).Name}\n  实际: {ex.GetType().Name}: {ex.Message}");
        }
        throw new AssertFailedException($"{context ?? "未抛出异常"}，期望: {typeof(TException).Name}");
    }

    private static string Fmt(object? value) => value switch
    {
        null => "<null>",
        string s => "\"" + s.Replace("\n", "\\n") + "\"",
        _ => value.ToString() ?? "<null>",
    };
}

public static class TestRunner
{
    public static int RunAll(string[] args)
    {
        string? filter = args.Length > 0 ? args[0] : null;
        var methods = Assembly.GetExecutingAssembly()
            .GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
            .OrderBy(m => m.DeclaringType!.Name)
            .ThenBy(m => m.Name)
            .ToList();

        int passed = 0;
        var failures = new List<(string name, Exception ex)>();
        var sw = Stopwatch.StartNew();

        foreach (var m in methods)
        {
            string name = $"{m.DeclaringType!.Name}.{m.Name}";
            if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                object?[] parms = m.GetParameters().Length == 0
                    ? Array.Empty<object?>()
                    : new object?[] { new TestContext() };
                m.Invoke(null, parms);
                passed++;
            }
            catch (TargetInvocationException tie)
            {
                failures.Add((name, tie.InnerException ?? tie));
            }
            catch (Exception ex)
            {
                failures.Add((name, ex));
            }
        }

        sw.Stop();

        foreach (var (name, ex) in failures)
        {
            Console.WriteLine($"  失败  {name}");
            Console.WriteLine($"        {ex.Message.Replace("\n", "\n        ")}");
            if (ex is not AssertFailedException)
                Console.WriteLine($"        {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
            Console.WriteLine();
        }

        Console.WriteLine($"通过 {passed}，失败 {failures.Count}，用时 {sw.ElapsedMilliseconds}ms");
        return failures.Count == 0 ? 0 : 1;
    }
}

/// <summary>给测试方法注入的上下文（占位，便于将来扩展）。</summary>
public sealed class TestContext
{
}
