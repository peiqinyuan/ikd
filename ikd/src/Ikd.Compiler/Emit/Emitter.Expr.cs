using Ikd.Compiler.Diagnostics;
using Ikd.Compiler.Syntax;
using Ikd.Runtime;

namespace Ikd.Compiler.Emit;

// ===================================================================
//  表达式发射
// ===================================================================

public sealed partial class Emitter
{
    private void EmitExpression(Expression expr)
    {
        switch (expr)
        {
            case LiteralExpr lit:
                EmitLiteral(lit);
                break;
            case NameExpr n:
                EmitName(n);
                break;
            case MemberExpr m:
                EmitMember(m);
                break;
            case IndexExpr ix:
                EmitExpression(ix.Target);
                EmitExpression(ix.Index);
                Pop(Op.IndexGet);
                break;
            case CallExpr c:
                EmitCall(c);
                break;
            case BinaryExpr b:
                EmitBinary(b);
                break;
            case UnaryExpr u:
                EmitUnary(u);
                break;
            case AssignExpr a:
                EmitAssign(a);
                break;
            case TernaryExpr t:
                EmitTernary(t);
                break;
            case ThisExpr ti:
                if (!_ctx!.IsMethod)
                    _bag.Report(ErrorCode.ThisOutsideClass, ti.Span, "this 只能在类方法中使用");
                Push(Op.LoadThis);
                break;
            case SuperExpr su:
                _bag.Report(ErrorCode.SuperOutsideClass, su.Span, "super 只能作为 super.method 的目标使用");
                Push(Op.Null);
                break;
            case NewExpr nw:
                EmitNew(nw);
                break;
            case ListExpr l:
                foreach (var e in l.Elements) EmitExpression(e);
                Emit(Op.NewList);
                U32(l.Elements.Count);
                D(1 - l.Elements.Count);
                break;
            case MapExpr mp:
                foreach (var (k, v) in mp.Entries)
                {
                    EmitExpression(k);
                    EmitExpression(v);
                }
                Emit(Op.NewMap);
                U32(mp.Entries.Count);
                D(1 - 2 * mp.Entries.Count);
                break;
            case LambdaExpr la:
                EmitLambda(la);
                break;
            case MatchExpr mm:
                EmitMatch(mm);
                break;
            case CastExpr ce:
                EmitExpression(ce.Target);
                KeepU32(Op.Cast, TypeDescIndex(ce.Type));
                break;
            case IsExpr ie:
                EmitExpression(ie.Target);
                KeepU32(Op.IsType, TypeDescIndex(ie.Type));
                break;
            default:
                Internal($"未处理的表达式 {expr.GetType().Name}");
                Push(Op.Null);
                break;
        }
    }

    // ----------------------------------------------------------------
    //  字面量 / 名称
    // ----------------------------------------------------------------

    private void EmitLiteral(LiteralExpr lit)
    {
        switch (lit.Kind)
        {
            case LiteralKind.Null:
                Push(Op.Null);
                break;
            case LiteralKind.Int:
                PushU32(Op.Const, ConstIndex(Value.Of((long)lit.Value!)));
                break;
            case LiteralKind.Float:
                PushU32(Op.Const, ConstIndex(Value.Of((double)lit.Value!)));
                break;
            case LiteralKind.Bool:
                PushU32(Op.Const, ConstIndex(Value.Of((bool)lit.Value!)));
                break;
            case LiteralKind.Str:
                EmitInterpolatedString((InterpolatedString)lit.Value!);
                break;
        }
    }

    private void EmitInterpolatedString(InterpolatedString s)
    {
        if (s.IsPlain)
        {
            PushU32(Op.Const, ConstIndex(Value.OfStr(s.PlainText)));
            return;
        }

        int count = 0;
        foreach (var p in s.Parts)
        {
            if (p.IsExpression)
            {
                var tokens = Lexer.LexRange(_unit.Source, _bag, p.ExprStart, p.ExprEnd);
                var sub = new Parser(_unit.Source, _bag, tokens).ParseExpression();
                EmitExpression(sub);
            }
            else
            {
                if (p.Text is { Length: 0 }) continue;
                PushU32(Op.Const, ConstIndex(Value.OfStr(p.Text!)));
            }
            count++;
        }

        if (count == 0)
            PushU32(Op.Const, ConstIndex(Value.OfStr("")));
        else if (count > 1)
        {
            Emit(Op.Concat);
            U8(count);
            D(1 - count);
        }
    }

