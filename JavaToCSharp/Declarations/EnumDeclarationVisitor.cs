using com.github.javaparser.ast;
using com.github.javaparser.ast.body;
using com.github.javaparser.ast.expr;
using com.github.javaparser.ast.stmt;
using com.github.javaparser.ast.type;
using JavaToCSharp.Expressions;
using JavaToCSharp.Statements;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Parameter = com.github.javaparser.ast.body.Parameter;

namespace JavaToCSharp.Declarations;

/// <summary>
/// Converts Java enums to C# abstract/regular classes with static constant instances
/// and nested sealed classes per constant, so individually converted files can use them
/// without depending on C# <c>enum</c> semantics.
/// </summary>
public class EnumDeclarationVisitor : BodyDeclarationVisitor<EnumDeclaration>
{
    public override MemberDeclarationSyntax VisitForClass(
        ConversionContext context,
        ClassDeclarationSyntax? classSyntax,
        EnumDeclaration enumDecl,
        IReadOnlyList<ClassOrInterfaceType> extends,
        IReadOnlyList<ClassOrInterfaceType> implements)
    {
        return VisitEnumDeclaration(context, enumDecl);
    }

    public override MemberDeclarationSyntax VisitForInterface(
        ConversionContext context,
        InterfaceDeclarationSyntax interfaceSyntax,
        EnumDeclaration declaration)
    {
        return VisitEnumDeclaration(context, declaration);
    }

    public static ClassDeclarationSyntax VisitEnumDeclaration(
        ConversionContext context,
        EnumDeclaration enumDecl,
        bool isNested = false)
    {
        bool previousIsConvertingEnum = context.IsConvertingEnum;
        try
        {
            context.IsConvertingEnum = true;
            return VisitEnumDeclarationCore(context, enumDecl, isNested);
        }
        finally
        {
            context.IsConvertingEnum = previousIsConvertingEnum;
        }
    }

