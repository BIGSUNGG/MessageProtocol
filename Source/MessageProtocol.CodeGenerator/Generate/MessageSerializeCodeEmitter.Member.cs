using MessageProtocol.CodeGenerator.Graph;
using MessageProtocol.CodeGenerator.Metadata;
using Microsoft.CodeAnalysis;

namespace MessageProtocol.CodeGenerator.Generate
{
    internal static partial class MessageSerializeCodeEmitter
    {
        /// <summary>Per-member serialize/deserialize code emitter.</summary>
        internal static class Member
        {
            public static string EmitSerialize(
                MemberMetadata member,
                string instanceExpression,
                string indent,
                SerializationGraph graph,
                EmitState state)
            {
                string memberAccess = $"{instanceExpression}.{member.Name}";
                Location location = member.Symbol.Locations.FirstOrDefault() ?? Location.None;
                return EmitSerializeValue(member.Type, memberAccess, indent, graph, state, location, member.Name);
            }

            public static string EmitDeserialize(
                MemberMetadata member,
                string instanceExpression,
                string indent,
                SerializationGraph graph,
                EmitState state,
                bool isRootType)
            {
                string memberAccess = $"{instanceExpression}.{member.Name}";
                Location location = member.Symbol.Locations.FirstOrDefault() ?? Location.None;

                if (!IsDeserializableMember(member, isRootType))
                {
                    state.ReportNotAssignable(location, GetTypeDisplayName(member.Type), member.Name);
                    return string.Empty;
                }

                return EmitDeserializeValue(member.Type, memberAccess, indent, graph, state, location, member.Name);
            }

            /// <summary>
            /// Generated code assigns via `result.Member = …` — read-only, init-only, and readonly fields cannot be filled.
            /// The root type is assigned inside its own partial, so any access level is allowed; nested payloads must be
            /// at least internal, i.e. accessible from the root class.
            /// </summary>
            static bool IsDeserializableMember(MemberMetadata member, bool isRootType)
            {
                if (member.Symbol is IFieldSymbol field)
                {
                    if (field.IsConst || field.IsReadOnly)
                    {
                        return false;
                    }

                    return isRootType || IsAtLeastInternal(field.DeclaredAccessibility);
                }

                if (member.Symbol is IPropertySymbol property)
                {
                    var setter = property.SetMethod;
                    if (setter == null || setter.IsInitOnly)
                    {
                        return false;
                    }

                    return isRootType || IsAtLeastInternal(setter.DeclaredAccessibility);
                }

                return false;
            }

            static bool IsAtLeastInternal(Accessibility accessibility)
            {
                return accessibility == Accessibility.Public || accessibility == Accessibility.Internal;
            }

            static string EmitSerializeValue(
                ITypeSymbol typeSymbol,
                string valueExpression,
                string indent,
                SerializationGraph graph,
                EmitState state,
                Location diagnosticLocation,
                string memberDisplayName)
            {
                // 1) primitive / string / enum fast path
                if (TryEmitPrimitiveWrite(typeSymbol, valueExpression, indent, out string primitiveWrite))
                {
                    return primitiveWrite;
                }

                // 1.5) Type parameters: runtime message dispatch (only registered message types can appear as T).
                if (typeSymbol is ITypeParameterSymbol)
                {
                    return EmitRuntimeDispatchWrite(valueExpression, indent, state);
                }

                // 2) Arrays (one-dimensional only)
                if (typeSymbol is IArrayTypeSymbol arrayType)
                {
                    if (arrayType.Rank != 1)
                    {
                        return ReportUnsupported(typeSymbol, state, diagnosticLocation, memberDisplayName);
                    }

                    return EmitArrayWrite(arrayType, valueExpression, indent, graph, state, diagnosticLocation, memberDisplayName);
                }

                // 3) List<T> / IList<T>
                if (SerializationGraph.TryGetCollectionElementType(typeSymbol, out var collectionElementType)
                    && typeSymbol is INamedTypeSymbol listType
                    && listType.IsGenericType)
                {
                    return EmitListWrite(typeSymbol, collectionElementType, valueExpression, indent, graph, state, diagnosticLocation, memberDisplayName);
                }

                // 4) In-graph types (messages and nested objects alike)
                if (graph.TryGetSerializableObjectType(typeSymbol, out var inGraphModel))
                {
                    return EmitInGraphMessageWrite(inGraphModel, valueExpression, indent, state);
                }

                // 5) Message types outside the graph (other assemblies etc.) — delegate to static Serialize.
                //    Except abstract message types (e.g. abstract [Message(MessageKind.Parent)]) get no static
                //    Serialize/Deserialize emitted (MSGPROT010 family — not instantiable), so delegation code would
                //    break the consumer build with CS0117. Instead, runtime message dispatch writes the *concrete*
                //    element with its header — polymorphism is restored without losing derived members.
                if (graph.IsMessageType(typeSymbol))
                {
                    return typeSymbol.IsAbstract
                        ? EmitRuntimeDispatchWrite(valueExpression, indent, state)
                        : EmitOutOfGraphMessageWrite(typeSymbol, valueExpression, indent, state);
                }

                return ReportUnsupported(typeSymbol, state, diagnosticLocation, memberDisplayName);
            }

