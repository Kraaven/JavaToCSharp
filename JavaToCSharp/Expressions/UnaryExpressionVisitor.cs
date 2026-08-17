using com.github.javaparser.ast.expr;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Expressions;

public class UnaryExpressionVisitor : ExpressionVisitor<UnaryExpr>
{
    protected override ExpressionSyntax? Visit(ConversionContext context, UnaryExpr unaryExpr)
    {
        var expr = unaryExpr.getExpression();
        var exprSyntax = VisitExpression(context, expr);

        if (exprSyntax is null)
        {
            return null;
        }

        var op = unaryExpr.getOperator();
        var kind = SyntaxKind.None;
        bool isPostfix = false;

        if (op == UnaryExpr.Operator.BITWISE_COMPLEMENT)
            kind = SyntaxKind.BitwiseNotExpression;
        else if (op == UnaryExpr.Operator.MINUS)
            kind = SyntaxKind.UnaryMinusExpression;
        else if (op == UnaryExpr.Operator.LOGICAL_COMPLEMENT)
        {
            // Convert !(x is T y) into x is not T y
            if (TryConvertToIsNotPattern(exprSyntax, out var isNotPattern))
            {
                return isNotPattern;
            }

            kind = SyntaxKind.LogicalNotExpression;
        }
        else if (op == UnaryExpr.Operator.POSTFIX_DECREMENT)
        {
            kind = SyntaxKind.PostDecrementExpression;
            isPostfix = true;
        }
        else if (op == UnaryExpr.Operator.POSTFIX_INCREMENT)
        {
            kind = SyntaxKind.PostIncrementExpression;
            isPostfix = true;
        }
        else if (op == UnaryExpr.Operator.PLUS)
            kind = SyntaxKind.UnaryPlusExpression;
        else if (op == UnaryExpr.Operator.PREFIX_DECREMENT)
            kind = SyntaxKind.PreDecrementExpression;
        else if (op == UnaryExpr.Operator.PREFIX_INCREMENT)
            kind = SyntaxKind.PreIncrementExpression;

        return isPostfix
                   ? SyntaxFactory.PostfixUnaryExpression(kind, exprSyntax)
                   : WrapLogicalNotIfNeeded(kind, exprSyntax);
    }

    private static bool TryConvertToIsNotPattern(ExpressionSyntax exprSyntax, out ExpressionSyntax? result)
    {
        result = null;

        while (exprSyntax is ParenthesizedExpressionSyntax parenthesized)
        {
            exprSyntax = parenthesized.Expression;
        }

        if (exprSyntax is not IsPatternExpressionSyntax isPattern)
        {
            return false;
        }

        result = SyntaxFactory.IsPatternExpression(
            isPattern.Expression,
            SyntaxFactory.UnaryPattern(SyntaxFactory.Token(SyntaxKind.NotKeyword), isPattern.Pattern));
        return true;
    }

    /// <summary>
    /// Wraps the expression in parentheses when applying logical-not to a binary expression
    /// (e.g. <c>!(x == 0)</c> rather than the ambiguous <c>!x == 0</c>).
    /// </summary>
    private static ExpressionSyntax WrapLogicalNotIfNeeded(SyntaxKind kind, ExpressionSyntax expr)
    {
        if (kind == SyntaxKind.LogicalNotExpression
            && expr is BinaryExpressionSyntax)
        {
            return SyntaxFactory.ParenthesizedExpression(
                SyntaxFactory.PrefixUnaryExpression(kind, expr));
        }

        return SyntaxFactory.PrefixUnaryExpression(kind, expr);
    }
}
