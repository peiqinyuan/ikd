using Ikd.Compiler.Diagnostics;
using Ikd.Compiler.Syntax;
using Ikd.Runtime;

namespace Ikd.Compiler.Emit;

// ===================================================================
//  符号 / 作用域 / 上下文
// ===================================================================

internal enum SymKind { Local, Global }

internal sealed class Symbol
{
    public string Name = "";
    public SymKind Kind;
    public int Index;
    public bool IsConst;
}

internal sealed class Scope
{
    public Scope? Parent;
    public readonly Dictionary<string, Symbol> Vars = new(StringComparer.Ordinal);

    public Symbol? FindLocal(string name)
        => Vars.TryGetValue(name, out var s) ? s : null;
}

internal enum TypeSymKind { Class, Enum, Interface }

internal sealed class TypeSymbol
{
    public string Name = "";
    public TypeSymKind Kind;
    public int ClassIndex = -1;
    public int EnumIndex = -1;
    public int InterfaceId = -1;
    public ClassDecl? Class;
    public EnumDecl? EnumDecl;
    public InterfaceDecl? Interface;
    public readonly List<string> FieldNames = new();
    public readonly HashSet<int> InterfaceIds = new();
    public readonly List<(string Name, int Arity)> Methods = new();
    public bool HasInit;
    /// <summary>本类链上的基类（FillClass 按继承深度排序后填充）。</summary>
    public TypeSymbol? Base;
    /// <summary>本类自己声明的 init 形参数；-1 表示没有。</summary>
    public int OwnInitArity = -1;
    /// <summary>本类或基类链上存在 init（用户或合成）。</summary>
    public bool HasAnyInit;
    /// <summary>本类或基类链上存在带默认值的实例字段。</summary>
    public bool HasAnyDefaults;
    /// <summary>本类自己声明的带默认值实例字段（触发合成 init）。</summary>
    public bool OwnDefaults;
}

internal sealed class LoopCtx
{
    public int ContinueTarget;
    public int TryDepth;
    public readonly List<int> BreakJumps = new();
    public readonly List<int> ContinueJumps = new();
}

internal sealed class TryCtx
{
    public bool EntryActive;
    public BlockStmt? Finally;
    public bool InFinally;
}

internal sealed class FnCtx
{
    public string Name = "";
    public int Arity;
    public bool IsMethod;
    public bool IsModuleInit;
    public string ModulePath = "";
    public FnCtx? Parent;
    public readonly List<byte> Code = new();
    public readonly List<Value> Constants = new();
    public readonly Dictionary<string, int> ConstMap = new(StringComparer.Ordinal);
    public readonly List<UpvalueDesc> Upvalues = new();
    public int LocalCount;
    public int Depth;
    public int MaxDepth;
    public Scope Scope = new();
    public readonly List<(int Ip, int Line)> Lines = new();
    public readonly List<LoopCtx> Loops = new();
    public readonly List<TryCtx> Tries = new();
    public readonly HashSet<string> TypeParams = new(StringComparer.Ordinal);
        public string[] ParamNames = Array.Empty<string>();
}

internal sealed class ProgramState
{
    public readonly Dictionary<string, int> Interfaces = new(StringComparer.Ordinal);
    public readonly List<InterfaceImage> InterfaceList = new();
    public readonly List<string> NativeNames = new();
    public readonly Dictionary<string, int> NativeIndex = new(StringComparer.Ordinal);
}

// ===================================================================
//  发射器（同时承担名称解析）
// ===================================================================

/// <summary>把一个编译单元发射成 <see cref="ModuleImage"/>。</summary>
public sealed partial class Emitter
{
    private static readonly Dictionary<string, string> Prelude = new(StringComparer.Ordinal)
    {
        ["print"] = "print",
        ["println"] = "println",
        ["readLine"] = "readLine",
        ["toStr"] = "toStr",
        ["toInt"] = "toInt",
        ["toFloat"] = "toFloat",
        ["len"] = "len",
        ["typeOf"] = "typeOf",
        ["range"] = "range",
        ["assert"] = "assert",
    };

    private readonly ProgramState _prog;
    private readonly DiagnosticBag _bag;
    private readonly CompilationUnit _unit;
    private readonly string _path;
    private readonly string _name;

