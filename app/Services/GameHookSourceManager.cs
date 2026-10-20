using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;

namespace AzerothCompanion.Services;

/// <summary>Owns one exact, reviewed helper attachment. Never scans hooks or creates game profiles.</summary>
internal sealed class GameHookSourceManager : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim configureGate = new(1, 1);
    private readonly IGameHookProcessInspector inspector;
    private readonly IGameProfileFileSystem files;
    private readonly string runtimePath;
    private GameHookStreamReader? reader;
    private Process? helper;
    private WindowChoice? target;
    private Task drain = Task.CompletedTask;
    private bool disposed;
    private long epoch;
    private CancellationTokenSource? setupCancellation;
    private GameHookBinding? calibrationBinding;
    private DirectTextProfile? calibrationProfile;
    private GameHookNativeProfile? calibrationNative;
    private DirectTextReadiness readiness = new(false, false, null, "No verified direct-text profile is configured for this game; accessibility/OCR remain available.");
    internal GameHookSourceManager(IGameHookProcessInspector inspector, string? runtimePath = null, IGameProfileFileSystem? files = null)
    { this.inspector = inspector; this.files = files ?? new LocalExecutableFileSystem(); this.runtimePath = GameHookFiles.LocalPath(runtimePath ?? Path.Combine(AppContext.BaseDirectory, "DirectText", "Runtime.json")); }
    internal bool Active { get { lock (gate) return reader?.Active == true; } }
    internal bool Connected { get { lock (gate) return reader?.Connected == true; } }
    internal DirectTextReadiness Readiness { get { lock (gate) return readiness with { Active = reader?.Active == true, Connected = reader?.Connected == true }; } }
    internal async Task<DirectTextReadiness> ConfigureAsync(WindowChoice window, GameLaunchIdentity? identity, GameHookProcessIdentity game, CancellationToken ct, string? calibrationId = null)
    {
        await configureGate.WaitAsync(ct).ConfigureAwait(false);
        long owner = 0;
        try
        {
            Suspend(); await DrainAsync(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
            lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); owner = ++epoch; setupCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct); ct = setupCancellation.Token; }
            if (DirectTextProtection.BlockReason(identity?.StoreId, game.ExecutablePath) is { } blocked) return Set(new(false, false, null, blocked));
            if (!File.Exists(runtimePath)) return Set(new(false, false, null, "Direct-text helper is not installed in this candidate; no game attachment attempted. Accessibility/OCR remain available."));
            using var runtimeJson = GameHookFiles.Json(await GameHookFiles.ReadAsync(runtimePath, 16384, ct).ConfigureAwait(false));
            var runtime = runtimeJson.RootElement.Deserialize<GameHookRuntime>(DirectTextProfileStore.Options) ?? throw new InvalidDataException("Missing direct-text runtime metadata.");
            if (game.Architecture == "x86") runtime = runtime with { HelperPath = runtime.X86HelperPath ?? "", HelperSha256 = runtime.X86HelperSha256 ?? "",
                HookDllPath = runtime.X86HookDllPath ?? "", HookDllSha256 = runtime.X86HookDllSha256 ?? "" };
            if (runtime.Schema != 1 || !GameHookFiles.Hash(runtime.HelperSha256) || !GameHookFiles.Hash(runtime.HookDllSha256)) throw new InvalidDataException("Unsupported direct-text runtime pins.");
            string helperPath = RuntimePath(runtime.HelperPath), dllPath = RuntimePath(runtime.HookDllPath);
            if (!helperPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !dllPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetDirectoryName(helperPath), Path.GetDirectoryName(dllPath), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Hook DLL must be pinned beside the helper.");
            var store = new DirectTextProfileStore(RuntimePath(runtime.ProfileStorePath));
            var profiles = await store.ListAsync(ct).ConfigureAwait(false);
            var matches = profiles.Where(p => p.Verification == (calibrationId == null ? DirectTextVerification.Verified : DirectTextVerification.Candidate)
                && (calibrationId == null || p.Id == calibrationId) && Matches(p, game, identity)).ToArray();
            if (matches.Length == 0) return Set(new(false, false, null, "No verified exact executable/edition direct-text profile; calibration is required. Accessibility/OCR remain available."));
            if (matches.Length != 1) throw new InvalidDataException("Ambiguous verified direct-text source binding.");
            var profile = matches[0];
            if (profile.Method != DirectTextMethod.CalibratedHook) return Set(new(false, false, profile.Id, "This structured-export adapter has not been implemented; no hook was substituted. Accessibility/OCR remain available."));
            if (profile.HelperSha256 != runtime.HelperSha256 || profile.HookDllSha256 != runtime.HookDllSha256) throw new InvalidDataException("Reviewed helper/DLL pins differ from this installed candidate.");
            if (calibrationId == null) await store.VerifyEvidenceAsync(profile, ct).ConfigureAwait(false);
            var native = await GameHookNativeProfile.LoadAsync(profile.NativeProfilePath, profile.NativeProfileSha256, profile, game.Architecture, ct, calibrationId == null ? "verified" : "candidate").ConfigureAwait(false);
            var helperFile = await files.FingerprintAsync(helperPath, LocalGameProfileStore.MaximumExecutableBytes, LocalGameProfileStore.HashTimeout, ct).ConfigureAwait(false);
            var dllFile = await files.FingerprintAsync(dllPath, LocalGameProfileStore.MaximumExecutableBytes, LocalGameProfileStore.HashTimeout, ct).ConfigureAwait(false);
            if (helperFile.Sha256 != runtime.HelperSha256 || dllFile.Sha256 != runtime.HookDllSha256) throw new InvalidDataException("Direct-text helper or hook DLL changed.");
            // Initial content checks are bounded; live checks use exact file ID/length/write metadata and process start.
            var storeIdentity = files.ReadIdentity(store.StorePath); var profileIdentity = files.ReadIdentity(profile.NativeProfilePath);
            var runtimeIdentity = files.ReadIdentity(runtimePath); var evidenceIdentity = calibrationId == null ? files.ReadIdentity(profile.EvidencePath!) : null;
            ct.ThrowIfCancellationRequested(); if (!inspector.IsCurrent(game)) throw new IOException("Game changed before attachment.");
            string root = RuntimePath(runtime.SessionRoot); GameHookFiles.NoReparse(root); Directory.CreateDirectory(root); GameHookFiles.NoReparse(root);
            string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), streamId = Guid.NewGuid().ToString("N"), pipeName = "AzerothGameText-" + Guid.NewGuid().ToString("N");
            string requestPath = Path.Combine(root, streamId + "-request.json");
            using var parent = Process.GetCurrentProcess();
            byte[] request = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, protocol = 1, mode = calibrationId == null ? "runtime" : "calibration", pipeName, sessionNonce = nonce, streamId, profileId = profile.Id,
                parentPid = Environment.ProcessId, parentStartFileTime = parent.StartTime.ToUniversalTime().ToFileTimeUtc(), gamePid = game.ProcessId, gameStartFileTime = game.StartedFileTime,
                gameExePath = game.ExecutablePath, gameExeSha256 = game.ExecutableSha256, profilePath = profile.NativeProfilePath, profileSha256 = profile.NativeProfileSha256, hookDllSha256 = runtime.HookDllSha256 });
            if (request.Length > 16384) throw new InvalidDataException("Direct-text launch request exceeds its bound.");
            NamedPipeServerStream? pipe = GameHookStreamReader.CreatePipe(pipeName); Process? started = null; GameHookStreamReader? stream = null;
            try
            {
                await DirectTextProfileStore.AtomicWriteAsync(requestPath, request, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!inspector.IsCurrent(game) || files.ReadIdentity(helperPath) != helperFile.Identity || files.ReadIdentity(dllPath) != dllFile.Identity) throw new IOException("Exact source changed before helper start.");
                var start = new ProcessStartInfo(helperPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(helperPath)! };
                start.ArgumentList.Add("--request"); start.ArgumentList.Add(requestPath);
                lock (gate)
                {
                    if (disposed || owner != epoch) throw new OperationCanceledException(ct); ct.ThrowIfCancellationRequested();
                    started = Process.Start(start) ?? throw new IOException("Owned direct-text helper did not start.");
                }
                var owned = await inspector.InspectAsync((uint)started.Id, ct).ConfigureAwait(false);
                if (owned.Architecture != game.Architecture || !string.Equals(owned.ExecutablePath, helperPath, StringComparison.OrdinalIgnoreCase) || owned.ExecutableSha256 != runtime.HelperSha256 || owned.FileIdentity != helperFile.Identity) throw new IOException("Started helper does not match exact installed pins/architecture.");
                bool Current()
                {
                    try { return inspector.IsCurrent(game) && files.ReadIdentity(store.StorePath) == storeIdentity
                        && files.ReadIdentity(profile.NativeProfilePath) == profileIdentity && files.ReadIdentity(runtimePath) == runtimeIdentity
                        && (evidenceIdentity == null || files.ReadIdentity(profile.EvidencePath!) == evidenceIdentity) && files.ReadIdentity(dllPath) == dllFile.Identity; } catch { return false; }
                }
                var binding = new GameHookBinding(nonce, streamId, profile.Id, game, owned, DateTimeOffset.UtcNow,
                    Convert.ToUInt64(native.ContextHex, 16).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Convert.ToUInt64(native.SubcontextHex, 16).ToString(System.Globalization.CultureInfo.InvariantCulture));
                stream = new GameHookStreamReader(pipeName, binding, Current, ct, calibration: calibrationId != null, existingPipe: pipe, producerCurrent: () => inspector.IsCurrent(owned)); pipe = null;
                lock (gate)
                {
                    if (disposed || owner != epoch) throw new OperationCanceledException(ct); ct.ThrowIfCancellationRequested(); reader = stream; helper = started; target = window;
                    readiness = new(true, false, profile.Id, "Connecting to the exact verified game text source.");
                    if (calibrationId != null) { calibrationBinding = binding; calibrationProfile = profile; calibrationNative = native; }
                }
                stream.Start(); await stream.WaitAttachedAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested(); if (!Current()) throw new IOException("Source lease changed during attachment.");
                return Set(new(true, true, profile.Id, calibrationId != null ? "Explicit calibration attached; record-only, not narration or automatic readiness." : "Connected; waiting for literal game dialogue. OCR fallback is paused while this stream is connected."));
            }
            catch
            {
                pipe?.Dispose(); stream?.Dispose();
                if (started != null)
                {
                    // Only disconnect the helper we created. Native parent/pipe death detaches; never kill a game or unrelated process.
                    Task exit = AwaitOwnedExitAsync(started, stream); lock (gate) { reader = null; helper = null; target = null; drain = exit; }
                }
                throw;
            }
            finally { try { if (File.Exists(requestPath)) File.Delete(requestPath); } catch (IOException) { } }
        }
        catch (OperationCanceledException) { Suspend(); throw; }
        catch (Exception) { Suspend(); return Set(new(false, false, null, "Exact direct-text profile/helper validation failed or detachment is pending; no unverified hook was enabled. Accessibility/OCR remain available.")); }
        finally { configureGate.Release(); }
    }
    internal static bool Matches(DirectTextProfile p, GameHookProcessIdentity game, GameLaunchIdentity? identity)
    {
        if (!string.Equals(p.ExecutablePath, game.ExecutablePath, StringComparison.OrdinalIgnoreCase) || p.ExecutableSha256 != game.ExecutableSha256 || p.ExecutableIdentity != game.FileIdentity) return false;
        return p.Distribution == "Steam" ? identity is { IsVerified: true, Provenance: GameLaunchProvenance.SteamManifest }
            && identity.ProcessId == game.ProcessId && identity.StoreId == p.SteamAppId && identity.Title == p.Edition
            && string.Equals(identity.ProcessPath, game.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            : p.Distribution == "Local" && p.SteamAppId == null;
    }
    internal Task<GameTextObservation> ReadAsync(WindowChoice window, CancellationToken ct)
    {
        GameHookStreamReader? current; WindowChoice? selected; DirectTextReadiness state;
        lock (gate) { current = reader; selected = target; state = readiness; }
        if (current == null) return Task.FromResult(GameTextObservation.Empty(GameTextSource.VerifiedHook, GameTextStatus.Unavailable, state.Message));
        if (selected?.Handle != window.Handle || selected.ProcessId != window.ProcessId) { Suspend(); return Task.FromResult(GameTextObservation.Empty(GameTextSource.VerifiedHook, GameTextStatus.TargetChanged, "Direct-text source belongs to a different selected target.")); }
        return current.ReadAsync(window, ct);
    }
    internal void Suspend()
    {
        GameHookStreamReader? previous; Process? process; CancellationTokenSource? setup;
        lock (gate)
        {
            epoch++; setup = setupCancellation; setupCancellation = null;
            previous = reader; process = helper; reader = null; helper = null; target = null;
            calibrationBinding = null; calibrationProfile = null; calibrationNative = null;
            if (previous != null || process != null)
            {
                Task earlier = drain; drain = DrainAllAsync(earlier, previous, process);
                readiness = new(false, false, readiness.ProfileId, "Direct-text source suspended; resume explicitly to reconnect.");
            }
        }
        setup?.Cancel(); setup?.Dispose();
    }
    private static async Task DrainAllAsync(Task earlier, GameHookStreamReader? stream, Process? process)
    { try { await earlier.ConfigureAwait(false); } finally { await AwaitOwnedExitAsync(process, stream).ConfigureAwait(false); } }
    private static async Task AwaitOwnedExitAsync(Process? process, GameHookStreamReader? stream)
    {
        try { if (stream != null) await stream.StopAsync().ConfigureAwait(false); if (process != null) await process.WaitForExitAsync().ConfigureAwait(false); }
        finally { stream?.Dispose(); process?.Dispose(); }
    }
    internal async Task DrainAsync(CancellationToken ct)
    {
        Task pending; lock (gate) pending = drain;
        try { await pending.WaitAsync(TimeSpan.FromSeconds(6), ct).ConfigureAwait(false); }
        catch (TimeoutException) { throw new IOException("The owned helper has not exited after detachment; wait before reconnecting. No process was force-terminated."); }
    }
    internal IReadOnlyList<GameHookPacket> ReadCalibrationPackets()
    { lock (gate) return calibrationBinding != null && reader != null ? reader.CalibrationPackets : []; }
    internal async Task<DirectTextSessionTrace> FinishCalibrationAsync(IReadOnlyList<DirectTextExpectedCheck> checks, CancellationToken ct)
    {
        GameHookStreamReader stream; Process process; GameHookBinding binding; DirectTextProfile profile; GameHookNativeProfile native;
        lock (gate)
        {
            stream = reader ?? throw new InvalidOperationException("No calibration stream."); process = helper ?? throw new InvalidOperationException("No owned calibration helper.");
            binding = calibrationBinding ?? throw new InvalidOperationException("Production sessions cannot create calibration proofs.");
            profile = calibrationProfile!; native = calibrationNative!;
        }
        long interrupted = stream.CalibrationPackets.LastOrDefault()?.Sequence ?? 0;
        await stream.StopAsync().WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException("Owned calibration helper did not exit successfully.");
        var packets = stream.CalibrationPackets;
        var marked = checks.ToDictionary(c => c.Sequence);
        if (marked.Count != checks.Count || checks.Any(c => c.Kind is not ("dialogue" or "choice" or "transition"))) throw new InvalidDataException("Calibration check selection is ambiguous.");
        var events = packets.Select(p =>
        {
            if (p.Type != "text") return new DirectTextTraceEvent(p.Sequence, p.Stage == "calibration-attached" ? "calibration-attached" : p.Type, Stage: p.Stage);
            marked.TryGetValue(p.Sequence, out var check); byte[] literal = System.Text.Encoding.UTF8.GetBytes(p.Text!);
            return new DirectTextTraceEvent(p.Sequence, "text", literal.Length, GameHookFiles.Sha(literal), check?.Kind,
                check == null ? null : GameHookFiles.Sha(System.Text.Encoding.UTF8.GetBytes(check.ExpectedLiteralText)), p.Stage);
        }).ToArray();
        if (checks.Any(c => !packets.Any(p => p.Sequence == c.Sequence && p.Type == "text"))) throw new InvalidDataException("A check refers to an event that was not actually received.");
        return new(1, "managed-direct-text-calibration", profile.Id, binding.Nonce, binding.StreamId, binding.Game.ProcessId, binding.Game.StartedFileTime,
            binding.Game.ExecutablePath, binding.Game.ExecutableSha256, binding.Game.FileIdentity, profile.SteamAppId, profile.Edition, profile.NativeProfileSha256,
            profile.HelperSha256, profile.HookDllSha256!, binding.Helper.ProcessId, binding.Helper.StartedFileTime, binding.Helper.ProcessId,
            native.Architecture, native.HookCode, native.ContextHex, native.SubcontextHex, binding.StartedUtc, DateTimeOffset.UtcNow, interrupted,
            binding.Helper.ProcessId, binding.Helper.StartedFileTime, events);
    }
    private DirectTextReadiness Set(DirectTextReadiness value) { lock (gate) return readiness = value; }
    private string RuntimePath(string value)
    {
        if (Path.IsPathFullyQualified(value)) return GameHookFiles.LocalPath(value);
        if (string.IsNullOrWhiteSpace(value) || value.Split(['\\', '/']).Any(x => x is "." or "..") || value.Contains(':')) throw new InvalidDataException("Invalid packaged runtime path.");
        string root = Path.GetDirectoryName(runtimePath)!; string full = GameHookFiles.LocalPath(Path.Combine(root, value));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Runtime path escaped its package."); return full;
    }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; } Suspend(); }
}