    private void EmitName(NameExpr n)
    {
        string name = n.Name;
        var sym = LookupValue(_ctx!, name);
        if (sym is { Kind: SymKind.Local })
        {
            Emit(Op.LoadLocalCell);
            U16(sym.Index);
            D(1);
            return;
        }

        int up = ResolveUpvalue(_ctx!, name);
        if (up >= 0)
        {
            Emit(Op.LoadUpvalue);
            U16(up);
            D(1);
            return;
        }

        // 类/枚举名走静态通道（无需等待全局绑定）
        if (_types.TryGetValue(name, out var ts))
        {
            switch (ts.Kind)
            {
                case TypeSymKind.Class:
                    PushU32(Op.LoadClass, ts.ClassIndex);
                    break;
                case TypeSymKind.Enum:
                    PushU32(Op.LoadEnumType, ts.EnumIndex);
                    break;
                default:
                    _bag.Report(ErrorCode.NotAType, n.NameSpan, $"'{name}' 是接口，不能作为值使用");
                    Push(Op.Null);
                    break;
            }
            return;
        }

        if (_globals.TryGetValue(name, out int g))
        {
            Emit(Op.LoadGlobalCell);
            U32(g);
            D(1);
            return;
        }

        if (Prelude.TryGetValue(name, out var pn))
        {
            PushU32(Op.LoadNative, NativeIndex(pn));
            return;
        }

        _bag.Report(ErrorCode.UndefinedName, n.NameSpan, $"未定义的名称 '{name}'");
        Push(Op.Null);
    }

    /// <summary>该名称没有被任何局部/全局/类型遮蔽时可直调 prelude。</summary>
    private bool IsPreludeFree(string name)
    {
        if (_globals.ContainsKey(name) || _types.ContainsKey(name)) return false;
        if (LookupValue(_ctx!, name) is not null) return false;
        return ResolveUpvalue(_ctx!, name) < 0;
    }

    // ----------------------------------------------------------------
    //  成员 / 调用
    // ----------------------------------------------------------------

    private void EmitMember(MemberExpr m)
    {
        int X = _ctx!.Depth;
        if (m.Target is SuperExpr)
        {
            if (_currentClass is null || !_ctx.IsMethod)
                _bag.Report(ErrorCode.SuperOutsideClass, m.Span, "super 只能在类方法中使用");
            PushU32(Op.GetSuper, NameIndex(m.Name));
            SetDepth(X + 1);
            return;
        }

        CheckEnumMember(m);

        if (m.NullSafe)
        {
            EmitExpression(m.Target);                                  // X+1
            Push(Op.Dup);                                                // X+2
            KeepU32(Op.IsType, TypeDescOfKind(TypeDesc.TypeDescKind.Null)); // X+2
            int end = EmitJump(Op.JumpIfTrue);
            D(-1);                                                       // X+1
            KeepU32(Op.GetProp, NameIndex(m.Name));                      // X+1
            Patch(end);
        }
        else
        {
            EmitExpression(m.Target);
            KeepU32(Op.GetProp, NameIndex(m.Name));
        }
        SetDepth(X + 1);
    }

    /// <summary>target 为枚举类型名时做静态成员存在性检查。</summary>
    private void CheckEnumMember(MemberExpr m)
    {
        if (m.Target is not NameExpr ne) return;
        if (LookupValue(_ctx!, ne.Name) is { Kind: SymKind.Local }) return;
        if (!_types.TryGetValue(ne.Name, out var ts) || ts.Kind != TypeSymKind.Enum) return;
        if (ts.EnumDecl is null) return;
        foreach (var cs in ts.EnumDecl.Cases)
            if (cs.Name == m.Name)
                return;
        _bag.Report(ErrorCode.EnumMemberNotFound, m.NameSpan,
            $"枚举 '{ne.Name}' 没有成员 '{m.Name}'");
    }

