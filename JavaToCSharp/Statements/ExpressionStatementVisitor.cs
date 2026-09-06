using com.github.javaparser.ast.body;
using com.github.javaparser.ast.expr;
using com.github.javaparser.ast.stmt;
using JavaToCSharp.Expressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Statements;

public class ExpressionStatementVisitor : StatementVisitor<ExpressionStmt>
{
    public override StatementSyntax? Visit(ConversionContext context, ExpressionStmt exprStmt)
    {
        var expression = exprStmt.getExpression();

        // handle special case where AST is different
        if (expression is VariableDeclarationExpr expr)
        {
            return VisitVariableDeclarationStatement(context, expr);
        }

        if (expression is AssignExpr assignExpr
            && assignExpr.getOperator() == AssignExpr.Operator.ASSIGN
            && assignExpr.getValue() is SwitchExpr switchExpr)
        {
            var target = ExpressionVisitor.VisitExpression(context, assignExpr.getTarget())
                         ?? throw new InvalidOperationException("Assignment target cannot be null");

            return SwitchConversion.ConvertSwitchExpression(
                context,
                switchExpr,
                value =>
                [
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            target,
                            value))
                ]);
        }

        if (expression is SwitchExpr loneSwitch)
        {
            // Switch expression used as a statement — execute arms for side effects / throws
            return SwitchConversion.ConvertSwitchExpression(
                context,
                loneSwitch,
                value => [SyntaxFactory.ExpressionStatement(value)]);
        }

        var expressionSyntax = ExpressionVisitor.VisitExpression(context, expression);

        return expressionSyntax is null ? null : SyntaxFactory.ExpressionStatement(expressionSyntax);
    }

    private static StatementSyntax VisitVariableDeclarationStatement(ConversionContext context, VariableDeclarationExpr varExpr)
    {
        var commonType = varExpr.getCommonType();
        int? arrayRank = null;

        var variables = new List<VariableDeclaratorSyntax>();

        var variableDeclarators = varExpr.getVariables()?.ToList<VariableDeclarator>() ?? [];

        // Special-case: single variable initialized from a switch expression
        if (variableDeclarators.Count == 1)
        {
            var item = variableDeclarators[0];
            var initExpr = item.getInitializer().FromOptional<Expression>();
            if (initExpr is SwitchExpr switchExpr)
            {
                var type = item.getType();
                arrayRank = type.getArrayLevel();
                string name = item.getNameAsString();
                context.DeclareVariable(name, item.getType().toString());

                var typeSyntax = TypeHelper.ConvertTypeSyntax(context, commonType, arrayRank ?? 0);

                // Convert first so selector hoist (if any) is queued before the declaration
                var ifElse = SwitchConversion.ConvertSwitchExpression(
                    context,
                    switchExpr,
                    value =>
                    [
                        SyntaxFactory.ExpressionStatement(
                            SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                SyntaxFactory.IdentifierName(name),
                                value))
                    ]);

                context.PendingStatements.Add(
                    SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(typeSyntax)
                            .AddVariables(SyntaxFactory.VariableDeclarator(name))));

                return ifElse;
            }
        }

        foreach (var item in variableDeclarators)
        {
            var type = item.getType();

            if (arrayRank is not null && type.getArrayLevel() != arrayRank)
            {
                throw new InvalidOperationException("Different array levels in the same field declaration are not yet supported");
            }

            arrayRank ??= type.getArrayLevel();

            string name = item.getNameAsString();

            context.DeclareVariable(name, item.getType().toString());

            var initExpr = item.getInitializer().FromOptional<Expression>();

            if (initExpr is not null)
            {
                var initSyntax = ExpressionVisitor.VisitExpression(context, initExpr);
                if (initSyntax is not null)
                {
                    var varDeclarationSyntax = SyntaxFactory.VariableDeclarator(TypeHelper.EscapeIdentifier(name)).WithInitializer(SyntaxFactory.EqualsValueClause(initSyntax));
                    variables.Add(varDeclarationSyntax);
                }
            }
            else
            {
                variables.Add(SyntaxFactory.VariableDeclarator(TypeHelper.EscapeIdentifier(name)));
            }
        }

        var typeSyntaxFinal = TypeHelper.ConvertTypeSyntax(context, commonType, arrayRank ?? 0);

        return SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(typeSyntaxFinal, SyntaxFactory.SeparatedList(variables, Enumerable.Repeat(SyntaxFactory.Token(SyntaxKind.CommaToken), Math.Max(variables.Count - 1, 0)))));
    }
}
