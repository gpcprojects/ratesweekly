using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using QLNet;
using RateDesk.Core.Analytics;
using RateDesk.Core.Config;
using RateDesk.Core.Curves;
using RateDesk.Core.Dates;
using RateDesk.Core.Market;
using RateDesk.Core.Pricing;
using RateDesk.Core.Trades;

namespace RateDesk.Core
{
    // ---------- data shapes for the Monitor and Meetings boards ----------

    public sealed class MonitorCell
    {
        public string Label { get; init; } = "";
        public double? MidPct { get; set; }
        public double? CoDBp { get; set; }
    }

    public sealed class MonitorColumn
    {
        public string Ccy { get; init; } = "";
        public List<MonitorCell> Tenors { get; } = new();
        public List<MonitorCell> Spreads { get; } = new();
        /// <summary>Annuity-less par forwards from the tenor mids (1y1y, 2y2y, 5y5y, 10y10y).</summary>
        public List<MonitorCell> Fwds { get; } = new();
    }

    public sealed class MeetingRow
    {
        public DateTime Date { get; init; }
        /// <summary>END of the period this row's quote covers (the next meeting boundary) —
        /// null when the run has no resolved next date (never a guess).</summary>
        public DateTime? EndDate { get; init; }
        public double MidPct { get; init; }
        public double? PricedBp { get; init; }
        public double? StepBp { get; init; }
        public double? CoDBp { get; init; }
        /// <summary>Where the mid came from: the meeting-dated OIS ticker, or "curve" when implied.</summary>
        public string MidSource { get; init; } = "";
        /// <summary>The period spans a year-end and the schedule marks turn periods: renderers
        /// print "Y/E Turn" instead of the numbers (which stay populated — they are the real,
        /// turn-dominated market prints, still valid as blend inputs).</summary>
        public bool TurnPeriod { get; init; }
        /// <summary>The quoted print was rejected by the neighbour guard as impossible. The row
        /// keeps the REAL print internally (blend inputs, guards) but publishes NO NUMBER — the
        /// app never invents a mid (desk 2026-08-27: "we should never have to invent mids"), and
        /// the hard-data rule already says blank beats manufactured. A CHECK note names it.</summary>
        public bool Rejected { get; init; }

        /// <summary>The row publishes a LABEL instead of numbers. Two causes, one mechanism.</summary>
        public bool Masked => TurnPeriod || Rejected;

        /// <summary>What the renderers print in the Mid cell of a masked row. ONE definition, so
        /// the surfaces cannot drift apart (they carried three copies of the turn literal).</summary>
        public string MaskLabel => TurnPeriod ? MaskLabels.Turn : Rejected ? MaskLabels.Rejected : "";
    }

    /// <summary>The labels a masked row publishes in place of its numbers.</summary>
    public static class MaskLabels
    {
        public const string Turn = "Y/E Turn";
        /// <summary>Short enough for the blast's fixed-width Mid column.</summary>
        public const string Rejected = "n/a";
    }

    public sealed class MeetingRunResult
    {
        public string Name { get; init; } = "";
        public string Ccy { get; init; } = "";
        public string Header { get; init; } = "";
        public string RefName { get; init; } = "";
        public double? RefPct { get; set; }
        /// <summary>The ref rate was replaced by a manual override (post-decision, fixing not yet printed).</summary>
        public bool RefOverridden { get; set; }
        /// <summary>The ref was AUTO re-based onto the just-decided period's own OIS (the
        /// decision→start compensation) — a swap mid standing in for the fixing until the new
        /// rate prints. Renderers must mark it (fresh-eyes review 2026-08-26).</summary>
        public bool RefRebased { get; set; }
        /// <summary>The re-base fell all the way back to the decided contract's last close BEFORE
        /// the statement — a real print of the right contract, but one that cannot contain
        /// whatever the decision surprised the market with. Surfaces must not claim the base is
        /// current (fix 2026-08-27, scenario 61).</summary>
        public bool RefRebasedStale { get; set; }
        /// <summary>The family renumbered between the previous close and these marks (the
        /// announcement-day or evidence-detected roll). Surfaces use it to SAY the Δ columns
        /// difference each contract against its own prior marks — the desk read a correct
        /// roll-day board as wrong twice on 02-Sep-26 because nothing explained the shift.</summary>
        public bool RenumberedToday { get; set; }
        /// <summary>Next decision date + announcement time on the London clock.</summary>
        public DateTime? NextDecision { get; set; }
        public string DecisionTimeLondon { get; set; } = "";
        public List<MeetingRow> Rows { get; } = new();
        /// <summary>Published rungs whose quote had not ticked in over an hour at snapshot time
        /// (desk 2026-08-26: "install a warning for stale feeds") — surfaced as non-blocking
        /// STALE warnings, one line per run. A quiet far rung can be legitimate; the desk sees
        /// it and judges.</summary>
        public List<string> StaleNotes { get; } = new();
        public string? Warning { get; set; }
        /// <summary>"tickers" (market meeting-dated OIS) / "curve" (our OIS fwd between ticker dates) / "schedule" (json dates).</summary>
        public string DatesSource { get; set; } = "";
    }

    public sealed class MeetingScheduleDef
    {
        public string Name { get; set; } = "";
        public string Ccy { get; set; } = "";
        public string Header { get; set; } = "";
        /// <summary>Meeting-dated OIS ticker patterns, {N} = meeting number (0 = run-down whose
        /// maturity is the next meeting). Tried in order per N (BOJ switches root at 10).</summary>
        public List<string> Tickers { get; set; } = new();
        /// <summary>STIR futures pattern ({MY} = month code + year digit, e.g. SSY{MY} Comdty) used
        /// for mids when the meeting OIS has no quote — SNB periods map onto the quarterly SARON strip.</summary>
        public string? FuturesPattern { get; set; }
        /// <summary>Exchange-settled futures family used ONLY as an independent cross-check of the
        /// meeting rows (FuturesGuard) — never as a mid source, which is what FuturesPattern is.
        /// Must settle on the SAME overnight index the meeting OIS fixes on (FF↔EFFR, IB↔AUD cash
        /// rate, SFI↔SONIA, COR↔CORRA), or the guard measures basis instead of faults.</summary>
        public string? GuardFutures { get; set; }
        /// <summary>"monthavg" = 30-day cash-rate future settling on the delivery month's average
        /// (FF, IB); "imm3m" = 3M future compounding the index over an IMM quarter (SFI, COR).</summary>
        public string GuardFuturesKind { get; set; } = "monthavg";
        /// <summary>Breach threshold in bp between the futures-implied rate and the meeting-row
        /// blend (after subtracting GuardFuturesBasisBp). The index-matched families' honest gap
        /// is ~1-3bp; 8bp default keeps quiet weeks quiet while a mis-rolled front (a full step,
        /// 25bp+) always trips.</summary>
        // 2.5bp (desk 2026-08-25, was 8: "8bp tells us nothing") — a triggered guard should
        // mean the boards genuinely disagree with the exchange, not that vol was high
        public double GuardFuturesTolBp { get; set; } = 2.5;
        /// <summary>Expected futures-minus-OIS spread in bp, for guard futures that settle on a
        /// DIFFERENT index than the meeting OIS (EUR: Euribor futures vs ESTR meetings — the desk
        /// hedges with them, so they guard here too, ~+14bp measured 2026-08-20). The guard tests
        /// |gap − basis| ≤ tol; re-centre this knob when the basis regime shifts. 0 for the
        /// index-matched families.</summary>
        public double GuardFuturesBasisBp { get; set; }
        /// <summary>Day-count denominator for the imm3m compounding/annualization: 365 (GBP SONIA,
        /// CAD CORRA) or 360 (EUR Euribor/ESTR, USD money markets).</summary>
        public int GuardFuturesDcc { get; set; } = 365;
        /// <summary>Mark meeting periods that SPAN A YEAR-END as "Y/E Turn" instead of publishing
        /// their numbers (desk 2026-08-20, SEK). SWESTR drops sharply on the last business day of
        /// the year (a documented dislocation the Riksbank opened an investigation into in 2023),
        /// so a meeting OIS averaging over the turn prints far below the policy path — real market
        /// pricing of the turn, not policy expectation, and not a misprint. The date stays on the
        /// boards (the decision is real); the level/priced/changes are suppressed in every
        /// rendering and the row is excluded from movers ranking and chart scaling.</summary>
        public bool MarkTurnPeriods { get; set; }

        /// <summary>This family's generics renumber when the period STARTS, not at the decision
        /// (SKSF, probed 2026-08-25: five days after the 20-Aug decision SKSF1A still fronted
        /// the 26-Aug period). The stitcher must then keep roll boundaries ON the start dates —
        /// snapping them back to the decision (right for ECB/MPC/BOJ, whose feeds re-point at
        /// the announcement) mis-rungs every lookback that lands inside a decision→start
        /// window, which is how the Feb-27 row's Δ1d differenced the turn rung's history.</summary>
        public bool RollsAtPeriodStart { get; set; }

        /// <summary>DESK-CONFIRMED config dates count as documented (hard-data rule carve-out,
        /// desk 2026-08-25): set only where the desk has verified the period grid against
        /// Bloomberg's own swap table but the far rungs quote prices WITHOUT eff/maturity
        /// fields (SKSF5A+). Rows still need a real price to publish — this never invents
        /// a quote, only lets a verified date carry one.</summary>
        public bool TrustConfigDates { get; set; }
        /// <summary>Day-count denominator of the run's overnight FIXING index, for the
        /// compounded-fixing trial (desk 2026-08-26): 360 for EFFR/ESTR/SWESTR/SARON,
        /// 365 (the default) for SONIA/CORRA/AONIA/NZ OCR/TONAR/NOWA. Validated against the
        /// desk pricer's own compounded values 2026-08-26 (RBNZ reproduced to the tick).</summary>
        public int FixingDcc { get; set; } = 365;
        /// <summary>Business days the o/n fixing lags behind the rate it reports, i.e. how far
        /// past the period start the announced-but-not-yet-effective re-base must keep running.
        /// ZERO by default, corrected 2026-08-27 against the live Riksbank run. The reasoning:
        /// the fixing printed on day d reports day d-1, and for eight of the ten runs the new
        /// policy rate applies FROM the period start. So the printed fixing is stale only while
        /// d-1 &lt; start, i.e. d &lt;= start, which is a window of zero extra days beyond the start
        /// itself. A default of 1 re-based the Riksbank on 27-Aug, the day AFTER its period began
        /// - by which time SWESTR had already printed the new rate - and replaced a real 1.642
        /// fixing with a 1.660 swap mid, moving every Priced on the board by ~1.8bp.
        ///
        /// FOMC and MPC are the exception and carry 1 in config: their period starts ON the
        /// decision date but the new target applies from the day AFTER, so their fixing is stale
        /// for one day longer.</summary>
        public int FixingLagDays { get; set; }
        public string? RefTicker { get; set; }
        /// <summary>The bank's own POLICY TARGET ticker (FDTR, UKBRBASE, EUORDEPO, ...) — the
        /// documented source of the DELIVERED MOVE SIZE for the announced-but-not-yet-effective
        /// base (desk 2026-09-01: "use the most recent fixing ± the amount they move by, not
        /// the stub mid"). Δ = target now − target's last pre-decision close; inside the window
        /// the base is fixing print + Δ, and it resets to the print alone the moment the fixing
        /// itself has genuinely moved. Null = the pre-2026-09-01 behaviour (the decided period's
        /// own OIS), which also remains the fallback whenever the target data is missing.</summary>
        public string? PolicyTicker { get; set; }
        /// <summary>Ladder name whose strip is the POLICY curve for this central bank, when that is a
        /// different index from the currency's default OIS curve. USD is the case: tenor swaps and forwards
        /// are SOFR, but everything meeting-dated is Fed Funds — the board's own USSOFED{N} tickers and its
        /// FEDL01 reference are already EFFR, so a meeting trade must price on the FedFunds strip and not on
        /// SOFR. Null (the norm) means the currency's OIS curve IS the policy curve.</summary>
        public string? PolicyLadder { get; set; }
        /// <summary>Decision announcement time on the London clock, e.g. "19:00".</summary>
        public string DecisionTimeLondon { get; set; } = "";
        /// <summary>"" = meeting run; "fra" = curve-implied 3M forwards at quarterly IMM dates.</summary>
        public string Kind { get; set; } = "";
        /// <summary>Default pricing contributor for the meeting tickers ("" = composite). The
        /// composite drops thin meeting OIS (BOC run-down) that BMOD/NABZ carry live.</summary>
        public string Source { get; set; } = "";
        public List<DateTime> Dates { get; set; } = new();
        /// <summary>Trailing-year decision dates — only used to stitch ticker HISTORY across rolls.</summary>
        public List<DateTime> PastDates { get; set; } = new();
        /// <summary>ANNOUNCEMENT dates, where they differ from the swap-period boundaries in
        /// <see cref="Dates"/>. Some families' periods start ON the decision (FOMC, MPC), others at
        /// the effective date days later (ECB's maintenance-period Wednesday, BOJ's settlement) — so
        /// "Dates" is the period grid the tickers key on, and THIS is what a human calls the meeting.
        /// Optional, hand-curated from the official calendars; consumers must fall back to Dates.</summary>
        public List<DateTime> DecisionDates { get; set; } = new();
    }

    /// <summary>Central-bank decision dates: config\meetings.json next to the exe overrides the embedded list.
    /// Past dates are skipped at read time, so runs roll automatically after each meeting.</summary>
    public static class MeetingsStore
    {
        private sealed class FileShape { public List<MeetingScheduleDef> Runs { get; set; } = new(); }

