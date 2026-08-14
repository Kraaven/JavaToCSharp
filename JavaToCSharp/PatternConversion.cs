using com.github.javaparser.ast.expr;
using JavaToCSharp.Expressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Type = com.github.javaparser.ast.type.Type;

namespace JavaToCSharp;

/// <summary>
/// Converts Java pattern expressions (type patterns, record patterns, match-all) to C# pattern syntax.
/// </summary>
internal static class PatternConversion
{
    public static ExpressionSyntax PatternToIsCondition(
        ConversionContext context,
        ExpressionSyntax target,
        PatternExpr pattern)
    {
        return SyntaxFactory.IsPatternExpression(target, ToPatternSyntax(context, pattern));
    }

    public static PatternSyntax ToPatternSyntax(ConversionContext context, ComponentPatternExpr pattern)
    {
        if (pattern is MatchAllPatternExpr)
        {
            return SyntaxFactory.DiscardPattern();
        }

        if (pattern is TypePatternExpr typePattern)
        {
            return TypePatternToSyntax(context, typePattern);
        }

        if (pattern is RecordPatternExpr recordPattern)
        {
            return RecordPatternToSyntax(context, recordPattern);
        }

        throw new InvalidOperationException($"Unsupported pattern type: {pattern.getClass().getName()}");
    }

    private static PatternSyntax TypePatternToSyntax(ConversionContext context, TypePatternExpr typePattern)
    {
        var typeName = ConvertPatternType(context, typePattern.getType());
        string name = TypeHelper.EscapeIdentifier(typePattern.getNameAsString());

        return SyntaxFactory.DeclarationPattern(
            SyntaxFactory.ParseTypeName(typeName),
            SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier(name)));
    }

    private static PatternSyntax RecordPatternToSyntax(ConversionContext context, RecordPatternExpr recordPattern)
    {
        var typeName = ConvertPatternType(context, recordPattern.getType());
        var nested = recordPattern.getPatternList().ToList<ComponentPatternExpr>() ?? [];

        var subpatterns = nested
            .Select(p => SyntaxFactory.Subpattern(pattern: ToPatternSyntax(context, p)))
            .ToArray();

        var recursive = SyntaxFactory.RecursivePattern()
            .WithType(SyntaxFactory.ParseTypeName(typeName));

        if (subpatterns.Length > 0)
        {
            recursive = recursive.WithPositionalPatternClause(
                SyntaxFactory.PositionalPatternClause(
                    SyntaxFactory.SeparatedList(subpatterns)));
        }

        return recursive;
    }

    private static string ConvertPatternType(ConversionContext context, Type type)
    {
        var typeName = TypeHelper.ConvertType(type.toString());
        return TypeHelper.ReplaceWildcardsWithTypeParameters(typeName, context.TypeParameterNames);
    }
}
