using System.Globalization;
using System.Text;
using Ikd.Compiler.Diagnostics;

namespace Ikd.Compiler.Syntax;

public sealed class Lexer
{
    private static readonly Dictionary<string, TokenKind> Keywords = new(StringComparer.Ordinal)
    {
        ["class"] = TokenKind.Class,
        ["interface"] = TokenKind.Interface,
        ["enum"] = TokenKind.Enum,
        ["fn"] = TokenKind.Fn,
        ["let"] = TokenKind.Let,
        ["var"] = TokenKind.Var,
        ["const"] = TokenKind.Const,
        ["if"] = TokenKind.If,
        ["else"] = TokenKind.Else,
        ["while"] = TokenKind.While,
        ["for"] = TokenKind.For,
        ["in"] = TokenKind.In,
        ["loop"] = TokenKind.Loop,
        ["break"] = TokenKind.Break,
        ["continue"] = TokenKind.Continue,
        ["return"] = TokenKind.Return,
        ["match"] = TokenKind.Match,
        ["when"] = TokenKind.When,
        ["macro"] = TokenKind.Macro,
        ["throw"] = TokenKind.Throw,
        ["try"] = TokenKind.Try,
        ["catch"] = TokenKind.Catch,
        ["finally"] = TokenKind.Finally,
        ["this"] = TokenKind.This,
        ["super"] = TokenKind.Super,
        ["new"] = TokenKind.New,
        ["import"] = TokenKind.Import,
        ["as"] = TokenKind.As,
        ["pub"] = TokenKind.Pub,
        ["static"] = TokenKind.Static,
        ["override"] = TokenKind.Override,
        ["abstract"] = TokenKind.Abstract,
        ["true"] = TokenKind.True,
        ["false"] = TokenKind.False,
        ["null"] = TokenKind.Null,
        ["and"] = TokenKind.And,
        ["or"] = TokenKind.Or,
        ["not"] = TokenKind.Not,
        ["is"] = TokenKind.Is,
    };

    private readonly SourceText _source;
    private readonly string _text;
    private readonly DiagnosticBag _bag;
    private readonly int _end;
    private int _pos;

    public Lexer(SourceText source, DiagnosticBag bag, int start = 0, int? end = null)
    {
        _source = source;
        _text = source.Text;
        _bag = bag;
        _pos = Math.Max(0, start);
        _end = Math.Min(end ?? _text.Length, _text.Length);
        if (_pos > _end) _pos = _end;
    }

    public static List<Token> LexRange(SourceText source, DiagnosticBag bag, int start, int end)
        => new Lexer(source, bag, start, end).LexAll();

    public List<Token> LexAll()
    {
        var tokens = new List<Token>();
        while (true)
        {
            var t = NextToken();
            tokens.Add(t);
            if (t.Kind == TokenKind.EndOfFile) break;
        }
        return tokens;
    }

    private char Current => _pos < _end ? _text[_pos] : '\0';
    private char Peek(int n = 1) => _pos + n < _end ? _text[_pos + n] : '\0';
    private bool AtEnd => _pos >= _end;
    private char Advance() => _text[_pos++];
    private TextSpan SpanFrom(int start) => new(start, _pos - start);

