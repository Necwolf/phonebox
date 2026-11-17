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

    [Fact]
    public void ParsesThePlainLegacyFormatAndAliasesItsRegionIds()
    {
        var raw = JsonSerializer.Serialize(new
        {
            date_today = "2026-11-17",
            date_tomorrow = "2026-11-18",
            regions = new object[]
            {
                new { cpu = "harkivska-oblast", name_ua = "Харківська", name_ru = "Харьковская", name_en = "Kharkiv", schedule = new Dictionary<string, object> { ["1.1"] = new Dictionary<string, object> { ["2026-11-17"] = Slots(_ => 1) } } },
                new { cpu = "chernivetska-oblast", name_ua = "Чернівецька", name_ru = "Черновицкая", name_en = "Chernivtsi", schedule = new Dictionary<string, object> { ["5"] = new Dictionary<string, object> { ["2026-11-17"] = Slots(i => i < 2 ? 2 : 1) } } },
                new { cpu = "luganska-oblast", name_ua = "Луганська", name_ru = "Луганская", name_en = "Luhansk", schedule = (object?)null },
            },
        });
        var data = OutageSource.Parse(raw);
        Assert.True(data.Regions.ContainsKey("kharkivska-oblast"));
        Assert.False(data.Regions.ContainsKey("harkivska-oblast"));
        Assert.Equal([2, 2, 1], data.Regions["chernivetska-oblast"].Queues["5"]["2026-11-17"][..3]);
        Assert.Empty(data.Regions["luganska-oblast"].Queues);
        Assert.False(data.Regions["chernivetska-oblast"].Emergency);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"body":42}""")]
    [InlineData("""{"body":"{}"}""")]
    [InlineData("""{"date_today":"2026-11-17"}""")]
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
                    ["4.1"] = new Dictionary<string, object> { ["2026-10-17"] = OutageSourceTests.Slots(_ => 1) },
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

    /// <summary>Legacy server: Chernivtsi groups (only there) and a current Lviv day (stale on the main server).</summary>
    private static string Legacy() => System.Text.Json.JsonSerializer.Serialize(new
    {
        date_today = "2026-11-17",
        date_tomorrow = "2026-11-18",
        regions = new object[]
        {
            new { cpu = "chernivetska-oblast", name_ua = "Чернівецька", name_ru = "Черновицкая", name_en = "Chernivtsi", schedule = new Dictionary<string, object> { ["5"] = new Dictionary<string, object> { ["2026-11-17"] = OutageSourceTests.Slots(i => i is >= 20 and < 28 ? 2 : 1) } } },
            new { cpu = "lvivska-oblast", name_ua = "Львівська", name_ru = "Львовская", name_en = "Lviv", schedule = new Dictionary<string, object> { ["3.1"] = new Dictionary<string, object> { ["2026-11-17"] = OutageSourceTests.Slots(i => i < 4 ? 2 : 1) } } },
            new { cpu = "kiivska-oblast", name_ua = "Київська", name_ru = "Киевская", name_en = "Kyiv (legacy)", schedule = new Dictionary<string, object> { ["3.1"] = new Dictionary<string, object> { ["2026-11-17"] = OutageSourceTests.Slots(_ => 1) } } },
        },
    });

    private static Func<HttpRequestMessage, HttpResponseMessage> Both(Func<HttpResponseMessage>? main = null, Func<HttpResponseMessage>? legacy = null) =>
        req => req.RequestUri!.Host == new Uri(OutageMonitor.Url).Host ? (main ?? (() => Json(Body())))() : (legacy ?? (() => Json(Legacy())))();

    private OutageMonitor Monitor(ConfigStore cfg, FeedHttpTests.Fake handler) =>
        new(cfg, Microsoft.Extensions.Logging.Abstractions.NullLogger<OutageMonitor>.Instance) { Handler = handler, Now = () => _now };

    private async Task<OutageMonitor> Loaded()
    {
        var m = Monitor(Config(), new FeedHttpTests.Fake(Both()));
        await m.TickAsync(CancellationToken.None);
        return m;
    }

    [Fact]
    public async Task NothingIsFetchedWithoutAShownWidget()
    {
        var handler = new FeedHttpTests.Fake(Both());
        using var m = Monitor(Config(pageEnabled: false), handler);
        await m.TickAsync(CancellationToken.None);
        Assert.Empty(handler.Seen);
        Assert.False(m.Shown());
    }

    [Fact]
    public async Task OneFetchPerIntervalForAnyNumberOfWidgets()
    {
        var handler = new FeedHttpTests.Fake(Both());
        using var m = Monitor(Config(widgets: 2), handler);
        await m.TickAsync(CancellationToken.None);
        await m.TickAsync(CancellationToken.None);
        Assert.Equal([new Uri(OutageMonitor.Url), new Uri(OutageMonitor.LegacyUrl)], handler.Seen.OrderBy(u => u.Host == "dtek-api.svitlo-proxy.workers.dev" ? 0 : 1).ToArray());
        _now += (long)OutageMonitor.Interval.TotalMilliseconds - 1000;
        await m.TickAsync(CancellationToken.None);
        Assert.Equal(2, handler.Seen.Count);
        _now += 1000;
        await m.TickAsync(CancellationToken.None);
        Assert.Equal(4, handler.Seen.Count);
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
        var day = OutageSource.Day(new DateOnly(2026, 11, 17));
        Assert.Equal(day.End, v.DayEnd);
        Assert.Equal(["on", "off", "on", "off", "on"], v.Runs.Select(r => r.State).ToArray());
        Assert.Equal(day.Start, v.Runs[0].From);
        Assert.Equal(OutageSource.Day(new DateOnly(2026, 11, 18)).End, v.Runs[^1].To);
    }

    [Fact]
    public async Task RegionOnlyOnTheLegacyServerIsServed()
    {
        using var m = await Loaded();
        var v = m.View("chernivetska-oblast", "5");
        Assert.True(v.Found);
        Assert.Equal("Chernivtsi", v.Region!.NameEn);
        Assert.Equal(["on", "off", "on"], v.Runs.Select(r => r.State).ToArray());
    }

    [Fact]
    public async Task StaleMainRegionFallsBackToACurrentLegacyOne()
    {
        using var m = await Loaded();
        var v = m.View("lvivska-oblast", "3.1");
        Assert.False(v.Stale);
        Assert.True(v.Found);
        Assert.Equal("off", v.Runs[0].State);
        Assert.Equal("Kyiv Region", m.View("kiivska-oblast", "3.1").Region!.NameEn); // a current main region wins
    }

    [Fact]
    public async Task APlaceholderDayOfUnknownSlotsIsNotAFallback()
    {
        var placeholder = System.Text.Json.JsonSerializer.Serialize(new
        {
            date_today = "2026-11-17",
            date_tomorrow = "2026-11-18",
            regions = new object[] { new { cpu = "lvivska-oblast", name_ua = "Львівська", name_ru = "Львовская", name_en = "Lviv", schedule = new Dictionary<string, object> { ["3.1"] = new Dictionary<string, object> { ["2026-11-17"] = OutageSourceTests.Slots(_ => 0) } } } },
        });
        using var m = Monitor(Config(), new FeedHttpTests.Fake(Both(legacy: () => Json(placeholder))));
        await m.TickAsync(CancellationToken.None);
        var v = m.View("lvivska-oblast", "3.1");
        Assert.True(v.Stale); // the main server's old schedule, honestly marked, rather than a day of nothing
        Assert.Equal("Lviv Region", v.Region!.NameEn);
    }

    [Fact]
    public async Task OneServerFailingKeepsTheOther()
    {
        using var m = Monitor(Config(), new FeedHttpTests.Fake(Both(main: () => new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway))));
        await m.TickAsync(CancellationToken.None);
        var v = m.View("chernivetska-oblast", "5");
        Assert.True(v.Found);
        Assert.Equal("server answered 502", v.Error);
        Assert.Equal(_now, v.Updated);
        Assert.Equal(0, m.View("odeska-oblast", "3.1").Updated); // only the main server has Odesa, and it never answered
    }

    [Fact]
    public async Task StaleOnBothServersStaysStale()
    {
        using var m = Monitor(Config(), new FeedHttpTests.Fake(Both(legacy: () => Json("""{"date_today":"2026-11-17","date_tomorrow":"2026-11-18","regions":[]}"""))));
        await m.TickAsync(CancellationToken.None);
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
    [InlineData("٣.١", null)] // non-ASCII digits match \d but aren't queue ids
    [InlineData("5", "5")]     // Chernivtsi and Donetsk number whole groups
    [InlineData(" 05 ", "5")]
    [InlineData("12", "12")]
    [InlineData("123", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void QueueIsNormalizedAndValidated(string? given, string? expected) => Assert.Equal(expected, OutageMonitor.NormalizeQueue(given));

    [Fact]
    public async Task QueueWithoutTodaysScheduleIsStaleNotMissing()
    {
        using var m = await Loaded();
        var v = m.View("kiivska-oblast", "4.1");
        Assert.True(v.Stale);
        Assert.Empty(v.Runs);
        Assert.False(m.View("kiivska-oblast", "3.1").Stale); // the rest of the region is current
    }

    [Fact]
    public async Task NonAsciiDigitsAreNotFoundRatherThanAnError()
    {
        using var m = await Loaded();
        var v = m.View("kiivska-oblast", "٣.١");
        Assert.False(v.Found);
        Assert.Empty(v.Runs);
    }

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
        Assert.True(v.Region!.Emergency);
        Assert.Equal(["on"], v.Runs.Select(r => r.State).ToArray()); // the unknown day is left out
    }

    [Fact]
    public async Task FailedFetchKeepsLastDataAndError()
    {
        var fail = false;
        var handler = new FeedHttpTests.Fake(Both(() => fail ? new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) : Json(Body())));
        using var m = Monitor(Config(), handler);
        await m.TickAsync(CancellationToken.None);
        var updated = m.View("kiivska-oblast", "3.1").Updated;
        fail = true;
        _now += (long)OutageMonitor.Interval.TotalMilliseconds;
        await m.TickAsync(CancellationToken.None);
        var v = m.View("kiivska-oblast", "3.1");
        Assert.Equal(4, handler.Seen.Count);
        Assert.Equal("server answered 500", v.Error);
        Assert.Equal(updated, v.Updated);
        Assert.NotEmpty(v.Runs);
    }

    [Fact]
    public async Task MalformedAnswerIsAnErrorNotACrash()
    {
        using var m = Monitor(Config(), new FeedHttpTests.Fake(_ => Json("<html>busy</html>")));
        // neither server answers with a schedule
        await m.TickAsync(CancellationToken.None);
        var v = m.View("kiivska-oblast", "3.1");
        Assert.Equal("not a schedule", v.Error);
        Assert.Equal(0, v.Updated);
    }

    [Fact]
    public async Task ThePhoneGetsOnlyTheFieldsItUses()
    {
        using var m = await Loaded();
        var json = JsonSerializer.Serialize(m.View("kiivska-oblast", "3.1"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(["dayEnd", "error", "found", "region", "runs", "stale", "tomorrow", "updated"],
            doc.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    public async Task AnOversizedAnswerIsReportedAsTheSchedulesOwnError()
    {
        var huge = new string(' ', FeedHttp.MaxBytes + 1);
        using var m = Monitor(Config(), new FeedHttpTests.Fake(_ => Json(huge)));
        await m.TickAsync(CancellationToken.None);
        var error = m.View("kiivska-oblast", "3.1").Error;
        Assert.NotNull(error);
        Assert.DoesNotContain("feed", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BeforeAnyFetchThereIsNothing()
    {
        using var m = Monitor(Config(), new FeedHttpTests.Fake(Both()));
        var v = m.View("kiivska-oblast", "3.1");
        Assert.Equal(0, v.Updated);
        Assert.Null(v.Region);
        Assert.Empty(v.Runs);
    }
}