    private readonly List<Value> _constants = new();
    private readonly Dictionary<string, int> _constMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _globals = new(StringComparer.Ordinal);
    private readonly List<string> _globalNames = new();
    private readonly List<string> _exports = new();
    private readonly List<string> _imports = new();
    private readonly Dictionary<string, int> _importMap = new(StringComparer.Ordinal);
    private readonly List<IkdFunction?> _functions = new();
    private readonly List<ClassImage> _classes = new();
    private readonly List<EnumImage> _enums = new();
    private readonly List<TypeDesc> _typeDescs = new();
    private readonly Dictionary<string, TypeSymbol> _types = new(StringComparer.Ordinal);
    private readonly Scope _globalscope = new();

    private FnCtx? _ctx;
    private ClassDecl? _currentClass;

    internal Emitter(ProgramState prog, DiagnosticBag bag, CompilationUnit unit,
        string path, string name)
    {
        _prog = prog;
        _bag = bag;
        _unit = unit;
        _path = path;
        _name = name;
    }

    // ----------------------------------------------------------------
    //  低层发射
    // ----------------------------------------------------------------

    private void D(int delta)
    {
        _ctx!.Depth += delta;
        if (_ctx.Depth > _ctx.MaxDepth) _ctx.MaxDepth = _ctx.Depth;
        if (_ctx.Depth < 0)
        {
            _ctx.Depth = 0;
            Internal("栈深度为负");
        }
    }

    private void SetDepth(int d)
    {
        _ctx!.Depth = d;
        if (d > _ctx.MaxDepth) _ctx.MaxDepth = d;
    }

    private void Internal(string message)
        => _bag.Report(ErrorCode.UnknownError, TextSpan.Empty, "编译器内部错误: " + message);

    private void Emit(Op op) => _ctx!.Code.Add((byte)op);
    private void U8(int v) => _ctx!.Code.Add((byte)v);

    private void U16(int v)
    {
        _ctx!.Code.Add((byte)(v & 0xff));
        _ctx!.Code.Add((byte)((v >> 8) & 0xff));
    }

    private void U32(int v)
    {
        _ctx!.Code.Add((byte)(v & 0xff));
        _ctx!.Code.Add((byte)((v >> 8) & 0xff));
        _ctx!.Code.Add((byte)((v >> 16) & 0xff));
        _ctx!.Code.Add((byte)((v >> 24) & 0xff));
    }

    private int EmitJump(Op op)
    {
        Emit(op);
        int pos = _ctx!.Code.Count;
        U32(0);
        return pos;
    }

    private void Patch(int pos) => PatchTo(pos, _ctx!.Code.Count);

    private void PatchTo(int pos, int target)
    {
        var code = _ctx!.Code;
        code[pos] = (byte)(target & 0xff);
        code[pos + 1] = (byte)((target >> 8) & 0xff);
        code[pos + 2] = (byte)((target >> 16) & 0xff);
        code[pos + 3] = (byte)((target >> 24) & 0xff);
    }

    private void MarkLine(SyntaxNode node)
    {
        int line = _unit.Source.GetLine(node.Span.Start);
        var lines = _ctx!.Lines;
        if (lines.Count > 0 && lines[^1].Ip == _ctx.Code.Count && lines[^1].Line == line) return;
        lines.Add((_ctx.Code.Count, line));
    }

    private void Push(Op op) { Emit(op); D(1); }
    private void PushU16(Op op, int a) { Emit(op); U16(a); D(1); }
    private void PushU32(Op op, int a) { Emit(op); U32(a); D(1); }
    private void Pop(Op op) { Emit(op); D(-1); }
    private void PopU16(Op op, int a) { Emit(op); U16(a); D(-1); }
    private void PopU32(Op op, int a) { Emit(op); U32(a); D(-1); }
    private void Keep(Op op) { Emit(op); }
    private void KeepU16(Op op, int a) { Emit(op); U16(a); }
    private void KeepU32(Op op, int a) { Emit(op); U32(a); }
    private void KeepU8(int v) { U8(v); }

    private int ConstIndex(Value v)
    {
        string key = v.Kind switch
        {
            ValueKind.Int => "i" + v.AsLong,
            ValueKind.Float => "f" + v.AsDouble.ToString("R",
                System.Globalization.CultureInfo.InvariantCulture),
            ValueKind.Bool => v.AsLong != 0 ? "b1" : "b0",
            ValueKind.Null => "n",
            _ => "s" + ((IkdString)v.AsRef!).Value,
        };
        if (_ctx!.ConstMap.TryGetValue(key, out int idx)) return idx;
        idx = _ctx.Constants.Count;
        _ctx.Constants.Add(v);
        _ctx.ConstMap[key] = idx;
        return idx;
    }

