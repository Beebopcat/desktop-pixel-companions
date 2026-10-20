using System.Text.Json;

namespace AzerothCompanion.Services;

public sealed record DirectTextTraceEvent(long Sequence, string Type, int Utf8Bytes = 0, string? TextSha256 = null,
    string? Check = null, string? ExpectedTextSha256 = null, string? Stage = null);
public sealed record DirectTextSessionTrace(int Schema, string Kind, string ProfileId, string SessionNonce, string StreamId,
    uint GamePid, long GameStartFileTime, string GameExePath, string GameExeSha256, GameExecutableIdentity GameFileIdentity,
    string? SteamAppId, string Edition, string NativeProfileSha256, string HelperSha256, string HookDllSha256,
    uint HelperPid, long HelperStartFileTime, uint KernelPipeClientPid, string Architecture, string HookCode,
    string ContextHex, string SubcontextHex, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc,
    long InterruptedAfterSequence, uint ExitedHelperPid, long ExitedHelperStartFileTime, IReadOnlyList<DirectTextTraceEvent> Events);

/// <summary>Checks hash-pinned local calibration records. This is reproducible local evidence, not cryptographic attestation of human review.</summary>
internal static class DirectTextEvidence
{
    internal static async Task ValidateSessionsAsync(DirectTextProfile p, IReadOnlyList<DirectTextProofPin> pins, CancellationToken ct)
    {
        if (pins.Count != 3 || pins.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3) throw new InvalidDataException("Three distinct calibration session files are required.");
        var native = await GameHookNativeProfile.LoadAsync(p.NativeProfilePath, p.NativeProfileSha256, p, null, ct, "candidate").ConfigureAwait(false);
        var nonces = new HashSet<string>(StringComparer.Ordinal); var streams = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pin in pins)
        {
            if (!GameHookFiles.Hash(pin.Sha256)) throw new InvalidDataException("Calibration proof lacks a SHA-256 pin.");
            byte[] bytes = await GameHookFiles.ReadAsync(pin.Path, 65536, ct).ConfigureAwait(false);
            if (GameHookFiles.Sha(bytes) != pin.Sha256) throw new InvalidDataException("Calibration session proof changed.");
            using var document = GameHookFiles.Json(bytes); var t = document.RootElement.Deserialize<DirectTextSessionTrace>(DirectTextProfileStore.Options);
            if (t is not { Schema: 1, Kind: "managed-direct-text-calibration" } || t.ProfileId != p.Id
                || !GameHookFiles.Hash(t.SessionNonce) || !GameHookFiles.Id(t.StreamId) || !nonces.Add(t.SessionNonce) || !streams.Add(t.StreamId)
                || t.GamePid == 0 || t.GameStartFileTime <= 0 || t.HelperPid == 0 || t.HelperStartFileTime <= 0 || t.HelperPid == t.GamePid
                || t.KernelPipeClientPid != t.HelperPid || t.GameExePath != p.ExecutablePath || t.GameExeSha256 != p.ExecutableSha256
                || t.GameFileIdentity != p.ExecutableIdentity || t.SteamAppId != p.SteamAppId || t.Edition != p.Edition
                || t.NativeProfileSha256 != p.NativeProfileSha256 || t.HelperSha256 != p.HelperSha256 || t.HookDllSha256 != p.HookDllSha256
                || t.Architecture != native.Architecture || t.HookCode != native.HookCode || t.ContextHex != native.ContextHex || t.SubcontextHex != native.SubcontextHex
                || t.StartedUtc.Offset != TimeSpan.Zero || t.CompletedUtc.Offset != TimeSpan.Zero || t.CompletedUtc <= t.StartedUtc
                || t.CompletedUtc - t.StartedUtc > TimeSpan.FromMinutes(30) || t.Events is not { Count: >= 6 and <= 128 })
                throw new InvalidDataException("Calibration session does not bind the exact game/edition/source/kernel-owner pins.");
            if (t.Events[^1].Type != "stopped" || t.Events.Count(e => e.Type == "calibration-attached") != 1
                || !t.Events.Any(e => e.Type == "status" && e.Stage == "detached") || t.ExitedHelperPid != t.HelperPid || t.ExitedHelperStartFileTime != t.HelperStartFileTime
                || t.InterruptedAfterSequence <= 1 || t.InterruptedAfterSequence >= t.Events[^1].Sequence) throw new InvalidDataException("Calibration lacks ready, owned interruption and actual owned-helper exit evidence.");
            var checks = new HashSet<string>(StringComparer.Ordinal);
            long sequence = 0; bool attached = false;
            foreach (var e in t.Events)
            {
                if (e.Sequence != ++sequence || e.Type is not ("calibration-attached" or "text" or "status" or "stopped")) throw new InvalidDataException("Calibration event trace is incomplete, replayed or malformed.");
                if (e.Type == "calibration-attached") attached = true;
                if (e.Type == "text" && !attached || e.Type == "stopped" && e.Sequence != t.Events[^1].Sequence) throw new InvalidDataException("Calibration ownership lifecycle is out of order.");
                if (e.Type == "text")
                {
                    if (e.Utf8Bytes is <= 0 or > 8192 || !GameHookFiles.Hash(e.TextSha256)) throw new InvalidDataException("Calibration has no bounded literal text digest.");
                    if (e.Check != null)
                    {
                        if (e.Check is not ("dialogue" or "choice" or "transition") || e.ExpectedTextSha256 != e.TextSha256 || e.Sequence > t.InterruptedAfterSequence)
                            throw new InvalidDataException("Calibration expected dialogue/choice/transition does not match actual literal output.");
                        checks.Add(e.Check);
                    }
                }
                else if (e.Utf8Bytes != 0 || e.TextSha256 != null || e.Check != null || e.ExpectedTextSha256 != null) throw new InvalidDataException("Non-text calibration record contains dialogue metadata.");
            }
            if (!checks.SetEquals(["dialogue", "choice", "transition"])) throw new InvalidDataException("Every session must demonstrate dialogue, choices and transitions before owned interruption.");
        }
    }
}

