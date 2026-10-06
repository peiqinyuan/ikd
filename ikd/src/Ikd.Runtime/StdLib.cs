using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

namespace Ikd.Runtime;

public sealed class NativeModule
{
    public string Name { get; }
    public Dictionary<string, Value> Members { get; } = new(StringComparer.Ordinal);

    public NativeModule(string name) => Name = name;
}

/// <summary>内置方法/函数的类型签名（供编译器类型检查使用）。</summary>
public sealed class BuiltinSig
{
    public string Name { get; }
    /// <summary>参数类型名：Int/Float/Str/Bool/Any/List/Map/Fn。</summary>
    public string[] Params { get; }
    public string Return { get; }

    public BuiltinSig(string name, string[] paras, string ret)
    {
        Name = name;
        Params = paras;
        Return = ret;
    }
}

/// <summary>标准库：模块、预置函数、内置方法分发与签名。</summary>
public static class StdLib
{
    private static readonly Dictionary<string, NativeModule> ModuleMap = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IkdNativeFn> NativeMap = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, BuiltinSig[]> SignatureMap = new(StringComparer.Ordinal);

    static StdLib()
    {
        RegisterNatives();
        RegisterModules();
        RegisterSignatures();
    }

    public static IReadOnlyDictionary<string, NativeModule> Modules => ModuleMap;

    public static bool TryGetModule(string path, out NativeModule module)
        => ModuleMap.TryGetValue(path, out module!);

    public static IkdNativeFn? GetNative(string name)
        => NativeMap.TryGetValue(name, out var f) ? f : null;

    public static BuiltinSig[]? GetSignatures(string kind)
        => SignatureMap.TryGetValue(kind, out var s) ? s : null;

    private static void AddNative(string name, int arity, NativeFn fn)
        => NativeMap[name] = new IkdNativeFn(name, arity, fn);

    // ---------------------------------------------------------------
    //  预置全局函数
    // ---------------------------------------------------------------

    private static void RegisterNatives()
    {
        AddNative("print", 1, (_, a) =>
        {
            Console.Write(a[0].ToStr());
            return Value.Null;
        });
        AddNative("println", 1, (_, a) =>
        {
            Console.WriteLine(a[0].ToStr());
            return Value.Null;
        });
        AddNative("readLine", 0, (_, _) =>
        {
            var line = Console.ReadLine();
            return line is null ? Value.Null : Value.OfStr(line);
        });
        AddNative("toStr", 1, (_, a) => Value.OfStr(a[0].ToStr()));
        AddNative("toInt", 1, (_, a) => Value.Of(ToIntOrThrow(a[0])));
        AddNative("toFloat", 1, (_, a) => Value.Of(ToFloatOrThrow(a[0])));
        AddNative("len", 1, (_, a) => Value.Of((long)LengthOf(a[0])));
        AddNative("sizeof", 1, (_, a) => Value.Of(SizeOf(a[0])));
        AddNative("typeOf", 1, (_, a) => Value.OfStr(TypeNameOf(a[0])));
        AddNative("range", 2, (_, a) => Value.OfRef(new IkdRange(a[0].AsLong, a[1].AsLong, 1)));
        AddNative("range3", 3, (_, a) =>
        {
            long step = a[2].AsLong;
            if (step == 0) throw new IkdRuntimeException("ValueError", "range 的步长不能为 0");
            return Value.OfRef(new IkdRange(a[0].AsLong, a[1].AsLong, step));
        });
        AddNative("assert", 1, (_, a) =>
        {
            if (!a[0].Truthy)
                throw new IkdRuntimeException("AssertionError", "断言失败");
            return Value.Null;
        });
        AddNative("assertMsg", 2, (_, a) =>
        {
            if (!a[0].Truthy)
                throw new IkdRuntimeException("AssertionError", a[1].ToStr());
            return Value.Null;
        });
        // List() / List(1, 2, 3)
        AddNative("List", -1, (_, a) => Value.OfRef(new IkdList(a)));
        // Map() / Map("k", v, "k2", v2)
        AddNative("Map", -1, (_, a) =>
        {
            if (a.Length % 2 != 0)
                throw new IkdRuntimeException("TypeError",
                    $"Map 需要偶数个参数（键, 值, 键, 值...），实得 {a.Length} 个");
            var m = new IkdMap();
            for (int i = 0; i < a.Length; i += 2)
            {
                var k = IkdMap.NormalizeKey(a[i]);
                if (k is null)
                    throw new IkdRuntimeException("TypeError", "该值不能作为映射的键");
                m.Pairs[k] = a[i + 1];
            }
            return Value.OfRef(m);
        });
    }

    public static int LengthOf(Value v) => v.Kind switch
    {
        ValueKind.Ref when v.AsRef is IkdString s => s.Value.Length,
        ValueKind.Ref when v.AsRef is IkdList l => l.Items.Count,
        ValueKind.Ref when v.AsRef is IkdMap m => m.Pairs.Count,
        _ => throw new IkdRuntimeException("TypeError", $"len() 不支持类型 {TypeNameOf(v)}"),
    };