    private void EmitCall(CallExpr call)
    {
        int X = _ctx!.Depth;
        int argc = call.Args.Count;

        // prelude 直调（可按实参个数选变体）
        if (call.Callee is NameExpr ne && Prelude.TryGetValue(ne.Name, out var pn) &&
            IsPreludeFree(ne.Name))
        {
            string native = pn switch
            {
                "range" => argc == 3 ? "range3" : "range",
                "assert" => argc == 2 ? "assertMsg" : "assert",
                _ => pn,
            };
            var nf = StdLib.GetNative(native);
            if (nf is not null && nf.Arity >= 0 && nf.Arity != argc)
                _bag.Report(ErrorCode.ArgumentCountMismatch,
                    call.LParenSpan.Length > 0 ? call.LParenSpan : call.Span,
                    $"内置函数 {ne.Name} 需要 {nf.Arity} 个参数，实得 {argc} 个");

            PushU32(Op.LoadNative, NativeIndex(native));
            foreach (var a in call.Args) EmitExpression(a);
            Emit(Op.Call);
            U8(argc);
            D(-argc);
            SetDepth(X + 1);
            return;
        }

        // ClassName(args) → 实例化（类名未被局部变量遮蔽时）
        if (call.Callee is NameExpr cn &&
            LookupValue(_ctx!, cn.Name) is not { Kind: SymKind.Local } &&
            ResolveUpvalue(_ctx!, cn.Name) < 0 &&
            _types.TryGetValue(cn.Name, out var cts) && cts.Kind == TypeSymKind.Class)
        {
            EmitConstruct(cts, call.Args, call.Span, call.LParenSpan);
            return;
        }

        // super.m(...)
        if (call.Callee is MemberExpr { Target: SuperExpr } sm)
        {
            if (_currentClass is null || !_ctx.IsMethod)
                _bag.Report(ErrorCode.SuperOutsideClass, sm.Span, "super 只能在类方法中使用");
            Push(Op.LoadThis);
            foreach (var a in call.Args) EmitExpression(a);
            Emit(Op.CallSuper);
            U32(NameIndex(sm.Name));
            U8(argc);
            D(-argc);
            SetDepth(X + 1);
            return;
        }

        // obj?.m(...)
        if (call.Callee is MemberExpr { NullSafe: true } nm)
        {
            EmitExpression(nm.Target);                                   // X+1
            Push(Op.Dup);                                                 // X+2
            KeepU32(Op.IsType, TypeDescOfKind(TypeDesc.TypeDescKind.Null)); // X+2
            int end = EmitJump(Op.JumpIfTrue);
            D(-1);                                                        // X+1
            KeepU32(Op.GetProp, NameIndex(nm.Name));                      // X+1
            foreach (var a in call.Args) EmitExpression(a);               // X+1+n
            Emit(Op.Call);
            U8(argc);
            D(-argc);                                                     // X+1
            Patch(end);
            SetDepth(X + 1);
            return;
        }

        EmitExpression(call.Callee);
        foreach (var a in call.Args) EmitExpression(a);
        Emit(Op.Call);
        U8(argc);
        D(-argc);
        SetDepth(X + 1);
    }

    // ----------------------------------------------------------------
    //  运算
    // ----------------------------------------------------------------

    private void EmitBinary(BinaryExpr b)
    {
        int X = _ctx!.Depth;
        switch (b.Op)
        {
            case TokenKind.AmpAmp:
            case TokenKind.PipePipe:
            {
                bool isAnd = b.Op == TokenKind.AmpAmp;
                EmitExpression(b.Left);
                Push(Op.Dup);
                int end = EmitJump(isAnd ? Op.JumpIfFalse : Op.JumpIfTrue);
                D(-1);
                Pop(Op.Pop);
                EmitExpression(b.Right);
                Patch(end);
                break;
            }
            case TokenKind.QuestionQuestion:
                EmitExpression(b.Left);
                Push(Op.Dup);
                KeepU32(Op.IsType, TypeDescOfKind(TypeDesc.TypeDescKind.Null));
                int endNull = EmitJump(Op.JumpIfFalse);
                D(-1);
                Pop(Op.Pop);
                EmitExpression(b.Right);
                Patch(endNull);
                break;
            default:
                EmitExpression(b.Left);
                EmitExpression(b.Right);
                ApplyBinaryOp(b.Op, b.OpSpan);
                break;
        }
        SetDepth(X + 1);
    }

