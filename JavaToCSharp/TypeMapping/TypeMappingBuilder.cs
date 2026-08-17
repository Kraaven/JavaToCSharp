using System.Globalization;

namespace JavaToCSharp.TypeMapping;

/// <summary>
/// Fluent builder for constructing a <see cref="TypeMappingIndex"/> programmatically instead of
/// from YAML. Entries added via the builder flow through the exact same registration, validation
/// and conflict-detection pipeline as YAML sources, so a programmatically built index behaves
/// identically to one loaded from a file.
///
/// <para>The builder also accepts YAML sources (<see cref="AddYaml"/>, <see cref="AddYamlFile"/>),
/// so a single mapping set can combine shared YAML files with programmatic additions. All
/// declarations are merged under the usual rules: the same <c>java_class</c> root with a matching
/// <c>csharp_class</c> merges its entries, while conflicting roots and duplicate member keys fail
/// <see cref="Build"/>.</para>
///
/// <para>Instances are not thread-safe but may be reused: <see cref="Build"/> can be called any
/// number of times, and declarations added after a <c>Build()</c> affect the next one.</para>
/// </summary>
/// <example>
/// <code>
/// var index = new TypeMappingBuilder()
///     .AddNamespace("java.lang", "System")
///     .AddClass("java.lang.Math", "Math", c => c
///         .Rename("abs", 1, "Abs")
///         .Template("random", 0, "new Random().NextDouble()", confidence: MappingConfidence.Medium))
///     .AddClass("java.lang.System", "Console", c => c
///         .Route("out", MemberMappingType.Field, n => n
///             .AddClass("java.io.PrintStream", "Console", p => p.Rename("println", 1, "WriteLine"))))
///     .Build();
/// </code>
/// </example>
public sealed class TypeMappingBuilder
{
    /// <summary>Source name reported for programmatically added declarations in error messages.</summary>
    public const string ProgrammaticSource = "<programmatic>";

    private readonly List<(string SourceName, MappingFileDto File)> _declarations = [];

    /// <summary>Adds a single Java package → C# namespace mapping (drives <c>using</c> injection).</summary>
    public TypeMappingBuilder AddNamespace(string javaPackage, string csharpPackage)
        => AddNamespace([javaPackage], [csharpPackage]);

    /// <summary>Adds a many-to-many package → namespace mapping, e.g. several Java packages
    /// collapsing into one C# namespace, or one Java package needing multiple <c>using</c>s.</summary>
    public TypeMappingBuilder AddNamespace(IEnumerable<string> javaPackages, IEnumerable<string> csharpPackages)
    {
        _declarations.Add((ProgrammaticSource, new MappingFileDto
        {
            Namespaces = [new NamespaceMappingDto
            {
                JavaPackages = [.. javaPackages],
                CSharpPackages = [.. csharpPackages],
            }],
        }));
        return this;
    }

    /// <summary>
    /// Adds a class mapping. Member entries are added through <paramref name="configure"/> (or by
    /// capturing the <see cref="ClassMappingBuilder"/> it receives); the returned builder is this
    /// <see cref="TypeMappingBuilder"/>, so the fluent chain can continue.
    /// </summary>
    public TypeMappingBuilder AddClass(
        string javaClass,
        string csharpClass,
        Action<ClassMappingBuilder>? configure = null,
        bool generic = false,
        ClassMappingKind kind = ClassMappingKind.Class)
    {
        var dto = new ClassMappingDto
        {
            JavaClass = javaClass,
            CSharpClass = csharpClass,
            Generic = generic ? true : null,
            Kind = kind == ClassMappingKind.Class ? null : kind.ToString().ToLowerInvariant(),
            Entries = [],
        };

        _declarations.Add((ProgrammaticSource, new MappingFileDto { ClassMap = [dto] }));

        configure?.Invoke(new ClassMappingBuilder(dto));
        return this;
    }

    /// <summary>Adds a raw YAML mapping source (deserialized immediately, so invalid YAML fails fast).</summary>
    public TypeMappingBuilder AddYaml(string yaml, string sourceName = "<inline>")
    {
        _declarations.Add((sourceName, TypeMappingIndex.ParseYaml(sourceName, yaml)));
        return this;
    }

