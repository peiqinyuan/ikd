using Ikd.Compiler.Diagnostics;

namespace Ikd.Compiler.Syntax;

// ============================================================
//  AST 基类
// ============================================================

public abstract class SyntaxNode
{
    public TextSpan Span { get; set; }

    public virtual IEnumerable<SyntaxNode> Children() => Enumerable.Empty<SyntaxNode>();
}

public abstract class Expression : SyntaxNode { }
public abstract class Statement : SyntaxNode { }
public abstract class Pattern : SyntaxNode { }
public abstract class TypeSyntax : SyntaxNode { }

/// <summary>类/接口成员的修饰符集合。</summary>
public sealed class MemberModifiers : SyntaxNode
{
    public bool IsPub { get; set; }
    public bool IsStatic { get; set; }
    public bool IsOverride { get; set; }
    public bool IsAbstract { get; set; }
    public TextSpan PubSpan { get; set; }
    public TextSpan StaticSpan { get; set; }
    public TextSpan OverrideSpan { get; set; }
    public TextSpan AbstractSpan { get; set; }
}

public enum VarDeclKind { Let, Var, Const }

public enum LiteralKind { Int, Float, Str, Bool, Null }

// ============================================================
//  类型
// ============================================================

public sealed class NamedTypeSyntax : TypeSyntax
{
    public string Name { get; set; } = "";
    public TextSpan NameSpan { get; set; }
    public List<TypeSyntax> TypeArgs { get; } = new();

    public override IEnumerable<SyntaxNode> Children()
    {
        yield return this;
        foreach (var a in TypeArgs) yield return a;
    }
}

public sealed class FnTypeSyntax : TypeSyntax
{
    public List<TypeSyntax> ParamTypes { get; } = new();
    public TypeSyntax? ReturnType { get; set; }
}

// ============================================================
//  表达式
// ============================================================

public sealed class LiteralExpr : Expression
{
    public LiteralKind Kind { get; set; }
    /// <summary>long / double / bool / null；Str 时为 InterpolatedString。</summary>
    public object? Value { get; set; }
}

public sealed class NameExpr : Expression
{
    public string Name { get; set; } = "";
    public TextSpan NameSpan { get; set; }
}

public sealed class MemberExpr : Expression
{
    public Expression Target { get; set; } = null!;
    public string Name { get; set; } = "";
    public bool NullSafe { get; set; }
    public TextSpan NameSpan { get; set; }
}

public sealed class IndexExpr : Expression
{
    public Expression Target { get; set; } = null!;
    public Expression Index { get; set; } = null!;
}

public sealed class CallExpr : Expression
{
    public Expression Callee { get; set; } = null!;
    public List<Expression> Args { get; } = new();
    public TextSpan LParenSpan { get; set; }
    public TextSpan RParenSpan { get; set; }
}

public sealed class BinaryExpr : Expression
{
    public Expression Left { get; set; } = null!;
    public TokenKind Op { get; set; }
    public TextSpan OpSpan { get; set; }
    public Expression Right { get; set; } = null!;
}

public sealed class UnaryExpr : Expression
{
    public TokenKind Op { get; set; }
    public TextSpan OpSpan { get; set; }
    public Expression Operand { get; set; } = null!;
}

public sealed class AssignExpr : Expression
{
    public Expression Target { get; set; } = null!;
    public TokenKind Op { get; set; }
    public TextSpan OpSpan { get; set; }
    public Expression Value { get; set; } = null!;
}

public sealed class TernaryExpr : Expression
{
    public Expression Cond { get; set; } = null!;
    public Expression Then { get; set; } = null!;
    public Expression Else { get; set; } = null!;
}

public sealed class ThisExpr : Expression { }

public sealed class SuperExpr : Expression { }

public sealed class NewExpr : Expression
{
    public TypeSyntax Type { get; set; } = null!;
    public List<Expression> Args { get; } = new();
    public TextSpan LParenSpan { get; set; }
}

