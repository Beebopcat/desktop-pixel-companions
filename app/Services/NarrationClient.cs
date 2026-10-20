using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AzerothCompanion.Services;

/// <summary>A voice with a verified, render-only route in the existing local player.</summary>
public sealed record NarrationVoice(string Id, string Label, string Language, string Engine, int? Seed = null,
    bool PrivateLocalOnly = false, bool? UserApproved = null, string? ReferenceSha256 = null, string? ReferenceTextSha256 = null,
    string RenderRoute = "/render-voice", string? SourceSha256 = null, bool Canonical = true);

/// <summary>Uses only the existing approved render-only service. Never starts or stops a worker.</summary>
public sealed class NarrationClient
{
    public const string ApprovedProfile = "qn_lorath_refined";
    public const int ApprovedSeed = 93019;
    private const string Origin = "http://127.0.0.1:8787";
    // These entries were verified against the installed daemon and its CPU-only recording route.
    // Further entries require explicit compatible route metadata; a qn_ prefix is never enough.
    private static readonly HashSet<string> VerifiedKokoroIds = new(StringComparer.Ordinal)
    {
        "af_heart", "af_bella", "af_nicole", "bf_emma", "am_michael", "am_fenrir", "am_puck",
        "bm_george", "bm_fable", "bm_lewis", "bm_daniel", "am_onyx", "am_adam", "af_sarah", "af_kore"
    };
    private readonly HttpClient _http;
    public string? CatalogVersion { get; private set; }
    private readonly SemaphoreSlim _serial = new(1, 1);
    private long _generation;
    private bool _uncertainRender;

