using System.Text;

namespace Ikd.Compiler.Diagnostics;

/// <summary>把诊断渲染成带源码行 + 指示符的文本。</summary>
public static class DiagnosticRenderer
{
    private const int TabWidth = 4;

    public static string Render(Diagnostic d, bool color = false)
    {
        var sb = new StringBuilder();
        string sevColor = d.Severity switch
        {
            DiagnosticSeverity.Error => "\x1b[31m",
            DiagnosticSeverity.Warning => "\x1b[33m",
            _ => "\x1b[36m",
        };
        const string reset = "\x1b[0m";
        const string bold = "\x1b[1m";
        const string dim = "\x1b[2m";

        string head = $"{d.Path}({d.Line},{d.Column}): {d.SeverityText}[{d.CodeString}]: {d.Message}";
        if (color) sb.Append(bold).Append(head).Append(reset);
        else sb.Append(head);

        var src = d.Source;
        int line = d.Line;
        string lineText = src.GetLineText(line);
        string gutter = line.ToString();
        string pad = new string(' ', gutter.Length);

        // 展开制表符后的列位置
        int rawCol = d.Column - 1;
        int displayCol = DisplayColumn(lineText, rawCol);
        int spanLen = Math.Max(1, Math.Min(DisplayColumn(lineText, Math.Min(d.Span.End - 1, lineText.Length)) - displayCol + 1, 200));

        sb.AppendLine();
        sb.Append(pad).Append(" |");
        if (color) sb.Append(dim).Append(pad).Append(" |").Append(reset);
        sb.AppendLine();
        if (color) sb.Append(dim).Append(gutter).Append(" | ").Append(reset);
        else sb.Append(gutter).Append(" | ");
        sb.Append(ExpandTabs(lineText));
        sb.AppendLine();
        sb.Append(pad).Append(" | ");
        string caretPad = new string(' ', displayCol);
        string caret = new string('^', spanLen);
        if (color) sb.Append(caretPad).Append("\x1b[32m").Append(caret).Append(reset);
        else sb.Append(caretPad).Append(caret);

        if (!string.IsNullOrEmpty(d.Help))
        {
            sb.AppendLine();
            sb.Append(pad).Append(" = 提示: ").Append(d.Help);
        }

        return sb.ToString();
    }

    public static string RenderAll(IEnumerable<Diagnostic> diagnostics, bool color = false)
    {
        var sb = new StringBuilder();
        foreach (var d in diagnostics)
        {
            sb.AppendLine(Render(d, color));
        }
        return sb.ToString();
    }

    private static int DisplayColumn(string line, int rawIndex)
    {
        int col = 0;
        for (int i = 0; i < rawIndex && i < line.Length; i++)
        {
            col = line[i] == '\t' ? col + TabWidth - (col % TabWidth) : col + 1;
        }
        return col;
    }

    private static string ExpandTabs(string line)
    {
        if (!line.Contains('\t')) return line;
        var sb = new StringBuilder(line.Length + 8);
        int col = 0;
        foreach (char c in line)
        {
            if (c == '\t')
            {
                int spaces = TabWidth - (col % TabWidth);
                sb.Append(' ', spaces);
                col += spaces;
            }
            else
            {
                sb.Append(c);
                col++;
            }
        }
        return sb.ToString();
    }
}
