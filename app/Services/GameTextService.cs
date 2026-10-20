namespace AzerothCompanion.Services;

/// <summary>Selected-game literal text: explicit exports, verified hook, accessibility; OCR remains caller-owned.</summary>
public sealed class GameTextService : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim leaseGate = new(1, 1);
    private readonly Func<WindowChoice, bool> validateTarget;
    private readonly Func<DateTimeOffset> clock;
    private readonly IGameHookProcessInspector? inspector;
    private readonly GameHookSourceManager? hooks;
    private IReadOnlyList<IGameTextReader> readers = [];
    private CancellationTokenSource configurationCancellation = new();
    private CancellationTokenSource? automaticCancellation;
    private long configurationGeneration;
    private long automaticGeneration;
    private TimeSpan maximumAge = TimeSpan.FromSeconds(30);
    private bool disposed, directConnected;
    private GameHookProcessIdentity? processLease;
    private nint leaseWindow;
    private (nint Handle, uint Pid, string Fingerprint)? last;
    public GameTextService(GameTextOptions? options = null, IGameHookProcessInspector? processInspector = null, string? runtimePath = null)
    {
        validateTarget = GameTextScope.IsCurrent; clock = () => DateTimeOffset.UtcNow;
        inspector = processInspector ?? new GameHookProcessInspector(); hooks = new(inspector, runtimePath); Configure(options ?? new());
    }
    // Fixtures must explicitly supply their target validator. Production constructors always have a process inspector.
    internal GameTextService(IReadOnlyList<IGameTextReader> fixtureReaders, Func<WindowChoice, bool> validator, Func<DateTimeOffset> fixtureClock, IGameHookProcessInspector? processInspector = null)
    { readers = fixtureReaders; validateTarget = validator; clock = fixtureClock; inspector = processInspector; }
    public bool HasActiveDirectSource { get { lock (gate) return !disposed && (readers.Any(r => r.Source is GameTextSource.DialogueFile or GameTextSource.ClipboardBridge) || hooks?.Active == true); } }
    public bool DirectSourceConnected { get { lock (gate) return !disposed && (directConnected || hooks?.Connected == true); } }
    public void Configure(GameTextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumAge is { } age && age <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        var next = new List<IGameTextReader>();
        if (options.FileProfile != null) next.Add(new GameTextFileReader(options.FileProfile, clock));
        if (options.ClipboardProfile?.Enabled == true) next.Add(new GameTextClipboardReader(options.ClipboardProfile, options.AccessibilityTimeout, clock));
        if (options.AccessibilityEnabled) next.Add(new AccessibleTextService(options.AccessibilityTimeout, clock));
        CancellationTokenSource previous;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this); configurationGeneration++;
            previous = configurationCancellation; configurationCancellation = new(); readers = next;
            maximumAge = options.MaximumAge ?? TimeSpan.FromSeconds(30); last = null; directConnected = false;
        }
        previous.Cancel(); previous.Dispose();
    }
    public async Task<DirectTextReadiness> ConfigureAutomaticSourceAsync(WindowChoice target, GameLaunchIdentity? identity, CancellationToken ct = default)
    {
        CancellationTokenSource previous; CancellationTokenSource? oldAutomatic; long owner; CancellationTokenSource linked; CancellationToken setupToken;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this); configurationGeneration++; owner = ++automaticGeneration;
            previous = configurationCancellation; configurationCancellation = new();
            // Reader/options refresh does not own or detach the game session's automatic helper.
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct); setupToken = linked.Token;
            oldAutomatic = automaticCancellation; automaticCancellation = linked;
            processLease = null; leaseWindow = 0; last = null; directConnected = false;
            // Swapping lifetime ownership and suspending the previous setup are one ordered operation.
            hooks?.Suspend();
        }
        previous.Cancel(); previous.Dispose();
        oldAutomatic?.Cancel(); oldAutomatic?.Dispose();
        if (DirectTextProtection.BlockReason(identity?.StoreId, identity?.ProcessPath) is { } blocked) return new(false, false, null, blocked);
        if (!validateTarget(target)) return new(false, false, null, "Selected game target changed; no direct source configured.");
        if (inspector == null) return new(false, false, null, "Fixture has no automatic hook manager.");
        try
        {
            var game = await inspector.InspectAsync(target.ProcessId, setupToken).ConfigureAwait(false);
            setupToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (disposed || owner != automaticGeneration || !validateTarget(target) || !inspector.IsCurrent(game)) throw new OperationCanceledException(setupToken);
                processLease = game; leaseWindow = target.Handle;
            }
            // The session token, rather than a speech turn token, owns attachment lifetime.
            var result = hooks == null ? new(false, false, null, "No automatic direct-text helper configured.") : await hooks.ConfigureAsync(target, identity, game, setupToken).ConfigureAwait(false);
            setupToken.ThrowIfCancellationRequested();
            lock (gate) if (disposed || owner != automaticGeneration) throw new OperationCanceledException(setupToken);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(false, false, null, "Game executable fingerprint could not be verified; text reads remain blocked until the selected target is revalidated."); }
    }
    public void SuspendDirectSource()
    {
        CancellationTokenSource previous; CancellationTokenSource? automatic;
        lock (gate)
        {
            if (disposed) return; configurationGeneration++; automaticGeneration++; previous = configurationCancellation; configurationCancellation = new();
            automatic = automaticCancellation; automaticCancellation = null; directConnected = false; last = null; hooks?.Suspend();
        }
        previous.Cancel(); previous.Dispose(); automatic?.Cancel(); automatic?.Dispose();
    }
    public Task DrainDirectSourceAsync(CancellationToken ct = default) => hooks?.DrainAsync(ct) ?? Task.CompletedTask;
    public void Reset()
    {
        SuspendDirectSource(); Configure(new());
        lock (gate) { processLease = null; leaseWindow = 0; }
    }
    public Task<GameTextObservation> ReadAsync(WindowChoice target, CancellationToken cancellationToken = default) => ReadCoreAsync(target, false, cancellationToken);
    public Task<GameTextObservation> ReadDirectAsync(WindowChoice target, CancellationToken cancellationToken = default) => ReadCoreAsync(target, true, cancellationToken);
    private async Task<bool> EnsureLeaseAsync(WindowChoice target, long owner, CancellationToken ct)
    {
        if (inspector == null) return validateTarget(target); // Internal injected fixtures only.
        await leaseGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            GameHookProcessIdentity? current; nint handle;
            lock (gate) { current = processLease; handle = leaseWindow; if (disposed || owner != configurationGeneration) throw new OperationCanceledException(ct); }
            if (current != null) return handle == target.Handle && current.ProcessId == target.ProcessId && inspector.IsCurrent(current) && validateTarget(target);
            var game = await inspector.InspectAsync(target.ProcessId, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (disposed || owner != configurationGeneration) throw new OperationCanceledException(ct);
                if (!validateTarget(target) || !inspector.IsCurrent(game)) return false;
                processLease = game; leaseWindow = target.Handle; return true;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
        finally { leaseGate.Release(); }
    }
    private bool Current(WindowChoice target, long owner)
    {
        lock (gate) return !disposed && owner == configurationGeneration && validateTarget(target)
            && (inspector == null || processLease != null && leaseWindow == target.Handle && processLease.ProcessId == target.ProcessId && inspector.IsCurrent(processLease));
    }
    private async Task<GameTextObservation> ReadCoreAsync(WindowChoice target, bool directOnly, CancellationToken ct)
    {
        IReadOnlyList<IGameTextReader> selected; long owner; CancellationToken config; TimeSpan age;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this); selected = readers; owner = configurationGeneration; config = configurationCancellation.Token; age = maximumAge;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, config); linked.Token.ThrowIfCancellationRequested();
        if (!validateTarget(target) || !await EnsureLeaseAsync(target, owner, linked.Token).ConfigureAwait(false))
        { hooks?.Suspend(); return GameTextObservation.Empty(GameTextSource.None, GameTextStatus.TargetChanged, "Selected window/process start/executable lease is no longer valid.", clock()); }
        var ordered = new List<IGameTextReader>();
        ordered.AddRange(selected.Where(r => r.Source is GameTextSource.DialogueFile or GameTextSource.ClipboardBridge));
        if (hooks?.Active == true) ordered.Add(new HookAdapter(hooks));
        if (!directOnly) ordered.AddRange(selected.Where(r => r.Source is not (GameTextSource.DialogueFile or GameTextSource.ClipboardBridge)));
        var notices = new List<string>();
        foreach (var reader in ordered)
        {
            linked.Token.ThrowIfCancellationRequested(); GameTextObservation observation;
            try { observation = await reader.ReadAsync(target, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { notices.Add(reader.Source + ": source unavailable."); continue; }
            linked.Token.ThrowIfCancellationRequested();
            if (!Current(target, owner)) { hooks?.Suspend(); return GameTextObservation.Empty(GameTextSource.None, GameTextStatus.TargetChanged, "Selected exact game lease changed during the text read.", clock()); }
            if (observation.BlocksOcr)
            {
                lock (gate) { if (disposed || owner != configurationGeneration) throw new OperationCanceledException(linked.Token); directConnected = observation.Status == GameTextStatus.ConnectedWaiting; }
                return observation;
            }
            if (!observation.HasText) { notices.Add(observation.Message); continue; }
            if (clock() - observation.Timestamp > age || observation.Timestamp - clock() > TimeSpan.FromSeconds(2))
            { notices.Add(reader.Source + ": source text is stale."); continue; }
            var fingerprint = observation.Source + "\0" + observation.SourceInstanceId + "\0" + string.Join("\n", observation.Lines.Select(line => line.SpeakerName + "\0" +
                (observation.IsEventStream ? line.Sequence.ToString() : "") + "\0" + line.Text));
            lock (gate)
            {
                if (disposed || owner != configurationGeneration || !Current(target, owner)) throw new OperationCanceledException(linked.Token);
                bool same = !observation.IsEventStream && last is { } previous && previous.Handle == target.Handle && previous.Pid == target.ProcessId && previous.Fingerprint == fingerprint;
                last = (target.Handle, target.ProcessId, fingerprint); directConnected = observation.IsEventStream;
                return observation with { Status = same ? GameTextStatus.Unchanged : GameTextStatus.Ready };
            }
        }
        linked.Token.ThrowIfCancellationRequested();
        if (!Current(target, owner)) return GameTextObservation.Empty(GameTextSource.None, GameTextStatus.TargetChanged, "Selected exact game lease changed during the text read.", clock());
        lock (gate) directConnected = false;
        return GameTextObservation.Empty(GameTextSource.None, GameTextStatus.Unavailable,
            directOnly ? (notices.Count == 0 ? hooks?.Readiness.Message ?? "No active direct-text source." : string.Join(" ", notices))
                : (notices.Count == 0 ? "No non-OCR source is enabled. OCR fallback is available." : string.Join(" ", notices) + " OCR fallback is available."), clock());
    }
    private sealed class HookAdapter(GameHookSourceManager manager) : IGameTextReader
    { public GameTextSource Source => GameTextSource.VerifiedHook; public Task<GameTextObservation> ReadAsync(WindowChoice target, CancellationToken ct = default) => manager.ReadAsync(target, ct); }
    public void Dispose()
    {
        CancellationTokenSource previous;
        lock (gate) { if (disposed) return; disposed = true; configurationGeneration++; automaticGeneration++; previous = configurationCancellation; last = null; processLease = null; }
        previous.Cancel(); previous.Dispose(); automaticCancellation?.Cancel(); automaticCancellation?.Dispose(); hooks?.Dispose();
    }
}
