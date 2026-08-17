namespace JavaToCSharp;

/// <summary>
/// Tracks Java variable types through nested lexical scopes. Each scope maps
/// variable names to their declared Java type strings. Supports push/pop for
/// nested blocks and lookup from innermost scope outward.
/// </summary>
public sealed class VariableScopeTracker
{
    private readonly List<Dictionary<string, string>> _scopes = [];

    /// <summary>
    /// Enters a new lexical scope. Call <see cref="PopScope"/> when the scope ends
    /// so shadowed names are restored.
    /// </summary>
    public void PushScope() =>
        _scopes.Add(new Dictionary<string, string>(StringComparer.Ordinal));

    public void PopScope()
    {
        if (_scopes.Count > 0)
        {
            _scopes.RemoveAt(_scopes.Count - 1);
        }
    }

    /// <summary>
    /// Records the declared Java type of a variable in the current scope. The stored value is the
    /// raw declared type string (e.g. <c>Map&lt;String, Item&gt;</c> or <c>BufferedReader</c>).
    /// </summary>
    public void Declare(string name, string javaType)
    {
        if (_scopes.Count == 0)
        {
            PushScope();
        }

        _scopes[^1][name] = javaType;
    }

    /// <summary>
    /// Looks up the declared Java type of a variable, innermost scope first.
    /// </summary>
    public bool TryGetVariableType(string name, out string javaType)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGetValue(name, out javaType!))
            {
                return true;
            }
        }

        javaType = "";
        return false;
    }
}
