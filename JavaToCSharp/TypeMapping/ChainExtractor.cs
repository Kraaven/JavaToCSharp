using com.github.javaparser.ast;
using com.github.javaparser.ast.expr;

namespace JavaToCSharp.TypeMapping;

/// <summary>
/// Flattens a Java member access chain (e.g. <c>System.out.println</c>) into an ordered list
/// of name segments (outermost first: <c>["System", "out", "println"]</c>).
/// Only chains built from <see cref="NameExpr"/> and <see cref="FieldAccessExpr"/> scopes
/// are considered; a method call is only allowed as the outermost node.
/// Intermediate <see cref="MethodCallExpr"/> scopes cause the extraction to return
/// <c>null</c> because their arguments are not representable in the flat name list.
/// </summary>
internal static class ChainExtractor
{
    public static List<string>? Extract(Expression node, bool isMethodCall)
    {
        var names = new List<string>();
        var current = node;
        var first = true;

        while (true)
        {
            switch (current)
            {
                case MethodCallExpr methodCall when first && isMethodCall:
                    names.Add(methodCall.getNameAsString());
                    var callScope = methodCall.getScope().FromOptional<Expression>();
                    if (callScope is null)
                    {
                        names.Reverse();
                        return names;
                    }

                    current = callScope;
                    break;

                case FieldAccessExpr fieldAccess:
                    names.Add(fieldAccess.getNameAsString());
                    current = fieldAccess.getScope();
                    break;

                case NameExpr nameExpr:
                    names.Add(nameExpr.getNameAsString());
                    names.Reverse();
                    return names;

                default:
                    return null;
            }

            first = false;
        }
    }
}