    private int NameIndex(string name)
    {
        if (_constMap.TryGetValue(name, out int idx)) return idx;
        idx = _constants.Count;
        _constants.Add(Value.OfStr(name));
        _constMap[name] = idx;
        return idx;
    }

    private int NativeIndex(string name)
    {
        if (_prog.NativeIndex.TryGetValue(name, out int idx)) return idx;
        idx = _prog.NativeNames.Count;
        _prog.NativeNames.Add(name);
        _prog.NativeIndex[name] = idx;
        return idx;
    }

    private int TypeDescIndex(TypeSyntax? syntax)
    {
        var d = ResolveTypeDesc(syntax);
        for (int i = 0; i < _typeDescs.Count; i++)
        {
            var c = _typeDescs[i];
            if (c.Kind == d.Kind && c.ClassIndex == d.ClassIndex && c.EnumIndex == d.EnumIndex &&
                c.InterfaceId == d.InterfaceId && c.Args.Length == 0 && d.Args.Length == 0)
                return i;
        }
        _typeDescs.Add(d);
        return _typeDescs.Count - 1;
    }

    private int TypeDescOfKind(TypeDesc.TypeDescKind kind)
    {
        for (int i = 0; i < _typeDescs.Count; i++)
            if (_typeDescs[i].Kind == kind && _typeDescs[i].Args.Length == 0)
                return i;
        _typeDescs.Add(new TypeDesc { Kind = kind });
        return _typeDescs.Count - 1;
    }

    private static TypeDesc New(TypeDesc.TypeDescKind kind) => new() { Kind = kind };

    private TypeDesc ResolveTypeDesc(TypeSyntax? syntax)
    {
        if (syntax is null) return New(TypeDesc.TypeDescKind.Any);
        if (syntax is FnTypeSyntax ft)
        {
            var d = New(TypeDesc.TypeDescKind.Fn);
            d.Args = ft.ParamTypes.Select(ResolveTypeDesc).ToArray();
            return d;
        }

        var nt = (NamedTypeSyntax)syntax;
        switch (nt.Name)
        {
            case "Int": return New(TypeDesc.TypeDescKind.Int);
            case "Float": return New(TypeDesc.TypeDescKind.Float);
            case "Str": return New(TypeDesc.TypeDescKind.Str);
            case "Bool": return New(TypeDesc.TypeDescKind.Bool);
            case "Null": return New(TypeDesc.TypeDescKind.Null);
            case "Void": return New(TypeDesc.TypeDescKind.Void);
            case "Any": return New(TypeDesc.TypeDescKind.Any);
            case "Error": return New(TypeDesc.TypeDescKind.Error);
            case "List":
            {
                var d = New(TypeDesc.TypeDescKind.List);
                if (nt.TypeArgs.Count > 0) d.Args = new[] { ResolveTypeDesc(nt.TypeArgs[0]) };
                return d;
            }
            case "Map": return New(TypeDesc.TypeDescKind.Map);
            case "Fn": return New(TypeDesc.TypeDescKind.Fn);
        }

        if (_types.TryGetValue(nt.Name, out var ts))
        {
            foreach (var a in nt.TypeArgs) ResolveTypeDesc(a);
            return ts.Kind switch
            {
                TypeSymKind.Class => new TypeDesc
                    { Kind = TypeDesc.TypeDescKind.Class, ClassIndex = ts.ClassIndex },
                TypeSymKind.Enum => new TypeDesc
                    { Kind = TypeDesc.TypeDescKind.Enum, EnumIndex = ts.EnumIndex },
                _ => new TypeDesc
                    { Kind = TypeDesc.TypeDescKind.Interface, InterfaceId = ts.InterfaceId },
            };
        }

        // 程序级接口表（允许跨模块引用）
        if (_prog.Interfaces.TryGetValue(nt.Name, out int progIfaceId))
        {
            foreach (var a in nt.TypeArgs) ResolveTypeDesc(a);
            return new TypeDesc
                { Kind = TypeDesc.TypeDescKind.Interface, InterfaceId = progIfaceId };
        }

        if (_ctx is not null && _ctx.TypeParams.Contains(nt.Name))
            return New(TypeDesc.TypeDescKind.TypeParam);

        _bag.Report(ErrorCode.UnknownType, nt.NameSpan.Length > 0 ? nt.NameSpan : nt.Span,
            $"未知类型 '{nt.Name}'");
        return New(TypeDesc.TypeDescKind.Any);
    }

