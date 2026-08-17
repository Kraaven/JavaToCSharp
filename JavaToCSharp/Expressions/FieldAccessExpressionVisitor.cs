using com.github.javaparser.ast.expr;
using JavaToCSharp.TypeMapping;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Expressions;

public class FieldAccessExpressionVisitor : ExpressionVisitor<FieldAccessExpr>
{
    protected override ExpressionSyntax? Visit(ConversionContext context, FieldAccessExpr fieldAccessExpr)
    {
        if (context.Options.MappingIndex is not null)
        {
            var status = TypeMappingResolver.TryResolveFieldAccess(context, fieldAccessExpr, out var mappedSyntax);
            if (status != MappingResolutionStatus.Unresolved)
            {
                return mappedSyntax;
            }
        }

        var scope = fieldAccessExpr.getScope();
        ExpressionSyntax? scopeSyntax = null;

        if (scope is not null)
        {
            scopeSyntax = VisitExpression(context, scope);
        }

        if (scopeSyntax is null)
        {
            return null;
        }

        var field = fieldAccessExpr.getNameAsString();

        if (context.RecordComponentBackingFields.TryGetValue(field, out string? backingField))
        {
            field = backingField;
        }

        field = context.Options.PreserveJavaMethodNames
            ? field
            : TypeHelper.ReplaceCommonFieldNames(field);
        field = TypeHelper.EscapeIdentifier(field);

        return SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, scopeSyntax, SyntaxFactory.IdentifierName(field));
    }
}
