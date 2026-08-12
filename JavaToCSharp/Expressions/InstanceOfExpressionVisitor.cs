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
            var typeName = TypeHelper.ConvertType(pattern.getType().toString());
            typeName = TypeHelper.ReplaceWildcardsWithTypeParameters(typeName, context.TypeParameterNames);

            return SyntaxFactory.IsPatternExpression(
                exprSyntax,
                SyntaxFactory.DeclarationPattern(
                    SyntaxFactory.ParseTypeName(typeName),
                    SyntaxFactory.SingleVariableDesignation(
                        SyntaxFactory.Identifier(TypeHelper.EscapeIdentifier(pattern.getNameAsString())))));
        }

        var type = TypeHelper.ConvertTypeOf(expr);
        type = TypeHelper.ReplaceWildcardsWithTypeParameters(type, context.TypeParameterNames);

        return SyntaxFactory.BinaryExpression(
            SyntaxKind.IsExpression,
            exprSyntax,
            SyntaxFactory.IdentifierName(type));
    }
}
