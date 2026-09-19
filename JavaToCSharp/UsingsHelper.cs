using com.github.javaparser.ast;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp;

public static class UsingsHelper
{
    public static IEnumerable<UsingDirectiveSyntax> GetUsings(ConversionContext context,
        IEnumerable<ImportDeclaration> imports,
        JavaConversionOptions? options)
    {
        var usings = new List<UsingDirectiveSyntax>();

        foreach (var import in imports)
        {
            // The import directive in Java will import a specific class.
            string importName = import.getNameAsString();
            var lastPartStartIndex = importName.LastIndexOf('.');
            var importNameWithoutClassName = lastPartStartIndex == -1 ?
                                                 importName :
                                                 importName[..lastPartStartIndex];

            bool isStatic = import.isStatic();

            // Single-type import of a NESTED type (import a.b.C.Nested — the segment before the
            // leaf is a class name by Java's casing convention): C# has no using-form for nested
            // types, so emit a USING ALIAS to the mapped target instead. The mapped target is
            // either an existing dotted mapping row (TryMapTypeLeafName) or the enclosing
            // package's mapped namespace with the capitalized outer type appended (a tree-typed
            // outer like com.mojang.datafixers.DSL.TypeReference has no mapping row of its own).
            if (!isStatic && importName.IndexOf('.') != lastPartStartIndex && lastPartStartIndex > 0)
            {
                var outerTailStart = importName.LastIndexOf('.', lastPartStartIndex - 1);

                // Only a genuinely nested-type import takes this branch: the segment before the
                // leaf must start a class name (java.util.Map.Entry → outer "Map"). A flat
                // package import (java.util.List → outer segment "util", lowercase) must fall
                // through to the package-row mapping path below — aliasing it to a tree type
                // that merely shares the package segment's name (Util) is a misfire.
                if (outerTailStart >= 0 && char.IsUpper(importName[outerTailStart + 1]))
                {
                    var nestedSegments = importName[(outerTailStart + 1)..];
                    var nestedLeaf = importName[(lastPartStartIndex + 1)..];

                    if (TypeMapping.TypeMappingResolver.TryMapTypeLeafName(context, nestedSegments, out var mappedNested))
                    {
                        usings.Add(MakeAliasUsing(context, import, nestedLeaf, mappedNested));
                        continue;
                    }

                    var outerName = importName[(outerTailStart + 1)..lastPartStartIndex];

                    // Tree-typed outer (e.g. com.mojang.datafixers.DSL.TypeReference has no dotted
                    // mapping row of its own): alias to the outer type's known namespace. JDK
                    // nested imports (java.util.Map.Entry) have no tree declaration and fall
                    // through unchanged.
                    if (context.Options.KnownTypeNamespaces is { Count: > 0 } known
                        && known.TryGetValue(outerName.ToLowerInvariant(), out var outerNamespace)
                        && !string.IsNullOrWhiteSpace(outerNamespace))
                    {
                        var aliasTarget = outerNamespace + "." + TypeHelper.Capitalize(nestedLeaf);

                        usings.Add(MakeAliasUsing(context, import, nestedLeaf, aliasTarget));
                        continue;
                    }
                }
            }

            // Remaining nested imports (package-adjacent uppercase tails with no resolvable
            // mapping) fall through to bare-name import-scan resolution — nothing to emit.
            if (!isStatic && importNameWithoutClassName.Contains('.')
                && char.IsUpper(importNameWithoutClassName[(importNameWithoutClassName.LastIndexOf('.') + 1)]))
            {
                continue;
            }

            if (isStatic)
            {
                // Static import: "import static X.Y.Z.method" -> "using static X.Y.Z;"
                // The full path including the class name becomes the using static target.
                var staticTarget = TypeHelper.Capitalize(importName);

                if (options?.MappingIndex is not null
                    && options.MappingIndex.TryGetCSharpPackages(importNameWithoutClassName, out var staticCsharpPackages))
                {
                    // Replace the package part with the mapped namespace, keep the class name
                    var mappedPackage = staticCsharpPackages[0];
                    var className = importName[(lastPartStartIndex + 1)..];
                    staticTarget = mappedPackage + "." + className;
                }

                var staticUsing = SyntaxFactory.UsingDirective(
                    SyntaxFactory.NameEquals(SyntaxFactory.IdentifierName("static")),
                    SyntaxFactory.ParseName(staticTarget));

                if (context.Options.IncludeComments)
                {
                    staticUsing = CommentsHelper.AddUsingComments(staticUsing, import);
                }

                usings.Add(staticUsing.NormalizeWhitespace().WithTrailingNewLines());
                continue;
            }

            // Derive the namespace(s) from the mapping index's package-to-namespace rows.
            // A row may map to several C# namespaces (e.g. LWJGL bindings spread across
            // Silk.NET packages); emit a using for each so every mapped type resolves.
            if (options?.MappingIndex is not null
                     && options.MappingIndex.TryGetCSharpPackages(importNameWithoutClassName, out var csharpPackages))
            {
                for (var i = 0; i < csharpPackages.Length; i++)
                {
                    var usingSyntax = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(csharpPackages[i]));

                    if (context.Options.IncludeComments && i == 0)
                    {
                        usingSyntax = CommentsHelper.AddUsingComments(usingSyntax, import);
                    }

                    usings.Add(usingSyntax.NormalizeWhitespace().WithTrailingNewLines());
                }

                continue;
            }

            var nameSpace = TypeHelper.Capitalize(importNameWithoutClassName);

            var defaultUsingSyntax = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(nameSpace));

