using Ikd.Compiler.Diagnostics;

namespace Ikd.Compiler.Syntax;

/// <summary>
/// 递归下降解析器。
///
/// 换行规则：解析器对换行基本无感知，只有「续行」判定 ——
/// 中缀/后缀运算符（含 is/as、调用 '('、下标 '['）必须与前一个 token 同处一行，
/// 否则视为语句结束；成员访问 '.' / '?.' 可跨行；闭合括号与结构性记号（=>、else、when）不受限。
/// </summary>
public sealed class Parser
{
    private readonly SourceText _source;
    private readonly DiagnosticBag _bag;
    private readonly List<Token> _tokens;
    private int _index;

    public Parser(SourceText source, DiagnosticBag bag, List<Token>? tokens = null)
    {
        _source = source;
        _bag = bag;
        _tokens = tokens ?? new Lexer(source, bag).LexAll();
        if (_tokens.Count == 0) _tokens.Add(Token.Eof(0));
    }

    public static CompilationUnit Parse(SourceText source, DiagnosticBag bag)
    {
        bag.SetSource(source);
        return new Parser(source, bag).ParseCompilationUnit();
    }

    // ---------------- 基础操作 ----------------

    private Token Current => _tokens[_index < _tokens.Count ? _index : _tokens.Count - 1];
    private Token Peek(int n = 1) => _tokens[Math.Min(_index + n, _tokens.Count - 1)];
    private Token Previous => _tokens[_index > 0 ? _index - 1 : 0];
    private bool AtEnd => Current.Kind == TokenKind.EndOfFile;
    private TextSpan CurrentSpan => Current.Span;

    private Token Advance()
    {
        var t = Current;
        if (!AtEnd) _index++;
        return t;
    }

    private bool Check(TokenKind kind) => Current.Kind == kind;

    private bool CheckAny(params TokenKind[] kinds)
    {
        var k = Current.Kind;
        foreach (var x in kinds) if (x == k) return true;
        return false;
    }

    private bool Match(TokenKind kind)
    {
        if (!Check(kind)) return false;
        Advance();
        return true;
    }

    private Token Expect(TokenKind kind, string message, string? help = null)
    {
        if (Check(kind)) return Advance();
        _bag.Report(ErrorCode.ExpectedToken, CurrentSpan,
            $"{message}，但读到 {Current.Kind.Display()}", help);
        return Current;
    }

    private string ExpectIdentifier(string what)
    {
        if (Check(TokenKind.Identifier))
            return (string)Advance().Value!;
        _bag.Report(ErrorCode.ExpectedIdentifier, CurrentSpan,
            $"{what}应为标识符，但读到 {Current.Kind.Display()}");
        return "";
    }

    /// <summary>当前 token 是否与前一个 token 同在一行（用于续行判定）。</summary>
    private bool ContinuesExpression()
    {
        if (_index == 0 || _index - 1 >= _tokens.Count) return false;
        var prev = _tokens[_index - 1];
        if (prev.Length == 0) return false;
        var cur = Current;
        return _source.GetLine(prev.End - 1) == _source.GetLine(cur.Start);
    }

    private static bool StartsExpression(TokenKind k) => k is
        TokenKind.Identifier or TokenKind.IntLiteral or TokenKind.FloatLiteral or
        TokenKind.StringLiteral or TokenKind.True or TokenKind.False or TokenKind.Null or
        TokenKind.This or TokenKind.Super or TokenKind.New or TokenKind.LParen or
        TokenKind.LBracket or TokenKind.LBrace or TokenKind.Bang or TokenKind.Not or
        TokenKind.Minus or TokenKind.Fn or TokenKind.Match or TokenKind.Pipe or
        TokenKind.PipePipe;

    // ---------------- 顶层 ----------------

    public CompilationUnit ParseCompilationUnit()
    {
        var unit = new CompilationUnit { Source = _source };
        while (!AtEnd)
        {
            int before = _index;
            var stmt = ParseStatement();
            if (stmt is not null) unit.Statements.Add(stmt);
            Match(TokenKind.Semicolon);
            if (_index == before) Advance(); // 保证前进
        }
        return unit;
    }

    private BlockStmt ParseBlock(string context)
    {
        var block = new BlockStmt();
        if (!Check(TokenKind.LBrace))
        {
            _bag.Report(ErrorCode.ExpectedBlock, CurrentSpan,
                $"{context}后需要 '{{'，但读到 {Current.Kind.Display()}");
            return block;
        }
        var open = Advance();
        block.Span = open.Span;

        while (!AtEnd && !Check(TokenKind.RBrace))
        {
            int before = _index;
            var stmt = ParseStatement();
            if (stmt is not null) block.Statements.Add(stmt);
            Match(TokenKind.Semicolon);
            if (_index == before) Advance();
        }

        if (Check(TokenKind.RBrace))
        {
            var close = Advance();
            block.Span = new TextSpan(open.Start, close.End - open.Start);
        }
        else
        {
            _bag.Report(ErrorCode.ExpectedToken, CurrentSpan, "代码块没有闭合的 '}'");
            block.Span = new TextSpan(open.Start, Current.End - open.Start);
        }
        return block;
    }

    // ---------------- 语句 ----------------

    public Statement? ParseStatement()
    {
        switch (Current.Kind)
        {
            case TokenKind.Semicolon:
                Advance();
                return null;
            case TokenKind.Import:
                return ParseImport();
            case TokenKind.Class:
                return ParseClass(new MemberModifiers());
            case TokenKind.Interface:
                return ParseInterface(new MemberModifiers());
            case TokenKind.Enum:
                return ParseEnum(new MemberModifiers());
            case TokenKind.Let:
            case TokenKind.Var:
            case TokenKind.Const:
                return ParseVarDecl();
            case TokenKind.If:
                return ParseIf();
            case TokenKind.While:
                return ParseWhile();
            case TokenKind.For:
                return ParseFor();
            case TokenKind.Loop:
                return ParseLoop();
            case TokenKind.Return:
                return ParseReturn();
            case TokenKind.Break:
                Advance();
                return new BreakStmt { Span = Previous.Span };
            case TokenKind.Continue:
                Advance();
                return new ContinueStmt { Span = Previous.Span };
            case TokenKind.Try:
                return ParseTry();
            case TokenKind.Throw:
            {
                var kw = Advance();
                var value = ParseExpression();
                return new ThrowStmt { Value = value, Span = new TextSpan(kw.Start, value.Span.End - kw.Start) };
            }
            case TokenKind.Pub:
            case TokenKind.Static:
            case TokenKind.Override:
            case TokenKind.Abstract:
                return ParseModifiedDeclaration();
            case TokenKind.Fn when Peek().Kind == TokenKind.LParen:
                return ParseExpressionStatement();
            case TokenKind.Fn:
                return ParseModifiedDeclaration();
            default:
                return ParseExpressionStatement();
        }
    }