    private Token NextToken()
    {
        SkipTrivia();
        if (AtEnd) return Token.Eof(_pos);

        int start = _pos;
        char c = Current;

        if (char.IsLetter(c) || c == '_') return ReadIdentifier(start);
        if (char.IsDigit(c)) return ReadNumber(start);
        if (c == '"') return ReadString(start);

        switch (c)
        {
            case '(': Advance(); return Make(TokenKind.LParen, start);
            case ')': Advance(); return Make(TokenKind.RParen, start);
            case '{': Advance(); return Make(TokenKind.LBrace, start);
            case '}': Advance(); return Make(TokenKind.RBrace, start);
            case '[': Advance(); return Make(TokenKind.LBracket, start);
            case ']': Advance(); return Make(TokenKind.RBracket, start);
            case ',': Advance(); return Make(TokenKind.Comma, start);
            case '.': Advance(); return Make(TokenKind.Dot, start);
            case ':': Advance(); return Make(TokenKind.Colon, start);
            case ';': Advance(); return Make(TokenKind.Semicolon, start);
            case '=':
                Advance();
                if (Current == '=') { Advance(); return Make(TokenKind.EqEq, start); }
                if (Current == '>') { Advance(); return Make(TokenKind.FatArrow, start); }
                return Make(TokenKind.Assign, start);
            case '+':
                Advance();
                if (Current == '=') { Advance(); return Make(TokenKind.PlusEq, start); }
                return Make(TokenKind.Plus, start);
            case '-':
                Advance();
                if (Current == '=') { Advance(); return Make(TokenKind.MinusEq, start); }
                return Make(TokenKind.Minus, start);
            case '*':
                Advance();
                if (Current == '=') { Advance(); return Make(TokenKind.StarEq, start); }
                return Make(TokenKind.Star, start);
            case '/':
                Advance();
                if (Current == '=') { Advance(); return Make(TokenKind.SlashEq, start); }
                return Make(TokenKind.Slash, start);
            case '%':
                Advance();
                if (Current == '=') { Advance(); return Make(TokenKind.PercentEq, start); }
                return Make(TokenKind.Percent, start);
            case '!':
                Advance();
                if (Current == '=') { Advance(); return Make(TokenKind.BangEq, start); }
                return Make(TokenKind.Bang, start);
            case '<':
                Advance();
                if (Current == '=') { Advance(); return Make(TokenKind.LtEq, start); }
                return Make(TokenKind.Lt, start);
            case '>':
                Advance();
                if (Current == '=') { Advance(); return Make(TokenKind.GtEq, start); }
                return Make(TokenKind.Gt, start);
            case '&':
                if (Peek() == '&') { Advance(); Advance(); return Make(TokenKind.AmpAmp, start); }
                return BadChar(start, '&');
            case '|':
                if (Peek() == '|') { Advance(); Advance(); return Make(TokenKind.PipePipe, start); }
                Advance();
                return Make(TokenKind.Pipe, start);
            case '?':
                Advance();
                if (Current == '?') { Advance(); return Make(TokenKind.QuestionQuestion, start); }
                if (Current == '.') { Advance(); return Make(TokenKind.QuestionDot, start); }
                return Make(TokenKind.Question, start);
            default:
                return BadChar(start, c);
        }
    }

    private Token Make(TokenKind kind, int start) => new(kind, start, _pos - start);

    private Token BadChar(int start, char c)
    {
        Advance();
        _bag.Report(ErrorCode.UnexpectedCharacter, SpanFrom(start),
            $"意外的字符 '{c}'",
            "此处不应出现该字符");
        return NextToken(); // 跳过并继续
    }

    private void SkipTrivia()
    {
        while (!AtEnd)
        {
            char c = Current;
            if (char.IsWhiteSpace(c)) { Advance(); continue; }

            if (c == '/' && Peek() == '/')
            {
                Advance(); Advance();
                while (!AtEnd && Current != '\n' && Current != '\r') Advance();
                continue;
            }

            if (c == '/' && Peek() == '*')
            {
                int start = _pos;
                Advance(); Advance();
                int depth = 1;
                while (!AtEnd && depth > 0)
                {
                    if (Current == '/' && Peek() == '*') { Advance(); Advance(); depth++; }
                    else if (Current == '*' && Peek() == '/') { Advance(); Advance(); depth--; }
                    else Advance();
                }
                if (depth > 0)
                {
                    _bag.Report(ErrorCode.UnterminatedComment, new TextSpan(start, _pos - start),
                        "块注释没有闭合", "需要 '*/' 来结束注释");
                }
                continue;
            }

            break;
        }
    }

    private Token ReadIdentifier(int start)
    {
        while (!AtEnd && (char.IsLetterOrDigit(Current) || Current == '_')) Advance();
        string text = _text.Substring(start, _pos - start);
        if (Keywords.TryGetValue(text, out var kind))
            return new Token(kind, start, text.Length, text);
        return new Token(TokenKind.Identifier, start, text.Length, text);
    }

