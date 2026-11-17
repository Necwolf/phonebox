# Power Outages Widget (Ukraine) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A 2×2 panel widget that counts down to the next power-off (or, while off, to power-on) for a chosen Ukrainian region and queue.

**Architecture:** The agent (`OutageMonitor`, modeled on `FeedMonitor`) downloads the svitlo.live schedule JSON at most every 15 min while an enabled page shows the widget, converts Kyiv-time 30-min slots into UTC runs, and serves one region+queue slice at `GET /api/outages`. The phone widget (`W.outages`) polls that every 60 s and renders the countdown every second from the runs.

**Tech Stack:** .NET 10 / ASP.NET Core minimal API, xUnit; vanilla ES modules in the Android WebView; Playwright against the web demo (`tools/pages/demo.js`).

**Spec:** `docs/superpowers/specs/2026-11-17-power-outages-widget-design.md` (on branch `personal`; read it first).

## Global Constraints

- Work on branch `feat/ua-power-outages` created from fresh `upstream/main` (`git fetch upstream && git switch -c feat/ua-power-outages upstream/main`). The spec/plan files are NOT on this branch. At the end merge it into `personal`.
- Code, comments, commit messages in English; comments explain why. Do not touch `CHANGELOG.md` or version numbers.
- Source URL: `https://dtek-api.svitlo-proxy.workers.dev/`; fetch interval `TimeSpan.FromMinutes(15)`; fetch only while `config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == "outages")`; one request per interval regardless of widget count.
- Slot codes: `1` = on, `2` = off, anything else/missing = unknown. Slots are Europe/Kyiv local `"HH:mm"`, 48 per day.
- All displayed times are Kyiv time (labels come from the agent); countdowns use UTC ms and `Date.now()`.
- Widget type `outages`, catalogue name `Power outages (Ukraine)`, category `Web & data`, `w:2,h:2,minW:2,minH:2`, icon `💡`.
- Phone freshness limit: 60 min (`updated` age, and time since the phone last reached the agent).
- Panel strings `w.outages.*` in all 12 panel languages (en tr de fr es it pt nl pl ru vi zh); no English copied into other languages (enforced by `tools/ui-tests/i18n.spec.js`).
- DOM only via `h()`/`textContent`, never `innerHTML` with data.
- Tests must not depend on the machine (inject clock/handler; Playwright uses `page.clock`).
- Commands need: `export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH`.

## Review Focus

