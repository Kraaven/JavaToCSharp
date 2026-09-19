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
        if (declaration.isInterface())
        {
            return VisitInterfaceDeclaration(context, declaration, true);
        }
        return VisitClassDeclaration(context, declaration, true);
    }

public static MemberDeclarationSyntax VisitInterfaceDeclaration(ConversionContext context,
        ClassOrInterfaceDeclaration interfaceDecl, bool isNested = false)
    {
        var newTypeName = interfaceDecl.getName().getIdentifier();

        if (!isNested)
        {
            context.RootTypeName = newTypeName;
        }

        context.LastTypeName = newTypeName;
        context.DeclaredTypeNames.Add(newTypeName);

        // Interface reshape: multi-method interfaces become C# abstract classes with virtual
        // throw-stub members, so partial implementers always compile (CS0534/CS0535 die at the
        // source) and unimplemented members carry REVIEW tripwires.
        var reshaped = context.Options.ReshapeableInterfaceNames is { } reshapeable
            && reshapeable.Contains(newTypeName.ToLowerInvariant());

        TypeDeclarationSyntax classSyntax = reshaped
            ? SyntaxFactory.ClassDeclaration(newTypeName)
            : SyntaxFactory.InterfaceDeclaration(newTypeName);

        var typeParams = interfaceDecl.getTypeParameters().ToList<TypeParameter>();

        if (typeParams is { Count: > 0 })
        {
            classSyntax = classSyntax.AddTypeParameterListParameters(typeParams
                    .Select(i => SyntaxFactory.TypeParameter(i.getNameAsString())).ToArray());
            // Interface type parameters carry Java bounds too (Kind1<F extends K1, ...>) —
            // without the clauses every use of F as a type argument to a constrained type is
            // CS0314.
            classSyntax = classSyntax.AddConstraintClauses(TypeHelper.GetTypeParameterListConstraints(context, typeParams).ToArray());
        }

        if (reshaped)
        {
            classSyntax = classSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.AbstractKeyword));
        }

        var mods = interfaceDecl.getModifiers().ToModifierKeywordSet();

        classSyntax = ModifierHelper.AddAccessModifiers(classSyntax, mods);
        if (mods.Contains(Modifier.Keyword.FINAL))
            classSyntax = classSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.SealedKeyword));

        // Java interfaces are at least package-private; C# internal would be less accessible than
        // the public members implementing them. Promote to public. A protected interface already
        // emitted `protected` above — substitute it (CS0107 forbids protected + public).
        if (ModifierHelper.IsPackagePrivate(mods))
        {
            classSyntax = classSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
        }
        else if (isNested && mods.Contains(Modifier.Keyword.PRIVATE))
        {
            // Java allows a private nested interface in a public member signature (erasure);
            // C# rejects the combination (CS0050/CS0051). Promote to public.
            classSyntax = ModifierHelper.ReplacePrivateWithPublic(classSyntax);
        }
        else if (mods.Contains(Modifier.Keyword.PROTECTED))
        {
            classSyntax = ModifierHelper.ReplaceProtectedWithPublic(classSyntax);
        }

        var extends = interfaceDecl.getExtendedTypes().ToList<ClassOrInterfaceType>();

        if (extends is not null)
        {
            foreach (var extend in extends)
            {
                // Base lists never infer diamond arguments (unlike `new X<>()`), so raw generic
                // references must gain their arity arguments here (CS0305).
                classSyntax = (TypeDeclarationSyntax)classSyntax.AddBaseListTypes(GetBaseTypeSyntax(context, extend));
            }
        }

        var implements = interfaceDecl.getImplementedTypes().ToList<ClassOrInterfaceType>();

        if (implements is not null)
        {
            foreach (var implement in implements)
            {
                classSyntax = (TypeDeclarationSyntax)((TypeDeclarationSyntax)classSyntax).AddBaseListTypes(GetBaseTypeSyntax(context, implement));
            }
        }

        classSyntax = DeduplicateBaseList(classSyntax);

        var members = interfaceDecl.getMembers()?.ToList<BodyDeclaration>();

        if (members is not null)
        {
            context.PushVariableScope();

            var previousTypeParameterNames = context.TypeParameterNames.ToList();
            var previousTypeParameterNodes = context.TypeParameterNodes.ToList();
            context.TypeParameterNames.Clear();
            context.TypeParameterNodes.Clear();

            foreach (var typeParameter in typeParams ?? [])
            {
                context.TypeParameterNames.Add(typeParameter!.getNameAsString());
                context.TypeParameterNodes.Add(typeParameter);
            }

            // Placeholders synthesized from here on belong to this interface — the base list
            // above was resolved BEFORE taking ownership, so a base-list wildcard's stand-in
            // stays owned by the enclosing type (a C# base list cannot see the class's own
            // nested types).
            var previousPlaceholderOwner = context.CurrentPlaceholderOwner;
            context.CurrentPlaceholderOwner = newTypeName;

            // Reshaped default methods implementing inherited reshaped members need `override`.
            var previousOverrideMembers = context.CurrentReshapeOverrideMembers.ToList();
            var previousInterfaceMembers = context.CurrentImplementedInterfaceMembers.ToList();
            var previousReshapedFlag = context.IsConvertingReshapedInterface;

            if (reshaped)
            {
                context.IsConvertingReshapedInterface = true;
                AddReshapeOverrideMembers(context, extends ?? [], implements ?? []);
            }

            try
            {
                foreach (var member in members)
                {
                    MemberDeclarationSyntax? memberSyntax;

                    if (reshaped)
                    {
                        // Members convert through the CLASS path: methods gain access modifiers
                        // and (for bodyless abstract members) virtual throw-stub bodies, fields
                        // become static members, nested types nest legally.
                        var reshapedHost = (ClassDeclarationSyntax)classSyntax;
                        memberSyntax = VisitBodyDeclarationForClass(context, reshapedHost, member, extends ?? [], implements ?? []);
                    }
                    else
                    {
                        memberSyntax = VisitBodyDeclarationForInterface(context, (InterfaceDeclarationSyntax)classSyntax, member);
                    }

                    var memberWithComments = memberSyntax?.WithJavaComments(context, member);

                    if (memberWithComments is not null)
                    {
                        classSyntax = AddMember(classSyntax, memberWithComments);
                    }

                    // Interface members (constants, default methods) can create hoisted
                    // anonymous classes too. C# interfaces may contain nested classes, so
                    // drain them here — otherwise the queue leaks into the next converted
                    // type in the file, which nests the hoisted classes in the wrong type.
while (context.PendingAnonymousTypes.Count > 0)
                    {
                        var anon = context.PendingAnonymousTypes.Dequeue();
                        classSyntax = AddMember(classSyntax, anon);
                    }

                    context.DrainPlaceholderTypes(context.CurrentPlaceholderOwner,
                        placeholder => classSyntax = AddMember(classSyntax, placeholder));
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

                context.CurrentPlaceholderOwner = previousPlaceholderOwner;
                context.IsConvertingReshapedInterface = previousReshapedFlag;
                context.CurrentReshapeOverrideMembers.Clear();
                foreach (var restoredName in previousOverrideMembers)
                {
                    context.CurrentReshapeOverrideMembers.Add(restoredName);
                }

                context.CurrentImplementedInterfaceMembers.Clear();
                foreach (var restoredName in previousInterfaceMembers)
                {
                    context.CurrentImplementedInterfaceMembers.Add(restoredName);
                }

                context.PopVariableScope();
            }
        }

        return classSyntax.WithJavaComments(context, interfaceDecl);
    }

    /// <summary>
    /// Adds a member to either declaration shape (the Roslyn AddMembers extensions are
    /// per-type; MemberDeclarationSyntax has no common form).
    /// </summary>
    private static TypeDeclarationSyntax AddMember(TypeDeclarationSyntax host, MemberDeclarationSyntax member)
    {
        return host switch
        {
            ClassDeclarationSyntax c => c.AddMembers(member),
            InterfaceDeclarationSyntax i => i.AddMembers(member),
            _ => host,
        };
    }

    /// <summary>
    /// Removes duplicate base-list entries with identical type text. Java may both extend
    /// <c>JsonDeserializer&lt;T&gt;</c> and implement <c>JsonSerializer&lt;T&gt;</c> — two rows
    /// that map to the same C# base type — which C# rejects as "multiple base classes" listing
    /// the same type twice (CS1721).
    /// </summary>
    private static T DeduplicateBaseList<T>(T syntax) where T : TypeDeclarationSyntax
    {
        if (syntax.BaseList is not { Types.Count: > 1 } baseList)
        {
            return syntax;
        }

        var distinct = baseList.Types
            .GroupBy(t => t.Type.ToString().Trim(), StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        return distinct.Count == baseList.Types.Count
            ? syntax
            : (T)syntax.WithBaseList(baseList.WithTypes(SyntaxFactory.SeparatedList(distinct)));
    }

    /// <summary>
    /// Builds a base-list entry. Bases that map to BCL DELEGATES (Runnable → Action,
    /// Predicate → Predicate&lt;T&gt;, ...) cannot appear in a C# base list (delegates are
    /// sealed, CS0509); a synthesized stand-in abstract class replaces them — see
    /// <see cref="TypeHelper.TryCreateDelegateBaseReplacement"/>.
    /// </summary>
    private static BaseTypeSyntax GetBaseTypeSyntax(ConversionContext context, ClassOrInterfaceType javaType)
    {
        if (TypeHelper.TryCreateDelegateBaseReplacement(context, javaType, out var replacement))
        {
            return replacement;
        }

        return SyntaxFactory.SimpleBaseType(TypeHelper.GetSyntaxFromType(context, javaType, synthesizeRawArguments: true));
    }

    /// <summary>
    /// Registers the reshaped-interface members implemented by the current type (cumulative —
    /// nested types inherit the enclosing type's bases, so the set is unioned, never cleared).
    /// Reshaped bases contribute to CurrentReshapeOverrideMembers (their C# base member is a
    /// virtual class member, so `override` is required); NON-reshaped (interface-form) bases
    /// contribute to CurrentImplementedInterfaceMembers (C# forbids `override` there, and the
    /// suppression must be scoped to these — a tree-wide name set wrongly suppressed
    /// class-method overrides that merely shared a name with an unrelated interface member).
    /// </summary>
    private static void AddReshapeOverrideMembers(ConversionContext context,
        IReadOnlyList<ClassOrInterfaceType> extends,
        IReadOnlyList<ClassOrInterfaceType> implements)
    {
        if (context.Options.InterfaceMembersByName is not { Count: > 0 } membersByName)
        {
            return;
        }

        var reshapeable = context.Options.ReshapeableInterfaceNames;
        var bases = extends.Concat(implements)
            .Select(t => t.getNameAsString().Split('.')[^1].ToLowerInvariant());

        foreach (var baseName in bases)
        {
            if (!membersByName.TryGetValue(baseName, out var members))
            {
                continue;
            }

            if (reshapeable?.Contains(baseName) ?? false)
            {
                foreach (var member in members)
                {
                    context.CurrentReshapeOverrideMembers.Add(member);
                }
            }
            else
            {
                foreach (var member in members)
                {
                    context.CurrentImplementedInterfaceMembers.Add(member);
                }
            }
        }
    }

    /// <summary>
    /// Finds fields whose simple name matches a method of the same type. Java allows the pair;
    /// C# rejects it (CS0102). The field is renamed (name + "Field") through
    /// <see cref="ConversionContext.FieldNameRenames"/>: the method keeps its name so cross-file
    /// callers are unaffected, and the private field is the safe side to rename.
    /// </summary>
    private static void PreScanFieldMethodCollisions(ConversionContext context, List<BodyDeclaration> members)
    {
        var methodNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in members)
        {
            if (member is MethodDeclaration method)
            {
                methodNames.Add(method.getNameAsString());
            }
        }

        if (methodNames.Count == 0)
        {
            return;
        }

        foreach (var member in members)
        {
            if (member is FieldDeclaration field)
            {
                foreach (var variable in field.getVariables()?.ToList<VariableDeclarator>() ?? [])
                {
                    var name = variable.getNameAsString();

                    if (methodNames.Contains(name))
                    {
                        context.FieldNameRenames[name] = name + "Field";
                    }
                }
            }
        }
    }

    /// <summary>
    /// Enum-shaped entry point for the field/method collision prescan (the enum visitor holds
    /// its members as a <c>List&lt;BodyDeclaration&gt;</c> and has no class syntax yet).
    /// </summary>
    internal static void PreScanFieldMethodCollisionsForEnum(ConversionContext context, List<BodyDeclaration> members)
        => PreScanFieldMethodCollisions(context, members);

    public static ClassDeclarationSyntax VisitClassDeclaration(ConversionContext context,
        ClassOrInterfaceDeclaration classDecl, bool isNested = false)
    {
        string name = classDecl.getNameAsString();

        if (!isNested)
        {
            context.RootTypeName = name;
        }

        context.LastTypeName = name;
        context.DeclaredTypeNames.Add(name);

        var classSyntax = SyntaxFactory.ClassDeclaration(name);

        var typeParams = classDecl.getTypeParameters().ToList<TypeParameter>();

        if (typeParams is { Count: > 0 })
        {
            classSyntax = classSyntax.AddTypeParameterListParameters(typeParams
                    .Select(i => SyntaxFactory.TypeParameter(i.getNameAsString())).ToArray());
            classSyntax = classSyntax.AddConstraintClauses(TypeHelper.GetTypeParameterListConstraints(context, typeParams).ToArray());
        }

        var mods = classDecl.getModifiers().ToModifierKeywordSet();

        classSyntax = ModifierHelper.AddAccessModifiers(classSyntax, mods);
        classSyntax = ModifierHelper.AddNonAccessModifiers(classSyntax, mods,
            mapAbstract: true, mapFinalToSealed: true);

        // Java's package-private default has no access modifier keyword. In C# nested classes
        // default to private (hiding them from the enclosing class) and top-level classes to
        // internal (less accessible than the public members that reference them — CS0050/CS0051/
        // CS0060). Promote modifier-less classes to public so both shapes remain usable.
        if (ModifierHelper.IsPackagePrivate(mods))
        {
            classSyntax = classSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
        }
        else if (isNested && mods.Contains(Modifier.Keyword.PRIVATE))
        {
            // Java allows a private nested type in a public member signature (erasure); C#
            // rejects the combination (CS0050/CS0051). Promote — the enclosing class still
            // gates access through its own members.
            classSyntax = ModifierHelper.ReplacePrivateWithPublic(classSyntax);
        }
        else if (isNested && mods.Contains(Modifier.Keyword.PROTECTED))
        {
            // Java protected nested types are reachable package-wide (CreativeModeTab.Output is
            // referenced from sibling files); C# protected restricts to subclasses, which breaks
            // those references (CS0122). Promote to public — the enclosing class still gates
            // access. The `protected` token added by AddAccessModifiers is substituted, not
            // duplicated (CS0107).
            classSyntax = ModifierHelper.ReplaceProtectedWithPublic(classSyntax);
        }

        var extends = classDecl.getExtendedTypes().ToList<ClassOrInterfaceType>() ?? [];

        foreach (var extend in extends)
        {
            // Base lists never infer diamond arguments (unlike `new X<>()`), so raw generic
            // references must gain their arity arguments here (CS0305).
            classSyntax = classSyntax.AddBaseListTypes(GetBaseTypeSyntax(context, extend));
        }

        var implements = classDecl.getImplementedTypes().ToList<ClassOrInterfaceType>() ?? [];

        foreach (var implement in implements)
        {
            classSyntax = classSyntax.AddBaseListTypes(GetBaseTypeSyntax(context, implement));
        }

        classSyntax = DeduplicateBaseList(classSyntax);

        var members = classDecl.getMembers()?.ToList<BodyDeclaration>();

        if (members is not null)
        {
            PreScanFieldMethodCollisions(context, members);

            context.PushVariableScope();

            // Expose this type's own parameters to member conversion (anonymous-class hoisting
            // references them); nested scopes clear and restore, mirroring the record visitor.
            var previousTypeParameterNames = context.TypeParameterNames.ToList();
            var previousTypeParameterNodes = context.TypeParameterNodes.ToList();
            context.TypeParameterNames.Clear();
            context.TypeParameterNodes.Clear();

            foreach (var typeParameter in typeParams ?? [])
            {
                context.TypeParameterNames.Add(typeParameter!.getNameAsString());
                context.TypeParameterNodes.Add(typeParameter);
            }

            // Placeholders synthesized from here on belong to this class — the base list above
            // was resolved BEFORE taking ownership, so a base-list wildcard's stand-in stays
            // owned by the enclosing type (a C# base list cannot see the class's own nested
            // types).
            var previousPlaceholderOwner = context.CurrentPlaceholderOwner;
            context.CurrentPlaceholderOwner = name;

            // Implementers of reshaped interfaces flip `override` for their member methods.
            var previousOverrideMembers = context.CurrentReshapeOverrideMembers.ToList();
            var previousInterfaceMembers = context.CurrentImplementedInterfaceMembers.ToList();
            AddReshapeOverrideMembers(context, extends ?? [], implements ?? []);

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
                        var syntax = VisitBodyDeclarationForClass(context, classSyntax, member, extends ?? [], implements ?? []);
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

                    context.DrainPlaceholderTypes(context.CurrentPlaceholderOwner,
                        placeholder => classSyntax = classSyntax.AddMembers(placeholder));
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

                context.CurrentPlaceholderOwner = previousPlaceholderOwner;
                context.CurrentReshapeOverrideMembers.Clear();
                foreach (var restoredName in previousOverrideMembers)
                {
                    context.CurrentReshapeOverrideMembers.Add(restoredName);
                }

                context.CurrentImplementedInterfaceMembers.Clear();
                foreach (var restoredName in previousInterfaceMembers)
                {
                    context.CurrentImplementedInterfaceMembers.Add(restoredName);
                }

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

        classSyntax = AddInterfaceBaseImplementations(context, classSyntax, members ?? [], implements ?? []);

        return classSyntax.WithJavaComments(context, classDecl);
    }

    /// <summary>
    /// Emits explicit interface implementations for members of transitively-extended tree
    /// interfaces that the class neither declares nor inherits a satisfying default for.
    /// C# rule (verified): a derived interface's default re-declaration does NOT satisfy the
    /// base interface's member contract for implementing classes (CS0535) — e.g.
    /// SimpleWaterloggedBlock's default pickupBlock does not exempt AmethystClusterBlock from
    /// BucketPickup.pickupBlock. The emitted member delegates through the DIRECTLY implemented
    /// interface, whose chain resolves to the most specific default:
    /// <c>ItemStack BucketPickup.pickupBlock(...) => ((SimpleWaterloggedBlock)this).pickupBlock(...);</c>
    /// Members the class declares (implicit implementation) and members with a default in the
    /// base interface itself are skipped.
    /// </summary>
    private static T AddInterfaceBaseImplementations<T>(
        ConversionContext context,
        T classSyntax,
        List<BodyDeclaration> members,
        IReadOnlyList<ClassOrInterfaceType> implements) where T : TypeDeclarationSyntax
    {
        if (implements.Count == 0
            || context.Options.InterfaceExtendsClosures is not { Count: > 0 } closures
            || context.Options.InterfaceMemberSignatures is not { Count: > 0 } signaturesByName
            || context.Options.InterfaceDefaultMemberNames is not { Count: > 0 } defaultsByName)
        {
            return classSyntax;
        }

        var reshapeable = context.Options.ReshapeableInterfaceNames;

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (member is MethodDeclaration method)
            {
                declared.Add($"{method.getNameAsString().ToLowerInvariant()}#{method.getParameters().size()}");
            }
        }

        var added = new HashSet<string>(StringComparer.Ordinal);
        List<MemberDeclarationSyntax>? additions = null;

        foreach (var implement in implements)
        {
            var javaName = implement.getNameAsString();
            var directName = javaName.Split('.')[^1].ToLowerInvariant();

            // Reshaped bases are abstract classes with inherited stubs; mapped bases define
            // their own C# shape. Only interface-form tree bases need base-member satisfaction.
            if ((reshapeable?.Contains(directName) ?? false)
                || !closures.TryGetValue(directName, out var closure))
            {
                continue;
            }

            foreach (var baseInterface in closure)
            {
                // Closure values are original-case interface names; the fact indexes are
                // keyed lowercase. The original case is used for the emitted specifier.
                var baseInterfaceLower = baseInterface.ToLowerInvariant();

                // Reshaped bases are abstract CLASSES with virtual throw-stubs: the explicit
                // interface specifier is invalid (CS0538) and the class inherits their stubs
                // implicitly, so there is nothing to re-declare.
                if (reshapeable?.Contains(baseInterfaceLower) ?? false)
                {
                    continue;
                }

                if (!signaturesByName.TryGetValue(baseInterfaceLower, out var baseSignatures))
                {
                    continue;
                }

                defaultsByName.TryGetValue(baseInterfaceLower, out var baseDefaults);

                // Signature lists are closed over extends: a member inherited from a base of
                // the base must be implemented against the interface that DECLARES it
                // (CS0539 otherwise). Fall back to the closure interface itself.
                var declarationMap = context.Options.InterfaceMemberDeclarationMap;

                foreach (var signature in baseSignatures)
                {
                    var parts = signature.Split('|');

                    if (parts.Length != 4)
                    {
                        continue;
                    }

                    var methodName = parts[2];
                    var argCount = parts[3].Trim().Length == 0
                        ? 0
                        : TypeHelper.SplitTopLevelTypeArguments(parts[3]).Count;

                    var key = $"{methodName.ToLowerInvariant()}#{argCount}";

                    // The member may be declared on an ancestor of the closure interface.
                    var declaringName = baseInterface;

                    if (declarationMap is not null
                        && declarationMap.TryGetValue($"{baseInterfaceLower}|{methodName.ToLowerInvariant()}", out var declaredOn))
                    {
                        declaringName = declaredOn;
                    }

                    // The declaring interface may itself be reshaped (abstract class with
                    // virtual stubs) — no explicit implementation is possible or needed.
                    if (reshapeable?.Contains(declaringName.ToLowerInvariant()) ?? false)
                    {
                        continue;
                    }

                    if (declared.Contains(key)
                        || (baseDefaults?.Contains(methodName.ToLowerInvariant()) ?? false)
                        || !added.Add($"{declaringName}#{key}"))
                    {
                        continue;
                    }

                    if (!TypeHelper.TryBuildSignatureMethod(context, signature, out var method))
                    {
                        continue;
                    }

                    // Explicit interface implementations cannot re-declare type parameters —
                    // they are inferred from the interface method.
                    if (method.TypeParameterList is not null)
                    {
                        method = method.WithTypeParameterList(null);
                    }

                    var interfaceType = SyntaxFactory.IdentifierName(declaringName);
                    var delegateTarget = SyntaxFactory.ParenthesizedExpression(
                        SyntaxFactory.CastExpression(
                            SyntaxFactory.IdentifierName(javaName),
                            SyntaxFactory.ThisExpression()));
                    var delegateCall = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            delegateTarget,
                            SyntaxFactory.IdentifierName(method.Identifier)),
                        SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                            method.ParameterList.Parameters.Select(p =>
                                SyntaxFactory.Argument(SyntaxFactory.IdentifierName(p.Identifier.Text))))));

                    StatementSyntax body = method.ReturnType.ToString().Trim() == "void"
                        ? SyntaxFactory.ExpressionStatement(delegateCall)
                        : SyntaxFactory.ReturnStatement(delegateCall);

                    // Explicit interface implementations cannot carry access modifiers (CS0106).
                    var explicitMember = method
                        .WithExplicitInterfaceSpecifier(SyntaxFactory.ExplicitInterfaceSpecifier(interfaceType))
                        .WithBody(SyntaxFactory.Block(body));

                    additions ??= [];
                    additions.Add(explicitMember);
                }
            }
        }

        if (additions is not null)
        {
            classSyntax = (T)classSyntax.AddMembers(additions.ToArray());
        }

        return classSyntax;
    }
}