            static string EmitDeserializeValue(
                ITypeSymbol typeSymbol,
                string targetExpression,
                string indent,
                SerializationGraph graph,
                EmitState state,
                Location diagnosticLocation,
                string memberDisplayName)
            {
                if (TryEmitPrimitiveRead(typeSymbol, targetExpression, indent, out string primitiveRead))
                {
                    return primitiveRead;
                }

                if (typeSymbol is ITypeParameterSymbol)
                {
                    return EmitRuntimeDispatchRead(typeSymbol, targetExpression, indent, state);
                }

                if (typeSymbol is IArrayTypeSymbol arrayType)
                {
                    if (arrayType.Rank != 1)
                    {
                        return ReportUnsupported(typeSymbol, state, diagnosticLocation, memberDisplayName);
                    }

                    return EmitArrayRead(arrayType, targetExpression, indent, graph, state, diagnosticLocation, memberDisplayName);
                }

                if (SerializationGraph.TryGetCollectionElementType(typeSymbol, out var collectionElementType)
                    && typeSymbol is INamedTypeSymbol listType
                    && listType.IsGenericType)
                {
                    return EmitListRead(collectionElementType, targetExpression, indent, graph, state, diagnosticLocation, memberDisplayName);
                }

                if (graph.TryGetSerializableObjectType(typeSymbol, out var inGraphModel))
                {
                    return EmitInGraphMessageRead(inGraphModel, targetExpression, indent, state);
                }

                // Abstract message types have no generated static Deserialize, so delegation would raise CS0117 — for
                // the same reason as the write path, read via runtime dispatch and cast to the declared (abstract root)
                // type (the actual instance is a registered concrete element).
                if (graph.IsMessageType(typeSymbol))
                {
                    return typeSymbol.IsAbstract
                        ? EmitRuntimeDispatchRead(typeSymbol, targetExpression, indent, state)
                        : EmitOutOfGraphMessageRead(typeSymbol, targetExpression, indent, state);
                }

                return ReportUnsupported(typeSymbol, state, diagnosticLocation, memberDisplayName);
            }

            static string ReportUnsupported(
                ITypeSymbol typeSymbol,
                EmitState state,
                Location diagnosticLocation,
                string memberDisplayName)
            {
                state.ReportUnsupported(diagnosticLocation, GetTypeDisplayName(typeSymbol), memberDisplayName);
                return string.Empty;
            }

            // ------- In-graph objects (reference tracking) -------
            //
            // The write/read skeletons of all three reference-tracking paths (in-graph, out-of-graph delegation,
            // runtime dispatch) share the same Null/BackReference/NewObject wire protocol. The two helpers below
            // emit the skeleton and guidance messages (KI-34 back-reference mismatch, KI-36 unknown tag) from a
            // single source of truth; callers pass only the per-path differences (registration order, null
            // representation, frame call statement, tag local name) — six hand-copied sites already showed sentence
            // order drift (in-graph registers the object first, the rest write the tag first) (structural audit
            // FINDING 1, 2026-09-08). Emitted bytes are identical to before (verified by golden comparison).

            static string EmitTrackedReferenceWrite(string valueExpression, int uid, string indent, string newObjectBody)
            {
                return $@"{indent}if ({valueExpression} is null)
{indent}{{
{indent}    writer.WriteByte((byte)MessageSerializer.ReferenceKind.Null);
{indent}}}
{indent}else if (context.TryGetObjectId({valueExpression}, out int __backId{uid}))
{indent}{{
{indent}    writer.WriteByte((byte)MessageSerializer.ReferenceKind.BackReference);
{indent}    writer.WriteInt32(__backId{uid});
{indent}}}
{indent}else
{indent}{{
{newObjectBody}{indent}}}
";
            }

