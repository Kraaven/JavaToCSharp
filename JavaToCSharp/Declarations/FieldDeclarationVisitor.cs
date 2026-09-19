using com.github.javaparser.ast;
using com.github.javaparser.ast.body;
using com.github.javaparser.ast.expr;
using com.github.javaparser.ast.type;
using JavaToCSharp.Expressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Declarations;

public class FieldDeclarationVisitor : BodyDeclarationVisitor<FieldDeclaration>
{
    // C# forbids volatile on long/ulong/double/decimal and on structs (CS0677). Java's volatile
    // is legal on any field; the memory-model guarantee is simply dropped for those types.
    private static readonly HashSet<string> VolatileIllegalTypes = new(StringComparer.Ordinal)
    {
        "long", "ulong", "double", "decimal", "TimeSpan",
    };

    private static bool IsVolatileLegal(ConversionContext context, com.github.javaparser.ast.type.Type javaType)
    {
        if (javaType is com.github.javaparser.ast.type.PrimitiveType primitive)
        {
            var primitiveName = primitive.getType().asString();

            return primitiveName is not ("long" or "double");
        }

        var converted = TypeHelper.ConvertType(context, javaType.toString()).Trim();
        var leaf = converted.Split('<')[0].Trim();

        return !VolatileIllegalTypes.Contains(leaf);
    }

    public override MemberDeclarationSyntax VisitForClass(
        ConversionContext context,
        ClassDeclarationSyntax? classSyntax,
        FieldDeclaration fieldDecl,
        IReadOnlyList<ClassOrInterfaceType> extends,
        IReadOnlyList<ClassOrInterfaceType> implements)
    {
        var variables = new List<VariableDeclaratorSyntax>();

        var commonType = fieldDecl.getCommonType();
        int? arrayRank = null;

        var variableDeclarators = fieldDecl.getVariables()?.ToList<VariableDeclarator>() ?? [];

        foreach (var item in variableDeclarators)
        {
            var type = item.getType();

            if (arrayRank is not null && type.getArrayLevel() != arrayRank)
            {
                throw new InvalidOperationException("Different array levels in the same field declaration are not yet supported");
            }

            arrayRank ??= type.getArrayLevel();

            string name = item.getNameAsString();

            if (context.FieldNameRenames.TryGetValue(name, out string? renamedField))
            {
                name = renamedField;
            }

            context.DeclareVariable(name, item.getType().toString());

            var initExpr = item.getInitializer().FromOptional<Expression>();

            if (initExpr is not null)
            {
                var initSyntax = ExpressionVisitor.VisitExpression(context, initExpr);

                if (initSyntax is not null)
                {
                    var varDeclarationSyntax = SyntaxFactory.VariableDeclarator(TypeHelper.EscapeIdentifier(name)).WithInitializer(SyntaxFactory.EqualsValueClause(initSyntax));
                    variables.Add(varDeclarationSyntax);
                }
            }
            else
                variables.Add(SyntaxFactory.VariableDeclarator(TypeHelper.EscapeIdentifier(name)));
        }

        var typeSyntax = TypeHelper.ConvertTypeSyntax(context, commonType, arrayRank ?? 0);

        var fieldSyntax = SyntaxFactory.FieldDeclaration(
            SyntaxFactory.VariableDeclaration(
                typeSyntax,
                SyntaxFactory.SeparatedList(variables, Enumerable.Repeat(SyntaxFactory.Token(SyntaxKind.CommaToken), Math.Max(variables.Count - 1, 0)))));

        var mods = fieldDecl.getModifiers().ToModifierKeywordSet();

        fieldSyntax = ModifierHelper.AddAccessModifiers(fieldSyntax, mods);
        fieldSyntax = ModifierHelper.AddNonAccessModifiers(fieldSyntax, mods,
            mapStatic: true, mapFinalToReadOnly: true, mapVolatile: IsVolatileLegal(context, commonType));

        // Java's package-private default on a nested class field means it is
        // accessible from the enclosing class. In C# a modifier-less field on a
        // public nested class is private, so promote it.
        fieldSyntax = ModifierHelper.PromoteNestedPackagePrivate(fieldSyntax, context, mods);

        return fieldSyntax;
    }

    public override MemberDeclarationSyntax VisitForInterface(ConversionContext context,
        InterfaceDeclarationSyntax interfaceSyntax, FieldDeclaration declaration)
    {
        // C# interfaces cannot declare fields (CS0525). Java interface fields are implicitly
        // public static final constants, so emit each declarator as a static get-only
        // property. This keeps per-file conversion self-contained and preserves cross-file
        // reference syntax such as Codec.BOOL. The getter body evaluates lazily, which also
        // makes forward references between constants legal.
        var variableDeclarators = declaration.getVariables()?.ToList<VariableDeclarator>() ?? [];

        if (variableDeclarators.Count > 1)
        {
            throw new InvalidOperationException("Multiple declarators in the same interface field declaration are not yet supported");
        }

        var declarator = variableDeclarators.FirstOrDefault();

        if (declarator is null)
        {
            throw new InvalidOperationException("Interface field declaration has no declarators");
        }

        string name = declarator.getNameAsString();

        context.DeclareVariable(name, declarator.getType().toString());

        var typeSyntax = TypeHelper.ConvertTypeSyntax(context, declarator.getType(), declarator.getType().getArrayLevel());

        var property = SyntaxFactory.PropertyDeclaration(typeSyntax, TypeHelper.EscapeIdentifier(name))
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword));

        var initExpr = declarator.getInitializer().FromOptional<Expression>();

        if (initExpr is not null)
        {
            var initSyntax = ExpressionVisitor.VisitExpression(context, initExpr);

            if (initSyntax is not null)
            {
                return property
                    .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(initSyntax))
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            }
        }

        // Java interface fields always carry initializers; fall back to an empty getter so
        // the output stays parseable if the initializer could not be converted.
        return property
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))));
    }
}