    /// <summary>近似内存占用（字节）：标量按值本身大小，引用类型含对象头并按元素累加。</summary>
    public static long SizeOf(Value v) => v.Kind switch
    {
        ValueKind.Null => 0,
        ValueKind.Bool => 1,
        ValueKind.Int => 8,
        ValueKind.Float => 8,
        ValueKind.Ref => v.AsRef switch
        {
            IkdString s => 16 + 2L * s.Value.Length,
            IkdList l => 16 + 16L * l.Items.Count,
            IkdMap m => 16 + 48L * m.Pairs.Count,
            IkdInstance i => 16 + 16L * i.Fields.Length,
            IkdClass c => 48 + 16L * c.FieldCount,
            IkdEnumValue e => 24 + 8L * e.Payload.Length,
            IkdEnumType e => 48 + 8L * e.CaseNames.Length,
            EnumCaseCtor => 32,
            IkdClosure => 48,
            IkdFunction f => 64 + 8L * f.LocalCount,
            IkdNativeFn => 32,
            IkdBoundMethod => 32,
            IkdRange => 24,
            IkdNamespace => 48,
            IkdError => 24,
            _ => 32,
        },
        _ => 16,
    };

    public static string TypeNameOf(Value v) => v.Kind switch
    {
        ValueKind.Null => "Null",
        ValueKind.Bool => "Bool",
        ValueKind.Int => "Int",
        ValueKind.Float => "Float",
        ValueKind.Ref => v.AsRef switch
        {
            IkdString => "Str",
            IkdList => "List",
            IkdMap => "Map",
            IkdInstance i => i.Class.Name,
            IkdClass c => "Class<" + c.Name + ">",
            IkdEnumType e => "Enum<" + e.Name + ">",
            IkdEnumValue e => e.EnumName,
            EnumCaseCtor ec => "Fn",
            IkdClosure => "Fn",
            IkdNativeFn => "Fn",
            IkdBoundMethod => "Fn",
            IkdNamespace ns => "Module<" + ns.Name + ">",
            IkdError => "Error",
            IkdRange => "Range",
            null => "Null",
            _ => "Object",
        },
        _ => "Object",
    };

    public static long ToIntOrThrow(Value v) => v.Kind switch
    {
        ValueKind.Int => v.AsLong,
        ValueKind.Float => (long)Math.Truncate(v.AsDouble),
        ValueKind.Bool => v.AsLong,
        ValueKind.Ref when v.AsRef is IkdString s => long.TryParse(s.Value.Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var r) ? r : throw new IkdRuntimeException(
            "ValueError", $"无法把 \"{s.Value}\" 转成 Int"),
        _ => throw new IkdRuntimeException("TypeError", $"无法把 {TypeNameOf(v)} 转成 Int"),
    };

    public static double ToFloatOrThrow(Value v) => v.Kind switch
    {
        ValueKind.Float => v.AsDouble,
        ValueKind.Int => v.AsLong,
        ValueKind.Bool => v.AsLong,
        ValueKind.Ref when v.AsRef is IkdString s => double.TryParse(s.Value.Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var r) ? r : throw new IkdRuntimeException(
            "ValueError", $"无法把 \"{s.Value}\" 转成 Float"),
        _ => throw new IkdRuntimeException("TypeError", $"无法把 {TypeNameOf(v)} 转成 Float"),
    };

    // ---------------------------------------------------------------
    //  std 模块
    // ---------------------------------------------------------------