    // ----------------------------------------------------------------
    //  作用域与符号
    // ----------------------------------------------------------------

    private void PushScope() => _ctx!.Scope = new Scope { Parent = _ctx.Scope };

    private void PopScope()
    {
        var s = _ctx!.Scope;
        if (s.Parent is not null) _ctx.Scope = s.Parent;
    }

    private Symbol DeclareLocal(string name, bool isConst, TextSpan span)
    {
        var scope = _ctx!.Scope;
        if (scope.FindLocal(name) is not null)
            _bag.Report(ErrorCode.DuplicateDefinition, span, $"'{name}' 在同一作用域中重复定义");
        var sym = new Symbol
        {
            Name = name,
            Kind = SymKind.Local,
            Index = _ctx.LocalCount++,
            IsConst = isConst,
        };
        scope.Vars[name] = sym;
        return sym;
    }

    private int NewTemp() => _ctx!.LocalCount++;

    private bool IsAtModuleLevel()
        => _ctx is { IsModuleInit: true } && ReferenceEquals(_ctx.Scope, _globalscope);

    private Symbol DeclareGlobal(string name, bool isConst, TextSpan span)
    {
        if (_globalscope.FindLocal(name) is not null || _types.ContainsKey(name))
        {
            _bag.Report(ErrorCode.DuplicateDefinition, span, $"'{name}' 已经定义");
            var old = _globalscope.FindLocal(name);
            if (old is not null) return old;
        }
        var sym = new Symbol
        {
            Name = name,
            Kind = SymKind.Global,
            Index = _globalNames.Count,
            IsConst = isConst,
        };
        _globalNames.Add(name);
        _globals[name] = sym.Index;
        _globalscope.Vars[name] = sym;
        _exports.Add(name);
        return sym;
    }

    private Symbol? LookupValue(FnCtx ctx, string name)
    {
        for (var s = ctx.Scope; s is not null; s = s.Parent)
            if (s.FindLocal(name) is { } sym)
                return sym;
        return null;
    }

    private int AddUpvalue(FnCtx ctx, UpvalueDesc desc)
    {
        for (int i = 0; i < ctx.Upvalues.Count; i++)
        {
            var e = ctx.Upvalues[i];
            if (e.IsLocal == desc.IsLocal && e.IsGlobal == desc.IsGlobal && e.Index == desc.Index)
                return i;
        }
        ctx.Upvalues.Add(desc);
        return ctx.Upvalues.Count - 1;
    }

    private int ResolveUpvalue(FnCtx ctx, string name)
    {
        if (ctx.Parent is null) return -1;
        var sym = LookupValue(ctx.Parent, name);
        if (sym is not null)
        {
            if (sym.Kind == SymKind.Global) return -1;
            return AddUpvalue(ctx, UpvalueDesc.FromLocal(sym.Index));
        }
        int outer = ResolveUpvalue(ctx.Parent, name);
        if (outer < 0) return -1;
        return AddUpvalue(ctx, UpvalueDesc.FromUpvalue(outer));
    }

    // ----------------------------------------------------------------
    //  声明阶段
    // ----------------------------------------------------------------

