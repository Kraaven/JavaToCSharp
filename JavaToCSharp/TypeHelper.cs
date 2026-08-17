using com.github.javaparser.ast.expr;
using com.github.javaparser.ast.type;
using JavaToCSharp.Expressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Ast = com.github.javaparser.ast;
using Type = com.github.javaparser.ast.type.Type;

namespace JavaToCSharp;

public static class TypeHelper
{
    private static readonly Dictionary<string, string> _typeNameConversions = new()
    {
        // Simple types
        ["boolean"] = "bool",
        ["Boolean"] = "bool",
        ["ICloseable"] = "IDisposable",
        ["Integer"] = "int",
        ["Long"] = "long",
        ["Float"] = "float",
        ["String"] = "string",
        ["Object"] = "object",
        ["AutoCloseable"] = "IDisposable",

        // Generic types
        ["ArrayList"] = "List",
        ["List"] = "IList",
        ["Map"] = "Dictionary",
        ["Set"] = "HashSet",
        ["Iterator"] = "IEnumerator",
        ["Comparable"] = "IComparable",

        // Exceptions
        ["AlreadyClosedException"] = "ObjectDisposedException",
        ["Error"] = "Exception",
        ["IllegalArgumentException"] = "ArgumentException",
        ["IllegalStateException"] = "InvalidOperationException",
        ["UnsupportedOperationException"] = "NotSupportedException",
        ["RuntimeException"] = "Exception",
        ["AccessDeniedException"] = "UnauthorizedAccessException",
        ["AssertionError"] = "InvalidOperationException",
        ["NullPointerException"] = "NullReferenceException",
        ["UncheckedIOException"] = "IOException",
        ["EOFException"] = "EndOfStreamException",
        ["NoSuchFileException"] = "FileNotFoundException",
    };

    public static string ConvertTypeOf(Ast.nodeTypes.NodeWithType typedNode)
    {
        return ConvertType(typedNode.getType().toString());
    }

    public static string ConvertType(Type type)
    {
        return ConvertType(type.toString());
    }

    public static string ConvertType(string typeName)
        => TypeNameParser.ParseTypeName(typeName, s => _typeNameConversions.TryGetValue(s, out string? converted) ? converted : s);

    /// <summary>
    /// Converts a type name, first rewriting mapped names through <see cref="TypeMappingIndex"/>
    /// (when configured) and then applying the built-in dictionary fallback. Mapped names override
    /// the dictionary (e.g. <c>Map</c> → <c>IDictionary</c> instead of <c>Dictionary</c>).
    /// </summary>
    public static string ConvertType(ConversionContext context, string typeName)
    {
        if (context.Options.MappingIndex is not null)
        {
            TypeMapping.TypeMappingResolver.TryResolveTypeName(context, typeName, out var mapped);
            return mapped;
        }

        return ConvertType(typeName);
    }

    public static string ConvertType(ConversionContext context, Type type)
        => ConvertType(context, type.toString());

    public static string ConvertTypeOf(ConversionContext context, Ast.nodeTypes.NodeWithType typedNode)
        => ConvertType(context, typedNode.getType().toString());

    public static TypeSyntax ConvertTypeSyntax(ConversionContext context, Type type, int arrayRank)
    {
        if (type is ArrayType arrayType)
        {
            if (arrayRank > 0 && arrayType.getArrayLevel() != arrayRank)
            {
                throw new ArgumentException("Given array rank does not match the array level of the type", nameof(arrayRank));
            }

            arrayRank = arrayType.getArrayLevel();
            Type elementType;

            while ((elementType = arrayType.getElementType()) is ArrayType nestedArrayType)
            {
                arrayType = nestedArrayType;
            }

            return ConvertTypeSyntax(context, elementType, arrayRank);
        }

        return arrayRank == 0
            ? SyntaxFactory.ParseTypeName(ConvertType(context, type))
            : ConvertArrayTypeSyntax(context, type, arrayRank);
    }

    public static ArrayTypeSyntax ConvertArrayTypeSyntax(ConversionContext context, Type type, int arrayRank)
    {
        var rankSpecifiers = SyntaxFactory.ArrayRankSpecifier(
            SyntaxFactory.SeparatedList<ExpressionSyntax>(
                Enumerable.Repeat(SyntaxFactory.OmittedArraySizeExpression(), arrayRank)
            ));

        return SyntaxFactory.ArrayType(ConvertTypeSyntax(context, type, 0))
            .WithRankSpecifiers(SyntaxFactory.SingletonList(rankSpecifiers));
    }

