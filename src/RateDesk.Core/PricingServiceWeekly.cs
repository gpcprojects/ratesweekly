using System;
using System.Collections.Generic;
using System.Linq;
using RateDesk.Core.Analytics;
using RateDesk.Core.Config;
using RateDesk.Core.Dates;
using RateDesk.Core.Market;

namespace RateDesk.Core
{
    // ---------- data shapes for the WEEKLY (investor email) report ----------

    public sealed class WeeklyCell
    {
        public string Label { get; init; } = "";
        /// <summary>Level: % for tenors/forwards, bp for spreads (matching the monitor).</summary>
        public double? Mid { get; set; }
        public double? W1Bp { get; set; }
        public double? M1Bp { get; set; }
        /// <summary>Spread cells quote their LEVEL in bp already.</summary>
        public bool IsSpread { get; init; }
    }

    public sealed class WeeklyCcy
    {
        public string Ccy { get; init; } = "";
        public List<WeeklyCell> Cells { get; } = new();
    }

    public sealed class WeeklySection
    {
        public string Title { get; init; } = "";
        public List<WeeklyCcy> Ccys { get; } = new();
    }

    public sealed class WeeklyMeeting
    {
        /// <summary>START of the meeting-period swap (the family's own period boundary — for ECB/BOJ
        /// this is the effective date days after the announcement, for FOMC/MPC the decision day).</summary>
        public DateTime Date { get; init; }
        /// <summary>End of the period (next meeting boundary); null when unresolved.</summary>
        public DateTime? EndDate { get; init; }
        public double MidPct { get; init; }
        public double? PricedBp { get; init; }
        public double? StepBp { get; init; }
        public double? D1Bp { get; set; }
        public double? W1Bp { get; set; }
        public double? M1Bp { get; set; }
        /// <summary>Render "Y/E Turn" instead of the numbers — the period spans a year-end and
        /// its average carries the turn dislocation (SEK/SWESTR; desk 2026-08-20).</summary>
        public bool TurnPeriod { get; init; }
    }

    public sealed class WeeklyRun
    {
        public string Title { get; init; } = "";   // "FOMC · USD" — no terminal flag tokens
        public string RefName { get; init; } = "";
        public double? RefPct { get; init; }
        /// <summary>The pricing contributor this run was actually built on ("" = composite) —
        /// set post-build so history surfaces read the SAME source as the mids (RATESWEEKLY,
        /// source-selection trial 2026-08-26).</summary>
        public string? Source { get; set; }
        /// <summary>COMPOUNDED FIXING (trial, desk 2026-08-26): the overnight fixing compounded
        /// over the CURRENT meeting period (period start → asOf), calendar-day weighted on the
        /// index's own day count — the realized leg of the front run-down swap, the desk
        /// pricer's front-step anchor. Display-only for now: Step/Priced still anchor the spot
        /// fixing. Null when the fixing history is missing or stale (never guessed).</summary>
        public double? CompoundedPct { get; set; }
        /// <summary>Start of the compounding window (the current period's effective date).</summary>
        public DateTime? CompoundedFrom { get; set; }
        public List<WeeklyMeeting> Rows { get; } = new();
    }

    /// <summary>One line of the CB front-meeting summary: the next decision per bank.</summary>
    public sealed class WeeklyFront
    {
        public string Bank { get; init; } = "";
        public string Ccy { get; init; } = "";
        /// <summary>Announcement date (schedule's decisionDates); null when only the swap-period
        /// start is known — the renderers show the start with a marker then.</summary>
        public DateTime? Decision { get; init; }
        public DateTime StartDate { get; init; }
        public double MidPct { get; init; }
        /// <summary>The run's reference (policy/fixing) rate — the "Base Rate" column.</summary>
        public double? RefPct { get; init; }
        public double? PricedBp { get; init; }
        /// <summary>The front period spans a year-end (marked run): the front line shows
        /// "Y/E Turn" for its market-pricing cells.</summary>
        public bool TurnPeriod { get; init; }
    }

    public sealed class WeeklyReport
    {
        public DateTime AsOf { get; init; } = DateTime.Now;
        /// <summary>CB front-meeting pricing, sorted by decision date — the email's lead table.</summary>
        public List<WeeklyFront> Fronts { get; } = new();
        public List<WeeklySection> Sections { get; } = new();
        public List<WeeklyRun> Runs { get; } = new();
        public List<string> Notes { get; } = new();
    }

