using Ikd.Compiler.Diagnostics;
using Ikd.Compiler.Syntax;

namespace Ikd.Tests;

public static class ParserTests
{
    private static (CompilationUnit unit, DiagnosticBag bag) Parse(string text)
    {
        var src = SourceText.From(text, "test.ikd");
        var bag = new DiagnosticBag();
        var unit = Parser.Parse(src, bag);
        return (unit, bag);
    }

    private static Expression ExprOf(string text)
    {
        var (unit, bag) = Parse(text);
        Assert.False(bag.HasErrors, "解析出错: " + string.Join("; ", bag.Select(d => d.ToString())));
        Assert.Equal(1, unit.Statements.Count, "语句数量");
        var stmt = Assert2.IsType<ExprStmt>(unit.Statements[0]);
        return stmt.Expression;
    }

    [Test]
    public static void EmptyProgram()
    {
        var (unit, bag) = Parse("");
        Assert.False(bag.HasErrors);
        Assert.Equal(0, unit.Statements.Count);
    }

    [Test]
    public static void VarDeclStatements()
    {
        var (unit, bag) = Parse("let a = 1\nvar b: Int = 2\nconst c = 3");
        Assert.False(bag.HasErrors, string.Join("; ", bag.Select(d => d.ToString())));
        Assert.Equal(3, unit.Statements.Count);
        var d = Assert2.IsType<VarDeclStmt>(unit.Statements[0]);
        Assert.Equal("a", d.Name);
        Assert.Equal(VarDeclKind.Let, d.Kind);
        var d2 = Assert2.IsType<VarDeclStmt>(unit.Statements[1]);
        Assert.Equal(VarDeclKind.Var, d2.Kind);
        Assert.NotNull(d2.Type);
        Assert.Equal("Int", ((NamedTypeSyntax)d2.Type!).Name);
        Assert.Equal(VarDeclKind.Const, Assert2.IsType<VarDeclStmt>(unit.Statements[2]).Kind);
    }

    [Test]
    public static void PrecedenceMultiplicationFirst()
    {
        var stmt = Assert2.IsType<VarDeclStmt>(Parse("let x = 1 + 2 * 3").unit.Statements[0]);
        var bin = Assert2.IsType<BinaryExpr>(stmt.Value!);
        Assert.Equal(TokenKind.Plus, bin.Op);
        var right = Assert2.IsType<BinaryExpr>(bin.Right);
        Assert.Equal(TokenKind.Star, right.Op);
        var lit = Assert2.IsType<LiteralExpr>(bin.Left);
        Assert.Equal(1L, lit.Value);
    }

    [Test]
    public static void PrecedenceComparisonAndLogic()
    {
        // a == b && c  →  (a == b) && c
        var bin = Assert2.IsType<BinaryExpr>(ExprOf("a == b && c"));
        Assert.Equal(TokenKind.AmpAmp, bin.Op);
        var left = Assert2.IsType<BinaryExpr>(bin.Left);
        Assert.Equal(TokenKind.EqEq, left.Op);
    }

    [Test]
    public static void AssignmentIsRightAssociative()
    {
        var a = Assert2.IsType<AssignExpr>(ExprOf("a = b = c"));
        Assert.Equal(TokenKind.Assign, a.Op);
        var inner = Assert2.IsType<AssignExpr>(a.Value);
        Assert.Equal("c", ((NameExpr)inner.Value).Name);
    }

    [Test]
    public static void CompoundAssignment()
    {
        var a = Assert2.IsType<AssignExpr>(ExprOf("x += 1"));
        Assert.Equal(TokenKind.PlusEq, a.Op);
    }

    [Test]
    public static void CallAndMemberAccess()
    {
        var call = Assert2.IsType<CallExpr>(ExprOf("obj.method(1, 2)"));
        Assert.Equal(2, call.Args.Count);
        var member = Assert2.IsType<MemberExpr>(call.Callee);
        Assert.Equal("method", member.Name);
        Assert.False(member.NullSafe);
        Assert.Equal("obj", ((NameExpr)member.Target).Name);
    }

    [Test]
    public static void NullSafeMember()
    {
        var member = Assert2.IsType<MemberExpr>(ExprOf("a?.b"));
        Assert.True(member.NullSafe);
    }

