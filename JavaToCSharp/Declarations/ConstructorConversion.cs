using com.github.javaparser.ast.expr;
using com.github.javaparser.ast.stmt;
using JavaToCSharp.Expressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Declarations;

/// <summary>
/// Shared helpers for converting Java constructors, including flexible constructor bodies
/// (statements before <c>this(...)</c> / <c>super(...)</c>).
/// </summary>
internal static class ConstructorConversion
{
    /// <summary>
    /// Finds an explicit constructor invocation anywhere in the body, builds a C# initializer,
    /// and returns the remaining statements (prologue moved after the invoke for C# ordering).
    /// </summary>
    /// <param name="prependArgs">Optional arguments prepended to <c>this(...)</c>/<c>base(...)</c> (e.g. enum name/ordinal).</param>
    public static (ConstructorInitializerSyntax? Initializer, List<Statement> RemainingStatements)
        ExtractInitializer(
            ConversionContext context,
            List<Statement> statements,
            IReadOnlyList<ArgumentSyntax>? prependArgs = null)
    {
        int invokeIndex = statements.FindIndex(static s => s is ExplicitConstructorInvocationStmt);
        if (invokeIndex < 0)
        {
            return (null, statements);
        }

        var ctorInvStmt = (ExplicitConstructorInvocationStmt)statements[invokeIndex];

        if (invokeIndex > 0)
        {
            context.Warning(
                "Statements before this()/super() were moved after the C# constructor initializer.",
                ctorInvStmt.getBegin().FromOptional<com.github.javaparser.Position>()?.line ?? -1);
        }

        var argSyntaxes = new List<ArgumentSyntax>();
        if (prependArgs is { Count: > 0 })
        {
            argSyntaxes.AddRange(prependArgs);
        }

        var initArgs = ctorInvStmt.getArguments();
        if (initArgs is not null && initArgs.size() > 0)
        {
            foreach (var arg in initArgs.OfType<Expression>())
            {
                // Use `default` instead of `null` in constructor initializers so generic
                // type parameters (common in records) convert cleanly.
                ExpressionSyntax? argSyntax = arg is NullLiteralExpr
                    ? SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)
                    : ExpressionVisitor.VisitExpression(context, arg);

                if (argSyntax is not null)
                {
                    argSyntaxes.Add(SyntaxFactory.Argument(argSyntax));
                }
            }
        }

        ArgumentListSyntax? argsSyntax = argSyntaxes.Count > 0
            ? SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(argSyntaxes))
            : null;

        var initializer = SyntaxFactory.ConstructorInitializer(
            ctorInvStmt.isThis()
                ? SyntaxKind.ThisConstructorInitializer
                : SyntaxKind.BaseConstructorInitializer,
            argsSyntax);

        var remaining = new List<Statement>(statements.Count - 1);
        for (int i = 0; i < statements.Count; i++)
        {
            if (i == invokeIndex)
            {
                continue;
            }

            remaining.Add(statements[i]);
        }

        return (initializer, remaining);
    }
}
