using com.github.javaparser.ast;
using com.github.javaparser.ast.body;
using com.github.javaparser.ast.expr;
using com.github.javaparser.ast.stmt;
using com.github.javaparser.ast.type;
using JavaToCSharp.Declarations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp.Statements;

/// <summary>
/// Hoists Java local classes and local records (declared inside method bodies) into private
/// nested types of the enclosing class. C# has no local classes; a bare class declaration in a
/// method body is a syntax error.
///
/// Variables referenced by the class but declared outside it (effectively-final locals, method
/// parameters, enclosing instance fields) are captured the way Java does implicitly: captured
/// locals/parameters become constructor parameters assigned to private readonly fields, and
/// references to enclosing instance fields are routed through an injected <c>parent</c> field.
/// </summary>
internal static class LocalClassHoister
{
    public static StatementSyntax? HoistLocalType(ConversionContext context, Statement statement, TypeDeclaration typeDeclaration)
    {
        // 1. Resolve the enclosing method/constructor and the enclosing type chain (innermost first).
        Node? current = statement.getParentNode().FromOptional<Node>();
        MethodDeclaration? enclosingMethod = null;
        ConstructorDeclaration? enclosingCtor = null;
        var enclosingTypes = new List<TypeDeclaration>();

        while (current is not null)
        {
            switch (current)
            {
                case MethodDeclaration method when enclosingMethod is null && enclosingCtor is null:
                    enclosingMethod = method;
                    break;
                case ConstructorDeclaration ctor when enclosingMethod is null && enclosingCtor is null:
                    enclosingCtor = ctor;
                    break;
                case TypeDeclaration type:
                    enclosingTypes.Add(type);
                    break;
            }

            current = current.getParentNode().FromOptional<Node>();
        }

        // 2. Capture analysis.
        var declaredInClass = CollectDeclaredNames(typeDeclaration);
        var referencedNames = CollectReferencedNames(typeDeclaration);

        var okAsStaticOrTypeName = new HashSet<string>(StringComparer.Ordinal);
        var outerInstanceFields = new HashSet<string>(StringComparer.Ordinal);

        foreach (var enclosingType in enclosingTypes)
        {
            var members = enclosingType.getMembers()?.ToList<BodyDeclaration>();

            if (members is null)
            {
                continue;
            }

            // Only fields of the innermost enclosing type can be routed through the injected
            // "parent" reference; fields of outer types are left for name lookup (best-effort).
            bool isInnermost = enclosingType == enclosingTypes[0];

            foreach (var member in members)
            {
                switch (member)
                {
                    case FieldDeclaration field:
                    {
                        var variables = field.getVariables()?.ToList<VariableDeclarator>();

                        if (variables is null)
                        {
                            break;
                        }

                        foreach (var variable in variables)
                        {
                            var fieldName = variable.getNameAsString();
                            if (field.isStatic())
                            {
                                okAsStaticOrTypeName.Add(fieldName);
                                outerInstanceFields.Remove(fieldName);
                            }
                            else if (isInnermost && !okAsStaticOrTypeName.Contains(fieldName))
                            {
                                outerInstanceFields.Add(fieldName);
                            }
                        }

                        break;
                    }
                    case EnumConstantDeclaration enumConstant:
                        okAsStaticOrTypeName.Add(enumConstant.getNameAsString());
                        break;
                }
            }
        }

        var capturedLocals = new List<(string Name, string JavaType)>();
        bool needsParent = false;

        foreach (var name in referencedNames)
        {
            if (declaredInClass.Contains(name))
            {
                continue;
            }

            // Enclosing instance fields are reached through the injected "parent" reference;
            // this also avoids the variable-scope tracker (which knows class fields) capturing
            // them as constructor parameters.
            if (outerInstanceFields.Contains(name))
            {
                needsParent = true;
                continue;
            }

            if (context.TryGetVariableType(name, out var javaType))
            {
                capturedLocals.Add((name, javaType));
            }
            // Anything else (statics, class names, imports) resolves through normal C#
            // nested-type name lookup and is left untouched.
        }

        // Names the class declares itself always win over outer names during rewriting.
        outerInstanceFields.RemoveWhere(declaredInClass.Contains);

        // 3. Convert the declaration as a nested type.
        ClassDeclarationSyntax classSyntax = typeDeclaration switch
        {
            ClassOrInterfaceDeclaration classDecl =>
                ClassOrInterfaceDeclarationVisitor.VisitClassDeclaration(context, classDecl, true),
            RecordDeclaration recordDecl =>
                RecordDeclarationVisitor.VisitRecordDeclaration(context, recordDecl, true),
            _ => throw new InvalidOperationException($"Unsupported local type declaration: {typeDeclaration.GetType().Name}"),
        };

        // 4. Method-level type parameters cannot be referenced from a nested C# type; promote the
        // ones actually used by the class body to type parameters of the hoisted class itself.
        // Type parameters the local class declares itself are excluded (already present).
        var usedTypeParams = new List<string>();
        if (enclosingMethod is not null)
        {
            var ownTypeParams = typeDeclaration is ClassOrInterfaceDeclaration classDeclWithTypeParams
                ? classDeclWithTypeParams.getTypeParameters().ToList<TypeParameter>()
                    ?.Select(t => t.getNameAsString()).ToHashSet(StringComparer.Ordinal)
                    ?? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            var methodTypeParams = enclosingMethod.getTypeParameters().ToList<TypeParameter>()
                ?.Select(t => t.getNameAsString()) ?? [];

            foreach (var typeParam in methodTypeParams)
            {
                if (!ownTypeParams.Contains(typeParam) && ReferencesTypeParameter(typeDeclaration, typeParam))
                {
                    usedTypeParams.Add(typeParam);
                }
            }
        }

        if (usedTypeParams.Count > 0)
        {
            classSyntax = classSyntax.AddTypeParameterListParameters(usedTypeParams
                .Select(n => SyntaxFactory.TypeParameter(n))
                .ToArray());
        }

        // 5. Augment constructors so captures are received and stored.
        var enclosingFirst = enclosingTypes.Count > 0 ? enclosingTypes[0] : null;
        var enclosingTypeName = enclosingFirst is not null
            ? TypeHelper.ConvertType(context, enclosingFirst.getNameAsString(), synthesizeRawArguments: false)
            : "object";

        // The enclosing instance carries the enclosing type's own type parameters
        // (`Sorter.this` inside `class Sorter<T>` is Sorter<T>) — a raw spelling of a generic
        // type is CS0305 in the hoisted class. The hoisted class is already generic over the
        // same parameters (the hoister copies referenced ones), so spell them out.
        if (enclosingTypeName is not null && enclosingFirst is not null)
        {
            var enclosingParams = enclosingFirst is ClassOrInterfaceDeclaration enclosingDecl
                ? enclosingDecl.getTypeParameters().ToList<com.github.javaparser.ast.type.TypeParameter>() ?? []
                : [];
            if (enclosingTypeName.IndexOf('<') < 0 && enclosingParams is { Count: > 0 })
            {
                enclosingTypeName += "<" + string.Join(", ", enclosingParams.Select(p => p.getNameAsString())) + ">";
            }
        }
        else
        {
            enclosingTypeName = "object";
        }

        var captureParameters = new List<ParameterSyntax>();
        var captureAssignments = new List<StatementSyntax>();
        var captureFields = new List<MemberDeclarationSyntax>();

        if (needsParent)
        {
            captureParameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier("parent"))
                .WithType(SyntaxFactory.ParseTypeName(enclosingTypeName)));
            captureAssignments.Add(SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.ThisExpression(), SyntaxFactory.IdentifierName("parent")),
                    SyntaxFactory.IdentifierName("parent"))));
            captureFields.Add(MakeCaptureField(enclosingTypeName, "parent"));
        }

        foreach (var (name, javaType) in capturedLocals)
        {
            var escaped = TypeHelper.EscapeIdentifier(name);
            var fieldType = string.IsNullOrWhiteSpace(javaType)
                ? "object"
                : TypeHelper.ConvertType(context, javaType);

            captureParameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(escaped))
                .WithType(SyntaxFactory.ParseTypeName(fieldType)));
            captureAssignments.Add(SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.ThisExpression(), SyntaxFactory.IdentifierName(escaped)),
                    SyntaxFactory.IdentifierName(escaped))));
            captureFields.Add(MakeCaptureField(fieldType, escaped));
        }

        if (captureParameters.Count > 0)
        {
            var existingCtors = classSyntax.Members.OfType<ConstructorDeclarationSyntax>().ToList();

            if (existingCtors.Count > 0)
            {
                foreach (var ctor in existingCtors)
                {
                    var augmented = ctor.AddParameterListParameters(captureParameters.ToArray());

                    if (augmented.Body is { } body)
                    {
                        augmented = augmented.WithBody(SyntaxFactory.Block(captureAssignments.Concat(body.Statements)));
                    }

                    classSyntax = classSyntax.ReplaceNode(ctor, augmented);
                }
            }
            else
            {
                var ctor = SyntaxFactory.ConstructorDeclaration(classSyntax.Identifier.Text)
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                    .AddParameterListParameters(captureParameters.ToArray())
                    .AddBodyStatements(captureAssignments.ToArray());
                classSyntax = classSyntax.AddMembers(ctor);
            }

            classSyntax = classSyntax.AddMembers(captureFields.ToArray());
        }

        // 6. Rewrite references to captures inside the hoisted body:
        //    captured local "x"      -> "this.x"
        //    outer instance field "f"-> "parent.f" (and "this.f" -> "parent.f")
        if (capturedLocals.Count > 0 || needsParent)
        {
            var rewriter = new CaptureRewriter(
                capturedLocals.Select(c => c.Name).ToHashSet(StringComparer.Ordinal),
                outerInstanceFields);
            classSyntax = (ClassDeclarationSyntax)rewriter.Visit(classSyntax)!;
        }

        // 7. Publish: enqueue as a nested type of the enclosing class and register the capture
        //    mapping so object-creation sites can supply the capture arguments.
        context.DeclaredTypeNames.Add(typeDeclaration.getNameAsString());
        context.PendingAnonymousTypes.Enqueue(classSyntax);

        context.RegisterHoistedLocalClass(new ConversionContext.HoistedLocalClassInfo(
            Name: typeDeclaration.getNameAsString(),
            NeedsParent: needsParent,
            CapturedLocals: capturedLocals,
            TypeParams: usedTypeParams));

        return null;
    }

    private static FieldDeclarationSyntax MakeCaptureField(string type, string name)
    {
        return SyntaxFactory.FieldDeclaration(
                SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName(type))
                    .AddVariables(SyntaxFactory.VariableDeclarator(name)))
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));
    }

    /// <summary>
    /// Collects every name declared inside the type's body: fields, locals, parameters, record
    /// components, type parameters and enum constants.
    /// </summary>
    private static HashSet<string> CollectDeclaredNames(Node root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in Descendants(root))
        {
            switch (node)
            {
                case VariableDeclarator variable:
                    names.Add(variable.getNameAsString());
                    break;
                case Parameter parameter:
                    names.Add(parameter.getNameAsString());
                    break;
                case TypeParameter typeParameter:
                    names.Add(typeParameter.getNameAsString());
                    break;
                case EnumConstantDeclaration enumConstant:
                    names.Add(enumConstant.getNameAsString());
                    break;
            }
        }

        return names;
    }

    private static HashSet<string> CollectReferencedNames(Node root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in Descendants(root))
        {
            if (node is NameExpr nameExpr)
            {
                names.Add(nameExpr.getNameAsString());
            }
        }

        return names;
    }

    private static IEnumerable<Node> Descendants(Node root)
    {
        foreach (var child in root.getChildNodes()?.ToList<Node>() ?? [])
        {
            yield return child;

            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool ReferencesTypeParameter(Node root, string typeParameterName)
    {
        foreach (var node in Descendants(root))
        {
            if (node is com.github.javaparser.ast.type.Type type)
            {
                var text = type.toString();
                if (string.Equals(text, typeParameterName, StringComparison.Ordinal)
                    || text.StartsWith(typeParameterName + "<", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Rewrites bare identifier references inside a hoisted class body so they resolve to the
    /// injected capture fields instead of the (now nonexistent) enclosing scope.
    /// </summary>
    private sealed class CaptureRewriter(ISet<string> capturedLocals, ISet<string> outerInstanceFields) : CSharpSyntaxRewriter
    {
        private ISet<string> _ctorParameterNames = new HashSet<string>(StringComparer.Ordinal);

        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            var previous = _ctorParameterNames;
            _ctorParameterNames = node.ParameterList.Parameters
                .Select(p => p.Identifier.Text)
                .ToHashSet(StringComparer.Ordinal);

            try
            {
                return base.VisitConstructorDeclaration(node);
            }
            finally
            {
                _ctorParameterNames = previous;
            }
        }

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            // "this.f" where f is an enclosing instance field becomes "parent.f".
            if (node.Expression is ThisExpressionSyntax
                && node.Name is IdentifierNameSyntax name
                && outerInstanceFields.Contains(name.Identifier.Text))
            {
                return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("parent"),
                    name);
            }

            return base.VisitMemberAccessExpression(node);
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            // Leave member-access names ("x.f") and base/interface lists alone.
            if (node.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == node)
            {
                return node;
            }

            var text = node.Identifier.Text;

            // Constructor parameters (including the capture parameters themselves) shadow the
            // captures inside the constructor body; rewriting them would produce
            // "this.x = this.x" nonsense.
            if (_ctorParameterNames.Contains(text))
            {
                return node;
            }

            // Constructor initializers ("base(captured)") run before field assignment; the
            // capture arrives as a constructor parameter there, so keep the bare name.
            for (SyntaxNode? ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (ancestor is ConstructorInitializerSyntax)
                {
                    return node;
                }
            }

            if (capturedLocals.Contains(text) || outerInstanceFields.Contains(text))
            {
                var qualifier = outerInstanceFields.Contains(text) ? "parent" : "this";
                return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(qualifier),
                    node.WithoutTrivia());
            }

            return node;
        }
    }
}
