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
        context.DeclaredTypeNames.Add(name);

        var entries = enumDecl.getEntries().ToList<EnumConstantDeclaration>() ?? [];
        var members = enumDecl.getMembers()?.ToList<BodyDeclaration>() ?? [];

        bool hasAbstractMethod = members.OfType<MethodDeclaration>().Any(m => m.isAbstract());
        // Nested constant subclasses require a non-sealed base; use abstract when needed
        bool isAbstract = hasAbstractMethod || entries.Any(e => !e.getClassBody().isEmpty());

        // Java enums allow a field and a method to share a simple name; C# rejects the pair
        // (CS0102). Run the same prescan classes get so the FIELD is renamed (name + "Field")
        // through FieldNameRenames while the method — the cross-file-stable side — keeps its
        // name. Without this, enum members like `transformation` (field + accessor) collide
        // once the synthetic name/ordinal members are also present.
        ClassOrInterfaceDeclarationVisitor.PreScanFieldMethodCollisionsForEnum(context, members);

        // The converted enum form adds synthetic members: name()/ordinal() methods, and
        // values()/valueOf() methods. A user FIELD sharing a synthetic method's simple name is a
        // CS0102 collision — rename the field (the method name is the cross-file-stable side).
        // Java forbids user methods named name/ordinal (final on java.lang.Enum), but values()/
        // valueOf() can be user-declared — in that case the user's method wins and the synthetic
        // one is skipped (a duplicate would be CS0111).
        foreach (var field in members.OfType<FieldDeclaration>())
        {
            foreach (var variable in field.getVariables()?.ToList<VariableDeclarator>() ?? [])
            {
                var fieldName = variable.getNameAsString();

                if (fieldName is "name" or "ordinal")
                {
                    context.FieldNameRenames[fieldName] = fieldName + "Field";
                }
            }
        }

        bool userDefinesValues = members.OfType<MethodDeclaration>().Any(m => m.getNameAsString() == "values");
        bool userDefinesValueOf = members.OfType<MethodDeclaration>().Any(m => m.getNameAsString() == "valueOf");

        var classSyntax = SyntaxFactory.ClassDeclaration(name);

        var mods = enumDecl.getModifiers().ToModifierKeywordSet();

        classSyntax = ModifierHelper.AddAccessModifiers(classSyntax, mods);
        if (ModifierHelper.IsPackagePrivate(mods))
        {
            // Java package-private enums referenced by public members (or cross-file) are less
            // accessible in C# (CS0050/CS0051) — promote to public.
            classSyntax = classSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
        }
        else if (mods.Contains(Modifier.Keyword.PRIVATE))
        {
            // Java allows a private nested enum in a public member signature (erasure); C#
            // rejects the combination — promote (replaces the private token, CS0107-safe).
            classSyntax = ModifierHelper.ReplacePrivateWithPublic(classSyntax);
        }
        else if (mods.Contains(Modifier.Keyword.PROTECTED))
        {
            // Java protected nested types are reachable package-wide; C# protected restricts to
            // subclasses, which breaks sibling-file references (CS0122) and public signatures
            // (CS0050/CS0051). Promote to public — same rule as nested classes/records.
            classSyntax = ModifierHelper.ReplaceProtectedWithPublic(classSyntax);
        }
        if (isAbstract)
            classSyntax = classSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.AbstractKeyword));

        // Placeholders synthesized from here on belong to this enum (Java enums have no base
        // list of their own, so ownership starts immediately).
        var previousPlaceholderOwner = context.CurrentPlaceholderOwner;
        context.CurrentPlaceholderOwner = name;

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

            context.DrainPlaceholderTypes(context.CurrentPlaceholderOwner,
                placeholder => nestedTypes.Add(placeholder));
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
            EnumSyntaxHelpers.CreateNameMethod(),
            EnumSyntaxHelpers.CreateOrdinalMethod(),
            EnumSyntaxHelpers.CreateReferenceEqualsMethod(),
            EnumSyntaxHelpers.CreateReferenceGetHashCodeMethod(),
            EnumSyntaxHelpers.CreateReferenceEqualityOperator(name),
            EnumSyntaxHelpers.CreateReferenceInequalityOperator(name));

        foreach (var method in convertedMethods)
        {
            classSyntax = classSyntax.AddMembers(method);
        }

        var constantNames = entries.Select(e => e.getNameAsString()).ToList();

        if (!userDefinesValues)
        {
            classSyntax = classSyntax.AddMembers(EnumSyntaxHelpers.CreateValuesMethod(enumTypeSyntax, constantNames));
        }

        if (!userDefinesValueOf)
        {
            classSyntax = classSyntax.AddMembers(EnumSyntaxHelpers.CreateValueOfMethod(enumTypeSyntax, name, constantNames));
        }

        for (int ordinal = 0; ordinal < entries.Count; ordinal++)
        {
            classSyntax = classSyntax.AddMembers(
                CreateConstantNestedClass(context, classSyntax, name, entries[ordinal], ordinal));
        }

        foreach (var nested in nestedTypes)
        {
            classSyntax = classSyntax.AddMembers(nested);
        }

        context.CurrentPlaceholderOwner = previousPlaceholderOwner;

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

        var parameters = ctorDecl.getParameters().ToList<Parameter>() ?? [];

        // The synthetic name/ordinal parameters mirror javac's own enum construction; when the
        // user's constructor already declares a parameter with the same name, disambiguate by
        // appending underscores (name_, name__, ...) until the identifier is unique.
        var userParamNames = new HashSet<string>(
            parameters.Select(p => TypeHelper.EscapeIdentifier(p.getNameAsString())),
            StringComparer.Ordinal);
        var nameParamName = GetDisambiguatedSyntheticName(userParamNames, "name");
        var ordinalParamName = GetDisambiguatedSyntheticName(userParamNames, "ordinal");

        var paramSyntaxes = new List<ParameterSyntax>
        {
            SyntaxFactory.Parameter(SyntaxFactory.Identifier(nameParamName))
                .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword))),
            SyntaxFactory.Parameter(SyntaxFactory.Identifier(ordinalParamName))
                .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)))
        };

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
                        .WithType(TypeHelper.ConvertTypeSyntax(context, param.getType(), param.getType().getArrayLevel())));
            }
        }

        ctorSyntax = ctorSyntax.AddParameterListParameters(paramSyntaxes.ToArray());

        var block = ctorDecl.getBody();
        var statements = block.getStatements().ToList<Statement>() ?? [];

        // Enum ctors always take synthetic name/ordinal first; pass them through this()/base().
        var prependArgs = new List<ArgumentSyntax>
        {
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName(nameParamName)),
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName(ordinalParamName))
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
            bodyStatements.Add(CreateSimpleAssignment("_name", nameParamName));
            bodyStatements.Add(CreateSimpleAssignment("_ordinal", ordinalParamName));
        }

        bodyStatements.AddRange(StatementVisitor.VisitStatements(context, remainingStatements));

        return ctorSyntax.WithBody(SyntaxFactory.Block(bodyStatements));
    }

    private static string GetDisambiguatedSyntheticName(HashSet<string> userParamNames, string baseName)
    {
        if (!userParamNames.Contains(baseName))
        {
            return baseName;
        }

        var candidate = baseName;
        while (userParamNames.Contains(candidate))
        {
            candidate += "_";
        }

        return candidate;
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