    public sealed partial class PricingService
    {
        /// <summary>The weekly email's currency universe — the desk's grouping, fixed by spec.
        /// A currency with no config (or disabled) drops out with a note rather than a crash.
        /// RATESWEEKLY DIVERGENCE (desk spec 2026-08-11): THREE grid lines — DM on one, EM and
        /// LATAM merged on one, ASIA EM on one — each rendered as a single full-width table.</summary>
        private static readonly (string title, string[] ccys)[] WeeklyGroups =
        {
            ("DM", new[] { "USD", "EUR", "GBP", "JPY", "CAD", "SEK", "NOK", "DKK", "CHF", "AUD", "NZD" }),
            ("EM · LATAM", new[] { "HUF", "CZK", "PLN", "ZAR", "ILS", "COP", "CLP", "MXN", "BRL" }),
            ("ASIA EM", new[] { "TWD", "THB", "MYR", "INR", "CNY", "HKD", "SGD", "KRW" }),
        };

        /// <summary>The Forward Rates Summary ladder (desk spec 2026-08-06): spot 1y, then forwards
        /// along the standard diagonal. No par curves, no spreads. Every start/end lands on a
        /// standard quoted year on purpose — the blank rule below keys off those endpoint quotes.</summary>
        private static readonly (string label, int startY, int tenorY)[] WeeklyFwdPoints =
        {
            ("1y", 0, 1), ("1y1y", 1, 1), ("2y1y", 2, 1), ("3y1y", 3, 1), ("4y1y", 4, 1),
            ("5y2y", 5, 2), ("7y3y", 7, 3), ("10y2y", 10, 2), ("12y3y", 12, 3),
            ("15y5y", 15, 5), ("20y10y", 20, 10),
        };