    /// <summary>栈顶 [a, b] → [result]（净 −1）。也接受复合赋值的运算符记号。</summary>
    private void ApplyBinaryOp(TokenKind kind, TextSpan span)
    {
        Op? op = kind switch
        {
            TokenKind.Plus or TokenKind.PlusEq => Op.Add,
            TokenKind.Minus or TokenKind.MinusEq => Op.Sub,
            TokenKind.Star or TokenKind.StarEq => Op.Mul,
            TokenKind.Slash or TokenKind.SlashEq => Op.Div,
            TokenKind.Percent or TokenKind.PercentEq => Op.Mod,
            TokenKind.EqEq => Op.Eq,
            TokenKind.BangEq => Op.Ne,
            TokenKind.Lt => Op.Lt,
            TokenKind.LtEq => Op.Le,
            TokenKind.Gt => Op.Gt,
            TokenKind.GtEq => Op.Ge,
            _ => null,
        };
        if (op is null)
        {
            _bag.Report(ErrorCode.InvalidOperatorOperands, span,
                $"运算符 '{kind.Display()}' 不能用于此处");
            Pop(Op.Pop);
            return;
        }
        Keep(op.Value);
        D(-1);
    }

    private void EmitUnary(UnaryExpr u)
    {
        EmitExpression(u.Operand);
        switch (u.Op)
        {
            case TokenKind.Minus:
                Keep(Op.Neg);
                break;
            case TokenKind.Bang:
            case TokenKind.Not:
                Keep(Op.Not);
                break;
            default:
                _bag.Report(ErrorCode.InvalidOperatorOperands, u.OpSpan,
                    $"运算符 '{u.Op.Display()}' 不能用于此处");
                break;
        }
    }

    private void EmitTernary(TernaryExpr t)
    {
        int X = _ctx!.Depth;
        EmitExpression(t.Cond);
        int elseJ = EmitJump(Op.JumpIfFalse);
        D(-1);
        EmitExpression(t.Then);
        int endJ = EmitJump(Op.Jump);
        Patch(elseJ);
        SetDepth(X);
        EmitExpression(t.Else);
        Patch(endJ);
        SetDepth(X + 1);
    }

    // ----------------------------------------------------------------
    //  赋值
    // ----------------------------------------------------------------

    private void EmitAssign(AssignExpr a)
    {
        int X = _ctx!.Depth;

        switch (a.Target)
        {
            case NameExpr n:
            {
                var sym = LookupValue(_ctx!, n.Name);
                int up = -1;
                if (sym is null) up = ResolveUpvalue(_ctx!, n.Name);
                int g = -1;
                if (sym is not { Kind: SymKind.Local } && up < 0 &&
                    _globals.TryGetValue(n.Name, out int gi))
                    g = gi;

                if (sym is { Kind: SymKind.Local } || up >= 0 || g >= 0)
                {
                    var decl = sym ?? (g >= 0 ? _globalscope.FindLocal(n.Name) : null);
                    if (decl is { IsConst: true })
                        _bag.Report(ErrorCode.CannotAssignToConst, n.NameSpan,
                            $"'{n.Name}' 是常量，不能赋值");

                    if (a.Op == TokenKind.Assign)
                    {
                        EmitExpression(a.Value);
                    }
                    else
                    {
                        if (sym is { Kind: SymKind.Local })
                        {
                            Emit(Op.LoadLocalCell);
                            U16(sym.Index);
                            D(1);
                        }
                        else if (up >= 0)
                        {
                            Emit(Op.LoadUpvalue);
                            U16(up);
                            D(1);
                        }
                        else
                        {
                            Emit(Op.LoadGlobalCell);
                            U32(g);
                            D(1);
                        }
                        EmitExpression(a.Value);
                        ApplyBinaryOp(a.Op, a.OpSpan);
                    }

                    if (sym is { Kind: SymKind.Local }) KeepU16(Op.StoreLocalCell, sym.Index);
                    else if (up >= 0) KeepU16(Op.StoreUpvalue, up);
                    else KeepU32(Op.StoreGlobalCell, g);
                }
                else
                {
                    _bag.Report(ErrorCode.UndefinedName, n.NameSpan,
                        $"未定义的名称 '{n.Name}'");
                    EmitExpression(a.Value);
                }
                break;
            }

            case MemberExpr m:
            {
                if (m.NullSafe)
                    _bag.Report(ErrorCode.InvalidLValue, m.Span, "不能给空安全成员赋值");
                CheckConstField(m);
                EmitExpression(m.Target);
                if (a.Op == TokenKind.Assign)
                {
                    EmitExpression(a.Value);
                }
                else
                {
                    Push(Op.Dup);
                    KeepU32(Op.GetProp, NameIndex(m.Name));
                    EmitExpression(a.Value);
                    ApplyBinaryOp(a.Op, a.OpSpan);
                }
                PopU32(Op.SetProp, NameIndex(m.Name));
                break;
            }

            case IndexExpr ix:
            {
                if (a.Op == TokenKind.Assign)
                {
                    EmitExpression(ix.Target);
                    EmitExpression(ix.Index);
                    EmitExpression(a.Value);
                    Emit(Op.IndexSet);
                    D(-2);
                }
                else
                {
                    EmitExpression(ix.Target);
                    int t = NewTemp();
                    PopU16(Op.DefLocal, t);
                    EmitExpression(ix.Index);
                    int i2 = NewTemp();
                    PopU16(Op.DefLocal, i2);
                    PushU16(Op.LoadLocal, t);
                    PushU16(Op.LoadLocal, i2);
                    Pop(Op.IndexGet);
                    EmitExpression(a.Value);
                    ApplyBinaryOp(a.Op, a.OpSpan);
                    PushU16(Op.LoadLocal, t);
                    PushU16(Op.LoadLocal, i2);
                    Emit(Op.IndexSet);
                    D(-2);
                }
                break;
            }

            default:
                _bag.Report(ErrorCode.InvalidAssignmentTarget, a.Target.Span,
                    "赋值目标必须是变量、成员或下标");
                EmitExpression(a.Value);
                break;
        }

        SetDepth(X + 1);
    }

