using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp;

public class ConversionContext(
    JavaConversionOptions options,
    Action<ConversionState>? stateChanged = null,
    Action<ConversionWarningEventArgs>? warning = null,
    IConversionLogger? logger = null)
{
    private int _tempCounter;

    private readonly Action<ConversionState>? _stateChanged = stateChanged;
    private readonly Action<ConversionWarningEventArgs>? _warning = warning;
    private readonly IConversionLogger? _logger = logger;

    public void NotifyStateChanged(ConversionState state)
    {
        _stateChanged?.Invoke(state);
        _logger?.OnStateChanged(state);
    }

    public void Warning(string message, int javaLineNumber)
    {
        _warning?.Invoke(new ConversionWarningEventArgs(message, javaLineNumber));
        _logger?.OnWarning(message, javaLineNumber);
    }

    public Queue<ClassDeclarationSyntax> PendingAnonymousTypes { get; } = new();

    public ISet<string> UsedAnonymousTypeNames { get; } = new HashSet<string>();

    /// <summary>
    /// Statements that must be emitted immediately before the statement currently being converted
    /// (e.g. temporaries produced while converting a switch expression used as a value).
    /// </summary>
    public List<StatementSyntax> PendingStatements { get; } = new();

    /// <summary>
    /// When converting a Java switch expression, how each yielded/result value is consumed
    /// (assignment into a temp, <c>return</c>, etc.). Nested <c>yield</c> statements use this
    /// even when they appear inside <c>if</c>/<c>while</c>/classic <c>switch</c>.
    /// </summary>
    public Func<ExpressionSyntax, IEnumerable<StatementSyntax>>? SwitchExpressionResultSink { get; set; }

    public string NextTempName(string prefix) => $"{prefix}{++_tempCounter}";

    private int _placeholderCounter;

    /// <summary>
    /// Stand-in types synthesized by the constraint satisfier, tagged with the type they must
    /// nest in. C# name lookup walks outward through enclosing class scopes, so a placeholder
    /// nested in ANY ancestor of the reference site resolves — but a class's own base list can
    /// NOT see the class's own nested types, so a placeholder synthesized for a base-list
    /// wildcard must nest one level up. Drained owner-filtered alongside
    /// <see cref="PendingAnonymousTypes"/>.
    /// </summary>
    public Queue<(string? Owner, ClassDeclarationSyntax Declaration)> PendingPlaceholderTypes { get; } = new();

    /// <summary>
    /// The type declaration currently being converted (innermost). Used as the owner key for
    /// placeholder deduplication: the same unsatisfiable constraint referenced twice within one
    /// type yields one nested stand-in, while sibling types each get their own copy (a bare
    /// name registered in one nested type is not visible to its siblings).
    /// </summary>
    public string? CurrentPlaceholderOwner { get; set; }

    /// <summary>
    /// Placeholder names carry the file's root type name: multiple converted files in the same
    /// namespace each drain unowned (namespace-level) stand-ins, and a bare counter resets per
    /// file, so identical names would collide (CS0101). References and the declaration both use
    /// the returned name, so the suffix is transparent.
    /// </summary>
    public string NextPlaceholderName()
    {
        var suffix = RootTypeName is { Length: > 0 } root
            ? "_" + root.Replace('.', '_')
            : "";

        return $"__Satisfy{++_placeholderCounter}{suffix}";
    }

    /// <summary>
    /// Registers a synthesized constraint-satisfying nested class, deduplicating by the
    /// (owner, closed base-list) signature. Returns the type's name.
    /// </summary>
    public string RegisterPlaceholderType(string closedBaseList, Func<string, ClassDeclarationSyntax> factory)
    {
        var key = (CurrentPlaceholderOwner ?? "") + "|" + closedBaseList;

        if (PlaceholderSignatures.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var name = NextPlaceholderName();
        PlaceholderSignatures[key] = name;
        PendingPlaceholderTypes.Enqueue((CurrentPlaceholderOwner, factory(name)));
        return name;
    }

    public IDictionary<string, string> PlaceholderSignatures { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Drains pending placeholder classes owned by <paramref name="ownerName"/> (the type whose
    /// members are currently being collected). Placeholders owned by other types stay queued —
    /// they drain into their own owner when its visitor loop runs, or one level up when they
    /// were synthesized for an owner's base list.
    /// </summary>
    public void DrainPlaceholderTypes(string? ownerName, Action<ClassDeclarationSyntax> addMember)
    {
        var passes = PendingPlaceholderTypes.Count;

        for (var i = 0; i < passes; i++)
        {
            var (owner, declaration) = PendingPlaceholderTypes.Dequeue();

            if (string.Equals(owner, ownerName, StringComparison.Ordinal))
            {
                addMember(declaration);
            }
            else
            {
                PendingPlaceholderTypes.Enqueue((owner, declaration));
            }
        }
    }

    public List<StatementSyntax> DrainPendingStatements()
    {
        if (PendingStatements.Count == 0)
        {
            return [];
        }

        var drained = PendingStatements.ToList();
        PendingStatements.Clear();
        return drained;
    }

    public JavaConversionOptions Options { get; } = options;

    /// <summary>
    /// Holds the set of Java packages that are actively imported in the current file.
    /// When set, mapping resolution is gated on this context: only types from active packages
    /// are resolved through the <see cref="TypeMapping.TypeMappingIndex"/>.
    /// </summary>
    internal TypeMapping.FileMappingContext? FileMappingContext { get; set; }

    public string? RootTypeName { get; set; }

    public string? LastTypeName { get; set; }

    /// <summary>
    /// Type parameter names of the type currently being converted (e.g. record/class).
    /// Used to replace Java wildcards like <c>Person&lt;?&gt;</c> with <c>Person&lt;T&gt;</c>.
    /// </summary>
    public IList<string> TypeParameterNames { get; } = new List<string>();

    /// <summary>
    /// The Java AST nodes of the currently enclosing type's type parameters (same order as
    /// <see cref="TypeParameterNames"/>). Used when hoisting anonymous classes so they can be
    /// made generic over the enclosing parameters they reference — Java anonymous bodies see
    /// the enclosing type's parameters, while C# nested classes do not.
    /// </summary>
    public IList<com.github.javaparser.ast.type.TypeParameter> TypeParameterNodes { get; } =
        new List<com.github.javaparser.ast.type.TypeParameter>();

    /// <summary>
    /// Names of Java record components for the record currently being converted.
    /// Used to avoid emitting <c>override</c> on component accessor methods.
    /// </summary>
    public ISet<string> RecordComponentNames { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// When true, method names are left as in Java (not capitalized) so record/enum APIs
    /// stay callable/accessible with the original names across individually converted files.
    /// </summary>
    public bool IsConvertingRecord { get; set; }

    /// <summary>
    /// When true, method names are left as in Java (same as records) for class-based enums.
    /// </summary>
    public bool IsConvertingEnum { get; set; }

    /// <summary>
    /// When true, we are converting methods inside an enum constant class body;
    /// <c>@Override</c> should map to the C# <c>override</c> keyword.
    /// </summary>
    public bool IsConvertingEnumConstantBody { get; set; }

    public bool PreserveJavaMethodNames => Options.PreserveJavaMethodNames || IsConvertingRecord || IsConvertingEnum;

    /// <summary>
    /// When true, we are converting members of an interface reshaped into an abstract class;
    /// bodyless interface methods become virtual members with REVIEW throw-stub bodies.
    /// </summary>
    public bool IsConvertingReshapedInterface { get; set; }

    /// <summary>
    /// Member names (lowercased) of reshaped interfaces implemented by the type currently being
    /// converted (cumulative across enclosing types — nested types inherit their bases). A
    /// method whose name is in this set flips from interface-suppression to `override`, so
    /// virtual dispatch reaches the implementer instead of the throw-stub base member.
    /// </summary>
    public ISet<string> CurrentReshapeOverrideMembers { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Member names (lowercased) of NON-reshaped (interface-form) bases implemented by the type
    /// currently being converted (cumulative across enclosing types). C# forbids `override` on
    /// interface implementations, so a method whose name is in this set must not claim `override`
    /// — implicit implementation satisfies the contract. Scoped to the type's own bases: a
    /// tree-wide name set wrongly suppressed class-method overrides that merely shared a name
    /// with some unrelated interface member (run/codec/getType/fix hiding abstract base members).
    /// </summary>
    public ISet<string> CurrentImplementedInterfaceMembers { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// C# namespaces collected while resolving member access through the mapping set.
    /// Merged into the output usings, sorted alphabetically and deduplicated.
    /// </summary>
    public ISet<string> MappingUsings { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Maps record component names to private backing field names (e.g. name → name_attribute).
    /// </summary>
    public IDictionary<string, string> RecordComponentBackingFields { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Maps field names to their deconflicted replacements when a Java type declares a field and
    /// a method with the same name (legal in Java, CS0102 in C#). The field is renamed by
    /// appending "Field"; its declaration and references (bare, or <c>this.</c>-qualified) are
    /// rewritten through this map, while the method keeps its name so cross-file callers are
    /// unaffected. Populated per type by a member pre-scan.
    /// </summary>
    public IDictionary<string, string> FieldNameRenames { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Maps simple enum constant names to qualified names (e.g. MONDAY → Day.MONday)
    /// for switch labels and similar unqualified references.
    /// </summary>
    public IDictionary<string, string> EnumConstantsBySimpleName { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Tracks Java variable types through nested lexical scopes.
    /// </summary>
    public VariableScopeTracker VariableScopes { get; } = new();

    /// <summary>
    /// Simple names of types declared in the file currently being converted (nested types,
    /// records, enums and hoisted local classes included). Used to keep program-defined type
    /// names intact when they collide with names that have built-in conversions (e.g. a nested
    /// record named <c>Double</c> must not become the C# <c>double</c> keyword).
    /// </summary>
    public ISet<string> DeclaredTypeNames { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// A Java local class or record that was hoisted out of a method body into a private nested
    /// type of the enclosing class, together with the variables it captures.
    /// </summary>
    public sealed record HoistedLocalClassInfo(
        string Name,
        bool NeedsParent,
        IReadOnlyList<(string Name, string JavaType)> CapturedLocals,
        IReadOnlyList<string> TypeParams);

    private readonly Stack<List<HoistedLocalClassInfo>> _hoistedLocalClassScopes = new();

    /// <summary>
    /// Begins a method-level scope for hoisted local class registrations. Local classes are
    /// method-scoped in Java; registrations must not leak into sibling methods.
    /// </summary>
    public void PushHoistedLocalClassScope() => _hoistedLocalClassScopes.Push([]);

    public void PopHoistedLocalClassScope()
    {
        if (_hoistedLocalClassScopes.Count > 0)
        {
            _hoistedLocalClassScopes.Pop();
        }
    }

    public void RegisterHoistedLocalClass(HoistedLocalClassInfo info)
    {
        if (_hoistedLocalClassScopes.Count > 0)
        {
            _hoistedLocalClassScopes.Peek().Add(info);
        }
    }

    /// <summary>
    /// Finds a hoisted local class by simple name, searching method scopes innermost-first.
    /// </summary>
    public HoistedLocalClassInfo? FindHoistedLocalClass(string name)
    {
        foreach (var scope in _hoistedLocalClassScopes)
        {
            var found = scope.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Enters a new lexical scope for variable-type tracking. Call <see cref="PopVariableScope"/>
    /// when the scope ends so shadowed names are restored.
    /// </summary>
    public void PushVariableScope() => VariableScopes.PushScope();

    public void PopVariableScope() => VariableScopes.PopScope();

    /// <summary>
    /// Records the declared Java type of a variable in the current scope.
    /// </summary>
    public void DeclareVariable(string name, string javaType) =>
        VariableScopes.Declare(name, javaType);

    /// <summary>
    /// Looks up the declared Java type of a variable, innermost scope first.
    /// </summary>
    public bool TryGetVariableType(string name, out string javaType) =>
        VariableScopes.TryGetVariableType(name, out javaType);
}