    public void Declare()
    {
        foreach (var stmt in _unit.Statements)
        {
            switch (stmt)
            {
                case ClassDecl c:
                    if (_types.ContainsKey(c.Name) || _globalscope.FindLocal(c.Name) is not null)
                        _bag.Report(ErrorCode.DuplicateDefinition, c.NameSpan, $"'{c.Name}' 已经定义");
                    else
                    {
                        // 先注册为全局（供命名空间导出），再进类型表
                        DeclareGlobal(c.Name, true, c.NameSpan);
                        _types[c.Name] = new TypeSymbol
                        {
                            Name = c.Name,
                            Kind = TypeSymKind.Class,
                            ClassIndex = _classes.Count,
                            Class = c,
                        };
                        _classes.Add(new ClassImage { Name = c.Name });
                    }
                    break;
                case EnumDecl e:
                    if (_types.ContainsKey(e.Name) || _globalscope.FindLocal(e.Name) is not null)
                        _bag.Report(ErrorCode.DuplicateDefinition, e.NameSpan, $"'{e.Name}' 已经定义");
                    else
                    {
                        DeclareGlobal(e.Name, true, e.NameSpan);
                        _types[e.Name] = new TypeSymbol
                        {
                            Name = e.Name,
                            Kind = TypeSymKind.Enum,
                            EnumIndex = _enums.Count,
                            EnumDecl = e,
                        };
                        _enums.Add(new EnumImage { Name = e.Name });
                    }
                    break;
                case InterfaceDecl i:
                    if (_types.ContainsKey(i.Name))
                        _bag.Report(ErrorCode.DuplicateDefinition, i.NameSpan, $"'{i.Name}' 已经定义");
                    else
                    {
                        if (!_prog.Interfaces.TryGetValue(i.Name, out int id))
                        {
                            id = _prog.InterfaceList.Count;
                            _prog.Interfaces[i.Name] = id;
                            _prog.InterfaceList.Add(new InterfaceImage { Id = id, Name = i.Name });
                        }
                        _types[i.Name] = new TypeSymbol
                        {
                            Name = i.Name,
                            Kind = TypeSymKind.Interface,
                            InterfaceId = id,
                            Interface = i,
                        };
                    }
                    break;
            }
        }

        // 第二遍：按依赖顺序填充（父接口/基类在前）
        var ifaces = new List<InterfaceDecl>();
        var classes = new List<ClassDecl>();
        var enums = new List<EnumDecl>();
        foreach (var stmt in _unit.Statements)
        {
            if (stmt is InterfaceDecl ii && _types.TryGetValue(ii.Name, out var isym) &&
                isym.Interface is not null)
                ifaces.Add(ii);
            else if (stmt is ClassDecl cc && _types.TryGetValue(cc.Name, out var ts) &&
                     ts.Class is not null)
                classes.Add(cc);
            else if (stmt is EnumDecl ee && _types.TryGetValue(ee.Name, out var es) &&
                     es.EnumDecl is not null)
                enums.Add(ee);
        }

        ifaces.Sort((a, b) => InterfaceDepth(a).CompareTo(InterfaceDepth(b)));
        classes.Sort((a, b) => ClassDepth(a).CompareTo(ClassDepth(b)));

        foreach (var i in ifaces)
        {
            if (!_types.TryGetValue(i.Name, out var isym) || isym.Interface is null) continue;
            var names = new List<string>();
            foreach (var m in isym.Interface.Members)
                if (m is MethodDecl md)
                    names.Add(md.Name);
            foreach (var p in i.Parents)
                if (p is NamedTypeSyntax pn && _types.TryGetValue(pn.Name, out var par) &&
                    par.Interface is not null)
                    foreach (var m in par.Interface.Members)
                        if (m is MethodDecl md && !names.Contains(md.Name))
                            names.Add(md.Name);
            var img = _prog.InterfaceList[isym.InterfaceId];
            if (img.MethodNames.Length == 0) img.MethodNames = names.ToArray();
        }

        foreach (var c in classes)
        {
            if (!_types.TryGetValue(c.Name, out var ts) || ts.Class is null) continue;
            int depth = ClassDepth(c, new HashSet<string>(StringComparer.Ordinal));
            if (depth < 0)
            {
                _bag.Report(ErrorCode.CannotInheritFrom, c.NameSpan,
                    $"'{c.Name}' 存在循环继承，无法编译");
                continue;
            }
            FillClass(ts, c);
        }

        foreach (var e in enums)
        {
            if (!_types.TryGetValue(e.Name, out var es) || es.EnumDecl is null) continue;
            var img = _enums[es.EnumIndex];
            img.CaseNames = e.Cases.Select(x => x.Name).ToArray();
            img.CaseArities = e.Cases.Select(x => x.Payload.Count).ToArray();
            foreach (var cs in e.Cases)
                foreach (var p in cs.Payload)
                    ResolveTypeDesc(p);
        }

        foreach (var stmt in _unit.Statements)
        {
            switch (stmt)
            {
                case ImportStmt im:
                {
                    string alias = im.Alias ?? DefaultAlias(im.Path);
                    if (alias.Length == 0) break;
                    if (!_importMap.TryGetValue(im.Path, out int idx))
                    {
                        idx = _imports.Count;
                        _imports.Add(im.Path);
                        _importMap[im.Path] = idx;
                    }
                    if (_globalscope.FindLocal(alias) is not null || _types.ContainsKey(alias))
                        _bag.Report(ErrorCode.DuplicateDefinition,
                            im.AliasSpan.Length > 0 ? im.AliasSpan : im.PathSpan,
                            $"'{alias}' 已经定义");
                    else
                        DeclareGlobal(alias, true, im.PathSpan);
                    break;
                }
                case FnDecl f:
                    DeclareGlobal(f.Name, true, f.NameSpan);
                    break;
                case VarDeclStmt v:
                    DeclareGlobal(v.Name, v.Kind == VarDeclKind.Const, v.NameSpan);
                    break;
            }
        }
    }

