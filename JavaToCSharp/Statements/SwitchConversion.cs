using com.github.javaparser.ast.expr;
using com.github.javaparser.ast.stmt;
using JavaToCSharp.Expressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Statements;

/// <summary>
/// Converts Java <c>switch</c> statements and expressions into C# <c>if / else-if / else</c> chains.
/// </summary>
public static class SwitchConversion
{
    public const string ConversionComment = "// Java Switch has been converted into if-else chain";

    public static StatementSyntax ConvertSwitchStatement(ConversionContext context, SwitchStmt switchStmt)
    {
        var selectorSyntax = ExpressionVisitor.VisitExpression(context, switchStmt.getSelector())
                             ?? throw new InvalidOperationException("Switch selector cannot be null");

        var entries = switchStmt.getEntries().ToList<SwitchEntry>() ?? [];
        var (selectorExpr, hoist) = PossiblyHoistSelector(context, selectorSyntax);

        var arms = BuildStatementArms(context, entries, selectorExpr);
        var ifElse = WithConversionComment(BuildIfElseChain(arms));

        if (hoist is not null)
        {
            context.PendingStatements.Add(hoist);
        }

        return ifElse;
    }

    /// <summary>
    /// Converts a switch expression, using <paramref name="resultSink"/> to emit how each yielded/result value is consumed
    /// (assignment, return, etc.).
    /// </summary>
    public static StatementSyntax ConvertSwitchExpression(
        ConversionContext context,
        SwitchExpr switchExpr,
        Func<ExpressionSyntax, IEnumerable<StatementSyntax>> resultSink)
    {
        var selectorSyntax = ExpressionVisitor.VisitExpression(context, switchExpr.getSelector())
                             ?? throw new InvalidOperationException("Switch expression selector cannot be null");

        var entries = switchExpr.getEntries().ToList<SwitchEntry>() ?? [];
        var (selectorExpr, hoist) = PossiblyHoistSelector(context, selectorSyntax);

        var arms = BuildExpressionArms(context, entries, selectorExpr, resultSink);
        var ifElse = WithConversionComment(BuildIfElseChain(arms));

        if (hoist is not null)
        {
            context.PendingStatements.Add(hoist);
        }

        return ifElse;
    }