    private void CheckConstField(MemberExpr m)
    {
        if (m.Target is not ThisExpr || _currentClass is null) return;
        // 构造器内允许初始化 const 字段（含合成 init 的默认值前插）
        if (_ctx is { IsMethod: true, Name: "init" }) return;
        foreach (var mem in _currentClass.Members)
            if (mem is FieldDecl fd && fd.Name == m.Name && fd.Kind == VarDeclKind.Const)
            {
                _bag.Report(ErrorCode.CannotAssignToConst, m.NameSpan,
                    $"'{m.Name}' 是常量字段，不能赋值");
                return;
            }
    }

    // ----------------------------------------------------------------
    //  new
    // ----------------------------------------------------------------

    private void EmitNew(NewExpr n)
    {
        int X = _ctx!.Depth;
        if (n.Type is not NamedTypeSyntax nt)
        {
            ResolveTypeDesc(n.Type);
            foreach (var a in n.Args)
            {
                EmitExpression(a);
                Pop(Op.Pop);
            }
            Push(Op.Null);
            return;
        }

        foreach (var a in nt.TypeArgs) ResolveTypeDesc(a);

        if (!_types.TryGetValue(nt.Name, out var ts) || ts.Kind != TypeSymKind.Class)
        {
            bool isType = _types.ContainsKey(nt.Name);
            _bag.Report(isType ? ErrorCode.NotAType : ErrorCode.UnknownType,
                nt.NameSpan.Length > 0 ? nt.NameSpan : nt.Span,
                isType ? $"'{nt.Name}' 不是类，不能实例化" : $"未知类型 '{nt.Name}'");
            foreach (var a in n.Args)
            {
                EmitExpression(a);
                Pop(Op.Pop);
            }
            Push(Op.Null);
            return;
        }

        EmitConstruct(ts, n.Args, n.Span, n.LParenSpan);
    }

    /// <summary>发射实例化 + init 调用；前置深度 X，后置 X+1。</summary>
    private void EmitConstruct(TypeSymbol ts, IReadOnlyList<Expression> args,
        TextSpan span, TextSpan lParenSpan)
    {
        int X = _ctx!.Depth;
        PushU32(Op.NewInstance, ts.ClassIndex);   // X+1

        int arity = FindInitArity(ts);
        if (arity >= 0)
        {
            if (args.Count != arity)
                _bag.Report(ErrorCode.ArgumentCountMismatch,
                    lParenSpan.Length > 0 ? lParenSpan : span,
                    $"类 '{ts.Name}' 的构造器需要 {arity} 个参数，实得 {args.Count} 个");
            Push(Op.Dup);                          // X+2
            foreach (var a in args) EmitExpression(a);  // X+2+n
            Emit(Op.CallProp);
            U32(NameIndex("init"));
            U8(args.Count);
            D(-args.Count);                      // X+2 [inst, result]
            Pop(Op.Pop);                           // X+1 [inst]
        }
        else if (args.Count > 0)
        {
            _bag.Report(ErrorCode.ArgumentCountMismatch, span,
                $"类 '{ts.Name}' 没有构造器 init，不能传参数");
            foreach (var a in args)
            {
                EmitExpression(a);
                Pop(Op.Pop);
            }
        }

        SetDepth(X + 1);
    }
}
