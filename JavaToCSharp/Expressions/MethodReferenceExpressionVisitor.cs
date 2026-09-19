using com.github.javaparser.ast;
using com.github.javaparser.ast.body;
using com.github.javaparser.ast.expr;
using com.github.javaparser.ast.type;
using JavaToCSharp.TypeMapping;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Type = com.github.javaparser.ast.type.Type;

namespace JavaToCSharp.Expressions;

public class MethodReferenceExpressionVisitor : ExpressionVisitor<MethodReferenceExpr>
{
    protected override ExpressionSyntax Visit(ConversionContext context, MethodReferenceExpr expr)
    {
        // Constructor method references ("Foo::new", "T[]::new") are not valid C# member
        // accesses. Emit a constructor-call lambda: "() => new Foo()" or, when the constructor's
        // arity is resolvable from the enclosing file, "(a, b) => new Foo(a, b)".
        if (string.Equals(expr.getIdentifier(), "new", StringComparison.Ordinal) && expr.getScope() is not null)
        {
            return VisitConstructorReference(context, expr);
        }

        var scope = expr.getScope();
        ExpressionSyntax? scopeSyntax = null;
        bool isUnbound = false;
        string? scopeVariableName = null;

        if (scope is not null)
        {
            // A type on the left of "::" (e.g. "KnownPack::namespace") is an *unbound* instance
            // method reference: the receiver is supplied as the lambda's first argument.
            // However, javaparser also wraps *unresolved variable names* in a TypeExpr (there is
            // no symbol solver), so "out::add" looks like a type reference too. When the simple
            // name resolves as a declared variable, the reference is bound to that variable.
            if (scope is TypeExpr typeExpr)
            {
                scopeVariableName = ExtractSimpleTypeName(typeExpr);
                isUnbound = scopeVariableName is null || !context.TryGetVariableType(scopeVariableName, out _);

                scopeSyntax = isUnbound
                    ? VisitExpression(context, scope)
                    : (ExpressionSyntax)SyntaxFactory.IdentifierName(TypeHelper.EscapeIdentifier(scopeVariableName!));
            }
            else
            {
                scopeSyntax = VisitExpression(context, scope);
            }
        }

        // Same contract as method calls: object-method equivalents always map to their C# names,
        // even when preserving Java method names; other names honor the preserve flag.
        var methodName = TypeHelper.ReplaceCommonMethodNames(
            context.PreserveJavaMethodNames
                ? expr.getIdentifier()
                : TypeHelper.Capitalize(expr.getIdentifier()));

        methodName = TypeHelper.EscapeMemberName(methodName);

        // Determine the referenced method's arity/static-ness so the emitted lambda has the right
        // shape. Without this, "Foo::bar" becomes "() => Foo.bar()" which neither compiles when the
        // functional interface expects arguments nor when the method is an instance method.
        bool methodResolved = TryResolveReferencedMethod(expr, out bool isStatic, out int paramCount);

        // Bound references to a variable whose declared type is a mapped class (e.g. "out::add"
        // where "out" is a java.util.List) can still recover the member's arity and mapped C#
        // name through the mapping index.
        if (!methodResolved
            && !isUnbound
            && scopeVariableName is not null
            && context.TryGetVariableType(scopeVariableName, out var receiverJavaType))
        {
            if (TryResolveMemberThroughMapping(context, receiverJavaType, expr.getIdentifier(),
                    out var mappedParamCount, out var mappedMemberName))
            {
                paramCount = mappedParamCount;
                if (mappedMemberName is not null)
                {
                    methodName = mappedMemberName;
                }

                methodResolved = true;
            }
        }

        // Unbound references to a type's method ("Pattern::asPredicate") resolve through the
        // mapping index the same way: look the scope type up by simple name and match the
        // referenced member. A template entry emits the mapped expression with the receiver
        // lambda parameter as "$0" (e.g. "new Predicate<string>(s => mrefArg.IsMatch(s))"); a
        // rename entry emits an invocation on the receiver parameter. Without this, references
        // into types declared outside the file degraded to a parameterless invocation such as
        // "Regex.asPredicate()", which does not exist on the mapped C# type.
        if (!methodResolved
            && isUnbound
            && scopeVariableName is not null
            && TryResolveTypeScopedReferenceThroughMapping(context, scopeVariableName, expr.getIdentifier(),
                out var typeScopedEntry, out var typeScopedArity)
            && EmitMappedTypeScopedReference(context, expr, typeScopedEntry, typeScopedArity) is { } mappedLambda)
        {
            if (typeScopedEntry.IsStatic)
            {
                // The mapping declares the member static: keep the static-style invocation
                // shape ("Type.Method(args)" — no receiver lambda parameter) and continue
                // through the generic emission path below with the mapped member name.
                methodResolved = true;
                isStatic = true;
                paramCount = typeScopedArity;

                if (typeScopedEntry.CSharpMember is { } staticMappedName)
                {
                    methodName = staticMappedName;
                }
            }
            else
            {
                return mappedLambda;
            }
        }

        var lambdaParameters = new List<ParameterSyntax>();

        // Build the receiver part of the invocation and the parameters feeding it.
        ExpressionSyntax? receiver;
        int valueArgCount;
        bool receiverIsLambdaParameter;

        if (isUnbound)
        {
            if (methodResolved && isStatic)
            {
                // "Type::staticMethod" — no receiver; parameters mirror the method's.
                receiver = scopeSyntax;
                valueArgCount = paramCount;
                receiverIsLambdaParameter = false;
            }
            else
            {
                // "Type::instanceMethod" — receiver becomes the first lambda parameter.
                var receiverName = context.NextTempName("mrefArg");
                lambdaParameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(receiverName)));
                receiver = SyntaxFactory.IdentifierName(receiverName);
                valueArgCount = paramCount;
                receiverIsLambdaParameter = true;
            }
        }
        else
        {
            receiver = scopeSyntax;
            valueArgCount = paramCount;
            receiverIsLambdaParameter = false;
        }

        // If the method could not be resolved in the enclosing file (e.g. a reference into a
        // type imported from another file, typically a static factory like
        // "BoolArgumentType::bool"), fall back to a parameterless static-style invocation on
        // the referenced type instead of an untyped receiver lambda.
        if (!methodResolved && isUnbound)
        {
            // Drop the receiver lambda parameter emitted tentatively above.
            if (lambdaParameters.Count > 0)
            {
                lambdaParameters.RemoveAt(0);
            }

            receiver = scopeSyntax;
            valueArgCount = 0;
            receiverIsLambdaParameter = false;
        }

        for (int i = 0; i < valueArgCount; i++)
        {
            lambdaParameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(context.NextTempName("mrefArg"))));
        }

        var argNames = lambdaParameters
            .Skip(receiverIsLambdaParameter ? 1 : 0)
            .Select(p => (ExpressionSyntax)SyntaxFactory.IdentifierName(p.Identifier.Text))
            .ToList();

        if (!methodResolved && isUnbound)
        {
            argNames.Clear();
        }

        SimpleNameSyntax methodSimpleName = expr.getTypeArguments().FromOptional<NodeList>() is { } typeArgs
            && typeArgs.ToList<Type>() is { Count: > 0 } typeList
            ? SyntaxFactory.GenericName(methodName).AddTypeArgumentListArguments(typeList
                .Select(t => TypeHelper.ConvertTypeSyntax(context, t, 0))
                .ToArray())
            : SyntaxFactory.IdentifierName(methodName);

        InvocationExpressionSyntax invocation;

        if (receiver is null)
        {
            invocation = SyntaxFactory.InvocationExpression(methodSimpleName);
        }
        else
        {
            invocation = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, receiver, methodSimpleName));
        }

        if (argNames.Count > 0)
        {
            invocation = invocation.AddArgumentListArguments(argNames
                .Select(a => SyntaxFactory.Argument(a))
                .ToArray());
        }

        var lambda = SyntaxFactory.ParenthesizedLambdaExpression(invocation);
        if (lambdaParameters.Count > 0)
        {
            lambda = lambda.AddParameterListParameters(lambdaParameters.ToArray());
        }

        return lambda;
    }

    /// <summary>
    /// Extracts the simple name of a <see cref="TypeExpr"/> scope, when it has one.
    /// </summary>
    private static string? ExtractSimpleTypeName(TypeExpr typeExpr)
    {
        var type = typeExpr.getType();
        return type switch
        {
            ClassOrInterfaceType classOrInterface => classOrInterface.getNameAsString(),
            null => null,
            _ => type.toString(),
        };
    }

    /// <summary>
    /// Attempts to recover a bound method reference's arity and mapped C# member name from the
    /// mapping index, using the receiver variable's declared Java type. Returns false when the
    /// receiver type or member is not mapped.
    /// </summary>
    private static bool TryResolveMemberThroughMapping(
        ConversionContext context,
        string receiverJavaType,
        string javaMemberName,
        out int paramCount,
        out string? mappedMemberName)
    {
        paramCount = 0;
        mappedMemberName = null;

        var index = context.Options.MappingIndex;
        if (index is null)
        {
            return false;
        }

        var rawClass = TypeMappingResolver.StripGenericsAndArrays(receiverJavaType);
        if (rawClass.Length == 0
            || !index.TryGetByDottedName(rawClass, out var cls, out _)
            || cls is null)
        {
            return false;
        }

        var candidates = cls.Entries
            .Where(m => string.Equals(m.JavaMember, javaMemberName, StringComparison.Ordinal)
                && m.MemberType == MemberMappingType.Method
                && m.Kind != MemberMappingKind.Unsupported)
            .ToList();

        if (candidates.Count == 0)
        {
            return false;
        }

        // Prefer the entry with the largest explicit argument count (consumer/bi-consumer shape).
        // Variadic entries carry no arity; a method reference consumed by a functional interface
        // almost always forwards exactly one value, so treat variadic as single-argument.
        var withArity = candidates
            .Where(m => m.ArgCount is { } count && !m.IsVariadic)
            .OrderByDescending(m => m.ArgCount.GetValueOrDefault())
            .FirstOrDefault();

        if (withArity is null)
        {
            withArity = candidates.FirstOrDefault(m => m.IsVariadic);
            if (withArity is null)
            {
                return false;
            }

            paramCount = 1;
            mappedMemberName = withArity.CSharpMember;
            return true;
        }

        paramCount = withArity.ArgCount.GetValueOrDefault();
        mappedMemberName = withArity.CSharpMember;
        return true;
    }

    /// <summary>
    /// Attempts to resolve an unbound method reference's member ("Pattern::asPredicate") through
    /// the mapping index, using the simple name of the type on the left of "::". Returns the
    /// matching member entry (template or rename) and the parameter count the emitted lambda
    /// should forward. Returns false when the type or member is not mapped (or the simple name is
    /// ambiguous across the mapping index).
    /// </summary>
    private static bool TryResolveTypeScopedReferenceThroughMapping(
        ConversionContext context,
        string scopeSimpleTypeName,
        string javaMemberName,
        out MemberMapping entry,
        out int paramCount)
    {
        entry = null!;
        paramCount = 0;

        var index = context.Options.MappingIndex;
        if (index is null
            || !index.TryGetBySimpleName(scopeSimpleTypeName, out var cls, out var ambiguous)
            || ambiguous
            || cls is null)
        {
            return false;
        }

        var candidates = cls.Entries
            .Where(m => string.Equals(m.JavaMember, javaMemberName, StringComparison.Ordinal)
                && m.MemberType == MemberMappingType.Method
                && m.Kind != MemberMappingKind.Unsupported)
            .ToList();

        if (candidates.Count == 0)
        {
            return false;
        }

        // Same arity preference as TryResolveMemberThroughMapping: the largest explicit argument
        // count wins; variadic entries forward a single value from a method reference.
        var withArity = candidates
            .Where(m => m.ArgCount is { } count && !m.IsVariadic)
            .OrderByDescending(m => m.ArgCount.GetValueOrDefault())
            .FirstOrDefault();

        if (withArity is null)
        {
            withArity = candidates.FirstOrDefault(m => m.IsVariadic);
            if (withArity is null)
            {
                return false;
            }

            entry = withArity;
            paramCount = 1;
            return true;
        }

        entry = withArity;
        paramCount = withArity.ArgCount.GetValueOrDefault();
        return true;
    }

    /// <summary>
    /// Emits the lambda for an unbound method reference resolved through the mapping index:
    /// "(mrefArg) => mapped" where the receiver type is the first lambda parameter. Template
    /// entries are filled with the receiver parameter as "$0"; rename entries emit an invocation
    /// of the mapped C# member on the receiver parameter with the remaining forwarded arguments.
    /// Returns null when a template entry cannot be filled, letting the caller fall back.
    /// </summary>
    private static ExpressionSyntax? EmitMappedTypeScopedReference(
        ConversionContext context,
        MethodReferenceExpr expr,
        MemberMapping entry,
        int paramCount)
    {
        var receiverName = context.NextTempName("mrefArg");
        var lambdaParameters = new List<ParameterSyntax>
        {
            SyntaxFactory.Parameter(SyntaxFactory.Identifier(receiverName)),
        };

        var valueArgNames = new List<string>();
        for (var i = 0; i < paramCount; i++)
        {
            var name = context.NextTempName("mrefArg");
            lambdaParameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(name)));
            valueArgNames.Add(name);
        }

        ExpressionSyntax body;

        if (entry.Kind == MemberMappingKind.Template && entry.Expression is not null)
        {
            if (!TypeMappingResolver.TryFillTemplate(context, receiverName, entry.Expression, null, out var filled))
            {
                return null;
            }

            body = filled;
        }
        else
        {
            var memberName = entry.CSharpMember
                ?? (context.PreserveJavaMethodNames ? entry.JavaMember : TypeHelper.Capitalize(entry.JavaMember));
            var methodSimpleName = SyntaxFactory.IdentifierName(TypeHelper.EscapeMemberName(memberName));

            var invocation = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(receiverName),
                    methodSimpleName));

            if (valueArgNames.Count > 0)
            {
                invocation = invocation.AddArgumentListArguments(valueArgNames
                    .Select(a => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(a)))
                    .ToArray());
            }

            body = invocation;
        }

        var lambda = SyntaxFactory.ParenthesizedLambdaExpression(body);
        return lambda.AddParameterListParameters(lambdaParameters.ToArray());
    }

    /// <summary>
    /// Resolves the method named by the reference within the enclosing type chain of the current
    /// file, reporting whether it is static and how many parameters it declares. Best-effort:
    /// types pulled in via imports (or produced by type mappings) cannot be resolved here.
    /// </summary>
    private static bool TryResolveReferencedMethod(MethodReferenceExpr expr, out bool isStatic, out int paramCount)
    {
        isStatic = false;
        paramCount = 0;

        var identifier = expr.getIdentifier();
        Node? current = expr;

        while (current is not null)
        {
            current = current.getParentNode().FromOptional<Node>();

            if (current is TypeDeclaration typeDeclaration)
            {
                // Search this type and its nested types (e.g. a reference inside a nested
                // class to a member of a sibling nested class).
                if (TryFindMethodInType(typeDeclaration, identifier, out isStatic, out paramCount))
                {
                    return true;
                }

                foreach (var nested in typeDeclaration.getMembers().ToList<Node>()?.OfType<TypeDeclaration>() ?? [])
                {
                    if (TryFindMethodInType(nested, identifier, out isStatic, out paramCount))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool ResolveCtorArity(List<ConstructorDeclaration>? ctors, out int paramCount)
    {
        paramCount = 0;

        if (ctors is not { Count: > 0 })
        {
            // Default constructor only.
            return true;
        }

        if (ctors.Count == 1)
        {
            paramCount = ctors[0].getParameters()?.size() ?? 0;
            return true;
        }

        // Ambiguous overloads: assume the canonical (widest) constructor.
        paramCount = ctors.Max(c => c.getParameters()?.size() ?? 0);
        return true;
    }

    private static bool TryFindMethodInType(TypeDeclaration type, string methodName, out bool isStatic, out int paramCount)
    {
        isStatic = false;
        paramCount = 0;

        var methods = type.getMethodsByName(methodName).ToList<MethodDeclaration>();

        if (methods is { Count: > 0 })
        {
            var method = methods[0];
            isStatic = method.isStatic();
            paramCount = method.getParameters()?.size() ?? 0;
            return true;
        }

        return false;
    }

    private static ExpressionSyntax VisitConstructorReference(ConversionContext context, MethodReferenceExpr expr)
    {
        ExpressionSyntax newExpression;

        if (expr.getScope() is TypeExpr typeExpr && typeExpr.getType() is ArrayType arrayType)
        {
            newExpression = CreateArrayConstructorExpression(context, arrayType);
            return SyntaxFactory.ParenthesizedLambdaExpression(newExpression);
        }

        var typeName = expr.getScope() is TypeExpr typedScope && typedScope.getType() is ClassOrInterfaceType classOrInterfaceType
            ? classOrInterfaceType.getNameAsString()
            : expr.getScope()?.toString() ?? string.Empty;
        var typeSyntax = SyntaxFactory.ParseTypeName(TypeHelper.ConvertType(context, typeName));

        // Resolve the constructor's arity when the type is declared in the current file so the
        // lambda receives and forwards the expected arguments.
        var parameters = new List<ParameterSyntax>();
        var arguments = new List<ArgumentSyntax>();

        if (TryResolveConstructorArity(context, expr, out int ctorParamCount) && ctorParamCount > 0)
        {
            for (int i = 0; i < ctorParamCount; i++)
            {
                var name = context.NextTempName("ctorRefArg");
                parameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(name)));
                arguments.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(name)));
            }
        }

        var creation = SyntaxFactory.ObjectCreationExpression(typeSyntax, SyntaxFactory.ArgumentList(), null);
        if (arguments.Count > 0)
        {
            creation = creation.AddArgumentListArguments(arguments.ToArray());
        }

        var lambda = SyntaxFactory.ParenthesizedLambdaExpression(creation);
        if (parameters.Count > 0)
        {
            lambda = lambda.AddParameterListParameters(parameters.ToArray());
        }

        return lambda;
    }

    /// <summary>
    /// Attempts to determine the constructor arity for a "Type::new" reference by locating the
    /// type declaration in the enclosing file. When the type declares exactly one constructor its
    /// parameter count is used; when several exist the one with the most parameters is preferred
    /// (records and canonical constructors). Returns false when the type cannot be resolved.
    /// </summary>
    private static bool TryResolveConstructorArity(ConversionContext context, MethodReferenceExpr expr, out int paramCount)
    {
        paramCount = 0;

        var typeName = expr.getScope() is TypeExpr typedScope && typedScope.getType() is ClassOrInterfaceType classOrInterfaceType
            ? classOrInterfaceType.getNameAsString()
            : expr.getScope()?.toString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(typeName))
        {
            return false;
        }

        var simpleName = typeName.Split('.').Last();

        Node? current = expr;

        while (current is not null)
        {
            current = current.getParentNode().FromOptional<Node>();

            if (current is TypeDeclaration typeDeclaration
                && string.Equals(typeDeclaration.getNameAsString(), simpleName, StringComparison.Ordinal))
            {
                var ctors = typeDeclaration.getConstructors().ToList<ConstructorDeclaration>();
                return ResolveCtorArity(ctors, out paramCount);
            }

            // Also search nested types (e.g. "Outer.Inner::new" from inside Outer).
            if (current is TypeDeclaration outerType)
            {
                foreach (var nested in outerType.getMembers().ToList<Node>()?.OfType<TypeDeclaration>() ?? [])
                {
                    if (string.Equals(nested.getNameAsString(), simpleName, StringComparison.Ordinal))
                    {
                        var nestedCtors = nested.getConstructors().ToList<ConstructorDeclaration>();
                        return ResolveCtorArity(nestedCtors, out paramCount);
                    }
                }
            }
        }

        return false;
    }

    // "T[]::new" -> "() => new T[0]"; "T[][]::new" -> "() => new T[0][]".
    private static ExpressionSyntax CreateArrayConstructorExpression(ConversionContext context, ArrayType arrayType)
    {
        var elementType = arrayType.getElementType();
        while (elementType is ArrayType nestedArrayType)
        {
            elementType = nestedArrayType;
        }

        var rankSpecifiers = new List<ArrayRankSpecifierSyntax>();
        for (var i = 0; i < arrayType.getArrayLevel(); i++)
        {
            rankSpecifiers.Add(i == 0
                ? SyntaxFactory.ArrayRankSpecifier(SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                    SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0))))
                : SyntaxFactory.ArrayRankSpecifier(SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                    SyntaxFactory.OmittedArraySizeExpression())));
        }

        var elementSyntax = SyntaxFactory.ParseTypeName(TypeHelper.ConvertType(context, elementType));
        var arrayTypeSyntax = SyntaxFactory.ArrayType(elementSyntax, SyntaxFactory.List(rankSpecifiers));
        return SyntaxFactory.ArrayCreationExpression(arrayTypeSyntax);
    }
}

