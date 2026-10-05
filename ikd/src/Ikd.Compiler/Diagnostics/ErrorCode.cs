namespace Ikd.Compiler.Diagnostics;

/// <summary>错误码。1xxx 词法，2xxx 语法，3xxx 语义/类型，4xxx 模块/IO。</summary>
public enum ErrorCode
{
    // ---- 词法 1xxx ----
    UnexpectedCharacter = 1001,
    UnterminatedString = 1002,
    UnterminatedInterpolation = 1003,
    InvalidEscapeSequence = 1004,
    UnterminatedComment = 1005,
    InvalidNumberLiteral = 1006,

    // ---- 语法 2xxx ----
    UnexpectedToken = 2001,
    ExpectedExpression = 2002,
    ExpectedType = 2003,
    ExpectedIdentifier = 2004,
    ExpectedToken = 2005,
    ExpectedBlock = 2006,
    ExpectedStatement = 2007,
    InvalidAssignmentTarget = 2008,
    ExpectedPattern = 2009,
    DuplicateModifier = 2010,
    MisplacedKeyword = 2011,

    // ---- 语义/类型 3xxx ----
    UndefinedName = 3001,
    TypeMismatch = 3002,
    NotCallable = 3003,
    ArgumentCountMismatch = 3004,
    ArgumentTypeMismatch = 3005,
    UndefinedMember = 3006,
    NotAMember = 3007,
    DuplicateDefinition = 3008,
    IncompatibleOverride = 3009,
    CannotInheritFrom = 3010,
    ClassNotFullyImplemented = 3011,
    NotAnInstance = 3012,
    MissingThisContext = 3013,
    InvalidOperatorOperands = 3014,
    ReturnOutsideFunction = 3015,
    BreakOutsideLoop = 3016,
    ContinueOutsideLoop = 3017,
    SuperOutsideClass = 3018,
    ThisOutsideClass = 3019,
    AccessNotPermitted = 3020,
    UnknownType = 3021,
    TypeArgumentCountMismatch = 3022,
    NotAType = 3023,
    InvalidLValue = 3024,
    AbstractMemberCall = 3025,
    MissingReturnValue = 3026,
    UnreachableCode = 3027,
    GenericParameterMismatch = 3028,
    MatchNotExhaustive = 3029,
    CannotAssignToConst = 3030,
    ImportOutsideTopLevel = 3031,
    InvalidCast = 3032,
    EnumMemberNotFound = 3033,
    MatchArmUnreachable = 3034,
    InterfaceMethodMustBeAbstract = 3035,
    StaticMemberAccess = 3036,
    UnknownError = 3999,

    // ---- 模块/IO 4xxx ----
    FileNotFound = 4001,
    ImportCycle = 4002,
    InvalidSourceEncoding = 4003,
}

public static class ErrorCodeExtensions
{
    public static string ToCodeString(this ErrorCode code)
        => "E" + ((int)code).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
}
