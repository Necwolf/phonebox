using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed record OutageRegionInfo(string Id, string NameUa, string NameRu, string NameEn, bool Emergency);

/// <summary>What the Power outages widget gets for its region and queue: today's and tomorrow's runs, Kyiv day bounds and how fresh it all is.</summary>
public sealed record OutageView(long Updated, string? Error, OutageRegionInfo? Region, bool Stale, bool Found,
    long DayStart, long DayEnd, bool Tomorrow, bool TomorrowOff, IReadOnlyList<OutageRun> Runs);

/// <summary>
/// Planned power outage schedules for Ukraine (svitlo.live), for the Power outages widget. svitlo.live runs two servers:
/// the main one and a legacy one that also covers seven regions the main one doesn't (Chernivtsi among them) and is
/// sometimes current where the main one is stale. Both are downloaded at most every 15 minutes, once for every widget,
/// and only while an enabled page shows the widget. The phone never contacts them and the requests carry nothing about
/// the user: the region and queue are picked out here.
/// </summary>
public sealed partial class OutageMonitor(ConfigStore config, ILogger<OutageMonitor> log) : BackgroundService
{
    internal const string WidgetType = "outages";
    internal const string Url = "https://dtek-api.svitlo-proxy.workers.dev/";
    internal const string LegacyUrl = "https://svitlo-proxy.svitlo-proxy.workers.dev/";
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private sealed class Source(string url)
    {
        public readonly string Url = url;
        public OutageData? Data;
        public long Updated;
        public string? Error;
    }

    private readonly Lock _gate = new();
    private readonly Source[] _sources = [new(Url), new(LegacyUrl)]; // in order of preference
    private long _nextAt;

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
        await Task.WhenAll(_sources.Select(s => FetchAsync(s, ct)));
    }

    private async Task FetchAsync(Source src, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var r = await FeedHttp.GetAsync(Client, src.Url, null, null, timeout.Token);
            var data = OutageSource.Parse(Encoding.UTF8.GetString(r.Body ?? []));
            lock (_gate) { src.Data = data; src.Updated = Now(); src.Error = null; }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var msg = ex is OperationCanceledException ? "no answer in 20 s" : ex is FormatException ? "not a schedule" : ex.Message;
            lock (_gate) src.Error = msg; // the last good schedule stays
            log.LogInformation("outages {Host}: {Msg}", new Uri(src.Url).Host, msg);
        }
    }

    /// <summary>A region is current when some queue has a known slot today; a day of unknown slots is a placeholder.</summary>
    private static bool Current(OutageData d, OutageRegion r) =>
        r.Queues.Values.Any(days => days.TryGetValue(d.DateToday, out var slots) && slots.Any(c => c is 1 or 2));

    // [0-9], not \d: \d also matches other scripts' digits, which int.Parse refuses
    [GeneratedRegex(@"^\s*0*([0-9]{1,2})(?:[.,]([0-9]))?\s*$")]
    private static partial Regex QueuePattern();

    /// <summary>"3.1", " 3,1 ", "03.1" → "3.1"; a whole group "05" → "5" (Chernivtsi, Donetsk); anything else → null.</summary>
    internal static string? NormalizeQueue(string? q)
    {
        if (q is null || QueuePattern().Match(q) is not { Success: true } m) return null;
        var group = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        return m.Groups[2].Success ? $"{group}.{m.Groups[2].Value}" : group;
    }

    public OutageView View(string? region, string? queue)
    {
        (OutageData Data, OutageRegion Region, long Updated)[] candidates;
        string? error;
        lock (_gate)
        {
            error = _sources.Select(s => s.Error).FirstOrDefault(e => e is not null);
            candidates = region is null ? [] : _sources
                .Where(s => s.Data is not null && s.Data.Regions.ContainsKey(region))
                .Select(s => (s.Data!, s.Data!.Regions[region], s.Updated)).ToArray();
        }
        // the preferred server's region when it is current, else any current one, else the preferred one as it is
        var pick = candidates.Where(c => Current(c.Data, c.Region)).Cast<(OutageData, OutageRegion, long)?>().FirstOrDefault()
            ?? candidates.Cast<(OutageData, OutageRegion, long)?>().FirstOrDefault();
        var none = new OutageView(pick?.Item3 ?? 0, error, null, false, false, 0, 0, false, false, []);
        if (pick is not { } chosen) return none;
        var (data, r, updated) = chosen;
        if (!DateOnly.TryParseExact(data.DateToday, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var today)) return none;
        var info = new OutageRegionInfo(r.Id, r.NameUa, r.NameRu, r.NameEn, r.Emergency);
        var (dayStart, dayEnd) = OutageSource.Day(today);
        // a region whose newest day isn't the source's today has an old schedule: nothing in it can be trusted for now
        var stale = !Current(data, r);
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
