using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AzerothCompanion.Services;

internal sealed record GameHookBinding(string Nonce, string StreamId, string ProfileId, GameHookProcessIdentity Game, GameHookProcessIdentity Helper, DateTimeOffset StartedUtc,
    string? Context = null, string? Subcontext = null);
internal sealed record GameHookPacket(string Type, string Stage, long Sequence, DateTimeOffset Utc, string? Text, string? Speaker);

/// <summary>One authenticated, bounded, session-owned pipe. Never stores dialogue on disk.</summary>
internal sealed class GameHookStreamReader : IGameTextReader, IDisposable
{
    internal const int MaximumEventBytes = 16384, MaximumTextBytes = 8192;
    private readonly object gate = new();
    private readonly NamedPipeServerStream pipe;
    private readonly GameHookBinding binding;
    private readonly Func<bool> leaseValid;
    private readonly Func<bool> producerCurrent;
    private readonly Func<DateTimeOffset> clock;
    private readonly CancellationTokenSource stop;
    private readonly Queue<(GameTextLine Line, DateTimeOffset Utc)> queue = new();
    private readonly TaskCompletionSource<bool> attached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool calibration;
    private readonly List<GameHookPacket> calibrationPackets = new();
    private Task? pump;
    private long sequence;
    private int queuedBytes;
    private bool connected, disposed;
    private string failure = "Connecting to the exact verified game text helper.";
    internal string PipeName { get; }
    internal bool Connected { get { lock (gate) return connected && !disposed && !stop.IsCancellationRequested; } }
    internal bool Active { get { lock (gate) return !disposed && !stop.IsCancellationRequested; } }
    internal Task Completion => pump ?? Task.CompletedTask;
    internal IReadOnlyList<GameHookPacket> CalibrationPackets { get { lock (gate) return calibrationPackets.ToArray(); } }
    public GameTextSource Source => GameTextSource.VerifiedHook;
    internal GameHookStreamReader(string pipeName, GameHookBinding binding, Func<bool> leaseValid, CancellationToken sessionToken,
        Func<DateTimeOffset>? clock = null, bool calibration = false, NamedPipeServerStream? existingPipe = null, Func<bool>? producerCurrent = null)
    {
        this.binding = binding; this.leaseValid = leaseValid; this.producerCurrent = producerCurrent ?? (() => true); this.clock = clock ?? (() => DateTimeOffset.UtcNow); this.calibration = calibration;
        if (!GameHookFiles.Hash(binding.Nonce) || !GameHookFiles.Id(binding.StreamId) || !GameHookFiles.Id(binding.ProfileId)
            || binding.Game.ProcessId == 0 || binding.Helper.ProcessId == 0 || pipeName.Length is < 12 or > 128 || pipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Invalid bounded game text pipe binding.");
        PipeName = pipeName; stop = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        pipe = existingPipe ?? CreatePipe(pipeName);
    }
    internal static NamedPipeServerStream CreatePipe(string pipeName) => new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance, 16384, 16384);
    internal void Start() { lock (gate) { if (pump != null) throw new InvalidOperationException("This stream has already started."); pump = PumpAsync(); } }
    internal async Task WaitAttachedAsync(CancellationToken ct)
    {
        if (!await attached.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false)) throw new IOException(failure);
    }
    private async Task PumpAsync()
    {
        Task? watch = null;
        try
        {
            await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint actual) || actual != binding.Helper.ProcessId || !leaseValid() || !producerCurrent())
                throw new InvalidDataException("Game text pipe client process does not match the owned helper.");
            watch = WatchLeaseAsync();
            while (!stop.IsCancellationRequested)
            {
                byte[]? bytes = await ReadLineAsync(pipe, stop.Token).ConfigureAwait(false);
                if (bytes == null) break;
                stop.Token.ThrowIfCancellationRequested(); if (!leaseValid()) throw new IOException("Game/helper identity or profile was revoked.");
                var packet = ValidatePacket(bytes);
                // The authenticated pipe can contain detached/stopped packets written immediately before helper exit.
                // They do not authorize more dialogue. New text/attachment still requires the original live producer.
                if ((packet.Type is "text" or "ready" || packet.Stage == "calibration-attached") && !producerCurrent()) throw new IOException("Owned producer exited before literal publication.");
                lock (gate)
                {
                    if (disposed || stop.IsCancellationRequested || !leaseValid()) throw new OperationCanceledException(stop.Token);
                    sequence = packet.Sequence;
                    if (calibration)
                    {
                        if (calibrationPackets.Count >= 128) throw new InvalidDataException("Calibration event bound exceeded.");
                        calibrationPackets.Add(packet);
                    }
                    if (packet.Type == "ready" || calibration && packet.Stage == "calibration-attached")
                    { connected = true; failure = calibration ? "Calibration attached; recording only, never narration." : "Connected; waiting for literal game dialogue."; attached.TrySetResult(true); }
                    else if (packet.Type == "text" && !calibration)
                    {
                        int size = Encoding.UTF8.GetByteCount(packet.Text!);
                        if (queue.Count >= 64 || queuedBytes + size > 524288) throw new InvalidDataException("Game text stream overflow; reconnect rather than silently lose dialogue.");
                        queue.Enqueue((new(packet.Text!, packet.Speaker, packet.Sequence), packet.Utc)); queuedBytes += size;
                    }
                    else if (packet.Type == "error") throw new IOException("Game text helper reported a bounded attachment error.");
                    else if (packet.Type == "stopped") { connected = false; failure = "Game text helper stopped; direct text is unavailable."; stopped.TrySetResult(true); break; }
                }
            }
        }
        catch (OperationCanceledException) { lock (gate) failure = "Game text session stopped or target/profile changed."; }
        catch (Exception ex) { lock (gate) failure = ex is InvalidDataException ? "Rejected malformed, replayed or mismatched game text stream." : "Game text helper is unavailable or its identity changed."; }
        finally
        {
            lock (gate) { connected = false; queue.Clear(); queuedBytes = 0; attached.TrySetResult(false); }
            stop.Cancel(); pipe.Dispose();
            if (watch != null) try { await watch.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }
    private async Task WatchLeaseAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            await Task.Delay(250, stop.Token).ConfigureAwait(false);
            if (!leaseValid()) { stop.Cancel(); break; }
        }
    }
    private GameHookPacket ValidatePacket(byte[] bytes)
    {
        using var document = GameHookFiles.Json(bytes); var r = document.RootElement;
        var allowed = new HashSet<string>(["schema", "type", "stage", "sessionNonce", "streamId", "profileId", "producerPid", "gamePid", "gameStartFileTime", "gameExeSha256", "sequence", "utc", "text", "speaker", "hookAddress", "context", "subcontext"], StringComparer.Ordinal);
        string S(string name) => r.GetProperty(name).GetString() ?? throw new InvalidDataException("Missing string field.");
        if (r.ValueKind != JsonValueKind.Object || r.EnumerateObject().Any(p => !allowed.Contains(p.Name)) || r.GetProperty("schema").GetInt32() != 1
            || S("sessionNonce") != binding.Nonce || S("streamId") != binding.StreamId || S("profileId") != binding.ProfileId
            || r.GetProperty("producerPid").GetUInt32() != binding.Helper.ProcessId || r.GetProperty("gamePid").GetUInt32() != binding.Game.ProcessId
            || r.GetProperty("gameStartFileTime").GetInt64() != binding.Game.StartedFileTime || S("gameExeSha256") != binding.Game.ExecutableSha256
            || r.GetProperty("sequence").GetInt64() != sequence + 1) throw new InvalidDataException("Wrong direct text owner or sequence.");
        long next = sequence + 1; string type = S("type"), stage = S("stage");
        if (type is not ("ready" or "text" or "status" or "error" or "stopped") || stage.Length is < 1 or > 128 || stage.Any(char.IsControl)) throw new InvalidDataException("Invalid stream event type/stage.");
        if (!DateTimeOffset.TryParse(S("utc"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var utc)
            || utc.Offset != TimeSpan.Zero || utc < binding.StartedUtc - TimeSpan.FromSeconds(2) || utc > clock() + TimeSpan.FromSeconds(2) || clock() - utc > TimeSpan.FromSeconds(30)) throw new InvalidDataException("Stale or future stream event.");
        if (stage.StartsWith("fixture", StringComparison.Ordinal) || !calibration && stage.StartsWith("calibration", StringComparison.Ordinal)
            || calibration && (type == "ready" || type == "text" && stage != "calibration")) throw new InvalidDataException("Fixture/calibration output cannot masquerade as production dialogue.");
        bool currentlyConnected; lock (gate) currentlyConnected = connected;
        if (type == "ready" && (stage != "attached-waiting" || currentlyConnected) || type == "text" && !currentlyConnected) throw new InvalidDataException("Dialogue arrived before exact hook attachment.");
        string? text = null, speaker = null;
        if (type == "text")
        {
            text = S("text");
            if (!calibration && stage != "dialogue" || binding.Context != null && S("context") != binding.Context || binding.Subcontext != null && S("subcontext") != binding.Subcontext
                || binding.Context != null && (!ulong.TryParse(S("hookAddress"), out ulong address) || address == 0)) throw new InvalidDataException("Literal event came from a different calibrated hook context.");
            if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > MaximumTextBytes || text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t')) throw new InvalidDataException("Invalid literal text bound.");
            if (r.TryGetProperty("speaker", out var s)) { if (s.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid explicit speaker."); speaker = s.GetString(); if (speaker?.Length > 256 || speaker?.Any(char.IsControl) == true) throw new InvalidDataException("Speaker metadata exceeds its bound."); }
        }
        else if (r.TryGetProperty("text", out _) || r.TryGetProperty("speaker", out _)) throw new InvalidDataException("Non-dialogue event has literal payload.");
        return new(type, stage, next, utc, text, speaker);
    }
    internal static async Task<byte[]?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        // A bounded buffer prevents ReadLineAsync from allocating an arbitrary producer-controlled line.
        byte[] bytes = new byte[MaximumEventBytes]; byte[] one = new byte[1]; int count = 0;
        while (true)
        {
            int read = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (read == 0) { if (count == 0) return null; throw new InvalidDataException("Incomplete stream event."); }
            if (one[0] == 10) { if (count == 0) throw new InvalidDataException("Empty stream event."); byte[] result = bytes[..count]; _ = new UTF8Encoding(false, true).GetString(result); return result; }
            if (count == bytes.Length) throw new InvalidDataException("Stream event exceeds its byte bound."); bytes[count++] = one[0];
        }
    }
    public Task<GameTextObservation> ReadAsync(WindowChoice target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (calibration) return Task.FromResult(GameTextObservation.Empty(Source, GameTextStatus.Unavailable, "Calibration output is record-only and cannot be narrated.", clock()));
            if (target.ProcessId != binding.Game.ProcessId || disposed || !leaseValid()) { queue.Clear(); queuedBytes = 0; return Task.FromResult(GameTextObservation.Empty(Source, GameTextStatus.TargetChanged, "Exact game/helper/source lease changed.", clock())); }
            if (!producerCurrent()) { queue.Clear(); queuedBytes = 0; return Task.FromResult(GameTextObservation.Empty(Source, GameTextStatus.Unavailable, "Owned game-text helper exited.", clock())); }
            if (!Active) return Task.FromResult(GameTextObservation.Empty(Source, GameTextStatus.Unavailable, failure, clock()));
            if (!connected) return Task.FromResult(GameTextObservation.Empty(Source, GameTextStatus.Connecting, "Connecting; exact hook attachment has not yet been acknowledged.", clock()) with { SourceInstanceId = binding.StreamId, IsEventStream = true });
            var lines = new List<GameTextLine>(); DateTimeOffset at = clock(); int bytes = 0;
            while (queue.TryPeek(out var entry))
            {
                int size = Encoding.UTF8.GetByteCount(entry.Line.Text); if (lines.Count != 0 && (bytes + size > 16384 || lines.Count >= 16)) break;
                queue.Dequeue(); queuedBytes -= size;
                if (clock() - entry.Utc > TimeSpan.FromSeconds(30)) continue; lines.Add(entry.Line); bytes += size; at = entry.Utc;
            }
            var result = lines.Count == 0 ? GameTextObservation.Empty(Source, GameTextStatus.ConnectedWaiting, failure, at)
                : GameTextObservation.FromLines(Source, lines, at, GameTextExactness.ExportedText, "Literal dialogue from an exact verified game hook; no OCR or speaker inference.");
            return Task.FromResult(result with { SourceInstanceId = binding.StreamId, IsEventStream = true });
        }
    }
    internal async Task StopAsync()
    {
        bool canWrite;
        try { canWrite = pipe.IsConnected && !stop.IsCancellationRequested; } catch (ObjectDisposedException) { canWrite = false; }
        if (canWrite)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                byte[] command = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, type = "stop", sessionNonce = binding.Nonce });
                await pipe.WriteAsync(command, timeout.Token).ConfigureAwait(false); await pipe.WriteAsync(new byte[] { 10 }, timeout.Token).ConfigureAwait(false); await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
                try { await stopped.Task.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); } catch (TimeoutException) { }
            }
            catch (IOException) { } catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
        }
        Dispose(); if (pump != null) await pump.ConfigureAwait(false);
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; connected = false; queue.Clear(); queuedBytes = 0; }
        stop.Cancel(); pipe.Dispose(); attached.TrySetResult(false);
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