            static string EmitTrackedReferenceRead(string tagLocalName, string typeName, string targetExpression, int uid, string indent, string nullAssignment, string newObjectBody)
            {
                return $@"{indent}{{
{indent}    byte {tagLocalName}{uid} = reader.ReadByte();
{indent}    if ({tagLocalName}{uid} == (byte)MessageSerializer.ReferenceKind.Null)
{indent}    {{
{indent}        {nullAssignment}
{indent}    }}
{indent}    else if ({tagLocalName}{uid} == (byte)MessageSerializer.ReferenceKind.BackReference)
{indent}    {{
{indent}        int __objId{uid} = reader.ReadInt32();
{indent}        var __back{uid} = context.GetObject(__objId{uid});
{indent}        if (!(__back{uid} is {typeName}))
{indent}        {{
{indent}            throw new System.IO.InvalidDataException($""Back-reference {{__objId{uid}}} resolved to '{{__back{uid}.GetType().FullName}}' but member '{targetExpression}' requires '{{typeof({typeName}).FullName}}'. The same instance was first recorded through a member with a less derived static type, so only its base members were written; declare the member as the concrete type or make the base abstract so the concrete element is dispatched at runtime (Known-Issues KI-34)."");
{indent}        }}
{indent}        {targetExpression} = ({typeName})__back{uid};
{indent}    }}
{indent}    else if ({tagLocalName}{uid} != (byte)MessageSerializer.ReferenceKind.NewObject)
{indent}    {{
{indent}        throw new System.IO.InvalidDataException($""Unknown reference kind {{{tagLocalName}{uid}}}; expected Null(0), NewObject(1) or BackReference(2). The payload is corrupt or from an incompatible protocol version."");
{indent}    }}
{indent}    else
{indent}    {{
{newObjectBody}{indent}    }}
{indent}}}
";
            }

            static string EmitInGraphMessageWrite(SerializableTypeModel model, string valueExpression, string indent, EmitState state)
            {
                if (!model.IsReferenceType)
                {
                    return $"{indent}{model.WritePayloadMethodName}(ref writer, {valueExpression}, ref context);\n";
                }

                int uid = state.NextUniqueId();
                string newObjectBody = $@"{indent}    context.RegisterObject({valueExpression});
{indent}    writer.WriteByte((byte)MessageSerializer.ReferenceKind.NewObject);
{indent}    writer.EnterNestedObject();
{indent}    {model.WritePayloadMethodName}(ref writer, {valueExpression}, ref context);
{indent}    writer.LeaveNestedObject();
";
                return EmitTrackedReferenceWrite(valueExpression, uid, indent, newObjectBody);
            }

            static string EmitInGraphMessageRead(SerializableTypeModel model, string targetExpression, string indent, EmitState state)
            {
                if (!model.IsReferenceType)
                {
                    return $"{indent}{targetExpression} = {model.ReadPayloadMethodName}(ref reader, ref context);\n";
                }

                int uid = state.NextUniqueId();
                string newObjectBody = $@"{indent}        reader.EnterNestedObject();
{indent}        var __tmp{uid} = {model.CreateInstanceMethodName}();
{indent}        context.RegisterNewObject(__tmp{uid});
{indent}        {model.PopulatePayloadMethodName}(ref reader, __tmp{uid}, ref context);
{indent}        reader.LeaveNestedObject();
{indent}        {targetExpression} = __tmp{uid};
";
                return EmitTrackedReferenceRead("__refKind", model.TypeName, targetExpression, uid, indent, $"{targetExpression} = null;", newObjectBody);
            }

            // ------- Out-of-graph messages (static Serialize/Deserialize delegation) -------

            static string EmitOutOfGraphMessageWrite(ITypeSymbol typeSymbol, string valueExpression, string indent, EmitState state)
            {
                string typeName = GetTypeDisplayName(typeSymbol);
                if (typeSymbol.IsReferenceType)
                {
                    int uid = state.NextUniqueId();
                    string newObjectBody = $@"{indent}    writer.WriteByte((byte)MessageSerializer.ReferenceKind.NewObject);
{indent}    context.RegisterObject({valueExpression});
{indent}    writer.EnterNestedObject();
{indent}    {typeName}.Serialize({valueExpression}, ref writer);
{indent}    writer.LeaveNestedObject();
";
                    return EmitTrackedReferenceWrite(valueExpression, uid, indent, newObjectBody);
                }

                return $"{indent}{typeName}.Serialize({valueExpression}, ref writer);\n";
            }

