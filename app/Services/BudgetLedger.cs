using System.Globalization;
using System.Text.Json;

namespace AzerothCompanion.Services;

/// <summary>App-only monthly ledger. Reservations survive crashes and ambiguous HTTP outcomes.</summary>
public sealed class BudgetLedger
{
    public const decimal MonthlyLimitUsd = 25m;
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public BudgetLedger(string? path = null, Func<DateTimeOffset>? clock = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AzerothCompanion", "budget-ledger.json"));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    private string CurrentMonth()
    {
        // User's budget calendar is America/Chicago, including daylight saving time.
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time"); }
        return TimeZoneInfo.ConvertTime(_clock(), zone).ToString("yyyy-MM", CultureInfo.InvariantCulture);
    }

    public async Task<BudgetSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await AcquireAsync(cancellationToken);
        return Snapshot(Read(), CurrentMonth());
    }

    public async Task<BudgetReservation> ReserveAsync(decimal worstCaseUsd, CancellationToken cancellationToken = default)
    {
        if (worstCaseUsd <= 0 || worstCaseUsd > MonthlyLimitUsd)
            throw new BudgetException("The request cannot fit within the monthly budget.");
        using var lease = await AcquireAsync(cancellationToken);
        var state = Read();
        if (state.PricingBlocked) throw new BudgetException("Cloud pricing or usage needs review. Hybrid requests are blocked; use Local CPU mode.");
        var month = CurrentMonth();
        var snapshot = Snapshot(state, month);
        if (worstCaseUsd > snapshot.RemainingUsd)
            throw new BudgetException("Monthly cloud budget reached, including uncertain charges. Use Local CPU mode.");
        var entry = new Entry { Id = Guid.NewGuid().ToString("N"), Month = month,
            ReservedUsd = worstCaseUsd, CreatedUtc = _clock(), State = "reserved" };
        state.Entries.Add(entry);
        Write(state);
        return new(entry.Id, month, worstCaseUsd);
    }

    public Task<BudgetSnapshot> SettleAsync(BudgetReservation reservation, decimal actualUsd) =>
        UpdateAsync(reservation, "settled", actualUsd);

    public Task<BudgetSnapshot> MarkUncertainAsync(BudgetReservation reservation) =>
        UpdateAsync(reservation, "uncertain", null);

    public Task<BudgetSnapshot> BlockUnknownPricingAsync(BudgetReservation reservation) =>
        UpdateAsync(reservation, "uncertain", null, blockPricing: true);

    /// <summary>Only call when HTTP send has definitely not begun.</summary>
    public Task<BudgetSnapshot> ReleaseUnsentAsync(BudgetReservation reservation) =>
        UpdateAsync(reservation, "unsent", 0);

    private async Task<BudgetSnapshot> UpdateAsync(BudgetReservation reservation, string status, decimal? actual, bool blockPricing = false)
    {
        if (actual < 0) throw new BudgetException("Invalid usage cost; cloud access stopped.");
        // Accounting must finish even when the request's cancellation token was canceled.
        using var lease = await AcquireAsync(CancellationToken.None);
        var state = Read();
        var entry = state.Entries.SingleOrDefault(x => x.Id == reservation.Id && x.Month == reservation.Month)
            ?? throw new BudgetException("Budget reservation missing; cloud access stopped.");
        if (entry.State is "settled" or "unsent") return Snapshot(state, CurrentMonth());
        entry.State = status;
        entry.ActualUsd = actual;
        if (blockPricing || actual > entry.ReservedUsd) state.PricingBlocked = true;
        Write(state);
        return Snapshot(state, CurrentMonth());
    }

    private static BudgetSnapshot Snapshot(State state, string month)
    {
        var entries = state.Entries.Where(x => x.Month == month).ToArray();
        var charged = entries.Sum(x => x.State == "settled" ? x.ActualUsd ?? x.ReservedUsd : 0);
        var pending = entries.Where(x => x.State is "reserved" or "uncertain").ToArray();
        var reserved = pending.Sum(x => x.ReservedUsd);
        return new(month, MonthlyLimitUsd, charged, reserved,
            state.PricingBlocked ? 0 : Math.Max(0, MonthlyLimitUsd - charged - reserved), pending.Length, state.PricingBlocked);
    }

    private State Read()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path), JsonOptions);
            if (state is null || state.Version != 1 || state.Entries is null ||
                state.Entries.Any(x => string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Month) ||
                    x.ReservedUsd <= 0 || x.ReservedUsd > MonthlyLimitUsd || x.ActualUsd < 0 ||
                    x.State is not ("reserved" or "uncertain" or "settled" or "unsent")) ||
                state.Entries.Select(x => x.Id).Distinct().Count() != state.Entries.Count)
                throw new InvalidDataException();
            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        { throw new BudgetException("Budget ledger cannot be verified. Cloud requests are disabled; preserve the ledger."); }
    }

    private void Write(State state)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, state, JsonOptions);
                file.Flush(flushToDisk: true);
            }
            if (File.Exists(_path)) File.Replace(temporary, _path, null);
            else File.Move(temporary, _path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<FileStream> AcquireAsync(CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(40, token); }
            catch (IOException) { throw new BudgetException("Budget ledger is busy. Try again after the other request finishes."); }
        }
    }

    public sealed class State
    {
        public int Version { get; set; } = 1;
        public bool PricingBlocked { get; set; }
        public List<Entry> Entries { get; set; } = [];
    }
    public sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Month { get; set; } = "";
        public decimal ReservedUsd { get; set; }
        public decimal? ActualUsd { get; set; }
        public string State { get; set; } = "reserved";
        public DateTimeOffset CreatedUtc { get; set; }
    }
}
