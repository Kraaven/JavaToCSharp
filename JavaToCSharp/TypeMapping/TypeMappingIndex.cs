using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace JavaToCSharp.TypeMapping;

/// <summary>
/// An immutable, validated index of a loaded mapping set. Instances are safe to share
/// across parallel conversions. Loading validates the schema, merges entries across
/// files, and fails the whole load (with file and field locations) on any conflict.
/// </summary>
public sealed partial class TypeMappingIndex
{
    private const string Variadic = "variadic";

    private readonly IReadOnlyDictionary<string, ClassMapping> _byQualifiedName;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ClassMapping>> _bySimpleName;
    private readonly IReadOnlyDictionary<string, string[]> _csharpPackagesByPackage;

    private TypeMappingIndex(
        IReadOnlyDictionary<string, ClassMapping> byQualifiedName,
        IReadOnlyDictionary<string, IReadOnlyList<ClassMapping>> bySimpleName,
        IReadOnlyDictionary<string, string[]> csharpPackagesByPackage)
    {
        _byQualifiedName = byQualifiedName;
        _bySimpleName = bySimpleName;
        _csharpPackagesByPackage = csharpPackagesByPackage;
    }

    /// <summary>All class mappings (roots and nested) registered in the mapping set.</summary>
    public IReadOnlyCollection<ClassMapping> Classes => [.. _byQualifiedName.Values];

    /// <summary>
    /// All Java-package → C#-namespace mappings. Each key is a Java package prefix and each
    /// value is the corresponding set of C# namespaces. Used by <see cref="CopyToBuilder"/>
    /// to reconstruct the namespace mapping layer.
    /// </summary>
    public IReadOnlyDictionary<string, string[]> NamespaceMappings => _csharpPackagesByPackage;

    /// <summary>
    /// Loads and validates the given mapping files. Files are processed in a deterministic
    /// order (full path, ordinal) so identical inputs always produce the same index.
    /// </summary>
    public static TypeMappingIndex LoadFromFiles(IEnumerable<string> filePaths)
    {
        var sources = filePaths
            .Select(p => new FileInfo(p))
            .OrderBy(f => f.FullName, StringComparer.Ordinal)
            .Select(f => (SourceName: f.FullName, Yaml: File.ReadAllText(f.FullName)));
        return LoadFromSources(sources);
    }

    /// <summary>Loads a mapping set from a single YAML string (used by tests).</summary>
    public static TypeMappingIndex LoadFromYaml(string yaml, string sourceName = "<inline>")
        => LoadFromSources([(sourceName, yaml)]);

    /// <summary>Loads and validates a mapping set from named YAML sources.</summary>
    public static TypeMappingIndex LoadFromSources(IEnumerable<(string SourceName, string Yaml)> sources)
    {
        var files = sources
            .OrderBy(s => s.SourceName, StringComparer.Ordinal)
            .Select(s => (s.SourceName, File: ParseYaml(s.SourceName, s.Yaml)));
        return BuildFromDeclarations(files);
    }

    /// <summary>Deserializes a single YAML mapping source, wrapping parse failures in a
    /// <see cref="MappingValidationException"/>.</summary>
    internal static MappingFileDto ParseYaml(string sourceName, string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        try
        {
            return deserializer.Deserialize<MappingFileDto>(yaml) ?? new MappingFileDto();
        }
        catch (Exception ex)
        {
            throw new MappingValidationException($"Mapping file '{sourceName}' is invalid YAML: {ex.Message}");
        }
    }

    /// <summary>
    /// Registers, validates and freezes the given declarations into an immutable index. This is the
    /// shared pipeline for both YAML-loaded and programmatically built mapping sets, so the two
    /// paths always behave identically.
    /// </summary>
    internal static TypeMappingIndex BuildFromDeclarations(IEnumerable<(string SourceName, MappingFileDto File)> files)
    {
        var classes = new Dictionary<string, MutableClass>(StringComparer.Ordinal);
        var csharpPackagesByPackage = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var (sourceName, fileDto) in files)
        {
            if (fileDto.Namespaces is { Count: > 0 })
            {
                foreach (var ns in fileDto.Namespaces)
                {
                    RegisterNamespaces(csharpPackagesByPackage, sourceName, ns);
                }
            }

            if (fileDto.ClassMap is not null)
            {
                foreach (var classDto in fileDto.ClassMap)
                {
                    RegisterClass(classes, sourceName, classDto, $"class_map '{classDto.JavaClass}'");
                }
            }
        }