    [Test]
    public static void IndexExpression()
    {
        var idx = Assert2.IsType<IndexExpr>(ExprOf("list[0]"));
        Assert.Equal("list", ((NameExpr)idx.Target).Name);
    }

    [Test]
    public static void ListAndMapLiterals()
    {
        var list = Assert2.IsType<ListExpr>(ExprOf("[1, 2, 3]"));
        Assert.Equal(3, list.Elements.Count);

        var map = Assert2.IsType<MapExpr>(ExprOf("{ a: 1, \"b\": 2 }"));
        Assert.Equal(2, map.Entries.Count);
        // 裸标识符键应转成字符串字面量
        var key = Assert2.IsType<LiteralExpr>(map.Entries[0].Key);
        Assert.Equal(LiteralKind.Str, key.Kind);
        var s = (InterpolatedString)key.Value!;
        Assert.Equal("a", s.PlainText);
    }

    [Test]
    public static void ArrowLambda()
    {
        var lambda = Assert2.IsType<LambdaExpr>(ExprOf("|x, y| x + y"));
        Assert.Equal(2, lambda.Params.Count);
        Assert.NotNull(lambda.BodyExpr);
        Assert.True(lambda.IsArrow);
    }

    [Test]
    public static void EmptyParamLambda()
    {
        var lambda = Assert2.IsType<LambdaExpr>(ExprOf("|| 42"));
        Assert.Equal(0, lambda.Params.Count);
        Assert.NotNull(lambda.BodyExpr);
    }

    [Test]
    public static void AnonymousFnLambda()
    {
        var lambda = Assert2.IsType<LambdaExpr>(ExprOf("fn (a: Int): Int { return a }"));
        Assert.False(lambda.IsArrow);
        Assert.NotNull(lambda.BodyBlock);
        Assert.NotNull(lambda.ReturnType);
        Assert.Equal(1, lambda.Params.Count);
    }

    [Test]
    public static void TernaryExpression()
    {
        var t = Assert2.IsType<TernaryExpr>(ExprOf("a ? b : c"));
        Assert.Equal("a", ((NameExpr)t.Cond).Name);
        Assert.Equal("c", ((NameExpr)t.Else).Name);
    }

    [Test]
    public static void StringInterpolationLiteral()
    {
        var lit = Assert2.IsType<LiteralExpr>(ExprOf("\"a ${x} b\""));
        Assert.Equal(LiteralKind.Str, lit.Kind);
        var s = (InterpolatedString)lit.Value!;
        Assert.Equal(3, s.Parts.Count);
        Assert.True(s.Parts[1].IsExpression);
    }

    [Test]
    public static void MatchExpressionWithArms()
    {
        var m = Assert2.IsType<MatchExpr>(ExprOf("match x { 1 => \"一\", is Str => \"字符串\", _ => \"其它\" }"));
        Assert.Equal(3, m.Arms.Count);
        Assert2.IsType<LiteralPattern>(m.Arms[0].Pattern);
        Assert2.IsType<TypePattern>(m.Arms[1].Pattern);
        Assert2.IsType<WildcardPattern>(m.Arms[2].Pattern);
    }

    [Test]
    public static void MatchArmWithGuard()
    {
        var m = Assert2.IsType<MatchExpr>(ExprOf("match x { n when n > 0 => 1, _ => 0 }"));
        Assert.NotNull(m.Arms[0].Guard);
        Assert2.IsType<BindingPattern>(m.Arms[0].Pattern);
    }

    [Test]
    public static void EnumPatternWithArgs()
    {
        var m = Assert2.IsType<MatchExpr>(ExprOf("match t { Tree.Leaf(v) => v, _ => 0 }"));
        var ep = Assert2.IsType<EnumPattern>(m.Arms[0].Pattern);
        Assert.Equal("Tree", ep.Qualifier);
        Assert.Equal("Leaf", ep.Name);
        Assert.True(ep.HasArgs);
        Assert.Equal(1, ep.Args.Count);
    }

    [Test]
    public static void ListPattern()
    {
        var m = Assert2.IsType<MatchExpr>(ExprOf("match v { [a, b] => 1, _ => 0 }"));
        var lp = Assert2.IsType<ListPattern>(m.Arms[0].Pattern);
        Assert.Equal(2, lp.Items.Count);
        Assert2.IsType<BindingPattern>(lp.Items[0]);
    }

