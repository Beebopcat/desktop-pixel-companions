using System.Text.Json;
namespace AzerothCompanion.Services;

public sealed record DirectTextExpectedCheck(long Sequence, string Kind, string ExpectedLiteralText);
public sealed record DirectTextCalibrationEvent(long Sequence, string Stage, string? LiteralText);

/// <summary>Explicit developer calibration only; no output from this API is offered to narration or game context.</summary>
public sealed class GameHookCalibrationSession : IDisposable
{
    private readonly GameHookSourceManager manager;
    private readonly IGameHookProcessInspector inspector;
    private GameHookCalibrationSession(GameHookSourceManager manager, IGameHookProcessInspector inspector) { this.manager = manager; this.inspector = inspector; }
    public static async Task<GameHookCalibrationSession> StartAsync(string runtimePath, string candidateId, WindowChoice target,
        GameLaunchIdentity? identity, CancellationToken ct = default, IGameHookProcessInspector? processInspector = null)
    {
        if (!GameHookFiles.Id(candidateId)) throw new ArgumentException("Select one staged candidate ID.", nameof(candidateId));
        var inspector = processInspector ?? new GameHookProcessInspector(); var manager = new GameHookSourceManager(inspector, runtimePath);
        var session = new GameHookCalibrationSession(manager, inspector);
        try
        {
            var game = await inspector.InspectAsync(target.ProcessId, ct).ConfigureAwait(false);
            var result = await manager.ConfigureAsync(target, identity, game, ct, candidateId).ConfigureAwait(false);
            if (!result.Connected) throw new IOException(result.Message); return session;
        }
        catch { session.Dispose(); throw; }
    }
    public IReadOnlyList<DirectTextCalibrationEvent> ReadEvents() => manager.ReadCalibrationPackets().Select(e => new DirectTextCalibrationEvent(e.Sequence, e.Stage, e.Text)).ToArray();
    public async Task<DirectTextProofPin> FinishAndSaveAsync(IReadOnlyList<DirectTextExpectedCheck> checks, string proofPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checks);
        if (checks.Count is < 3 or > 32 || checks.Any(x => string.IsNullOrWhiteSpace(x.ExpectedLiteralText) || System.Text.Encoding.UTF8.GetByteCount(x.ExpectedLiteralText) > 8192))
            throw new InvalidDataException("Select bounded exact expected dialogue, choice and transition checks.");
        var proof = await manager.FinishCalibrationAsync(checks, ct).ConfigureAwait(false);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(proof, DirectTextProfileStore.Options);
        if (bytes.Length > 65536) throw new InvalidDataException("Calibration proof exceeds its bound.");
        await DirectTextProfileStore.AtomicWriteAsync(proofPath, bytes, ct).ConfigureAwait(false); return new(GameHookFiles.LocalPath(proofPath), GameHookFiles.Sha(bytes));
    }
    public void Dispose() => manager.Dispose();
}
