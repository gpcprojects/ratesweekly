# Handoff — 2026-08-20

Read `CLAUDE.md` (team conventions) and `DESIGN.md` (full spec + decision log) with this.
Chat transcripts do NOT travel between machines — what matters is written down here or there.

## Where things stand

- **v0.5.0**, 219/219 tests green. `master` through PR 331; PRs 327 (localised delivery,
  DESIGN §12 hardening) and 331 (download-link handoff) are MERGED. v0.5.0 (2026-08-20, the
  live Riksbank decision day) adds: TIME-GATED FRONT ROLL — decisionTimeLondon now gates the
  decision-day roll (DecisionClock; the just-decided period leaves the boards/strips at the
  announcement, feed re-point or not), the Priced re-base moves to the same clock (was next
  day), and the meeting cards carry a **1d Chg** column off the stitched series. DESIGN §12
  has the full mechanics. All of §12 is now the dodgeball cherry-pick bundle.
- The desk email + all dashboards are BUILT AND VERIFIED from this repo. dodgeball's standalone
  DodgeballWeekly.exe is slated for removal once the desk runs this app.
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
  meeting row (level/1w/1m) + a Fed Funds futures cross-check. Last run 2026-08-20: 67/67
  reconciled, 0 mismatches.
  Run AFTER a fresh `RatesWeeklyCli render`; both scripts fail loudly on positive-control gaps.
- 219 unit tests: `dotnet test tests\RateDesk.Tests\RateDesk.Tests.csproj -c Release`.

## Open items (nobody is blocked, but know these)

1. **PR #327** awaits Complete.
2. **decisionDates calendars need topping up** from the official CB calendars (config
   \meetings.json). The code degrades honestly (front shows `start *`), but real decision dates
   are better — and since v0.5.0 they also power the decision-day front roll. CalendarHealth
   flagged on 2026-08-20: RIKSBANK has no decision for the 11-Nov-26 period (the riksbank.se
   calendar page is JS-rendered — read it in a browser or off the terminal's RIKSBANK page;
   announcement pattern is publish 09:30 CET, applies the following Wednesday). Never
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
