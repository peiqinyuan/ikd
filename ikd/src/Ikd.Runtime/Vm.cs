using System.Text;

namespace Ikd.Runtime;

/// <summary>一个已加载（并初始化）的用户模块。</summary>
public sealed class RuntimeModule
{
    public ModuleImage Image { get; }
    public IkdClass[] Classes { get; }
    public IkdEnumType[] Enums { get; }
    public Value[] Globals { get; }
    public IkdNamespace Namespace { get; }
    public bool Initialized { get; set; }

    public RuntimeModule(ModuleImage image, IkdClass[] classes, IkdEnumType[] enums,
        Value[] globals, IkdNamespace ns)
    {
        Image = image;
        Classes = classes;
        Enums = enums;
        Globals = globals;
        Namespace = ns;
    }
}

/// <summary>基于字节码的虚拟机。</summary>
public sealed class Vm
{
    private const int MaxFrames = 4096;
    private const int MaxTries = 8192;

    private struct Frame
    {
        public IkdFunction Fn;
        public RuntimeModule Module;
        public IkdClosure? Closure;
        public object? This;
        public int Base;
        public int Ip;
        public int TryMark;
        public bool ReturnThis;
    }

    private struct TryEntry
    {
        public int FrameIdx;
        public int PushIp;
        public int HandlerIp;
        public int StackTop;
    }

    private sealed class BoundNative : IkdObject
    {
        public Value Recv;
        public IkdNativeFn Fn;
        public BoundNative(Value recv, IkdNativeFn fn) { Recv = recv; Fn = fn; }
    }

    private sealed class ListIter : IkdIterable
    {
        private readonly IkdList _list;
        private int _i;
        public ListIter(IkdList list) => _list = list;
        public override bool TryNext(out Value value)
        {
            if (_i < _list.Items.Count) { value = _list.Items[_i++]; return true; }
            value = Value.Null;
            return false;
        }
    }

    private sealed class StrIter : IkdIterable
    {
        private readonly string _s;
        private int _i;
        public StrIter(string s) => _s = s;
        public override bool TryNext(out Value value)
        {
            if (_i < _s.Length) { value = Value.OfStr(_s[_i++].ToString()); return true; }
            value = Value.Null;
            return false;
        }
    }

    private sealed class MapIter : IkdIterable
    {
        private readonly IkdMap _map;
        private readonly List<object> _keys = new();
        private int _i;
        public MapIter(IkdMap map)
        {
            _map = map;
            foreach (var k in map.Pairs.Keys) _keys.Add(k);
        }
        public override bool TryNext(out Value value)
        {
            if (_i < _keys.Count)
            {
                var k = _keys[_i++];
                value = k is string s ? Value.OfStr(s) : Value.Of((long)k!);
                return true;
            }
            value = Value.Null;
            return false;
        }
    }

    private readonly ProgramImage _image;
    private readonly Dictionary<string, RuntimeModule> _modules = new(StringComparer.Ordinal);

    private Value[] _stack = new Value[4096];
    private int _sp;
    private readonly Frame[] _frames = new Frame[MaxFrames];
    private int _fp;
    private readonly TryEntry[] _tries = new TryEntry[MaxTries];
    private int _tryCount;

    public Vm(ProgramImage image) => _image = image;

    public ProgramImage Image => _image;

    // ---------------------------------------------------------------
    //  栈操作
    // ---------------------------------------------------------------

    private void Ensure(int needed)
    {
        if (needed > _stack.Length)
        {
            int newSize = _stack.Length;
            while (newSize < needed) newSize *= 2;
            Array.Resize(ref _stack, newSize);
        }
    }

    private void Push(Value v)
    {
        Ensure(_sp + 1);
        _stack[_sp++] = v;
    }

    private Value Pop() => _stack[--_sp];

    private Value Peek(int depth = 0) => _stack[_sp - 1 - depth];

    // ---------------------------------------------------------------
    //  模块加载
    // ---------------------------------------------------------------

    private RuntimeModule LoadModule(string name)
    {
        if (_modules.TryGetValue(name, out var existing))
        {
            if (!existing.Initialized)
            {
                existing.Initialized = true;
                RunInit(existing);
            }
            return existing;
        }

        if (name.StartsWith("std.", StringComparison.Ordinal))
        {
            if (!StdLib.TryGetModule(name, out var nativeModule))
                throw new IkdRuntimeException("ImportError", $"找不到模块 {name}");
            var ns = new IkdNamespace(name);
            foreach (var kv in nativeModule.Members) ns.Members[kv.Key] = kv.Value;
            var stdRm = new RuntimeModule(
                new ModuleImage { Name = name, Path = name },
                Array.Empty<IkdClass>(), Array.Empty<IkdEnumType>(), Array.Empty<Value>(), ns)
            { Initialized = true };
            _modules[name] = stdRm;
            return stdRm;
        }

        ModuleImage? image = null;
        foreach (var m in _image.Modules)
            if (m.Name == name) { image = m; break; }
        if (image is null)
            throw new IkdRuntimeException("ImportError", $"找不到模块 {name}");

        var classes = BuildClasses(image);
        var enums = BuildEnums(image);
        var globals = new Value[image.GlobalCount];
        var module = new RuntimeModule(image, classes, enums, globals, new IkdNamespace(image.Name));
        module.Namespace.LiveModule = module;
        foreach (var f in image.Functions) f.Module = module;
        // 类/枚举全局按名预填（init 执行到声明处会覆盖），供循环导入期间访问
        for (int i = 0; i < image.GlobalNames.Length; i++)
        {
            var gn = image.GlobalNames[i];
            for (int c = 0; c < classes.Length; c++)
                if (image.Classes[c].Name == gn)
                    globals[i] = Value.OfRef(classes[c]);
            for (int e = 0; e < enums.Length; e++)
                if (image.Enums[e].Name == gn)
                    globals[i] = Value.OfRef(enums[e]);
        }
        _modules[name] = module;
        module.Initialized = true;
        RunInit(module);
        return module;
    }

