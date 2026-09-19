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
        var returnTypeName = TypeHelper.ConvertTypeSyntax(context, returnType, 0).ToString();

        var methodName = context.PreserveJavaMethodNames
            ? methodDecl.getNameAsString()
            : TypeHelper.Capitalize(methodDecl.getNameAsString());
        methodName = TypeHelper.ReplaceCommonMethodNames(methodName);
        methodName = TypeHelper.EscapeMemberName(methodName);

        var methodSyntax = SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName(returnTypeName), methodName);

        var typeParams = methodDecl.getTypeParameters().ToList<TypeParameter>();

        if (typeParams is { Count: > 0 })
        {
            methodSyntax = methodSyntax.AddTypeParameterListParameters(typeParams
                .Select(i => SyntaxFactory.TypeParameter(i.getNameAsString())).ToArray());
            methodSyntax = methodSyntax.AddConstraintClauses(TypeHelper.GetTypeParameterListConstraints(context, typeParams).ToArray());
        }

        var parameters = methodDecl.getParameters().ToList<Parameter>() ?? [];

        if (parameters is {Count: > 0})
        {
            var paramSyntax = parameters.Select(i =>
                SyntaxFactory.Parameter(
                    attributeLists: [],
                    modifiers: SyntaxFactory.TokenList(),
                    type: TypeHelper.ConvertTypeSyntax(context, i.getType(), i.getType().getArrayLevel()),
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
        var returnTypeName = TypeHelper.ConvertTypeSyntax(context, returnType, 0).ToString();

        var methodName = context.PreserveJavaMethodNames
            ? methodDecl.getNameAsString()
            : TypeHelper.Capitalize(methodDecl.getNameAsString());
        methodName = TypeHelper.ReplaceCommonMethodNames(methodName);
        methodName = TypeHelper.EscapeMemberName(methodName);

        var methodSyntax = SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName(returnTypeName), methodName);

        var typeParams = methodDecl.getTypeParameters().ToList<TypeParameter>();

        if (typeParams is { Count: > 0 })
        {
            methodSyntax = methodSyntax.AddTypeParameterListParameters(typeParams
                .Select(i => SyntaxFactory.TypeParameter(i.getNameAsString())).ToArray());
            methodSyntax = methodSyntax.AddConstraintClauses(TypeHelper.GetTypeParameterListConstraints(context, typeParams).ToArray());
        }

        var mods = methodDecl.getModifiers().ToModifierKeywordSet();

        methodSyntax = ModifierHelper.AddAccessModifiers(methodSyntax, mods);

        // Java package-private ("default") access has no C# equivalent: an emitted method with no
        // access modifier defaults to private, which both hides it from other files and makes
        // `virtual`/`override` chains illegal (CS0621, CS0115). Promote to `public`: base and
        // derived declarations across separately converted files then agree on the same access
        // level deterministically, keeping override chains legal (an `internal` base vs `public`
        // derived pair is CS0507).
        if (!isInterface && ModifierHelper.IsPackagePrivate(mods))
        {
            methodSyntax = methodSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
        }

        // Java lets an override widen access (protected -> public is legal); C# override must
        // match the base's access exactly (CS0507). Promote protected methods to public so
        // base and derived declarations agree tree-wide, mirroring the package-private rule
        // above and the protected-nested-type promotion.
        if (!isInterface && mods.Contains(Modifier.Keyword.PROTECTED))
        {
            methodSyntax = ModifierHelper.ReplaceProtectedWithPublic(methodSyntax);
        }

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
                    // Object-method overrides require the EXACT object signature: a java
                    // `toString(int)` or `equals(String, int)` overload is an unrelated method,
                    // and marking it `override` fails (CS0115 — no virtual base with that shape).
                    var objectMethodParamCount = methodDecl.getParameters().size();
                    bool isObjectMethod = (javaMethodName == "hashCode" && objectMethodParamCount == 0)
                        || (javaMethodName == "toString" && objectMethodParamCount == 0)
                        || (javaMethodName == "equals" && objectMethodParamCount == 1);
                    // A reshaped interface's member set also proves a virtual base exists (the
                    // reshaped abstract class declares the member as a virtual stub).
                    bool hasBase = extends.Count > 0 || isObjectMethod || context.IsConvertingEnumConstantBody
                        || context.CurrentReshapeOverrideMembers.Contains(javaMethodName.ToLowerInvariant());

                    if (hasBase && !isInterface)
                    {
                        // C# `override` requires a virtual base member with a byte-identical
                        // signature. Only emit it when the base is verifiable from this file:
                        // java.lang.Object methods (always virtual), enum-constant bodies (base
                        // is the enclosing enum), or a base type declared in this file. A
                        // cross-file base may have drifted through context-dependent type
                        // conversion, so those become plain virtual re-declarations instead.
                        bool emitOverrideKeyword = context.Options.OverrideMode switch
                        {
                            OverrideMode.Faithful => true,
                            OverrideMode.AlwaysHide => false,
                            OverrideMode.Safe => isObjectMethod
                                || context.IsConvertingEnumConstantBody
                                || IsBaseDeclaredInFile(context, extends),
                            _ => true,
                        };

                        // Java's @Override also marks interface implementations; C# forbids
                        // `override` there (the implicit implementation of a plain virtual
                        // method satisfies the contract). The decision is scoped to the CURRENT
                        // type's own non-reshaped (interface-form) bases: a method whose name is
                        // a member of such a base implements it implicitly. A tree-wide name set
                        // was too coarse — every class-method override sharing a name with ANY
                        // interface member anywhere (run/codec/getType/fix...) was demoted to a
                        // hiding virtual, leaving the inherited abstract member unimplemented
                        // (CS0533) and the whole subclass chain failing (CS0534). Reshaped
                        // interfaces are exempt: their base is now an abstract class with a
                        // virtual member, so `override` is both legal and required for dispatch.
                        if (emitOverrideKeyword
                            && !isObjectMethod
                            && context.CurrentImplementedInterfaceMembers.Contains(javaMethodName.ToLowerInvariant())
                            && !context.CurrentReshapeOverrideMembers.Contains(javaMethodName.ToLowerInvariant()))
                        {
                            emitOverrideKeyword = false;
                        }

                        // A reshaped interface's @Override against a MAPPED base (AutoCloseable ->
                        // IDisposable, Comparable -> IComparable, ...) has no virtual base member:
                        // the mapped interface declares it non-virtual. Emit a plain virtual stub
                        // instead — it still satisfies the interface contract (CS0115 otherwise).
                        if (emitOverrideKeyword
                            && context.IsConvertingReshapedInterface
                            && !isObjectMethod
                            && !IsBaseDeclaredInFile(context, extends)
                            && !context.CurrentReshapeOverrideMembers.Contains(javaMethodName.ToLowerInvariant()))
                        {
                            emitOverrideKeyword = false;
                        }

                        if (emitOverrideKeyword)
                        {
                            methodSyntax = methodSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.OverrideKeyword));

                            // C# inherits constraint clauses from the overridden member — a
                            // re-specified list is CS0460 (except class/struct). The Java
                            // source restates them; drop the emitted clauses on override.
                            if (methodSyntax.ConstraintClauses.Count > 0)
                            {
                                methodSyntax = methodSyntax.RemoveNodes(methodSyntax.ConstraintClauses, SyntaxRemoveOptions.KeepNoTrivia)!;
                            }
                        }

                        // Suppress `virtual` only when we emitted `override` (the two are mutually
                        // exclusive in C#); a hiding re-declaration must stay virtual so its own
                        // subclasses can dispatch.
                        isOverride = emitOverrideKeyword;
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
            if (context.IsConvertingReshapedInterface)
            {
                // Reshaped interface member: a virtual throw-stub keeps partial implementers
                // compiling (no abstract-in-class CS0534 wave) while marking the missing
                // implementation for hand-finishing. The virtual rule above already added
                // `virtual`; replace the semicolon with the stub body.
                return methodSyntax
                    .AddBodyStatements(
                        SyntaxFactory.ThrowStatement(
                            SyntaxFactory.ParseExpression(
                                $"new NotSupportedException(\"REVIEW(java-to-csharp): unimplemented reshaped interface member '{methodName}'\")")))
                    .WithLeadingTrivia(methodSyntax.GetLeadingTrivia());
            }

            // i.e. abstract method
            methodSyntax = methodSyntax.WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

            return methodSyntax;
        }

        context.PushVariableScope();
        context.PushHoistedLocalClassScope();

        // Method type parameters must be visible while the body converts: anonymous classes
        // created inside the method are hoisted to nested types, and the hoister decides which
        // type parameters the hoisted class must declare by scanning context.TypeParameterNodes.
        // Without this registration a body referencing <T> hoists a non-generic class that uses
        // T (CS0246).
        var previousTypeParameterNames = context.TypeParameterNames.ToList();
        var previousTypeParameterNodes = context.TypeParameterNodes.ToList();
        context.TypeParameterNames.Clear();
        context.TypeParameterNodes.Clear();

        foreach (var name in previousTypeParameterNames)
        {
            context.TypeParameterNames.Add(name);
        }

        foreach (var node in previousTypeParameterNodes)
        {
            context.TypeParameterNodes.Add(node);
        }

        foreach (var typeParameter in typeParams ?? [])
        {
            var typeName = typeParameter!.getNameAsString();

            if (!context.TypeParameterNames.Contains(typeName))
            {
                context.TypeParameterNames.Add(typeName);
                context.TypeParameterNodes.Add(typeParameter);
            }
        }

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
            context.TypeParameterNames.Clear();
            foreach (var restoredName in previousTypeParameterNames)
            {
                context.TypeParameterNames.Add(restoredName);
            }

            context.TypeParameterNodes.Clear();
            foreach (var restoredNode in previousTypeParameterNodes)
            {
                context.TypeParameterNodes.Add(restoredNode);
            }

            context.PopHoistedLocalClassScope();
            context.PopVariableScope();
        }

        return methodSyntax;
    }

    private static bool IsRecordComponentAccessor(ConversionContext context, MethodDeclaration methodDecl)
    {
        return (methodDecl.getParameters()?.size() ?? 0) == 0
               && context.RecordComponentNames.Contains(methodDecl.getNameAsString());
    }

    /// <summary>
    /// Returns true when every base type in the class's <c>extends</c> clause is declared in the
    /// file being converted (nested type, hoisted local class, or the file's own root type). In
    /// that case the converter can verify the base member's virtualness and signature locally,
    /// making C# <c>override</c> emission safe.
    /// </summary>
    private static bool IsBaseDeclaredInFile(ConversionContext context, IReadOnlyList<ClassOrInterfaceType> extends)
    {
        if (extends.Count == 0)
        {
            return false;
        }

        foreach (var baseType in extends)
        {
            var name = baseType.getNameAsString();
            var dot = name.LastIndexOf('.');
            if (dot >= 0)
            {
                name = name[(dot + 1)..];
            }

            if (!context.DeclaredTypeNames.Contains(name)
                && context.FindHoistedLocalClass(name) is null)
            {
                return false;
            }
        }

        return true;
    }
}


