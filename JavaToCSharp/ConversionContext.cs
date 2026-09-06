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
