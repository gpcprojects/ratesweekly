# Central-bank decision scenario harness

Purpose: prove — or disprove — that RatesWeekly's generated output is correct on and around the
days a central bank **hikes, cuts or holds**. It exists to answer one question before the app is
put in front of an advanced IRD desk: *when a bank moves, is every number the desk and its
clients read still right?*

## What it actually runs

Everything except Bloomberg is production code:

| Layer | Under test | Faked |
|---|---|---|
| `config\meetings.json` loader | real (`MeetingsStore`, incl. the settled-date migration) | the calendar contents |
| meeting runs | real (`PricingService.MeetingRun`, `ResolveMeetingDates`, gate roll, re-base, guards) | — |
| report | real (`BuildWeekly(meetingsOnly)`, `CompoundedFixing.Stamp`, `FuturesGuard`, `OutlierGuard`) | — |
| history stitching | real (`MeetingSeriesBuilder`, `MeetingRungMap`, despike) | — |
| store | real `HistoryStore` (SQLite, temp file) | its contents |
| renderers | real (`SheetEmail`, `WeeklyEmail`, `DailyBlast`, `DailyBook`/xlsx, `RunsTable`, `ReportStore`) | — |
| Bloomberg | — | `RatesSnapshot` + `ScenarioHistory` (closes from the store, London snaps from the scenario) |

Time is **not** faked. `DateTime.Today` and `DecisionClock.LondonNow()` are read straight from the
machine, exactly as the shipped app reads them, and every scenario dates itself *relative to
today*. "The decision is today, after the statement" is expressed as a decision on today's date
with `DecisionTimeLondon = Cal.TimePassed`; "before the statement" uses `Cal.TimeNotYetPassed`.
There is no test-only branch anywhere in the product.

## Running it

```
dotnet build tests\RateDesk.Scenarios\RateDesk.Scenarios.csproj -c Release
dotnet tests\RateDesk.Scenarios\bin\Release\net8.0\RateDesk.Scenarios.dll list
dotnet tests\RateDesk.Scenarios\bin\Release\net8.0\RateDesk.Scenarios.dll run 7  --out out\scenarios
dotnet tests\RateDesk.Scenarios\bin\Release\net8.0\RateDesk.Scenarios.dll run all --out out\scenarios
```

**One process per scenario, strictly serial.** `MeetingsStore` caches `config\meetings.json` for
the life of the process, and every scenario needs a different one. `run all` spawns the children
itself; never run two scenarios concurrently.

