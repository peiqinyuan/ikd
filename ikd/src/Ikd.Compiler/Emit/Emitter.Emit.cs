using Ikd.Compiler.Diagnostics;
using Ikd.Compiler.Syntax;
using Ikd.Runtime;

namespace Ikd.Compiler.Emit;

// ===================================================================
//  语句 / 声明发射
// ===================================================================

public sealed partial class Emitter
{
    private void EmitStatement(Statement stmt)
    {
        MarkLine(stmt);
        int X = _ctx!.Depth;
        switch (stmt)
        {
            case BlockStmt b:
                EmitBlockStatements(b);
                break;
            case ExprStmt e:
                EmitExpression(e.Expression);
                Pop(Op.Pop);
                break;
            case VarDeclStmt v:
                EmitVarDecl(v);
                break;
            case IfStmt i:
                EmitIf(i);
                break;
            case WhileStmt w:
                EmitWhile(w);
                break;
            case ForStmt f:
                EmitFor(f);
                break;
            case LoopStmt l:
                EmitLoop(l);
                break;
            case BreakStmt:
                EmitJumpCtrl(stmt, isBreak: true);
                break;
            case ContinueStmt:
                EmitJumpCtrl(stmt, isBreak: false);
                break;
            case ReturnStmt r:
                EmitReturn(r);
                break;
            case ThrowStmt t:
                EmitThrow(t);
                break;
            case TryStmt tr:
                EmitTry(tr);
                break;
            case FnDecl fn:
                EmitLocalFnDecl(fn);
                break;
            case ImportStmt im:
                _bag.Report(ErrorCode.ImportOutsideTopLevel, im.PathSpan,
                    "import 只能出现在模块顶层");
                break;
            case ClassDecl:
            case EnumDecl:
            case InterfaceDecl:
                _bag.Report(ErrorCode.MisplacedKeyword, stmt.Span,
                    "class/interface/enum 只能定义在模块顶层");
                break;
            default:
                Internal($"未处理的语句 {stmt.GetType().Name}");
                break;
        }

        if (_ctx.Depth != X)
        {
            Internal($"语句净栈高度不为 0: {stmt.GetType().Name}");
            SetDepth(X);
        }
    }

    private void EmitBlockStatements(BlockStmt block)
    {
        PushScope();
        EmitStatements(block);
        PopScope();
    }

    /// <summary>在当前作用域内发射一组语句（不再压新作用域）。</summary>
    private void EmitStatements(BlockStmt block)
    {
        // 1) 块级变量先建 Cell，保证闭包可前向捕获
        foreach (var s in block.Statements)
            if (s is VarDeclStmt vd)
            {
                var sym = DeclareLocal(vd.Name, vd.Kind == VarDeclKind.Const, vd.NameSpan);
                Push(Op.Null);
                PopU16(Op.DefLocalCell, sym.Index);
            }

        // 2) 块级 fn：提升到块首（声明 + 编译 + 闭包）
        foreach (var s in block.Statements)
            if (s is FnDecl f)
            {
                if (_ctx!.Scope.FindLocal(f.Name) is not null)
                    _bag.Report(ErrorCode.DuplicateDefinition, f.NameSpan,
                        $"'{f.Name}' 在同一作用域中重复定义");
                else
                    EmitLocalFnDecl(f);
            }

        // 3) 其余语句按文本顺序发射
        foreach (var s in block.Statements)
            if (s is not FnDecl)
                EmitStatement(s);
    }

    private void EmitLocalFnDecl(FnDecl f)
    {
        MarkLine(f);
        Symbol sym;
        if (_ctx!.Scope.FindLocal(f.Name) is { } existing)
            sym = existing;
        else
        {
            sym = DeclareLocal(f.Name, false, f.NameSpan);
            Push(Op.Null);
            PopU16(Op.DefLocalCell, sym.Index);
        }

        var fnCtx = CompileFn(f.Name, f.Params, f.ReturnType, f.Body, _ctx,
            isMethod: false, f.TypeParams);
        int index = ReserveFunction();
        FillFunction(index, fnCtx);
        EmitClosureOp(index, fnCtx);
        KeepU16(Op.StoreLocalCell, sym.Index);
        Pop(Op.Pop);
    }