    private Statement ParseModifiedDeclaration()
    {
        var mods = ParseModifiers();
        switch (Current.Kind)
        {
            case TokenKind.Fn:
                return ParseFnDecl(mods);
            case TokenKind.Class:
                return ParseClass(mods);
            case TokenKind.Interface:
                return ParseInterface(mods);
            case TokenKind.Enum:
                return ParseEnum(mods);
            case TokenKind.Let:
            case TokenKind.Var:
            case TokenKind.Const:
            {
                var decl = ParseVarDecl();
                if (mods.IsStatic || mods.IsOverride || mods.IsAbstract)
                    _bag.Report(ErrorCode.MisplacedKeyword, mods.Span,
                        "局部变量不能使用 static / override / abstract 修饰");
                return decl;
            }
            default:
                _bag.Report(ErrorCode.MisplacedKeyword, CurrentSpan,
                    $"修饰符之后应为 fn / class / interface / enum / let / var / const，但读到 {Current.Kind.Display()}");
                return ParseExpressionStatement();
        }
    }

    private MemberModifiers ParseModifiers()
    {
        var mods = new MemberModifiers();
        while (true)
        {
            switch (Current.Kind)
            {
                case TokenKind.Pub:
                    if (mods.IsPub) _bag.Report(ErrorCode.DuplicateModifier, CurrentSpan, "重复的 'pub' 修饰");
                    else { mods.IsPub = true; mods.PubSpan = CurrentSpan; mods.Span = mods.Span.Union(CurrentSpan); }
                    Advance();
                    break;
                case TokenKind.Static:
                    if (mods.IsStatic) _bag.Report(ErrorCode.DuplicateModifier, CurrentSpan, "重复的 'static' 修饰");
                    else { mods.IsStatic = true; mods.StaticSpan = CurrentSpan; mods.Span = mods.Span.Union(CurrentSpan); }
                    Advance();
                    break;
                case TokenKind.Override:
                    if (mods.IsOverride) _bag.Report(ErrorCode.DuplicateModifier, CurrentSpan, "重复的 'override' 修饰");
                    else { mods.IsOverride = true; mods.OverrideSpan = CurrentSpan; mods.Span = mods.Span.Union(CurrentSpan); }
                    Advance();
                    break;
                case TokenKind.Abstract:
                    if (mods.IsAbstract) _bag.Report(ErrorCode.DuplicateModifier, CurrentSpan, "重复的 'abstract' 修饰");
                    else { mods.IsAbstract = true; mods.AbstractSpan = CurrentSpan; mods.Span = mods.Span.Union(CurrentSpan); }
                    Advance();
                    break;
                default:
                    return mods;
            }
        }
    }

    private Statement ParseExpressionStatement()
    {
        var expr = ParseExpression();
        return new ExprStmt { Expression = expr, Span = expr.Span };
    }

    private VarDeclStmt ParseVarDecl()
    {
        var kw = Advance();
        var kind = kw.Kind switch
        {
            TokenKind.Let => VarDeclKind.Let,
            TokenKind.Var => VarDeclKind.Var,
            _ => VarDeclKind.Const,
        };
        string name = ExpectIdentifier("变量名");
        var nameSpan = name == "" ? kw.Span : Previous.Span;

        TypeSyntax? type = null;
        if (Match(TokenKind.Colon)) type = ParseType();

        Expression? value = null;
        if (Match(TokenKind.Assign)) value = ParseExpression();

        if (value is null && kind != VarDeclKind.Var)
        {
            _bag.Report(ErrorCode.ExpectedExpression, kw.Span,
                kind == VarDeclKind.Const ? "'const' 必须初始化" : "'let' 必须初始化");
        }
        if (value is null && type is null && kind == VarDeclKind.Var)
            _bag.Report(ErrorCode.ExpectedExpression, kw.Span,
                "'var' 若不初始化则必须给出类型标注", "例如 var x: Int");

        return new VarDeclStmt
        {
            Kind = kind,
            Name = name,
            NameSpan = nameSpan,
            KeywordSpan = kw.Span,
            Type = type,
            Value = value,
            Span = new TextSpan(kw.Start, (value?.Span.End ?? type?.Span.End ?? kw.End) - kw.Start),
        };
    }

    private Statement ParseIf()
    {
        var kw = Advance();
        var cond = ParseExpression();
        var then = ParseBlock("if 条件");
        Statement? els = null;
        if (Match(TokenKind.Else))
        {
            els = Check(TokenKind.If) ? ParseIf() : (Statement)ParseBlock("else");
        }
        int end = els?.Span.End ?? then.Span.End;
        return new IfStmt { Cond = cond, Then = then, Else = els, Span = new TextSpan(kw.Start, end - kw.Start) };
    }

    private Statement ParseWhile()
    {
        var kw = Advance();
        var cond = ParseExpression();
        var body = ParseBlock("while 条件");
        return new WhileStmt { Cond = cond, Body = body, Span = new TextSpan(kw.Start, body.Span.End - kw.Start) };
    }