    /// <summary>
    /// Adds one of the bundled built-in mapping specs (see <see cref="BuiltInMappings"/>).
    /// The spec is embedded YAML loaded through the same pipeline as <see cref="AddYamlFile"/>,
    /// so it merges with other built-ins and user files under the usual rules.
    /// </summary>
    public TypeMappingBuilder AddBuiltIn(BuiltInMappingSpec spec)
    {
        var sourceName = $"<built-in:{spec}>";
        _declarations.Add((sourceName, TypeMappingIndex.ParseYaml(sourceName, BuiltInMappings.YamlFor(spec))));
        return this;
    }

    /// <summary>Loads and adds a YAML mapping file.</summary>
    public TypeMappingBuilder AddYamlFile(string path)
    {
        var file = new FileInfo(path);
        return AddYaml(File.ReadAllText(file.FullName), file.FullName);
    }

    /// <summary>Loads and adds the given YAML mapping files in order.</summary>
    public TypeMappingBuilder AddYamlFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            AddYamlFile(path);
        }

        return this;
    }

    /// <summary>
    /// Validates all accumulated declarations and builds an immutable, thread-safe
    /// <see cref="TypeMappingIndex"/>. Throws <see cref="MappingValidationException"/> on any
    /// schema, conflict or duplicate-key violation.
    /// </summary>
    public TypeMappingIndex Build() => TypeMappingIndex.BuildFromDeclarations(_declarations);

    internal IEnumerable<ClassMappingDto> AllClassDeclarations =>
        _declarations.SelectMany(d => d.File.ClassMap ?? []);
}

/// <summary>
/// Fluent per-class member configuration. Instances are returned by
/// <see cref="TypeMappingBuilder.AddClass"/>; each method registers one member entry and returns
/// the builder for further chaining.
/// </summary>
public sealed class ClassMappingBuilder
{
    private readonly string _javaClass;
    private readonly string _csharpClass;
    private readonly List<MemberMappingDto> _entries;

    internal ClassMappingBuilder(ClassMappingDto dto)
    {
        _javaClass = dto.JavaClass ?? string.Empty;
        _csharpClass = dto.CSharpClass ?? string.Empty;
        _dto = dto;
        _entries = dto.Entries ??= [];
    }

    private readonly ClassMappingDto _dto;

    /// <summary>The fully qualified Java class name this builder configures.</summary>
    public string JavaClass => _javaClass;

    /// <summary>The bare C# type name this class maps to.</summary>
    public string CSharpClass => _csharpClass;

    /// <summary>Sets whether this class mapping represents a generic type.</summary>
    internal ClassMappingBuilder SetGeneric(bool isGeneric)
    {
        _dto.Generic = isGeneric ? true : null;
        return this;
    }

    /// <summary>Sets the kind of this class mapping (class, interface, exception).</summary>
    internal ClassMappingBuilder SetKind(ClassMappingKind kind)
    {
        _dto.Kind = kind == ClassMappingKind.Class ? null : kind.ToString().ToLowerInvariant();
        return this;
    }

    /// <summary>Maps a method to a renamed method with the same call shape: <c>Math.pow(a, b)</c>
    /// → <c>Math.Pow(a, b)</c>.</summary>
    public ClassMappingBuilder Rename(
        string javaMember,
        int argCount,
        string csharpMember,
        IReadOnlyList<string>? typeHint = null,
        MappingConfidence confidence = MappingConfidence.High,
        string? notes = null)
        => AddMember(MemberMappingKind.Rename, javaMember, argCount, null, csharpMember, null, typeHint, confidence, notes);

    /// <summary>Maps a variadic method to a renamed method matching any arity.</summary>
    public ClassMappingBuilder RenameVariadic(
        string javaMember,
        string csharpMember,
        IReadOnlyList<string>? typeHint = null,
        MappingConfidence confidence = MappingConfidence.High,
        string? notes = null)
        => AddMember(MemberMappingKind.Rename, javaMember, null, variadic: true, csharpMember, null, typeHint, confidence, notes);

    /// <summary>Maps a Java method call to a C# property access, dropping the parentheses:
    /// <c>str.length()</c> → <c>str.Length</c>.</summary>
    public ClassMappingBuilder Property(
        string javaMember,
        int argCount,
        string csharpMember,
        IReadOnlyList<string>? typeHint = null,
        MappingConfidence confidence = MappingConfidence.High,
        string? notes = null)
        => AddMember(MemberMappingKind.Property, javaMember, argCount, null, csharpMember, null, typeHint, confidence, notes);

