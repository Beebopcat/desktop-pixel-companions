using System.Net;
using System.Net.Http;
using System.Text;

namespace AzerothCompanion.Services;

/// <summary>Headless checks; only the in-memory handler below is used. No OS UI/capture/audio APIs.</summary>
public static class LocalFirstSelfTests
{
    public static async Task<IReadOnlyList<string>> RunAsync(string scratchDirectory)
    {
        Directory.CreateDirectory(scratchDirectory);
        var passed = new List<string>();
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("Local-first check failed: " + label);
            passed.Add(label);
        }
        Check(!PaidServicePolicy.IsAuthorized && PaidServicePolicy.AuthorizedSpendingUsd == 0, "Spending authorization is zero and cannot be enabled by runtime settings.");
        var ledgerPath = Path.Combine(scratchDirectory, "preserved-ledger.json");
        const string historical = "Historical accounting sentinel: never reset to enable a request.";
        await File.WriteAllTextAsync(ledgerPath, historical);
        var keyReads = 0;
        using var handler = new InMemoryHandler();
        using var http = new HttpClient(handler);
        string Key() { keyReads++; return "sk-fixture-not-a-real-key"; }
        var reasoning = new ReasoningClient(http, new BudgetLedger(ledgerPath), Key);
        foreach (var withImage in new[] { false, true })
        {
            try
            {
                await reasoning.ReplyAsync(new("Fixture question", Mode: ReasoningMode.HybridOpenAI,
                    ImagePngOrJpeg: withImage ? [137, 80, 78, 71, 13, 10, 26, 10] : null));
                throw new InvalidOperationException("Paid reasoning unexpectedly succeeded.");
            }
            catch (CompanionServiceException error) when (error.Message == PaidServicePolicy.Message) { }
        }
        Check(handler.Calls == 0 && keyReads == 0, "Hybrid text and image requests are blocked before credential reads or transport calls.");
        using var research = new GameResearchService(http, new BudgetLedger(ledgerPath), Key);
        foreach (var consent in new[] { false, true, false, true })
        {
            research.SetSessionConsent(consent);
            var result = await research.ResearchAsync(new(GameResearchIdentity.Identify("Fixture Game", "fixture"), "Fixture quest"));
            Check(result.Status == GameResearchStatus.Disabled && result.AccountedUsd == 0 && !result.HasContext,
                $"Research consent={consent} cannot grant payment authorization (check {passed.Count}).");
        }
        Check(handler.Calls == 0 && keyReads == 0, "Research toggle and API-key provider never reach a paid request.");
        Check(await File.ReadAllTextAsync(ledgerPath) == historical && !File.Exists(ledgerPath + ".lock"),
            "Blocked requests leave historical accounting untouched and create no reservations.");
        var localResult = await reasoning.ReplyAsync(new("A synthetic local question"));
        Check(handler.Calls == 1 && handler.ExternalCalls == 0 && keyReads == 0 && localResult.Text == "Simulated local answer.",
            "Local reasoning remains reachable through an in-memory loopback fixture; no model is loaded.");
        Check(!localResult.Budget.IsAvailable && localResult.Budget.CloudBlocked,
            "Local answers survive unavailable historical cloud accounting without enabling payment.");
        return passed;
    }

    private sealed class InMemoryHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public int ExternalCalls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (request.RequestUri?.Host != "127.0.0.1") { ExternalCalls++; throw new InvalidOperationException("External request blocked by test fixture."); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"message\":{\"content\":\"Simulated local answer.\"}}", Encoding.UTF8, "application/json") });
        }
    }
}
