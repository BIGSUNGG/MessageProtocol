extern alias generator;

using generator::MessageProtocol.CodeGenerator;
using generator::MessageProtocol.CodeGenerator.Generate;
using generator::MessageProtocol.CodeGenerator.Metadata;
using generator::MessageProtocol.CodeGenerator.Reference;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Xunit;

namespace MessageProtocol.Tests;

/// <summary>Verifies generator diagnostics (MSGPROT001–006) and successful generation via GeneratorDriver.</summary>
public class GeneratorDiagnosticTests
{
    static (ImmutableArray<Diagnostic> Diagnostics, string GeneratedText) RunGenerator(string source)
    {
        var (diagnostics, generated, _) = RunGeneratorWithCompilation(source);
        return (diagnostics, generated);
    }

    static (ImmutableArray<Diagnostic> Diagnostics, string GeneratedText, ImmutableArray<Diagnostic> CompileErrors) RunGeneratorWithCompilation(string source)
    {
        var compilation = CreateTpaCompilation(source);

        var driver = CSharpGeneratorDriver.Create(new MessageCodeGenerator().AsSourceGenerator());
        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics);
        var runResult = driver.GetRunResult();

        var generated = string.Concat(
            runResult.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SourceText.ToString()));
        var compileErrors = updated.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
        return (diagnostics, generated, compileErrors);
    }

    /// <summary>Builds a source compilation from the test runtime TPA references (shared helper for generator-driven tests).</summary>
    static CSharpCompilation CreateTpaCompilation(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(Serialize.MessageSerializer).Assembly.Location));

        return CSharpCompilation.Create(
            "GeneratorTest",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    const string Header = """
        using MessageProtocol;
        using MessageProtocol.Serialize;
        namespace TestNs
        {
        """;

    const string Footer = """
        }
        """;

    [Fact]
    public void MSGPROT001_message_type_must_be_partial()
    {
        var (diagnostics, _) = RunGenerator(Header + """
            [Message(MessageKind.Standalone, 1)]
            public class NotPartial { public int X { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT001");
    }

    [Fact]
    public void MSGPROT002_containing_type_must_be_partial()
    {
        var (diagnostics, _) = RunGenerator(Header + """
            public class Outer
            {
                [Message(MessageKind.Standalone, 1)]
                public partial class Inner { public int X { get; set; } }
            }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT002");
    }

    [Fact]
    public void MSGPROT003_element_message_has_no_root()
    {
        var (diagnostics, _) = RunGenerator(Header + """
            [Message(MessageKind.Child, 1)]
            public partial class OrphanElement { public int X { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT003");
    }

    [Fact]
    public void MSGPROT004_root_parented_by_another_root()
    {
        var (diagnostics, _) = RunGenerator(Header + """
            [Message(MessageKind.Parent, 1)]
            public partial class RootA { }

            [Message(MessageKind.Parent, 2)]
            public partial class RootB : RootA { }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT004");
    }

    [Fact]
    public void MSGPROT005_id_out_of_range()
    {
        var (diagnostics, _) = RunGenerator(Header + """
            [Message(MessageKind.Standalone, 16777216)]
            public partial class TooBig { public int X { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT005");
    }

    [Fact]
    public void MSGPROT006_unsupported_member_type()
    {
        var (diagnostics, _) = RunGenerator(Header + """
            [Message(MessageKind.Standalone, 1)]
            public partial class BadMember
            {
                public System.Collections.Generic.Dictionary<string, int>? Map { get; set; }
            }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT006");
    }

    [Fact]
    public void MSGPROT018_NonId_rejects_id_argument()
    {
        // The NonId header is 1 byte — it has no id slot.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.NonId, id: 7)]
            public partial class NonIdWithId { public int X { get; set; } }
            """ + Footer);

        var diagnostic = Assert.Single(diagnostics, d => d.Id == "MSGPROT018");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("cannot take id or category", diagnostic.GetMessage());
        Assert.DoesNotContain("partial class NonIdWithId", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT018_NonId_rejects_category_argument()
    {
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.NonId, category: MessageCategory.Category3)]
            public partial class NonIdWithCategory { public int X { get; set; } }
            """ + Footer);

        var diagnostic = Assert.Single(diagnostics, d => d.Id == "MSGPROT018");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.DoesNotContain("partial class NonIdWithCategory", generated);
        Assert.Empty(compileErrors);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(99)]
    public void MSGPROT018_undefined_message_kind_value_is_rejected(int kindValue)
    {
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + $$"""
            [Message((MessageKind){{kindValue}})]
            public partial class BadKind { public int X { get; set; } }
            """ + Footer);

        var diagnostic = Assert.Single(diagnostics, d => d.Id == "MSGPROT018");
        Assert.Contains(kindValue.ToString(), diagnostic.GetMessage());
        Assert.DoesNotContain("partial class BadKind", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void generic_message_type_generates_compilable_code_preserving_type_parameters()
    {
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 1)]
            public partial class Msg<T> { public T? Value { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Empty(compileErrors);
        Assert.Contains("public partial class Msg<T>", generated);
        Assert.Contains("IHasIdMessageSerializable<Msg<T>>", generated);
        Assert.Contains("Serialize(Msg<T> message", generated);
    }

    [Fact]
    public void generic_message_type_does_not_generate_registration_code()
    {
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.NonId)]
            public partial class Pair<T> { public T? First { get; set; } public int Tag { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Empty(compileErrors);
        Assert.DoesNotContain("RegisterNonIdMessage", generated);
        Assert.DoesNotContain("[ModuleInitializer]", generated);
    }

    [Fact]
    public void GenericMessage_construction_declaration_generates_registration_class()
    {
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 1)]
            public partial class Target { public int X { get; set; } }

            [Message(MessageKind.Standalone, 2)]
            [GenericMessage(typeof(Box<Target>), ClassId = 7)]
            public partial class Box<T> { public T? Value { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Empty(compileErrors);
        Assert.Contains("RegisterGenericConstruction<global::TestNs.Box<global::TestNs.Target>>(7)", generated);
        // The class id is looked up from the runtime registry (no cached field).
        Assert.Contains("MessageSerializer.GetGenericClassId<Box<T>>()", generated);
        Assert.DoesNotContain("__GenericClassId", generated);
        // Generic header flag 0: the generic flag participates in the MessageId composition.
        Assert.Contains("MessageId => 2;", generated);
    }

    [Fact]
    public void MSGPROT008_GenericMessage_on_non_generic_type_is_an_error()
    {
        var (diagnostics, _) = RunGenerator(Header + """
            [Message(MessageKind.Standalone, 1)]
            [GenericMessage(typeof(int), ClassId = 1)]
            public partial class NotGeneric { public int X { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT008");
    }

    [Fact]
    public void MSGPROT008_unbound_generic_construction_declaration_is_an_error()
    {
        var (diagnostics, _) = RunGenerator(Header + """
            [Message(MessageKind.Standalone, 2)]
            public partial class Box<T> { public T? Value { get; set; } }

            [GenericMessage(typeof(Box<>), ClassId = 1)]
            static class Carrier { }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT008");
    }

    [Fact]
    public void MSGPROT008_generic_declaration_without_standalone_message_is_an_error()
    {
        var (diagnostics, _) = RunGenerator(Header + """
            [Message(MessageKind.NonId)]
            public partial class NoId<T> { public T? V { get; set; } }

            [GenericMessage(typeof(NoId<int>), ClassId = 1)]
            static class Carrier { }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT008");
    }

    [Theory]
    [InlineData(16777216u)]    // 2^24 — first value beyond the 24-bit wire slot
    [InlineData(4294967295u)]  // uint.MaxValue
    public void MSGPROT008_class_id_above_upper_limit_is_rejected_by_compile_diagnostic(uint classId)
    {
        // KI-27 regression: without the upper-limit check the generator passed, and the generated registration carrier
        // threw RegisterGenericConstruction's ArgumentOutOfRangeException from the **module initializer**,
        // turning into TypeInitializationException (assembly load failure).
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + $$"""
            [Message(MessageKind.Standalone, 1)]
            [GenericMessage(typeof(Box<int>), ClassId = {{classId}})]
            public partial class Box<T> { public T? Value { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT008");
        // Must check up to `<` — the guidance exception in the generated `Serialize` also contains the word "RegisterGenericConstruction".
        Assert.DoesNotContain("RegisterGenericConstruction<", generated);
        Assert.Empty(compileErrors);
    }

    [Theory]
    [InlineData(1u)]          // minimum allowed
    [InlineData(16777215u)]   // maximum allowed (2^24 - 1)
    public void ClassId_boundary_values_generate_registration_code_without_diagnostics(uint classId)
    {
        // Reverse guard: adding the upper-limit check must not cut off the valid range (boundary values especially).
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + $$"""
            [Message(MessageKind.Standalone, 1)]
            [GenericMessage(typeof(Box<int>), ClassId = {{classId}})]
            public partial class Box<T> { public T? Value { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Contains("RegisterGenericConstruction<", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void derived_element_of_abstract_group_root_is_generated_without_new_modifier()
    {
        // KI-28 regression: abstract [Message(MessageKind.Parent)] is inheritance-only and emits no static contract,
        // yet derived elements got `new`, so consumer builds hit CS0109 with nothing to hide
        // (64 occurrences on a clean rebuild of this repo alone — polymorphic groups are a normal pattern since KI-24, so consumers hit it too).
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Parent, 500)]
            public abstract partial class AbstractRoot { public long Timestamp { get; set; } }

            [Message(MessageKind.Child, 501)]
            public partial class ElementA : AbstractRoot { public string? User { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.DoesNotContain("new static", generated);
        Assert.Contains("public static void Serialize(ElementA message, ref MessageBufferWriter writer)", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void derived_element_of_concrete_group_root_keeps_new_modifier()
    {
        // Reverse guard: when the base does emit static contracts (MessageId, Deserialize, etc.), `new` is required
        // (to avoid CS0108) and must stay — bulk-removing `new` to kill CS0109 breaks this side.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Parent, 510)]
            public partial class ConcreteRoot { public long Timestamp { get; set; } }

            [Message(MessageKind.Child, 511)]
            public partial class ElementB : ConcreteRoot { public int Reason { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Contains("new static", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void element_derived_from_abstract_group_root_in_another_assembly_is_generated_without_new_modifier()
    {
        // KI-28 cross-assembly tail: splitting a protocol DLL (abstract root) from server/client DLLs (concrete elements)
        // is a standard commercial layout. A metadata-only base has no syntax references, so partial detection fails and
        // the old implementation always emitted `new` — but abstractness is decidable from metadata alone, and abstract message
        // types never emit static contracts (group-root skip, MSGPROT010), guaranteeing CS0109 ×6 per type.
        // TreatWarningsAsErrors consumers fail the build; otherwise warnings pile up on every clean rebuild.
        var (diagnostics, generated, compileErrors, warnings) = RunGeneratorWithMetadataBase("""
            using MessageProtocol;
            namespace ProtocolShared
            {
                [Message(MessageKind.Parent, 520)]
                public abstract partial class SharedAbstractRoot { public long Stamp { get; set; } }
            }
            """, Header + """
            [Message(MessageKind.Child, 521)]
            public partial class ElementC : ProtocolShared.SharedAbstractRoot { public string? Tag { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.DoesNotContain("new static", generated);
        Assert.DoesNotContain(warnings, d => d.Id == "CS0109");
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void element_derived_from_concrete_group_root_in_another_assembly_keeps_new_modifier()
    {
        // Reverse guard (cross-assembly): a concrete metadata-only base cannot tell whether the generated contract
        // exists on that side, so `new` is kept as before — guessing wrong flips the failure to CS0108/CS0114.
        var (diagnostics, generated, compileErrors, warnings) = RunGeneratorWithMetadataBase("""
            using MessageProtocol;
            namespace ProtocolShared
            {
                [Message(MessageKind.Parent, 530)]
                public partial class SharedConcreteRoot { public long Stamp { get; set; } }
            }
            """, Header + """
            [Message(MessageKind.Child, 531)]
            public partial class ElementD : ProtocolShared.SharedConcreteRoot { public int Code { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Contains("new static", generated);
        Assert.DoesNotContain(warnings, d => d.Id == "CS0109");
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void initialize_of_cross_assembly_base_exposed_via_ivt_keeps_new_modifier()
    {
        // KI-42 IVT loop: when the protocol DLL opens internals via [InternalsVisibleTo("consumer")],
        // the base's internal Initialize becomes accessible and the hidden target comes back — dropping `new`
        // then surfaces an unfixable CS0108 in generated code (TreatWarningsAsErrors consumers fail the build).
        var (diagnostics, generated, compileErrors, warnings) = RunGeneratorWithMetadataBase("""
            using System.Runtime.CompilerServices;
            using MessageProtocol;
            [assembly: InternalsVisibleTo("GeneratorConsumer")]
            namespace ProtocolShared
            {
                [Message(MessageKind.Parent, 540)]
                public partial class SharedIvtRoot { public long Stamp { get; set; } }
            }
            """, Header + """
            [Message(MessageKind.Child, 541)]
            public partial class ElementE : ProtocolShared.SharedIvtRoot { public int Code { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Contains("internal new static void Initialize()", generated);
        Assert.DoesNotContain(warnings, d => d.Id == "CS0108");
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void initialize_of_cross_assembly_base_without_ivt_omits_new_modifier()
    {
        // Non-IVT reverse guard: an internal base Initialize with closed access is nothing to hide, so adding
        // `new` yields CS0109 — pins that the old KI-42 behavior (omit) did not regress with the IVT branch.
        var (diagnostics, generated, compileErrors, warnings) = RunGeneratorWithMetadataBase("""
            using MessageProtocol;
            namespace ProtocolShared
            {
                [Message(MessageKind.Parent, 550)]
                public partial class SharedClosedRoot { public long Stamp { get; set; } }
            }
            """, Header + """
            [Message(MessageKind.Child, 551)]
            public partial class ElementF : ProtocolShared.SharedClosedRoot { public int Code { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.DoesNotContain("internal new static void Initialize()", generated);
        Assert.Contains("internal static void Initialize()", generated);
        Assert.DoesNotContain(warnings, d => d.Id == "CS0109");
        Assert.Empty(compileErrors);
    }

    /// <summary>
    /// Compiles the base into a separate assembly (in-memory PE emit) so the consumer compilation inherits from a
    /// metadata-only base — the cross-assembly inheritance scenario (protocol DLL + project DLL). Also returns warnings (for CS0109 checks).
    /// </summary>
    static (ImmutableArray<Diagnostic> Diagnostics, string GeneratedText, ImmutableArray<Diagnostic> CompileErrors, ImmutableArray<Diagnostic> CompileWarnings)
        RunGeneratorWithMetadataBase(string baseSource, string consumerSource)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(Serialize.MessageSerializer).Assembly.Location));

        var baseWithGenerator = CSharpGeneratorDriver.Create(new MessageCodeGenerator().AsSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(
                CSharpCompilation.Create(
                    "ProtocolShared",
                    new[] { CSharpSyntaxTree.ParseText(baseSource) },
                    references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)),
                out var updatedBase, out _);
        _ = baseWithGenerator;
        using var peStream = new System.IO.MemoryStream();
        var emitResult = updatedBase.Emit(peStream);
        Assert.True(emitResult.Success, string.Join("\n", emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        peStream.Position = 0;
        references.Add(MetadataReference.CreateFromStream(peStream));

        var consumer = CSharpCompilation.Create(
            "GeneratorConsumer",
            new[] { CSharpSyntaxTree.ParseText(consumerSource) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(new MessageCodeGenerator().AsSourceGenerator());
        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(consumer, out var updated, out var diagnostics);
        var runResult = driver.GetRunResult();

        var generated = string.Concat(
            runResult.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SourceText.ToString()));
        var allCompileDiagnostics = updated.GetDiagnostics();
        var compileErrors = allCompileDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        var warnings = allCompileDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToImmutableArray();
        return (diagnostics, generated, compileErrors, warnings);
    }

    [Fact]
    public void same_type_produces_identical_generated_text_regardless_of_emit_order_and_count()
    {
        // KI-3 regression: when generated local name numbering (`__item3` etc.) used a process-global static counter,
        // a second emit received different numbers, giving **same input → different text**. That non-determinism
        // broke Roslyn's generated-output comparison every run, replacing and recompiling generated trees even for
        // unrelated edits, and hurt build reproducibility and diff readability. Numbering now lives in per-emit EmitState and depends only on the input.
        var compilation = CreateTpaCompilation(Header + """
            [Message(MessageKind.Standalone, 1)]
            public partial class DeterminismMessage
            {
                public System.Collections.Generic.List<int>? Values { get; set; }
                public string[]? Tags { get; set; }
                public DeterminismMessage? Next { get; set; }
                public DeterminismPayload? Payload { get; set; }
            }

            public class DeterminismPayload
            {
                public int X { get; set; }
                public string? Name { get; set; }
            }

            [Message(MessageKind.Standalone, 2)]
            public partial class OtherDeterminismMessage
            {
                public System.Collections.Generic.List<long>? Samples { get; set; }
                public OtherDeterminismMessage? Next { get; set; }
            }
            """ + Footer);

        var attributeReferences = new AttributeReferences(compilation);

        // Emits A → B → A → B twice each: with a global counter the second A/B would shift numbers and differ.
        string firstA = EmitFor(compilation, "TestNs.DeterminismMessage", attributeReferences);
        string firstB = EmitFor(compilation, "TestNs.OtherDeterminismMessage", attributeReferences);
        string secondA = EmitFor(compilation, "TestNs.DeterminismMessage", attributeReferences);
        string secondB = EmitFor(compilation, "TestNs.OtherDeterminismMessage", attributeReferences);

        // First confirm several locals actually use the numbering — so the text comparison cannot pass vacuously.
        Assert.Contains("__item", firstA);
        Assert.Contains("__arr", firstA);
        Assert.Contains("__span", firstA);
        Assert.Contains("__refKind", firstA);
        Assert.Contains("__backId", firstA);

        Assert.Equal(firstA, secondA);
        Assert.Equal(firstB, secondB);
        Assert.NotEqual(firstA, firstB);
    }

    [Fact]
    public void MSGPROT012_concrete_base_member_warns_about_derived_member_loss()
    {
        // KI-29: using a **concrete** message base (which has derived message types) as a member's static type serializes
        // by declared type, silently dropping derived members (verified by execution: LoginEvent.User lost, restored as EventBase).
        // The behavior itself is valid, so generation is not blocked — a warning is reported instead.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Parent, 600)]
            public partial class PolyRoot { public long Timestamp { get; set; } }

            [Message(MessageKind.Child, 601)]
            public partial class PolyElement : PolyRoot { public string? User { get; set; } }

            [Message(MessageKind.Standalone, 602)]
            public partial class PolyHost { public PolyRoot? Event { get; set; } }
            """ + Footer);

        var warning = Assert.Single(diagnostics, d => d.Id == "MSGPROT012");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("Event", warning.GetMessage());
        // Only a warning — generation proceeds normally, with no error diagnostics or compile errors.
        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT") && d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("__WritePayload", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT012_collection_element_type_also_warns()
    {
        var (diagnostics, _, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Parent, 610)]
            public partial class ListRoot { public long Timestamp { get; set; } }

            [Message(MessageKind.Child, 611)]
            public partial class ListElement : ListRoot { public string? User { get; set; } }

            [Message(MessageKind.Standalone, 612)]
            public partial class ListHost { public System.Collections.Generic.List<ListRoot>? Events { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT012");
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT012_abstract_root_member_does_not_warn()
    {
        // Reverse guard: abstract message type members record concrete elements with their header via runtime dispatch (KI-24),
        // so nothing is lost — supported polymorphic patterns must not trigger the warning.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Parent, 620)]
            public abstract partial class AbsRoot { public long Timestamp { get; set; } }

            [Message(MessageKind.Child, 621)]
            public partial class AbsElement : AbsRoot { public string? User { get; set; } }

            [Message(MessageKind.Standalone, 622)]
            public partial class AbsHost { public AbsRoot? Event { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT012");
        Assert.Contains("SerializeToWriter", generated);   // confirms the dispatch path
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT012_concrete_member_without_derived_messages_does_not_warn()
    {
        // Reverse guard: a concrete-type member with no derived message types (an ordinary nested payload) loses nothing and must stay silent.
        var (diagnostics, _, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 630)]
            public partial class PlainPayload { public int X { get; set; } }

            [Message(MessageKind.Standalone, 631)]
            public partial class PlainHost { public PlainPayload? Payload { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT012");
        Assert.Empty(compileErrors);
    }

    /// <summary>Drives the emitter once for the given type and returns the generated text (each call gets a fresh EmitState).</summary>
    static string EmitFor(CSharpCompilation compilation, string metadataName, AttributeReferences attributeReferences)
    {
        var rootType = compilation.GetTypeByMetadataName(metadataName)!;
        var typeMeta = new TypeMetadata(rootType, attributeReferences);

        bool emitted = MessageSerializeCodeEmitter.TryEmit(
            typeMeta, attributeReferences, hasCollectionsMarshal: true, consumerAssembly: compilation.Assembly, out var code, out _);

        Assert.True(emitted);
        Assert.NotNull(code);
        return code!;
    }

    [Fact]
    public void distributed_declaration_carrier_generates_construction_registration_code()
    {
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 1)]
            public partial class Target { public int X { get; set; } }

            [Message(MessageKind.Standalone, 2)]
            public partial class Box<T> { public T? Value { get; set; } }

            [GenericMessage(typeof(Box<Target>), ClassId = 5)]
            static class Carrier { }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Empty(compileErrors);
        Assert.Contains("RegisterGenericConstruction<global::TestNs.Box<global::TestNs.Target>>(5)", generated);
    }

    [Fact]
    public void MSGPROT008_declaring_the_same_construction_twice_in_one_compilation_is_an_error()
    {
        var (diagnostics, generated) = RunGenerator(Header + """
            [Message(MessageKind.Standalone, 1)]
            public partial class Target { public int X { get; set; } }

            [Message(MessageKind.Standalone, 2)]
            public partial class Box<T> { public T? Value { get; set; } }

            [GenericMessage(typeof(Box<Target>), ClassId = 1)]
            static class CarrierA { }

            [GenericMessage(typeof(Box<Target>), ClassId = 1)]
            static class CarrierB { }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT008");
        Assert.DoesNotContain("RegisterGenericConstruction<global::TestNs.Box<global::TestNs.Target>>(1)", generated);
    }

    [Fact]
    public void MSGPROT008_distributed_construction_on_non_generic_message_is_an_error()
    {
        var (diagnostics, _) = RunGenerator(Header + """
            [GenericMessage(typeof(int), ClassId = 1)]
            static class Carrier { }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT008");
    }

    [Fact]
    public void MSGPROT008_distributed_declaration_without_class_id_is_an_error()
    {
        var (diagnostics, _) = RunGenerator(Header + """
            [Message(MessageKind.Standalone, 1)]
            public partial class Target { public int X { get; set; } }

            [Message(MessageKind.Standalone, 2)]
            public partial class Box<T> { public T? Value { get; set; } }

            [GenericMessage(typeof(Box<Target>))]
            static class Carrier { }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT008");
    }

    [Fact]
    public void valid_type_generates_registration_code()
    {
        var (diagnostics, generated) = RunGenerator(Header + """
            [Message(MessageKind.Standalone, 7, MessageCategory.Category2)]
            public partial class Good
            {
                public int X { get; set; }
                public string? Y { get; set; }
            }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Contains("RegisterHasIdMessage<Good>", generated);
        Assert.Contains("[ModuleInitializer]", generated);
        Assert.Contains("public static uint MessageId => ", generated);
    }

    [Fact]
    public void NonId_type_generates_NonId_registration()
    {
        var (diagnostics, generated) = RunGenerator(Header + """
            [Message(MessageKind.NonId)]
            public partial class NoId { public byte B { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Contains("RegisterNonIdMessage<NoId>", generated);
    }

    [Fact]
    public void abstract_group_root_is_skipped_from_generation()
    {
        var (diagnostics, generated) = RunGenerator(Header + """
            [Message(MessageKind.Parent, 1)]
            public abstract partial class AbstractRoot { public int X { get; set; } }

            [Message(MessageKind.Child, 2)]
            public partial class Concrete : AbstractRoot { public int Y { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.DoesNotContain("__WritePayload_TestNs_AbstractRoot", generated);
        Assert.Contains("RegisterHasIdMessage<Concrete>", generated);
    }

    [Fact]
    public void group_hierarchy_element_includes_root_members()
    {
        var (diagnostics, generated) = RunGenerator(Header + """
            [Message(MessageKind.Parent, 1)]
            public partial class R { public int BaseField { get; set; } }

            [Message(MessageKind.Child, 2)]
            public partial class E : R { public int ChildField { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Contains("BaseField", generated);
        Assert.Contains("ChildField", generated);
    }

    [Fact]
    public void same_named_types_in_different_namespaces_are_all_generated_without_conflict()
    {
        // If hint names use only the simple type name, AddSource throws ArgumentException and the entire generation is lost.
        var (diagnostics, generated) = RunGenerator("""
            using MessageProtocol;
            namespace NsA
            {
                [Message(MessageKind.Standalone, 1)]
                public partial class Same { public int X { get; set; } }
            }
            namespace NsB
            {
                [Message(MessageKind.Standalone, 2)]
                public partial class Same { public int Y { get; set; } }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Contains("namespace NsA", generated);
        Assert.Contains("namespace NsB", generated);
        Assert.Equal(2, CountOccurrences(generated, "RegisterHasIdMessage<Same>"));
    }

    [Fact]
    public void nested_type_and_namespace_dots_with_same_shape_do_not_collide()
    {
        // Class C in namespace A.B → 'A.B.C'; nested B.C in namespace A → 'A.B+C'.
        // If the nested separator were '.', the two hint names would collide and the entire generation would be lost.
        var (diagnostics, generated) = RunGenerator("""
            using MessageProtocol;
            namespace A.B
            {
                [Message(MessageKind.Standalone, 1)]
                public partial class C { public int X { get; set; } }
            }
            namespace A
            {
                public partial class B
                {
                    [Message(MessageKind.Standalone, 2)]
                    public partial class C { public int Y { get; set; } }
                }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Contains("namespace A.B", generated);
        Assert.Contains("namespace A", generated);
        Assert.Equal(2, CountOccurrences(generated, "RegisterHasIdMessage<C>"));
    }

    [Fact]
    public void list_bulk_read_on_target_without_collectionsmarshal_validates_count_times_element_size()
    {
        // KI-17 regression: on targets without CollectionsMarshal (e.g. netstandard2.0 consumers), the per-element read path
        // must also validate count × element size ≤ remaining bytes before pre-allocating the List (validating count alone forces 8× over-allocation).
        var compilation = CreateTpaCompilation(Header + """
            [Message(MessageKind.Standalone, 1)]
            public partial class BulkFallbackMessage
            {
                public System.Collections.Generic.List<long>? Values { get; set; }
            }
            """ + Footer);

        var rootType = compilation.GetTypeByMetadataName("TestNs.BulkFallbackMessage")!;
        var attributeReferences = new AttributeReferences(compilation);
        var typeMeta = new TypeMetadata(rootType, attributeReferences);

        bool emitted = MessageSerializeCodeEmitter.TryEmit(
            typeMeta, attributeReferences, hasCollectionsMarshal: false, consumerAssembly: compilation.Assembly, out var code, out _);

        Assert.True(emitted);
        Assert.NotNull(code);
        Assert.Contains("* 8 > reader.Remaining", code);
    }

    [Fact]
    public void MSGPROT010_abstract_message_type_is_rejected_from_generation()
    {
        var (diagnostics, generated) = RunGenerator(Header + """
            [Message(MessageKind.NonId)]
            public abstract partial class AbstractMessage { public int X { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT010");
        Assert.DoesNotContain("__WritePayload", generated);
    }

    [Fact]
    public void MSGPROT010_positional_record_message_is_rejected_from_generation()
    {
        var (diagnostics, generated) = RunGenerator(Header + """
            [Message(MessageKind.NonId)]
            public partial record PointRecord(int X);
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT010");
        Assert.DoesNotContain("__WritePayload", generated);
    }

    [Fact]
    public void MSGPROT011_read_only_member_is_rejected_from_generation()
    {
        var (diagnostics, generated) = RunGenerator(Header + """
            [Message(MessageKind.NonId)]
            public partial class GetOnlyMessage { public int X { get; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT011");
        Assert.DoesNotContain("__WritePayload", generated);
    }

    [Fact]
    public void member_violating_two_rules_reports_both_diagnostics()
    {
        // Audit ledger LOW (2026-09-08) regression: the diagnostic dedup key was name+type only, so a member that was both
        // unsupported (MSGPROT006) and read-only (MSGPROT011) silently lost the second rule — the key now includes
        // reason (kind) and location so only repeated reports of the same rule on the same member are deduplicated.
        var (diagnostics, _) = RunGenerator(Header + """
            [Message(MessageKind.NonId)]
            public partial class BothRulesMessage
            {
                public System.Collections.Generic.Dictionary<string, int>? Bad { get; }
            }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT006");
        Assert.Contains(diagnostics, d => d.Id == "MSGPROT011");
    }

    [Fact]
    public void two_members_violating_different_rules_produce_two_diagnostics()
    {
        // Reverse guard: when each member violates one distinct rule, both locations are reported.
        var (diagnostics, _) = RunGenerator(Header + """
            [Message(MessageKind.NonId)]
            public partial class TwoMembersMessage
            {
                public System.Collections.Generic.Dictionary<string, int>? Unsupported { get; set; }
                public int ReadOnly { get; }
            }
            """ + Footer);

        Assert.Equal(1, diagnostics.Count(d => d.Id == "MSGPROT006"));
        Assert.Equal(1, diagnostics.Count(d => d.Id == "MSGPROT011"));
    }

    [Fact]
    public void MSGPROT006_unconstructible_payload_member_reports_unsupported_type_diagnostic()
    {
        // Abstract class and positional record payloads cannot be instantiated via a default constructor, so they are rejected with per-member diagnostics.
        var (diagnostics, _) = RunGenerator(Header + """
            public abstract class AbstractPayload { public int X { get; set; } }
            public partial record PositionalPayload(int X);

            [Message(MessageKind.Standalone, 1)]
            public partial class Host
            {
                public AbstractPayload? A { get; set; }
                public PositionalPayload? B { get; set; }
            }
            """ + Footer);

        Assert.Equal(2, diagnostics.Count(d => d.Id == "MSGPROT006"));
    }

    [Fact]
    public void MSGPROT011_read_only_member_inside_payload_is_rejected_from_generation()
    {
        var (diagnostics, _) = RunGenerator(Header + """
            public partial class GetOnlyPayload { public int X { get; } }

            [Message(MessageKind.Standalone, 1)]
            public partial class Host
            {
                public GetOnlyPayload? Payload { get; set; }
            }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT011");
    }

    [Fact]
    public void same_named_nested_construction_carriers_each_generate_a_unique_registration_class()
    {
        // KI-19 regression: two same-named nested carriers in the same namespace each emit a unique registration class without colliding.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 1)]
            [GenericMessage(typeof(Envelope<int>), ClassId = 1)]
            public partial class Envelope<T>
            {
                public T? Value { get; set; }
            }

            static class OuterA
            {
                [GenericMessage(typeof(Envelope<string>), ClassId = 2)]
                internal static class Carrier { }
            }

            static class OuterB
            {
                [GenericMessage(typeof(Envelope<long>), ClassId = 3)]
                internal static class Carrier { }
            }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Empty(compileErrors);
        Assert.Contains("__GenericConstructionRegistration_TestNs_OuterA_Carrier", generated);
        Assert.Contains("__GenericConstructionRegistration_TestNs_OuterB_Carrier", generated);
        Assert.Contains("RegisterGenericConstruction<global::TestNs.Envelope<string>>(2)", generated);
        Assert.Contains("RegisterGenericConstruction<global::TestNs.Envelope<long>>(3)", generated);
    }

    [Fact]
    public void public_indexer_is_excluded_from_serialization_members()
    {
        // KI-23 regression: an indexer is also an `IPropertySymbol` (Name = "this[]"); picking it up as a member generated
        // syntactically invalid code like `message.this[]` without any diagnostic, breaking consumer builds.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 1)]
            public partial class WithIndexer
            {
                public int Value { get; set; }
                public int this[int index] { get => Value; set => Value = value; }
            }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.DoesNotContain("this[", generated);
        Assert.Contains("Value", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void abstract_message_type_member_generates_runtime_dispatch_instead_of_delegation()
    {
        // KI-24 regression: abstract [Message(MessageKind.Parent)] is the natural declaration for a polymorphic group, but the generator
        // cannot instantiate it and emits no static Serialize/Deserialize — delegation code broke consumer builds with CS0117
        // ('AbstractEvent' has no definition for 'Serialize') without any diagnostic.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Parent, 300)]
            public abstract partial class AbstractEvent { public long Timestamp { get; set; } }

            [Message(MessageKind.Child, 301)]
            public partial class LoginEvent : AbstractEvent { public string? User { get; set; } }

            [Message(MessageKind.Standalone, 302)]
            public partial class Envelope
            {
                public AbstractEvent? Payload { get; set; }
                public System.Collections.Generic.List<AbstractEvent>? History { get; set; }
            }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.DoesNotContain("AbstractEvent.Serialize", generated);
        Assert.DoesNotContain("AbstractEvent.Deserialize", generated);
        Assert.Contains("MessageSerializer.SerializeToWriter(message.Payload, ref writer)", generated);
        Assert.Contains("var __dispatched", generated);
        Assert.Contains("MessageSerializer.DeserializeFromReader(ref reader)", generated);
        Assert.Contains("if (!(__dispatched", generated); // KI-41: guidance type check on the dispatched restore object
        Assert.Contains("(global::TestNs.AbstractEvent)__dispatched", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void concrete_message_member_outside_the_graph_keeps_static_delegation()
    {
        // Reverse guard for the KI-24 fix: a *concrete* message type left out of the graph because of a private parameterless
        // constructor still has generated static members, so static delegation is correct (must not be over-routed to runtime dispatch).
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 310)]
            public partial class PrivateCtorHost { public PrivateCtorPayload? Payload { get; set; } }

            [Message(MessageKind.NonId)]
            public partial class PrivateCtorPayload
            {
                PrivateCtorPayload() { }
                public int X { get; set; }
            }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Contains("global::TestNs.PrivateCtorPayload.Serialize(message.Payload, ref writer)", generated);
        Assert.Contains("global::TestNs.PrivateCtorPayload.Deserialize(ref reader)", generated);
        Assert.DoesNotContain("SerializeToWriter", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void wire_member_order_pins_base_declaration_order_and_shadow_removal_position()
    {
        // KI-4 regression: payload byte order is wire format that both sides must agree on, but it used to ride on
        // `Dictionary.Values` enumeration order (insertion order only — a BCL implementation detail, not a contract).
        // `TypeMetadata.GetWireMembers` now pins it explicitly, and this test nails down that layout.
        var compilation = CreateTpaCompilation(Header + """
            [Message(MessageKind.Parent, 400)]
            public partial class OrderBase
            {
                public int BaseFirst { get; set; }
                public string? Shadowed { get; set; }
                public long BaseLast { get; set; }
            }

            [Message(MessageKind.Child, 401)]
            public partial class OrderDerived : OrderBase
            {
                public new long Shadowed { get; set; }
                public byte DerivedOwn { get; set; }
            }
            """ + Footer);

        var rootType = compilation.GetTypeByMetadataName("TestNs.OrderDerived")!;
        var attributeReferences = new AttributeReferences(compilation);
        var typeMeta = new TypeMetadata(rootType, attributeReferences);

        bool emitted = MessageSerializeCodeEmitter.TryEmit(
            typeMeta, attributeReferences, hasCollectionsMarshal: true, consumerAssembly: compilation.Assembly, out var code, out _);

        Assert.True(emitted);
        Assert.NotNull(code);
        // Base declaration order first, derived-only members after.
        Assert.Equal(
            new[] { "BaseFirst", "Shadowed", "BaseLast", "DerivedOwn" },
            ExtractWriteOrder(code!));
        // The shadow-removed member keeps its **base position** and is written with the derived type (long).
        Assert.Contains("writer.WriteInt64(message.Shadowed)", code);
        Assert.DoesNotContain("writer.WriteString(message.Shadowed)", code);
    }

    [Fact]
    public void nested_payload_helper_member_order_follows_the_same_rule()
    {
        // The emitter (root payload) and the graph (nested payload helper) used to carry the same merge logic separately.
        // They now share one implementation (`TypeMetadata.GetWireMembers`), so this pins that the nested helper's byte order follows the same rule.
        var compilation = CreateTpaCompilation(Header + """
            public class NestedOrderBase
            {
                public int NestedBaseFirst { get; set; }
                public string? NestedShadow { get; set; }
            }

            public class NestedOrderDerived : NestedOrderBase
            {
                public new long NestedShadow { get; set; }
                public byte NestedOwn { get; set; }
            }

            [Message(MessageKind.Standalone, 402)]
            public partial class NestedOrderHost
            {
                public int HostOwn { get; set; }
                public NestedOrderDerived? Payload { get; set; }
            }
            """ + Footer);

        var rootType = compilation.GetTypeByMetadataName("TestNs.NestedOrderHost")!;
        var attributeReferences = new AttributeReferences(compilation);
        var typeMeta = new TypeMetadata(rootType, attributeReferences);

        bool emitted = MessageSerializeCodeEmitter.TryEmit(
            typeMeta, attributeReferences, hasCollectionsMarshal: true, consumerAssembly: compilation.Assembly, out var code, out _);

        Assert.True(emitted);
        Assert.NotNull(code);

        // Slices the write order after the nested payload write helper (the one with signature `NestedOrderDerived message`) for verification.
        int helperStart = code!.IndexOf("NestedOrderDerived message", StringComparison.Ordinal);
        Assert.True(helperStart >= 0, "nested payload write helper not found");

        Assert.Equal(
            new[] { "NestedBaseFirst", "NestedShadow", "NestedOwn" },
            ExtractWriteOrder(code[helperStart..]));
        Assert.Contains("writer.WriteInt64(message.NestedShadow)", code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void collection_write_snapshots_the_member_into_a_local(bool hasCollectionsMarshal)
    {
        // KI-26 regression: if the length prefix, loop condition, and element access each re-evaluate `message.Values`, the getter runs 2N+2 times,
        // and for computed properties the length and elements can come from different instances, making the frame self-contradictory.
        // hasCollectionsMarshal=false is the Unity/netstandard2.1 consumer path, not executed in this repo → pinned via generated text.
        var compilation = CreateTpaCompilation(Header + """
            [Message(MessageKind.Standalone, 1)]
            public partial class SnapshotMessage
            {
                public System.Collections.Generic.List<int>? Values { get; set; }
                public System.Collections.Generic.List<string>? Names { get; set; }
                public string[]? Tags { get; set; }
            }
            """ + Footer);

        var rootType = compilation.GetTypeByMetadataName("TestNs.SnapshotMessage")!;
        var attributeReferences = new AttributeReferences(compilation);
        var typeMeta = new TypeMetadata(rootType, attributeReferences);

        bool emitted = MessageSerializeCodeEmitter.TryEmit(
            typeMeta, attributeReferences, hasCollectionsMarshal, consumerAssembly: compilation.Assembly, out var code, out _);

        Assert.True(emitted);
        Assert.NotNull(code);

        // The member expression appears only once, in the snapshot — the null check also uses the snapshot local, so
        // a computed property returning null on the second evaluation cannot produce an NRE (TOCTOU blocked).
        Assert.Equal(1, CountOccurrences(code!, "message.Values"));
        Assert.Equal(1, CountOccurrences(code!, "message.Names"));
        Assert.Equal(1, CountOccurrences(code!, "message.Tags"));
        Assert.DoesNotContain("if (message.Values is null)", code);

        Assert.Contains(hasCollectionsMarshal ? "var __list" : "var __coll", code);
        Assert.Contains("var __arr", code);      // arrays are snapshotted in both configurations
    }

    [Theory]
    [InlineData(16)]
    [InlineData(99)]
    [InlineData(255)]
    public void MSGPROT013_out_of_range_category_is_rejected_by_compile_diagnostic(int categoryValue)
    {
        // KI-8 regression: the emitter **silently masked** the category value with `& 0x0F`, turning 99 into 3 —
        // the wire MessageId differed from the developer's intent (with no diagnostic).
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + $$"""
            [Message(MessageKind.Standalone, 1, (MessageCategory){{categoryValue}})]
            public partial class BadCategory { public int X { get; set; } }
            """ + Footer);

        var diagnostic = Assert.Single(diagnostics, d => d.Id == "MSGPROT013");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(categoryValue.ToString(), diagnostic.GetMessage());
        Assert.DoesNotContain("__WritePayload", generated);   // generation skipped
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT013_message_id_collision_from_masking_is_blocked_at_compile_time()
    {
        // Shape confirmed by experiment: `(MessageCategory)99` is masked to 99 & 0x0F = 3, producing the **same wire MessageId**
        // (0x23000007) as the `Category3` message, and the module initializer threw a registration conflict exception
        // ("Message type with ID 587202567 is already registered by 'MaskedCategory'"), failing the assembly load.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 7, (MessageCategory)99)]
            public partial class MaskedCategory { public int X { get; set; } }

            [Message(MessageKind.Standalone, 7, MessageCategory.Category3)]
            public partial class RealCategory3 { public int X { get; set; } }
            """ + Footer);

        var diagnostic = Assert.Single(diagnostics, d => d.Id == "MSGPROT013");
        Assert.Contains("MaskedCategory", diagnostic.GetMessage());
        // The unmasked side is generated normally (no over-blocking).
        Assert.Contains("RealCategory3", generated);
        Assert.DoesNotContain("partial class MaskedCategory", generated);
        Assert.Empty(compileErrors);
    }

    [Theory]
    [InlineData("MessageCategory.Category0", "0x20")]
    [InlineData("MessageCategory.Category15", "0x2F")]
    public void category_boundary_values_are_allowed_and_carried_into_the_header_nibble(string categoryExpression, string expectedHeaderByte)
    {
        // Reverse guard: adding the upper-limit check must not cut off the valid range (especially 0 and 15).
        // Standalone(2) << 4 | category → Category0 = 0x20, Category15 = 0x2F.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + $$"""
            [Message(MessageKind.Standalone, 1, {{categoryExpression}})]
            public partial class CategoryBoundary { public int X { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Contains($"writer.WriteByte({expectedHeaderByte})", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT014_two_messages_with_the_same_wire_message_id_are_rejected_at_compile_time()
    {
        // KI-31 regression: id collisions were only discovered in the module initializer's `_registeredMessageIds`,
        // becoming `InvalidOperationException: Message type with ID … is already registered by '…'` → TypeInitializationException
        // (assembly load failure); the error message pointed at the counterpart type without explaining the cause.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 7)]
            public partial class FirstMessage { public int X { get; set; } }

            [Message(MessageKind.Standalone, 7)]
            public partial class SecondMessage { public int Y { get; set; } }
            """ + Footer);

        // Both types are named as the counterpart from their own perspective.
        var reported = diagnostics.Where(d => d.Id == "MSGPROT014").ToArray();
        Assert.Equal(2, reported.Length);
        Assert.All(reported, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.Contains("SecondMessage", reported[0].GetMessage());
        Assert.Contains("FirstMessage", reported[1].GetMessage());
        Assert.Contains("0x", reported[0].GetMessage());   // also reports the collided wire id in hex

        // Neither is generated (neither can be registered).
        Assert.DoesNotContain("partial class FirstMessage", generated);
        Assert.DoesNotContain("partial class SecondMessage", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT014_different_category_same_id_value_does_not_collide()
    {
        // Reverse guard: the collision key is the **assembled wire MessageId** (flags + category + 24-bit value), not the raw property values.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 7, MessageCategory.Category1)]
            public partial class CategoryOneMessage { public int X { get; set; } }

            [Message(MessageKind.Standalone, 7, MessageCategory.Category2)]
            public partial class CategoryTwoMessage { public int Y { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT014");
        Assert.Contains("partial class CategoryOneMessage", generated);
        Assert.Contains("partial class CategoryTwoMessage", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT014_generic_declarations_do_not_collide_even_with_the_same_id_value()
    {
        // Reverse guard: the generic construction's runtime key is (MessageId, ClassId), so the same declaration id value coexists when ClassId differs.
        // (Cross-construction collisions are handled by the existing `CollectConstructionConflicts`.)
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 7)]
            [GenericMessage(typeof(GenA<int>), ClassId = 1)]
            public partial class GenA<T> { public T? Value { get; set; } }

            [Message(MessageKind.Standalone, 7)]
            [GenericMessage(typeof(GenB<int>), ClassId = 2)]
            public partial class GenB<T> { public T? Value { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT014");
        Assert.Contains("GenA", generated);
        Assert.Contains("GenB", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT014_abstract_group_roots_are_excluded_from_collision_checking()
    {
        // Reverse guard: an abstract group root is inheritance-only and is never generated or registered (the generator skips it
        // intentionally), so it does not collide with a concrete root of the same id value — only registrable types must be counted to avoid false positives.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Parent, 9)]
            public abstract partial class AbstractRoot { public long Timestamp { get; set; } }

            [Message(MessageKind.Parent, 9)]
            public partial class ConcreteRoot { public long Timestamp { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT014");
        Assert.Contains("partial class ConcreteRoot", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT014_valid_type_sharing_an_id_with_a_hierarchy_violation_gets_no_false_positive()
    {
        // KI-43 regression: types already rejected by MSGPROT003 (element without root) or MSGPROT004 (root with a root ancestor)
        // are never generated or registered, so they must be excluded from collision checking (KI-31: "count only what will actually be registered").
        // Including them blocks an innocent counterpart type behind a false-positive MSGPROT014.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Parent, 9)]
            public partial class RealRoot { public long Timestamp { get; set; } }

            [Message(MessageKind.Child, 7)]
            public partial class ValidChild : RealRoot { public int X { get; set; } }

            [Message(MessageKind.Child, 7)]
            public partial class OrphanChild { public int Y { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT003");       // only OrphanChild violates the hierarchy
        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT014"); // no false positive for ValidChild
        Assert.Contains("partial class ValidChild", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT014_valid_type_sharing_an_id_with_a_kind_rejected_declaration_gets_no_false_positive()
    {
        // KI-43 regression: an out-of-range kind value ((MessageKind)99) fails to decode, so TypeMetadata falls back to Automatic
        // inference; if the gate skipped the 018 check, the would-be-rejected declaration would be counted as Standalone.
        // (NonId+id combinations decode successfully and are already excluded via the IsNonIdMessage path — that case was the real gap.)
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message((MessageKind)99, id: 7)]
            public partial class BrokenKind { public int X { get; set; } }

            [Message(MessageKind.Standalone, 7)]
            public partial class OkStandalone { public int Y { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT018");       // only BrokenKind violates the kind rule
        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT014"); // no false positive for OkStandalone
        Assert.Contains("partial class OkStandalone", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT015_valid_construction_sharing_a_key_with_a_kind_rejected_declaration_gets_no_false_positive()
    {
        // KI-43 regression (generic variant): a generic declaration with an out-of-range kind fails to decode → Automatic fallback
        // counts it as Standalone, pulling it into the (MessageId, ClassId) runtime-key collision check. Including it costs a valid
        // construction its registration carrier via a false-positive MSGPROT015. (NonId+id combinations decode fine and are already excluded via IsNonIdMessage.)
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message((MessageKind)99, id: 5)]
            [GenericMessage(typeof(BrokenGeneric<int>), ClassId = 1)]
            public partial class BrokenGeneric<T> { public T? Value { get; set; } }

            [Message(MessageKind.Standalone, 5)]
            [GenericMessage(typeof(OkGeneric<int>), ClassId = 1)]
            public partial class OkGeneric<T> { public T? Value { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT018");       // only BrokenGeneric violates the kind rule
        Assert.Contains(diagnostics, d => d.Id == "MSGPROT008");       // carrier rejection for the BrokenGeneric construction
        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT015"); // no false positive for the OkGeneric construction
        Assert.Contains("RegisterGenericConstruction<global::TestNs.OkGeneric<int>>(1)", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void valid_type_sharing_an_id_with_a_MSGPROT002_rejected_type_gets_no_false_positive()
    {
        // KI-43 second round (review round 1) regression: a message whose nested containing type is non-partial is never generated
        // or registered (MSGPROT002), so it must be excluded from collision checking. The gate's IsPartial looks only at the type itself;
        // counting this type would cost the top-level valid type with the same assembled id its generation behind a false-positive MSGPROT014.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            public class NestOuter
            {
                [Message(MessageKind.Standalone, 7)]
                public partial class NestedStand { public int X { get; set; } }
            }

            [Message(MessageKind.Standalone, 7)]
            public partial class OkTopStand { public int Y { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT002");       // only NestedStand violates the nested partial rule
        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT014"); // no false positive for OkTopStand
        Assert.Contains("partial class OkTopStand", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void carrier_is_not_emitted_when_the_construction_declaration_is_rejected_by_MSGPROT005()
    {
        // KI-43 second round regression: if the carrier guard only checks 018, the carrier for a declaration rejected for id range
        // (MSGPROT005) is still emitted — RegisterGenericConstruction<T> then references a type not implementing
        // IHasIdMessageSerializable<T>, producing uncompilable generated code (CS0311).
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 99999999)]
            [GenericMessage(typeof(IdRangeBox<int>), ClassId = 1)]
            public partial class IdRangeBox<T> { public T? Value { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT005"); // first-line diagnostic at the declaration site
        Assert.Contains(diagnostics, d => d.Id == "MSGPROT008"); // second-line carrier rejection
        Assert.DoesNotContain("RegisterGenericConstruction", generated); // no CS0311 carrier emission
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void carrier_is_not_emitted_when_the_construction_declaration_is_not_partial()
    {
        // KI-43 second round regression: the carrier for a declaration rejected by MSGPROT001 must not be emitted either (same defect class as the 005 trigger).
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 5)]
            [GenericMessage(typeof(NotPartialBox<int>), ClassId = 1)]
            public class NotPartialBox<T> { public T? Value { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT001");
        Assert.Contains(diagnostics, d => d.Id == "MSGPROT008");
        Assert.DoesNotContain("RegisterGenericConstruction", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void valid_construction_sharing_a_key_with_a_MSGPROT002_rejected_declaration_gets_no_false_positive()
    {
        // KI-43 second round regression (generic 002 gap): a nested non-partial generic declaration is never generated or registered,
        // so it must also be excluded from runtime-key collision checking. Counting it would ① cost a valid construction with the same key
        // its carrier via a false-positive MSGPROT015, and ② emit the rejected declaration's carrier, raising CS0311.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            public class GenericNestOuter
            {
                [Message(MessageKind.Standalone, 5)]
                [GenericMessage(typeof(NestedBox<int>), ClassId = 1)]
                public partial class NestedBox<T> { public T? Value { get; set; } }
            }

            [Message(MessageKind.Standalone, 5)]
            [GenericMessage(typeof(TopBox<int>), ClassId = 1)]
            public partial class TopBox<T> { public T? Value { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT002");       // only NestedBox violates the nested partial rule
        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT015"); // no false positive for the TopBox construction
        Assert.Contains("RegisterGenericConstruction<global::TestNs.TopBox<int>>(1)", generated);
        Assert.DoesNotContain("RegisterGenericConstruction<global::TestNs.GenericNestOuter.NestedBox<int>>", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void pure_carrier_referencing_a_cross_assembly_construction_gets_no_false_MSGPROT008()
    {
        // KI-43 second round unification regression (blocker): if the carrier guard reuses the generic gate verdict without a
        // "source-declared only" condition, a metadata-only declaration (referenced assembly PE — no DeclaringSyntaxReferences → IsPartial=false)
        // is misjudged as "will not be generated", breaking the standard protocol DLL (declaration+generation) + game DLL (pure carrier)
        // layout (KI-42) with MSGPROT008. External declarations were already validated by the origin compilation's gate; cross-assembly
        // duplicates follow ADR-0005's runtime detection contract — pinned via a two-compilation replay (base PE emit).
        var (diagnostics, generated, compileErrors, _) = RunGeneratorWithMetadataBase("""
            using MessageProtocol;
            namespace ProtocolShared
            {
                [Message(MessageKind.Standalone, 560)]
                [GenericMessage(typeof(ProtoBox<int>), ClassId = 1)]
                public partial class ProtoBox<T> { public T? Value { get; set; } }
            }
            """, Header + """
            [GenericMessage(typeof(ProtocolShared.ProtoBox<int>), ClassId = 7)]
            static class GameCarrier { }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));                                  // no false 008/015
        Assert.Contains("RegisterGenericConstruction<global::ProtocolShared.ProtoBox<int>>(7)", generated); // pure carrier emitted
        Assert.Empty(compileErrors);                                                                          // no CS0311 (the base PE provides the implementation)
    }

    [Fact]
    public void MSGPROT017_hash_zero_child_is_excluded_from_collision_checking()
    {
        // KI-43 second round regression (contract pin + reverse guard): "aacN86426" and "aafw42693" both have FNV-1a 24-bit hash == 0 (found by offline search).
        // Both are rejected by MSGPROT017 — if the gate counted them, 014/016 noise would make each look like the other's peer.
        // Generate returns at the 017 checkpoint before collision checking, and no valid type with a value-0 Child assembled id can exist
        // (manual 0 is unrepresentable, hash 0 = rejected), so this test passes even before the fix — a contract pin, not a tooth.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation("""
            using MessageProtocol;

            [Message(MessageKind.Parent, 1)]
            public partial class HashZeroRoot { public int X { get; set; } }

            [Message]
            public partial class aacN86426 : HashZeroRoot { public int Y { get; set; } }

            [Message]
            public partial class aafw42693 : HashZeroRoot { public int Z { get; set; } }
            """);

        var rejected = diagnostics.Where(d => d.Id == "MSGPROT017").ToArray();
        Assert.Equal(2, rejected.Length);                              // both rejected for hash 0
        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT014" || d.Id == "MSGPROT016");
        Assert.DoesNotContain("partial class aacN86426", generated);
        Assert.DoesNotContain("partial class aafw42693", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT015_different_generic_declarations_using_the_same_message_id_and_class_id_are_rejected_at_compile_time()
    {
        // Audit ledger MEDIUM (2026-09-06) regression: two different generic declarations using the same MessageId value + the same ClassId
        // share the runtime key (MessageId, ClassId), colliding RegisterGenericReaderInvoker in the module initializer and turning into
        // TypeInitializationException (assembly load failure). The (Declaration, ClassId) key collision check treated different declarations
        // as different keys and missed this shape. Reproduced against the pre-fix generator in a consumer project experiment.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 7)]
            [GenericMessage(typeof(GenA<int>), ClassId = 1)]
            public partial class GenA<T> { public T? Value { get; set; } }

            [Message(MessageKind.Standalone, 7)]
            [GenericMessage(typeof(GenB<int>), ClassId = 1)]
            public partial class GenB<T> { public T? Value { get; set; } }
            """ + Footer);

        var reported = diagnostics.Where(d => d.Id == "MSGPROT015").ToArray();
        Assert.Equal(2, reported.Length);
        Assert.All(reported, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.Contains("GenB<T>", reported[0].GetMessage());   // the counterpart declaration's qualified name
        Assert.Contains("GenA<T>", reported[1].GetMessage());
        Assert.Contains("0x00000007", reported[0].GetMessage()); // the assembled runtime key's hex MessageId

        // No registration carrier is generated (the colliding registration would break module load).
        // ("RegisterGenericConstruction" also appears in the unregistered-construction guidance exception message, so detect by carrier class name.)
        Assert.DoesNotContain("__GenericConstructionRegistration", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT015_different_class_id_same_message_id_value_does_not_collide()
    {
        // Reverse guard: the runtime key is the (MessageId, ClassId) pair — different ClassIds coexist.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 7)]
            [GenericMessage(typeof(GenA<int>), ClassId = 1)]
            public partial class GenA<T> { public T? Value { get; set; } }

            [Message(MessageKind.Standalone, 7)]
            [GenericMessage(typeof(GenB<int>), ClassId = 2)]
            public partial class GenB<T> { public T? Value { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT015");
        Assert.Contains("RegisterGenericConstruction", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT015_different_message_id_value_same_class_id_does_not_collide()
    {
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 7)]
            [GenericMessage(typeof(GenA<int>), ClassId = 1)]
            public partial class GenA<T> { public T? Value { get; set; } }

            [Message(MessageKind.Standalone, 8)]
            [GenericMessage(typeof(GenB<int>), ClassId = 1)]
            public partial class GenB<T> { public T? Value { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT015");
        Assert.Contains("RegisterGenericConstruction", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT015_multiple_constructions_of_a_single_declaration_do_not_collide()
    {
        // Reverse guard: multiple constructions of one declaration (different type arguments) share the same MessageId but are
        // distinguished by ClassId — a normal usage shape. (Same declaration + same ClassId duplication is caught by the existing MSGPROT008.)
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 7)]
            [GenericMessage(typeof(GenA<int>), ClassId = 1)]
            [GenericMessage(typeof(GenA<string>), ClassId = 2)]
            public partial class GenA<T> { public T? Value { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT015");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(generated, "RegisterGenericConstruction<").Count);
        Assert.Empty(compileErrors);
    }

    /// <summary>Extracts member names from `writer.Write*(message.Member)` calls in generated code, in order — the wire write order.</summary>
    static IReadOnlyList<string> ExtractWriteOrder(string generated)
    {
        return System.Text.RegularExpressions.Regex
            .Matches(generated, @"writer\.Write\w+\(message\.(\w+)")
            .Select(static match => match.Groups[1].Value)
            .ToList();
    }

    [Fact]
    public void generated_text_per_file_is_unchanged_by_unrelated_edits()
    {
        // Pins the KI-3 property at the **driver level**. Measurement (Known-Issues KI-10): the incremental pipeline's output step
        // re-runs on every edit — the `Compilation` step is always Modified, and `ForAttributeWithMetadataName`'s transform output
        // lacks value equality because symbols are per-compilation instances (`SourceOutput -> Modified`).
        // Still, if the generated **text** is identical, Roslyn's output comparison blocks downstream work so the generated tree
        // is not replaced or recompiled — before KI-3 (global counter removal) the text changed every time, making that shield useless.
        string source = Header + """
            [Message(MessageKind.Standalone, 1)]
            public partial class IncrementalMsgA
            {
                public int X { get; set; }
                public System.Collections.Generic.List<int>? Values { get; set; }
                public IncrementalMsgA? Next { get; set; }
            }

            [Message(MessageKind.Standalone, 2)]
            public partial class IncrementalMsgB { public string? Text { get; set; } }

            public static class IncrementalUnrelated
            {
                public static int Compute(int value) => value + 1;
            }
            """ + Footer;

        var compilation = CreateTpaCompilation(source);
        var driver = CSharpGeneratorDriver.Create(
            new[] { new MessageCodeGenerator().AsSourceGenerator() },
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, true));
        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
        var firstRun = driver.GetRunResult().Results.Single();

        // Unrelated edit: change only the body of a non-message class.
        var edited = compilation.ReplaceSyntaxTree(
            compilation.SyntaxTrees.Single(),
            CSharpSyntaxTree.ParseText(source.Replace("value + 1", "value + 2")));
        var secondRun = ((CSharpGeneratorDriver)driver.RunGenerators(edited)).GetRunResult().Results.Single();

        var first = GeneratedByHintName(firstRun);
        var second = GeneratedByHintName(secondRun);

        // Confirm real generated output exists first, so the comparison cannot pass vacuously.
        Assert.Equal(2, first.Count);
        Assert.All(first.Values, text => Assert.Contains("__WritePayload", text));

        Assert.Equal(
            first.Keys.OrderBy(static key => key, StringComparer.Ordinal),
            second.Keys.OrderBy(static key => key, StringComparer.Ordinal));
        foreach (var (hintName, text) in first)
        {
            Assert.Equal(text, second[hintName]);
        }
    }

    static Dictionary<string, string> GeneratedByHintName(GeneratorRunResult runResult)
    {
        return runResult.GeneratedSources.ToDictionary(
            static source => source.HintName,
            static source => source.SourceText.ToString());
    }

    static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    // ---------- Hint name collisions (KI-40) ----------

    // Back when `+` in nested type hint names was replaced with `_`, `Ns.A+B` (nested) and a real `Ns.A_B` type produced the
    // same hint name, AddSource threw a duplicate exception — AD0001, losing the compilation's entire generated source.
    // `+` is now preserved (impossible in an identifier), making the collision structurally impossible.
    [Fact]
    public void nested_type_and_underscore_joined_top_level_type_are_generated_independently()
    {
        const string source = """
            using MessageProtocol;

            namespace Ns
            {
                [Message(MessageKind.Standalone, 201)]
                public partial class Outer
                {
                    public int Value { get; set; }

                    [Message(MessageKind.Standalone, 202)]
                    public partial class Inner
                    {
                        public int Value { get; set; }
                    }
                }

                [Message(MessageKind.Standalone, 203)]
                public partial class Outer_Inner
                {
                    public int Value { get; set; }
                }
            }
            """;

        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(source);

        // All three types are generated without AD0001 (generator abort).
        Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001");
        Assert.Contains("class Outer", generated);
        Assert.Contains("Outer.Inner", generated);
        Assert.Contains("Outer_Inner", generated);
        Assert.Empty(compileErrors);
    }

    // ---------- Error-type ClassId diagnostics (metadata audit FINDING 2) ----------

    // `ClassId = <error>` (e.g. an undeclared constant) already produces a first-line compiler error at the attribute use site —
    // the generator now skips appending the secondary "missing 'ClassId'" misguidance it used to add when classId=0 slipped through.
    [Fact]
    public void error_type_class_id_expression_does_not_append_missing_class_id_misguidance()
    {
        const string source = """
            using MessageProtocol;

            [Message(MessageKind.Standalone, 210)]
            [GenericMessage(typeof(Ns.ErrorCarrier.Box<int>), ClassId = UNDECLARED_CONSTANT)]
            public partial class ErrorCarrier
            {
                public int Value { get; set; }
            }

            namespace Ns
            {
                [Message(MessageKind.Standalone, 211)]
                public partial class Box<T>
                {
                    public T? Value { get; set; }
                }
            }
            """;

        var (diagnostics, _, compileErrors) = RunGeneratorWithCompilation(source);

        // The compiler's first-line error (CS0103) exists — the cause is there.
        Assert.Contains(compileErrors, d => d.Id == "CS0103");
        // The generator adds no misguidance.
        Assert.DoesNotContain(diagnostics, d => d.GetMessage().Contains("missing 'ClassId'"));
    }

    // ---------- Exotic consumer shape matrix (2026-09-08) ----------
    // Legal but rare member/type shapes — each must get a clean diagnostic (not AD0001) or normal generation.

    [Theory]
    [InlineData("public int[,] Grid { get; set; }", "MSGPROT006")]                       // rank-2 array
    [InlineData("public System.Span<int> Slice { get; set; }", "MSGPROT006")]            // ref struct member
    [InlineData("public (int A, string B)? Pair { get; set; }", "MSGPROT006")]           // tuple
    [InlineData("public System.IntPtr Handle { get; set; }", "MSGPROT006")]              // pointer-adjacent legal shape
    [InlineData("public System.Threading.Tasks.Task<int>? Task { get; set; }", "MSGPROT006")] // future-proofing member
    public void exotic_member_shapes_are_rejected_with_clean_diagnostics(string member, string expectedDiagnostic)
    {
        var (diagnostics, generated, _) = RunGeneratorWithCompilation(
            Header
            + """
            [Message(MessageKind.Standalone, 221)]
            public partial class ExoticMember
            {
            """
            + member
            + """
            }
            """
            + Footer);

        Assert.Contains(diagnostics, d => d.Id == expectedDiagnostic);
        Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001");
    }

    [Fact]
    public void mutable_struct_message_is_generated_normally()
    {
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 222)]
            public partial struct MutablePoint
            {
                public int X { get; set; }
                public int Y { get; set; }
            }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Empty(compileErrors);
        Assert.Contains("MutablePoint", generated);
    }

    [Fact]
    public void static_members_and_const_are_excluded_from_the_wire()
    {
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 223)]
            public partial class WithStatics
            {
                public const int Const = 5;
                public static int Static;
                public static int StaticProp => 7;
                public int Instance { get; set; }
            }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Empty(compileErrors);
        // The generated payload contains only instance members — static/const must not appear.
        int payloadStart = generated.IndexOf("WritePayload", StringComparison.Ordinal);
        string payload = payloadStart >= 0 ? generated[payloadStart..] : generated;
        Assert.DoesNotContain(".Const", payload);      // member-access qualified — avoids false matches on type names
        Assert.DoesNotContain(".Static", payload);
        Assert.Contains(".Instance", payload);
    }

    [Fact]
    public void MSGPROT016_message_hash_collision_is_rejected_at_compile_time()
    {
        // TestNs.C17819 / TestNs.C21964 — a pair with the same FullName FNV-1a 24-bit hash 0x1867E1 (found by offline search).
        // Rejected via diagnostic without automatic rehashing; guides toward renaming or switching to an explicit attribute.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message]
            public partial class C17819 { public int X { get; set; } }

            [Message]
            public partial class C21964 { public int Y { get; set; } }
            """ + Footer);

        var reported = diagnostics.Where(d => d.Id == "MSGPROT016").ToArray();
        Assert.Equal(2, reported.Length);
        Assert.All(reported, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.Contains("Rename", reported[0].GetMessage());
        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT014");   // reported under the [Message]-only diagnostic

        // Neither is generated (neither can be registered).
        Assert.DoesNotContain("partial class C17819", generated);
        Assert.DoesNotContain("partial class C21964", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT017_message_element_with_hash_zero_is_rejected()
    {
        // "Zq4197766" (global namespace) has FNV-1a 24-bit hash == 0 (found by offline search).
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation("""
            using MessageProtocol;

            [Message(MessageKind.Parent, 1)]
            public partial class ZRoot { public int X { get; set; } }

            [Message]
            public partial class Zq4197766 : ZRoot { public int Y { get; set; } }
            """);

        var reported = diagnostics.Where(d => d.Id == "MSGPROT017").ToArray();
        Assert.Single(reported);
        Assert.Equal(DiagnosticSeverity.Error, reported[0].Severity);
        Assert.Contains("Zq4197766", reported[0].GetMessage());
        Assert.DoesNotContain("partial class Zq4197766", generated);
        Assert.Empty(compileErrors);
    }

    static string ExtractMessageId(string generated)
    {
        var match = Regex.Match(generated, @"MessageId => (\d+);");
        Assert.True(match.Success, "generated code has no MessageId constant");
        return match.Groups[1].Value;
    }

    [Fact]
    public void standalone_kind_with_omitted_id_produces_the_same_fullname_hash_message_id_as_automatic()
    {
        // An explicit kind with an omitted (0) id must hash via the same path as Automatic inference (MessageIdHash.FromFullName).
        var (standaloneDiagnostics, standaloneGenerated, standaloneErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone)]
            public partial class HashTarget { public int X { get; set; } }
            """ + Footer);

        var (messageDiagnostics, messageGenerated, messageErrors) = RunGeneratorWithCompilation(Header + """
            [Message]
            public partial class HashTarget { public int X { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(standaloneDiagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Empty(standaloneErrors);
        Assert.Empty(messageErrors);

        // Same FullName → same hash → same wire MessageId ([Message] Automatic infers Standalone since nothing is derived).
        Assert.Equal(ExtractMessageId(standaloneGenerated), ExtractMessageId(messageGenerated));
    }

    [Fact]
    public void automatic_kind_with_manual_id_carries_the_manual_id_in_the_inferred_kind()
    {
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(id: 300)]
            public partial class AutoManualId { public int X { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Empty(compileErrors);
        // Standalone(2)<<4 | 0, id 300 → 0x2000012C = 536871212.
        Assert.Equal("536871212", ExtractMessageId(generated));
        Assert.Contains("writer.WriteByte(0x20);", generated);
    }

    [Fact]
    public void child_id_zero_is_interpreted_as_hash_not_manual_zero()
    {
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Parent, 10)]
            public partial class ZeroIdRoot { public int X { get; set; } }

            [Message(MessageKind.Child, 0)]
            public partial class ZeroIdChild : ZeroIdRoot { public int Y { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Empty(compileErrors);
        // id 0 means 'omitted' — the FullName hash is used (MSGPROT017 if the hash is 0); manual 0 is not representable.
        Assert.DoesNotContain("MessageId => 0;", generated);
        Assert.Contains("writer.WriteByte(0x80);", generated);   // Child(8)<<4 | 0
    }

    [Fact]
    public void manual_id_and_category_constructor_args_are_reflected_in_the_wire_message_id()
    {
        // The (uint id, MessageCategory category) overload — compatible with existing manual id assignment + the category nibble.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 5, MessageCategory.Category3)]
            public partial class ManualWithCategory { public int X { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Empty(compileErrors);
        Assert.Contains("MessageId => 587202565;", generated);   // 0x23000005: Standalone(2)<<4 | 3, id 5
        Assert.Contains("writer.WriteByte(0x23);", generated);
    }

    [Fact]
    public void category_only_constructor_combines_the_hash_id_with_the_category()
    {
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Parent, category: MessageCategory.Category2)]
            public partial class CatRoot { public int X { get; set; } }

            [Message(MessageKind.Child)]
            public partial class CatChild : CatRoot { public int Y { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Empty(compileErrors);
        // Root header: GroupRoot(4)<<4 | 2 = 0x42. The hash id is not 0 (a GroupElement with hash 0 is rejected by MSGPROT017).
        Assert.Contains("writer.WriteByte(0x42);", generated);
        Assert.DoesNotContain("MessageId => 0;", generated);
    }
}
