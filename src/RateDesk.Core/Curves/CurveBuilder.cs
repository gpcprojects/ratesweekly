using System;
using System.Collections.Generic;
using System.Linq;
using QLNet;
using RateDesk.Core.Config;
using RateDesk.Core.Dates;
using RateDesk.Core.Market;
using RateDesk.Core.Pricing;
using RateDesk.Core.QL;

namespace RateDesk.Core.Curves
{
    public sealed class PillarInfo
    {
        public string Label { get; init; } = "";
        public string Ticker { get; init; } = "";
        public string CurveName { get; init; } = "";   // "OIS" or "IRS"
        public double MarketRatePct { get; init; }
        public Date Maturity { get; init; } = new Date();
    }

    public sealed class CurveSet
    {
        public string Ccy { get; init; } = "";
        public string Source { get; init; } = "";
        public Date AsOf { get; init; } = new Date();
        public CurrencyConfig Cfg { get; init; } = new();
        public Calendar Cal { get; init; } = new WeekendsOnly();

        public YieldTermStructure? Ois { get; set; }
        public YieldTermStructure? Irs { get; set; }
        /// <summary>Per-float-tenor IRS projection curves ("3M"/"6M") for multi-index currencies (AUD 3s/6s).</summary>
        public Dictionary<string, YieldTermStructure> IrsByBand { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Last QUOTED pillar (in years) per band — a band curve must never project past this.</summary>
        public Dictionary<string, double> BandMaxYears { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Handle<YieldTermStructure> DiscountHandle { get; set; } = new();
        public Handle<YieldTermStructure> OisProjectionHandle { get; set; } = new();
        public Handle<YieldTermStructure> IrsProjectionHandle { get; set; } = new();

        /// <summary>Discount handle for a product. Under SELF discounting each product discounts on
        /// its own curve (an OIS swap off the OIS curve — the quoted convention); OIS/USD-OIS
        /// discounting stays product-agnostic.</summary>
        public Handle<YieldTermStructure> DiscountHandleFor(Trades.ProductKind product) =>
            product == Trades.ProductKind.OIS && Ois != null
            && !Cfg.Discounting.Equals("OIS", StringComparison.OrdinalIgnoreCase)
            && !Cfg.Discounting.Equals("USD-OIS", StringComparison.OrdinalIgnoreCase)
                ? new Handle<YieldTermStructure>(Ois)
                : DiscountHandle;

        /// <summary>Projection handle for a float-index tenor: exact band curve if built, else default IRS.</summary>
        public Handle<YieldTermStructure> ProjectionFor(string floatTenor) =>
            IrsByBand.TryGetValue(floatTenor, out var ts)
                ? new Handle<YieldTermStructure>(ts)
                : IrsProjectionHandle;

        /// <summary>Band curve only if its quoted ladder covers the swap's maturity — NEVER extrapolate a
        /// short band (AUD 3s end at 4y; a 10y2y must fall back to the full-term curve, matching FWCM).</summary>
        public (Handle<YieldTermStructure> handle, bool usedBand) ProjectionFor(string floatTenor, double requiredYears)
        {
            if (IrsByBand.TryGetValue(floatTenor, out var ts)
                && BandMaxYears.TryGetValue(floatTenor, out var max)
                && requiredYears <= max + 1.0 / 52)
                return (new Handle<YieldTermStructure>(ts), true);
            return (IrsProjectionHandle, false);
        }

        public List<PillarInfo> Pillars { get; } = new();
        public TimeSpan BuildTime { get; set; }
        public List<string> Warnings { get; } = new();
    }

    /// <summary>Bootstraps discount/projection curves for a currency from live quotes.</summary>
    public static class CurveBuilder
    {
        /// <summary>FRA pillar tenor: "3X6" = 3M-start, 6M-end, on the (end−start)M index.</summary>
        private static readonly System.Text.RegularExpressions.Regex FraTenorRx =
            new(@"^(\d+)\s*[xX]\s*(\d+)$");

        private static bool IsFraPillar(PillarDef p) =>
            p.Enabled && p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase);

        /// <summary>Does this currency quote ROLLING AxB FRAs (AUD, CZK, HUF, NZD, PLN)? Derived from
        /// the config through the same <c>FraTenorRx</c> the bootstrapper uses, so the query bar's
        /// validation can never drift from what actually gets built.</summary>
        public static bool HasRollingFraPillars(CurrencyConfig cfg) =>
            cfg.Irs?.Curve.Any(p => IsFraPillar(p) && FraTenorRx.IsMatch(p.Tenor)) == true;

        /// <summary>Does this currency quote an IMM-dated quarterly FRA strip? Either as FRA pillars
        /// with a plain index period ("3M" — SEK), or as the front-end strip borrowed from OIS
        /// (<c>Irs.FrontFromOis.FraTickers</c> — NOK, DKK).</summary>
        public static bool HasImmFraStrip(CurrencyConfig cfg) =>
            cfg.Irs?.Curve.Any(p => IsFraPillar(p) && !FraTenorRx.IsMatch(p.Tenor)) == true
            || cfg.Irs?.FrontFromOis?.FraTickers.Count > 0;

        /// <summary>Index period of a currency's IMM FRA contracts, from the config that defines the
        /// strip. NOT the swap leg's float tenor: DKK quotes a 3M IMM strip while its only IRS leg is
        /// 6M CIBOR, so reading the leg would price a 6M period against a 3M contract.</summary>
        public static string? ImmFraIndexTenor(CurrencyConfig cfg)
        {
            var typed = cfg.Irs?.Curve.FirstOrDefault(p => IsFraPillar(p) && !FraTenorRx.IsMatch(p.Tenor));
            if (typed != null) return typed.Tenor;                       // SEK: FRA pillars tenored "3M"
            var ff = cfg.Irs?.FrontFromOis;
            return ff?.FraTickers.Count > 0 ? ff.StripTenor : null;      // NOK, DKK
        }
        /// <summary>Build all curves for a currency. Quotes are % rates keyed by full ticker.
        /// externalDiscount: exogenous discount curve (e.g. USD SOFR for USD-settled ND swaps).</summary>
        /// <param name="datedOis">Meeting-dated OIS pillars (start, end, ticker, label) bootstrapped
        /// ALONGSIDE the config's tenor pillars. A central-bank-dated OIS accrues over one inter-meeting
        /// period, so these pin the overnight rate flat across each period and let it step on the decision
        /// date - which is what makes the curve reprice the quoted meeting OIS. A smooth tenor strip cannot:
        /// it smears each policy step across the meeting.</param>
        public static CurveSet Build(CurrencyConfig cfg, string source, RatesSnapshot snap, Date asOf,
            Func<string, double, double>? bump = null, Handle<YieldTermStructure>? externalDiscount = null,
            IEnumerable<(Date Start, Date End, string Ticker, string Label)>? datedOis = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Settings.setEvaluationDate(asOf);
            var cal = QlMaps.MakeCalendar(cfg.Calendar);
            var set = new CurveSet { Ccy = cfg.Ccy, Source = source, AsOf = asOf, Cfg = cfg, Cal = cal };

            double GetRate(string baseTicker)
            {
                var full = ConfigStore.ResolveTicker(baseTicker, source);
                if (!snap.TryGetMid(full, out var pct))
                    throw new MissingQuoteException(cfg.Ccy, full);
                return bump?.Invoke(full, pct / 100.0) ?? pct / 100.0;
            }

            bool usdDisc = cfg.Discounting.Equals("USD-OIS", StringComparison.OrdinalIgnoreCase)
                           && externalDiscount != null && !externalDiscount.empty();

            // ---------- OIS curve ----------
            if (cfg.Ois != null && cfg.Ois.Curve.Count > 0)
            {
                var helpers = new List<RateHelper>();
                var onIndexNoCurve = SwapBuilder.MakeOvernightIndex(cfg, cfg.Ois, cal, new Handle<YieldTermStructure>());
                foreach (var p in cfg.Ois.Curve.Where(p => p.Enabled))
                {
                    var tenor = TenorUtil.Parse(p.Tenor);
                    double r;
                    try { r = GetRate(p.Ticker); }
                    catch (MissingQuoteException ex) { set.Warnings.Add(ex.Message); continue; }
                    helpers.Add(new OisPillarHelper(new Handle<Quote>(new SimpleQuote(r)),
                        cfg, cfg.Ois, cal, tenor, usdDisc ? externalDiscount : null));
                    set.Pillars.Add(new PillarInfo
                    {
                        Label = TenorUtil.Format(tenor),
                        Ticker = ConfigStore.ResolveTicker(p.Ticker, source),
                        CurveName = "OIS",
                        MarketRatePct = r * 100.0,
                        Maturity = cal.adjust(SwapBuilder.SpotDate(cfg, cal, asOf) + tenor,
                            BusinessDayConvention.ModifiedFollowing),
                    });
                }
                // meeting-dated pillars: absolute dates, so they are added after the tenor strip and the
                // bootstrap orders everything by maturity. A tenor pillar maturing INSIDE the dated range
                // would fight them for the same forward, so those are dropped in favour of the dated quote,
                // which is the instrument the market actually trades on those dates.
                if (datedOis != null)
                {
                    var dated = datedOis.ToList();
                    if (dated.Count > 0)
                    {
                        var firstDatedEnd = dated.Min(d => d.End);
                        var lastDatedEnd = dated.Max(d => d.End);
                        int dropped = helpers.RemoveAll(h => h.latestDate() > firstDatedEnd
                                                            && h.latestDate() <= lastDatedEnd);
                        if (dropped > 0)
                            set.Pillars.RemoveAll(p => p.CurveName == "OIS"
                                && p.Maturity > firstDatedEnd && p.Maturity <= lastDatedEnd);
                        foreach (var d in dated)
                        {
                            double r;
                            try { r = GetRate(d.Ticker); }
                            catch (MissingQuoteException ex) { set.Warnings.Add(ex.Message); continue; }
                            helpers.Add(new DatedOisPillarHelper(new Handle<Quote>(new SimpleQuote(r)),
                                cfg, cfg.Ois, cal, d.Start, d.End, usdDisc ? externalDiscount : null));
                            set.Pillars.Add(new PillarInfo
                            {
                                Label = d.Label,
                                Ticker = ConfigStore.ResolveTicker(d.Ticker, source),
                                CurveName = "OIS",
                                MarketRatePct = r * 100.0,
                                Maturity = d.End,
                            });
                        }
                    }
                }

                if (helpers.Count < 2)
                {
                    // fatal only when something actually needs the OIS curve — an OIS-family quote
                    // outage must never take down IRS pricing for a SELF-discounting currency
                    if (cfg.Irs == null || cfg.Discounting.Equals("OIS", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"{cfg.Ccy}: not enough OIS quotes ({helpers.Count}) on source {source} — try the default source");
                    set.Warnings.Add($"{cfg.Ccy}: only {helpers.Count} live OIS quote(s) — OIS curve not built, IRS unaffected");
                    set.Pillars.RemoveAll(p => p.CurveName == "OIS");
                }
                else
                {
                    var ois = new PiecewiseYieldCurve<Discount, LogLinear>(asOf, helpers, new Actual365Fixed());
                    ois.enableExtrapolation();
                    set.Ois = ois;
                }
            }

            // ---------- IRS projection curves (one per float-index band) ----------
            if (cfg.Irs != null && cfg.Irs.Curve.Count > 0)
            {
                bool oisDiscounting = cfg.Discounting.Equals("OIS", StringComparison.OrdinalIgnoreCase) && set.Ois != null;
                var discount = usdDisc ? externalDiscount!
                    : oisDiscounting ? new Handle<YieldTermStructure>(set.Ois)
                    : new Handle<YieldTermStructure>(); // empty => self-discounting during bootstrap

                // group pillars by band (explicit Band tag, else the tenor-rule band)
                var byBand = new Dictionary<string, List<(PillarDef p, Period tenor, double r, IrsLegConv leg)>>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in cfg.Irs.Curve.Where(p => p.Enabled))
                {
                    // FRA pillars: "AxB" months (rolling) OR a plain index period ("3M") for IMM
                    // CONTRACTS whose period end comes from the snapshot MATURITY (SKF30001...)
                    bool isFra = p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase);
                    var fra = isFra ? FraTenorRx.Match(p.Tenor) : null;
                    Period tenor;
                    if (fra is { Success: true })
                        tenor = new Period(int.Parse(fra.Groups[2].Value), TimeUnit.Months);
                    else if (isFra)
                    {
                        // IMM contract: order/maturity by its actual period end when quoted
                        var immQ = snap.Get(ConfigStore.ResolveTicker(p.Ticker, source));
                        if (immQ?.Maturity is DateTime im)
                            tenor = new Period(Math.Max(1, (int)Math.Round(
                                (new Date(im.Day, (Month)im.Month, im.Year) - asOf) / 30.4375)), TimeUnit.Months);
                        else { set.Warnings.Add($"{cfg.Ccy}: FRA {p.Ticker} has no maturity — skipped"); continue; }
                    }
                    else
                        tenor = TenorUtil.Parse(p.Tenor);
                    double r;
                    try { r = GetRate(p.Ticker); }
                    catch (MissingQuoteException ex) { set.Warnings.Add(ex.Message); continue; }

                    IrsLegConv leg;
                    if (p.Band != null)
                        leg = cfg.Irs.Legs.FirstOrDefault(l => l.FloatTenor.Equals(p.Band, StringComparison.OrdinalIgnoreCase))
                              ?? SwapBuilder.SelectIrsLeg(cfg.Irs, tenor, null);
                    else if (p.Type.Equals("DEPO", StringComparison.OrdinalIgnoreCase))
                        leg = SelectLegForDepo(cfg.Irs, tenor);
                    else
                        leg = SwapBuilder.SelectIrsLeg(cfg.Irs, tenor, null);

                    if (!byBand.TryGetValue(leg.FloatTenor, out var list))
                        byBand[leg.FloatTenor] = list = new();
                    list.Add((p, tenor, r, leg));

                    set.Pillars.Add(new PillarInfo
                    {
                        Label = fra != null ? p.Tenor.ToUpperInvariant().Replace(" ", "") : TenorUtil.Format(tenor),
                        Ticker = ConfigStore.ResolveTicker(p.Ticker, source),
                        CurveName = byBand.Count > 1 || cfg.Irs.Legs.Select(l => l.FloatTenor).Distinct().Count() > 1
                            ? $"IRS {leg.FloatTenor}" : "IRS",
                        MarketRatePct = r * 100.0,
                        Maturity = cal.adjust(SwapBuilder.SpotDate(cfg, cal, asOf) + tenor,
                            BusinessDayConvention.ModifiedFollowing),
                    });
                }

                // OIS-shaped synthetic front end (FrontFromOis): forward IBOR = OIS forward
                // + today's fixing spread, + the quoted tenor basis for the long-index leg.
                // Used where the FRA strip publishes no API prices (NOK) — the OIS curve carries
                // the meeting-dated shape a sparse swap ladder linearises away.
                var synth = new List<(string band, int startM, int lenM, double rate)>();
                if (cfg.Irs.FrontFromOis is { } ff && cfg.Irs.Legs.Count > 0)
                {
                    var spotD = SwapBuilder.SpotDate(cfg, cal, asOf);
                    double OisFwd(int m, int len)
                    {
                        var d1 = m == 0 ? spotD
                            : cal.adjust(spotD + new Period(m, TimeUnit.Months), BusinessDayConvention.ModifiedFollowing);
                        var d2 = cal.adjust(d1 + new Period(len, TimeUnit.Months), BusinessDayConvention.ModifiedFollowing);
                        return set.Ois!.forwardRate(d1, d2, new Actual360(), Compounding.Simple).value();
                    }
                    var shortLeg = cfg.Irs.Legs.First();
                    var longLeg = cfg.Irs.Legs.Last();
                    // strip contracts have their OWN index period (3M), independent of the legs —
                    // DKK is a single-6M-leg market whose strip is still 3M FRAs
                    int stripLen = Math.Max(1, (int)Math.Round(TenorUtil.ApproxMonths(TenorUtil.Parse(ff.StripTenor))));
                    int lLen = Math.Max(1, (int)Math.Round(TenorUtil.ApproxMonths(TenorUtil.Parse(longLeg.FloatTenor))));
                    {
                        var basisPts = new List<(double y, double bp)>();
                        foreach (var bt in ff.BasisTickers)
                            if (snap.TryGetMid(bt, out var bpv))
                            {
                                var stem = bt.Split(' ')[0];
                                var digs = new string(stem.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
                                if (digs.Length > 0) basisPts.Add((double.Parse(digs), bpv));
                            }
                        basisPts.Sort((x, y2) => x.y.CompareTo(y2.y));
                        double Basis(double y)
                        {
                            if (basisPts.Count == 0) return 0;
                            if (y <= basisPts[0].y) return basisPts[0].bp;
                            if (y >= basisPts[^1].y) return basisPts[^1].bp;
                            for (int i = 1; i < basisPts.Count; i++)
                                if (y <= basisPts[i].y)
                                {
                                    double w2 = (y - basisPts[i - 1].y) / (basisPts[i].y - basisPts[i - 1].y);
                                    return basisPts[i - 1].bp + w2 * (basisPts[i].bp - basisPts[i - 1].bp);
                                }
                            return basisPts[^1].bp;
                        }
                        // real short-index IMM FRA strip when quoted; OIS-derived shape otherwise.
                        // LONG-leg strip only: a synthetic short band would hijack sub-2y pricing
                        // away from the quoted vs-{long} screen convention (NKSW1 is vs 6M)
                        var strip = new List<(int m, double r)>();
                        foreach (var ft in ff.FraTickers)
                            if (snap.TryGetMid(ft, out var fr) && snap.Get(ft)?.Maturity is DateTime fm)
                            {
                                int m = (int)Math.Round(((new Date(fm.Day, (Month)fm.Month, fm.Year) - spotD) / 30.4375) - stripLen);
                                if (m >= 1) strip.Add((m, fr / 100.0));
                            }
                        if (strip.Count >= 3)
                        {
                            foreach (var (m, fr) in strip)
                                synth.Add((longLeg.FloatTenor, m, lLen,
                                    fr + (lLen != stripLen ? Basis((m + lLen / 2.0) / 12.0) / 10000.0 : 0.0)));
                            set.Warnings.Add($"{cfg.Ccy}: {longLeg.FloatTenor} front end from the {ff.StripTenor} IMM FRA strip ({strip.Count} contracts) + quoted tenor basis");
                        }
                        else if (set.Ois != null && !string.IsNullOrEmpty(shortLeg.FixingTicker)
                                 && snap.TryGetMid(shortLeg.FixingTicker, out var fixPct))
                        {
                            int sLen = Math.Max(1, (int)Math.Round(TenorUtil.ApproxMonths(TenorUtil.Parse(shortLeg.FloatTenor))));
                            double spread = fixPct / 100.0 - OisFwd(0, sLen);
                            foreach (int m in new[] { 1, 3, 5, 8, 10, 13, 15 })
                                synth.Add((longLeg.FloatTenor, m, lLen,
                                    OisFwd(m, lLen) + spread
                                    + (lLen != sLen ? Basis((m + lLen / 2.0) / 12.0) / 10000.0 : 0.0)));
                            set.Warnings.Add($"{cfg.Ccy}: {longLeg.FloatTenor} front end OIS-shaped + quoted {shortLeg.FloatTenor}/{longLeg.FloatTenor} basis (FRA strip unpublished)");
                        }
                    }
                }

                var spotDate = SwapBuilder.SpotDate(cfg, cal, asOf);
                Date PillarEnd(Period t) => cal.adjust(spotDate + t, BusinessDayConvention.ModifiedFollowing);

                foreach (var (band, entries) in byBand)
                {
                    var helpers = new List<RateHelper>();
                    // FRA/synthetic ends must not collide with quoted pillar maturities (bootstrap
                    // rejects duplicate dates) — quoted instruments always win
                    var ends = entries.Where(e => !e.p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase))
                        .Select(e => PillarEnd(e.tenor)).ToList();
                    bool ClashFree(Date end) => ends.All(d => Math.Abs(d - end) > 10);
                    var bandLeg = entries.Count > 0 ? entries[0].leg
                        : cfg.Irs.Legs.FirstOrDefault(l => l.FloatTenor.Equals(band, StringComparison.OrdinalIgnoreCase))
                          ?? cfg.Irs.Legs.Last();
                    foreach (var (p, tenor, r, leg) in entries)
                    {
                        if (p.Type.Equals("DEPO", StringComparison.OrdinalIgnoreCase))
                        {
                            var idx = SwapBuilder.MakeIborIndex(cfg, leg, cal, new Handle<YieldTermStructure>(), tenor);
                            helpers.Add(new DepositRateHelper(new Handle<Quote>(new SimpleQuote(r)), idx));
                        }
                        else if (p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase))
                        {
                            var f = FraTenorRx.Match(p.Tenor);
                            // "3X6" = rolling months; plain "3M" = IMM CONTRACT, period end from
                            // the snapshot MATURITY (SKF30001 "SEK FRA 3M SEP 26 DEC 26" etc.)
                            Date? immEnd = f.Success ? null
                                : snap.Get(ConfigStore.ResolveTicker(p.Ticker, source))?.Maturity is DateTime mdt
                                    ? new Date(mdt.Day, (Month)mdt.Month, mdt.Year) : null;
                            if (!f.Success && immEnd == null)
                            {
                                set.Warnings.Add($"{cfg.Ccy}: FRA {p.Ticker} has no maturity — skipped");
                                continue;
                            }
                            var end = f.Success ? PillarEnd(tenor) : immEnd!;
                            if (!ClashFree(end) || end <= spotDate)
                            {
                                set.Warnings.Add($"{cfg.Ccy}: FRA {p.Tenor} {p.Ticker} skipped — clash or expired");
                                continue;
                            }
                            int lenM2 = f.Success ? int.Parse(f.Groups[2].Value) - int.Parse(f.Groups[1].Value)
                                : (int)Math.Round(TenorUtil.ApproxMonths(TenorUtil.Parse(p.Tenor)));
                            var idx = SwapBuilder.MakeIborIndex(cfg, leg, cal, new Handle<YieldTermStructure>(),
                                new Period(lenM2, TimeUnit.Months));
                            if (f.Success)
                                helpers.Add(new FraRateHelper(new Handle<Quote>(new SimpleQuote(r)),
                                    int.Parse(f.Groups[1].Value), idx));
                            else
                            {
                                int em = (int)end.month(), sy = end.year(), sm = em - lenM2;
                                if (sm <= 0) { sm += 12; sy--; }
                                helpers.Add(new FuturesRateHelper(
                                    new Handle<Quote>(new SimpleQuote(100.0 * (1.0 - r))),
                                    Dates.ImmUtil.ThirdWednesday(sm, sy),
                                    idx, new Handle<Quote>(new SimpleQuote(0.0))));
                            }
                            ends.Add(end);
                        }
                        else
                        {
                            helpers.Add(new IrsPillarHelper(new Handle<Quote>(new SimpleQuote(r)),
                                cfg, cfg.Irs, cal, tenor, discount, leg));
                        }
                    }
                    double synthMaxM = 0;
                    foreach (var s in synth.Where(x => x.band.Equals(band, StringComparison.OrdinalIgnoreCase)))
                    {
                        var end = PillarEnd(new Period(s.startM + s.lenM, TimeUnit.Months));
                        if (!ClashFree(end)) continue;
                        var idx = SwapBuilder.MakeIborIndex(cfg, bandLeg, cal, new Handle<YieldTermStructure>(),
                            new Period(s.lenM, TimeUnit.Months));
                        helpers.Add(new FraRateHelper(new Handle<Quote>(new SimpleQuote(s.rate)), s.startM, idx));
                        ends.Add(end);
                        synthMaxM = Math.Max(synthMaxM, s.startM + s.lenM);
                    }
                    if (helpers.Count < 2)
                    {
                        set.Warnings.Add($"{cfg.Ccy}: {band} band has only {helpers.Count} quote(s) — band curve not built");
                        continue;
                    }
                    var crv = new PiecewiseYieldCurve<Discount, LogLinear>(asOf, helpers, new Actual365Fixed());
                    crv.enableExtrapolation();
                    set.IrsByBand[band] = crv;
                    set.BandMaxYears[band] = Math.Max(
                        entries.Count > 0 ? entries.Max(e => TenorUtil.ApproxMonths(e.tenor)) : 0.0, synthMaxM) / 12.0;
                }

                // default IRS curve = the STANDARD MIXED ladder (each pillar with its natural tenor-rule
                // convention, e.g. AUD 1-3y quarterly then semi) — this is the curve FWCM forwards match.
                // Pillars band-tagged away from their natural band (BBSW6M depo, ADSWAP4Q) are band-only.
                {
                    var mixedHelpers = new List<RateHelper>();
                    var mixedEnds = byBand.Values.SelectMany(es => es)
                        .Where(e => !e.p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase))
                        .Select(e => PillarEnd(e.tenor)).ToList();
                    foreach (var entries in byBand.Values)
                        foreach (var (p, tenor, r, leg) in entries)
                        {
                            var naturalBand = p.Type.Equals("DEPO", StringComparison.OrdinalIgnoreCase)
                                ? SelectLegForDepo(cfg.Irs, tenor).FloatTenor
                                : SwapBuilder.SelectIrsLeg(cfg.Irs, tenor, null).FloatTenor;
                            if (!leg.FloatTenor.Equals(naturalBand, StringComparison.OrdinalIgnoreCase)) continue;
                            if (p.Type.Equals("DEPO", StringComparison.OrdinalIgnoreCase))
                            {
                                var idx = SwapBuilder.MakeIborIndex(cfg, leg, cal, new Handle<YieldTermStructure>(), tenor);
                                mixedHelpers.Add(new DepositRateHelper(new Handle<Quote>(new SimpleQuote(r)), idx));
                            }
                            else if (p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase))
                            {
                                var f = FraTenorRx.Match(p.Tenor);
                                Date? immEnd = f.Success ? null
                                    : snap.Get(ConfigStore.ResolveTicker(p.Ticker, source))?.Maturity is DateTime mdt
                                        ? new Date(mdt.Day, (Month)mdt.Month, mdt.Year) : null;
                                if (!f.Success && immEnd == null) continue;
                                var end = f.Success ? PillarEnd(tenor) : immEnd!;
                                if (end > spotDate && mixedEnds.All(d => Math.Abs(d - end) > 10))
                                {
                                    int lenM2 = f.Success ? int.Parse(f.Groups[2].Value) - int.Parse(f.Groups[1].Value)
                                        : (int)Math.Round(TenorUtil.ApproxMonths(TenorUtil.Parse(p.Tenor)));
                                    var idx = SwapBuilder.MakeIborIndex(cfg, leg, cal, new Handle<YieldTermStructure>(),
                                        new Period(lenM2, TimeUnit.Months));
                                    if (f.Success)
                                        mixedHelpers.Add(new FraRateHelper(new Handle<Quote>(new SimpleQuote(r)),
                                            int.Parse(f.Groups[1].Value), idx));
                                    else
                                    {
                                        int em = (int)end.month(), sy = end.year(), sm = em - lenM2;
                                        if (sm <= 0) { sm += 12; sy--; }
                                        mixedHelpers.Add(new FuturesRateHelper(
                                            new Handle<Quote>(new SimpleQuote(100.0 * (1.0 - r))),
                                            Dates.ImmUtil.ThirdWednesday(sm, sy),
                                            idx, new Handle<Quote>(new SimpleQuote(0.0))));
                                    }
                                    mixedEnds.Add(end);
                                }
                            }
                            else
                            {
                                mixedHelpers.Add(new IrsPillarHelper(new Handle<Quote>(new SimpleQuote(r)),
                                    cfg, cfg.Irs, cal, tenor, discount, leg));
                            }
                        }
                    // OIS-shaped synthetic front pillars shape the DEFAULT curve too (its band)
                    if (cfg.Irs.Legs.Count > 0)
                        foreach (var s in synth.Where(x =>
                            x.band.Equals(cfg.Irs.Legs.Last().FloatTenor, StringComparison.OrdinalIgnoreCase)))
                        {
                            var end = PillarEnd(new Period(s.startM + s.lenM, TimeUnit.Months));
                            if (mixedEnds.All(d => Math.Abs(d - end) > 10))
                            {
                                var idx = SwapBuilder.MakeIborIndex(cfg, cfg.Irs.Legs.Last(), cal,
                                    new Handle<YieldTermStructure>(), new Period(s.lenM, TimeUnit.Months));
                                mixedHelpers.Add(new FraRateHelper(new Handle<Quote>(new SimpleQuote(s.rate)), s.startM, idx));
                                mixedEnds.Add(end);
                            }
                        }
                    if (mixedHelpers.Count >= 2)
                    {
                        var mixed = new PiecewiseYieldCurve<Discount, LogLinear>(asOf, mixedHelpers, new Actual365Fixed());
                        mixed.enableExtrapolation();
                        set.Irs = mixed;
                    }
                    else if (set.IrsByBand.Count > 0)
                    {
                        var defBand = cfg.Irs.Legs.Last().FloatTenor;
                        set.Irs = set.IrsByBand.TryGetValue(defBand, out var d) ? d : set.IrsByBand.Values.First();
                    }
                    else if (set.Ois == null)
                    {
                        throw new InvalidOperationException($"{cfg.Ccy}: no IRS band could be built");
                    }
                }
            }

            // ---------- wire handles ----------
            if (usdDisc)
            {
                set.DiscountHandle = externalDiscount!;
                if (set.Ois == null && set.Irs == null)
                    throw new InvalidOperationException($"{cfg.Ccy}: no curve built");
            }
            else
            {
                var discountCurve = cfg.Discounting.Equals("OIS", StringComparison.OrdinalIgnoreCase) && set.Ois != null
                    ? set.Ois
                    : (set.Irs ?? set.Ois) ?? throw new InvalidOperationException($"{cfg.Ccy}: no curve built");
                set.DiscountHandle = new Handle<YieldTermStructure>(discountCurve);
            }
            if (set.Ois != null) set.OisProjectionHandle = new Handle<YieldTermStructure>(set.Ois);
            if (set.Irs != null) set.IrsProjectionHandle = new Handle<YieldTermStructure>(set.Irs);

            sw.Stop();
            set.BuildTime = sw.Elapsed;
            return set;
        }

        private static IrsLegConv SelectLegForDepo(IrsConfig irs, Period depoTenor)
        {
            foreach (var leg in irs.Legs)
                if (Math.Abs(TenorUtil.ApproxMonths(TenorUtil.Parse(leg.FloatTenor)) - TenorUtil.ApproxMonths(depoTenor)) < 0.5)
                    return leg;
            return irs.Legs.Last();
        }
    }

    public sealed class MissingQuoteException : Exception
    {
        public MissingQuoteException(string ccy, string ticker)
            : base($"{ccy}: no quote for {ticker}") { }
    }
}
