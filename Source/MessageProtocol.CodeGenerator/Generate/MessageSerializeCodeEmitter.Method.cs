using MessageProtocol.CodeGenerator.Graph;
using MessageProtocol.CodeGenerator.Metadata;
using MessageProtocol;
using Microsoft.CodeAnalysis;
using System.Text;

namespace MessageProtocol.CodeGenerator.Generate
{
    internal static partial class MessageSerializeCodeEmitter
    {
        /// <summary>Emitter for Serialize / Deserialize / ModuleInitializer / graph helper methods.</summary>
        internal static class Method
        {
            public static string EmitOnModuleInitialize(TypeMetadata typeMeta, string indent, IAssemblySymbol? consumerAssembly)
            {
                // Initialize is internal, so by default only same-assembly bases can be hideable targets — omit `new`
                // for out-of-assembly bases, except when the base assembly opens access via InternalsVisibleTo.
                string staticHidingModifier = GetStaticHidingModifier(typeMeta, isModuleInitializer: true, consumerAssembly);
                string typeName = typeMeta.Symbol.Name;
                bool hasId = typeMeta.IsStandaloneMessage || typeMeta.IsGroupMessage;

                var sb = new StringBuilder();
                sb.AppendLine($@"
{indent}[ModuleInitializer]
{indent}internal {staticHidingModifier}static void Initialize()
{indent}{{");
                if (hasId)
                {
                    // Delegate + MessageId passed directly → skips SerializerCache reflection.
                    sb.AppendLine($@"{indent}    MessageSerializer.RegisterHasIdMessage<{typeName}>({typeName}.Serialize, {typeName}.Deserialize, {typeName}.MessageId);");
                }
                else
                {
                    sb.AppendLine($@"{indent}    MessageSerializer.RegisterNonIdMessage<{typeName}>({typeName}.Serialize, {typeName}.Deserialize);");
                }
                sb.AppendLine($@"{indent}}}");
                return sb.ToString();
            }

            /// <summary>
            /// Decomposes the 4-byte (big-endian) wire MessageId — the value EmitSerialize writes and EmitDeserialize
            /// verifies must come from the same decomposition. Decomposing in two places meant that an id-layout change
            /// fixed on one side only produced a self-contradicting bug: the reader rejects the bytes the writer writes
            /// (structural audit FINDING 5, 2026-09-08).
            /// </summary>
            static (byte Header, byte B1, byte B2, byte B3) DecomposeWireId(uint messageId)
            {
                return ((byte)(messageId >> 24), (byte)(messageId >> 16), (byte)(messageId >> 8), (byte)messageId);
            }

            public static string EmitSerialize(TypeMetadata typeMeta, string indent, SerializationGraph graph)
            {
                var rootModel = graph.RootType;
                uint id = typeMeta.GetMessageId();
                var (headerByte, idB1, idB2, idB3) = DecomposeWireId(id);
                bool hasEmbeddedId = typeMeta.IsStandaloneMessage || typeMeta.IsGroupMessage;

                var sb = new StringBuilder();

                // Hot path: writer-based
                sb.AppendLine($@"public static void Serialize({typeMeta.DeclarationName} message, ref MessageBufferWriter writer)");
                sb.AppendLine($@"{indent}{{");
                if (rootModel.IsReferenceType)
                {
                    sb.AppendLine($@"{indent}    if (message is null) throw new ArgumentNullException(nameof(message));");
                }
                sb.AppendLine($@"{indent}    writer.WriteByte(0x{headerByte:X2});");
                if (hasEmbeddedId)
                {
                    sb.AppendLine($@"{indent}    writer.WriteByte(0x{idB1:X2});");
                    sb.AppendLine($@"{indent}    writer.WriteByte(0x{idB2:X2});");
                    sb.AppendLine($@"{indent}    writer.WriteByte(0x{idB3:X2});");
                }
                if (typeMeta.IsGenericWireMessage)
                {
                    // 3 bytes of construction class ID after the header — the class ID is looked up in the runtime registry (throws when no construction is declared).
                    sb.AppendLine($@"{indent}    uint __classId = MessageSerializer.GetGenericClassId<{typeMeta.DeclarationName}>();");
                    sb.AppendLine($@"{indent}    if (__classId == 0) throw new InvalidOperationException(""This generic construction is not registered for serialization; declare it with [GenericMessage(typeof({typeMeta.DeclarationName}), ClassId = n)] on the declaration or any carrier type, or call MessageSerializer.RegisterGenericConstruction at startup."");");
                    sb.AppendLine($@"{indent}    writer.WriteByte((byte)(__classId >> 16));");
                    sb.AppendLine($@"{indent}    writer.WriteByte((byte)(__classId >> 8));");
                    sb.AppendLine($@"{indent}    writer.WriteByte((byte)__classId);");
                }
                sb.AppendLine($@"{indent}    var __context = default(MessageSerializer.SerializeContext);");
                if (rootModel.IsReferenceType)
                {
                    sb.AppendLine($@"{indent}    __context.RegisterObject(message);");
                }
                sb.AppendLine($@"{indent}    {rootModel.WritePayloadMethodName}(ref writer, message, ref __context);");
                sb.AppendLine($@"{indent}}}");
                sb.AppendLine();

                // Compat: returns byte[]
                sb.AppendLine($@"{indent}public static byte[] Serialize({typeMeta.DeclarationName} message)");
                sb.AppendLine($@"{indent}{{");
                if (rootModel.IsReferenceType)
                {
                    sb.AppendLine($@"{indent}    if (message is null) throw new ArgumentNullException(nameof(message));");
                }
                sb.AppendLine($@"{indent}    var __writer = MessageBufferWriter.Create();");
                sb.AppendLine($@"{indent}    try");
                sb.AppendLine($@"{indent}    {{");
                sb.AppendLine($@"{indent}        Serialize(message, ref __writer);");
                sb.AppendLine($@"{indent}        return __writer.ToArray();");
                sb.AppendLine($@"{indent}    }}");
                sb.AppendLine($@"{indent}    finally");
                sb.AppendLine($@"{indent}    {{");
                sb.AppendLine($@"{indent}        __writer.Dispose();");
                sb.AppendLine($@"{indent}    }}");
                sb.AppendLine($@"{indent}}}");

                return sb.ToString();
            }

            public static string EmitDeserialize(TypeMetadata typeMeta, string indent, SerializationGraph graph)
            {
                var rootModel = graph.RootType;
                string staticHidingModifier = GetStaticHidingModifier(typeMeta);

                // Verification constants — the same 4 big-endian MessageId bytes EmitSerialize writes.
                // Feeding another type's bytes used to silently reinterpret the payload (defect KI-5); blocked at frame entry.
                uint expectedId = typeMeta.GetMessageId();
                var (expectedHeader, expectedB1, expectedB2, expectedB3) = DecomposeWireId(expectedId);
                string typeName = typeMeta.DeclarationName;

                var sb = new StringBuilder();

                // Hot path: reader-based
                sb.AppendLine($@"public {staticHidingModifier}static {typeMeta.DeclarationName} Deserialize(ref MessageBufferReader reader)");
                sb.AppendLine($@"{indent}{{");
                sb.AppendLine($@"{indent}    byte __headerByte = reader.ReadByte();");
                // The header rule calls the shared single source of truth — structurally making it impossible for an
                // inlined bit re-implementation to drift from the wire rule (audit ledger LOW, 2026-09-08). The branch
                // judges on the **frame byte (runtime)**: if untrusted bytes claim the NonId flag, the 4-byte read is
                // skipped and the bytes are rejected by the comparison below.
                sb.AppendLine($@"{indent}    if (MessageProtocol.MessageWireFormat.HasEmbeddedMessageId(__headerByte))");
                sb.AppendLine($@"{indent}    {{");
                sb.AppendLine($@"{indent}        byte __idB1 = reader.ReadByte();");
                sb.AppendLine($@"{indent}        byte __idB2 = reader.ReadByte();");
                sb.AppendLine($@"{indent}        byte __idB3 = reader.ReadByte();");
                sb.AppendLine($@"{indent}        if (__headerByte != 0x{expectedHeader:X2} || __idB1 != 0x{expectedB1:X2} || __idB2 != 0x{expectedB2:X2} || __idB3 != 0x{expectedB3:X2})");
                sb.AppendLine($@"{indent}        {{");
                sb.AppendLine($@"{indent}            throw new System.IO.InvalidDataException($""Wire header {{__headerByte:X2}} {{__idB1:X2}} {{__idB2:X2}} {{__idB3:X2}} does not match {typeName} (expected MessageId 0x{expectedId:X8}); the bytes belong to a different message type or are corrupt."");");
                sb.AppendLine($@"{indent}        }}");
                sb.AppendLine($@"{indent}    }}");
                sb.AppendLine($@"{indent}    else if (__headerByte != 0x{expectedHeader:X2})");
                sb.AppendLine($@"{indent}    {{");
                sb.AppendLine($@"{indent}        throw new System.IO.InvalidDataException($""Wire header {{__headerByte:X2}} does not match {typeName} (expected 0x{expectedHeader:X2}); the bytes belong to a different message type or are corrupt."");");
                sb.AppendLine($@"{indent}    }}");
                if (typeMeta.IsGenericWireMessage)
                {
                    // Consume the 3 bytes of construction class ID (routing already used it).
                    sb.AppendLine($@"{indent}    reader.ReadByte();");
                    sb.AppendLine($@"{indent}    reader.ReadByte();");
                    sb.AppendLine($@"{indent}    reader.ReadByte();");
                }
                sb.AppendLine($@"{indent}    var __context = default(MessageSerializer.DeserializeContext);");
                if (rootModel.IsReferenceType)
                {
                    sb.AppendLine($@"{indent}    var result = {rootModel.CreateInstanceMethodName}();");
                    sb.AppendLine($@"{indent}    __context.RegisterNewObject(result);");
                    sb.AppendLine($@"{indent}    {rootModel.PopulatePayloadMethodName}(ref reader, result, ref __context);");
                    sb.AppendLine($@"{indent}    return result;");
                }
                else
                {
                    sb.AppendLine($@"{indent}    return {rootModel.ReadPayloadMethodName}(ref reader, ref __context);");
                }
                sb.AppendLine($@"{indent}}}");
                sb.AppendLine();

                // Compat: takes byte[]
                sb.AppendLine($@"{indent}public {staticHidingModifier}static {typeMeta.DeclarationName} Deserialize(byte[] data)");
                sb.AppendLine($@"{indent}{{");
                sb.AppendLine($@"{indent}    if (data is null) throw new ArgumentNullException(nameof(data));");
                sb.AppendLine($@"{indent}    var __reader = new MessageBufferReader(data);");
                sb.AppendLine($@"{indent}    return Deserialize(ref __reader);");
                sb.AppendLine($@"{indent}}}");

                return sb.ToString();
            }

            public static string EmitHelperMethods(string indent, SerializationGraph graph, EmitState state)
            {
                var sb = new StringBuilder();
                sb.Append(EmitTypeMethods(graph.RootType, indent, graph, state));

                foreach (var typeModel in graph.ReachableTypes)
                {
                    if (ReferenceEquals(typeModel, graph.RootType))
                    {
                        continue;
                    }

                    sb.AppendLine();
                    sb.Append(EmitTypeMethods(typeModel, indent, graph, state));
                }

                return sb.ToString();
            }

            static string EmitTypeMethods(SerializableTypeModel typeModel, string indent, SerializationGraph graph, EmitState state)
            {
                bool isRootType = ReferenceEquals(typeModel, graph.RootType);
                return typeModel.IsReferenceType
                    ? EmitReferenceTypeMethods(typeModel, indent, graph, state, isRootType)
                    : EmitValueTypeMethods(typeModel, indent, graph, state, isRootType);
            }

            static string EmitReferenceTypeMethods(SerializableTypeModel typeModel, string indent, SerializationGraph graph, EmitState state, bool isRootType)
            {
                var sb = new StringBuilder();

                sb.AppendLine($@"private static {typeModel.TypeName} {typeModel.CreateInstanceMethodName}()");
                sb.AppendLine($@"{indent}{{");
                sb.AppendLine($@"{indent}    return new {typeModel.TypeName}();");
                sb.AppendLine($@"{indent}}}");
                sb.AppendLine();

                sb.AppendLine($@"{indent}private static void {typeModel.WritePayloadMethodName}(ref MessageBufferWriter writer, {typeModel.TypeName} message, ref MessageSerializer.SerializeContext context)");
                sb.AppendLine($@"{indent}{{");
                AppendWritePayloadBody(sb, typeModel, indent + "    ", graph, state);
                sb.AppendLine($@"{indent}}}");
                sb.AppendLine();

                sb.AppendLine($@"{indent}private static void {typeModel.PopulatePayloadMethodName}(ref MessageBufferReader reader, {typeModel.TypeName} result, ref MessageSerializer.DeserializeContext context)");
                sb.AppendLine($@"{indent}{{");
                foreach (var member in TypeMetadata.GetWireMembers(typeModel.Metadata))
                {
                    sb.Append(Member.EmitDeserialize(member, "result", indent + "    ", graph, state, isRootType));
                }
                sb.AppendLine($@"{indent}}}");

                return sb.ToString();
            }

            static string EmitValueTypeMethods(SerializableTypeModel typeModel, string indent, SerializationGraph graph, EmitState state, bool isRootType)
            {
                var sb = new StringBuilder();

                sb.AppendLine($@"private static void {typeModel.WritePayloadMethodName}(ref MessageBufferWriter writer, {typeModel.TypeName} message, ref MessageSerializer.SerializeContext context)");
                sb.AppendLine($@"{indent}{{");
                AppendWritePayloadBody(sb, typeModel, indent + "    ", graph, state);
                sb.AppendLine($@"{indent}}}");
                sb.AppendLine();

                sb.AppendLine($@"{indent}private static {typeModel.TypeName} {typeModel.ReadPayloadMethodName}(ref MessageBufferReader reader, ref MessageSerializer.DeserializeContext context)");
                sb.AppendLine($@"{indent}{{");
                sb.AppendLine($@"{indent}    var result = default({typeModel.TypeName});");
                foreach (var member in TypeMetadata.GetWireMembers(typeModel.Metadata))
                {
                    sb.Append(Member.EmitDeserialize(member, "result", indent + "    ", graph, state, isRootType));
                }
                sb.AppendLine($@"{indent}    return result;");
                sb.AppendLine($@"{indent}}}");

                return sb.ToString();
            }

            /// <summary>Sums fixed-size primitive stretches into a single EnsureCapacity call, then writes members in order.</summary>
            static void AppendWritePayloadBody(
                StringBuilder sb,
                SerializableTypeModel typeModel,
                string indent,
                SerializationGraph graph,
                EmitState state)
            {
                int fixedSize = 0;
                foreach (var member in TypeMetadata.GetWireMembers(typeModel.Metadata))
                {
                    if (Member.TryGetFixedPrimitiveWireSize(member.Type, out int size))
                    {
                        fixedSize += size;
                    }
                }

                if (fixedSize > 0)
                {
                    sb.AppendLine($@"{indent}writer.EnsureCapacity({fixedSize});");
                }

                foreach (var member in TypeMetadata.GetWireMembers(typeModel.Metadata))
                {
                    sb.Append(Member.EmitSerialize(member, "message", indent, graph, state));
                }
            }
        }
    }
}
