# Handoff — 2026-08-27

## v0.16.0 — the central-bank decision-day fixes (READ THIS FIRST)

A 62-scenario harness (`tests\RateDesk.Scenarios`, its own README) drove the shipping code end to
end on hike/cut/hold days and found 12 issues. All are fixed and all 62 scenarios are green, along
with 314/314 unit tests. `tests\RateDesk.Scenarios\FINDINGS.md` is the audit; `REMEDIATION.md` is
the plan and what was delivered. **Not yet done: the two live-terminal audits
(`tools/audit_email_dates.py`, `tools/verify_strip_changes.py`) must be clean before release, and
one live decision day should be watched with this build in place.**

Four desk decisions shaped the fixes, and they are now product rules:

1. **NEVER INVENT A MID.** The neighbour-misprint guard used to publish the neighbour midpoint in
   place of an impossible print. It now WITHHOLDS: the row keeps the real print internally (blend
   inputs, guards) and publishes the label `n/a` with no numbers, on every surface, exactly as a
   Y/E turn row does — and the step chain steps over it, so the next row carries the cumulative
   move. `MeetingRow.Rejected` / `Masked` / `MaskLabel`; `MaskLabels` holds the two labels, so the
   surfaces no longer carry three copies of the turn literal.
2. **THE DECISION GATES READ THE MARKS' CLOCK, NOT THE WALL CLOCK.**
   `PricingService.MarksAsOfLondon` — set by `DailyBuilder` from `SnapDiscipline` once the 16:15
   snap owns the marks. The FOMC announces at 19:00, after the close: a run pressed at 19:30 now
   keeps the pre-decision board its prices belong to, and the run says so.
3. **THE RE-BASE FOLLOWS THE FIXING, NOT THE PERIOD START.** The window runs through the period
   start plus `fixingLagDays` business days (new per-run config knob, default 1), because every
   o/n index publishes a day in arrears. That is what finally gives FOMC and MPC a re-base at all
   — their period starts on the decision date, so the old `today < start` window was empty.
   The re-base also WALKS FORWARD now: it finds the decided period's own mark from a day on which
   Bloomberg's record proves that rung was that contract, instead of reading a close from before
   the statement (which cannot contain a surprise). When only the pre-statement close is
   reachable, `RefRebasedStale` makes every surface say "(rebased, pre-statement)".
4. **A FUTURES-GUARD BREACH BLOCKS.** The note now carries the `CHECK:` prefix, so it reaches the
   pre-publish gate. `TriggerPrefix` stays inside the text for the audit scripts.

### The renumbering is now read off the prices too (2026-08-27, second pass)

`Series/RungShiftScan.cs`. There are three ways to know which contract a rolling ticker meant on a
past day, and the boards use them strictly in order:

1. **The ticker's own recorded SW_EFF_DT** (`HistoryStore.EffectiveOn`). Exact. Only exists from
   26-Aug-26, because a reference field can only be written down as it is seen — it cannot be
   fetched retrospectively, which is why it is stamped on every run.
2. **The strip's own price history** — new. A renumbering is not a price move, it is the whole
   strip stepping along itself, so it is legible in the prices: today's rung N holding what
   yesterday's rung N+1 held, every rung at once. Prices ARE backfilled (45 days on a machine's
   first run), so unlike (1) this reaches the whole window immediately.
3. **The meeting calendar**, as before.

Why (2) had to exist: an UNSCHEDULED decision inserts a meeting into the calendar after the fact,
and the calendar then numbers every past day as though the market had always known about it. The
insertion has its own unmistakable signature — the strip shifting the OTHER way — and nothing else
can see it. Scenario 63 proves the emergency case comes out right on a store with prices and no
maturity records at all; 64 is its control, the same market with no emergency, which must stay
untouched and unremarked.

**It abstains rather than guesses.** Confirmed needs one hypothesis fitting within 1.5bp per rung,
the runner-up at least 4bp worse, and three rungs agreeing at once — and only rungs where the strip
is sloped enough (3bp) to tell the hypotheses apart are counted. A flat strip abstains, which costs
nothing, because a flat strip gives the same answer under every hypothesis. One unjudgeable day
breaks the chain and the whole correction stands down to the calendar. Eight unit tests in
`RungShiftScanTests` pin the abstentions, including a 30bp parallel curve move that must NOT read
as a roll.

