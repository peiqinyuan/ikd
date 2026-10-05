using System.Collections;

namespace Ikd.Compiler.Diagnostics;

/// <summary>诊断收集器。编译一个文件前先 <see cref="SetSource"/>。</summary>
public sealed class DiagnosticBag : IEnumerable<Diagnostic>
{
    private readonly List<Diagnostic> _items = new();
    private SourceText? _current;

    public IReadOnlyList<Diagnostic> Items => _items;
    public bool HasErrors { get; private set; }
    public int ErrorCount { get; private set; }
    public int WarningCount { get; private set; }

    public void SetSource(SourceText source) => _current = source;

    public Diagnostic? Report(ErrorCode code, TextSpan span, string message, string? help = null)
        => Report(DiagnosticSeverity.Error, code, span, message, help);

    public Diagnostic? Report(DiagnosticSeverity severity, ErrorCode code, TextSpan span,
        string message, string? help = null)
    {
        var source = _current ?? throw new InvalidOperationException("DiagnosticBag 尚未设置 SourceText。");
        return Report(severity, code, source, span, message, help);
    }

    public Diagnostic? Report(DiagnosticSeverity severity, ErrorCode code, SourceText source,
        TextSpan span, string message, string? help = null)
    {
        // 同位置同码只记一次（避免级联报错）
        foreach (var d in _items)
        {
            if (d.Code == code && d.Span.Start == span.Start && d.Span.Length == span.Length
                && ReferenceEquals(d.Source, source))
                return null;
        }

        var diag = new Diagnostic(severity, code, message, source, span, help);
        _items.Add(diag);
        if (severity == DiagnosticSeverity.Error) { HasErrors = true; ErrorCount++; }
        else if (severity == DiagnosticSeverity.Warning) WarningCount++;
        return diag;
    }

    public void AddRange(IEnumerable<Diagnostic> items)
    {
        foreach (var d in items)
        {
            _items.Add(d);
            if (d.Severity == DiagnosticSeverity.Error) { HasErrors = true; ErrorCount++; }
            else if (d.Severity == DiagnosticSeverity.Warning) WarningCount++;
        }
    }

    public IEnumerator<Diagnostic> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
}