    public static TypeSyntax ConvertTypeSyntax(Type type, int arrayRank)
    {
        if (type is ArrayType arrayType)
        {
            if (arrayRank > 0 && arrayType.getArrayLevel() != arrayRank)
            {
                throw new ArgumentException("Given array rank does not match the array level of the type", nameof(arrayRank));
            }

            arrayRank = arrayType.getArrayLevel();
            Type elementType;

            while ((elementType = arrayType.getElementType()) is ArrayType nestedArrayType)
            {
                arrayType = nestedArrayType;
            }

            return ConvertTypeSyntax(elementType, arrayRank);
        }

        return arrayRank == 0
            ? SyntaxFactory.ParseTypeName(ConvertType(type))
            : ConvertArrayTypeSyntax(type, arrayRank);
    }

    public static ArrayTypeSyntax ConvertArrayTypeSyntax(Type type, int arrayRank)
    {
        var rankSpecifiers = SyntaxFactory.ArrayRankSpecifier(
            SyntaxFactory.SeparatedList<ExpressionSyntax>(
                Enumerable.Repeat(SyntaxFactory.OmittedArraySizeExpression(), arrayRank)
            ));

        return SyntaxFactory.ArrayType(ConvertTypeSyntax(type, 0))
            .WithRankSpecifiers(SyntaxFactory.SingletonList(rankSpecifiers));
    }

    public static string Capitalize(string name)
    {
        var parts = name.Split('.');

        var joined = string.Join(".", parts.Select(i =>
        {
            if (i.Length == 1)
                return i.ToUpper();
            else
                return i[0].ToString().ToUpper() + i[1..];
        }));

        return joined;
    }

    public static string EscapeIdentifier(string name)
    {
        // @ (C# Reference): https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/tokens/verbatim
        // Only reserved keywords need escaping; contextual keywords (type, var, value, …) are valid identifiers.
        var kind = SyntaxFacts.GetKeywordKind(name);
        if (kind != SyntaxKind.None && SyntaxFacts.IsReservedKeyword(kind))
        {
            return "@" + name;
        }

        return name;
    }

    public static string ReplaceCommonMethodNames(string name)
    {
        return name.ToLower() switch {
            "hashcode" => "GetHashCode",
            "getclass" => "GetType",
            "close" => "Dispose",
            "touppercase" => "ToUpper",
            "tolowercase" => "ToLower",
            "equals" => "Equals",
            "tostring" => "ToString",
            "trim" => "Trim",
            "readline" => "ReadLine",
            "read" => "Read",
            "ready" => "EndOfStream",  // handled as property below
            "getmessage" => "Message",
            "abs" => "Abs",
            "compareto" => "CompareTo",
            _ => name,
        };
    }

    /// <summary>
    /// Normalizes common Java field names to their C# equivalents
    /// (e.g. <c>length</c> → <c>Length</c>).
    /// </summary>
    public static string ReplaceCommonFieldNames(string name)
    {
        return name.ToLower() switch {
            "length" => "Length",
            _ => name,
        };
    }

    /// <summary>
    /// Replaces <c>TWildcardTodo</c> placeholders from Java <c>?</c> wildcards with the
    /// current type's type parameter names when available.
    /// </summary>
    public static string ReplaceWildcardsWithTypeParameters(string typeName, IList<string> typeParameterNames)
    {
        if (typeParameterNames.Count == 0 || !typeName.Contains("TWildcardTodo", StringComparison.Ordinal))
        {
            return typeName;
        }

        int paramIndex = 0;
        while (typeName.Contains("TWildcardTodo", StringComparison.Ordinal) && paramIndex < typeParameterNames.Count)
        {
            typeName = ReplaceFirst(typeName, "TWildcardTodo", typeParameterNames[paramIndex]);
            paramIndex++;
        }

        return typeName;
    }

    private static string ReplaceFirst(string text, string search, string replace)
    {
        int index = text.IndexOf(search, StringComparison.Ordinal);
        return index < 0 ? text : string.Concat(text.AsSpan(0, index), replace, text.AsSpan(index + search.Length));
    }

