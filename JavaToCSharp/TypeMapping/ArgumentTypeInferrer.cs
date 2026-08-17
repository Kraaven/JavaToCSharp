using com.github.javaparser.ast.expr;

namespace JavaToCSharp.TypeMapping;

/// <summary>
/// Infers Java type names from AST expressions and matches them against type hints.
/// Used to disambiguate overloaded method mappings by argument type.
/// </summary>
internal static class ArgumentTypeInferrer
{
    public static string? InferArgType(Expression arg) => arg switch
    {
        IntegerLiteralExpr => "int",
        LongLiteralExpr => "long",
        DoubleLiteralExpr => "double",
        BooleanLiteralExpr => "boolean",
        CharLiteralExpr => "char",
        StringLiteralExpr => "String",
        NullLiteralExpr => null,
        NameExpr name => name.getNameAsString(),
        ObjectCreationExpr creation => creation.getType().getNameAsString(),
        CastExpr cast => cast.getType().toString(),
        UnaryExpr unary => InferArgType(unary.getExpression()),
        _ => null,
    };

    public static bool TypeHintMatches(IReadOnlyList<string>? hint, List<string?> inferred)
    {
        if (hint is null || hint.Count != inferred.Count)
        {
            return false;
        }

        for (var i = 0; i < hint.Count; i++)
        {
            var actual = inferred[i];
            if (actual is null)
            {
                continue;
            }

            if (!TypeNamesEqual(hint[i], actual))
            {
                return false;
            }
        }

        return true;
    }

    public static bool TypeNamesEqual(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var aSimple = a.Contains('.') ? a[(a.LastIndexOf('.') + 1)..] : a;
        var bSimple = b.Contains('.') ? b[(b.LastIndexOf('.') + 1)..] : b;
        return string.Equals(aSimple, bSimple, StringComparison.OrdinalIgnoreCase);
    }
}
