using com.github.javaparser.ast.expr;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Ast = com.github.javaparser.ast;
using Type = com.github.javaparser.ast.type.Type;

namespace JavaToCSharp.Expressions;

public class MethodCallExpressionVisitor : ExpressionVisitor<MethodCallExpr>
{
    protected override ExpressionSyntax? Visit(ConversionContext context, MethodCallExpr methodCallExpr)
    {
        if (!context.PreserveJavaMethodNames
            && TypeHelper.TryTransformMethodCall(context, methodCallExpr, out var transformedSyntax))
        {
            return transformedSyntax;
        }

        var scope = methodCallExpr.getScope().FromOptional<Expression>();
        ExpressionSyntax? scopeSyntax = null;

        var methodName = context.PreserveJavaMethodNames
            ? methodCallExpr.getNameAsString()
            : TypeHelper.Capitalize(methodCallExpr.getNameAsString());
        methodName = TypeHelper.ReplaceCommonMethodNames(methodName);

        if (scope is not null)
        {
            scopeSyntax = VisitExpression(context, scope);

            if (context.Options.ConvertSystemOutToConsole
                && scope is FieldAccessExpr fieldAccessExpr
                && fieldAccessExpr.getScope() is NameExpr nameExpr
                && nameExpr.getNameAsString() == "System"
                && fieldAccessExpr.getNameAsString() == "out"
                && scopeSyntax is IdentifierNameSyntax { Identifier: { Text: "Console" } })
            {
                if (methodCallExpr.getNameAsString() == "println")
                {
                    methodName = "WriteLine";
                }
                else if (methodCallExpr.getNameAsString() == "print")
                {
                    methodName = "Write";
                }
            }
        }

        // Override methodName if a mapping is found
        if (TryGetMappedMethodName(methodCallExpr.getNameAsString(), scope, context, out var mappedMethodName))
        {
            methodName = mappedMethodName;
        }

        SimpleNameSyntax methodSimpleName = CreateMethodName(methodCallExpr, methodName);

        ExpressionSyntax methodExpression;

        if (scopeSyntax is null)
        {
            methodExpression = methodSimpleName;
        }
        else
        {
            methodExpression = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                scopeSyntax,
                methodSimpleName);
        }

        var args = methodCallExpr.getArguments();

        if (args is null || args.size() == 0)
        {
            return SyntaxFactory.InvocationExpression(methodExpression);
        }

        return SyntaxFactory.InvocationExpression(methodExpression, TypeHelper.GetSyntaxFromArguments(context, args));
    }

    private static SimpleNameSyntax CreateMethodName(MethodCallExpr methodCallExpr, string methodName)
    {
        var typeArgs = methodCallExpr.getTypeArguments().FromOptional<Ast.NodeList>()?.ToList<Type>();
        if (typeArgs is not { Count: > 0 })
        {
            return SyntaxFactory.IdentifierName(methodName);
        }

        var typeArgSyntaxes = typeArgs
            .Select(t => SyntaxFactory.ParseTypeName(TypeHelper.ConvertType(t.toString())))
            .ToArray();

        return SyntaxFactory.GenericName(methodName)
            .AddTypeArgumentListArguments(typeArgSyntaxes);
    }

    private static bool TryGetMappedMethodName(string methodName, Expression? scope, ConversionContext context, out string mappedMethodName)
    {
        var mappings = context.Options.SyntaxMappings;
        if (scope is null && mappings.VoidMethodMappings.TryGetValue(methodName, out var voidMapping))
        {
            mappedMethodName = voidMapping;
            return true;
        }
        else if (scope is not null && mappings.NonVoidMethodMappings.TryGetValue(methodName, out var nonVoidMapping))
        {
            mappedMethodName = nonVoidMapping;
            return true;
        }
        mappedMethodName = methodName;
        return false;
    }
}