public sealed class ListExpr : Expression
{
    public List<Expression> Elements { get; } = new();
}

public sealed class MapExpr : Expression
{
    public List<(Expression Key, Expression Value)> Entries { get; } = new();
}

public sealed class LambdaExpr : Expression
{
    public List<ParamSyntax> Params { get; } = new();
    public TypeSyntax? ReturnType { get; set; }
    public Expression? BodyExpr { get; set; }
    public BlockStmt? BodyBlock { get; set; }
    public bool IsArrow => BodyExpr != null;
}

public sealed class MatchExpr : Expression
{
    public Expression Subject { get; set; } = null!;
    public List<MatchArm> Arms { get; } = new();
}

public sealed class CastExpr : Expression
{
    public Expression Target { get; set; } = null!;
    public TypeSyntax Type { get; set; } = null!;
}

public sealed class IsExpr : Expression
{
    public Expression Target { get; set; } = null!;
    public TypeSyntax Type { get; set; } = null!;
}

public sealed class MatchArm : SyntaxNode
{
    public Pattern Pattern { get; set; } = null!;
    public Expression? Guard { get; set; }
    public Expression Body { get; set; } = null!;
    public TextSpan ArrowSpan { get; set; }
}

// ============================================================
//  模式
// ============================================================

public sealed class LiteralPattern : Pattern
{
    public LiteralKind Kind { get; set; }
    public object? Value { get; set; }
}

public sealed class WildcardPattern : Pattern { }

public sealed class BindingPattern : Pattern
{
    public string Name { get; set; } = "";
    public TextSpan NameSpan { get; set; }
}

public sealed class TypePattern : Pattern
{
    public TypeSyntax Type { get; set; } = null!;
}

public sealed class EnumPattern : Pattern
{
    /// <summary>限定名（如 Color 中的 "Color"），无则为 null。</summary>
    public string? Qualifier { get; set; }
    public string Name { get; set; } = "";
    public bool HasArgs { get; set; }
    public List<Pattern> Args { get; } = new();
    public TextSpan NameSpan { get; set; }
}

public sealed class ListPattern : Pattern
{
    public List<Pattern> Items { get; } = new();
}

// ============================================================
//  语句 / 声明
// ============================================================

public sealed class BlockStmt : Statement
{
    public List<Statement> Statements { get; } = new();

    public override IEnumerable<SyntaxNode> Children() => Statements;
}

public sealed class ExprStmt : Statement
{
    public Expression Expression { get; set; } = null!;
}

public sealed class VarDeclStmt : Statement
{
    public VarDeclKind Kind { get; set; }
    public string Name { get; set; } = "";
    public TextSpan NameSpan { get; set; }
    public TextSpan KeywordSpan { get; set; }
    public TypeSyntax? Type { get; set; }
    public Expression? Value { get; set; }
}

public sealed class IfStmt : Statement
{
    public Expression Cond { get; set; } = null!;
    public BlockStmt Then { get; set; } = null!;
    public Statement? Else { get; set; }
}

public sealed class WhileStmt : Statement
{
    public Expression Cond { get; set; } = null!;
    public BlockStmt Body { get; set; } = null!;
}

public sealed class ForStmt : Statement
{
    public string Name { get; set; } = "";
    public TextSpan NameSpan { get; set; }
    public TypeSyntax? Type { get; set; }
    public Expression Iterable { get; set; } = null!;
    public BlockStmt Body { get; set; } = null!;
}

public sealed class LoopStmt : Statement
{
    public BlockStmt Body { get; set; } = null!;
}

public sealed class BreakStmt : Statement { }
public sealed class ContinueStmt : Statement { }

public sealed class ReturnStmt : Statement
{
    public Expression? Value { get; set; }
    public TextSpan KeywordSpan { get; set; }
}

public sealed class ThrowStmt : Statement
{
    public Expression Value { get; set; } = null!;
}

