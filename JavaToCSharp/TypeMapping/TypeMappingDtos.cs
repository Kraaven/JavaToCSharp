using YamlDotNet.Serialization;

namespace JavaToCSharp.TypeMapping;

/// <summary>Deserialization shape of a whole mapping file (snake_case YAML keys).</summary>
internal sealed class MappingFileDto
{
    public List<NamespaceMappingDto>? Namespaces { get; set; }

    public List<ClassMappingDto>? ClassMap { get; set; }
}

/// <summary>Deserialization shape of a <c>namespaces</c> entry.</summary>
internal sealed class NamespaceMappingDto
{
    public List<string>? JavaPackages { get; set; }

    [YamlMember(Alias = "csharp_packages")]
    public List<string>? CSharpPackages { get; set; }
}

/// <summary>Deserialization shape of a <c>class_map</c> entry.</summary>
internal sealed class ClassMappingDto
{
    public string? JavaClass { get; set; }

    [YamlMember(Alias = "csharp_class")]
    public string? CSharpClass { get; set; }

    public bool? Generic { get; set; }

    public string? Kind { get; set; }

    [YamlMember(Alias = "match_qualified_only")]
    public bool? MatchQualifiedOnly { get; set; }

    /// <summary>
    /// True when a bare simple name imported via a single-type import should resolve to this
    /// class through the import map (nested-type rewrite rows). Independent of
    /// MatchQualifiedOnly: that flag blocks simple-name lookup, this re-enables the
    /// import-disambiguated path only.
    /// </summary>
    [YamlMember(Alias = "import_resolvable")]
    public bool? ImportResolvable { get; set; }

    public List<MemberMappingDto>? Entries { get; set; }
}

/// <summary>Deserialization shape of an <c>entries</c> entry.</summary>
internal sealed class MemberMappingDto
{
    public string? JavaMember { get; set; }

    public string? MemberType { get; set; }

    /// <summary>An integer arity or the string <c>variadic</c>.</summary>
    public string? ArgCount { get; set; }

    public string? Kind { get; set; }

    [YamlMember(Alias = "csharp_member")]
    public string? CSharpMember { get; set; }

    public string? Expression { get; set; }

    public List<string>? TypeHint { get; set; }

    public string? Confidence { get; set; }

    public string? Notes { get; set; }

    [YamlMember(Alias = "returns_type")]
    public string? ReturnsType { get; set; }

    /// <summary>
    /// True when the Java member is static. Lets the method-reference emitter choose the
    /// static-style lambda shape (<c>Type::method</c> → <c>x => Type.Method(x)</c>) without
    /// heuristics.
    /// </summary>
    [YamlMember(Alias = "static")]
    public bool? Static { get; set; }

    public List<ClassMappingDto>? ClassMap { get; set; }
}
