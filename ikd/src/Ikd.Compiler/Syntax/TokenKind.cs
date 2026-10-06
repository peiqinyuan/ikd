namespace Ikd.Compiler.Syntax;

public enum TokenKind
{
    EndOfFile,
    Invalid,

    // 字面量与名称
    Identifier,
    IntLiteral,
    FloatLiteral,
    StringLiteral,   // Value = InterpolatedString

    // 关键字
    Class,
    Interface,
    Enum,
    Fn,
    Let,
    Var,
    Const,
    If,
    Else,
    While,
    For,
    In,
    Loop,
    Break,
    Continue,
    Return,
    Match,
    When,
    Throw,
    Try,
    Catch,
    Finally,
    This,
    Super,
    New,
    Import,
    As,
    Pub,
    Static,
    Override,
    Abstract,
    True,
    False,
    Null,
    And,
    Or,
    Not,
    Is,
    Macro,

    // 标点
    LParen,          // (
    RParen,          // )
    LBrace,          // {
    RBrace,          // }
    LBracket,        // [
    RBracket,        // ]
    Comma,           // ,
    Dot,             // .
    Colon,           // :
    Semicolon,       // ;
    Question,        // ?
    QuestionDot,     // ?.
    QuestionQuestion,// ??
    FatArrow,        // =>
    Plus,            // +
    Minus,           // -
    Star,            // *
    Slash,           // /
    Percent,         // %
    Pipe,            // |
    PipePipe,        // ||
    AmpAmp,          // &&
    Assign,          // =
    EqEq,            // ==
    Bang,            // !
    BangEq,          // !=
    Lt,              // <
    LtEq,            // <=
    Gt,              // >
    GtEq,            // >=
    PlusEq,          // +=
    MinusEq,         // -=
    StarEq,          // *=
    SlashEq,         // /=
    PercentEq,       // %=
}

public static class TokenKindExtensions
{
    public static bool IsKeyword(this TokenKind kind)
        => kind >= TokenKind.Class && kind <= TokenKind.Macro;

    /// <summary>用于诊断消息的人类可读文本。</summary>
    public static string Display(this TokenKind kind) => kind switch
    {
        TokenKind.EndOfFile => "文件结束",
        TokenKind.Identifier => "标识符",
        TokenKind.IntLiteral => "整数",
        TokenKind.FloatLiteral => "小数",
        TokenKind.StringLiteral => "字符串",
        TokenKind.Class => "'class'",
        TokenKind.Interface => "'interface'",
        TokenKind.Enum => "'enum'",
        TokenKind.Fn => "'fn'",
        TokenKind.Let => "'let'",
        TokenKind.Var => "'var'",
        TokenKind.Const => "'const'",
        TokenKind.If => "'if'",
        TokenKind.Else => "'else'",
        TokenKind.While => "'while'",
        TokenKind.For => "'for'",
        TokenKind.In => "'in'",
        TokenKind.Loop => "'loop'",
        TokenKind.Break => "'break'",
        TokenKind.Continue => "'continue'",
        TokenKind.Return => "'return'",
        TokenKind.Match => "'match'",
        TokenKind.When => "'when'",
        TokenKind.Throw => "'throw'",
        TokenKind.Try => "'try'",
        TokenKind.Catch => "'catch'",
        TokenKind.Finally => "'finally'",
        TokenKind.This => "'this'",
        TokenKind.Super => "'super'",
        TokenKind.New => "'new'",
        TokenKind.Import => "'import'",
        TokenKind.As => "'as'",
        TokenKind.Pub => "'pub'",
        TokenKind.Static => "'static'",
        TokenKind.Override => "'override'",
        TokenKind.Abstract => "'abstract'",
        TokenKind.True => "'true'",
        TokenKind.False => "'false'",
        TokenKind.Null => "'null'",
        TokenKind.And => "'and'",
        TokenKind.Or => "'or'",
        TokenKind.Not => "'not'",
        TokenKind.Is => "'is'",
        TokenKind.Macro => "'macro'",
        TokenKind.LParen => "'('",
        TokenKind.RParen => "')'",
        TokenKind.LBrace => "'{'",
        TokenKind.RBrace => "'}'",
        TokenKind.LBracket => "'['",
        TokenKind.RBracket => "']'",
        TokenKind.Comma => "','",
        TokenKind.Dot => "'.'",
        TokenKind.Colon => "':'",
        TokenKind.Semicolon => "';'",
        TokenKind.Question => "'?'",
        TokenKind.QuestionDot => "'?.'",
        TokenKind.QuestionQuestion => "'??'",
        TokenKind.FatArrow => "'=>'",
        TokenKind.Plus => "'+'",
        TokenKind.Minus => "'-'",
        TokenKind.Star => "'*'",
        TokenKind.Slash => "'/'",
        TokenKind.Percent => "'%'",
        TokenKind.Pipe => "'|'",
        TokenKind.PipePipe => "'||'",
        TokenKind.AmpAmp => "'&&'",
        TokenKind.Assign => "'='",
        TokenKind.EqEq => "'=='",
        TokenKind.Bang => "'!'",
        TokenKind.BangEq => "'!='",
        TokenKind.Lt => "'<'",
        TokenKind.LtEq => "'<='",
        TokenKind.Gt => "'>'",
        TokenKind.GtEq => "'>='",
        TokenKind.PlusEq => "'+='",
        TokenKind.MinusEq => "'-='",
        TokenKind.StarEq => "'*='",
        TokenKind.SlashEq => "'/='",
        TokenKind.PercentEq => "'%='",
        _ => $"'{kind}'",
    };
}
