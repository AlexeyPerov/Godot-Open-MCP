#nullable enable

namespace GodotOpenMcp.Verify.Core
{
    /// <summary>
    /// Why a verify pass is running. Drives budget and diagnostic behavior in
    /// <see cref="VerifyRunner"/>: <see cref="Checkpoint"/> runs are expected to stay fast (the gate
    /// calls them on every mutation), <see cref="Validate"/> is a scoped post-mutation check, and
    /// <see cref="Full"/> is an explicit whole-project scan. Ported (copy) from Unity Open MCP's
    /// <c>VerifyRunMode</c>; the enum is identical because it is part of the stable rule contract.
    /// </summary>
    public enum VerifyRunMode
    {
        Checkpoint,
        Validate,
        Full
    }
}