    private IkdClass[] BuildClasses(ModuleImage image)
    {
        var classes = new IkdClass[image.Classes.Length];
        for (int i = 0; i < image.Classes.Length; i++)
        {
            var ci = image.Classes[i];
            classes[i] = new IkdClass(ci.Name)
            {
                FieldNames = ci.FieldNames,
                FieldCount = ci.FieldCount,
                IsAbstract = ci.IsAbstract,
                ClassId = i,
            };
        }

        for (int i = 0; i < image.Classes.Length; i++)
        {
            var ci = image.Classes[i];
            var cls = classes[i];
            if (ci.BaseIndex >= 0) cls.Base = classes[ci.BaseIndex];
            foreach (var id in ci.InterfaceIds) cls.InterfaceIds.Add(id);
            // 静态字段先置 Null（初值在模块 init 中赋），避免初始化期访问报错
            foreach (var sf in ci.StaticFieldNames) cls.Statics[sf] = Value.Null;

            var methodIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var methods = new List<IkdFunction>();
            for (var b = cls.Base; b is not null; b = b.Base)
            {
                foreach (var kv in b.MethodIndex)
                    if (!methodIndex.ContainsKey(kv.Key))
                    {
                        methodIndex[kv.Key] = methods.Count;
                        methods.Add(b.Methods[kv.Value]);
                    }
            }
            foreach (var (mname, fidx) in ci.Methods)
            {
                var proto = image.Functions[fidx];
                if (methodIndex.TryGetValue(mname, out int slot)) methods[slot] = proto;
                else { methodIndex[mname] = methods.Count; methods.Add(proto); }
            }
            cls.MethodIndex.Clear();
            foreach (var kv in methodIndex) cls.MethodIndex[kv.Key] = kv.Value;
            cls.Methods = methods.ToArray();
        }
        return classes;
    }

    private static IkdEnumType[] BuildEnums(ModuleImage image)
    {
        var enums = new IkdEnumType[image.Enums.Length];
        for (int i = 0; i < image.Enums.Length; i++)
        {
            var ei = image.Enums[i];
            enums[i] = new IkdEnumType(ei.Name, ei.CaseNames, ei.CaseArities);
        }
        return enums;
    }

    private IkdClosure ClosureFor(IkdFunction fn, Value[]? cells = null)
        => new(fn, cells ?? Array.Empty<Value>());

    private void RunInit(RuntimeModule module)
    {
        if (module.Image.InitFunction < 0) return;
        var fn = module.Image.Functions[module.Image.InitFunction];
        fn.Module = module;
        int callee = _sp;
        Push(Value.Null);
        int stop = _fp;
        PushFrame(fn, 0, null, ClosureFor(fn), module);
        Execute(stop);
        _sp = callee;
    }

    // ---------------------------------------------------------------
    //  调用
    // ---------------------------------------------------------------

    private void PushFrame(IkdFunction fn, int argc, object? thisValue, IkdClosure? closure,
        RuntimeModule module, bool returnThis = false)
    {
        if (_fp >= _frames.Length)
            throw new IkdRuntimeException("RuntimeError", "调用栈过深");
        int baseIdx = _sp - argc;
        int need = baseIdx + fn.LocalCount + fn.MaxStack + 8;
        Ensure(need);
        for (int i = argc; i < fn.LocalCount; i++) _stack[baseIdx + i] = Value.Null;
        _sp = baseIdx + fn.LocalCount;
        _frames[_fp++] = new Frame
        {
            Fn = fn,
            Module = module,
            Closure = closure,
            This = thisValue,
            Base = baseIdx,
            Ip = 0,
            TryMark = _tryCount,
            ReturnThis = returnThis,
        };
    }

    /// <summary>从原生函数内部调用一个 ikd 可调用对象。</summary>
    public Value Invoke(Value callee, Value[] args)
    {
        int resultIdx = _sp;
        Push(callee);
        foreach (var a in args) Push(a);
        int stop = _fp;
        int before = _fp;
        CallValue(args.Length, before);
        if (_fp > before) Execute(stop);
        _sp = resultIdx;
        return _stack[resultIdx];
    }