        private static readonly Lazy<List<MeetingScheduleDef>> _schedules = new(Load);

        public static IReadOnlyList<MeetingScheduleDef> Schedules => _schedules.Value;
        public static string Origin { get; private set; } = "embedded";

        private static List<MeetingScheduleDef> Load()
        {
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            string? json = null;
            var over = System.IO.Path.Combine(AppContext.BaseDirectory, "config", "meetings.json");
            if (File.Exists(over)) { json = File.ReadAllText(over); Origin = over; }
            if (json == null)
            {
                var asm = Assembly.GetExecutingAssembly();
                using var s = asm.GetManifestResourceStream("RateDesk.Core.config.meetings.json");
                if (s != null) { using var r = new StreamReader(s); json = r.ReadToEnd(); }
            }
            if (json == null) return new List<MeetingScheduleDef>();
            var shape = JsonSerializer.Deserialize<FileShape>(json, opts);
            var runs = shape?.Runs ?? new List<MeetingScheduleDef>();
            // a date that has settled is a PAST date now: migrate it so the history stitcher and
            // the roll-day CoD correction stay current without anyone hand-editing pastDates after
            // every decision (BOJ's Jul-31 roll was missed exactly that way). 6-day dedup matches
            // the stitcher's clustering of ticker-maturity vs config dates for the same meeting.
            foreach (var s in runs)
                foreach (var d in s.Dates.Where(d => d.Date <= DateTime.Today))
                    if (!s.PastDates.Any(p => Math.Abs((p - d).TotalDays) <= 14))
                        s.PastDates.Add(d);
            return runs;
        }
    }

    public sealed partial class PricingService
    {
        /// <summary>The day the official snap moved 16:30 → 16:15 London (desk 2026-08-25).
        /// Snap history up to and including this date is NEVER re-read at the new time.</summary>
        public static readonly DateTime SnapTimeCutover = new(2026, 8, 25);

        /// <summary>Manual ref-rate overrides per run name (post-decision, before the fixing prints).
        /// Concurrent: written from the UI thread, read from the meetings worker.</summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, double> MeetingRefOverrides { get; }
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>WHEN THE PUBLISHED MARKS WERE TAKEN, on the London clock. Null = the marks
        /// are live, so the decision gates read the wall clock. Set to the snap time once
        /// SnapDiscipline has PINNED the marks (from 16:15 London), because the boards must then
        /// be gated by the clock the PRICES belong to, not by the clock the button was pressed
        /// at. The FOMC announces at 19:00 — after the 16:15 close — so a run pressed at 19:30
        /// was rolling the board past a decision every one of its prices predates
        /// (desk 2026-08-27, scenario 58: "don't roll — the marks are the close").</summary>
        public DateTime? MarksAsOfLondon { get; set; }

        /// <summary>Per-run pricing-source overrides (run name → contributor mnemonic, "" = composite).</summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, string> MeetingSourceOverrides { get; }
            = new(StringComparer.OrdinalIgnoreCase);

        public string MeetingSrc(MeetingScheduleDef sched) =>
            MeetingSourceOverrides.TryGetValue(sched.Name, out var o) ? o : sched.Source ?? "";

        /// <summary>Full meeting-ticker security for pattern index n, on the run's active source.</summary>
        public string MeetingTick(MeetingScheduleDef sched, string pat, int n)
        {
            var src = MeetingSrc(sched);
            return pat.Replace("{N}", n.ToString()) + (src.Length > 0 ? " " + src : "") + " Curncy";
        }

        // prev-close curves rebuild once per day per (ccy, source) — PX_CLOSE_1D is static intraday
        private readonly Dictionary<(string ccy, string src), (DateTime day, CurveSet curves)> _prevCurveCache = new();

        private CurveSet? GetPrevCloseCurvesUnlocked(CurrencyConfig cfg, string src)
        {
            var key = (cfg.Ccy.ToUpperInvariant(), src.ToUpperInvariant());
            if (_prevCurveCache.TryGetValue(key, out var hit) && hit.day == DateTime.Today) return hit.curves;
            try
            {
                // only day-cache a CLEAN build — if any pillar lacked PX_CLOSE_1D (early morning,
                // partial snapshot) the live-mid substitute must not be frozen in for the whole day
                bool clean = true;
                var curves = CurveBuilder.Build(cfg, src, Snapshot, AdjustedToday(cfg),
                    (full, r) =>
                    {
                        if (Snapshot.Get(full)?.PrevClose is double pc) return pc / 100.0;
                        clean = false;
                        return r;
                    },
                    ExternalDiscountFor(cfg));
                if (clean) _prevCurveCache[key] = (DateTime.Today, curves);
                return curves;
            }
            catch { return null; }
        }

        // ---------- rates monitor ----------

        /// <summary>Mids + change-on-day for one currency's headline curve (default product's quotes,
        /// dated-ladder fallback for analytics-only ccys like BRL), plus curve spreads and par
        /// forwards. Tenor matching is nearest-within-tolerance so 28-day-period markets (MXN 26P
        /// ≈ 2Y) populate their columns. Values come straight from the snapshot.</summary>
        /// <summary>chgDays: 1 = change vs prior close (PX_CLOSE_1D); 7/31/93 = change vs the
        /// close N calendar days back from (raw) BDH history.</summary>
        public MonitorColumn MonitorFor(string ccy, string[] tenors, (string a, string b)[] spreads,
            int chgDays = 1)
        {
            var cfg = Configs.Get(ccy);
            var src = SourceFor(ccy);

            double? ChgBp(string ticker, double midPct)
            {
                if (History == null) return null;
                var h = History.GetDaily(ticker, 220);
                var target = DateTime.Today.AddDays(-chgDays);
                for (int i = h.Count - 1; i >= 0; i--)
                    if (h[i].Date <= target)
                        return (midPct - h[i].Value) * 100.0;
                return null;
            }

            // pillar list: default product, else the first quoted ladder (BRL DI).
            // Dual-band markets quote TWO families at one tenor (AUD 4Y-9Y q/q AND s/s, ~26bp apart),
            // so every pillar carries its band and whether that band is the tenor-rule (screen) one:
            // tenor ROWS read screen-convention pillars only; par-FORWARD rows read both endpoints
            // from the window tenor's own family, never straddling the basis.
            List<(double months, string full, bool natural, string? band)>? pillars = null;
            bool boardIrs = cfg.DefaultProduct.Equals("IRS", StringComparison.OrdinalIgnoreCase) && cfg.Irs != null;
            bool multiBand = boardIrs && cfg.Irs!.Legs.Count > 1;
            var curve = boardIrs ? cfg.Irs!.Curve : cfg.Ois?.Curve ?? cfg.Irs?.Curve;
            if (curve != null)
                pillars = curve.Where(p => p.Enabled && !p.Type.Equals("DEPO", StringComparison.OrdinalIgnoreCase) && !p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase))
                    .Select(p =>
                    {
                        string? band = multiBand ? RateDesk.Core.Pricing.SwapBuilder.PillarBand(cfg.Irs!, p) : null;
                        bool natural = band == null || band.Equals(
                            RateDesk.Core.Pricing.SwapBuilder.SelectIrsLeg(cfg.Irs!, TenorUtil.Parse(p.Tenor), null).FloatTenor,
                            StringComparison.OrdinalIgnoreCase);
                        return (TenorUtil.ApproxMonths(TenorUtil.Parse(p.Tenor)), ConfigStore.ResolveTicker(p.Ticker, src), natural, band);
                    })
                    .ToList();
            else if (cfg.Ladders.Count > 0)
                pillars = cfg.Ladders[0].Pillars.Where(p => p.Enabled && !p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase))
                    .Select(p => (TenorUtil.ApproxMonths(TenorUtil.Parse(p.Tenor)), ConfigStore.ResolveTicker(p.Ticker, ""), true, (string?)null))
                    .ToList();

            (double mid, double? cod)? QuoteAt(double wantMonths, string? band)
            {
                var near = pillars?
                    .Where(p => band == null ? p.natural : band.Equals(p.band, StringComparison.OrdinalIgnoreCase))
                    .Where(p => Math.Abs(p.months - wantMonths) <= Math.Max(1.5, wantMonths * 0.035))
                    .OrderBy(p => Math.Abs(p.months - wantMonths)).ToList();
                if (near is not { Count: > 0 } || Snapshot.Get(near[0].full) is not { } q || !q.Mid.HasValue)
                    return null;
                return (q.Mid.Value, chgDays <= 1 ? q.CoDBp : ChgBp(near[0].full, q.Mid.Value));
            }