    public NarrationClient(HttpClient? httpClient = null, string? privateRegistryConfigPath = null)
    {
        _http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(280) };
        // Retained constructor argument for older isolated callers. Private file/approval
        // verification belongs to the canonical daemon; no second registry is selected here.
        _ = privateRegistryConfigPath;
    }

    /// <summary>Reads the player's catalog without changing its selected voice or preferences.</summary>
    public async Task<IReadOnlyList<NarrationVoice>> GetVoiceCatalogAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var response = await _http.GetAsync(Origin + "/voices", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new CompanionServiceException("Existing voice catalog is unavailable.");
            using var json = JsonDocument.Parse(await ReadBoundedContentAsync(response.Content, 1024 * 1024, timeout.Token));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("voices", out var voices) || voices.ValueKind != JsonValueKind.Array || voices.GetArrayLength() > 256)
                throw new CompanionServiceException("Existing voice catalog failed validation.");
            var catalog = new List<NarrationVoice>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var canonical = TryText(root, "catalogVersion", out var catalogVersion);
            if (canonical && catalogVersion.Length > 80)
                throw new CompanionServiceException("Voice catalog version is invalid.");
            foreach (var entry in voices.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !TryText(entry, "id", out var id) || id.Length > 128 ||
                    !id.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
                    throw new CompanionServiceException("Existing voice catalog failed validation.");
                if (!ids.Add(id)) throw new CompanionServiceException("Existing voice catalog contains duplicate voice identities.");
                if (!TryVoice(entry, id, canonical, out var voice)) continue;
                if (!labels.Add(voice!.Label)) throw new CompanionServiceException("Existing voice catalog contains duplicate display names.");
                catalog.Add(voice);
            }
            CatalogVersion = canonical ? catalogVersion : null;
            return catalog.AsReadOnly();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new CompanionServiceException("Existing voice catalog timed out. No worker was started."); }
        catch (HttpRequestException)
        { throw new CompanionServiceException("Existing voice catalog is offline. No replacement worker was started."); }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { throw new CompanionServiceException("Existing voice catalog returned an unreadable response."); }
    }

    private static bool TryVoice(JsonElement entry, string id, bool canonical, out NarrationVoice? voice)
    {
        voice = null;
        if (!TryText(entry, "label", out var label) || label.Length > 160 || label.Trim() != label)
            throw new CompanionServiceException("Existing voice catalog contains an invalid display name.");
        var language = TryText(entry, "lang", out var lang) && lang.Length <= 32 ? lang : "unknown";
        if (!canonical)
        {
            // Compatibility for the older public catalog and controlled regression fixtures.
            // Private entries require the canonical daemon's pinned registry validation.
            if (id == ApprovedProfile)
                voice = new(id, label, language, "qwen3-tts", ApprovedSeed, RenderRoute: "/render-background", Canonical: false);
            else if (VerifiedKokoroIds.Contains(id))
                voice = new(id, label, language, "kokoro-cpu", RenderRoute: "/render-kokoro", Canonical: false);
            return voice != null;
        }
        if (!TryText(entry, "engine", out var engine) || engine is not ("kokoro" or "kokoro-cpu" or "qwen3-tts" or "qwen3-tts-1.7b-base") ||
            !TryText(entry, "renderRoute", out var route) || route != "/render-voice" || !IsBoolean(entry, "renderOnly", true))
            throw new CompanionServiceException("Voice catalog contains an incompatible render-only route. No substitute was used.");
        int? seed = null;
        if (entry.TryGetProperty("seed", out var seedValue))
        {
            if (seedValue.ValueKind != JsonValueKind.Number || !seedValue.TryGetInt32(out var parsed) || parsed < 0)
                throw new CompanionServiceException("Voice catalog contains an invalid seed.");
            seed = parsed;
        }
        bool privateVoice = IsBoolean(entry, "privateLocalOnly", true);
        bool? approved = entry.TryGetProperty("userApproved", out var acceptance) && acceptance.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? acceptance.GetBoolean() : null;
        string? reference = null, transcript = null, source = null;
        if (privateVoice)
        {
            if (!IsBoolean(entry, "publicPublicationAllowed", false) || approved is null || seed is null ||
                !TryText(entry, "referenceSha256", out reference) || !IsDigest(reference) ||
                !TryText(entry, "referenceTextSha256", out transcript) || !IsDigest(transcript) ||
                !TryText(entry, "sourceSha256", out source) || !IsDigest(source))
                throw new CompanionServiceException("Private voice catalog is missing its pinned identity. Existing choices were not replaced.");
        }
        if (id == ApprovedProfile && seed != ApprovedSeed)
            throw new CompanionServiceException("Approved Lorath B seed differs from the preserved profile.");
        voice = new(id, label, language, engine, seed, privateVoice, approved, reference, transcript, route, source);
        return true;
    }

    private static bool IsDigest(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool TryText(JsonElement entry, string name, out string value)
    {
        value = "";
        if (!entry.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    public async Task<NarrationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var response = await _http.GetAsync(Origin + "/status", timeout.Token);
            if (!response.IsSuccessStatusCode) throw new CompanionServiceException("Existing narrator status is unavailable.");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var value = json.RootElement;
            return new(value.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.True,
                value.TryGetProperty("state", out var state) ? state.GetString() ?? "unknown" : "unknown",
                value.TryGetProperty("narrator_phase", out var phase) ? phase.GetString() : null,
                value.TryGetProperty("voice", out var voice) ? voice.GetString() : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new CompanionServiceException("Existing narrator status timed out. No worker was started."); }
        catch (HttpRequestException)
        { throw new CompanionServiceException("Existing narrator is offline. The companion will not launch a replacement."); }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { throw new CompanionServiceException("Existing narrator returned an unreadable status."); }
    }

    /// <summary>Invalidates companion audio only. Existing foreground narration is never stopped.</summary>
    public void CancelPending() => Interlocked.Increment(ref _generation);

    /// <summary>Renders a currently registered compatible voice, without changing global voice selection.</summary>
    public Task<NarrationAudio?> RenderAsync(string text, string voiceId, CancellationToken cancellationToken = default) =>
        RenderAsyncCore(text, voiceId, true, cancellationToken);

    /// <summary>Compatibility entry point pinned to approved Lorath B and its original seed.</summary>
    public Task<NarrationAudio?> RenderLorathAsync(string text, CancellationToken cancellationToken = default) =>
        RenderAsyncCore(text, ApprovedProfile, false, cancellationToken);

    /// <summary>Explicit private audition request only. Returns audio bytes without
    /// playback; QA-pending references still cannot enter the verified catalog.</summary>
    public Task<NarrationAudio?> RenderCandidateAuditionAsync(string text, string voiceId, CancellationToken cancellationToken = default)
    {
        if (!voiceId.StartsWith("qn_private_", StringComparison.Ordinal))
            throw new CompanionServiceException("Choose an explicitly requested private candidate for an audition.");
        return RenderAsyncCore(text, voiceId, true, cancellationToken, allowUnapprovedPrivate: true);
    }

    private async Task<NarrationAudio?> RenderAsyncCore(string text, string voiceId, bool verifyCatalog, CancellationToken cancellationToken, bool allowUnapprovedPrivate = false)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 600)
            throw new CompanionServiceException("Narration chunks must contain 1–600 characters.");
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new CompanionServiceException("Choose an available companion voice before rendering.");
        var generation = Interlocked.Increment(ref _generation);
        try
        {
            var voice = verifyCatalog ? (await GetVoiceCatalogAsync(cancellationToken)).FirstOrDefault(voice => voice.Id == voiceId) :
                new NarrationVoice(ApprovedProfile, "Lorath B", "en-gb", "qwen3-tts", ApprovedSeed, RenderRoute: "/render-background", Canonical: false);
            if (voice is null)
                throw new CompanionServiceException("The assigned voice is unavailable or has no compatible render-only route. Choose another voice; no substitute was used.");
            // Availability is controlled by the canonical catalog. User approval describes
            // the source/listening state; retained candidates stay explicitly selectable.
            _ = allowUnapprovedPrivate;
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Interlocked.Read(ref _generation)) return null;
            await _serial.WaitAsync(cancellationToken);
            if (generation != Interlocked.Read(ref _generation)) { _serial.Release(); return null; }
            // The HTTP operation continues after UI cancellation: aborting the connection cannot
            // cancel the existing renderer. Retain the semaphore until it actually responds.
            var operation = RenderOwnedAsync(text, voice, generation, cancellationToken);
            _ = operation.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return await operation.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.CompareExchange(ref _generation, generation + 1, generation);
            throw;
        }
    }

    private async Task<NarrationAudio?> RenderOwnedAsync(string text, NarrationVoice voice, long generation, CancellationToken initialCancellation)
    {
        var sent = false;
        var responseReceived = false;
        try
        {
            if (_uncertainRender)
                throw new CompanionServiceException("Previous narrator render has an unknown outcome. Speech is paused for this app session; existing playback is untouched.");
            var status = await GetStatusAsync(initialCancellation);
            initialCancellation.ThrowIfCancellationRequested();
            if (generation != Interlocked.Read(ref _generation)) return null;
            if (!status.Ready || status.State != "idle")
                throw new CompanionServiceException("Existing narration is busy or unavailable. Companion speech will wait for another user request.");
            var id = "companion-" + Guid.NewGuid().ToString("N");
            var kokoro = voice.Engine is "kokoro" or "kokoro-cpu";
            var privateVoice = voice.PrivateLocalOnly;
            object payload = voice.Canonical || kokoro ? new { requestId = id, text, voice = voice.Id, speed = 1.0 } :
                new { requestId = id, text, voice = voice.Id, seed = ApprovedSeed };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(260));
            using var request = new HttpRequestMessage(HttpMethod.Post, Origin + voice.RenderRoute)
            { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
            sent = true;
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            responseReceived = true;
            if (!response.IsSuccessStatusCode)
            {
                var message = (int)response.StatusCode switch
                {
                    409 => "Foreground narration took priority; this companion utterance was skipped.",
                    429 => "An existing render is in progress; this companion utterance was skipped.",
                    422 => "The selected renderer rejected this audio; no substitute voice was used.",
                    503 => "Existing narrator is not ready; no replacement worker was started.",
                    _ => "Existing narrator could not render this utterance. No substitute voice was used."
                };
                throw new CompanionServiceException(message);
            }
            using var json = JsonDocument.Parse(await ReadBoundedContentAsync(response.Content, 48 * 1024 * 1024, timeout.Token));
            var root = json.RootElement;
            if (!root.GetProperty("completed").GetBoolean() || root.GetProperty("requestId").GetString() != id ||
                root.GetProperty("profileId").GetString() != voice.Id ||
                (voice.Canonical && (!TryText(root, "engine", out var renderedEngine) || renderedEngine != voice.Engine)) ||
                (voice.Seed is { } expectedSeed && (!root.TryGetProperty("seed", out var renderedSeed) || !renderedSeed.TryGetInt32(out var actualSeed) || actualSeed != expectedSeed)) ||
                privateVoice && (root.GetProperty("referenceSha256").GetString() != voice.ReferenceSha256 ||
                    root.GetProperty("referenceTextSha256").GetString() != voice.ReferenceTextSha256 ||
                    root.GetProperty("sourceSha256").GetString() != voice.SourceSha256 ||
                    !IsBoolean(root, "privateLocalOnly", true) || !IsBoolean(root, "publicPublicationAllowed", false) ||
                    !IsBoolean(root, "userApproved", voice.UserApproved == true)) ||
                root.GetProperty("sampleRate").GetInt32() != 24000 || root.GetProperty("channels").GetInt32() != 1 ||
                root.GetProperty("dtype").GetString() != "float32-le")
                throw new CompanionServiceException("Narrator identity or audio format did not match the selected voice.");
            var frames = root.GetProperty("frames").GetInt32();
            var pcm = Convert.FromBase64String(root.GetProperty("pcm").GetString() ?? "");
            if (frames <= 0 || frames > 24000 * (kokoro ? 120 : 180) || pcm.Length != frames * sizeof(float))
                throw new CompanionServiceException("Narrator audio dimensions are invalid.");
            for (var offset = 0; offset < pcm.Length; offset += sizeof(float))
                if (!float.IsFinite(BitConverter.ToSingle(pcm, offset)))
                    throw new CompanionServiceException("Narrator audio contains invalid samples.");
            if (generation != Interlocked.Read(ref _generation)) return null;
            double? seconds = root.TryGetProperty("metrics", out var metrics) &&
                metrics.TryGetProperty("renderSeconds", out var renderTime) && renderTime.TryGetDouble(out var value) ? value : null;
            return new(pcm, 24000, 1, voice.Id, id, seconds);
        }
        catch (OperationCanceledException) when (sent)
        {
            _uncertainRender = true;
            throw new CompanionServiceException("Narrator render timed out. Speech is paused for this app session because the original request may still be running.");
        }
        catch (HttpRequestException)
        {
            if (sent && !responseReceived) _uncertainRender = true;
            throw new CompanionServiceException("Existing renderer could not be reached. No replacement worker was started.");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        { throw new CompanionServiceException("Narrator response failed validation; no audio will be played."); }
        finally { _serial.Release(); }
    }

    private static async Task<byte[]> ReadBoundedContentAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes)
            throw new CompanionServiceException("Existing narrator returned an oversized response.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(block, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new CompanionServiceException("Existing narrator returned an oversized response.");
            buffer.Write(block, 0, read);
        }
        return buffer.ToArray();
    }

    private static bool IsBoolean(JsonElement entry, string name, bool value) =>
        entry.TryGetProperty(name, out var property) && property.ValueKind == (value ? JsonValueKind.True : JsonValueKind.False);

}