    private Statement ParseFor()
    {
        var kw = Advance();
        string name = ExpectIdentifier("循环变量名");
        var nameSpan = name == "" ? kw.Span : Previous.Span;
        TypeSyntax? type = null;
        if (Match(TokenKind.Colon)) type = ParseType();
        Expect(TokenKind.In, "'for' 循环需要 'in'", "例如 for x in list { ... }");
        var iter = ParseExpression();
        var body = ParseBlock("for 循环");
        return new ForStmt
        {
            Name = name,
            NameSpan = nameSpan,
            Type = type,
            Iterable = iter,
            Body = body,
            Span = new TextSpan(kw.Start, body.Span.End - kw.Start),
        };
    }

    private Statement ParseLoop()
    {
        var kw = Advance();
        var body = ParseBlock("loop");
        return new LoopStmt { Body = body, Span = new TextSpan(kw.Start, body.Span.End - kw.Start) };
    }

    private Statement ParseReturn()
    {
        var kw = Advance();
        Expression? value = null;
        if (StartsExpression(Current.Kind) && ContinuesExpression())
            value = ParseExpression();
        return new ReturnStmt
        {
            Value = value,
            KeywordSpan = kw.Span,
            Span = new TextSpan(kw.Start, (value?.Span.End ?? kw.End) - kw.Start),
        };
    }

    private Statement ParseTry()
    {
        var kw = Advance();
        var body = ParseBlock("try");
        string? catchName = null;
        var catchNameSpan = TextSpan.Empty;
        BlockStmt? catchBody = null;
        BlockStmt? finallyBody = null;

        if (Match(TokenKind.Catch))
        {
            if (Check(TokenKind.Identifier))
            {
                var t = Advance();
                catchName = (string)t.Value!;
                catchNameSpan = t.Span;
            }
            catchBody = ParseBlock("catch");
        }
        if (Match(TokenKind.Finally))
            finallyBody = ParseBlock("finally");

        if (catchBody is null && finallyBody is null)
            _bag.Report(ErrorCode.ExpectedToken, kw.Span,
                "'try' 需要 'catch' 或 'finally' 分支");

        return new TryStmt
        {
            Body = body,
            CatchName = catchName,
            CatchNameSpan = catchNameSpan,
            CatchBody = catchBody,
            FinallyBody = finallyBody,
            Span = new TextSpan(kw.Start, (finallyBody ?? catchBody ?? body).Span.End - kw.Start),
        };
    }

    private Statement ParseImport()
    {
        var kw = Advance();
        string path = "";
        var pathSpan = CurrentSpan;
        if (Check(TokenKind.StringLiteral))
        {
            var t = Advance();
            var s = (InterpolatedString)t.Value!;
            if (s.IsPlain) path = s.PlainText;
            else _bag.Report(ErrorCode.UnexpectedToken, t.Span, "模块路径不能包含插值");
            pathSpan = t.Span;
        }
        else
        {
            _bag.Report(ErrorCode.UnexpectedToken, CurrentSpan,
                "'import' 后需要模块路径字符串", "例如 import \"std.math\"");
        }

        string? alias = null;
        var aliasSpan = TextSpan.Empty;
        if (Match(TokenKind.As))
        {
            alias = ExpectIdentifier("别名");
            aliasSpan = Previous.Span;
        }

        return new ImportStmt
        {
            Path = path,
            Alias = alias,
            PathSpan = pathSpan,
            AliasSpan = aliasSpan,
            Span = new TextSpan(kw.Start, (aliasSpan.Length > 0 ? aliasSpan.End : pathSpan.End) - kw.Start),
        };
    }

    private FnDecl ParseFnDecl(MemberModifiers mods)
    {
        var kw = Advance(); // fn
        string name = ExpectIdentifier("函数名");
        var nameSpan = Previous.Span;
        var tps = ParseTypeParamList();
        var (ps, ret) = ParseSignature();
        var body = ParseBlock($"函数 '{name}'");
        var decl = new FnDecl
        {
            Name = name,
            NameSpan = nameSpan,
            ReturnType = ret,
            Body = body,
            Mods = mods,
            Span = new TextSpan(mods.Span.Length > 0 ? mods.Span.Start : kw.Start, body.Span.End - kw.Start),
        };
        decl.TypeParams.AddRange(tps);
        decl.Params.AddRange(ps);
        return decl;
    }

    /// <summary>解析可选的泛型形参列表 &lt;T, U&gt;；无则返回空列表。</summary>
    private List<TypeParamSyntax> ParseTypeParamList()
    {
        var list = new List<TypeParamSyntax>();
        if (!Check(TokenKind.Lt)) return list;
        Advance();
        do
        {
            string n = ExpectIdentifier("类型参数名");
            var span = n == "" ? CurrentSpan : Previous.Span;
            list.Add(new TypeParamSyntax { Name = n, NameSpan = span, Span = span });
        } while (Match(TokenKind.Comma));
        Expect(TokenKind.Gt, "泛型参数列表缺少 '>'");
        return list;
    }

    private (List<ParamSyntax> ps, TypeSyntax? ret) ParseSignature()
    {
        var ps = new List<ParamSyntax>();
        Expect(TokenKind.LParen, "缺少 '('");
        while (!AtEnd && !Check(TokenKind.RParen) && !Check(TokenKind.LBrace))
        {
            int before = _index;
            string name = ExpectIdentifier("参数名");
            var nameSpan = Previous.Span;
            TypeSyntax? type = null;
            if (Match(TokenKind.Colon)) type = ParseType();
            ps.Add(new ParamSyntax { Name = name, NameSpan = nameSpan, Type = type, Span = nameSpan });
            if (!Match(TokenKind.Comma)) break;
            if (_index == before) Advance();
        }
        Expect(TokenKind.RParen, "缺少 ')'");

        TypeSyntax? ret = null;
        if (Match(TokenKind.Colon)) ret = ParseType();
        return (ps, ret);
    }