    private static void RegisterModules()
    {
        var io = new NativeModule("io");
        io.Members["print"] = Value.OfRef(NativeMap["print"]);
        io.Members["println"] = Value.OfRef(NativeMap["println"]);
        io.Members["readLine"] = Value.OfRef(NativeMap["readLine"]);
        ModuleMap["std.io"] = io;

        var math = new NativeModule("math");
        math.Members["abs"] = MathFn("abs", 1, a => Math.Abs(a[0].AsLong));
        math.Members["absf"] = MathFn("absf", 1, a => Math.Abs(ToFloatOrThrow(a[0])));
        math.Members["min"] = new IkdNativeFn("min", 2, (_, a) =>
            Value.Of(Math.Min(a[0].AsLong, a[1].AsLong)));
        math.Members["max"] = new IkdNativeFn("max", 2, (_, a) =>
            Value.Of(Math.Max(a[0].AsLong, a[1].AsLong)));
        math.Members["minf"] = new IkdNativeFn("minf", 2, (_, a) =>
            Value.Of(Math.Min(ToFloatOrThrow(a[0]), ToFloatOrThrow(a[1]))));
        math.Members["maxf"] = new IkdNativeFn("maxf", 2, (_, a) =>
            Value.Of(Math.Max(ToFloatOrThrow(a[0]), ToFloatOrThrow(a[1]))));
        math.Members["sqrt"] = FloatFn("sqrt", a => Math.Sqrt(a));
        math.Members["pow"] = new IkdNativeFn("pow", 2, (_, a) =>
            Value.Of(Math.Pow(ToFloatOrThrow(a[0]), ToFloatOrThrow(a[1]))));
        math.Members["floor"] = FloatFn("floor", a => Math.Floor(a));
        math.Members["ceil"] = FloatFn("ceil", a => Math.Ceiling(a));
        math.Members["round"] = FloatFn("round", Math.Round);
        math.Members["sin"] = FloatFn("sin", Math.Sin);
        math.Members["cos"] = FloatFn("cos", Math.Cos);
        math.Members["tan"] = FloatFn("tan", Math.Tan);
        math.Members["log"] = FloatFn("log", Math.Log);
        math.Members["exp"] = FloatFn("exp", Math.Exp);
        math.Members["pi"] = Value.Of(Math.PI);
        math.Members["e"] = Value.Of(Math.E);
        math.Members["random"] = new IkdNativeFn("random", 0, (_, _) =>
            Value.Of(Random.Shared.NextDouble()));
        ModuleMap["std.math"] = math;

        var str = new NativeModule("str");
        str.Members["len"] = StrFn("len", 1, s => Value.Of((long)s.Value.Length));
        str.Members["upper"] = StrFn("upper", 1, s => Value.OfStr(s.Value.ToUpperInvariant()));
        str.Members["lower"] = StrFn("lower", 1, s => Value.OfStr(s.Value.ToLowerInvariant()));
        str.Members["trim"] = StrFn("trim", 1, s => Value.OfStr(s.Value.Trim()));
        str.Members["split"] = new IkdNativeFn("split", 2, (_, a) =>
            Value.OfRef(new IkdList(a[0].AsString.Value.Split(a[1].ToStr()).Select(Value.OfStr))));
        str.Members["contains"] = new IkdNativeFn("contains", 2, (_, a) =>
            Value.Of(a[0].AsString.Value.Contains(a[1].ToStr(), StringComparison.Ordinal)));
        str.Members["join"] = new IkdNativeFn("join", 2, (_, a) =>
        {
            if (a[0].AsRef is not IkdList l)
                throw new IkdRuntimeException("TypeError", "str.join 需要 List");
            return Value.OfStr(string.Join(a[1].ToStr(), l.Items.Select(x => x.ToStr())));
        });
        ModuleMap["std.str"] = str;

        var errors = new NativeModule("errors");
        errors.Members["runtimeError"] = new IkdNativeFn("runtimeError", 1, (_, a) =>
            throw new IkdRuntimeException("RuntimeError", a[0].ToStr()));
        errors.Members["valueError"] = new IkdNativeFn("valueError", 1, (_, a) =>
            throw new IkdRuntimeException("ValueError", a[0].ToStr()));
        errors.Members["typeError"] = new IkdNativeFn("typeError", 1, (_, a) =>
            throw new IkdRuntimeException("TypeError", a[0].ToStr()));
        ModuleMap["std.errors"] = errors;

        var list = new NativeModule("list");
        list.Members["new"] = new IkdNativeFn("new", 0, (_, _) => Value.OfRef(new IkdList()));
        list.Members["of"] = new IkdNativeFn("of", 1, (_, a) =>
            Value.OfRef(new IkdList(new[] { a[0] })));
        ModuleMap["std.list"] = list;

        var map = new NativeModule("map");
        map.Members["new"] = new IkdNativeFn("new", 0, (_, _) => Value.OfRef(new IkdMap()));
        ModuleMap["std.map"] = map;

        var http = new NativeModule("http");
        http.Members["request"] = Value.OfRef(new IkdNativeFn("request", -1, (_, a) => HttpRequest(a)));
        ModuleMap["std.http"] = http;
    }

    private static IkdNativeFn MathFn(string name, int arity, Func<Value[], double> f)
        => new(name, arity, (_, a) => Value.Of(f(a)));

    private static IkdNativeFn FloatFn(string name, Func<double, double> f)
        => new(name, 1, (_, a) => Value.Of(f(ToFloatOrThrow(a[0]))));

    private static IkdNativeFn StrFn(string name, int arity, Func<IkdString, Value> f)
        => new(name, arity, (_, a) => f(a[0].AsString));

    // ---------------------------------------------------------------
    //  std.http
    // ---------------------------------------------------------------