            static string EmitOutOfGraphMessageRead(ITypeSymbol typeSymbol, string targetExpression, string indent, EmitState state)
            {
                string typeName = GetTypeDisplayName(typeSymbol);
                int uid = state.NextUniqueId();
                if (typeSymbol.IsReferenceType)
                {
                    string newObjectBody = $@"{indent}        reader.EnterNestedObject();
{indent}        {targetExpression} = {typeName}.Deserialize(ref reader);
{indent}        reader.LeaveNestedObject();
{indent}        context.RegisterNewObject({targetExpression}!);
";
                    return EmitTrackedReferenceRead("__nk", typeName, targetExpression, uid, indent, $"{targetExpression} = null;", newObjectBody);
                }

                return $"{indent}{targetExpression} = {typeName}.Deserialize(ref reader);\n";
            }

            // ------- Runtime message dispatch (type parameters and abstract message members) -------

            /// <summary>
            /// Runtime type-dispatch write: writes the whole message (header included) via <c>SerializeToWriter</c>.
            /// Shared by type-parameter members and abstract message-typed members. Reuses the caller's
            /// SerializeContext object-id tracking — when the same instance appears twice, the second occurrence is
            /// written as a back-reference, restoring reference identity (audit ledger MEDIUM, 2026-09-05 pass, KI-9).
            /// Frame interiors still use their own context, so sharing across frame boundaries remains separate instances.
            /// </summary>
            static string EmitRuntimeDispatchWrite(string valueExpression, string indent, EmitState state)
            {
                int uid = state.NextUniqueId();
                string newObjectBody = $@"{indent}    writer.WriteByte((byte)MessageSerializer.ReferenceKind.NewObject);
{indent}    context.RegisterObject({valueExpression});
{indent}    MessageSerializer.SerializeToWriter({valueExpression}, ref writer);
";
                return EmitTrackedReferenceWrite(valueExpression, uid, indent, newObjectBody);
            }

            /// <summary>
            /// Runtime type-dispatch read: restores the registered concrete type from the header's MessageId and casts
            /// it to the declared type. Symmetric with the write path: dereferences back-references and registers the
            /// restored instance with the caller's context — the write registers before the frame and the read after
            /// it, but no external context registration happens in between, so the id order matches on both sides
            /// (KI-9 resolved).
            /// </summary>
            static string EmitRuntimeDispatchRead(ITypeSymbol typeSymbol, string targetExpression, string indent, EmitState state)
            {
                int uid = state.NextUniqueId();
                string typeName = GetTypeDisplayName(typeSymbol);
                // Do not blind-cast the dispatched object to the declared type (KI-41, found by the fuzzer 2026-09-08):
                // an untrusted header routing to a different registered type used to explode with an
                // InvalidCastException with no explanation — corrected with a guiding check of the same family as
                // the back-reference branch (KI-34).
                string newObjectBody = $@"{indent}        var __dispatched{uid} = MessageSerializer.DeserializeFromReader(ref reader);
{indent}        if (!(__dispatched{uid} is {typeName}))
{indent}        {{
{indent}            throw new System.IO.InvalidDataException($""Dispatched wire element resolved to '{{__dispatched{uid}.GetType().FullName}}' but member '{targetExpression}' requires '{{typeof({typeName}).FullName}}'. The payload is corrupt or from an incompatible peer."");
{indent}        }}
{indent}        {targetExpression} = ({typeName})__dispatched{uid};
{indent}        context.RegisterNewObject({targetExpression}!);
";
                return EmitTrackedReferenceRead("__pk", typeName, targetExpression, uid, indent, $"{targetExpression} = default;", newObjectBody);
            }

            // ------- Arrays -------
            //
            // Collection writes evaluate the member expression **exactly once** and snapshot it into a local
            // (`__arr`/`__coll`/`__list` → `__span`/`__count`); the null check uses the snapshot local too. Two
            // reasons (Known-Issues KI-26):
            //  (1) Consistency — taking the length prefix and the elements from different evaluations makes the frame
            //      contradict itself. On a computed property (`public IList<int> Codes => Build();`) the length comes
            //      from one collection and, if the second evaluation returns null, an NRE hits inside the else
            //      branch (TOCTOU).
            //  (2) Cost — the previous code ran the getter 2N+2 times: `Count` for the length prefix + `Count` in the
            //      loop condition (N+1) + indexer (N member accesses). This matches the contract the
            //      `CollectionsMarshal` path already had (snapshot as a span) — most visible on `List<T>`/`IList<T>`
            //      under Unity/netstandard2.1 where `CollectionsMarshal` is unavailable.

