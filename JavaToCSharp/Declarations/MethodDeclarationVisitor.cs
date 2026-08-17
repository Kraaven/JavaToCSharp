using com.github.javaparser.ast;
using com.github.javaparser.ast.body;
using com.github.javaparser.ast.expr;
using com.github.javaparser.ast.stmt;
using com.github.javaparser.ast.type;
using JavaToCSharp.Statements;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Parameter = com.github.javaparser.ast.body.Parameter;

namespace JavaToCSharp.Declarations;

public class MethodDeclarationVisitor : BodyDeclarationVisitor<MethodDeclaration>
{
    public override MemberDeclarationSyntax VisitForClass(
        ConversionContext context,
        ClassDeclarationSyntax classSyntax,
        MethodDeclaration methodDecl,
        IReadOnlyList<ClassOrInterfaceType> extends,
        IReadOnlyList<ClassOrInterfaceType> implements)
    {
        return VisitInternal(context, false, classSyntax.Identifier.Text, classSyntax.Modifiers, methodDecl, extends);
    }

    public override MemberDeclarationSyntax VisitForInterface(ConversionContext context,
        InterfaceDeclarationSyntax interfaceSyntax,
        MethodDeclaration methodDecl)
    {
        // If there is a body, mostly treat it like a class method
        if (methodDecl.getBody().isPresent())
        {
            return VisitInternal(context, true, interfaceSyntax.Identifier.Text, interfaceSyntax.Modifiers, methodDecl,
                ArraySegment<ClassOrInterfaceType>.Empty);
        }

        var returnType = methodDecl.getType();
        var returnTypeName = TypeHelper.ConvertType(context, returnType.toString());

        var methodName = context.PreserveJavaMethodNames
            ? methodDecl.getNameAsString()
            : TypeHelper.Capitalize(methodDecl.getNameAsString());
        methodName = TypeHelper.ReplaceCommonMethodNames(methodName);

        string typeParameters = methodDecl.getTypeParameters().ToString() ?? "";
        if (typeParameters.Length > 2)
        {
            // Looks like "[T, U]". Convert to "<T, U>"
            methodName += typeParameters.Replace('[', '<').Replace(']', '>');
        }

        var methodSyntax = SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName(returnTypeName), methodName);

        var parameters = methodDecl.getParameters().ToList<Parameter>() ?? [];

        if (parameters is {Count: > 0})
        {
            var paramSyntax = parameters.Select(i =>
                SyntaxFactory.Parameter(
                    attributeLists: [],
                    modifiers: SyntaxFactory.TokenList(),
                    type: SyntaxFactory.ParseTypeName(TypeHelper.ConvertTypeOf(context, i)),
                    identifier: SyntaxFactory.Identifier(TypeHelper.EscapeIdentifier(i.getNameAsString())),
                    @default: null))
                .ToArray();

            methodSyntax = methodSyntax.AddParameterListParameters(paramSyntax.ToArray());
        }

