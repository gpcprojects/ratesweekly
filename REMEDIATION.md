# Remediation plan - central-bank decision days

> **EXECUTED 2026-08-27 (v0.16.0). 62/62 scenarios green, 314/314 unit tests green, WPF and CLI
> both build.** Desk answers: futures breach BLOCKS like a CHECK; never invent mids (so the guard
> withholds instead of marking); re-base window runs through period start + 1 business day; and on
> an FOMC evening the board does NOT roll past a statement its marks predate.
>
> Delivered: P1 (F5/F10/F12), P2 (replaced by the withhold rule), P3 (F4/F11), P4 (F3),
> P5 (F1/F6), P6 (F2/F7 via the recorded-maturity resolver - no config curation needed after all),
> P7 (F9, behaviour not just a note), P8 (scenarios wired into azure-pipelines.yml).
>
> **Follow-up delivered:** the F2 residual (history older than the maturity records) is closed by
> `Series/RungShiftScan.cs`, which reads the renumbering off the seeded price history. 64 scenarios,
> 322 unit tests.
>
> **Still outstanding, and needing a live terminal:** `python tools/audit_email_dates.py` and
> `python tools/verify_strip_changes.py` must both come back clean before this ships, and one
> live decision day should be watched with the build in place (ECB 10-Sep, FOMC 16-Sep,
> MPC 17-Sep). Nothing else in P9 has been done.

Source of truth for the work: `tests\RateDesk.Scenarios\FINDINGS.md` (11 findings, 12 red
scenarios) and the harness that produced them, `tests\RateDesk.Scenarios\README.md`.

Baseline at the time of writing: **v0.15.0, 62 scenarios, 50 pass, 314 unit tests green, `src/`
untouched.** Target: **v0.16.0, 62/62 scenarios green, no finding downgraded by weakening a test.**

The rule for the whole plan, from `CLAUDE.md`: *get sign-off before building on inferred
quantities.* Three of the fixes turn on conventions only the desk can confirm. Those questions are
P0 and they are asked before any code is written — but most of the work does not wait on them.

---

## P0 — Desk questions (ask first, code around them)

| # | Question | Blocks | Why it cannot be inferred |
|---|---|---|---|
| Q1 | After a meeting family re-points at an announcement, is the just-decided period still quoted as a numbered rung, or is it absorbed into the run-down (rung 0)? | F1 fix shape | Determines whether the re-base can find a *live* post-decision mark or must read the statement-day close. Probe: on the next ECB/BOJ decision day + 1, `BbgSmoke` the family's SW_EFF_DT chain and compare to the day before. |
| Q2 | How many business days does each o/n fixing lag before it prints the post-decision rate? EFFR, SONIA, ESTR, CORRA, RBACOR, NZOCRS, SSARON, TONA, SWESTR, NOWA. | F6 gate width | Sets `fixingLagDays` per run. Currently inferred as 1 business day for EFFR/SONIA; unconfirmed for the rest. |
| Q3 | On an FOMC day, should the 19:00 statement roll a board whose marks are the 16:15 close — or should the board stay pre-decision with a note? | F9 behaviour | A product decision about what "closing run" means, not a bug. |
| Q4 | The xlsx cannot carry a `†` in a numeric cell. Acceptable substitute: warning fill + cell comment + a footnote row under the block? | F8 xlsx half | Desk owns the sheet's visual grammar. |
| Q5 | Should a `FUTURES GUARD TRIGGERED` breach **block** publication (Yes/No dialog) or only warn loudly? | F5 severity | DESIGN §12 says treat it as a fault until proven otherwise; blocking is the logical consequence, but it changes the press ritual. |
| Q6 | May we hand-curate **past** announcement dates into `config\meetings.json` for all ten runs (~400 days back, from the official calendars / BBG ECO)? | F7, part of F2 | Data change, but it is a calendar the desk owns. Sources are published, so this is documentation, not estimation. |

Q4, Q5 and Q6 are cheap yes/no answers. Q1 needs one probe on a decision day. Q2 is a desk fact.
**Q3 is the only one that should hold up a release.**

---

## Work packages, in execution order

Each package is one PR off `master`, each ends green on the full battery (below), each names the
scenario that must flip. Do not reorder: P3–P6 touch the same two files and the ordering keeps the
diffs readable.

