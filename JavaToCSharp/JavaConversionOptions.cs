using System.Text.RegularExpressions;
using JavaToCSharp.TypeMapping;

namespace JavaToCSharp;

/// <summary>
/// Read-only configuration for a conversion. Instances are safe to share across
/// concurrent <see cref="JavaToCSharpConverter.ConvertText"/> calls; conversion never mutates
/// this object. Configure it before converting and treat it as immutable afterwards.
/// </summary>
public class JavaConversionOptions
{
    private readonly List<Replacement> _packageReplacements = [];
    private readonly List<string> _usings = [];

    public IReadOnlyList<Replacement> PackageReplacements => _packageReplacements;

    public IReadOnlyList<string> Usings => _usings;

    public bool IncludeSubdirectories { get; set; } = true;

    public bool IncludeUsings { get; set; } = true;

    public bool IncludeNamespace { get; set; } = true;

    public bool UseDebugAssertForAsserts { get; set; }

    /// <summary>
    /// Unrecognized code is translated into comments
    /// </summary>
    public bool UseUnrecognizedCodeToComment { get; set; } = true;

    /// <summary>
    /// When true, converts <c>System.out.print/println</c> to <c>Console.Write/WriteLine</c>.
    /// This is now handled by the built-in Console mapping when <see cref="MappingIndex"/>
    /// includes <see cref="TypeMapping.BuiltInMappingSpec.Console"/>. This property is retained
    /// for backward compatibility and will be removed in a future version.
    /// </summary>
    [Obsolete("Use MappingIndex with BuiltInMappingSpec.Console instead. This property will be removed in a future version.")]
    public bool ConvertSystemOutToConsole { get; set; }

    public bool IncludeComments { get; set; } = true;

    public bool UseFileScopedNamespaces { get; set; }

    /// <summary>
    /// When true (default), method and member names keep their original Java casing and no
    /// capitalization is applied. This keeps individually converted files call-compatible with
    /// each other across files. Common Java object-method equivalents (<c>equals</c>,
    /// <c>hashCode</c>, <c>toString</c>, <c>getClass</c>, …) are always replaced with their C#
    /// names regardless of this flag. When false, method names are PascalCased and the same
    /// common Java method names are replaced with their C# equivalents. Java-to-C#
    /// property/indexer rewrites (such as <c>size()</c> to <c>Count</c> or <c>length</c> to
    /// <c>Length</c>) are no longer performed automatically; use a member mapping set
    /// (<see cref="MappingIndex"/>) for those.
    /// </summary>
    public bool PreserveJavaMethodNames { get; set; } = true;

    /// <summary>
    /// When true, one log line is emitted per converted file to each enabled sink
    /// (<see cref="LogToConsole"/>, <see cref="LogToCallback"/>, <see cref="LogToFile"/>).
    /// False by default.
    /// </summary>
    public bool EnableLogging { get; set; }

    /// <summary>When <see cref="EnableLogging"/> is true, write each log line to <see cref="Console.WriteLine"/>.</summary>
    public bool LogToConsole { get; set; }

    /// <summary>When <see cref="EnableLogging"/> is true, invoke <see cref="Log"/> for each log line.</summary>
    public bool LogToCallback { get; set; }

    /// <summary>
    /// Callback sink used when <see cref="EnableLogging"/> and <see cref="LogToCallback"/> are true.
    /// May be invoked from multiple threads concurrently during a directory conversion, so the
    /// handler must be thread-safe.
    /// </summary>
    public Action<string>? Log { get; set; }

    /// <summary>When <see cref="EnableLogging"/> is true, append each log line to <see cref="LogFilePath"/>.</summary>
    public bool LogToFile { get; set; }

    /// <summary>
    /// Path of the log file when <see cref="LogToFile"/> is true. Defaults to "conversion.log",
    /// resolved relative to the process working directory unless an absolute path is given.
    /// </summary>
    public string LogFilePath { get; set; } = "conversion.log";

    private readonly object _logLock = new();

    /// <summary>
    /// Emits a log line to every enabled sink. No-op unless <see cref="EnableLogging"/> is true.
    /// Thread-safe: file appends are serialized with an internal lock.
    /// </summary>
    /// <param name="message">The log line to write (without a trailing newline).</param>
    public void LogMessage(string message)
    {
        if (!EnableLogging)
        {
            return;
        }

        if (LogToConsole)
        {
            Console.WriteLine(message);
        }

        if (LogToCallback)
        {
            Log?.Invoke(message);
        }

        if (LogToFile)
        {
            lock (_logLock)
            {
                File.AppendAllText(LogFilePath, message + Environment.NewLine);
            }
        }
    }