**Known limit:** the scan checks a rung against the one above it, so it needs the family to quote
DEEPER than the run publishes. Every shipped family does (8-13 rungs quoted, 3-6 published), but a
family that ever quoted only as deep as it publishes would abstain on its last row.

**Not yet seen against a real Bloomberg strip.** The thresholds are calibrated on synthetic data.
The failure direction is safe — too strict means it never fires and the calendar governs exactly as
before — but a live decision day should be watched before this is trusted to correct anything.

The structural change worth knowing: **`MeetingRungMap` prefers Bloomberg's own per-day record of
what each rung pointed at** (`IHistoryProvider.EffectiveOn`, served by `StoreBackedHistory` from
the store's maturity table) over the derived boundary count. Evidence beats inference. It closed
both the inter-meeting-decision fault (a calendar that gains a meeting re-numbers history that was
recorded under the old numbering) and the BOJ unstable-lag fault, and it turned the mixed-state
exclusion into a fallback rather than a blanket refusal — attributable days are used now, not
discarded. The store only holds those records from when it started recording them, so the boundary
count still governs older history; that is the residual exposure and it shrinks every day.

Also in v0.16.0: the dashboard strip's published LEVEL goes through `RolledValue` like its own 1w/1m
levels (it was the one read in the class that skipped the boundary-day step-back, so every strip
row was the neighbouring contract whenever the render's as-of was a renumber day); the Δ1d CoD
fallback no longer re-admits a mixed-state close the stitcher just excluded; a run that publishes
rows with no o/n fixing says so; `CalendarHealth` now covers the empty-calendar case and runs on
the daily cadence too; and the 62 scenarios are wired into `azure-pipelines.yml`.

---

# Handoff — 2026-08-20

Read `CLAUDE.md` (team conventions) and `DESIGN.md` (full spec + decision log) with this.
Chat transcripts do NOT travel between machines — what matters is written down here or there.

## Where things stand

- **v0.10.3 (2026-08-25 evening polish, one PR on top of v0.10.0)**: (1) save-down templates
  now DERIVED FROM THE INCUMBENT WORKBOOKS — entry pages exact (BDP/BDH array formulas,
  Vandit/Table/Copy/US-CPI pages, formatting, their own store macros & buttons all travel
  verbatim; regenerate via the v3 builder script if the incumbents change); ModApp module adds
  a Runs display page + StoreAllRefresh; inflation's client-BCC email module stripped; app fill
  = history only, ALL columns (OIS Step/PricedIn/Percent vs each day's ref fixing — schedule
  refTicker else the ccy's OIS OnFixingTicker; inflation +%mom) — the Current/Copy pages stay
  formula-live and manually overridable. (2) INFLATION CHANGE CONVENTION: the inflation sheet
  anchors at EXACT dates −1bd/−7d/−28d (its own Table helpers; NOT same-day-last-month — that
  is the OIS convention) — replicated 1:1 after the app's monthly diverged badly; front row
  then matched the pricer to the tick. (3) RIKSBANK extended past the Y/E turn: period starts
  corrected to Bloomberg's swap table (10-Feb / 31-MAR / 12-MAY / 30-Jun-27 — config had used
  decision dates 24-Mar/05-May as starts); SKSF5A/6A probed = price-only, so a narrowly-scoped
  `trustConfigDates` flag (RIKSBANK only) lets desk-confirmed config dates publish rows that
  carry REAL prices; turn logic untouched. (4) Email subject "DRAX Swaps Closing Runs"; lean
  attachments renamed "DRAX OIS Runs 25Aug26.xlsx" / "DRAX Fixing Runs 25Aug26.xlsx" (xlsm
  history books keep the underscore names — date parsing + never emailed); JBDH banner
  (assets\jbdh_banner.jpg, embedded, hidden CID attachment) tops the daily email; COPY BLAST
  is CF_HTML — a single-table replica of the attachment xls MINUS Maturity; futures guard
  tightened to 2.5bp; RECIPIENTS dialog persists to recipients.json. 256/256.
  PARKED FOR NEXT SESSION: compounded-rate feature (compound the fixing) across the suite.
- **v0.10.0 (SHIPPED 2026-08-25 — the whole day's batch in one release)**: everything below
  (inflation integration, snap discipline, save-down system, outlier guard, store-first
  history, unified fixings history) PLUS: (a) BBG PRINT-HOLE ADOPTION — a validated sheet
  row whose base month is old enough that its print must exist but Bloomberg has none gets its
  base adopted as an 'xls' print (insert-only, a real print supersedes); live: exactly 1 hole,
  the shutdown-skipped Oct-25 CPURNSA (Oct-26 fixing now shows Base 325.60/YoY as the incumbent
  did); forecast bases (inside the ~45d publication lag) are never adopted. (b) RECIPIENTS
  button (daily row) — paste-editable list, %APPDATA%\recipients.json, PRELOADED with the
  incumbent VBA's client list, applied to the daily draft as BCC, ALWAYS BCC, never To/Cc.
  256/256 tests.
- **INFLATION RUNS INTEGRATED (desk 2026-08-25 dictation, same batch)**: the
  "Inflation Fixing Runs" section (three cards CPI·CPURNSA / RPI·UKRPI / HICP·CPTFEMU, columns
  Month|Base|Mid|YoY%|MoM%|Δ1d/Δ1w/Δ1m index changes, furthest fixing dropped, "Next Print:"
  from ECO_RELEASE_DT — omitted when absent, never guessed) appended to BOTH emails: below the
  meeting tables on the daily, below the fwd grid on the weekly (section order Front→Runs→Grid
  makes plain append correct). One derivation for every surface: InflHistory.BuildDisplayRows
  (marks + prints + unified history; MoM chains mid-over-prev-mid, front row anchors the last
  published print). Fragments frozen at run time (out\daily_infl.html/.txt, weekly_infl.*),
  appended at click time under NEW tickboxes: Daily "Inflation Fixing Runs (in email)" +
  "Inflation Sheet (XLS attachment)", Weekly "Inflation Fixing Runs (in email)" — all default
  ON. ATTACHMENTS ARE LEAN (desk: "no history"): OIS_Runs_*.xlsx is Runs-sheet-only again (the
  incumbent's own shape — the in-xlsx history sheets eran a v0.7 addition, now retired) plus
  NEW Inflation_Runs_*.xlsx (InflRunsXlsx, same writer fills the save-down book's Runs page).
  Save-down folders RENAMED to "OIS Run History" / "Inflation Fixing Run History" (desk names).
  Save-down VBA gained RebuildRunsPage: any manual Store also regenerates the Runs display page
  from the Current/Copy tables so it stays copy-able into a separate xls (COM-tested: manual
  9.999 override → history append + Δ recompute + Runs page carries it). CLI savedown also
  regenerates the lean infl xlsx + daily fragment offline. NOTE the Oct-25 CPURNSA HOLE: BLS
  never published Oct-2025 CPI (shutdown) — Bloomberg has no print, so the Oct-26 fixing shows
  Mid only, Base/YoY honestly blank (the incumbent sheet's 325.60 there is the pricer's own
  estimate — it matched no print in validation). EmailBuilder.Build/Render take the store;
  weekly snapshots now include the 36 SWIF tickers. 255/255 tests.
- **SNAP DISCIPLINE (desk 2026-08-25, in the same uncommitted batch)**: the official snap moved
  16:30 → 16:15 LONDON with a press-time tolerance band. Before 15:30 = PRE-CLOSE (run works,
  live mids, CHECK note + popup); 15:30–16:14:59 = live mid saves AS the close; from 16:15 the
  published marks are PINNED to the 16:15 snap (SnapDiscipline.Apply overwrites the snapshot
  mids for meeting tickers + WeeklyExtraTickers + SWIF fixings from intraday bars; barless
  tickers stay live, counted). EXISTING HISTORY KEPT: PricingService.SnapTimeCutover
  (2026-08-25) — snap days ≤ cutover still read 16:30 bars, days after read 16:15; the old-time
  pull self-retires once the 50d window passes the cutover. GOTCHA: GetLondonSnaps bar size now
  follows the snap time (a :15 snap needs 15-MIN bars — with the old hardcoded 30-min bars a
  16:15 request silently returned the 16:00 bar). The weekly forward grid deliberately stays
  live-at-press (not a close product). 253/253 tests.
- **UNCOMMITTED BATCH (2026-08-25, deliberately held locally — desk asked to stop per-iteration
  uploads; commit+push as ONE final version when the inflation email integration is dictated)**:
  (1) OUTLIER GUARD — cross-sectional CHECK flags on every run's Δ1d/1w/1m (|x−median| >
  max(4bp, 4×MAD), ≥4 rows; OutlierGuard.cs in Core), wired into daily+weekly builds, popup in
  the app after runs; notes never reach the email body. Born from the live BOJ case (Δ1m +4.9
  in a strip of +11 — traced to a REAL Oct→Dec hike-odds migration, steps mirror-imaged; the
  guard exists because nobody can tell real-vs-bad-print from the email). (2) MACRO-ENABLED
  SAVE-DOWN: templates\*.xlsm (clean-room VBA replicating the incumbent workbooks' store
  machinery — learned from olevba dumps of Central Bank OIS MAIN + MOST RECENT Inflation Fixing
  Runs; regenerate via the template builder script if store semantics change; AccessVBOM needed
  temporarily) embedded in Weekly.Core; SaveDown\StoreBooks fills them (ClosedXML PRESERVES
  vbaProject + buttons — tested); daily Render writes OIS_Runs_*.xlsm + Inflation_Runs_*.xlsm
  locally and mirrors (catch-up) into "OIS Runs"/"Inflation Runs" folders. Macros LIVE-TESTED
  via Excel COM: StoreBank appends today with recomputed Δ vs identical (Start,End) at
  yesterday/−7d/same-day-last-month; manual overrides honoured; Copy_CPI inserts at top.
  (3) STARTUP FLOW: every open searches network drives named "salix" for Coverage &
  Counterparties (both spellings, prefers its "OIS and Inflation Runs" subfolder) → "C+C folder
  located successfully" status; else dialog Locate C+C (folder picker) / Save Locally
  (Documents + OK box); savedown.json; detection re-runs every open so a local fallback
  upgrades back to C+C automatically. (4) INGEST-BACK: next daily run re-ingests the NEWEST
  saved book per folder, RESTRICTED to rows dated on/after the file's own date (desk-stored
  macro rows only — app-written roll-corrected history rows must never re-enter raw ticker
  history) + onlyMissingOrChanged for inflation (unchanged rows keep bbg provenance).
  (5) STORE-FIRST HISTORY (API-load, desk ask): StoreBackedHistory serves all lookbacks from
  the store; Bloomberg touched only for live snapshot, 16:30 intraday snaps, and per-ticker
  gap-fills (gap+5d overlap, upserted, one attempt/ticker/run). The old path re-pulled ~220d ×
  whole universe per email build (Prefetch) — now a no-op; EmailBuilder.Build takes the store.
  (6) CLI savedown verb. FallbackIngest maps Historical_<BANK> names too. 251/251 tests.
  DISCOVERED IN THE INCUMBENT VBA (for the email-integration decision): CreateClosingRunsEmail
  builds the daily CLIENT email — BCC lists (~19 external addresses) hardcoded in the xlsm VBA,
  attaching OIS_Runs + Inflation_Runs xlsx; plus a second product USCPI_Lookback_<date>.xlsx
  ("US - CPI Vitor Request" tab: YoY/MoM lookbacks Daily/Weekly/Monthly/Quarterly/Semi) with
  its own client BCC list. Templates dir MUST be committed (csproj embeds from it).
- **master past v0.9.2** (unreleased): UNIFIED INFLATION-FIXINGS HISTORY (desk 2026-08-25) — store `fixings` table keyed by fixing identity (family + reference month yyyy-MM; value = native quote: CPI index level, RPI/HICP yoy bp). Merge rule: VALIDATED sheet rows ('xls') always win; 'bbg' fills gaps and never overwrites xls. Ingest (`Infl\InflHistory.Ingest`, CLI `inflingest`, publish.json "inflBook") gates every sheet row through BASE-PRINT VALIDATION (Base must equal the fixing month's year-ago published print; label-shifted rows re-keyed by what their Base proves — the pricer's export bug; placeholders/dupes/inconsistent/unresolvable dropped; HICP old/new basis both accepted, rebase 1.281085). Bloomberg closes map to identity via each ticker's RECORDED MATURITY minus derived lag (ticker's own field; undocumented days skipped, never guessed) — `Maintain` runs at the end of every weekly UPDATE and daily run (daily also snapshots+tops up the 36 SWIF series, meeting-closes-style). One-off validated bbg backfill CSV seeded (%APPDATA%\RatesWeekly\infl_bbg_backfill.csv, from the Aug-25 comparison analysis). Export: `Infl\InflBook` → out\Inflation_Fixings_History.xlsx (Hist_CPI/RPI/HICP, Date|Fixing|Value|Δ1d|Δ1w|Δ1m|Source, full depth), CLI `inflexport`. Live store: 40,384 rows, 2021-11..2026-08. EMAIL + OIS-workbook integration deliberately NOT built — the desk dictates that next. 243/243 tests. v0.9.2: v0.9.2 (desk 2026-08-25): daily format polish — Runs sheet column order StartDate/Maturity/Mid/Step/Priced/Δ1d/Δ1w/Δ1m ("T"→"Mid" everywhere), sheet title "DRAX OIS Runs 25Aug26" (filename stays the incumbent OIS_Runs_25August26.xlsx pattern — Y: consumers + SyncDailyDir glob depend on it), blast = the same table minus Maturity (IB window widths; gains Δ1m, drops End; fixed-width so a chat paste reads as a table), daily email subject "DRAX Swaps Closing OIS Runs - 25 Aug 2026", and fixed a v0.8.1 miss: the HTML front table still said "Base Rate" (plaintext had been renamed) — now "Fixing". v0.9.1: OFFLINE EXPORT + DRIVE CATCH-UP (desk 2026-08-25, the unreachable-Y: question) — the app itself is the unified information store: `DailyBuilder.SyncDailyDir` mirrors EVERY local OIS_Runs_*.xlsx that is missing/older on the shared drive (a week-long outage catches up in one pass; unreachable = honest soft-fail, nothing lost), and `DailyBuilder.ExportBook` (EXPORT XLS button — live from open by design — + CLI `export`) rebuilds the workbook from the stored daily_report.json + history.db with NO Bloomberg and NO drive; the workbook carries its own as-of so staleness is visible. v0.9.0: EMAIL SETTINGS PANE — tickbox matrices (daily: front/runs/xls-attach; weekly: front/runs/grid/dashboards-attach) on the front page, always clickable, persisted to %APPDATA%emailsettings.json; applied at CREATE/COPY/DAILY EMAIL click time by re-composing from the persisted report JSON (runs pull+store everything regardless). v0.8.1: Base Rate→Fixing, ref→fixing. v0.8.0: INTEGRATED HISTORY + FAILSAFE — store = single truth with provenance; workbook regenerated full-depth (historyDays) with Source col; outage days manually stored in the incumbent xlsm are ingested back (FallbackIngest, insert-only, bbg wins). publish.json: dailyDir, fallbackBook, historyDays. Earlier: v0.7.6 hard-data rule. v0.7.6: HARD-DATA RULE (pricing AND dates — see CLAUDE.md/DESIGN §12): runs end where Bloomberg documentation ends; v0.7.5 curve anchors SCRAPPED same day, its release deleted. v0.7.0-0.7.4: the DAILY OIS RUN (see DESIGN §13). v0.7.0 (2026-08-20): THE DAILY OIS RUN — DAILY RUN/COPY BLAST/DAILY EMAIL in the app + CLI `daily`: chat blast (improved, bp, auto Y/E Turn), OIS_Runs xlsx (runs + 60d roll-corrected history per bank, incumbent name pattern, optional dailyDir copy), daily email with workbook attached. DESIGN §13. Earlier: v0.6.4 step-skip; v0.6.3 Y/E Turn. v0.6.3 (2026-08-20): Y/E TURN labelling — year-end-spanning meeting periods on marked runs (SEK/SWESTR) render "Y/E Turn" instead of numbers everywhere, guard stands down, movers/charts skip; v0.6.4: the step chain SKIPS turn rows — the next row shows the cumulative move across the masked meeting + its own (DESIGN has the mechanics). v0.6.2 (2026-08-20): 1m = same-day-last-month everywhere (was fixed 31d; matches the sheet's intended convention), ECB futures guard on ICE 3M ESTR (TKY, index-matched, 0.0bp live; basis knob available for ER/TKYER if wanted). v0.6.1 (2026-08-20): STALE-OUTPUT GATE in the app — on open
  only UPDATE is clickable; CREATE/COPY EMAIL and OPEN OUTPUT unlock when THIS session's update
  rebuilt what they serve ("UPDATE COMPLETE" in the log; a failed email leg keeps the email
  buttons locked). Earlier same day: `master` through PR 331; branch `decision-day-roll` carries
  v0.5.0 + v0.6.0 (2026-08-20, the live Riksbank decision day). v0.5.0: TIME-GATED FRONT ROLL —
  decisionTimeLondon gates the decision-day roll (DecisionClock; the just-decided period leaves
  the boards/strips at the announcement, feed re-point or not), the Priced re-base moves to the
  same clock (was next day), and the meeting cards carry a **1d Chg** column off the stitched
  series. v0.6.0: FUTURES GUARD — FOMC/RBA/MPC/BOC meeting rows cross-checked against FF/IB/
  SFI/COR futures on every email build ("FUTURES GUARD TRIGGERED" note = the flag), and the
  decision calendars topped up through mid-2027 for every run (Riksbank from Bloomberg ECO,
  FOMC/MPC/SNB from the official calendars). DESIGN §12 has the full mechanics and is the
  dodgeball cherry-pick bundle.
- The desk email + all dashboards are BUILT AND VERIFIED from this repo. dodgeball's standalone
  DodgeballWeekly.exe was REMOVED (2026-08-20, dodgeball PR 359) and local copies deleted —
  this repo is the only weekly app.
- Source lives on Azure DevOps (`origin`, JBDHServices/DraxSwaps → `ratesweekly`), mirrored to
  GitHub `gpcprojects/ratesweekly` (public — that repo also carries the desk RELEASES:
  assets named `RatesWeekly.exe` / `RatesWeekly_v*.exe` ONLY).

## Fresh machine (~10 min, no admin needed)

1. .NET 8 **SDK** (not just runtime): `winget install Microsoft.DotNet.SDK.8`, or per-user
   `dotnet-install.ps1 -Channel 8.0` → `%USERPROFILE%\.dotnet` (then put it on PATH).
2. NuGet source if `dotnet nuget list source` is empty:
   `dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org`
3. `git config windows.appendAtomically false` on a OneDrive-synced clone.
4. Python 3.12 + `pip install blpapi --index-url https://blpapi.bloomberg.com/repository/releases/python/simple/`
   for the `tools\*.py` audits. Bloomberg terminal logged in on localhost:8194 to run them.
5. Build/test/publish: see README. ALWAYS verify the published exe's `FileVersion` AND SIZE
   (~172MB single-file; ~9.6MB = a bare apphost whose bundling step failed) — never trust build
   output. Bump the csproj versions (app + CLI in lockstep) on every user-visible change.
   RELEASE STAGING (lesson, 2026-08-20): publish each version into a FRESH staging dir and never
   write into a dir a `gh release` upload is still reading from — v0.6.4's bundle step failed on
   the lock held by v0.6.3's in-flight upload, produced a 9.6MB apphost that passed the
   FileVersion check, and shipped broken until the size check caught it (fixed with
   `gh release upload --clobber`). After every release: exit the running RatesWeekly.exe, swap
   publish\RatesWeekly.exe to the verified build, relaunch (desk instruction 2026-08-20).

## How the desk gets a build

- **GitHub release** (people without Azure): `gh release create v<ver> --repo
  gpcprojects/ratesweekly --target <branch> --title "RatesWeekly v<ver>" --notes "..."
  publish\RatesWeekly.exe publish\RatesWeekly_v<ver>.exe` — asset names must stay
  `RatesWeekly.exe` / `RatesWeekly_v*.exe` (the dodgeball 6.2.6 matcher lesson).
  **The permanent download link** — always the newest build, never expires, no sign-in:
  <https://github.com/gpcprojects/ratesweekly/releases/latest/download/RatesWeekly.exe>
  It works because every release carries the unversioned `RatesWeekly.exe` asset; keep it that
  way. Version-pinned permalink form (rollbacks):
  `https://github.com/gpcprojects/ratesweekly/releases/download/v<ver>/RatesWeekly_v<ver>.exe`.
- **Azure pipeline artifact** (people with Azure): every master push builds, tests, and
  publishes artifact `ratesweekly-exes` (Pipelines → ratesweekly → run → Artifacts).
- Users need ONLY `RatesWeekly.exe` + a logged-in terminal. State lives in `%APPDATA%\RatesWeekly`
  (`history.db` store, `out\` renders, optional `publish.json` `{"siteBase": …}` for links).

## Verification battery (run against a live terminal after touching dates/rolls/pricing)

- `python tools\audit_email_dates.py` — every date the RENDERED email lists vs each rung's own
  SW_EFF_DT; understands the v0.5.0 announced-shift (rows pair with rung N+shift on a decision
  day until the family re-points). Last run 2026-08-20 (live Riksbank decision day, family not
  yet re-pointed): 75 verified, 0 mismatches.
- `python tools\verify_strip_changes.py` — independent raw-BDH restitch of every rendered
  meeting row (level/1w/1m) + futures cross-checks for every guardFutures run (FF/IB/SFI/COR,
  level and 1w change). Last run 2026-08-20: 67/67 reconciled, all four guards ok (gaps ≤1.6bp),
  0 mismatches. Run AFTER a fresh `RatesWeeklyCli render`; both scripts fail loudly on
  positive-control gaps.
- 224 unit tests: `dotnet test tests\RateDesk.Tests\RateDesk.Tests.csproj -c Release`.

## Open items (nobody is blocked, but know these)

1. **PR #327** awaits Complete.
2. **decisionDates calendars**: topped up through mid-2027 for every run on 2026-08-20
   (RIKSBANK from Bloomberg ECO — the riksbank.se calendar page is JS-rendered, read it off the
   terminal; FOMC/MPC/SNB from the official calendars). Since v0.5.0 these power the
   decision-day front roll, so keep CalendarHealth's 90-day runway warnings at zero. Never
   hand-estimate SWAP-period dates — probe (`dodgeball tools\probe_all_meetings.py`).
3. **Real-Outlook paste/send test** — CREATE EMAIL and the `.html` attachment have never been
   sent to an external recipient; some gateways quarantine html attachments (PDF pack is the
   designed fallback, not yet built).
4. **Azure static site** (DESIGN §0/§9) — the roadmap for stable public links; needs someone
   with an Azure subscription to create the storage account. Email links light up via
   publish.json `siteBase` with zero code change.
5. **Store depth** — 45d by desk decision; corr charts + strict weekly-σ movers light up as it
   deepens (DESIGN §0a). Deepen out-of-hours only, desk sign-off first.
6. **Cherry-picks owed to dodgeball** (it has these bugs/lacks these fixes live):
   the RBA decision-week date fix (ResolveMeetingDates 3-day tolerance), the
   announced-but-not-yet-effective Priced re-base (replaces its manual MeetingRefOverrides),
   and the v0.5.0 bundle — DecisionClock time-gated front roll, announcement-gated re-base,
   1d meeting column, observation-window CalendarHealth. Its standalone DodgeballWeekly.exe
   showed all the symptoms live on 2026-08-20 (Riksbank front not rolled, stale base, wrong
   1m changes) — the desk answer is switching to RatesWeekly.exe, not patching it twice.
   Target its `history-basis-guard` line — dodgeball master does NOT contain v7.1.0 yet.
7. **Movers refinement with the desk** — hero diversity caps, slope set, beta-conditional
   "things to flag" rules (pending ~6m history), then reintegration into dodgeball.