    /// <summary>调用位于 _sp-argc-1 处的值；argc 个参数已入栈。</summary>
    private void CallValue(int argc, int stopFp)
    {
        int calleeIdx = _sp - argc - 1;
        var callee = _stack[calleeIdx];

        if (callee.Kind == ValueKind.Ref)
        {
            switch (callee.AsRef)
            {
                case IkdClosure cl:
                {
                    if (argc != cl.Proto.Arity) throw ArityError(cl.Proto, argc);
                    PushFrame(cl.Proto, argc, null, cl, GetModule(cl.Proto));
                    return;
                }
                case IkdNativeFn nf:
                {
                    if (nf.Arity >= 0 && argc != nf.Arity)
                        throw new IkdRuntimeException("TypeError",
                            $"函数 {nf.Name} 需要 {nf.Arity} 个参数，实得 {argc} 个");
                    var args = new Value[argc];
                    Array.Copy(_stack, calleeIdx + 1, args, 0, argc);
                    _sp = calleeIdx;
                    Push(nf.Invoke(this, args));
                    return;
                }
                case BoundNative bn:
                {
                    if (bn.Fn.Arity >= 0 && argc != bn.Fn.Arity)
                        throw new IkdRuntimeException("TypeError",
                            $"方法 {bn.Fn.Name} 需要 {bn.Fn.Arity} 个参数，实得 {argc} 个");
                    var args = new Value[argc + 1];
                    args[0] = bn.Recv;
                    Array.Copy(_stack, calleeIdx + 1, args, 1, argc);
                    _sp = calleeIdx;
                    Push(bn.Fn.Invoke(this, args));
                    return;
                }
                case IkdBoundMethod bm:
                {
                    if (argc != bm.Fn.Arity) throw ArityError(bm.Fn, argc);
                    bm.Cached ??= ClosureFor(bm.Fn);
                    PushFrame(bm.Fn, argc, bm.Receiver, bm.Cached, GetModule(bm.Fn));
                    return;
                }
                case EnumCaseCtor ec:
                {
                    if (argc != ec.Arity)
                        throw new IkdRuntimeException("TypeError",
                            $"枚举成员 {ec} 需要 {ec.Arity} 个参数，实得 {argc} 个");
                    var payload = new Value[argc];
                    Array.Copy(_stack, calleeIdx + 1, payload, 0, argc);
                    _sp = calleeIdx;
                    Push(Value.OfRef(new IkdEnumValue(ec.EnumName, ec.CaseName,
                        ec.CaseIndex, payload)));
                    return;
                }
                case IkdClass kc:
                {
                    // 类值作可调用对象：等价于 new K(...)
                    if (kc.IsAbstract)
                        throw new IkdRuntimeException("TypeError",
                            $"抽象类 {kc.Name} 不能实例化");
                    var inst = new IkdInstance(kc);
                    _stack[calleeIdx] = Value.OfRef(inst);

                    if (!kc.MethodIndex.TryGetValue("init", out int initSlot))
                    {
                        if (argc != 0)
                            throw new IkdRuntimeException("TypeError",
                                $"类 {kc.Name} 没有构造器 init，不能传参数");
                        _sp = calleeIdx;
                        Push(Value.OfRef(inst));
                        return;
                    }
                    var initFn = kc.Methods[initSlot];
                    if (argc != initFn.Arity)
                        throw ArityError(initFn, argc);
                    PushFrame(initFn, argc, inst, ClosureFor(initFn), GetModule(initFn),
                        returnThis: true);
                    return;
                }
            }
        }

        throw new IkdRuntimeException("TypeError", $"{StdLib.TypeNameOf(callee)} 不可调用");
    }

    private RuntimeModule GetModule(IkdFunction fn)
    {
        if (fn.Module is not null) return fn.Module;
        if (_modules.TryGetValue(fn.ModulePath, out var m)) return m;
        foreach (var kv in _modules)
            if (Array.IndexOf(kv.Value.Image.Functions, fn) >= 0) return kv.Value;
        throw new IkdRuntimeException("InternalError", $"函数 {fn.Name} 未绑定模块");
    }

    private IkdRuntimeException ArityError(IkdFunction fn, int argc)
        => new("TypeError", $"函数 {fn.Name} 需要 {fn.Arity} 个参数，实得 {argc} 个");

    // ---------------------------------------------------------------
    //  异常处理
    // ---------------------------------------------------------------

    private bool HandleThrow(Value error, int stopFp)
    {
        while (_fp > stopFp)
        {
            int frameIdx = _fp - 1;
            var f = _frames[frameIdx];
            int found = -1;
            for (int i = _tryCount - 1; i >= f.TryMark; i--)
            {
                if (_tries[i].FrameIdx == frameIdx && _tries[i].PushIp <= f.Ip)
                {
                    found = i;
                    break;
                }
            }
            if (found >= 0)
            {
                var entry = _tries[found];
                _sp = entry.StackTop;
                _tryCount = found;
                Push(error);
                f.Ip = entry.HandlerIp;
                _frames[frameIdx] = f;
                return true;
            }
            _tryCount = f.TryMark;
            _fp--;
        }
        return false;
    }

    // ---------------------------------------------------------------
    //  主循环
    // ---------------------------------------------------------------

    private void Execute(int stopFp)
    {
        while (true)
        {
            try
            {
                RunLoop(stopFp);
                return;
            }
            catch (IkdRuntimeException ex)
            {
                if (HandleThrow(ex.Error, stopFp)) continue;
                throw;
            }
        }
    }