    /// <summary>
    /// An optional, pre-loaded member-level mapping set. When set, member access and method
    /// call resolution runs against it first, and any matching entry overrides all default
    /// conversions (including <see cref="PreserveJavaMethodNames"/>). Unmatched calls fall
    /// through to the normal conversion logic. Instances are immutable and safe to share.
    /// </summary>
    public TypeMappingIndex? MappingIndex { get; set; }

    /// <summary>
    /// Maps the simple names of generic types (as emitted in C# code) to their type-parameter
    /// count. When a Java <em>raw</em> type (a generic type used without type arguments — illegal
    /// in C#) converts to one of these names, the missing type arguments are synthesized as
    /// <c>object</c>. Hosts typically populate this from a scan of their Java input tree.
    /// </summary>
    public IReadOnlyDictionary<string, int>? KnownGenericArity { get; set; }

    /// <summary>
    /// Controls how Java <c>@Override</c>-annotated methods are emitted. Java treats every
    /// non-<c>final</c>, non-<c>private</c>, non-<c>static</c> instance method as dynamically
    /// dispatched, and <c>@Override</c> is a compiler-checked annotation rather than a
    /// declaration keyword. C# <c>override</c> additionally requires the base member to be
    /// <c>virtual</c>/<c>abstract</c>/<c>override</c> with an exactly matching accessible
    /// signature — a guarantee a per-file converter cannot verify.
    /// </summary>
    public OverrideMode OverrideMode { get; set; } = OverrideMode.Faithful;

    /// <summary>
    /// Optional cross-file type-constraint index, keyed by the Java simple name of a generic
    /// type. Each value maps the declared type parameter's name (or its 0-based position when
    /// the name is unknown) to the resolved bound type as emitted in C#. Hosts converting a
    /// whole tree typically populate this from a scan of the input sources so unbounded
    /// wildcards (<c>Box&lt;?&gt;</c>) can resolve to the bound required by the generic type's
    /// own constraints (<c>class Box&lt;T extends Widget&gt;</c> → <c>Box&lt;Widget&gt;</c>)
    /// instead of <c>Box&lt;object&gt;</c>, which C# rejects when the constraint is a class
    /// or interface bound.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? GenericConstraintIndex { get; set; }

    /// <summary>
    /// Optional set of type simple names that exist somewhere in the host's input tree. Per-file
    /// conversion cannot know whether an unqualified scope name in a nested reference
    /// (<c>DebugRenderer.SimpleDebugRenderer</c>) refers to a type declared in another file or to
    /// a package segment; hosts scanning their tree populate this so nested-type qualifiers are
    /// retained instead of being dropped (which would emit bare <c>SimpleDebugRenderer</c> and
    /// fail to resolve). Dotted names may also be included.
    /// </summary>
    public IReadOnlySet<string>? KnownTypeNames { get; set; }

    /// <summary>
    /// Optional map of tree-declared type simple name (lowercased) → the C# namespace the type
    /// is declared in, computed by the host with the same package replacements and casing the
    /// declarations get. Consumed when a type reference resolves outside the mapping index
    /// (user types): the mapped namespace is registered as a using so references to types that
    /// were <em>injected</em> into a file by context-dependent conversion (wildcard resolution
    /// through a constraint bound from another file) still compile even though the file never
    /// imported the type. Conflicted simple names (same name declared in several packages) are
    /// omitted by the host; usings are deduplicated at emission.
    /// </summary>
    public IReadOnlyDictionary<string, string>? KnownTypeNamespaces { get; set; }

    /// <summary>
    /// Optional map of CONFLICTED tree-declared type simple names (lowercased) → ALL C#
    /// namespaces a type of that name is declared in. Unlike <see cref="KnownTypeNamespaces"/>,
    /// which omits conflicted names entirely, every candidate namespace is registered as a
    /// using. Without this, a reference to a conflicted name injected by context-dependent
    /// conversion (or referenced without an import) emits CS0246; injecting all candidates makes
    /// the reference resolve. Ambiguity (CS0104) only arises when a file actually references the
    /// name with two candidates imported — strictly better than the unconditional CS0246.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? KnownTypeNamespaceCandidates { get; set; }

    /// <summary>
    /// Optional set of member names (lowercased) that any interface in the host's tree declares
    /// or inherits. Java's <c>@Override</c> marks both superclass overrides and interface
    /// implementations, but C# forbids <c>override</c> on an interface implementation — the
    /// method must be a plain (virtual) member. A per-file converter sees the <c>implements</c>
    /// clause but not the interface's own (possibly inherited) members, so hosts scanning the
    /// tree supply this set; an <c>@Override</c> method whose name is in the set is emitted
    /// without the <c>override</c> keyword (implicit interface implementation — which the
    /// plain virtual form satisfies).
    /// </summary>
    public IReadOnlySet<string>? InterfaceMemberNames { get; set; }