        /// <summary>Build the WEEKLY report: the monitor's own numbers (same MonitorFor path — same
        /// mids, same pillar-history changes) at 1w and 1m horizons, plus every central-bank run with
        /// roll-safe 1w/1m changes from the stitched meeting series. Blank cells are DELIBERATE:
        /// a currency without a quoted 30Y pillar publishes nothing there rather than an
        /// extrapolation — we would rather leave it blank than publish a number that is wrong.
        /// <paramref name="meetingsOnly"/> skips the forward-grid sections entirely — the DAILY
        /// surface (desk 2026-08-20) needs only the front table and the meeting runs, and the
        /// forward columns cost a full curve bootstrap per currency.</summary>
        public WeeklyReport BuildWeekly(int meetingsPerRun = 8, bool meetingsOnly = false)
        {
            var rep = new WeeklyReport { AsOf = DateTime.Now };

            foreach (var (title, ccys) in meetingsOnly
                         ? Array.Empty<(string, string[])>() : WeeklyGroups)
            {
                var sec = new WeeklySection { Title = title };
                foreach (var ccy in ccys)
                {
                    if (!Configs.TryGet(ccy, out var cfg) || !cfg.Enabled)
                    {
                        rep.Notes.Add($"{ccy}: not configured — omitted");
                        continue;
                    }
                    try
                    {
                        bool cap = !title.Equals("DM", StringComparison.OrdinalIgnoreCase);
                        var col = BuildWeeklyFwdColumn(cfg, cap, rep.Notes);
                        // OUTCOME-based fallback: a preferred contributor can pass a liveness check
                        // on a couple of tickers and still leave the ladder empty (AUD NABZ prices
                        // a handful of points, none of them ladder endpoints). If the preference
                        // produced nothing, rebuild on the default source rather than publish a
                        // blank currency.
                        var dflt = SourceFor(ccy);
                        if (col.Cells.All(c => c.Mid == null) && !WeeklySource(ccy).Equals(dflt, StringComparison.OrdinalIgnoreCase))
                        {
                            var retry = BuildWeeklyFwdColumn(cfg, cap, null, forceSource: dflt);
                            if (retry.Cells.Any(c => c.Mid != null))
                            {
                                rep.Notes.Add($"{ccy}: {WeeklySource(ccy)} quotes no ladder points here — using {dflt}");
                                col = retry;
                            }
                        }
                        if (col.Cells.All(c => c.Mid == null))
                            rep.Notes.Add($"{ccy}: no quoted points on the forward ladder — omitted");
                        else
                            sec.Ccys.Add(col);
                    }
                    catch (Exception ex)
                    {
                        rep.Notes.Add($"{ccy}: {ex.Message} — omitted");
                    }
                }
                if (sec.Ccys.Count > 0) rep.Sections.Add(sec);
            }

            // central-bank runs — the FRA strips are desk tools, not meeting dates, so kind="fra"
            // stays out; SNB is dropped by spec (quarterly futures-implied, and 9 banks make the
            // email's 3x3 grid)
            foreach (var sched in MeetingsStore.Schedules.Where(s => string.IsNullOrEmpty(s.Kind)
                         && !s.Name.Equals("SNB", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var run = MeetingRun(sched, meetingsPerRun);
                    if (run.Rows.Count == 0) { rep.Notes.Add($"{sched.Name}: {run.Warning ?? "no rows"}"); continue; }
                    var wr = new WeeklyRun
                    {
                        Title = $"{sched.Name} · {run.Ccy}",
                        RefName = run.RefName, RefPct = run.RefPct,
                    };
                    // front-meeting summary line: the run's first row IS the next decision's period
                    if (run.Rows.Count > 0)
                    {
                        var front = run.Rows[0];
                        // the decision must BELONG to this period (within the settlement lag):
                        // on decision day the front rolls to the next meeting, and a calendar
                        // that isn't topped up yet would otherwise pair it with the JUST-DELIVERED
                        // decision (RBA, 2026-08-11: 30-Sep start shown against the 11-Aug
                        // decision). No match ⇒ null ⇒ the honest "start *" rendering.
                        var dec = sched.DecisionDates
                            .Where(d => d.Date >= DateTime.Today && d.Date <= front.Date
                                     && (front.Date - d.Date).TotalDays <= 10)
                            .OrderBy(d => d).Cast<DateTime?>().FirstOrDefault();
                        rep.Fronts.Add(new WeeklyFront
                        {
                            Bank = sched.Name, Ccy = run.Ccy,
                            Decision = dec, StartDate = front.Date,
                            MidPct = front.MidPct, RefPct = run.RefPct, PricedBp = front.PricedBp,
                            TurnPeriod = front.TurnPeriod,
                        });
                    }
                    var series = MeetingSeriesBuilder(sched, run.Rows.Select(r => r.Date));
                    foreach (var row in run.Rows)
                    {
                        var wm = new WeeklyMeeting
                        {
                            Date = row.Date, EndDate = row.EndDate, MidPct = row.MidPct,
                            PricedBp = row.PricedBp, StepBp = row.StepBp, TurnPeriod = row.TurnPeriod,
                        };
                        if (row.TurnPeriod) { wr.Rows.Add(wm); continue; }   // no changes for a label row
                        try
                        {
                            // the stitched series is meeting-CONSTANT across ticker rolls, so a
                            // change straddling a decision compares the same meeting on both
                            // sides — the 1d lookback (desk 2026-08-20) rides the same series,
                            // which also gives it the boundary-day rules (decision-day closes
                            // excluded, 16:30 snaps included) for free
                            var s = series(row.Date);
                            wm.D1Bp = ChangeToBp(s, row.MidPct, DateTime.Today.AddDays(-1));
                            wm.W1Bp = ChangeToBp(s, row.MidPct, DateTime.Today.AddDays(-7));
                            wm.M1Bp = ChangeToBp(s, row.MidPct, MonthAgo(DateTime.Today));
                        }
                        catch { /* changes are best-effort per meeting */ }
                        // 1d fallback: a contract with no pre-roll snap history still has a
                        // roll-aware change-on-day from MeetingRun...
                        // ...but ONLY for hard prints: a ticker row's CoD is a real Bloomberg
                        // close of a real contract; a curve-implied row's CoD is curve-vs-curve
                        // and a curve is not data. HARD-DATA RULE (desk 2026-08-20, final): the
                        // change columns pull exclusively from documented Bloomberg history —
                        // 16:30-London snaps, closes as the CoD convention — never from curves
                        // or interpolation. A historical-curve anchor for the curve-implied
                        // tails was built and SCRAPPED the same day; do not re-add it. Blank
                        // beats manufactured.
                        wm.D1Bp ??= row.MidSource == "ticker" || row.MidSource == "future"
                            ? row.CoDBp : null;
                        wr.Rows.Add(wm);
                    }
                    rep.Runs.Add(wr);
                }
                catch (Exception ex) { rep.Notes.Add($"{sched.Name}: {ex.Message}"); }
            }
            rep.Fronts.Sort((a, b) => (a.Decision ?? a.StartDate).CompareTo(b.Decision ?? b.StartDate));
            return rep;
        }

        /// <summary>One currency's Forward Rates Summary column. LEVELS are curve-true — the same
        /// bootstrapped forwards the pricer quotes and auditfwd checks against FWCM — never the
        /// annuity-less approximation. BLANK RULE: a point publishes only when BOTH its endpoint
        /// years are LIVE QUOTED pillars right now (band-aware, following the pricer's re-legging
        /// for dual-convention markets). Past the quoted ladder, or on a dead quote, the cell is
        /// blank — an extrapolated number in an investor email is a lie with three decimals.
        /// CHANGES are the endpoint pillar quotes' close-to-close moves combined with the forward's
        /// own weights ((B·ΔrB − A·ΔrA)/(B−A)) — differences, so the approximation error cancels to
        /// first order; all four inputs or nothing.</summary>
        /// <summary>Contributor preferences for the WEEKLY only (desk spec 2026-08-06): the dealers
        /// the desk trusts for their own market — NAB across AUD/NZD, BMO for CAD — with Bloomberg's
        /// own composite/BGN everywhere else. Deliberately weekly-scoped: the pricer keeps whatever
        /// source the trader has selected. Falls back to the config default if the named source
        /// isn't configured for that currency (never silently prices off a source that isn't there).</summary>
        private static readonly Dictionary<string, string> WeeklySourcePrefs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AUD"] = "NABZ", ["NZD"] = "NABZ", ["CAD"] = "BMOD",
        };

