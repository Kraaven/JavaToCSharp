using com.github.javaparser.ast.body;
using com.github.javaparser.ast.expr;
using JavaToCSharp.Statements;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Expressions;

public class LambdaExpressionVisitor : ExpressionVisitor<LambdaExpr>
{
    protected override ExpressionSyntax? Visit(ConversionContext context, LambdaExpr expr)
    {
        var parameters = expr.getParameters().ToList<Parameter>() ?? [];

        context.PushVariableScope();

        try
        {
            foreach (var param in parameters)
            {
                context.DeclareVariable(param.getNameAsString(), param.getType().toString());
            }

            var bodyStatement = expr.getBody();
            var bodyStatementSyntax = StatementVisitor.VisitStatement(context, bodyStatement);

            ParenthesizedLambdaExpressionSyntax? lambdaExpressionSyntax = null;

            if (bodyStatementSyntax is ExpressionStatementSyntax ess)
            {
                lambdaExpressionSyntax = SyntaxFactory.ParenthesizedLambdaExpression(ess.Expression);
            }
            else if(bodyStatementSyntax is not null)
            {
                lambdaExpressionSyntax = SyntaxFactory.ParenthesizedLambdaExpression(bodyStatementSyntax);
            }

            if (parameters is not { Count: > 0 })
                return lambdaExpressionSyntax;

            var paramSyntaxes = new List<ParameterSyntax>();

            foreach (var param in parameters)
            {
                var type = param.getType();
                int arrayLevel = type.getArrayLevel();
                string identifier = TypeHelper.EscapeIdentifier(param.getNameAsString());

                if (param.isVarArgs() && arrayLevel == 0)
                {
                    arrayLevel = 1;
                }

                var modifiers = SyntaxFactory.TokenList();

                if (param.isVarArgs())
                    modifiers = SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.ParamsKeyword));

                var paramSyntax = SyntaxFactory.Parameter(
                    attributeLists: [],
                    modifiers: modifiers,
                    type: TypeHelper.ConvertTypeSyntax(context, type, arrayLevel),
                    identifier: SyntaxFactory.Identifier(identifier),
                    @default: null);

                paramSyntaxes.Add(paramSyntax);
            }

            lambdaExpressionSyntax = lambdaExpressionSyntax?.AddParameterListParameters(paramSyntaxes.ToArray());

            return lambdaExpressionSyntax;
        }
        finally
        {
            context.PopVariableScope();
        }
    }
}