    private void RunLoop(int stopFp)
    {
        while (_fp > stopFp)
        {
            ref var frame = ref _frames[_fp - 1];
            var fn = frame.Fn;
            var code = fn.Code;
            var module = frame.Module;
            int ip = frame.Ip;

            try
            {
                while (true)
                {
                    var op = (Op)code[ip++];
                    switch (op)
                    {
                        case Op.Nop: break;
                        case Op.Const:
                        {
                            uint idx = Disassembler.ReadU32(code, ref ip);
                            Push(fn.Constants[idx]);
                            break;
                        }
                        case Op.Null: Push(Value.Null); break;
                        case Op.True: Push(Value.True); break;
                        case Op.False: Push(Value.False); break;
                        case Op.Pop: _sp--; break;
                        case Op.PopN: _sp -= code[ip++]; break;
                        case Op.Dup: Push(Peek()); break;

                        case Op.LoadLocal: Push(_stack[frame.Base + Disassembler.ReadU16(code, ref ip)]); break;
                        case Op.DefLocal: _stack[frame.Base + Disassembler.ReadU16(code, ref ip)] = Pop(); break;
                        case Op.StoreLocal: _stack[frame.Base + Disassembler.ReadU16(code, ref ip)] = Peek(); break;
                        case Op.LoadLocalCell:
                        {
                            var slot = _stack[frame.Base + Disassembler.ReadU16(code, ref ip)];
                            Push(slot.AsRef is Cell c ? c.Value : slot);
                            break;
                        }
                        case Op.DefLocalCell:
                        {
                            int slot = frame.Base + Disassembler.ReadU16(code, ref ip);
                            _stack[slot] = Value.OfRef(new Cell(Pop()));
                            break;
                        }
                        case Op.StoreLocalCell:
                        {
                            int slot = frame.Base + Disassembler.ReadU16(code, ref ip);
                            if (_stack[slot].AsRef is Cell c) c.Value = Peek();
                            break;
                        }
                        case Op.LoadUpvalue:
                        {
                            int idx = Disassembler.ReadU16(code, ref ip);
                            var cellVal = frame.Closure!.Cells[idx];
                            Push(cellVal.AsRef is Cell c ? c.Value : cellVal);
                            break;
                        }
                        case Op.StoreUpvalue:
                        {
                            int idx = Disassembler.ReadU16(code, ref ip);
                            if (frame.Closure!.Cells[idx].AsRef is Cell c) c.Value = Peek();
                            break;
                        }
                        case Op.LoadGlobal:
                            Push(module.Globals[Disassembler.ReadU32(code, ref ip)]);
                            break;
                        case Op.DefGlobal:
                            module.Globals[Disassembler.ReadU32(code, ref ip)] = Pop();
                            break;
                        case Op.StoreGlobal:
                            module.Globals[Disassembler.ReadU32(code, ref ip)] = Peek();
                            break;
                        case Op.LoadGlobalCell:
                        {
                            var g = module.Globals[Disassembler.ReadU32(code, ref ip)];
                            Push(g.AsRef is Cell c ? c.Value : g);
                            break;
                        }
                        case Op.DefGlobalCell:
                            module.Globals[Disassembler.ReadU32(code, ref ip)] =
                                Value.OfRef(new Cell(Pop()));
                            break;
                        case Op.StoreGlobalCell:
                        {
                            if (module.Globals[Disassembler.ReadU32(code, ref ip)].AsRef is Cell c)
                                c.Value = Peek();
                            break;
                        }

                        case Op.Closure:
                        {
                            uint fidx = Disassembler.ReadU32(code, ref ip);
                            int n = code[ip++];
                            var cells = new Value[n];
                            for (int i = 0; i < n; i++)
                            {
                                byte flags = code[ip++];
                                ushort uidx = Disassembler.ReadU16(code, ref ip);
                                cells[i] = (flags & 1) != 0 ? _stack[frame.Base + uidx]
                                    : (flags & 2) != 0 ? module.Globals[uidx]
                                    : frame.Closure!.Cells[uidx];
                            }
                            var proto = module.Image.Functions[fidx];
                            proto.Module = module;
                            Push(Value.OfRef(new IkdClosure(proto, cells)));
                            break;
                        }

                        case Op.GetProp:
                        {
                            var name = ModuleName(module, Disassembler.ReadU32(code, ref ip));
                            var recv = Pop();
                            Push(GetProperty(recv, name));
                            break;
                        }
                        case Op.SetProp:
                        {
                            var name = ModuleName(module, Disassembler.ReadU32(code, ref ip));
                            var value = Pop();
                            var recv = Pop();
                            SetProperty(recv, name, value);
                            Push(value);
                            break;
                        }
                        case Op.CallProp:
                        {
                            var name = ModuleName(module, Disassembler.ReadU32(code, ref ip));
                            int argc = code[ip++];
                            int calleeIdx = _sp - argc - 1;
                            var recv = _stack[calleeIdx];
                            _stack[calleeIdx] = ResolveCallCallee(recv, name);
                            int before = _fp;
                            CallValue(argc, before);
                            if (_fp > before) goto nextFrame;
                            break;
                        }
                        case Op.GetSuper:
                        {
                            var name = ModuleName(module, Disassembler.ReadU32(code, ref ip));
                            var mfn = FindBaseMethod(frame.This, name);
                            Push(Value.OfRef(new IkdBoundMethod(frame.This!, mfn)));
                            break;
                        }
                        case Op.CallSuper:
                        {
                            var name = ModuleName(module, Disassembler.ReadU32(code, ref ip));
                            int argc = code[ip++];
                            var mfn = FindBaseMethod(frame.This, name);
                            if (argc != mfn.Arity) throw ArityError(mfn, argc);
                            PushFrame(mfn, argc, frame.This, ClosureFor(mfn), GetModule(mfn));
                            goto nextFrame;
                        }
                        case Op.Call:
                        {
                            int argc = code[ip++];
                            int before = _fp;
                            CallValue(argc, before);
                            if (_fp > before) goto nextFrame;
                            break;
                        }
                        case Op.Return:
                        {
                            var ret0 = Pop();
                            _fp--;
                            var f2 = _frames[_fp];
                            _tryCount = f2.TryMark;
                            var ret = f2.ReturnThis && f2.This is not null
                                ? Value.OfRef(f2.This)
                                : ret0;
                            _sp = f2.Base - 1;
                            Push(ret);
                            if (_fp == stopFp) return;
                            goto nextFrame;
                        }

                        case Op.NewInstance:
                        {
                            var cls = module.Classes[Disassembler.ReadU32(code, ref ip)];
                            if (cls.IsAbstract)
                                throw new IkdRuntimeException("TypeError",
                                    $"抽象类 {cls.Name} 不能实例化");
                            Push(Value.OfRef(new IkdInstance(cls)));
                            break;
                        }
                        case Op.LoadClass:
                            Push(Value.OfRef(module.Classes[Disassembler.ReadU32(code, ref ip)]));
                            break;
                        case Op.LoadEnumType:
                            Push(Value.OfRef(module.Enums[Disassembler.ReadU32(code, ref ip)]));
                            break;
                        case Op.GetEnumConst:
                        {
                            uint eIdx = Disassembler.ReadU32(code, ref ip);
                            uint cIdx = Disassembler.ReadU32(code, ref ip);
                            var en = module.Enums[eIdx];
                            Push(Value.OfRef(new IkdEnumValue(en.Name, en.CaseNames[cIdx],
                                (int)cIdx, Array.Empty<Value>())));
                            break;
                        }
                        case Op.NewEnumCase:
                        {
                            uint eIdx = Disassembler.ReadU32(code, ref ip);
                            uint cIdx = Disassembler.ReadU32(code, ref ip);
                            int arity = code[ip++];
                            var en = module.Enums[eIdx];
                            var payload = new Value[arity];
                            for (int i = arity - 1; i >= 0; i--) payload[i] = Pop();
                            Push(Value.OfRef(new IkdEnumValue(en.Name, en.CaseNames[cIdx],
                                (int)cIdx, payload)));
                            break;
                        }
                        case Op.IsEnumCase:
                        {
                            uint eIdx = Disassembler.ReadU32(code, ref ip);
                            uint cIdx = Disassembler.ReadU32(code, ref ip);
                            var en = module.Enums[eIdx];
                            var v = Peek();
                            Push(Value.Of(v.Kind == ValueKind.Ref && v.AsRef is IkdEnumValue ev
                                && ev.EnumName == en.Name && ev.CaseIndex == (int)cIdx));
                            break;
                        }
                        case Op.GetEnumPayload:
                        {
                            int idx = (int)Disassembler.ReadU32(code, ref ip);
                            var v = Pop();
                            if (v.AsRef is not IkdEnumValue ev)
                                throw new IkdRuntimeException("TypeError", "期望枚举值");
                            Push(idx < ev.Payload.Length ? ev.Payload[idx] : Value.Null);
                            break;
                        }
                        case Op.IsType:
                        {
                            uint tdIdx = Disassembler.ReadU32(code, ref ip);
                            var v = Pop();
                            Push(Value.Of(IsType(v, module, module.Image.TypeDescriptors[tdIdx])));
                            break;
                        }
                        case Op.Cast:
                        {
                            uint tdIdx = Disassembler.ReadU32(code, ref ip);
                            Push(CastValue(Pop(), module.Image.TypeDescriptors[tdIdx]));
                            break;
                        }

                        case Op.Jump:
                            ip = (int)Disassembler.ReadU32(code, ref ip);
                            break;
                        case Op.JumpIfFalse:
                        {
                            uint target = Disassembler.ReadU32(code, ref ip);
                            if (!Pop().Truthy) ip = (int)target;
                            break;
                        }
                        case Op.JumpIfTrue:
                        {
                            uint target = Disassembler.ReadU32(code, ref ip);
                            if (Pop().Truthy) ip = (int)target;
                            break;
                        }

                        case Op.Add:
                        {
                            var b = Pop();
                            var a = Pop();
                            Push(AddValues(a, b));
                            break;
                        }
                        case Op.Sub: BinNum((a, b) => a - b, (a, b) => a - b); break;
                        case Op.Mul: BinNum((a, b) => a * b, (a, b) => a * b); break;
                        case Op.Div: BinNum(DivInt, (a, b) => a / b); break;
                        case Op.Mod: BinNum(ModInt, (a, b) => a % b); break;
                        case Op.Neg:
                        {
                            var v = Pop();
                            Push(v.Kind == ValueKind.Int ? Value.Of(-v.AsLong) : Value.Of(-v.AsDouble));
                            break;
                        }
                        case Op.Not: Push(Value.Of(!Pop().Truthy)); break;
                        case Op.Eq:
                        {
                            var b = Pop();
                            var a = Pop();
                            Push(Value.Of(StdLib.ValuesEqual(a, b)));
                            break;
                        }
                        case Op.Ne:
                        {
                            var b = Pop();
                            var a = Pop();
                            Push(Value.Of(!StdLib.ValuesEqual(a, b)));
                            break;
                        }
                        case Op.Lt: Push(Value.Of(Cmp(-1, orEqual: false))); break;
                        case Op.Le: Push(Value.Of(Cmp(-1, orEqual: true))); break;
                        case Op.Gt: Push(Value.Of(Cmp(1, orEqual: false))); break;
                        case Op.Ge: Push(Value.Of(Cmp(1, orEqual: true))); break;

                        case Op.Concat:
                        {
                            int n = code[ip++];
                            var sb = new StringBuilder();
                            for (int i = _sp - n; i < _sp; i++) sb.Append(_stack[i].ToStr());
                            _sp -= n;
                            Push(Value.OfStr(sb.ToString()));
                            break;
                        }
                        case Op.IndexGet:
                        {
                            var idx = Pop();
                            var target = Pop();
                            Push(IndexGet(target, idx));
                            break;
                        }
                        case Op.IndexSet:
                        {
                            var value = Pop();
                            var idx = Pop();
                            var target = Pop();
                            IndexSet(target, idx, value);
                            Push(value);
                            break;
                        }
                        case Op.NewList:
                        {
                            int n = (int)Disassembler.ReadU32(code, ref ip);
                            var items = new Value[n];
                            for (int i = n - 1; i >= 0; i--) items[i] = Pop();
                            Push(Value.OfRef(new IkdList(items)));
                            break;
                        }
                        case Op.NewMap:
                        {
                            int n = (int)Disassembler.ReadU32(code, ref ip);
                            var keys = new Value[n];
                            var vals = new Value[n];
                            for (int i = n - 1; i >= 0; i--)
                            {
                                vals[i] = Pop();
                                keys[i] = Pop();
                            }
                            var map = new IkdMap();
                            for (int i = 0; i < n; i++)
                            {
                                var nk = IkdMap.NormalizeKey(keys[i]);
                                if (nk is null)
                                    throw new IkdRuntimeException("TypeError", "该值不能作为映射的键");
                                map.Pairs[nk] = vals[i];
                            }
                            Push(Value.OfRef(map));
                            break;
                        }
                        case Op.IterNew:
                            Push(Value.OfRef(MakeIterator(Pop())));
                            break;
                        case Op.IterNext:
                        {
                            uint elseIp = Disassembler.ReadU32(code, ref ip);
                            var it = Peek();
                            if (it.AsRef is IkdIterable iterable && iterable.TryNext(out var next))
                                Push(next);
                            else
                            {
                                _sp--;
                                ip = (int)elseIp;
                            }
                            break;
                        }

                        case Op.Import:
                        {
                            uint idx = Disassembler.ReadU32(code, ref ip);
                            var path = module.Image.Imports[idx];
                            frame.Ip = ip;
                            Push(Value.OfRef(LoadModule(path).Namespace));
                            break;
                        }
                        case Op.LoadNative:
                        {
                            uint idx = Disassembler.ReadU32(code, ref ip);
                            var name = _image.NativeNames[idx];
                            var native = StdLib.GetNative(name)
                                ?? throw new IkdRuntimeException("ImportError", $"没有内置函数 {name}");
                            Push(Value.OfRef(native));
                            break;
                        }

                        case Op.TryPush:
                        {
                            uint handler = Disassembler.ReadU32(code, ref ip);
                            if (_tryCount >= _tries.Length)
                                throw new IkdRuntimeException("RuntimeError", "try 嵌套过深");
                            _tries[_tryCount++] = new TryEntry
                            {
                                FrameIdx = _fp - 1,
                                PushIp = ip,
                                HandlerIp = (int)handler,
                                StackTop = _sp,
                            };
                            break;
                        }
                        case Op.TryPop: _tryCount--; break;
                        case Op.Throw:
                        {
                            var err = Pop();
                            throw new IkdRuntimeException(err);
                        }
                        case Op.MatchFail:
                        {
                            var subject = Pop();
                            throw new IkdRuntimeException("MatchError",
                                "match 没有匹配任何分支: " + subject.ToDisplayString());
                        }
                        case Op.LoadThis:
                            if (frame.This is null)
                                throw new IkdRuntimeException("TypeError", "this 只能在类方法里使用");
                            Push(Value.OfRef(frame.This));
                            break;

                        default:
                            throw new IkdRuntimeException("InternalError", $"未知操作码 {op}");
                    }
                }
            nextFrame:
                frame.Ip = ip;
                continue;
            }
            catch (IkdRuntimeException)
            {
                frame.Ip = ip;
                throw;
            }
        }
    }

