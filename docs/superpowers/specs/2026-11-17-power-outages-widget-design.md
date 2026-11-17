# Power outages widget (Ukraine) — design

Status: draft for approval · 2026-11-17
Built on a feature branch from `upstream/main` (upstream-ready), merged into `personal` for daily use; offered
upstream (issue first) if it looks good. This spec lives only on `personal`.

## Intent

In Ukraine, planned hourly outages (ГПВ, графіки погодинних відключень) are published per region for 12 sub-queues
(1.1 … 6.2; Kyiv city uses its own groups 1.1 … 60.1), for today and tomorrow, in 30-minute steps. The user wants a
**2×2 panel widget** that shows, for a **chosen region (oblast / city) and queue**, **how long until the power goes
off, or — while it is off — how long until it comes back**. Glanceable on the desk phone; several widgets for
several addresses are possible.

## Goals

- 2×2 widget (default size 2×2; min 2×2) with a large countdown: "until off" while power is on, "until on" while off.
- Per-widget options: region, queue.
- Today + tomorrow schedule, so the countdown crosses midnight correctly.
- Honest states: no data yet, source unreachable, region's schedule is stale (not for today), queue not in the data,
  no outages today, tomorrow not published yet.
- Works over USB and over Wi-Fi (paired), in the web demo (mock data) and in the Playwright tests.

## Non-goals

- Emergency outages (ГАВ) — schedules don't contain them; we only show the region's `emergency` flag as a warning.
- Address → queue lookup. The user types/picks the queue they already know.
- Notifications/rules ("power off in 10 min"). Possible later via the rules engine; not in this change.
- Regions not served by the chosen source (see Approaches) — later, behind a second provider.

## Anti-goals (would be a failure even if it "works")

- Showing "power is on" or a countdown when we don't actually know (stale/missing data must never look valid).
- The phone talking to third-party servers, or more than one download per interval regardless of widget count.
- Fetching when no enabled page shows the widget.

## Data source

`https://dtek-api.svitlo-proxy.workers.dev/` — community Cloudflare Worker (svitlo.live, MIT-licensed client
`chaichuk/svitlo_live`), no key. One GET returns `{"body": "<JSON string>"}`; ~284 KB. Inner JSON:

```json
{ "date_today": "2026-11-17", "date_tomorrow": "2026-11-18",
  "regions": [ { "cpu": "kiivska-oblast", "name_ua": "Київська область", "name_ru": "…", "name_en": "Kyiv Region",
                 "emergency": false,
                 "schedule": { "3.1": { "2026-11-17": { "00:00": 1, "00:30": 2, … "23:30": 1 }, "2026-11-18": { … } } } } ] }
```

- Slot codes (confirmed in svitlo_live `coordinator.py`): `1` = power on, `2` = off, anything else / missing = unknown.
- Slot keys are local **Europe/Kyiv** times, 48 per day.
- Observed 2026-11-17: 18 regions — 15 oblasts with 12 queues, `kyiv` with 60 groups, `dnipro-dnem`, `dnipro-cek`.
  Five oblasts (Lviv, Ivano-Frankivsk, Rivne, Ternopil, Zakarpattia) only had dates from October → **stale**.