    private void EmitVarDecl(VarDeclStmt v)
    {
        int X = _ctx!.Depth;
        if (v.Type is not null) ResolveTypeDesc(v.Type);

        if (IsAtModuleLevel())
        {
            if (!_globals.TryGetValue(v.Name, out int g))
                g = DeclareGlobal(v.Name, v.Kind == VarDeclKind.Const, v.NameSpan).Index;
            if (v.Value is not null) EmitExpression(v.Value);
            else Push(Op.Null);
            PopU32(Op.DefGlobalCell, g);
        }
        else
        {
            Symbol sym;
            if (_ctx.Scope.FindLocal(v.Name) is { } existing)
                sym = existing;
            else
            {
                sym = DeclareLocal(v.Name, v.Kind == VarDeclKind.Const, v.NameSpan);
                Push(Op.Null);
                PopU16(Op.DefLocalCell, sym.Index);
            }
            if (v.Value is not null) EmitExpression(v.Value);
            else Push(Op.Null);
            KeepU16(Op.StoreLocalCell, sym.Index);
            Pop(Op.Pop);
        }
        SetDepth(X);
    }

    // ----------------------------------------------------------------
    //  控制流
    // ----------------------------------------------------------------

    private void EmitIf(IfStmt s)
    {
        int X = _ctx!.Depth;
        EmitExpression(s.Cond);
        int elseJ = EmitJump(Op.JumpIfFalse);
        D(-1);
        EmitBlockStatements(s.Then);
        if (s.Else is not null)
        {
            int endJ = EmitJump(Op.Jump);
            Patch(elseJ);
            SetDepth(X);
            EmitStatement(s.Else);
            Patch(endJ);
            SetDepth(X);
        }
        else
        {
            Patch(elseJ);
            SetDepth(X);
        }
    }

    private void EmitWhile(WhileStmt s)
    {
        int X = _ctx!.Depth;
        int top = _ctx.Code.Count;
        EmitExpression(s.Cond);
        int exit = EmitJump(Op.JumpIfFalse);
        D(-1);
        var loop = new LoopCtx { ContinueTarget = top, TryDepth = _ctx.Tries.Count };
        _ctx.Loops.Add(loop);
        EmitBlockStatements(s.Body);
        int back = EmitJump(Op.Jump);
        PatchTo(back, top);
        Patch(exit);
        SetDepth(X);
        foreach (var b in loop.BreakJumps) PatchTo(b, _ctx.Code.Count);
        foreach (var c in loop.ContinueJumps) PatchTo(c, loop.ContinueTarget);
        _ctx.Loops.RemoveAt(_ctx.Loops.Count - 1);
    }

    private void EmitLoop(LoopStmt s)
    {
        int X = _ctx!.Depth;
        int top = _ctx.Code.Count;
        var loop = new LoopCtx { ContinueTarget = top, TryDepth = _ctx.Tries.Count };
        _ctx.Loops.Add(loop);
        EmitBlockStatements(s.Body);
        int back = EmitJump(Op.Jump);
        PatchTo(back, top);
        SetDepth(X);
        foreach (var b in loop.BreakJumps) PatchTo(b, _ctx.Code.Count);
        foreach (var c in loop.ContinueJumps) PatchTo(c, loop.ContinueTarget);
        _ctx.Loops.RemoveAt(_ctx.Loops.Count - 1);
    }

    private void EmitFor(ForStmt s)
    {
        int X = _ctx!.Depth;
        if (s.Type is not null) ResolveTypeDesc(s.Type);

        EmitExpression(s.Iterable);          // X+1
        Keep(Op.IterNew);                    // X+1  迭代器
        int iterTmp = NewTemp();
        PopU16(Op.DefLocal, iterTmp);        // X    迭代器入临时槽

        int top = _ctx.Code.Count;
        PushU16(Op.LoadLocal, iterTmp);      // X+1
        int exit = EmitJump(Op.IterNext);    // 成功: 运行时 push 值 → 深度 X+2；失败: 内部 pop → 深度 X
        SetDepth(X + 2);

        PushScope();
        var sym = DeclareLocal(s.Name, false, s.NameSpan);
        PopU16(Op.DefLocalCell, sym.Index);  // X+1  值 → Cell（迭代器副本仍在）
        Pop(Op.Pop);                         // X    丢弃迭代器副本

        var loop = new LoopCtx { ContinueTarget = top, TryDepth = _ctx.Tries.Count };
        _ctx.Loops.Add(loop);
        EmitBlockStatements(s.Body);         // X
        PopScope();

        int back = EmitJump(Op.Jump);
        PatchTo(back, top);
        Patch(exit);
        SetDepth(X);
        foreach (var b in loop.BreakJumps) PatchTo(b, _ctx.Code.Count);
        foreach (var c in loop.ContinueJumps) PatchTo(c, loop.ContinueTarget);
        _ctx.Loops.RemoveAt(_ctx.Loops.Count - 1);
    }