### P1 — Notes and gates that are one line each *(no published number changes)*

**Fixes F5, F10, F12.** Nothing about the boards moves; these only make the app say what it
already knows. Ship first, independently — they are worth having even if the rest slips.

1. **F5 — a futures breach reaches the pre-publish gate.**
   `src\RateDesk.Core\FuturesGuard.cs:81` — prefix the breach string with `OutlierGuard.Prefix`:
   ```csharp
   return $"{OutlierGuard.Prefix}: {TriggerPrefix} — {sched.Name}: {tk} {window} implies …"
   ```
   Keep `TriggerPrefix` *inside* the text so `tools\verify_strip_changes.py` and any log grep still
   match. `ConfirmChecks` (`MainWindow.xaml.cs:310`) then gates it with no change of its own.
   Subject to **Q5** — if the answer is "warn, don't block", instead add a dedicated
   `ShowFuturesNotes` popup beside `ShowStaleNotes` and leave the prefix alone.
   *Proves:* scenario **43** green.

2. **F10 — a dead o/n fixing says so.**
   `src\RateDesk.Core\PricingServiceBoards.cs`, at the end of `MeetingRun`, beside the existing
   `StaleNotes` block: when `res.RefPct is null && res.Rows.Count > 0`, add
   `CHECK: {name} — the {RefName} fixing is unquoted; Priced, Step and % of 25bp are blank on
   every row`. Route it through `res.StaleNotes` (already surfaced into `rep.Notes` by
   `BuildWeekly`) or a new `res.Notes` list.
   *Proves:* new scenario **63** (assert the note exists and the columns are blank); **39** stays green.

3. **F12 — the calendar guard covers the state that disables the machinery, and runs daily.**
   - `src\RateDesk.Weekly.Core\CalendarHealth.cs:73` — drop the `decisions.Count > 0` gate on the
     `decisionTimeLondon` warning, and add a new one for `decisions.Count == 0`:
     *"no decision calendar — the decision-day front roll and the Priced re-base are both disabled"*.
   - `src\RateDesk.Weekly.Core\Daily\DailyBuilder.cs:225-234` — call
     `CalendarHealth.Check(MeetingsStore.Schedules, snap, store, DateTime.Today)` after
     `OutlierGuard.Check` and fold the results into `rep.Notes`, `CHECK:`-prefixed **only** where the
     warning means a published number is affected (missing time, empty calendar), plain otherwise
     (runway, observed-roll drift) so the daily press does not gain a modal it never had.
   *Proves:* scenario **06** green.

**Risk:** near zero. **Regression watch:** the CHECK popup now fires on a futures breach and on a
calendar hole — confirm on a live run that neither fires today (they should not; the shipped
calendars are complete and the guard reads ≤1.6bp live).

---

### P2 — A synthesized mid is marked on every surface *(F8)*

`RunsTable.Row` already carries `Synthetic`; only three renderers ignore it.