    private static string DefaultAlias(string path)
    {
        if (path.StartsWith("std.", StringComparison.Ordinal))
        {
            var parts = path.Split('.');
            return parts[^1];
        }
        var file = Path.GetFileNameWithoutExtension(path);
        int dot = file.LastIndexOf('.');
        return dot >= 0 ? file[(dot + 1)..] : file;
    }

    /// <summary>基类链深度；-1 表示存在循环继承。</summary>
    private int ClassDepth(ClassDecl c) => ClassDepth(c, new HashSet<string>(StringComparer.Ordinal));

    private int ClassDepth(ClassDecl c, HashSet<string> visiting)
    {
        if (c.Base is not NamedTypeSyntax bn) return 0;
        if (!visiting.Add(c.Name)) return -1;
        int d = 0;
        if (_types.TryGetValue(bn.Name, out var bt) && bt.Kind == TypeSymKind.Class &&
            bt.Class is not null)
        {
            var pd = ClassDepth(bt.Class, visiting);
            visiting.Remove(c.Name);
            if (pd < 0) return -1;
            d = pd + 1;
        }
        visiting.Remove(c.Name);
        return d;
    }

    private int InterfaceDepth(InterfaceDecl i) => InterfaceDepth(i, new HashSet<string>(StringComparer.Ordinal));

    private int InterfaceDepth(InterfaceDecl i, HashSet<string> visiting)
    {
        int best = 0;
        foreach (var p in i.Parents)
        {
            if (p is not NamedTypeSyntax pn) continue;
            if (!_types.TryGetValue(pn.Name, out var par) || par.Kind != TypeSymKind.Interface ||
                par.Interface is null) continue;
            if (!visiting.Add(i.Name)) return -1;
            var pd = InterfaceDepth(par.Interface, visiting);
            visiting.Remove(i.Name);
            if (pd < 0) return -1;
            if (pd + 1 > best) best = pd + 1;
        }
        return best;
    }