    private ClassDecl ParseClass(MemberModifiers mods)
    {
        var kw = Advance(); // class
        string name = ExpectIdentifier("类名");
        var nameSpan = Previous.Span;
        var typeParams = ParseTypeParamList();

        TypeSyntax? baseType = null;
        var baseSpan = TextSpan.Empty;
        var interfaces = new List<TypeSyntax>();

        if (Match(TokenKind.Colon))
        {
            var first = ParseType();
            baseType = first;
            baseSpan = first.Span;
            while (Match(TokenKind.Comma))
                interfaces.Add(ParseType());
        }

        var members = ParseClassMembers($"类 '{name}'");
        var decl = new ClassDecl
        {
            Name = name,
            NameSpan = nameSpan,
            Base = baseType,
            BaseSpan = baseSpan,
            Mods = mods,
            Span = new TextSpan(kw.Start, members.Span.End - kw.Start),
        };
        decl.TypeParams.AddRange(typeParams);
        decl.Interfaces.AddRange(interfaces);
        decl.Members.AddRange(members.Members);
        return decl;
    }

    private InterfaceDecl ParseInterface(MemberModifiers mods)
    {
        var kw = Advance();
        string name = ExpectIdentifier("接口名");
        var nameSpan = Previous.Span;
        var iTypeParams = ParseTypeParamList();
        var parents = new List<TypeSyntax>();
        if (Match(TokenKind.Colon))
        {
            do { parents.Add(ParseType()); } while (Match(TokenKind.Comma));
        }
        var members = ParseClassMembers($"接口 '{name}'", interfaceMode: true);
        var decl = new InterfaceDecl
        {
            Name = name,
            NameSpan = nameSpan,
            Mods = mods,
            Span = new TextSpan(kw.Start, members.Span.End - kw.Start),
        };
        decl.TypeParams.AddRange(iTypeParams);
        decl.Parents.AddRange(parents);
        decl.Members.AddRange(members.Members);
        return decl;
    }

    private EnumDecl ParseEnum(MemberModifiers mods)
    {
        var kw = Advance();
        string name = ExpectIdentifier("枚举名");
        var nameSpan = Previous.Span;
        var cases = new List<EnumCaseDecl>();

        if (Match(TokenKind.LBrace))
        {
            while (!AtEnd && !Check(TokenKind.RBrace))
            {
                int before = _index;
                string caseName = ExpectIdentifier("枚举成员名");
                var caseSpan = Previous.Span;
                var decl = new EnumCaseDecl { Name = caseName, NameSpan = caseSpan, Span = caseSpan };
                if (Match(TokenKind.LParen))
                {
                    do
                    {
                        decl.Payload.Add(ParseType());
                    } while (Match(TokenKind.Comma));
                    var rp = Expect(TokenKind.RParen, "缺少 ')'");
                    decl.Span = new TextSpan(caseSpan.Start, rp.End - caseSpan.Start);
                }
                cases.Add(decl);
                if (!Match(TokenKind.Comma)) break;
                if (_index == before) Advance();
            }
            Expect(TokenKind.RBrace, "缺少 '}'");
        }
        else
        {
            _bag.Report(ErrorCode.ExpectedBlock, CurrentSpan, "枚举定义需要 '{'");
        }

        var en = new EnumDecl
        {
            Name = name,
            NameSpan = nameSpan,
            Mods = mods,
            Span = new TextSpan(kw.Start, Previous.End - kw.Start),
        };
        en.Cases.AddRange(cases);
        return en;
    }

    private sealed class MemberList
    {
        public List<ClassMember> Members { get; } = new();
        public TextSpan Span { get; set; }
    }

    private MemberList ParseClassMembers(string context, bool interfaceMode = false)
    {
        var result = new MemberList();
        if (!Check(TokenKind.LBrace))
        {
            _bag.Report(ErrorCode.ExpectedBlock, CurrentSpan, $"{context}定义需要 '{{'");
            result.Span = CurrentSpan;
            return result;
        }
        var open = Advance();

        while (!AtEnd && !Check(TokenKind.RBrace))
        {
            int before = _index;
            var m = ParseClassMember(interfaceMode);
            if (m is not null) result.Members.Add(m);
            Match(TokenKind.Semicolon);
            if (_index == before) Advance();
        }

        if (Check(TokenKind.RBrace))
        {
            var close = Advance();
            result.Span = new TextSpan(open.Start, close.End - open.Start);
        }
        else
        {
            _bag.Report(ErrorCode.ExpectedToken, CurrentSpan, $"'{context}' 没有闭合的 '}}'");
            result.Span = new TextSpan(open.Start, Current.End - open.Start);
        }
        return result;
    }

    private ClassMember? ParseClassMember(bool interfaceMode = false)
    {
        switch (Current.Kind)
        {
            case TokenKind.Semicolon:
                Advance();
                return null;
            case TokenKind.Pub:
            case TokenKind.Static:
            case TokenKind.Override:
            case TokenKind.Abstract:
            case TokenKind.Fn:
            case TokenKind.Let:
            case TokenKind.Var:
            case TokenKind.Const:
                break;
            case TokenKind.Identifier when Current.Value as string == "init" && Peek().Kind == TokenKind.LParen:
                return ParseMethod(new MemberModifiers(), isCtor: true, interfaceMode);
            default:
                _bag.Report(ErrorCode.UnexpectedToken, CurrentSpan,
                    $"类成员应为 fn / var / let / const / init，但读到 {Current.Kind.Display()}");
                Advance();
                return null;
        }

        var mods = ParseModifiers();
        switch (Current.Kind)
        {
            case TokenKind.Fn:
                return ParseMethod(mods, isCtor: false, interfaceMode);
            case TokenKind.Let:
            case TokenKind.Var:
            case TokenKind.Const:
                if (interfaceMode)
                    _bag.Report(ErrorCode.InterfaceMethodMustBeAbstract, CurrentSpan,
                        "接口只能声明方法，不能声明字段");
                return ParseField(mods);
            case TokenKind.Identifier when Current.Value as string == "init" && Peek().Kind == TokenKind.LParen:
                if (mods.IsPub || mods.IsStatic || mods.IsOverride || mods.IsAbstract)
                    _bag.Report(ErrorCode.MisplacedKeyword, mods.Span, "构造器不能使用修饰符");
                if (interfaceMode)
                    _bag.Report(ErrorCode.InterfaceMethodMustBeAbstract, CurrentSpan, "接口不能声明构造器");
                return ParseMethod(new MemberModifiers(), isCtor: true, interfaceMode);
            default:
                _bag.Report(ErrorCode.UnexpectedToken, CurrentSpan,
                    $"类成员应为 fn / var / let / const，但读到 {Current.Kind.Display()}");
                Advance();
                return null;
        }
    }

