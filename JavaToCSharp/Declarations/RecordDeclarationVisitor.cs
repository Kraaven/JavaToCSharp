using com.github.javaparser.ast;
using com.github.javaparser.ast.body;
using com.github.javaparser.ast.stmt;
using com.github.javaparser.ast.type;
using JavaToCSharp.Statements;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Parameter = com.github.javaparser.ast.body.Parameter;

namespace JavaToCSharp.Declarations;

public class RecordDeclarationVisitor : BodyDeclarationVisitor<RecordDeclaration>
{
    public override MemberDeclarationSyntax? VisitForClass(
        ConversionContext context,
        ClassDeclarationSyntax classSyntax,
        RecordDeclaration declaration,
        IReadOnlyList<ClassOrInterfaceType> extends,
        IReadOnlyList<ClassOrInterfaceType> implements)
    {
        return VisitRecordDeclaration(context, declaration, isNested: true);
    }

    public override MemberDeclarationSyntax? VisitForInterface(
        ConversionContext context,
        InterfaceDeclarationSyntax interfaceSyntax,
        RecordDeclaration declaration)
    {
        return VisitRecordDeclaration(context, declaration, isNested: true);
    }

    public static ClassDeclarationSyntax VisitRecordDeclaration(
        ConversionContext context,
        RecordDeclaration recordDecl,
        bool isNested = false)
    {
        var previousTypeParameters = context.TypeParameterNames.ToList();
        var previousComponentNames = context.RecordComponentNames.ToList();
        var previousBackingFields = context.RecordComponentBackingFields.ToDictionary(
            static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        bool previousIsConvertingRecord = context.IsConvertingRecord;

        try
        {
            context.IsConvertingRecord = true;
            return VisitRecordDeclarationCore(context, recordDecl, isNested);
        }
        finally
        {
            context.IsConvertingRecord = previousIsConvertingRecord;

            context.TypeParameterNames.Clear();
            foreach (var name in previousTypeParameters)
            {
                context.TypeParameterNames.Add(name);
            }

            context.RecordComponentNames.Clear();
            foreach (var name in previousComponentNames)
            {
                context.RecordComponentNames.Add(name);
            }

            context.RecordComponentBackingFields.Clear();
            foreach (var pair in previousBackingFields)
            {
                context.RecordComponentBackingFields[pair.Key] = pair.Value;
            }
        }
    }

    private static ClassDeclarationSyntax VisitRecordDeclarationCore(
        ConversionContext context,
        RecordDeclaration recordDecl,
        bool isNested)
    {
        string name = recordDecl.getNameAsString();

        if (!isNested)
        {
            context.RootTypeName = name;
        }

        context.LastTypeName = name;

        var classSyntax = SyntaxFactory.ClassDeclaration(name);

        var typeParams = recordDecl.getTypeParameters().ToList<TypeParameter>() ?? [];
        context.TypeParameterNames.Clear();

        if (typeParams.Count > 0)
        {
            classSyntax = classSyntax.AddTypeParameterListParameters(typeParams
                .Select(i => SyntaxFactory.TypeParameter(i.getNameAsString())).ToArray());

            foreach (var typeParam in typeParams)
            {
                context.TypeParameterNames.Add(typeParam.getNameAsString());

                var bounds = typeParam.getTypeBound().ToList<ClassOrInterfaceType>() ?? [];
                if (bounds.Count == 0)
                {
                    continue;
                }

                var constraint = SyntaxFactory.TypeParameterConstraintClause(typeParam.getNameAsString())
                    .AddConstraints(bounds
                        .Select(bound => SyntaxFactory.TypeConstraint(TypeHelper.GetSyntaxFromType(bound)))
                        .ToArray<TypeParameterConstraintSyntax>());

                classSyntax = classSyntax.AddConstraintClauses(constraint);
            }
        }

        var mods = recordDecl.getModifiers().ToModifierKeywordSet();

        if (mods.Contains(Modifier.Keyword.PRIVATE))
            classSyntax = classSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));
        if (mods.Contains(Modifier.Keyword.PROTECTED))
            classSyntax = classSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));
        if (mods.Contains(Modifier.Keyword.PUBLIC))
            classSyntax = classSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));

        // Records are implicitly final
        classSyntax = classSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.SealedKeyword));

        var implements = recordDecl.getImplementedTypes().ToList<ClassOrInterfaceType>() ?? [];

        foreach (var implement in implements)
        {
            classSyntax = classSyntax.AddBaseListTypes(
                SyntaxFactory.SimpleBaseType(TypeHelper.GetSyntaxFromType(implement)));
        }

        var components = recordDecl.getParameters().ToList<Parameter>() ?? [];
        var componentNames = components
            .Select(c => c.getNameAsString())
            .ToHashSet(StringComparer.Ordinal);

        context.RecordComponentNames.Clear();
        foreach (var componentName in componentNames)
        {
            context.RecordComponentNames.Add(componentName);
        }

        var members = recordDecl.getMembers()?.ToList<BodyDeclaration>() ?? [];

        // Identify overridden component accessors before converting members
        var overriddenAccessors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members.OfType<MethodDeclaration>())
        {
            string methodName = member.getNameAsString();
            if ((member.getParameters()?.size() ?? 0) == 0 && componentNames.Contains(methodName))
            {
                overriddenAccessors.Add(methodName);
            }
        }

        // Every component uses a private {name}_attribute backing field.
        context.RecordComponentBackingFields.Clear();
        foreach (var component in components)
        {
            string componentName = component.getNameAsString();
            context.RecordComponentBackingFields[componentName] = GetStorageName(componentName);
        }

        var staticAndOtherFields = new List<MemberDeclarationSyntax>();
        var convertedConstructors = new List<ConstructorDeclarationSyntax>();
        var convertedMethods = new List<MemberDeclarationSyntax>();
        var nestedTypes = new List<MemberDeclarationSyntax>();
        var hasEquals = false;
        var hasHashCode = false;
        var hasToString = false;
        CompactConstructorDeclaration? compactCtor = recordDecl.getCompactConstructors()
            .OfType<CompactConstructorDeclaration>()
            .FirstOrDefault();

        foreach (var member in members)
        {
            switch (member)
            {
                case CompactConstructorDeclaration:
                    continue;

                case FieldDeclaration fieldDecl:
                {
                    // Avoid rewriting component names inside unrelated field initializers
                    var savedBackingFields = SnapshotAndClearBackingFields(context);
                    var fieldSyntax = BodyDeclarationVisitor.VisitBodyDeclarationForClass(
                        context, classSyntax, fieldDecl, [], implements);
                    RestoreBackingFields(context, savedBackingFields);

                    var fieldWithComments = fieldSyntax?.WithJavaComments(context, fieldDecl);
                    if (fieldWithComments is not null)
                    {
                        staticAndOtherFields.Add(fieldWithComments);
                    }

                    break;
                }

                case ConstructorDeclaration ctorDecl:
                {
                    // Ctor params use component names; do not rewrite them to backing fields
                    var savedBackingFields = SnapshotAndClearBackingFields(context);
                    var ctorSyntax = BodyDeclarationVisitor.VisitBodyDeclarationForClass(
                        context, classSyntax, ctorDecl, [], implements) as ConstructorDeclarationSyntax;
                    RestoreBackingFields(context, savedBackingFields);

                    var ctorWithComments = ctorSyntax?.WithJavaComments(context, ctorDecl);
                    if (ctorWithComments is not null)
                    {
                        convertedConstructors.Add(ctorWithComments);
                    }

                    break;
                }

                case MethodDeclaration methodDecl:
                {
                    string methodName = methodDecl.getNameAsString();
                    int paramCount = methodDecl.getParameters()?.size() ?? 0;

                    if (methodName == "equals" && paramCount == 1)
                        hasEquals = true;
                    else if (methodName == "hashCode" && paramCount == 0)
                        hasHashCode = true;
                    else if (methodName == "toString" && paramCount == 0)
                        hasToString = true;

                    var methodSyntax = BodyDeclarationVisitor.VisitBodyDeclarationForClass(
                        context, classSyntax, methodDecl, [], implements);
                    var methodWithComments = methodSyntax?.WithJavaComments(context, methodDecl);
                    if (methodWithComments is not null)
                    {
                        convertedMethods.Add(methodWithComments);
                    }

                    break;
                }

                case RecordDeclaration nestedRecord:
                {
                    nestedTypes.Add(VisitRecordDeclaration(context, nestedRecord, isNested: true));
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
                    nestedTypes.Add(EnumDeclarationVisitor.VisitEnumDeclaration(context, nestedEnum));
                    break;
                }

                default:
                {
                    var syntax = BodyDeclarationVisitor.VisitBodyDeclarationForClass(
                        context, classSyntax, member, [], implements);
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

        foreach (var field in staticAndOtherFields)
        {
            classSyntax = classSyntax.AddMembers(field);
        }

        foreach (var component in components)
        {
            classSyntax = classSyntax.AddMembers(CreateComponentField(component));
        }

        string genericTypeName = BuildGenericTypeName(name, context.TypeParameterNames);

        // Canonical ctor params keep Java component names; field assigns use backing names
        var savedBackingForCtor = SnapshotAndClearBackingFields(context);
        classSyntax = classSyntax.AddMembers(
            CreateCanonicalConstructor(context, name, components, compactCtor));
        RestoreBackingFields(context, savedBackingForCtor);

        foreach (var ctor in convertedConstructors)
        {
            if (IsCanonicalConstructor(ctor, components))
            {
                continue;
            }

            classSyntax = classSyntax.AddMembers(ctor);
        }

        foreach (var method in convertedMethods)
        {
            classSyntax = classSyntax.AddMembers(method);
        }

        foreach (var component in components)
        {
            string componentName = component.getNameAsString();
            if (!overriddenAccessors.Contains(componentName))
            {
                classSyntax = classSyntax.AddMembers(CreateDefaultAccessor(component));
            }
        }

        if (components.Count > 0)
        {
            classSyntax = classSyntax.AddMembers(CreateDeconstructMethod(components));
        }

        if (!hasEquals)
        {
            classSyntax = classSyntax.AddMembers(CreateEqualsMethod(genericTypeName, components));
        }

        if (!hasHashCode)
        {
            classSyntax = classSyntax.AddMembers(CreateGetHashCodeMethod(components));
        }

        if (!hasToString)
        {
            classSyntax = classSyntax.AddMembers(CreateToStringMethod(name, components));
        }

        foreach (var nested in nestedTypes)
        {
            classSyntax = classSyntax.AddMembers(nested);
        }

        return classSyntax.WithJavaComments(context, recordDecl);
    }

    private static Dictionary<string, string> SnapshotAndClearBackingFields(ConversionContext context)
    {
        var snapshot = context.RecordComponentBackingFields.ToDictionary(
            static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        context.RecordComponentBackingFields.Clear();
        return snapshot;
    }

    private static void RestoreBackingFields(ConversionContext context, Dictionary<string, string> snapshot)
    {
        context.RecordComponentBackingFields.Clear();
        foreach (var pair in snapshot)
        {
            context.RecordComponentBackingFields[pair.Key] = pair.Value;
        }
    }

    private static string BuildGenericTypeName(string name, IList<string> typeParameterNames)
    {
        if (typeParameterNames.Count == 0)
        {
            return name;
        }

        return $"{name}<{string.Join(", ", typeParameterNames)}>";
    }

    private static string GetStorageName(string componentName) => componentName + "_attribute";

    private static FieldDeclarationSyntax CreateComponentField(Parameter component)
    {
        string typeName = TypeHelper.ConvertTypeOf(component);
        string identifier = TypeHelper.EscapeIdentifier(GetStorageName(component.getNameAsString()));

        return SyntaxFactory.FieldDeclaration(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.ParseTypeName(typeName),
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(identifier))))
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));
    }

    private static MethodDeclarationSyntax CreateDefaultAccessor(Parameter component)
    {
        string typeName = TypeHelper.ConvertTypeOf(component);
        string componentName = component.getNameAsString();
        string storageName = TypeHelper.EscapeIdentifier(GetStorageName(componentName));
        string methodName = TypeHelper.EscapeIdentifier(componentName);

        return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.ParseTypeName(typeName),
                methodName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .WithExpressionBody(
                SyntaxFactory.ArrowExpressionClause(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.ThisExpression(),
                        SyntaxFactory.IdentifierName(storageName))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    private static MethodDeclarationSyntax CreateDeconstructMethod(IReadOnlyList<Parameter> components)
    {
        var parameters = new List<ParameterSyntax>();
        var body = new List<StatementSyntax>();

        foreach (var component in components)
        {
            string componentName = component.getNameAsString();
            string paramName = TypeHelper.EscapeIdentifier(componentName);
            string storageName = TypeHelper.EscapeIdentifier(GetStorageName(componentName));
            string typeName = TypeHelper.ConvertTypeOf(component);

            parameters.Add(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName))
                    .WithType(SyntaxFactory.ParseTypeName(typeName))
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.OutKeyword))));

            body.Add(
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(paramName),
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.ThisExpression(),
                            SyntaxFactory.IdentifierName(storageName)))));
        }

        return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                "Deconstruct")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddParameterListParameters(parameters.ToArray())
            .WithBody(SyntaxFactory.Block(body));
    }

    private static ConstructorDeclarationSyntax CreateCanonicalConstructor(
        ConversionContext context,
        string typeName,
        IReadOnlyList<Parameter> components,
        CompactConstructorDeclaration? compactCtor)
    {
        var ctor = SyntaxFactory.ConstructorDeclaration(typeName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));

        var paramSyntaxes = new List<ParameterSyntax>();
        foreach (var component in components)
        {
            string paramName = TypeHelper.EscapeIdentifier(component.getNameAsString());
            paramSyntaxes.Add(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName))
                    .WithType(SyntaxFactory.ParseTypeName(TypeHelper.ConvertTypeOf(component))));
        }

        if (paramSyntaxes.Count > 0)
        {
            ctor = ctor.AddParameterListParameters(paramSyntaxes.ToArray());
        }

        var bodyStatements = new List<StatementSyntax>();

        if (compactCtor != null)
        {
            var compactStatements = compactCtor.getBody().getStatements().ToList<Statement>() ?? [];
            bodyStatements.AddRange(StatementVisitor.VisitStatements(context, compactStatements));
        }

        foreach (var component in components)
        {
            string componentName = component.getNameAsString();
            string storageName = TypeHelper.EscapeIdentifier(GetStorageName(componentName));
            string paramName = TypeHelper.EscapeIdentifier(componentName);

            bodyStatements.Add(
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.ThisExpression(),
                            SyntaxFactory.IdentifierName(storageName)),
                        SyntaxFactory.IdentifierName(paramName))));
        }

        return ctor.WithBody(SyntaxFactory.Block(bodyStatements));
    }

    private static bool IsCanonicalConstructor(
        ConstructorDeclarationSyntax ctor,
        IReadOnlyList<Parameter> components)
    {
        if (ctor.ParameterList.Parameters.Count != components.Count)
        {
            return false;
        }

        for (int i = 0; i < components.Count; i++)
        {
            string expectedName = TypeHelper.EscapeIdentifier(components[i].getNameAsString());
            if (!string.Equals(ctor.ParameterList.Parameters[i].Identifier.Text, expectedName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static MethodDeclarationSyntax CreateEqualsMethod(
        string genericTypeName,
        IReadOnlyList<Parameter> components)
    {
        var otherType = SyntaxFactory.ParseTypeName(genericTypeName);

        var statements = new List<StatementSyntax>
        {
            SyntaxFactory.IfStatement(
                SyntaxFactory.IsPatternExpression(
                    SyntaxFactory.IdentifierName("obj"),
                    SyntaxFactory.UnaryPattern(
                        SyntaxFactory.Token(SyntaxKind.NotKeyword),
                        SyntaxFactory.DeclarationPattern(
                            otherType,
                            SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier("other"))))),
                SyntaxFactory.Block(SyntaxFactory.ReturnStatement(
                    SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression))))
        };

        ExpressionSyntax? comparison = null;
        foreach (var component in components)
        {
            string storageName = TypeHelper.EscapeIdentifier(GetStorageName(component.getNameAsString()));

            var equalsCall = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)),
                        SyntaxFactory.IdentifierName("Equals")))
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
                {
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName(storageName)),
                    SyntaxFactory.Argument(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("other"),
                            SyntaxFactory.IdentifierName(storageName)))
                })));

            comparison = comparison == null
                ? equalsCall
                : SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression, comparison, equalsCall);
        }

        statements.Add(SyntaxFactory.ReturnStatement(
            comparison ?? SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression)));

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
            .WithBody(SyntaxFactory.Block(statements));
    }

    private static MethodDeclarationSyntax CreateGetHashCodeMethod(IReadOnlyList<Parameter> components)
    {
        ExpressionSyntax hashExpr;

        if (components.Count == 0)
        {
            hashExpr = SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0));
        }
        else
        {
            var args = components
                .Take(8)
                .Select(c => SyntaxFactory.Argument(
                    SyntaxFactory.IdentifierName(TypeHelper.EscapeIdentifier(
                        GetStorageName(c.getNameAsString())))))
                .ToArray();

            hashExpr = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("HashCode"),
                        SyntaxFactory.IdentifierName("Combine")))
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args)));
        }

        return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                "GetHashCode")
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(hashExpr)));
    }

    private static MethodDeclarationSyntax CreateToStringMethod(
        string typeName,
        IReadOnlyList<Parameter> components)
    {
        var contents = new List<InterpolatedStringContentSyntax>
        {
            SyntaxFactory.InterpolatedStringText(
                SyntaxFactory.Token(
                    SyntaxTriviaList.Empty,
                    SyntaxKind.InterpolatedStringTextToken,
                    $"{typeName}[",
                    $"{typeName}[",
                    SyntaxTriviaList.Empty))
        };

        for (int i = 0; i < components.Count; i++)
        {
            if (i > 0)
            {
                contents.Add(SyntaxFactory.InterpolatedStringText(
                    SyntaxFactory.Token(
                        SyntaxTriviaList.Empty,
                        SyntaxKind.InterpolatedStringTextToken,
                        ", ",
                        ", ",
                        SyntaxTriviaList.Empty)));
            }

            string componentName = components[i].getNameAsString();
            string storageName = GetStorageName(componentName);

            contents.Add(SyntaxFactory.InterpolatedStringText(
                SyntaxFactory.Token(
                    SyntaxTriviaList.Empty,
                    SyntaxKind.InterpolatedStringTextToken,
                    $"{componentName}=",
                    $"{componentName}=",
                    SyntaxTriviaList.Empty)));
            contents.Add(SyntaxFactory.Interpolation(
                SyntaxFactory.IdentifierName(TypeHelper.EscapeIdentifier(storageName))));
        }

        contents.Add(SyntaxFactory.InterpolatedStringText(
            SyntaxFactory.Token(
                SyntaxTriviaList.Empty,
                SyntaxKind.InterpolatedStringTextToken,
                "]",
                "]",
                SyntaxTriviaList.Empty)));

        var interpolated = SyntaxFactory.InterpolatedStringExpression(
            SyntaxFactory.Token(SyntaxKind.InterpolatedStringStartToken),
            SyntaxFactory.List(contents));

        return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
                "ToString")
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(interpolated)));
    }
}
