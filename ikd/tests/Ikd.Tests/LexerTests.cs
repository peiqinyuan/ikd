using Ikd.Compiler.Diagnostics;
using Ikd.Compiler.Syntax;

namespace Ikd.Tests;

public static class LexerTests
{
    private static (List<Token> tokens, DiagnosticBag bag) Lex(string text)
    {
        var src = SourceText.From(text, "test.ikd");
        var bag = new DiagnosticBag();
        bag.SetSource(src);
        var tokens = new Lexer(src, bag).LexAll();
        return (tokens, bag);
    }

    private static TokenKind[] Kinds(string text)
        => Lex(text).tokens.Select(t => t.Kind).ToArray();

    [Test]
    public static void EmptySourceProducesEof()
    {
        var (tokens, bag) = Lex("");
        Assert.Equal(1, tokens.Count);
        Assert.Equal(TokenKind.EndOfFile, tokens[0].Kind);
        Assert.False(bag.HasErrors);
    }

    [Test]
    public static void OperatorsAndPunctuation()
    {
        Assert.Equal(TokenKind.Plus, Kinds("+")[0]);
        Assert.Equal(TokenKind.Percent, Kinds("%")[0]);
        Assert.Equal(TokenKind.FatArrow, Kinds("=>")[0]);
        Assert.Equal(TokenKind.EqEq, Kinds("==")[0]);
        Assert.Equal(TokenKind.BangEq, Kinds("!=")[0]);
        Assert.Equal(TokenKind.LtEq, Kinds("<=")[0]);
        Assert.Equal(TokenKind.GtEq, Kinds(">=")[0]);
        Assert.Equal(TokenKind.AmpAmp, Kinds("&&")[0]);
        Assert.Equal(TokenKind.PipePipe, Kinds("||")[0]);
        Assert.Equal(TokenKind.Pipe, Kinds("|")[0]);
        Assert.Equal(TokenKind.PlusEq, Kinds("+=")[0]);
        Assert.Equal(TokenKind.MinusEq, Kinds("-=")[0]);
        Assert.Equal(TokenKind.StarEq, Kinds("*=")[0]);
        Assert.Equal(TokenKind.SlashEq, Kinds("/=")[0]);
        Assert.Equal(TokenKind.PercentEq, Kinds("%=")[0]);
        Assert.Equal(TokenKind.QuestionDot, Kinds("?.")[0]);
        Assert.Equal(TokenKind.QuestionQuestion, Kinds("??")[0]);
        Assert.Equal(TokenKind.Assign, Kinds("=")[0]);
        Assert.Equal(TokenKind.Bang, Kinds("!")[0]);
        var many = Kinds("+-*/%( ){ }[],.:;?<>");
        Assert.Equal(TokenKind.LBrace, many[7]);
        Assert.Equal(TokenKind.RBracket, many[10]);
        Assert.Equal(TokenKind.Semicolon, many[14]);
    }

    [Test]
    public static void KeywordsAreRecognized()
    {
        var kinds = Kinds("class interface enum fn let var const if else while for in loop break continue return match when throw try catch finally this super new import as pub static override abstract true false null and or not is");
        Assert.Equal(TokenKind.Class, kinds[0]);
        Assert.Equal(TokenKind.Is, kinds[^2]);
        Assert.Equal(TokenKind.EndOfFile, kinds[^1]);
        Assert.False(bagHasErrorsOf("class"));
    }

    private static bool bagHasErrorsOf(string s) => Lex(s).bag.HasErrors;