    [Test]
    public static void ClassDeclaration()
    {
        var (unit, bag) = Parse("""
            class Animal {
                pub var name: Str
                var age: Int = 0
                init(name: Str) { this.name = name }
                pub fn speak(): Str { return this.name }
            }
            """);
        Assert.False(bag.HasErrors, string.Join("; ", bag.Select(d => d.ToString())));
        var c = Assert2.IsType<ClassDecl>(unit.Statements[0]);
        Assert.Equal("Animal", c.Name);
        Assert.Equal(4, c.Members.Count);
        var f = Assert2.IsType<FieldDecl>(c.Members[0]);
        Assert.True(f.Mods.IsPub);
        Assert2.IsType<FieldDecl>(c.Members[1]);
        var ctor = Assert2.IsType<MethodDecl>(c.Members[2]);
        Assert.True(ctor.IsConstructor);
        var m = Assert2.IsType<MethodDecl>(c.Members[3]);
        Assert.Equal("speak", m.Name);
        Assert.True(m.Mods.IsPub);
        Assert.NotNull(m.ReturnType);
    }

    [Test]
    public static void ClassWithInheritanceAndInterfaces()
    {
        var (unit, bag) = Parse("class Dog : Animal, Shape, Named { fn speak(): Str { return \"woof\" } }");
        Assert.False(bag.HasErrors, string.Join("; ", bag.Select(d => d.ToString())));
        var c = Assert2.IsType<ClassDecl>(unit.Statements[0]);
        Assert.NotNull(c.Base);
        Assert.Equal("Animal", ((NamedTypeSyntax)c.Base!).Name);
        Assert.Equal(2, c.Interfaces.Count);
        Assert.Equal("Shape", ((NamedTypeSyntax)c.Interfaces[0]).Name);
    }

    [Test]
    public static void InterfaceDeclaration()
    {
        var (unit, bag) = Parse("interface Shape { fn area(): Float }");
        Assert.False(bag.HasErrors, string.Join("; ", bag.Select(d => d.ToString())));
        var i = Assert2.IsType<InterfaceDecl>(unit.Statements[0]);
        Assert.Equal("Shape", i.Name);
        var m = Assert2.IsType<MethodDecl>(i.Members[0]);
        Assert.Null(m.Body);
    }

    [Test]
    public static void EnumDeclaration()
    {
        var (unit, bag) = Parse("enum Tree { Leaf(Int), Node(Tree, Tree), Empty }");
        Assert.False(bag.HasErrors, string.Join("; ", bag.Select(d => d.ToString())));
        var e = Assert2.IsType<EnumDecl>(unit.Statements[0]);
        Assert.Equal(3, e.Cases.Count);
        Assert.Equal(1, e.Cases[0].Payload.Count);
        Assert.Equal(2, e.Cases[1].Payload.Count);
        Assert.Equal(0, e.Cases[2].Payload.Count);
    }

    [Test]
    public static void GenericFunctionAndClass()
    {
        var (unit, bag) = Parse("fn id<T>(x: T): T { return x }\nclass Box<T> { var value: T }");
        Assert.False(bag.HasErrors, string.Join("; ", bag.Select(d => d.ToString())));
        var fn = Assert2.IsType<FnDecl>(unit.Statements[0]);
        Assert.Equal("id", fn.Name);
        var cls = Assert2.IsType<ClassDecl>(unit.Statements[1]);
        Assert.Equal("Box", cls.Name);
    }

    [Test]
    public static void GenericTypeAnnotation()
    {
        var (unit, bag) = Parse("let xs: List<Int> = [1, 2]");
        Assert.False(bag.HasErrors, string.Join("; ", bag.Select(d => d.ToString())));
        var d = Assert2.IsType<VarDeclStmt>(unit.Statements[0]);
        var nt = Assert2.IsType<NamedTypeSyntax>(d.Type!);
        Assert.Equal("List", nt.Name);
        Assert.Equal(1, nt.TypeArgs.Count);
        Assert.Equal("Int", ((NamedTypeSyntax)nt.TypeArgs[0]).Name);
    }

