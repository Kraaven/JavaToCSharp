namespace JavaToCSharp.TypeMapping;

/// <summary>
/// Holds the set of Java packages that are actively imported or auto-imported in the current
/// file being converted. Used to gate mapping resolution: only types from active packages
/// are resolved through the <see cref="TypeMappingIndex"/>, preventing false-positive
/// remapping on user-defined types that happen to share names with mapped types.
/// Also provides import-based class name resolution for chains like <c>A.sb.func()</c>
/// where <c>A</c> is an imported class.
/// </summary>
public sealed class FileMappingContext
{
    private readonly HashSet<string> _activeJavaPackages;
    private readonly TypeMappingIndex? _index;
    private readonly Dictionary<string, string> _classNameToFQN;

    /// <summary>
    /// Creates a new file mapping context by scanning the file's imports against the mapping index.
    /// </summary>
    /// <param name="index">The mapping index to check against. If null, all packages are considered active.</param>
    /// <param name="importNames">Fully qualified import names from the Java file (e.g. "java.util.List").</param>
    /// <param name="autoImportedPackages">Packages that are auto-imported in Java (e.g. "java.lang").</param>
    public FileMappingContext(
        TypeMappingIndex? index,
        IEnumerable<string> importNames,
        IEnumerable<string>? autoImportedPackages = null)
    {
        _index = index;
        _activeJavaPackages = new HashSet<string>(StringComparer.Ordinal);
        _classNameToFQN = new Dictionary<string, string>(StringComparer.Ordinal);

        if (index is null)
        {
            // No mapping index → all packages are "active" (no gating needed, everything passes through)
            return;
        }

        // Add auto-imported packages (java.lang.* by default)
        if (autoImportedPackages is not null)
        {
            foreach (var pkg in autoImportedPackages)
            {
                _activeJavaPackages.Add(pkg);
            }
        }

        // Scan imports and extract package prefixes that have mappings
        foreach (var importName in importNames)
        {
            var lastDot = importName.LastIndexOf('.');
            if (lastDot <= 0) continue;

            // Check each prefix of the import path (e.g. "java.util" from "java.util.List")
            var parts = importName.Split('.');
            for (int i = parts.Length - 1; i >= 1; i--)
            {
                var prefix = string.Join('.', parts, 0, i);
                if (index.TryGetCSharpPackages(prefix, out _))
                {
                    _activeJavaPackages.Add(prefix);
                }
            }
        }

        // Build simple-name → FQN lookup from explicit (non-wildcard) imports
        foreach (var importName in importNames)
        {
            if (importName.EndsWith(".*")) continue;

            var lastDot = importName.LastIndexOf('.');
            if (lastDot <= 0) continue;

            var simpleName = importName[(lastDot + 1)..];
            _classNameToFQN.TryAdd(simpleName, importName);
        }

        // Also populate from auto-imported packages: scan the index for classes in those packages
        if (autoImportedPackages is not null)
        {
            foreach (var cls in index.Classes)
            {
                var pkg = PackageOf(cls.JavaClass);
                if (autoImportedPackages.Contains(pkg))
                {
                    _classNameToFQN.TryAdd(cls.SimpleName, cls.JavaClass);
                }
            }
        }
    }

    /// <summary>
    /// Returns true if the given Java class name belongs to a package that has at least one
    /// mapping in the index. Always returns true when no mapping index is configured.
    /// </summary>
    public bool IsPackageActive(string javaClassName)
    {
        if (_index is null) return true;

        var lastDot = javaClassName.LastIndexOf('.');
        if (lastDot <= 0)
        {
            // Simple name — check if any mapped package is active
            return _activeJavaPackages.Count > 0;
        }

        var pkg = javaClassName[..lastDot];
        return _activeJavaPackages.Contains(pkg);
    }

    /// <summary>
    /// Returns true if any mapped package was found in this file's imports.
    /// When false, the entire mapping resolution can be skipped for this file.
    /// </summary>
    public bool HasAnyMappedPackage => _activeJavaPackages.Count > 0;

    /// <summary>
    /// The set of Java packages that are active (imported or auto-imported) in this file.
    /// Used by heuristic type inference to narrow candidate classes.
    /// </summary>
    public IReadOnlySet<string> ActiveJavaPackages => _activeJavaPackages;

    /// <summary>
    /// Resolves a simple Java class name (e.g. "A") to its fully qualified name (e.g. "com.example.A")
    /// using the file's import declarations and auto-imported packages. Returns false when the name
    /// cannot be resolved through imports.
    /// </summary>
    public bool TryResolveClassName(string simpleName, out string? fullyQualifiedName)
    {
        return _classNameToFQN.TryGetValue(simpleName, out fullyQualifiedName);
    }

    private static string PackageOf(string javaClass)
    {
        var lastDot = javaClass.LastIndexOf('.');
        return lastDot < 0 ? "" : javaClass[..lastDot];
    }
}