    private Token ReadNumber(int start)
    {
        bool isFloat = false;

        if (Current == '0' && (Peek() == 'x' || Peek() == 'X'))
        {
            Advance(); Advance();
            int digitStart = _pos;
            while (!AtEnd && (Uri.IsHexDigit(Current) || Current == '_')) Advance();
            if (_pos == digitStart)
            {
                _bag.Report(ErrorCode.InvalidNumberLiteral, SpanFrom(start), "十六进制字面量缺少数字", "例如 0xFF");
                return new Token(TokenKind.IntLiteral, start, _pos - start, 0L);
            }
            return FinishInt(start);
        }

        if (Current == '0' && (Peek() == 'b' || Peek() == 'B'))
        {
            Advance(); Advance();
            int digitStart = _pos;
            while (!AtEnd && (Current == '0' || Current == '1' || Current == '_')) Advance();
            if (_pos == digitStart)
            {
                _bag.Report(ErrorCode.InvalidNumberLiteral, SpanFrom(start), "二进制字面量缺少数字", "例如 0b1010");
                return new Token(TokenKind.IntLiteral, start, _pos - start, 0L);
            }
            return FinishInt(start);
        }

        while (!AtEnd && (char.IsDigit(Current) || Current == '_')) Advance();

        if (Current == '.' && char.IsDigit(Peek()))
        {
            isFloat = true;
            Advance();
            while (!AtEnd && (char.IsDigit(Current) || Current == '_')) Advance();
        }

        if (Current == 'e' || Current == 'E')
        {
            int save = _pos;
            Advance();
            if (Current == '+' || Current == '-') Advance();
            if (char.IsDigit(Current))
            {
                isFloat = true;
                while (!AtEnd && (char.IsDigit(Current) || Current == '_')) Advance();
            }
            else
            {
                _pos = save; // 指数部分无效，回退，当作普通数字
            }
        }

        if (!AtEnd && (char.IsLetter(Current) || Current == '_'))
        {
            while (!AtEnd && (char.IsLetterOrDigit(Current) || Current == '_')) Advance();
            _bag.Report(ErrorCode.InvalidNumberLiteral, SpanFrom(start),
                $"非法的数字字面量 '{_text.Substring(start, _pos - start)}'");
            return new Token(TokenKind.IntLiteral, start, _pos - start, 0L);
        }

        string raw = _text.Substring(start, _pos - start).Replace("_", "");

        if (isFloat)
        {
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return new Token(TokenKind.FloatLiteral, start, _pos - start, d);
            _bag.Report(ErrorCode.InvalidNumberLiteral, SpanFrom(start),
                $"无法解析的小数字面量 '{raw}'");
            return new Token(TokenKind.FloatLiteral, start, _pos - start, 0.0);
        }

        return FinishInt(start);
    }

