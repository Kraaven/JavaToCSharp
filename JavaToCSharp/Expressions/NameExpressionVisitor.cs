using com.github.javaparser.ast.expr;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Expressions;

public class NameExpressionVisitor : ExpressionVisitor<NameExpr>
{
    protected override ExpressionSyntax Visit(ConversionContext context, NameExpr nameExpr)
    {
        string name = nameExpr.getNameAsString();

        if (context.RecordComponentBackingFields.TryGetValue(name, out string? backingField))
        {
            name = backingField;
        }

        return SyntaxFactory.IdentifierName(TypeHelper.EscapeIdentifier(name));
    }
}
