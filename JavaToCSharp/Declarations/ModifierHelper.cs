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
    /// Replaces a private modifier token with public (CS0107 forbids the pair). Java allows a
    /// private nested type in a public member signature (erasure hides it); C# rejects the
    /// combination (CS0050/CS0051) — promote, as with package-private and protected.
    /// </summary>
    public static T ReplacePrivateWithPublic<T>(T syntax) where T : MemberDeclarationSyntax
    {
        var privateToken = syntax.Modifiers.FirstOrDefault(t => t.IsKind(SyntaxKind.PrivateKeyword));

        if (privateToken == default)
        {
            return syntax;
        }

        return (T)syntax.WithModifiers(syntax.Modifiers.Replace(privateToken, SyntaxFactory.Token(SyntaxKind.PublicKeyword)));
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

    /// <summary>
    /// Replaces an existing C# <c>protected</c> modifier with <c>public</c> (nested type
    /// promotion). Adding <c>public</c> alongside <c>protected</c> is illegal C# (CS0107,
    /// "more than one protection modifier"), so the original token must be substituted.
    /// Returns the syntax unchanged when no <c>protected</c> token is present.
    /// </summary>
    public static T ReplaceProtectedWithPublic<T>(T syntax) where T : MemberDeclarationSyntax
    {
        var protectedToken = syntax.Modifiers.FirstOrDefault(t => t.IsKind(SyntaxKind.ProtectedKeyword));

        if (protectedToken == default)
        {
            return syntax;
        }

        return (T)syntax.WithModifiers(syntax.Modifiers.Replace(protectedToken, SyntaxFactory.Token(SyntaxKind.PublicKeyword)));
    }
}
