# Handoff — 2026-08-20

Read `CLAUDE.md` (team conventions) and `DESIGN.md` (full spec + decision log) with this.
Chat transcripts do NOT travel between machines — what matters is written down here or there.

## Where things stand

- **v0.6.3**, 229/229 tests green. v0.6.3 (2026-08-20): Y/E TURN labelling — year-end-spanning meeting periods on marked runs (SEK/SWESTR) render "Y/E Turn" instead of numbers everywhere, guard stands down, movers/charts skip; v0.6.4: the step chain SKIPS turn rows — the next row shows the cumulative move across the masked meeting + its own (DESIGN has the mechanics). v0.6.2 (2026-08-20): 1m = same-day-last-month everywhere (was fixed 31d; matches the sheet's intended convention), ECB futures guard on ICE 3M ESTR (TKY, index-matched, 0.0bp live; basis knob available for ER/TKYER if wanted). v0.6.1 (2026-08-20): STALE-OUTPUT GATE in the app — on open
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
5. Build/test/publish: see README. ALWAYS verify the published exe's `FileVersion` — never trust
   build output. Bump the csproj versions (app + CLI in lockstep) on every user-visible change.

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
