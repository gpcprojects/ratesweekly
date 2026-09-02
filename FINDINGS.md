## v0.19.1 (2026-09-02) — the * convention, and a hold never re-bases

Desk dictations off the 02-Sep-26 board (RBNZ hiked overnight; BOC held):

1. **A HOLD never re-bases** ("they didn't do anything so it isn't rebased — it's just the same
   fixing", the CORRA case). The decision-day stub bridge now fires ONLY when the market says a
   move happened that the target print has not caught up with (|stub − fixing| ≥ 8bp — above
   corridor noise, under half of BOJ's 15bp); a genuine hold publishes the plain print, no star.
   Δ = 0 on any later day was already the print. Applies to every bank via the shared window
   logic; RBNZ's hike (stub 2.751 vs OCR 2.500 = 25bp) still bridges correctly on its decision
   day, because its target IS its fixing and reads Δ = 0 until the OCR re-prints.
2. **The wordy "(rebased, pre-statement)" label is retired everywhere.** The fixing renders as
   the NUMBER with a `*`, italic where markup allows, on: the sheet email (front table + run
   blocks), the card email, the plaintext, the chat blast `(FEDL01 4.330*)`, the xlsx Runs sheet
   (text cell, italic) — and ONE shared disclaimer line (`RunsTable.RebaseNote`, italic) sits
   snug under the OIS tables, before the inflation runs, on starred days only:
   `* = has been adjusted to reflect hike/cut prior to new fixing`. Column widths are back to
   their originals — the long label was what stretched them. The guard-synthesized-mid dagger
   (a different, standing convention) is untouched. Harness parsers + Invariants + the custom
   scenario checks all speak the new grammar.
3. **The RBNZ Δ columns were verified CORRECT against the feed's own numbers**: the family
   re-pointed overnight and yesterday's close of each CONTRACT (today's rung N = yesterday's
   N+1) shows the strip genuinely gave back 15–26bp — Δ1d −15.4 on the front = 2.756 − 2.910
   (old 2A close) to the tick. A dovish-hike repricing, not a mis-roll.

347/347 tests; scenarios 78/80 (14 + 47, the standing date fragility). Seed refreshed
(179,978 closes to 01-Sep-26, 40,592 fixings) per the release ritual.

## v0.19.0 (2026-09-02) — the history ships INSIDE the app (desk order)

Desk: "the app is STANDALONE. incorporate the history INTO the app. that way it can't be missed.
no copy paste of external file." `assets\history_seed.db` (a VACUUM'd copy of the desk store —
179,782 closes from 2019-07, 40,556 fixings, 4,151 rung records, 9.8 MB) is now an EMBEDDED
RESOURCE like the configs and templates. `SaveDown.EmbeddedSeed`:
- a machine with NO store is BORN from the seed (before anything opens the db, app + CLI);
- a SHALLOW store (<120d of closes) inherits everything it lacks from the seed, insert-only,
  provenance kept — then the share snapshot still tops up when one exists;
- a deep store answers one local query and skips. Startup logs the seed's manifest
  ("embedded desk history: as of ...") so a stale seed is visible.
The seed-to-today gap self-fills via StoreBackedHistory's ordinary gap-fill on the first run.
**RELEASE RITUAL ADDITION: refresh the seed at every release** — VACUUM INTO
assets\history_seed.db from the live store + regenerate assets\history_seed.txt, then build
(now in CLAUDE.md). Exe grows ~10 MB (186 MB total). FLAGGED to the desk: the release repo is
PUBLIC, so the seed puts bulk Bloomberg-derived history in a public download — flipping the
repo private is one command if compliance wants it.

## Fixed 2026-09-02 (v0.18.4) — the history carrier was broken: both machines saved down LOCALLY

Second terminal's popup: "fixing history is 0 day(s) deep and the share snapshot adds nothing."
Diagnosis on the desk machine: savedown.json said Mode local, Root Documents — the salix drive
scan has been failing (drive letters are per-logon-session), so BOTH machines quietly snapshotted
into their OWN Documents. Nothing ever travelled; the second terminal "inherited" from its own
thin copy. The desk store itself is intact (179,782 closes to 2019-07, 40,556 fixings/1,163 days,
4,151 maturity records — local snapshot verified identical).

