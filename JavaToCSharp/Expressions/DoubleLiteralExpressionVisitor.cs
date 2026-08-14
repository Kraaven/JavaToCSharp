using System.Globalization;
using com.github.javaparser.ast.expr;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Expressions;

public class DoubleLiteralExpressionVisitor : ExpressionVisitor<DoubleLiteralExpr>
{
    private static readonly CultureInfo _cultureInfo = CultureInfo.InvariantCulture;

    protected override ExpressionSyntax Visit(ConversionContext context, DoubleLiteralExpr expr)
    {
        var value = expr.getValue().Replace("_", string.Empty);

        var literalSyntax = value.EndsWith("f", StringComparison.OrdinalIgnoreCase)
            ? SyntaxFactory.Literal(float.Parse(value.TrimEnd('f', 'F'), NumberStyles.Any, _cultureInfo))
            : SyntaxFactory.Literal(double.Parse(value.TrimEnd('d', 'D'), NumberStyles.Any, _cultureInfo));

        return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, literalSyntax);
    }
}