    public static TypeSyntax GetSyntaxFromType(ClassOrInterfaceType type)
    {
        string typeName = ConvertType(type.getNameAsString());
        var typeArgs = type.getTypeArguments().FromOptional<Ast.NodeList>()?.ToList<Type>();

        TypeSyntax typeSyntax;

        if (typeArgs is { Count: > 0 })
        {
            typeSyntax = SyntaxFactory.GenericName(typeName)
                .AddTypeArgumentListArguments(typeArgs.Select(t => SyntaxFactory.ParseTypeName(ConvertType(t))).ToArray());
        }
        else
        {
            typeSyntax = SyntaxFactory.ParseTypeName(typeName);
        }

        return typeSyntax;
    }

    /// <summary>
    /// Builds a C# type syntax from a Java class/interface type, resolving the type name (and its
    /// generic arguments) through the mapping index when configured.
    /// </summary>
    public static TypeSyntax GetSyntaxFromType(ConversionContext context, ClassOrInterfaceType type)
    {
        string typeName = ConvertType(context, type.getNameAsString());
        var typeArgs = type.getTypeArguments().FromOptional<Ast.NodeList>()?.ToList<Type>();

        TypeSyntax typeSyntax;

        if (typeArgs is { Count: > 0 })
        {
            typeSyntax = SyntaxFactory.GenericName(typeName)
                .AddTypeArgumentListArguments(typeArgs.Select(t => SyntaxFactory.ParseTypeName(ConvertType(context, t))).ToArray());
        }
        else
        {
            typeSyntax = SyntaxFactory.ParseTypeName(typeName);
        }

        return typeSyntax;
    }

    public static ArgumentListSyntax GetSyntaxFromArguments(ConversionContext context, java.util.List args)
    {
        return SyntaxFactory.ArgumentList(GetSeparatedListFromArguments(context, args));
    }

    private static SeparatedSyntaxList<ArgumentSyntax> GetSeparatedListFromArguments(ConversionContext context, java.util.List args)
    {
        return GetSeparatedListFromArguments(context, args.OfType<Expression>());
    }

    private static SeparatedSyntaxList<ArgumentSyntax> GetSeparatedListFromArguments(ConversionContext context, IEnumerable<Expression> args)
    {
        var argSyntaxes = new List<ArgumentSyntax>();

        foreach (var arg in args)
        {
            var argSyntax = ExpressionVisitor.VisitExpression(context, arg);
            if (argSyntax is null)
            {
                continue;
            }

            argSyntaxes.Add(SyntaxFactory.Argument(argSyntax));
        }

        var separators = argSyntaxes.Count == 0
            ? Enumerable.Empty<SyntaxToken>()
            : Enumerable.Repeat(SyntaxFactory.Token(SyntaxKind.CommaToken), argSyntaxes.Count - 1);
        return SyntaxFactory.SeparatedList(argSyntaxes, separators);
    }

    /// <summary>
    /// Returns the list of C# type parameter constraints that must be added to a class syntax
    /// to convert the java bounded type parameters of a class declaration.
    /// e.g. to convert <![CDATA[ <T extends Clazz<T>> ]]> into <![CDATA[ <T> where T : Clazz<T> ]]>
    /// </summary>
    public static IEnumerable<TypeParameterConstraintClauseSyntax> GetTypeParameterListConstraints(List<TypeParameter> typeParams)
    {
        var typeParameterConstraints = new List<TypeParameterConstraintClauseSyntax>();

        foreach (TypeParameter typeParam in typeParams)
        {
            if (typeParam.getTypeBound().size() > 0)
            {
                var typeConstraintsSyntax = new SeparatedSyntaxList<TypeParameterConstraintSyntax>();

                foreach (ClassOrInterfaceType bound in typeParam.getTypeBound())
                {
                    typeConstraintsSyntax = typeConstraintsSyntax.Add(SyntaxFactory.TypeConstraint(SyntaxFactory.ParseTypeName(bound.asString())));
                }

                var typeIdentifier = SyntaxFactory.IdentifierName(typeParam.getName().asString());
                var parameterConstraintClauseSyntax = SyntaxFactory.TypeParameterConstraintClause(typeIdentifier, typeConstraintsSyntax);

                typeParameterConstraints.Add(parameterConstraintClauseSyntax);
            }
        }

        return typeParameterConstraints;
    }
}
