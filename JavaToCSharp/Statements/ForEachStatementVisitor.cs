using com.github.javaparser.ast.body;
using com.github.javaparser.ast.stmt;
using JavaToCSharp.Expressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Statements;

public class ForEachStatementVisitor : StatementVisitor<ForEachStmt>
{
    public override StatementSyntax? Visit(ConversionContext context, ForEachStmt foreachStmt)
    {
        var iterableExpr = foreachStmt.getIterable();
        var iterableSyntax = ExpressionVisitor.VisitExpression(context, iterableExpr);
        if (iterableSyntax is null)
        {
            return null;
        }

        var varExpr = foreachStmt.getVariable();
        var varType = varExpr.getCommonType();
        var type = TypeHelper.ConvertType(context, varType);

        var variableDeclarators = varExpr.getVariables()?.ToList<VariableDeclarator>()?? [];
        var vars = variableDeclarators
                   .Select(i => SyntaxFactory.VariableDeclarator(
                       SyntaxFactory.Identifier(TypeHelper.EscapeIdentifier(i.getNameAsString()))))
                   .ToArray();

        var body = foreachStmt.getBody();

        context.PushVariableScope();

        try
        {
            foreach (var declarator in variableDeclarators)
            {
                context.DeclareVariable(declarator.getNameAsString(), varExpr.getCommonType().toString());
            }

            var bodySyntax = VisitStatement(context, body);

            return bodySyntax is null
                ? null
                : SyntaxFactory.ForEachStatement(SyntaxFactory.ParseTypeName(type), vars[0].Identifier.ValueText, iterableSyntax, bodySyntax);
        }
        finally
        {
            context.PopVariableScope();
        }
    }
}