    [Test]
    public static void NestedGenericTypes()
    {
        var (unit, bag) = Parse("let m: Map<Str, List<Int>> = {}");
        Assert.False(bag.HasErrors, string.Join("; ", bag.Select(d => d.ToString())));
        var d = Assert2.IsType<VarDeclStmt>(unit.Statements[0]);
        var nt = Assert2.IsType<NamedTypeSyntax>(d.Type!);
        Assert.Equal(2, nt.TypeArgs.Count);
        Assert.Equal("List", ((NamedTypeSyntax)nt.TypeArgs[1]).Name);
    }

    [Test]
    public static void ControlFlow()
    {
        var (unit, bag) = Parse("""
            if a > 1 { b() } else if a > 0 { c() } else { d() }
            while x < 10 { x += 1 }
            for i in list { print(i) }
            loop { break }
            """);
        Assert.False(bag.HasErrors, string.Join("; ", bag.Select(d => d.ToString())));
        Assert.Equal(4, unit.Statements.Count);
        var ifStmt = Assert2.IsType<IfStmt>(unit.Statements[0]);
        Assert.NotNull(ifStmt.Else);
        Assert2.IsType<IfStmt>(ifStmt.Else);
        Assert2.IsType<WhileStmt>(unit.Statements[1]);
        Assert2.IsType<ForStmt>(unit.Statements[2]);
        Assert2.IsType<LoopStmt>(unit.Statements[3]);
    }

    [Test]
    public static void TryCatchFinally()
    {
        var (unit, bag) = Parse("try { f() } catch e { g(e) } finally { h() }");
        Assert.False(bag.HasErrors, string.Join("; ", bag.Select(d => d.ToString())));
        var t = Assert2.IsType<TryStmt>(unit.Statements[0]);
        Assert.Equal("e", t.CatchName);
        Assert.NotNull(t.FinallyBody);
    }

    [Test]
    public static void ImportStatements()
    {
        var (unit, bag) = Parse("import \"std.math\"\nimport \"./util.ikd\" as util");
        Assert.False(bag.HasErrors, string.Join("; ", bag.Select(d => d.ToString())));
        var i1 = Assert2.IsType<ImportStmt>(unit.Statements[0]);
        Assert.Equal("std.math", i1.Path);
        Assert.Null(i1.Alias);
        var i2 = Assert2.IsType<ImportStmt>(unit.Statements[1]);
        Assert.Equal("util", i2.Alias);
    }

    [Test]
    public static void NewExpression()
    {
        var e = ExprOf("new Dog(\"旺财\")");
        var n = Assert2.IsType<NewExpr>(e);
        Assert.Equal("Dog", ((NamedTypeSyntax)n.Type).Name);
        Assert.Equal(1, n.Args.Count);
    }

    [Test]
    public static void ClassCallIsJustCallExpr()
    {
        var e = ExprOf("Dog(\"小明\")");
        Assert2.IsType<CallExpr>(e);
    }

    // ---- 换行规则 ----

    [Test]
    public static void CallRequiresSameLine()
    {
        var (unit, bag) = Parse("foo\n(bar)");
        Assert.False(bag.HasErrors, string.Join("; ", bag.Select(d => d.ToString())));
        Assert.Equal(2, unit.Statements.Count, "换行后的 '(' 不应成为调用");
        Assert2.IsType<NameExpr>(Assert2.IsType<ExprStmt>(unit.Statements[0]).Expression);
        Assert2.IsType<ExprStmt>(unit.Statements[1]);
    }

    [Test]
    public static void CallOnSameLineWorks()
    {
        var (unit, _) = Parse("foo(bar)");
        Assert.Equal(1, unit.Statements.Count);
        Assert2.IsType<CallExpr>(Assert2.IsType<ExprStmt>(unit.Statements[0]).Expression);
    }

    [Test]
    public static void MethodChainingAcrossLines()
    {
        var e = ExprOf("list\n  .map(|x| x * 2)\n  .sum()");
        // ((list.map(f)).sum)()
        var sumCall = Assert2.IsType<CallExpr>(e);
        var sumMember = Assert2.IsType<MemberExpr>(sumCall.Callee);
        Assert.Equal("sum", sumMember.Name);
        var mapCall = Assert2.IsType<CallExpr>(sumMember.Target);
        var mapMember = Assert2.IsType<MemberExpr>(mapCall.Callee);
        Assert.Equal("map", mapMember.Name);
        Assert.Equal("list", ((NameExpr)mapMember.Target).Name);
    }

