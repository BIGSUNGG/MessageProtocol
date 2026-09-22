using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Benchmarks;
using MessageProtocol;
using MessageProtocol.Serialize;

BenchmarkRunner.Run<SerializationBenchmarks>();

namespace Benchmarks
{
    /// <summary>
    /// Uses the in-process emit toolchain because the repo contains two projects with the same name (including
    /// Legacy), which breaks the default csproj toolchain.
    /// </summary>
    public class InProcessConfig : ManualConfig
    {
        public InProcessConfig()
        {
            AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance));
        }
    }

    [Message(MessageKind.Standalone, 1)]
    public partial class BenchMessage
    {
        public int Id { get; set; }
        public long Timestamp { get; set; }
        public float Value { get; set; }
        public string? Text { get; set; }
        public List<int>? Numbers { get; set; }
    }

    /// <summary>
    /// A string-heavy message (realistic game-server profile: names, chat, ASCII keys). Measures the effect of
    /// changing WriteString capacity estimation from a 3n+3 conservative reservation to exact ASCII sizing.
    /// Four 100-char strings — conservative: 4·(4+303) = 1,228 B, exact ASCII: 416 B.
    /// </summary>
    [Message(MessageKind.Standalone, 2)]
    public partial class StringHeavyMessage
    {
        public string? Name { get; set; }
        public string? Channel { get; set; }
        public string? Text { get; set; }
        public string? Tag { get; set; }
    }

    /// <summary>
    /// Reference-tracking graph scenario — two branches share the same subgraph, forcing back-references on both
    /// write and read (depth-5 chain, shared subtree). Benchmarks the two paths that were the baseline gap,
    /// together with the pooled path.
    /// </summary>
    [Message(MessageKind.Standalone, 3)]
    public partial class GraphNode
    {
        public string? Label { get; set; }
        public GraphNode? Left { get; set; }
        public GraphNode? Right { get; set; }
    }

    /// <summary>
    /// Large-collection scenario (realistic scale for inventory/entity batch transfers) — `List&lt;int&gt;` with 100k
    /// elements stresses the CollectionsMarshal bulk-copy path; `string[]` with 1k elements stresses the
    /// per-element write path.
    /// </summary>
    [Message(MessageKind.Standalone, 4)]
    public partial class LargeCollections
    {
        public List<int>? Numbers { get; set; }
        public string[]? Names { get; set; }
    }

    [MemoryDiagnoser]
    [Config(typeof(InProcessConfig))]
    public class SerializationBenchmarks
    {
        readonly BenchMessage _message = new()
        {
            Id = 42,
            Timestamp = 1717000000L,
            Value = 3.14f,
            Text = "benchmark payload",
            Numbers = new List<int> { 1, 2, 3, 4, 5, 6, 7, 8 },
        };

        byte[] _bytes = null!;
        byte[] _stringBytes = null!;
        byte[] _graphBytes = null!;
        byte[] _largeBytes = null!;

        readonly StringHeavyMessage _stringMessage = new()
        {
            Name = new string('a', 100),
            Channel = new string('b', 100),
            Text = new string('c', 100),
            Tag = new string('d', 100),
        };

        // Both branches share the end of a depth-5 chain — forces back-reference tags on write and GetObject dereference on read.
        static GraphNode BuildSharedGraph()
        {
            GraphNode tail = new() { Label = "leaf" };
            for (int i = 0; i < 4; i++)
            {
                tail = new GraphNode { Label = "n" + i, Left = tail, Right = null };
            }
            return new GraphNode { Label = "root", Left = tail, Right = tail };   // same subgraph shared
        }

        readonly GraphNode _graphRoot = BuildSharedGraph();

        readonly LargeCollections _largeCollections = new()
        {
            Numbers = Enumerable.Range(0, 100_000).Select(i => i * 7).ToList(),
            Names = Enumerable.Range(0, 1_000).Select(i => "name" + i).ToArray(),
        };

        [GlobalSetup]
        public void Setup()
        {
            _bytes = MessageSerializer.Serialize(_message);
            _stringBytes = MessageSerializer.Serialize(_stringMessage);
            _graphBytes = MessageSerializer.Serialize(_graphRoot);
            _largeBytes = MessageSerializer.Serialize(_largeCollections);
        }

        [Benchmark]
        public byte[] SerializeBytes() => MessageSerializer.Serialize(_message);

        [Benchmark]
        public byte[] SerializeStringHeavy() => MessageSerializer.Serialize(_stringMessage);

        [Benchmark]
        public object DeserializeStringHeavy() => MessageSerializer.Deserialize(_stringBytes);

        [Benchmark]
        public int SerializePooledFlat()   // allocation contrast with the byte[] path (SerializeBytes) — the return includes releasing ownership
        {
            using var pooled = MessageSerializer.SerializePooled(_message);
            return pooled.Length;
        }

        [Benchmark]
        public byte[] SerializeSharedGraph() => MessageSerializer.Serialize(_graphRoot);

        [Benchmark]
        public GraphNode DeserializeSharedGraph() => MessageSerializer.Deserialize<GraphNode>(_graphBytes);

        [Benchmark]
        public byte[] SerializeLargeCollections() => MessageSerializer.Serialize(_largeCollections);

        [Benchmark]
        public LargeCollections DeserializeLargeCollections() => MessageSerializer.Deserialize<LargeCollections>(_largeBytes);

        [Benchmark]
        public BenchMessage DeserializeTyped() => MessageSerializer.Deserialize<BenchMessage>(_bytes);

        [Benchmark]
        public object DeserializeDispatch() => MessageSerializer.Deserialize(_bytes);
    }
}
