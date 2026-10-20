using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzerothCompanion.Services;

/// <summary>Explicit local pins. Missing stores are empty; candidates never attach automatically.</summary>
public sealed class DirectTextProfileStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path;
    internal static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private sealed record Document(int Schema, IReadOnlyList<DirectTextProfile> Profiles);
    private sealed record Promotion(int Schema, string Kind, string ProfileId, IReadOnlyList<DirectTextProofPin> Sessions, DirectTextProtectionCheck ProtectedGameCheck);
    public DirectTextProfileStore(string? path = null) => this.path = GameHookFiles.LocalPath(path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzerothCompanion", "verified-direct-text-profiles.json"));
    public string StorePath => path;
    public async Task<IReadOnlyList<DirectTextProfile>> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (!File.Exists(path)) return [];
        using var data = GameHookFiles.Json(await GameHookFiles.ReadAsync(path, 262144, ct).ConfigureAwait(false));
        var document = data.RootElement.Deserialize<Document>(Options);
        if (document is not { Schema: 1 } || document.Profiles == null || document.Profiles.Count > 64) throw new InvalidDataException("Unsupported direct-text profile store.");
        foreach (var profile in document.Profiles) Validate(profile);
        if (document.Profiles.GroupBy(p => p.Id, StringComparer.Ordinal).Any(g => g.Count() != 1)
            || document.Profiles.GroupBy(Binding).Any(g => g.Count() != 1)) throw new InvalidDataException("Ambiguous direct-text profile ID or executable/edition/source binding.");
        return document.Profiles;
    }
    public Task SaveCandidateAsync(DirectTextProfile profile, CancellationToken ct = default)
    {
        profile = profile with { Verification = DirectTextVerification.Candidate, EvidencePath = null, EvidenceSha256 = null }; Validate(profile);
        return MutateAsync(profiles => { profiles.RemoveAll(p => p.Id == profile.Id); profiles.Add(profile); return Task.FromResult(true); }, ct);
    }
    /// <summary>Three hash-pinned, distinct session traces must demonstrate exact expected literal output and owned interruption/detachment.</summary>
    public async Task PromoteAsync(string id, IReadOnlyList<DirectTextProofPin> sessions, CancellationToken ct = default)
    {
        if (!GameHookFiles.Id(id) || sessions == null || sessions.Count != 3) throw new InvalidDataException("Promotion requires one explicit candidate ID and exactly three session proofs.");
        await MutateAsync(async profiles =>
        {
            var candidate = profiles.SingleOrDefault(p => p.Id == id) ?? throw new InvalidDataException("Stage the exact candidate before promotion.");
            if (candidate.Verification != DirectTextVerification.Candidate) throw new InvalidDataException("Only a staged candidate can be promoted.");
            var protectedCheck = DirectTextProtection.Review(candidate);
            await DirectTextEvidence.ValidateSessionsAsync(candidate, sessions, ct).ConfigureAwait(false);
            var nativeBytes = await GameHookFiles.ReadAsync(candidate.NativeProfilePath, 16384, ct).ConfigureAwait(false);
            using var nativeJson = GameHookFiles.Json(nativeBytes);
            var verifiedFields = nativeJson.RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone());
            verifiedFields["status"] = JsonSerializer.SerializeToElement("verified");
            byte[] verifiedBytes = JsonSerializer.SerializeToUtf8Bytes(verifiedFields, Options);
            string verifiedPath = Path.Combine(Path.GetDirectoryName(path)!, "native", id + "-" + Guid.NewGuid().ToString("N") + ".json");
            await AtomicWriteAsync(verifiedPath, verifiedBytes, ct).ConfigureAwait(false);
            string evidencePath = Path.Combine(Path.GetDirectoryName(path)!, "proofs", id + "-" + Guid.NewGuid().ToString("N") + ".json");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Promotion(1, "direct-text-three-session-promotion", id, sessions.ToArray(), protectedCheck), Options);
            await AtomicWriteAsync(evidencePath, bytes, ct).ConfigureAwait(false);
            profiles.RemoveAll(p => p.Id == id);
            profiles.Add(candidate with { Verification = DirectTextVerification.Verified, EvidencePath = evidencePath, EvidenceSha256 = GameHookFiles.Sha(bytes),
                NativeProfilePath = verifiedPath, NativeProfileSha256 = GameHookFiles.Sha(verifiedBytes),
                CalibrationProfilePath = candidate.NativeProfilePath, CalibrationProfileSha256 = candidate.NativeProfileSha256 });
            return true;
        }, ct).ConfigureAwait(false);
    }
    public async Task<bool> RevokeAsync(string id, CancellationToken ct = default)
    {
        if (!GameHookFiles.Id(id)) throw new ArgumentException("Revoke requires the explicit profile ID.", nameof(id));
        bool found = false;
        await MutateAsync(profiles =>
        {
            var current = profiles.SingleOrDefault(p => p.Id == id); if (current == null) return Task.FromResult(false);
            found = true; profiles.RemoveAll(p => p.Id == id); profiles.Add(current with { Verification = DirectTextVerification.Revoked }); return Task.FromResult(true);
        }, ct).ConfigureAwait(false); return found;
    }
    public async Task<bool> RemoveAsync(string id, CancellationToken ct = default)
    {
        if (!GameHookFiles.Id(id)) throw new ArgumentException("Remove requires the explicit profile ID.", nameof(id));
        bool found = false;
        await MutateAsync(profiles => { found = profiles.RemoveAll(p => p.Id == id) != 0; return Task.FromResult(found); }, ct).ConfigureAwait(false); return found;
    }
    private static (string, string?, string, DirectTextMethod) Binding(DirectTextProfile p) => (p.ExecutablePath.ToUpperInvariant(), p.SteamAppId, p.Edition, p.Method);
    internal static void Validate(DirectTextProfile p)
    {
        if (p == null || !GameHookFiles.Id(p.Id) || string.IsNullOrWhiteSpace(p.PublicTitle) || p.PublicTitle.Length > 160
            || p.PublicTitle.Any(char.IsControl) || !GameHookFiles.Hash(p.ExecutableSha256) || !GameHookFiles.Hash(p.NativeProfileSha256)
            || !GameHookFiles.Hash(p.HelperSha256) || !GameHookFiles.Hash(p.HookDllSha256) || !Enum.IsDefined(p.Verification) || !Enum.IsDefined(p.Method)
            || string.IsNullOrWhiteSpace(p.Edition) || p.Edition.Length > 160 || p.Edition.Any(char.IsControl)
            || p.ExecutableIdentity is not { Bytes: > 0 } || p.ExecutableIdentity.Bytes > LocalGameProfileStore.MaximumExecutableBytes
            || string.IsNullOrWhiteSpace(p.ExecutableIdentity.FileIdentity)) throw new InvalidDataException("Invalid direct-text profile pins.");
        if (GameHookFiles.LocalPath(p.ExecutablePath) != p.ExecutablePath || !p.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || GameHookFiles.LocalPath(p.NativeProfilePath) != p.NativeProfilePath) throw new InvalidDataException("Invalid direct-text profile paths.");
        if (p.Distribution == "Steam")
        {
            if (string.IsNullOrEmpty(p.SteamAppId) || p.SteamAppId.Length > 10 || !p.SteamAppId.All(char.IsAsciiDigit)) throw new InvalidDataException("Steam direct-text profile requires its exact app ID.");
        }
        else if (p.Distribution != "Local" || p.SteamAppId != null) throw new InvalidDataException("Non-Steam profiles cannot carry a Steam ID.");
        if (p.Verification == DirectTextVerification.Verified && (p.EvidencePath == null || !GameHookFiles.Hash(p.EvidenceSha256))) throw new InvalidDataException("Verified profile lacks exact evidence pins.");
        _ = DirectTextProtection.Review(p);
    }
    internal async Task VerifyEvidenceAsync(DirectTextProfile p, CancellationToken ct)
    {
        Validate(p); if (p.Verification != DirectTextVerification.Verified) throw new InvalidDataException("Unverified direct-text profile cannot attach.");
        byte[] evidence = await GameHookFiles.ReadAsync(p.EvidencePath!, 16384, ct).ConfigureAwait(false);
        if (GameHookFiles.Sha(evidence) != p.EvidenceSha256) throw new InvalidDataException("Direct-text promotion receipt changed.");
        using var document = GameHookFiles.Json(evidence); var promotion = document.RootElement.Deserialize<Promotion>(Options);
        if (promotion is not { Schema: 1, Kind: "direct-text-three-session-promotion" } || promotion.ProfileId != p.Id || promotion.Sessions == null) throw new InvalidDataException("Invalid direct-text promotion receipt.");
        if (promotion.ProtectedGameCheck != DirectTextProtection.Review(p)) throw new InvalidDataException("Promotion lacks the current bounded protected-game review.");
        if (p.CalibrationProfilePath == null || !GameHookFiles.Hash(p.CalibrationProfileSha256)) throw new InvalidDataException("Verified profile lacks its original calibration pins.");
        var calibration = p with { NativeProfilePath = p.CalibrationProfilePath, NativeProfileSha256 = p.CalibrationProfileSha256!, Verification = DirectTextVerification.Candidate };
        await DirectTextEvidence.ValidateSessionsAsync(calibration, promotion.Sessions, ct).ConfigureAwait(false);
        var original = await GameHookNativeProfile.LoadAsync(calibration.NativeProfilePath, calibration.NativeProfileSha256, calibration, null, ct, "candidate").ConfigureAwait(false);
        var verified = await GameHookNativeProfile.LoadAsync(p.NativeProfilePath, p.NativeProfileSha256, p, null, ct).ConfigureAwait(false);
        if (original != verified) throw new InvalidDataException("Promoted hook parameters differ from the three calibrated sessions.");
    }
    private async Task MutateAsync(Func<List<DirectTextProfile>, Task<bool>> mutation, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Explicit mutations only. A missing List never creates this directory.
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); GameHookFiles.NoReparse(path);
            await using var writeLock = await LockAsync(ct).ConfigureAwait(false);
            var profiles = (await ListAsync(ct).ConfigureAwait(false)).ToList();
            if (!await mutation(profiles).ConfigureAwait(false)) return;
            foreach (var p in profiles) Validate(p);
            if (profiles.Count > 64 || profiles.GroupBy(Binding).Any(g => g.Count() != 1)) throw new InvalidDataException("Direct-text profile limit or duplicate binding.");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, profiles), Options);
            if (bytes.Length > 262144) throw new InvalidDataException("Direct-text store exceeded its bound.");
            await AtomicWriteAsync(path, bytes, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    internal static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        path = GameHookFiles.LocalPath(path); GameHookFiles.NoReparse(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); GameHookFiles.NoReparse(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            { await file.WriteAsync(bytes, ct).ConfigureAwait(false); await file.FlushAsync(ct).ConfigureAwait(false); file.Flush(true); }
            ct.ThrowIfCancellationRequested(); GameHookFiles.NoReparse(path); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task<FileStream> LockAsync(CancellationToken ct)
    {
        for (int n = 0; n < 100; n++)
        {
            ct.ThrowIfCancellationRequested(); GameHookFiles.NoReparse(path + ".lock");
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(25, ct).ConfigureAwait(false); }
        }
        throw new IOException("Another direct-text profile writer is busy; nothing was installed.");
    }
}
