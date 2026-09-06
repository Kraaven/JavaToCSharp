using com.github.javaparser.ast.expr;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Expressions;

public class InstanceOfExpressionVisitor : ExpressionVisitor<InstanceOfExpr>
{
    protected override ExpressionSyntax? Visit(ConversionContext context, InstanceOfExpr expr)
    {
        var innerExpr = expr.getExpression();
        var exprSyntax = VisitExpression(context, innerExpr);

        if (exprSyntax is null)
        {
            return null;
        }

        if (expr.getPattern().isPresent() && expr.getPattern().get() is PatternExpr pattern)
        {
            return PatternConversion.PatternToIsCondition(context, exprSyntax, pattern);
        }

        var javaTypeName = expr.getType().toString();
        javaTypeName = TypeHelper.ReplaceWildcardsWithTypeParameters(javaTypeName, context.TypeParameterNames);
        var type = TypeHelper.ConvertType(context, javaTypeName);

        return SyntaxFactory.BinaryExpression(
            SyntaxKind.IsExpression,
            exprSyntax,
            SyntaxFactory.IdentifierName(type));
    }
}
