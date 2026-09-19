namespace JavaToCSharp;

/// <summary>
/// Controls how Java <c>@Override</c>-annotated methods are emitted in C#.
/// </summary>
public enum OverrideMode
{
    /// <summary>
    /// <c>@Override</c> is emitted as C# <c>override</c>. Faithful to intent, but produces
    /// compile errors (CS0115, CS0507, CS0508) when the cross-file base member is not a
    /// matching virtual member — something a per-file converter cannot verify.
    /// </summary>
    Faithful,

    /// <summary>
    /// <c>@Override</c> is emitted as a plain (re)declaration with <c>public virtual</c>
    /// modifiers instead of <c>override</c>. This always compiles: where the base member is
    /// virtual the C# compiler reports a hiding warning (CS0108/CS0114) rather than an error,
    /// and where the Java parent was an interface member it becomes a normal implicit
    /// implementation. Virtual dispatch through base-typed references degrades to hiding for
    /// the affected members.
    /// </summary>
    AlwaysHide,

    /// <summary>
    /// <c>@Override</c> emits C# <c>override</c> only when the base type is verifiable from the
    /// file being converted: a base type declared in the same file (nested/hoisted), an
    /// enum-constant body (base is the enclosing enum), or a <c>java.lang.Object</c> method
    /// (always virtual in C#). Cross-file bases cannot be verified per-file — their signature
    /// may have drifted through context-dependent type conversion — so those methods become
    /// plain <c>public virtual</c> re-declarations (AlwaysHide behavior). Local inference is
    /// encouraged by the host architecture; only unverifiable cross-file claims are dropped.
    /// </summary>
    Safe,
}
