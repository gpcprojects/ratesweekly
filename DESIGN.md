# RATESWEEKLY — design spec v0.2 (decisions locked 2026-08-05)

> App name: **RatesWeekly** (renamed from WeeklySummary, user 2026-08-05). Q1-Q4 answered
> 2026-08-05 (see §10). Items marked ⚠ remain inferred
> and get individual sign-off before they are wired (standing rule). This doc moves into the repo
> with the code.

## 0. Decisions (2026-08-05)

- **Users**: some desk members have Dodgeball, some don't. RatesWeekly ships as its own
  standalone exe with its own release channel — a user needs ONLY RatesWeekly + a Bloomberg
  terminal. No Dodgeball install, no repo access.
- **Source (revised 2026-08-05, supersedes the in-dodgeball-repo interpretation)**: fully separate
  repo at `{OneDrive}\ratesweekly`, pushed to its own Azure DevOps repo (teammates get access) and
  a separate GitHub repo for releases — mirroring dodgeball's delivery pattern but sharing nothing.
  RateDesk.Core/Bloomberg are copied in verbatim (dodgeball commit 0324397); improvements flow by
  manual cherry-pick.
- **History (2026-08-05)**: ~1 month only for now — seed 45d, NO deep backfill; deepen gradually /
  overnight later by raising `UpdateEngine.SeedDays` (upsert deepens the store in place). Curve
  pages with 1w/1m overlays work on 45d; rolling-corr charts and |z| movers ranking stay dark
  until the deep seed.
- **Hosting**: dashboards must be single links, **zero sign-in, viewable by internal AND external
  people** (investor-facing). → Azure Blob Storage static website, anonymous public read, in the
  JBDH tenant; the app uploads directly on Update. Claude artifacts REJECTED as primary host
  (publish requires an interactive Claude Code session; viewing requires org sign-in or an
  org-Owner-enabled public toggle). GitHub Pages (gpcprojects) is the zero-new-infra fallback —
  the release-token machinery already exists — at the cost of public git history for the content.
- **Layout**: Movers hub + 28 per-currency pages, shared grouped nav.
- **"corr 10y to usd"**: both lines on one chart — vs US 10y swap and vs DXY (USD page: DXY only).

## 1. What it is

A standalone Windows desktop app for the interest-rate derivatives desk. Once a week, one click:

1. pulls Bloomberg (localhost:8194, same DAPI as Dodgeball),
2. appends to a locally maintained history store,
3. regenerates ~30 self-contained HTML dashboard pages (1 Movers Summary + 1 per currency),
4. publishes them to stable URLs (Claude artifacts OR self-hosted Azure — Q2),
5. produces the desk email: Dodgeball's WEEKLY monitor table + central-bank grid, where every
   currency header is a hardwired hyperlink to its dashboard and a top-line link goes to Movers
   Summary. Copied to clipboard as CF_HTML → pastes into Outlook formatted.

Deployment philosophy inherited from Dodgeball: single self-contained exe, configs embedded,
user/market state in `%APPDATA%\RatesWeekly` only, no external file dependencies.

## 2. Verified constraints (2026-08-05)

- **Claude artifacts cannot be published headlessly.** The Artifact tool works only in an
  interactive, logged-in Claude Code session — it is off in `claude -p`, absent from the Agent SDK,
  and the only artifact HTTP API is read-only (compliance). Republish-to-same-URL from a new session
  IS supported (give the session the URL), so links stay stable. Per-page cap 16 MiB.
  Sharing: Team/Enterprise = private org share (viewers sign in with org claude.ai accounts) or
  public link if an org Owner enables it.
- These constraints are why artifacts were rejected as the primary host (§0): the publish leg
  cannot be one-click from a desktop app, and zero-sign-in external viewing needs an org-Owner
  public toggle. With the Azure static-site target the app publishes directly (HTTPS PUT) and no
  Claude session is involved.
- Publisher stays an interface (`IPublisher`: `AzureBlobPublisher` now; `GitHubPagesPublisher` or
  `ArtifactPublisher` addable later) so the hosting choice is not load-bearing.

## 3. What is inherited from Dodgeball (all verified against v7.0.1, branch `update-channel-fixes`)

