namespace JavaToCSharp.TypeMapping;

/// <summary>
/// Extension methods for copying and modifying existing <see cref="TypeMappingIndex"/> instances.
/// </summary>
public static class TypeMappingIndexExtensions
{
    /// <summary>
    /// Creates a new <see cref="TypeMappingBuilder"/> pre-populated with all namespace, class,
    /// and member mappings from this index. The builder can be modified (add/change/remove
    /// entries) and then built into a new, modified index. This is useful for copying a
    /// built-in mapping set and making targeted changes.
    /// </summary>
    /// <example>
    /// <code>
    /// var modified = BuiltInMappings.Console
    ///     .CopyToBuilder()
    ///     .AddClass("com.example.MyClass", "MyClass", c => c.RenameVariadic("doWork", "Execute"))
    ///     .Build();
    /// </code>
    /// </example>
    public static TypeMappingBuilder CopyToBuilder(this TypeMappingIndex index)
    {
        var builder = new TypeMappingBuilder();

        // Re-add namespace mappings (flattened dictionary form), preserving
        // every mapped C# namespace - not just the first.
        foreach (var (javaPackage, csharpPackages) in index.NamespaceMappings)
        {
            builder.AddNamespace([javaPackage], csharpPackages);
        }

        // Re-add class mappings with their members
        foreach (var cls in index.Classes)
        {
            builder.AddClass(cls.JavaClass, cls.CSharpClass, c =>
            {
                c.SetGeneric(cls.IsGeneric);
                c.SetKind(cls.Kind);
                c.SetMatchQualifiedOnly(cls.MatchQualifiedOnly);
                c.SetImportResolvable(cls.ImportResolvable);

                foreach (var entry in cls.Entries)
                {
                    CopyMemberEntry(c, entry);
                }
            }, cls.IsGeneric, cls.Kind, cls.MatchQualifiedOnly);
        }

        return builder;
    }

    private static void CopyMemberEntry(ClassMappingBuilder c, MemberMapping entry)
    {
        switch (entry.Kind)
        {
            case MemberMappingKind.Rename:
                if (entry.MemberType == MemberMappingType.Field)
                {
                    c.AddField(entry.JavaMember, entry.CSharpMember!, entry.Confidence, entry.Notes);
                }
                else if (entry.IsVariadic)
                {
                    c.RenameVariadic(entry.JavaMember, entry.CSharpMember!, entry.TypeHint, entry.Confidence, entry.Notes);
                }
                else
                {
                    c.Rename(entry.JavaMember, entry.ArgCount ?? 0, entry.CSharpMember!, entry.TypeHint, entry.Confidence, entry.Notes);
                }
                break;

            case MemberMappingKind.Property:
                if (entry.IsVariadic)
                {
                    c.PropertyVariadic(entry.JavaMember, entry.CSharpMember!, entry.TypeHint, entry.Confidence, entry.Notes);
                }
                else
                {
                    c.Property(entry.JavaMember, entry.ArgCount ?? 0, entry.CSharpMember!, entry.TypeHint, entry.Confidence, entry.Notes);
                }
                break;

            case MemberMappingKind.Template:
                if (entry.IsVariadic)
                {
                    c.TemplateVariadic(entry.JavaMember, entry.Expression!, entry.TypeHint, entry.Confidence, entry.Notes);
                }
                else
                {
                    c.Template(entry.JavaMember, entry.ArgCount ?? 0, entry.Expression!, entry.TypeHint, entry.Confidence, entry.Notes);
                }
                break;

            case MemberMappingKind.Unsupported:
                c.Unsupported(entry.JavaMember, entry.ArgCount, entry.Confidence, entry.Notes);
                break;
        }

        if (entry.IsStatic)
        {
            c.MarkStatic(entry.JavaMember);
        }

        // Re-add nested class mappings (routing nodes)
        if (entry.ClassMap is { Count: > 0 })
        {
            c.Route(entry.JavaMember, entry.MemberType, nested =>
            {
                foreach (var nestedCls in entry.ClassMap)
                {
                    nested.AddClass(nestedCls.JavaClass, nestedCls.CSharpClass, nc =>
                    {
                        nc.SetGeneric(nestedCls.IsGeneric);
                        nc.SetKind(nestedCls.Kind);
                        nc.SetMatchQualifiedOnly(nestedCls.MatchQualifiedOnly);

                        foreach (var nestedEntry in nestedCls.Entries)
                        {
                            CopyMemberEntry(nc, nestedEntry);
                        }
                    }, nestedCls.IsGeneric, nestedCls.Kind, nestedCls.MatchQualifiedOnly);
                }
            });
        }
    }
}
