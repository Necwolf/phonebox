using System.Globalization;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>A region of the outage schedule; <see cref="Queues"/> maps a queue id to a "yyyy-MM-dd" date to its 48 half-hour codes.</summary>
public sealed record OutageRegion(string Id, string NameUa, string NameRu, string NameEn, bool Emergency,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int[]>> Queues);

public sealed record OutageData(string DateToday, string DateTomorrow, IReadOnlyDictionary<string, OutageRegion> Regions);

/// <summary>A stretch with one power state, in UTC milliseconds; the labels are Kyiv wall-clock times ("24:00" for a midnight end).</summary>
public sealed record OutageRun(long From, long To, string State, string FromLabel, string ToLabel);

/// <summary>
/// Reads the svitlo.live schedule (Ukrainian planned power outages by queue) and turns its half-hour slots, which are
/// Kyiv wall-clock times, into absolute runs, so the phone needs no time zone rules of its own.
/// </summary>
internal static class OutageSource
{
    internal const int SlotsPerDay = 48;

    public static TimeZoneInfo Kyiv { get; } = FindKyiv();

    private static TimeZoneInfo FindKyiv()
    {
        // the IANA id was renamed in 2022; Windows without ICU only knows its own name for the zone
        foreach (var id in new[] { "Europe/Kyiv", "Europe/Kiev", "FLE Standard Time" })
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        throw new TimeZoneNotFoundException("Europe/Kyiv");
    }

    /// <summary>Parses the source's answer: {"body": "&lt;JSON as a string&gt;"}. Anything else is a <see cref="FormatException"/>.</summary>
    public static OutageData Parse(string raw)
    {
        try
        {
            using var outer = JsonDocument.Parse(raw);
            if (outer.RootElement.ValueKind != JsonValueKind.Object || !outer.RootElement.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.String)
                throw new FormatException("no body");
            using var doc = JsonDocument.Parse(body.GetString()!);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("regions", out var regions) || regions.ValueKind != JsonValueKind.Array)
                throw new FormatException("no regions");
            var result = new Dictionary<string, OutageRegion>(StringComparer.Ordinal);
            foreach (var r in regions.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object || Str(r, "cpu") is not { Length: > 0 } id) continue;
                var queues = new Dictionary<string, IReadOnlyDictionary<string, int[]>>(StringComparer.Ordinal);
                if (r.TryGetProperty("schedule", out var schedule) && schedule.ValueKind == JsonValueKind.Object)
                    foreach (var q in schedule.EnumerateObject())
                    {
                        if (q.Value.ValueKind != JsonValueKind.Object) continue;
                        var days = new Dictionary<string, int[]>(StringComparer.Ordinal);
                        foreach (var d in q.Value.EnumerateObject())
                            if (d.Value.ValueKind == JsonValueKind.Object) days[d.Name] = SlotCodes(d.Value);
                        queues[q.Name] = days;
                    }
                result[id] = new OutageRegion(id, Str(r, "name_ua"), Str(r, "name_ru"), Str(r, "name_en"),
                    r.TryGetProperty("emergency", out var e) && e.ValueKind == JsonValueKind.True, queues);
            }
            return new OutageData(Str(root, "date_today"), Str(root, "date_tomorrow"), result);
        }
        catch (JsonException ex) { throw new FormatException("not a schedule", ex); }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int[] SlotCodes(JsonElement day)
    {
        var codes = new int[SlotsPerDay];
        foreach (var s in day.EnumerateObject())
            if (TimeOnly.TryParseExact(s.Name, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                && t.Minute % 30 == 0 && s.Value.ValueKind == JsonValueKind.Number && s.Value.TryGetInt32(out var code))
                codes[t.Hour * 2 + t.Minute / 30] = code;
        return codes;
    }

    /// <summary>The Kyiv day as UTC milliseconds: its midnight and the next one (23 or 25 hours apart on clock-change days).</summary>
    public static (long Start, long End) Day(DateOnly day) => (SlotStart(day, 0)!.Value, SlotStart(day.AddDays(1), 0)!.Value);

    /// <summary>UTC ms where a Kyiv wall-clock slot starts; null when the clocks skip that time.</summary>
    private static long? SlotStart(DateOnly day, int slot)
    {
        var local = day.ToDateTime(TimeOnly.MinValue).AddMinutes(30 * slot);
        if (Kyiv.IsInvalidTime(local)) return null;
        // a repeated (fall-back) time takes its first occurrence, the summer offset
        var offset = Kyiv.IsAmbiguousTime(local) ? Kyiv.GetAmbiguousTimeOffsets(local).Max() : Kyiv.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// Consecutive days of slot codes to runs: slot i lasts until the next slot that exists, the last one until the next
    /// midnight; equal neighbours merge, also across midnight. Codes: 1 on, 2 off, anything else unknown.
    /// </summary>
    public static List<OutageRun> Runs(IReadOnlyList<(DateOnly Day, int[] Slots)> days)
    {
        var runs = new List<OutageRun>();
        foreach (var (day, slots) in days)
        {
            var end = Day(day).End;
            for (var i = 0; i < SlotsPerDay; i++)
            {
                if (SlotStart(day, i) is not { } from) continue;
                long to = end;
                for (var j = i + 1; j < SlotsPerDay; j++)
                    if (SlotStart(day, j) is { } next) { to = next; break; }
                var state = (i < slots.Length ? slots[i] : 0) switch { 1 => "on", 2 => "off", _ => "unknown" };
                if (runs.Count > 0 && runs[^1].State == state && runs[^1].To == from)
                    runs[^1] = runs[^1] with { To = to, ToLabel = Label(to, end: true) };
                else
                    runs.Add(new OutageRun(from, to, state, Label(from, end: false), Label(to, end: true)));
            }
        }
        return runs;
    }

    private static string Label(long utcMs, bool end)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(utcMs), Kyiv);
        // an end at midnight reads as the end of that day, not the start of the next
        return end && local.Hour == 0 && local.Minute == 0 ? "24:00" : local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}