    private void FillClass(TypeSymbol ts, ClassDecl c)
    {
        var img = _classes[ts.ClassIndex];
        int baseIndex = -1;
        var baseIsInterface = false;
        if (c.Base is NamedTypeSyntax bn)
        {
            if (_types.TryGetValue(bn.Name, out var bt) && bt.Kind == TypeSymKind.Class)
            {
                baseIndex = bt.ClassIndex;
                ts.Base = bt;
                ts.HasAnyInit = bt.HasAnyInit;
                ts.HasAnyDefaults = bt.HasAnyDefaults;
                foreach (var m in bt.Methods) if (!ts.Methods.Contains(m)) ts.Methods.Add(m);
                foreach (var f in bt.FieldNames) ts.FieldNames.Add(f);
                foreach (var id in bt.InterfaceIds) ts.InterfaceIds.Add(id);
            }
            else if (_types.TryGetValue(bn.Name, out var it2) && it2.Kind == TypeSymKind.Interface)
                baseIsInterface = true;
            else if (_prog.Interfaces.ContainsKey(bn.Name))
                baseIsInterface = true;
            else
                _bag.Report(ErrorCode.CannotInheritFrom, c.BaseSpan,
                    $"'{bn.Name}' 不是类，不能作为基类");
        }

        var interfaceIds = new List<int>();
        if (baseIsInterface && c.Base is NamedTypeSyntax ibn)
        {
            int foundId = -1;
            if (_types.TryGetValue(ibn.Name, out var isym0) && isym0.Kind == TypeSymKind.Interface)
                foundId = isym0.InterfaceId;
            else if (_prog.Interfaces.TryGetValue(ibn.Name, out int pid0))
                foundId = pid0;
            if (foundId >= 0)
            {
                interfaceIds.Add(foundId);
                ts.InterfaceIds.Add(foundId);
            }
        }
        foreach (var iface in c.Interfaces)
        {
            int foundId = -1;
            if (iface is NamedTypeSyntax it)
            {
                if (_types.TryGetValue(it.Name, out var isym) && isym.Kind == TypeSymKind.Interface)
                    foundId = isym.InterfaceId;
                else if (_prog.Interfaces.TryGetValue(it.Name, out int pid))
                    foundId = pid; // 跨模块接口（程序级注册）
            }
            if (foundId >= 0)
            {
                interfaceIds.Add(foundId);
                ts.InterfaceIds.Add(foundId);
            }
            else
                _bag.Report(ErrorCode.CannotInheritFrom, iface.Span, $"'{iface}' 不是接口");
        }

        foreach (var m in c.Members)
            if (m is FieldDecl fd && !fd.Mods.IsStatic)
            {
                ts.FieldNames.Add(fd.Name);
                if (fd.Value is not null)
                {
                    ts.HasAnyDefaults = true;
                    ts.OwnDefaults = true;
                }
            }

        foreach (var m in c.Members)
        {
            if (m is not MethodDecl md) continue;
            int arity = md.Params.Count;
            int found = -1;
            for (int i = 0; i < ts.Methods.Count; i++)
                if (ts.Methods[i].Name == md.Name) { found = i; break; }
            if (found >= 0) ts.Methods[found] = (md.Name, arity);
            else ts.Methods.Add((md.Name, arity));
            if (md.IsConstructor || md.Name == "init")
            {
                ts.OwnInitArity = arity;
                ts.HasInit = true;
                ts.HasAnyInit = true;
            }
        }

        foreach (var id in interfaceIds)
        {
            foreach (var required in _prog.InterfaceList[id].MethodNames)
            {
                bool ok = false;
                foreach (var (mn, _) in ts.Methods)
                    if (mn == required) { ok = true; break; }
                if (!ok)
                    _bag.Report(ErrorCode.ClassNotFullyImplemented, c.NameSpan,
                        $"类 '{c.Name}' 没有实现接口 '{_prog.InterfaceList[id].Name}' 要求的方法 '{required}'");
            }
        }

        img.BaseIndex = baseIndex;
        img.InterfaceIds = interfaceIds.ToArray();
        img.FieldNames = ts.FieldNames.ToArray();
        img.FieldCount = ts.FieldNames.Count;
        img.IsAbstract = c.Mods.IsAbstract;
    }

    // ----------------------------------------------------------------
    //  发射入口 / 函数构建
    // ----------------------------------------------------------------

    public ModuleImage Emit()
    {
        int initIndex = ReserveFunction();
        var init = new FnCtx
        {
            Name = "<init>",
            Arity = 0,
            IsModuleInit = true,
            ModulePath = _path,
            Scope = _globalscope,
        };
        init.Lines.Add((0, 1));
        _ctx = init;

        foreach (var stmt in _unit.Statements)
        {
            if (stmt is not ImportStmt im) continue;
            MarkLine(im);
            if (!_importMap.TryGetValue(im.Path, out int idx)) continue;
            PushU32(Op.Import, idx);
            string alias = im.Alias ?? DefaultAlias(im.Path);
            if (_globals.TryGetValue(alias, out int g)) PopU32(Op.DefGlobalCell, g);
            else Pop(Op.Pop);
        }

        foreach (var stmt in _unit.Statements)
            if (stmt is FnDecl f)
                EmitTopLevelFn(f);

        foreach (var stmt in _unit.Statements)
        {
            switch (stmt)
            {
                case ImportStmt:
                case FnDecl:
                    break;                      // 已在上面发射
                case ClassDecl c:
                    EmitClassDecl(c);
                    break;
                case EnumDecl e:
                    EmitEnumDecl(e);
                    break;
                case InterfaceDecl:
                    break;                      // 仅编译期，无运行时产物
                default:
                    EmitStatement(stmt);
                    break;
            }
        }

        Push(Op.Null);
        Pop(Op.Return);
        FillFunction(initIndex, init);
        _ctx = null;

        return new ModuleImage
        {
            Name = _name,
            Path = _path,
            Imports = _imports.ToArray(),
            Constants = _constants.ToArray(),
            Functions = _functions.Select(f => f!).ToArray(),
            Classes = _classes.ToArray(),
            Enums = _enums.ToArray(),
            TypeDescriptors = _typeDescs.ToArray(),
            GlobalCount = _globalNames.Count,
            GlobalNames = _globalNames.ToArray(),
            ExportNames = _exports.ToArray(),
            InitFunction = initIndex,
        };
    }