        methodSyntax = methodSyntax.WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        return methodSyntax;
    }

    private static MemberDeclarationSyntax VisitInternal(
        ConversionContext context,
        bool isInterface,
        string typeIdentifier,
        SyntaxTokenList typeModifiers,
        MethodDeclaration methodDecl,
        IReadOnlyList<ClassOrInterfaceType> extends)
    {
        var returnType = methodDecl.getType();
        var returnTypeName = TypeHelper.ConvertType(context, returnType.toString());

        var methodName = context.PreserveJavaMethodNames
            ? methodDecl.getNameAsString()
            : TypeHelper.Capitalize(methodDecl.getNameAsString());
        methodName = TypeHelper.ReplaceCommonMethodNames(methodName);

        string typeParameters = methodDecl.getTypeParameters().ToString() ?? "";
        if (typeParameters.Length > 2)
        {
            // Looks like "[T, U]". Convert to "<T, U>"
            methodName += typeParameters.Replace('[', '<').Replace(']', '>');
        }

        var methodSyntax = SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName(returnTypeName), methodName);

        var mods = methodDecl.getModifiers().ToModifierKeywordSet();

        methodSyntax = ModifierHelper.AddAccessModifiers(methodSyntax, mods);
        methodSyntax = ModifierHelper.AddNonAccessModifiers(methodSyntax, mods,
            mapStatic: true, mapAbstract: true);

        var annotations = methodDecl.getAnnotations().ToList<AnnotationExpr>();
        bool isOverride = false;

        // TODO: figure out how to check for a non-interface base type
        if (annotations is { Count: > 0 })
        {
            foreach (var annotation in annotations)
            {
                string name = annotation.getNameAsString();

                // ignore @Override on interface-only types; Object methods still need C# override.
                // Record component accessors use @Override in Java but are not virtual in our class form.
                if (name == "Override" && !IsRecordComponentAccessor(context, methodDecl))
                {
                    string javaMethodName = methodDecl.getNameAsString();
                    bool isObjectMethod = javaMethodName is "equals" or "hashCode" or "toString";

                    if (extends.Count > 0 || isObjectMethod || context.IsConvertingEnumConstantBody)
                    {
                        methodSyntax = methodSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.OverrideKeyword));
                        isOverride = true;
                    }
                }
                else if (name == "Deprecated")
                {
                    var obsoleteAttr = AnnotationHelper.ConvertDeprecatedToObsolete(annotation);
                    if (obsoleteAttr is not null)
                    {
                        methodSyntax = methodSyntax.AddAttributeLists(obsoleteAttr);
                    }
                }
            }
        }

        if (!mods.Contains(Modifier.Keyword.FINAL)
            && !mods.Contains(Modifier.Keyword.ABSTRACT)
            && !mods.Contains(Modifier.Keyword.STATIC)
            && !mods.Contains(Modifier.Keyword.PRIVATE)
            && !isOverride
            && !isInterface
            && !typeModifiers.Any(i => i.IsKind(SyntaxKind.SealedKeyword)))
            methodSyntax = methodSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.VirtualKeyword));

        var parameters = methodDecl.getParameters().ToList<Parameter>() ?? [];

        if (parameters is {Count: > 0})
        {
            var paramSyntaxes = new List<ParameterSyntax>();

            foreach (var param in parameters)
            {
                var type = param.getType();
                int arrayLevel = type.getArrayLevel();
                string identifier = TypeHelper.EscapeIdentifier(param.getNameAsString());

                if (param.isVarArgs() && arrayLevel == 0)
                {
                    arrayLevel = 1;
                }

                var modifiers = SyntaxFactory.TokenList();

                if (param.isVarArgs())
                    modifiers = SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.ParamsKeyword));

                var paramSyntax = SyntaxFactory.Parameter(
                    attributeLists: [],
                    modifiers: modifiers,
                    type: TypeHelper.ConvertTypeSyntax(context, type, arrayLevel),
                    identifier: SyntaxFactory.Identifier(identifier),
                    @default: null);

                paramSyntaxes.Add(paramSyntax);
            }

            methodSyntax = methodSyntax.AddParameterListParameters(paramSyntaxes.ToArray());
        }

        var block = methodDecl.getBody().FromOptional<BlockStmt>();

        if (block is null)
        {
            // i.e. abstract method
            methodSyntax = methodSyntax.WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

            return methodSyntax;
        }

        context.PushVariableScope();

        try
        {
            foreach (var param in parameters)
            {
                context.DeclareVariable(param.getNameAsString(), param.getType().toString());
            }

            var statements = block.getStatements().ToList<Statement>();

            var statementSyntax = StatementVisitor.VisitStatements(context, statements);

            if (mods.Contains(Modifier.Keyword.SYNCHRONIZED))
            {
                var lockBlock = SyntaxFactory.Block(statementSyntax);

                var lockSyntax = mods.Contains(Modifier.Keyword.STATIC)
                    ? SyntaxFactory.LockStatement(SyntaxFactory.TypeOfExpression(SyntaxFactory.ParseTypeName(typeIdentifier)), lockBlock)
                    : SyntaxFactory.LockStatement(SyntaxFactory.ThisExpression(), lockBlock);

                methodSyntax = methodSyntax.AddBodyStatements(lockSyntax);
            }
            else
            {
                methodSyntax = methodSyntax.AddBodyStatements(statementSyntax.ToArray());
            }
        }
        finally
        {
            context.PopVariableScope();
        }

        return methodSyntax;
    }

    private static bool IsRecordComponentAccessor(ConversionContext context, MethodDeclaration methodDecl)
    {
        return (methodDecl.getParameters()?.size() ?? 0) == 0
               && context.RecordComponentNames.Contains(methodDecl.getNameAsString());
    }
}
