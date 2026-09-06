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

            var nameSpace = TypeHelper.Capitalize(importNameWithoutClassName);

            // Derive the namespace from the mapping index's package-to-namespace rows.
            if (options?.MappingIndex is not null
                     && options.MappingIndex.TryGetCSharpPackages(importNameWithoutClassName, out var csharpPackages))
            {
                nameSpace = csharpPackages[0];
            }

            var usingSyntax = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(nameSpace));

            if (context.Options.IncludeComments)
            {
                usingSyntax = CommentsHelper.AddUsingComments(usingSyntax, import);
            }

            usings.Add(usingSyntax.NormalizeWhitespace().WithTrailingNewLines());
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
