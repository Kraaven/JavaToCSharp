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

        // Deconflicted field references (field + method sharing a name): a bare identifier that
        // is NOT a declared local/parameter refers to the renamed field.
        if (context.FieldNameRenames.TryGetValue(name, out string? renamedField)
            && !context.VariableScopes.TryGetVariableType(name, out _))
        {
            name = renamedField;
        }

        return SyntaxFactory.IdentifierName(TypeHelper.EscapeIdentifier(name));
    }
}