    private ClassMember ParseField(MemberModifiers mods)
    {
        var kw = Advance();
        var kind = kw.Kind switch
        {
            TokenKind.Let => VarDeclKind.Let,
            TokenKind.Var => VarDeclKind.Var,
            _ => VarDeclKind.Const,
        };
        string name = ExpectIdentifier("字段名");
        var nameSpan = name == "" ? kw.Span : Previous.Span;
        TypeSyntax? type = null;
        if (Match(TokenKind.Colon)) type = ParseType();
        Expression? value = null;
        if (Match(TokenKind.Assign)) value = ParseExpression();

        if (kind == VarDeclKind.Let && value is null && type is null)
            _bag.Report(ErrorCode.ExpectedExpression, kw.Span, "'let' 字段必须初始化");

        return new FieldDecl
        {
            Mods = mods,
            Kind = kind,
            Name = name,
            NameSpan = nameSpan,
            KeywordSpan = kw.Span,
            Type = type,
            Value = value,
            Span = new TextSpan(mods.Span.Length > 0 ? mods.Span.Start : kw.Start,
                (value?.Span.End ?? type?.Span.End ?? kw.End) - kw.Start),
        };
    }

    private ClassMember ParseMethod(MemberModifiers mods, bool isCtor, bool interfaceMode = false)
    {
        var kw = Advance(); // fn 或 init
        string name;
        var nameSpan = kw.Span;
        if (isCtor)
        {
            name = "init";
            nameSpan = kw.Span;
        }
        else
        {
            name = ExpectIdentifier("方法名");
            nameSpan = name == "" ? kw.Span : Previous.Span;
        }

        var tps = isCtor ? new List<TypeParamSyntax>() : ParseTypeParamList();
        var (ps, ret) = ParseSignature();

        BlockStmt? body = null;
        if (Check(TokenKind.LBrace))
            body = ParseBlock($"方法 '{name}'");
        else if (!mods.IsAbstract && !interfaceMode)
            _bag.Report(ErrorCode.ExpectedBlock, CurrentSpan,
                isCtor ? "构造器需要函数体" : $"方法 '{name}' 需要函数体",
                "抽象方法请加 'abstract' 修饰且不能有函数体");

        var m = new MethodDecl
        {
            Mods = mods,
            Name = name,
            NameSpan = nameSpan,
            ReturnType = ret,
            Body = body,
            IsConstructor = isCtor,
            Span = new TextSpan(mods.Span.Length > 0 ? mods.Span.Start : kw.Start,
                (body?.Span.End ?? nameSpan.End) - kw.Start),
        };
        m.TypeParams.AddRange(tps);
        m.Params.AddRange(ps);
        return m;
    }

    // ---------------- 类型 ----------------

    public TypeSyntax ParseType()
    {
        if (Match(TokenKind.Fn))
        {
            var ft = new FnTypeSyntax();
            Expect(TokenKind.LParen, "函数类型需要 '('");
            var start = Previous.Start;
            while (!AtEnd && !Check(TokenKind.RParen))
            {
                int before = _index;
                ft.ParamTypes.Add(ParseType());
                if (!Match(TokenKind.Comma)) break;
                if (_index == before) Advance();
            }
            Expect(TokenKind.RParen, "缺少 ')'");
            Expect(TokenKind.Colon, "函数类型需要返回类型", "例如 fn(Int): Int");
            ft.ReturnType = ParseType();
            ft.Span = new TextSpan(start, ft.ReturnType.Span.End - start);
            return ft;
        }

        if (Check(TokenKind.Identifier) || Check(TokenKind.Null))
        {
            var t = Advance();
            string name = t.Kind == TokenKind.Null ? "Null" : (string)t.Value!;
            var nt = new NamedTypeSyntax { Name = name, NameSpan = t.Span, Span = t.Span };
            if (Match(TokenKind.Lt))
            {
                do
                {
                    nt.TypeArgs.Add(ParseType());
                } while (Match(TokenKind.Comma));
                var gt = Expect(TokenKind.Gt, "缺少 '>'");
                nt.Span = new TextSpan(t.Start, gt.End - t.Start);
            }
            return nt;
        }

        _bag.Report(ErrorCode.ExpectedType, CurrentSpan,
            $"此处需要类型，但读到 {Current.Kind.Display()}",
            "可用类型: Int Float Str Bool Any Null List<T> Map<K,V> 或自定义类型");
        return new NamedTypeSyntax { Name = "?", NameSpan = CurrentSpan, Span = CurrentSpan };
    }

    // ---------------- 表达式 ----------------

    public Expression ParseExpression() => ParseAssignment();

    private Expression ParseAssignment()
    {
        var target = ParseTernary();
        var k = Current.Kind;
        if (k is TokenKind.Assign or TokenKind.PlusEq or TokenKind.MinusEq or
            TokenKind.StarEq or TokenKind.SlashEq or TokenKind.PercentEq)
        {
            if (!ContinuesExpression()) return target;
            var op = Advance();
            var value = ParseAssignment();
            return new AssignExpr
            {
                Target = target,
                Op = op.Kind,
                OpSpan = op.Span,
                Value = value,
                Span = new TextSpan(target.Span.Start, value.Span.End - target.Span.Start),
            };
        }
        return target;
    }

    private Expression ParseTernary()
    {
        var cond = ParseCoalesce();
        if (Check(TokenKind.Question) && ContinuesExpression())
        {
            Advance();
            var then = ParseAssignment();
            Expect(TokenKind.Colon, "三元表达式缺少 ':'");
            var els = ParseAssignment();
            return new TernaryExpr
            {
                Cond = cond,
                Then = then,
                Else = els,
                Span = new TextSpan(cond.Span.Start, els.Span.End - cond.Span.Start),
            };
        }
        return cond;
    }