    /// <summary>
    /// Optional map of interface simple name (lowercased) → the SIGNATURE PARTS of its abstract
    /// members, format "typeParams|returnType|name|params" (types in Java spelling, closed over
    /// extends by the host). Consumed by the constraint stand-in generator: a C# stand-in class
    /// deriving a tree INTERFACE bound must re-declare every interface member abstract — an
    /// empty abstract class is CS0535. Reshaped interfaces (now abstract classes with virtual
    /// stubs) need no re-declaration and are excluded by the consumer.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? InterfaceMemberSignatures { get; set; }

    /// <summary>
    /// Optional map of interface simple name (lowercased) → ALL interfaces it transitively
    /// extends (interface-form, mapped excluded). A class implementing J must satisfy every
    /// base interface of J; combined with <see cref="InterfaceMemberSignatures"/> and
    /// <see cref="InterfaceDefaultMemberNames"/> the converter emits explicit interface
    /// implementations for base members neither declared by the class nor defaulted by the
    /// base interface itself (C#: a derived interface's default re-declaration does NOT
    /// satisfy the base's contract for implementing classes — verified CS0535).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? InterfaceExtendsClosures { get; set; }

    /// <summary>
    /// Optional map of interface simple name (lowercased) → names of the interface's OWN
    /// default (body-carrying) members. A base interface's own default satisfies its member
    /// contract for implementing classes; only members WITHOUT such a default need explicit
    /// implementations in the class.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>>? InterfaceDefaultMemberNames { get; set; }

    /// <summary>
    /// Optional map "interfaceName|memberName" (both lowercased) → the ORIGINAL-case simple
    /// name of the interface that DECLARES the member. Signature lists are closed over extends,
    /// but a C# explicit interface implementation must name the declaring interface — an
    /// inherited member implemented against the derived interface's name is CS0539.
    /// </summary>
    public IReadOnlyDictionary<string, string>? InterfaceMemberDeclarationMap { get; set; }

    /// <summary>
    /// Optional set of interface simple names declared anywhere in the host's tree
    /// (lowercased). Used by the constraint satisfier: an interface-typed constraint
    /// (<c>T : SomeInterface</c>) cannot be satisfied by the interface itself (C# interfaces
    /// cannot implement interfaces), so the converter synthesizes a stand-in class implementing
    /// it instead of using the bound's own name. Class bounds satisfy themselves.
    /// </summary>
    public IReadOnlySet<string>? KnownInterfaceNames { get; set; }

    /// <summary>
    /// Multi-method interfaces reshaped into C# abstract classes (virtual throw-stub members).
    /// Implementers always compile (CS0534/CS0535 die at the source); unimplemented members
    /// carry REVIEW-marked NotSupportedException tripwires. SAM interfaces are not reshaped —
    /// they stay interfaces until the delegate reshape decides their fate. Names are
    /// lowercased interface simple names.
    /// </summary>
    public IReadOnlySet<string>? ReshapeableInterfaceNames { get; set; }

    /// <summary>
    /// Interface simple name (lowercased) → the interface's own and inherited member names
    /// (lowercased). Implementers of reshaped interfaces flip `override` on methods whose name
    /// is in the reshaped base's member set, so virtual dispatch reaches the implementer
    /// instead of the throw-stub base member.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>>? InterfaceMembersByName { get; set; }

    /// <summary>
    /// Java packages that are automatically imported in every Java file (without an explicit
    /// import statement). Used by the namespace-aware mapping pre-scan to include these
    /// packages when determining which types should be resolved through the mapping index.
    /// Defaults to <c>["java.lang"]</c>.
    /// </summary>
    public IReadOnlyList<string> AutoImportedJavaPackages { get; set; } = ["java.lang"];

    public JavaConversionOptions AddPackageReplacement(string pattern, string replacement, RegexOptions options = RegexOptions.None)
    {
        _packageReplacements.Add(new Replacement(pattern, replacement, options));

        return this;
    }

    public JavaConversionOptions ClearUsings()
    {
        _usings.Clear();

        return this;
    }

    public JavaConversionOptions AddUsing(string ns)
    {
        _usings.Add(ns);

        return this;
    }

    public JavaConversionOptions SetUsings(IEnumerable<string> usings)
    {
        _usings.Clear();

        _usings.AddRange(usings);

        return this;
    }
}
