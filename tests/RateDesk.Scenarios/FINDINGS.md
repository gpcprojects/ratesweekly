# Central-bank decision days - findings

> **STATUS 2026-08-27, v0.16.0: ALL TWELVE CLOSED. 62/62 scenarios green, 314/314 unit tests
> green.** Every finding below now has a scenario that fails if it comes back. What changed, and
> the desk decisions behind it, is recorded in `REMEDIATION.md`; the fixes themselves are in
> `src/` with the finding number in the comment beside each one.
>
> Two decisions changed the shape of the fixes:
>   * **"We should never have to invent mids."** So the neighbour-misprint guard no longer
>     substitutes the neighbour midpoint - it WITHHOLDS. A rejected print publishes a label
>     (`n/a`) and no numbers, on every surface, exactly as a Y/E turn row does, and the step chain
>     steps over it. F8 disappeared rather than getting a better marker.
>   * **"Don't roll - the marks are the close."** So the decision gates read the clock the PRICES
>     belong to (`PricingService.MarksAsOfLondon`), not the wall clock. A run pressed after a
>     19:00 FOMC statement keeps the pre-decision board its 16:15 marks belong to, and says so.
>
> **Follow-up, same day:** the residual F2 exposure is closed too. `RungShiftScan` reads the
> renumbering off the strip's own price history — which IS backfilled 45 days on a machine's first
> run — so an unscheduled decision no longer has to wait for maturity records to accrue. Scenarios
> 63 and 64 prove it on a store holding prices and nothing else, and it abstains rather than
> guesses whenever the prices cannot say. 64 scenarios now.
>
> One fix turned out to be worth more than its finding: `MeetingRungMap` now prefers Bloomberg's
> own per-day record of what each rung pointed at (`IHistoryProvider.EffectiveOn`, served from the
> store's maturity table) over the derived boundary count. Evidence beats inference, and it closed
> F2 and F7 together - and made the mixed-state exclusion a fallback rather than a blanket refusal,
> so attributable days are now used instead of discarded.


**61 scenarios + 1 positive control.** The record below is how they stood at v0.15.0, when the
audit was run: 50 passing, 12 red across 12 distinct issues. Each section states what the app did
then; the fix that closed it is in `src/`, and the scenario named beside the finding is now the
regression guard that fails if it returns.

Scope — what RatesWeekly generates on and around the days a central bank hikes, cuts or holds: the
CB front table, the meeting rows (Mid / Priced / Step), the change columns, the chat blast, the
xlsx attachment, both email bodies, the plaintext email, the save-down history tables and the
published dashboard strips.

Method — `tests\RateDesk.Scenarios`. Each scenario writes its own `config\meetings.json`, seeds a
synthetic Bloomberg (live snapshot + a real SQLite store + London snaps) and drives the SHIPPING
code path end to end: `MeetingsStore` → `BuildWeekly` → `CompoundedFixing.Stamp` → `FuturesGuard`
→ `OutlierGuard` → every renderer. Expected values are derived by hand from the synthetic market
and written as literal numbers. Time is not faked: `DateTime.Today` and `DecisionClock.LondonNow()`
are read exactly as the app reads them, and every scenario dates itself relative to today. There
is no test-only branch anywhere in the product. `README.md` has the harness and its limits.

```
dotnet build tests\RateDesk.Scenarios\RateDesk.Scenarios.csproj -c Release
dotnet tests\RateDesk.Scenarios\bin\Release\net8.0\RateDesk.Scenarios.dll run all --out out\scenarios
dotnet tests\RateDesk.Scenarios\bin\Release\net8.0\RateDesk.Scenarios.dll run 61  --out out\scenarios
```

Each scenario leaves `out\scenarios\NN\` behind: `market.txt` (the synthetic world in full),
`blast.txt`, `runs_sheet.txt`, `Runs.xlsx`, `email_sheet.html`, `email_cards.html`, `email.txt`,
`strips.txt`, `calendar_health.txt`, `notes.txt`, `report.json`, `result.json`.

---

## Summary

| # | Finding | Severity | Red scenario |
|---|---|---|---|
| 1 | The re-base falls back to a pre-statement price, so Priced keeps the surprise for the whole decision-to-start window | high | 61 |
| 2 | An inter-meeting decision makes every change column read the neighbouring contract | high | 21 |
| 3 | Dashboard strips carry the neighbouring contract when the render as-of is a renumber day | high | 59, 53 |
| 4 | The front row's change columns are differenced against a fabricated anchor | high | 62 |
| 5 | A triggered futures guard does not gate publication | high | 43 |
| 6 | FOMC and MPC never re-base Priced; the other eight banks do | medium-high | 54, 55 |
| 7 | A bank with an unstable settlement lag mis-rungs anchors in the announcement→start gap | medium | 56 |
| 8 | A guard-synthesized mid reaches clients unmarked | medium | 38 |
| 9 | On FOMC days the 16:15 close precedes the 19:00 statement | medium | 58 |
| 10 | A dead o/n fixing blanks Priced and Step for a whole run with no note | medium | — (39 passes; code-verified) |
| 11 | The Δ1d fallback re-admits a mixed-state close the stitcher just excluded | medium | — (14 passes; code-verified) |
| 12 | A run with no announcement time, or no decision calendar at all, degrades silently on the daily path | low | 06 |

Findings 1–9 and 12 are each pinned by a red scenario that fails against a hand-derived
expectation. Findings 10 and 11 are established from the source and from a passing scenario's
recorded output; they are stated separately for that reason.

---

## Rejected claims

Recorded because a false alarm costs more than a missed one. Each of these was raised during the
exercise and does **not** stand:

- *"A Y/E turn row publishes a Priced value"* (scenarios 30/31/33). True of the in-memory report,
  which keeps the real print by design so the guards and the futures blend can use it. Every
  renderer substitutes the label — verified cell by cell in the blast, the workbook, the sheet
  email, the card email and both front tables. The step chain across the turn is also correct: the
  row after a turn carries the cumulative move (+30.0 = +45.0 − +15.0 in scenario 31).
- *"The dashboard strip is wrong on a decision day"* (scenario 51). An artefact of my first
  harness, which rendered as of `DateTime.Today`; production renders as of `store.LatestDate()`.
  Corrected; the scenario now passes. The real exposure is finding 3.
- *"The 14-day cluster drops the scheduled announcement, so the change columns are wrong"*
  (scenario 21). The cluster does drop it, but the scheduled period's START survives as a boundary
  and the count is preserved. The red is real; the cause is finding 2.
- *"An emergency-day Δ1d of −42.0 is correct"* — my own first reading. Re-derived: the anchor must
  be the same contract's mark under the numbering in force **then**, which was one rung nearer.
  Finding 2 stands.

---

## 1. The re-base falls back to a pre-statement price, so Priced keeps the whole surprise

**Severity: high.** Every published Priced on that bank, for the whole decision-to-start window.
**Scenario: 61.**

Between a decision and the start of the period it decided, Priced re-bases onto that period's own
OIS. There are two ways to get it (`PricingServiceBoards.cs:866-878`):

1. the **live** mid of `quotes[0]`, which the announced-gate shift makes the decided period on the
   statement day itself;
2. otherwise, that contract's last **close strictly before the decision day**.

Path 2 is a pre-decision price. It cannot contain anything the decision surprised the market with.
Once the feed re-points — normally by the next morning — `quotes[0]` is the run-down again, its
effective date is before the decision, and path 2 is the only one left.

Measured (scenario 61 — ECB hiked 25bp yesterday, the market had priced 2bp, the period starts in
five days, the feed has re-pointed):

| | published | against the rate the ECB actually set |
|---|---|---|
| fixing shown | **2.250 †(rebased)** | 2.480 |
| front row Priced | **+28.0bp** | +5.0bp |
| second row | **+31.0bp** | +8.0bp |
| third row | **+33.0bp** | +10.0bp |

Every row is 23bp high — exactly the part of the move the market had not priced — and the cell
carries the dagger while the blast prints `(€STR 2.250 rebased)`, both asserting that the base *is*
the decided period's own OIS. The desk also sees the entire Priced column jump by the surprise
overnight on an unchanged market, which reads as a roll fault rather than a base change.

Reaches all eight lagged runs: ECB, BOJ, SNB and the Riksbank for six days a meeting; RBA, RBNZ,
BOC and Norges for one. On the statement day itself the live path is used and the base is right —
scenarios 3, 7, 16, 17 and 20 all pass.

---

## 2. An inter-meeting decision makes every change column read the neighbouring contract

**Severity: high impact, low frequency** — and it lands on the single day the desk is read hardest.
**Scenario: 21.**

`MeetingRungMap` numbers a historical day by counting the boundaries between that day and the
contract, using **today's** boundary list. That is right while the calendar is stable, because the
boundaries were already known then. An unscheduled meeting inserts a NEW boundary into the
past-facing window: from the moment it is announced, every earlier day is numbered as though the
market had always known about it, while the recorded data was quoted under the old numbering.

Measured (scenario 21 — an emergency 50bp cut announced today, effective in six days, twelve days
before the scheduled Governing Council; yesterday the family had one fewer rung):

| published row | Δ1d / Δ1w / Δ1m published | that contract's own move |
|---|---|---|
| 14-Sep-26 | **−42.0** | −46.0 |
| 02-Nov-26 | **−45.0** | −49.0 |
| 21-Dec-26 | **−47.5** | −50.5 |

The error is one inter-contract gap on every row and every horizon — 4bp here because the
contracts are 4bp apart; a whole step (25bp+) on a steep strip. `Mid`, `Priced` and `Step` stay
correct and internally consistent, and the wrong numbers are identical on all five surfaces, so no
cross-surface check can see it.

`MeetingRungMap.cs:44-46` also drops the scheduled announcement from the boundary list (it falls
inside the 14-day cluster of the unscheduled one, and the cluster keeps the earliest), leaving the
scheduled meeting's boundary on its period start six days late and its mixed-state window
unmarked — the same class of error as finding 7, armed for the week after the scheduled meeting.

The same mechanism fires for any newly-added boundary inside the lookback window, including a
`decisionDates` top-up that adds a meeting the calendar was previously missing.

---

## 3. Dashboard strips carry the neighbouring contract when the render as-of is a renumber day

**Severity: high** — investor-facing, wrong level and wrong-signed change.
**Scenarios: 59 (every row), 53 (partial re-point). Controls: 51, 52 — both pass.**

The weekly render uses `asOf = store.LatestDate()`, normally the previous business day
(`MainWindow.xaml.cs:596`, `Cli\Program.cs:93`). `RollingStrip.Build` resolves each row's rung as
of that date:

```csharp
int idx = bounds.Count(b => b > asOf.Date && b <= contract.Date);   // RollingStrip.cs:69
mids[i] = store.ValueAsOf(ticker(r0), asOf);                        // RollingStrip.cs:76-78
```

A boundary falling **on** `asOf` is excluded by `b > asOf.Date`, so every row maps one rung too
near. The same class's own lookback helper does not make that mistake — `RolledValue` steps back
off a boundary day before counting (`RollingStrip.cs:143-144`) — so the 1w and 1m levels on the
same row are right while the current level is wrong, and the rendered change is a phantom of one
full inter-meeting step.

It bites whenever the newest stored close is an announcement day: whenever the weekly run happens
the day after a bank announced. The ECB announces on a Thursday; a Friday run hits it. The
product's own probe records the EESF composite re-pointing only *between* the 24-Jul and 27-Jul
closes around a 23-Jul announcement — not by the announcement day's own close — which is exactly
what scenario 59 models.

Measured (scenario 59 — every contract flat all month, so nothing should move):

| dashboard row | published level | that contract's own close | rendered 1w change | true |
|---|---|---|---|---|
| 20-Oct-26 | 2.250 | 2.300 | **−5.0bp** | 0.0bp |
| 08-Dec-26 | 2.300 | 2.330 | **−3.0bp** | 0.0bp |

The email, the blast and the xlsx are **not** affected — they come from `MeetingRun` and live mids.
`MoverScan` re-derives its own series (`MoverScan.cs:158-183`) and is not affected. What is
affected is `CurrencyPage` — the 28 per-currency pages on the public site, and the single-file
dashboard pack attached to the weekly email.

One line, already written elsewhere in the same file: resolve the current level through
`RolledValue(..., then: asOf)` instead of `RungAt` + `ValueAsOf`.

---

## 4. The front row's change columns are differenced against a fabricated anchor

**Severity: high** — the most-read row on the board, silently wrong, with no marker.
**Scenario: 62.**

The neighbour-misprint guard deliberately refuses to judge the **front** row —
"Edge rows are never judged — the front meeting is the one that gaps for real"
(`PricingServiceBoards.cs:924`, `lo >= 1`). The stitcher carries its own copy of the guard for the
same reason but keys the exemption on the generic index instead of the row position
(`PricingServiceBoards.cs:1506`, `idx - 1 >= 1`). On a decision day the front published row's
recent history is read at `idx = 2` — the newest window starts at today's boundary and contains
only today — so the test passes and the front contract's own closes are rewritten to the neighbour
midpoint.

Measured (scenario 62 — the front contract re-priced yesterday to 2.150 and has not moved since;
its neighbours sit at 2.460 and 2.490):

```
StartDate      Mid  Priced   Step    Δ1d    Δ1w    Δ1m
08-Oct-26    2.150   -35.0      —  -32.5  -33.0  -33.0     <- Δ1d should be 0.0
```

The live guard spares the print, so `Mid` is the real 2.150. The Δ1d anchor is 2.475 — the midpoint
of the two neighbouring generics — so one row asserts a real price and a change measured from an
invented one. Δ1w and Δ1m are right (−33.0): their anchors predate the gap and were not rewritten,
which is what makes the 0.5bp disagreement between Δ1d and Δ1w so easy to miss.

The `CHECK` note that fires reports the fabricated −32.5 as if it were a market move.

---

## 5. A triggered futures guard does not gate publication

**Severity: high.**
**Scenario: 43.** (Scenario 42 confirms the agreeing case reports correctly.)

The futures guard cross-checks the meeting rows against exchange-settled contracts that share
none of the OIS machinery — no rolling generics, no stitcher, no calendars. DESIGN.md §12 calls a
breach a signal to "treat as a roll/calendar/re-base fault until proven otherwise": the strongest
independent evidence the app has that a decision-day board is wrong.

The pre-publish gate filters on one prefix:

```csharp
var checks = notes.Where(n => n.StartsWith(OutlierGuard.Prefix + ":")      // "CHECK:"
                              && !n.Contains("PRE-CLOSE RUN")).ToList();  // MainWindow.xaml.cs:310
```

The breach note begins `FUTURES GUARD TRIGGERED` (`FuturesGuard.cs:30`), so it matches neither
that gate nor the stale popup. `grep -rn TriggerPrefix src/` finds only its declaration and the one
format string — nothing consumes it. A 25bp break ships silently, while a +12.1bp Δ1d (usually a
genuine market move) blocks the same build. Contrast `CompoundedFixing.cs:108`, where the
completeness gate deliberately borrows `OutlierGuard.Prefix` so that it "must demand eyes, not just
a log line".

---

## 6. FOMC and MPC never re-base Priced; the other eight banks do

**Severity: medium-high** — wrong by the size of the move, on the most-read row, for about two
business days per meeting, with no marker.
**Scenarios: 54, 55.**

The re-base gate is `today < effStart` (`PricingServiceBoards.cs:873`). FOMC and MPC start the
period **on** the decision date — lag 0 in the shipped config — so the window is empty and the
re-base can never fire. Their fixings are published a day in arrears (EFFR and SONIA for day T
print on T+1 and carry the pre-decision rate), so the base is stale on the decision day and the
day after.

Measured (scenario 54 — the Fed cut 25bp yesterday; the current period is quoted at 3.650, EFFR
still prints 3.900): front row Priced **−34.0bp**, against −9.0bp measured on the rate now in
force; fixing shown 3.900 with no marker.

Scenario 55 puts both banks in one table on one day: the ECB line is re-based to 1.900 and marked
`†`; the FOMC line still reads against 3.900 and is marked with nothing. Two columns headed
"Priced (bp)", two different bases, nothing on the page to say so.

Not a regression against the incumbent sheet, which also uses the raw fixing — but an
inconsistency the app introduces by fixing the problem for eight banks and not the two largest.
`MeetingRefOverrides` exists as a manual escape hatch; nothing in the WPF app writes to it.

---

## 7. A bank whose settlement lag is not constant mis-rungs anchors in the gap

**Severity: medium.** The BOJ is the only exposed run.
**Scenario: 56.**

Past announcements are not in the shipped calendars, so they are recovered as *period start −
median lag* — but only when the lag is stable to within a day (`MeetingCalendar.LagIsStable`). The
BOJ's shipped lags are 1, 2, 3 and 6 days, so the derivation is refused and every past boundary
falls back to the **period start**, one to six days after the family actually renumbered. Nothing
masks the gap: the mixed-state window runs from a boundary to its start, and when the boundary *is*
the start there is no window.

Measured (scenario 56 — announcement 9 days ago, period start 5 days ago, every contract flat):

| published row | Δ1d | Δ1w | Δ1m |
|---|---|---|---|
| 24-Sep-26 | 0.0 | **−5.0** | 0.0 |
| 11-Nov-26 | 0.0 | **−4.0** | 0.0 |
| 29-Dec-26 | 0.0 | **−3.0** | 0.0 |

Δ1d and Δ1m are right; only the anchor that lands inside the gap is wrong, which is what makes it
hard to spot. The Riksbank's lag is also unstable (6–8 days) but it renumbers at the period start
(`rollsAtPeriodStart`) and is unaffected — scenarios 28 and 29 pass.

---

## 8. A guard-synthesized mid reaches clients unmarked

**Severity: medium.**
**Scenario: 38.**

When the misprint guard rejects an implausible interior quote it publishes the neighbour midpoint.
The design says that value is "published FLAGGED, never bare". It is flagged in the two HTML email
bodies (`†` plus a footnote). It is **not** flagged in the chat blast (`DailyBlast.Render` writes
the bare number), the xlsx attachment (`DailyBook.WriteRunsSheet` writes a numeric cell, which
cannot carry the dagger as things stand) or the plaintext email (`WeeklyEmail.PlainText`).

A `CHECK` note does gate the run, so the desk sees it. The clients who receive the blast paste and
the workbook do not. Scenario 38 also confirms the good half of the rule: the live guard correctly
spares the front row.

---

## 9. On FOMC days the desk's 16:15 close precedes the 19:00 statement

**Severity: medium** — arguably a labelling problem rather than a numbers problem.
**Scenario: 58.**

`SnapDiscipline` pins every published mark to the 16:15 London snap from 16:15 onwards. The front
roll fires at `decisionTimeLondon`, which is **19:00** for the FOMC; every other run announces
before 16:15. A daily run pressed after a Fed statement publishes 16:15 marks — taken while the
market was still waiting — on a board whose front row has already rolled past the decision.

Measured (scenario 58): the period the Fed just decided is absent from the run, and every Δ1d reads
0.0 on a day the Fed cut 25bp. Both are individually correct for a 16:15 close product; the
combination is a closing run whose *shape* knows about the decision and whose *numbers* do not,
with nothing on the page saying so.

---

## 10. A dead o/n fixing blanks Priced and Step for a whole run, with no note

**Severity: medium.** Established from the source and scenario 39's recorded output; 39 passes.

With the fixing ticker unquoted, `res.RefPct` is null, so `Priced` is null on every row, `Step` —
which is built from Priced — is null too, and the `% of 25bp` cell is empty. The mids still
publish. Nothing in the report says why: `CompoundedFixing.Stamp` raises a `CHECK` only when a run
produces **no rows**, so a run with rows and no fixing ships a table with two empty columns and no
explanation on any surface. The behaviour is honest (nothing is fabricated) but silent.

---

## 11. The Δ1d fallback re-admits a mixed-state close the stitcher just excluded

**Severity: medium.** Established from the source and scenario 14's recorded output; 14 passes.

The stitcher refuses to source a value from a mixed-state day (between an announcement and its
period start) because the family is renumbering through it. `BuildWeekly` then applies a fallback:

```csharp
wm.D1Bp ??= row.MidSource == "ticker" || row.MidSource == "future" ? row.CoDBp : null;
```

`row.CoDBp` is `(mid − PX_CLOSE_1D) × 100` straight off the snapshot — and inside the
announcement-to-start window, yesterday's close **is** a mixed-state close. So the column the
stitcher deliberately blanked is filled from the very print it rejected. Blank is the honest value
there; the fallback exists for a different case (a rung with no pre-roll history) and does not
distinguish them.

---

## 12. A run with no announcement time, or no decision calendar, degrades silently on the daily path

**Severity: low** — config-regression guards, not live defects. All ten shipped runs carry both.
**Scenario: 06.**

With no `decisionTimeLondon` the decision-day roll degrades to the next morning — documented and
deliberate. On the decision day itself the front line then shows today's decision date and `+25.0`
priced (`+100%` of 25bp) for a move already delivered. `CalendarHealth` does warn
(`CalendarHealth.cs:73-75`) but it runs only inside the weekly `UpdateEngine`
(`UpdateEngine.cs:100`); the daily run never calls it and no note reaches any generated surface.

The neighbouring case is worse-behaved: with an **empty** `decisionDates` list, both the front roll
and the re-base are disabled, and both `CalendarHealth` warnings are gated on `decisions.Count > 0`
— so the one configuration that turns off the decision-day machinery entirely raises no warning at
all. Scenario 48 confirms the front table degrades honestly to `{start} *` with its footnote.

---

## What was checked and found correct

Each is a scenario that passes against hand-derived expectations, not an untested area:

- **The decision-day front roll** (2, 3, 4, 5, 7, 8, 9): the just-decided period leaves the board
  at the announcement whether or not the feed has re-pointed; dates and quotes shift together so
  each row keeps its own price; the gate self-disarms once the feed catches up and shifts twice
  when two decisions are outstanding; before the announcement time nothing moves.
- **The re-base on the statement day** (3, 7, 16, 17, 20, 35): fires for every lagged family off
  the decided period's live mid, falls back correctly to the composite when a contributor page has
  no history, and is marked `†` / `(rebased)`. Its failure mode the next morning is finding 1.
- **Roll-corrected change columns** (22–27): a quiet market across a renumber prints 0.0; the
  renumber-day change-on-day correction fires; the 10-day staleness cap blanks rather than
  stretches; boundary-day closes never anchor while the 16:15 snap does; weekend walk-back works;
  the Δ1d CoD fallback fires only for real prints.
- **The days after a decision** (10–15): the mixed-state window is excluded from every stitched
  anchor, the re-base switches off when the period starts, and anchors crossing one or two
  renumbers compare the same contract.
- **Move sizes and direction** (16–20): fully-priced hikes and cuts, surprise 25bp and 50bp, and a
  hawkish hold — correct signs on every surface, with the outlier guard firing where it should.
- **The Riksbank** (28–33): the announcement-gated roll with a period-start renumber, the roll-day
  correction on the start day, `Y/E Turn` labelled on every surface with no numbers riding along,
  the step chain carrying the cumulative move across the masked meeting, and `trustConfigDates`.
- **Sources and gaps** (34–39): contributor prices merged with composite dates, a truncating run,
  the >1h stale warning, the live guard sparing the front row, and a missing fixing degrading to
  blank rather than fabricated.
- **Guards and notes** (40–45): absolute and cross-sectional outlier bars with the front exempt,
  the futures guard agreeing, the completeness gate, and no note text leaking into any output.
- **The whole email** (46–50): nine banks with one deciding, two banks deciding together, an empty
  decision calendar rendering `start *`, front-table ordering and `% of 25bp` signs, and one hard
  day carried consistently across all five surfaces.
- **The save-down history tables across a decision** (57): every row carries the value that period
  was quoted at on that day, including through the announcement-to-start window; the announcement
  day publishes no 1d change.
- **Cross-surface identity** (all 62): the blast, the xlsx, the sheet email and the meeting cards
  are the same table cell for cell; `Priced == Mid − Fixing` and `Step == ΔPriced` hold on the
  published numbers; the frozen report round-trips.

## Operational notes

- A `CHECK` note opens a **blocking** Yes/No dialog before anything is written
  (`MainWindow.xaml.cs:301-319`); answering No writes no blast, no workbook and no email fragments.
  `OutlierGuard`'s absolute Δ1d bar is 12bp, so a genuine surprise trips it on most rows of the
  deciding bank and the desk meets that dialog on exactly the days they are in the most hurry.
  Deliberate and well argued in the code — but worth saying out loud before rollout.
- The **stale-feed** popup is informational and fires *after* the artefacts are written, so a stale
  front rung is reported once the blast, workbook, shared-drive copy and email already exist.
- A run whose decision calendar has run out publishes nothing and disappears from every
  client-facing surface; the completeness `CHECK` tells the operator, and nothing tells the reader.
