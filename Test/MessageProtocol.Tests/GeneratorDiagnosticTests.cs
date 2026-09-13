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

/// <summary>생성기 진단(MSGPROT001–006)과 정상 생성을 GeneratorDriver 로 검증한다.</summary>
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

    /// <summary>테스트 런타임 TPA 참조로 소스 컴파일을 만든다 (생성기 내부 구동 테스트 공용).</summary>
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
    public void MSGPROT001_partial_아닌_메시지()
    {
        var (diagnostics, _) = RunGenerator(Header + """
            [Message(MessageKind.Standalone, 1)]
            public class NotPartial { public int X { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT001");
    }

    [Fact]
    public void MSGPROT002_컨테이닝_타입이_partial_아님()
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
    public void MSGPROT003_요소_메시지에_루트가_없음()
    {
        var (diagnostics, _) = RunGenerator(Header + """
            [Message(MessageKind.Child, 1)]
            public partial class OrphanElement { public int X { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT003");
    }

    [Fact]
    public void MSGPROT004_루트의_부모가_루트()
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
    public void MSGPROT005_ID_범위_초과()
    {
        var (diagnostics, _) = RunGenerator(Header + """
            [Message(MessageKind.Standalone, 16777216)]
            public partial class TooBig { public int X { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT005");
    }

    [Fact]
    public void MSGPROT006_미지원_멤버_타입()
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
    public void MSGPROT018_NonId에_id_인자를_주면_거부된다()
    {
        // NonId 헤더는 1바이트 — id 슬롯이 없다.
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
    public void MSGPROT018_NonId에_category_인자를_주면_거부된다()
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
    public void MSGPROT018_정의되지_않은_MessageKind_값은_거부된다(int kindValue)
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
    public void 제네릭_메시지_타입은_매개변수를_유지한_채_컴파일_가능한_코드를_생성한다()
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
    public void 제네릭_메시지_타입은_자동_등록_코드를_생성하지_않는다()
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
    public void GenericMessage_구성_선언은_자동_등록_클래스를_생성한다()
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
        // 클래스 ID는 런타임 레지스트리 조회 (내부 필드 미사용).
        Assert.Contains("MessageSerializer.GetGenericClassId<Box<T>>()", generated);
        Assert.DoesNotContain("__GenericClassId", generated);
        // 제네릭 헤더 플래그 0: MessageId 구성에 제네릭 플래그가 쓰인다.
        Assert.Contains("MessageId => 2;", generated);
    }

    [Fact]
    public void MSGPROT008_제네릭이_아닌_타입에_GenericMessage를_붙이면_에러()
    {
        var (diagnostics, _) = RunGenerator(Header + """
            [Message(MessageKind.Standalone, 1)]
            [GenericMessage(typeof(int), ClassId = 1)]
            public partial class NotGeneric { public int X { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT008");
    }

    [Fact]
    public void MSGPROT008_미바운드_제네릭_구성_선언은_에러()
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
    public void MSGPROT008_제네릭_선언에_StandaloneMessage가_없으면_에러()
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
    [InlineData(16777216u)]    // 2^24 — 24비트 와이어 슬롯을 넘는 첫 값
    [InlineData(4294967295u)]  // uint.MaxValue
    public void MSGPROT008_ClassId_상한_초과는_컴파일_진단으로_거부된다(uint classId)
    {
        // KI-27 회귀: 상한 미검증이라 생성기를 통과하고, 생성된 등록 캐리어가 **모듈 이니셜라이저**에서
        // RegisterGenericConstruction 의 ArgumentOutOfRangeException 을 터뜨려 TypeInitializationException(어셈블리 로드 실패)이 된다.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + $$"""
            [Message(MessageKind.Standalone, 1)]
            [GenericMessage(typeof(Box<int>), ClassId = {{classId}})]
            public partial class Box<T> { public T? Value { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT008");
        // `<` 까지 봐야 한다 — 생성 `Serialize` 의 안내 예외 메시지도 "RegisterGenericConstruction" 이라는 단어를 포함한다.
        Assert.DoesNotContain("RegisterGenericConstruction<", generated);
        Assert.Empty(compileErrors);
    }

    [Theory]
    [InlineData(1u)]          // 최소 허용
    [InlineData(16777215u)]   // 최대 허용 (2^24 - 1)
    public void ClassId_경계값은_진단_없이_등록_코드를_생성한다(uint classId)
    {
        // 역방향 가드: 상한 검증을 넣으면서 정상 범위(특히 경계값)를 잘라내면 안 된다.
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
    public void 추상_그룹_루트의_파생_요소는_new_수식어_없이_생성된다()
    {
        // KI-28 회귀: abstract [Message(MessageKind.Parent)] 는 상속 전용이라 정적 계약을 방출하지 않는데,
        // 파생 요소에 `new` 를 붙이니 가릴 멤버가 없어 소비자 빌드에 CS0109 가 떴다
        // (클린 리빌드 기준 이 저장소에서만 64건 — 다형 그룹은 KI-24 이후 정상 사용 패턴이라 소비자도 동일하게 밟는다).
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
    public void 구체_그룹_루트의_파생_요소는_new_수식어를_유지한다()
    {
        // 역방향 가드: 베이스가 실제로 정적 계약(MessageId·Deserialize 등)을 방출하면 `new` 가 필요하므로
        // (CS0108 방지) 유지해야 한다 — CS0109 를 없앤다고 `new` 를 일괄 제거하면 이쪽이 깨진다.
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
    public void 다른_어셈블리의_추상_그룹_루트에서_파생된_요소는_new_수식어_없이_생성된다()
    {
        // KI-28 교차 어셈블리 꼬리: 프로토콜 DLL(abstract 루트) + 서버/클라이언트 DLL(구체 요소) 분리는
        // 상용 프로젝트의 표준 구성이다. 메타데이터 베이스는 구문 참조가 없어 partial 판정을 못 하므로
        // 기존 구현은 무조건 `new` 를 붙였는데 — abstract 여부는 메타데이터만으로 확정되고, abstract 메시지
        // 타입은 절대 정적 계약을 방출하지 않으므로(그룹 루트 skip·MSGPROT010) CS0109 ×6/타입이 확정된다.
        // TreatWarningsAsErrors 소비자는 빌드 실패, 아니어도 클린 리빌드마다 경고가 쌓인다.
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
    public void 다른_어셈블리의_구체_그룹_루트에서_파생된_요소는_new_수식어를_유지한다()
    {
        // 역방향 가드(교차 어셈블리): 구체 메타데이터 베이스는 그쪽 컴파일에서 생성됐는지 여기를 알 수
        // 없어 기존대로 `new` 를 유지한다 — 잘못 내리면 CS0108/CS0114 로 역전한다.
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
    public void IVT로_internal에_접근_가능한_교차_어셈블리_베이스의_Initialize는_new_수식어를_유지한다()
    {
        // KI-42 IVT 고리: 프로토콜 DLL 이 [InternalsVisibleTo("소비자")] 로 internal 을 열면
        // 베이스의 internal Initialize 는 접근 가능해져 가릴 대상이 되돌아온다 — 이때 `new` 를 빼면
        // 사용자가 수정할 수 없는 CS0108 이 생성 코드에 뜬다(TreatWarningsAsErrors 소비자는 빌드 실패).
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
    public void IVT가_없는_교차_어셈블리_베이스의_Initialize는_new_수식어를_생략한다()
    {
        // 비-IVT 역방향 가드: 접근이 닫힌 internal 베이스 Initialize 는 가릴 대상이 아니므로 `new` 를
        // 붙이면 CS0109 — 기존 KI-42 동작(생략)이 IVT 분기 도입으로 퇴행하지 않았음을 고정한다.
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
    /// 별도 어셈블리로 베이스를 컴파일(PE 메모리 방출)해 소비 컴파일이 메타데이터 베이스로 상속받게 한다 —
    /// 교차 어셈블리 상속(프로토콜 DLL + 프로젝트 DLL) 시나리오. 경고도 함께 반환한다(CS0109 검증용).
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
    public void 이미트_순서와_횟수에_관계없이_같은_타입은_같은_생성_텍스트를_낸다()
    {
        // KI-3 회귀: 생성 로컬 이름 번호(`__item3` 등)가 프로세스 전역 정적 카운터였을 때는
        // 두 번째 이미트가 다른 번호를 받아 **동일 입력 → 다른 텍스트**가 됐다. 그 비결정성은
        // Roslyn 의 생성 출력 비교를 매번 깨뜨려 무관한 편집에도 생성 트리가 교체·재컴파일되게 하고,
        // 빌드 재현성·diff 판독성도 해친다. 이제 번호는 EmitState(이미트 단위) 상태라 입력에만 의존한다.
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

        // A → B → A → B 순서로 두 번씩 이미트: 전역 카운터였다면 두 번째 A/B 는 번호가 밀려 다르다.
        string firstA = EmitFor(compilation, "TestNs.DeterminismMessage", attributeReferences);
        string firstB = EmitFor(compilation, "TestNs.OtherDeterminismMessage", attributeReferences);
        string secondA = EmitFor(compilation, "TestNs.DeterminismMessage", attributeReferences);
        string secondB = EmitFor(compilation, "TestNs.OtherDeterminismMessage", attributeReferences);

        // 번호를 실제로 쓰는 로컬이 여럿 있는지 먼저 확인 — 빈 텍스트 비교로 검증이 vacuous 해지지 않게.
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
    public void MSGPROT012_구체_베이스_멤버는_파생_멤버_유실을_경고한다()
    {
        // KI-29: 파생 메시지 타입이 있는 **구체** 메시지 베이스를 멤버 정적 타입으로 쓰면 선언 타입 기준으로
        // 직렬화되어 파생 멤버가 조용히 사라진다(실행 확인: LoginEvent.User 유실, 복원 타입은 EventBase).
        // 동작 자체는 유효하므로 생성은 막지 않고 경고로 알린다.
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
        // 경고일 뿐 — 생성은 정상적으로 이뤄지고 에러 진단·컴파일 오류도 없다.
        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT") && d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("__WritePayload", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT012_컬렉션_요소_타입도_경고한다()
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
    public void MSGPROT012_추상_루트_멤버는_경고하지_않는다()
    {
        // 역방향 가드: 추상 메시지 타입 멤버는 런타임 디스패치로 구체 요소가 헤더째 기록되므로(KI-24)
        // 유실이 없다 — 지원되는 다형 패턴에 경고를 뿌리면 안 된다.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Parent, 620)]
            public abstract partial class AbsRoot { public long Timestamp { get; set; } }

            [Message(MessageKind.Child, 621)]
            public partial class AbsElement : AbsRoot { public string? User { get; set; } }

            [Message(MessageKind.Standalone, 622)]
            public partial class AbsHost { public AbsRoot? Event { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT012");
        Assert.Contains("SerializeToWriter", generated);   // 디스패치 경로 확인
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT012_파생_메시지가_없는_구체_타입_멤버는_경고하지_않는다()
    {
        // 역방향 가드: 파생 메시지 타입이 없는 구체 타입 멤버(일반 중첩 페이로드)는 손실이 없으므로 조용해야 한다.
        var (diagnostics, _, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 630)]
            public partial class PlainPayload { public int X { get; set; } }

            [Message(MessageKind.Standalone, 631)]
            public partial class PlainHost { public PlainPayload? Payload { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT012");
        Assert.Empty(compileErrors);
    }

    /// <summary>지정 타입에 대해 이미터를 한 번 구동해 생성 텍스트를 반환한다(매 호출이 새 EmitState).</summary>
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
    public void 분산_선언_캐리어는_구성_등록_코드를_생성한다()
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
    public void MSGPROT008_한_컴파일에서_같은_구성을_두_번_선언하면_에러()
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
    public void MSGPROT008_분산_선언_구성이_제네릭_메시지가_아니면_에러()
    {
        var (diagnostics, _) = RunGenerator(Header + """
            [GenericMessage(typeof(int), ClassId = 1)]
            static class Carrier { }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT008");
    }

    [Fact]
    public void MSGPROT008_분산_선언에_ClassId가_없으면_에러()
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
    public void 정상_타입은_등록_코드를_생성한다()
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
    public void NonId_타입은_NonId_등록을_생성한다()
    {
        var (diagnostics, generated) = RunGenerator(Header + """
            [Message(MessageKind.NonId)]
            public partial class NoId { public byte B { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Contains("RegisterNonIdMessage<NoId>", generated);
    }

    [Fact]
    public void abstract_그룹_루트는_생성을_건너뛴다()
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
    public void 그룹_계층_요소는_루트_멤버를_포함한다()
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
    public void 다른_네임스페이스의_동명_타입도_충돌없이_모두_생성된다()
    {
        // 힌트 이름이 단순 타입 이름만 쓰면 AddSource 가 ArgumentException 을 던져 전체 생성이 유실된다.
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
    public void 중첩_타입과_네임스페이스_점이_동일한_모양이어도_충돌하지_않는다()
    {
        // 네임스페이스 A.B의 클래스 C → 'A.B.C', 네임스페이스 A의 중첩 B.C → 'A.B+C'.
        // 중첩 구분자가 '.' 이면 두 힌트 이름이 충돌해 전체 생성이 유실된다.
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
    public void CollectionsMarshal_미지원_타깃의_List_벌크_판독도_개수_곱하기_요소크기를_검증한다()
    {
        // KI-17 회귀: CollectionsMarshal 이 없는 타깃(예: netstandard2.0 소비자)의 요소별 판독 경로도
        // List 사전 할당 전에 개수×요소크기 ≤ 남은 바이트 를 검증해야 한다 (개수만 검증하면 8배 선할당 강요).
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
    public void MSGPROT010_추상_메시지_타입은_생성_거부()
    {
        var (diagnostics, generated) = RunGenerator(Header + """
            [Message(MessageKind.NonId)]
            public abstract partial class AbstractMessage { public int X { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT010");
        Assert.DoesNotContain("__WritePayload", generated);
    }

    [Fact]
    public void MSGPROT010_포지셔널_레코드_메시지는_생성_거부()
    {
        var (diagnostics, generated) = RunGenerator(Header + """
            [Message(MessageKind.NonId)]
            public partial record PointRecord(int X);
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT010");
        Assert.DoesNotContain("__WritePayload", generated);
    }

    [Fact]
    public void MSGPROT011_읽기_전용_멤버는_생성_거부()
    {
        var (diagnostics, generated) = RunGenerator(Header + """
            [Message(MessageKind.NonId)]
            public partial class GetOnlyMessage { public int X { get; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT011");
        Assert.DoesNotContain("__WritePayload", generated);
    }

    [Fact]
    public void 같은_멤버가_두_규칙을_위반하면_두_진단이_모두_보고된다()
    {
        // 감사 원장 LOW(2026-09-08) 회귀: 진단 중복제거 키가 이름+타입만이라 미지원 타입(MSGPROT006)이면서
        // 읽기 전용(MSGPROT011)인 멤버는 두 번째 규칙이 조용히 유실됐다 — 키에 사유(kind)·위치를 포함해
        // 같은 멤버의 같은 규칙 반복만 제거되도록 수정했다.
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
    public void 서로_다른_규칙_위반_멤버_2개는_진단_2건을_낸다()
    {
        // 역방향 가드: 멤버별로 각각 한 규칙씩 위반하면 두 위치 모두 보고된다.
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
    public void MSGPROT006_생성_불가_페이로드_멤버는_미지원_타입_진단()
    {
        // 추상 클래스·포지셔널 레코드 페이로드는 기본 생성자로 인스턴스를 만들 수 없어 멤버 단위 진단으로 거부한다.
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
    public void MSGPROT011_페이로드의_읽기_전용_멤버는_생성_거부()
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
    public void 동명_중첩_구성_캐리어도_유일한_등록_클래스를_생성한다()
    {
        // KI-19 회귀: 같은 네임스페이스의 동명 중첩 캐리어 두 개가 충돌 없이 각각 유일한 등록 클래스를 방출한다.
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
    public void 공개_인덱서는_직렬화_멤버에서_제외된다()
    {
        // KI-23 회귀: 인덱서도 `IPropertySymbol`(Name = "this[]")이라 멤버로 뽑히면 `message.this[]` 같은
        // 문법 오류 코드가 진단 없이 생성되어 소비자 빌드가 깨진다.
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
    public void 추상_메시지_타입_멤버는_위임_대신_런타임_디스패치를_생성한다()
    {
        // KI-24 회귀: abstract [Message(MessageKind.Parent)] 는 다형 그룹의 자연스러운 선언이지만 생성기는 인스턴스를
        // 만들 수 없어 정적 Serialize/Deserialize 를 방출하지 않는다 — 위임 코드는 진단 없이 소비자 빌드를
        // CS0117('AbstractEvent'에 'Serialize' 정의가 없음)로 깨뜨렸다.
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
        Assert.Contains("if (!(__dispatched", generated); // KI-41: 디스패치 복원 객체의 안내 타입 검사
        Assert.Contains("(global::TestNs.AbstractEvent)__dispatched", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void 그래프_밖_구체_메시지_멤버는_정적_위임을_유지한다()
    {
        // KI-24 수정의 역방향 가드: 비공개 매개변수 없는 생성자 때문에 그래프에서 빠진 *구체* 메시지 타입은
        // 여전히 생성 정적 멤버를 가지므로 정적 위임이 맞다 (런타임 디스패치로 과도하게 돌리면 안 된다).
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
    public void 와이어_멤버_순서는_베이스_선언순서와_그림자_제거_위치를_고정한다()
    {
        // KI-4 회귀: 페이로드 바이트 순서는 송수신이 반드시 일치해야 하는 와이어 형식인데, 이전에는
        // `Dictionary.Values` 열거 순서(삽입 순서일 뿐 규약이 아닌 BCL 구현 세부)에 얹혀 있었다.
        // 이제 `TypeMetadata.GetWireMembers` 가 명시적으로 고정하며, 이 테스트가 그 레이아웃을 못박는다.
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
        // 베이스 선언 순서 먼저, 파생 고유 멤버 나중.
        Assert.Equal(
            new[] { "BaseFirst", "Shadowed", "BaseLast", "DerivedOwn" },
            ExtractWriteOrder(code!));
        // 그림자 제거된 멤버는 **베이스 위치**를 유지한 채 파생 타입(long)으로 기록된다.
        Assert.Contains("writer.WriteInt64(message.Shadowed)", code);
        Assert.DoesNotContain("writer.WriteString(message.Shadowed)", code);
    }

    [Fact]
    public void 중첩_페이로드_헬퍼의_멤버_순서도_같은_규칙을_쓴다()
    {
        // 이미터(루트 페이로드)와 그래프(중첩 페이로드 헬퍼)가 예전에는 동일한 병합 로직을 각각 갖고 있었다.
        // 한 구현(`TypeMetadata.GetWireMembers`)을 공유하므로 중첩 헬퍼의 바이트 순서도 같은 규칙임을 고정한다.
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

        // 중첩 페이로드 기록 헬퍼(시그니처가 `NestedOrderDerived message`) 이후의 기록 순서만 잘라 검증한다.
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
    public void 컬렉션_쓰기는_멤버를_로컬로_스냅샷한다(bool hasCollectionsMarshal)
    {
        // KI-26 회귀: 길이 접두·루프 조건·요소 접근이 각자 `message.Values` 를 다시 평가하면 게터가 2N+2회 돌고,
        // 계산형 프로퍼티에서는 길이와 요소가 서로 다른 인스턴스에서 나와 프레임이 스스로 모순된다.
        // hasCollectionsMarshal=false 는 Unity/netstandard2.1 소비자 경로라 이 저장소에서는 실행되지 않음 → 생성 텍스트로 고정.
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

        // 멤버 표현식은 스냅샷 한 곳에서만 등장한다 — null 판정도 스냅샷 로컬로 하므로
        // 계산형 프로퍼티가 두 번째 평가에서 null 을 돌려줘도 NRE 가 나지 않는다(TOCTOU 차단).
        Assert.Equal(1, CountOccurrences(code!, "message.Values"));
        Assert.Equal(1, CountOccurrences(code!, "message.Names"));
        Assert.Equal(1, CountOccurrences(code!, "message.Tags"));
        Assert.DoesNotContain("if (message.Values is null)", code);

        Assert.Contains(hasCollectionsMarshal ? "var __list" : "var __coll", code);
        Assert.Contains("var __arr", code);      // 배열은 양쪽 구성 모두 스냅샷
    }

    [Theory]
    [InlineData(16)]
    [InlineData(99)]
    [InlineData(255)]
    public void MSGPROT013_범위_밖_카테고리는_컴파일_진단으로_거부된다(int categoryValue)
    {
        // KI-8 회귀: 이미터는 카테고리 값을 `& 0x0F` 로 **조용히 마스킹**했으므로 99 는 3 이 되어
        // 와이어 MessageId 가 개발자 의도와 달라졌다(진단 없음).
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + $$"""
            [Message(MessageKind.Standalone, 1, (MessageCategory){{categoryValue}})]
            public partial class BadCategory { public int X { get; set; } }
            """ + Footer);

        var diagnostic = Assert.Single(diagnostics, d => d.Id == "MSGPROT013");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(categoryValue.ToString(), diagnostic.GetMessage());
        Assert.DoesNotContain("__WritePayload", generated);   // 생성 건너뜀
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT013_마스킹이_만드는_MessageId_충돌을_컴파일에서_막는다()
    {
        // 실험으로 확인한 실제 형태: `(MessageCategory)99` 는 99 & 0x0F = 3 으로 마스킹되어 `Category3` 메시지와
        // **동일한 와이어 MessageId**(0x23000007) 를 만들고, 모듈 이니셜라이저에서 등록 충돌 예외
        // ("Message type with ID 587202567 is already registered by 'MaskedCategory'")로 어셈블리 로드가 실패했다.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 7, (MessageCategory)99)]
            public partial class MaskedCategory { public int X { get; set; } }

            [Message(MessageKind.Standalone, 7, MessageCategory.Category3)]
            public partial class RealCategory3 { public int X { get; set; } }
            """ + Footer);

        var diagnostic = Assert.Single(diagnostics, d => d.Id == "MSGPROT013");
        Assert.Contains("MaskedCategory", diagnostic.GetMessage());
        // 마스킹되지 않은 쪽은 정상 생성된다(과잉 차단 아님).
        Assert.Contains("RealCategory3", generated);
        Assert.DoesNotContain("partial class MaskedCategory", generated);
        Assert.Empty(compileErrors);
    }

    [Theory]
    [InlineData("MessageCategory.Category0", "0x20")]
    [InlineData("MessageCategory.Category15", "0x2F")]
    public void 카테고리_경계값은_허용되고_헤더_니블에_그대로_실린다(string categoryExpression, string expectedHeaderByte)
    {
        // 역방향 가드: 상한 검증을 넣으면서 정상 범위(특히 0·15)를 잘라내면 안 된다.
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
    public void MSGPROT014_동일_와이어_MessageId_두_메시지는_컴파일에서_거부된다()
    {
        // KI-31 회귀: id 충돌은 모듈 이니셜라이저의 `_registeredMessageIds` 에서만 발견되어
        // `InvalidOperationException: Message type with ID … is already registered by '…'` → TypeInitializationException
        // (어셈블리 로드 실패)이 되고, 오류 메시지는 상대 타입만 지목해 원인을 가리키지 않았다.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 7)]
            public partial class FirstMessage { public int X { get; set; } }

            [Message(MessageKind.Standalone, 7)]
            public partial class SecondMessage { public int Y { get; set; } }
            """ + Footer);

        // 두 타입 모두 자기 관점에서 상대를 지목받는다.
        var reported = diagnostics.Where(d => d.Id == "MSGPROT014").ToArray();
        Assert.Equal(2, reported.Length);
        Assert.All(reported, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.Contains("SecondMessage", reported[0].GetMessage());
        Assert.Contains("FirstMessage", reported[1].GetMessage());
        Assert.Contains("0x", reported[0].GetMessage());   // 충돌한 와이어 ID(16진)를 함께 알려준다

        // 둘 다 생성되지 않는다(등록될 수 없으므로).
        Assert.DoesNotContain("partial class FirstMessage", generated);
        Assert.DoesNotContain("partial class SecondMessage", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT014_카테고리가_다르면_같은_id_값도_충돌하지_않는다()
    {
        // 역방향 가드: 충돌 키는 속성 원값이 아니라 **조립된 와이어 MessageId**(flags+category+24비트 값)다.
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
    public void MSGPROT014_제네릭_선언은_같은_id_값이어도_충돌로_보지_않는다()
    {
        // 역방향 가드: 제네릭 구성의 런타임 키는 (MessageId, ClassId) 라 선언 id 값이 같아도 ClassId 가 다르면 공존한다.
        // (구성 간 충돌은 기존 `CollectConstructionConflicts` 가 담당한다.)
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
    public void MSGPROT014_추상_그룹_루트는_충돌_판정에서_제외된다()
    {
        // 역방향 가드: abstract 그룹 루트는 상속 전용이라 생성·등록되지 않으므로(생성기가 의도적으로 건너뜀)
        // 같은 id 값의 구체 루트와 충돌하지 않는다 — 등록될 타입만 세야 거짓 양성이 안 난다.
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
    public void MSGPROT014_계층_위반으로_거부될_타입과_같은_id여도_정상_타입은_거짓_양성을_받지_않는다()
    {
        // KI-43 회귀: MSGPROT003(루트 없는 요소)·MSGPROT004(루트의 루트 조상)으로 이미 거부될 타입은
        // 생성·등록되지 않으므로 충돌 판정에서 빠져야 한다(KI-31 "실제로 등록될 형태만 센다").
        // 빼놓으면 문제없는 상대 타입이 거짓 양성 MSGPROT014 로 생성이 막힌다.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Parent, 9)]
            public partial class RealRoot { public long Timestamp { get; set; } }

            [Message(MessageKind.Child, 7)]
            public partial class ValidChild : RealRoot { public int X { get; set; } }

            [Message(MessageKind.Child, 7)]
            public partial class OrphanChild { public int Y { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT003");       // OrphanChild 만 계층 위반
        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT014"); // ValidChild 거짓 양성 금지
        Assert.Contains("partial class ValidChild", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT014_MSGPROT018로_거부될_선언과_같은_id여도_정상_타입은_거짓_양성을_받지_않는다()
    {
        // KI-43 회귀: 정의 밖 kind 값((MessageKind)99)은 decode 가 실패해 TypeMetadata 가 Automatic
        // 폴백 추론을 하므로, 게이트가 018 검사를 건너뛰면 거부될 선언이 Standalone 으로 세 count 된다.
        // (NonId+id 조합은 decode 가 성공해 IsNonIdMessage 경로로 이미 제외된다 — 이 케이스가 진짜 갭이다.)
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message((MessageKind)99, id: 7)]
            public partial class BrokenKind { public int X { get; set; } }

            [Message(MessageKind.Standalone, 7)]
            public partial class OkStandalone { public int Y { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT018");       // BrokenKind 만 종류 위반
        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT014"); // OkStandalone 거짓 양성 금지
        Assert.Contains("partial class OkStandalone", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT015_MSGPROT018로_거부될_제네릭_선언과_같은_키여도_정상_구성은_거짓_양성을_받지_않는다()
    {
        // KI-43 회귀(제네릭 변형): 정의 밖 kind 의 제네릭 선언은 decode 실패→Automatic 폴백 Standalone 로
        // 세 count 되어 (MessageId, ClassId) 런타임 키 충돌 판정에 들어간다. 빼놓으면 정상 구성이
        // 거짓 양성 MSGPROT015 로 등록 캐리어까지 잃는다. (NonId+id 조합은 decode 성공→IsNonIdMessage 로 이미 제외.)
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message((MessageKind)99, id: 5)]
            [GenericMessage(typeof(BrokenGeneric<int>), ClassId = 1)]
            public partial class BrokenGeneric<T> { public T? Value { get; set; } }

            [Message(MessageKind.Standalone, 5)]
            [GenericMessage(typeof(OkGeneric<int>), ClassId = 1)]
            public partial class OkGeneric<T> { public T? Value { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT018");       // BrokenGeneric 만 종류 위반
        Assert.Contains(diagnostics, d => d.Id == "MSGPROT008");       // BrokenGeneric 구성의 캐리러 거부 진단
        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT015"); // OkGeneric 구성 거짓 양성 금지
        Assert.Contains("RegisterGenericConstruction<global::TestNs.OkGeneric<int>>(1)", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT002로_거부될_타입과_같은_id여도_정상_타입은_거짓_양성을_받지_않는다()
    {
        // KI-43 2차(리뷰 라운드 1) 회귀: 중첩 컨테이닝 타입이 non-partial 인 메시지는 MSGPROT002 로
        // 생성·등록되지 않으므로 충돌 판정에서 빠져야 한다. 게이트의 IsPartial 은 타입 자신만 봐서
        // 이 타입이 카운트되면 같은 조립 id 의 최상위 정상 타입이 거짓 양성 MSGPROT014 로 생성을 잃는다.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            public class NestOuter
            {
                [Message(MessageKind.Standalone, 7)]
                public partial class NestedStand { public int X { get; set; } }
            }

            [Message(MessageKind.Standalone, 7)]
            public partial class OkTopStand { public int Y { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT002");       // NestedStand 만 중첩 partial 위반
        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT014"); // OkTopStand 거짓 양성 금지
        Assert.Contains("partial class OkTopStand", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void 구성_선언이_MSGPROT005로_거부되면_캐리러도_방출되지_않는다()
    {
        // KI-43 2차 회귀: 캐리러 가드가 018 만 보면 id 범위 초과(MSGPROT005)로 거부될 선언의 캐리러가
        // 그래도 방출된다 — RegisterGenericConstruction<T> 가 IHasIdMessageSerializable<T> 미구현 타입을
        // 참조해 CS0311 컴파일 불가 생성 코드가 된다.
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Standalone, 99999999)]
            [GenericMessage(typeof(IdRangeBox<int>), ClassId = 1)]
            public partial class IdRangeBox<T> { public T? Value { get; set; } }
            """ + Footer);

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT005"); // 선언 위치 1차 진단
        Assert.Contains(diagnostics, d => d.Id == "MSGPROT008"); // 캐리러 거부 2차 진단
        Assert.DoesNotContain("RegisterGenericConstruction", generated); // CS0311 캐리러 방출 금지
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void 구성_선언이_partial이_아니면_캐리러도_방출되지_않는다()
    {
        // KI-43 2차 회귀: MSGPROT001 로 거부될 선언의 캐리러도 방출되면 안 된다(005 트리거와 같은 결함류).
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
    public void MSGPROT002로_거부될_제네릭_선언과_같은_키여도_정상_구성은_거짓_양성을_받지_않는다()
    {
        // KI-43 2차 회귀(제네릭 002 갭): 중첩 non-partial 제네릭 선언은 생성·등록되지 않으므로 런타임 키
        // 충돌 판정에서도 빠져야 한다. 세 count 되면 ① 같은 키의 정상 구성이 거짓 양성 MSGPROT015 로
        // 캐리러를 잃고 ② 거부될 선언의 캐리러가 방출되어 CS0311 이 난다.
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

        Assert.Contains(diagnostics, d => d.Id == "MSGPROT002");       // NestedBox 만 중첩 partial 위반
        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT015"); // TopBox 구성 거짓 양성 금지
        Assert.Contains("RegisterGenericConstruction<global::TestNs.TopBox<int>>(1)", generated);
        Assert.DoesNotContain("RegisterGenericConstruction<global::TestNs.GenericNestOuter.NestedBox<int>>", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void 크로스_어셈블리_구성_선언을_참조하는_순수_캐리러는_거짓_MSGPROT008을_받지_않는다()
    {
        // KI-43 2차 일원화 회귀(블로커): 캐리러 가드가 "소스 선언 한정" 조건 없이 제네릭 게이트 판정을 그대로
        // 쓰면, 메타데이터 전용 선언(참조 어셈블리 PE — DeclaringSyntaxReferences 없음 → IsPartial=false)이
        // "생성되지 않을 선언"으로 오판돼 프로토콜 DLL(선언+생성) + 게임 DLL(순수 캐리러) 표준 구성(KI-42)이
        // MSGPROT008 으로 깨진다. 외부 선언은 원 컴파일의 게이트가 이미 검증했고, 크로스 어셈블리 중복은
        // ADR-0005 의 런타임 감지 계약을 따른다 — 2컴파일레이션 재생(베이스 PE 방출)으로 고정한다.
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

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));                                  // 거짓 008·015 부재
        Assert.Contains("RegisterGenericConstruction<global::ProtocolShared.ProtoBox<int>>(7)", generated); // 순수 캐리러 방출
        Assert.Empty(compileErrors);                                                                          // CS0311 부재(베이스 PE 가 구현 제공)
    }

    [Fact]
    public void MSGPROT017_해시_0_Child는_충돌_판정에_들어가지_않는다()
    {
        // KI-43 2차 회귀(규약 고정·역방향 가드): "aacN86426"·"aafw42693"의 FNV-1a 24비트 == 0(오프라인 탐색).
        // 둘 다 MSGPROT017 로 거부되는 타입 — 게이트가 이들을 세면 서로를 피어로 삼는 014/016 노이즈가 생긴다.
        // Generate 가 017 지점에서 충돌 검사 앞에 반환하고 value-0 Child 조립 id 를 가진 유효 타입은 존재할 수
        // 없어(수동 0 미표현·해시 0=거부) 이 테스트는 수정 전에도 통과한다 — 치아가 아니라 규약 고정이다.
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
        Assert.Equal(2, rejected.Length);                              // 둘 다 해시 0 거부
        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT014" || d.Id == "MSGPROT016");
        Assert.DoesNotContain("partial class aacN86426", generated);
        Assert.DoesNotContain("partial class aafw42693", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT015_다른_제네릭_선언이_같은_MessageId_ClassId를_쓰면_컴파일에서_거부된다()
    {
        // 감사 원장 MEDIUM(2026-09-06) 회귀: 서로 다른 두 제네릭 선언이 같은 MessageId 값 + 같은 ClassId 를 쓰면
        // 런타임 키 (MessageId, ClassId) 가 같아져 RegisterGenericReaderInvoker 가 모듈 이니셜라이저에서 충돌,
        // TypeInitializationException(어셈블리 로드 실패)이 된다. (Declaration, ClassId) 키 충돌 검사는 선언이
        // 다르면 다른 키로 봐서 이 형태를 못 잡았다. 소비자 프로젝트 실험으로 수정 전 재생 확인.
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
        Assert.Contains("GenB<T>", reported[0].GetMessage());   // 상대 선언의 정규 이름
        Assert.Contains("GenA<T>", reported[1].GetMessage());
        Assert.Contains("0x00000007", reported[0].GetMessage()); // 조립된 런타임 키의 16진 MessageId

        // 등록 캐리어가 생성되지 않는다(충돌 등록이 모듈 로드를 깨뜨리므로).
        // ("RegisterGenericConstruction" 문자열 자체는 미등록 구성 안내 예외 메시지에도 등장하니 캐리어 클래스명으로 판별한다.)
        Assert.DoesNotContain("__GenericConstructionRegistration", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT015_ClassId가_다르면_같은_MessageId_값도_충돌하지_않는다()
    {
        // 역방향 가드: 런타임 키는 (MessageId, ClassId) 조합 — ClassId 가 다르면 공존한다.
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
    public void MSGPROT015_MessageId_값이_다르면_같은_ClassId도_충돌하지_않는다()
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
    public void MSGPROT015_단일_선언의_여러_구성은_충돌하지_않는다()
    {
        // 역방향 가드: 한 선언의 여러 구성(서로 다른 타입 인자)은 같은 MessageId 를 공유하지만 ClassId 로
        // 구분된다 — 정상적인 사용 형태. (같은 선언 + 같은 ClassId 중복은 기존 MSGPROT008 이 잡는다.)
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

    /// <summary>생성 코드에서 `writer.Write*(message.멤버)` 호출의 멤버 이름을 나온 순서대로 뽑는다 = 와이어 기록 순서.</summary>
    static IReadOnlyList<string> ExtractWriteOrder(string generated)
    {
        return System.Text.RegularExpressions.Regex
            .Matches(generated, @"writer\.Write\w+\(message\.(\w+)")
            .Select(static match => match.Groups[1].Value)
            .ToList();
    }

    [Fact]
    public void 무관한_편집에도_생성_파일별_텍스트는_변하지_않는다()
    {
        // KI-3 가 산 성질을 **드라이버 수준**에서 고정한다. 측정 결과(Known-Issues KI-10): 증분 파이프라인의
        // 출력 스텝은 매 편집마다 재실행된다 — `Compilation` 스텝이 항상 Modified 이고 `ForAttributeWithMetadataName`
        // 의 transform 출력이 컴파일별 심볼 인스턴스라 값 동등성이 없어서다(`SourceOutput -> Modified`).
        // 그래도 생성 **텍스트**이 동일하면 Roslyn 의 출력 비교가 다운스트림을 막아 생성 트리가 교체·재컴파일되지
        // 않는다 — KI-3(전역 카운터 제거) 이전에는 텍스트가 매번 달라서 그 방어막이 소용없었다.
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

        // 무관한 편집: 메시지 타입이 아닌 클래스의 본문만 바꾼다.
        var edited = compilation.ReplaceSyntaxTree(
            compilation.SyntaxTrees.Single(),
            CSharpSyntaxTree.ParseText(source.Replace("value + 1", "value + 2")));
        var secondRun = ((CSharpGeneratorDriver)driver.RunGenerators(edited)).GetRunResult().Results.Single();

        var first = GeneratedByHintName(firstRun);
        var second = GeneratedByHintName(secondRun);

        // 비교가 vacuous 하지 않도록 실제 생성물이 있음을 먼저 확인한다.
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

    // ---------- 힌트 이름 충돌 (KI-40) ----------

    // 중첩 타입 힌트 이름의 `+` 가 `_` 로 치환되던 시절, `Ns.A+B`(중첩)와 실재하는 `Ns.A_B` 타입이 같은
    // 힌트 이름을 만들어 AddSource 가 중복 예외를 던졌다 — AD0001 로 컴파일의 생성 소스 전체 유실.
    // 이제 `+` 가 보존되어(식별자에 `+` 는 불가능) 충돌이 구조적으로 불가능하다.
    [Fact]
    public void 중첩_타입과_밑줄_조인_이름의_탑레벨_타입은_독립적으로_생성된다()
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

        // AD0001(생성기 비정상 종료) 없이 세 타입 모두 생성된다.
        Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001");
        Assert.Contains("class Outer", generated);
        Assert.Contains("Outer.Inner", generated);
        Assert.Contains("Outer_Inner", generated);
        Assert.Empty(compileErrors);
    }

    // ---------- 오류형 ClassId 진단 (메타데이터 감사 FINDING 2) ----------

    // `ClassId = <error>`(선언되지 않은 상수 등)는 컴파일러가 속성 사용 위치에서 이미 1차 오류를 낸다 —
    // 생성기가 classId=0 을 그대로 흘려보내 "missing 'ClassId'" 2차 오안내를 보태던 것을 건너뛰게 했다.
    [Fact]
    public void 오류형_ClassId_식은_missing_ClassId_오안내를_보태지_않는다()
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

        // 컴파일러 1차 오류(CS0103)는 존재 — 원인은 거기에 있다.
        Assert.Contains(compileErrors, d => d.Id == "CS0103");
        // 생성기는 오안내를 보태지 않는다.
        Assert.DoesNotContain(diagnostics, d => d.GetMessage().Contains("missing 'ClassId'"));
    }

    // ---------- 이형(엑조틱) 소비자 형태 행렬 (2026-09-08) ----------
    // 합법적이지만 희귀한 멤버/타입 모양 — 각각 깨끗한 진단(AD0001 아님) 또는 정상 생성이어야 한다.

    [Theory]
    [InlineData("public int[,] Grid { get; set; }", "MSGPROT006")]                       // 랭크-2 배열
    [InlineData("public System.Span<int> Slice { get; set; }", "MSGPROT006")]            // ref struct 멤버
    [InlineData("public (int A, string B)? Pair { get; set; }", "MSGPROT006")]           // 튜플
    [InlineData("public System.IntPtr Handle { get; set; }", "MSGPROT006")]              // 포인터 대응 합법형
    [InlineData("public System.Threading.Tasks.Task<int>? Task { get; set; }", "MSGPROT006")] // 미래형 멤버
    public void 이형_멤버_모양은_깨끗한_진단으로_거부된다(string member, string expectedDiagnostic)
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
    public void 가변_struct_메시지는_정상_생성된다()
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
    public void static_멤버와_const는_와이어에서_제외된다()
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
        // 생성 페이로드는 인스턴스 멤버만 담는다 — static/const 가 등장하면 안 된다.
        int payloadStart = generated.IndexOf("WritePayload", StringComparison.Ordinal);
        string payload = payloadStart >= 0 ? generated[payloadStart..] : generated;
        Assert.DoesNotContain(".Const", payload);      // 멤버 접근 한정 — 타입명 오검출 방지
        Assert.DoesNotContain(".Static", payload);
        Assert.Contains(".Instance", payload);
    }

    [Fact]
    public void MSGPROT016_Message_해시_충돌은_컴파일에서_거부된다()
    {
        // TestNs.C17819 / TestNs.C21964 — FullName FNV-1a 24비트가 0x1867E1 로 같은 쌍(오프라인 탐색으로 발굴).
        // 자동 재해시 없이 진단으로 거부하고 이름 변경·명시적 속성 전환을 안내한다.
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
        Assert.DoesNotContain(diagnostics, d => d.Id == "MSGPROT014");   // [Message] 전용 진단으로 보고된다

        // 둘 다 생성되지 않는다(등록될 수 없으므로).
        Assert.DoesNotContain("partial class C17819", generated);
        Assert.DoesNotContain("partial class C21964", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void MSGPROT017_Message_요소_해시_0은_거부된다()
    {
        // "Zq4197766"(전역 네임스페이스)의 FNV-1a 24비트 == 0 (오프라인 탐색으로 발굴).
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
        Assert.True(match.Success, "생성 코드에 MessageId 상수가 없다");
        return match.Groups[1].Value;
    }

    [Fact]
    public void kind_Standalone의_id_생략은_Automatic과_동일한_FullName_해시_MessageId를_생성한다()
    {
        // id 를 생략(0)한 explicit kind 의 해시 Id 는 Automatic 추론과 동일 경로(MessageIdHash.FromFullName)를 써야 한다.
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

        // 동일 FullName → 동일 해시 → 동일 와이어 MessageId ([Message] Automatic 은 파생이 없어 Standalone 로 추론된다).
        Assert.Equal(ExtractMessageId(standaloneGenerated), ExtractMessageId(messageGenerated));
    }

    [Fact]
    public void Automatic에_수동_id를_함께_쓰면_추론_종류에_수동_id가_실린다()
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
    public void Child의_id_0은_수동_0이_아니라_해시로_해석된다()
    {
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Parent, 10)]
            public partial class ZeroIdRoot { public int X { get; set; } }

            [Message(MessageKind.Child, 0)]
            public partial class ZeroIdChild : ZeroIdRoot { public int Y { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Empty(compileErrors);
        // id 0 은 '생략' — FullName 해시가 실리며(해시 0 이면 MSGPROT017), 수동 0 은 표현되지 않는다.
        Assert.DoesNotContain("MessageId => 0;", generated);
        Assert.Contains("writer.WriteByte(0x80);", generated);   // Child(8)<<4 | 0
    }

    [Fact]
    public void 수동_Id와_category_생성자_인자는_와이어_MessageId에_그대로_반영된다()
    {
        // (uint id, MessageCategory category) 오버로드 — 기존 수동 Id 할당 호환 + category 니블 반영.
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
    public void category_전용_생성자는_해시_Id와_category를_함께_쓴다()
    {
        var (diagnostics, generated, compileErrors) = RunGeneratorWithCompilation(Header + """
            [Message(MessageKind.Parent, category: MessageCategory.Category2)]
            public partial class CatRoot { public int X { get; set; } }

            [Message(MessageKind.Child)]
            public partial class CatChild : CatRoot { public int Y { get; set; } }
            """ + Footer);

        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("MSGPROT"));
        Assert.Empty(compileErrors);
        // 루트 헤더: GroupRoot(4)<<4 | 2 = 0x42. 해시 Id 는 0이 아니다(GroupElement 해시 0 은 MSGPROT017 로 거부됨).
        Assert.Contains("writer.WriteByte(0x42);", generated);
        Assert.DoesNotContain("MessageId => 0;", generated);
    }
}