    private void EmitJumpCtrl(Statement s, bool isBreak)
    {
        int X = _ctx!.Depth;
        if (_ctx.Loops.Count == 0)
        {
            _bag.Report(isBreak ? ErrorCode.BreakOutsideLoop : ErrorCode.ContinueOutsideLoop,
                s.Span, isBreak ? "break 只能用在循环里" : "continue 只能用在循环里");
            return;
        }
        var loop = _ctx.Loops[^1];
        EmitStackCleanup(loop.TryDepth, X);
        int j = EmitJump(Op.Jump);
        if (isBreak) loop.BreakJumps.Add(j);
        else loop.ContinueJumps.Add(j);
        SetDepth(X);
    }

    private void EmitReturn(ReturnStmt r)
    {
        int X = _ctx!.Depth;
        if (r.Value is not null)
        {
            EmitExpression(r.Value);
            int tmp = NewTemp();
            PopU16(Op.DefLocal, tmp);
            EmitStackCleanup(0, X);
            PushU16(Op.LoadLocal, tmp);
        }
        else
        {
            EmitStackCleanup(0, X);
            Push(Op.Null);
        }
        Pop(Op.Return);
        SetDepth(X);
    }

    private void EmitThrow(ThrowStmt t)
    {
        int X = _ctx!.Depth;
        EmitExpression(t.Value);
        Pop(Op.Throw);
        SetDepth(X);
    }

    /// <summary>为跳出当前函数/循环清理 try 条目并内联 pending finally。</summary>
    private void EmitStackCleanup(int downTo, int X)
    {
        for (int i = _ctx!.Tries.Count - 1; i >= downTo; i--)
        {
            var t = _ctx.Tries[i];
            if (t.EntryActive)
            {
                Keep(Op.TryPop);
                t.EntryActive = false;
            }
            if (t.Finally is not null && !t.InFinally)
            {
                t.InFinally = true;
                EmitBlockStatements(t.Finally);
                t.InFinally = false;
            }
        }
        SetDepth(X);
    }

    private void EmitTry(TryStmt s)
    {
        int X = _ctx!.Depth;
        int tp = EmitJump(Op.TryPush);
        var ctx = new TryCtx { EntryActive = true, Finally = s.FinallyBody };
        _ctx!.Tries.Add(ctx);

        EmitBlockStatements(s.Body);

        // 正常路径：弹出 try 条目 + 内联 finally
        if (ctx.EntryActive)
        {
            Keep(Op.TryPop);
            ctx.EntryActive = false;
        }
        if (s.FinallyBody is not null)
        {
            ctx.InFinally = true;
            EmitBlockStatements(s.FinallyBody);
            ctx.InFinally = false;
        }
        var endJumps = new List<int> { EmitJump(Op.Jump) };

        // 处理器入口：VM 已回退栈并压入错误值 → 运行时深度 X+1
        Patch(tp);
        SetDepth(X + 1);

        if (s.CatchBody is not null || s.CatchName is not null)
        {
            PushScope();
            if (s.CatchName is not null)
            {
                var sym = DeclareLocal(s.CatchName, false, s.CatchNameSpan);
                PopU16(Op.DefLocalCell, sym.Index);   // X
            }
            else
                Pop(Op.Pop);                          // X（丢弃错误值）

            if (s.CatchBody is not null)
                EmitStatements(s.CatchBody);          // 名字已在本作用域中
            PopScope();

            if (s.FinallyBody is not null)
            {
                ctx.InFinally = true;
                EmitBlockStatements(s.FinallyBody);
                ctx.InFinally = false;
            }
            endJumps.Add(EmitJump(Op.Jump));
        }
        else
        {
            // 仅 finally：暂存错误 → 执行 finally → 重新抛出
            int tmp = NewTemp();
            PopU16(Op.DefLocal, tmp);                 // X
            if (s.FinallyBody is not null)
            {
                ctx.InFinally = true;
                EmitBlockStatements(s.FinallyBody);
                ctx.InFinally = false;
            }
            PushU16(Op.LoadLocal, tmp);               // X+1
            Pop(Op.Throw);                            // X（不可达）
        }

        foreach (var j in endJumps) Patch(j);
        SetDepth(X);
        _ctx.Tries.RemoveAt(_ctx.Tries.Count - 1);
    }

