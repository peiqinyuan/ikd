namespace Ikd.Compiler.Diagnostics;

public enum DiagnosticSeverity
{
    Error,
    Warning,
    Note,
}

public sealed class Diagnostic
{
    public DiagnosticSeverity Severity { get; }
    public ErrorCode Code { get; }
    public string Message { get; }
    public SourceText Source { get; }
    public TextSpan Span { get; }
    public string? Help { get; }

    public Diagnostic(DiagnosticSeverity severity, ErrorCode code, string message,
        SourceText source, TextSpan span, string? help = null)
    {
        Severity = severity;
        Code = code;
        Message = message;
        Source = source;
        Span = span;
        Help = help;
    }

    public int Line => Source.GetLine(Span.Start);
    public int Column => Source.GetColumn(Span.Start);
    public string Path => Source.Path;
    public string CodeString => Code.ToCodeString();

    public string SeverityText => Severity switch
    {
        DiagnosticSeverity.Error => "错误",
        DiagnosticSeverity.Warning => "警告",
        _ => "提示",
    };

    public override string ToString()
        => $"{Path}({Line},{Column}): {SeverityText}[{CodeString}]: {Message}";
}