    /// <summary>Maps a variadic method call to a C# property access matching any arity.</summary>
    public ClassMappingBuilder PropertyVariadic(
        string javaMember,
        string csharpMember,
        IReadOnlyList<string>? typeHint = null,
        MappingConfidence confidence = MappingConfidence.High,
        string? notes = null)
        => AddMember(MemberMappingKind.Property, javaMember, null, variadic: true, csharpMember, null, typeHint, confidence, notes);

    /// <summary>
    /// Replaces a call with an explicit expression built from <paramref name="expression"/>.
    /// Use <c>$0</c> for the receiver, <c>$1</c>… for positional arguments, and <c>$rest</c> for
    /// the trailing arguments of a variadic entry, e.g. <c>"$0.Substring($1, $2 - $1)"</c>.
    /// </summary>
    public ClassMappingBuilder Template(
        string javaMember,
        int argCount,
        string expression,
        IReadOnlyList<string>? typeHint = null,
        MappingConfidence confidence = MappingConfidence.High,
        string? notes = null)
        => AddMember(MemberMappingKind.Template, javaMember, argCount, null, null, expression, typeHint, confidence, notes);

    /// <summary>Template mapping for a variadic method.</summary>
    public ClassMappingBuilder TemplateVariadic(
        string javaMember,
        string expression,
        IReadOnlyList<string>? typeHint = null,
        MappingConfidence confidence = MappingConfidence.High,
        string? notes = null)
        => AddMember(MemberMappingKind.Template, javaMember, null, variadic: true, null, expression, typeHint, confidence, notes);

    /// <summary>Declares a member with no safe automatic mapping; call sites are left untranslated
    /// with a <c>TODO(java-to-csharp)</c> comment.</summary>
    public ClassMappingBuilder Unsupported(
        string javaMember,
        int? argCount = null,
        MappingConfidence confidence = MappingConfidence.High,
        string? notes = null)
        => AddMember(MemberMappingKind.Unsupported, javaMember, argCount, null, null, null, null, confidence, notes);

    /// <summary>Maps a field to a renamed field, e.g. <c>Math.PI</c>.</summary>
    public ClassMappingBuilder AddField(
        string javaMember,
        string csharpMember,
        MappingConfidence confidence = MappingConfidence.High,
        string? notes = null)
        => AddMember(MemberMappingKind.Rename, javaMember, null, null, csharpMember, null, null, confidence, notes,
            memberType: MemberMappingType.Field);

    /// <summary>
    /// Adds a routing node for a chained access such as <c>System.out.println(x)</c>. The node
    /// itself is elided from the output; the nested classes configured through
    /// <paramref name="configure"/> describe the segments that follow it.
    /// </summary>
    public ClassMappingBuilder Route(
        string javaMember,
        MemberMappingType memberType = MemberMappingType.Method,
        Action<TypeMappingBuilder>? configure = null)
    {
        var nested = new TypeMappingBuilder();
        configure?.Invoke(nested);

        var nestedClasses = nested.AllClassDeclarations.ToList();
        var dto = new MemberMappingDto
        {
            JavaMember = javaMember,
            MemberType = memberType == MemberMappingType.Field ? "field" : null,
            Kind = "rename",
            ClassMap = nestedClasses.Count > 0 ? nestedClasses : null,
        };
        _entries.Add(dto);
        return this;
    }

    private ClassMappingBuilder AddMember(
        MemberMappingKind kind,
        string javaMember,
        int? argCount,
        bool? variadic,
        string? csharpMember,
        string? expression,
        IReadOnlyList<string>? typeHint,
        MappingConfidence confidence,
        string? notes,
        MemberMappingType? memberType = null)
    {
        _entries.Add(new MemberMappingDto
        {
            JavaMember = javaMember,
            MemberType = memberType == MemberMappingType.Field ? "field" : null,
            ArgCount = variadic == true ? "variadic" : argCount?.ToString(CultureInfo.InvariantCulture),
            Kind = kind.ToString().ToLowerInvariant(),
            CSharpMember = csharpMember,
            Expression = expression,
            TypeHint = typeHint is null ? null : [.. typeHint],
            Confidence = confidence == MappingConfidence.High ? null : confidence.ToString().ToLowerInvariant(),
            Notes = notes,
        });
        return this;
    }
}