public sealed class TryStmt : Statement
{
    public BlockStmt Body { get; set; } = null!;
    public string? CatchName { get; set; }
    public TextSpan CatchNameSpan { get; set; }
    public BlockStmt? CatchBody { get; set; }
    public BlockStmt? FinallyBody { get; set; }
}

public sealed class ImportStmt : Statement
{
    public string Path { get; set; } = "";
    public string? Alias { get; set; }
    public TextSpan PathSpan { get; set; }
    public TextSpan AliasSpan { get; set; }
}

public sealed class ParamSyntax : SyntaxNode
{
    public string Name { get; set; } = "";
    public TextSpan NameSpan { get; set; }
    public TypeSyntax? Type { get; set; }
}

/// <summary>泛型形参，如 fn id&lt;T&gt;(x: T) 中的 T。</summary>
public sealed class TypeParamSyntax : SyntaxNode
{
    public string Name { get; set; } = "";
    public TextSpan NameSpan { get; set; }
}

public sealed class FnDecl : Statement
{
    public string Name { get; set; } = "";
    public TextSpan NameSpan { get; set; }
    public List<TypeParamSyntax> TypeParams { get; } = new();
    public List<ParamSyntax> Params { get; } = new();
    public TypeSyntax? ReturnType { get; set; }
    public BlockStmt Body { get; set; } = null!;
    public MemberModifiers Mods { get; set; } = new();
}

public abstract class ClassMember : SyntaxNode
{
    public MemberModifiers Mods { get; set; } = new();
}

public sealed class FieldDecl : ClassMember
{
    public VarDeclKind Kind { get; set; }
    public string Name { get; set; } = "";
    public TextSpan NameSpan { get; set; }
    public TextSpan KeywordSpan { get; set; }
    public TypeSyntax? Type { get; set; }
    public Expression? Value { get; set; }
}

public sealed class MethodDecl : ClassMember
{
    public string Name { get; set; } = "";
    public TextSpan NameSpan { get; set; }
    public List<TypeParamSyntax> TypeParams { get; } = new();
    public List<ParamSyntax> Params { get; } = new();
    public TypeSyntax? ReturnType { get; set; }
    /// <summary>abstract 方法或接口方法为 null。</summary>
    public BlockStmt? Body { get; set; }
    public bool IsConstructor { get; set; }
}

public sealed class EnumCaseDecl : SyntaxNode
{
    public string Name { get; set; } = "";
    public TextSpan NameSpan { get; set; }
    public List<TypeSyntax> Payload { get; } = new();
}

public sealed class ClassDecl : Statement
{
    public string Name { get; set; } = "";
    public TextSpan NameSpan { get; set; }
    public List<TypeParamSyntax> TypeParams { get; } = new();
    public TypeSyntax? Base { get; set; }
    public TextSpan BaseSpan { get; set; }
    public List<TypeSyntax> Interfaces { get; } = new();
    public List<ClassMember> Members { get; } = new();
    public MemberModifiers Mods { get; set; } = new();
}

public sealed class InterfaceDecl : Statement
{
    public string Name { get; set; } = "";
    public TextSpan NameSpan { get; set; }
    public List<TypeParamSyntax> TypeParams { get; } = new();
    public List<TypeSyntax> Parents { get; } = new();
    public List<ClassMember> Members { get; } = new();
    public MemberModifiers Mods { get; set; } = new();
}

public sealed class EnumDecl : Statement
{
    public string Name { get; set; } = "";
    public TextSpan NameSpan { get; set; }
    public List<EnumCaseDecl> Cases { get; } = new();
    public MemberModifiers Mods { get; set; } = new();
}

/// <summary>一个源文件的解析结果。</summary>
public sealed class CompilationUnit : SyntaxNode
{
    public SourceText Source { get; set; } = null!;
    public List<Statement> Statements { get; } = new();
}
