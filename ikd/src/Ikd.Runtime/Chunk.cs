using System.Text;

namespace Ikd.Runtime;

/// <summary>字节码操作码。</summary>
public enum Op : byte
{
    Nop = 0,
    Const,          // u32
    Null,
    True,
    False,
    Pop,
    PopN,           // u8
    Dup,

    // 局部变量（cell 版本用于被闭包捕获的变量）
    LoadLocal,      // u16
    DefLocal,       // u16  pop
    StoreLocal,     // u16  peek & keep
    LoadLocalCell,  // u16
    DefLocalCell,   // u16  pop -> 包成 Cell
    StoreLocalCell, // u16  peek & keep
    LoadUpvalue,    // u16
    StoreUpvalue,   // u16  peek & keep

    // 模块全局
    LoadGlobal,     // u32
    DefGlobal,      // u32  pop
    StoreGlobal,    // u32  peek & keep
    LoadGlobalCell, // u32
    DefGlobalCell,  // u32  pop -> 包成 Cell
    StoreGlobalCell,// u32  peek & keep

    // 成员 / 调用
    GetProp,        // u32 nameIdx        pop recv -> push value
    SetProp,        // u32 nameIdx        pop value, pop recv -> push value
    CallProp,       // u32 nameIdx, u8 argc
    GetSuper,       // u32 nameIdx
    CallSuper,      // u32 nameIdx, u8 argc
    Call,           // u8 argc
    Return,         // pop value

    // 对象 / 类型
    NewInstance,    // u32 classIdx
    LoadClass,      // u32 classIdx
    LoadEnumType,   // u32 enumIdx
    GetEnumConst,   // u32 enumIdx, u32 caseIdx
    NewEnumCase,    // u32 enumIdx, u32 caseIdx, u8 arity
    IsEnumCase,     // u32 enumIdx, u32 caseIdx
    GetEnumPayload, // u32 index
    IsType,         // u32 typeDescIdx   pop -> bool
    Cast,           // u32 typeDescIdx   pop -> push

    // 控制流
    Jump,           // u32 absolute ip
    JumpIfFalse,    // u32  pop
    JumpIfTrue,     // u32  pop

    // 运算
    Add, Sub, Mul, Div, Mod, Neg, Not,
    Eq, Ne, Lt, Le, Gt, Ge,

    Concat,         // u8 count
    IndexGet,       // pop idx, pop target -> push
    IndexSet,       // pop value, pop idx, pop target -> push value
    NewList,        // u32 count
    NewMap,         // u32 pairCount

    // 迭代
    IterNew,        // pop iterable -> push iterator
    IterNext,       // u32 elseOffset；peek 迭代器，有值则 push，否则跳转

    // 模块 / 原生
    Import,         // u32 importIdx -> push namespace
    LoadNative,     // u32 nativeIdx -> push native fn

    // 异常
    TryPush,        // u32 handler ip
    TryPop,
    Throw,          // pop value
    MatchFail,      // pop subject

    LoadThis,       // push this

    // 创建闭包：u32 funcIdx, u8 upvalCount, 每个 upval 为 u8 flags + u16 index
    // flags: bit0=IsLocal(取当前帧局部槽), bit1=IsGlobal(取模块全局), 否则取父闭包 upvalue
    Closure,
}

/// <summary>闭包捕获来源。</summary>
public struct UpvalueDesc
{
    /// <summary>true：来自当前帧的局部槽；false：来自父闭包的 upvalue 数组或全局槽。</summary>
    public bool IsLocal;
    public bool IsGlobal;
    public ushort Index;

    public static UpvalueDesc FromLocal(int i) => new() { IsLocal = true, Index = (ushort)i };
    public static UpvalueDesc FromUpvalue(int i) => new() { IsLocal = false, IsGlobal = false, Index = (ushort)i };
    public static UpvalueDesc FromGlobal(int i) => new() { IsLocal = false, IsGlobal = true, Index = (ushort)i };

    public override string ToString() =>
        IsLocal ? $"local {Index}" : IsGlobal ? $"global {Index}" : $"upval {Index}";
}

