using com.github.javaparser.ast;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Declarations;

/// <summary>
/// Converts Java modifiers to C# syntax tokens and detects common patterns
/// such as package-private promotion for nested class members.
/// </summary>
internal static class ModifierHelper
{
    private static readonly Dictionary<Modifier.Keyword, SyntaxKind> AccessModifierMap = new()
    {
        [Modifier.Keyword.PUBLIC] = SyntaxKind.PublicKeyword,
        [Modifier.Keyword.PROTECTED] = SyntaxKind.ProtectedKeyword,
        [Modifier.Keyword.PRIVATE] = SyntaxKind.PrivateKeyword,
    };

    /// <summary>
    /// Adds access modifiers (public, protected, private) to a syntax node based on the given Java modifier set.
    /// </summary>
    public static T AddAccessModifiers<T>(T syntax, ISet<Modifier.Keyword> mods) where T : MemberDeclarationSyntax
    {
        if (mods.Contains(Modifier.Keyword.PUBLIC))
            syntax = (T)syntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
        if (mods.Contains(Modifier.Keyword.PROTECTED))
            syntax = (T)syntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));
        if (mods.Contains(Modifier.Keyword.PRIVATE))
            syntax = (T)syntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));

        return syntax;
    }

    /// <summary>
    /// Adds static, abstract, final/sealed, and volatile modifiers based on the given Java modifier set.
    /// </summary>
    public static T AddNonAccessModifiers<T>(T syntax, ISet<Modifier.Keyword> mods,
        bool mapFinalToSealed = false,
        bool mapFinalToReadOnly = false,
        bool mapAbstract = false,
        bool mapStatic = false,
        bool mapVolatile = false) where T : MemberDeclarationSyntax
    {
        if (mapStatic && mods.Contains(Modifier.Keyword.STATIC))
            syntax = (T)syntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.StaticKeyword));

        if (mapAbstract && mods.Contains(Modifier.Keyword.ABSTRACT))
            syntax = (T)syntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.AbstractKeyword));

        if (mapFinalToReadOnly && mods.Contains(Modifier.Keyword.FINAL))
            syntax = (T)syntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));

        if (mapFinalToSealed && mods.Contains(Modifier.Keyword.FINAL))
            syntax = (T)syntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.SealedKeyword));

        if (mapVolatile && mods.Contains(Modifier.Keyword.VOLATILE))
            syntax = (T)syntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.VolatileKeyword));

        return syntax;
    }

    /// <summary>
    /// Returns true if the modifier set is package-private (no access modifier specified).
    /// </summary>
    public static bool IsPackagePrivate(ISet<Modifier.Keyword> mods) =>
        !mods.Contains(Modifier.Keyword.PUBLIC) &&
        !mods.Contains(Modifier.Keyword.PROTECTED) &&
        !mods.Contains(Modifier.Keyword.PRIVATE);

    /// <summary>
    /// When a nested class member has package-private (no access modifier), promote it to
    /// public so the enclosing class can access it (C# default for members is private).
    /// </summary>
    public static T PromoteNestedPackagePrivate<T>(T syntax, ConversionContext context, ISet<Modifier.Keyword> mods) where T : MemberDeclarationSyntax
    {
        if (context.RootTypeName is not null
            && context.LastTypeName is not null
            && context.RootTypeName != context.LastTypeName
            && IsPackagePrivate(mods))
        {
            syntax = (T)syntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
        }

        return syntax;
    }
}