        /// <summary>Direct market tickers for ladder cells the pillar rule leaves blank (desk spec
        /// 2026-08-06, every ticker NAME+price verified live before wiring): NOK's front trades vs
        /// 3M (NKSW1V3 + the S0312 vs-3M forward surface — the vs-6M NKSW1 is the documented
        /// rich-vs-FRA quote); DKK's 1Y quote is likewise display-worthy even though the CURVE
        /// deliberately excludes it; CZK's 1y1y comes from Bloomberg's own forward surface because
        /// no 6M-family 1Y pillar exists. Weekly display ONLY — none of this touches the bootstrap.
        /// Levels are the ticker's mid; 1w/1m are its own close-to-close moves.</summary>
        private static readonly Dictionary<(string ccy, string label), string> WeeklyCellOverrides = new()
        {
            [("NOK", "1y")] = "NKSW1V3 Curncy",
            [("NOK", "1y1y")] = "S0312FS 1Y1Y BLC Curncy",
            [("DKK", "1y")] = "DKSW1 Curncy",
            [("DKK", "1y1y")] = "S0339FS 1Y1Y BLC Curncy",
            [("CZK", "1y1y")] = "S0320FS 1Y1Y BLC Curncy",
        };

        /// <summary>Tickers the weekly needs beyond the curve/meeting sets — loaders must snapshot
        /// and prefetch these or the override cells stay blank.</summary>
        public static IEnumerable<string> WeeklyExtraTickers => WeeklyCellOverrides.Values;

        /// <summary>The source the weekly WANTS for a currency (config-checked, not price-checked).
        /// Public so the loaders can pull that source's tickers into the snapshot — validating a
        /// preference we never fetched would always fail.</summary>
        public string WeeklySource(string ccy)
        {
            if (WeeklySourcePrefs.TryGetValue(ccy, out var want) && Configs.TryGet(ccy, out var cfg)
                && (cfg.DefaultSource.Equals(want, StringComparison.OrdinalIgnoreCase)
                    || cfg.AltSources.Any(a => a.Equals(want, StringComparison.OrdinalIgnoreCase))))
                return want;
            return SourceFor(ccy);
        }