- No CORS headers → the WebView can't fetch it directly (one reason the agent fetches).
- Known upstream issue: incomplete 30-minute windows for Poltava X.1 queues (svitlo_live #71) — we display what the
  source says.

## Approaches (considered; user picked A)

- **A. Agent fetches, phone gets only its slice — chosen.** One `OutageMonitor` in the agent downloads the JSON every
  15 min, only while an enabled page shows an `outages` widget; parses once; `/api/outages` returns per-widget data.
  Works around CORS, one download for any number of widgets, the phone never contacts a third party, the source's
  host sees only this computer's IP. Parsing isolated behind a provider so YASNO-official / alerts.energy (key) can be
  added later (matters for upstream).
- B. YASNO official API only — reliable, but Kyiv + Dnipro only; contradicts "any region". Rejected.
- C. Phone fetches directly (like weather) — blocked by CORS, 284 KB per phone per poll. Rejected.

## Design

### Agent

- `agent/PhoneBox.Agent/OutageMonitor.cs` — `BackgroundService`, modeled on `FeedMonitor`:
  - `WidgetType = "outages"`, `Interval = 15 min`; tick every few seconds, fetch only when due **and**
    `config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == "outages")`.
  - HTTP via the existing `FeedHttp` helpers (public-only handler, size cap, 20 s timeout, no cookies/proxy). Failed
    fetch waits a full interval; the last good data stays, with the error kept.
  - Test seams like FeedMonitor: `Handler`, `Now`.
  - Parse into an in-memory model: per region `{ id, nameUa, nameRu, nameEn, emergency, queues: { id → [slots] } }`,
    plus `dateToday`. **Convert** each day's 48 slots into absolute **UTC epoch-ms intervals** using
    `TimeZoneInfo` "Europe/Kyiv" (IANA id works on Windows .NET 6+ via ICU) — the phone never does tz math. Merge
    adjacent equal slots into runs: `[{ from, to, state: "on" | "off" | "unknown" }]`. DST days (46/50 real
    half-hours) handled by converting each local slot start independently.
  - Parser in its own static class (`OutageSource.Parse(string json) → OutageData`), pure, unit-tested.
- `GET /api/outages?region=<cpu>&queue=<id>` in `Program.cs`, next to `/api/feeds`. Validates both against the
  parsed data (never echoes raw input into anything but JSON). Response:

  ```json
  { "updated": 1763380000000,          // when the agent last fetched successfully (0 = never)
    "error": null,                      // last fetch error, if any
    "region": { "id": "kiivska-oblast", "name": "Київська область", "emergency": false },
    "stale": false,                     // region has no data for the source's date_today
    "found": true,                      // queue exists in that region
    "runs": [ { "from": …, "to": …, "state": "off" }, … ],   // today + tomorrow, UTC ms, ordered
    "tomorrow": true }                  // tomorrow's day present
  ```
  Region name: the agent returns `nameUa`/`nameRu`/`nameEn`; the phone shows `nameRu` for panel language `ru`,
  `nameEn` otherwise (the panel has no Ukrainian locale; Ukrainian name used in the settings select).
  **Display times are Kyiv time** (decision): the agent also sends, per run, ready Kyiv-local labels
  `fromLabel`/`toLabel` ("14:00") and `dayStart`/`dayEnd` (UTC ms of today's Kyiv midnights) for the strip, so the
  phone never does tz math; countdowns use the UTC ms fields and `Date.now()`.
  **Tomorrow** (decision): `tomorrow` = tomorrow's date present with at least one known slot. If tomorrow has no
  `off` slot the widget says "no outages scheduled tomorrow (per schedule)", never "no outages until end of tomorrow"
  — a placeholder day of all-on can't be told apart from a real one.
- `Lan.cs`: add `/api/outages` to the Wi-Fi allow-list. Justification for the PR: read-only, public schedule data,
  no control, no secrets; the phone needs it over Wi-Fi like `/api/feeds`.
- Not in `ToPublic()` — nothing new in config; options live in the widget like other widgets.

### Widget catalogue (`agent/PhoneBox.Agent/widgets.json`)

`{"type": "outages", "category": "Web & data", "name": "Power outages (Ukraine)", "w": 2, "h": 2, "minW": 2, "minH": 2, …}`
options:
- `region` — `select`, the 18 known ids with Ukrainian names (static list in the catalogue; unknown ids show
  "region not in the data").
- `queue` — `text`, default `"1.1"`; placeholder explains format `X.Y` (Kyiv: groups up to 60.1). Trimmed and
  validated by the agent. (Builder's call to switch to a select if the settings UI supports per-region choices.)
- `title` — `text`, default empty = "<region short> · <queue>".

Plus: icon in `WIDGET_ICONS` (main.js) — `💡`; README widget count (checked by `tools/check-release.py`).

### Phone widget (`android/app/src/main/assets/www/widgets/web.js`, `W.outages`)

- `needs: ['tick']`; load `/outages?region=…&queue=…` every 60 s (cheap — served from the agent's cache), render
  every tick (1 s) from `runs` and `Date.now()`; no network per tick.
- Find the run containing now:
  - `off` → big "до вкл." countdown to the end of the contiguous off-time (merging across midnight if tomorrow
    continues it); dark/red accent.
  - `on` → "до відкл." countdown to the next `off` start; if none in the known horizon → "сьогодні без відключень" /
    "до кінця завтра без відключень".
  - `unknown` or beyond the horizon → "немає даних", never a countdown.
- Countdown format: `H:MM` (≥ 1 h), `MM:SS` under an hour.
- Bottom: next window "off 14:00–18:00" (local phone time formatting via existing helpers) and a 24-hour strip of
  today's runs with a now-marker. On 2×2 keep it legible; drop the strip below a height threshold.
- **Freshness** (decision): if `updated` is older than 60 min, or the phone hasn't reached the agent for 60 min, the
  countdown is replaced by "no fresh data" (last window still shown greyed). Enforced on the phone (it knows both
  ages); tested with the page clock.
- States: loading; offline (agent unreachable — keep last data, show ⚠ until the 60-min rule above); `error` from agent with no data;
  `stale` → "графік не оновлено"; `!found` → "черги X.Y немає"; `emergency` → small ⚠ "діють аварійні" badge.
- Text via `textContent` / `h()` only (CONTRIBUTING). Strings via i18n keys (`w.outages.*`) in **all 12 panel
  languages** (user's choice: the widget follows the panel language; the panel has no Ukrainian locale —
  `i18n.spec.js` requires every panel key in every language).
- Without the agent (web demo) it must show an empty state, not throw.

### Demo + tests

- `tools/pages/demo.js`: mock `/api/outages` building runs relative to `now()` (e.g. off in 40 min for 3 h), so the
  demo and UI tests are deterministic and machine-independent.
- Agent unit tests (`agent/PhoneBox.Agent.Tests/OutageTests.cs`): parse sample (inner-JSON-in-`body`), slot→run
  merging, Kyiv tz incl. a DST-change day, stale detection, unknown queue/region, codes other than 1/2 → unknown,
  malformed JSON → error kept + last good data stays, no fetch while no enabled page shows the widget, one fetch for
  several widgets.
- Playwright (`tools/ui-tests/outages.spec.js`, English panel): renders "until off" with the mock, switches to "до вкл." when the
  clock passes the window start (page clock), stale and not-found states, fits 2×2 at 780×360.

### Privacy

`PRIVACY.md` row: *Power outages widget — `dtek-api.svitlo-proxy.workers.dev` (svitlo.live), fetched by the agent at
most every 15 minutes and only while an enabled page shows the widget; the phone never contacts it — Sends: nothing
but the request (the region and queue are filtered on this computer) — Off: remove the widget.*

## Constraints

- CONTRIBUTING: one feature per PR; issue first before the upstream PR (new widget/integration); English code,
  comments, commit messages; don't touch CHANGELOG/versions; tests must not depend on the machine (clock injected).
- Kyiv time regardless of the PC's and phone's time zones.
- Third-party free source with limited capacity: never more than one request per 15 min per agent.

## Decisions made by Claude (check these)

- Feature branch from `upstream/main`, merged into `personal`.
- Kyiv time for all displayed times; agent sends labels.
- Tomorrow without any `off` → "no outages scheduled tomorrow (per schedule)".
- 60-minute freshness limit, enforced on the phone.
- `queue` is a free text option validated by the agent (Kyiv has 60 groups).
- Icon 💡, category "Web & data", 15-minute interval.

## Left to the builder

- Exact visual styling within the 2×2 sketch below; colors from the existing palette.
- Whether the agent returns one localized region name or all three.
- The precise strip-hiding threshold.

```
┌───────────────┐   ┌───────────────┐   ┌───────────────┐
│ ⚡ Київ · 3.1  │   │ 🕯 Київ · 3.1  │   │ ⚡ Київ · 3.1  │
│  до відкл.    │   │  до вкл.      │   │  сьогодні     │
│   1:24        │   │   2:36        │   │  без відкл.   │
│ ▓▓░░▓▓▓░░▓▓▓▓ │   │ ▓▓░░▓▓▓░░▓▓▓▓ │   │ ▓▓▓▓▓▓▓▓▓▓▓▓▓ │
│ off 14:00–18:00│   │ on ≈ 18:00    │   │ завтра: ще нема│
└───────────────┘   └───────────────┘   └───────────────┘
```
