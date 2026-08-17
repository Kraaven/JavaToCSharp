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
        var text = expr.getValue();

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            // Java hexadecimal floating-point literals (0xM.Pe) are not valid C#; emit the decimal equivalent.
            return SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(ParseHexFloat(text)));
        }

        var parseable = text.Replace("_", string.Empty);

        if (parseable.EndsWith("f", StringComparison.OrdinalIgnoreCase))
        {
            var floatValue = float.Parse(parseable.TrimEnd('f', 'F'), NumberStyles.Any, _cultureInfo);
            return SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(text, floatValue));
        }

        var doubleValue = double.Parse(parseable.TrimEnd('d', 'D'), NumberStyles.Any, _cultureInfo);

        // Preserve the original literal text (suffix, exponent and digit grouping) so the
        // numeric type of the Java literal carries over to the C# output, e.g. -1.0 stays -1.0.
        return SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal(text, doubleValue));
    }

    private static double ParseHexFloat(string text)
    {
        var hex = text[2..];
        var pIndex = hex.IndexOfAny(['p', 'P']);
        if (pIndex < 0)
        {
            return Convert.ToInt64(hex.Replace(".", string.Empty), 16);
        }

        var mantissaHex = hex[..pIndex];
        var exponent = int.Parse(hex[(pIndex + 1)..], NumberStyles.AllowLeadingSign, _cultureInfo);

        var dot = mantissaHex.IndexOf('.');
        var whole = dot < 0 ? mantissaHex : mantissaHex[..dot];
        var fraction = dot < 0 ? string.Empty : mantissaHex[(dot + 1)..];

        double mantissa = Convert.ToInt64(whole.Length == 0 ? "0" : whole, 16);
        for (var i = 0; i < fraction.Length; i++)
        {
            mantissa += Convert.ToInt32(fraction[i].ToString(), 16) / Math.Pow(16, i + 1);
        }

        return mantissa * Math.Pow(2, exponent);
    }
}
