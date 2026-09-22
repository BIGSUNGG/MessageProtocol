using MessageProtocol;

namespace MessageProtocol.NetStandardFixtures;

// This assembly compiles against netstandard2.1 (the Unity-compatible profile). It has no CollectionsMarshal, so
// the generator emits the fallback variant (indexer loop) instead of the fast List<T> path (AsSpan/SetCount).
// This is the only place where the generated code for KI-17 (fallback bulk guard), KI-26 (member snapshot), and
// KI-14/25 (nesting-depth guard) actually executes, so these fixtures pin the wire behavior consumers see there.

/// <summary>The 5 fallback collection path shapes: List (fixed size), List (variable size), IList, array (variable), array (fixed size).</summary>
[Message(MessageKind.Standalone, 900)]
public partial class FallbackCollections
{
    public List<int>? Bulk { get; set; }
    public List<string>? Texts { get; set; }
    public IList<byte>? Codes { get; set; }
    public string[]? Tags { get; set; }
    public double[]? Samples { get; set; }
}

/// <summary>A NonId payload with self-reference and nested collections — verifies the nesting-depth guard also runs on the fallback path.</summary>
[Message(MessageKind.NonId)]
public partial class FallbackNode
{
    public string? Label { get; set; }
    public FallbackNode? Next { get; set; }
    public List<FallbackNode>? Children { get; set; }
}

/// <summary>
/// A [Message] root candidate for cross-assembly inheritance checks — this assembly (netstandard2.1) has no
/// derivatives, so it resolves as Standalone here. When MessageProtocol.Tests declares a [Message]-derived element,
/// inheritance is proven to be recognized across the referenced assembly.
/// </summary>
[Message]
public partial class CrossProjectRoot
{
    public int BaseValue { get; set; }
    public string? BaseText { get; set; }
}