/// <summary>运行时类型描述（用于 is / as）。</summary>
public sealed class TypeDesc
{
    public TypeDescKind Kind;
    public int ClassIndex = -1;      // 模块内类下标
    public int InterfaceId = -1;
    public int EnumIndex = -1;
    public TypeDesc[] Args = Array.Empty<TypeDesc>();
    /// <summary>加载后解析出的运行时对象（IkdClass / IkdEnumType / null）。</summary>
    public object? Resolved;

    public enum TypeDescKind : byte
    {
        Int, Float, Str, Bool, Null, Void, Any, Error,
        Class, Interface, Enum, List, Map, Fn, TypeParam,
    }

    public override string ToString() => Kind switch
    {
        TypeDescKind.Class => "class#" + ClassIndex,
        TypeDescKind.Interface => "iface#" + InterfaceId,
        TypeDescKind.Enum => "enum#" + EnumIndex,
        TypeDescKind.List => "List<" + (Args.Length > 0 ? Args[0] : "?") + ">",
        TypeDescKind.Map => "Map<...>",
        TypeDescKind.Fn => "fn",
        _ => Kind.ToString(),
    };
}

/// <summary>接口定义（程序级）。</summary>
public sealed class InterfaceImage
{
    public int Id;
    public string Name = "";
    public string[] MethodNames = Array.Empty<string>();
}

/// <summary>类定义（模块内）。</summary>
public sealed class ClassImage
{
    public string Name = "";
    public int BaseIndex = -1;
    public int[] InterfaceIds = Array.Empty<int>();
    public string[] FieldNames = Array.Empty<string>();
    public int FieldCount;
    public bool IsAbstract;
    /// <summary>(方法名, 在模块 Functions 中的下标)</summary>
    public (string Name, int FuncIndex)[] Methods = Array.Empty<(string, int)>();
    public (string Name, int FuncIndex)[] StaticMethods = Array.Empty<(string, int)>();
    public string[] StaticFieldNames = Array.Empty<string>();
    public string?[] TypeParams = Array.Empty<string?>();
}

/// <summary>枚举定义（模块内）。</summary>
public sealed class EnumImage
{
    public string Name = "";
    public string[] CaseNames = Array.Empty<string>();
    public int[] CaseArities = Array.Empty<int>();
    public string?[] TypeParams = Array.Empty<string?>();
}

/// <summary>一个源文件的编译产物。</summary>
public sealed class ModuleImage
{
    public string Name = "";
    public string Path = "";
    /// <summary>导入表：用户模块路径或 "std.xxx"。</summary>
    public string[] Imports = Array.Empty<string>();
    public Value[] Constants = Array.Empty<Value>();
    public IkdFunction[] Functions = Array.Empty<IkdFunction>();
    public ClassImage[] Classes = Array.Empty<ClassImage>();
    public EnumImage[] Enums = Array.Empty<EnumImage>();
    public TypeDesc[] TypeDescriptors = Array.Empty<TypeDesc>();
    public int GlobalCount;
    public string[] GlobalNames = Array.Empty<string>();
    /// <summary>导出到模块命名空间的全局名。</summary>
    public string[] ExportNames = Array.Empty<string>();
    public int[] CellGlobalIndices = Array.Empty<int>();
    public int InitFunction = -1;
}

/// <summary>整个程序的编译产物。</summary>
public sealed class ProgramImage
{
    public string EntryPath = "";
    public string EntryName = "";
    public ModuleImage[] Modules = Array.Empty<ModuleImage>();
    public InterfaceImage[] Interfaces = Array.Empty<InterfaceImage>();
    /// <summary>原生函数引用（按名字解析）。</summary>
    public string[] NativeNames = Array.Empty<string>();
    public int Version = 1;

    public static readonly byte[] Magic = { (byte)'I', (byte)'K', (byte)'D', (byte)'B' };
}

