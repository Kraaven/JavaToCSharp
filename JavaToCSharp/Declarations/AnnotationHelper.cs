using com.github.javaparser.ast.expr;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Declarations;

/// <summary>
/// Converts Java annotations to C# attributes.
/// </summary>
internal static class AnnotationHelper
{
    /// <summary>
    /// Converts a Java <c>@Deprecated</c> annotation to a C# <c>[Obsolete]</c> attribute.
    /// Parses the <c>since</c> and <c>forRemoval</c> attributes when present.
    /// Returns <c>null</c> if the annotation is not <c>@Deprecated</c>.
    /// </summary>
    public static AttributeListSyntax? ConvertDeprecatedToObsolete(AnnotationExpr annotation)
    {
        if (annotation.getNameAsString() != "Deprecated")
            return null;

        string message = "";
        bool isError = false;

        if (annotation.isNormalAnnotationExpr())
        {
            var pairs = annotation.asNormalAnnotationExpr().getPairs();
            for (int i = 0; i < pairs.size(); i++)
            {
                var pair = (MemberValuePair)pairs.get(i);
                string pairName = pair.getName().asString();
                var value = pair.getValue();

                if (pairName == "since" && value is StringLiteralExpr stringLit)
                {
                    message = stringLit.getValue();
                }
                else if (pairName == "forRemoval" && value is BooleanLiteralExpr boolLit)
                {
                    isError = boolLit.getValue();
                }
                else if (pairName == "forRemoval")
                {
                    isError = value?.ToString()?.Trim() == "true";
                }
            }
        }

        if (string.IsNullOrEmpty(message))
        {
            message = "Use replacement instead.";
        }

        var obsoleteArgs = new List<AttributeArgumentSyntax>
        {
            SyntaxFactory.AttributeArgument(
                SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(message)))
        };

        if (isError)
        {
            obsoleteArgs.Add(
                SyntaxFactory.AttributeArgument(
                    SyntaxFactory.NameEquals("error"),
                    null,
                    SyntaxFactory.LiteralExpression(
                        SyntaxKind.TrueLiteralExpression)));
        }

        return SyntaxFactory.AttributeList(
            SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Attribute(SyntaxFactory.IdentifierName("Obsolete"))
                    .WithArgumentList(
                        SyntaxFactory.AttributeArgumentList(
                            SyntaxFactory.SeparatedList(obsoleteArgs)))));
    }
}
