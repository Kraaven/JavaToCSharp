using com.github.javaparser.ast.expr;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Expressions;

public class CastExpressionVisitor : ExpressionVisitor<CastExpr>
{
    protected override ExpressionSyntax? Visit(ConversionContext context, CastExpr castExpr)
    {
        var expr = castExpr.getExpression();
        var exprSyntax = VisitExpression(context, expr);
        if (exprSyntax is null)
        {
            return null;
        }

        var type = TypeHelper.ConvertTypeOf(context, castExpr);

        // Java intersection types in cast position, (A & B)value, have no C# equivalent. Emit the
        // first (least-derived) type only; the value already holds a compatible instance at
        // runtime. Cut at the FIRST ampersand regardless of surrounding whitespace: the type
        // string may be rendered compactly (A<B>&C) or spaced (A<B> & C) depending on the path
        // that produced it.
        int intersection = type.IndexOf('&');
        if (intersection >= 0)
        {
            type = type[..intersection].TrimEnd();
        }

        // A lambda directly under a cast, (T)(a, b) => body, is ambiguous for the C# parser and the
        // tolerant parser either misbinds it or reports diagnostics, producing garbage on reprint.
        // Parenthesizing the lambda disambiguates: (T)((a, b) => body). Both parenthesized and
        // simple (single-parameter, unparenthesized) lambda forms must be wrapped.
        if (exprSyntax is ParenthesizedLambdaExpressionSyntax or SimpleLambdaExpressionSyntax)
        {
            exprSyntax = SyntaxFactory.ParenthesizedExpression(exprSyntax);
        }

        return SyntaxFactory.CastExpression(SyntaxFactory.ParseTypeName(type), exprSyntax);
    }
}