        /// <summary>The source actually USED, after checking the preferred one is quoting. A
        /// preference is a preference, not a promise: NABZ resolves for AUD/NZD on this terminal but
        /// publishes no API prices (entitlement), and honouring it blindly would blank both columns.
        /// Falls back to the configured default and says so in the report's notes.</summary>
        private string WeeklySourceFor(CurrencyConfig cfg, List<string>? notes = null)
        {
            var want = WeeklySource(cfg.Ccy);
            var fallback = SourceFor(cfg.Ccy);
            if (want.Equals(fallback, StringComparison.OrdinalIgnoreCase)) return want;

            var pillars = (cfg.DefaultProduct.Equals("IRS", StringComparison.OrdinalIgnoreCase) && cfg.Irs != null
                    ? cfg.Irs.Curve : cfg.Ois?.Curve ?? cfg.Irs?.Curve)
                ?.Where(p => p.Enabled && !p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase))
                .Select(p => ConfigStore.ResolveTicker(p.Ticker, want)) ?? Enumerable.Empty<string>();
            int live = pillars.Count(t => Snapshot.TryGetMid(t, out _));
            if (live >= 2) return want;
            notes?.Add($"{cfg.Ccy}: {want} publishes no prices here — using {fallback}");
            return fallback;
        }