    private static string ModuleName(RuntimeModule module, uint idx)
        => module.Image.Constants[idx].AsString.Value;

    private static IkdFunction FindBaseMethod(object? thisValue, string name)
    {
        if (thisValue is not IkdInstance inst)
            throw new IkdRuntimeException("TypeError", "super 只能在类方法中使用");
        for (var c = inst.Class.Base; c is not null; c = c.Base)
            if (c.MethodIndex.TryGetValue(name, out int i))
                return c.Methods[i];
        throw new IkdRuntimeException("AttributeError",
            $"基类中没有方法 {name}");
    }

    private void BinNum(Func<long, long, long> fi, Func<double, double, double> ff)
    {
        var b = Pop();
        var a = Pop();
        if (a.Kind == ValueKind.Int && b.Kind == ValueKind.Int)
            Push(Value.Of(fi(a.AsLong, b.AsLong)));
        else
            Push(Value.Of(ff(ToDouble(a), ToDouble(b))));
    }

    private static double ToDouble(Value v) => v.Kind == ValueKind.Int ? v.AsLong : v.AsDouble;

    private static long DivInt(long a, long b)
    {
        if (b == 0) throw new IkdRuntimeException("ZeroDivisionError", "整数除以 0");
        return a / b;
    }

    private static long ModInt(long a, long b)
    {
        if (b == 0) throw new IkdRuntimeException("ZeroDivisionError", "整数取模 0");
        return a % b;
    }

