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
            else
            {
                // A raw generic ("new ArrayList()") has no arguments to infer and is illegal in
                // C# (CS0305): synthesize object arguments for known generic types.
                typeSyntax = SyntaxFactory.ParseTypeName(
                    TypeHelper.ConvertType(context, TypeHelper.GetFullTypeName(context, type)));
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
        // The hoisted class name must be a plain C# identifier: only the Java simple name may
        // flow into it. Raw-argument synthesis inside ConvertType would otherwise splice
        // "<...>" (and a counter directly after ">") into the identifier, producing unparseable
        // output such as `AnonymousCodec<object>1`.
        string baseIdentifier = SanitizeAnonymousBaseName(newExpr.getType().getNameAsString());

        string anonTypeName = string.Empty;

        for (int i = 0; i < 100; i++)
        {
            if (i == 99)
            {
                throw new InvalidOperationException("Too many anonymous types");
            }

            anonTypeName = $"Anonymous{baseIdentifier}{(i == 0 ? string.Empty : i.ToString())}";

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
                // The declared type (including any type arguments, explicit or synthesized)
                // belongs in the base list — not in the hoisted class's identifier.
                SyntaxFactory.SimpleBaseType(TypeHelper.GetSyntaxFromType(context, newExpr.getType()))
            })));

        // Java anonymous classes capture the enclosing instance only when the creation
        // expression sits in an instance (non-static) member context. Static fields, static
        // methods and interface members never capture — and emitting `this` there would not
        // even be valid C#.
        string? contextLastTypeName = IsInStaticMemberContext(newExpr) ? null : context.LastTypeName;

        // Java anonymous bodies see the enclosing type's type parameters; C# nested classes do
        // not. Make the hoisted class generic over the enclosing parameters the body actually
        // references, and pass them at the usage site — preserving types instead of erasing
        // them to object.
        var bodyText = string.Join("\n", anonBody.Select(member => member.toString()));
        var referencedParameters = new List<TypeParameter>();
        var seenParameterNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var typeParameter in context.TypeParameterNodes)
        {
            var parameterName = typeParameter.getNameAsString();

            if (seenParameterNames.Add(parameterName)
                && System.Text.RegularExpressions.Regex.IsMatch(bodyText, @"\b" + System.Text.RegularExpressions.Regex.Escape(parameterName) + @"\b"))
            {
                referencedParameters.Add(typeParameter);
            }
        }

        if (contextLastTypeName is not null)
        {
            // The enclosing instance carries the enclosing type's own type parameters
            // (`Sorter.this` inside `class Sorter<T>` is Sorter<T>) — a raw spelling of a
            // generic type is CS0305 in the hoisted class. The hoisted class is generic over
            // the parameters the body references (same set), so spell them out; mismatched
            // arities fall back to object fill.
            var parentTypeText = contextLastTypeName;
            var parentArity = 0;
            context.Options.KnownGenericArity?.TryGetValue(contextLastTypeName, out parentArity);

            if (parentArity > 0)
            {
                var parentArgs = referencedParameters.Select(parameter => parameter.getNameAsString()).ToList();

                if (parentArgs.Count != parentArity)
                {
                    parentArgs = context.TypeParameterNames.Take(parentArity).ToList();
                }

                parentTypeText += "<" + string.Join(", ",
                    parentArgs.Count == parentArity ? parentArgs : Enumerable.Repeat("object", parentArity)) + ">";
            }

            var parentField = SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName(parentTypeText))
                                                                          .AddVariables(SyntaxFactory.VariableDeclarator("parent")))
                                           .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));

            var ctorSyntax = SyntaxFactory.ConstructorDeclaration(anonTypeName)
                                          .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                                          .AddParameterListParameters(SyntaxFactory.Parameter(SyntaxFactory.ParseToken("parent")).WithType(SyntaxFactory.ParseTypeName(parentTypeText)))
                                          .AddBodyStatements(SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.ThisExpression(), SyntaxFactory.IdentifierName("parent")), SyntaxFactory.IdentifierName("parent"))));

            classSyntax = classSyntax.AddMembers(ctorSyntax, parentField);
        }

        if (referencedParameters.Count > 0)
        {
            classSyntax = classSyntax.AddTypeParameterListParameters(referencedParameters
                .Select(parameter => SyntaxFactory.TypeParameter(parameter.getNameAsString())).ToArray());
            classSyntax = classSyntax.AddConstraintClauses(TypeHelper.GetTypeParameterListConstraints(context, referencedParameters).ToArray());
        }

        var usageTypeName = referencedParameters.Count > 0
            ? anonTypeName + "<" + string.Join(", ", referencedParameters.Select(parameter => parameter.getNameAsString())) + ">"
            : anonTypeName;

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
            var creation = SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(usageTypeName));

            if (contextLastTypeName is not null)
            {
                return creation.AddArgumentListArguments(SyntaxFactory.Argument(SyntaxFactory.ThisExpression()));
            }

            return creation.WithArgumentList(SyntaxFactory.ArgumentList());
        }

        return SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(usageTypeName),
            TypeHelper.GetSyntaxFromArguments(context, args),
            null);
    }

    private static string SanitizeAnonymousBaseName(string name)
    {
        var identifierChars = (name ?? string.Empty).Where(char.IsLetterOrDigit).ToArray();

        if (identifierChars.Length == 0)
        {
            return "Type";
        }

        return char.IsDigit(identifierChars[0])
            ? "_" + new string(identifierChars)
            : new string(identifierChars);
    }

    /// <summary>
    /// Walks up from the anonymous creation expression to the nearest enclosing member and
    /// reports whether that member is static. Static contexts (static fields/methods, static
    /// initializers, interface constants) have no enclosing instance in Java, so the hoisted
    /// class must not receive a parent field or a `this` argument.
    /// </summary>
    private static bool IsInStaticMemberContext(ObjectCreationExpr newExpr)
    {
        var parent = newExpr.getParentNode().FromOptional<Node>();

        while (parent is not null)
        {
            switch (parent)
            {
                case MethodDeclaration method:
                    // Default interface methods are instance contexts; only explicitly static
                    // methods (or static interface methods) are static.
                    return method.getModifiers().ToModifierKeywordSet().Contains(com.github.javaparser.ast.Modifier.Keyword.STATIC);
                case ConstructorDeclaration:
                    return false;
                case FieldDeclaration field:
                    // Interface fields are implicitly static even without the modifier.
                    return field.getModifiers().ToModifierKeywordSet().Contains(com.github.javaparser.ast.Modifier.Keyword.STATIC)
                        || IsDeclaredInInterface(field);
                case InitializerDeclaration initializer:
                    return initializer.isStatic();
                case EnumConstantDeclaration:
                    // An enum constant's body is an instance context of its anonymous subclass.
                    return false;
            }

            parent = parent.getParentNode().FromOptional<Node>();
        }

        return false;
    }

    private static bool IsDeclaredInInterface(Node node)
    {
        var parent = node.getParentNode().FromOptional<Node>();

        while (parent is not null)
        {
            if (parent is ClassOrInterfaceDeclaration typeDeclaration)
            {
                // The first type declaration up the chain is the declaring type.
                return typeDeclaration.isInterface();
            }

            parent = parent.getParentNode().FromOptional<Node>();
        }

        return false;
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
                    var typeName = TypeHelper.ConvertType(context, type.getNameAsString(), synthesizeRawArguments: false);
                    return SyntaxFactory.GenericName(typeName)
                        .AddTypeArgumentListArguments(typeArgs
                            .Select(t => TypeHelper.ConvertTypeSyntax(context, t, 0))
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
                        var typeName = TypeHelper.ConvertType(context, type.getNameAsString(), synthesizeRawArguments: false);
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

