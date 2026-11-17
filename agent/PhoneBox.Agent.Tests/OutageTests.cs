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