            static string EmitArrayWrite(
                IArrayTypeSymbol arrayType,
                string valueExpression,
                string indent,
                SerializationGraph graph,
                EmitState state,
                Location diagnosticLocation,
                string memberDisplayName)
            {
                var elementType = arrayType.ElementType;
                string elementTypeName = GetTypeDisplayName(elementType);
                int uid = state.NextUniqueId();

                if (IsBulkCopyable(elementType))
                {
                    return $@"{indent}var __arr{uid} = {valueExpression};
{indent}if (__arr{uid} is null)
{indent}{{
{indent}    writer.WriteInt32(-1);
{indent}}}
{indent}else
{indent}{{
{indent}    writer.WriteInt32(__arr{uid}.Length);
{indent}    if (__arr{uid}.Length > 0)
{indent}    {{
{indent}        writer.WriteBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes<{elementTypeName}>(__arr{uid}.AsSpan()));
{indent}    }}
{indent}}}
";
                }

                var itemName = $"__item{uid}";
                return $@"{indent}var __arr{uid} = {valueExpression};
{indent}if (__arr{uid} is null)
{indent}{{
{indent}    writer.WriteInt32(-1);
{indent}}}
{indent}else
{indent}{{
{indent}    int __count{uid} = __arr{uid}.Length;
{indent}    writer.WriteInt32(__count{uid});
{indent}    for (int __i{uid} = 0; __i{uid} < __count{uid}; __i{uid}++)
{indent}    {{
{indent}        var {itemName} = __arr{uid}[__i{uid}];
{EmitSerializeValue(elementType, itemName, indent + "        ", graph, state, diagnosticLocation, memberDisplayName)}{indent}    }}
{indent}}}
";
            }

            static string EmitArrayRead(
                IArrayTypeSymbol arrayType,
                string targetExpression,
                string indent,
                SerializationGraph graph,
                EmitState state,
                Location diagnosticLocation,
                string memberDisplayName)
            {
                var elementType = arrayType.ElementType;
                string elementTypeName = GetTypeDisplayName(elementType);
                int uid = state.NextUniqueId();

                if (IsBulkCopyable(elementType))
                {
                    int size = GetBulkElementSize(elementType);
                    return $@"{indent}{{
{indent}    int __len{uid} = reader.ReadInt32();
{indent}    if (__len{uid} < 0)
{indent}    {{
{indent}        {targetExpression} = null;
{indent}    }}
{indent}    else
{indent}    {{
{indent}        if ((long)__len{uid} * {size} > reader.Remaining) throw new System.IO.EndOfStreamException(""Collection length prefix exceeds the remaining buffer."");
{indent}        var __arr{uid} = new {elementTypeName}[__len{uid}];
{indent}        if (__len{uid} > 0)
{indent}        {{
{indent}            reader.ReadBytes(__len{uid} * {size}).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes<{elementTypeName}>(__arr{uid}.AsSpan()));
{indent}        }}
{indent}        {targetExpression} = __arr{uid};
{indent}    }}
{indent}}}
";
                }

                var itemName = $"__item{uid}";
                return $@"{indent}{{
{indent}    int __len{uid} = reader.ReadInt32();
{indent}    if (__len{uid} < 0)
{indent}    {{
{indent}        {targetExpression} = null;
{indent}    }}
{indent}    else
{indent}    {{
{indent}        if (__len{uid} > reader.Remaining) throw new System.IO.EndOfStreamException(""Collection length prefix exceeds the remaining buffer."");
{indent}        var __arr{uid} = new {elementTypeName}[__len{uid}];
{indent}        for (int __i{uid} = 0; __i{uid} < __len{uid}; __i{uid}++)
{indent}        {{
{indent}            {elementTypeName} {itemName} = default({elementTypeName});
{EmitDeserializeValue(elementType, itemName, indent + "            ", graph, state, diagnosticLocation, memberDisplayName)}{indent}            __arr{uid}[__i{uid}] = {itemName};
{indent}        }}
{indent}        {targetExpression} = __arr{uid};
{indent}    }}
{indent}}}
";
            }

            // ------- List<T> / IList<T> -------

            /// <summary>
            /// The CollectionsMarshal fast path is used only when the declared type is exactly List&lt;T&gt;
            /// (IList&lt;T&gt; members take the indexer loop).
            /// </summary>
            static bool UseCollectionsMarshal(ITypeSymbol containerType, EmitState state)
            {
                return state.HasCollectionsMarshal
                    && containerType is INamedTypeSymbol namedContainer
                    && namedContainer.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>";
            }

            static string EmitListWrite(
                ITypeSymbol containerType,
                ITypeSymbol elementType,
                string valueExpression,
                string indent,
                SerializationGraph graph,
                EmitState state,
                Location diagnosticLocation,
                string memberDisplayName)
            {
                int uid = state.NextUniqueId();
                bool useCollectionsMarshal = UseCollectionsMarshal(containerType, state);

                if (IsBulkCopyable(elementType))
                {
                    if (useCollectionsMarshal)
                    {
                        return $@"{indent}var __list{uid} = {valueExpression};
{indent}if (__list{uid} is null)
{indent}{{
{indent}    writer.WriteInt32(-1);
{indent}}}
{indent}else
{indent}{{
{indent}    var __span{uid} = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(__list{uid});
{indent}    writer.WriteInt32(__span{uid}.Length);
{indent}    if (__span{uid}.Length > 0)
{indent}    {{
{indent}        writer.WriteBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(__span{uid}));
{indent}    }}
{indent}}}
";
                    }

                    var bulkItemName = $"__item{uid}";
                    return $@"{indent}var __coll{uid} = {valueExpression};
{indent}if (__coll{uid} is null)
{indent}{{
{indent}    writer.WriteInt32(-1);
{indent}}}
{indent}else
{indent}{{
{indent}    int __count{uid} = __coll{uid}.Count;
{indent}    writer.WriteInt32(__count{uid});
{indent}    for (int __i{uid} = 0; __i{uid} < __count{uid}; __i{uid}++)
{indent}    {{
{indent}        var {bulkItemName} = __coll{uid}[__i{uid}];
{EmitSerializeValue(elementType, bulkItemName, indent + "        ", graph, state, diagnosticLocation, memberDisplayName)}{indent}    }}
{indent}}}
";
                }

                var itemName = $"__item{uid}";
                if (useCollectionsMarshal)
                {
                    return $@"{indent}var __list{uid} = {valueExpression};
{indent}if (__list{uid} is null)
{indent}{{
{indent}    writer.WriteInt32(-1);
{indent}}}
{indent}else
{indent}{{
{indent}    var __span{uid} = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(__list{uid});
{indent}    writer.WriteInt32(__span{uid}.Length);
{indent}    for (int __i{uid} = 0; __i{uid} < __span{uid}.Length; __i{uid}++)
{indent}    {{
{indent}        var {itemName} = __span{uid}[__i{uid}];
{EmitSerializeValue(elementType, itemName, indent + "        ", graph, state, diagnosticLocation, memberDisplayName)}{indent}    }}
{indent}}}
";
                }

                return $@"{indent}var __coll{uid} = {valueExpression};
{indent}if (__coll{uid} is null)
{indent}{{
{indent}    writer.WriteInt32(-1);
{indent}}}
{indent}else
{indent}{{
{indent}    int __count{uid} = __coll{uid}.Count;
{indent}    writer.WriteInt32(__count{uid});
{indent}    for (int __i{uid} = 0; __i{uid} < __count{uid}; __i{uid}++)
{indent}    {{
{indent}        var {itemName} = __coll{uid}[__i{uid}];
{EmitSerializeValue(elementType, itemName, indent + "        ", graph, state, diagnosticLocation, memberDisplayName)}{indent}    }}
{indent}}}
";
            }

            static string EmitListRead(
                ITypeSymbol elementType,
                string targetExpression,
                string indent,
                SerializationGraph graph,
                EmitState state,
                Location diagnosticLocation,
                string memberDisplayName)
            {
                string elementTypeName = GetTypeDisplayName(elementType);
                int uid = state.NextUniqueId();

                if (IsBulkCopyable(elementType))
                {
                    int size = GetBulkElementSize(elementType);
                    if (state.HasCollectionsMarshal)
                    {
                        return $@"{indent}{{
{indent}    int __c{uid} = reader.ReadInt32();
{indent}    if (__c{uid} < 0)
{indent}    {{
{indent}        {targetExpression} = null;
{indent}    }}
{indent}    else
{indent}    {{
{indent}        if ((long)__c{uid} * {size} > reader.Remaining) throw new System.IO.EndOfStreamException(""Collection length prefix exceeds the remaining buffer."");
{indent}        var __list{uid} = new System.Collections.Generic.List<{elementTypeName}>(__c{uid});
{indent}        if (__c{uid} > 0)
{indent}        {{
{indent}            System.Runtime.InteropServices.CollectionsMarshal.SetCount(__list{uid}, __c{uid});
{indent}            reader.ReadBytes(__c{uid} * {size}).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(__list{uid})));
{indent}        }}
{indent}        {targetExpression} = __list{uid};
{indent}    }}
{indent}}}
";
                    }

                    var bulkItemName = $"__item{uid}";
                    return $@"{indent}{{
{indent}    int __c{uid} = reader.ReadInt32();
{indent}    if (__c{uid} < 0)
{indent}    {{
{indent}        {targetExpression} = null;
{indent}    }}
{indent}    else
{indent}    {{
{indent}        if ((long)__c{uid} * {size} > reader.Remaining) throw new System.IO.EndOfStreamException(""Collection length prefix exceeds the remaining buffer."");
{indent}        var __list{uid} = new System.Collections.Generic.List<{elementTypeName}>(__c{uid});
{indent}        for (int __i{uid} = 0; __i{uid} < __c{uid}; __i{uid}++)
{indent}        {{
{indent}            {elementTypeName} {bulkItemName} = default({elementTypeName});
{EmitDeserializeValue(elementType, bulkItemName, indent + "            ", graph, state, diagnosticLocation, memberDisplayName)}{indent}            __list{uid}.Add({bulkItemName});
{indent}        }}
{indent}        {targetExpression} = __list{uid};
{indent}    }}
{indent}}}
";
                }

                var itemName = $"__item{uid}";
                return $@"{indent}{{
{indent}    int __c{uid} = reader.ReadInt32();
{indent}    if (__c{uid} < 0)
{indent}    {{
{indent}        {targetExpression} = null;
{indent}    }}
{indent}    else
{indent}    {{
{indent}        if (__c{uid} > reader.Remaining) throw new System.IO.EndOfStreamException(""Collection length prefix exceeds the remaining buffer."");
{indent}        var __list{uid} = new System.Collections.Generic.List<{elementTypeName}>(__c{uid});
{indent}        for (int __i{uid} = 0; __i{uid} < __c{uid}; __i{uid}++)
{indent}        {{
{indent}            {elementTypeName} {itemName} = default({elementTypeName});
{EmitDeserializeValue(elementType, itemName, indent + "            ", graph, state, diagnosticLocation, memberDisplayName)}{indent}            __list{uid}.Add({itemName});
{indent}        }}
{indent}        {targetExpression} = __list{uid};
{indent}    }}
{indent}}}
";
            }

            // ------- Primitives / enum / string -------

            static bool TryEmitPrimitiveWrite(ITypeSymbol typeSymbol, string valueExpression, string indent, out string code)
            {
                if (typeSymbol.TypeKind == TypeKind.Enum && typeSymbol is INamedTypeSymbol enumType)
                {
                    var underlying = enumType.EnumUnderlyingType;
                    if (underlying != null && TryGetPrimitiveWriteCall(underlying, $"({GetTypeDisplayName(underlying)}){valueExpression}", out string call))
                    {
                        code = $"{indent}{call};\n";
                        return true;
                    }
                }

                if (TryGetPrimitiveWriteCall(typeSymbol, valueExpression, out string writeCall))
                {
                    code = $"{indent}{writeCall};\n";
                    return true;
                }

                code = string.Empty;
                return false;
            }

            /// <summary>
            /// Single source of truth for primitives — read expression, write call format, fixed wire size, and bulk
            /// copy size in one place. The previous four independent switches (read/write/fixed size/bulk size) had to
            /// be updated in four places per primitive, and a single mismatch was a wire-drift bug class where reads and
            /// writes silently disagreed (structural audit FINDING 2, 2026-09-08). String is variable length, so both
            /// fixed and bulk are -1; boolean (not packable) and decimal (representable as 20 bytes — unsafe, differing
            /// from the runtime's fixed 16-byte GetBits) have a fixed size only.
            /// </summary>
            static readonly System.Collections.Generic.Dictionary<SpecialType, (string Read, string WriteFormat, int FixedSize, int BulkSize)> PrimitiveWireTable =
                new System.Collections.Generic.Dictionary<SpecialType, (string, string, int, int)>
            {
                [SpecialType.System_Boolean]  = ("reader.ReadBoolean()",  "writer.WriteBoolean({0})",  1, -1),
                [SpecialType.System_Byte]     = ("reader.ReadByte()",     "writer.WriteByte({0})",     1,  1),
                [SpecialType.System_SByte]    = ("reader.ReadSByte()",    "writer.WriteSByte({0})",    1,  1),
                [SpecialType.System_Int16]    = ("reader.ReadInt16()",    "writer.WriteInt16({0})",    2,  2),
                [SpecialType.System_UInt16]   = ("reader.ReadUInt16()",   "writer.WriteUInt16({0})",   2,  2),
                [SpecialType.System_Char]     = ("reader.ReadChar()",     "writer.WriteChar({0})",     2,  2),
                [SpecialType.System_Int32]    = ("reader.ReadInt32()",    "writer.WriteInt32({0})",    4,  4),
                [SpecialType.System_UInt32]   = ("reader.ReadUInt32()",   "writer.WriteUInt32({0})",   4,  4),
                [SpecialType.System_Single]   = ("reader.ReadSingle()",   "writer.WriteSingle({0})",   4,  4),
                [SpecialType.System_Int64]    = ("reader.ReadInt64()",    "writer.WriteInt64({0})",    8,  8),
                [SpecialType.System_UInt64]   = ("reader.ReadUInt64()",   "writer.WriteUInt64({0})",   8,  8),
                [SpecialType.System_Double]   = ("reader.ReadDouble()",   "writer.WriteDouble({0})",   8,  8),
                [SpecialType.System_Decimal]  = ("reader.ReadDecimal()",  "writer.WriteDecimal({0})", 16, -1),
                [SpecialType.System_String]   = ("reader.ReadString()",   "writer.WriteString({0})",  -1, -1),
            };

            static bool TryGetPrimitiveWriteCall(ITypeSymbol typeSymbol, string expression, out string call)
            {
                if (PrimitiveWireTable.TryGetValue(typeSymbol.SpecialType, out var info))
                {
                    call = string.Format(info.WriteFormat, expression);
                    return true;
                }
                call = string.Empty;
                return false;
            }

            static bool TryEmitPrimitiveRead(ITypeSymbol typeSymbol, string targetExpression, string indent, out string code)
            {
                if (typeSymbol.TypeKind == TypeKind.Enum && typeSymbol is INamedTypeSymbol enumType)
                {
                    var underlying = enumType.EnumUnderlyingType;
                    if (underlying != null && TryGetPrimitiveReadExpression(underlying, out string underlyingRead))
                    {
                        code = $"{indent}{targetExpression} = ({GetTypeDisplayName(typeSymbol)})({underlyingRead});\n";
                        return true;
                    }
                }

                if (TryGetPrimitiveReadExpression(typeSymbol, out string readExpr))
                {
                    code = $"{indent}{targetExpression} = {readExpr};\n";
                    return true;
                }

                code = string.Empty;
                return false;
            }

            static bool TryGetPrimitiveReadExpression(ITypeSymbol typeSymbol, out string expression)
            {
                if (PrimitiveWireTable.TryGetValue(typeSymbol.SpecialType, out var info))
                {
                    expression = info.Read;
                    return true;
                }
                expression = string.Empty;
                return false;
            }

            /// <summary>Fixed-wire-size primitives (and enums). Used for the EnsureCapacity bulk sum.</summary>
            public static bool TryGetFixedPrimitiveWireSize(ITypeSymbol typeSymbol, out int size)
            {
                if (typeSymbol.TypeKind == TypeKind.Enum && typeSymbol is INamedTypeSymbol enumType)
                {
                    var underlying = enumType.EnumUnderlyingType;
                    if (underlying != null)
                    {
                        return TryGetFixedPrimitiveWireSize(underlying, out size);
                    }
                    size = 0;
                    return false;
                }

                if (PrimitiveWireTable.TryGetValue(typeSymbol.SpecialType, out var info) && info.FixedSize > 0)
                {
                    size = info.FixedSize;
                    return true;
                }
                size = 0;
                return false;
            }

            /// <summary>Whether the element type is a memory-block copy candidate (excludes boolean, string, and variable-size formats).</summary>
            static bool IsBulkCopyable(ITypeSymbol typeSymbol)
            {
                if (typeSymbol.TypeKind == TypeKind.Enum && typeSymbol is INamedTypeSymbol enumType)
                {
                    var underlying = enumType.EnumUnderlyingType;
                    return underlying != null && GetBulkElementSize(underlying) > 0;
                }
                return GetBulkElementSize(typeSymbol) > 0;
            }

            static int GetBulkElementSize(ITypeSymbol typeSymbol)
            {
                if (typeSymbol.TypeKind == TypeKind.Enum && typeSymbol is INamedTypeSymbol enumType && enumType.EnumUnderlyingType != null)
                {
                    return GetBulkElementSize(enumType.EnumUnderlyingType);
                }

                return PrimitiveWireTable.TryGetValue(typeSymbol.SpecialType, out var info) ? info.BulkSize : -1;
            }
        }
    }
}
