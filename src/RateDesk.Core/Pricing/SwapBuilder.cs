using System;
using System.Linq;
using QLNet;
using RateDesk.Core.Config;
using RateDesk.Core.Dates;
using RateDesk.Core.QL;

namespace RateDesk.Core.Pricing
{
    /// <summary>Builds QLNet swap instruments from currency conventions. Single source of truth:
    /// both curve bootstrap helpers and priced trades come through here.</summary>
    public static class SwapBuilder
    {
        // ---------- date logic ----------

        public static Date SpotDate(CurrencyConfig cfg, Calendar cal, Date asOf) =>
            cal.advance(asOf, cfg.SpotLag, TimeUnit.Days);

        /// <summary>Effective date for a trade spec (spot / IMM / forward / explicit).</summary>
        public static Date EffectiveDate(Trades.TradeSpec spec, CurrencyConfig cfg, Calendar cal, Date asOf)
        {
            switch (spec.StartKind)
            {
                case Trades.StartKind.Spot:
                    return SpotDate(cfg, cal, asOf);
                case Trades.StartKind.Imm:
                    var imm = spec.ImmDate ?? throw new InvalidOperationException("IMM date missing");
                    var adj = cal.adjust(imm, BusinessDayConvention.ModifiedFollowing);
                    if (adj <= asOf)
                        throw new InvalidOperationException(
                            $"IMM {spec.ImmCode} ({imm}) has passed; next IMM is {NextImmCode(asOf)}");
                    return adj;
                case Trades.StartKind.Forward:
                    var spot = SpotDate(cfg, cal, asOf);
                    return cal.advance(spot, spec.ForwardStart!, BusinessDayConvention.ModifiedFollowing);
                case Trades.StartKind.Date:
                    // A start in the past is allowed: the trade is SEASONED, and the elapsed accrual is
                    // priced off published fixings loaded by MakeOvernightIndex. Refusing it used to strand
                    // a blotter row on a stale dv01 the moment its start date rolled over.
                    return cal.adjust(spec.ExplicitStart!, BusinessDayConvention.Following);
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>Unadjusted maturity = effective + tenor ("P" tenors advance in exact 28-day blocks).</summary>
        public static Date MaturityDate(Date effective, Period tenor)
        {
            return effective + tenor; // schedule generation applies business-day adjustment
        }

        private static string NextImmCode(Date asOf)
        {
            for (var d = asOf; ; d += 1)
            {
                if (d.month() % 3 == 0)
                {
                    var imm = Dates.ImmUtil.ThirdWednesday(d.month(), d.year());
                    if (imm > asOf) return $"{Dates.ImmUtil.CodeFor(imm)} ({imm})";
                }
                d = new Date(1, d.month(), d.year()) + new Period(1, TimeUnit.Months) - 1;
            }
        }

        private static bool UseEndOfMonth(Calendar cal, Date effective, Period tenor) =>
            tenor.units() == TimeUnit.Months || tenor.units() == TimeUnit.Years
                ? cal.isEndOfMonth(effective)
                : false;

        // ---------- convention selection ----------

        /// <summary>Pick the IRS convention band for a tenor (AUD: &lt;=3Y => 3M leg, beyond => 6M leg).</summary>
        public static IrsLegConv SelectIrsLeg(IrsConfig irs, Period tenor, Period? floatTenorOverride)
        {
            if (floatTenorOverride != null)
            {
                var byIdx = irs.Legs.FirstOrDefault(l =>
                    TenorUtil.ApproxMonths(TenorUtil.Parse(l.FloatTenor)) == TenorUtil.ApproxMonths(floatTenorOverride));
                if (byIdx != null) return byIdx;
            }
            double months = TenorUtil.ApproxMonths(tenor);
            foreach (var band in irs.Legs)
            {
                if (band.MaxTenor == null) return band;
                if (months <= TenorUtil.ApproxMonths(TenorUtil.Parse(band.MaxTenor)) + 1e-9) return band;
            }
            return irs.Legs.Last();
        }

        /// <summary>The quote family a pillar belongs to: its explicit band tag, else the tenor-rule
        /// band for its own tenor. Callers must exclude FRA pillars first ("3X6" does not parse as a
        /// tenor). Boards and history interpolation must never mix families at one tenor — AUD quotes
        /// BOTH q/q and s/s at 4Y-9Y, ~26bp apart, and a tie-break onto the wrong family shows a level
        /// 26bp off the screen (2026-08-04).</summary>
        public static string PillarBand(IrsConfig irs, PillarDef p) =>
            p.Band ?? SelectIrsLeg(irs, TenorUtil.Parse(p.Tenor), null).FloatTenor;

        /// <summary>Last quoted (non-FRA) pillar, in years, of the SHORT band's ladder — the horizon its
        /// band curve is real to, computed from config alone (mirrors CurveBuilder.BandMaxYears without a
        /// build). Beyond it a short-index leg re-legs to the default convention, so FWCM routing and the
        /// audit must switch surfaces at the same point. DEPO pillars route via the tenor rule here (a 6M
        /// depo credits the short band) — harmless, since only the longest SWAP pillar can drive the max.</summary>
        public static double ShortBandMaxYears(CurrencyConfig cfg)
        {
            var irs = cfg.Irs;
            if (irs == null || irs.Legs.Count < 2) return 0.0;
            string shortTen = irs.Legs[0].FloatTenor;
            double maxY = 0.0;
            foreach (var p in irs.Curve)
            {
                if (!p.Enabled || p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase)) continue;
                var tenor = TenorUtil.Parse(p.Tenor);
                string band = p.Band ?? SelectIrsLeg(irs, tenor, null).FloatTenor;
                if (band.Equals(shortTen, StringComparison.OrdinalIgnoreCase))
                    maxY = Math.Max(maxY, TenorUtil.ApproxMonths(tenor) / 12.0);
            }
            return maxY;
        }

        // ---------- index construction ----------

        public static OvernightIndex MakeOvernightIndex(CurrencyConfig cfg, OisConfig ois, Calendar cal,
            Handle<YieldTermStructure> projection, Date? fixingsFrom = null)
        {
            var idx = new OvernightIndex(
                ois.IndexName, ois.FixingDays, QlMaps.MakeCurrency(cfg.Ccy), cal,
                QlMaps.MakeDayCounter(ois.IndexDcc), projection);
            // Single choke point for past fixings: a seasoned trade has already accrued, and QLNet needs
            // the published overnight prints for every elapsed business day. Only fetched when a caller
            // actually reaches back (fixingsFrom set), so spot-starting trades pay nothing for this.
            if (fixingsFrom is { } from)
                Fixings.Ensure(idx, ois.OnFixingTicker,
                    new DateTime(from.year(), from.month(), from.Day));
            return idx;
        }

        public static IborIndex MakeIborIndex(CurrencyConfig cfg, IrsLegConv leg, Calendar cal,
            Handle<YieldTermStructure> projection, Period? tenorOverride = null)
        {
            var tenor = tenorOverride ?? TenorUtil.Parse(leg.FloatTenor);
            return new IborIndex(
                $"{leg.FloatIndex}{TenorUtil.Format(tenor)}", tenor, leg.FixingDays,
                QlMaps.MakeCurrency(cfg.Ccy), cal, QlMaps.MakeBdc("ModifiedFollowing"),
                endOfMonth: true, QlMaps.MakeDayCounter(leg.FloatDcc), projection);
        }

        // ---------- instrument construction ----------

        /// <summary>Standard OIS: both legs on the same payment schedule; sub-1Y single payment at maturity.</summary>
        public static OvernightIndexedSwap BuildOis(
            CurrencyConfig cfg, OisConfig ois, Calendar cal,
            Date effective, Date maturityUnadjusted, Period tenorForFreqRule,
            double fixedRate, double notional, bool payFixed,
            OvernightIndex onIndex)
        {
            var bdc = QlMaps.MakeBdc(ois.Bdc);
            Frequency freq = QlMaps.MakeFrequency(ois.FixedFreq);

            bool zeroCoupon = false;
            if (!string.IsNullOrEmpty(ois.ShortZeroCouponUnder))
            {
                double cutoffM = TenorUtil.ApproxMonths(TenorUtil.Parse(ois.ShortZeroCouponUnder));
                // Zero-coupon is a MONEY-MARKET convention: it applies to swaps whose money ends
                // within the cutoff of today, not to any short TENOR started years out. An INR 1y1y
                // forward pays semi like every other 2y-maturity swap — pricing it single-pay audited
                // +11.2bp vs S0266 1Y1Y once the 1Y pillar itself was right (2026-08-03; COP at 12%
                // rates was +38.7bp). Spot-starting short pillars, seasoned short remainders and
                // meeting-dated periods all still end inside the window, so they keep the single
                // payment (+1M grace covers spot lag and the MF roll).
                bool tenorShort = TenorUtil.ApproxMonths(tenorForFreqRule) < cutoffM - 1e-9;
                bool endsSoon = (maturityUnadjusted - Settings.evaluationDate()) / 30.4375 < cutoffM + 1.0;
                zeroCoupon = tenorShort && endsSoon;
            }

            Schedule sched = zeroCoupon
                ? new Schedule(effective, maturityUnadjusted, new Period(Frequency.Once), cal, bdc, bdc,
                    DateGeneration.Rule.Backward, false)
                : new Schedule(effective, maturityUnadjusted, QlMaps.PeriodOf(freq), cal, bdc, bdc,
                    DateGeneration.Rule.Backward, UseEndOfMonth(cal, effective, tenorForFreqRule));

            var type = payFixed ? OvernightIndexedSwap.Type.Payer : OvernightIndexedSwap.Type.Receiver;
            return new OvernightIndexedSwap(type, notional, sched, fixedRate,
                QlMaps.MakeDayCounter(ois.FixedDcc), onIndex, 0.0);
        }

        /// <summary>Vanilla fixed-vs-IBOR swap per the selected convention band.</summary>
        public static VanillaSwap BuildIrs(
            CurrencyConfig cfg, IrsConfig irs, IrsLegConv leg, Calendar cal,
            Date effective, Date maturityUnadjusted, Period tenor,
            double fixedRate, double notional, bool payFixed,
            IborIndex iborIndex)
        {
            var bdc = QlMaps.MakeBdc(irs.Bdc);
            bool eom = UseEndOfMonth(cal, effective, tenor);

            var fixedSched = new Schedule(effective, maturityUnadjusted,
                QlMaps.PeriodOf(QlMaps.MakeFrequency(leg.FixedFreq)), cal, bdc, bdc,
                DateGeneration.Rule.Backward, eom);
            var floatSched = new Schedule(effective, maturityUnadjusted,
                QlMaps.PeriodOf(QlMaps.MakeFrequency(leg.FloatFreq)), cal, bdc, bdc,
                DateGeneration.Rule.Backward, eom);

            var type = payFixed ? VanillaSwap.Type.Payer : VanillaSwap.Type.Receiver;
            return new VanillaSwap(type, notional,
                fixedSched, fixedRate, QlMaps.MakeDayCounter(leg.FixedDcc),
                floatSched, iborIndex, 0.0, QlMaps.MakeDayCounter(leg.FloatDcc));
        }

        /// <summary>FRA modelled as a single-period swap on the index (cleared-FRA style).</summary>
        public static VanillaSwap BuildFra(
            CurrencyConfig cfg, IrsConfig irs, IrsLegConv leg, Calendar cal,
            Date asOf, int startMonths, int endMonths,
            double fixedRate, double notional, bool payFixed,
            IborIndex iborIndex)
        {
            var spot = SpotDate(cfg, cal, asOf);
            var bdc = QlMaps.MakeBdc(irs.Bdc);
            var start = cal.advance(spot, new Period(startMonths, TimeUnit.Months), bdc);
            var end = cal.advance(spot, new Period(endMonths, TimeUnit.Months), bdc);
            return BuildFraPeriod(irs, leg, cal, start, end, fixedRate, notional, payFixed, iborIndex);
        }

        /// <summary>FRA on an EXPLICIT start date — an IMM contract date rather than a rolling AxB
        /// offset from spot. The period runs one index tenor from that date. SEK, NOK and DKK quote
        /// their strips this way (IMM quarterly), where AUD/CZK/HUF/NZD/PLN quote rolling AxB.
        /// Shares its construction with <see cref="BuildFra"/>, whose signature and behaviour are
        /// unchanged.</summary>
        public static VanillaSwap BuildFraAt(
            IrsConfig irs, IrsLegConv leg, Calendar cal,
            Date start, Period indexTenor,
            double fixedRate, double notional, bool payFixed,
            IborIndex iborIndex)
        {
            var bdc = QlMaps.MakeBdc(irs.Bdc);
            var end = cal.advance(start, indexTenor, bdc);
            return BuildFraPeriod(irs, leg, cal, start, end, fixedRate, notional, payFixed, iborIndex);
        }

        /// <summary>The single-period swap both FRA builders produce: one fixed flow against one
        /// index fixing over [start, end].</summary>
        private static VanillaSwap BuildFraPeriod(
            IrsConfig irs, IrsLegConv leg, Calendar cal, Date start, Date end,
            double fixedRate, double notional, bool payFixed, IborIndex iborIndex)
        {
            var bdc = QlMaps.MakeBdc(irs.Bdc);
            var sched = new Schedule(start, end, new Period(Frequency.Once), cal, bdc, bdc,
                DateGeneration.Rule.Backward, false);
            var type = payFixed ? VanillaSwap.Type.Payer : VanillaSwap.Type.Receiver;
            return new VanillaSwap(type, notional,
                sched, fixedRate, QlMaps.MakeDayCounter(leg.FixedDcc),
                sched, iborIndex, 0.0, QlMaps.MakeDayCounter(leg.FloatDcc));
        }
    }
}