    private bool Cmp(int direction, bool orEqual)
    {
        var b = Pop();
        var a = Pop();
        int c = StdLib.CompareValues(a, b);
        return orEqual
            ? direction > 0 ? c >= 0 : c <= 0
            : direction > 0 ? c > 0 : c < 0;
    }

    private static Value AddValues(Value a, Value b)
    {
        if (a.IsStr || b.IsStr)
            return Value.OfStr(a.ToStr() + b.ToStr());
        if (a.Kind == ValueKind.Int && b.Kind == ValueKind.Int)
            return Value.Of(a.AsLong + b.AsLong);
        if (a.Kind is ValueKind.Int or ValueKind.Float && b.Kind is ValueKind.Int or ValueKind.Float)
            return Value.Of(ToDouble(a) + ToDouble(b));
        throw new IkdRuntimeException("TypeError",
            $"不能对 {StdLib.TypeNameOf(a)} 和 {StdLib.TypeNameOf(b)} 使用 +");
    }

    // ---------------------------------------------------------------
    //  成员访问
    // ---------------------------------------------------------------

    private Value GetProperty(Value recv, string name)
    {
        if (recv.Kind == ValueKind.Ref)
        {
            switch (recv.AsRef)
            {
                case IkdInstance inst:
                {
                    var fields = inst.Class.FieldNames;
                    for (int i = 0; i < fields.Length; i++)
                        if (fields[i] == name)
                            return inst.Fields[i];
                    if (FindMethod(inst.Class, name, out var mfn))
                        return Value.OfRef(new IkdBoundMethod(inst, mfn));
                    if (inst.Class.Statics.TryGetValue(name, out var sv)) return sv;
                    throw new IkdRuntimeException("AttributeError",
                        $"{inst.Class.Name} 没有成员 {name}");
                }
                case IkdClass cls:
                    if (cls.Statics.TryGetValue(name, out var st)) return st;
                    throw new IkdRuntimeException("AttributeError",
                        $"类 {cls.Name} 没有静态成员 {name}");
                case IkdNamespace ns:
                    if (ns.TryGetMember(name, out var mv)) return mv;
                    throw new IkdRuntimeException("AttributeError",
                        $"模块 {ns.Name} 没有成员 {name}");
                case IkdError err:
                    if (name == "message") return Value.OfStr(err.Message);
                    if (name == "kind") return Value.OfStr(err.Kind);
                    return BuiltinMember(recv, name);
                case IkdEnumType en:
                {
                    int c = en.FindCase(name);
                    if (c < 0) return BuiltinMember(recv, name);
                    if (en.CaseArities[c] != 0)
                        return Value.OfRef(new EnumCaseCtor(en.Name, name, c, en.CaseArities[c]));
                    return Value.OfRef(new IkdEnumValue(en.Name, name, c, Array.Empty<Value>()));
                }
                case IkdClosure or IkdNativeFn or IkdBoundMethod:
                    throw new IkdRuntimeException("AttributeError", $"函数没有成员 {name}");
            }
        }
        return BuiltinMember(recv, name);
    }

