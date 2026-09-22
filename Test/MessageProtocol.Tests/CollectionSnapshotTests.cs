using MessageProtocol.Serialize;
using MessageProtocol.Tests.Fixtures;
using Xunit;

namespace MessageProtocol.Tests;

/// <summary>
/// KI-26 regression: collection member writes evaluate the member expression **exactly once** and snapshot it locally.
/// The previously generated code re-evaluated the member separately for the length prefix (`Count`), the loop
/// condition (`Count`), and element access (`[i]`), so the getter ran 2N+2 times per N elements. With a computed
/// property (`public IList&lt;int&gt; Codes =&gt; Build();`) the length and the elements could come from different
/// instances, letting the frame contradict itself; even plain auto-properties wasted a getter call plus an
/// interface `Count` call per element (notably on Unity/netstandard2.1, which lacks `CollectionsMarshal`).
/// </summary>
public class CollectionSnapshotTests
{
    [Fact]
    public void collection_member_is_evaluated_exactly_once_during_serialization()
    {
        var message = new SnapshotCollectionMessage();

        byte[] bytes = MessageSerializer.Serialize(message);

        // Before the fix: 3 Codes → 2*3+2 = 8 calls, 2 Tags → 6 calls. After the fix: 1 call each.
        Assert.Equal(1, message.CodesGetterCalls);
        Assert.Equal(1, message.TagsGetterCalls);

        var roundTrip = MessageSerializer.Deserialize<SnapshotCollectionMessage>(bytes);
        Assert.Equal(new[] { 1, 2, 3 }, roundTrip.Codes);
        Assert.Equal(new[] { "a", "b" }, roundTrip.Tags);
    }

    [Fact]
    public void empty_collections_and_null_are_still_written_per_contract_after_snapshot()
    {
        var empty = new SnapshotCollectionMessage { Codes = new List<int>(), Tags = Array.Empty<string>() };

        var emptyRoundTrip = MessageSerializer.Deserialize<SnapshotCollectionMessage>(MessageSerializer.Serialize(empty));
        Assert.Empty(emptyRoundTrip.Codes);
        Assert.Empty(emptyRoundTrip.Tags);

        var nulls = new SnapshotCollectionMessage { Codes = null!, Tags = null! };

        var nullRoundTrip = MessageSerializer.Deserialize<SnapshotCollectionMessage>(MessageSerializer.Serialize(nulls));
        Assert.Null(nullRoundTrip.Codes);
        Assert.Null(nullRoundTrip.Tags);
    }
}