1. **Root derivation fallback**: when the salix scan fails, the C+C home is derived from
   publish.json's own dailyDir (its parent IS "OIS and Inflation Runs"); local Documents only as
   the last resort, and then the app SAYS so loudly at startup ("nothing reaches the desk share").
2. **`StoreBackup.InheritAll`**: a SHALLOW store (closes reaching back <120d) inherits everything
   the share snapshot holds that it lacks — daily closes (insert-only, provenance kept), maturity
   records, fixings — before any run reads it. Wired into DAILY/WEEKLY clicks and the CLI. Deep
   stores skip on one local query. THE APP NOW LITERALLY COMES WITH THE DESK'S HISTORY.
3. **Snapshot rotation depth guard** (audit scenario 165/168, about to become live): a snapshot
   at least 10% thinner than the standing one never takes the latest slot — a thin machine can
   no longer rotate the desk's history off the share.
4. **ImportInflation** tries the previous generation too, and its notes NAME the root path it
   looked at, so "adds nothing" is diagnosable at a glance.

Interim unblock: the full snapshot staged at Desktop\RatesWeekly_history_for_other_terminal.db —
place on the second terminal at Documents\RatesWeekly Data Store\history_backup.db, update the
exe, run DAILY. Open desk question: the durable share root — ideally the salix UNC path
(\server\share\...) so per-session drive letters stop mattering.

## Fixed 2026-09-01 (v0.18.3, same branch) — history is kept forever, and the sheets grow with it

Desk rule made explicit: NO history ever rolls off. Verified layer by layer:

- **The store never prunes** — zero DELETE statements in HistoryStore; closes, fixings (the
  ingested 2021-2026 incumbent sheet included), maturity records and manual marks accumulate
  for the life of the desk. A rung recorded from today has six months of life in six months.
- **The rendered history tables were the gap**: publish.json `historyDays` (250 on the desk
  machine) windowed the save-down books' history_ pages, so in ~10 months the oldest rows
  would have rolled off the SHEETS while the store kept them. `historyDays: 0` now means
  EVERYTHING — the window resolves to the family's own earliest stored close — and the desk
  machine's publish.json is set to 0. Positive values remain a lean-book option.
- **Read windows widened so they can never become silent caps**: the inflation Base-print
  reads (2600d → 7300d ≈ 20y); the save-down refTicker read follows historyDays' 0-semantics.
- Inflation history pages already rendered the full fixings table (GetFixingHistory is
  uncapped) — no change needed there.

## Fixed 2026-09-01 (v0.18.2, same branch) — RPI Daily blank the morning after a bank holiday

