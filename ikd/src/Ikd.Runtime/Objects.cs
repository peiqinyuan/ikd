namespace Ikd.Runtime;

/// <summary>ikd 的通用受控异常（编译器/运行时共用）。</summary>
public sealed class IkdException : Exception
{
    public IkdException(string message) : base(message) { }
}

/// <summary>所有引用类型值的基类。</summary>
public abstract class IkdObject
{
}

/// <summary>堆上的可变单元：被闭包捕获的变量。</summary>
public sealed class Cell : IkdObject
{
    public Value Value;
    public Cell(Value v) => Value = v;
}

public sealed class IkdString : IkdObject
{
    public string Value { get; }
    public IkdString(string v) => Value = v;
    public override string ToString() => Value;
}

public sealed class IkdList : IkdObject
{
    public List<Value> Items { get; } = new();
    public IkdList() { }
    public IkdList(IEnumerable<Value> items) => Items.AddRange(items);
    public int Count => Items.Count;
}

public sealed class IkdMap : IkdObject
{
    public Dictionary<object, Value> Pairs { get; } = new();
    public int Count => Pairs.Count;

    public static object? NormalizeKey(Value k) => k.Kind switch
    {
        ValueKind.Int => k.AsLong,
        ValueKind.Bool => k.AsLong != 0,
        ValueKind.Float => k.AsDouble,
        ValueKind.Null => null,
        ValueKind.Ref when k.AsRef is IkdString s => s.Value,
        _ => null,
    };
}

public sealed class IkdClass : IkdObject
{
    public string Name { get; }
    public IkdClass? Base { get; set; }
    public HashSet<int> InterfaceIds { get; } = new();
    public string[] FieldNames { get; set; } = Array.Empty<string>();
    public int FieldCount { get; set; }
    public Dictionary<string, int> MethodIndex { get; } = new(StringComparer.Ordinal);
    public IkdFunction[] Methods { get; set; } = Array.Empty<IkdFunction>();
    public Dictionary<string, Value> Statics { get; } = new(StringComparer.Ordinal);
    public bool IsAbstract { get; set; }
    public int ClassId { get; set; }

    public IkdClass(string name) => Name = name;

    public bool TryGetMethod(string name, out IkdFunction fn)
    {
        if (MethodIndex.TryGetValue(name, out int i))
        {
            fn = Methods[i];
            return true;
        }
        fn = null!;
        return false;
    }

    public bool IsSubclassOf(IkdClass other)
    {
        for (var c = this; c is not null; c = c.Base)
            if (c == other) return true;
        return false;
    }
}

public sealed class IkdInstance : IkdObject
{
    public IkdClass Class { get; }
    public Value[] Fields { get; }

    public IkdInstance(IkdClass cls)
    {
        Class = cls;
        Fields = new Value[cls.FieldCount];
        for (int i = 0; i < Fields.Length; i++) Fields[i] = Value.Null;
    }
}

/// <summary>函数原型（编译期产物，运行期共享）。</summary>
public sealed class IkdFunction
{
    public string Name { get; }
    public int Arity { get; }
    public byte[] Code { get; }
    public Value[] Constants { get; }
    public int LocalCount { get; }
    public int MaxStack { get; }
    /// <summary>指令偏移到源码行号的断点表（升序）。</summary>
    public int[] LineIps { get; }
    public int[] LineNums { get; }
    public UpvalueDesc[] Upvalues { get; }
    public string ModulePath { get; }
    public bool IsMethod { get; }
    public string?[] ParamNames { get; }
    /// <summary>加载后绑定到所属运行时模块。</summary>
    public RuntimeModule? Module { get; set; }

    public IkdFunction(string name, int arity, byte[] code, Value[] constants,
        int localCount, int maxStack, int[] lineIps, int[] lineNums,
        UpvalueDesc[] upvalues, string modulePath, bool isMethod, string?[] paramNames)
    {
        Name = name;
        Arity = arity;
        Code = code;
        Constants = constants;
        LocalCount = localCount;
        MaxStack = maxStack;
        LineIps = lineIps;
        LineNums = lineNums;
        Upvalues = upvalues;
        ModulePath = modulePath;
        IsMethod = isMethod;
        ParamNames = paramNames;
    }

    public int GetLine(int ip)
    {
        if (LineNums.Length == 0) return 0;
        int lo = 0, hi = LineNums.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (LineIps[mid] <= ip) lo = mid;
            else hi = mid - 1;
        }
        return LineNums[lo];
    }

    public override string ToString() => $"fn {Name}/{Arity}";
}

/// <summary>闭包：函数原型 + 捕获的堆单元。</summary>
public sealed class IkdClosure : IkdObject
{
    public IkdFunction Proto { get; }
    public Value[] Cells { get; }

    public IkdClosure(IkdFunction proto, Value[] cells)
    {
        Proto = proto;
        Cells = cells;
    }

    public override string ToString() => $"fn {Proto.Name}";
}

