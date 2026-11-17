using System.Text.Json;
using Xunit;

namespace PhoneBox.Agent.Tests;

public sealed class OutageSourceTests
{
    private static long Utc(int y, int mo, int d, int h, int mi) => new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <summary>The source wraps its JSON in a string field, so the sample is serialized twice like the real answer.</summary>
    internal static string Envelope(object inner) => JsonSerializer.Serialize(new { body = JsonSerializer.Serialize(inner) });

    /// <summary>48 slot codes as the source's "HH:mm" map.</summary>
    internal static Dictionary<string, int> Slots(Func<int, int> code) =>
        Enumerable.Range(0, 48).ToDictionary(i => $"{i / 2:00}:{i % 2 * 30:00}", code);

    private static int[] Codes(Func<int, int> code) => Enumerable.Range(0, 48).Select(code).ToArray();

    [Fact]
    public void ParsesTheBodyStringEnvelope()
    {
        var raw = Envelope(new
        {
            date_today = "2026-11-17",
            date_tomorrow = "2026-11-18",
            regions = new[]
            {
                new
                {
                    cpu = "kiivska-oblast", name_ua = "Київська область", name_ru = "Киевская область", name_en = "Kyiv Region", emergency = true,
                    schedule = new Dictionary<string, Dictionary<string, Dictionary<string, int>>>
                    {
                        ["3.1"] = new() { ["2026-11-17"] = new() { ["00:00"] = 2, ["00:30"] = 2, ["01:00"] = 1 } },
                    },
                },
            },
        });
        var data = OutageSource.Parse(raw);
        Assert.Equal("2026-11-17", data.DateToday);
        Assert.Equal("2026-11-18", data.DateTomorrow);
        var region = data.Regions["kiivska-oblast"];
        Assert.True(region.Emergency);
        Assert.Equal("Kyiv Region", region.NameEn);
        Assert.Equal("Київська область", region.NameUa);
        var slots = region.Queues["3.1"]["2026-11-17"];
        Assert.Equal(48, slots.Length);
        Assert.Equal([2, 2, 1, 0], slots[..4]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"body":42}""")]
    [InlineData("""{"body":"{}"}""")]
    [InlineData("""{"body":"{\"date_today\":\"2026-11-17\",\"regions\":42}"}""")]
    public void MalformedInputThrowsFormatException(string raw) => Assert.Throws<FormatException>(() => OutageSource.Parse(raw));

    [Fact]
    public void RunsMergeEqualSlotsAndLabelInKyivTime()
    {
        var day = new DateOnly(2026, 11, 17); // UTC+2
        var runs = OutageSource.Runs([(day, Codes(i => i is >= 12 and < 20 ? 2 : 1))]);
        Assert.Equal(["on", "off", "on"], runs.Select(r => r.State).ToArray());
        Assert.Equal(Utc(2026, 11, 17, 4, 0), runs[1].From);
        Assert.Equal(Utc(2026, 11, 17, 8, 0), runs[1].To);
        Assert.Equal("06:00", runs[1].FromLabel);
        Assert.Equal("10:00", runs[1].ToLabel);
        Assert.Equal("00:00", runs[0].FromLabel);
        Assert.Equal("24:00", runs[^1].ToLabel);
        Assert.Equal(OutageSource.Day(day).Start, runs[0].From);
        Assert.Equal(OutageSource.Day(day).End, runs[^1].To);
    }

    [Fact]
    public void RunsMergeAcrossMidnight()
    {
        var today = new DateOnly(2026, 11, 17);
        var runs = OutageSource.Runs([(today, Codes(i => i >= 44 ? 2 : 1)), (today.AddDays(1), Codes(i => i < 4 ? 2 : 1))]);
        Assert.Equal(["on", "off", "on"], runs.Select(r => r.State).ToArray());
        Assert.Equal(Utc(2026, 11, 17, 20, 0), runs[1].From); // 22:00 Kyiv
        Assert.Equal(Utc(2026, 11, 18, 0, 0), runs[1].To);    // 02:00 Kyiv next day
        Assert.Equal("22:00", runs[1].FromLabel);
        Assert.Equal("02:00", runs[1].ToLabel);
    }

    [Fact]
    public void UnknownCodesBecomeUnknownRuns()
    {
        var runs = OutageSource.Runs([(new DateOnly(2026, 11, 17), Codes(i => i < 2 ? 0 : i < 4 ? 3 : 1))]);
        Assert.Equal(["unknown", "on"], runs.Select(r => r.State).ToArray());
        Assert.Equal("02:00", runs[0].ToLabel);
    }

    [Theory]
    [InlineData(2026, 3, 29)]  // clocks spring forward: 03:00 and 03:30 don't exist
    [InlineData(2026, 10, 25)] // clocks fall back: the 03:00 hour happens twice
    public void DstDaysProduceOrderedGaplessRuns(int y, int m, int d)
    {
        var day = new DateOnly(y, m, d);
        var runs = OutageSource.Runs([(day, Codes(i => i % 2 == 0 ? 1 : 2))]);
        var (start, end) = OutageSource.Day(day);
        Assert.Equal(start, runs[0].From);
        Assert.Equal(end, runs[^1].To);
        Assert.All(runs, r => Assert.True(r.To > r.From, $"{r.FromLabel}-{r.ToLabel}"));
        for (var i = 1; i < runs.Count; i++)
        {
            Assert.Equal(runs[i - 1].To, runs[i].From);
            Assert.NotEqual(runs[i - 1].State, runs[i].State);
        }
        Assert.Equal(m == 3 ? 23 * 3_600_000L : 25 * 3_600_000L, end - start);
    }
}

public sealed class OutageMonitorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-outages-" + Guid.NewGuid().ToString("N"));
    private long _now = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(); // 12:00 in Kyiv

    public OutageMonitorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private ConfigStore Config(int widgets = 1, bool pageEnabled = true)
    {
        var path = Path.Combine(_dir, "config.json");
        var list = string.Join(",", Enumerable.Range(0, widgets).Select(i =>
            $$$"""{"id":"o{{{i}}}","type":"outages","x":{{{i * 2}}},"y":0,"w":2,"h":2,"options":{"region":"kiivska-oblast","queue":"3.1"}}"""));
        File.WriteAllText(path, $$"""{"pages":[{"id":"p","title":"P","enabled":{{(pageEnabled ? "true" : "false")}},"widgets":[{{list}}]},{"id":"q","title":"Q","widgets":[{"id":"c","type":"clock","x":0,"y":0,"w":6,"h":4}]}]}""");
        return new ConfigStore(path);
    }

    /// <summary>Kyiv region 3.1: off 06:00-10:00 today, off 00:00-02:00 tomorrow; Lviv only has an October day; Odesa's tomorrow is all unknown.</summary>
    private static string Body() => OutageSourceTests.Envelope(new
    {
        date_today = "2026-11-17",
        date_tomorrow = "2026-11-18",
        regions = new object[]
        {
            new
            {
                cpu = "kiivska-oblast", name_ua = "Київська область", name_ru = "Киевская область", name_en = "Kyiv Region", emergency = false,
                schedule = new Dictionary<string, object>
                {
                    ["3.1"] = new Dictionary<string, object>
                    {
                        ["2026-11-17"] = OutageSourceTests.Slots(i => i is >= 12 and < 20 ? 2 : 1),
                        ["2026-11-18"] = OutageSourceTests.Slots(i => i < 4 ? 2 : 1),
                    },
                },
            },
            new
            {
                cpu = "lvivska-oblast", name_ua = "Львівська область", name_ru = "Львовская область", name_en = "Lviv Region", emergency = false,
                schedule = new Dictionary<string, object> { ["3.1"] = new Dictionary<string, object> { ["2026-10-17"] = OutageSourceTests.Slots(_ => 1) } },
            },
            new
            {
                cpu = "odeska-oblast", name_ua = "Одеська область", name_ru = "Одесская область", name_en = "Odesa Region", emergency = true,
                schedule = new Dictionary<string, object>
                {
                    ["3.1"] = new Dictionary<string, object> { ["2026-11-17"] = OutageSourceTests.Slots(_ => 1), ["2026-11-18"] = OutageSourceTests.Slots(_ => 0) },
                },
            },
        },
    });

    private static HttpResponseMessage Json(string body) => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private OutageMonitor Monitor(ConfigStore cfg, FeedHttpTests.Fake handler) =>
        new(cfg, Microsoft.Extensions.Logging.Abstractions.NullLogger<OutageMonitor>.Instance) { Handler = handler, Now = () => _now };

    private async Task<OutageMonitor> Loaded()
    {
        var m = Monitor(Config(), new FeedHttpTests.Fake(_ => Json(Body())));
        await m.TickAsync(CancellationToken.None);
        return m;
    }

    [Fact]
    public async Task NothingIsFetchedWithoutAShownWidget()
    {
        var handler = new FeedHttpTests.Fake(_ => Json(Body()));
        using var m = Monitor(Config(pageEnabled: false), handler);
        await m.TickAsync(CancellationToken.None);
        Assert.Empty(handler.Seen);
        Assert.False(m.Shown());
    }

    [Fact]
    public async Task OneFetchPerIntervalForAnyNumberOfWidgets()
    {
        var handler = new FeedHttpTests.Fake(_ => Json(Body()));
        using var m = Monitor(Config(widgets: 2), handler);
        await m.TickAsync(CancellationToken.None);
        await m.TickAsync(CancellationToken.None);
        Assert.Single(handler.Seen);
        Assert.Equal(new Uri(OutageMonitor.Url), handler.Seen[0]);
        _now += (long)OutageMonitor.Interval.TotalMilliseconds - 1000;
        await m.TickAsync(CancellationToken.None);
        Assert.Single(handler.Seen);
        _now += 1000;
        await m.TickAsync(CancellationToken.None);
        Assert.Equal(2, handler.Seen.Count);
    }

    [Fact]
    public async Task ViewReturnsTodayAndTomorrowRunsForTheQueue()
    {
        using var m = await Loaded();
        var v = m.View("kiivska-oblast", "3.1");
        Assert.True(v.Found);
        Assert.False(v.Stale);
        Assert.Equal(_now, v.Updated);
        Assert.Null(v.Error);
        Assert.Equal("Kyiv Region", v.Region!.NameEn);
        Assert.Equal("Київська область", v.Region.NameUa);
        Assert.True(v.Tomorrow);
        Assert.True(v.TomorrowOff);
        var day = OutageSource.Day(new DateOnly(2026, 11, 17));
        Assert.Equal(day.Start, v.DayStart);
        Assert.Equal(day.End, v.DayEnd);
        Assert.Equal(["on", "off", "on", "off", "on"], v.Runs.Select(r => r.State).ToArray());
        Assert.Equal(day.Start, v.Runs[0].From);
        Assert.Equal(OutageSource.Day(new DateOnly(2026, 11, 18)).End, v.Runs[^1].To);
    }

    [Fact]
    public async Task StaleRegionHasNoRuns()
    {
        using var m = await Loaded();
        var v = m.View("lvivska-oblast", "3.1");
        Assert.True(v.Stale);
        Assert.Empty(v.Runs);
        Assert.Equal("Lviv Region", v.Region!.NameEn);
    }

    [Fact]
    public async Task UnknownRegionOrQueue()
    {
        using var m = await Loaded();
        Assert.Null(m.View("nowhere", "3.1").Region);
        Assert.Empty(m.View("nowhere", "3.1").Runs);
        var v = m.View("kiivska-oblast", "9.9");
        Assert.False(v.Found);
        Assert.Empty(v.Runs);
        Assert.False(m.View("kiivska-oblast", "abc").Found);
    }

    [Theory]
    [InlineData(" 3,1 ", "3.1")]
    [InlineData("3.1", "3.1")]
    [InlineData("03.1", "3.1")]
    [InlineData("60.1", "60.1")]
    [InlineData("abc", null)]
    [InlineData("3.1; rm", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void QueueIsNormalizedAndValidated(string? given, string? expected) => Assert.Equal(expected, OutageMonitor.NormalizeQueue(given));

    [Fact]
    public async Task LooselyTypedQueueFindsTheSchedule()
    {
        using var m = await Loaded();
        Assert.True(m.View("kiivska-oblast", " 3,1 ").Found);
    }

    [Fact]
    public async Task TomorrowWithOnlyUnknownSlotsIsNotPublished()
    {
        using var m = await Loaded();
        var v = m.View("odeska-oblast", "3.1");
        Assert.False(v.Tomorrow);
        Assert.False(v.TomorrowOff);
        Assert.True(v.Region!.Emergency);
        Assert.Equal(["on"], v.Runs.Select(r => r.State).ToArray()); // the unknown day is left out
    }

    [Fact]
    public async Task FailedFetchKeepsLastDataAndError()
    {
        var fail = false;
        var handler = new FeedHttpTests.Fake(_ => fail ? new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) : Json(Body()));
        using var m = Monitor(Config(), handler);
        await m.TickAsync(CancellationToken.None);
        var updated = m.View("kiivska-oblast", "3.1").Updated;
        fail = true;
        _now += (long)OutageMonitor.Interval.TotalMilliseconds;
        await m.TickAsync(CancellationToken.None);
        var v = m.View("kiivska-oblast", "3.1");
        Assert.Equal(2, handler.Seen.Count);
        Assert.NotNull(v.Error);
        Assert.Equal(updated, v.Updated);
        Assert.NotEmpty(v.Runs);
    }

    [Fact]
    public async Task MalformedAnswerIsAnErrorNotACrash()
    {
        using var m = Monitor(Config(), new FeedHttpTests.Fake(_ => Json("<html>busy</html>")));
        await m.TickAsync(CancellationToken.None);
        var v = m.View("kiivska-oblast", "3.1");
        Assert.Equal("not a schedule", v.Error);
        Assert.Equal(0, v.Updated);
    }

    [Fact]
    public void BeforeAnyFetchThereIsNothing()
    {
        using var m = Monitor(Config(), new FeedHttpTests.Fake(_ => Json(Body())));
        var v = m.View("kiivska-oblast", "3.1");
        Assert.Equal(0, v.Updated);
        Assert.Null(v.Region);
        Assert.Empty(v.Runs);
    }
}