    private Expression ParseCoalesce()
    {
        var left = ParseOr();
        while (Check(TokenKind.QuestionQuestion) && ContinuesExpression())
        {
            Advance();
            var right = ParseOr();
            left = new BinaryExpr
            {
                Left = left, Op = TokenKind.QuestionQuestion, OpSpan = Previous.Span, Right = right,
                Span = new TextSpan(left.Span.Start, right.Span.End - left.Span.Start),
            };
        }
        return left;
    }

    private Expression ParseOr()
    {
        var left = ParseAnd();
        while (CheckAny(TokenKind.PipePipe, TokenKind.Or) && ContinuesExpression())
        {
            var op = Advance();
            var right = ParseAnd();
            left = new BinaryExpr
            {
                Left = left, Op = TokenKind.PipePipe, OpSpan = op.Span, Right = right,
                Span = new TextSpan(left.Span.Start, right.Span.End - left.Span.Start),
            };
        }
        return left;
    }

    private Expression ParseAnd()
    {
        var left = ParseEquality();
        while (CheckAny(TokenKind.AmpAmp, TokenKind.And) && ContinuesExpression())
        {
            var op = Advance();
            var right = ParseEquality();
            left = new BinaryExpr
            {
                Left = left, Op = TokenKind.AmpAmp, OpSpan = op.Span, Right = right,
                Span = new TextSpan(left.Span.Start, right.Span.End - left.Span.Start),
            };
        }
        return left;
    }

    private Expression ParseEquality()
    {
        var left = ParseRelational();
        while (CheckAny(TokenKind.EqEq, TokenKind.BangEq) && ContinuesExpression())
        {
            var op = Advance();
            var right = ParseRelational();
            left = new BinaryExpr
            {
                Left = left, Op = op.Kind, OpSpan = op.Span, Right = right,
                Span = new TextSpan(left.Span.Start, right.Span.End - left.Span.Start),
            };
        }
        return left;
    }

    private Expression ParseRelational()
    {
        var left = ParseAdditive();
        while (true)
        {
            var k = Current.Kind;
            bool isOp = k is TokenKind.Lt or TokenKind.LtEq or TokenKind.Gt or TokenKind.GtEq
                or TokenKind.Is or TokenKind.As;
            if (!isOp || !ContinuesExpression()) break;

            var op = Advance();
            if (op.Kind == TokenKind.Is)
            {
                var type = ParseType();
                left = new IsExpr
                {
                    Target = left, Type = type,
                    Span = new TextSpan(left.Span.Start, type.Span.End - left.Span.Start),
                };
            }
            else if (op.Kind == TokenKind.As)
            {
                var type = ParseType();
                left = new CastExpr
                {
                    Target = left, Type = type,
                    Span = new TextSpan(left.Span.Start, type.Span.End - left.Span.Start),
                };
            }
            else
            {
                var right = ParseAdditive();
                left = new BinaryExpr
                {
                    Left = left, Op = op.Kind, OpSpan = op.Span, Right = right,
                    Span = new TextSpan(left.Span.Start, right.Span.End - left.Span.Start),
                };
            }
        }
        return left;
    }

