using System;
using QLNet;
using RateDesk.Core.Config;
using RateDesk.Core.Dates;
using RateDesk.Core.Pricing;

namespace RateDesk.Core.Curves
{
    /// <summary>
    /// Custom bootstrap helpers that construct pillar instruments through SwapBuilder,
    /// guaranteeing the curve reprices exactly the same instruments the pricer builds
    /// (spot lags, tenor-banded conventions, 28-day schedules, zero-coupon short OIS...).
    /// </summary>
    public sealed class OisPillarHelper : RelativeDateRateHelper
    {
        private readonly CurrencyConfig _cfg;
        private readonly OisConfig _ois;
        private readonly Calendar _cal;
        private readonly Period _tenor;
        private readonly Handle<YieldTermStructure>? _extDiscount;
        private readonly RelinkableHandle<YieldTermStructure> _proj = new();
        private readonly RelinkableHandle<YieldTermStructure> _disc = new();
        private OvernightIndexedSwap _swap = null!;

        public OisPillarHelper(Handle<Quote> quote, CurrencyConfig cfg, OisConfig ois, Calendar cal,
            Period tenor, Handle<YieldTermStructure>? externalDiscount)
            : base(quote)
        {
            _cfg = cfg; _ois = ois; _cal = cal; _tenor = tenor; _extDiscount = externalDiscount;
            initializeDates();
        }

        protected override void initializeDates()
        {
            if (_cfg == null) return; // guard: base ctor may invoke before fields assigned
            var asOf = Settings.evaluationDate();
            var spot = SwapBuilder.SpotDate(_cfg, _cal, asOf);
            var index = SwapBuilder.MakeOvernightIndex(_cfg, _ois, _cal, _proj);
            _swap = SwapBuilder.BuildOis(_cfg, _ois, _cal, spot, SwapBuilder.MaturityDate(spot, _tenor),
                _tenor, 0.0, 1.0, true, index);
            _swap.setPricingEngine(new DiscountingSwapEngine(_disc));
            earliestDate_ = _swap.startDate();
            latestDate_ = _swap.maturityDate();
        }

        public override void setTermStructure(YieldTermStructure t)
        {
            _proj.linkTo(t, false);
            if (_extDiscount == null || _extDiscount.empty()) _disc.linkTo(t, false);
            else _disc.linkTo(_extDiscount.currentLink(), false);
            base.setTermStructure(t);
        }

        public override double impliedQuote()
        {
            _swap.recalculate();
            return _swap.fairRate() ?? 0.0;
        }
    }

    /// <summary>An OIS pillar with FIXED start and end dates rather than spot+tenor - the shape a
    /// meeting-dated OIS actually has.
    ///
    /// <para>A central-bank-dated OIS (USSOFED{N} = FOMC meeting N to meeting N+1) accrues over one
    /// inter-meeting period, so the overnight rate is flat across it and steps on the decision date.
    /// Bootstrapping these instead of a smooth tenor strip is what makes the curve reprice the quoted
    /// meeting OIS: a 1M/2M/3M par strip smears each policy step across the meeting, which measured
    /// -2.1bp against USSOFED1 and +5.2bp against USSOFED2 on 2026-07-30.</para>
    ///
    /// <para>Uses <see cref=RateHelper/> directly, not RelativeDateRateHelper: the dates are absolute
    /// and must NOT be recomputed off the evaluation date.</para></summary>
    public sealed class DatedOisPillarHelper : RateHelper
    {
        private readonly CurrencyConfig _cfg;
        private readonly OisConfig _ois;
        private readonly Calendar _cal;
        private readonly Date _start, _end;
        private readonly Handle<YieldTermStructure>? _extDiscount;
        private readonly RelinkableHandle<YieldTermStructure> _proj = new();
        private readonly RelinkableHandle<YieldTermStructure> _disc = new();
        private OvernightIndexedSwap _swap = null!;

        public DatedOisPillarHelper(Handle<Quote> quote, CurrencyConfig cfg, OisConfig ois, Calendar cal,
            Date start, Date end, Handle<YieldTermStructure>? externalDiscount)
            : base(quote)
        {
            _cfg = cfg; _ois = ois; _cal = cal; _start = start; _end = end; _extDiscount = externalDiscount;
            Build();
        }

        private void Build()
        {
            var index = SwapBuilder.MakeOvernightIndex(_cfg, _ois, _cal, _proj);
            // one inter-meeting period, so a single coupon at maturity: pass the period as the tenor so
            // the short-ZC rule applies and no interior payment is invented
            var tenor = new Period(_end - _start, TimeUnit.Days);
            _swap = SwapBuilder.BuildOis(_cfg, _ois, _cal, _start, _end, tenor, 0.0, 1.0, true, index);
            _swap.setPricingEngine(new DiscountingSwapEngine(_disc));
            earliestDate_ = _swap.startDate();
            latestDate_ = _swap.maturityDate();
        }

        public override void setTermStructure(YieldTermStructure t)
        {
            _proj.linkTo(t, false);
            if (_extDiscount == null || _extDiscount.empty()) _disc.linkTo(t, false);
            else _disc.linkTo(_extDiscount.currentLink(), false);
            base.setTermStructure(t);
        }

        public override double impliedQuote()
        {
            _swap.recalculate();
            return _swap.fairRate() ?? 0.0;
        }
    }

    public sealed class IrsPillarHelper : RelativeDateRateHelper
    {
        private readonly CurrencyConfig _cfg;
        private readonly IrsConfig _irs;
        private readonly Calendar _cal;
        private readonly Period _tenor;
        private readonly Handle<YieldTermStructure>? _extDiscount;
        private readonly IrsLegConv? _legOverride;
        private readonly RelinkableHandle<YieldTermStructure> _proj = new();
        private readonly RelinkableHandle<YieldTermStructure> _disc = new();
        private VanillaSwap _swap = null!;

        public IrsPillarHelper(Handle<Quote> quote, CurrencyConfig cfg, IrsConfig irs, Calendar cal,
            Period tenor, Handle<YieldTermStructure>? externalDiscount, IrsLegConv? legOverride = null)
            : base(quote)
        {
            _cfg = cfg; _irs = irs; _cal = cal; _tenor = tenor; _extDiscount = externalDiscount;
            _legOverride = legOverride;
            initializeDates();
        }

        protected override void initializeDates()
        {
            if (_cfg == null) return;
            var asOf = Settings.evaluationDate();
            var spot = SwapBuilder.SpotDate(_cfg, _cal, asOf);
            var leg = _legOverride ?? SwapBuilder.SelectIrsLeg(_irs, _tenor, null);
            var index = SwapBuilder.MakeIborIndex(_cfg, leg, _cal, _proj);
            _swap = SwapBuilder.BuildIrs(_cfg, _irs, leg, _cal, spot, SwapBuilder.MaturityDate(spot, _tenor),
                _tenor, 0.0, 1.0, true, index);
            _swap.setPricingEngine(new DiscountingSwapEngine(_disc));
            earliestDate_ = _swap.startDate();
            latestDate_ = _swap.maturityDate();
        }

        public override void setTermStructure(YieldTermStructure t)
        {
            _proj.linkTo(t, false);
            if (_extDiscount == null || _extDiscount.empty()) _disc.linkTo(t, false);
            else _disc.linkTo(_extDiscount.currentLink(), false);
            base.setTermStructure(t);
        }

        public override double impliedQuote()
        {
            _swap.recalculate();
            return _swap.fairRate();
        }
    }
}
