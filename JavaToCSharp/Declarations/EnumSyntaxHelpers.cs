using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Declarations;

/// <summary>
/// Builds the identity-based (reference equality) syntax members used by converted Java enums.
/// These are: Equals, GetHashCode, ==, != operators, name(), ordinal(), values(), valueOf().
/// </summary>
internal static class EnumSyntaxHelpers
{
    public static MethodDeclarationSyntax CreateReferenceEqualsMethod()
    {
        return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)),
                "Equals")
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword))
            .AddParameterListParameters(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("obj"))
                    .WithType(SyntaxFactory.NullableType(
                        SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)))))
            .WithExpressionBody(
                SyntaxFactory.ArrowExpressionClause(
                    SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName("ReferenceEquals"))
                        .WithArgumentList(
                            SyntaxFactory.ArgumentList(
                                SyntaxFactory.SeparatedList(new[]
                                {
                                    SyntaxFactory.Argument(SyntaxFactory.ThisExpression()),
                                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName("obj"))
                                })))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    public static MethodDeclarationSyntax CreateReferenceGetHashCodeMethod()
    {
        return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                "GetHashCode")
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword))
            .WithExpressionBody(
                SyntaxFactory.ArrowExpressionClause(
                    SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.ParseName("System.Runtime.CompilerServices.RuntimeHelpers"),
                                SyntaxFactory.IdentifierName("GetHashCode")))
                        .WithArgumentList(
                            SyntaxFactory.ArgumentList(
                                SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.Argument(SyntaxFactory.ThisExpression()))))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    public static OperatorDeclarationSyntax CreateReferenceEqualityOperator(string typeName)
    {
        var typeSyntax = SyntaxFactory.NullableType(SyntaxFactory.ParseTypeName(typeName));
        return SyntaxFactory.OperatorDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)),
                SyntaxFactory.Token(SyntaxKind.EqualsEqualsToken))
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword))
            .AddParameterListParameters(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("a")).WithType(typeSyntax),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("b")).WithType(typeSyntax))
            .WithExpressionBody(
                SyntaxFactory.ArrowExpressionClause(
                    SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName("ReferenceEquals"))
                        .WithArgumentList(
                            SyntaxFactory.ArgumentList(
                                SyntaxFactory.SeparatedList(new[]
                                {
                                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName("a")),
                                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName("b"))
                                })))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    public static OperatorDeclarationSyntax CreateReferenceInequalityOperator(string typeName)
    {
        var typeSyntax = SyntaxFactory.NullableType(SyntaxFactory.ParseTypeName(typeName));
        return SyntaxFactory.OperatorDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)),
                SyntaxFactory.Token(SyntaxKind.ExclamationEqualsToken))
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword))
            .AddParameterListParameters(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("a")).WithType(typeSyntax),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("b")).WithType(typeSyntax))
            .WithExpressionBody(
                SyntaxFactory.ArrowExpressionClause(
                    SyntaxFactory.PrefixUnaryExpression(
                        SyntaxKind.LogicalNotExpression,
                        SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName("ReferenceEquals"))
                            .WithArgumentList(
                                SyntaxFactory.ArgumentList(
                                    SyntaxFactory.SeparatedList(new[]
                                    {
                                        SyntaxFactory.Argument(SyntaxFactory.IdentifierName("a")),
                                        SyntaxFactory.Argument(SyntaxFactory.IdentifierName("b"))
                                    }))))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    public static MethodDeclarationSyntax CreateNameMethod()
    {
        return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
                "name")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .WithExpressionBody(
                SyntaxFactory.ArrowExpressionClause(
                    SyntaxFactory.IdentifierName("_name")))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    public static MethodDeclarationSyntax CreateOrdinalMethod()
    {
        return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                "ordinal")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .WithExpressionBody(
                SyntaxFactory.ArrowExpressionClause(
                    SyntaxFactory.IdentifierName("_ordinal")))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    public static MethodDeclarationSyntax CreateValuesMethod(
        TypeSyntax enumTypeSyntax,
        IReadOnlyList<string> constantNames)
    {
        var arrayType = SyntaxFactory.ArrayType(enumTypeSyntax)
            .AddRankSpecifiers(
                SyntaxFactory.ArrayRankSpecifier(
                    SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                        SyntaxFactory.OmittedArraySizeExpression())));

        var initials = constantNames
            .Select(name => (ExpressionSyntax)SyntaxFactory.IdentifierName(name))
            .ToArray();

        var arrayCreation = SyntaxFactory.ArrayCreationExpression(arrayType)
            .WithInitializer(
                SyntaxFactory.InitializerExpression(
                    SyntaxKind.ArrayInitializerExpression,
                    SyntaxFactory.SeparatedList(initials)));

        return SyntaxFactory.MethodDeclaration(arrayType, "values")
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(arrayCreation))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    public static MethodDeclarationSyntax CreateValueOfMethod(
        TypeSyntax enumTypeSyntax,
        string enumName,
        IReadOnlyList<string> constantNames)
    {
        StatementSyntax? elseStatement = SyntaxFactory.ThrowStatement(
            SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.ParseTypeName("ArgumentException"))
                .WithArgumentList(
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(
                                SyntaxFactory.InterpolatedStringExpression(
                                    SyntaxFactory.Token(SyntaxKind.InterpolatedStringStartToken),
                                    SyntaxFactory.List(new InterpolatedStringContentSyntax[]
                                    {
                                        SyntaxFactory.InterpolatedStringText(
                                            SyntaxFactory.Token(
                                                SyntaxTriviaList.Empty,
                                                SyntaxKind.InterpolatedStringTextToken,
                                                $"No enum constant {enumName}.",
                                                $"No enum constant {enumName}.",
                                                SyntaxTriviaList.Empty)),
                                        SyntaxFactory.Interpolation(SyntaxFactory.IdentifierName("name"))
                                    })))))));

        for (int i = constantNames.Count - 1; i >= 0; i--)
        {
            string constantName = constantNames[i];
            var body = SyntaxFactory.Block(
                SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(constantName)));
            var condition = SyntaxFactory.BinaryExpression(
                SyntaxKind.EqualsExpression,
                SyntaxFactory.IdentifierName("name"),
                SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(constantName)));

            elseStatement = SyntaxFactory.IfStatement(condition, body, SyntaxFactory.ElseClause(elseStatement));
        }

        return SyntaxFactory.MethodDeclaration(enumTypeSyntax, "valueOf")
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword))
            .AddParameterListParameters(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("name"))
                    .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword))))
            .WithBody(SyntaxFactory.Block(elseStatement));
    }
}