1. Kyiv DST change days (2026-03-29: 03:00/03:30 don't exist; 2026-10-25: 03:00 hour repeats) — runs must stay ordered, non-overlapping, and cover the day without gaps. → Task 1 test `DstDaysProduceOrderedGaplessRuns`.
2. An off-window that continues past midnight into tomorrow — the "until on" countdown must run to the end of tomorrow's part, not to 00:00. → Task 1 test `RunsMergeAcrossMidnight`.
3. Region whose newest date is not the source's `date_today` (Lviv etc. in October) — `stale: true`, no runs, never a countdown. → Task 2 test `StaleRegionHasNoRuns`.
4. Queue typed loosely (`" 3,1 "`, `"3.1"`, `"03.1"`) — normalized to `3.1`; garbage (`"abc"`, `"3.1; rm"`) → `found: false`, never echoed except as JSON. → Task 2 test `QueueIsNormalizedAndValidated`.
5. Fetch fails after an earlier success — last data stays with `error` set; after 60 min the phone shows "No fresh data" instead of a countdown. → Task 2 test `FailedFetchKeepsLastDataAndError`; Task 4 Playwright `old data stops the countdown`.

---

### Task 1: Schedule parser and Kyiv-time runs

**Files:**
- Create: `agent/PhoneBox.Agent/OutageSource.cs`
- Test: `agent/PhoneBox.Agent.Tests/OutageTests.cs` (class `OutageSourceTests`)

**Interfaces:**
- Produces:
  - `public sealed record OutageRegion(string Id, string NameUa, string NameRu, string NameEn, bool Emergency, IReadOnlyDictionary<string, IReadOnlyDictionary<string, int[]>> Queues)` — queue id → `"yyyy-MM-dd"` → 48 codes (missing slot = 0).
  - `public sealed record OutageData(string DateToday, string DateTomorrow, IReadOnlyDictionary<string, OutageRegion> Regions)`
  - `public sealed record OutageRun(long From, long To, string State, string FromLabel, string ToLabel)` — UTC ms, `State` ∈ `on|off|unknown`, labels Kyiv `"HH:mm"` (`To` at midnight → `"24:00"`).
  - `internal static class OutageSource { OutageData Parse(string raw); TimeZoneInfo Kyiv { get; } List<OutageRun> Runs(IReadOnlyList<(DateOnly Day, int[] Slots)> days); (long Start, long End) Day(DateOnly day); }`

- [ ] **Step 1: Write failing tests** in `OutageSourceTests`:
  - `ParsesTheBodyStringEnvelope`: raw = `{"body":"<escaped inner json>"}` with one region `kiivska-oblast` (names ua/ru/en, `emergency:true`), queue `3.1`, date `2026-11-17` slots `00:00:2,00:30:2,01:00:1` → `data.DateToday == "2026-11-17"`, region found, `Emergency == true`, `Queues["3.1"]["2026-11-17"][0..2] == [2,2,1]`, `[3] == 0`.
  - `MalformedInputThrowsFormatException`: `"not json"`, `{"body":42}`, `{"body":"{}"}` (no regions) → `Assert.Throws<FormatException>`.
  - `RunsMergeEqualSlotsAndLabelInKyivTime`: day 2026-11-17 (UTC+2), slots 0–11 = 1, 12–19 = 2, 20–47 = 1 → 3 runs; run[1] = `off`, `From == 2026-11-17T04:00Z`, `To == 08:00Z`, labels `"06:00"`/`"10:00"`; last run `ToLabel == "24:00"`.
  - `RunsMergeAcrossMidnight`: today slots 44–47 = 2 (rest 1), tomorrow slots 0–3 = 2 (rest 1) → one `off` run from today 22:00 to tomorrow 02:00 Kyiv.
  - `UnknownCodesBecomeUnknownRuns`: codes `0` and `3` → state `unknown`.
  - `DstDaysProduceOrderedGaplessRuns`: for 2026-03-29 and 2026-10-25 with alternating codes per slot: runs ordered, each `To == next.From`, first `From == Day(day).Start`, last `To == Day(day).End`, no run with `To <= From`.
- [ ] **Step 2: Run** `dotnet test agent/PhoneBox.Agent.Tests --filter OutageSourceTests` → FAIL (types don't exist).
- [ ] **Step 3: Implement `OutageSource`.** `Parse`: `JsonDocument` outer → `body` must be a string → parse inner; any structural problem → `FormatException`. Keep unknown codes as their int. `Kyiv`: `TimeZoneInfo.FindSystemTimeZoneById` trying `"Europe/Kyiv"`, then `"Europe/Kiev"`, then `"FLE Standard Time"`. `Day(d)`: UTC ms of Kyiv local midnight of `d` and of `d+1`. `Runs`: slot *i* covers `[start(i), start(i+1))` where `start(i)` = Kyiv local `d 00:00 + 30·i min` converted to UTC (`start(48)` = next midnight); a nonexistent local time (spring-forward) is skipped — its slot is zero-length and dropped; an ambiguous one uses the earlier (summer) offset. Merge adjacent runs with equal state across all given days. Labels formatted from the Kyiv local time of `From`/`To`.
- [ ] **Step 4: Run** the filter again → PASS.
- [ ] **Step 5: Commit** `git add agent/PhoneBox.Agent/OutageSource.cs agent/PhoneBox.Agent.Tests/OutageTests.cs && git commit -m "Outages: parse the svitlo.live schedule into Kyiv-time runs"`

### Task 2: OutageMonitor, `/api/outages`, Wi-Fi allow-list

**Files:**
- Create: `agent/PhoneBox.Agent/OutageMonitor.cs`
- Modify: `agent/PhoneBox.Agent/Program.cs` (register `AddMonitor<OutageMonitor>(builder.Services);` after `FeedMonitor`; map endpoint after `/api/feeds`), `agent/PhoneBox.Agent/Lan.cs:88-99` (`AllowedOverLan` += `path.StartsWithSegments("/api/outages")`)
- Test: `agent/PhoneBox.Agent.Tests/OutageTests.cs` (class `OutageMonitorTests`, harness copied from `FeedMonitorTests`: temp `ConfigStore`, `FeedHttpTests.Fake`, `_now`), `agent/PhoneBox.Agent.Tests/ConfigTests.cs` (`LanTests` += `[InlineData("/api/outages", true)]`)

**Interfaces:**
- Consumes: Task 1 `OutageSource.Parse/Runs/Day/Kyiv`, `OutageData`, `OutageRun`; `FeedHttp.GetAsync(Func<bool,HttpClient>, string url, null, null, ct)` and `FeedHttp.CreateHandler(allowPrivate:false)`.
- Produces:
  - `public sealed class OutageMonitor(ConfigStore config, ILogger<OutageMonitor> log) : BackgroundService` with `internal const string WidgetType = "outages"`, `internal const string Url`, `internal static readonly TimeSpan Interval`, test seams `internal HttpMessageHandler? Handler { get; init; }`, `internal Func<long> Now { get; init; }`, `internal Task TickAsync(CancellationToken)`, `public bool Shown()`, `public OutageView View(string? region, string? queue)`, `internal static string? NormalizeQueue(string? q)`.
  - `public sealed record OutageRegionInfo(string Id, string NameUa, string NameRu, string NameEn, bool Emergency)`
  - `public sealed record OutageView(long Updated, string? Error, OutageRegionInfo? Region, bool Stale, bool Found, long DayStart, long DayEnd, bool Tomorrow, bool TomorrowOff, IReadOnlyList<OutageRun> Runs)` — serialized camelCase by `Results.Json`: `updated, error, region, stale, found, dayStart, dayEnd, tomorrow, tomorrowOff, runs`.
  - `GET /api/outages?region=<id>&queue=<id>` → `Results.Json(monitor.View(region, queue))`.

- [ ] **Step 1: Write failing tests** in `OutageMonitorTests` (sample body built in a helper `Body(...)`, `_now` = 2026-11-17 12:00 Kyiv):
  - `NothingIsFetchedWithoutAShownWidget`: config whose only `outages` widget is on a disabled page → `TickAsync` → `handler.Seen` empty.
  - `OneFetchPerIntervalForAnyNumberOfWidgets`: two `outages` widgets → one request; `_now += Interval - 1s` → still one; `+1s` → two.
  - `ViewReturnsTodayAndTomorrowRunsForTheQueue`: `View("kiivska-oblast","3.1")` → `Found`, `!Stale`, `Region.NameEn == "Kyiv Region"`, runs cover today+tomorrow, `Tomorrow == true`, `TomorrowOff` matches data, `DayStart/DayEnd` == `OutageSource.Day(2026-11-17)`.
  - `StaleRegionHasNoRuns`: region whose only date is `2026-10-17` → `Stale == true`, `Runs` empty.
  - `UnknownRegionOrQueue`: `View("nowhere","3.1").Region == null`; `View("kiivska-oblast","9.9").Found == false`, `Runs` empty.
  - `QueueIsNormalizedAndValidated`: `NormalizeQueue(" 3,1 ") == "3.1"`, `("03.1") == "3.1"`, `("60.1") == "60.1"`, `("abc") == null`, `("3.1; rm") == null`, `(null) == null`.
  - `TomorrowWithOnlyUnknownSlotsIsNotPublished`: tomorrow date present with all `0` → `Tomorrow == false`.
  - `FailedFetchKeepsLastDataAndError`: first tick OK, next interval handler returns 500 → `View(...).Error` non-null, runs still present, `Updated` unchanged.
  - Before any successful fetch: `View(...)` → `Updated == 0`, `Region == null`.
- [ ] **Step 2: Run** `dotnet test agent/PhoneBox.Agent.Tests --filter "OutageMonitorTests|LanTests"` → FAIL.
- [ ] **Step 3: Implement `OutageMonitor`.** Loop like `FeedMonitor.ExecuteAsync` (tick every 5 s); `TickAsync`: return if `!Shown()` or `Now() < _nextAt`; set `_nextAt = Now() + Interval` before fetching (failures wait too); 20 s timeout; on success store parsed `OutageData`, `_updated = Now()`, `_error = null`; on failure keep data, set `_error` (`"no answer in 20 s"` / `"not a schedule"` for `FormatException` / message), `log.LogInformation`. `View`: region by id; `Stale` = region has no `DateToday` entry; runs = `OutageSource.Runs` over today and (if tomorrow has any slot ≠ 0) tomorrow; `NormalizeQueue` regex `^\s*0*(\d{1,2})[.,](\d)\s*$`. Register service, map endpoint, extend `AllowedOverLan` with a one-line comment why (read-only public schedule the phone needs over Wi-Fi).
- [ ] **Step 4: Run** the filter → PASS; then full `dotnet test agent/PhoneBox.Agent.Tests` → all pass.
- [ ] **Step 5: Manual check:** run the agent with an isolated config containing one enabled `outages` widget (`--config <scratch>/config.json --port 18765`), wait ≤ 10 s, `curl 'http://127.0.0.1:18765/api/outages?region=kiivska-oblast&queue=3.1'` → `found:true`, non-empty `runs`, `updated > 0`. Stop it.
- [ ] **Step 6: Commit** `git commit -m "Outages: fetch the schedule while the widget is shown and serve one queue at /api/outages"`

### Task 3: Catalogue entry, counts, privacy and docs

**Files:**
- Modify: `agent/PhoneBox.Agent/widgets.json` (new entry after `crypto`), `android/app/src/main/assets/www/main.js:969` (`WIDGET_ICONS` += `outages: '💡'`), `README.md:42` and the widget count in `docs/README.{de,es,fr,it,nl,pl,pt,ru,tr,vi,zh}.md` (122 → 123), `PRIVACY.md` (row in the second table after "Crypto widget"), `docs/GUIDE.md:82` (add "**Power outages (Ukraine)**" to Web & data)

**Interfaces:**
- Produces: widget options `region` (select), `queue` (text, default `"1.1"`), `title` (text, default `""`).

- [ ] **Step 1: Run** `python3 tools/check-release.py` → PASS (baseline).
- [ ] **Step 2: Add the catalogue entry:** `{"type":"outages","category":"Web & data","name":"Power outages (Ukraine)","icon":"💡","desc":"Countdown to the next planned power cut, or to power coming back, for your region and queue (schedules from svitlo.live, fetched by the PC every 15 minutes).","w":2,"h":2,"minW":2,"minH":2,"options":[{"key":"region","label":"Region","type":"select","default":"kiivska-oblast","choices":[[id, Ukrainian name] × 18]},{"key":"queue","label":"Queue (e.g. 3.1; Kyiv groups up to 60.1)","type":"text","default":"1.1"},{"key":"title","label":"Title (empty = region · queue)","type":"text","default":""}]}`. The 18 ids/names: `kiivska-oblast` Київська область, `kyiv` Київ, `dnipro-dnem` м. Дніпро (ДнЕМ), `dnipro-cek` м. Дніпро (ЦЕК), `dnipropetrovska-oblast` Дніпропетровська область, `odeska-oblast` Одеська область, `lvivska-oblast` Львівська область, `poltavska-oblast` Полтавська область, `cherkaska-oblast` Черкаська область, `chernihivska-oblast` Чернігівська область, `kharkivska-oblast` Харківська область, `khmelnytska-oblast` Хмельницька область, `ivano-frankivska-oblast` Івано-Франківська область, `rivnenska-oblast` Рівненська область, `ternopilska-oblast` Тернопільська область, `zakarpatska-oblast` Закарпатська область, `zaporizka-oblast` Запорізька область, `zhytomyrska-oblast` Житомирська область.
- [ ] **Step 3: Icon, counts, PRIVACY, GUIDE.** PRIVACY row: `| Power outages widget | \`dtek-api.svitlo-proxy.workers.dev\` (svitlo.live), fetched by the agent at most every 15 minutes and only while an enabled page shows the widget; the phone never contacts it. Remove the widget to stop it | Nothing but the request: the region and queue are picked out on this computer |`.
- [ ] **Step 4: Run** `python3 tools/check-release.py` → `PASS: …; 123 widgets; …`, and `dotnet test agent/PhoneBox.Agent.Tests` → all pass (catalogue tests).
- [ ] **Step 5: Commit** `git commit -m "Outages: add the widget to the catalogue, privacy table and docs"`

### Task 4: Phone widget, demo mock, translations, UI tests

**Files:**
- Modify: `android/app/src/main/assets/www/widgets/web.js` (add `W.outages` after `W.feeds`), `android/app/src/main/assets/www/style.css` (`.w-outages` block), `android/app/src/main/assets/www/i18n.js` (`w.outages.*` in all 12 `STRINGS` languages, next to `w.feeds.*`), `tools/pages/demo.js` (mock `/api/outages` next to `/api/feeds`)
- Create: `tools/ui-tests/outages.spec.js`

**Interfaces:**
- Consumes: `GET ${API}/outages?region=&queue=` → `OutageView` JSON (Task 2); widget options from Task 3; helpers already in `web.js`/`main.js`: `h`, `t`, `api(path, method, timeoutMs)`, `every(ms, fn)`, `lang` via `PhoneBoxI18n`.
- Produces: DOM: root `.w-outages` with `.ou-t` (title), `.ou-l` (label), `.ou-v` (big value), `.ou-n` (next window / note), `.ou-strip` (24-h bar, children `.ou-seg.off|.on|.unknown` + `.ou-now`), `.ou-warn` (⚠ emergency / error). Root gets class `off` / `on` / `nodata` for the accent.

i18n keys (English values; translate each into tr de fr es it pt nl pl ru vi zh, keeping `{…}` placeholders):
`w.outages.untilOff` "Until power off" · `w.outages.untilOn` "Until power on" · `w.outages.noneToday` "No outages today" · `w.outages.noneTomorrow` "No outages scheduled tomorrow" · `w.outages.tomorrowUnknown` "Tomorrow not published yet" · `w.outages.next` "Off {from}–{to}" · `w.outages.onAt` "Power on at {time}" · `w.outages.stale` "Schedule not updated" · `w.outages.notFound` "No queue {queue} here" · `w.outages.noData` "No fresh data" · `w.outages.emergency` "Emergency outages in effect"

Mock rules in `demo.js` (deterministic, relative to the page's `Date.now()` at first call `t0`): `region === 'lvivska-oblast'` → `stale:true, runs:[]`; queue not matching `^\d{1,2}\.\d$` or `=== '9.9'` → `found:false, runs:[]`; otherwise `updated: now()`, runs `on [dayStart, t0+40min)`, `off [t0+40min, t0+40min+3h)`, `on [.., dayEnd+24h)` with Kyiv labels via `Intl.DateTimeFormat('en-GB',{timeZone:'Europe/Kyiv',hour:'2-digit',minute:'2-digit'})`, `tomorrow:true, tomorrowOff:false`, region `{id, nameUa:'Київська область', nameRu:'Киевская область', nameEn:'Kyiv Region', emergency: region === 'dnipropetrovska-oblast'}`.

- [ ] **Step 1: Write failing Playwright tests** `tools/ui-tests/outages.spec.js` (config injected like `exchange.spec.js`, widget `{type:'outages', w:2, h:2, options:{region:'kiivska-oblast', queue:'3.1'}}`, `page.clock.install()` before `goto`):
  - `counts down to the next power cut`: `.ou-l` = "Until power off", `.ou-v` matches `/^(39|40):\d\d$/`, `.ou-n` = `Off HH:MM–HH:MM`, root has class `on`.
  - `switches to power-on countdown inside the window`: `page.clock.fastForward('00:41:00')` → `.ou-l` "Until power on", `.ou-v` matches `/^2:5\d$/`, root class `off`.
  - `stale region shows no countdown`: region `lvivska-oblast` → `.ou-v` text "—" and `.ou-n` "Schedule not updated".
  - `unknown queue`: queue `9.9` → `.ou-n` "No queue 9.9 here", `.ou-v` "—".
  - `old data stops the countdown`: route-free — `page.clock.fastForward('01:01:00')` with the mock's `updated` frozen at `t0` (mock returns `updated: t0` when `localStorage['pb-demo-outages-frozen']==='1'`) → `.ou-v` "—", `.ou-n` "No fresh data".
  - `emergency badge`: region `dnipropetrovska-oblast` → `.ou-warn` visible with "Emergency outages in effect".
  - `fits a 2 × 2 slot`: every child's bounding box inside the widget box at 780×360.
- [ ] **Step 2: Run** (with the large-backlog server from CLAUDE.md) `cd tools/ui-tests && npx playwright test outages.spec.js` → FAIL.
- [ ] **Step 3: Implement** the mock in `demo.js`, `W.outages` in `web.js` (`needs:['tick']`; `every(60_000)` loads; render on each tick; compute: run containing now → `off` ⇒ untilOn to that run's `to` (`onAt` label); `on` ⇒ next `off` run → untilOff + `next` label, none ⇒ `noneToday`, plus `noneTomorrow`/`tomorrowUnknown` from `tomorrow/tomorrowOff`; `unknown`/no run ⇒ `—`; freshness: `now - updated > 60 min` or `now - lastOk > 60 min` ⇒ `—` + `noData`; value format `H:MM` when ≥ 1 h, `MM:SS` below; region name `nameRu` when panel lang is `ru`, else `nameEn`; title `o.title || "<region short> · <queue>"`; strip from `dayStart..dayEnd`, hidden when the box is < 150 px tall), styles in `style.css`, translations in `i18n.js`.
- [ ] **Step 4: Run** `npx playwright test outages.spec.js i18n.spec.js` → PASS; then the full suite `npx playwright test` → all pass.
- [ ] **Step 5: Commit** `git commit -m "Outages: the 2 × 2 countdown widget, demo data and UI tests"`

### Task 5: Integrate into `personal` and try it on the phone

**Files:** none new.

- [ ] **Step 1: Full checks on the feature branch:** `dotnet test agent/PhoneBox.Agent.Tests`, `python3 tools/check-release.py`, `cd android && ./gradlew lintDebug assembleDebug`, Playwright full suite → all pass; record the counts.
- [ ] **Step 2: Push the branch** `git push -u origin feat/ua-power-outages`.
- [ ] **Step 3: Merge into personal** `git switch personal && git merge --no-edit feat/ua-power-outages && git push origin personal`.
- [ ] **Step 4: Run on the real setup:** restart the agent from `personal` (`pkill -f PhoneBoxAgent; dotnet run --project agent/PhoneBox.Agent -f net10.0`), install the new APK over USB (`adb install -r android/app/build/outputs/apk/debug/app-debug.apk` — may take minutes on the Samsung), ask the user to add the widget in Settings › Pages and pick region/queue; confirm `/api/outages` answers and the widget shows a countdown.
- [ ] **Step 5: Report** to the user what passed and offer the upstream path (issue first, then PR from `feat/ua-power-outages`).
