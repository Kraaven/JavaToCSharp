using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JavaToCSharp;

public class ConversionContext(
    JavaConversionOptions options,
    Action<ConversionState>? stateChanged = null,
    Action<ConversionWarningEventArgs>? warning = null)
{
    private int _tempCounter;

    private readonly Action<ConversionState>? _stateChanged = stateChanged;
    private readonly Action<ConversionWarningEventArgs>? _warning = warning;

    public void NotifyStateChanged(ConversionState state) => _stateChanged?.Invoke(state);

    public void Warning(string message, int javaLineNumber) =>
        _warning?.Invoke(new ConversionWarningEventArgs(message, javaLineNumber));

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

    public bool PreserveJavaMethodNames => IsConvertingRecord || IsConvertingEnum;

    /// <summary>
    /// Maps record component names to private backing field names (e.g. name → name_attribute).
    /// </summary>
    public IDictionary<string, string> RecordComponentBackingFields { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Maps simple enum constant names to qualified names (e.g. MONDAY → Day.MONDAY)
    /// for switch labels and similar unqualified references.
    /// </summary>
    public IDictionary<string, string> EnumConstantsBySimpleName { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