/// <summary>字节码反汇编（调试用）。</summary>
public static class Disassembler
{
    public static string Disassemble(IkdFunction fn)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"== {fn.Name}/{fn.Arity} (locals={fn.LocalCount}, stack={fn.MaxStack}, module={fn.ModulePath}) ==");
        int ip = 0;
        var code = fn.Code;
        while (ip < code.Length)
        {
            int start = ip;
            string line = DisassembleOne(fn, ref ip);
            sb.AppendLine($"{start,4:D4}  {line}");
        }
        return sb.ToString();
    }

    public static string DisassembleModule(ModuleImage m)
    {
        var sb = new StringBuilder();
        sb.AppendLine($";; module {m.Name} ({m.Path})");
        sb.AppendLine($";; globals={m.GlobalCount} functions={m.Functions.Length} classes={m.Classes.Length} enums={m.Enums.Length}");
        if (m.Imports.Length > 0)
            sb.AppendLine(";; imports: " + string.Join(", ", m.Imports));
        foreach (var f in m.Functions)
        {
            sb.AppendLine();
            sb.Append(Disassemble(f));
        }
        return sb.ToString();
    }

    private static string DisassembleOne(IkdFunction fn, ref int ip)
    {
        var code = fn.Code;
        var op = (Op)code[ip++];
        switch (op)
        {
            case Op.Const:
            case Op.GetProp:
            case Op.SetProp:
            case Op.CallProp:
            case Op.GetSuper:
            case Op.CallSuper:
            case Op.NewInstance:
            case Op.LoadClass:
            case Op.LoadEnumType:
            case Op.IsType:
            case Op.Cast:
            case Op.Import:
            case Op.LoadNative:
            {
                uint a = ReadU32(code, ref ip);
                return op.ToString() + " " + a + "  ; " + ConstText(fn, a, op);
            }
            case Op.GetEnumConst:
            case Op.IsEnumCase:
            {
                uint a = ReadU32(code, ref ip);
                uint b = ReadU32(code, ref ip);
                return $"{op} {a}.{b}";
            }
            case Op.NewEnumCase:
            {
                uint a = ReadU32(code, ref ip);
                uint b = ReadU32(code, ref ip);
                byte n = code[ip++];
                return $"{op} {a}.{b} ({n})";
            }
            case Op.Jump:
            case Op.JumpIfFalse:
            case Op.JumpIfTrue:
            case Op.IterNext:
            case Op.TryPush:
            {
                uint a = ReadU32(code, ref ip);
                return $"{op} -> {a}";
            }
            case Op.LoadLocal:
            case Op.DefLocal:
            case Op.StoreLocal:
            case Op.LoadLocalCell:
            case Op.DefLocalCell:
            case Op.StoreLocalCell:
            case Op.LoadUpvalue:
            case Op.StoreUpvalue:
            {
                ushort a = ReadU16(code, ref ip);
                return $"{op} {a}";
            }
            case Op.LoadGlobal:
            case Op.DefGlobal:
            case Op.StoreGlobal:
            case Op.LoadGlobalCell:
            case Op.DefGlobalCell:
            case Op.StoreGlobalCell:
            case Op.GetEnumPayload:
            {
                uint a = ReadU32(code, ref ip);
                return $"{op} {a}";
            }
            case Op.Call:
            case Op.PopN:
            {
                byte a = code[ip++];
                return $"{op} {a}";
            }
            case Op.Closure:
            {
                uint fi = ReadU32(code, ref ip);
                byte n = code[ip++];
                var parts = new List<string>();
                for (int i = 0; i < n; i++)
                {
                    byte flags = code[ip++];
                    ushort idx = ReadU16(code, ref ip);
                    parts.Add((flags & 1) != 0 ? $"local {idx}" :
                        (flags & 2) != 0 ? $"global {idx}" : $"upval {idx}");
                }
                return $"{op} fn#{fi} [{string.Join(", ", parts)}]";
            }
            default:
                return op.ToString();
        }
    }

    private static string ConstText(IkdFunction fn, uint idx, Op op)
    {
        if (op == Op.Const && idx < fn.Constants.Length)
        {
            var c = fn.Constants[idx];
            if (c.IsStr)
                return "\"" + c.ToDisplayString().Replace("\n", "\\n") + "\"";
            return c.ToDisplayString();
        }
        return "";
    }

    public static uint ReadU32(byte[] code, ref int ip)
    {
        uint v = (uint)(code[ip] | (code[ip + 1] << 8) | (code[ip + 2] << 16) | (code[ip + 3] << 24));
        ip += 4;
        return v;
    }

    public static ushort ReadU16(byte[] code, ref int ip)
    {
        ushort v = (ushort)(code[ip] | (code[ip + 1] << 8));
        ip += 2;
        return v;
    }
}