    private Token FinishInt(int start)
    {
        string raw = _text.Substring(start, _pos - start).Replace("_", "");

        try
        {
            long v;
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                v = unchecked((long)ulong.Parse(raw[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
            else if (raw.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                ulong acc = 0;
                foreach (char b in raw.AsSpan(2))
                {
                    if (b == '0') acc = acc << 1;
                    else if (b == '1') acc = (acc << 1) | 1;
                    else throw new FormatException();
                    if (acc > long.MaxValue) throw new FormatException();
                }
                v = (long)acc;
            }
            else if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            {
            }
            else if (ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u))
            {
                v = unchecked((long)u);
            }
            else
            {
                throw new FormatException();
            }
            return new Token(TokenKind.IntLiteral, start, _pos - start, v);
        }
        catch (FormatException)
        {
            _bag.Report(ErrorCode.InvalidNumberLiteral, SpanFrom(start),
                $"整数字面量超出范围或格式非法: '{raw}'", "Int 为 64 位有符号整数");
            return new Token(TokenKind.IntLiteral, start, _pos - start, 0L);
        }
    }

    private Token ReadString(int start)
    {
        Advance(); // 跳过起始引号
        var parts = new List<StringPart>();
        var literal = new StringBuilder();

        while (true)
        {
            if (AtEnd)
            {
                _bag.Report(ErrorCode.UnterminatedString, SpanFrom(start),
                    "字符串没有闭合的引号", "需要 '\"' 结束字符串");
                break;
            }

            char c = Current;
            if (c == '"')
            {
                Advance();
                break;
            }

            if (c == '\\')
            {
                int escStart = _pos;
                Advance();
                literal.Append(ReadEscape(escStart));
                continue;
            }

            if (c == '$' && Peek() == '{')
            {
                if (literal.Length > 0)
                {
                    parts.Add(StringPart.Literal(literal.ToString()));
                    literal.Clear();
                }
                Advance(); Advance(); // 吃掉 ${
                int exprStart = _pos;
                int close = FindInterpolationEnd(_text, _pos, _end);
                if (close < 0)
                {
                    _bag.Report(ErrorCode.UnterminatedInterpolation, new TextSpan(start, _end - start),
                        "字符串插值 ${...} 没有闭合", "需要 '}' 结束插值表达式");
                    _pos = _end;
                    break;
                }
                parts.Add(StringPart.Expr(exprStart, close));
                _pos = close + 1; // 吃掉 }
                continue;
            }

            literal.Append(Advance());
        }

        if (literal.Length > 0)
            parts.Add(StringPart.Literal(literal.ToString()));

        var value = new InterpolatedString();
        value.Parts.AddRange(parts);
        return new Token(TokenKind.StringLiteral, start, _pos - start, value);
    }

    private char ReadEscape(int escStart)
    {
        if (AtEnd) return '\0';
        char c = Advance();
        switch (c)
        {
            case 'n': return '\n';
            case 't': return '\t';
            case 'r': return '\r';
            case '0': return '\0';
            case '\\': return '\\';
            case '"': return '"';
            case '\'': return '\'';
            case '$': return '$';
            case 'u':
            {
                if (Current == '{')
                {
                    Advance();
                    int hexStart = _pos;
                    int value = 0;
                    bool ok = true;
                    while (!AtEnd && Current != '}')
                    {
                        char h = Current;
                        if (!Uri.IsHexDigit(h)) { ok = false; break; }
                        value = value * 16 + HexVal(h);
                        if (value > 0x10FFFF) { ok = false; break; }
                        Advance();
                    }
                    if (!ok || AtEnd || Current != '}')
                    {
                        _bag.Report(ErrorCode.InvalidEscapeSequence,
                            new TextSpan(escStart, _pos - escStart),
                            "非法的 \\u{...} 转义", "例如 \\u{4F60}");
                        return 'u';
                    }
                    Advance(); // 吃掉 }
                    if (_pos - hexStart == 0)
                    {
                        _bag.Report(ErrorCode.InvalidEscapeSequence,
                            new TextSpan(escStart, _pos - escStart), "空的 \\u{} 转义");
                        return 'u';
                    }
                    return (char)value; // 基本多文种平面
                }
                _bag.Report(ErrorCode.InvalidEscapeSequence, new TextSpan(escStart, _pos - escStart),
                    "非法的 \\u 转义", "应写作 \\u{XXXX}");
                return 'u';
            }
            default:
                _bag.Report(ErrorCode.InvalidEscapeSequence, new TextSpan(escStart - 1, 2),
                    $"未知的转义序列 '\\{c}'", "可用: \\n \\t \\r \\0 \\\\ \\\" \\$ \\u{XXXX}");
                return c;
        }
    }

    private static int HexVal(char h) => h switch
    {
        >= '0' and <= '9' => h - '0',
        >= 'a' and <= 'f' => h - 'a' + 10,
        >= 'A' and <= 'F' => h - 'A' + 10,
        _ => 0,
    };

    /// <summary>从 from 开始查找插值表达式的结束 '}'，返回其索引；失败返回 -1。</summary>
    internal static int FindInterpolationEnd(string text, int from, int limit)
    {
        int depth = 0;
        int i = from;
        while (i < limit)
        {
            char c = text[i];
            if (c == '"')
            {
                int e = FindStringEnd(text, i + 1, limit);
                if (e < 0) return -1;
                i = e;
                continue;
            }
            if (c == '{') { depth++; i++; continue; }
            if (c == '}')
            {
                if (depth == 0) return i;
                depth--;
                i++;
                continue;
            }
            i++;
        }
        return -1;
    }

    /// <summary>从 from（引号之后）开始查找字符串结束，返回闭引号之后的索引；失败返回 -1。</summary>
    internal static int FindStringEnd(string text, int from, int limit)
    {
        int i = from;
        while (i < limit)
        {
            char c = text[i];
            if (c == '\\') { i += 2; continue; }
            if (c == '"') return i + 1;
            if (c == '$' && i + 1 < limit && text[i + 1] == '{')
            {
                int e = FindInterpolationEnd(text, i + 2, limit);
                if (e < 0) return -1;
                i = e + 1;
                continue;
            }
            i++;
        }
        return -1;
    }
}
