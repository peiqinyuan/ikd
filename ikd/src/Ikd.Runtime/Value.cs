using System.Globalization;

namespace Ikd.Runtime;

public enum ValueKind : byte
{
    Null = 0,
    Bool = 1,
    Int = 2,
    Float = 3,
    Ref = 4,
}

/// <summary>ikd 的运行时值（tagged union，避免装箱）。</summary>
public struct Value
{
    public ValueKind Kind;
    public long AsLong;      // Int / Bool(0/1)
    public double AsDouble;
    public object? AsRef;

    public static readonly Value Null = new() { Kind = ValueKind.Null };
    public static readonly Value True = new() { Kind = ValueKind.Bool, AsLong = 1 };
    public static readonly Value False = new() { Kind = ValueKind.Bool, AsLong = 0 };

    public static Value Of(long v) => new() { Kind = ValueKind.Int, AsLong = v };
    public static Value Of(double v) => new() { Kind = ValueKind.Float, AsDouble = v };
    public static Value Of(bool v) => new() { Kind = ValueKind.Bool, AsLong = v ? 1 : 0 };
    public static Value OfStr(string s) => new() { Kind = ValueKind.Ref, AsRef = new IkdString(s) };
    public static Value OfRef(object? o) => o is null ? Null : new Value { Kind = ValueKind.Ref, AsRef = o };

    public static implicit operator Value(IkdNativeFn fn) => OfRef(fn);

    public bool IsNull => Kind == ValueKind.Null;
    public bool IsBool => Kind == ValueKind.Bool;
    public bool IsInt => Kind == ValueKind.Int;
    public bool IsFloat => Kind == ValueKind.Float;
    public bool IsStr => Kind == ValueKind.Ref && AsRef is IkdString;
    public bool IsRef => Kind == ValueKind.Ref;

    /// <summary>条件判断：null/false 为假，其余为真。</summary>
    public bool Truthy => Kind switch
    {
        ValueKind.Null => false,
        ValueKind.Bool => AsLong != 0,
        _ => true,
    };

    public IkdString AsString => (IkdString)AsRef!;

    public override string ToString() => Kind switch
    {
        ValueKind.Null => "null",
        ValueKind.Bool => AsLong != 0 ? "true" : "false",
        ValueKind.Int => AsLong.ToString(CultureInfo.InvariantCulture),
        ValueKind.Float => FormatDouble(AsDouble),
        _ => RefToString(AsRef),
    };

    private static string RefToString(object? o) => o switch
    {
        null => "null",
        IkdString s => s.Value,
        IkdInstance i => i.Class.Name + " {...}",
        IkdClass c => "class " + c.Name,
        IkdClosure f => "fn " + f.Proto.Name,
        IkdNativeFn nf => "fn " + nf.Name,
        IkdList l => ListToString(l),
        IkdMap m => MapToString(m),
        IkdEnumValue e => EnumToString(e),
        IkdNamespace ns => "module " + ns.Name,
        IkdBoundMethod b => "fn " + b.Name,
        Cell c => c.Value.ToString()!,
        _ => o.ToString() ?? "null",
    };

    private static string ListToString(IkdList l)
    {
        var parts = new string[l.Items.Count];
        for (int i = 0; i < parts.Length; i++) parts[i] = l.Items[i].ToDisplayString();
        return "[" + string.Join(", ", parts) + "]";
    }

    private static string MapToString(IkdMap m)
    {
        var parts = new List<string>(m.Pairs.Count);
        foreach (var kv in m.Pairs)
            parts.Add(KeyToString(kv.Key) + ": " + kv.Value.ToDisplayString());
        return "{" + string.Join(", ", parts) + "}";
    }

    private static string EnumToString(IkdEnumValue e)
    {
        if (e.Payload.Length == 0) return e.EnumName + "." + e.CaseName;
        var parts = new string[e.Payload.Length];
        for (int i = 0; i < parts.Length; i++) parts[i] = e.Payload[i].ToDisplayString();
        return e.EnumName + "." + e.CaseName + "(" + string.Join(", ", parts) + ")";
    }

    private static string KeyToString(object k) => k switch
    {
        string s => "\"" + s + "\"",
        bool b => b ? "true" : "false",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => FormatDouble(d),
        _ => k.ToString() ?? "null",
    };

    public static string FormatDouble(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsPositiveInfinity(d)) return "inf";
        if (double.IsNegativeInfinity(d)) return "-inf";
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
            return d.ToString("0.0", CultureInfo.InvariantCulture);
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>输出给用户：Str 返回原文。</summary>
    public string ToDisplayString()
    {
        if (Kind == ValueKind.Ref && AsRef is IkdString s) return s.Value;
        return ToString();
    }

    /// <summary>插值 / toStr 使用。</summary>
    public string ToStr() => ToDisplayString();
}