Desk screenshot, 01-Sep-26: the UK RPI card's whole Daily column blank while Weekly/Monthly
populated. 31-Aug-26 was the UK summer bank holiday: `PrevBd` only knows weekends, so the Δ1d
anchor landed ON the holiday, and the 2026-08-25 convention ("EXACT saved date, blank when
missing") blanked every tenor — while −7d and −28d hit real Tuesdays. The inflation cards were
the last exact-match surface in the app; the OIS side has walked its anchors under per-horizon
staleness caps since 2026-08-26.

Fix (`InflHistory.BuildDisplayRows`): the three anchors now walk to the LAST SAVE AT OR BEFORE
the target under the same 5/7/10-day caps ("weekends and long holiday bridges, no more"). On
ordinary days the exact date still hits, so nothing else changes; across a holiday the anchor
is the last real trading day (28-Aug here); past the cap the cell stays blank rather than
stretching. One consequence to know: on a post-holiday morning the Δ1d column is a genuine
one-business-day change that spans several calendar days, so the FIXING lone-mover watch may
read slightly wider dispersion that day — informational only.

## Fixed 2026-09-01 later (v0.18.1, same branch) — the policy-delta base (desk dictation)

Desk (Gabriel), 2026-09-01: "use the most recent fixing + or - the amount they move by, we don't
want to use the stub mid for the fixing." Supersedes the 2026-08-11 stub-mid re-base as the
PRIMARY path; the stub survives only as the flagged fallback.

- `meetings.json` gains a `policyTicker` per run — the bank's own target, the documented source
  of the delivered move. All nine probed by NAME 2026-09-01 (BbgSmoke): FDTR, UKBRBASE,
  EUORDEPO (depo — the rate ESTR tracks), BOJDTR, SWRRATEI, RBATCTR, NZOCRS, CABROVER
  (overnight LENDING — moves 1:1 with target, so the delta is identical), NOBRDEP. Values
  cross-checked: RBATCTR 4.35 = RBACOR's flat level, NZOCRS 2.50, SWRRATEI 1.75 vs SWESTR ~1.71.
- Inside the announcement→effective(+fixingLagDays) window: Δ = target now − target's last
  pre-decision close; base = FIXING PRINT + Δ; dagger on. No OIS basis, no intra-period
  expectations; surprises included because the target itself re-prints at the statement.
- THE RESET (desk: "it has to reset once the new rate DOES genuinely kick in"): the moment the
  fixing print has moved ≥ half the delta in the move's direction since the decision, the base
  is the print alone, dagger off — whenever that happens (the RBNZ OCR is its own fixing and
  re-prints at the effective date, inside the still-open window: adding Δ to a moved print
  would double-count the cut, scenario 80's exact test). The calendar windowEnd stays the hard
  stop regardless.
- Δ = 0 ON the decision day falls through to the stub bridge (the target print can lag the
  statement; RBNZ's decision day reads Δ = 0 by construction since target == fixing) — the one
  place the decided period's OIS still serves, flagged, exactly as before. Δ = 0 on any later
  day is a hold: base = the print, no re-base.
- Missing target data (the entire pre-2026-09-01 suite seeds none) → the stub path verbatim,
  so every existing scenario's behaviour is unchanged by construction.
- Policy tickers ride the snapshot universe (deliberately NOT the 16:15 snap set), and the
  SNAP partial-pin note now fires only for a ticker that HAS bars but not today — fixings,
  futures and policy targets, which never bar, stay out of it.
- Scenarios 66-80 (Group19): all nine banks mid-window (hike/cut/15bp/50bp mixed), the reset
  per lag shape, and the kick-in — all business-day anchored. 340/340 units, 78/80 scenarios
  (14 + 47 = the pre-existing date fragility, identical to baseline).

## Fixed 2026-09-01 (v0.18.0, branch audit-fixes-0.18.0) — the audit batch + the blank-Δ terminal

Fixes from the 2026-08-31 audit (`tests\RateDesk.Scenarios\CATALOGUE_101_200.md`), plus the desk's
2026-09-01 report that a second terminal's inflation Δ1d/1w/1m never populated. NOT RELEASED —
v0.17.0 kept at `C:\Users\GPC Work\RatesWeekly\backup\`. Scenario 65 added (the fourth quadrant).

1. **Second-terminal blank Δ columns (the desk report).** Root cause: the fixings mapping is
   maturity-documented by design and records begin on a machine's own first run, so 45 days of
   seeded closes can never map — the fixings table opens one day deep and the exact-date anchors
   (−1bd/−7d/−28d) find nothing for a month. Fix: `StoreBackup.ImportInflation` — a store whose
   fixing depth is under 8 days inherits the `fixings` rows, index prints and SWIF maturity
   records from the share snapshot (temp-copy read; standing merge rules; a locally-saved cell is
   never rewritten except validated-xls-over-bbg). Wired into both builders before `Maintain`;
   when no snapshot exists the run now SAYS the history is thin instead of publishing silent
   blanks (`INFL:` note).
2. **The weekly email's decision gate now rides its own marks (catalogue 102, the headline).**
   `EmailBuilder` pinned marks to 16:15 but discarded the mode and left `MarksAsOfLondon` null, so
   a WEEKLY RUN pressed after a statement rolled the board past a decision every price predates —
   the exact 2026-08-27 daily fix, missing from the second builder. Also: one `LondonNow()` per
   run, passed into `SnapDiscipline.Apply` (a run straddling midnight could date the gate a day
   after its prices — catalogue 101), and `LateAnnouncementNotes` now reaches the weekly.
3. **The public pages gate on the marks' clock (106).** `RollingStrip.ForMeetings` callers
   (CurrencyPage, MoverScan) pass `nowLondon = asOf + 16:15`, so a dashboard rendered after a
   statement no longer drops a meeting the blast keeps.
4. **The rung map is armed everywhere it can be (finding 2).** `PricingService.RecordedEffective`
   is now the ONE cached, source-aware record lookup; the weekly Δ1d-fallback map, the dashboard
   strips and the movers scan all pass it (records key the composite spelling). `FallbackIngest`
   stays deliberately unarmed — its map must match the save-down reader's, which FINDINGS keeps
   unarmed pending the desk's data-collection call; a comment now says arm BOTH or NEITHER.
5. **Roll-correction evidence arm (120 — the untested quadrant).** A feed that re-points BEFORE
   the statement now triggers the CoD correction on record-vs-live SW_EFF_DT evidence, standing
   down when the previous business day is a boundary/mixed-state day. Scenario 65 locks it.
6. **The CHECK gate protects the dashboards (144).** `Update_Click` gates BEFORE `RenderAll` —
   declining now withholds the public pages and the pack, not just the email fragments. The modal
   defaults to **No** (199). An email BUILD failure still renders pages, as before.
7. **Inflation coherence notes are non-blocking, as their own spec says (146).** Prefix moved
   `CHECK:` → `FIXING:`; they surface in the log and the informational popup (now also carrying
   `SNAP:`/`INFL:` lines) and can no longer cancel the daily OIS product over one RPI tenor.
8. **The arbitration reference is independent of its own picks (152).** The strip median is built
   close-against-previous-day's-close (consecutive days only); with no reference the close is the
   default (the old med=0.0 fallback favoured whichever candidate moved less); a tenor whose last
   mark is not from the preceding day defaults to its close instead of judging a multi-day change
   against a one-day median.
9. **A close inside a record gap that a re-point crossed is skipped (151/154).**
   `HistoryStore.MaturityBrackets`: a day maps only when the records bracketing it agree (the
   record day itself always maps). No more year-wrong fixings keyed off a stale record.
10. **MPC carries `fixingLagDays: 1` (108/141).** The code has documented "FOMC and MPC carry 1
    in config" since 2026-08-27; the MPC entry never did — the morning after every MPC move, GBP
    Priced was measured against the pre-decision SONIA with no re-base and no dagger.
11. **Partial snap pins are said out loud (104)** — a `SNAP:` note when some published tickers
    had no 16:15 bar — and **a machine whose date ≠ London's date is flagged at startup**.

Deliberately NOT changed (desk calls, per the catalogue): the BOC 14:45 source-time question, the
Δ1w anchor-slack convention (111), a guard on the re-base's live mid (109), arming the save-down
history maps (needs the 16:15 rung-probe data change), and everything marked
"needs-a-desk-call" in the catalogue.


---

## Open, recorded 2026-08-28 — inflation fixing change columns are noisy at the tenor level

Not a bug in the app's arithmetic; a property of the fixing feed, quantified over three
months of Bloomberg closes (28-May → 28-Aug-2026, ~68 day-pairs per family).

**What the desk sees.** RPI change columns read like `+0 +22 +8 +0 +10 +14 +0` across the
strip on a day the curve moved smoothly. The total move is right; its distribution across
tenors is not.

**Two distinct causes, both in the feed:**

1. TENORS THAT PRINT BUT DO NOT TICK. A tenor quotes the identical number two days running
   while its neighbours move, so its change reads 0.00 and the move it should have shown
   turns up in whichever neighbour requoted.
   · RPI: 22 of 68 days have ≥1 such tenor (32%), 7 days have ≥2 (10%), one day has 4.
   · HICP: 17 of 69 days (25%), 2 with ≥2.  · CPI: 6 of 68 days (9%), never ≥2.
   · 27-Aug-26 is the WORST day in the window — the only 4-flat day, and the one the desk
     queried: Aug-26, Oct-26, Jan-27 and Apr-27 all flat while the rest moved.

2. LONE-MOVER QUOTES. One tenor moves double digits while the strip does not move at all.
   BPSWIF3 (Mar-27) printed 432.125 → 415.000 → 433.750 across 26/27/28-Aug: a 17bp drop
   and 19bp recovery on a flat strip. The app's own live mark that day implied ~434.6,
   consistent with both neighbours, so the CLOSE was the outlier.

**Despiking does NOT fix this** (tested): Hampel 5/k=6 over three months flags 2 RPI points,
0 CPI, 13 HICP — and misses the 415.000 entirely, because BPSWIF3's own daily volatility is
10-18bp so a 17bp print is not an outlier by its own history.

**The separation that DOES work — cross-sectional coherence.** Judge a tenor's move against
the median move of the months either side of it (its curve neighbours), same day, no lag:

  | | strip move | worst single-tenor residual |
  |---|---|---|
  | real repricings (5 days, incl. the 25-Aug Ofgem cap reset) | 12-20bp | **2.0 - 4.8bp** |
  | bad quotes and rolls (8 tenor-days) | ~0 | **18 - 105bp** |

  Median residual 1.0bp, p90 4.3bp. There is a clean empty gap between ~5bp and ~18bp, so
  any threshold in 8-15bp separates them; at 12bp it fires on 8 tenor-days in three months.
  25-Aug (Ofgem, every month -12bp together, worst residual -4.75bp) passes untouched.

**Why this shape and not a smoother.** The desk needs BOTH: Bloomberg caught the Ofgem cap
reset on 25-Aug and the external pricer's curve did not, so smoothing destroys the signal
the feed is there to provide. A coherence test keeps every coherent move in full, same day,
and gates only the single cell where a tenor provably moved alone. It is also the principle
the OIS side already uses (the neighbour guard, the futures guard) — it simply never reached
the inflation path.

**Proposed, NOT built — needs desk sign-off before it changes a published number:**
  · lone-mover test: residual >12bp ⇒ withhold that tenor's change cell and name it;
  · non-ticker test: tenor exactly unchanged while ≥4 others moved ⇒ note at ≥2 such tenors
    (would have fired 7 times in 3 months, including 27-Aug);
  · the LEVEL is never touched by either — the raw mark always publishes, so an Ofgem-style
    reset still lands in Mid the same day.

**Caveats to settle first:** three months is a thin calibration sample (5 real events); and
a genuine SINGLE-MONTH event (an energy or seasonal effect hitting one fixing) would look
like a lone mover — confirm that is not a real risk for UK RPI before switching it on.

Evidence scripts: scratchpad rpi_vs_bbg2.py, sawtooth.py, signal_vs_noise.py (2026-08-28).

---

## Open, recorded 2026-08-28 — save-down history carries the previous close across a roll

`DailyBook.BankHistoryRows.ValueAt` walks back off a boundary or mixed-state day to the last
usable close — correct for a change ANCHOR — but the caller stamps that walked-back value with
the day it ASKED about. So every day the walk skips publishes the previous close under its own
date in the `Historical_*` tables.

Seen live: `Historical_SEK` on the 26-Aug-26 Riksbank roll carries 1.7237 (25-Aug's rung-2
close) where the family had moved to rung 1 at 1.7150 — about 0.9bp, self-correcting the next
day. `Historical_EU` froze at 2.7940 across 23-28 Jul, the whole announcement→start window of
the 23-Jul ECB. It reads back as "the market did not move".

**ATTEMPTED AND REVERTED, deliberately.** Feeding the store's maturity record into this walk's
MeetingRungMap so recorded days are not stepped over. It fixes the Riksbank case, and it is the
same evidence-before-inference move that fixed the boards on 2026-08-27 — but it is wrong here,
and scenario 57 caught it on twelve rows. A maturity record is stamped WHEN THE RUN LOOKED; a
boundary day's feed re-points intraday, so the record does not establish which contract that
day's CLOSE belonged to. Scenario 57 derives the same rule independently of the app ("a day is
unattributable when it IS the announcement or falls between the announcement and the period
start") and is right to. The narrower variant — lift for boundaries, keep the precaution for
mixed-state — fails for the same reason.

What would actually settle it is a mark timed like the close (a 16:15 rung probe recorded
alongside the price), not a run-time record. That is a data-collection change, not a logic one.
Needs desk sign-off; the code carries a comment at the site so it is not re-attempted blind.

## Recorded 2026-08-28 — two scenarios are weekday-fragile

26 ("Weekend walk-back: Friday decision, run after the weekend") and 47 fail when the suite runs
on a FRIDAY and pass otherwise: `Cal.D(n)`/`Cal.Bd(n)` are offsets from `DateTime.Today`, so
scenario 26 cannot construct "a Friday decision with the weekend after it" from a Friday — its
own premise becomes unsatisfiable and the walk-back it exists to test does not occur. 64/64 on
Thursday 27-Aug, 62/64 on Friday 28-Aug, with no change between that the harness can even reach
(it drives BuildWeekly → guards → renderers; it never touches the save-down or the template).
Not a product fault, but the suite should pin its own weekday rather than read the wall clock.

---

## Fixed 2026-08-31 — neither the close nor the 16:15 snap is reliable alone

Two lone-mover rows, a working day apart, in opposite directions:

  · Mar-27 (BPSWIF3) 27-Aug — the CLOSE was 415.000 while the tenor traded 429-435 all day.
    A bad tick printed twice and the second landed on the last bar, so it became the close.
    The 16:15 snap was 435.500. Published +0.99 index pts against neighbours at +0.15/+0.26.
  · Nov-26 (BPSWIF11) 28-Aug — the SNAP was 434.250 while the close was 439.375. Neither is
    wrong: the last trade before 16:15 really was 434.250, the tenor jumped AT 16:15 and held
    439.375 for five hours. Thin instrument, two honest marks, 5bp apart. Published +0.21
    against a strip that moved 0.00 to -0.04.

Switching wholesale from closes to snaps fixed the first and caused the second. The snap rule
itself is right (last bar ENDING at or before 16:15 = the last traded price as of the snap);
the problem is that a monthly fixing is quoted on its own and trades thinly, so ANY single
observation can be junk while the rest of the curve is fine.

**Fix (InflHistory.Maintain): keep both candidate marks and let the strip arbitrate.** For each
day take the median day-over-day change across the twelve tenors, measured on closes so the
reference never depends on what is being chosen, then give each tenor whichever of {close, snap}
lands closer to it. Same discriminator as the OIS neighbour and futures guards, and the one
measured to work here: a real repricing carries the whole curve (25-Aug Ofgem cap reset, worst
single-tenor disagreement 4.8bp) while a bad mark moves one month alone (18-105bp).

Verified on both cases with one rule: Mar-27 27-Aug takes the SNAP (435.500), Nov-26 28-Aug
takes the CLOSE (439.375). Nothing invented, nothing discarded - both are real prints of that
tenor on that day; the strip only decides which to believe.

## Recorded 2026-08-31 — the scenario suite is weekday-fragile, widened

10, 11, 12, 13, 15, 21 join 26 and 47 on the CI skip list. They say "hiked YESTERDAY" or "cut N
days ago"; on a MONDAY yesterday is a Sunday, so the decision lands on a non-business day and
every change anchor walks past it (scenario 10: Δ1d reads +11.0 against an expected +3.0).
26 and 47 fail the mirror-image way on a Friday.

Verified not a regression: reverting the day's product change and re-running 10 reproduces the
failure identically, and the harness never references InflHistory at all.

Eight of sixty-four is real coverage lost on any given day. The fix is to give the harness its
own pinned weekday, or to express these offsets in BUSINESS days (Cal.Bd already exists), rather
than reading DateTime.Today. Worth doing before the suite is trusted as a release gate.
