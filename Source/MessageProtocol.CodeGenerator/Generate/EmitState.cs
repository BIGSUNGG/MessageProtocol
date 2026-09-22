using Microsoft.CodeAnalysis;

namespace MessageProtocol.CodeGenerator.Generate
{
    /// <summary>Emit progress state: CollectionsMarshal availability, unsupported-member collection, generated local-name numbering.</summary>
    internal sealed class EmitState
    {
        readonly HashSet<string> _reportedKeys = new();
        int _uniqueId;

        public EmitState(bool hasCollectionsMarshal)
        {
            HasCollectionsMarshal = hasCollectionsMarshal;
        }

        /// <summary>
        /// Unique number for generated local names (`__item3`, `__coll1`, etc.). State is **per emit**, so the same
        /// input always gets the same numbers. When this was a process-global static counter, generated code depended
        /// on the compiler process's earlier compilation history and was nondeterministic (same input → different
        /// text), which kept breaking Roslyn's generated-output comparison — unrelated edits caused generated trees to
        /// be replaced and recompiled (Known-Issues KI-3). Each emit runs on a single thread per type, so no lock is
        /// needed (the reason the global counter needed `Interlocked` is gone entirely).
        /// </summary>
        public int NextUniqueId() => ++_uniqueId;

        public bool HasCollectionsMarshal { get; }

        public List<UnsupportedMemberInfo> UnsupportedMembers { get; } = new();

        public void ReportUnsupported(Location location, string typeName, string memberOrTypeName)
        {
            Report(location, typeName, memberOrTypeName, UnsupportedMemberKind.UnsupportedType);
        }

        public void ReportNotAssignable(Location location, string typeName, string memberOrTypeName)
        {
            Report(location, typeName, memberOrTypeName, UnsupportedMemberKind.NotAssignable);
        }

        void Report(Location location, string typeName, string memberOrTypeName, UnsupportedMemberKind kind)
        {
            // The dedup key includes the reason (kind) and location — it merges only double reports of the same member
            // under the same rule from the write and read emits. With a name+type-only key, a member that was both an
            // unsupported type (MSGPROT006) and not assignable (MSGPROT011) silently lost the second rule, and
            // same-named/same-typed members in different nested types lost the second location (audit ledger LOW, 2026-09-08).
            string key = kind + "\0" + memberOrTypeName + "\0" + typeName + "\0" + location.ToString();
            if (!_reportedKeys.Add(key))
            {
                return;
            }

            UnsupportedMembers.Add(new UnsupportedMemberInfo(location, typeName, memberOrTypeName, kind));
        }
    }

    /// <summary>Why a member is unsupported: the type itself is unsupported, or it is not assignable (no setter).</summary>
    internal enum UnsupportedMemberKind
    {
        UnsupportedType,
        NotAssignable,
    }

    internal readonly struct UnsupportedMemberInfo
    {
        public UnsupportedMemberInfo(Location location, string typeName, string memberOrTypeName, UnsupportedMemberKind kind)
        {
            Location = location;
            TypeName = typeName;
            MemberOrTypeName = memberOrTypeName;
            Kind = kind;
        }

        public Location Location { get; }
        public string TypeName { get; }
        public string MemberOrTypeName { get; }
        public UnsupportedMemberKind Kind { get; }
    }
}
