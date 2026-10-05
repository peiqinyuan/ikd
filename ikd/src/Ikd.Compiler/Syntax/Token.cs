using Ikd.Compiler.Diagnostics;

namespace Ikd.Compiler.Syntax;

public readonly struct Token
{
    public TokenKind Kind { get; }
    public int Start { get; }
    public int Length { get; }
    /// <summary>标识符文本 / 数值 / InterpolatedString。</summary>
    public object? Value { get; }

    public Token(TokenKind kind, int start, int length, object? value = null)
    {
        Kind = kind;
        Start = start;
        Length = length;
        Value = value;
    }

    public int End => Start + Length;
    public TextSpan Span => new(Start, Length);

    public static Token Eof(int position) => new(TokenKind.EndOfFile, position, 0);

    public string Text(SourceText source)
        => Start >= 0 && End <= source.Text.Length
            ? source.Text.Substring(Start, Length)
            : "";

    public override string ToString() => $"{Kind}@{Start}+{Length}";
}

/// <summary>字符串字面量的组成部分。</summary>
public sealed class InterpolatedString
{
    public List<StringPart> Parts { get; } = new();

    public bool IsPlain => Parts.Count == 1 && !Parts[0].IsExpression;

    /// <summary>无插值时的纯文本。</summary>
    public string PlainText => Parts.Count == 0 ? "" : Parts[0].Text ?? "";
}

public struct StringPart
{
    public bool IsExpression;
    /// <summary>字面量部分的文本（已解转义）；表达式部分为 null。</summary>
    public string? Text;
    /// <summary>表达式部分在源文件中的绝对范围。</summary>
    public int ExprStart;
    public int ExprEnd;

    public static StringPart Literal(string text) => new() { IsExpression = false, Text = text };
    public static StringPart Expr(int start, int end) => new() { IsExpression = true, ExprStart = start, ExprEnd = end };
}