    // ----------------------------------------------------------------
    //  类 / 枚举
    // ----------------------------------------------------------------

    private void EmitClassDecl(ClassDecl c)
    {
        int X = _ctx!.Depth;
        if (!_types.TryGetValue(c.Name, out var ts) || ts.Kind != TypeSymKind.Class)
        {
            SetDepth(X);
            return;
        }
        var img = _classes[ts.ClassIndex];
        var savedClass = _currentClass;
        _currentClass = c;

        var instanceDefaults = new List<FieldDecl>();
        var staticFields = new List<FieldDecl>();
        var staticFieldNames = new List<string>();
        MethodDecl? userInit = null;
        var seenMethods = new HashSet<string>(StringComparer.Ordinal);

        foreach (var m in c.Members)
        {
            switch (m)
            {
                case FieldDecl fd when fd.Mods.IsStatic:
                    staticFieldNames.Add(fd.Name);
                    if (fd.Value is not null) staticFields.Add(fd);
                    break;
                case FieldDecl fd:
                    if (fd.Value is not null) instanceDefaults.Add(fd);
                    break;
                case MethodDecl md:
                    if (!seenMethods.Add(md.Name))
                        _bag.Report(ErrorCode.DuplicateDefinition, md.NameSpan,
                            $"'{md.Name}' 在类中重复定义");
                    if (md.IsConstructor || md.Name == "init") userInit = md;
                    break;
            }
        }

        var imgMethods = new List<(string, int)>();
        var staticEmits = new List<(string Name, int FuncIdx, FnCtx Ctx)>();

        foreach (var m in c.Members)
        {
            if (m is not MethodDecl md || md.Body is null) continue;
            bool isCtor = md.IsConstructor || md.Name == "init";
            bool isStatic = md.Mods.IsStatic;
            BlockStmt body = md.Body;
            if (isCtor && !isStatic && instanceDefaults.Count > 0)
                body = PrependFieldDefaults(instanceDefaults, body);

            var mTypeParams = new List<TypeParamSyntax>();
            if (c.TypeParams.Count > 0) mTypeParams.AddRange(c.TypeParams);
            mTypeParams.AddRange(md.TypeParams);
            var fnCtx = CompileFn(isCtor ? "init" : md.Name, md.Params, md.ReturnType, body,
                _ctx, isMethod: !isStatic, mTypeParams);
            int idx = ReserveFunction();
            FillFunction(idx, fnCtx);
            if (isStatic) staticEmits.Add((md.Name, idx, fnCtx));
            else imgMethods.Add((md.Name, idx));
        }

        if (instanceDefaults.Count > 0 && userInit?.Body is null)
            imgMethods.Add(("init", EmitSyntheticInit(ts, c, instanceDefaults)));

        img.Methods = imgMethods.ToArray();
        img.StaticMethods = staticEmits.Select(x => (x.Name, x.FuncIdx)).ToArray();
        img.StaticFieldNames = staticFieldNames.ToArray();
        if (c.TypeParams.Count > 0)
            img.TypeParams = c.TypeParams.Select(t => (string?)t.Name).ToArray();

        // 类值 → 全局 Cell（供命名空间导出）
        if (_globals.TryGetValue(c.Name, out int g))
        {
            PushU32(Op.LoadClass, ts.ClassIndex);
            PopU32(Op.DefGlobalCell, g);
        }

        // 静态方法闭包（静态字段初值可能调用它们，先于初值发射）
        foreach (var (mname, fidx, fnCtx) in staticEmits)
        {
            PushU32(Op.LoadClass, ts.ClassIndex);
            EmitClosureOp(fidx, fnCtx);
            PopU32(Op.SetProp, NameIndex(mname));
            Pop(Op.Pop);
        }

        // 静态字段初值
        foreach (var fd in staticFields)
        {
            MarkLine(fd);
            PushU32(Op.LoadClass, ts.ClassIndex);
            EmitExpression(fd.Value!);
            PopU32(Op.SetProp, NameIndex(fd.Name));
            Pop(Op.Pop);
        }

        _currentClass = savedClass;
        SetDepth(X);
    }

