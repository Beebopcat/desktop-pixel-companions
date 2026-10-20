namespace AzerothCompanion.Services;

public enum DirectTextVerification { Candidate, Verified, Revoked }
public enum DirectTextMethod { StructuredExport, CalibratedHook }
public sealed record DirectTextProfile(string Id, string PublicTitle, string ExecutablePath, string ExecutableSha256,
    GameExecutableIdentity ExecutableIdentity, string Distribution, string? SteamAppId, string Edition,
    DirectTextMethod Method, string NativeProfilePath, string NativeProfileSha256, string HelperSha256,
    DirectTextVerification Verification, string? EvidencePath = null, string? EvidenceSha256 = null,
    string? HookDllSha256 = null, string? CalibrationProfilePath = null, string? CalibrationProfileSha256 = null);
public sealed record DirectTextReadiness(bool Active, bool Connected, string? ProfileId, string Message);
public sealed record GameHookProcessIdentity(uint ProcessId, long StartedFileTime, string ExecutablePath,
    string ExecutableSha256, GameExecutableIdentity FileIdentity, string Architecture);
public interface IGameHookProcessInspector
{
    Task<GameHookProcessIdentity> InspectAsync(uint processId, CancellationToken ct);
    bool IsCurrent(GameHookProcessIdentity identity);
}
public sealed record GameHookRuntime(int Schema, string HelperPath, string HelperSha256,
    string ProfileStorePath, string SessionRoot, string HookDllPath, string HookDllSha256,
    string? X86HelperPath = null, string? X86HelperSha256 = null, string? X86HookDllPath = null, string? X86HookDllSha256 = null);
public sealed record DirectTextProofPin(string Path, string Sha256);