            var col = new MonitorColumn { Ccy = ccy.ToUpperInvariant() };
            var byLabel = new Dictionary<string, MonitorCell>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tenors)
            {
                var cell = new MonitorCell { Label = t };
                if (QuoteAt(TenorUtil.ApproxMonths(TenorUtil.Parse(t)), null) is { } q)
                {
                    cell.MidPct = q.mid;
                    cell.CoDBp = q.cod;
                }
                col.Tenors.Add(cell);
                byLabel[t] = cell;
            }
            foreach (var (a, b) in spreads)
            {
                var cell = new MonitorCell { Label = $"{a.TrimEnd('Y', 'y')}s{b.TrimEnd('Y', 'y')}s" };
                if (byLabel.TryGetValue(a, out var ca) && byLabel.TryGetValue(b, out var cb)
                    && ca.MidPct.HasValue && cb.MidPct.HasValue)
                {
                    cell.MidPct = (cb.MidPct - ca.MidPct) * 100.0; // spread quoted in bp
                    if (ca.CoDBp.HasValue && cb.CoDBp.HasValue) cell.CoDBp = cb.CoDBp - ca.CoDBp;
                }
                col.Spreads.Add(cell);
            }
            // par forwards: f(A,B) = (B·rB − A·rA)/(B−A), CoD combined with the same weights.
            // Both endpoints come from the WINDOW tenor's own quote family (AUD 2y2y = 2Q & 4Q, not
            // the screen rows' 2Q & s/s 4Y — that straddle books the 3s6s basis into the forward).
            foreach (var (label, ta, tb, win) in new[]
                     { ("1y1y", "1Y", "2Y", "1Y"), ("2y2y", "2Y", "4Y", "2Y"), ("5y5y", "5Y", "10Y", "5Y"), ("10y10y", "10Y", "20Y", "10Y") })
            {
                var cell = new MonitorCell { Label = label };
                string? fwdBand = multiBand
                    ? RateDesk.Core.Pricing.SwapBuilder.SelectIrsLeg(cfg.Irs!, TenorUtil.Parse(win), null).FloatTenor
                    : null;
                double a = TenorUtil.ApproxMonths(TenorUtil.Parse(ta)) / 12.0;
                double b = TenorUtil.ApproxMonths(TenorUtil.Parse(tb)) / 12.0;
                if (QuoteAt(a * 12.0, fwdBand) is { } qa && QuoteAt(b * 12.0, fwdBand) is { } qb)
                {
                    cell.MidPct = (b * qb.mid - a * qa.mid) / (b - a);
                    if (qa.cod.HasValue && qb.cod.HasValue)
                        cell.CoDBp = (b * qb.cod.Value - a * qa.cod.Value) / (b - a);
                }
                col.Fwds.Add(cell);
            }
            return col;
        }

        /// <summary>"Things to flag": beta-conditional anomalies across the monitored currencies.
        /// Rule 1 — curve moves scored against the ccy's own 6m beta to its 10y move
        /// ("NZD 2s10s out-flattening 2.1σ vs its usual steepening-in-selloff beta").
        /// Rule 2 — relative performance vs the G3 average when there IS a common move
        /// ("GBP 2y underperforming the G3 selloff, +2.9σ vs +1.1σ avg").
        /// Only ≥1.5σ speaks; a quiet day says so explicitly.</summary>
        public List<string> MonitorFlags(IEnumerable<string> ccys, int maxFlags = 5)
        {
            var found = new List<(double score, string txt)>();
            if (History == null) return new List<string> { "no history provider" };

            // per ccy per tenor: aligned level history + today's move (bp) + daily vol (bp).
            // G3 legs always load — even under an EM/DM filter rule 2 needs a common move to be
            // relative TO — but only ccys in the passed universe are FLAGGED.
            var universe = new HashSet<string>(ccys, StringComparer.OrdinalIgnoreCase);
            var data = new Dictionary<string, Dictionary<int, (IReadOnlyList<HistPoint> hist, double today, double vol)>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var ccy in universe.Union(new[] { "USD", "EUR", "JPY" }, StringComparer.OrdinalIgnoreCase))
            {
                if (!Configs.TryGet(ccy, out var cfg) || (cfg.Ois == null && cfg.Irs == null)) continue;
                var src = SourceFor(ccy);
                var product = cfg.DefaultProduct.Equals("IRS", StringComparison.OrdinalIgnoreCase) && cfg.Irs != null
                    ? ProductKind.IRS : cfg.Ois != null ? ProductKind.OIS : ProductKind.IRS;
                var per = new Dictionary<int, (IReadOnlyList<HistPoint>, double, double)>();
                foreach (var t in new[] { 2, 10, 30 })
                {
                    var tkr = ResolvePillarTicker(cfg, product, TenorUtil.Parse($"{t}Y"), src);
                    if (tkr == null) continue;
                    var q = Snapshot.Get(tkr);
                    if (q?.Mid is not double mid || q.PrevClose is not double pc) continue;
                    // despiked: one bad print in the window distorts the beta and the vol
                    var h = Analytics.HistoryFilter.Despike(History.GetDaily(tkr, 220));
                    if (h.Count < 100) continue;
                    double vol = 0;
                    int n = Math.Min(126, h.Count - 1);
                    var d = new double[n];
                    for (int i = 0; i < n; i++)
                        d[i] = (h[h.Count - n + i].Value - h[h.Count - n + i - 1].Value) * 100.0;
                    double m = d.Average();
                    vol = Math.Sqrt(d.Sum(x => (x - m) * (x - m)) / Math.Max(1, n - 1));
                    if (vol < 0.3) continue; // stale marks — a conditional z would be meaningless
                    double today = (mid - pc) * 100.0;
                    // an EXACTLY unchanged mark on a market that normally moves is almost surely
                    // an unticked quote (pre-open, snapshot seeded from the close), not genuine
                    // outperformance — without this every illiquid ccy "outperforms" each G3 move
                    if (today == 0.0) continue;
                    per[t] = (h, today, vol);
                }
                if (per.Count > 0) data[ccy.ToUpperInvariant()] = per;
            }

            // rule 1: curve move conditional on the level move (per ccy)
            foreach (var (ccy, per) in data)
            {
                if (!universe.Contains(ccy)) continue; // G3 loaded only as the rule-2 baseline
                foreach (var (name, tA, tB) in new[] { ("2s10s", 2, 10), ("10s30s", 10, 30) })
                {
                    if (!per.TryGetValue(tA, out var a) || !per.TryGetValue(tB, out var b)) continue;
                    // regime word from the REGRESSOR leg (the long leg of this pair), so the
                    // narrative matches what the beta is actually conditioned on
                    string word = b.today >= b.vol ? "selloff" : b.today <= -b.vol ? "rally" : "move";
                    var (_, dxA, dxB) = Analytics.Correlation.AlignedChanges(a.hist, b.hist, false, false);
                    int n = Math.Min(126, dxA.Length);
                    if (n < 60) continue;
                    // OLS: Δspread on the long leg's Δ (the level driver for that curve segment)
                    var y = new double[n];
                    var x = new double[n];
                    for (int i = 0; i < n; i++)
                    {
                        y[i] = (dxB[dxB.Length - n + i] - dxA[dxA.Length - n + i]) * 100.0;
                        x[i] = dxB[dxB.Length - n + i] * 100.0; // long leg ≈ the level driver
                    }
                    double mx = x.Average(), my = y.Average();
                    double sxx = 0, sxy = 0;
                    for (int i = 0; i < n; i++) { sxx += (x[i] - mx) * (x[i] - mx); sxy += (x[i] - mx) * (y[i] - my); }
                    if (sxx < 1e-9) continue;
                    double beta = sxy / sxx, alpha = my - beta * mx;
                    double ss = 0;
                    for (int i = 0; i < n; i++)
                    {
                        double e = y[i] - (alpha + beta * x[i]);
                        ss += e * e;
                    }
                    double sd = Math.Sqrt(ss / Math.Max(1, n - 2));
                    if (sd < 0.2) continue;
                    double sprToday = b.today - a.today;
                    double lvlToday = b.today;
                    double resid = sprToday - (alpha + beta * lvlToday);
                    double z = resid / sd;
                    if (Math.Abs(z) < 1.5) continue;
                    string dir = resid > 0 ? "out-steepening" : "out-flattening";
                    found.Add((Math.Abs(z),
                        $"{ccy} {name} {dir} {Math.Abs(z):0.0}σ vs its 6m beta in this {word} " +
                        $"({sprToday:+0.0;-0.0}bp curve on a {lvlToday:+0.0;-0.0}bp long-leg move)"));
                }
            }

            // rule 2: relative performance vs the G3 average when the move is real
            foreach (var tenor in new[] { 2, 10 })
            {
                var g3 = new[] { "USD", "EUR", "JPY" }
                    .Where(c => data.TryGetValue(c, out var p) && p.ContainsKey(tenor))
                    .Select(c => data[c][tenor].today / data[c][tenor].vol).ToList();
                if (g3.Count < 2) continue;
                double avg = g3.Average();
                if (Math.Abs(avg) < 1.0) continue; // no common move — nothing to be relative TO
                string word = avg > 0 ? "selloff" : "rally";
                foreach (var (ccy, per) in data)
                {
                    if (ccy is "USD" or "EUR" or "JPY") continue;
                    if (!per.TryGetValue(tenor, out var v)) continue;
                    double sig = v.today / v.vol;
                    double rel = sig - avg;
                    if (Math.Abs(rel) < 1.2) continue;
                    string perf = rel > 0 ? "underperforming" : "outperforming";
                    found.Add((Math.Abs(rel),
                        $"{ccy} {tenor}y {perf} the G3 {word} ({sig:+0.0;-0.0}σ vs {avg:+0.0;-0.0}σ avg)"));
                }
            }

            if (found.Count == 0)
                return new List<string> { "nothing unusual — moves are in line with 6m betas" };
            return found.OrderByDescending(f => f.score).Take(maxFlags).Select(f => f.txt).ToList();
        }

        // ---------- central-bank meeting runs ----------

        public List<MeetingRunResult> MeetingRuns(int maxRows = 10)
        {
            var outp = new List<MeetingRunResult>();
            foreach (var sched in MeetingsStore.Schedules)
            {
                MeetingRunResult res;
                try { res = MeetingRun(sched, maxRows); }
                catch (Exception ex)
                {
                    res = new MeetingRunResult { Name = sched.Name, Ccy = sched.Ccy, Header = sched.Header, Warning = ex.Message };
                }
                outp.Add(res);
            }
            return outp;
        }

        /// <summary>All meeting-dated OIS tickers for every run (N = 0..maxN), for snapshot/subscribe.
        /// Unknown candidates are harmless — the snapshot just marks them missing.</summary>
        public IEnumerable<string> MeetingTickers(int maxN = 12)
        {
            foreach (var sched in MeetingsStore.Schedules)
            {
                foreach (var pat in sched.Tickers)
                {
                    // explicit securities (the FRA-run IMM strips) carry no {N} — once is enough
                    if (!pat.Contains("{N}")) { yield return MeetingTick(sched, pat, 0); continue; }
                    for (int n = 0; n <= maxN; n++)
                    {
                        yield return MeetingTick(sched, pat, n);
                        // the COMPOSITE spelling rides along when a contributor source is active:
                        // Resolve() merges prices from the contributor with dates from whichever
                        // spelling carries the fields — a fallback that can only work if the plain
                        // spelling was actually snapshotted (audit 2026-08-26: it never was in the
                        // email/daily builds, so contributor pages without SW_EFF_DT silently
                        // shortened the run)
                        if (MeetingSrc(sched).Length > 0)
                            yield return pat.Replace("{N}", n.ToString()) + " Curncy";
                    }
                }
                if (!string.IsNullOrEmpty(sched.RefTicker)) yield return sched.RefTicker;
                if (!string.IsNullOrEmpty(sched.FuturesPattern))
                {
                    var q = new DateTime(DateTime.Today.Year, ((DateTime.Today.Month - 1) / 3) * 3 + 3, 1);
                    for (int i = 0; i < 14; i++)
                    {
                        yield return sched.FuturesPattern.Replace("{MY}", FutMy(q));
                        q = q.AddMonths(3);
                    }
                }
                if (!string.IsNullOrEmpty(sched.GuardFutures))
                {
                    // cross-check contracts: monthly for month-average families, IMM quarters for
                    // 3M ones — enough forward months that FuturesGuard always finds a covered,
                    // not-yet-started window inside the run
                    bool imm = sched.GuardFuturesKind.Equals("imm3m", StringComparison.OrdinalIgnoreCase);
                    var m = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                    for (int i = 0; i < 12; i++)
                    {
                        m = m.AddMonths(1);
                        if (imm && m.Month % 3 != 0) continue;
                        yield return sched.GuardFutures.Replace("{MY}", FutMy(m));
                    }
                }
            }
        }

        private static string FutMy(DateTime quarterMonth) =>
            "FGHJKMNQUVXZ"[quarterMonth.Month - 1] + (quarterMonth.Year % 10).ToString();

        /// <summary>One CB run. Primary: Bloomberg meeting-dated OIS tickers — dates from MATURITY
        /// (ticker N matures at meeting N+1; the N=0 run-down matures at the FIRST meeting), mids from
        /// their live quotes, CoD vs their prior close. Fallbacks: mid implied off our bootstrapped OIS
        /// curve between those dates; hardcoded schedule dates only when tickers are absent entirely.
        /// Priced = mid − current fixing (bp); Step = per-meeting increment.</summary>
        /// <summary>Resolved meeting dates and the quotes they came from.</summary>
        public sealed class MeetingDatesResult
        {
            public Market.QuoteData?[] Quotes { get; init; } = Array.Empty<Market.QuoteData?>();
            public Dictionary<int, DateTime> Dates { get; init; } = new();
            /// <summary>True when ticker MATURITY fields carried the run (rather than meetings.json).</summary>
            public bool FromTickers { get; init; }
            /// <summary>Indices whose date came from the TICKERS' OWN FIELDS (maturity chain /
            /// SW_EFF_DT) — the only rows the boards may publish under the hard-data rule
            /// (desk 2026-08-20): dates and prices from documented Bloomberg data only, never
            /// config fills or curves.</summary>
            public HashSet<int> TickerDated { get; init; } = new();
        }

        /// <summary>Future meeting DATES only — no curve, no mids, no live data required beyond what
        /// the snapshot already holds.
        ///
        /// <para>Extracted verbatim from <see cref="MeetingRun"/> because that method only populates
        /// its Rows AFTER computing a mid per meeting: with no live ticker and a failed OIS curve it
        /// breaks on the first row and returns empty even though the dates are perfectly well known
        /// from config/meetings.json. That is fine for the board (which needs the mid) but wrong for
        /// resolving a meeting ANCHOR — an IMM anchor needs zero live data, so a meeting anchor
        /// shouldn't either. Also makes the date logic unit-testable on its own.</para></summary>
        public MeetingDatesResult ResolveMeetingDates(MeetingScheduleDef sched, int maxRows = 10)
        {
            // Meeting-dated OIS quote per N. PRICES come from the run's contributor source and
            // DATES from whichever spelling carries the fields — the desk's own sheet splits
            // exactly this way (BDH "ADSF2A NABZ" for the rate, BDP "ADSF2A" for eff/maturity),
            // because contributor pages quote live prices but often publish no date fields.
            // Merging keeps tickerDated intact when a source is added (BOJ MTRT, NORGES DNBP).
            Market.QuoteData? Resolve(int n)
            {
                Market.QuoteData? priced = null, dated = null;
                foreach (var pat in sched.Tickers)
                {
                    var q = Snapshot.Get(MeetingTick(sched, pat, n));          // source-qualified
                    if (q != null)
                    {
                        priced ??= q.Mid.HasValue ? q : null;
                        dated ??= q.Maturity.HasValue ? q : null;
                    }
                    if (MeetingSrc(sched).Length > 0)
                    {
                        var plain = Snapshot.Get(pat.Replace("{N}", n.ToString()) + " Curncy");
                        if (plain != null)
                        {
                            priced ??= plain.Mid.HasValue ? plain : null;
                            dated ??= plain.Maturity.HasValue ? plain : null;
                        }
                        // a later PATTERN is a different family (BOJ's retired JYOMPM root, kept
                        // for rungs past where JYSOMPM quotes) — never merge ITS stale mid under
                        // THIS family's date (audit 2026-08-26): fall through only when this
                        // pattern produced nothing at all on either spelling
                        if (q != null || plain != null) break;
                    }
                    else if (q != null) break;
                    if (priced != null && dated != null) break;
                }
                if (priced == null && dated == null) return null;
                if (ReferenceEquals(priced, dated) || dated == null) return priced;
                if (priced == null) return dated;
                var merged = new Market.QuoteData
                {
                    Bid = priced.Bid, Ask = priced.Ask, Last = priced.Last,
                    PrevClose = priced.PrevClose, AgeMinutes = priced.AgeMinutes,
                    UpdatedUtc = priced.UpdatedUtc,
                    Maturity = dated.Maturity, Effective = dated.Effective,
                };
                return merged;
            }

            // Meeting date N (1-based) = maturity of ticker N-1; the run-down (0) matures at meeting 1.
            // ALIAS GUARD: Bloomberg aliases past-the-end numbers back to #1 (USSOFED10 -> USSOFED1,
            // JYSOMPM10 -> JYSOMPM1), so maturities must strictly increase — the family ends at the
            // first violation. Numbering is never evidence; a rung's own MATURITY is.
            var quotes = new Market.QuoteData?[maxRows + 2];
            var meetDates = new Dictionary<int, DateTime>();
            var tickerDated = new HashSet<int>();
            var lastMat = DateTime.MinValue;
            for (int n = 0; n <= maxRows + 1; n++)
            {
                var q = Resolve(n);
                quotes[n] = q;
                if (q?.Maturity is DateTime m)
                {
                    if (m > lastMat) { meetDates[n + 1] = m.Date; tickerDated.Add(n + 1); lastMat = m; }
                    else { quotes[n] = null; break; }
                }
            }
            bool tickerDates = meetDates.Count >= 2;

            // ...but a row's date is the START of the period its own quote covers, and that is only
            // the previous rung's maturity when the periods are contiguous. Nine of the ten families
            // are (eff(N) == mat(N-1) exactly, verified ticker by ticker 2026-08-07). The BOJ is not:
            // its periods begin at the settlement date after the decision, so JYSOMPM2 quotes
            // 2026-11-02 -> 2026-12-18 while mat(1) is 2026-10-30. Labelling that row 30-Oct names
            // the DECISION the rate responds to instead of the period the rate applies over, and the
            // two drift 1-3 days apart all the way down the run.
            //
            // So prefer the rung's own SW_EFF_DT. Bounded deliberately: a start may sit at most a
            // settlement lag (10d) AFTER the maturity-derived date, strictly before its own
            // maturity — and up to 3 days BEFORE it. That last bound was ZERO until 2026-08-11,
            // when the live RBA decision week showed why it cannot be: the run-down ADSF0A's
            // maturity printed 13-Aug (a T+1 settlement artifact) while ADSF1A's own SW_EFF_DT
            // said 12-Aug, the true period start (decision 11-Aug + 1d). A rung's own field is the
            // authority on its own period; rejecting it labelled the front row one day late in the
            // very week everyone reads it. A genuinely stale eff is a whole meeting period early
            // (~5 weeks), far outside 3 days, so the garbage guard keeps its teeth.
            bool laggedFamily = false;
            for (int n = 1; n <= maxRows + 1; n++)
            {
                if (!meetDates.TryGetValue(n, out var viaMat)) continue;
                if (quotes[n]?.Effective is not DateTime eff) continue;
                if ((viaMat.Date - eff.Date).TotalDays > 3 || (eff.Date - viaMat.Date).TotalDays > 10) continue;
                if (quotes[n]?.Maturity is DateTime own && eff.Date >= own.Date) continue;
                if (eff.Date > viaMat.Date) laggedFamily = true;
                meetDates[n] = eff.Date;
                tickerDated.Add(n);
            }

            // The last rung the family quotes has no NEXT rung to read a start from — its own row
            // would silently revert to naming the decision while every row above it names a period.
            // Only for a family that has DEMONSTRATED a settlement lag above, and only from the
            // config grid, which exists for exactly this (dates past where MATURITY is populated).
            // A contiguous family cannot be touched: its config dates equal the maturities, so
            // there is never one strictly after.
            if (laggedFamily)
                for (int n = 1; n <= maxRows + 1; n++)
                {
                    if (!meetDates.TryGetValue(n, out var d) || quotes[n]?.Effective != null) continue;
                    var start = sched.Dates.FirstOrDefault(x => x.Date > d.Date
                        && (x.Date - d.Date).TotalDays <= 10);
                    if (start != default) meetDates[n] = start.Date;
                }

            // FIELDS CAN LEAD PRICES (RBNZ, discovered live 02-Sep-26). NDSF's SW_EFF_DT and
            // MATURITY re-point at the ANNOUNCEMENT while its PRICES re-point at the period
            // start — for the day(s) in between, every rung's fields describe the NEXT
            // contract out, and a board trusting them labels each mid one meeting late and
            // stitches its changes one rung wrong (published Δ1d −15.4 where NAB's own
            // monitor and the desk said −8.0; NDSF1A's own closes ran 2.748→2.756 SMOOTHLY
            // across the alleged renumbering). The state announces itself with an impossible
            // claim: a RUN-DOWN (rung 0) that is UNQUOTED yet says it starts in the FUTURE.
            // While it holds, quote n prices the period [eff(n−1) → eff(n)]: shift every
            // resolved date one rung out (rung 0's own eff becomes row 1's start). Start-
            // rolling families only — everywhere else fields and prices flip together
            // (RBA/NORGES price-jump receipts around their 05/08-May-26 hikes).
            if (sched.RollsAtPeriodStart
                && quotes[0] is { Mid: null, Effective: { } e0 } && e0.Date > DateTime.Today)
            {
                var shifted = new Dictionary<int, DateTime> { [1] = e0.Date };
                var shiftedDated = new HashSet<int> { 1 };
                for (int n = 1; n <= maxRows + 1; n++)
                {
                    if (meetDates.TryGetValue(n, out var d0)) shifted[n + 1] = d0;
                    if (tickerDated.Contains(n)) shiftedDated.Add(n + 1);
                }
                meetDates = shifted;
                tickerDated = shiftedDated;
            }

            // fill gaps from the schedule: some families price beyond where MATURITY is populated
            // (EESF4A+, JYSOMPM4+), and without any tickers the schedule carries the whole run
            var schedDates = sched.Dates.Where(d => d.Date > DateTime.Today).OrderBy(d => d).ToList();
            var prevDate = DateTime.Today;
            bool havePrev = false;
            for (int n = 1; n <= maxRows + 1; n++)
            {
                if (meetDates.TryGetValue(n, out var known)) { prevDate = known; havePrev = true; continue; }
                // the 7-day guard de-duplicates against the PREVIOUS resolved meeting (ticker maturities
                // and config dates describe the same meeting a day or two apart). With no previous
                // meeting it must not apply, or an imminent one is silently dropped: on 28-Jul-26 the
                // 29-Jul-26 FOMC was skipped and the run started at SEP-26, which also made
                // "usd jul fomc 5y" anchor on JUL-27.
                var fill = schedDates.FirstOrDefault(d => havePrev ? d > prevDate.AddDays(7) : d > DateTime.Today);
                if (fill == default) break;
                meetDates[n] = fill;
                // desk-confirmed grids (TrustConfigDates) publish on config dates — the
                // Riksbank carve-out where SKSF5A+ quote real prices with no date fields
                if (sched.TrustConfigDates) tickerDated.Add(n);
                prevDate = fill;
                havePrev = true;
            }
            return new MeetingDatesResult
            {
                Quotes = quotes, Dates = meetDates, FromTickers = tickerDates, TickerDated = tickerDated,
            };
        }

        /// <summary>Date of the meeting a month (and optional year) names, for anchoring a swap.
        /// Needs no mid, so it works when the board itself would come back empty.</summary>
        public DateTime MeetingDateFor(string runName, int month, int? year, out string label)
        {
            label = "";
            var sched = MeetingsStore.Schedules.FirstOrDefault(s =>
                s.Name.Equals(runName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"unknown central-bank run '{runName}'.");
            var dates = ResolveMeetingDates(sched).Dates.Values.OrderBy(d => d).ToList();
            var hit = dates.FirstOrDefault(d => d.Month == month && (year == null || d.Year == year));
            if (hit == default)
            {
                string mn = System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat
                    .GetAbbreviatedMonthName(month);
                throw new InvalidOperationException(
                    $"{sched.Name}: no {mn}{(year != null ? $"-{year % 100:00}" : "")} meeting in the next "
                    + $"{dates.Count}{(dates.Count > 0 ? $" ({dates[0]:MMM-yy}..{dates[^1]:MMM-yy})" : "")}.");
            }
            label = $"{sched.Name} {hit:MMM-yy}".ToUpperInvariant();
            return hit;
        }

        public MeetingRunResult MeetingRun(MeetingScheduleDef sched, int maxRows = 10)
        {
            if (sched.Kind.Equals("fra", StringComparison.OrdinalIgnoreCase)) return FraRun(sched, maxRows);
            lock (_gate)
            {
                var cfg = Configs.Get(sched.Ccy);
                var src = SourceFor(sched.Ccy);
                // the curve is only needed for its CALENDAR (roll-boundary business days) — the
                // hard-data rule (desk 2026-08-20) removed curve-implied mids from published rows
                CurveSet? curves = null;
                if (cfg.Ois != null)
                    try { curves = GetCurvesUnlocked(cfg, src); }
                    catch { /* ticker-only run */ }

                var res = new MeetingRunResult
                {
                    Name = sched.Name, Ccy = sched.Ccy.ToUpperInvariant(), Header = sched.Header,
                    RefName = sched.RefTicker ?? cfg.Ois?.OnFixingTicker ?? "",
                    DecisionTimeLondon = sched.DecisionTimeLondon,
                };
                if (!string.IsNullOrEmpty(res.RefName) && Snapshot.TryGetMid(res.RefName, out var fix))
                    res.RefPct = fix;
                // manual ref override: after a decision the fixing lags a day — Priced re-bases off this
                if (MeetingRefOverrides.TryGetValue(sched.Name, out var ovr))
                {
                    res.RefPct = ovr;
                    res.RefOverridden = true;
                }

                var resolved = ResolveMeetingDates(sched, maxRows);
                var quotes = resolved.Quotes;
                var meetDates = resolved.Dates;
                var tickerDated = resolved.TickerDated;
                bool tickerDates = resolved.FromTickers;

                if (meetDates.Count == 0)
                {
                    res.Warning = "no meeting tickers and schedule exhausted — update config\\meetings.json";
                    return res;
                }

                // TIME-GATED FRONT ROLL (desk 2026-08-20). The generics re-point at the decision,
                // but non-uniformly through the day — a run minutes after the statement can still
                // be entirely old-numbered, leaving the just-decided period on the front (live
                // RIKSBANK, 20-Aug-26 08:30). Once the calendar says the front period's decision
                // is ANNOUNCED (decision date + decisionTimeLondon), that period rolls off here
                // regardless of the feed. The drop is a uniform SHIFT: under old numbering
                // quotes[k] covers the period starting dates[k], so shifting both keeps every
                // row's date↔quote pairing intact — and quotes[0] becomes the just-decided
                // period's own OIS, exactly the rung the re-base below reads. When the feed HAS
                // re-pointed, the new front pairs only with the NEXT (unannounced) decision, so
                // the gate self-disarms and nothing double-rolls.
                // the decision gates ride the marks' own clock (see MarksAsOfLondon)
                var nowLdn = MarksAsOfLondon ?? Dates.DecisionClock.LondonNow();
                int gateShift = 0;
                {
                    while (meetDates.TryGetValue(gateShift + 1, out var f)
                           && Dates.DecisionClock.DecisionFor(sched.DecisionDates, f) is { } fd
                           && Dates.DecisionClock.Announced(fd, sched.DecisionTimeLondon, nowLdn))
                        gateShift++;
                    if (gateShift > 0)
                    {
                        quotes = quotes.Skip(gateShift).ToArray();
                        meetDates = meetDates.Where(kv => kv.Key > gateShift)
                            .ToDictionary(kv => kv.Key - gateShift, kv => kv.Value);
                        tickerDated = tickerDated.Where(i => i > gateShift).Select(i => i - gateShift).ToHashSet();
                        if (meetDates.Count == 0)
                        {
                            res.Warning = "every resolved meeting is already decided — top up config\\meetings.json";
                            return res;
                        }
                    }
                }
                if (meetDates.TryGetValue(1, out var next)) res.NextDecision = next;

                // ANNOUNCED-BUT-NOT-YET-EFFECTIVE compensation (RATESWEEKLY DIVERGENCE, desk
                // 2026-08-11 — the zero-touch replacement for the manual MeetingRefOverrides
                // case). Between a decision and the start of the period it decided, the o/n
                // fixing still prints the OLD rate — the ECB announces Thursday and the change
                // starts the next maintenance-period Wednesday — so priced-vs-fixing would
                // overstate every row by the full just-delivered change for up to a week. Inside
                // that window the base re-bases AUTOMATICALLY onto the just-decided period's own
                // OIS: the live run-down mid when the family quotes one, else that contract's
                // last close BEFORE the decision day (the pre-roll rung 1 — decision-day closes
                // are unanchorable). No policy-rate ticker, no rate calendar: the market print
                // carries the new rate, surprises included. Gated on the ANNOUNCEMENT (decision
                // date + decisionTimeLondon), the same clock as the front roll above, so the
                // re-base starts the moment the just-decided period leaves the front — priced-in
                // must never spend the rest of decision day measured against the stale fixing
                // (desk 2026-08-20; previously next-day). A manual override still wins.
                if (!res.RefOverridden && sched.DecisionDates.Count > 0)
                {
                    var today = nowLdn.Date;
                    DateTime? lastDec = null;
                    foreach (var d in sched.DecisionDates.OrderBy(d => d))
                        if (Dates.DecisionClock.Announced(d.Date, sched.DecisionTimeLondon, nowLdn))
                            lastDec = d.Date;
                    if (lastDec is { } dec)
                    {
                        DateTime? effStart = null;
                        foreach (var d in sched.Dates.OrderBy(d => d))
                            if (d.Date >= dec) { effStart = d.Date; break; }
                        if (effStart is { } eff)
                        {
                        // THE WINDOW (fix 2026-08-27, scenarios 54/55). It used to close the
                        // moment the period started — but the o/n fixings publish a day in
                        // ARREARS, so on the period's first day the printed fixing still refers
                        // to the day before it, i.e. the old rate. For FOMC and MPC, whose
                        // period starts ON the decision date, `today < eff` was empty and the
                        // re-base could never fire at all while EFFR/SONIA carried the pre-cut
                        // rate. The window now runs through eff + fixingLagDays business days;
                        // erring long is safe, because once the fixing catches up it equals the
                        // decided period's own OIS and the re-base becomes a no-op.
                        var windowEnd = eff;
                        for (int i = 0; i < Math.Max(0, sched.FixingLagDays); i++)
                        {
                            windowEnd = windowEnd.AddDays(1);
                            while (windowEnd.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                                windowEnd = windowEnd.AddDays(1);
                        }
                        if (today <= windowEnd && (eff - dec).TotalDays <= 10)
                        {
                            // THE POLICY-DELTA BASE (desk 2026-09-01, supersedes the stub-mid
                            // as the PRIMARY path: "use the most recent fixing ± the amount they
                            // move by, we don't want to use the stub mid"). The bank's own
                            // target ticker documents the delivered move: Δ = target now − the
                            // target's last close BEFORE the decision. Base = fixing print + Δ —
                            // no OIS basis, no intra-period expectations, surprises included
                            // because the target itself re-prints at the statement. Resets to
                            // the print alone the moment the fixing has GENUINELY moved (≥ half
                            // the delta, right sign — whenever that happens: the RBNZ OCR is its
                            // own fixing and re-prints the day the change takes effect, and a
                            // moved print must never carry the delta a second time); the
                            // calendar windowEnd above remains the hard stop either way.
                            bool polResolved = false;
                            // set when the target PRINTED Δ = 0 on the decision day itself: a
                            // HOLD must publish the plain fixing, never a re-based stub — "they
                            // didn't do anything so it isn't rebased, it's just the same fixing"
                            // (desk 2026-09-02, the BOC hold). The stub bridge below then only
                            // fires when the market says a MOVE happened that the target print
                            // has not caught up with (≥ 8bp — above corridor noise, under half
                            // of the smallest odd move, BOJ's 15bp).
                            double? holdBridgeFix = null;
                            if (!string.IsNullOrEmpty(sched.PolicyTicker)
                                && res.RefPct is { } fixPrint
                                && Snapshot.Get(sched.PolicyTicker)?.Mid is { } polNow
                                && History != null)
                            {
                                int span0 = (int)(today - dec).TotalDays + 15;
                                double? polPre = null, fixPre = null;
                                foreach (var pt in History.GetDaily(sched.PolicyTicker, span0))
                                    if (pt.Date.Date < dec) polPre = pt.Value;
                                if (polPre is { } pp)
                                {
                                    double delta = polNow - pp;
                                    bool kickedIn = false;
                                    if (Math.Abs(delta) > 1e-9
                                        && !string.IsNullOrEmpty(res.RefName))
                                    {
                                        foreach (var pt in History.GetDaily(res.RefName, span0))
                                            if (pt.Date.Date < dec) fixPre = pt.Value;
                                        if (fixPre is { } fp0
                                            && Math.Abs(fixPrint - fp0) >= Math.Abs(delta) / 2.0
                                            && Math.Sign(fixPrint - fp0) == Math.Sign(delta))
                                            kickedIn = true;
                                    }
                                    if (Math.Abs(delta) > 1e-9 && !kickedIn)
                                    {
                                        res.RefPct = fixPrint + delta;
                                        res.RefRebased = true;
                                        polResolved = true;
                                    }
                                    // kicked in, or a genuine hold on any later day: the print
                                    // IS the base — no re-base, no dagger
                                    else if (kickedIn || today != dec)
                                        polResolved = true;
                                    // Δ == 0 ON the decision day falls through GUARDED: the
                                    // target print can lag the statement by minutes, so a real
                                    // move still bridges via the stub — but a genuine hold
                                    // (stub ≈ fixing) publishes the plain print, no re-base
                                    else holdBridgeFix = fixPrint;
                                }
                            }
                            if (!polResolved)
                            {
                            // 1. the LIVE mark of the decided period, wherever the family quotes
                            //    it. Reading only index 0 worked on the statement day (the gate
                            //    shift puts it there) and stopped working the moment the feed
                            //    re-pointed; matching on the contract's own effective date works
                            //    in both states.
                            double? pending = null;
                            for (int k = 0; k < quotes.Length && pending is null; k++)
                                if (quotes[k]?.Effective is { } ek && ek.Date == eff)
                                    pending = quotes[k]?.Mid;

                            var pat0 = sched.Tickers.FirstOrDefault(t => t.Contains("{N}"));
                            bool stale = false;
                            if (pending is null && History != null && pat0 is { } pat)
                            {
                                int span = (int)(today - dec).TotalDays + 15;
                                string[] Spellings(int n) => MeetingSrc(sched).Length > 0
                                    ? new[] { MeetingTick(sched, pat, n), pat.Replace("{N}", n.ToString()) + " Curncy" }
                                    : new[] { MeetingTick(sched, pat, n) };

                                // 2. a CLOSE of the decided period, from a day on which Bloomberg's
                                //    own record proves that rung WAS this contract. Walking forward
                                //    from the decision is what lets the mark contain the surprise;
                                //    the old code walked backward and could not (scenario 61).
                                for (var d = today; d >= dec && pending is null; d = d.AddDays(-1))
                                    for (int n = 0; n <= 3 && pending is null; n++)
                                        foreach (var tk in Spellings(n))
                                        {
                                            if (History.EffectiveOn(tk, d) != eff) continue;
                                            foreach (var pt in History.GetDaily(tk, span))
                                                if (pt.Date.Date == d.Date) pending = pt.Value;
                                            if (pending is not null) break;
                                        }

                                // 3. last resort: that contract's last close BEFORE the decision —
                                //    the market's guess at what the meeting would deliver. It is a
                                //    real print of the right contract, so it beats the stale
                                //    fixing, but it CANNOT contain a surprise. Flagged, so the
                                //    surfaces stop claiming the base is current.
                                if (pending is null)
                                    foreach (var tk in Spellings(1))
                                    {
                                        foreach (var pt in History.GetDaily(tk, span))
                                            if (pt.Date.Date < dec) pending = pt.Value;
                                        if (pending is not null) { stale = true; break; }
                                    }
                            }
                            if (pending is { } pv
                                && (holdBridgeFix is not { } hb || Math.Abs(pv - hb) >= 0.08))
                            {
                                res.RefPct = pv;
                                res.RefRebased = true;
                                res.RefRebasedStale = stale;
                            }
                            }
                        }
                        }
                    }
                }

                Calendar? cal = null;
                if (curves != null && cfg.Ois != null)
                {
                    Settings.setEvaluationDate(curves.AsOf);
                    cal = curves.Cal;
                }

                // the feed re-pointed since the previous close ⇒ N's own PrevClose belongs to
                // the meeting N used to be, and yesterday's N is today's N+1 — difference
                // against THAT close instead. REDESIGNED 2026-08-26 (audit): the correction is
                // due only on the day the family actually RENUMBERS — the ANNOUNCEMENT for
                // decision-renumbering families (every family but SKSF; the old period-start
                // keying fired six days late on ECB and spuriously on its period start), the
                // period START for rollsAtPeriodStart — and NEVER while the announced-gate
                // shift above is active (the feed has not re-pointed yet, so the shifted
                // pairing makes the naive CoD correct; correcting on top double-shifts).
                bool rolled = RollCorrectionDue(sched, nowLdn, gateShift);
                // EVIDENCE ARM (audit 2026-08-31, scenario 120 — the untested quadrant of the
                // announcement-day 2x2): a feed that re-points BEFORE the statement leaves the
                // calendar arm false while every PrevClose already belongs to the next contract
                // along, so a flat tape prints a phantom full step down the strip. When the
                // store's own record of the previous business day disagrees with a rung's LIVE
                // SW_EFF_DT, the family provably renumbered since that close — Bloomberg's
                // fields, not inference. The arm stands down when the previous day is itself a
                // boundary or mixed-state day (its record cannot attribute that day's close,
                // the same rule the stitcher applies), and when there are no records at all.
                bool fieldsLead = sched.RollsAtPeriodStart
                    && quotes.Length > 0 && quotes[0] is { Mid: null, Effective: { } fle }
                    && fle.Date > nowLdn.Date;
                if (!rolled && gateShift == 0 && !fieldsLead && RecordedEffective(sched) is { } recEff)
                {
                    var prevBd0 = nowLdn.Date.AddDays(-1);
                    while (prevBd0.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                        prevBd0 = prevBd0.AddDays(-1);
                    var evMap = new MeetingRungMap(sched, meetDates.Values, recEff);
                    if (!evMap.IsBoundary(prevBd0) && !evMap.IsMixedState(prevBd0))
                        for (int n = 1; n <= 3 && !rolled && n < quotes.Length; n++)
                            if (quotes[n]?.Effective is { } liveEff
                                && recEff(n, prevBd0) is { } recD && recD.Date != liveEff.Date)
                                rolled = true;
                }
                // the gate shift is the same renumbering seen from the other side (feed not yet
                // re-pointed on the announcement): either way, these marks sit across a roll
                res.RenumberedToday = rolled || gateShift > 0;

                // Thin meeting OIS families misprint with a straight face: SKSF4A published a live
                // two-sided 1.387 between 1.848/2.086 neighbours (2026-08-03) — an impossible
                // inter-meeting rate. Interior ticker rows are judged against their QUOTED
                // NEIGHBOURS, not the curve (a year-turn pillar legitimately drags curve-implied
                // rates near December, which false-flagged good prints): rejected when >25bp from
                // the neighbour midpoint while the neighbours agree within 25bp of each other,
                // replaced by that midpoint and labelled. Edge rows are never judged — the front
                // meeting is the one that gaps for real.
                var tickMid = new double?[quotes.Length];
                for (int k = 0; k < quotes.Length; k++) tickMid[k] = quotes[k]?.Mid;
                // Y/E-turn flags per rung, computed up front: a turn row is a LEGITIMATE far-off
                // print, so the misprint guard must neither judge it nor use it as a neighbour
                // (audit 2026-08-26: SKSF4A — the guard's own motivating misprint — is
                // turn-ADJACENT, and a turn neighbour disabled the guard exactly there)
                bool TurnAt(int k) =>
                    sched.MarkTurnPeriods && meetDates.TryGetValue(k, out var td)
                    && (meetDates.TryGetValue(k + 1, out var te) ? td.Year != te.Year : td.Month == 12);
                // The guard REJECTS, it does not replace. Publishing the neighbour midpoint put a
                // number the market never quoted in front of clients (desk 2026-08-27: "we should
                // never have to invent mids"), and the hard-data rule already governs this case —
                // blank beats manufactured. Returns how far off the print sat, for the note.
                double? RejectedBy(int k)
                {
                    double m0 = tickMid[k]!.Value;
                    int lo = k - 1, hi = k + 1;
                    if (lo >= 1 && TurnAt(lo)) lo--;                       // skip a turn neighbour
                    if (hi < tickMid.Length && TurnAt(hi)) hi++;
                    if (lo >= 1 && lo >= k - 2 && tickMid[lo] is double a
                        && hi < tickMid.Length && hi <= k + 2 && tickMid[hi] is double b
                        && Math.Abs(a - b) * 100.0 < 25.0)
                    {
                        double mExp = (a + b) / 2.0;
                        if (Math.Abs(m0 - mExp) * 100.0 > 25.0) return (m0 - mExp) * 100.0;
                    }
                    return null;
                }

                double? prevPriced = null;
                var staleRungs = new List<(DateTime Row, double Age)>();
                // ages are OFFSET-CALIBRATED against this snapshot's own baseline (10th
                // percentile) — raw ages carry one systematic clock offset per machine
                // (desk 2026-08-26: nine liquid fronts all read "~120m" at 13:03 London)
                double ageBase = Snapshot.BaselineAgeMinutes() ?? 0;
                for (int n = 1; n <= maxRows; n++)
                {
                    if (!meetDates.TryGetValue(n, out var d0)) break;
                    // HARD-DATA RULE (desk 2026-08-20, final): a published row needs its DATE from
                    // the tickers' own fields and its PRICE from a real print. The run ends where
                    // Bloomberg's documentation ends — config dates still drive roll boundaries
                    // and decision gating internally, but never label a published row.
                    if (!tickerDated.Contains(n)) break;
                    // Y/E TURN periods are detected FIRST: a print far from its neighbours is what
                    // a year-end-spanning period legitimately looks like (SWESTR), so the interior
                    // misprint guard must stand down for it — the real print stays on the row and
                    // the renderers label it instead of publishing it.
                    bool haveEnd = meetDates.TryGetValue(n + 1, out var nx0);
                    var dEnd0 = haveEnd ? nx0 : d0.AddDays(42);
                    // unresolved end: only a DECEMBER start provably spans the year-end — the old
                    // 42-day guess could mask a real, publishable print (audit 2026-08-26)
                    bool turn0 = sched.MarkTurnPeriods
                        && (haveEnd ? d0.Year != dEnd0.Year : d0.Month == 12);
                    var q = quotes[n];
                    double mid;
                    string midSrc;
                    double? cod = null;
                    double? off = null;      // how far a rejected print sat from its neighbours
                    if (q?.Mid is double qm)
                    {
                        // feed-staleness watch (desk 2026-08-26): a published rung whose quote
                        // has not moved in >1h — measured against the snapshot's own freshest
                        // feed, so the terminal's timezone offset cancels
                        if (q.AgeMinutes is double age0 && age0 - ageBase > 60)
                            staleRungs.Add((d0, age0 - ageBase));
                        // the REAL print stays on the row (blend inputs, guards, the note); when
                        // the guard rejects it the row publishes a label instead of a number
                        off = turn0 ? null : RejectedBy(n);
                        mid = qm;
                        midSrc = off is { } ob
                            ? $"rejected (ticker {SignedBp(ob)}bp off its neighbours)" : "ticker";
                        cod = off != null ? null
                            : rolled
                                ? (n + 1 < quotes.Length && quotes[n + 1]?.PrevClose is double pc
                                    ? (qm - pc) * 100.0 : null)
                                : q.CoDBp;
                    }
                    else if (!string.IsNullOrEmpty(sched.FuturesPattern) && d0.Month % 3 == 0
                             && Snapshot.Get(sched.FuturesPattern.Replace("{MY}", FutMy(new DateTime(d0.Year, d0.Month, 1))))
                                 is { Mid: double fpx } fq)
                    {
                        // STIR future covering the post-meeting quarter (SNB ≈ the SARON strip): rate = 100 − price
                        mid = 100.0 - fpx;
                        midSrc = "future";
                        if (fq.PrevClose is double fprev) cod = (fprev - fpx) * 100.0;
                    }
                    else
                    {
                        // HARD-DATA RULE: no curve-implied mids on published rows — a curve is a
                        // model, not a print. The run ends at the last real quote.
                        break;
                    }
                    double? priced = res.RefPct.HasValue ? (mid - res.RefPct.Value) * 100.0 : null;
                    // Y/E TURN (desk 2026-08-20): a period straddling a year-end carries the turn
                    // dislocation in its average (SWESTR's is extreme), so it renders as a label
                    // and the step chain SKIPS it (desk 2026-08-20): the next row differences the
                    // last CLEAN Priced, giving the CUMULATIVE move priced across the masked
                    // meeting and its own. That number is clean by construction — neither
                    // neighbouring period contains the turn days, so the turn drag cancels; only
                    // the masked meeting's OWN step is unrecoverable from these contracts.
                    // A REJECTED print is masked by the same mechanism for the same reason: the
                    // row cannot publish a number, so it publishes a label and the step chain
                    // steps over it to the last clean Priced.
                    bool turn = turn0;
                    bool masked = turn || off != null;
                    res.Rows.Add(new MeetingRow
                    {
                        Date = d0, EndDate = haveEnd && tickerDated.Contains(n + 1) ? dEnd0 : null,
                        MidPct = mid, PricedBp = priced,
                        StepBp = !masked && priced.HasValue && prevPriced.HasValue ? priced - prevPriced : null,
                        CoDBp = cod, MidSource = midSrc, TurnPeriod = turn, Rejected = off != null,
                    });
                    if (!masked) prevPriced = priced;
                }

                if (staleRungs.Count > 0)
                {
                    var worst = staleRungs.OrderByDescending(x => x.Age).First();
                    var frontStale = res.Rows.Count > 0
                                     && staleRungs.Any(x => x.Row == res.Rows[0].Date);
                    res.StaleNotes.Add(
                        $"STALE: {sched.Name} — {staleRungs.Count} published rung(s) off a feed " +
                        $"quiet >1h ({(frontStale ? "INCLUDING THE FRONT, " : "")}worst " +
                        $"{worst.Row.ToString("dd-MMM-yy", System.Globalization.CultureInfo.InvariantCulture)} " +
                        $"at {worst.Age:0}m) — consider another contributor (SOURCES)");
                }

                res.DatesSource = tickerDates ? "tickers" : "schedule";
                if (!tickerDates && res.Rows.Count > 0)
                    res.Warning = "dates from config\\meetings.json";
                if (res.Rows.Count == 0)
                    res.Warning = "no future meetings resolved — check config\\meetings.json";
                // hard-data rule: every run ends at the last ticker-dated, ticker-priced row by
                // design, so a curve-build failure no longer changes what is published
                return res;
            }
        }

        /// <summary>Signed bp display that never renders "-+0.0" (.NET section-format quirk on tiny negatives).</summary>
        public static string SignedBp(double? v) =>
            v.HasValue ? (v.Value >= 0 ? "+" : "-") + Math.Abs(v.Value).ToString("0.0") : "";

        /// <summary>True when the roll-day CoD correction (mid(N) − PrevClose(N+1)) is due: the
        /// family renumbered intraday TODAY, so ticker N's own PX_CLOSE_1D belongs to the
        /// instrument N pointed at yesterday (the first session after the Jul-31 BOJ, JYSOMPM1
        /// — now SEP — printed 1.104 vs a 0.980 close that was the JUL period: +12.4bp of
        /// phantom CoD on every row). Renumbering happens at the ANNOUNCEMENT for every family
        /// but SKSF (store-close verified: EESF jumped between the 24-Jul and 27-Jul closes
        /// around the 23-Jul ECB decision, six days before the period start) and at the period
        /// START for rollsAtPeriodStart families (SKSF probed 2026-08-25). Never while the
        /// announced-gate shift is active: an un-re-pointed feed under the shifted pairing
        /// makes the naive CoD correct, and correcting on top would double-shift (audit
        /// 2026-08-26). The day after any roll, PrevClose is post-roll and naive is right.</summary>
        private static bool RollCorrectionDue(MeetingScheduleDef sched, DateTime nowLdn, int gateShift)
        {
            if (gateShift > 0) return false;
            if (sched.RollsAtPeriodStart)
                return sched.Dates.Any(d => d.Date == nowLdn.Date);
            foreach (var dec in MeetingCalendar.AnnouncementDates(sched))
                if (dec.Date == nowLdn.Date
                    && Dates.DecisionClock.Announced(dec.Date, sched.DecisionTimeLondon, nowLdn))
                    return true;
            return false;
        }

        /// <summary>The calendar-boundary flavour, kept for the IMM FRA strips: those generics
        /// DO re-point intraday on the boundary date itself (contract expiry needs no
        /// announcement), so "a boundary fell after the previous business day" is exactly
        /// right there — it was only the meeting families it mis-served.</summary>
        private static bool BoundarySincePrevClose(IEnumerable<DateTime> boundaries, Calendar? cal)
        {
            DateTime last = DateTime.MinValue;
            foreach (var b in boundaries)
                if (b.Date <= DateTime.Today && b.Date > last) last = b.Date;
            if (last == DateTime.MinValue) return false;
            DateTime prevBd;
            if (cal != null)
            {
                var q = cal.advance(new Date(DateTime.Today.Day, (Month)DateTime.Today.Month, DateTime.Today.Year),
                    -1, TimeUnit.Days);
                prevBd = new DateTime(q.year(), q.month(), q.Day);
            }
            else
            {
                prevBd = DateTime.Today.AddDays(-1);
                while (prevBd.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) prevBd = prevBd.AddDays(-1);
            }
            return last > prevBd;
        }

        /// <summary>IMM FRA run: 3M forwards at quarterly IMM dates, implied off the bootstrapped
        /// curve's 3M projection (SEK STIBOR / NOK NIBOR). Ref = the 3M fixing; CoD vs prev closes.</summary>
        private MeetingRunResult FraRun(MeetingScheduleDef sched, int maxRows)
        {
            lock (_gate)
            {
                var cfg = Configs.Get(sched.Ccy);
                var src = SourceFor(sched.Ccy);
                var curves = GetCurvesUnlocked(cfg, src);
                var res = new MeetingRunResult
                {
                    Name = sched.Name, Ccy = sched.Ccy.ToUpperInvariant(), Header = sched.Header,
                    RefName = sched.RefTicker ?? "",
                };
                if (!string.IsNullOrEmpty(res.RefName) && Snapshot.TryGetMid(res.RefName, out var fix))
                    res.RefPct = fix;
                if (MeetingRefOverrides.TryGetValue(sched.Name, out var ovr))
                {
                    res.RefPct = ovr;
                    res.RefOverridden = true;
                }

                Settings.setEvaluationDate(curves.AsOf);
                var cal = curves.Cal;
                var dcc = new Actual360();
                // 3M band where its quotes cover the date, else the full-term curve
                YieldTermStructure ProjAt(double years)
                {
                    try
                    {
                        var (h, _) = curves.ProjectionFor("3M", years);
                        if (!h.empty()) return h.currentLink();
                    }
                    catch { /* fall through */ }
                    return curves.Irs ?? curves.Ois
                        ?? throw new InvalidOperationException($"{sched.Ccy}: no curve for FRA run");
                }

                // quarterly IMM dates (3rd Wednesday of Mar/Jun/Sep/Dec)
                static DateTime Imm(int y, int m)
                {
                    var d = new DateTime(y, m, 15);
                    while (d.DayOfWeek != DayOfWeek.Wednesday) d = d.AddDays(1);
                    return d;
                }
                var imms = new List<DateTime>();
                var q = DateTime.Today;
                for (int i = 0; imms.Count < maxRows && i < 40; i++)
                {
                    int mm = ((q.Month - 1) / 3) * 3 + 3;
                    var candidate = Imm(q.Year, mm);
                    if (candidate > DateTime.Today && !imms.Contains(candidate)) imms.Add(candidate);
                    q = new DateTime(q.Year, mm, 1).AddMonths(3);
                }

                Date Q(DateTime d) => cal.adjust(new Date(d.Day, (Month)d.Month, d.Year), BusinessDayConvention.Following);
                double FwdAt(CurveSet set, DateTime a)
                {
                    double years = (a.AddMonths(3) - DateTime.Today).TotalDays / 365.25;
                    YieldTermStructure c;
                    if (ReferenceEquals(set, curves)) c = ProjAt(years);
                    else
                    {
                        try
                        {
                            var (h, _) = set.ProjectionFor("3M", years);
                            c = !h.empty() ? h.currentLink() : set.Irs ?? set.Ois!;
                        }
                        catch { c = set.Irs ?? set.Ois!; }
                    }
                    return c.forwardRate(Q(a), Q(a.AddMonths(3)), dcc, Compounding.Simple, Frequency.Annual).value() * 100.0;
                }

                var prev = GetPrevCloseCurvesUnlocked(cfg, src);

                // REAL IMM FRA quotes when the sched lists the contracts (NKF30001../SKF30001..),
                // matched to each row BY MATURITY — the numbers are rolling generics, and the rule
                // stands: use a ticker only when its own MATURITY equals the period being assigned.
                // Without this NOK's rows were curve-implied off a curve with no 3M band, i.e. 3M
                // rows read from the 6M NIBOR curve, ~a 3s6s too high against the NIBOR3M ref.
                var strip = sched.Tickers
                    .Select(t => Snapshot.Get(MeetingTick(sched, t, 0)))
                    .Where(q2 => q2?.Maturity != null)
                    .OrderBy(q2 => q2!.Maturity!.Value).ToList();
                // contracts expire intraday on their IMM start — same roll discipline as meetings
                var pastImms = new List<DateTime>();
                for (var pq0 = DateTime.Today.AddMonths(-4); pq0 <= DateTime.Today; pq0 = pq0.AddDays(1))
                    if (pq0.Month % 3 == 0 && pq0 == Imm(pq0.Year, pq0.Month)) pastImms.Add(pq0);
                bool rolled = strip.Count > 0 && BoundarySincePrevClose(pastImms, cal);

                res.NextDecision = null;
                double? prevPriced = null;
                foreach (var d0 in imms)
                {
                    double mid;
                    string midSrc;
                    double? cod = null;
                    var end = d0.AddMonths(3);
                    int si = strip.FindIndex(q2 => Math.Abs((q2!.Maturity!.Value - end).TotalDays) <= 12);
                    if (si >= 0 && strip[si]!.Mid is double sm)
                    {
                        mid = sm;
                        midSrc = "ticker";
                        // on the roll day, yesterday's holder of this maturity was the NEXT generic
                        cod = rolled
                            ? (si + 1 < strip.Count && strip[si + 1]!.PrevClose is double pc
                                ? (sm - pc) * 100.0 : null)
                            : strip[si]!.CoDBp;
                    }
                    else if (strip.Count > 0)
                    {
                        // the quoted strip has run out — curve rows here would silently change basis
                        // (NOK has no 3M band to imply from), so say so and stop
                        res.Warning = $"run ends at the quoted {sched.Ccy} 3M IMM strip ({res.Rows.Count} contracts)";
                        break;
                    }
                    else
                    {
                        // no contracts configured: curve-implied as before
                        try { mid = FwdAt(curves, d0); }
                        catch { break; }
                        midSrc = "curve";
                        if (prev != null)
                        {
                            try { cod = (mid - FwdAt(prev, d0)) * 100.0; } catch { /* gap */ }
                        }
                    }
                    double? priced = res.RefPct.HasValue ? (mid - res.RefPct.Value) * 100.0 : null;
                    res.Rows.Add(new MeetingRow
                    {
                        Date = d0, MidPct = mid, PricedBp = priced,
                        StepBp = priced.HasValue && prevPriced.HasValue ? priced - prevPriced : null,
                        CoDBp = cod, MidSource = midSrc,
                    });
                    prevPriced = priced;
                }
                res.DatesSource = "imm";
                return res;
            }
        }

        /// <summary>Meeting-dated structure ("jul fomc" / "jul sep boe" / "jul sep dec ecb"):
        /// legs are meeting-period rates from the run; 2/3 legs quote the spread/fly in bp.</summary>
        internal Analytics.InstrumentResult AnalyzeMeeting(Query.ParsedQuery pq)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var sched = MeetingsStore.Schedules.First(s => s.Name == pq.MeetingRun);
            var run = MeetingRun(sched, 12);
            if (run.Rows.Count == 0)
                throw new InvalidOperationException($"{sched.Name}: no meeting data ({run.Warning}).");

            var chosen = new List<MeetingRow>();
            foreach (var (month, year) in pq.MeetingMonths!)
            {
                var row = run.Rows.FirstOrDefault(r => r.Date.Month == month && (year == null || r.Date.Year == year));
                if (row == null)
                    throw new InvalidOperationException(
                        $"{sched.Name}: no {System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(month)}" +
                        $"{(year != null ? $"-{year % 100:00}" : "")} meeting in the next {run.Rows.Count} ({run.Rows[0].Date:MMM-yy}..{run.Rows[^1].Date:MMM-yy}).");
                if (chosen.Any(c => c.Date == row.Date))
                    throw new InvalidOperationException($"{sched.Name}: duplicate meeting {row.Date:dd-MMM-yy}.");
                chosen.Add(row);
            }
            chosen.Sort((a, b) => a.Date.CompareTo(b.Date));

            int n = chosen.Count;
            double[] w = n switch { 1 => new[] { 1.0 }, 2 => new[] { -1.0, 1.0 }, _ => new[] { -1.0, 2.0, -1.0 } };
            double level = n == 1 ? chosen[0].MidPct
                : chosen.Select((r, i) => w[i] * r.MidPct).Sum() * 100.0;

            var res = new Analytics.InstrumentResult
            {
                Query = pq.Raw,
                Label = $"{sched.Name} {string.Join(" / ", chosen.Select(r => r.Date.ToString("MMM-yy").ToUpperInvariant()))}"
                        + (n == 2 ? " meeting spread" : n == 3 ? " meeting fly" : " meeting"),
                Ccy = run.Ccy, Kind = n == 1 ? "Meeting" : n == 2 ? "Meeting spread" : "Meeting fly",
                Unit = n == 1 ? "%" : "bp",
                Source = "meeting OIS" + (MeetingSrc(sched).Length > 0 ? $" ({MeetingSrc(sched)})" : ""),
                // headline stays clean/screenshottable — only the ref, no run-header echo
                ConventionSummary = run.RefPct.HasValue
                    ? $"ref {run.RefPct.Value:0.000} ({(run.RefOverridden ? "manual" : run.RefName)})"
                    : "",
                Mid = level, ParRatePct = level,
            };
            // meeting-period OIS risk: P&L per 1bp = notional × yearFrac(meeting → next meeting,
            // index dcc ~ ACT/360) × 1e-4 — the same dates shown as Effective/Maturity on the legs.
            // The period's payoff settles at the period END, so P&L per bp is discounted to the
            // payment date off the OIS curve; undiscounted DV01 overstates blotter NPV by ~DF.
            double legNotional = pq.Notional > 0 ? pq.Notional : 10_000_000;
            string? meetingFxNote = null;
            double Df(DateTime periodEnd)
            {
                lock (_gate)
                {
                    var cfg = Configs.Get(sched.Ccy);
                    if (cfg.Ois != null)
                        try
                        {
                            var set = GetCurvesUnlocked(cfg, SourceFor(sched.Ccy));
                            Settings.setEvaluationDate(set.AsOf);
                            if (set.Ois is { } ois)
                                return ois.discount(new Date(periodEnd.Day, (Month)periodEnd.Month, periodEnd.Year));
                        }
                        catch { /* simple-rate fallback below */ }
                }
                // no OIS curve (none configured, or build failed): 1/(1 + ref·t), t ACT/360 to pay date
                double t = Math.Max(0.0, (periodEnd - DateTime.Today).TotalDays) / 360.0;
                return run.RefPct is double rp ? 1.0 / (1.0 + rp / 100.0 * t) : 1.0;
            }
            // path F: size off the period's OWN density rather than a flat notional, so an unsized
            // meeting query carries the same desk risk as an unsized swap and "dv01 50k" works here
            // too. Density per 1mm = accrual × DF, i.e. exactly the dv01 formula below at 1mm.
            double DensityFor(MeetingRow rowAt)
            {
                var nxt = run.Rows.FirstOrDefault(r => r.Date > rowAt.Date);
                return nxt != null
                    ? 1_000_000.0 * (nxt.Date - rowAt.Date).TotalDays / 360.0 * 1e-4 * Df(nxt.Date)
                    : 0.0;
            }
            if (pq.LegNotionals is { Count: > 0 } exactNot)
            {
                legNotional = exactNot[0];   // blotter's exact channel — never resized or rounded
            }
            else if ((pq.LegDv01s is { Count: > 0 } || pq.Dv01Target.HasValue)
                     && chosen.Select(DensityFor).FirstOrDefault(d => d > 0) is > 0 and var dens)
            {
                double want = pq.LegDv01s is { Count: > 0 } pl ? pl[0] : pq.Dv01Target!.Value;
                try
                {
                    double fx = FxRiskFactor(pq.Dv01Ccy, run.Ccy);
                    if (!pq.Dv01Ccy.Equals(run.Ccy, StringComparison.OrdinalIgnoreCase))
                        meetingFxNote = $"dv01 input in {pq.Dv01Ccy} × {fx:0.####} → {run.Ccy}/bp";
                    legNotional = Risk.RiskSizer.Resolve(dens, explicitDv01: want * fx).Notional;
                }
                catch (Exception ex)
                {
                    meetingFxNote = $"dv01 target not applied — {ex.Message}; showing the flat notional.";
                }
            }

            for (int i = 0; i < n; i++)
            {
                var next = run.Rows.FirstOrDefault(r => r.Date > chosen[i].Date);
                double dv01 = next != null
                    ? legNotional * (next.Date - chosen[i].Date).TotalDays / 360.0 * 1e-4 * Df(next.Date) : 0.0;
                res.Legs.Add(new Analytics.LegResult
                {
                    Label = chosen[i].Date.ToString("dd-MMM-yy"),
                    Weight = w[i],
                    Effective = new Date(chosen[i].Date.Day, (Month)chosen[i].Date.Month, chosen[i].Date.Year),
                    Maturity = next != null
                        ? new Date(next.Date.Day, (Month)next.Date.Month, next.Date.Year) : new Date(),
                    RatePct = chosen[i].MidPct,
                    Notional = legNotional,
                    Dv01 = dv01,
                    DensityPerMm = dv01 > 0 ? dv01 / (legNotional / 1_000_000.0) : 0.0,
                    HistoryNote = chosen[i].MidSource,
                });
            }
            // structure dv01 basis so NET DV01/tiles and +watch sizing see risk — per unit weight,
            // meeting spreads/flies trade ±1 per leg; legs with an unknown period carry no risk
            var riskLegs = res.Legs.Where(l => l.Dv01 > 0).ToList();
            if (riskLegs.Count > 0) res.StructDv01 = riskLegs.Average(l => l.Dv01);
            if (n == 1 && res.Legs[0].Dv01 > 0) res.Dv01 = res.Legs[0].Dv01;
            if (riskLegs.Count > 0) res.Notes.Add("meeting P&L discounted to period end");
            if (meetingFxNote != null) res.Notes.Add(meetingFxNote);
            if (n == 1 && run.RefPct.HasValue)
                res.Notes.Add($"priced {SignedBp((level - run.RefPct.Value) * 100.0)} bp vs {run.RefName}" +
                              (run.RefOverridden ? " (ref OVERRIDDEN)" : ""));

            // history: stitch the generic ticker series so each history date uses the ticker index
            // that POINTED AT this meeting on that date — indices shift down after every decision
            if (!pq.SkipHistory)
            try
            {
                var stitched = MeetingSeriesBuilder(sched, run.Rows.Select(r => r.Date));
                var legSeries = chosen.Select(c => stitched(c.Date)).ToList();
                if (legSeries.All(s2 => s2.Count >= 10))
                {
                    var combined = HistoryFilter.Despike(
                        CombineSeries(legSeries, w, scaleToBp: n > 1), window: 7, k: 4,
                        madFloorPct: n > 1 ? 0.5 : 0.005, passes: 2);
                    if (combined.Count >= 10)
                    {
                        // stats on the full stitched window; the chart shows the lookback slice
                        res.History = SliceLookback(combined);
                        ApplyMidOverride(pq, res);
                        res.Stats = Analytics.SeriesStats.Compute(combined, liveLast: res.Mid ?? level,
                            changeScale: n > 1 ? 1.0 : 100.0,
                            basisRef: res.MidTrue ?? res.Mid ?? level);
                        if (res.Stats?.SuppressReason is string basisWhy)
                            res.Notes.Add($"level stats withheld: {basisWhy}.");
                        // exact Δ 1d from the run rows (live mid vs prev close) — the stitched
                        // series' last point can predate today, which skews a history-based 1d
                        if (chosen.All(c => c.CoDBp.HasValue))
                            res.Stats.Chg1d = chosen.Select((c, i) => w[i] * c.CoDBp!.Value).Sum() + OvrShiftBp(res);
                        res.Notes.Add("history stitched across ticker rolls at decision dates (past dates from config).");
                    }
                }
            }
            catch { /* history is best-effort for meeting structures */ }
            ApplyMidOverride(pq, res); // no-op when already applied ahead of the stats pass
            if (res.History.Count == 0)
                res.Notes.Add("no stitched history available for this structure.");
            res.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return res;
        }

        /// <summary>Meeting-CONSTANT history for any meeting in a run: stitches the generic ticker
        /// series so each history date reads the ticker index that POINTED AT that meeting on that
        /// date — indices shift down after every decision, and a naive single-ticker BDH would splice
        /// two different meetings at each roll. One batched prefetch per builder; call the returned
        /// func per meeting. Used by the pricer's meeting charts AND the weekly report's 1w/1m
        /// changes, so both stay roll-safe by construction.</summary>
        /// <summary>Reads the strip's own price history for renumbering, when a provider offers
        /// it. Set by the app/CLI to Weekly.Core's RungShiftScan; null in Core-only tests, where
        /// the calendar fallback governs exactly as before.</summary>
        public Func<MeetingScheduleDef, Func<int, string>, DateTime, Func<int, double?>,
            IReadOnlyList<(DateTime Day, int Shift, bool Confirmed)>>? ObservedShifts { get; set; }

        /// <summary>Bloomberg's own per-day record of what each rung pointed at — the
        /// MeetingRungMap's evidence arm, built ONCE here so every consumer gets the same
        /// source-aware, cached lookup. Five call sites used to construct unarmed maps while
        /// only the stitcher passed records, and the doc-committed SKSF fixes (−10.6 vs −0.6,
        /// −8.7 vs +4.6) never reached the weekly Δ1d fallback (audit 2026-08-31, finding 2).
        /// Null when there is no history provider or no rolling-generic pattern.</summary>
        public Func<int, DateTime, DateTime?>? RecordedEffective(MeetingScheduleDef sched)
        {
            var patRec = sched.Tickers.FirstOrDefault(t => t.Contains("{N}"));
            if (History == null || patRec == null) return null;
            var recCache = new Dictionary<(int, DateTime), DateTime?>();
            var leadCache = new Dictionary<DateTime, bool>();
            DateTime? Raw(int n0, DateTime d0)
            {
                if (recCache.TryGetValue((n0, d0), out var hit)) return hit;
                DateTime? v = History.EffectiveOn(MeetingTick(sched, patRec, n0), d0);
                if (v is null && MeetingSrc(sched).Length > 0)
                    v = History.EffectiveOn(patRec.Replace("{N}", n0.ToString()) + " Curncy", d0);
                recCache[(n0, d0)] = v;
                return v;
            }
            // records taken on a FIELDS-LEAD day (see ResolveMeetingDates) carry the same
            // one-out lie the live fields did — rung 1's record skipping an imminent start is
            // the day-level signature, and the honest identity of rung n's PRICE that day is
            // rung n−1's recorded field
            bool Lead(DateTime d0)
            {
                if (!sched.RollsAtPeriodStart) return false;
                if (leadCache.TryGetValue(d0.Date, out var l)) return l;
                var s = sched.Dates.Where(x => x.Date >= d0.Date)
                    .OrderBy(x => x).Cast<DateTime?>().FirstOrDefault();
                bool lead = s is { } s0 && Raw(1, d0) is { } r1 && r1.Date > s0.Date;
                leadCache[d0.Date] = lead;
                return lead;
            }
            return (n, d) => Lead(d) ? (n <= 1 ? Raw(0, d) : Raw(n - 1, d)) : Raw(n, d);
        }

        internal Func<DateTime, IReadOnlyList<HistPoint>> MeetingSeriesBuilder(
            MeetingScheduleDef sched, IEnumerable<DateTime> runDates, List<string>? notes = null)
        {
            // warm every ticker index the stitching can touch in one batched BDH round-trip
            try
            {
                History?.Prefetch(sched.Tickers.Where(p => p.Contains("{N}")).SelectMany(p =>
                    Enumerable.Range(1, 13).Select(i => p.Replace("{N}", i.ToString()) + " Curncy")), 1825);
            }
            catch { /* per-ticker fallback */ }
            // ONE boundary derivation for every consumer — MeetingRungMap (fresh-eyes review
            // 2026-08-26): the period grid + settled history + this run's own ticker-derived
            // dates, announcements folded in for decision-renumbering families (the config's
            // decision list is FUTURE-only, so the just-settled ECB 23-Jul-26 announcement was
            // previously never a boundary and up to a week of closes after every recent decision
            // stitched to the wrong contract — including the Δ1m anchors in that window),
            // starts kept for SKSF, 14-day cluster keeping the earliest.
            // Bloomberg's own per-day record of what each rung pointed at, when the store has
            // been recording it (every daily run stores it). Evidence beats the boundary count.
            var patRec = sched.Tickers.FirstOrDefault(t => t.Contains("{N}"));
            var recorded = RecordedEffective(sched);
            var rungMap = new MeetingRungMap(sched, runDates, recorded);

            // THE STRIP'S OWN ACCOUNT OF ITS RENUMBERING (2026-08-27). Prices are seeded 45 days
            // back on a machine's first run, so this reaches the whole window - unlike the
            // recorded SW_EFF_DT above, which only exists from the day recording began. It is the
            // only source that can see an UNSCHEDULED meeting being inserted, because that is not
            // in the calendar retrospectively and the field history cannot be fetched.
            IReadOnlyList<(DateTime Day, int Shift, bool Confirmed)>? shifts = null;
            if (ObservedShifts != null && patRec != null)
                try
                {
                    shifts = ObservedShifts(sched, n => MeetingTick(sched, patRec, n),
                        DateTime.Today.AddDays(-70),
                        n => Snapshot.Get(MeetingTick(sched, patRec, n))?.Mid
                             ?? (MeetingSrc(sched).Length > 0
                                 ? Snapshot.Get(patRec.Replace("{N}", n.ToString()) + " Curncy")?.Mid
                                 : null));
                }
                catch { /* the calendar still governs */ }

            // Which rung each published contract sits on RIGHT NOW, from the tickers' own live
            // SW_EFF_DT - no calendar, no store, no inference. This is the anchor the observed
            // shifts are measured from.
            int? RungToday(DateTime meeting)
            {
                if (patRec == null) return null;
                for (int n = 0; n <= 13; n++)
                {
                    var q = Snapshot.Get(MeetingTick(sched, patRec, n));
                    if (q?.Effective?.Date == meeting.Date) return n;
                    if (MeetingSrc(sched).Length > 0
                        && Snapshot.Get(patRec.Replace("{N}", n.ToString()) + " Curncy")?.Effective?.Date
                           == meeting.Date) return n;
                }
                return null;
            }

            // total renumbering between a past day and now; null when any day in between could
            // not be judged (a broken chain is a guess, and this must never guess)
            int? ShiftSince(DateTime day)
            {
                if (shifts == null) return null;
                int t = 0;
                foreach (var (d, sh, ok) in shifts)
                {
                    if (d.Date <= day.Date) continue;
                    if (!ok) return null;
                    t += sh;
                }
                return t;
            }
            var noted = new HashSet<string>();
            var allMeet = rungMap.Boundaries.ToList();
            // the run's own front row — the contract the misprint guard must never judge
            DateTime? frontMeeting = runDates.Select(d => (DateTime?)d.Date).FirstOrDefault();
            // DESK CONVENTION (2026-08-06): history values are the daily 4:30pm-LONDON snaps, not
            // closes — the desk's incumbent sheet snaps then, and the changes must reconcile. The
            // snaps are also STRUCTURALLY cleaner at roll boundaries: at 16:30 on a decision day
            // only generic #1 has re-pointed (probed GPSF 30-Jul-26: 2A/3A/4A still old-numbered
            // carrying the post-decision prices) and the decision-day mapping reads tickers 2+, so
            // a snapped boundary day stitches EXACTLY under old numbering. Closes stay as fallback
            // for days without bars — those keep the exclusive-boundary rule (mixed-state closes).
            // SNAP-TIME CUTOVER (desk 2026-08-25): the official snap moved 16:30 → 16:15
            // London. Existing history is NOT rewritten — days up to the cutover keep their
            // 16:30 snaps, days after ride 16:15. The old-time pull dies naturally once the
            // whole snap window post-dates the cutover.
            var snapAtOld = new TimeSpan(16, 30, 0);
            var snapAtNew = new TimeSpan(16, 15, 0);
            var snapCutover = SnapTimeCutover;
            const int snapDays = 50; // covers the 1m lookback; charts keep closes further back
            var famCache = new Dictionary<int, (IReadOnlyList<HistPoint> pts, HashSet<DateTime> snapped)?>();
            (IReadOnlyList<HistPoint> pts, HashSet<DateTime> snapped)? FamilyHist(int idx)
            {
                if (idx < 0) return null;
                if (famCache.TryGetValue(idx, out var cached)) return cached;
                (IReadOnlyList<HistPoint>, HashSet<DateTime>)? result = null;
                foreach (var pat in sched.Tickers)
                {
                    if (!pat.Contains("{N}")) continue; // explicit FRA-strip securities don't renumber this way
                    // the CHANGE ANCHORS must ride the same contributor the mids do (desk
                    // 2026-08-25: the composite prints off NABZ episodically — 1.7bp on ADSF2A
                    // 24-Jul — and anchoring composite closes under contributor mids poisoned
                    // Δ1w/Δ1m). Source series first, composite closes as the fallback.
                    var tkr = MeetingTick(sched, pat, idx);
                    var cand = Hist(tkr, full: true);
                    if (cand.Count == 0 && MeetingSrc(sched).Length > 0)
                    {
                        tkr = pat.Replace("{N}", idx.ToString()) + " Curncy";
                        cand = Hist(tkr, full: true);
                    }
                    if (cand.Count == 0) continue;
                    // the CUTOVER DAY itself is a 16:15 day — SnapDiscipline pinned its published
                    // marks at 16:15, so history must anchor it there too (audit 2026-08-26: the
                    // old &gt;/&lt;= split left every Δ1d spanning 25-Aug carrying 15 minutes of tape)
                    var snaps = new List<HistPoint>();
                    if (DateTime.Today.AddDays(-snapDays) < snapCutover)
                        snaps.AddRange((History?.GetLondonSnaps(tkr, snapDays, snapAtOld)
                            ?? Array.Empty<HistPoint>()).Where(sp => sp.Date.Date < snapCutover));
                    snaps.AddRange((History?.GetLondonSnaps(tkr, snapDays, snapAtNew)
                        ?? Array.Empty<HistPoint>()).Where(sp => sp.Date.Date >= snapCutover));
                    if (snaps.Count == 0) { result = (cand, new HashSet<DateTime>()); break; }
                    var merged = cand.ToDictionary(p => p.Date, p => p.Value);
                    var snapped = new HashSet<DateTime>();
                    foreach (var sp in snaps) { merged[sp.Date] = sp.Value; snapped.Add(sp.Date); }
                    result = (merged.OrderBy(kv => kv.Key)
                        .Select(kv => new HistPoint(kv.Key, kv.Value)).ToList(), snapped);
                    break;
                }
                famCache[idx] = result;
                return result;
            }
            return meeting =>
            {
                var upTo = allMeet.Where(m => m <= meeting).ToList();
                var pts = new List<HistPoint>();
                for (int i = upTo.Count - 2; i >= 0; i--)
                {
                    int idx = upTo.Count - 1 - i; // in (upTo[i], upTo[i+1]] this meeting is the idx-th next
                    if (idx > 13) break;
                    var fam = FamilyHist(idx);
                    if (fam == null) continue;
                    var (h, snapped) = fam.Value;
                    var lo = upTo[i];
                    var hi = upTo[i + 1];
                    // boundary-day rule: a decision-day CLOSE is unanchorable (raw GPSF closes on the
                    // 30-Jul-26 MPC show the family re-pointing NON-uniformly by the close — 1A rolled,
                    // 2A not, 3A/4A alternating) so close-sourced points at hi are EXCLUDED and the
                    // lookback anchors a day earlier. A 16:30-London SNAP at hi is uniformly OLD
                    // numbered (only #1 re-points intraday, and this mapping starts at #2), so snapped
                    // boundary days are included — post-decision prices under the old index, exactly
                    // the desk sheet's baseline.
                    // mixed-state days (announcement→start, per-rung renumber in flight) source
                    // NOTHING — closes or snaps (desk 2026-08-26, the ECB +24.3bp Δ1m)
                    // MIXED-STATE IS A PRECAUTION, NOT A VERDICT (2026-08-27). Those days are
                    // excluded because the family renumbers through them and nothing says which
                    // rung is which — but when the store RECORDED what each rung pointed at that
                    // day, we are not guessing and the day is perfectly usable. Evidence lifts
                    // the precaution; the re-point below then puts the point on the right rung.
                    // THE BOUNDARY DAY BELONGS TO WHICHEVER RUNG ACTUALLY HELD THE CONTRACT
                    // (fix 2026-08-27, live SKSF). `lo` is excluded because on the boundary day
                    // itself this contract sat one rung HIGHER, and that rung's window covers it.
                    // That reasoning fails when the higher rung does not exist: SKSF quotes six
                    // rungs, so on the 26-Aug roll the 12-May-27 contract's pre-roll rung was
                    // SKSF7A - nothing - and the day fell down the gap between the two windows.
                    // The board published the row and then blanked all three change columns, on
                    // a day when the value was sitting on SKSF6A in plain sight (2.236, matching
                    // Bloomberg's own +0.0 and the desk sheet's +0.1).
                    //
                    // Where the store RECORDED what the rungs pointed at that day we are not
                    // guessing about the boundary at all, so admit the day and let the re-point
                    // below place it - it reads RungFor, keeps the point only if this really is
                    // the rung that held the contract, and takes the value from that rung's own
                    // series either way. With no record the old exclusion stands untouched.
                    var win = h.Where(p => (p.Date > lo || (p.Date == lo && rungMap.HasRecordFor(p.Date)))
                        && (!rungMap.IsMixedState(p.Date) || rungMap.HasRecordFor(p.Date))
                        && (p.Date < hi || (p.Date == hi && snapped.Contains(p.Date)))).ToList();
                    if (win.Count == 0) continue;

                    // RE-POINT AGAINST THE RECORD (fix 2026-08-27, scenario 21). The window's idx
                    // comes from counting boundaries as they stand TODAY. A meeting the calendar
                    // gained after the fact — an unscheduled decision — re-numbers every day
                    // before it under that count, while the data was published under the
                    // numbering the market actually had. Where the store recorded what each rung
                    // pointed at, use that instead: read the rung that WAS this contract, and
                    // drop the day when none was. Nothing is invented either way.
                    // EVIDENCE OVER INFERENCE, in order. `idx` above is the calendar's opinion.
                    // Two sources can overrule it, and both are the tickers' own data:
                    //   1. the recorded SW_EFF_DT for that day - exact, but only since 26-Aug-26;
                    //   2. the renumbering read off the strip's own prices - reaches the whole
                    //      seeded window, and is the ONLY thing that sees an unscheduled meeting
                    //      inserted at the front.
                    // Neither ever invents a number: they pick which rung to read, and the value
                    // is that rung's own close. When neither can say, the calendar stands.
                    if (recorded != null || shifts != null)
                    {
                        int? rungNow = RungToday(meeting);
                        var fixedWin = new List<HistPoint>(win.Count);
                        foreach (var p in win)
                        {
                            int? trueIdx = null;
                            string source = "";

                            if (rungMap.HasRecordFor(p.Date))
                            {
                                if (recorded!(idx, p.Date)?.Date == meeting.Date) { fixedWin.Add(p); continue; }
                                trueIdx = rungMap.RungFor(meeting, p.Date);
                                source = "the tickers' own recorded dates";
                            }
                            else if (rungNow is { } r0 && ShiftSince(p.Date) is { } sh)
                            {
                                trueIdx = r0 + sh;
                                source = "the strip's own prices";
                            }

                            if (trueIdx is not { } ti) { fixedWin.Add(p); continue; }
                            if (ti == idx) { fixedWin.Add(p); continue; }
                            if (ti < 1 || FamilyHist(ti) is not { } alt) continue;   // no rung held it
                            var hit = alt.pts.FirstOrDefault(x => x.Date.Date == p.Date.Date);
                            if (hit == default) continue;
                            fixedWin.Add(hit);

                            // say it once per run, in words a reader can act on
                            if (notes != null && noted.Add(sched.Name))
                                notes.Add($"{sched.Name}: on {p.Date:dd-MMM-yy} the tickers were not " +
                                          $"numbered the way the meeting calendar implies, so the change " +
                                          $"columns follow {source} instead. This is normal after an " +
                                          "unscheduled meeting; if there has not been one, the calendar " +
                                          "in config/meetings.json needs checking.");
                        }
                        win = fixedWin;
                        if (win.Count == 0) continue;
                    }

                    // Same neighbour guard as the live rows, with the live rows' TWO exemptions,
                    // which this arm was missing (verified 2026-08-27, scenario 62):
                    //   · THE FRONT CONTRACT IS NEVER JUDGED. GuardedMid refuses the front row
                    //     because "the front meeting is the one that gaps for real" — and a
                    //     decision day is exactly when it gaps. Keying the exemption on the
                    //     generic index instead of the row meant the front's own closes WERE
                    //     rewritten (its recent history is read at idx 2 on a decision day), so
                    //     the board published a real print and a change measured from an
                    //     invented one.
                    //   · a TURN period is a legitimate far-off print — never judged, never a
                    //     neighbour (the live guard's TurnAt stand-down).
                    // And the guard WITHHOLDS rather than substitutes: the point is dropped from
                    // the window so the lookback walks back to the last clean day, instead of
                    // anchoring on a number the market never quoted.
                    var loN = FamilyHist(idx - 1)?.pts;
                    var hiN = FamilyHist(idx + 1)?.pts;
                    if (loN != null && hiN != null && idx - 1 >= 1 && meeting.Date != frontMeeting?.Date)
                    {
                        var loBy = loN.ToDictionary(p => p.Date, p => p.Value);
                        var hiBy = hiN.ToDictionary(p => p.Date, p => p.Value);
                        win = win.Where(p =>
                            !(loBy.TryGetValue(p.Date, out var a) && hiBy.TryGetValue(p.Date, out var b)
                              && Math.Abs(a - b) * 100.0 < 25.0
                              && Math.Abs(p.Value - (a + b) / 2.0) * 100.0 > 25.0)).ToList();
                        if (win.Count == 0) continue;
                    }
                    pts.InsertRange(0, win);
                }
                return pts;
            };
        }

        /// <summary>Chat-paste text for one run, monospace-aligned.</summary>
        public static string MeetingRunText(MeetingRunResult r)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(r.Header);
            if (r.RefPct.HasValue) sb.Append($"   ref {r.RefPct.Value:0.000}");
            sb.AppendLine();
            sb.AppendLine($"{"StartDate",-11} {"Mid",7} {"Priced",8} {"Step",7} {"CoD",6}");
            foreach (var row in r.Rows)
                sb.AppendLine($"{row.Date.ToString("dd-MMM-yy", System.Globalization.CultureInfo.InvariantCulture)}   " +
                    $"{row.MidPct,7:0.000} {SignedBp(row.PricedBp),8} {SignedBp(row.StepBp),7} {SignedBp(row.CoDBp),6}");
            return sb.ToString();
        }
    }
}
