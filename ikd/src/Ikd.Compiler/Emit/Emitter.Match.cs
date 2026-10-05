using Ikd.Compiler.Diagnostics;
using Ikd.Compiler.Syntax;
using Ikd.Runtime;

namespace Ikd.Compiler.Emit;

// ===================================================================
//  match / 模式发射
// ===================================================================

public sealed partial class Emitter
{
    /// <summary>
    /// match 是表达式：subject 存入临时槽，逐臂测试，命中臂的求值结果存入结果槽。
    /// 所有失败跳转均携带一个待测值（落点深度 X+1），失败桩 Pop 后进入下一臂。
    /// </summary>
    private void EmitMatch(MatchExpr m)
    {
        int X = _ctx!.Depth;

        EmitExpression(m.Subject);                  // X+1
        int subj = NewTemp();
        PopU16(Op.DefLocal, subj);                  // X
        int result = NewTemp();

        var endJumps = new List<int>();
        bool irrefutable = false;

        foreach (var arm in m.Arms)
        {
            if (irrefutable)
                _bag.Report(DiagnosticSeverity.Warning, ErrorCode.MatchArmUnreachable,
                    arm.Span, "前面的分支已可匹配任何值，此分支不可达");

            MarkLine(arm);
            PushScope();

            var fails = new List<int>();
            PushU16(Op.LoadLocal, subj);            // X+1  待测值
            EmitPatternValue(arm.Pattern, fails);   // X

            var guardFails = new List<int>();
            if (arm.Guard is not null)
            {
                EmitExpression(arm.Guard);          // X+1
                int gj = EmitJump(Op.JumpIfFalse);
                D(-1);                              // X
                guardFails.Add(gj);
            }

            EmitExpression(arm.Body);               // X+1
            PopU16(Op.DefLocal, result);            // X
            endJumps.Add(EmitJump(Op.Jump));

            if (fails.Count > 0)
            {
                foreach (var f in fails) Patch(f);
                SetDepth(X + 1);                    // 失败落点：栈上仍有待测值
                Pop(Op.Pop);                        // X
            }
            foreach (var g in guardFails) Patch(g);

            if (arm.Guard is null && IsIrrefutable(arm.Pattern))
                irrefutable = true;

            PopScope();
            SetDepth(X);
        }

        // 没有任何分支匹配 → 抛 MatchError
        PushU16(Op.LoadLocal, subj);                // X+1
        Pop(Op.MatchFail);                          // X

        foreach (var j in endJumps) Patch(j);
        PushU16(Op.LoadLocal, result);              // X+1
        SetDepth(X + 1);
    }

    private static bool IsIrrefutable(Pattern p)
        => p is WildcardPattern or BindingPattern ||
           p is TypePattern { Type: NamedTypeSyntax { Name: "Any" } };

    // ----------------------------------------------------------------
    //  模式值
    // ----------------------------------------------------------------

    /// <summary>
    /// 发射单个模式的测试。
    /// 前置：栈顶为待测值（深度 X+1）。
    /// 成功：消费该值，深度回到 X；
    /// 失败：跳入 fails（携带该值，落点深度 X+1，由失败桩 Pop）。
    /// </summary>
    private void EmitPatternValue(Pattern p, List<int> fails)
    {
        switch (p)
        {
            case WildcardPattern:
                Pop(Op.Pop);
                break;

            case BindingPattern bp:
            {
                var sym = DeclareLocal(bp.Name, false, bp.NameSpan);
                PopU16(Op.DefLocalCell, sym.Index);     // 值 → Cell
                break;
            }

            case TypePattern tp:
                Push(Op.Dup);
                KeepU32(Op.IsType, TypeDescIndex(tp.Type));
                fails.Add(EmitJumpIfFalseDrop());
                Pop(Op.Pop);
                break;

            case LiteralPattern lp:
                EmitLiteralPattern(lp, fails);
                break;

            case EnumPattern ep:
                EmitEnumPattern(ep, fails);
                break;

            case ListPattern lst:
                EmitListPattern(lst, fails);
                break;

            default:
                Internal($"未处理的模式 {p.GetType().Name}");
                Pop(Op.Pop);
                break;
        }
    }

    /// <summary>发射 JumpIfFalse：出条件后栈上仍留待测值。</summary>
    private int EmitJumpIfFalseDrop()
    {
        int j = EmitJump(Op.JumpIfFalse);
        D(-1);
        return j;
    }

    private void EmitLiteralPattern(LiteralPattern lp, List<int> fails)
    {
        switch (lp.Kind)
        {
            case LiteralKind.Null:
                Push(Op.Dup);
                KeepU32(Op.IsType, TypeDescOfKind(TypeDesc.TypeDescKind.Null));
                fails.Add(EmitJumpIfFalseDrop());
                Pop(Op.Pop);
                break;

            case LiteralKind.Str:
            {
                string text = "";
                if (lp.Value is InterpolatedString s)
                {
                    if (!s.IsPlain)
                        _bag.Report(ErrorCode.ExpectedPattern, lp.Span,
                            "模式中的字符串不支持插值");
                    text = s.PlainText;
                }
                Push(Op.Dup);
                PushU32(Op.Const, ConstIndex(Value.OfStr(text)));
                Keep(Op.Eq);
                D(-1);
                fails.Add(EmitJumpIfFalseDrop());
                Pop(Op.Pop);
                break;
            }

            case LiteralKind.Int:
            case LiteralKind.Float:
            case LiteralKind.Bool:
            {
                Value cv = lp.Kind switch
                {
                    LiteralKind.Int => Value.Of((long)lp.Value!),
                    LiteralKind.Float => Value.Of((double)lp.Value!),
                    _ => Value.Of((bool)lp.Value!),
                };
                Push(Op.Dup);
                PushU32(Op.Const, ConstIndex(cv));
                Keep(Op.Eq);
                D(-1);
                fails.Add(EmitJumpIfFalseDrop());
                Pop(Op.Pop);
                break;
            }
        }
    }

