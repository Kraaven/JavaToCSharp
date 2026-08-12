using com.github.javaparser.ast.expr;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace JavaToCSharp.Expressions;

public class SwitchExpressionVisitor : ExpressionVisitor<SwitchExpr>
{
    protected override ExpressionSyntax Visit(ConversionContext context, SwitchExpr expr)
        => Statements.SwitchConversion.ConvertSwitchExpressionToTemp(context, expr);

    internal static bool TryGetEnumConstantExpression(
        ConversionContext context,
        Expression label,
        out ExpressionSyntax enumConstant)
    {
        enumConstant = null!;

        if (label is NameExpr nameExpr
            && context.EnumConstantsBySimpleName.TryGetValue(nameExpr.getNameAsString(), out string? qualified))
        {
            enumConstant = ParseName(qualified);
            return true;
        }

        if (label is FieldAccessExpr fieldAccess
            && fieldAccess.getScope() is NameExpr scope
            && context.EnumConstantsBySimpleName.TryGetValue(fieldAccess.getNameAsString(), out string? qualifiedFromField)
            && qualifiedFromField.StartsWith(scope.getNameAsString() + ".", StringComparison.Ordinal))
        {
            enumConstant = ParseName(qualifiedFromField);
            return true;
        }

        return false;
    }
}
