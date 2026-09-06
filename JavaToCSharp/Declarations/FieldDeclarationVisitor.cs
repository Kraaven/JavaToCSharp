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
            mapStatic: true, mapFinalToReadOnly: true, mapVolatile: true);

        // Java's package-private default on a nested class field means it is
        // accessible from the enclosing class. In C# a modifier-less field on a
        // public nested class is private, so promote it.
        fieldSyntax = ModifierHelper.PromoteNestedPackagePrivate(fieldSyntax, context, mods);

        return fieldSyntax;
    }

    public override MemberDeclarationSyntax VisitForInterface(ConversionContext context,
        InterfaceDeclarationSyntax interfaceSyntax, FieldDeclaration declaration)
    {
        return VisitForClass(context, null, declaration, new List<ClassOrInterfaceType>(), new List<ClassOrInterfaceType>());
    }
}