    private Value BuiltinMember(Value recv, string name)
    {
        if (StdLib.GetBuiltinMethod(recv, name) is { } native)
            return Value.OfRef(new BoundNative(recv, native));
        if (name is "toStr" or "toString")
            return Value.OfRef(new BoundNative(recv,
                new IkdNativeFn("toStr", 0, (_, a) => Value.OfStr(a[0].ToStr()))));
        throw new IkdRuntimeException("AttributeError",
            $"{StdLib.TypeNameOf(recv)} 没有成员 {name}");
    }

    private void SetProperty(Value recv, string name, Value value)
    {
        if (recv.Kind == ValueKind.Ref && recv.AsRef is IkdInstance inst)
        {
            var fields = inst.Class.FieldNames;
            for (int i = 0; i < fields.Length; i++)
                if (fields[i] == name)
                {
                    inst.Fields[i] = value;
                    return;
                }
            throw new IkdRuntimeException("AttributeError", $"{inst.Class.Name} 没有字段 {name}");
        }
        if (recv.Kind == ValueKind.Ref && recv.AsRef is IkdClass cls)
        {
            cls.Statics[name] = value;
            return;
        }
        throw new IkdRuntimeException("AttributeError",
            $"不能给 {StdLib.TypeNameOf(recv)} 的 {name} 赋值");
    }

    /// <summary>把 CallProp 的接收者槽替换为真正的被调用值。</summary>
    private Value ResolveCallCallee(Value recv, string name)
    {
        if (recv.Kind == ValueKind.Ref)
        {
            switch (recv.AsRef)
            {
                case IkdInstance inst:
                {
                    if (FindMethod(inst.Class, name, out var mfn))
                        return Value.OfRef(new IkdBoundMethod(inst, mfn));
                    var fields = inst.Class.FieldNames;
                    for (int i = 0; i < fields.Length; i++)
                        if (fields[i] == name)
                            return inst.Fields[i];
                    if (inst.Class.Statics.TryGetValue(name, out var sv)) return sv;
                    throw new IkdRuntimeException("AttributeError",
                        $"{inst.Class.Name} 没有方法 {name}");
                }
                case IkdClass cls:
                    if (cls.Statics.TryGetValue(name, out var st)) return st;
                    throw new IkdRuntimeException("AttributeError",
                        $"类 {cls.Name} 没有静态方法 {name}");
                case IkdNamespace ns:
                    if (ns.TryGetMember(name, out var mv)) return mv;
                    throw new IkdRuntimeException("AttributeError",
                        $"模块 {ns.Name} 没有函数 {name}");
            }
        }
        if (StdLib.GetBuiltinMethod(recv, name) is { } native)
            return Value.OfRef(new BoundNative(recv, native));
        if (name is "toStr" or "toString")
            return Value.OfRef(new BoundNative(recv,
                new IkdNativeFn("toStr", 0, (_, a) => Value.OfStr(a[0].ToStr()))));
        throw new IkdRuntimeException("AttributeError",
            $"{StdLib.TypeNameOf(recv)} 没有方法 {name}");
    }

    private static bool FindMethod(IkdClass cls, string name, out IkdFunction fn)
    {
        for (var c = cls; c is not null; c = c.Base)
            if (c.MethodIndex.TryGetValue(name, out int i))
            {
                fn = c.Methods[i];
                return true;
            }
        fn = null!;
        return false;
    }

    // ---------------------------------------------------------------
    //  迭代 / 索引 / 类型
    // ---------------------------------------------------------------

    private static IkdIterable MakeIterator(Value v)
    {
        switch (v.AsRef)
        {
            case IkdIterable it: return it;
            case IkdList list: return new ListIter(list);
            case IkdMap map: return new MapIter(map);
            case IkdString s: return new StrIter(s.Value);
        }
        throw new IkdRuntimeException("TypeError", $"{StdLib.TypeNameOf(v)} 不可迭代");
    }

    private static Value IndexGet(Value target, Value idx)
    {
        if (target.AsRef is IkdList list)
        {
            long i = StdLib.ToIntOrThrow(idx);
            if (i < 0) i += list.Items.Count;
            if (i < 0 || i >= list.Items.Count)
                throw new IkdRuntimeException("IndexError",
                    $"下标 {i} 超出范围 (长度 {list.Items.Count})");
            return list.Items[(int)i];
        }
        if (target.AsRef is IkdMap map)
        {
            var key = IkdMap.NormalizeKey(idx);
            return key is not null && map.Pairs.TryGetValue(key, out var v) ? v : Value.Null;
        }
        if (target.AsRef is IkdString s)
        {
            long i = StdLib.ToIntOrThrow(idx);
            if (i < 0) i += s.Value.Length;
            if (i < 0 || i >= s.Value.Length)
                throw new IkdRuntimeException("IndexError",
                    $"下标 {i} 超出范围 (长度 {s.Value.Length})");
            return Value.OfStr(s.Value[(int)i].ToString());
        }
        throw new IkdRuntimeException("TypeError", $"{StdLib.TypeNameOf(target)} 不支持下标访问");
    }

