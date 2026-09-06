namespace JavaToCSharp.TypeMapping;

/// <summary>
/// The kind of member-level transformation a mapping entry requests.
/// </summary>
public enum MemberMappingKind
{
    /// <summary>Rename the member (method calls keep their parentheses and arguments).</summary>
    Rename,

    /// <summary>Rewrite a Java method/property access as a C# property access (parentheses dropped).</summary>
    Property,

    /// <summary>Replace the call with an explicit C# expression built from the provided template.</summary>
    Template,

    /// <summary>No safe mapping exists; the original Java fragment is emitted with a TODO marker.</summary>
    Unsupported,
}

/// <summary>Whether a mapping entry applies to a method, a field, or a constructor.</summary>
public enum MemberMappingType
{
    Method,
    Field,
    Constructor,
}

/// <summary>Metadata describing what kind of Java type a class mapping represents.</summary>
public enum ClassMappingKind
{
    Class,
    Interface,
    Exception,
}

/// <summary>Confidence of a mapping entry, used to emit REVIEW comments.</summary>
public enum MappingConfidence
{
    High,
    Medium,
    Low,
}

/// <summary>
/// A namespace mapping: a set of Java packages that map to a set of C# namespaces.
/// The first C# package is emitted as a using when any class in one of the Java
/// packages is resolved through the mapping set.
/// </summary>
public sealed class NamespaceMapping
{
    internal NamespaceMapping(string sourceFile, IReadOnlyList<string> javaPackages, IReadOnlyList<string> csharpPackages)
    {
        SourceFile = sourceFile;
        JavaPackages = javaPackages;
        CSharpPackages = csharpPackages;
    }

    public string SourceFile { get; }

    public IReadOnlyList<string> JavaPackages { get; }

    public IReadOnlyList<string> CSharpPackages { get; }
}

/// <summary>
/// A class-level mapping. Identified by its fully qualified Java class name and
/// converted to the given bare C# class name. Contains member-level entries and
/// optionally nested class mappings for chained member access.
/// </summary>
public sealed class ClassMapping
{
    internal ClassMapping(
        string sourceFile,
        string javaClass,
        string csharpClass,
        bool isGeneric,
        ClassMappingKind kind,
        bool matchQualifiedOnly,
        IReadOnlyList<MemberMapping> entries)
    {
        SourceFile = sourceFile;
        JavaClass = javaClass;
        CSharpClass = csharpClass;
        IsGeneric = isGeneric;
        Kind = kind;
        MatchQualifiedOnly = matchQualifiedOnly;
        Entries = entries;
    }

    public string SourceFile { get; }

    public string JavaClass { get; }

    /// <summary>The bare (unqualified) C# type name.</summary>
    public string CSharpClass { get; }

    public bool IsGeneric { get; }

    public ClassMappingKind Kind { get; }

    /// <summary>
    /// When true, the mapping is only applied through the fully qualified Java class name.
    /// The class is excluded from simple-name lookup, so it never steals references written
    /// as a bare simple name (e.g. <c>Packet</c>) even when no other class claims that name.
    /// Use for placeholder classes whose simple name collides with unrelated target types.
    /// </summary>
    public bool MatchQualifiedOnly { get; }

    public IReadOnlyList<MemberMapping> Entries { get; }

    public string SimpleName =>
        JavaClass.IndexOf('.') >= 0 ? JavaClass[(JavaClass.LastIndexOf('.') + 1)..] : JavaClass;
}

/// <summary>
/// A single member-level mapping entry. The lookup key is
/// (<see cref="JavaMember"/>, <see cref="MemberType"/>, <see cref="ArgCount"/>,
/// <see cref="TypeHint"/>).
/// </summary>
public sealed class MemberMapping
{
    internal MemberMapping(
        string sourceFile,
        string ownerClass,
        string javaMember,
        MemberMappingType memberType,
        int? argCount,
        bool isVariadic,
        MemberMappingKind kind,
        string? csharpMember,
        string? expression,
        IReadOnlyList<string>? typeHint,
        MappingConfidence confidence,
        string? notes,
        IReadOnlyList<ClassMapping> classMap,
        string? returnsType)
    {
        SourceFile = sourceFile;
        OwnerClass = ownerClass;
        JavaMember = javaMember;
        MemberType = memberType;
        ArgCount = argCount;
        IsVariadic = isVariadic;
        Kind = kind;
        CSharpMember = csharpMember;
        Expression = expression;
        TypeHint = typeHint;
        Confidence = confidence;
        Notes = notes;
        ClassMap = classMap;
        ReturnsType = returnsType;
    }

    public string SourceFile { get; }

    /// <summary>The java_class this entry belongs to, for error and comment messages.</summary>
    public string OwnerClass { get; }

    public string JavaMember { get; }

    public MemberMappingType MemberType { get; }

    /// <summary>The fixed arity for methods; null for fields and variadic entries.</summary>
    public int? ArgCount { get; }

    /// <summary>True when the entry declares <c>arg_count: variadic</c> and matches any arity.</summary>
    public bool IsVariadic { get; }

    public MemberMappingKind Kind { get; }

    /// <summary>The renamed / property member name. Null for template and unsupported entries.</summary>
    public string? CSharpMember { get; }

    /// <summary>The template expression for <see cref="MemberMappingKind.Template"/> entries.</summary>
    public string? Expression { get; }

    /// <summary>Optional argument type hints used to disambiguate overloads.</summary>
    public IReadOnlyList<string>? TypeHint { get; }

    public MappingConfidence Confidence { get; }

    public string? Notes { get; }

    /// <summary>Nested class mappings owned by this member (routing nodes).</summary>
    public IReadOnlyList<ClassMapping> ClassMap { get; }

    /// <summary>
    /// The Java return type of this method, used by chain resolution to resolve
    /// chained calls when the variable's declared type is not available.
    /// </summary>
    public string? ReturnsType { get; }

    /// <summary>
    /// The unique lookup key for a member entry. Type-hint entries share a key only when
    /// they also share an identical hint, which is how cross-file duplicates are detected.
    /// </summary>
    internal string LookupKey =>
        string.Join('|', JavaMember, MemberType.ToString(), ArgCount?.ToString() ?? (IsVariadic ? "variadic" : ""), TypeHintKey);

    private string TypeHintKey => TypeHint is null ? "" : string.Join(",", TypeHint);
}