/// <summary>绑定到接收者的方法值。</summary>
public sealed class IkdBoundMethod : IkdObject
{
    public object Receiver { get; }
    public IkdFunction Fn { get; }
    public string Name => Fn.Name;
    /// <summary>惰性创建的闭包缓存。</summary>
    public IkdClosure? Cached { get; set; }

    public IkdBoundMethod(object receiver, IkdFunction fn)
    {
        Receiver = receiver;
        Fn = fn;
    }
}

/// <summary>模块命名空间（import 得到的对象）。</summary>
public sealed class IkdNamespace : IkdObject
{
    public string Name { get; }
    public Dictionary<string, Value> Members { get; } = new(StringComparer.Ordinal);
    /// <summary>绑定的运行时模块（用户模块导出的实时视图）。</summary>
    public RuntimeModule? LiveModule { get; set; }

    public IkdNamespace(string name) => Name = name;

    /// <summary>先查预置成员，再按 GlobalNames 索引读模块全局（解包 Cell）。</summary>
    public bool TryGetMember(string name, out Value value)
    {
        if (Members.TryGetValue(name, out value)) return true;
        var m = LiveModule;
        if (m is not null)
        {
            var names = m.Image.GlobalNames;
            for (int i = 0; i < names.Length; i++)
                if (names[i] == name)
                {
                    var g = m.Globals[i];
                    value = g.AsRef is Cell c ? c.Value : g;
                    return true;
                }
        }
        value = Value.Null;
        return false;
    }
}

/// <summary>枚举值。</summary>
public sealed class IkdEnumValue : IkdObject
{
    public string EnumName { get; }
    public string CaseName { get; }
    public Value[] Payload { get; }
    public int CaseIndex { get; }

    public IkdEnumValue(string enumName, string caseName, int caseIndex, Value[] payload)
    {
        EnumName = enumName;
        CaseName = caseName;
        CaseIndex = caseIndex;
        Payload = payload;
    }
}

/// <summary>枚举类型对象（如 Tree.Leaf 这种「构造器」）。</summary>
public sealed class IkdEnumType : IkdObject
{
    public string Name { get; }
    public string[] CaseNames { get; }
    public int[] CaseArities { get; }

    public IkdEnumType(string name, string[] caseNames, int[] caseArities)
    {
        Name = name;
        CaseNames = caseNames;
        CaseArities = caseArities;
    }

    public int FindCase(string name)
    {
        int i = Array.IndexOf(CaseNames, name);
        return i;
    }

    public override string ToString() => "enum " + Name;
}

/// <summary>带载荷枚举成员的构造器值（如 Tree.Leaf，调用后得到 IkdEnumValue）。</summary>
public sealed class EnumCaseCtor : IkdObject
{
    public string EnumName { get; }
    public string CaseName { get; }
    public int CaseIndex { get; }
    public int Arity { get; }

    public EnumCaseCtor(string enumName, string caseName, int caseIndex, int arity)
    {
        EnumName = enumName;
        CaseName = caseName;
        CaseIndex = caseIndex;
        Arity = arity;
    }

    public override string ToString() => EnumName + "." + CaseName;
}

/// <summary>原生函数。</summary>
public delegate Value NativeFn(Vm vm, Value[] args);

public sealed class IkdNativeFn : IkdObject
{
    public string Name { get; }
    public int Arity { get; }
    public NativeFn Invoke { get; }

    public IkdNativeFn(string name, int arity, NativeFn invoke)
    {
        Name = name;
        Arity = arity;
        Invoke = invoke;
    }

    public override string ToString() => $"fn {Name}";
}

/// <summary>运行时错误对象（原生函数抛出，可用 catch 捕获）。</summary>
public sealed class IkdError : IkdObject
{
    public string Kind { get; }
    public string Message { get; }

    public IkdError(string kind, string message)
    {
        Kind = kind;
        Message = message;
    }

    public override string ToString() => Kind + ": " + Message;
}

/// <summary>VM 抛出的内部异常，携带一个 ikd 值作为错误对象。</summary>
public sealed class IkdRuntimeException : Exception
{
    public Value Error { get; }

    public IkdRuntimeException(Value error, string? fallback = null)
        : base(fallback ?? error.ToDisplayString())
    {
        Error = error;
    }

    public IkdRuntimeException(string kind, string message)
        : base(message)
    {
        Error = Value.OfRef(new IkdError(kind, message));
    }
}

/// <summary>内置可迭代对象（range 等）。</summary>
public abstract class IkdIterable : IkdObject
{
    public abstract bool TryNext(out Value value);
}
public sealed class IkdRange : IkdIterable
{
    private long _current;
    private readonly long _end;
    private readonly long _step;

    public IkdRange(long start, long end, long step)
    {
        _current = start;
        _end = end;
        _step = step;
    }

    public override bool TryNext(out Value value)
    {
        if (_step > 0 ? _current < _end : _current > _end)
        {
            value = Value.Of(_current);
            _current += _step;
            return true;
        }
        value = Value.Null;
        return false;
    }
}