        private WeeklyCcy BuildWeeklyFwdColumn(CurrencyConfig cfg, bool emCap, List<string>? notes = null,
            string? forceSource = null)
        {
            var src = forceSource ?? WeeklySourceFor(cfg, notes);
            var prod = cfg.DefaultProduct.Equals("IRS", StringComparison.OrdinalIgnoreCase) && cfg.Irs != null
                ? Trades.ProductKind.IRS
                : cfg.Ois != null ? Trades.ProductKind.OIS : Trades.ProductKind.IRS;
            var col = new WeeklyCcy { Ccy = cfg.Ccy.ToUpperInvariant() };

            foreach (var (label, aY, tY) in WeeklyFwdPoints)
            {
                var cell = new WeeklyCell { Label = label };
                col.Cells.Add(cell);
                // EM (incl. ASIA EM) publishes to 7y3y and no further — desk spec 2026-08-06. Those
                // long ends are quoted thinly enough that a printed level implies more liquidity
                // than exists, whatever our curve can interpolate.
                if (emCap && aY + tY > 10) continue;

                // desk-specified direct quote for this cell (see WeeklyCellOverrides)
                if (WeeklyCellOverrides.TryGetValue((col.Ccy, label), out var ovrTk))
                {
                    if (Snapshot.TryGetMid(ovrTk, out var om))
                    {
                        cell.Mid = om;
                        double? OChg(DateTime tgt)
                        {
                            var h = History?.GetDaily(ovrTk, 220);
                            if (h == null || h.Count == 0) return null;
                            for (int i = h.Count - 1; i >= 0; i--)
                                if (h[i].Date <= tgt)
                                    return (tgt - h[i].Date).TotalDays > 10 ? null : (om - h[i].Value) * 100.0;
                            return null;
                        }
                        cell.W1Bp = OChg(DateTime.Today.AddDays(-7));
                        cell.M1Bp = OChg(MonthAgo(DateTime.Today));
                    }
                    continue;   // an override cell never falls through to the curve path
                }
                var tenor = new QLNet.Period(tY, QLNet.TimeUnit.Years);

                // dual-convention markets: the family follows the leg the PRICER would use — the
                // tenor-rule band while its quotes reach the point's end, the default band after
                string? band = null;
                if (prod == Trades.ProductKind.IRS && cfg.Irs is { } irs && irs.Legs.Count > 1)
                {
                    var leg = Pricing.SwapBuilder.SelectIrsLeg(irs, tenor, null);
                    var def = irs.Legs[^1];
                    if (!leg.FloatTenor.Equals(def.FloatTenor, StringComparison.OrdinalIgnoreCase)
                        && aY + tY > Pricing.SwapBuilder.ShortBandMaxYears(cfg) + 1.0 / 52)
                        leg = def;
                    band = leg.FloatTenor;
                }

                // endpoint pillars by NEAREST months, not exact format: MXN quotes in 28-day periods
                // ("13P" IS the 1y point) and an exact "1Y" match blanked its whole column. The
                // tolerance is the monitor's own rule — no pillar family has neighbours close enough
                // to mismatch inside it.
                string? PillarNear(int years)
                {
                    var list = prod == Trades.ProductKind.OIS ? cfg.Ois?.Curve : cfg.Irs?.Curve;
                    if (list == null) return null;
                    double want = years * 12.0;
                    var best = list.Where(p => p.Enabled
                            && !p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase)
                            && !p.Type.Equals("DEPO", StringComparison.OrdinalIgnoreCase)
                            && (band == null || cfg.Irs == null
                                || Pricing.SwapBuilder.PillarBand(cfg.Irs, p).Equals(band, StringComparison.OrdinalIgnoreCase)))
                        .Select(p => (p, m: Dates.TenorUtil.ApproxMonths(Dates.TenorUtil.Parse(p.Tenor))))
                        .Where(x => Math.Abs(x.m - want) <= Math.Max(1.5, want * 0.035))
                        .OrderBy(x => Math.Abs(x.m - want))
                        .Select(x => (Config.PillarDef?)x.p).FirstOrDefault();
                    return best == null ? null : Config.ConfigStore.ResolveTicker(best.Ticker, src);
                }
                var tkB = PillarNear(aY + tY);
                var tkA = aY > 0 ? PillarNear(aY) : null;
                if (tkB == null || !Snapshot.TryGetMid(tkB, out var nb)) continue;
                double na = 0;
                if (aY > 0 && (tkA == null || !Snapshot.TryGetMid(tkA, out na))) continue;

                try
                {
                    lock (_gate)
                    {
                        var curves = GetCurvesUnlocked(cfg, src);
                        QLNet.Settings.setEvaluationDate(curves.AsOf);
                        cell.Mid = Analytics.ForwardGrid.ForwardRate(curves, prod,
                            aY == 0 ? null : new QLNet.Period(aY, QLNet.TimeUnit.Years), tenor);
                    }
                }
                catch { continue; }

                double? Chg(DateTime tgt)
                {
                    double? CloseAt(string t)
                    {
                        var h = History?.GetDaily(t, 220);
                        if (h == null || h.Count == 0) return null;
                        for (int i = h.Count - 1; i >= 0; i--)
                            if (h[i].Date <= tgt)
                                return (tgt - h[i].Date).TotalDays > 10 ? null : h[i].Value;
                        return null;
                    }
                    var cb = CloseAt(tkB);
                    if (cb == null) return null;
                    if (aY == 0) return (nb - cb.Value) * 100.0;
                    var ca = CloseAt(tkA!);
                    if (ca == null) return null;
                    return ((aY + tY) * (nb - cb.Value) - aY * (na - ca.Value)) / tY * 100.0;
                }
                cell.W1Bp = Chg(DateTime.Today.AddDays(-7));
                cell.M1Bp = Chg(MonthAgo(DateTime.Today));
            }
            return col;
        }

        /// <summary>The 1m LOOKBACK CONVENTION (desk 2026-08-20): same day last month (Excel
        /// EDATE-style, clamped at month ends), anchored at the last close AT OR BEFORE it — the
        /// convention the desk's incumbent sheet targets. Not a fixed 30/31 days: those drift a
        /// day or two against "a month ago" as month lengths change. NOTE the sheet itself only
        /// STORES rows when someone updates it, so its realized 1m anchor can sit up to a week
        /// earlier than this (measured 2026-08-20: its FOMC anchor was 15-Jul for a 19-Aug sheet);
        /// ours is calendar-true against daily closes — small gaps vs the sheet on stale weeks are
        /// the sheet's cadence, not a fault.</summary>
        internal static DateTime MonthAgo(DateTime d) => d.AddMonths(-1);

        /// <summary>Live mid vs the stitched series' close at/before <paramref name="target"/>, in
        /// bp. STALENESS-BOUNDED: when a regime window gapped (a far generic's BDH failed), the
        /// naive "latest close at/before target" walks back into a MUCH older regime — BOJ's far
        /// rows printed +62.6bp "1w changes" that were live-minus-a-year-ago. A close more than 10
        /// days older than the target is a different world: publish a blank, never that.</summary>
        private static double? ChangeToBp(IReadOnlyList<HistPoint> s, double liveMid, DateTime target)
        {
            if (s.Count == 0) return null;
            for (int i = s.Count - 1; i >= 0; i--)
                if (s[i].Date <= target)
                    return (target - s[i].Date).TotalDays > 10 ? null : (liveMid - s[i].Value) * 100.0;
            return null;
        }
    }
}