            if (context.Options.IncludeComments)
            {
                defaultUsingSyntax = CommentsHelper.AddUsingComments(defaultUsingSyntax, import);
            }

            usings.Add(defaultUsingSyntax.NormalizeWhitespace().WithTrailingNewLines());
        }

        if (options?.IncludeUsings == true)
        {
            usings.AddRange(options.Usings
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(ns => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(ns)).NormalizeWhitespace().WithTrailingNewLines()));
        }

        // Mapping-derived usings: sorted alphabetically and deduplicated among themselves.
        if (context.MappingUsings.Count > 0)
        {
            usings.AddRange(context.MappingUsings
                .OrderBy(ns => ns, StringComparer.Ordinal)
                .Select(ns => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(ns)).NormalizeWhitespace().WithTrailingNewLines()));
        }

        usings = usings.Distinct(new UsingDirectiveSyntaxComparer()).ToList();

        if (usings.Count > 0)
        {
            usings[^1] = usings[^1].WithTrailingNewLines(2);
        }

        return usings;
    }

    /// <summary>
    /// Builds the using-alias directive for a nested-type import:
    /// <c>using NestedLeaf = Mapped.Outer.NestedLeaf;</c>
    /// </summary>
    private static UsingDirectiveSyntax MakeAliasUsing(
        ConversionContext context, ImportDeclaration import, string nestedLeaf, string aliasTarget)
    {
        var aliasUsing = SyntaxFactory.UsingDirective(
            SyntaxFactory.NameEquals(SyntaxFactory.IdentifierName(TypeHelper.EscapeIdentifier(nestedLeaf))),
            SyntaxFactory.ParseTypeName(aliasTarget));

        if (context.Options.IncludeComments)
        {
            aliasUsing = CommentsHelper.AddUsingComments(aliasUsing, import);
        }

        return aliasUsing.NormalizeWhitespace().WithTrailingNewLines();
    }
}

public class UsingDirectiveSyntaxComparer : IEqualityComparer<UsingDirectiveSyntax>
{
    public bool Equals(UsingDirectiveSyntax? x, UsingDirectiveSyntax? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null) return false;
        if (y is null) return false;
        if (x.GetType() != y.GetType()) return false;

        return Equals(x.Alias?.ToString(), y.Alias?.ToString()) &&
               Equals(x.Name?.ToString(), y.Name?.ToString());
    }

    public int GetHashCode(UsingDirectiveSyntax obj)
        => HashCode.Combine(obj.Alias?.ToString() ?? "", obj.Name?.ToString() ?? "");
}