- `src\RateDesk.Weekly.Core\Daily\DailyBlast.cs` — `Render`: append `†` to the mid and add a
  trailing footnote line `† mid is the neighbour midpoint — the quoted print was rejected as
  implausible`. Widen the `Mid` column from 7 to 8 so the fixed-width table still aligns (the blast
  is pasted into an IB window; check the width against the desk's own paste).
  `Html`: same, in the mid `<td>`.
- `src\RateDesk.Core\WeeklyEmail.cs:~408` — `PlainText`: append `†` to `{m.MidPct:0.000}` and add
  the same footnote after the last card.
- `src\RateDesk.Weekly.Core\Daily\DailyBook.cs` — `WriteRunsSheet`: per **Q4**, set a warning fill
  on the mid cell, attach a cell comment with the rejection text, and write a footnote row under the
  last block. Keep the cell numeric — the number format is desk-specified.

**Harness note:** `Invariants.Cell(..., allowDagger:)` was deliberately loosened so the suite does
not freeze the current placement. Once this ships, tighten it back: the dagger becomes *required* on
every surface, and `allowDagger` goes away.

*Proves:* scenario **38** green. *Risk:* cosmetic only, but the blast is fixed-width — eyeball a real
paste before release.

---

### P3 — The change columns stop reading invented numbers *(F4, F11)*

Both live in the stitcher and its caller; both are small and both are pure correctness.

1. **F4 — the stitcher's misprint guard gets the live guard's front exemption.**
   `src\RateDesk.Core\PricingServiceBoards.cs:1506`. `MeetingSeriesBuilder` already receives
   `runDates`; capture `var frontMeeting = runDates.FirstOrDefault()` and skip the guard block
   entirely when `meeting == frontMeeting` — mirroring `GuardedMid`'s `lo >= 1` rule, which exists
   because *"the front meeting is the one that gaps for real"*. While there, add the Y/E-turn
   stand-down the live guard has and this arm lacks (a turn print is a legitimate far-off value and
   must neither be judged nor used as a neighbour).
   *Proves:* scenario **62** green.

2. **F11 — the Δ1d fallback respects the exclusion the stitcher just applied.**
   `src\RateDesk.Core\PricingServiceWeekly.cs:~268`:
   ```csharp
   wm.D1Bp ??= row.MidSource == "ticker" || row.MidSource == "future" ? row.CoDBp : null;
   ```
   `row.CoDBp` is `(mid − PX_CLOSE_1D) × 100` off the snapshot, and inside an announcement→start
   window yesterday's close *is* a mixed-state close. Build `var map = new MeetingRungMap(sched,
   run.Rows.Select(r => r.Date))` once per run in `BuildWeekly` (cheap, and the stitcher builds the
   same thing) and gate the fallback:
   ```csharp
   bool prevClean = !map.IsBoundary(PrevBd(DateTime.Today)) && !map.IsMixedState(PrevBd(DateTime.Today))
                    && !map.IsBoundary(DateTime.Today);
   wm.D1Bp ??= prevClean && (row.MidSource == "ticker" || row.MidSource == "future") ? row.CoDBp : null;
   ```
   Blank is the honest value there; the fallback keeps working for its real case (a rung with no
   pre-roll history — scenario 27).
   *Proves:* new scenario **64** (a run inside the mixed-state window whose Δ1d must be blank, not
   the mixed-state CoD); **27** and **14** stay green.

**Risk:** moderate — these change published change columns. **Regression watch:** scenarios 22–27 and
10–15 are the guard rail; `tools\verify_strip_changes.py` must re-reconcile against a live terminal
before release.

---

### P4 — The dashboard strip reads the rung its own lookbacks read *(F3)*

`src\RateDesk.Weekly.Core\Series\RollingStrip.cs:76-78`. The level is the only value in the class
that skips the boundary-day step-back:

```csharp
// before
mids[i] = RungAt(contracts[i].Contract) is { } r0 ? store.ValueAsOf(ticker(r0), asOf) : null;
// after — the same resolver the 1w and 1m levels already use
mids[i] = RolledValue(store, ticker, bounds, contracts[i].Contract, asOf, maxIndexProbe, map);
```

`RolledValue` walks off a boundary or mixed-state day before counting rungs, and recurses when the
close it lands on is itself unattributable — which is precisely what the current code does not do.
Leave `RungAt` in place for `tkNow` (the row's displayed ticker) and for the maturity-record
truncation, or better, switch both to `map.RungFor(contract, asOf)` so one derivation serves the
whole method.

*Proves:* scenarios **59** and **53** green; **51**, **52** stay green (they are the controls that
say the fix did not break the ordinary day). *Risk:* low and contained to the dashboards — the
email, blast and xlsx do not use this path. *Regression watch:* re-render the 28 pages and diff a
currency page against the previous run on a non-decision day; nothing should move.

---

### P5 — Priced is measured against the rate that is actually in force *(F1, F6)*

The largest correctness gain, and the one the desk will feel. Two changes to the same block,
`src\RateDesk.Core\PricingServiceBoards.cs:845-878`.

1. **F1 — walk forward from the decision, not backward.**
   The fallback currently takes *the last close strictly before the decision day*, which cannot
   contain a surprise. Replace it with a forward search over documented data, using Bloomberg's own
   recorded fields:
   - extend `IHistoryProvider` with `DateTime? EffectiveOn(string ticker, DateTime day) => null;`
     — `HistoryStore.EffectiveOn` already exists and its own comment says it was added *"so future
     anchors can map rungs from Bloomberg's own fields instead of calendar inference"*;
     `StoreBackedHistory` delegates, `RefDataClient` keeps the null default;
   - for each business day `d` from `dec` to today, descending, and each rung `n` in 0..3, if
     `EffectiveOn(tick(n), d) == effStart` then take that ticker's close on `d` and stop. That is
     the decided period's own mark, from a day on which it was demonstrably that contract.
   - keep the pre-decision close as the **last** resort, and when it is used set a new
     `res.RefRebasedStale = true` so the surfaces can say *"(rebased, pre-statement)"* rather than
     claiming the base is current. A `CHECK` note names it.
   Subject to **Q1**: if the family still quotes the decided period as a numbered rung after the
   re-point, the live path can be extended to find it by `Effective` rather than only at index 0,
   which is simpler and better — prefer that if the probe says so.
   *Proves:* scenario **61** green.

2. **F6 — the gate follows the fixing, not the period start.**
   `:873` — `today < eff` becomes `today <= eff.AddBusinessDays(fixingLagDays - 1)` with a new
   per-run `fixingLagDays` in `meetings.json` (default **1**, i.e. `today <= eff`), pending **Q2**.
   For FOMC and MPC that turns an empty window into a one-day window covering the decision day
   itself — the day the board rolls and the desk reads it. Every other run is unchanged at the
   default.
   *Proves:* scenarios **54** and **55** green.

**Risk:** high — this moves the headline column. **Do not ship P5 without:** Q1 and Q2 answered; a
live decision day observed with the fix in place (the next FOMC is 16-Sep-26, MPC 17-Sep-26, ECB
10-Sep-26); and `tools\audit_email_dates.py` + `verify_strip_changes.py` both clean.

---

### P6 — The rung map stops inferring what Bloomberg records *(F2, F7)*

Two steps, cheapest first. The first alone closes F7 and softens F2.

1. **Config: past announcements become data.** *(Q6)*
   Add ~400 days of settled announcement dates to `decisionDates` for all ten runs, from the
   official calendars (FOMC/MPC/SNB/BOC/RBA/RBNZ/Norges/ECB) and BBG ECO (Riksbank, BOJ). That
   makes `MeetingCalendar.AnnouncementDates` yield them verbatim, so:
   - the BOJ's unstable lag no longer matters — nothing is derived (**F7 closed**);
   - every past announcement→start window is masked as mixed-state, including the one the 14-day
     cluster currently mislays in F2.
   Add a `CalendarHealth` check that warns when a run's `decisionDates` do not reach at least 400
   days back, so the list is maintained rather than decaying.
   *Proves:* scenario **56** green.

2. **Code: prefer recorded maturities over derived boundaries.**
   `MeetingRungMap.RungFor(contract, onDay)` infers from a boundary count. Where the store has a
   per-day record it should not infer at all: add an optional
   `Func<int, DateTime, DateTime?> recordedEffective` to `MeetingRungMap`, and when it answers for
   `(n, onDay)`, resolve the rung by matching `Effective == contract` directly; fall back to the
   count when it does not (older history, tickers never snapped).
   This is what finally closes **F2**: an unscheduled meeting inserts a boundary into the past, but
   the recorded fields say what each rung actually *was* on each day, so history stops being
   re-numbered retroactively. It also removes the 14-day cluster's dependence on the
   "no two meetings within 14 days" premise for any day the store covers.
   Wire it at the five call sites the map already unified: the stitcher, `BankHistoryRows`,
   `FallbackIngest`, `RollingStrip`, `MoverScan`.
   *Proves:* scenario **21** green.

**Risk:** highest in the plan, and the reason it is last. **Regression watch:** the whole change-column
surface. Run the 62 scenarios, the 314 unit tests, both audit scripts, and diff a full daily run
against the previous day's stored report — only the rows you intend to change may move. Consider
shipping step 1 in v0.16.0 and step 2 in v0.17.0.

---

### P7 — The FOMC clock *(F9)* — note now, behaviour on Q3

Immediate, no behaviour change: when a run is in `SnapDiscipline.Mode.Snap1615` and a bank's
`decisionTimeLondon` is **after** `SnapDiscipline.SnapAt`, add a note —
*"{bank} announces at {time}, after this run's 16:15 marks; the board has rolled past a decision the
prices predate"*. That alone removes the silent contradiction scenario 58 objects to.

If **Q3** says the board should stay pre-decision, gate the roll on the marks' as-of time rather than
the wall clock: pass the effective mark time into `MeetingRun` and compare the announcement against
*that* instead of `LondonNow()`.

*Proves:* scenario **58** green (the note satisfies the second half; the first half needs Q3).

---

### P8 — Make it stay fixed

1. **The scenario suite becomes a gate.** Add to `azure-pipelines.yml`, after the unit-test step:
   ```yaml
   - script: dotnet run --project tests/RateDesk.Scenarios/RateDesk.Scenarios.csproj -c Release -- run all --out $(Build.ArtifactStagingDirectory)/scenarios
     displayName: CB decision scenarios (62, must stay green)
   ```
   `run all` already returns non-zero on any red. Publish the artifact directory so a CI failure
   ships its `market.txt` and `result.json` with it. Note the suite is time-of-day sensitive
   (00:10–23:50 London) — the runner fails loudly outside that window rather than silently passing.
2. **Port the load-bearing assertions into `tests\RateDesk.Tests`** so they run even where the
   scenario project does not: the re-base forward walk, the strip's boundary-day read, the stitcher's
   front exemption, the Δ1d mixed-state gate, and the futures-guard prefix. Unit-level, no harness.
3. **Tighten the harness back** where it was loosened to avoid freezing current behaviour:
   `Invariants.Cell(allowDagger:)` (after P2) and the turn-row model check (leave as is — that one
   was corrected, not loosened).
4. **Harness debts** flagged during the exercise, worth an hour: `ScenarioHistory.GetLondonSnaps`
   ignores `londonTimeOfDay`; `Hygiene.Scan` skips the first and last point of a series;
   `Cal.D(n)` can land a scenario's dates on a weekend; `Checker` short-circuits on `NoRun` before
   the other expectations. None affect a shipped finding — all four were checked.

---

### P9 — Operational, for the desk not the compiler

- The `CHECK` modal fires on most rows of a bank that surprises by more than 12bp, which is every
  real move day. Consider collapsing per-run notes into one line per bank before the dialog, so the
  gate stays readable rather than becoming something to click through.
- The stale-feed popup fires *after* the blast, workbook, shared-drive copy and email fragments are
  written. Move `ShowStaleNotes` before the render, beside `ConfirmChecks`, or say plainly in the
  dialog that the artefacts already exist.
- A run whose calendar is exhausted publishes nothing and disappears from every client-facing
  surface. The operator gets a `CHECK`; the reader gets silence. Consider a placeholder row.

---

## Verification battery — after every package

```
dotnet test tests\RateDesk.Tests\RateDesk.Tests.csproj -c Release                      # 314+, all green
dotnet build tests\RateDesk.Scenarios\RateDesk.Scenarios.csproj -c Release
dotnet tests\RateDesk.Scenarios\bin\Release\net8.0\RateDesk.Scenarios.dll run all --out out\scenarios
```

Before any release, with a live terminal:

```
python tools\audit_email_dates.py          # every rendered date vs each rung's own SW_EFF_DT
python tools\verify_strip_changes.py       # independent raw-BDH restitch + the four futures guards
```

Both must come back with zero mismatches, as they did on 2026-08-20 (75/0 and 67/67). A fix that
moves a change column and leaves `verify_strip_changes` clean is a fix; one that does not is a
regression with a green test suite.

## Release ritual (standing, do not shortcut)

Publish into a **fresh** staging directory. Verify `FileVersion` **and** size (~172MB; ~9.6MB means
the single-file bundling step failed and the version check will not catch it). Build
`src\RateDesk.Weekly` before releasing — the test projects do not compile the WPF project. Then
exit the running `RatesWeekly.exe`, swap `publish\RatesWeekly.exe`, relaunch. Bump the app and CLI
csproj versions in lockstep.

## Suggested release shape

| Version | Contains | Gate |
|---|---|---|
| v0.16.0 | P1, P2, P3, P4, P6 step 1, P7 note, P8 | Q4, Q5, Q6 answered |
| v0.17.0 | P5, P7 behaviour, P6 step 2 | Q1, Q2, Q3 answered + one live decision day observed |

P1–P4 are independently shippable and carry no dependency on any desk answer except Q4/Q5. If the
session budget is tight, **P1 + P4 alone** remove one high finding and three notes for about an
hour of work and near-zero regression risk.