    private const int DefaultHttpTimeoutSeconds = 30;

    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        // 每次调用自己带超时，这里不设全局超时
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    });

    /// <summary>
    /// http.request(method, url, body?, headers?, timeoutSeconds?) → Map。
    /// 网络/协议层面的问题不抛异常，而是放进返回值的 error 字段。
    /// </summary>
    private static Value HttpRequest(Value[] args)
    {
        if (args.Length < 2 || args.Length > 5)
            throw new IkdRuntimeException("TypeError",
                "http.request 需要 2~5 个参数: request(method, url, body?, headers?, timeoutSeconds?)");
        if (!args[0].IsStr)
            throw new IkdRuntimeException("TypeError", "request 的 method 必须是 Str");
        if (!args[1].IsStr)
            throw new IkdRuntimeException("TypeError", "request 的 url 必须是 Str");
        if (args.Length >= 3 && !args[2].IsNull && !args[2].IsStr)
            throw new IkdRuntimeException("TypeError", "request 的 body 必须是 Str 或 null");
        if (args.Length >= 4 && !args[3].IsNull && args[3].AsRef is not IkdMap)
            throw new IkdRuntimeException("TypeError", "request 的 headers 必须是 Map");
        if (args.Length >= 5 && !args[4].IsInt)
            throw new IkdRuntimeException("TypeError", "request 的 timeoutSeconds 必须是 Int");

        string method = args[0].AsString.Value.Trim();
        string url = args[1].AsString.Value;

        var res = new IkdMap();
        res.Pairs["method"] = Value.OfStr(method.ToUpperInvariant());
        res.Pairs["url"] = Value.OfStr(url);
        res.Pairs["status"] = Value.Of(0L);
        res.Pairs["ok"] = Value.False;
        res.Pairs["body"] = Value.OfStr("");
        res.Pairs["headers"] = Value.OfRef(new IkdMap());
        res.Pairs["error"] = Value.OfStr("");
        res.Pairs["errorType"] = Value.OfStr("");

        if (method.Length == 0 || method.Any(c => char.IsWhiteSpace(c) || c < 0x21 || c > 0x7E))
            return HttpFail(res, "ValueError", $"非法的 HTTP 方法: \"{method}\"");
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return HttpFail(res, "ValueError", $"URL 必须以 http:// 或 https:// 开头: \"{url}\"");

        bool hasBody = args.Length >= 3 && !args[2].IsNull;
        int timeout = args.Length >= 5
            ? (int)Math.Clamp(args[4].AsLong, 1L, 600L)
            : DefaultHttpTimeoutSeconds;

        try
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), url);
            if (hasBody) req.Content = new StringContent(args[2].AsString.Value, Encoding.UTF8);

            if (args.Length >= 4 && args[3].AsRef is IkdMap hm)
                foreach (var kv in hm.Pairs)
                    ApplyRequestHeader(req, hasBody, kv.Key.ToString() ?? "", kv.Value.ToStr());

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            using var resp = Http.Value
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .GetAwaiter().GetResult();

            res.Pairs["status"] = Value.Of((long)(int)resp.StatusCode);
            res.Pairs["ok"] = Value.Of(resp.IsSuccessStatusCode);

            var rh = new IkdMap();
            foreach (var h in resp.Headers)
                rh.Pairs[h.Key.ToLowerInvariant()] = Value.OfStr(string.Join(", ", h.Value));
            if (resp.Content is not null)
            {
                foreach (var h in resp.Content.Headers)
                    rh.Pairs[h.Key.ToLowerInvariant()] = Value.OfStr(string.Join(", ", h.Value));
                res.Pairs["body"] = Value.OfStr(
                    resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult());
            }
            res.Pairs["headers"] = Value.OfRef(rh);
            return Value.OfRef(res);
        }
        catch (Exception ex)
        {
            Exception e = ex;
            while (e is AggregateException { InnerExceptions.Count: 1 } ag)
                e = ag.InnerExceptions[0];
            if (e is OperationCanceledException)
                return HttpFail(res, "TimeoutError", $"请求超时（{timeout} 秒）: {url}");
            if (e is FormatException && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return HttpFail(res, "ValueError", $"URL 无法解析: {url}");
            return HttpFail(res, e.GetType().Name, e.Message);
        }
    }

    private static Value HttpFail(IkdMap res, string type, string message)
    {
        res.Pairs["error"] = Value.OfStr(message);
        res.Pairs["errorType"] = Value.OfStr(type);
        return Value.OfRef(res);
    }

    /// <summary>请求头按「请求头 → 内容头」顺序尝试，Content-Type 先删后加避免拼接。</summary>
    private static void ApplyRequestHeader(HttpRequestMessage req, bool hasContent,
        string key, string value)
    {
        if (key.Length == 0) return;
        if (req.Headers.TryAddWithoutValidation(key, value)) return;
        if (!hasContent || req.Content is null) return;
        if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
        {
            req.Content.Headers.Remove(key);
            req.Content.Headers.TryAddWithoutValidation(key, value);
            return;
        }
        req.Content.Headers.TryAddWithoutValidation(key, value);
    }

    // ---------------------------------------------------------------
    //  内置方法分发
    // ---------------------------------------------------------------

    /// <summary>按接收者类型查找内置方法。</summary>
    public static IkdNativeFn? GetBuiltinMethod(Value recv, string name)
    {
        if (recv.Kind != ValueKind.Ref) return GetScalarMethod(recv.Kind, name);

        return recv.AsRef switch
        {
            IkdString s => GetStringMethod(s, name),
            IkdList l => GetListMethod(l, name),
            IkdMap m => GetMapMethod(m, name),
            IkdError e => GetErrorMethod(e, name),
            IkdEnumType en => GetEnumTypeMethod(en, name),
            _ => null,
        };
    }

    private static IkdNativeFn? GetScalarMethod(ValueKind kind, string name)
    {
        if (name == "toStr")
            return new IkdNativeFn("toStr", 0, (_, a) => Value.OfStr(a[0].ToStr()));

        if (kind == ValueKind.Int)
        {
            switch (name)
            {
                case "abs": return new IkdNativeFn("abs", 0, (_, a) => Value.Of(Math.Abs(a[0].AsLong)));
                case "toFloat": return new IkdNativeFn("toFloat", 0, (_, a) => Value.Of((double)a[0].AsLong));
                case "min": return new IkdNativeFn("min", 1, (_, a) => Value.Of(Math.Min(a[0].AsLong, a[1].AsLong)));
                case "max": return new IkdNativeFn("max", 1, (_, a) => Value.Of(Math.Max(a[0].AsLong, a[1].AsLong)));
            }
        }
        else if (kind == ValueKind.Float)
        {
            switch (name)
            {
                case "floor": return new IkdNativeFn("floor", 0, (_, a) => Value.Of(Math.Floor(a[0].AsDouble)));
                case "ceil": return new IkdNativeFn("ceil", 0, (_, a) => Value.Of(Math.Ceiling(a[0].AsDouble)));
                case "round": return new IkdNativeFn("round", 0, (_, a) => Value.Of(Math.Round(a[0].AsDouble)));
                case "abs": return new IkdNativeFn("abs", 0, (_, a) => Value.Of(Math.Abs(a[0].AsDouble)));
                case "toInt": return new IkdNativeFn("toInt", 0, (_, a) => Value.Of((long)Math.Truncate(a[0].AsDouble)));
            }
        }
        return null;
    }

    private static IkdNativeFn? GetStringMethod(IkdString s, string name)
    {
        switch (name)
        {
            case "toStr":
            case "clone": return new IkdNativeFn(name, 0, (_, a) => a[0]);
            case "len": return new IkdNativeFn("len", 0, (_, a) => Value.Of((long)a[0].AsString.Value.Length));
            case "upper": return new IkdNativeFn("upper", 0, (_, a) => Value.OfStr(a[0].AsString.Value.ToUpperInvariant()));
            case "lower": return new IkdNativeFn("lower", 0, (_, a) => Value.OfStr(a[0].AsString.Value.ToLowerInvariant()));
            case "trim": return new IkdNativeFn("trim", 0, (_, a) => Value.OfStr(a[0].AsString.Value.Trim()));
            case "isEmpty": return new IkdNativeFn("isEmpty", 0, (_, a) => Value.Of(a[0].AsString.Value.Length == 0));
            case "contains": return new IkdNativeFn("contains", 1, (_, a) =>
                Value.Of(a[0].AsString.Value.Contains(a[1].ToStr(), StringComparison.Ordinal)));
            case "startsWith": return new IkdNativeFn("startsWith", 1, (_, a) =>
                Value.Of(a[0].AsString.Value.StartsWith(a[1].ToStr(), StringComparison.Ordinal)));
            case "endsWith": return new IkdNativeFn("endsWith", 1, (_, a) =>
                Value.Of(a[0].AsString.Value.EndsWith(a[1].ToStr(), StringComparison.Ordinal)));
            case "indexOf": return new IkdNativeFn("indexOf", 1, (_, a) =>
                Value.Of((long)a[0].AsString.Value.IndexOf(a[1].ToStr(), StringComparison.Ordinal)));
            case "replace": return new IkdNativeFn("replace", 2, (_, a) =>
                Value.OfStr(a[0].AsString.Value.Replace(a[1].ToStr(), a[2].ToStr())));
            case "split": return new IkdNativeFn("split", 1, (_, a) =>
                Value.OfRef(new IkdList(a[0].AsString.Value.Split(a[1].ToStr()).Select(Value.OfStr))));
            case "substr": return new IkdNativeFn("substr", 2, (_, a) =>
            {
                var src = a[0].AsString.Value;
                int start = (int)Math.Clamp(a[1].AsLong, 0, src.Length);
                int len = (int)Math.Clamp(a[2].AsLong, 0, src.Length - start);
                return Value.OfStr(src.Substring(start, len));
            });
            case "charAt": return new IkdNativeFn("charAt", 1, (_, a) =>
            {
                var src = a[0].AsString.Value;
                long i = a[1].AsLong;
                if (i < 0 || i >= src.Length)
                    throw new IkdRuntimeException("IndexError", $"下标 {i} 超出范围 (长度 {src.Length})");
                return Value.OfStr(src[(int)i].ToString());
            });
            case "chars": return new IkdNativeFn("chars", 0, (_, a) =>
                Value.OfRef(new IkdList(a[0].AsString.Value.Select(c => Value.OfStr(c.ToString())))));
            case "toInt": return new IkdNativeFn("toInt", 0, (_, a) => Value.Of(ToIntOrThrow(a[0])));
            case "toFloat": return new IkdNativeFn("toFloat", 0, (_, a) => Value.Of(ToFloatOrThrow(a[0])));
            default: return null;
        }
    }

    private static IkdNativeFn? GetListMethod(IkdList l, string name)
    {
        switch (name)
        {
            case "len":
            case "count": return new IkdNativeFn(name, 0, (_, a) => Value.Of((long)((IkdList)a[0].AsRef!).Items.Count));
            case "isEmpty": return new IkdNativeFn("isEmpty", 0, (_, a) => Value.Of(((IkdList)a[0].AsRef!).Items.Count == 0));
            case "push": return new IkdNativeFn("push", 1, (_, a) =>
            {
                ((IkdList)a[0].AsRef!).Items.Add(a[1]);
                return a[0];
            });
            case "pop": return new IkdNativeFn("pop", 0, (_, a) =>
            {
                var list = (IkdList)a[0].AsRef!;
                if (list.Items.Count == 0)
                    throw new IkdRuntimeException("IndexError", "对空列表执行 pop");
                var v = list.Items[^1];
                list.Items.RemoveAt(list.Items.Count - 1);
                return v;
            });
            case "insert": return new IkdNativeFn("insert", 2, (_, a) =>
            {
                var list = (IkdList)a[0].AsRef!;
                long idx = a[1].AsLong;
                if (idx < 0 || idx > list.Items.Count)
                    throw new IkdRuntimeException("IndexError", $"下标 {idx} 超出范围 (长度 {list.Items.Count})");
                list.Items.Insert((int)idx, a[2]);
                return a[0];
            });
            case "removeAt": return new IkdNativeFn("removeAt", 1, (_, a) =>
            {
                var list = (IkdList)a[0].AsRef!;
                long idx = a[1].AsLong;
                if (idx < 0 || idx >= list.Items.Count)
                    throw new IkdRuntimeException("IndexError", $"下标 {idx} 超出范围 (长度 {list.Items.Count})");
                var v = list.Items[(int)idx];
                list.Items.RemoveAt((int)idx);
                return v;
            });
            case "remove": return new IkdNativeFn("remove", 1, (_, a) =>
            {
                var list = (IkdList)a[0].AsRef!;
                return Value.Of(list.Items.Remove(a[1]));
            });
            case "clear": return new IkdNativeFn("clear", 0, (_, a) =>
            {
                ((IkdList)a[0].AsRef!).Items.Clear();
                return Value.Null;
            });
            case "get": return new IkdNativeFn("get", 1, (_, a) =>
            {
                var list = (IkdList)a[0].AsRef!;
                long idx = a[1].AsLong;
                if (idx < 0 || idx >= list.Items.Count)
                    throw new IkdRuntimeException("IndexError", $"下标 {idx} 超出范围 (长度 {list.Items.Count})");
                return list.Items[(int)idx];
            });
            case "set": return new IkdNativeFn("set", 2, (_, a) =>
            {
                var list = (IkdList)a[0].AsRef!;
                long idx = a[1].AsLong;
                if (idx < 0 || idx >= list.Items.Count)
                    throw new IkdRuntimeException("IndexError", $"下标 {idx} 超出范围 (长度 {list.Items.Count})");
                list.Items[(int)idx] = a[2];
                return a[2];
            });
            case "indexOf": return new IkdNativeFn("indexOf", 1, (_, a) =>
            {
                var list = (IkdList)a[0].AsRef!;
                for (int i = 0; i < list.Items.Count; i++)
                    if (ValuesEqual(list.Items[i], a[1])) return Value.Of((long)i);
                return Value.Of(-1L);
            });
            case "contains": return new IkdNativeFn("contains", 1, (_, a) =>
            {
                var list = (IkdList)a[0].AsRef!;
                foreach (var x in list.Items)
                    if (ValuesEqual(x, a[1])) return Value.True;
                return Value.False;
            });
            case "slice": return new IkdNativeFn("slice", 2, (_, a) =>
            {
                var list = (IkdList)a[0].AsRef!;
                int start = (int)Math.Clamp(a[1].AsLong, 0, list.Items.Count);
                int end = (int)Math.Clamp(a[2].AsLong, start, list.Items.Count);
                return Value.OfRef(new IkdList(list.Items.Skip(start).Take(end - start)));
            });
            case "reverse": return new IkdNativeFn("reverse", 0, (_, a) =>
            {
                var list = (IkdList)a[0].AsRef!;
                list.Items.Reverse();
                return a[0];
            });
            case "join": return new IkdNativeFn("join", 1, (_, a) =>
            {
                var list = (IkdList)a[0].AsRef!;
                return Value.OfStr(string.Join(a[1].ToStr(), list.Items.Select(x => x.ToStr())));
            });
            case "first": return new IkdNativeFn("first", 0, (_, a) =>
            {
                var list = (IkdList)a[0].AsRef!;
                if (list.Items.Count == 0)
                    throw new IkdRuntimeException("IndexError", "空列表没有 first");
                return list.Items[0];
            });
            case "last": return new IkdNativeFn("last", 0, (_, a) =>
            {
                var list = (IkdList)a[0].AsRef!;
                if (list.Items.Count == 0)
                    throw new IkdRuntimeException("IndexError", "空列表没有 last");
                return list.Items[^1];
            });
            case "toStr": return new IkdNativeFn("toStr", 0, (_, a) => Value.OfStr(a[0].ToStr()));
            case "sort": return SortList(a: null, ascending: true);
            case "sortDesc": return SortList(a: null, ascending: false);
            case "sortWith": return SortWith();
            case "map": return new IkdNativeFn("map", 1, (vm, a) =>
            {
                var list = (IkdList)a[0].AsRef!;
                var result = new IkdList();
                foreach (var x in list.Items)
                    result.Items.Add(vm.Invoke(a[1], new[] { x }));
                return Value.OfRef(result);
            });
            case "filter": return new IkdNativeFn("filter", 1, (vm, a) =>
            {
                var list = (IkdList)a[0].AsRef!;
                var result = new IkdList();
                foreach (var x in list.Items)
                    if (vm.Invoke(a[1], new[] { x }).Truthy) result.Items.Add(x);
                return Value.OfRef(result);
            });
            case "forEach": return new IkdNativeFn("forEach", 1, (vm, a) =>
            {
                var list = (IkdList)a[0].AsRef!;
                foreach (var x in list.Items) vm.Invoke(a[1], new[] { x });
                return Value.Null;
            });
            case "any": return new IkdNativeFn("any", 1, (vm, a) =>
            {
                var list = (IkdList)a[0].AsRef!;
                foreach (var x in list.Items)
                    if (vm.Invoke(a[1], new[] { x }).Truthy) return Value.True;
                return Value.False;
            });
            case "all": return new IkdNativeFn("all", 1, (vm, a) =>
            {
                var list = (IkdList)a[0].AsRef!;
                foreach (var x in list.Items)
                    if (!vm.Invoke(a[1], new[] { x }).Truthy) return Value.False;
                return Value.True;
            });
            case "fold": return new IkdNativeFn("fold", 2, (vm, a) =>
            {
                var list = (IkdList)a[0].AsRef!;
                var acc = a[1];
                foreach (var x in list.Items)
                    acc = vm.Invoke(a[2], new[] { acc, x });
                return acc;
            });
            case "find": return new IkdNativeFn("find", 1, (vm, a) =>
            {
                var list = (IkdList)a[0].AsRef!;
                foreach (var x in list.Items)
                    if (vm.Invoke(a[1], new[] { x }).Truthy) return x;
                return Value.Null;
            });
            default: return null;
        }
    }

    private static IkdNativeFn SortList(object? a, bool ascending)
        => new(ascending ? "sort" : "sortDesc", 0, (_, args) =>
        {
            var list = (IkdList)args[0].AsRef!;
            list.Items.Sort((x, y) =>
            {
                int c = CompareValues(x, y);
                return ascending ? c : -c;
            });
            return args[0];
        });

    private static IkdNativeFn SortWith()
        => new("sortWith", 1, (vm, args) =>
        {
            var list = (IkdList)args[0].AsRef!;
            var cmp = args[1];
            list.Items.Sort((x, y) =>
            {
                var r = vm.Invoke(cmp, new[] { x, y });
                return (int)(r.Kind == ValueKind.Float ? (long)r.AsDouble : r.AsLong);
            });
            return args[0];
        });

    private static IkdNativeFn? GetMapMethod(IkdMap m, string name)
    {
        switch (name)
        {
            case "len":
            case "count": return new IkdNativeFn(name, 0, (_, a) => Value.Of((long)((IkdMap)a[0].AsRef!).Pairs.Count));
            case "isEmpty": return new IkdNativeFn("isEmpty", 0, (_, a) => Value.Of(((IkdMap)a[0].AsRef!).Pairs.Count == 0));
            case "get": return new IkdNativeFn("get", 1, (_, a) =>
            {
                var map = (IkdMap)a[0].AsRef!;
                var key = IkdMap.NormalizeKey(a[1]);
                return key is not null && map.Pairs.TryGetValue(key, out var v) ? v : Value.Null;
            });
            case "getOr": return new IkdNativeFn("getOr", 2, (_, a) =>
            {
                var map = (IkdMap)a[0].AsRef!;
                var key = IkdMap.NormalizeKey(a[1]);
                return key is not null && map.Pairs.TryGetValue(key, out var v) ? v : a[2];
            });
            case "set": return new IkdNativeFn("set", 2, (_, a) =>
            {
                var map = (IkdMap)a[0].AsRef!;
                var key = IkdMap.NormalizeKey(a[1]);
                if (key is null) throw new IkdRuntimeException("TypeError", "该值不能作为映射的键");
                map.Pairs[key] = a[2];
                return a[0];
            });
            case "has": return new IkdNativeFn("has", 1, (_, a) =>
            {
                var map = (IkdMap)a[0].AsRef!;
                var key = IkdMap.NormalizeKey(a[1]);
                return Value.Of(key is not null && map.Pairs.ContainsKey(key));
            });
            case "remove": return new IkdNativeFn("remove", 1, (_, a) =>
            {
                var map = (IkdMap)a[0].AsRef!;
                var key = IkdMap.NormalizeKey(a[1]);
                return Value.Of(key is not null && map.Pairs.Remove(key));
            });
            case "clear": return new IkdNativeFn("clear", 0, (_, a) =>
            {
                ((IkdMap)a[0].AsRef!).Pairs.Clear();
                return Value.Null;
            });
            case "keys": return new IkdNativeFn("keys", 0, (_, a) =>
                Value.OfRef(new IkdList(((IkdMap)a[0].AsRef!).Pairs.Keys
                    .Select(k => k is string sk ? Value.OfStr(sk) : Value.Of((long)k!)))) );
            case "values": return new IkdNativeFn("values", 0, (_, a) =>
                Value.OfRef(new IkdList(((IkdMap)a[0].AsRef!).Pairs.Values.ToList())));
            case "entries": return new IkdNativeFn("entries", 0, (_, a) =>
            {
                var map = (IkdMap)a[0].AsRef!;
                var result = new IkdList();
                foreach (var kv in map.Pairs)
                {
                    var pair = new IkdList();
                    pair.Items.Add(kv.Key is string sk ? Value.OfStr(sk) : Value.Of((long)kv.Key!));
                    pair.Items.Add(kv.Value);
                    result.Items.Add(Value.OfRef(pair));
                }
                return Value.OfRef(result);
            });
            case "forEach": return new IkdNativeFn("forEach", 1, (vm, a) =>
            {
                var map = (IkdMap)a[0].AsRef!;
                foreach (var kv in map.Pairs.ToList())
                {
                    var key = kv.Key is string sk ? Value.OfStr(sk) : Value.Of((long)kv.Key!);
                    vm.Invoke(a[1], new[] { key, kv.Value });
                }
                return Value.Null;
            });
            case "toStr": return new IkdNativeFn("toStr", 0, (_, a) => Value.OfStr(a[0].ToStr()));
            default: return null;
        }
    }

    private static IkdNativeFn? GetErrorMethod(IkdError e, string name)
    {
        switch (name)
        {
            case "toStr": return new IkdNativeFn("toStr", 0, (_, a) => Value.OfStr(a[0].ToStr()));
            case "toString": return new IkdNativeFn("toString", 0, (_, a) => Value.OfStr(a[0].ToStr()));
            default: return null;
        }
    }

    private static IkdNativeFn? GetEnumTypeMethod(IkdEnumType e, string name)
    {
        switch (name)
        {
            case "cases":
                return new IkdNativeFn("cases", 0, (_, a) =>
                    Value.OfRef(new IkdList(((IkdEnumType)a[0].AsRef!).CaseNames.Select(c => Value.OfStr(c)))));
            default:
                return null;
        }
    }

    public static bool ValuesEqual(Value a, Value b)
    {
        if (a.Kind != b.Kind)
        {
            if ((a.Kind == ValueKind.Int || a.Kind == ValueKind.Float) &&
                (b.Kind == ValueKind.Int || b.Kind == ValueKind.Float))
                return a.Kind == ValueKind.Int ? (double)a.AsLong == b.AsDouble : a.AsDouble == (double)b.AsLong;
            return false;
        }
        return a.Kind switch
        {
            ValueKind.Null => true,
            ValueKind.Bool => a.AsLong == b.AsLong,
            ValueKind.Int => a.AsLong == b.AsLong,
            ValueKind.Float => a.AsDouble == b.AsDouble,
            _ => ReferenceEquals(a.AsRef, b.AsRef) ||
                 (a.AsRef is IkdString sa && b.AsRef is IkdString sb && sa.Value == sb.Value) ||
                 (a.AsRef is IkdEnumValue ea && b.AsRef is IkdEnumValue eb &&
                  ea.EnumName == eb.EnumName && ea.CaseIndex == eb.CaseIndex &&
                  ea.Payload.Length == eb.Payload.Length &&
                  ea.Payload.Zip(eb.Payload).All(p => ValuesEqual(p.First, p.Second))),
        };
    }

    public static int CompareValues(Value a, Value b)
    {
        if (a.IsStr && b.IsStr)
            return string.CompareOrdinal(a.AsString.Value, b.AsString.Value);
        if ((a.Kind == ValueKind.Int || a.Kind == ValueKind.Float) &&
            (b.Kind == ValueKind.Int || b.Kind == ValueKind.Float))
        {
            double x = a.Kind == ValueKind.Int ? a.AsLong : a.AsDouble;
            double y = b.Kind == ValueKind.Int ? b.AsLong : b.AsDouble;
            return x.CompareTo(y);
        }
        if (a.Kind == ValueKind.Bool && b.Kind == ValueKind.Bool)
            return a.AsLong.CompareTo(b.AsLong);
        throw new IkdRuntimeException("TypeError",
            $"无法比较 {TypeNameOf(a)} 与 {TypeNameOf(b)}");
    }

    // ---------------------------------------------------------------
    //  签名表（编译期类型检查）
    // ---------------------------------------------------------------

    private static void RegisterSignatures()
    {
        SignatureMap["Str"] = new[]
        {
            new BuiltinSig("len", Array.Empty<string>(), "Int"),
            new BuiltinSig("upper", Array.Empty<string>(), "Str"),
            new BuiltinSig("lower", Array.Empty<string>(), "Str"),
            new BuiltinSig("trim", Array.Empty<string>(), "Str"),
            new BuiltinSig("isEmpty", Array.Empty<string>(), "Bool"),
            new BuiltinSig("contains", new[] { "Str" }, "Bool"),
            new BuiltinSig("startsWith", new[] { "Str" }, "Bool"),
            new BuiltinSig("endsWith", new[] { "Str" }, "Bool"),
            new BuiltinSig("indexOf", new[] { "Str" }, "Int"),
            new BuiltinSig("replace", new[] { "Str", "Str" }, "Str"),
            new BuiltinSig("split", new[] { "Str" }, "List"),
            new BuiltinSig("substr", new[] { "Int", "Int" }, "Str"),
            new BuiltinSig("charAt", new[] { "Int" }, "Str"),
            new BuiltinSig("chars", Array.Empty<string>(), "List"),
            new BuiltinSig("toInt", Array.Empty<string>(), "Int"),
            new BuiltinSig("toFloat", Array.Empty<string>(), "Float"),
            new BuiltinSig("toStr", Array.Empty<string>(), "Str"),
        };

        SignatureMap["Int"] = new[]
        {
            new BuiltinSig("abs", Array.Empty<string>(), "Int"),
            new BuiltinSig("toFloat", Array.Empty<string>(), "Float"),
            new BuiltinSig("toStr", Array.Empty<string>(), "Str"),
        };

        SignatureMap["Float"] = new[]
        {
            new BuiltinSig("floor", Array.Empty<string>(), "Int"),
            new BuiltinSig("ceil", Array.Empty<string>(), "Int"),
            new BuiltinSig("round", Array.Empty<string>(), "Int"),
            new BuiltinSig("abs", Array.Empty<string>(), "Float"),
            new BuiltinSig("toInt", Array.Empty<string>(), "Int"),
            new BuiltinSig("toStr", Array.Empty<string>(), "Str"),
        };

        SignatureMap["Bool"] = new[]
        {
            new BuiltinSig("toStr", Array.Empty<string>(), "Str"),
        };

        SignatureMap["Null"] = new[]
        {
            new BuiltinSig("toStr", Array.Empty<string>(), "Str"),
        };

        SignatureMap["Any"] = new[]
        {
            new BuiltinSig("toStr", Array.Empty<string>(), "Str"),
        };

        SignatureMap["Error"] = new[]
        {
            new BuiltinSig("toStr", Array.Empty<string>(), "Str"),
        };
    }
}