    private void EmitEnumPattern(EnumPattern ep, List<int> fails)
    {
        int X = _ctx!.Depth - 1;

        if (!TryResolveEnumPattern(ep, out var ts, out int caseIdx, out var decl))
        {
            // 已报错：该分支永不匹配
            fails.Add(EmitJump(Op.Jump));           // 携带待测值跳向失败桩
            SetDepth(X);
            return;
        }

        if (ep.Args.Count != decl.Payload.Count)
            _bag.Report(ErrorCode.ArgumentCountMismatch, ep.NameSpan,
                $"枚举成员 '{ts.Name}.{ep.Name}' 需要 {decl.Payload.Count} 个参数，实得 {ep.Args.Count} 个");

        int tmp = NewTemp();
        PopU16(Op.DefLocal, tmp);                   // X
        PushU16(Op.LoadLocal, tmp);                 // X+1
        Emit(Op.IsEnumCase);
        U32(ts.EnumIndex);
        U32(caseIdx);
        D(1);                                       // X+2 [v, ok]
        fails.Add(EmitJumpIfFalseDrop());           // X+1 [v]
        Pop(Op.Pop);                                // X

        for (int i = 0; i < ep.Args.Count; i++)
        {
            PushU16(Op.LoadLocal, tmp);             // X+1 [v]
            Emit(Op.GetEnumPayload);
            U32(i);                                 // X+1 [payload]
            EmitPatternValue(ep.Args[i], fails);
        }
    }

    private void EmitListPattern(ListPattern lp, List<int> fails)
    {
        int X = _ctx!.Depth - 1;

        int tmp = NewTemp();
        PopU16(Op.DefLocal, tmp);                       // X

        PushU16(Op.LoadLocal, tmp);                     // X+1 [v]
        Push(Op.Dup);                                   // X+2 [v,v]
        KeepU32(Op.IsType, TypeDescOfKind(TypeDesc.TypeDescKind.List));
        fails.Add(EmitJumpIfFalseDrop());               // X+1 [v]

        PushU32(Op.LoadNative, NativeIndex("len"));     // X+2 [v, native]
        Push(Op.Dup);                                   // X+3 [v, native, v]
        Emit(Op.Call);
        U8(1);
        D(-1);                                          // X+2 [v, len]
        PushU32(Op.Const, ConstIndex(Value.Of((long)lp.Items.Count)));
        Keep(Op.Eq);
        D(-1);                                          // X+2 [v, ok]
        fails.Add(EmitJumpIfFalseDrop());               // X+1 [v]
        Pop(Op.Pop);                                    // X

        for (int i = 0; i < lp.Items.Count; i++)
        {
            PushU16(Op.LoadLocal, tmp);                 // X+1 [list]
            PushU32(Op.Const, ConstIndex(Value.Of((long)i)));
            Emit(Op.IndexGet);
            D(-1);                                      // X+1 [elem]
            EmitPatternValue(lp.Items[i], fails);
        }
    }

    /// <summary>静态解析枚举模式（仅本模块的枚举可被引用）。</summary>
    private bool TryResolveEnumPattern(EnumPattern ep, out TypeSymbol ts,
        out int caseIdx, out EnumCaseDecl decl)
    {
        ts = null!;
        caseIdx = -1;
        decl = null!;

        if (ep.Qualifier is not null)
        {
            if (LookupValue(_ctx!, ep.Qualifier) is { Kind: SymKind.Local } ||
                ResolveUpvalue(_ctx!, ep.Qualifier) >= 0)
            {
                _bag.Report(ErrorCode.NotAType, ep.NameSpan,
                    $"'{ep.Qualifier}' 不是枚举类型，不能限定模式成员");
                return false;
            }
            if (!_types.TryGetValue(ep.Qualifier, out var q))
            {
                _bag.Report(ErrorCode.EnumMemberNotFound, ep.NameSpan,
                    $"未找到枚举 '{ep.Qualifier}'");
                return false;
            }
            if (q.Kind != TypeSymKind.Enum || q.EnumDecl is null)
            {
                _bag.Report(ErrorCode.NotAType, ep.NameSpan, $"'{ep.Qualifier}' 不是枚举");
                return false;
            }
            ts = q;
        }
        else
        {
            TypeSymbol? found = null;
            int hits = 0;
            foreach (var t in _types.Values)
            {
                if (t.Kind != TypeSymKind.Enum || t.EnumDecl is null) continue;
                foreach (var c in t.EnumDecl.Cases)
                    if (c.Name == ep.Name) { found = t; hits++; break; }
            }
            if (hits == 0)
            {
                _bag.Report(ErrorCode.EnumMemberNotFound, ep.NameSpan,
                    $"未找到枚举成员 '{ep.Name}'");
                return false;
            }
            if (hits > 1)
            {
                _bag.Report(ErrorCode.EnumMemberNotFound, ep.NameSpan,
                    $"枚举成员 '{ep.Name}' 存在于多个枚举中，请写成 '枚举.成员'");
                return false;
            }
            ts = found!;
        }

        var cases = ts.EnumDecl!.Cases;
        for (int i = 0; i < cases.Count; i++)
            if (cases[i].Name == ep.Name)
            {
                caseIdx = i;
                decl = cases[i];
                return true;
            }

        _bag.Report(ErrorCode.EnumMemberNotFound, ep.NameSpan,
            $"枚举 '{ts.Name}' 没有成员 '{ep.Name}'");
        return false;
    }
}