    private int ReserveFunction()
    {
        _functions.Add(null!);
        return _functions.Count - 1;
    }

    private void FillFunction(int index, FnCtx ctx)
    {
        int n = ctx.Lines.Count;
        var ips = new int[n + 1];
        var lns = new int[n + 1];
        ips[0] = 0;
        lns[0] = n > 0 ? ctx.Lines[0].Line : 1;
        for (int i = 0; i < n; i++)
        {
            ips[i + 1] = ctx.Lines[i].Ip;
            lns[i + 1] = ctx.Lines[i].Line;
        }
        _functions[index] = new IkdFunction(ctx.Name, ctx.Arity, ctx.Code.ToArray(),
            ctx.Constants.ToArray(), ctx.LocalCount, ctx.MaxDepth + 8, ips, lns,
            ctx.Upvalues.ToArray(), _path, ctx.IsMethod, ctx.ParamNames);
    }

    private void EmitClosureOp(int funcIndex, FnCtx fnCtx)
    {
        Emit(Op.Closure);
        U32(funcIndex);
        U8(fnCtx.Upvalues.Count);
        foreach (var u in fnCtx.Upvalues)
        {
            U8((u.IsLocal ? 1 : 0) | (u.IsGlobal ? 2 : 0));
            U16(u.Index);
        }
        D(1);
    }

    private void EmitTopLevelFn(FnDecl f)
    {
        var fnCtx = CompileFn(f.Name, f.Params, f.ReturnType, f.Body, null,
            isMethod: false, f.TypeParams);
        int index = ReserveFunction();
        FillFunction(index, fnCtx);
        if (_globals.TryGetValue(f.Name, out int g))
        {
            EmitClosureOp(index, fnCtx);
            PopU32(Op.DefGlobalCell, g);
        }
    }

    /// <summary>编译一个函数体；期间不向当前缓冲区写入任何指令。</summary>
    private FnCtx CompileFn(string name, List<ParamSyntax> parms, TypeSyntax? returnType,
        BlockStmt body, FnCtx? parent, bool isMethod, List<TypeParamSyntax> typeParams)
    {
        var saved = _ctx;
        var fn = new FnCtx
        {
            Name = name,
            Arity = parms.Count,
            IsMethod = isMethod,
            ModulePath = _path,
            Parent = parent,
            ParamNames = parms.Select(p => p.Name!).ToArray(),
        };
        if (parent is not null)
            foreach (var tp in parent.TypeParams)
                fn.TypeParams.Add(tp);
        foreach (var tp in typeParams)
            fn.TypeParams.Add(tp.Name);

        _ctx = fn;
        PushScope();
        foreach (var p in parms)
        {
            if (p.Type is not null) ResolveTypeDesc(p.Type);
            DeclareLocal(p.Name, false, p.NameSpan);
        }
        if (returnType is not null) ResolveTypeDesc(returnType);

        // 形参装箱成 Cell，便于闭包捕获
        for (int i = 0; i < parms.Count; i++)
        {
            PushU16(Op.LoadLocal, i);
            PopU16(Op.DefLocalCell, i);
        }

        EmitBlockStatements(body);
        Push(Op.Null);
        Pop(Op.Return);
        PopScope();
        _ctx = saved;
        return fn;
    }

    private void EmitLambda(LambdaExpr l)
    {
        var saved = _ctx;
        int index = ReserveFunction();
        var body = l.BodyBlock ?? BlockFromExpr(l.BodyExpr, l);
        var fn = CompileFn("<lambda>", l.Params, l.ReturnType, body, saved,
            isMethod: false, new List<TypeParamSyntax>());
        FillFunction(index, fn);
        _ctx = saved;
        EmitClosureOp(index, fn);
    }

    private static BlockStmt BlockFromExpr(Expression? expr, LambdaExpr owner)
    {
        var b = new BlockStmt { Span = owner.Span };
        b.Statements.Add(new ReturnStmt
        {
            Value = expr,
            KeywordSpan = owner.Span,
            Span = expr?.Span ?? owner.Span,
        });
        return b;
    }
}
