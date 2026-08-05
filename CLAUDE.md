# RATESWEEKLY — team guide for Claude Code

**Read `DESIGN.md` first**: full spec, decision log (2026-08-05), and the ⚠ backlog of quantities
that still need desk sign-off before they are wired. Chat transcripts do NOT travel between
machines — decisions and gotchas get written down here or in DESIGN.md.

Standalone .NET 8 WPF app (`RatesWeekly.exe`): one click weekly → Bloomberg pull (localhost:8194,
terminal running + logged in) → maintained SQLite history → ~29 self-contained HTML dashboards
(Movers hub + 28 currencies) uploaded to a public static site → desk email (CF_HTML into Outlook)
with hardwired links. Investor-facing output: pages are viewable by external people, zero sign-in.

## Hard constraints
- STANDALONE EXE: configs are EMBEDDED resources; user/market state lives in %APPDATA%\RatesWeekly
  only. Never add external file dependencies.
- This repo is FULLY SEPARATE from dodgeball. Teammates here may have no dodgeball access.
- HISTORY DEPTH: seed is 45d by design for now (desk call 2026-08-05 — deepen gradually/overnight
  later by raising UpdateEngine.SeedDays; upsert-on-overlap deepens in place, no migration).
  Do NOT run deep BDH backfills without the desk's say-so — terminal data limits are shared.
- Get sign-off before building on inferred quantities (tenors/structures/conventions) — the ⚠
  backlog in DESIGN.md §10 is the list.

## Provenance of src/RateDesk.Core and src/RateDesk.Bloomberg
Copied VERBATIM from the dodgeball repo at commit 0324397 (v7.0.0, 2026-08-05) for full
independence. They carry the 28-ccy configs, meeting roll/stitch logic, BDH client, and analytics
primitives — see dodgeball's CLAUDE.md for the accumulated gotchas (meeting tickers are rolling
generics; probe new tickers by NAME/SECURITY_DES via tools\BbgSmoke, never by "has price";
forward families need the BLC qualifier; etc). Improvements flow between the repos by manual
cherry-pick, deliberately — config drift is acceptable, silent coupling is not.

## Build / test
- SDK: dotnet 8 ("C:\Users\GPC Work\.dotnet\dotnet.exe" on the original machine).
- Tests: dotnet test tests\RateDesk.Tests\RateDesk.Tests.csproj -c Release  (must stay green;
  fixture has USD/GBP/AUD/MXN only).
- Publish (single file, self-contained):
  dotnet publish src\RateDesk.Weekly\RateDesk.Weekly.csproj -c Release -r win-x64 -o publish --self-contained true /p:PublishSingleFile=true
  Verify (Get-Item publish\RatesWeekly.exe).VersionInfo.FileVersion afterwards — never trust build output.
- Version lives in RateDesk.Weekly.csproj; bump on every user-visible change.
- Windows PowerShell 5.1 -replace/Set-Content MOJIBAKES em-dashes/UTF-8 — edit files with the Edit
  tool or python, never PS string surgery on source.
- Git on a OneDrive-synced clone needs `git config windows.appendAtomically false` (set here).

## Layout
- src\RateDesk.Weekly.Core — engine (no WPF): HistoryStore (SQLite, %APPDATA%\RatesWeekly\history.db,
  daily closes only, raw tickers only, today excluded, upsert self-heals), TickerUniverse (~989
  tickers), UpdateEngine (seed/maintain BDH → store).
- src\RateDesk.Weekly — WPF shell (UPDATE / COPY EMAIL / OPEN OUTPUT). Renderers + email builder
  land here (P2).
- tools\BbgSmoke — ticker probe (NAME/SECURITY_DES first). Use for the invoice-spread research.

## Team workflow
Same discipline as dodgeball: branch from master, PR back, never commit to master directly, push
regularly. Central repo: Azure DevOps (JBDHServices/DraxSwaps) — repo `ratesweekly`. Releases to
the desk: GitHub releases on a separate public repo (updater pattern), NOT gp-dbrel — asset names
RatesWeekly.exe / RatesWeekly_v*.exe only.