    private Expression ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (CheckAny(TokenKind.Plus, TokenKind.Minus) && ContinuesExpression())
        {
            var op = Advance();
            var right = ParseMultiplicative();
            left = new BinaryExpr
            {
                Left = left, Op = op.Kind, OpSpan = op.Span, Right = right,
                Span = new TextSpan(left.Span.Start, right.Span.End - left.Span.Start),
            };
        }
        return left;
    }

    private Expression ParseMultiplicative()
    {
        var left = ParseUnary();
        while (CheckAny(TokenKind.Star, TokenKind.Slash, TokenKind.Percent) && ContinuesExpression())
        {
            var op = Advance();
            var right = ParseUnary();
            left = new BinaryExpr
            {
                Left = left, Op = op.Kind, OpSpan = op.Span, Right = right,
                Span = new TextSpan(left.Span.Start, right.Span.End - left.Span.Start),
            };
        }
        return left;
    }

    private Expression ParseUnary()
    {
        if (CheckAny(TokenKind.Minus, TokenKind.Bang, TokenKind.Not))
        {
            var op = Advance();
            var operand = ParseUnary();
            return new UnaryExpr
            {
                Op = op.Kind, OpSpan = op.Span, Operand = operand,
                Span = new TextSpan(op.Start, operand.Span.End - op.Start),
            };
        }
        return ParsePostfix();
    }

    private Expression ParsePostfix()
    {
        var expr = ParsePrimary();
        while (true)
        {
            var k = Current.Kind;
            if (k is TokenKind.Dot or TokenKind.QuestionDot)
            {
                Advance();
                string name = ExpectIdentifier("成员名");
                var nameSpan = Previous.Span;
                expr = new MemberExpr
                {
                    Target = expr,
                    Name = name,
                    NullSafe = k == TokenKind.QuestionDot,
                    NameSpan = nameSpan,
                    Span = new TextSpan(expr.Span.Start, nameSpan.End - expr.Span.Start),
                };
                continue;
            }

            if ((k is TokenKind.LParen or TokenKind.LBracket) && ContinuesExpression())
            {
                if (k == TokenKind.LParen)
                {
                    var lp = Advance();
                    var call = new CallExpr { Callee = expr, LParenSpan = lp.Span };
                    while (!AtEnd && !Check(TokenKind.RParen))
                    {
                        int before = _index;
                        call.Args.Add(ParseExpression());
                        if (!Match(TokenKind.Comma)) break;
                        if (_index == before) Advance();
                    }
                    var rp = Expect(TokenKind.RParen, "缺少 ')'");
                    call.RParenSpan = rp.Span;
                    call.Span = new TextSpan(expr.Span.Start, rp.End - expr.Span.Start);
                    expr = call;
                }
                else
                {
                    Advance();
                    var index = ParseExpression();
                    var rb = Expect(TokenKind.RBracket, "缺少 ']'");
                    expr = new IndexExpr
                    {
                        Target = expr, Index = index,
                        Span = new TextSpan(expr.Span.Start, rb.End - expr.Span.Start),
                    };
                }
                continue;
            }

            break;
        }
        return expr;
    }

    private Expression ParsePrimary()
    {
        var t = Current;
        switch (t.Kind)
        {
            case TokenKind.IntLiteral:
                Advance();
                return new LiteralExpr { Kind = LiteralKind.Int, Value = t.Value, Span = t.Span };
            case TokenKind.FloatLiteral:
                Advance();
                return new LiteralExpr { Kind = LiteralKind.Float, Value = t.Value, Span = t.Span };
            case TokenKind.StringLiteral:
                Advance();
                return new LiteralExpr { Kind = LiteralKind.Str, Value = t.Value, Span = t.Span };
            case TokenKind.True:
            case TokenKind.False:
                Advance();
                return new LiteralExpr { Kind = LiteralKind.Bool, Value = t.Kind == TokenKind.True, Span = t.Span };
            case TokenKind.Null:
                Advance();
                return new LiteralExpr { Kind = LiteralKind.Null, Value = null, Span = t.Span };
            case TokenKind.Identifier:
                Advance();
                return new NameExpr { Name = (string)t.Value!, NameSpan = t.Span, Span = t.Span };
            case TokenKind.This:
                Advance();
                return new ThisExpr { Span = t.Span };
            case TokenKind.Super:
                Advance();
                return new SuperExpr { Span = t.Span };
            case TokenKind.New:
                return ParseNew();
            case TokenKind.LParen:
            {
                Advance();
                var inner = ParseExpression();
                int end = Check(TokenKind.RParen) ? Advance().End : Previous.End;
                inner.Span = new TextSpan(t.Start, end - t.Start);
                return inner;
            }
            case TokenKind.LBracket:
                return ParseListLiteral();
            case TokenKind.LBrace:
                return ParseMapLiteral();
            case TokenKind.Pipe:
            case TokenKind.PipePipe:
                return ParseArrowLambda();
            case TokenKind.Fn:
                return ParseAnonymousFn();
            case TokenKind.Match:
                return ParseMatch();
            default:
                _bag.Report(ErrorCode.ExpectedExpression, t.Span,
                    $"此处需要表达式，但读到 {t.Kind.Display()}");
                if (!AtEnd) Advance();
                return new LiteralExpr { Kind = LiteralKind.Null, Value = null, Span = t.Span };
        }
    }

    private Expression ParseNew()
    {
        var kw = Advance();
        var type = ParseType();
        if (Check(TokenKind.LParen))
        {
            var lp = Advance().Span;
            var call = new NewExpr { Type = type, LParenSpan = lp };
            while (!AtEnd && !Check(TokenKind.RParen))
            {
                int before = _index;
                call.Args.Add(ParseExpression());
                if (!Match(TokenKind.Comma)) break;
                if (_index == before) Advance();
            }
            var rp = Expect(TokenKind.RParen, "缺少 ')'");
            call.Span = new TextSpan(kw.Start, rp.End - kw.Start);
            return call;
        }
        _bag.Report(ErrorCode.ExpectedToken, CurrentSpan, "'new' 后需要构造调用",
            "例如 new Dog(\"旺财\")");
        return new NewExpr
        {
            Type = type,
            LParenSpan = CurrentSpan,
            Span = new TextSpan(kw.Start, Current.End - kw.Start),
        };
    }

    private Expression ParseListLiteral()
    {
        var lb = Advance();
        var list = new ListExpr();
        while (!AtEnd && !Check(TokenKind.RBracket))
        {
            int before = _index;
            list.Elements.Add(ParseExpression());
            if (!Match(TokenKind.Comma)) break;
            if (_index == before) Advance();
        }
        var rb = Expect(TokenKind.RBracket, "缺少 ']'");
        list.Span = new TextSpan(lb.Start, rb.End - lb.Start);
        return list;
    }

    private Expression ParseMapLiteral()
    {
        var lb = Advance();
        var map = new MapExpr();
        while (!AtEnd && !Check(TokenKind.RBrace))
        {
            int before = _index;
            var key = ParseExpression();
            if (key is NameExpr ne)
            {
                var s = new InterpolatedString();
                s.Parts.Add(StringPart.Literal(ne.Name));
                key = new LiteralExpr { Kind = LiteralKind.Str, Value = s, Span = ne.Span };
            }
            Expect(TokenKind.Colon, "映射字面量需要 ':' 分隔键值");
            var value = ParseExpression();
            map.Entries.Add((key, value));
            if (!Match(TokenKind.Comma)) break;
            if (_index == before) Advance();
        }
        var rb = Expect(TokenKind.RBrace, "缺少 '}'");
        map.Span = new TextSpan(lb.Start, rb.End - lb.Start);
        return map;
    }

    private Expression ParseArrowLambda()
    {
        var start = Current;
        var ps = new List<ParamSyntax>();
        if (Check(TokenKind.PipePipe))
        {
            Advance(); // || 表示无参数
        }
        else
        {
            Advance(); // |
            while (!AtEnd && !Check(TokenKind.Pipe))
            {
                int before = _index;
                string name = ExpectIdentifier("参数名");
                var nameSpan = name == "" ? Previous.Span : Previous.Span;
                TypeSyntax? type = null;
                if (Match(TokenKind.Colon)) type = ParseType();
                ps.Add(new ParamSyntax { Name = name, NameSpan = nameSpan, Type = type, Span = nameSpan });
                if (!Match(TokenKind.Comma)) break;
                if (_index == before) Advance();
            }
            Expect(TokenKind.Pipe, "lambda 参数列表需要闭合的 '|'");
        }

        var body = ParseExpression();
        var lambda = new LambdaExpr { BodyExpr = body, Span = new TextSpan(start.Start, body.Span.End - start.Start) };
        lambda.Params.AddRange(ps);
        return lambda;
    }

    private Expression ParseAnonymousFn()
    {
        var kw = Advance(); // fn
        var (ps, ret) = ParseSignature();
        var body = ParseBlock("匿名函数");
        var lambda = new LambdaExpr
        {
            ReturnType = ret,
            BodyBlock = body,
            Span = new TextSpan(kw.Start, body.Span.End - kw.Start),
        };
        lambda.Params.AddRange(ps);
        return lambda;
    }

    private Expression ParseMatch()
    {
        var kw = Advance();
        var subject = ParseExpression();
        var match = new MatchExpr { Subject = subject };

        if (Check(TokenKind.LBrace))
        {
            Advance();
            while (!AtEnd && !Check(TokenKind.RBrace))
            {
                int before = _index;
                var arm = ParseMatchArm();
                match.Arms.Add(arm);
                Match(TokenKind.Comma);
                if (_index == before) Advance();
            }
            var rp = Expect(TokenKind.RBrace, "缺少 '}'");
            match.Span = new TextSpan(kw.Start, rp.End - kw.Start);
        }
        else
        {
            _bag.Report(ErrorCode.ExpectedBlock, CurrentSpan, "'match' 需要 '{'");
            match.Span = new TextSpan(kw.Start, Current.End - kw.Start);
        }
        return match;
    }

    private MatchArm ParseMatchArm()
    {
        var pattern = ParsePattern();
        Expression? guard = null;
        if (Match(TokenKind.When))
            guard = ParseExpression();
        var arrow = Expect(TokenKind.FatArrow, "match 分支需要 '=>'", "例如 1 => \"一\"");
        var body = ParseExpression();
        return new MatchArm
        {
            Pattern = pattern,
            Guard = guard,
            Body = body,
            ArrowSpan = arrow.Span,
            Span = new TextSpan(pattern.Span.Start, body.Span.End - pattern.Span.Start),
        };
    }

    public Pattern ParsePattern()
    {
        var t = Current;
        switch (t.Kind)
        {
            case TokenKind.IntLiteral:
                Advance();
                return new LiteralPattern { Kind = LiteralKind.Int, Value = t.Value, Span = t.Span };
            case TokenKind.FloatLiteral:
                Advance();
                return new LiteralPattern { Kind = LiteralKind.Float, Value = t.Value, Span = t.Span };
            case TokenKind.StringLiteral:
                Advance();
                return new LiteralPattern { Kind = LiteralKind.Str, Value = t.Value, Span = t.Span };
            case TokenKind.True:
            case TokenKind.False:
                Advance();
                return new LiteralPattern { Kind = LiteralKind.Bool, Value = t.Kind == TokenKind.True, Span = t.Span };
            case TokenKind.Null:
                Advance();
                return new LiteralPattern { Kind = LiteralKind.Null, Value = null, Span = t.Span };
            case TokenKind.Minus:
            {
                Advance();
                if (Check(TokenKind.IntLiteral) || Check(TokenKind.FloatLiteral))
                {
                    var n = Advance();
                    if (n.Value is long l)
                        return new LiteralPattern { Kind = LiteralKind.Int, Value = -l, Span = new TextSpan(t.Start, n.End - t.Start) };
                    return new LiteralPattern { Kind = LiteralKind.Float, Value = -(double)n.Value!, Span = new TextSpan(t.Start, n.End - t.Start) };
                }
                _bag.Report(ErrorCode.ExpectedPattern, t.Span, "'-' 后需要数字字面量");
                return new WildcardPattern { Span = t.Span };
            }
            case TokenKind.Is:
            {
                Advance();
                var type = ParseType();
                return new TypePattern { Type = type, Span = new TextSpan(t.Start, type.Span.End - t.Start) };
            }
            case TokenKind.LBracket:
            {
                var lb = Advance();
                var lp = new ListPattern();
                while (!AtEnd && !Check(TokenKind.RBracket))
                {
                    int before = _index;
                    lp.Items.Add(ParsePattern());
                    if (!Match(TokenKind.Comma)) break;
                    if (_index == before) Advance();
                }
                var rb = Expect(TokenKind.RBracket, "缺少 ']'");
                lp.Span = new TextSpan(lb.Start, rb.End - lb.Start);
                return lp;
            }
            case TokenKind.Identifier:
            {
                var first = Advance();
                string name = (string)first.Value!;
                string? qualifier = null;
                var nameSpan = first.Span;

                while (Check(TokenKind.Dot))
                {
                    Advance();
                    var part = ExpectIdentifier("枚举成员名");
                    qualifier = qualifier is null ? name : qualifier + "." + name;
                    name = part;
                    nameSpan = new TextSpan(first.Start, Previous.End - first.Start);
                }

                if (name == "_" && qualifier is null)
                    return new WildcardPattern { Span = first.Span };

                if (Check(TokenKind.LParen) && ContinuesExpression())
                {
                    Advance();
                    var ep = new EnumPattern
                    {
                        Qualifier = qualifier,
                        Name = name,
                        HasArgs = true,
                        NameSpan = nameSpan,
                        Span = new TextSpan(first.Start, Previous.End - first.Start),
                    };
                    while (!AtEnd && !Check(TokenKind.RParen))
                    {
                        int before = _index;
                        ep.Args.Add(ParsePattern());
                        if (!Match(TokenKind.Comma)) break;
                        if (_index == before) Advance();
                    }
                    var rp = Expect(TokenKind.RParen, "缺少 ')'");
                    ep.Span = new TextSpan(first.Start, rp.End - first.Start);
                    return ep;
                }

                if (qualifier is not null)
                {
                    return new EnumPattern
                    {
                        Qualifier = qualifier,
                        Name = name,
                        HasArgs = false,
                        NameSpan = nameSpan,
                        Span = new TextSpan(first.Start, Previous.End - first.Start),
                    };
                }

                return new BindingPattern { Name = name, NameSpan = first.Span, Span = first.Span };
            }
            default:
                _bag.Report(ErrorCode.ExpectedPattern, t.Span,
                    $"此处需要匹配模式，但读到 {t.Kind.Display()}",
                    "可用模式: 字面量、_、变量名、is 类型、枚举.成员、[列表]");
                if (!AtEnd && t.Kind != TokenKind.FatArrow && t.Kind != TokenKind.RBrace) Advance();
                return new WildcardPattern { Span = t.Span };
        }
    }
}
