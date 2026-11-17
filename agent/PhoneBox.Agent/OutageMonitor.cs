using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed record OutageRegionInfo(string Id, string NameUa, string NameRu, string NameEn, bool Emergency);

/// <summary>What the Power outages widget gets for its region and queue: today's and tomorrow's runs, Kyiv day bounds and how fresh it all is.</summary>
public sealed record OutageView(long Updated, string? Error, OutageRegionInfo? Region, bool Stale, bool Found,
    long DayStart, long DayEnd, bool Tomorrow, bool TomorrowOff, IReadOnlyList<OutageRun> Runs);

/// <summary>
/// Planned power outage schedules for Ukraine (svitlo.live), for the Power outages widget. One download serves every
/// widget; it happens at most every 15 minutes and only while an enabled page shows the widget. The phone never
/// contacts the source and the request carries nothing about the user: the region and queue are picked out here.
/// </summary>
public sealed partial class OutageMonitor(ConfigStore config, ILogger<OutageMonitor> log) : BackgroundService
{
    internal const string WidgetType = "outages";
    internal const string Url = "https://dtek-api.svitlo-proxy.workers.dev/";
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly Lock _gate = new();
    private OutageData? _data;
    private long _updated, _nextAt;
    private string? _error;

    /// <summary>Test seam: the HTTP handler behind the fetch.</summary>
    internal HttpMessageHandler? Handler { get; init; }
    /// <summary>Test seam: the clock, in Unix milliseconds.</summary>
    internal Func<long> Now { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private HttpClient? _client;
    private HttpClient Client(bool _) =>
        _client ??= new HttpClient(Handler ?? FeedHttp.CreateHandler(allowPrivate: false), disposeHandler: Handler is null) { Timeout = Timeout.InfiniteTimeSpan };

    public bool Shown() => config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == WidgetType);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning("outages: {Msg}", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    /// <summary>Fetches the schedule when it is due and a widget is shown; a failed fetch waits a full interval too.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (Now() < _nextAt) return;
        }
        if (!Shown()) return;
        lock (_gate) _nextAt = Now() + (long)Interval.TotalMilliseconds;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var r = await FeedHttp.GetAsync(Client, Url, null, null, timeout.Token);
            var data = OutageSource.Parse(Encoding.UTF8.GetString(r.Body ?? []));
            lock (_gate) { _data = data; _updated = Now(); _error = null; }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var msg = ex is OperationCanceledException ? "no answer in 20 s" : ex is FormatException ? "not a schedule" : ex.Message;
            lock (_gate) _error = msg; // the last good schedule stays
            log.LogInformation("outages: {Msg}", msg);
        }
    }

    // [0-9], not \d: \d also matches other scripts' digits, which int.Parse refuses
    [GeneratedRegex(@"^\s*0*([0-9]{1,2})[.,]([0-9])\s*$")]
    private static partial Regex QueuePattern();

    /// <summary>"3.1", " 3,1 ", "03.1" → "3.1"; anything that isn't a queue id → null.</summary>
    internal static string? NormalizeQueue(string? q) =>
        q is not null && QueuePattern().Match(q) is { Success: true } m ? $"{int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)}.{m.Groups[2].Value}" : null;

    public OutageView View(string? region, string? queue)
    {
        OutageData? data; long updated; string? error;
        lock (_gate) { data = _data; updated = _updated; error = _error; }
        var none = new OutageView(updated, error, null, false, false, 0, 0, false, false, []);
        if (data is null || region is null || !data.Regions.TryGetValue(region, out var r)) return none;
        if (!DateOnly.TryParseExact(data.DateToday, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var today)) return none;
        var info = new OutageRegionInfo(r.Id, r.NameUa, r.NameRu, r.NameEn, r.Emergency);
        var (dayStart, dayEnd) = OutageSource.Day(today);
        // a region whose newest day isn't the source's today has an old schedule: nothing in it can be trusted for now
        var stale = !r.Queues.Values.Any(days => days.ContainsKey(data.DateToday));
        var id = NormalizeQueue(queue);
        var found = id is not null && r.Queues.ContainsKey(id);
        // a queue that exists but has no day for today is as untrustworthy as a stale region, not a typo
        if (stale || !found || !r.Queues[id!].TryGetValue(data.DateToday, out var todaySlots))
            return none with { Region = info, Stale = stale || found, DayStart = dayStart, DayEnd = dayEnd };
        var q = r.Queues[id!];
        // a tomorrow with no known slot is a placeholder, not a published schedule
        var tomorrow = q.TryGetValue(data.DateTomorrow, out var t) && t.Any(c => c is 1 or 2) ? t : null;
        var days = new List<(DateOnly, int[])> { (today, todaySlots) };
        if (tomorrow is not null) days.Add((today.AddDays(1), tomorrow));
        return new OutageView(updated, error, info, false, true, dayStart, dayEnd, tomorrow is not null,
            tomorrow?.Contains(2) == true, OutageSource.Runs(days));
    }
}