internal sealed record GameHookNativeProfile(string Id, string Architecture, string HookCode, string ContextHex, string SubcontextHex, int FlushMs)
{
    internal static async Task<GameHookNativeProfile> LoadAsync(string path, string sha, DirectTextProfile profile, string? architecture, CancellationToken ct, string expectedStatus = "verified")
    {
        var bytes = await GameHookFiles.ReadAsync(path, 16384, ct).ConfigureAwait(false);
        if (GameHookFiles.Sha(bytes) != sha) throw new InvalidDataException("Calibrated native profile changed.");
        using var json = GameHookFiles.Json(bytes); var r = json.RootElement;
        string S(string key) => r.GetProperty(key).GetString() ?? throw new InvalidDataException("Missing native profile field.");
        var allowed = new HashSet<string>(["schema", "id", "status", "gameExeSha256", "architecture", "hookCode", "contextHex", "subcontextHex", "flushMs", "provenance"], StringComparer.Ordinal);
        if (r.ValueKind != JsonValueKind.Object || r.EnumerateObject().Any(x => !allowed.Contains(x.Name)) || r.GetProperty("schema").GetInt32() != 1
            || S("id") != profile.Id || S("status") != expectedStatus || S("gameExeSha256") != profile.ExecutableSha256)
            throw new InvalidDataException("Native profile does not bind the verified exact game.");
        string arch = S("architecture"), hook = S("hookCode"), context = S("contextHex"), sub = S("subcontextHex"), provenance = S("provenance");
        static bool Hex(string value) => value.Length is > 0 and <= 16 && value.All(char.IsAsciiHexDigit);
        int flush = r.GetProperty("flushMs").GetInt32();
        if (arch is not ("x86" or "x64") || architecture != null && arch != architecture || hook.Length is < 2 or > 512 || hook.Any(char.IsControl)
            || !Hex(context) || !Hex(sub) || flush is < 100 or > 1500 || string.IsNullOrWhiteSpace(provenance) || provenance.Length > 2048)
            throw new InvalidDataException("Unsupported calibrated native hook parameters.");
        return new(profile.Id, arch, hook, context, sub, flush);
    }
}
