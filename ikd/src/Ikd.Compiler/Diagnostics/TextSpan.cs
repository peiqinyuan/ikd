namespace Ikd.Compiler.Diagnostics;

/// <summary>源码区间 [Start, Start+Length)。</summary>
public readonly struct TextSpan : IEquatable<TextSpan>
{
    public int Start { get; }
    public int Length { get; }

    public TextSpan(int start, int length)
    {
        Start = start;
        Length = length < 0 ? 0 : length;
    }

    public int End => Start + Length;

    public static readonly TextSpan Empty = new(0, 0);

    public bool Contains(int offset) => offset >= Start && offset < End;

    public TextSpan Union(TextSpan other)
    {
        if (other.Length == 0) return this;
        if (Length == 0) return other;
        int s = Math.Min(Start, other.Start);
        int e = Math.Max(End, other.End);
        return new TextSpan(s, e - s);
    }

    public bool Equals(TextSpan other) => Start == other.Start && Length == other.Length;
    public override bool Equals(object? obj) => obj is TextSpan t && Equals(t);
    public override int GetHashCode() => HashCode.Combine(Start, Length);
    public override string ToString() => $"{Start}..{End}";
}