    private static void IndexSet(Value target, Value idx, Value value)
    {
        if (target.AsRef is IkdList list)
        {
            long i = StdLib.ToIntOrThrow(idx);
            if (i < 0) i += list.Items.Count;
            if (i < 0 || i >= list.Items.Count)
                throw new IkdRuntimeException("IndexError",
                    $"下标 {i} 超出范围 (长度 {list.Items.Count})");
            list.Items[(int)i] = value;
            return;
        }
        if (target.AsRef is IkdMap map)
        {
            var key = IkdMap.NormalizeKey(idx);
            if (key is null)
                throw new IkdRuntimeException("TypeError", "该值不能作为映射的键");
            map.Pairs[key] = value;
            return;
        }
        throw new IkdRuntimeException("TypeError",
            $"{StdLib.TypeNameOf(target)} 不支持下标赋值");
    }

    private bool IsType(Value v, RuntimeModule module, TypeDesc desc)
    {
        switch (desc.Kind)
        {
            case TypeDesc.TypeDescKind.Any:
            case TypeDesc.TypeDescKind.TypeParam:
            case TypeDesc.TypeDescKind.Void:
                return true;
            case TypeDesc.TypeDescKind.Error:
                return v.AsRef is IkdError;
            case TypeDesc.TypeDescKind.Null:
                return v.IsNull;
            case TypeDesc.TypeDescKind.Int:
                return v.Kind == ValueKind.Int;
            case TypeDesc.TypeDescKind.Float:
                return v.Kind == ValueKind.Float;
            case TypeDesc.TypeDescKind.Str:
                return v.IsStr;
            case TypeDesc.TypeDescKind.Bool:
                return v.Kind == ValueKind.Bool;
            case TypeDesc.TypeDescKind.List:
                return v.AsRef is IkdList;
            case TypeDesc.TypeDescKind.Map:
                return v.AsRef is IkdMap;
            case TypeDesc.TypeDescKind.Fn:
                return v.AsRef is IkdClosure or IkdNativeFn or IkdBoundMethod or BoundNative;
            case TypeDesc.TypeDescKind.Class:
            {
                var cls = ResolveClass(module, desc);
                return v.AsRef is IkdInstance inst && inst.Class.IsSubclassOf(cls);
            }
            case TypeDesc.TypeDescKind.Interface:
                return v.AsRef is IkdInstance inst2 && HasInterface(inst2.Class, desc.InterfaceId);
            case TypeDesc.TypeDescKind.Enum:
            {
                var en = module.Enums[desc.EnumIndex];
                return v.AsRef is IkdEnumValue ev && ev.EnumName == en.Name;
            }
            default:
                return false;
        }
    }

    private static bool HasInterface(IkdClass cls, int id)
    {
        for (var c = cls; c is not null; c = c.Base)
            if (c.InterfaceIds.Contains(id)) return true;
        return false;
    }

    private static IkdClass ResolveClass(RuntimeModule module, TypeDesc desc)
    {
        if (desc.Resolved is IkdClass cached) return cached;
        var cls = module.Classes[desc.ClassIndex];
        desc.Resolved = cls;
        return cls;
    }

    private static Value CastValue(Value v, TypeDesc desc)
    {
        if (desc.Kind == TypeDesc.TypeDescKind.Float && v.Kind == ValueKind.Int)
            return Value.Of((double)v.AsLong);
        if (desc.Kind == TypeDesc.TypeDescKind.Int && v.Kind == ValueKind.Float)
            return Value.Of((long)v.AsDouble);
        if (desc.Kind == TypeDesc.TypeDescKind.Int && v.IsStr)
            return Value.Of(StdLib.ToIntOrThrow(v));
        return v;
    }

    // ---------------------------------------------------------------
    //  入口
    // ---------------------------------------------------------------

    /// <summary>运行整个程序；返回进程退出码。</summary>
    public int Run(string[] args)
    {
        try
        {
            var entry = LoadModule(_image.EntryName);

            Value? main = null;
            for (int i = 0; i < entry.Image.GlobalCount; i++)
                if (entry.Image.GlobalNames[i] == "main")
                {
                    var g = entry.Globals[i];
                    main = g.AsRef is Cell mc ? mc.Value : g;
                    break;
                }

            if (main is { } mv && !mv.IsNull)
            {
                var argList = Value.OfRef(new IkdList(args.Select(Value.OfStr)));
                Value ret;
                if (mv.AsRef is IkdNativeFn nc && nc.Arity == 1)
                    ret = Invoke(mv, new[] { argList });
                else
                    ret = Invoke(mv, mv.AsRef is IkdClosure mcc && mcc.Proto.Arity == 1
                        ? new[] { argList }
                        : Array.Empty<Value>());

                // main 的返回值（Int）作为进程退出码
                return ret.Kind == ValueKind.Int
                    ? (int)Math.Clamp(ret.AsLong, int.MinValue, int.MaxValue)
                    : 0;
            }
            return 0;
        }
        catch (IkdRuntimeException ex)
        {
            Console.Error.WriteLine("运行时错误: " + ex.Message);
            var trace = RenderStackTrace();
            if (trace.Length > 0) Console.Error.WriteLine(trace);
            return 1;
        }
    }

    public string RenderStackTrace()
    {
        var sb = new StringBuilder();
        for (int i = _fp - 1; i >= 0; i--)
        {
            var f = _frames[i];
            int line = f.Fn.GetLine(f.Ip);
            sb.Append("  at ").Append(f.Fn.Name);
            if (!string.IsNullOrEmpty(f.Fn.ModulePath))
            {
                sb.Append(" (").Append(f.Fn.ModulePath);
                if (line > 0) sb.Append(':').Append(line);
                sb.Append(')');
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
