using MessageProtocol;

namespace SandboxMessages;

// ---------- S12: Distributed declaration ----------
// Adds a construction via a separate carrier type, without modifying the Envelope<T> declaration itself.
[GenericMessage(typeof(Envelope<TreeNode>), ClassId = 3)]
static class EnvelopeExtraConstructions { }