    private static ClassDeclarationSyntax VisitEnumDeclarationCore(
        ConversionContext context,
        EnumDeclaration enumDecl,
        bool isNested)
    {
        string name = enumDecl.getNameAsString();

        if (!isNested)
        {
            context.RootTypeName = name;
        }

        context.LastTypeName = name;

        var entries = enumDecl.getEntries().ToList<EnumConstantDeclaration>() ?? [];
        var members = enumDecl.getMembers()?.ToList<BodyDeclaration>() ?? [];

        bool hasAbstractMethod = members.OfType<MethodDeclaration>().Any(m => m.isAbstract());
        // Nested constant subclasses require a non-sealed base; use abstract when needed
        bool isAbstract = hasAbstractMethod || entries.Any(e => !e.getClassBody().isEmpty());

        var classSyntax = SyntaxFactory.ClassDeclaration(name);

        var mods = enumDecl.getModifiers().ToModifierKeywordSet();

        if (mods.Contains(Modifier.Keyword.PRIVATE))
            classSyntax = classSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));
        if (mods.Contains(Modifier.Keyword.PROTECTED))
            classSyntax = classSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));
        if (mods.Contains(Modifier.Keyword.PUBLIC))
            classSyntax = classSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
        if (isAbstract)
            classSyntax = classSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.AbstractKeyword));

        // Qualify switch labels / unqualified references for this enum's constants
        foreach (var entry in entries)
        {
            string constantName = entry.getNameAsString();
            context.EnumConstantsBySimpleName[constantName] = $"{name}.{constantName}";
        }

        TypeSyntax enumTypeSyntax = SyntaxFactory.ParseTypeName(name);

        // Static constant fields
        for (int ordinal = 0; ordinal < entries.Count; ordinal++)
        {
            var entry = entries[ordinal];
            string constantName = entry.getNameAsString();
            string nestedClassName = BuildNestedClassName(constantName, name);

            classSyntax = classSyntax.AddMembers(
                SyntaxFactory.FieldDeclaration(
                        SyntaxFactory.VariableDeclaration(enumTypeSyntax)
                            .AddVariables(
                                SyntaxFactory.VariableDeclarator(constantName)
                                    .WithInitializer(
                                        SyntaxFactory.EqualsValueClause(
                                            SyntaxFactory.ObjectCreationExpression(
                                                    SyntaxFactory.ParseTypeName(nestedClassName))
                                                .WithArgumentList(SyntaxFactory.ArgumentList())))))
                    .AddModifiers(
                        SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                        SyntaxFactory.Token(SyntaxKind.StaticKeyword),
                        SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)));
        }

        // Synthetic name / ordinal storage
        classSyntax = classSyntax.AddMembers(
            CreatePrivateReadonlyField("string", "_name"),
            CreatePrivateReadonlyField("int", "_ordinal"));

        var convertedFields = new List<MemberDeclarationSyntax>();
        var convertedConstructors = new List<ConstructorDeclarationSyntax>();
        var convertedMethods = new List<MemberDeclarationSyntax>();
        var nestedTypes = new List<MemberDeclarationSyntax>();

        foreach (var member in members)
        {
            switch (member)
            {
                case FieldDeclaration fieldDecl:
                {
                    var fieldSyntax = BodyDeclarationVisitor.VisitBodyDeclarationForClass(
                        context, classSyntax, fieldDecl, [], []);
                    var fieldWithComments = fieldSyntax?.WithJavaComments(context, fieldDecl);
                    if (fieldWithComments is not null)
                    {
                        convertedFields.Add(fieldWithComments);
                    }

                    break;
                }

                case ConstructorDeclaration ctorDecl:
                {
                    var ctorSyntax = ConvertEnumConstructor(context, classSyntax, ctorDecl);
                    var ctorWithComments = ctorSyntax?.WithJavaComments(context, ctorDecl);
                    if (ctorWithComments is not null)
                    {
                        convertedConstructors.Add(ctorWithComments);
                    }

                    break;
                }

                case MethodDeclaration methodDecl:
                {
                    var methodSyntax = BodyDeclarationVisitor.VisitBodyDeclarationForClass(
                        context, classSyntax, methodDecl, [], []);
                    var methodWithComments = methodSyntax?.WithJavaComments(context, methodDecl);
                    if (methodWithComments is not null)
                    {
                        convertedMethods.Add(methodWithComments);
                    }

                    break;
                }

                case ClassOrInterfaceDeclaration nestedType:
                {
                    if (nestedType.isInterface())
                    {
                        nestedTypes.Add(
                            ClassOrInterfaceDeclarationVisitor.VisitInterfaceDeclaration(context, nestedType, true));
                    }
                    else
                    {
                        nestedTypes.Add(
                            ClassOrInterfaceDeclarationVisitor.VisitClassDeclaration(context, nestedType, true));
                    }

                    break;
                }

                case EnumDeclaration nestedEnum:
                {
                    nestedTypes.Add(VisitEnumDeclaration(context, nestedEnum, isNested: true));
                    break;
                }

                case RecordDeclaration nestedRecord:
                {
                    nestedTypes.Add(RecordDeclarationVisitor.VisitRecordDeclaration(context, nestedRecord, true));
                    break;
                }

                default:
                {
                    var syntax = BodyDeclarationVisitor.VisitBodyDeclarationForClass(
                        context, classSyntax, member, [], []);
                    var withComments = syntax?.WithJavaComments(context, member);
                    if (withComments is not null)
                    {
                        convertedMethods.Add(withComments);
                    }

                    break;
                }
            }

            while (context.PendingAnonymousTypes.Count > 0)
            {
                nestedTypes.Add(context.PendingAnonymousTypes.Dequeue());
            }
        }

        foreach (var field in convertedFields)
        {
            classSyntax = classSyntax.AddMembers(field);
        }

        if (convertedConstructors.Count == 0)
        {
            classSyntax = classSyntax.AddMembers(CreateDefaultEnumConstructor(name));
        }
        else
        {
            foreach (var ctor in convertedConstructors)
            {
                classSyntax = classSyntax.AddMembers(ctor);
            }
        }

        classSyntax = classSyntax.AddMembers(
            CreateNameMethod(),
            CreateOrdinalMethod(),
            CreateEqualsMethod(),
            CreateGetHashCodeMethod(),
            CreateEqualityOperator(name),
            CreateInequalityOperator(name));

        foreach (var method in convertedMethods)
        {
            classSyntax = classSyntax.AddMembers(method);
        }

        classSyntax = classSyntax.AddMembers(
            CreateValuesMethod(enumTypeSyntax, entries),
            CreateValueOfMethod(name, enumTypeSyntax, entries));

        for (int ordinal = 0; ordinal < entries.Count; ordinal++)
        {
            classSyntax = classSyntax.AddMembers(
                CreateConstantNestedClass(context, classSyntax, name, entries[ordinal], ordinal));
        }

        foreach (var nested in nestedTypes)
        {
            classSyntax = classSyntax.AddMembers(nested);
        }

        return WithEnumConversionComment(
            classSyntax.WithJavaComments(context, enumDecl) ?? classSyntax, name);
    }

    private static ClassDeclarationSyntax WithEnumConversionComment(ClassDeclarationSyntax classSyntax, string enumName)
    {
        var leading = classSyntax.GetLeadingTrivia()
            .Insert(0, SyntaxFactory.CarriageReturnLineFeed)
            .Insert(0, SyntaxFactory.Comment($"// Java Enum of type : {enumName} converted into sealed class"));
        return classSyntax.WithLeadingTrivia(leading);
    }

    private static FieldDeclarationSyntax CreatePrivateReadonlyField(string typeName, string identifier)
    {
        return SyntaxFactory.FieldDeclaration(
                SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName(typeName))
                    .AddVariables(SyntaxFactory.VariableDeclarator(identifier)))
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));
    }

    private static ConstructorDeclarationSyntax? ConvertEnumConstructor(
        ConversionContext context,
        ClassDeclarationSyntax classSyntax,
        ConstructorDeclaration ctorDecl)
    {
        string? identifier = classSyntax.Identifier.Value?.ToString();
        if (identifier is null)
        {
            return null;
        }

        // Enum constructors become protected so nested constant classes can call them
        var ctorSyntax = SyntaxFactory.ConstructorDeclaration(identifier)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));

        var paramSyntaxes = new List<ParameterSyntax>
        {
            SyntaxFactory.Parameter(SyntaxFactory.Identifier("name"))
                .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword))),
            SyntaxFactory.Parameter(SyntaxFactory.Identifier("ordinal"))
                .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)))
        };

        var parameters = ctorDecl.getParameters().ToList<Parameter>() ?? [];
        foreach (var param in parameters)
        {
            string paramName = TypeHelper.EscapeIdentifier(param.getNameAsString());
            if (param.isVarArgs())
            {
                paramSyntaxes.Add(
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName))
                        .WithType(TypeHelper.ConvertArrayTypeSyntax(context, param.getType(), 1))
                        .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.ParamsKeyword))));
            }
            else
            {
                paramSyntaxes.Add(
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName))
                        .WithType(SyntaxFactory.ParseTypeName(TypeHelper.ConvertTypeOf(context, param))));
            }
        }

        ctorSyntax = ctorSyntax.AddParameterListParameters(paramSyntaxes.ToArray());

        var block = ctorDecl.getBody();
        var statements = block.getStatements().ToList<Statement>() ?? [];

        // Enum ctors always take synthetic name/ordinal first; pass them through this()/base().
        var prependArgs = new List<ArgumentSyntax>
        {
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName("name")),
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName("ordinal"))
        };

        var (initializer, remainingStatements) = ConstructorConversion.ExtractInitializer(
            context, statements, prependArgs);

        bool chainsToThis = initializer is not null
            && initializer.ThisOrBaseKeyword.IsKind(SyntaxKind.ThisKeyword);

        if (initializer is not null)
        {
            ctorSyntax = ctorSyntax.WithInitializer(initializer);
        }

        var bodyStatements = new List<StatementSyntax>();

        // Chained this(...) already assigns name/ordinal in the target constructor.
        if (!chainsToThis)
        {
            bodyStatements.Add(CreateSimpleAssignment("_name", "name"));
            bodyStatements.Add(CreateSimpleAssignment("_ordinal", "ordinal"));
        }

        bodyStatements.AddRange(StatementVisitor.VisitStatements(context, remainingStatements));

        return ctorSyntax.WithBody(SyntaxFactory.Block(bodyStatements));
    }

    private static ConstructorDeclarationSyntax CreateDefaultEnumConstructor(string typeName)
    {
        return SyntaxFactory.ConstructorDeclaration(typeName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword))
            .AddParameterListParameters(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("name"))
                    .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword))),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("ordinal"))
                    .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword))))
            .WithBody(SyntaxFactory.Block(
                CreateSimpleAssignment("_name", "name"),
                CreateSimpleAssignment("_ordinal", "ordinal")));
    }

    private static ExpressionStatementSyntax CreateSimpleAssignment(string fieldName, string paramName)
    {
        return SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(fieldName),
                SyntaxFactory.IdentifierName(paramName)));
    }

    private static MethodDeclarationSyntax CreateNameMethod()
    {
        return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
                "name")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .WithExpressionBody(
                SyntaxFactory.ArrowExpressionClause(SyntaxFactory.IdentifierName("_name")))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    private static MethodDeclarationSyntax CreateOrdinalMethod()
    {
        return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                "ordinal")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .WithExpressionBody(
                SyntaxFactory.ArrowExpressionClause(SyntaxFactory.IdentifierName("_ordinal")))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    private static MethodDeclarationSyntax CreateEqualsMethod()
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

    private static MethodDeclarationSyntax CreateGetHashCodeMethod()
    {
        // Identity hash to stay consistent with ReferenceEquals-based Equals / ==
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

    private static OperatorDeclarationSyntax CreateEqualityOperator(string enumName)
    {
        var typeSyntax = SyntaxFactory.NullableType(SyntaxFactory.ParseTypeName(enumName));
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

    private static OperatorDeclarationSyntax CreateInequalityOperator(string enumName)
    {
        var typeSyntax = SyntaxFactory.NullableType(SyntaxFactory.ParseTypeName(enumName));
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

    private static MethodDeclarationSyntax CreateValuesMethod(
        TypeSyntax enumTypeSyntax,
        IReadOnlyList<EnumConstantDeclaration> entries)
    {
        var arrayType = SyntaxFactory.ArrayType(enumTypeSyntax)
            .AddRankSpecifiers(
                SyntaxFactory.ArrayRankSpecifier(
                    SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                        SyntaxFactory.OmittedArraySizeExpression())));

        var initials = entries
            .Select(e => (ExpressionSyntax)SyntaxFactory.IdentifierName(e.getNameAsString()))
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

    private static MethodDeclarationSyntax CreateValueOfMethod(
        string enumName,
        TypeSyntax enumTypeSyntax,
        IReadOnlyList<EnumConstantDeclaration> entries)
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

        for (int i = entries.Count - 1; i >= 0; i--)
        {
            string constantName = entries[i].getNameAsString();
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

    private static ClassDeclarationSyntax CreateConstantNestedClass(
        ConversionContext context,
        ClassDeclarationSyntax parentClass,
        string enumName,
        EnumConstantDeclaration entry,
        int ordinal)
    {
        string constantName = entry.getNameAsString();
        string nestedClassName = BuildNestedClassName(constantName, enumName);

        var nested = SyntaxFactory.ClassDeclaration(nestedClassName)
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword))
            .AddBaseListTypes(SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(enumName)));

        var baseArgs = new List<ArgumentSyntax>
        {
            SyntaxFactory.Argument(
                SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(constantName))),
            SyntaxFactory.Argument(
                SyntaxFactory.LiteralExpression(
                    SyntaxKind.NumericLiteralExpression,
                    SyntaxFactory.Literal(ordinal)))
        };

        foreach (var arg in entry.getArguments().OfType<Expression>())
        {
            var argSyntax = ExpressionVisitor.VisitExpression(context, arg);
            if (argSyntax != null)
            {
                baseArgs.Add(SyntaxFactory.Argument(argSyntax));
            }
        }

        var ctor = SyntaxFactory.ConstructorDeclaration(nestedClassName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .WithInitializer(
                SyntaxFactory.ConstructorInitializer(
                    SyntaxKind.BaseConstructorInitializer,
                    SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(baseArgs))))
            .WithBody(SyntaxFactory.Block());

        nested = nested.AddMembers(ctor);

        bool previousConstantBody = context.IsConvertingEnumConstantBody;
        context.IsConvertingEnumConstantBody = true;
        try
        {
            foreach (var bodyMember in entry.getClassBody().OfType<BodyDeclaration>())
            {
                var syntax = BodyDeclarationVisitor.VisitBodyDeclarationForClass(
                    context, parentClass, bodyMember, [], []);
                if (syntax != null)
                {
                    // Nested constant methods that override base members should not be virtual
                    if (syntax is MethodDeclarationSyntax methodSyntax
                        && methodSyntax.Modifiers.Any(m => m.IsKind(SyntaxKind.VirtualKeyword)))
                    {
                        syntax = methodSyntax.WithModifiers(
                            SyntaxFactory.TokenList(
                                methodSyntax.Modifiers.Where(m => !m.IsKind(SyntaxKind.VirtualKeyword))));
                    }

                    var bodyWithComments = syntax.WithJavaComments(context, bodyMember);
                    if (bodyWithComments is not null)
                    {
                        nested = nested.AddMembers(bodyWithComments);
                    }
                }
            }
        }
        finally
        {
            context.IsConvertingEnumConstantBody = previousConstantBody;
        }

        return nested;
    }

    /// <summary>
    /// Builds a nested class name such as ADD → AddOperation for enum Operation.
    /// </summary>
    internal static string BuildNestedClassName(string constantName, string enumName)
        => ToPascalCase(constantName) + enumName;

    internal static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(part =>
            part.Length == 1
                ? part.ToUpperInvariant()
                : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }
}