    [Test]
    public static void IdentifiersIncludingUnicode()
    {
        var (tokens, _) = Lex("hello _x 变量名 x1");
        Assert.Equal(TokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("hello", tokens[0].Value);
        Assert.Equal("变量名", tokens[2].Value);
        Assert.Equal(TokenKind.EndOfFile, tokens[4].Kind);
    }

    [Test]
    public static void IntegerLiterals()
    {
        var (tokens, bag) = Lex("42 0xFF 0b1011 1_000_000 18446744073709551615");
        Assert.False(bag.HasErrors);
        Assert.Equal(42L, (long)tokens[0].Value!);
        Assert.Equal(255L, (long)tokens[1].Value!);
        Assert.Equal(11L, (long)tokens[2].Value!);
        Assert.Equal(1000000L, (long)tokens[3].Value!);
        Assert.Equal(-1L, (long)tokens[4].Value!); // 0xFFFF... 按位解释
    }

    [Test]
    public static void FloatLiterals()
    {
        var (tokens, bag) = Lex("3.14 1e3 2.5e-2");
        Assert.False(bag.HasErrors);
        Assert.Equal(3.14, (double)tokens[0].Value!);
        Assert.Equal(1000.0, (double)tokens[1].Value!);
        Assert.Equal(0.025, (double)tokens[2].Value!);
        Assert.Equal(TokenKind.FloatLiteral, tokens[2].Kind);
    }

    [Test]
    public static void MemberAccessAfterInteger()
    {
        var kinds = Kinds("1.5.abs");
        Assert.Equal(TokenKind.FloatLiteral, kinds[0]);
        Assert.Equal(TokenKind.Dot, kinds[1]);
        Assert.Equal(TokenKind.Identifier, kinds[2]);
    }

    [Test]
    public static void StringLiteralPlain()
    {
        var (tokens, bag) = Lex("\"hello\"");
        Assert.False(bag.HasErrors);
        Assert.Equal(TokenKind.StringLiteral, tokens[0].Kind);
        var s = (InterpolatedString)tokens[0].Value!;
        Assert.True(s.IsPlain);
        Assert.Equal("hello", s.PlainText);
    }

    [Test]
    public static void StringEscapes()
    {
        var (tokens, bag) = Lex("\"a\\nb\\t\\\\ \\\" \\u{4F60}\"");
        Assert.False(bag.HasErrors, "不应有错误: " + string.Join(";", bag.Select(d => d.Message)));
        var s = (InterpolatedString)tokens[0].Value!;
        Assert.Equal("a\nb\t\\ \" 你", s.PlainText);
    }

    [Test]
    public static void StringInterpolationParts()
    {
        var (tokens, bag) = Lex("\"hi ${name}, ${a + 1}!\"");
        Assert.False(bag.HasErrors);
        var s = (InterpolatedString)tokens[0].Value!;
        Assert.Equal(5, s.Parts.Count);
        Assert.Equal("hi ", s.Parts[0].Text);
        Assert.True(s.Parts[1].IsExpression);
        Assert.Equal(", ", s.Parts[2].Text);
        Assert.True(s.Parts[3].IsExpression);
        Assert.Equal("!", s.Parts[4].Text);
    }

    [Test]
    public static void NestedInterpolationInExpression()
    {
        var (tokens, bag) = Lex("\"x ${f(\"in\")} y\"");
        Assert.False(bag.HasErrors, "错误: " + string.Join(";", bag.Select(d => d.Message)));
        var s = (InterpolatedString)tokens[0].Value!;
        Assert.Equal(3, s.Parts.Count);
        Assert.Equal("x ", s.Parts[0].Text);
        Assert.True(s.Parts[1].IsExpression);
        // 表达式范围应包含 f("in")
        var src = SourceText.From("\"x ${f(\"in\")} y\"", "t");
        var expr = src.Text.Substring(s.Parts[1].ExprStart, s.Parts[1].ExprEnd - s.Parts[1].ExprStart);
        Assert.Equal("f(\"in\")", expr);
    }

    [Test]
    public static void UnterminatedStringReports()
    {
        var (_, bag) = Lex("\"abc");
        Assert.True(bag.HasErrors);
        Assert.Equal(ErrorCode.UnterminatedString, bag.First().Code);
    }

    [Test]
    public static void CommentsAreSkipped()
    {
        var kinds = Kinds("a // comment\n b /* block\n comment */ c");
        Assert.Equal(TokenKind.Identifier, kinds[0]);
        Assert.Equal(TokenKind.Identifier, kinds[1]);
        Assert.Equal(TokenKind.Identifier, kinds[2]);
        Assert.Equal(TokenKind.EndOfFile, kinds[3]);
    }

    [Test]
    public static void NestedBlockComments()
    {
        var kinds = Kinds("a /* one /* two */ still */ b");
        Assert.Equal(3, kinds.Length);
    }

    [Test]
    public static void UnterminatedBlockCommentReports()
    {
        var (_, bag) = Lex("a /* oops");
        Assert.True(bag.HasErrors);
        Assert.Equal(ErrorCode.UnterminatedComment, bag.First().Code);
    }

    [Test]
    public static void UnexpectedCharacterReports()
    {
        var (_, bag) = Lex("let x = #");
        Assert.True(bag.HasErrors);
        Assert.Equal(ErrorCode.UnexpectedCharacter, bag.First().Code);
        Assert.Contains("意外的字符", bag.First().Message);
    }

    [Test]
    public static void TokenPositionsAreAccurate()
    {
        var src = SourceText.From("let x = 42", "t");
        var bag = new DiagnosticBag();
        bag.SetSource(src);
        var tokens = new Lexer(src, bag).LexAll();
        Assert.Equal(1, src.GetLine(tokens[0].Start));
        Assert.Equal(1, src.GetColumn(tokens[0].Start));
        Assert.Equal(5, src.GetColumn(tokens[1].Start));
        Assert.Equal(9, src.GetColumn(tokens[3].Start));
    }

    [Test]
    public static void MultipleLinesHaveCorrectLineNumbers()
    {
        var src = SourceText.From("a\nbb\nccc", "t");
        Assert.Equal(1, src.GetLine(0));
        Assert.Equal(2, src.GetLine(2));
        Assert.Equal(3, src.GetLine(5));
        Assert.Equal("bb", src.GetLineText(2));
        Assert.Equal("ccc", src.GetLineText(3));
    }
}
