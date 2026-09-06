using com.github.javaparser.ast.body;
using com.github.javaparser.ast.expr;
using JavaToCSharp.Declarations;
using JavaToCSharp.TypeMapping;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using com.github.javaparser.ast;
using com.github.javaparser.ast.type;
using Type = com.github.javaparser.ast.type.Type;

namespace JavaToCSharp.Expressions;

public class ObjectCreationExpressionVisitor : ExpressionVisitor<ObjectCreationExpr>
{
    protected override ExpressionSyntax Visit(ConversionContext context, ObjectCreationExpr newExpr)
    {
        var anonBody = newExpr.getAnonymousClassBody().FromOptional<NodeList>().ToList<BodyDeclaration>();

        if (anonBody is { Count: > 0 })
        {
            return VisitAnonymousClassCreationExpression(context, newExpr, anonBody);
        }

        var type = newExpr.getType();

        var typeSyntax = TypeHelper.GetSyntaxFromType(context, type);

        // Diamond operator (new HashMap<>()) — JavaParser infers type args from the LHS
        // but doesn't resolve them in the AST. Look at the enclosing variable declaration
        // to recover them so C# gets explicit generic arguments.
        var javaTypeArgs = type.getTypeArguments().FromOptional<NodeList>()?.ToList<Type>();
        if (javaTypeArgs is null or { Count: 0 } && typeSyntax is PredefinedTypeSyntax or IdentifierNameSyntax or QualifiedNameSyntax)
        {
            var inferred = InferTypeArgsFromEnclosing(context, newExpr, type);
            if (inferred is not null)
            {
                typeSyntax = inferred;
            }
        }

        // Constructor mapping: look up the Java type in the mapping index for
        // constructor-level template or rename entries.
        if (context.Options.MappingIndex is not null)
        {
            var javaTypeName = type.getNameAsString();
            var fileCtx = context.FileMappingContext;

            if (fileCtx is null || fileCtx.HasAnyMappedPackage)
            {
                if (TypeMappingResolver.TryResolveTypeName(context, javaTypeName, out _))
                {
                    var rawClass = TypeMappingResolver.StripGenericsAndArrays(javaTypeName);
                    if (rawClass.Length > 0
                        && context.Options.MappingIndex.TryGetByDottedName(rawClass, out var cls, out _)
                        && cls is not null)
                    {
                        var ctorArgs = newExpr.getArguments();
                        var argCount = ctorArgs?.size() ?? 0;
                        var ctorEntry = TypeMappingResolver.FindConstructor(cls, argCount, ctorArgs);
                        if (ctorEntry is not null)
                        {
                            return EmitConstructorMapping(context, ctorEntry, typeSyntax, ctorArgs);
                        }
                    }
                }
            }
        }

        var args = newExpr.getArguments();

        // Hoisted local classes (Java local class/record) are nested types whose captured
        // variables are received through constructor parameters; supply them at the call site.
        var hoisted = context.FindHoistedLocalClass(type.getNameAsString());
        if (hoisted is not null)
        {
            if (hoisted.TypeParams is { Count: > 0 } && typeSyntax is not GenericNameSyntax)
            {
                typeSyntax = SyntaxFactory.GenericName(typeSyntax.ToString())
                    .AddTypeArgumentListArguments(hoisted.TypeParams
                        .Select(p => SyntaxFactory.ParseTypeName(p))
                        .ToArray());
            }

            var creation = args is null || args.size() == 0
                ? SyntaxFactory.ObjectCreationExpression(typeSyntax).WithArgumentList(SyntaxFactory.ArgumentList())
                : SyntaxFactory.ObjectCreationExpression(typeSyntax, TypeHelper.GetSyntaxFromArguments(context, args), null);

            if (hoisted.NeedsParent)
            {
                creation = creation.AddArgumentListArguments(SyntaxFactory.Argument(SyntaxFactory.ThisExpression()));
            }

            if (hoisted.CapturedLocals is { Count: > 0 })
            {
                creation = creation.AddArgumentListArguments(hoisted.CapturedLocals
                    .Select(c => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(TypeHelper.EscapeIdentifier(c.Name))))
                    .ToArray());
            }

            return creation;
        }