Each scenario leaves `out\scenarios\NN\` behind:

| file | what it is |
|---|---|
| `market.txt` | the whole synthetic market, human-readable — start here when a result surprises you |
| `result.json` | verdict + every failure + the run notes |
| `blast.txt` | the Bloomberg-chat blast |
| `runs_sheet.txt` | the xlsx Runs sheet as text (`Runs.xlsx` is the real workbook) |
| `email_sheet.html` | the sheet-style email body (the default), openable in a browser |
| `email_cards.html`, `email.txt` | the card email and the plaintext |
| `notes.txt`, `report.json` | run notes and the frozen report |

## Writing a scenario

One file per group under `Catalogue\`, a `public static class` with
`public static IEnumerable<ScenarioSpec> All()`. The registry finds it by reflection, so files
never collide. `Group00_Baseline.cs` (quiet control + the positive control) and
`Group01_DecisionDay.cs` (same-day-start and lagged-start decision days) are the worked examples —
copy their shape.

### The rules that keep findings honest

1. **Write the expected numbers first, from the synthetic market, by hand.** Put the arithmetic
   in a comment. `Priced = (mid − fixing) × 100`; `Step = Priced − previous Priced`;
   `Δ = (live mid − the same contract's mark then) × 100`.
2. **Never edit an expectation just to make a scenario pass.** When output disagrees with
   expectation there are exactly three explanations, and you must say which:
   - **setup error** — the synthetic market does not mean what you thought. Fix the setup.
   - **arithmetic error** — your hand derivation was wrong. Fix it, and show the corrected working.
   - **product defect** — leave the expectation alone and report it. This is the output the
     exercise exists to produce.
3. **Make the wrong answer visible.** Give neighbouring contracts levels far enough apart
   (5–15bp) that a mis-rung read produces an obviously different number. A scenario where the
   right and wrong answers coincide proves nothing.
4. **Bank names must be one of the nine** the daily surfaces know: `ECB MPC RBA RBNZ FOMC BOC
   NORGES BOJ RIKSBANK`. Anything else is silently dropped from the blast, workbook and sheet
   email (`DailyBlast.Blocks`), and only the card email would show it. One scenario may use
   several banks; each bank at most once.
5. Everything except the calendar and the market comes from the **shipped** entry for that bank —
   real ticker roots, contributor source, fixing ticker, day counts. Override only what the
   scenario is about.

### Geometry

```
   past starts ......... today ......... decision ... period start ......... next decision ...
   (stay in "dates"; the loader migrates them to pastDates and derives their announcements)
```

- `Dates` = swap-period **starts**, past and future, exactly as the shipped config keeps them.
- `DecisionDates` = **announcements**. The shipped config lists future ones only, and a decision
  stays in the list after it happens — which is what lets today's decision be "announced".
- `Bounds` (the list you pass to `Contract`/`ContractStep`) = the dates the family **renumbers**
  on: the **announcement** for every bank except the Riksbank, the **period start** for the
  Riksbank (`rollsAtPeriodStart`). Include *every* meeting in the window, past and future —
  a missing boundary collapses two contracts onto one rung. The harness fails the scenario when
  that happens, but check `market.txt` anyway.
- Lag: FOMC/MPC start on the decision day (lag 0). ECB, BOJ, SNB, Riksbank start ~6 days later.
  RBA, RBNZ, BOC, Norges start the next day.

### Seeding the market

```csharp
var b = new BankSpec { Bank = "ECB", DecisionTimeLondon = Cal.TimePassed };
b.Dates.AddRange(new[] { s2, s1, st0, st1, st2, st3 });
b.DecisionDates.AddRange(new[] { dec0, dec1, dec2, dec3 });
b.Fix(2.000).FixHist(Cal.D(-70), Cal.D(-1), 2.000);       // the o/n fixing

// live quotes, rung by rung (0 = the run-down that matures at the next period start)
b.Quote(0, mid: 2.000, prevClose: 2.000, eff: s1,  mat: st0);
b.Quote(1, mid: 2.250, prevClose: 2.240, eff: st0, mat: st1);   // feed NOT re-pointed

// history, CONTRACT by contract - the harness puts each value on whichever rung carried that
// contract on that day, so a quiet market is a flat contract on a stepping rung number
b.ContractStep(st1, Bounds, Cal.D(-70), Cal.D(0), dec0, 2.290, 2.300);
```

Raw per-rung seeding (`Close`, `Snap`, `Level`, `LevelStep`) is also available and is the right
choice when the scenario is *about* the rung mapping — it shares nothing with the app's own
derivation.

Two data hazards the harness refuses outright, because either would manufacture a fake finding:

- an **isolated one-day move > ~4.4bp** with agreeing neighbours — the Hampel despike filter
  (`HistoryFilter.Despike`) rewrites it before the code under test sees it. Use a persistent
  level step instead;
- **two contracts on one rung on one day** (a missing boundary, see above).

### Expectations

```csharp
spec.Expect.Add(new BankExpect
{
    Bank = "ECB",
    Fixing = 2.250, Rebased = true,                    // Priced re-based onto the decided period
    Front  = new FrontExpect(dec1, st1, 2.300, 2.250, +5.0, Rebased: true),
    Rows = new List<RowExpect>
    {
        //          start end     mid    priced  step    d1     w1     m1
        new(st1, st2, 2.300, +5.0,  null,  +1.0,  +1.0,  +1.0),
    },
});
spec.NotesContain.Add("CHECK");            // a note the run must carry
spec.NotesNotContain.Add("FUTURES GUARD"); // one it must not
spec.Custom.Add(s => ...);                 // anything else, returning failure strings
```

`null` means **the cell must be blank**; `Any.Num` / `Any.Date` mean **not checked**.
Set `CheckHistoryRows` plus `HistoryRowExpect` to walk the save-down history tables as well.

### What is checked for free, in every scenario

`Invariants.cs` runs on all of them, and none of it depends on the scenario's own expectations:

- the chat blast, the xlsx attachment and the sheet-style email body are **the same table, cell
  for cell**, and each matches the report;
- `Priced == (Mid − Fixing) × 100`, and `Step` differences consecutive `Priced` (skipping Y/E
  turn rows, where the next row carries the cumulative move);
- the front-table line for a bank **is** that bank's first published row, and renders its
  decision date, `*` start-only marker, `†` re-based marker and `% of 25bp` correctly;
- rows ascend, maturities follow starts, nothing published is non-finite or absurd, a turn row
  publishes no numbers, a flat change prints `0.0` and never `+0.0`;
- every published mid reaches the card email and the plaintext;
- the frozen report round-trips (every offline rebuild starts from it).

## The catalogue

| ids | file | what it covers |
|---|---|---|
| 1, 99 | `Group00_Baseline.cs` | the quiet control, and the positive control that proves the suite can go red |
| 2, 3 | `Group01_DecisionDay.cs` | the two decision-day shapes: same-day start and lagged start |
| 4–9 | `Group02_TimingAndFeed.cs` | before/after the announcement time, feed re-pointed or not, no time on file, 1-day and 6-day lags, a calendar behind and a calendar exhausted |
| 10–15 | `Group03_AfterTheDecision.cs` | the day after, the week after, the month after; the mixed-state window; two renumbers in one lookback |
| 16–21 | `Group04_MoveSizes.cs` | fully-priced hike and cut, surprise 25 and 50, hawkish hold, inter-meeting emergency |
| 22–27 | `Group05_ChangeColumns.cs` | the phantom-step trap, the renumber-day correction, the staleness cap, boundary-day sources, weekend walk-back, the CoD fallback |
| 28–33 | `Group06_Riksbank.cs` | period-start renumbering and the year-end turn |
| 34–39 | `Group07_SourcesAndGaps.cs` | contributor vs composite, re-base fallbacks, truncation, stale feeds, the misprint guard, a dead fixing |
| 40–45 | `Group08_Guards.cs` | outlier bars, the futures guard, the completeness gate, notes never becoming content |
| 46–50 | `Group09_WholeEmail.cs` | nine banks, two deciding together, an empty calendar, front ordering, one report across every surface |
| 51–53, 59 | `Group10_Dashboards.cs` | the published per-currency meeting strips on and after a decision day |
| 54, 55 | `Group11_FixingBase.cs` | what Priced is measured against once a bank has moved |
| 56 | `Group12_UnstableLag.cs` | a bank whose settlement lag is not constant |
| 57 | `Group13_History.cs` | the save-down history tables across a decision |
| 58 | `Group14_SnapVsAnnouncement.cs` | the 16:15 close against the 19:00 FOMC statement |
| 61 | `Group15_RebaseFallback.cs` | the re-base fallback after a surprise |
| 62 | `Group16_FrontGuard.cs` | the misprint guard's two arms disagreeing about the front row |

`FINDINGS.md` has the results.

## Known harness limits

- **Time of day is real.** The clock-sensitive scenarios need London time between 00:10 and
  23:50; the runner fails loudly outside that window.
- **`SnapDiscipline` is not applied.** It rewrites the published marks from 16:15 intraday bars
  and would make results depend on when the suite is run. It is exercised by the unit tests
  (`tests\RateDesk.Tests`), not here.
- **Inflation, forward grids and dashboards are out of scope** — the report is built
  `meetingsOnly`, which is what the daily product does.
- **`DateTime.Today` is the machine's LOCAL date.** On a machine east of London (this one runs
  UTC+3) the local date rolls over before London's does; anything run after 21:00 local time is
  a day ahead of the London calendar the decisions are keyed on.
- **`Cal.D(n)` offsets are calendar days** and can land a decision, a period start or an anchor on
  a weekend depending on what day the suite is run. Scenarios that depend on a specific business
  day should use `Cal.Bd(n)` and assert properties rather than fixed dates.
- **`ScenarioHistory.GetLondonSnaps` ignores its `londonTimeOfDay`** and serves the same series for
  the 16:30 and 16:15 requests. Harmless as long as a scenario does not need the two to differ
  across the 2026-08-25 snap-time cutover.
- **`Hygiene.Scan` models the despike filter with a symmetric window** and skips the first and last
  points of a series, so a large move seeded at either end is not flagged. Seeding snaps as well as
  closes (which every `Contract`/`Level` helper does) makes the point moot, because a snap
  overwrites the despiked close.
- **`CalendarHealth` is exposed but not merged into the report notes** (`Surfaces.CalendarWarnings`,
  written to `calendar_health.txt`). That mirrors production, where it runs in the weekly
  `UpdateEngine` only and the daily run never calls it.