    /// <summary>有实例字段默认值但没有用户 init 时，合成 init。</summary>
    private int EmitSyntheticInit(TypeSymbol ts, ClassDecl c, List<FieldDecl> defaults)
    {
        var syn = new FnCtx
        {
            Name = "init",
            Arity = 0,
            IsMethod = true,
            ModulePath = _path,
            Parent = _ctx,
            ParamNames = Array.Empty<string>(),
        };
        foreach (var tp in _ctx!.TypeParams) syn.TypeParams.Add(tp);
        foreach (var tp in c.TypeParams) syn.TypeParams.Add(tp.Name);

        var saved = _ctx;
        _ctx = syn;
        PushScope();

        int baseArity = FindBaseInitArity(ts.Base);
        if (baseArity > 0)
            _bag.Report(ErrorCode.ArgumentCountMismatch, c.NameSpan,
                $"基类 '{ts.Base!.Name}' 的构造器需要 {baseArity} 个参数；请为 '{c.Name}' 定义自己的 init");
        else if (baseArity == 0)
        {
            Push(Op.LoadThis);
            KeepU32(Op.CallSuper, NameIndex("init"));
            KeepU8(0);
            Pop(Op.Pop);
        }

        foreach (var fd in defaults)
        {
            MarkLine(fd);
            Push(Op.LoadThis);
            EmitExpression(fd.Value!);
            PopU32(Op.SetProp, NameIndex(fd.Name));
            Pop(Op.Pop);
        }

        Push(Op.Null);
        Pop(Op.Return);
        PopScope();
        _ctx = saved;

        int idx = ReserveFunction();
        FillFunction(idx, syn);
        return idx;
    }

    /// <summary>类链上 init 的运行时形参数；-1 表示没有 init。</summary>
    private int FindInitArity(TypeSymbol ts)
    {
        if (ts.OwnInitArity >= 0) return ts.OwnInitArity;
        if (ts.OwnDefaults) return 0;
        return FindBaseInitArity(ts.Base);
    }

    private int FindBaseInitArity(TypeSymbol? from)
    {
        for (var cur = from; cur is not null; cur = cur.Base)
        {
            if (cur.OwnInitArity >= 0) return cur.OwnInitArity;
            if (cur.OwnDefaults) return 0;
        }
        return -1;
    }

    private static BlockStmt PrependFieldDefaults(List<FieldDecl> defaults, BlockStmt original)
    {
        var merged = new BlockStmt { Span = original.Span };
        foreach (var fd in defaults)
            merged.Statements.Add(MakeDefaultAssign(fd));
        foreach (var s in original.Statements)
            merged.Statements.Add(s);
        return merged;
    }

    private static Statement MakeDefaultAssign(FieldDecl fd)
    {
        var assign = new AssignExpr
        {
            Target = new MemberExpr
            {
                Target = new ThisExpr { Span = fd.Span },
                Name = fd.Name,
                NameSpan = fd.NameSpan,
                NullSafe = false,
                Span = fd.Span,
            },
            Op = TokenKind.Assign,
            OpSpan = fd.KeywordSpan,
            Value = fd.Value!,
            Span = fd.Span,
        };
        return new ExprStmt { Expression = assign, Span = fd.Span };
    }

    private void EmitEnumDecl(EnumDecl e)
    {
        if (!_types.TryGetValue(e.Name, out var ts) || ts.Kind != TypeSymKind.Enum) return;
        if (_globals.TryGetValue(e.Name, out int g))
        {
            PushU32(Op.LoadEnumType, ts.EnumIndex);
            PopU32(Op.DefGlobalCell, g);
        }
    }
}