        if (args is null || args.size() == 0)
        {
            return SyntaxFactory.ObjectCreationExpression(typeSyntax).WithArgumentList(SyntaxFactory.ArgumentList());
        }

        return SyntaxFactory.ObjectCreationExpression(typeSyntax, TypeHelper.GetSyntaxFromArguments(context, args), null);
    }

    private static ExpressionSyntax EmitConstructorMapping(
        ConversionContext context,
        MemberMapping entry,
        TypeSyntax typeSyntax,
        java.util.List? args)
    {
        switch (entry.Kind)
        {
            case MemberMappingKind.Rename:
            {
                TypeSyntax resolvedType;
                if (entry.CSharpMember is not null && typeSyntax is GenericNameSyntax genericType)
                {
                    resolvedType = SyntaxFactory.GenericName(entry.CSharpMember)
                        .WithTypeArgumentList(genericType.TypeArgumentList);
                }
                else
                {
                    resolvedType = SyntaxFactory.ParseTypeName(entry.CSharpMember ?? typeSyntax.ToFullString());
                }
                if (args is null || args.size() == 0)
                {
                    return SyntaxFactory.ObjectCreationExpression(resolvedType).WithArgumentList(SyntaxFactory.ArgumentList());
                }

                return SyntaxFactory.ObjectCreationExpression(resolvedType, TypeHelper.GetSyntaxFromArguments(context, args), null);
            }

            case MemberMappingKind.Template when entry.Expression is not null:
            {
                var receiverStr = typeSyntax.ToFullString().Trim();
                if (TypeMappingResolver.TryFillTemplate(context, receiverStr, entry.Expression, args, out var templateSyntax))
                {
                    return templateSyntax;
                }

                goto default;
            }

            default:
            {
                if (args is null || args.size() == 0)
                {
                    return SyntaxFactory.ObjectCreationExpression(typeSyntax).WithArgumentList(SyntaxFactory.ArgumentList());
                }

                return SyntaxFactory.ObjectCreationExpression(typeSyntax, TypeHelper.GetSyntaxFromArguments(context, args), null);
            }
        }
    }

    private static ObjectCreationExpressionSyntax VisitAnonymousClassCreationExpression(ConversionContext context, ObjectCreationExpr newExpr, List<BodyDeclaration> anonBody)
    {
        string baseTypeName = TypeHelper.ConvertType(context, newExpr.getType().getNameAsString());
        string anonTypeName = string.Empty;

        for (int i = 0; i <= 100; i++)
        {
            if (i == 100)
            {
                throw new InvalidOperationException("Too many anonymous types");
            }

            anonTypeName = $"Anonymous{baseTypeName}{(i == 0 ? string.Empty : i.ToString())}";

            if (context.UsedAnonymousTypeNames.Add(anonTypeName))
            {
                break; // go with this one
            }
        }

        var classSyntax = SyntaxFactory.ClassDeclaration(anonTypeName)
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword))
            .WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SeparatedList(new List<BaseTypeSyntax>
            {
                SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(baseTypeName))
            })));

        string? contextLastTypeName = context.LastTypeName;

        if (contextLastTypeName is not null)
        {
            var parentField = SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName(contextLastTypeName))
                                                                          .AddVariables(SyntaxFactory.VariableDeclarator("parent")))
                                           .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));

            var ctorSyntax = SyntaxFactory.ConstructorDeclaration(anonTypeName)
                                          .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                                          .AddParameterListParameters(SyntaxFactory.Parameter(SyntaxFactory.ParseToken("parent")).WithType(SyntaxFactory.ParseTypeName(contextLastTypeName)))
                                          .AddBodyStatements(SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.ThisExpression(), SyntaxFactory.IdentifierName("parent")), SyntaxFactory.IdentifierName("parent"))));

            classSyntax = classSyntax.AddMembers(ctorSyntax, parentField);
        }

        // TODO.PI: do we need to pass extends/implements here?
        foreach (var memberSyntax in anonBody
                     .Select(member => BodyDeclarationVisitor.VisitBodyDeclarationForClass(context, classSyntax, member, new List<ClassOrInterfaceType>(), new List<ClassOrInterfaceType>()))
                     .OfType<MemberDeclarationSyntax>())
        {
            classSyntax = classSyntax.AddMembers(memberSyntax);
        }

        context.PendingAnonymousTypes.Enqueue(classSyntax);

        var args = newExpr.getArguments();

        if (args is null || args.size() == 0)
        {
            return SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(anonTypeName))
                .AddArgumentListArguments(SyntaxFactory.Argument(SyntaxFactory.ThisExpression()));
        }

        return SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(anonTypeName),
            TypeHelper.GetSyntaxFromArguments(context, args),
            null);
    }

    /// <summary>
    /// When a Java <c>new HashMap&lt;&gt;()</c> uses the diamond operator, JavaParser leaves the
    /// type arguments empty. This method walks up to the enclosing variable declaration and
    /// uses its declared type's generics to rebuild the C# type with explicit arguments
    /// (e.g. <c>new Dictionary&lt;String, Item&gt;()</c>).
    /// </summary>
    private static TypeSyntax? InferTypeArgsFromEnclosing(ConversionContext context, ObjectCreationExpr newExpr, ClassOrInterfaceType type)
    {
        var parent = newExpr.getParentNode().FromOptional<Node>();
        if (parent is null)
        {
            return null;
        }

        // Case 1: Type x = new HashMap<>() — VariableDeclarator with an explicit type
        if (parent is VariableDeclarator varDecl)
        {
            var varType = varDecl.getType();
            if (varType is ClassOrInterfaceType classType)
            {
                var typeArgs = classType.getTypeArguments().FromOptional<NodeList>()?.ToList<Type>();
                if (typeArgs is { Count: > 0 })
                {
                    var typeName = TypeHelper.ConvertType(context, type.getNameAsString());
                    return SyntaxFactory.GenericName(typeName)
                        .AddTypeArgumentListArguments(typeArgs
                            .Select(t => SyntaxFactory.ParseTypeName(TypeHelper.ConvertType(context, t.toString())))
                            .ToArray());
                }
            }
        }

        // Case 2: x = new HashMap<>() — Assignment to a variable we already know the type of
        if (parent is AssignExpr assignExpr)
        {
            var target = assignExpr.getTarget();
            if (target is NameExpr nameExpr && context.TryGetVariableType(nameExpr.getNameAsString(), out var declaredType))
            {
                var raw = TypeMappingResolver.StripGenericsAndArrays(declaredType);
                if (raw != declaredType)
                {
                    // declaredType contains generics, e.g. "Map<String, Item>" — extract them
                    var genericStart = declaredType.IndexOf('<');
                    if (genericStart >= 0)
                    {
                        var inner = declaredType.Substring(genericStart + 1, declaredType.LastIndexOf('>') - genericStart - 1);
                        var typeName = TypeHelper.ConvertType(context, type.getNameAsString());
                        var typeArgStrings = SplitTopLevelTypeArgs(inner);
                        if (typeArgStrings.Count > 0)
                        {
                            return SyntaxFactory.GenericName(typeName)
                                .AddTypeArgumentListArguments(typeArgStrings
                                    .Select(a => SyntaxFactory.ParseTypeName(TypeHelper.ConvertType(context, a.Trim())))
                                    .ToArray());
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Splits a comma-separated list of type arguments, respecting nested generics
    /// (e.g. <c>"Map&lt;String, Integer&gt;, List&lt;String&gt;"</c> splits into
    /// <c>["Map&lt;String, Integer&gt;", "List&lt;String&gt;"]</c>).
    /// </summary>
    private static List<string> SplitTopLevelTypeArgs(string inner)
    {
        var result = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '<') depth++;
            else if (inner[i] == '>') depth--;
            else if (inner[i] == ',' && depth == 0)
            {
                result.Add(inner.Substring(start, i - start));
                start = i + 1;
            }
        }
        if (start < inner.Length)
        {
            result.Add(inner.Substring(start));
        }
        return result;
    }
}
