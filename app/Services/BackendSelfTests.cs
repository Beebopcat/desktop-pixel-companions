using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AzerothCompanion.Services;

/// <summary>Offline contract tests. Every HTTP request uses a fake handler, never the network.</summary>
public static class BackendSelfTests
{
    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "AzerothCompanion-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var passed = new List<string>();
        BudgetLedger Ledger(string name) => new(Path.Combine(root, name + ".json"),
            () => new DateTimeOffset(2026, 10, 3, 17, 0, 0, TimeSpan.Zero));
        try
        {
            var cap = Ledger("cap");
            var admitted = 0;
            await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Task.Run(async () =>
            {
                try { await Ledger("cap").ReserveAsync(1m); Interlocked.Increment(ref admitted); }
                catch (BudgetException) { }
            })));
            var capped = await cap.GetSnapshotAsync();
            Require(admitted == 25 && capped.ReservedUsd == 25 && capped.RemainingUsd == 0, "Atomic $25 reservation cap");
            passed.Add("Atomic monthly cap: concurrent ledger clients cannot reserve beyond $25.");

            var uncertainLedger = Ledger("uncertain");
            var held = await uncertainLedger.ReserveAsync(2m);
            await uncertainLedger.MarkUncertainAsync(held);
            var persisted = await Ledger("uncertain").GetSnapshotAsync();
            Require(persisted.ReservedUsd == 2m && persisted.UncertainRequests == 1, "Persistent uncertain cost");
            passed.Add("Unknown charges remain reserved across client recreation.");

            var corruptPath = Path.Combine(root, "corrupt.json");
            await File.WriteAllTextAsync(corruptPath, "not json");
            await ExpectAsync<BudgetException>(() => new BudgetLedger(corruptPath).ReserveAsync(1m));
            passed.Add("Corrupt ledger fails closed instead of resetting spending.");

            var over = Ledger("over");
            var overReservation = await over.ReserveAsync(0.01m);
            await over.SettleAsync(overReservation, 0.02m);
            await ExpectAsync<BudgetException>(() => over.ReserveAsync(0.01m));
            passed.Add("Unexpected provider cost persistently blocks further cloud requests.");

            string? localBody = null;
            using var localHttp = new HttpClient(new FakeHandler(async (request, token) =>
            {
                Require(request.RequestUri!.Host == "127.0.0.1", "Local host only");
                localBody = await request.Content!.ReadAsStringAsync(token);
                return Json("{\"message\":{\"content\":\"Take cover.\"}}");
            }));
            var local = new ReasoningClient(localHttp, Ledger("local"));
            await local.ReplyAsync(new("Help", ImagePngOrJpeg: PngStub));
            using (var document = JsonDocument.Parse(localBody!))
            {
                var value = document.RootElement;
                Require(value.GetProperty("model").GetString() == ReasoningClient.LocalVisionModel &&
                    value.GetProperty("options").GetProperty("num_gpu").GetInt32() == 0 &&
                    value.GetProperty("messages")[1].GetProperty("images").GetArrayLength() == 1, "CPU vision routing");
            }
            passed.Add("Local screenshots route to the existing CPU vision model with image bytes; no silent image discard.");

            var localWithBrokenLedger = await new ReasoningClient(localHttp, new BudgetLedger(corruptPath))
                .ReplyAsync(new("Help"));
            Require(localWithBrokenLedger.Text == "Take cover." && !localWithBrokenLedger.Budget.IsAvailable &&
                localWithBrokenLedger.Budget.CloudBlocked, "Local answer independent of cloud ledger");
            passed.Add("Local answers remain available when the cloud ledger is unreadable; budget is explicitly unavailable and cloud remains blocked.");

            using var missingHttp = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
            await ExpectAsync<CompanionServiceException>(() => new ReasoningClient(missingHttp, Ledger("missing"))
                .ReplyAsync(new("Look", ImagePngOrJpeg: PngStub)));
            passed.Add("Missing local model reports an error; no download or cloud fallback.");

            // The legacy accounting protocol is exercised only if future authorization changes.
            // Current local-first builds must instead reject all paid paths before the fake HTTP handler.
            if (PaidServicePolicy.IsAuthorized)
            {
            var cloudLedger = Ledger("cloud");
            using var cloudHttp = new HttpClient(new FakeHandler((request, _) =>
            {
                Require(request.RequestUri!.Host == "api.openai.com", "Fixed OpenAI origin");
                return Task.FromResult(Json("{\"model\":\"gpt-4.1-mini-2025-04-14\",\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":50},\"choices\":[{\"message\":{\"content\":\"Watch the flank.\"}}]}"));
            }));
            var cloud = await new ReasoningClient(cloudHttp, cloudLedger, () => "not-a-real-key")
                .ReplyAsync(new("Advise", Mode: ReasoningMode.HybridOpenAI));
            Require(cloud.ChargedUsd == 0.00012m && cloud.Budget.ReservedUsd == 0, "Verified usage settlement");
            passed.Add("Successful cloud usage replaces its reservation with verified input/output cost.");

            var unknownModelLedger = Ledger("unknown-model");
            using var unknownModelHttp = new HttpClient(new FakeHandler((_, _) => Task.FromResult(Json(
                "{\"model\":\"unknown-model\",\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1},\"choices\":[]}"))));
            await ExpectAsync<BudgetException>(() => new ReasoningClient(unknownModelHttp, unknownModelLedger, () => "not-a-real-key")
                .ReplyAsync(new("Advise", Mode: ReasoningMode.HybridOpenAI)));
            var unknownModelSnapshot = await unknownModelLedger.GetSnapshotAsync();
            Require(unknownModelSnapshot.CloudBlocked && unknownModelSnapshot.ReservedUsd > 0, "Unknown model fail-closed");
            passed.Add("Unknown returned model blocks Hybrid mode and preserves its unresolved charge reservation.");

            using var cancel = new CancellationTokenSource();
            var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancelHttp = new HttpClient(new FakeHandler(async (_, token) =>
            {
                sendStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException();
            }));
            var cancelLedger = Ledger("cancel");
            var cancelTask = new ReasoningClient(cancelHttp, cancelLedger, () => "not-a-real-key")
                .ReplyAsync(new("Advise", Mode: ReasoningMode.HybridOpenAI), cancel.Token);
            await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            await ExpectAsync<OperationCanceledException>(() => cancelTask);
            Require((await cancelLedger.GetSnapshotAsync()).ReservedUsd > 0, "Canceled cloud charge retained");
            passed.Add("Cancellation after HTTP begins retains possible cloud charges and does not retry.");

            }
            else
            {
                var paidCalls = 0; var paidKeyReads = 0;
                using var deniedHttp = new HttpClient(new FakeHandler((_, _) =>
                { paidCalls++; throw new InvalidOperationException("Paid transport must not be reached."); }));
                var deniedPath = Path.Combine(root, "denied.json");
                await ExpectAsync<CompanionServiceException>(() => new ReasoningClient(deniedHttp, new BudgetLedger(deniedPath),
                    () => { paidKeyReads++; return "fake-test-key"; }).ReplyAsync(new("Advise", Mode: ReasoningMode.HybridOpenAI)));
                Require(paidCalls == 0 && paidKeyReads == 0 && !File.Exists(deniedPath), "Independent zero-spending authorization");
                passed.Add("Zero-spending policy blocks Hybrid before key lookup, transport or ledger reservation.");
            }

            var narrationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseNarration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var narrationRoutes = new List<string>();
            using var narrationHttp = new HttpClient(new FakeHandler(async (request, token) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                narrationRoutes.Add(path);
                if (path == "/status") return Json("{\"ready\":true,\"state\":\"idle\",\"voice\":\"existing-voice\"}");
                Require(path == "/render-background", "Render-only route");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var id = body.RootElement.GetProperty("requestId").GetString();
                narrationStarted.TrySetResult();
                await releaseNarration.Task;
                return Json(JsonSerializer.Serialize(new { completed = true, requestId = id,
                    profileId = NarrationClient.ApprovedProfile, seed = NarrationClient.ApprovedSeed,
                    sampleRate = 24000, channels = 1, dtype = "float32-le", frames = 1,
                    pcm = Convert.ToBase64String(BitConverter.GetBytes(0.25f)) }));
            }));
            var narration = new NarrationClient(narrationHttp);
            var narrationTask = narration.RenderLorathAsync("For Azeroth.");
            await narrationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            narration.CancelPending();
            releaseNarration.TrySetResult();
            Require(await narrationTask is null, "Stale audio discarded");
            Require(narrationRoutes.SequenceEqual(new[] { "/status", "/render-background" }), "No shared playback controls");
            var validAudio = await narration.RenderLorathAsync("A fresh utterance.");
            Require(validAudio is { SampleRate: 24000, Channels: 1 } && validAudio.PcmFloat32.Length == 4, "Validated approved audio");
            passed.Add("Narration cancellation discards stale audio; only status/render-only endpoints are used.");
            passed.Add("Approved narration identity and 24 kHz mono finite PCM are validated before playback handoff.");
            return passed;
        }
        finally
        {
            // Exact, uniquely owned test files only; no recursive removal or user paths.
            foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
            Directory.Delete(root);
        }
    }

    private static readonly byte[] PngStub = [137, 80, 78, 71, 13, 10, 26, 10];
    private static void Require(bool condition, string label)
    { if (!condition) throw new InvalidOperationException("Backend self-test failed: " + label); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Backend self-test expected " + typeof(T).Name);
    }
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
    }
}
