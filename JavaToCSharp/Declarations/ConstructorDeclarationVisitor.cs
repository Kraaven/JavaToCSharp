using com.github.javaparser.ast;
using com.github.javaparser.ast.body;
using com.github.javaparser.ast.stmt;
using com.github.javaparser.ast.type;
using JavaToCSharp.Statements;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Declarations;

public class ConstructorDeclarationVisitor : BodyDeclarationVisitor<ConstructorDeclaration>
{
    public override MemberDeclarationSyntax? VisitForClass(
        ConversionContext context,
        ClassDeclarationSyntax classSyntax,
        ConstructorDeclaration ctorDecl,
        IReadOnlyList<ClassOrInterfaceType> extends,
        IReadOnlyList<ClassOrInterfaceType> implements)
    {
        string? identifier = classSyntax.Identifier.Value?.ToString();
        if (identifier is null)
        {
            return null;
        }

        var ctorSyntax = SyntaxFactory.ConstructorDeclaration(identifier).WithLeadingNewLines();

        var mods = ctorDecl.getModifiers().ToModifierKeywordSet();

        ctorSyntax = ModifierHelper.AddAccessModifiers(ctorSyntax, mods);

        // Java's package-private default on a nested class constructor means it is
        // accessible from the enclosing class. In C# a modifier-less constructor on a
        // public nested class is private, so promote it.
        ctorSyntax = ModifierHelper.PromoteNestedPackagePrivate(ctorSyntax, context, mods);

        var parameters = ctorDecl.getParameters().ToList<Parameter>() ?? [];

        if (parameters is {Count: > 0})
        {
            var paramSyntaxes = new List<ParameterSyntax>();

            foreach (var param in parameters)
            {
                var name = param.getNameAsString();
                var paramSyntax = SyntaxFactory.Parameter(SyntaxFactory.Identifier(TypeHelper.EscapeIdentifier(name)));

                if (param.isVarArgs())
                {
                    paramSyntax = paramSyntax.WithType(TypeHelper.ConvertArrayTypeSyntax(context, param.getType(), 1))
                        .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.ParamsKeyword)));
                }
                else
                {
                    paramSyntax = paramSyntax.WithType(SyntaxFactory.ParseTypeName(TypeHelper.ConvertTypeOf(context, param)));
                }

                paramSyntaxes.Add(paramSyntax);
            }

            ctorSyntax = ctorSyntax.AddParameterListParameters(paramSyntaxes.ToArray());
        }

        var block = ctorDecl.getBody();
        var statements = block.getStatements().ToList<Statement>() ?? [];

        var (initializer, remainingStatements) = ConstructorConversion.ExtractInitializer(context, statements);
        if (initializer is not null)
        {
            ctorSyntax = ctorSyntax.WithInitializer(initializer);
        }

        context.PushVariableScope();

        try
        {
            foreach (var param in parameters)
            {
                context.DeclareVariable(param.getNameAsString(), param.getType().toString());
            }

            var statementSyntax = StatementVisitor.VisitStatements(context, remainingStatements);

            ctorSyntax = ctorSyntax.AddBodyStatements(statementSyntax.ToArray());
        }
        finally
        {
            context.PopVariableScope();
        }

        return ctorSyntax;
    }

    public override MemberDeclarationSyntax VisitForInterface(ConversionContext context, InterfaceDeclarationSyntax interfaceSyntax, ConstructorDeclaration declaration)
    {
        throw new InvalidOperationException("Constructors are not valid on interfaces.");
    }
}