| Asset | Where in Dodgeball | Use here |
|---|---|---|
| 28-ccy universe + groups DM(11)/EM(5)/LATAM(4)/ASIA EM(8) | `PricingServiceWeekly.cs:66-72` | Page list, email layout, movers grouping |
| Per-ccy configs: OIS/IRS pillar tickers, bands, sources | `config/currencies/*.json` (embedded) | Curve + forward data pulls |
| Meetings: 10 CB runs + SEK/NOK FRA strips; fields incl. `pastDates` | `config/meetings.json`, `PricingServiceBoards.cs:65-136` | Meeting-curve charts |
| Meeting-ticker ROLL rule (rolled iff lastBoundary > prev bus day; CoD(n)=mid(n)−PrevClose(n+1)) | `PricingServiceBoards.cs:729-748,628,665-669` | Correct week-over-week meeting changes |
| Roll-stitched meeting history (cluster ±6d, splice index that pointed at that meeting then) | `MeetingSeriesBuilder`, `PricingServiceBoards.cs:1055-1100` | 1w/1m meeting-curve lookbacks — the "solve for ticker roll" the brief asks for |
| Alias/maturity guard (USSOFED10+ trap: use ticker only when its own MATURITY = the period end) | `PricingServiceBoards.cs:501-516` | Same |
| Interior-quote neighbour guard (SKSF4A case) | `PricingServiceBoards.cs:630-650` | Same |
| Inflation: USD CPI / GBP RPI / EUR HICP ZC ladders + fixings + `FWIS{cc}{A}{B} Index` forwards | `usd.json:266-324`, `gbp.json:176-219`, `eur.json:349-401` | Inflation charts (exactly the 3 ccys the brief names) |
| Forward year-pair families EUSA/NDFS/SKFS/SAFS/KWFS (`BLC` mandatory) + FWCM style | `ForwardTicker.cs:75-121` | Quoted-forward cross-check of par-derived annual forwards |
| Par-approx forward f(a,b)=(b·par_b−a·par_a)/(b−a) + band-aware pillar selection | `PricingServiceAnalytics.cs:1495-1504`, `HistBandFor :961` | 1y1y..9y1y strips for all 28 ccys |
| Change methodology: close at-or-before today−7/−31 calendar days | `PricingServiceBoards.cs:199-208,243` + weekly build | 1w/1m lookbacks everywhere |
| Correlation primitives: Pearson on daily Δ (bp for rates, log×100 otherwise), Rolling(63d, step 5) | `Analytics/Correlation.cs:16-88` | Rolling-corr charts |
| Corr anchors: Brent CO1, WTI CL1, DXY, MOVE, FX crosses (BBDXY does not exist in repo) | `config/correlations.json` | Oil/USD corr charts |
| Z-scores windows 366/186/93 cal days, sample std n−1, live-last | `Analytics/SeriesStats.cs:53-115` | Movers normalization |
| Hampel despike (window 5, k=6) | `Analytics/HistoryFilter.cs:17` | Store hygiene before stats |
| BDH batching: PX_LAST, DAILY/ACTUAL/ACTIVE_DAYS_ONLY, maxDataPoints 5000, chunk clamp 20-100 | `RefDataClient.cs:261-336` | History backfill + weekly append |
| Snapshot fields PX_LAST/BID/ASK/PX_CLOSE_1D/MATURITY, chunk 400 | `RefDataClient.cs:53-135` | Live mids at update time |
| CF_HTML clipboard writer (UTF-8 byte offsets) + Outlook-safe inline-table HTML + opaque heat pastels | `MainWindow.Weekly.cs:292-499` | The email, verbatim technique |
| Probe discipline: NAME/SECURITY_DES via BbgSmoke, never "has price+history" | `tools/BbgSmoke`, CLAUDE.md | Invoice-spread ticker research |

Dodgeball persists NO market history (in-memory day-stamped caches only) — the history store here
is new build.

## 4. The email

Layout = Dodgeball WEEKLY email (monitor 3 rows: DM / EM+LATAM / ASIA EM; rows 2y 5y 10y 30y ·
1y1y 5y5y · 2s10s 5s30s; cols mid/1w/1m per ccy; heat fills ≥2bp; 9-card CB grid ex-SNB), plus:

- top strip: **"► MOVERS SUMMARY"** link + one-line teaser (the single biggest mover of the week);
- every currency header cell is `<a href>` to that currency's dashboard URL;
- footer: "dashboards updated {date}" + source line.

Links are plain anchors to stable URLs — maximally email-client-compatible. CF_HTML copy +
plain-text fallback as in Dodgeball. ⚠ Paste into a real Outlook draft is a phase-1 acceptance test
(Dodgeball's own HANDOFF flags this was never manually verified there either).

## 5. Movers Summary page

Headline strip: the biggest mover in each of six categories, then a top-10 table per category.

Candidate universes (all 28 ccys unless noted):
- **(a) OIS meeting dates** — every stitched meeting-dated rate across the 10 CB runs.
- **(b) Inflation** — USD/GBP/EUR ZC 1y..30y + FWIS forwards (1y1y, 5y5y, ...).
- **(c) Curve** — par outrights (pillar grid), slopes ⚠{2s5s, 2s10s, 5s30s, 10s30s}, par flies
  ⚠{2s5s10s, 5s10s30s}.
- **(d) Forward curves** — annual forwards 1y1y..9y1y (par-derived; quoted family where wired).
- **(e) Forward flies** — ⚠ consecutive flies on the annual strip: fly(n)=2·f(n1y)−f((n−1)1y)−f((n+1)1y), n=2..8.
- **(f) Invoice spreads** — USD/GBP/EUR once the module exists (§7).

Ranking ⚠: primary metric = |z| of the 1w change, where z = Δ1w / σ(weekly changes, 1y window from
the store) — makes a 9bp MXN move comparable to a 9bp CHF move. Raw Δbp shown alongside; both
1w and 1m columns; minimum-data gates (≥40 weekly obs) and despike before σ. Each row links to the
currency's page. Tabs/nav bar across the top to all 28 currency pages, grouped DM / EM / LATAM /
ASIA EM.

## 6. Per-currency pages (maximum template; sections auto-drop where a ccy lacks the market)

1. **Meeting-date curve** (10 ccys) — x = meeting date, lines: today / 1w ago / 1m ago from the
   stitched series; table below with Mid/Priced/Step/1wΔ/1mΔ (Dodgeball weekly-card logic).
2. **Annual forwards 1y1y..9y1y** (28) — today/1w/1m lines.
3. **Par curve 1y-30y** (28, per config pillar grid) — today/1w/1m lines.
4. **Rolling corr, 2y vs oil** (28) — 63d window on daily Δ, ~2y span. Oil = Brent CO1 default;
   ⚠ WTI CL1 for CAD/COP/MXN (Dodgeball curates CAD×WTI); ⚠ EUR gas-sensitivity precedent
   (eur 2y × TTF) — offer as second line for EUR only?
5. **Rolling corr, 10y vs USD** (28) — ⚠ meaning of "USD" is Q4: US 10y swap (duration beta) and/or
   DXY (dollar). USD's own page degenerates to DXY-only.
6. **Inflation** (USD/GBP/EUR) — ZC curve today/1w/1m; fixings history; FWIS forward curve
   today/1w/1m.
7. **Invoice spreads** (USD/GBP/EUR) — see §7.
8. Header stat tiles: 2y/5y/10y/30y mid + 1wΔ heat, 2s10s, 5y5y, next CB date & priced step.

All charts inline SVG with embedded JSON + vanilla JS tooltips/hover (no external libs — works under
artifact CSP and equally on a static host; dark/light theme aware). Page weight target <1 MiB.
Nav bar on every page: Movers + 28 ccys, grouped. Build with the dataviz skill conventions.

## 7. Invoice spreads module — GREENFIELD, needs probe + sign-off before build

Nothing exists in Dodgeball (verified: zero hits for invoice/ASW/bond futures). Proposal ⚠:
- Definition: invoice spread = futures-implied CTD forward yield vs the matched-maturity forward
  swap (same convention as the desk trades it — CONFIRM sign and convention with the desk).
- Contracts: USD TU/FV/TY/US/WN; EUR Schatz/Bobl/Bund/Buxl (DU/OE/RX/UB); GBP long gilt (G ).
  Front contract, roll on volume or fixed calendar — CONFIRM.
- Bloomberg sourcing to be probed with BbgSmoke discipline (NAME/SECURITY_DES first): candidates
  include the futures' `FUT_CTD_*` / implied-repo fields and any pre-computed invoice-spread
  series; if none price via DAPI, compute from CTD yield + our forward swap.
- Until probed and signed off, the movers category (f) and page section 7 ship dark.

## 8. History store (new build — the maintained asset)

- **SQLite**, single file `%APPDATA%\RatesWeekly\history.db` (embedded engine, no external service;
  bundles into a single-file publish). Atomic writes; the app is its only writer.
- Schema: `series(id, ticker_or_key, kind, ccy, meta)` + `daily(series_id, date, value)` +
  `runs(update_id, asof, notes)`. Store RAW ticker closes only; derived series (stitched meeting
  history, par-derived forwards, flies, corr) are recomputed deterministically from raw — no risk of
  stale derived data.
- Universe: all pillar tickers (28 ccys), meeting tickers ({N}=1..13 × 12 runs), inflation ladders +
  fixings, FWIS forwards, quoted forward families, corr anchors (oil/DXY/FX). Order ~900 tickers.
- **Seed**: first run backfills ⚠730 days via BDH (chunked per Dodgeball's clamp — ~1 batch run).
- **Maintain**: every Update fetches the trailing 45 days for all series and upserts — self-healing
  (a skipped week or a restated print patches itself; BDH is the source of truth for the overlap).
- Corr charts need ~2.5y for a 2y rolling span → corr-anchor + 2y/10y pillar series seed at 1000d.
- Size: ~900 × 750 ≈ 0.7M rows — trivial (<50 MB).

## 9. Update flow (the one click)

App = small WPF window (Dodgeball-style dark theme): status log, buttons **UPDATE**,
**COPY EMAIL**, **OPEN OUTPUT**, per-page publish status, last-run timestamp.

UPDATE →
1. snapshot live mids + PX_CLOSE_1D (chunk 400);
2. BDH trailing-45d upsert into store (+ first-run backfill);
3. compute: stitched meeting series, forwards, flies, corrs, z's, movers ranking;
4. render: `out/movers.html`, `out/{ccy}.html` ×28, `out/email.html`, `out/manifest.json`;
5. publish leg: direct upload to the storage account's `$web` container (Entra-brokered token via
   the `AzureUpdates.cs` MSAL pattern, or a SAS held in `%APPDATA%\RatesWeekly\publish.json`) —
   fully in-process, page URLs fixed. One-time infra: storage account + static website enabled
   (needs the admin once; exact steps in the repo README when built).
6. COPY EMAIL → CF_HTML + plain text to clipboard.

## 10. Decision log + open ⚠ backlog

Q1-Q4 resolved 2026-08-05 — see §0. Repo placement is my stated interpretation of the Q1 answer
(requirement was user-independence from Dodgeball, satisfied by a standalone exe): flag if the
SOURCE must also live outside the dodgeball repo.

⚠ backlog (each confirmed with the desk before it lands, none blocks P1): slope/fly sets in §5(c,e);
movers ranked by |z| with bp alongside; oil anchor per ccy (Brent default, WTI for CAD/COP/MXN);
invoice spread definition/contracts (§7); 730d seed depth; storage-account naming/custom domain;
whether non-Dodgeball users also run Update (per-machine publish credentials) or only view.

## 11. Phasing

- **P1 skeleton + proof**: repo per Q1; store + BDH layer; par/forward pages for USD, EUR, GBP, JPY;
  publish end-to-end on the Q2 target (incl. the untested launch-claude leg if artifacts); email
  skeleton with live links; paste into real Outlook.
- **P2 breadth**: all 28 ccys; meeting-curve pages with stitcher; movers page + ranking; corr charts.
- **P3 depth**: inflation pages; invoice-spread probe → sign-off → build; polish per dataviz pass.
- **P4 ship**: share links to desk, ops notes (HANDOFF.md discipline), first supervised weekly run.