        var byQualifiedName = new Dictionary<string, ClassMapping>(StringComparer.Ordinal);
        foreach (var name in classes.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            byQualifiedName[name] = FreezeClass(classes, name);
        }

        var bySimpleName = new Dictionary<string, List<ClassMapping>>(StringComparer.Ordinal);
        foreach (var cls in byQualifiedName.Values)
        {
            if (!bySimpleName.TryGetValue(cls.SimpleName, out var list))
            {
                list = [];
                bySimpleName[cls.SimpleName] = list;
            }

            list.Add(cls);
        }

        return new TypeMappingIndex(
            byQualifiedName,
            bySimpleName.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ClassMapping>)kv.Value.AsReadOnly(), StringComparer.Ordinal),
            new Dictionary<string, string[]>(csharpPackagesByPackage, StringComparer.Ordinal));
    }

    public bool TryGetByQualifiedName(string javaClassName, out ClassMapping? cls)
    {
        return _byQualifiedName.TryGetValue(javaClassName, out cls);
    }

    /// <summary>
    /// Matches a class by simple name. Returns false with <paramref name="ambiguous"/>
    /// set when more than one class in the mapping set shares the simple name.
    /// </summary>
    public bool TryGetBySimpleName(string simpleName, out ClassMapping? cls, out bool ambiguous)
    {
        if (_bySimpleName.TryGetValue(simpleName, out var list))
        {
            if (list.Count == 1)
            {
                cls = list[0];
                ambiguous = false;
                return true;
            }

            cls = null;
            ambiguous = true;
            return false;
        }

        cls = null;
        ambiguous = false;
        return false;
    }

    /// <summary>
    /// Locates the nearest (longest prefix) package mapping for a Java package.
    /// Returns the C# packages with the primary (first) one used as the emitted using.
    /// </summary>
    public bool TryGetCSharpPackages(string javaPackage, out string[] csharpPackages)
    {
        var segments = javaPackage.Split('.');
        for (var length = segments.Length; length >= 1; length--)
        {
            var candidate = string.Join(".", segments.Take(length));
            if (_csharpPackagesByPackage.TryGetValue(candidate, out var packages))
            {
                csharpPackages = packages;
                return true;
            }
        }

        csharpPackages = [];
        return false;
    }

    /// <summary>
    /// Resolves a (possibly dotted) Java type reference such as <c>Map</c> or <c>Map.Entry</c>
    /// to a mapped class. Tries the exact qualified name, then the simple name, then a
    /// first-segment + nested-remainder path (e.g. simple name <c>Map</c> → java.util.Map, then
    /// <c>java.util.Map.Entry</c>).
    /// </summary>
    public bool TryGetByDottedName(string dottedName, out ClassMapping? cls, out bool ambiguous)
    {
        cls = null;
        ambiguous = false;

        if (TryGetByQualifiedName(dottedName, out cls))
        {
            return true;
        }

        var segments = dottedName.Split('.');
        if (segments.Length == 1)
        {
            return TryGetBySimpleName(dottedName, out cls, out ambiguous);
        }

        if (!TryGetByQualifiedName(segments[0], out var first)
            && !TryGetBySimpleName(segments[0], out first, out var simpleAmbiguous))
        {
            ambiguous = simpleAmbiguous;
            return false;
        }

        var qualified = $"{first!.JavaClass}.{string.Join(".", segments.Skip(1))}";
        return TryGetByQualifiedName(qualified, out cls);
    }

    private static void RegisterNamespaces(
        Dictionary<string, string[]> byPackage,
        string sourceName,
        NamespaceMappingDto ns)
    {
        if (ns.JavaPackages is not { Count: > 0 })
        {
            throw new MappingValidationException(
                $"Mapping file '{sourceName}' is invalid: namespaces entry requires a non-empty 'java_packages' list.");
        }

        if (ns.CSharpPackages is not { Count: > 0 })
        {
            throw new MappingValidationException(
                $"Mapping file '{sourceName}' is invalid: namespaces entry '{ns.JavaPackages[0]}' requires a non-empty 'csharp_packages' list.");
        }

        foreach (var javaPackage in ns.JavaPackages)
        {
            if (!IsValidJavaClass(javaPackage))
            {
                throw new MappingValidationException(
                    $"Mapping file '{sourceName}' is invalid: namespaces entry has invalid java_packages value '{javaPackage}'.");
            }
        }

        foreach (var csharpPackage in ns.CSharpPackages)
        {
            if (string.IsNullOrWhiteSpace(csharpPackage))
            {
                throw new MappingValidationException(
                    $"Mapping file '{sourceName}' is invalid: namespaces entry '{ns.JavaPackages[0]}' contains an empty csharp_packages value.");
            }
        }

        var csharp = ns.CSharpPackages.ToArray();
        foreach (var javaPackage in ns.JavaPackages)
        {
            if (byPackage.TryGetValue(javaPackage, out var existing) && !existing.SequenceEqual(csharp, StringComparer.Ordinal))
            {
                throw new MappingValidationException(
                    $"Mapping files conflict for Java package '{javaPackage}': maps to '{string.Join(", ", existing)}' and '{string.Join(", ", csharp)}'.");
            }

            byPackage[javaPackage] = csharp;
        }
    }

    private static void RegisterClass(
        Dictionary<string, MutableClass> classes,
        string sourceName,
        ClassMappingDto dto,
        string ownerPath)
    {
        if (string.IsNullOrWhiteSpace(dto.JavaClass) || !IsValidJavaClass(dto.JavaClass))
        {
            throw new MappingValidationException(
                $"Mapping file '{sourceName}' is invalid: {ownerPath} requires a valid 'java_class' (a dotted Java type name).");
        }

        if (string.IsNullOrWhiteSpace(dto.CSharpClass) || !IsValidIdentifier(dto.CSharpClass))
        {
            throw new MappingValidationException(
                $"Mapping file '{sourceName}' is invalid: {ownerPath} requires a valid 'csharp_class' (a bare C# type name).");
        }

        var kind = ParseClassKind(sourceName, ownerPath, dto.Kind);

        if (!classes.TryGetValue(dto.JavaClass, out var cls))
        {
            cls = new MutableClass
            {
                SourceFile = sourceName,
                JavaClass = dto.JavaClass,
                CSharpClass = dto.CSharpClass,
                IsGeneric = dto.Generic ?? false,
                Kind = kind,
            };
            classes.Add(dto.JavaClass, cls);
        }
        else if (!string.Equals(cls.CSharpClass, dto.CSharpClass, StringComparison.Ordinal))
        {
            throw new MappingValidationException(
                $"class_map '{dto.JavaClass}' is defined in both '{cls.SourceFile}' and '{sourceName}' with different csharp_class values ('{cls.CSharpClass}' vs '{dto.CSharpClass}').");
        }

        if (dto.Entries is null)
        {
            return;
        }

        var entryPath = $"{ownerPath} entries";
        foreach (var entryDto in dto.Entries)
        {
            cls.PendingMembers.Add(ConvertEntry(classes, sourceName, cls, entryDto, entryPath));
        }
    }

    private static PendingMember ConvertEntry(
        Dictionary<string, MutableClass> classes,
        string sourceName,
        MutableClass owner,
        MemberMappingDto dto,
        string entryPath)
    {
        var javaMember = dto.JavaMember;
        var path = $"{entryPath} '{javaMember}'";

        if (string.IsNullOrWhiteSpace(javaMember) || !IsValidIdentifier(javaMember))
        {
            throw new MappingValidationException(
                $"Mapping file '{sourceName}' is invalid: {path} requires a valid 'java_member' (a simple Java member name).");
        }

        var memberType = ParseMemberType(sourceName, path, dto.MemberType);
        var kind = ParseMemberKind(sourceName, path, dto.Kind);
        var (argCount, isVariadic) = ParseArgCount(sourceName, path, dto.ArgCount, memberType, kind);
        var confidence = ParseConfidence(sourceName, path, dto.Confidence);

        var isRouting = dto.ClassMap is { Count: > 0 };
        if ((kind == MemberMappingKind.Rename || kind == MemberMappingKind.Property) && !isRouting
            && (string.IsNullOrWhiteSpace(dto.CSharpMember) || !IsValidIdentifier(dto.CSharpMember)))
        {
            throw new MappingValidationException(
                $"Mapping file '{sourceName}' is invalid: {path} requires a valid 'csharp_member' for kind '{dto.Kind ?? "rename"}'.");
        }

        if (kind == MemberMappingKind.Template && string.IsNullOrWhiteSpace(dto.Expression))
        {
            throw new MappingValidationException(
                $"Mapping file '{sourceName}' is invalid: {path} requires a non-empty 'expression' for kind 'template'.");
        }

        IReadOnlyList<string>? typeHint = null;
        if (dto.TypeHint is { Count: > 0 })
        {
            typeHint = dto.TypeHint.AsReadOnly();
        }

        var nestedClassNames = new List<string>();
        if (dto.ClassMap is { Count: > 0 })
        {
            foreach (var nestedDto in dto.ClassMap)
            {
                var nestedPath = $"{path} class_map";
                RegisterClass(classes, sourceName, nestedDto, nestedPath);
                if (nestedDto.JavaClass is not null)
                {
                    nestedClassNames.Add(nestedDto.JavaClass);
                }
            }
        }

        return new PendingMember(
            sourceName,
            owner.JavaClass,
            javaMember,
            memberType,
            argCount,
            isVariadic,
            kind,
            dto.CSharpMember,
            dto.Expression,
            typeHint,
            confidence,
            dto.Notes,
            nestedClassNames);
    }

    private static MemberMappingType ParseMemberType(string sourceName, string path, string? raw)
    {
        if (raw is null or "")
        {
            return MemberMappingType.Method;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "method" => MemberMappingType.Method,
            "field" => MemberMappingType.Field,
            _ => throw new MappingValidationException(
                $"Mapping file '{sourceName}' is invalid: {path} has invalid member_type '{raw}'. Allowed: method, field."),
        };
    }

    private static MemberMappingKind ParseMemberKind(string sourceName, string path, string? raw)
    {
        if (raw is null or "")
        {
            return MemberMappingKind.Rename;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "rename" => MemberMappingKind.Rename,
            "property" => MemberMappingKind.Property,
            "template" => MemberMappingKind.Template,
            "unsupported" => MemberMappingKind.Unsupported,
            _ => throw new MappingValidationException(
                $"Mapping file '{sourceName}' is invalid: {path} has invalid kind '{raw}'. Allowed: rename, property, template, unsupported."),
        };
    }

    private static ClassMappingKind ParseClassKind(string sourceName, string ownerPath, string? raw)
    {
        if (raw is null or "")
        {
            return ClassMappingKind.Class;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "class" => ClassMappingKind.Class,
            "interface" => ClassMappingKind.Interface,
            "exception" => ClassMappingKind.Exception,
            _ => throw new MappingValidationException(
                $"Mapping file '{sourceName}' is invalid: {ownerPath} has invalid kind '{raw}'. Allowed: class, interface, exception."),
        };
    }

    private static MappingConfidence ParseConfidence(string sourceName, string path, string? raw)
    {
        if (raw is null or "")
        {
            return MappingConfidence.High;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "high" => MappingConfidence.High,
            "medium" => MappingConfidence.Medium,
            "low" => MappingConfidence.Low,
            _ => throw new MappingValidationException(
                $"Mapping file '{sourceName}' is invalid: {path} has invalid confidence '{raw}'. Allowed: high, medium, low."),
        };
    }

    private static (int? ArgCount, bool IsVariadic) ParseArgCount(
        string sourceName,
        string path,
        string? raw,
        MemberMappingType memberType,
        MemberMappingKind kind)
    {
        if (memberType == MemberMappingType.Field)
        {
            if (raw is not null and not "")
            {
                throw new MappingValidationException(
                    $"Mapping file '{sourceName}' is invalid: {path} is a field and must not declare 'arg_count'.");
            }

            return (null, false);
        }

        if (raw is null or "")
        {
            if (kind == MemberMappingKind.Unsupported)
            {
                return (null, false);
            }

            throw new MappingValidationException(
                $"Mapping file '{sourceName}' is invalid: {path} is a method and requires 'arg_count' (an integer or '{Variadic}').");
        }

        if (string.Equals(raw.Trim(), Variadic, StringComparison.OrdinalIgnoreCase))
        {
            return (null, true);
        }

        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
        {
            return (count, false);
        }

        throw new MappingValidationException(
            $"Mapping file '{sourceName}' is invalid: {path} has invalid arg_count '{raw}'. Expected an integer or '{Variadic}'.");
    }

    private static ClassMapping FreezeClass(Dictionary<string, MutableClass> classes, string name)
    {
        var inProgress = new HashSet<string>(StringComparer.Ordinal);
        var cache = new Dictionary<string, ClassMapping>(StringComparer.Ordinal);
        return FreezeClassCore(classes, name, inProgress, cache);
    }

    private static ClassMapping FreezeClassCore(
        Dictionary<string, MutableClass> classes,
        string name,
        HashSet<string> inProgress,
        Dictionary<string, ClassMapping> cache)
    {
        if (cache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        if (!inProgress.Add(name))
        {
            throw new MappingValidationException(
                $"class_map '{name}' has a cyclic nested class_map reference.");
        }

        var cls = classes[name];

        var keyed = cls.PendingMembers
            .GroupBy(m => m.LookupKey, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();
        if (keyed.Count > 0)
        {
            var dup = keyed[0].ToList();
            var files = string.Join(", ", dup.Select(d => $"'{d.SourceFile}'").Distinct(StringComparer.Ordinal));
            throw new MappingValidationException(
                $"class_map '{name}' defines duplicate entry '{dup[0].OwnerClass}.{dup[0].JavaMember}' (arg_count {dup[0].ArgCountText}): declared in {files}.");
        }

        var members = new List<MemberMapping>(cls.PendingMembers.Count);
        foreach (var member in cls.PendingMembers)
        {
            var nested = member.NestedClassNames
                .Select(n => FreezeClassCore(classes, n, inProgress, cache))
                .ToList();
            members.Add(new MemberMapping(
                member.SourceFile,
                member.OwnerClass,
                member.JavaMember,
                member.MemberType,
                member.ArgCount,
                member.IsVariadic,
                member.Kind,
                member.CSharpMember,
                member.Expression,
                member.TypeHint,
                member.Confidence,
                member.Notes,
                nested));
        }

        var result = new ClassMapping(
            cls.SourceFile,
            cls.JavaClass,
            cls.CSharpClass,
            cls.IsGeneric,
            cls.Kind,
            members.AsReadOnly());

        cache[name] = result;
        inProgress.Remove(name);
        return result;
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();

    private static bool IsValidIdentifier(string value) => IdentifierRegex().IsMatch(value);

    private static bool IsValidJavaClass(string value) =>
        value.Split('.').All(IsValidIdentifier);

    private sealed class MutableClass
    {
        public required string SourceFile { get; init; }

        public required string JavaClass { get; init; }

        public required string CSharpClass { get; init; }

        public bool IsGeneric { get; init; }

        public ClassMappingKind Kind { get; init; }

        public List<PendingMember> PendingMembers { get; } = [];
    }

    private sealed class PendingMember
    {
        public PendingMember(
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
            List<string> nestedClassNames)
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
            NestedClassNames = nestedClassNames;
        }

        public string SourceFile { get; }

        public string OwnerClass { get; }

        public string JavaMember { get; }

        public MemberMappingType MemberType { get; }

        public int? ArgCount { get; }

        public bool IsVariadic { get; }

        public MemberMappingKind Kind { get; }

        public string? CSharpMember { get; }

        public string? Expression { get; }

        public IReadOnlyList<string>? TypeHint { get; }

        public MappingConfidence Confidence { get; }

        public string? Notes { get; }

        public List<string> NestedClassNames { get; }

        public string ArgCountText =>
            MemberType == MemberMappingType.Field ? "n/a"
            : IsVariadic ? Variadic
            : ArgCount?.ToString(CultureInfo.InvariantCulture) ?? "n/a";

        public string LookupKey =>
            string.Join('|', JavaMember, MemberType.ToString(), ArgCountText, TypeHint is null ? "" : string.Join(",", TypeHint));
    }
}