    [Test]
    public static void OperatorAtLineStartEndsStatement()
    {
        var (unit, bag) = Parse("a\n+b");
        Assert.True(bag.HasErrors, "行首运算符应报错");
        // 'a'、报错的 '+'（被跳过）、'b' —— 共三条语句
        Assert.Equal(3, unit.Statements.Count);
    }

    [Test]
    public static void TrailingOperatorContinues()
    {
        var e = ExprOf("a +\n  b");
        var bin = Assert2.IsType<BinaryExpr>(e);
        Assert.Equal(TokenKind.Plus, bin.Op);
    }

    [Test]
    public static void ReturnWithoutValueOnNewLine()
    {
        var (unit, bag) = Parse("fn f(): Int { return\n}");
        Assert.False(bag.HasErrors, string.Join("; ", bag.Select(d => d.ToString())));
        var fn = Assert2.IsType<FnDecl>(unit.Statements[0]);
        var ret = Assert2.IsType<ReturnStmt>(fn.Body.Statements[0]);
        Assert.Null(ret.Value);
    }

    [Test]
    public static void ReturnWithValueOnSameLine()
    {
        var (unit, _) = Parse("fn f(): Int { return 42 }");
        var fn = Assert2.IsType<FnDecl>(unit.Statements[0]);
        var ret = Assert2.IsType<ReturnStmt>(fn.Body.Statements[0]);
        Assert.NotNull(ret.Value);
    }

    [Test]
    public static void MultiLineExpressionInsideParens()
    {
        var e = ExprOf("foo(\n  1,\n  2\n)");
        var call = Assert2.IsType<CallExpr>(e);
        Assert.Equal(2, call.Args.Count);
    }

    // ---- 错误恢复 ----

    [Test]
    public static void MissingBraceReports()
    {
        var (_, bag) = Parse("fn f() { let x = 1");
        Assert.True(bag.HasErrors);
        Assert.Contains("}", bag.First().Message);
    }

    [Test]
    public static void UnexpectedTokenReports()
    {
        var (_, bag) = Parse("class { }");
        Assert.True(bag.HasErrors);
        Assert.Equal(ErrorCode.ExpectedIdentifier, bag.First().Code);
    }

    [Test]
    public static void RecoversAfterError()
    {
        var (unit, bag) = Parse("fn ( { }\nlet a = 1");
        Assert.True(bag.HasErrors);
        // 错误后仍能解析出后续语句
        Assert.True(unit.Statements.Count >= 1, "错误恢复失败");
        bool foundLet = unit.Statements.OfType<VarDeclStmt>().Any(v => v.Name == "a");
        Assert.True(foundLet, "错误之后的 let 应被解析");
    }

    [Test]
    public static void DiagnosticsHaveLineAndColumn()
    {
        var src = SourceText.From("let a = 1\nlet b = @", "t.ikd");
        var bag = new DiagnosticBag();
        Parser.Parse(src, bag);
        Assert.True(bag.HasErrors);
        var d = bag.First();
        Assert.Equal(2, d.Line);
        Assert.Contains("t.ikd(2,", d.ToString());
    }

    [Test]
    public static void SemicolonsAreOptional()
    {
        var (unit, bag) = Parse("let a = 1; let b = 2;");
        Assert.False(bag.HasErrors, string.Join("; ", bag.Select(d => d.ToString())));
        Assert.Equal(2, unit.Statements.Count);
    }

    [Test]
    public static void StatementOnOneLineWithoutSemicolon()
    {
        var (unit, bag) = Parse("let a = 1 let b = 2");
        Assert.False(bag.HasErrors, string.Join("; ", bag.Select(d => d.ToString())));
        Assert.Equal(2, unit.Statements.Count);
    }
}

/// <summary>类型断言辅助。</summary>
public static class Assert2
{
    public static T IsType<T>(object? value) where T : class
    {
        if (value is T t) return t;
        throw new AssertFailedException(
            $"类型不符，期望 {typeof(T).Name}，实际 {(value is null ? "null" : value.GetType().Name)}");
    }
}
