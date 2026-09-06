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

            // Convert !(a == b) into a != b (and !(a != b) into a == b). This is essential for
            // template-mapped expressions such as java isEmpty() -> "Count == 0", where emitting
            // "(!x.Count == 0)" is invalid C# (negating an int) while "x.Count != 0" is correct.
            if (TryInvertEquality(exprSyntax, out var inverted))
            {
                return inverted;
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
    /// Rewrites <c>!(a == b)</c> as <c>a != b</c> and <c>!(a != b)</c> as <c>a == b</c>.
    /// </summary>
    private static bool TryInvertEquality(ExpressionSyntax exprSyntax, out ExpressionSyntax? result)
    {
        result = null;

        while (exprSyntax is ParenthesizedExpressionSyntax parenthesized)
        {
            exprSyntax = parenthesized.Expression;
        }

        if (exprSyntax is not BinaryExpressionSyntax binary
            || (binary.Kind() is not SyntaxKind.EqualsExpression and not SyntaxKind.NotEqualsExpression))
        {
            return false;
        }

        var newKind = binary.Kind() == SyntaxKind.EqualsExpression
            ? SyntaxKind.NotEqualsExpression
            : SyntaxKind.EqualsExpression;

        result = SyntaxFactory.BinaryExpression(newKind, binary.Left, binary.Right);
        return true;
    }

    /// <summary>
    /// Wraps the expression in parentheses when applying logical-not to an expression whose
    /// operator precedence is higher than <c>!</c> (binary operators, lambdas, conditionals,
    /// assignments), e.g. <c>!(x == 0)</c> rather than the ambiguous <c>!x == 0</c>.
    /// </summary>
    private static ExpressionSyntax WrapLogicalNotIfNeeded(SyntaxKind kind, ExpressionSyntax expr)
    {
        if (kind == SyntaxKind.LogicalNotExpression
            && (expr is BinaryExpressionSyntax
                || expr is LambdaExpressionSyntax
                || expr is ConditionalExpressionSyntax
                || expr is AssignmentExpressionSyntax))
        {
            return SyntaxFactory.ParenthesizedExpression(
                SyntaxFactory.PrefixUnaryExpression(kind, expr));
        }

        return SyntaxFactory.PrefixUnaryExpression(kind, expr);
    }
}