    /// <summary>
    /// Converts a switch expression into pending statements that assign into a temporary, and returns that temporary.
    /// </summary>
    public static ExpressionSyntax ConvertSwitchExpressionToTemp(ConversionContext context, SwitchExpr switchExpr)
    {
        string tempName = context.NextTempName("__switch");
        var typeSyntax = InferResultType(switchExpr) ?? SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));

        var ifElse = ConvertSwitchExpression(
            context,
            switchExpr,
            value =>
            [
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(tempName),
                        value))
            ]);

        // Pending may already contain a selector hoist from ConvertSwitchExpression
        context.PendingStatements.Add(
            SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(typeSyntax)
                    .AddVariables(SyntaxFactory.VariableDeclarator(tempName))));
        context.PendingStatements.Add(ifElse);
        return SyntaxFactory.IdentifierName(tempName);
    }

    private static List<SwitchArm> BuildStatementArms(
        ConversionContext context,
        IReadOnlyList<SwitchEntry> entries,
        ExpressionSyntax selectorExpr)
    {
        var arms = new List<SwitchArm>();
        bool hasArrowEntries = entries.Any(e => e.getType() != SwitchEntry.Type.STATEMENT_GROUP);

        if (hasArrowEntries)
        {
            // Arrow switches never fall through
            foreach (var entry in entries)
            {
                var condition = BuildCondition(context, selectorExpr, entry);
                var body = ConvertArrowOrStatementBody(context, entry, resultSink: null);
                arms.Add(new SwitchArm(condition, body));
            }

            return arms;
        }

        // Classic colon switches: resolve multi-label grouping and fall-through
        int i = 0;
        while (i < entries.Count)
        {
            var labels = entries[i].getLabels().ToList<Expression>() ?? [];
            if (labels.Count == 0)
            {
                arms.Add(new SwitchArm(null, ConvertFallThroughBody(context, entries, i)));
                break;
            }

            // Gather consecutive empty case labels into one OR-condition, ending at the
            // first entry that has a body (or the last entry).
            var collectedLabels = new List<Expression>();
            int bodyEntryIndex = i;

            while (bodyEntryIndex < entries.Count)
            {
                var current = entries[bodyEntryIndex];
                var currentLabels = current.getLabels().ToList<Expression>() ?? [];
                if (currentLabels.Count == 0)
                {
                    break;
                }

                collectedLabels.AddRange(currentLabels);

                var stmts = current.getStatements().ToList<Statement>() ?? [];
                if (stmts.Count > 0 || bodyEntryIndex == entries.Count - 1)
                {
                    break;
                }

                bodyEntryIndex++;
            }

            arms.Add(new SwitchArm(
                BuildOrCondition(context, selectorExpr, collectedLabels),
                ConvertFallThroughBody(context, entries, bodyEntryIndex)));

            i = bodyEntryIndex + 1;
        }

        return arms;
    }

    private static List<SwitchArm> BuildExpressionArms(
        ConversionContext context,
        IReadOnlyList<SwitchEntry> entries,
        ExpressionSyntax selectorExpr,
        Func<ExpressionSyntax, IEnumerable<StatementSyntax>> resultSink)
    {
        var arms = new List<SwitchArm>();

        foreach (var entry in entries)
        {
            var condition = BuildCondition(context, selectorExpr, entry);
            var body = ConvertArrowOrStatementBody(context, entry, resultSink);
            arms.Add(new SwitchArm(condition, body));
        }

        return arms;
    }

    private static List<StatementSyntax> ConvertFallThroughBody(
        ConversionContext context,
        IReadOnlyList<SwitchEntry> entries,
        int startIndex)
    {
        var result = new List<StatementSyntax>();

        for (int j = startIndex; j < entries.Count; j++)
        {
            var statements = entries[j].getStatements().ToList<Statement>() ?? [];
            foreach (var statement in statements)
            {
                if (statement is BreakStmt)
                {
                    return result;
                }

                result.AddRange(ConvertCaseStatement(context, statement));

                if (statement is ReturnStmt or ThrowStmt or ContinueStmt)
                {
                    return result;
                }
            }
        }

        return result;
    }

    private static List<StatementSyntax> ConvertArrowOrStatementBody(
        ConversionContext context,
        SwitchEntry entry,
        Func<ExpressionSyntax, IEnumerable<StatementSyntax>>? resultSink)
    {
        var type = entry.getType();
        var statements = entry.getStatements().ToList<Statement>() ?? [];

        if (type == SwitchEntry.Type.EXPRESSION)
        {
            if (statements.Count == 0)
            {
                return [];
            }

            if (resultSink is not null && statements[0] is ExpressionStmt exprStmt)
            {
                var value = ExpressionVisitor.VisitExpression(context, exprStmt.getExpression())
                            ?? throw new InvalidOperationException("Switch arm expression cannot be null");
                var pending = context.DrainPendingStatements();
                return pending.Concat(resultSink(value)).ToList();
            }

            return ConvertCaseStatement(context, statements[0]).ToList();
        }

        if (type == SwitchEntry.Type.THROWS_STATEMENT)
        {
            if (statements.Count == 0)
            {
                return [];
            }

            return ConvertCaseStatement(context, statements[0]).ToList();
        }

        if (type == SwitchEntry.Type.BLOCK)
        {
            if (statements.Count == 1 && statements[0] is BlockStmt block)
            {
                return ConvertBlockStatements(context, block, resultSink);
            }

            return statements.SelectMany(s => ConvertBlockOrYieldStatement(context, s, resultSink)).ToList();
        }

        // STATEMENT_GROUP
        var result = new List<StatementSyntax>();
        foreach (var statement in statements)
        {
            if (statement is BreakStmt)
            {
                break;
            }

            result.AddRange(ConvertCaseStatement(context, statement));
        }

        return result;
    }

    private static List<StatementSyntax> ConvertBlockStatements(
        ConversionContext context,
        BlockStmt block,
        Func<ExpressionSyntax, IEnumerable<StatementSyntax>>? resultSink)
    {
        var statements = block.getStatements().ToList<Statement>() ?? [];
        var result = new List<StatementSyntax>();

        foreach (var statement in statements)
        {
            result.AddRange(ConvertBlockOrYieldStatement(context, statement, resultSink));
        }

        return result;
    }

    private static IEnumerable<StatementSyntax> ConvertBlockOrYieldStatement(
        ConversionContext context,
        Statement statement,
        Func<ExpressionSyntax, IEnumerable<StatementSyntax>>? resultSink)
    {
        if (statement is YieldStmt yieldStmt)
        {
            if (resultSink is null)
            {
                throw new InvalidOperationException("yield is only valid inside a switch expression");
            }

            var value = ExpressionVisitor.VisitExpression(context, yieldStmt.getExpression())
                        ?? throw new InvalidOperationException("yield expression cannot be null");
            var pending = context.DrainPendingStatements();
            return pending.Concat(resultSink(value));
        }

        return ConvertCaseStatement(context, statement);
    }

    private static IEnumerable<StatementSyntax> ConvertCaseStatement(
        ConversionContext context,
        Statement statement)
    {
        var converted = StatementVisitor.VisitStatement(context, statement);
        if (converted is null)
        {
            return [];
        }

        // Flatten synthetic pending-carrier blocks produced by VisitStatement
        if (converted is BlockSyntax block && statement is not BlockStmt)
        {
            return block.Statements;
        }

        return [converted];
    }

    private static ExpressionSyntax? BuildCondition(
        ConversionContext context,
        ExpressionSyntax selectorExpr,
        SwitchEntry entry)
    {
        var labels = entry.getLabels().ToList<Expression>() ?? [];
        if (labels.Count == 0)
        {
            return null; // default
        }

        return BuildOrCondition(context, selectorExpr, labels);
    }

    private static ExpressionSyntax BuildOrCondition(
        ConversionContext context,
        ExpressionSyntax selectorExpr,
        IReadOnlyList<Expression> labels)
    {
        ExpressionSyntax? combined = null;

        foreach (var label in labels)
        {
            var part = LabelToCondition(context, selectorExpr, label);
            combined = combined is null
                ? part
                : SyntaxFactory.BinaryExpression(SyntaxKind.LogicalOrExpression, combined, part);
        }

        return combined
               ?? throw new InvalidOperationException("Switch case must have at least one label");
    }

    internal static ExpressionSyntax LabelToCondition(
        ConversionContext context,
        ExpressionSyntax selectorExpr,
        Expression label)
    {
        if (label is PatternExpr pattern)
        {
            var typeName = TypeHelper.ConvertType(pattern.getType().toString());
            typeName = TypeHelper.ReplaceWildcardsWithTypeParameters(typeName, context.TypeParameterNames);

            ExpressionSyntax patternCondition = SyntaxFactory.IsPatternExpression(
                selectorExpr,
                SyntaxFactory.DeclarationPattern(
                    SyntaxFactory.ParseTypeName(typeName),
                    SyntaxFactory.SingleVariableDesignation(
                        SyntaxFactory.Identifier(TypeHelper.EscapeIdentifier(pattern.getNameAsString())))));

            return patternCondition;
        }

        if (label is NullLiteralExpr)
        {
            return SyntaxFactory.BinaryExpression(
                SyntaxKind.EqualsExpression,
                selectorExpr,
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
        }

        ExpressionSyntax labelExpr;
        if (SwitchExpressionVisitor.TryGetEnumConstantExpression(context, label, out var enumConstant))
        {
            labelExpr = enumConstant;
        }
        else
        {
            labelExpr = ExpressionVisitor.VisitExpression(context, label)
                        ?? throw new InvalidOperationException("Switch case label cannot be null");
        }

        return SyntaxFactory.BinaryExpression(
            SyntaxKind.EqualsExpression,
            selectorExpr,
            labelExpr);
    }

    private static IfStatementSyntax BuildIfElseChain(IReadOnlyList<SwitchArm> arms)
    {
        if (arms.Count == 0)
        {
            return SyntaxFactory.IfStatement(
                SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression),
                SyntaxFactory.Block());
        }

        StatementSyntax? currentElse = null;

        for (int i = arms.Count - 1; i >= 0; i--)
        {
            var arm = arms[i];
            var body = SyntaxFactory.Block(arm.Body);

            if (arm.Condition is null)
            {
                currentElse = body;
                continue;
            }

            currentElse = currentElse is null
                ? SyntaxFactory.IfStatement(arm.Condition, body)
                : SyntaxFactory.IfStatement(arm.Condition, body, SyntaxFactory.ElseClause(currentElse));
        }

        if (currentElse is IfStatementSyntax ifStmt)
        {
            return ifStmt;
        }

        // Only a default arm
        return SyntaxFactory.IfStatement(
            SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression),
            currentElse ?? SyntaxFactory.Block());
    }

    private static (ExpressionSyntax Selector, StatementSyntax? Hoist) PossiblyHoistSelector(
        ConversionContext context,
        ExpressionSyntax selectorSyntax)
    {
        if (selectorSyntax is IdentifierNameSyntax or LiteralExpressionSyntax)
        {
            return (selectorSyntax, null);
        }

        string tempName = context.NextTempName("__sel");
        var decl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                .AddVariables(
                    SyntaxFactory.VariableDeclarator(tempName)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(selectorSyntax))));

        return (SyntaxFactory.IdentifierName(tempName), decl);
    }

    private static TypeSyntax? InferResultType(SwitchExpr switchExpr)
    {
        var entries = switchExpr.getEntries().ToList<SwitchEntry>() ?? [];
        TypeSyntax? inferred = null;

        foreach (var entry in entries)
        {
            foreach (var candidate in EnumerateResultExpressions(entry))
            {
                var type = InferTypeFromExpression(candidate);
                if (type is null)
                {
                    continue;
                }

                if (inferred is null)
                {
                    inferred = type;
                }
                else if (!inferred.IsEquivalentTo(type))
                {
                    return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
                }
            }
        }

        return inferred;
    }

    private static IEnumerable<Expression> EnumerateResultExpressions(SwitchEntry entry)
    {
        var statements = entry.getStatements().ToList<Statement>() ?? [];
        var type = entry.getType();

        if (type == SwitchEntry.Type.BLOCK && statements.Count == 1 && statements[0] is BlockStmt block)
        {
            foreach (var stmt in block.getStatements().ToList<Statement>() ?? [])
            {
                if (stmt is YieldStmt yieldStmt)
                {
                    yield return yieldStmt.getExpression();
                }
            }

            yield break;
        }

        foreach (var stmt in statements)
        {
            switch (stmt)
            {
                case YieldStmt yieldStmt:
                    yield return yieldStmt.getExpression();
                    break;
                case ExpressionStmt exprStmt:
                    yield return exprStmt.getExpression();
                    break;
                case BlockStmt inner:
                    foreach (var nested in inner.getStatements().ToList<Statement>() ?? [])
                    {
                        if (nested is YieldStmt y)
                        {
                            yield return y.getExpression();
                        }
                        else if (nested is ExpressionStmt e)
                        {
                            yield return e.getExpression();
                        }
                    }

                    break;
            }
        }
    }

    private static TypeSyntax? InferTypeFromExpression(Expression expr) =>
        expr switch
        {
            IntegerLiteralExpr => SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
            LongLiteralExpr => SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.LongKeyword)),
            DoubleLiteralExpr d when d.getValue().EndsWith("f", StringComparison.OrdinalIgnoreCase)
                || d.getValue().EndsWith("F")
                => SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.FloatKeyword)),
            DoubleLiteralExpr => SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.DoubleKeyword)),
            StringLiteralExpr => SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
            BooleanLiteralExpr => SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)),
            CharLiteralExpr => SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.CharKeyword)),
            NullLiteralExpr => null,
            _ => null
        };

    internal static TSyntax WithConversionComment<TSyntax>(TSyntax syntax)
        where TSyntax : SyntaxNode
    {
        var trivia = syntax.GetLeadingTrivia()
            .Insert(0, SyntaxFactory.CarriageReturnLineFeed)
            .Insert(0, SyntaxFactory.Comment(ConversionComment));
        return syntax.WithLeadingTrivia(trivia);
    }

    private sealed record SwitchArm(ExpressionSyntax? Condition, List<StatementSyntax> Body);
}
