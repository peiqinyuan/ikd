namespace Ikd.Compiler.Diagnostics;

/// <summary>源文件文本 + 行列定位。</summary>
public sealed class SourceText
{
    public string Text { get; }
    public string Path { get; }
    private readonly int[] _lineStarts;

    private SourceText(string text, string path)
    {
        Text = text;
        Path = path;
        _lineStarts = BuildLineStarts(text);
    }

    public static SourceText From(string text, string path = "<输入>")
        => new(text ?? "", string.IsNullOrEmpty(path) ? "<输入>" : path);

    public static SourceText FromFile(string path)
        => new(File.ReadAllText(path), path);

    public int Length => Text.Length;
    public int LineCount => _lineStarts.Length;

    private static int[] BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n')
            {
                starts.Add(i + 1);
            }
            else if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
        }
        return starts.ToArray();
    }

    /// <summary>偏移量 → 1 基行号。</summary>
    public int GetLine(int offset)
    {
        if (offset < 0) offset = 0;
        if (offset > Text.Length) offset = Text.Length;
        int lo = 0, hi = _lineStarts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_lineStarts[mid] <= offset) lo = mid;
            else hi = mid - 1;
        }
        return lo + 1;
    }

    /// <summary>偏移量 → 1 基列号（按 UTF-16 码元计）。</summary>
    public int GetColumn(int offset)
    {
        if (offset < 0) offset = 0;
        if (offset > Text.Length) offset = Text.Length;
        int line = GetLine(offset);
        return offset - _lineStarts[line - 1] + 1;
    }

    public string GetLineText(int line)
    {
        int start = _lineStarts[line - 1];
        int end = line < _lineStarts.Length ? _lineStarts[line] : Text.Length;
        // 去掉行终止符
        while (end > start && (Text[end - 1] == '\n' || Text[end - 1] == '\r')) end--;
        return Text.Substring(start, end - start);
    }

    public TextSpan GetLineSpan(int line)
    {
        int start = _lineStarts[line - 1];
        int end = line < _lineStarts.Length ? _lineStarts[line] : Text.Length;
        return new TextSpan(start, end - start);
    }
}
