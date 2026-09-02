# RATESWEEKLY — team guide for Claude Code

**Read `DESIGN.md` first**: full spec, decision log (2026-08-05), and the ⚠ backlog of quantities
that still need desk sign-off before they are wired. Chat transcripts do NOT travel between
machines — decisions and gotchas get written down here or in DESIGN.md.

Standalone .NET 8 WPF app (`RatesWeekly.exe`): one click weekly → Bloomberg pull (localhost:8194,
terminal running + logged in) → maintained SQLite history → ~29 self-contained HTML dashboards
(Movers hub + 28 currencies) uploaded to a public static site → desk email (CF_HTML into Outlook)
with hardwired links. Investor-facing output: pages are viewable by external people, zero sign-in.

## Hard constraints
- STANDALONE EXE: configs are EMBEDDED resources; settings live in %APPDATA%\RatesWeekly, the
  DATA STORE (history.db) in %LOCALAPPDATA%\RatesWeekly (desk 2026-08-26 — never on a
  roaming/synced/shared path: SQLite WAL + file sync corrupts). Cross-machine continuity =
  StoreBackup snapshots (VACUUM INTO) on the save-down share, restore offered on first run of
  an empty machine. The store is partly IRREPLACEABLE (incumbent-sheet 'xls' rows, manual
  marks, per-day maturity records) — never treat it as a rebuildable cache, never put the live
  .db next to the exe or in OneDrive. Never add external file dependencies.
- This repo is FULLY SEPARATE from dodgeball. Teammates here may have no dodgeball access.
- HISTORY DEPTH: seed is 45d by design for now (desk call 2026-08-05). Deepen later by raising
  UpdateEngine.SeedDays/CorrSeedDays and re-running `update` several times — each run deepens up to
  MaxSeedPerRun tickers per bucket and defers the rest, which is what makes it "gradual". Bucketing
  is on the store's per-ticker DEPTH WATERMARK (coverage.seed_days), never on row existence — an
  existence test makes deepening a silent no-op (audited 2026-08-05; regression tests guard it).
  Do NOT run deep BDH backfills without the desk's say-so — terminal data limits are shared.
- Get sign-off before building on inferred quantities (tenors/structures/conventions) — the ⚠
  backlog in DESIGN.md §10 is the list.
- HARD-DATA RULE (desk 2026-08-20, FINAL — applies to PRICING AND DATES): every published
  meeting-board number comes from documented Bloomberg data only — 16:30-London snaps and real
  closes/prints for values, the tickers' own SW_EFF_DT/MATURITY fields for dates. NO curve-implied
  mids, NO curve-anchored changes, NO config-dated rows: the run ENDS where Bloomberg's
  documentation ends. Config dates still drive roll boundaries and decision gating internally.
  A historical-curve 1w/1m anchor was built and SCRAPPED the same day — do not re-add it.

## Provenance of src/RateDesk.Core and src/RateDesk.Bloomberg
Copied VERBATIM from the dodgeball repo at commit 0324397 (v7.0.0, 2026-08-05), REFRESHED to
9b52693 (v7.1.0 line, 2026-08-11) for full independence. They carry the 28-ccy configs, meeting
roll/stitch logic, BDH client, and analytics primitives — see dodgeball's CLAUDE.md for the
accumulated gotchas (meeting tickers are rolling generics; probe new tickers by NAME/SECURITY_DES
via tools\BbgSmoke, never by "has price"; forward families need the BLC qualifier; etc).
Improvements flow between the repos by manual cherry-pick, deliberately — config drift is
acceptable, silent coupling is not. One marked divergence: WeeklyEmail.cs takes optional
dashboard-link/footer hooks (null = byte-identical to dodgeball's rendering).

## The weekly email lives HERE (consolidation decision, 2026-08-11)
The desk email (CB front table + meeting cards + forward grid, dodgeball's WEEKLY layout) is
BUILT AND SHIPPED from this app: Weekly.Core\EmailBuilder drives Core's BuildWeekly/WeeklyEmail
live at UPDATE time, persists email.html/email.txt/email_preview.html to out\. **CREATE EMAIL**
opens a ready Outlook draft (COM, late-bound) with the body + the single-file dashboards pack
attached (Render\SiteFile → out\RatesWeekly_Dashboards.html — the whole site, hash-routed, works
offline); COPY EMAIL remains the clipboard fallback (a paste cannot carry attachments). Layout
per desk spec 2026-08-11: three one-line forward grids (DM / EM · LATAM / ASIA EM), 26px spacing
unit everywhere, Priced heat on the meeting cards, NO movers strip in the email. Currency headers
hyperlink only when %APPDATA%\RatesWeekly\publish.json carries {"siteBase": …} — links are
OMITTED, never guessed. dodgeball's standalone DodgeballWeekly.exe was REMOVED over there
(2026-08-20, dodgeball PR 359) — this repo is the ONLY weekly app; never rebuild one there.

PRESENTATION RULES (desk, 2026-08-11 — same discipline as dodgeball's weekly): NO footer/source
line in the email ("dashboards updated … · source: Bloomberg" is gone — never re-add), NO blurb
on the movers page (no week line, no G3/methodology paragraph, no gate counts, no
pending-feature panels — sections only; the context lines print in the CLI render instead), and
the movers layout is DM and EM side by side (ordinary panels in the auto-fit grid), hero cards
stacked vertically per side, compact ranked table.

## Build / test
- SDK: dotnet 8 ("C:\Users\GPC Work\.dotnet\dotnet.exe" on the original machine).
- Tests: dotnet test tests\RateDesk.Tests\RateDesk.Tests.csproj -c Release  (must stay green;
  fixture has USD/GBP/AUD/MXN only).
- RELEASE RITUAL: refresh the embedded history seed FIRST — `VACUUM INTO` from the live store
  (%LOCALAPPDATA%\RatesWeekly\history.db) to assets\history_seed.db + regenerate
  assets\history_seed.txt (asOf/counts) — the app is STANDALONE and ships the desk's history
  inside the exe (desk 2026-09-02); a stale seed is a stale desk for every new install.
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
  tickers), UpdateEngine (seed/maintain BDH → store), MoverScan (outsized-movers ranking: |z| of
  the 1w change, est-σ until the deep seed, RMS week-vol ratio, roll-corrected meeting series —
  the "things to flag" descendant; refined here, then cherry-picked back to dodgeball).
  Render\MoversPage → out\index.html (the hub/landing page).
- src\RateDesk.Weekly — WPF shell (UPDATE / COPY EMAIL / OPEN OUTPUT) + ClipboardHtml (CF_HTML
  writer, UTF-8 byte offsets). Page renderers live in Weekly.Core\Render; the email builder in
  Weekly.Core\EmailBuilder (own Bloomberg session; runs inside UPDATE, after the engine).
- tools\BbgSmoke — ticker probe (NAME/SECURITY_DES first). Use for the invoice-spread research.

## Team workflow
Same discipline as dodgeball: branch from master, PR back, never commit to master directly, push
regularly. Central repo: Azure DevOps (JBDHServices/DraxSwaps) — repo `ratesweekly`. Releases to
the desk: GitHub releases on a separate public repo (updater pattern), NOT gp-dbrel — asset names
RatesWeekly.exe / RatesWeekly_v*.exe only.
