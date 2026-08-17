using com.github.javaparser.ast;
using com.github.javaparser.ast.body;
using com.github.javaparser.ast.expr;
using com.github.javaparser.ast.type;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Declarations;

public class ClassOrInterfaceDeclarationVisitor : BodyDeclarationVisitor<ClassOrInterfaceDeclaration>
{
    public override MemberDeclarationSyntax? VisitForClass(
        ConversionContext context,
        ClassDeclarationSyntax classSyntax,
        ClassOrInterfaceDeclaration declaration,
        IReadOnlyList<ClassOrInterfaceType> extends,
        IReadOnlyList<ClassOrInterfaceType> implements)
    {
        return VisitClassDeclaration(context, declaration);
    }

    public override MemberDeclarationSyntax? VisitForInterface(ConversionContext context,
        InterfaceDeclarationSyntax interfaceSyntax,
        ClassOrInterfaceDeclaration declaration)
    {
        return VisitClassDeclaration(context, declaration);
    }

    public static InterfaceDeclarationSyntax VisitInterfaceDeclaration(ConversionContext context,
        ClassOrInterfaceDeclaration interfaceDecl, bool isNested = false)
    {
        var newTypeName = interfaceDecl.getName().getIdentifier();

        if (!isNested)
        {
            context.RootTypeName = newTypeName;
        }

        context.LastTypeName = newTypeName;

        var classSyntax = SyntaxFactory.InterfaceDeclaration(newTypeName);

        var typeParams = interfaceDecl.getTypeParameters().ToList<TypeParameter>();

        if (typeParams is { Count: > 0 })
        {
            classSyntax = classSyntax.AddTypeParameterListParameters(typeParams
                    .Select(i => SyntaxFactory.TypeParameter(i.getNameAsString())).ToArray());
        }

        var mods = interfaceDecl.getModifiers().ToModifierKeywordSet();

        classSyntax = ModifierHelper.AddAccessModifiers(classSyntax, mods);
        if (mods.Contains(Modifier.Keyword.FINAL))
            classSyntax = classSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.SealedKeyword));

        var extends = interfaceDecl.getExtendedTypes().ToList<ClassOrInterfaceType>();

        if (extends is not null)
        {
            foreach (var extend in extends)
            {
                classSyntax = classSyntax.AddBaseListTypes(SyntaxFactory.SimpleBaseType(TypeHelper.GetSyntaxFromType(context, extend)));
            }
        }

        var implements = interfaceDecl.getImplementedTypes().ToList<ClassOrInterfaceType>();

        if (implements is not null)
        {
            foreach (var implement in implements)
            {
                classSyntax = classSyntax.AddBaseListTypes(SyntaxFactory.SimpleBaseType(TypeHelper.GetSyntaxFromType(context, implement)));
            }
        }

        var members = interfaceDecl.getMembers()?.ToList<BodyDeclaration>();

        if (members is not null)
        {
            context.PushVariableScope();

            try
            {
                foreach (var member in members)
                {
                    var syntax = VisitBodyDeclarationForInterface(context, classSyntax, member);
                    var memberWithComments = syntax?.WithJavaComments(context, member);

                    if (memberWithComments is not null)
                    {
                        classSyntax = classSyntax.AddMembers(memberWithComments);
                    }
                }
            }
            finally
            {
                context.PopVariableScope();
            }
        }

        return classSyntax.WithJavaComments(context, interfaceDecl);
    }

    public static ClassDeclarationSyntax VisitClassDeclaration(ConversionContext context,
        ClassOrInterfaceDeclaration classDecl, bool isNested = false)
    {
        string name = classDecl.getNameAsString();

        if (!isNested)
        {
            context.RootTypeName = name;
        }

        context.LastTypeName = name;

        var classSyntax = SyntaxFactory.ClassDeclaration(name);

        var typeParams = classDecl.getTypeParameters().ToList<TypeParameter>();

        if (typeParams is { Count: > 0 })
        {
            classSyntax = classSyntax.AddTypeParameterListParameters(typeParams
                    .Select(i => SyntaxFactory.TypeParameter(i.getNameAsString())).ToArray());
            classSyntax = classSyntax.AddConstraintClauses(TypeHelper.GetTypeParameterListConstraints(typeParams).ToArray());
        }

        var mods = classDecl.getModifiers().ToModifierKeywordSet();

        classSyntax = ModifierHelper.AddAccessModifiers(classSyntax, mods);
        classSyntax = ModifierHelper.AddNonAccessModifiers(classSyntax, mods,
            mapAbstract: true, mapFinalToSealed: true);

        // Java's package-private default has no access modifier keyword. In C# nested classes
        // default to private, which makes their members inaccessible from the enclosing class.
        // Promote modifier-less nested classes to public so they remain usable.
        if (isNested && ModifierHelper.IsPackagePrivate(mods))
        {
            classSyntax = classSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
        }

        var extends = classDecl.getExtendedTypes().ToList<ClassOrInterfaceType>() ?? [];

        foreach (var extend in extends)
        {
            classSyntax = classSyntax.AddBaseListTypes(SyntaxFactory.SimpleBaseType(TypeHelper.GetSyntaxFromType(context, extend)));
        }

        var implements = classDecl.getImplementedTypes().ToList<ClassOrInterfaceType>() ?? [];

        foreach (var implement in implements)
        {
            classSyntax = classSyntax.AddBaseListTypes(SyntaxFactory.SimpleBaseType(TypeHelper.GetSyntaxFromType(context, implement)));
        }

        var members = classDecl.getMembers()?.ToList<BodyDeclaration>();

        if (members is not null)
        {
            context.PushVariableScope();

            try
            {
                foreach (var member in members)
                {
                    if (member is ClassOrInterfaceDeclaration childType)
                    {
                        if (childType.isInterface())
                        {
                            var childInt = VisitInterfaceDeclaration(context, childType, true);

                            classSyntax = classSyntax.AddMembers(childInt);
                        }
                        else
                        {
                            var childClass = VisitClassDeclaration(context, childType, true);

                            classSyntax = classSyntax.AddMembers(childClass);
                        }
                    }
                    else
                    {
                        var syntax = VisitBodyDeclarationForClass(context, classSyntax, member, extends, implements);
                        var withJavaComments = syntax?.WithJavaComments(context, member);

                        if (withJavaComments is not null)
                        {
                            classSyntax = classSyntax.AddMembers(withJavaComments);
                        }
                    }

                    while (context.PendingAnonymousTypes.Count > 0)
                    {
                        var anon = context.PendingAnonymousTypes.Dequeue();
                        classSyntax = classSyntax.AddMembers(anon);
                    }
                }
            }
            finally
            {
                context.PopVariableScope();
            }
        }

        var annotations = classDecl.getAnnotations().ToList<AnnotationExpr>();

        if (annotations is { Count: > 0 })
        {
            foreach (var annotation in annotations)
            {
                var obsoleteAttr = AnnotationHelper.ConvertDeprecatedToObsolete(annotation);
                if (obsoleteAttr is not null)
                {
                    classSyntax = classSyntax.AddAttributeLists(obsoleteAttr);
                    break;
                }
            }
        }

        return classSyntax.WithJavaComments(context, classDecl);
    }
}
