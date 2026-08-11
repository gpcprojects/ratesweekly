using System;
using System.Collections.Generic;
using System.Linq;
using QLNet;
using RateDesk.Core.Analytics;
using RateDesk.Core.Config;
using RateDesk.Core.Curves;
using RateDesk.Core.Dates;
using RateDesk.Core.Market;
using RateDesk.Core.Pricing;
using RateDesk.Core.Query;
using RateDesk.Core.Trades;

namespace RateDesk.Core
{
    public sealed partial class PricingService
    {
        private IndexRegistry? _registry;
        private QueryParser? _parser;

        private IHistoryProvider? _history;
        /// <summary>Daily history provider. Also feeds <see cref="Pricing.Fixings"/>, which is what lets a
        /// SEASONED trade price — without a provider the elapsed accrual has no published fixings to use.</summary>
        public IHistoryProvider? History
        {
            get => _history;
            set { _history = value; Pricing.Fixings.Source = value; Pricing.Fixings.Reset(); }
        }
        /// <summary>Display/stats window. The provider always fetches 5y so changing this is instant.</summary>
        public int HistoryLookbackDays { get; set; } = 730;
        private const int FetchDays = 1825; // 5y — the cache superset every lookback slices from

        // despiked series cached per day AND per fetch window — the Hampel filter over years of
        // dailies is too expensive to re-run 10-15x on every 600ms live tick, and a 10y request
        // (CORR chart) must not be served from a 5y cache entry
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime day, int days, IReadOnlyList<HistPoint> data)>
            _despiked = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>All history flows through the despike filter (bad prints removed, real moves kept),
        /// then is sliced to the active lookback window. full: true skips the slice — stats must be
        /// computed on the whole fetch window; only CHART series follow the lookback.</summary>
        private IReadOnlyList<HistPoint> Hist(string ticker, bool full = false)
        {
            var data = HistStats(ticker);
            return full ? data : SliceLookback(data);
        }

        /// <summary>Unsliced despiked history — the input for every SeriesStats/regression computation.</summary>
        private IReadOnlyList<HistPoint> HistStats(string ticker)
        {
            if (History == null) return Array.Empty<HistPoint>();
            if (!_despiked.TryGetValue(ticker, out var c) || c.day != DateTime.Today || c.days < FetchDays)
            {
                var clean = HistoryFilter.Despike(History.GetDaily(ticker, FetchDays));
                c = (DateTime.Today, FetchDays, clean);
                if (clean.Count > 0) _despiked[ticker] = c; // never cache a transient failure
            }
            return c.data;
        }

        /// <summary>Chart copy of a full-window series: sliced to the active lookback.</summary>
        private IReadOnlyList<HistPoint> SliceLookback(IReadOnlyList<HistPoint> full)
        {
            if (HistoryLookbackDays >= FetchDays || full.Count == 0) return full;
            var cutoff = DateTime.Today.AddDays(-HistoryLookbackDays);
            return full.Where(p => p.Date >= cutoff).ToList();
        }

        public IndexRegistry Registry => _registry ??= new IndexRegistry(Configs);
        public QueryParser Parser => _parser ??= new QueryParser(Registry);

        public ParsedQuery ParseQuery(string text)
        {
            // Meeting-dated grammar first: "jul fomc", "jul sep boe", "usd july fomc $25k",
            // "usd jul fomc 5y". Sizing tokens are stripped BEFORE the grammar sees them: any token
            // that was neither a month nor a CB alias used to fail the whole meeting parse, after
            // which QueryParser (which has no CB/month vocabulary) blamed the month — the misleading
            // "Cannot parse 'july'". The length gate now only ever counts grammar tokens, too.
            var toks = Dates.DateUtil.Normalize(text)
                .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            SizingRequest? sizing = null;
            List<string> grammar = new(toks);
            try { sizing = SizingTokens.Extract(toks, out grammar); }
            catch (FormatException) { /* a malformed size: let the full parser own the error */ }

            if (sizing != null
                && MeetingQuery.TryParse(grammar, out var run, out var months, out var mccy, out var anchorTenor))
            {
                if (anchorTenor != null)
                    return MeetingAnchoredQuery(text, run, months[0], anchorTenor, mccy, sizing);

                var mq = new ParsedQuery
                {
                    Raw = text,
                    Shape = months.Count switch { 1 => QueryShape.Outright, 2 => QueryShape.Spread, _ => QueryShape.Fly },
                    Target = new CurveTarget(mccy, TargetKind.PrimaryOis, null, ProductKind.OIS),
                    MeetingRun = run,
                    MeetingMonths = months,
                };
                ApplySizing(mq, sizing);
                return mq;
            }
            // the ORIGINAL text, unstripped: QueryParser's tenor-vs-notional disambiguation has to see
            // every token, so the non-meeting path stays provably unchanged
            return Parser.Parse(text);
        }

        /// <summary>"usd jul fomc 5y" — an ordinary swap anchored on a meeting date.
        ///
        /// <para>Reuses <see cref="StartKind.Date"/>, which already does "anchor date + tenor,
        /// business-day adjusted, normal swap build" for "aud 25-jun-31 +5y". No new StartKind.</para>
        ///
        /// <para>MeetingRun/MeetingMonths stay NULL deliberately: four separate places use
        /// <c>MeetingRun != null</c> as a proxy for "meeting query with no Legs" (TickersForQuery's
        /// meeting-ticker branch, PricingServiceCorr, and two blotter repricing gates). Leaving them
        /// null makes all four do the right thing with zero changes, and Analyze needs no new branch —
        /// it falls straight into AnalyzeStructure.</para></summary>
        private ParsedQuery MeetingAnchoredQuery(string raw, string run, (int Month, int? Year) meeting,
            Period tenor, string ccy, SizingRequest sizing)
        {
            var when = MeetingDateFor(run, meeting.Month, meeting.Year, out var label);
            var pq = new ParsedQuery
            {
                Raw = raw,
                Shape = QueryShape.Outright,
                Target = new CurveTarget(ccy, TargetKind.PrimaryOis, null, ProductKind.OIS),
                AnchorRun = run,
                AnchorMeeting = meeting,
            };
            pq.Legs.Add(new Leg
            {
                StartKind = StartKind.Date,
                ExplicitStart = new Date(when.Day, (Month)when.Month, when.Year),
                Tenor = tenor,
                MeetingLabel = label,
            });
            ApplySizing(pq, sizing);
            return pq;
        }

        /// <summary>Push extracted sizing onto a query built outside QueryParser. Without this the
        /// meeting paths would quietly price at the flat notional while every other unsized query
        /// sizes to the desk dv01.</summary>
        private static void ApplySizing(ParsedQuery q, SizingRequest sizing)
        {
            if (sizing.Notional.HasValue) q.Notional = sizing.Notional.Value;
            if (sizing.Dv01.HasValue)
            {
                q.Dv01Target = sizing.Dv01.Value;
                q.Dv01Ccy = sizing.Dv01Ccy;
            }
            else if (!sizing.Notional.HasValue)
            {
                q.Dv01Target = Risk.RiskSizer.DefaultDv01Usd;
                q.Dv01Ccy = "USD";
            }
        }

        /// <summary>Flag the pillars behind a result that are being re-stamped rather than traded.
        /// Judged against the currency's OWN curve (see <see cref="Market.Staleness"/>), so every market
        /// self-flags when it goes dark instead of NZD being special-cased.</summary>
        private void FlagStaleQuotes(ParsedQuery pq, CurrencyConfig cfg, string source, InstrumentResult r)
        {
            try
            {
                var quotes = new List<(string, string, Market.QuoteData?)>();
                // judge the curve the trade actually PRICED on: a meeting-dated USD trade lives on the
                // FedFunds strip, so SOFR's pillars say nothing about whether its market is dark
                if (pq.Target.IsLadder || PolicyLadderFor(pq, cfg) != null)
                {
                    var lad = PolicyLadderFor(pq, cfg) ?? cfg.Ladders.FirstOrDefault(l =>
                        l.Name.Equals(pq.Target.LadderName, StringComparison.OrdinalIgnoreCase));
                    foreach (var p in lad?.Pillars.Where(p => p.Enabled) ?? Enumerable.Empty<PillarDef>())
                    {
                        var t = ConfigStore.ResolveTicker(p.Ticker, "");
                        quotes.Add((t, $"{lad!.Name} {p.Tenor}", Snapshot.Get(t)));
                    }
                }
                else
                {
                    var product = ResolveProductForTarget(pq.Target, cfg);
                    var list = product == ProductKind.OIS ? cfg.Ois?.Curve : cfg.Irs?.Curve;
                    // dual-band markets have two quotes per tenor — name the band or "AUD 5Y" is
                    // ambiguous between the s/s screen quote and the (thinner) q/q run
                    bool multi = product == ProductKind.IRS && cfg.Irs?.Legs.Count > 1;
                    foreach (var p in list?.Where(p => p.Enabled
                                 && !p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase)
                                 && !p.Type.Equals("DEPO", StringComparison.OrdinalIgnoreCase))
                             ?? Enumerable.Empty<PillarDef>())
                    {
                        var t = ConfigStore.ResolveTicker(p.Ticker, source);
                        string label = multi
                            ? $"{cfg.Ccy} {p.Tenor} vs{SwapBuilder.PillarBand(cfg.Irs!, p)}"
                            : $"{cfg.Ccy} {p.Tenor}";
                        quotes.Add((t, label, Snapshot.Get(t)));
                    }
                }
                if (quotes.Count == 0) return;
                // the DENOMINATOR matters as much as the flags: one stale point on a 30-pillar curve is
                // the normal ragged edge, the whole strip going quiet is a dark market
                r.StaleAssessed = quotes.Count(x => x.Item3 != null);
                r.StaleQuotes.AddRange(Market.Staleness.Assess(quotes, cfg.StaleQuoteMinutes));
            }
            catch { /* flagging is advisory — never fail an analysis over it */ }
        }

        /// <summary>A mid-curve pillar root (no source) suitable for live source discovery.</summary>
        public string? RepresentativeRoot(CurrencyConfig cfg)
        {
            // prefer the DEFAULT product's family (the market that actually trades on multiple
            // sources), and skip full securities ("CKSO5 Curncy") — they can't take a source infix
            var lists = cfg.DefaultProduct.Equals("IRS", StringComparison.OrdinalIgnoreCase)
                ? new[] { cfg.Irs?.Curve, cfg.Ois?.Curve }
                : new[] { cfg.Ois?.Curve, cfg.Irs?.Curve };
            foreach (var list in lists)
            {
                if (list == null) continue;
                var swap = list.FirstOrDefault(p => p.Enabled && !p.Ticker.Contains(' ') && p.Tenor is "5Y" or "10Y"
                                                     && !p.Type.Equals("DEPO", StringComparison.OrdinalIgnoreCase))
                           ?? list.FirstOrDefault(p => p.Enabled && !p.Ticker.Contains(' ')
                                                        && !p.Type.Equals("DEPO", StringComparison.OrdinalIgnoreCase));
                if (swap != null) return swap.Ticker;
            }
            var lad = cfg.Ladders.FirstOrDefault();
            var pillars = lad?.Pillars.Where(p => p.Enabled && !p.Ticker.Contains(' ')).ToList();
            if (pillars == null || pillars.Count == 0) return null;
            return pillars[pillars.Count / 2].Ticker; // pillars run short→long; the middle is mid-curve
        }

        // ---------- ticker requirements ----------

        public IEnumerable<string> TickersForQuery(ParsedQuery pq)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // a cross-market query snapshots BOTH sides' universes
            if (pq.Cross is { } xb)
                foreach (var t in TickersForQuery(xb)) set.Add(t);
            var cfg = Configs.Get(pq.Target.Ccy);
            var src = pq.Source ?? SourceFor(pq.Target.Ccy);

            // A meeting-dated trade prices on the policy ladder's strip, so its pillars have to be in the
            // snapshot or the curve cannot build and the trade silently falls back to the wrong index.
            // Ladder pillars are read with an EMPTY source, matching LadderParCurve's own build. The
            // MEETING-dated tickers matter just as much: without them the curve bootstraps off the smooth
            // tenor strip alone and smears every policy step across its decision date.
            if (PolicyLadderFor(pq, cfg) is { } pol)
            {
                foreach (var p in pol.Pillars.Where(p => p.Enabled))
                    set.Add(ConfigStore.ResolveTicker(p.Ticker, ""));
                foreach (var psched in MeetingsStore.Schedules.Where(x =>
                             x.Ccy.Equals(cfg.Ccy, StringComparison.OrdinalIgnoreCase)
                             && pol.Name.Equals(x.PolicyLadder, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var pat in psched.Tickers)
                        for (int n = 0; n <= 13; n++)
                            set.Add(MeetingTick(psched, pat, n));
                    if (!string.IsNullOrEmpty(psched.RefTicker)) set.Add(psched.RefTicker);
                }
            }

            if (pq.MeetingRun != null)
            {
                var sched = MeetingsStore.Schedules.First(s => s.Name == pq.MeetingRun);
                foreach (var pat in sched.Tickers)
                    for (int n = 0; n <= 13; n++)
                        set.Add(MeetingTick(sched, pat, n));
                if (!string.IsNullOrEmpty(sched.RefTicker)) set.Add(sched.RefTicker);
                foreach (var t in TickersWithDiscount(cfg, src)) set.Add(t);
                if (!string.IsNullOrEmpty(sched.FuturesPattern))
                    foreach (var t in MeetingTickers()) set.Add(t); // includes the futures strip
                // the dv01 target is a USD input: a non-USD meeting needs its spot to size at all
                if (!cfg.Ccy.Equals("USD", StringComparison.OrdinalIgnoreCase)) set.Add($"{cfg.Ccy} Curncy");
                if (!pq.Dv01Ccy.Equals(cfg.Ccy, StringComparison.OrdinalIgnoreCase)) set.Add($"{pq.Dv01Ccy} Curncy");
                return set;
            }

            // a meeting-ANCHORED swap resolves its start date off ticker MATURITY fields where they
            // exist (they are what the board itself shows) and only falls back to meetings.json
            // otherwise — so the run's tickers have to be in the snapshot
            if (pq.AnchorRun != null
                && MeetingsStore.Schedules.FirstOrDefault(s => s.Name == pq.AnchorRun) is { } aSched)
                foreach (var pat in aSched.Tickers)
                    for (int n = 0; n <= 13; n++)
                        set.Add(MeetingTick(aSched, pat, n));

            if (pq.Target.IsLadder)
            {
                var lad = cfg.Ladders.First(l => l.Name.Equals(pq.Target.LadderName, StringComparison.OrdinalIgnoreCase));
                foreach (var p in lad.Pillars.Where(p => p.Enabled && !p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase)))
                    set.Add(ConfigStore.ResolveTicker(p.Ticker, ""));
                if (!string.IsNullOrEmpty(lad.FixingTicker)) set.Add(lad.FixingTicker);
                // a ZC inflation swap's dv01 discounts on the nominal OIS curve, so that curve's own
                // quotes must be in the snapshot as well — without them there is no DF and the dv01
                // degrades to an openly-labelled undiscounted number. RATE ladders (Fed Funds, DI)
                // are undiscounted this pass and deliberately don't pull the extra tickers.
                if (lad.Kind.Equals("INFLATION", StringComparison.OrdinalIgnoreCase))
                    foreach (var t in TickersWithDiscount(cfg, src)) set.Add(t);
                if (pq.DatedCode != null && !string.IsNullOrEmpty(lad.DatedPattern))
                    set.Add(lad.DatedPattern.Replace("{MY}", pq.DatedCode));
                if (pq.Main?.StartKind == StartKind.Forward && pq.Main.ForwardStart != null
                    && pq.Main.Tenor != null && !string.IsNullOrEmpty(lad.FwdTickerPattern))
                {
                    double a = TenorUtil.ApproxMonths(pq.Main.ForwardStart) / 12.0;
                    double tn = TenorUtil.ApproxMonths(pq.Main.Tenor) / 12.0;
                    if (Math.Abs(a - Math.Round(a)) < 1e-6 && Math.Abs(tn - Math.Round(tn)) < 1e-6)
                        set.Add(lad.FwdTickerPattern.Replace("{A}", ((int)Math.Round(a)).ToString())
                                                    .Replace("{B}", ((int)Math.Round(tn)).ToString()));
                }
                // FX spot for the $01/NET DV01 USD conversion — ladder-only ccys (BRL) need it too
                if (!cfg.Ccy.Equals("USD", StringComparison.OrdinalIgnoreCase)) set.Add($"{cfg.Ccy} Curncy");
                return set;
            }

            foreach (var t in TickersWithDiscount(cfg, src)) set.Add(t);
            if (cfg.Ois != null)
                foreach (var v in cfg.Ois.Variants)
                    foreach (var p in v.Pillars.Where(p => p.Enabled && !p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase)))
                        set.Add(ConfigStore.ResolveTicker(p.Ticker, ""));

            // FWCM forward tickers for any forward-shaped legs — including the four ROLL-horizon
            // variants, so the analysis prefetch covers every BDH the overlays will read (a BDH
            // round-trip inside the _gate lock would stall the whole app)
            var product = ResolveProductForTarget(pq.Target, cfg);
            var fwdId = FwdCurveIdFor(cfg, product);
            if (!string.IsNullOrEmpty(fwdId))
            {
                var today = new Date(DateTime.Today.Day, (Month)DateTime.Today.Month, DateTime.Today.Year);
                foreach (var leg in pq.Legs)
                {
                    var fwdStyle = FwdStyleFor(cfg, product);
                    foreach (var t in FwcmCandidates(FwdIdForLeg(cfg, product, pq, pq.Legs.IndexOf(leg), fwdId), fwdStyle, leg)) set.Add(t);
                    foreach (var (_, h) in RollHorizons)
                        if (RolledLeg(leg, h, today) is { } rl)
                            foreach (var t in FwcmCandidates(fwdId, fwdStyle, rl))
                                set.Add(t);
                }
            }

            // FX spots: the trade ccy always (the $01 tile converts local dv01 to USD),
            // plus the dv01-input ccy when it differs
            if (!cfg.Ccy.Equals("USD", StringComparison.OrdinalIgnoreCase)) set.Add($"{cfg.Ccy} Curncy");
            if ((pq.Dv01Target.HasValue || pq.LegDv01s != null)
                && !pq.Dv01Ccy.Equals("USD", StringComparison.OrdinalIgnoreCase))
                set.Add($"{pq.Dv01Ccy} Curncy");
            return set;
        }

        // ---------- entry points ----------

        public InstrumentResult Analyze(string text) => Analyze(ParseQuery(text));

        public InstrumentResult Analyze(ParsedQuery pq)
        {
            if (pq.Cross != null) return AnalyzeCross(pq);
            if (pq.MeetingRun != null) return AnalyzeMeeting(pq);
            // one batched BDH round-trip warms every history this analysis can touch (no-op when cached)
            try { if (!pq.SkipHistory) History?.Prefetch(TickersForQuery(pq), FetchDays); } catch { /* per-ticker fallback */ }
            // the anchor date was resolved off config/meetings.json at PARSE time, when no snapshot
            // existed yet. Now that the run's tickers are warm their MATURITY fields are authoritative
            // — and are what the meetings board itself shows — so re-resolve to keep the two agreeing.
            if (pq.AnchorRun != null && pq.AnchorMeeting is { } am
                && pq.Main is { StartKind: StartKind.Date } anchorLeg)
            {
                try
                {
                    var refined = MeetingDateFor(pq.AnchorRun, am.Month, am.Year, out var refinedLabel);
                    anchorLeg.ExplicitStart = new Date(refined.Day, (Month)refined.Month, refined.Year);
                    anchorLeg.MeetingLabel = refinedLabel;
                }
                catch { /* keep the schedule-derived date */ }
            }

            lock (_gate)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var cfg = Configs.Get(pq.Target.Ccy);
                var source = pq.Source ?? SourceFor(pq.Target.Ccy);
                if (pq.DatedCode != null && !pq.Target.IsLadder)
                {
                    var datedLad = cfg.Ladders.FirstOrDefault(l => !string.IsNullOrEmpty(l.DatedPattern));
                    if (datedLad != null)
                        pq.Target = new CurveTarget(cfg.Ccy, TargetKind.Ladder, datedLad.Name, ProductKind.OIS);
                    else
                        throw new InvalidOperationException(
                            "dated month codes (jan27/f31) are only supported for dated-contract curves (BRL DI).");
                }
                // "5y ff" / "1m fedfunds" FORCES the index: price a real swap on that strip rather than
                // read the ladder's par quote. It matters now the strip is meeting-stepped — a 1M Fed Funds
                // swap spanning an FOMC date is not the smooth USSOA quote. "5y sofr" already forced the
                // primary OIS curve, so this is the other half of the pair.
                //
                // Restricted to a declared POLICY ladder on purpose. The CPI ladder is a zero-coupon
                // inflation strip and BRL DI needs its BUS/252 engine — neither is an OIS par swap, and
                // rewriting them into one would silently misprice both.
                if (pq.Target.IsLadder && pq.Legs.Count > 0
                    && cfg.Ladders.FirstOrDefault(l =>
                           l.Name.Equals(pq.Target.LadderName, StringComparison.OrdinalIgnoreCase)) is { } forced
                    && MeetingsStore.Schedules.Any(x =>
                           x.Ccy.Equals(cfg.Ccy, StringComparison.OrdinalIgnoreCase)
                           && forced.Name.Equals(x.PolicyLadder, StringComparison.OrdinalIgnoreCase)))
                {
                    pq.CurveLadder = forced.Name;
                    pq.Target = new CurveTarget(cfg.Ccy, TargetKind.PrimaryOis, null, ProductKind.OIS);
                }

                InstrumentResult r = pq.Target.IsLadder
                    ? AnalyzeLadder(pq, cfg)
                    : AnalyzeStructure(pq, cfg, source);
                ApplyMidOverride(pq, r); // no-op when already applied ahead of the stats pass
                FlagStaleQuotes(pq, cfg, source, r);
                r.ElapsedMs = sw.Elapsed.TotalMilliseconds;
                return r;
            }
        }

        /// <summary>CROSS-MARKET (BETA): price each side independently through the full Analyze path,
        /// order larger-minus-smaller so the spread prints positive, difference the FULL history
        /// series for stats, difference the roll profiles, and carry both sides' legs (side B weights
        /// flipped) so the FWCM cross-check stays per-leg. No cross dv01 sizing — each side keeps its
        /// own conventions and risk; the blotter books one row per side.</summary>
        private InstrumentResult AnalyzeCross(ParsedQuery pq)
        {
            var right = pq.Cross ?? throw new InvalidOperationException("not a cross query");
            right.SkipHistory = pq.SkipHistory;    // blotter's level-only refresh applies to BOTH sides
            InstrumentResult ra, rb;
            var crossOvr = pq.MidOverride;         // a mid o'ride belongs to the CROSS level — a bp
            pq.MidOverride = null;                 // spread applied to side A as a % rate is garbage
            pq.Cross = null;                       // detach so the side analyses don't recurse
            try { ra = Analyze(pq); rb = Analyze(right); }
            finally { pq.Cross = right; pq.MidOverride = crossOvr; }
            var qa = pq; var qb = right;

            if (ra.Unit != rb.Unit)
                throw new FormatException(
                    $"cross-market sides must be the same shape — {ra.Label} quotes in {ra.Unit} but {rb.Label} in {rb.Unit} "
                    + "(outright vs outright, curve vs curve, fly vs fly).");
            if (ra.Mid is not double ma || rb.Mid is not double mb)
                throw new InvalidOperationException("cross-market: one side has no live mid.");

            // larger yield minus smaller yield — the cross always prints positive AT ENTRY, and the
            // ordering PINS for the day per query: two near-equal sides must not flip the chart's
            // sign between live ticks (the spread itself may go negative intraday, which is honest)
            bool aLarger;
            var orderKey = pq.Raw.Trim().ToLowerInvariant();
            if (_crossOrderPins.TryGetValue(orderKey, out var pin) && pin.day == DateTime.Today)
                aLarger = pin.aLarger;
            else
                _crossOrderPins[orderKey] = (DateTime.Today, aLarger = ma >= mb);
            if (!aLarger)
            {
                (ra, rb) = (rb, ra);
                (qa, qb) = (qb, qa);
                (ma, mb) = (mb, ma);
            }
            double scale = ra.Unit == "%" ? 100.0 : 1.0;   // outright sides in % -> cross in bp

            var res = new InstrumentResult
            {
                Query = pq.Raw,
                Label = $"{ra.Label} − {rb.Label}",
                Ccy = $"{ra.Ccy}/{rb.Ccy}",
                Kind = "Cross",
                Unit = "bp",
                Source = ra.Source.Equals(rb.Source, StringComparison.OrdinalIgnoreCase)
                    ? ra.Source : $"{ra.Source}/{rb.Source}",
                ConventionSummary = $"X-MKT: [{ra.Ccy}] {ra.ConventionSummary}  MINUS  [{rb.Ccy}] {rb.ConventionSummary}",
            };
            res.IsCross = true;
            res.CrossSides = (qa.Raw, qb.Raw);
            res.Mid = (ma - mb) * scale;
            ApplyMidOverride(pq, res);             // cross-level o'ride: stats score at the entered bp level

            // roll: shared horizons difference (positive still = rolls in the spread's favour)
            var rbRoll = rb.RollBp.ToDictionary(k => k.Key, v => v.Value);
            foreach (var kv in ra.RollBp)
                if (rbRoll.TryGetValue(kv.Key, out var o))
                    res.RollBp.Add(new KeyValuePair<string, double>(kv.Key, kv.Value - o));

            // legs: side A as priced, side B flipped — FWCM columns stay per-leg and per-market
            foreach (var l in ra.Legs) res.Legs.Add(l);
            foreach (var l in rb.Legs) { l.Weight = -l.Weight; res.Legs.Add(l); }

            // history & stats on the FULL differenced series; chart gets the lookback slice
            var ha = ra.FullHistory ?? ra.History;
            var hb = rb.FullHistory ?? rb.History;
            if (ha.Count >= 10 && hb.Count >= 10)
            {
                var comb = CombineSeries(new List<IReadOnlyList<HistPoint>> { ha, hb },
                    new[] { scale, -scale }, scaleToBp: false);
                if (comb.Count >= 10)
                {
                    res.FullHistory = comb;
                    res.History = SliceLookback(comb);
                    res.Stats = SeriesStats.Compute(comb, liveLast: res.Mid, changeScale: 1.0,
                        basisRef: res.MidTrue ?? res.Mid);
                    if (res.Stats?.SuppressReason is string cw) res.Notes.Add($"level stats withheld: {cw}.");
                    // exact Δ1d from the sides' own prev-close computations where both exist
                    if (res.Stats != null && ra.Stats?.Chg1d is double c1 && rb.Stats?.Chg1d is double c2)
                        res.Stats.Chg1d = c1 - c2;
                    // hedge ratio: side A daily changes on side B daily changes, 1y window
                    var (beta, r2) = CrossBeta(ha, hb);
                    if (beta.HasValue) { res.EmpBeta = beta; res.EmpBetaR2 = r2; }
                }
            }
            else res.Notes.Add("cross history unavailable — one side has no usable series.");

            res.StaleQuotes.AddRange(ra.StaleQuotes.Concat(rb.StaleQuotes));
            res.StaleAssessed = ra.StaleAssessed + rb.StaleAssessed;
            res.Notes.Add($"CROSS-MARKET BETA — sides priced independently and quoted larger-minus-smaller "
                + $"({ra.Label} over {rb.Label}); no cross dv01 sizing; hedge each side in its own market.");
            foreach (var n in ra.Notes.Concat(rb.Notes)) res.Notes.Add(n);
            res.ElapsedMs = ra.ElapsedMs + rb.ElapsedMs;
            return res;
        }

        /// <summary>OLS slope + R² of side-A daily changes on side-B daily changes, last ~1y of
        /// overlapping dates. Beta for the cross's hedge ratio readout.</summary>
        private static (double? beta, double? r2) CrossBeta(IReadOnlyList<HistPoint> a, IReadOnlyList<HistPoint> b)
        {
            var byDate = b.ToDictionary(p => p.Date, p => p.Value);
            var pairs = new List<(double da, double db)>();
            (DateTime d, double va, double vb)? prev = null;
            foreach (var p in a)
            {
                if (!byDate.TryGetValue(p.Date, out var vb)) continue;
                if (prev is { } pr) pairs.Add((p.Value - pr.va, vb - pr.vb));
                prev = (p.Date, p.Value, vb);
            }
            if (pairs.Count > 260) pairs = pairs.Skip(pairs.Count - 260).ToList();
            if (pairs.Count < 60) return (null, null);
            double mb2 = pairs.Average(x => x.db), ma2 = pairs.Average(x => x.da);
            double cov = pairs.Sum(x => (x.da - ma2) * (x.db - mb2));
            double varB = pairs.Sum(x => (x.db - mb2) * (x.db - mb2));
            double varA = pairs.Sum(x => (x.da - ma2) * (x.da - ma2));
            if (varB < 1e-12 || varA < 1e-12) return (null, null);
            double beta = cov / varB;
            double r = cov / Math.Sqrt(varA * varB);
            return (beta, r * r);
        }

        public ForwardGridResult ForwardGridFor(string ccy, string? source = null, ProductKind? product = null)
        {
            lock (_gate)
            {
                var cfg = Configs.Get(ccy);
                var src = source ?? SourceFor(ccy);
                var curves = GetCurvesUnlocked(cfg, src);
                Settings.setEvaluationDate(curves.AsOf);
                var prod = product ?? ResolveProductForTarget(
                    Registry.TryResolve(ccy, out var t) ? t : new CurveTarget(ccy, TargetKind.PrimaryOis, null, ProductKind.OIS), cfg);
                return ForwardGrid.Build(curves, prod);
            }
        }

        /// <summary>Mid o'ride: swap the user's entered level in for the curve mid BEFORE stats are
        /// computed, so z/Δ/percentile/range/breakeven all score the entry level. The real curve mid
        /// is preserved on MidTrue for the headline boxes. Applied at most once per result.</summary>
        private static void ApplyMidOverride(ParsedQuery pq, InstrumentResult r)
        {
            if (pq.MidOverride is not double ov || r.MidTrue != null || r.Mid is not double tm) return;
            r.MidTrue = tm;
            r.Mid = ov;
            r.Notes.Add($"MID O'RIDE {ov:0.####}{(r.Unit == "%" ? "%" : "bp")} — stats scored at the entered level (curve {tm:0.####})");
        }

        /// <summary>bp shift the mid o'ride applied (0 when inactive) — the "exact Δ 1d from prev
        /// close" overwrites add this so Δ 1d stays (entered level − prev close) under an o'ride.</summary>
        private static double OvrShiftBp(InstrumentResult r) =>
            r.MidTrue is double t && r.Mid is double m ? (m - t) * (r.Unit == "%" ? 100.0 : 1.0) : 0.0;

        // ---------- unified structure analytics (1-3 legs) ----------

        private InstrumentResult AnalyzeStructure(ParsedQuery pq, CurrencyConfig cfg, string source)
        {
            if (cfg.Ois == null && cfg.Irs == null)
                return AnalyzeLadderFallback(pq, cfg);

            // A meeting-dated USD trade is a Fed Funds trade, not a SOFR one: the board's own USSOFED{N}
            // tickers and its FEDL01 reference are EFFR, and FF/SOFR OIS sit ~2bp apart at 3M. Tenor swaps
            // and forwards are untouched — they stay on the currency's OIS curve.
            var policyLadder = PolicyLadderFor(pq, cfg);
            CurveSet? policyCurves = policyLadder != null ? LadderParCurve(cfg, policyLadder, source) : null;
            var curves = policyCurves ?? GetCurvesUnlocked(cfg, source);
            Settings.setEvaluationDate(curves.AsOf);
            var product = ResolveProductForTarget(pq.Target, cfg);
            if (pq.ProductExplicit && product != pq.Target.Product)
                throw new InvalidOperationException(
                    $"{cfg.Ccy} has no {pq.Target.Product} curve configured — " +
                    $"it prices from {product} ({(product == ProductKind.IRS ? cfg.Irs?.Legs.FirstOrDefault()?.FloatIndex : cfg.Ois?.IndexName)}).");
            if (product == ProductKind.OIS && curves.Ois == null)
                throw new InvalidOperationException(
                    $"{cfg.Ccy}: no live {cfg.Ois?.IndexName} OIS quotes on {source} right now — OIS curve not built.");

            // FRA shape validation, config-derived so it can't drift from the bootstrapper. Deliberately
            // here rather than in the parser: QueryParser stays config-agnostic and accepts the SHAPE,
            // exactly as it does for every other product.
            if (pq.Legs.Count > 0 && pq.Legs[0].IsFra)
            {
                bool rolling = pq.Legs[0].FraStartMonths.HasValue;
                if (cfg.Irs == null)
                    throw new InvalidOperationException(
                        $"{cfg.Ccy} has no IBOR conventions — FRAs need an IRS curve, and {cfg.Ccy} prices off OIS only.");
                if (rolling && !CurveBuilder.HasRollingFraPillars(cfg))
                    throw new InvalidOperationException(
                        $"{cfg.Ccy} FRAs are IMM-dated, not rolling — use an IMM contract, e.g. \"{cfg.Ccy.ToLowerInvariant()} u26 fra\".");
                if (!rolling && !CurveBuilder.HasImmFraStrip(cfg))
                    throw new InvalidOperationException(
                        $"{cfg.Ccy} FRAs are rolling, not IMM-dated — use AxB months, e.g. \"{cfg.Ccy.ToLowerInvariant()} 3x6 fra\".");
            }
            var weights = pq.Legs.Count switch
            {
                1 => new[] { 1.0 },
                2 => new[] { -1.0, 1.0 },
                _ => new[] { -1.0, 2.0, -1.0 },
            };
            if (pq.Weights != null)
            {
                bool anySigned = pq.Weights.Any(w => w < 0);
                for (int i = 0; i < weights.Length; i++)
                    weights[i] = anySigned ? pq.Weights[i] : Math.Sign(weights[i]) * Math.Abs(pq.Weights[i]);
            }
            bool structure = pq.Legs.Count > 1;
            string fwdId = FwdCurveIdFor(cfg, product);

            // dv01 inputs may be expressed in another currency ($ = USD default) — convert at spot
            double fxRisk = 1.0;
            string fxNote = "";
            if ((pq.Dv01Target.HasValue || pq.LegDv01s != null)
                && !pq.Dv01Ccy.Equals(cfg.Ccy, StringComparison.OrdinalIgnoreCase))
            {
                fxRisk = FxRiskFactor(pq.Dv01Ccy, cfg.Ccy);
                fxNote = $"dv01 input in {pq.Dv01Ccy} × {fxRisk:0.####} → {cfg.Ccy}/bp";
            }

            // -- price each leg per 1mm to get dv01 densities
            var priced1mm = new List<PriceResult>();
            for (int i = 0; i < pq.Legs.Count; i++)
            {
                var spec = LegSpec(pq.Legs[i], cfg, product, pq, notional: 1_000_000,
                    fixedRate: structure ? null : pq.FixedRate, idxOverride: IdxFor(pq, i));
                priced1mm.Add(Pricer.Price(spec, curves));
            }

            // -- leg sizing: explicit per-leg notionals > explicit per-leg dv01s > wings dv01 > dv01-neutral
            var legNotionals = new double[pq.Legs.Count];
            var legDv01s = new double[pq.Legs.Count];
            double? structD = null;
            bool explicitSizing = false;
            if (pq.LegNotionals != null)
            {
                explicitSizing = true;
                for (int i = 0; i < pq.Legs.Count; i++)
                {
                    legNotionals[i] = pq.LegNotionals[i];
                    legDv01s[i] = priced1mm[i].Annuity01 * pq.LegNotionals[i] / 1_000_000.0;
                }
            }
            else if (pq.LegDv01s != null)
            {
                explicitSizing = true;
                for (int i = 0; i < pq.Legs.Count; i++)
                {
                    legDv01s[i] = pq.LegDv01s[i] * fxRisk;
                    legNotionals[i] = legDv01s[i] / Math.Max(priced1mm[i].Annuity01, 1e-9) * 1_000_000.0;
                }
            }
            else if (pq.WingsSizing && pq.Dv01Target.HasValue && pq.Legs.Count == 3)
            {
                // "$20k wings": wings carry the target each, belly 2x (1:2:1)
                double[] mult = { 1, 2, 1 };
                for (int i = 0; i < 3; i++)
                {
                    legDv01s[i] = pq.Dv01Target.Value * fxRisk * mult[i];
                    legNotionals[i] = legDv01s[i] / Math.Max(priced1mm[i].Annuity01, 1e-9) * 1_000_000.0;
                }
                structD = pq.Dv01Target.Value * fxRisk;
            }
            else if (pq.BellySizing && pq.Dv01Target.HasValue && pq.Legs.Count == 3)
            {
                // "$25k belly": the body carries the target, wings half each (1:2:1)
                double[] mult = { 0.5, 1, 0.5 };
                for (int i = 0; i < 3; i++)
                {
                    legDv01s[i] = pq.Dv01Target.Value * fxRisk * mult[i];
                    legNotionals[i] = legDv01s[i] / Math.Max(priced1mm[i].Annuity01, 1e-9) * 1_000_000.0;
                }
                structD = pq.Dv01Target.Value * fxRisk / 2.0;
            }
            else
            {
                double D = pq.Dv01Target.HasValue
                    ? pq.Dv01Target.Value * fxRisk
                    : priced1mm[0].Annuity01 * (pq.Notional / 1_000_000.0) / Math.Abs(weights[0]);
                structD = D;
                for (int i = 0; i < pq.Legs.Count; i++)
                {
                    legDv01s[i] = D * Math.Abs(weights[i]);
                    legNotionals[i] = legDv01s[i] / Math.Max(priced1mm[i].Annuity01, 1e-9) * 1_000_000.0;
                }
            }

            // -- round DERIVED notionals to a tradeable lot and re-derive each leg's dv01 from the
            // rounded size. The desk deals 16.5mm, not 16,470,219, so the round lot is the real
            // trade and the reported dv01 must be the round lot's dv01 (a shade off the target by
            // design). A dv01-neutral fly therefore nets to a small non-zero dv01 rather than an
            // exact zero — that residual is real. Explicitly typed notionals are left untouched.
            if (pq.LegNotionals == null)
            {
                for (int i = 0; i < pq.Legs.Count; i++)
                {
                    legNotionals[i] = Risk.RiskSizer.RoundNotional(legNotionals[i]);
                    legDv01s[i] = priced1mm[i].Annuity01 * legNotionals[i] / 1_000_000.0;
                }
            }

            var legResults = new List<LegResult>();
            for (int i = 0; i < pq.Legs.Count; i++)
            {
                legResults.Add(new LegResult
                {
                    Label = pq.Legs[i].Describe() + IdxTag(pq, i, cfg),
                    Weight = weights[i],
                    Effective = priced1mm[i].Effective,
                    Maturity = priced1mm[i].Maturity,
                    RatePct = priced1mm[i].ParRatePct,
                    Notional = legNotionals[i],
                    Dv01 = legDv01s[i],
                    DensityPerMm = priced1mm[i].Annuity01,
                });
            }

            // -- net level: quoted pillar mids for spot legs where available, curve par otherwise
            double net = 0;
            for (int i = 0; i < pq.Legs.Count; i++)
            {
                double r = legResults[i].RatePct;
                if (pq.Legs[i].StartKind == StartKind.Spot && pq.Legs[i].Tenor != null)
                {
                    var tkr = ResolvePillarTicker(cfg, product, pq.Legs[i].Tenor!, source, BandFor(cfg, product, pq, i));
                    if (tkr != null && Snapshot.TryGetMid(tkr, out var m)) r = m;
                }
                net += weights[i] * r;
            }

            // -- FWCM cross-check per leg
            for (int li = 0; li < pq.Legs.Count && li < legResults.Count; li++)
                AttachFwcm(FwdIdForLeg(cfg, product, pq, li, fwdId), FwdStyleFor(cfg, product), pq.Legs[li], legResults[li]);

            string label = structure
                ? $"{cfg.Ccy} {string.Join(" / ", pq.Legs.Select(l => l.Describe()))} {(pq.Legs.Count == 3 ? "fly" : "spread")}"
                // a FRA leg's Describe() already ends in "FRA" — don't print it twice
                : pq.Legs[0].IsFra ? $"{cfg.Ccy} {pq.Legs[0].Describe()}"
                : $"{cfg.Ccy} {pq.Legs[0].Describe()} {product}";
            // Name the index in the headline whenever it is NOT the currency's default. "USD 5Y OIS" read
            // identically whether it priced on SOFR or Fed Funds, which on a pricer is not acceptable —
            // they are 4.5bp apart at 5y.
            if (policyCurves != null && policyLadder != null) label += $" ({policyLadder.Name})";

            var r0 = priced1mm[0];
            string sizingTag = explicitSizing ? " · explicit sizing"
                : pq.WingsSizing ? " · wings-sized" : pq.BellySizing ? " · belly-sized" : " · dv01-neutral";
            var res = new InstrumentResult
            {
                Query = pq.Raw,
                Label = label,
                Ccy = cfg.Ccy,
                // a FRA is a FRA whether it is rolling (Spot start) or IMM-dated, so it must not read
                // "Outright" in one bucket and "Forward" in the other
                Kind = structure ? (pq.Legs.Count == 3 ? "Fly" : "Spread")
                     : pq.Legs[0].IsFra ? "FRA"
                     : pq.Legs[0].StartKind == StartKind.Spot ? "Outright" : "Forward",
                Unit = structure ? "bp" : "%",
                Source = source,
                ConventionSummary = r0.ConventionSummary + (structure ? $" · weights {string.Join("/", weights.Select(w => w.ToString("+0;-0")))}{sizingTag}" : ""),
                Effective = legResults[0].Effective,
                Maturity = legResults[^1].Maturity,
                StructDv01 = structD,
            };
            if (explicitSizing)
                res.NetDv01 = legResults.Select((lr, i) => Math.Sign(weights[i]) * lr.Dv01).Sum();
            res.Legs.AddRange(legResults);
            if (fxNote.Length > 0) res.Notes.Add(fxNote);
            // never leave the index a guess on a trade that deliberately prices off a different curve
            if (policyCurves != null && policyLadder != null)
                res.Notes.Add($"meeting-dated: priced on the {policyLadder.Name} strip "
                    + $"({policyLadder.Pillars.Count} pillars, fixing {policyLadder.FixingTicker}), "
                    + $"not the {cfg.Ccy} {cfg.Ois?.IndexName} curve.");
            else if (policyLadder != null)
                res.Notes.Add($"meeting-dated but the {policyLadder.Name} strip would not build — "
                    + $"FELL BACK to the {cfg.Ccy} {cfg.Ois?.IndexName} curve, which is the wrong index for this trade.");
            foreach (var w in curves.Warnings.Distinct()) res.Notes.Add(w);

            // beyond the last quoted pillar the curve is flat extrapolation — a "100y" typo would
            // otherwise print a plausible number with nothing to distrust. Note, don't error: 32y in
            // a 30y market is a real request (5y grace covers it).
            try
            {
                if (curves.Pillars.Count > 0)
                {
                    var lastQuoted = curves.Pillars.Max(p => p.Maturity);
                    foreach (var lr in legResults)
                        if (lr.Maturity > lastQuoted + (int)(5 * 365.25))
                            res.Notes.Add($"{lr.Label}: ends ~{(lr.Maturity - lastQuoted) / 365.25:0}y past the last "
                                + $"quoted pillar ({lastQuoted:dd-MMM-yy}) — flat extrapolation, level is indicative.");
                }
            }
            catch { /* advisory only */ }

            // -- CCP variant curve (e.g. JPY JSCC): reprice every leg off base + basis overlay
            ApplyVariant(pq, cfg, product, source, curves, weights, legResults, res, structure);

            if (structure)
            {
                res.Mid = net * 100.0;
                res.ParRatePct = net * 100.0;
            }
            else
            {
                // outright: re-price at sized notional for NPV; quoted mid preferred for spot standard tenors
                double sizedNotional = legResults[0].Notional;
                var spec = LegSpec(pq.Legs[0], cfg, product, pq, sizedNotional, pq.FixedRate, IdxFor(pq, 0));
                var priced = Pricer.Price(spec, curves);
                res.ParRatePct = priced.ParRatePct;
                res.Npv = priced.Npv;
                res.Annuity01 = priced.Annuity01;
                res.Dv01 = priced.Annuity01;
                res.RollBp.AddRange(priced.CarryRollBp);
                legResults[0].RatePct = priced.ParRatePct;
                legResults[0].Dv01 = priced.Annuity01;
                legResults[0].Notional = sizedNotional;

                string? ticker = pq.Legs[0].StartKind == StartKind.Spot && pq.Legs[0].Tenor != null
                    ? ResolvePillarTicker(cfg, product, pq.Legs[0].Tenor!, source, BandFor(cfg, product, pq, 0)) : null;
                if (ticker != null)
                {
                    var q = Snapshot.Get(ticker);
                    res.PrimaryTicker = ticker;
                    res.Bid = q?.Bid; res.Ask = q?.Ask; res.Mid = q?.Mid ?? res.ParRatePct;
                    if (q?.Bid != null && q.Ask != null) res.BidAskWideBp = (q.Ask - q.Bid) * 100.0;
                }
                else res.Mid = res.ParRatePct;
            }

            // -- structure roll: combine per-leg carry-roll (per-leg computed at 1mm; rate-space so notional-free)
            if (structure)
            {
                var horizons = priced1mm[0].CarryRollBp.Select(kv => kv.Key);
                foreach (var h in horizons)
                {
                    double val = 0; bool ok = true;
                    for (int i = 0; i < pq.Legs.Count; i++)
                    {
                        var kv = priced1mm[i].CarryRollBp.FirstOrDefault(x => x.Key == h);
                        if (kv.Key == null) { ok = false; break; }
                        val += weights[i] * kv.Value;
                    }
                    if (ok) res.RollBp.Add(new KeyValuePair<string, double>(h, val));
                }
            }

            AttachStructureHistory(res, pq, cfg, product, source, weights, curves);

            // -- Δ 1d from an exact prev-close reprice (the MONITOR's method). The history-based
            // value is wrong whenever the series' last BDH point predates today (e.g. NZD in
            // London hours) or the anchored par-approx pinned that stale point to the live mid —
            // both report yesterday's move (or ~0) as today's change.
            if (res.Stats != null && PrevCloseCoDBp(pq, cfg, product, source, curves, weights) is double xcod)
                res.Stats.Chg1d = xcod + OvrShiftBp(res);
            return res;
        }

        /// <summary>Exact structure change-on-day in bp: quoted (mid − prev close) for spot pillar
        /// legs, prev-close-curve reprice for everything else (forwards, IMM, dated). Live legs are
        /// repriced on the BASE curve so both sides are symmetric (CCP-variant basis cancels).
        /// Null when no prev-close curve can be built (missing PX_CLOSE_1D).</summary>
        private double? PrevCloseCoDBp(ParsedQuery pq, CurrencyConfig cfg, ProductKind product,
            string source, CurveSet curves, double[] weights)
        {
            try
            {
                CurveSet? prev = null;
                bool prevTried = false;
                double cod = 0;
                for (int i = 0; i < pq.Legs.Count; i++)
                {
                    double? liveI = null, prevI = null;
                    if (pq.Legs[i].StartKind == StartKind.Spot && pq.Legs[i].Tenor != null)
                    {
                        var tkr = ResolvePillarTicker(cfg, product, pq.Legs[i].Tenor!, source, BandFor(cfg, product, pq, i));
                        var q = tkr != null ? Snapshot.Get(tkr) : null;
                        if (q?.Mid is double m && q.PrevClose is double pc) { liveI = m; prevI = pc; }
                    }
                    if (prevI == null)
                    {
                        if (!prevTried)
                        {
                            prev = GetPrevCloseCurvesUnlocked(cfg, source);
                            prevTried = true;
                            Settings.setEvaluationDate(curves.AsOf); // prev bootstrap may have moved it
                        }
                        if (prev == null) return null;
                        var spec = LegSpec(pq.Legs[i], cfg, product, pq, notional: 1_000_000, idxOverride: IdxFor(pq, i));
                        liveI = Pricer.Price(spec, curves).ParRatePct;
                        prevI = Pricer.Price(spec, prev).ParRatePct;
                    }
                    cod += weights[i] * (liveI.Value - prevI.Value);
                }
                return cod * 100.0;
            }
            catch { return null; /* CoD is best-effort — fall back to the history value */ }
        }

        private static ProductKind ResolveProductForTarget(CurveTarget target, CurrencyConfig cfg)
        {
            var p = target.Product;
            if (p == ProductKind.OIS && cfg.Ois == null) p = ProductKind.IRS;
            if (p == ProductKind.IRS && cfg.Irs == null) p = ProductKind.OIS;
            return p;
        }

        private static Period? IdxFor(ParsedQuery pq, int i) =>
            pq.IndexOverrides != null && i < pq.IndexOverrides.Count ? pq.IndexOverrides[i] : null;

        /// <summary>Per-leg FWCM id for dual-convention markets (AUD): explicit qq/ss tag wins; else the
        /// surface FOLLOWS THE LEG that will actually price — the tenor-rule leg, downgraded to the
        /// default band once the short ladder's own quotes no longer reach the leg END (the pricer
        /// re-legs to the default convention there, so the reference must switch with it). The old
        /// END&lt;=FwdShortMaxYears rule booked ~25bp of AUD 3s6s as fake FWCM basis the moment the q/q
        /// ladder was extended past it with real ADSWAP5Q..9Q quotes (2026-08-04).</summary>
        private static string FwdIdForLeg(CurrencyConfig cfg, ProductKind product, ParsedQuery pq, int i, string baseId)
        {
            if (product != ProductKind.IRS || string.IsNullOrEmpty(cfg.Irs?.FwdCurveIdShort)
                || i < 0 || i >= pq.Legs.Count) return baseId;
            var leg = pq.Legs[i];
            var idx = IdxFor(pq, i);
            if (idx != null)
                return (int)Math.Round(TenorUtil.ApproxMonths(idx)) <= 3 ? cfg.Irs!.FwdCurveIdShort : baseId;
            if (leg.Tenor == null) return baseId;
            var conv = SwapBuilder.SelectIrsLeg(cfg.Irs!, leg.Tenor, null);
            if (!conv.FloatTenor.Equals(cfg.Irs!.Legs[0].FloatTenor, StringComparison.OrdinalIgnoreCase))
                return baseId;
            return LegEndYears(leg) <= SwapBuilder.ShortBandMaxYears(cfg) + 1.0 / 52
                ? cfg.Irs.FwdCurveIdShort : baseId;
        }

        /// <summary>Years from today to the leg's (approximate) END — forward/IMM/dated start plus tenor.</summary>
        private static double LegEndYears(Leg leg)
        {
            double tY = leg.Tenor != null ? TenorUtil.ApproxMonths(leg.Tenor) / 12.0 : 0.0;
            var today = new Date(DateTime.Today.Day, (Month)DateTime.Today.Month, DateTime.Today.Year);
            double sY = leg.StartKind == StartKind.Forward && leg.ForwardStart != null
                ? TenorUtil.ApproxMonths(leg.ForwardStart) / 12.0
                : leg.StartKind == StartKind.Imm && leg.ImmDate != null ? Math.Max(0, (leg.ImmDate - today) / 365.25)
                : leg.StartKind == StartKind.Date && leg.ExplicitStart != null ? Math.Max(0, (leg.ExplicitStart - today) / 365.25)
                : 0.0;
            return sY + tY;
        }

        /// <summary>The quote family (float tenor) the leg actually PRICES on, for history-source
        /// selection in dual-band markets — mirrors the pricer: explicit index tag wins; else the
        /// tenor-rule leg, re-legged to the default band when the short ladder's quotes don't reach
        /// the leg end. Null (no filtering) for OIS, single-band markets, or tenor-less legs.</summary>
        private static string? HistBandFor(CurrencyConfig cfg, ProductKind product, ParsedQuery pq, int i)
        {
            if (product != ProductKind.IRS || cfg.Irs == null || cfg.Irs.Legs.Count < 2
                || i < 0 || i >= pq.Legs.Count || pq.Legs[i].Tenor == null) return null;
            var conv = SwapBuilder.SelectIrsLeg(cfg.Irs, pq.Legs[i].Tenor!, IdxFor(pq, i));
            var def = cfg.Irs.Legs[^1];
            if (IdxFor(pq, i) == null
                && !conv.FloatTenor.Equals(def.FloatTenor, StringComparison.OrdinalIgnoreCase)
                && LegEndYears(pq.Legs[i]) > SwapBuilder.ShortBandMaxYears(cfg) + 1.0 / 52)
                conv = def; // the pricer re-legs there; the history must follow
            return conv.FloatTenor;
        }

        private static string IdxTag(ParsedQuery pq, int i, CurrencyConfig cfg)
        {
            var p = IdxFor(pq, i);
            if (p == null) return "";
            int m = p.length() == 3 ? 3 : 6;
            // an override that IS the leg's natural convention needs no tag (EUR "6s" on a 25y
            // is just ann/6s — labelling it "ss" mislabels the fixed leg)
            if (cfg.Irs != null && pq.Legs[i].Tenor is { } lt
                && (int)Math.Round(TenorUtil.ApproxMonths(TenorUtil.Parse(
                    SwapBuilder.SelectIrsLeg(cfg.Irs, lt, null).FloatTenor))) == m)
                return "";
            // ccys with a real band of that index keep the desk vocabulary (AUD qq/ss = the
            // fixed leg flips frequency too); otherwise the tag is just the float index
            var bandLeg = cfg.Irs?.Legs.FirstOrDefault(l =>
                (int)Math.Round(TenorUtil.ApproxMonths(TenorUtil.Parse(l.FloatTenor))) == m);
            if (bandLeg != null && m == 3 && bandLeg.FixedFreq.StartsWith("Q", StringComparison.OrdinalIgnoreCase))
                return " qq";
            if (bandLeg != null && m == 6 && bandLeg.FixedFreq.StartsWith("S", StringComparison.OrdinalIgnoreCase))
                return " ss";
            return $" {m}s";
        }

        private TradeSpec LegSpec(Leg leg, CurrencyConfig cfg, ProductKind product, ParsedQuery pq,
            double notional, double? fixedRate = null, Period? idxOverride = null) => new()
        {
            Ccy = cfg.Ccy,
            Product = product,
            StartKind = leg.StartKind,
            ForwardStart = leg.ForwardStart,
            ImmDate = leg.ImmDate,
            ImmCode = leg.ImmCode,
            ExplicitStart = leg.ExplicitStart,
            Tenor = leg.Tenor,
            // FRA months travel through unchanged; the names match 1:1 on purpose
            FraStartMonths = leg.FraStartMonths,
            FraEndMonths = leg.FraEndMonths,
            Notional = notional,
            PayFixed = pq.PayFixed,
            FixedRate = fixedRate,
            FloatTenorOverride = idxOverride,
            Source = pq.Source,
        };

        // ---------- CCP curve variants (basis overlays, e.g. JPY LCH vs JSCC) ----------

        private void ApplyVariant(ParsedQuery pq, CurrencyConfig cfg, ProductKind product, string source,
            CurveSet baseCurves, double[] weights, List<LegResult> legResults, InstrumentResult res, bool structure)
        {
            if (product != ProductKind.OIS || cfg.Ois == null || cfg.Ois.Variants.Count == 0) return;
            var variant = cfg.Ois.Variants[0];
            try
            {
                // basis ladder (bp) by tenor-years from live quotes
                var basisPts = new List<(double years, double bp)>();
                foreach (var p in variant.Pillars.Where(p => p.Enabled && !p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase)))
                {
                    var full = ConfigStore.ResolveTicker(p.Ticker, "");
                    if (Snapshot.TryGetMid(full, out var bpv))
                        basisPts.Add((TenorUtil.ApproxMonths(TenorUtil.Parse(p.Tenor)) / 12.0, bpv));
                }
                if (basisPts.Count < 2) { res.Notes.Add($"{variant.Name} basis quotes unavailable."); return; }
                basisPts.Sort((a, b) => a.years.CompareTo(b.years));

                // base pillar ticker -> tenor years (to shift each quote by the interpolated basis)
                var tickerYears = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in cfg.Ois.Curve.Where(p => p.Enabled && !p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase)))
                    tickerYears[ConfigStore.ResolveTicker(p.Ticker, source)] =
                        TenorUtil.ApproxMonths(TenorUtil.Parse(p.Tenor)) / 12.0;

                var variantCurves = CurveBuilder.Build(cfg, source, Snapshot, baseCurves.AsOf,
                    (ticker, r) => tickerYears.TryGetValue(ticker, out var yrs)
                        ? r + variant.Sign * (Interp(basisPts, yrs) ?? 0.0) / 10000.0
                        : r,
                    ExternalDiscountFor(cfg));

                double net = 0;
                for (int i = 0; i < pq.Legs.Count; i++)
                {
                    var spec = LegSpec(pq.Legs[i], cfg, product, pq, 1_000_000, null, IdxFor(pq, i));
                    var (swap, _, _, _) = Pricer.BuildTrade(spec, product, variantCurves, 0.0);
                    swap.setPricingEngine(new QLNet.DiscountingSwapEngine(variantCurves.DiscountHandleFor(product)));
                    double vr = Pricer.FairRate(swap) * 100.0;
                    legResults[i].AltRatePct = vr;
                    legResults[i].AltName = variant.Name;
                    net += weights[i] * vr;
                }
                res.AltName = variant.Name;
                res.AltMid = structure ? net * 100.0 : legResults[0].AltRatePct;
                res.Notes.Add($"{variant.Name} = base {(variant.Sign < 0 ? "-" : "+")} JYJLO basis (bp)");
            }
            catch (Exception ex)
            {
                res.Notes.Add($"{variant.Name} variant failed: {ex.Message}");
            }
        }

        /// <summary>Build the trade spec for the first leg of a query (ticket pricing).</summary>
        public TradeSpec SpecFromQuery(ParsedQuery pq)
        {
            var cfg = Configs.Get(pq.Target.Ccy);
            var product = ResolveProductForTarget(pq.Target, cfg);
            var leg = pq.Main ?? new Leg { Tenor = new Period(5, TimeUnit.Years) };
            // idxOverride matches every other LegSpec call site (incl. AnalyzeStructure's own
            // outright reprice at :424): without it an "aud 3s"/"6s" tag reached the headline but
            // not the ticket tabs, so the two disagreed on the float index — same class of split
            // as the sizing bug this method now fixes.
            return LegSpec(leg, cfg, product, pq, LegNotionalFor(pq, cfg, product, leg), pq.FixedRate,
                IdxFor(pq, 0));
        }

        /// <summary>Size for the FIRST leg of a query, on the same rule AnalyzeStructure sizes the
        /// headline tiles with — so the Cashflows/Risk-ladder tabs of an unsized outright are no
        /// longer stuck on ParsedQuery's flat 10mm default while the headline shows $25k dv01:
        /// explicit per-leg notional wins, else an explicit (or defaulted) dv01 target is converted
        /// through the leg's own density, else the raw Notional field (legacy direct callers).</summary>
        private double LegNotionalFor(ParsedQuery pq, CurrencyConfig cfg, ProductKind product, Leg leg)
        {
            // LegNotionals is the EXACT channel (blotter positions, real deal sizes) — never rounded
            if (pq.LegNotionals != null && pq.LegNotionals.Count > 0) return pq.LegNotionals[0];
            double? dv01 = pq.LegDv01s != null && pq.LegDv01s.Count > 0 ? pq.LegDv01s[0] : pq.Dv01Target;
            // a notional TYPED into a query rounds to a dealable lot, matching AnalyzeStructure —
            // otherwise "usd 5y 16.47mm" would show 16.5mm in the headline and 16.47mm on the ticket
            if (!dv01.HasValue) return Risk.RiskSizer.RoundNotional(pq.Notional);
            // QLNet's evaluation date is process-global, so the density probe belongs under the gate;
            // Monitor is reentrant, so this is a no-op when PriceStructureTicket/PriceQuery hold it
            lock (_gate)
            {
                var curves = GetCurvesUnlocked(cfg, pq.Source ?? SourceFor(cfg.Ccy));
                Settings.setEvaluationDate(curves.AsOf);
                var oneMm = LegSpec(leg, cfg, product, pq, 1_000_000, pq.FixedRate, IdxFor(pq, 0));
                double density = Pricer.Price(oneMm, curves).Annuity01; // dv01 per 1mm, trade ccy
                return Risk.RiskSizer.Resolve(density,
                    explicitDv01: dv01.Value * FxRiskFactor(pq.Dv01Ccy, cfg.Ccy)).Notional;
            }
        }

        public PriceResult PriceQuery(ParsedQuery pq, bool withLadder)
        {
            // one critical section: the sizing probe and the ticket price must see the same
            // curve build / evaluation date (the inner locks are reentrant no-ops)
            lock (_gate) return Price(SpecFromQuery(pq), withLadder);
        }

        private static readonly HashSet<string> UsdInverse = new(StringComparer.OrdinalIgnoreCase)
            { "EUR", "GBP", "AUD", "NZD" };

        /// <summary>USD value of 1 unit of ccy, from the live "{CCY} Curncy" spot quote.</summary>
        public double UsdPer(string ccy)
        {
            if (ccy.Equals("USD", StringComparison.OrdinalIgnoreCase)) return 1.0;
            if (!Snapshot.TryGetMid($"{ccy} Curncy", out var m) || m <= 0)
                throw new InvalidOperationException($"no FX spot for {ccy} ('{ccy} Curncy' not loaded)");
            return UsdInverse.Contains(ccy) ? m : 1.0 / m;
        }

        /// <summary>Spot factor that turns a dv01 typed in <paramref name="dv01Ccy"/> ("$25k") into
        /// the trade currency's own dv01 per bp. 1.0 when they match — no FX quote is touched, so an
        /// unsized single-ccy query never depends on "{CCY} Curncy" being loaded.</summary>
        internal double FxRiskFactor(string dv01Ccy, string tradeCcy) =>
            dv01Ccy.Equals(tradeCcy, StringComparison.OrdinalIgnoreCase)
                ? 1.0
                : UsdPer(dv01Ccy) / UsdPer(tradeCcy);

        /// <summary>Cashflows + combined bucketed ladder for a full structure (each leg at its sized
        /// notional, negative-weight legs flipped). Feeds the ticket tabs for spreads/flies too.</summary>
        public PriceResult PriceStructureTicket(ParsedQuery pq, InstrumentResult analyzed)
        {
            lock (_gate)
            {
                var cfg = Configs.Get(pq.Target.Ccy);
                var source = pq.Source ?? SourceFor(pq.Target.Ccy);
                var curves = GetCurvesUnlocked(cfg, source);
                var product = ResolveProductForTarget(pq.Target, cfg);

                var agg = new PriceResult
                {
                    Spec = SpecFromQuery(pq), Cfg = cfg, AsOf = curves.AsOf, Source = source,
                    ParRatePct = analyzed.Mid ?? 0, Npv = 0, Annuity01 = 0,
                    Effective = analyzed.Effective ?? curves.AsOf,
                    Maturity = analyzed.Maturity ?? curves.AsOf,
                    ProductUsed = product.ToString(), ConventionSummary = analyzed.ConventionSummary,
                };
                var ladderAcc = new Dictionary<(string curve, string label, string ticker), (double mkt, double dv01)>();
                var fwdAcc = new Dictionary<string, (double mkt, double dv01, int order)>();
                for (int i = 0; i < pq.Legs.Count && i < analyzed.Legs.Count; i++)
                {
                    var lr = analyzed.Legs[i];
                    bool pay = pq.PayFixed ^ (lr.Weight < 0);
                    var spec = LegSpec(pq.Legs[i], cfg, product, pq, Math.Abs(lr.Notional), null, IdxFor(pq, i));
                    spec.PayFixed = pay;
                    var pr = Pricer.Price(spec, curves);
                    Risk.Ladder.Compute(spec, cfg, source, Snapshot, curves.AsOf, pr, ExternalDiscountFor(cfg),
                        DiscountCcyFor(cfg));
                    try { Risk.Ladder.ComputeForward(spec, cfg, source, Snapshot, curves.AsOf, pr, ExternalDiscountFor(cfg)); }
                    catch { /* fwd ladder is best-effort */ }
                    foreach (var lp in pr.FwdLadder)
                    {
                        var cur = fwdAcc.TryGetValue(lp.Label, out var v0) ? v0 : (lp.MarketRatePct, 0.0, fwdAcc.Count);
                        fwdAcc[lp.Label] = (cur.Item1, cur.Item2 + lp.Dv01, cur.Item3);
                    }
                    string prefix = pq.Legs.Count > 1 ? $"{lr.Label} " : "";
                    foreach (var cf in pr.Cashflows)
                        agg.Cashflows.Add(new CashflowRow
                        {
                            Leg = prefix + cf.Leg, PayDate = cf.PayDate,
                            AccrualStart = cf.AccrualStart, AccrualEnd = cf.AccrualEnd,
                            RatePct = cf.RatePct, Amount = cf.Amount, Df = cf.Df, Pv = cf.Pv,
                        });
                    foreach (var lp in pr.Ladder)
                    {
                        var key = (lp.Curve, lp.Label, lp.Ticker);
                        var cur = ladderAcc.TryGetValue(key, out var v) ? v : (lp.MarketRatePct, 0.0);
                        ladderAcc[key] = (cur.Item1, cur.Item2 + lp.Dv01);
                    }
                }
                foreach (var (key, v) in ladderAcc)
                    agg.Ladder.Add(new LadderPoint
                    {
                        Curve = key.curve, Label = key.label, Ticker = key.ticker,
                        MarketRatePct = v.mkt, Dv01 = v.dv01,
                    });
                agg.LadderTotalDv01 = agg.Ladder.Sum(p => p.Dv01);
                foreach (var (label, v) in fwdAcc.OrderBy(kv => kv.Value.order))
                    agg.FwdLadder.Add(new LadderPoint
                    {
                        Curve = "FWD", Label = label, MarketRatePct = v.mkt, Dv01 = v.dv01,
                    });
                agg.FwdLadderTotalDv01 = agg.FwdLadder.Sum(p => p.Dv01);
                agg.Cashflows.Sort((a, b) => a.PayDate != b.PayDate
                    ? a.PayDate.CompareTo(b.PayDate) : string.CompareOrdinal(a.Leg, b.Leg));
                return agg;
            }
        }

        // ---------- FWCM forward-ticker cross-check ----------

        private static string FwdCurveIdFor(CurrencyConfig cfg, ProductKind product) =>
            product == ProductKind.OIS ? cfg.Ois?.FwdCurveId ?? "" : cfg.Irs?.FwdCurveId ?? "";

        /// <summary>Which forward-ticker family this curve's id belongs to. Decided per curve, because it
        /// has to match the index basis the pillars are quoted on — see <see cref="ForwardTicker"/>.</summary>
        private static FwdTickerStyle FwdStyleFor(CurrencyConfig cfg, ProductKind product) =>
            ForwardTicker.Parse(product == ProductKind.OIS ? cfg.Ois?.FwdTickerStyle : cfg.Irs?.FwdTickerStyle);

        /// <summary>FWCM tickers worth snapshotting for a leg: exact + bracketing starts.
        /// fwdId may be comma-separated ("S0484,S0485" = bid/ask curves whose average is the mid).</summary>
        /// <summary>Forward-start period for FWCM crossing: explicit for Forward legs; IMM and
        /// custom-dated legs cross to the NEAREST month on FWCM's monthly grid (the basis
        /// column absorbs the few-days start mismatch).</summary>
        private static Period? FwcmStart(Leg leg)
        {
            if (leg.StartKind == StartKind.Forward) return leg.ForwardStart;
            var d = leg.StartKind == StartKind.Imm ? leg.ImmDate
                : leg.StartKind == StartKind.Date ? leg.ExplicitStart : null;
            if (d == null) return null;
            var today = new Date(DateTime.Today.Day, (Month)DateTime.Today.Month, DateTime.Today.Year);
            int m = (int)Math.Round((d - today) / 30.4375);
            return m >= 1 && m <= 600 ? new Period(m, TimeUnit.Months) : null;
        }

        private static IEnumerable<string> FwcmCandidates(string fwdId, FwdTickerStyle style, Leg leg)
        {
            var fs = FwcmStart(leg);
            if (fs == null || leg.Tenor == null) yield break;
            foreach (var id in fwdId.Split(',', StringSplitOptions.TrimEntries))
            {
                if (ForwardTicker.Exact(id, style, fs, leg.Tenor) is { } exact) yield return exact;
                double sm = TenorUtil.ApproxMonths(fs);
                int lo = (int)Math.Floor(sm / 12.0), hi = (int)Math.Ceiling(sm / 12.0);
                if (lo >= 1 && ForwardTicker.AtStartYears(id, style, lo, leg.Tenor) is { } tl) yield return tl;
                if (hi != lo && ForwardTicker.AtStartYears(id, style, hi, leg.Tenor) is { } th) yield return th;
                // the QUOTED start grid brackets too (a 19y start needs 15Y/20Y off FWCM's sparse grid,
                // not 19Y/19Y) — these are what the interp paths actually read
                var (glo, ghi) = ForwardTicker.Bracket(style, sm / 12.0);
                if (glo > 0 && ForwardTicker.AtStartYears(id, style, glo, leg.Tenor) is { } tglo) yield return tglo;
                if (ghi > 0 && ForwardTicker.AtStartYears(id, style, ghi, leg.Tenor) is { } tghi) yield return tghi;
            }
        }

        /// <summary>Averaged mid across comma-separated ids (bid/ask curve pairs) for one start/tenor.</summary>
        private double? FwcmMid(string fwdId, FwdTickerStyle style, Period start, Period tenor, out string ticker)
        {
            var ids = fwdId.Split(',', StringSplitOptions.TrimEntries);
            var vals = new List<double>();
            foreach (var id in ids)
                if (ForwardTicker.Exact(id, style, start, tenor) is { } tk
                    && Snapshot.TryGetMid(tk, out var v)) vals.Add(v);
            if (vals.Count == 0) { ticker = ""; return null; }
            var pt = (ForwardTicker.Code(start) ?? "?") + (ForwardTicker.Code(tenor) ?? "?");
            ticker = ids.Length > 1
                ? $"avg({string.Join("/", ids)}) {pt}"
                : ForwardTicker.Label(ids[0], style, start, tenor);
            return vals.Average();
        }

        private void AttachFwcm(string fwdId, FwdTickerStyle style, Leg leg, LegResult lr)
        {
            var fs = FwcmStart(leg);
            if (string.IsNullOrEmpty(fwdId) || fs == null || leg.Tenor == null) return;

            var v0 = FwcmMid(fwdId, style, fs, leg.Tenor, out var tk0);
            if (v0.HasValue) { lr.BbgFwdPct = v0; lr.BbgFwdTicker = tk0; return; }

            // Nothing quoted at the exact point — bracket it. The year-pair family quotes every year but
            // has real liquidity holes on odd long combinations, so this fires there too, not only for
            // FWCM's sparse grid.
            double sm = TenorUtil.ApproxMonths(fs) / 12.0;
            var (lo, hi) = ForwardTicker.Bracket(style, sm);
            if (lo >= 1 && hi > lo)
            {
                var vLo = FwcmMid(fwdId, style, new Period(lo, TimeUnit.Years), leg.Tenor, out _);
                var vHi = FwcmMid(fwdId, style, new Period(hi, TimeUnit.Years), leg.Tenor, out _);
                if (vLo.HasValue && vHi.HasValue)
                {
                    double w = (sm - lo) / (hi - lo);
                    lr.BbgFwdPct = vLo + w * (vHi - vLo);
                    lr.BbgFwdTicker = $"interp {lo}Y/{hi}Y{ForwardTicker.Code(leg.Tenor)}";
                }
            }
        }

        // ---------- history: FWCM ticker > pillar (interp) > par-approx ----------

        private void AttachStructureHistory(InstrumentResult res, ParsedQuery pq, CurrencyConfig cfg,
            ProductKind product, string source, double[] weights, CurveSet curves)
        {
            if (History == null || pq.SkipHistory) return;
            string fwdId = FwdCurveIdFor(cfg, product);
            bool structure = pq.Legs.Count > 1;

            // ANCHORING POLICY — decided ONCE here, for the level series and every roll overlay alike.
            //
            // A single instrument anchors: the chart's last point should be the quoted Mid, and there is
            // no other leg for a level shift to distort.
            //
            // A STRUCTURE does not. Anchoring pins each leg to OUR curve's value for that leg, and on a
            // spread/fly those per-leg shifts do not cancel — they are multiplied by the weights and
            // land in the combined series as bp of rate that never traded. That is exactly how the
            // "in 1y" overlay on a JPY 10y2y/12y3y/15y5y fly came to sit ~4bp off: the x2 leg's gap to
            // our curve was 1.81bp, and the old per-leg 2bp threshold flipped it on and off as the live
            // rate ticked. Left unanchored, every leg stays on ONE source basis (Bloomberg's forward
            // matrix), which is the basis the desk cross-references and the only one on which a level
            // series and its roll overlay are comparable at all.
            //
            // Rolled legs land on unquoted forward ends (a 12y3y rolls to 11y3y, ending 14y) where our
            // own curve interpolates and drifts from FWCM - see CLAUDE.md on forward ends needing to
            // land on quoted pillars. Anchoring to it imports that drift as fake roll.
            bool anchor = !structure;

            var serieses = new List<IReadOnlyList<HistPoint>>();
            var levelFamilies = new HistFamily[pq.Legs.Count];
            for (int i = 0; i < pq.Legs.Count; i++)
            {
                var (hist, note, fam) = LegHistory(pq.Legs[i], cfg, product, source, FwdIdForLeg(cfg, product, pq, i, fwdId),
                    FwdStyleFor(cfg, product), curves, expectPct: res.Legs[i].RatePct, full: true, anchor: anchor,
                    band: HistBandFor(cfg, product, pq, i));
                res.Legs[i].HistoryNote = note;
                if (hist.Count < 10) { res.Notes.Add($"leg {res.Legs[i].Label}: no usable history ({note})."); return; }
                serieses.Add(hist);
                levelFamilies[i] = fam;
            }
            if (structure && levelFamilies.Distinct().Count() > 1)
                res.Notes.Add("history legs come from MIXED sources ("
                    + string.Join(", ", res.Legs.Select((l, i) => $"{l.Label}={levelFamilies[i]}"))
                    + ") — the combination mixes bases; treat level and roll as indicative.");

            // regression hedge ratio for 2-leg spreads: far-leg daily changes on near-leg daily changes
            if (pq.Legs.Count == 2)
            {
                var (near, far) = Regression.AlignByDate(serieses[0], serieses[1]);
                int win = Math.Min(near.Length, 253); // ~1y of daily points
                var reg = Regression.Simple(
                    Regression.Changes(far.Skip(far.Length - win).ToArray()),
                    Regression.Changes(near.Skip(near.Length - win).ToArray()));
                if (reg is { } r0)
                {
                    res.EmpBeta = r0.beta;
                    res.EmpBetaR2 = r0.r2;
                }
            }

            // second despike pass on the COMBINED series: a small surviving bad print on one leg is
            // amplified by the structure weights (x2, x4...), so the combination needs its own filter
            var combined = CleanCombined(CombineSeries(serieses, weights, scaleToBp: structure), structure);
            if (combined.Count < 10) { res.Notes.Add("insufficient overlapping history for the structure."); return; }

            // COMBINED-LEVEL ANCHOR — the counterpart to the per-leg policy decided above.
            //
            // Not anchoring the LEGS is right, and stays: per-leg shifts get multiplied by the
            // structure weights, do not cancel, and land as bp of fake roll (the JPY fly's overlay).
            // But leaving the COMBINATION unanchored left the level series on the source's basis
            // while res.Mid is our curve's, and every statistic that ranks the mid inside the series
            // then inherits the whole gap. On a -1/+2/-1 fly of IMM-dated legs — whose only history
            // rung is the annuity-less par approximation, and whose approximation errors do not
            // cancel across the wings — that gap was ~5.4bp against a 1y range of 4.9bp: %ile 100,
            // z 7.75, AT RANGE 186%, and six change tiles all reading the offset instead of a move.
            //
            // ONE shift applied AFTER combination has none of the per-leg problem: level series and
            // every roll overlay move together, so the roll (the gap between them) is untouched, and
            // beta/vol/half-life are difference-based and unchanged. Only the absolute level moves —
            // onto the basis of the number printed at the top of the screen, which is the one basis
            // a reader compares it against.
            //
            // Thresholdless and family-gated, deliberately: a threshold is what made the old per-leg
            // anchor flip on live ticks. A Pillar-sourced leg IS its instrument (the quotes our own
            // curve is bootstrapped from), so a pure-Pillar structure is already on one basis and its
            // residual gap is the honest intraday move — anchoring there would erase today's move
            // from every horizon. A proxy leg (FWCM ticker or par approx) is not the instrument.
            if (structure && res.Mid is double liveMid
                && levelFamilies.Any(f => f is HistFamily.Fwcm or HistFamily.Approx))
            {
                double shift = liveMid - combined[^1].Value;      // structures are in bp on both sides
                if (Math.Abs(shift) > 1e-9)
                {
                    combined = combined.Select(p => new HistPoint(p.Date, p.Value + shift)).ToList();
                    res.Notes.Add($"history anchored {shift:+0.0;-0.0}bp to the curve mid "
                        + $"({string.Join("/", levelFamilies.Select(f => f.ToString().ToLowerInvariant()))} "
                        + "source basis) — levels shifted, every daily change preserved.");
                }
            }

            res.History = SliceLookback(combined);
            res.FullHistory = combined;
            ApplyMidOverride(pq, res);
            res.Stats = SeriesStats.Compute(combined, liveLast: res.Mid,
                changeScale: structure ? 1.0 : 100.0, basisRef: res.MidTrue ?? res.Mid);
            if (res.Stats?.SuppressReason is string why)
                res.Notes.Add($"level stats withheld: {why}.");

            AttachRollOverlays(res, structure);
        }

        /// <summary>Where the trade WILL BE in 3m/6m/9m/1y: the level series shifted by the ACTUAL roll
        /// to each horizon.
        ///
        /// <para>Each horizon gets its OWN roll, straight from <see cref="Pricer.ComputeCarryRoll"/>, which
        /// slides the real effective and maturity dates and reprices the swap. Nothing is interpolated and
        /// nothing is expressed as a fraction of the 1y number, so 3m/6m/9m/1y are independently correct
        /// and the DIFFERENCES between them are the real thing - which is the whole point of drawing four
        /// of them. A previous cut scaled one resolved 1y series by legRoll(h)/legRoll(1y): the shape was
        /// right, but every horizon inherited the 1y magnitude including any error in it.</para>
        ///
        /// <para>Sign follows <see cref="InstrumentResult.RollBp"/>: positive roll means the structure
        /// rolls to a LOWER rate, so the aged level is level - roll. History is in bp for structures and
        /// in % for single instruments, hence the scale.</para>
        ///
        /// <para>Consequence worth stating: an overlay is now a parallel shift of the level line, so it
        /// says where the trade is heading but not how that roll varied historically. That is the price of
        /// having four individually-accurate horizons instead of one number stretched across them.</para>
        ///
        /// <para>It also needs NO extra Bloomberg history - the reference-horizon resolution is gone, and
        /// with it every chance of a transient miss demoting one leg of a structure mid-chart.</para></summary>
        private static void AttachRollOverlays(InstrumentResult res, bool structure)
        {
            if (res.History.Count == 0) return;
            double scale = structure ? 1.0 : 100.0;
            foreach (var (label, months) in RollHorizons)
            {
                var key = months == 12 ? "1Y" : $"{months}M";
                var kv = res.RollBp.FirstOrDefault(x => x.Key == key);
                if (kv.Key == null) continue;              // horizon beyond the trade's life
                double shift = kv.Value / scale;
                res.RollOverlays.Add((label,
                    res.History.Select(pt => new HistPoint(pt.Date, pt.Value - shift)).ToList()));
            }
        }

        private static readonly (string Label, int Months)[] RollHorizons =
            { ("in 3m", 3), ("in 6m", 6), ("in 9m", 9), ("in 1y", 12) };

        /// <summary>Post-combine despike: wider window, tighter threshold, 3 passes (kills 2-3 day
        /// spike clusters). Floor scales with units (structures are in bp, single instruments in %).</summary>
        private static IReadOnlyList<HistPoint> CleanCombined(IReadOnlyList<HistPoint> series, bool inBp) =>
            HistoryFilter.Despike(series, window: 7, k: 4, madFloorPct: inBp ? 0.5 : 0.005, passes: 3);

        /// <summary>The leg as it looks one roll-horizon later on a static curve: (E−h, M−h), start clamped at spot.</summary>
        private static Leg? RolledLeg(Leg leg, int hMonths, Date asOf)
        {
            double tenorM = leg.Tenor != null ? TenorUtil.ApproxMonths(leg.Tenor) : 0;
            if (tenorM <= 0) return null;
            double startM = leg.StartKind switch
            {
                StartKind.Spot => 0,
                StartKind.Forward => TenorUtil.ApproxMonths(leg.ForwardStart!),
                StartKind.Imm => Math.Max(0, (leg.ImmDate! - asOf) / 30.4375),
                StartKind.Date => Math.Max(0, (leg.ExplicitStart! - asOf) / 30.4375),
                _ => 0,
            };
            double newStart = startM - hMonths;
            if (newStart >= 1)
                return new Leg
                {
                    StartKind = StartKind.Forward,
                    ForwardStart = new Period((int)Math.Round(newStart), TimeUnit.Months),
                    Tenor = leg.Tenor,
                };
            double newTenor = startM + tenorM - hMonths;
            if (newTenor < 1) return null;
            return new Leg { StartKind = StartKind.Spot, Tenor = new Period((int)Math.Round(newTenor), TimeUnit.Months) };
        }

        /// <summary>Which FAMILY of source a leg's history came from. Only the family matters for
        /// comparability: the two FWCM branches (direct ticker, and interpolation between bracketing
        /// quoted starts) both carry the curve's forward curvature, while the par approximation does
        /// not — no constant shift can reconcile those two shapes. Combining legs, or comparing a
        /// level series against its roll overlay, is only meaningful WITHIN one family.</summary>
        private enum HistFamily { None, Pillar, Fwcm, Approx }

        /// <summary>Per-forward-leg history-source pin: rank 3 = exact forward ticker, 2 = interp
        /// between bracketing quoted starts, 1 = par approx. Within a session a leg's source may only
        /// ever move UP this ranking — a transient BDH miss on the exact ticker must reuse the pinned
        /// series, never silently rebuild the chart (and every roll overlay riding on it) on a
        /// different-shaped source. A NOK 3y2y/5y5y level chart was flapping between two shapes as one
        /// leg's exact-ticker fetch came and went — same class of flip the 2bp anchor threshold used
        /// to cause. Day-scoped: history legitimately changes shape when the calendar rolls.</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string,
            (int rank, DateTime day, IReadOnlyList<HistPoint> raw, string note, HistFamily fam)> _histSourcePins = new();

        /// <summary>Cross-market ordering pin (per query, per day): "positive at entry" decides side
        /// order ONCE, so two near-equal markets can't flip the chart's sign between live ticks.</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string,
            (DateTime day, bool aLarger)> _crossOrderPins = new();

        private (IReadOnlyList<HistPoint> hist, string note, HistFamily family) LegHistory(Leg leg, CurrencyConfig cfg,
            ProductKind product, string source, string fwdId, FwdTickerStyle style, CurveSet curves,
            double? expectPct = null, bool full = false, bool anchor = true, string? band = null)
        {
            // A history source must sit on OUR curve's basis. The old rule DISCARDED FWCM when its
            // last close was >5bp from our live leg value — but EUR's S0201 drift parks legs right
            // ON that threshold, so intraday ticks flipped the source (FWCM ↔ approx) and every
            // stat/chart silently rebuilt on a different-shaped series. Instead: ANCHOR (constant
            // shift preserves all daily changes) and only reject outright garbage (>50bp = wrong
            // series entirely).
            bool Usable(IReadOnlyList<HistPoint> h) =>
                expectPct == null || h.Count == 0 || Math.Abs(h[^1].Value - expectPct.Value) <= 0.50;
            // Anchoring is the CALLER's decision and has NO threshold. It used to fire per leg
            // whenever the gap to our curve exceeded 2bp, which made it a coin-flip on live ticks: a
            // fly's x2 leg sitting at 1.81bp shifted the whole "in 1y" overlay by 3.6bp the moment it
            // crossed, and back again when it ticked under. A shift that size is indistinguishable
            // from real roll on a structure whose 1y range is 25bp. See AttachStructureHistory for
            // the policy that replaced it.
            (IReadOnlyList<HistPoint> h, string note) Anchored(IReadOnlyList<HistPoint> h, string note)
            {
                if (!anchor || expectPct is not double exp || h.Count == 0) return (h, note);
                double shift = exp - h[^1].Value;
                if (Math.Abs(shift) < 1e-9) return (h, note);
                h = h.Select(p => new HistPoint(p.Date, p.Value + shift)).ToList();
                return (h, note + $" anchored {shift * 100.0:+0.0;-0.0}bp");
            }

            // 1. spot leg -> pillar ticker history (interpolating between the two nearest quoted tenors)
            // (never anchored: a spot pillar series IS the instrument, not a proxy for it)
            if (leg.StartKind == StartKind.Spot && leg.Tenor != null)
            {
                var (ph, pn) = PillarHistory(cfg, product, TenorUtil.ApproxMonths(leg.Tenor) / 12.0, source, full, band);
                return (ph, pn, HistFamily.Pillar);
            }

            // approx forward from par pillar histories: fwd(a,b) ~ (b*par_b - a*par_a)/(b-a).
            // Shared by the forward-leg resolver (rung 1) and dated/IMM legs (their only rung).
            (IReadOnlyList<HistPoint> h, string note) ParApprox(double aY, double bY)
            {
                var (pa0, _) = PillarHistory(cfg, product, aY, source, full, band);
                var (pb0, _) = PillarHistory(cfg, product, bY, source, full, band);
                if (pa0.Count < 10 || pb0.Count < 10)
                    return (Array.Empty<HistPoint>(), "approx failed (missing pillars)");
                IReadOnlyList<HistPoint> comb = CombineSeries(new List<IReadOnlyList<HistPoint>> { pa0, pb0 },
                    new[] { -aY / (bY - aY), bY / (bY - aY) }, scaleToBp: false);
                return (comb, $"approx (b·{bY:0.#}y − a·{aY:0.#}y par)");
            }

            // 2. forward leg — resolve the best available rung, then apply the SOURCE PIN: within a
            // session the source may only move UP the ranking (approx -> interp -> exact), so a
            // transient miss on the better source reuses its pinned series instead of rebuilding the
            // chart on a different shape. One discipline for all currencies and structures.
            if (leg.StartKind == StartKind.Forward && leg.Tenor != null)
            {
                double aY = TenorUtil.ApproxMonths(leg.ForwardStart!) / 12.0;
                if (aY < 1e-6) // degenerate zero-day forward = the spot instrument
                {
                    var (sh0, sn0) = PillarHistory(cfg, product, TenorUtil.ApproxMonths(leg.Tenor) / 12.0, source, full, band);
                    return (sh0, sn0, HistFamily.Pillar);
                }
                var tenorS = ForwardTicker.Code(leg.Tenor);
                (int rank, IReadOnlyList<HistPoint> raw, string note, HistFamily fam) cand = default;

                // rung 3: the exact forward ticker. "fwd", not "FWCM": the year-pair family is not the
                // FWCM surface, and the label already names the id ("S0490FS 10Y2Y" / "EUSA 5Y5Y")
                if (!string.IsNullOrEmpty(fwdId)
                    && ForwardTicker.Exact(fwdId, style, leg.ForwardStart!, leg.Tenor) is { } tk)
                {
                    var h = Hist(tk, full);
                    if (h.Count >= 10 && Usable(h))
                        cand = (3, h, $"fwd {ForwardTicker.Label(fwdId, style, leg.ForwardStart!, leg.Tenor)}", HistFamily.Fwcm);
                }

                // rung 2: the exact point is not quoted (a month start, or a year-pair combination with
                // no market): interpolate between the bracketing QUOTED starts. Unlike par-pillar
                // interpolation this keeps the curve's forward curvature, so structure combos stay
                // comparable — and it no longer sets the roll's horizon shape, which now comes from the
                // curve via RollFraction, so gap-filling here cannot flatten roll timing.
                if (cand.rank == 0 && !string.IsNullOrEmpty(fwdId) && tenorS != null)
                {
                    var (lo, hi) = ForwardTicker.Bracket(style, aY);
                    if (lo > 0 && hi > 0
                        && ForwardTicker.AtStartYears(fwdId, style, lo, leg.Tenor) is { } tkLo
                        && ForwardTicker.AtStartYears(fwdId, style, hi, leg.Tenor) is { } tkHi)
                    {
                        var hLo = Hist(tkLo, full);
                        var hHi = Hist(tkHi, full);
                        if (hLo.Count >= 10 && hHi.Count >= 10)
                        {
                            double w = (aY - lo) / (double)(hi - lo);
                            var interp = CombineSeries(new List<IReadOnlyList<HistPoint>> { hLo, hHi },
                                new[] { 1 - w, w }, scaleToBp: false);
                            if (Usable(interp))
                                cand = (2, interp, $"fwd interp {lo}Y{tenorS}/{hi}Y{tenorS}", HistFamily.Fwcm);
                        }
                    }
                }

                // rung 1: par approx
                if (cand.rank == 0)
                {
                    var (ah0, an0) = ParApprox(aY, aY + TenorUtil.ApproxMonths(leg.Tenor) / 12.0);
                    if (ah0.Count > 0) cand = (1, ah0, an0, HistFamily.Approx);
                }

                var pinKey = $"{cfg.Ccy}|{product}|{source}|{fwdId}|{style}|{band}|" +
                             $"{ForwardTicker.Code(leg.ForwardStart!)}|{tenorS ?? leg.Tenor.ToString()}|{full}";
                if (_histSourcePins.TryGetValue(pinKey, out var pin) && pin.day == DateTime.Today
                    && pin.rank > cand.rank)
                    cand = (pin.rank, pin.raw, pin.note + " (pinned)", pin.fam); // hold the better source
                else if (cand.rank > 0)
                    _histSourcePins[pinKey] = (cand.rank, DateTime.Today, cand.raw, cand.note, cand.fam);

                if (cand.rank == 0)
                    return (Array.Empty<HistPoint>(), "approx failed (missing pillars)", HistFamily.None);
                var (fh0, fn0) = Anchored(cand.raw, cand.note);
                return (fh0, fn0, cand.fam);
            }

            // 3. dated/IMM legs: the par approx is their only rung
            double aYears, bYears;
            if (leg.StartKind is StartKind.Date or StartKind.Imm && leg.Tenor != null)
            {
                var start = leg.StartKind == StartKind.Date ? leg.ExplicitStart! : leg.ImmDate!;
                aYears = Math.Max(0, (start - curves.AsOf) / 365.25);
                bYears = aYears + TenorUtil.ApproxMonths(leg.Tenor) / 12.0;
            }
            else return (Array.Empty<HistPoint>(), "no history source", HistFamily.None);

            if (aYears < 1e-6)
            {
                var (sh, sn) = PillarHistory(cfg, product, bYears, source, full, band);
                return (sh, sn, HistFamily.Pillar);
            }

            var (pApp, nApp) = ParApprox(aYears, bYears);
            if (pApp.Count == 0)
                return (Array.Empty<HistPoint>(), nApp, HistFamily.None);
            // the annuity-less approx has a systematic LEVEL bias — anchor it to our curve's value.
            // A constant shift preserves every daily change, so vol/z/Δ stats stay true. Thresholdless
            // and caller-gated for the same reason as Anchored above.
            var (fh, fn) = Anchored(pApp, nApp);
            return (fh, fn, HistFamily.Approx);
        }

        private (IReadOnlyList<HistPoint> hist, string note) PillarHistory(CurrencyConfig cfg,
            ProductKind product, double tenorYears, string source, bool full = false, string? band = null)
        {
            if (History == null) return (Array.Empty<HistPoint>(), "no provider");
            // dual-band markets quote TWO families at overlapping tenors (AUD 4Y-9Y q/q AND s/s,
            // ~26bp apart): a band-restricted request never mixes them — an interpolation or a
            // tie-break across the families books the tenor basis into the series as fake history.
            // If the restricted family cannot serve the request (missing history), fall back to the
            // unrestricted list rather than return nothing.
            (IReadOnlyList<HistPoint> hist, string note) Fallback(string why) =>
                band != null ? PillarHistory(cfg, product, tenorYears, source, full)
                             : (Array.Empty<HistPoint>(), why);
            var pillars = (product == ProductKind.OIS ? cfg.Ois?.Curve : cfg.Irs?.Curve)?
                .Where(p => p.Enabled && !p.Type.Equals("DEPO", StringComparison.OrdinalIgnoreCase) && !p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase)
                            && (band == null || product != ProductKind.IRS || cfg.Irs == null
                                || SwapBuilder.PillarBand(cfg.Irs, p).Equals(band, StringComparison.OrdinalIgnoreCase)))
                .Select(p => (years: TenorUtil.ApproxMonths(TenorUtil.Parse(p.Tenor)) / 12.0,
                              ticker: ConfigStore.ResolveTicker(p.Ticker, source)))
                .OrderBy(x => x.years).ToList();
            if (pillars == null || pillars.Count == 0) return Fallback("no pillars");

            var exact = pillars.FirstOrDefault(x => Math.Abs(x.years - tenorYears) < 1.0 / 24);
            if (exact.ticker != null)
            {
                var he = Hist(exact.ticker, full);
                return he.Count >= 10 ? (he, $"pillar {exact.years:0.#}y") : Fallback("missing pillar history");
            }

            var lo = pillars.LastOrDefault(x => x.years < tenorYears);
            var hi = pillars.FirstOrDefault(x => x.years > tenorYears);
            if (lo.ticker == null || hi.ticker == null)
            {
                var edge = lo.ticker ?? hi.ticker;
                return edge == null
                    ? Fallback("outside pillar range")
                    : (Hist(edge, full), "nearest pillar (extrapolated)");
            }
            double w = (tenorYears - lo.years) / (hi.years - lo.years);
            var hLo = Hist(lo.ticker, full);
            var hHi = Hist(hi.ticker, full);
            if (hLo.Count < 10 || hHi.Count < 10) return Fallback("missing pillar history");
            var interp = CombineSeries(new List<IReadOnlyList<HistPoint>> { hLo, hHi },
                new[] { 1 - w, w }, scaleToBp: false);
            return (interp, $"pillar interp {lo.years:0.#}y/{hi.years:0.#}y");
        }

        // ---------- inflation / Fed Funds / DI ladder point ----------

        private InstrumentResult AnalyzeLadderFallback(ParsedQuery pq, CurrencyConfig cfg)
        {
            if (cfg.Ladders.Count == 0)
                throw new InvalidOperationException($"{cfg.Ccy}: no curve or ladder configured.");
            pq.Target = new CurveTarget(cfg.Ccy, TargetKind.Ladder, cfg.Ladders[0].Name, ProductKind.OIS);
            return AnalyzeLadder(pq, cfg);
        }

        /// <summary>DV01 density per 1mm, per 1bp on the quoted rate, of a zero-coupon inflation swap
        /// (ZCIIS — what the USSWIT/BPSWIT/EUSWI ladders quote).
        ///
        /// A ZCIIS settles as ONE net cashflow at maturity, N.[I(T)/I(0) - (1+K)^T], so both "legs"
        /// pay on the same date and the whole instrument carries a single discount factor off the
        /// nominal (collateral/OIS) curve:
        ///     NPV = N.( P_r(t,T).I(t)/I(0) - P_D(t,T).(1+K)^T )
        /// which is model-independent — no inflation dynamics are needed to price it. Only the fixed
        /// term contains K, so dNPV/dK = -N.P_D(t,T).T.(1+K)^(T-1), i.e. per 1mm per bp:
        ///     100.T.(1+K)^(T-1).DF(T)
        /// The old code omitted DF(T), overstating the risk by 23% at 5y and 267% at 30y.
        ///
        /// T is the market convention's WHOLE NUMBER OF YEARS, not a day-count year fraction: using
        /// the ladder's Dcc here would overstate a 5y by ~1.4% on ACT/360. The indexation lag (US/EUR
        /// 3m, UK 2m) sits entirely inside I(T), i.e. the inflation leg, so it does not enter dNPV/dK
        /// at all — nor does seasonality. Payment-delay convexity is measured at under 1bp on the rate.
        ///
        /// Sources: Mercurio, "Pricing inflation-indexed derivatives", Quantitative Finance 5(3) 2005
        /// (= Brigo and Mercurio, Interest Rate Models, 2nd ed., ch. 15) for the payoff and the
        /// model-independent NPV; Zine-eddine, "Inflation: Instruments and curve construction",
        /// OpenGamma Quantitative Research n. 19, Jan 2014, section 3.1 eq (3) for nominal discounting
        /// and the whole-year exponent, Table 1 for the per-area lag conventions; Zine-eddine,
        /// "Convexity adjustment for inflation derivatives", OpenGamma QR, Jan 2014, for the
        /// sub-basis-point payment-delay convexity.</summary>
        private static double ZcInflationDensityPerMm(double tYears, double ratePct, double df) =>
            100.0 * tYears * Math.Pow(1 + ratePct / 100.0, tYears - 1) * df;

        /// <summary>Nominal (collateral/OIS) discount factor for a ladder maturity — what a ZCIIS's
        /// single terminal cashflow discounts at. Returns 1.0 plus a note when no OIS curve is live,
        /// so the DV01 degrades to an openly-labelled undiscounted figure rather than a silently
        /// wrong one. Caller is already inside _gate.</summary>
        private (double df, string? note) NominalDf(CurrencyConfig cfg, ParsedQuery pq, Date maturity)
        {
            if (cfg.Ois == null)
                return (1.0, $"DV01 UNDISCOUNTED — {cfg.Ccy} has no nominal OIS curve configured to discount on.");
            try
            {
                var curves = GetCurvesUnlocked(cfg, pq.Source ?? SourceFor(cfg.Ccy));
                if (curves.Ois == null)
                    return (1.0, $"DV01 UNDISCOUNTED — no live {cfg.Ois.IndexName} OIS curve to discount on right now.");
                Settings.setEvaluationDate(curves.AsOf);
                bool beyond = maturity > curves.Ois.maxDate();
                double df = curves.Ois.discount(maturity, true);
                if (df <= 0 || df > 1.5)
                    return (1.0, $"DV01 UNDISCOUNTED — {cfg.Ccy} OIS curve returned an unusable discount factor ({df:0.####}).");
                return (df, beyond
                    ? $"DF extrapolated past the {cfg.Ccy} OIS curve's last pillar."
                    : null);
            }
            catch (Exception ex)
            {
                return (1.0, $"DV01 UNDISCOUNTED — {cfg.Ccy} OIS curve unavailable ({ex.Message}).");
            }
        }

        /// <summary>DV01 density per 1mm, per 1bp, of a par BUS/252 zero-coupon DI swap (BRL pré x DI).
        ///
        /// A pré x DI swap is zero-coupon like the ZCIIS: the fixed side accrues to N.(1+r)^(du/252)
        /// at maturity, the DI side accrues realised compounded CDI and is worth N today, and the DI
        /// curve discounts itself (brl.json "discounting": "SELF"), DF(T) = (1+y)^(-du/252). So
        ///     NPV(receive fixed) = N.(1+r)^(du/252).DF(T) - N
        ///     dNPV/dr            = N.DF(T).(du/252).(1+r)^(du/252-1)
        /// and at the quoted PAR rate (r = y) the powers cancel exactly:
        ///     dNPV/dr = N.(du/252)/(1+r)          density per mm per bp = 100.(du/252)/(1+r)
        ///
        /// No DI-futures strip bootstrap is needed for this: a par zero-coupon swap self-discounts.
        /// (brl.json's "full ZC BUS/252 pricing is roadmap" note is only true for marking OFF-MARKET
        /// or seasoned trades, where the quoted rate no longer equals the discount rate.)
        ///
        /// Settlement: the desk trades this offshore, USD-settled (CME). That does not change the
        /// number — for a BRL-notional USD-settled NDS the USD discount factor cancels against the
        /// FX forward under covered interest parity, leaving USD risk = BRL risk x SPOT, which is
        /// exactly what the "$01" column already computes. The residual is the onshore/offshore
        /// (cupom cambial) basis, which we hold no data for.
        ///
        /// Sources: B3's One-day Interbank Deposit futures (DI1) contract specification — notional
        /// BRL 100,000 at maturity, quoted "as a percentage rate per annum compounded daily based on
        /// a 252-day year", PU = "BRL 100,000 discounted the trading rate"; the CRAN `rb3` package's
        /// historical-rates vignette states the inversion explicitly as
        /// rate = (100000/price)^(252/business_days) - 1, i.e. PU = 100,000.(1+r)^(-du/252), which
        /// pins the discount-factor convention used above.</summary>
        private static double DiDensityPerMm(double yearsBus252, double ratePct) =>
            100.0 * yearsBus252 / (1 + ratePct / 100.0);

        /// <summary>Business-252 (Brazil) year fraction, the du/252 a DI swap accrues on.</summary>
        private static double Bus252Years(Date from, Date to) =>
            new Business252(new Brazil()).yearFraction(from, to);

        /// <summary>A BUS/252 exponential ladder, i.e. the BRL pré x DI family — the zero-coupon DI
        /// closed form applies rather than a par-swap annuity. Read off the ladder's own Dcc so it
        /// can't drift from the config.</summary>
        private static bool IsBus252(Ladder lad) =>
            lad.Dcc.Replace(" ", "").Equals("BUS/252", StringComparison.OrdinalIgnoreCase)
            || lad.Dcc.Replace(" ", "").Equals("ACT/252", StringComparison.OrdinalIgnoreCase);

        private readonly Dictionary<(string ccy, string ladder, string src),
            (long version, DateTime builtUtc, CurveSet curves)> _ladderCurveCache = new();

        /// <summary>The ladder this trade must price on instead of the currency's default OIS curve, or
        /// null for the overwhelming majority of trades.
        ///
        /// <para>Three ways in, in order of precedence. Explicit — something set
        /// <see cref="ParsedQuery.CurveLadder"/>. Anchored — the query named a meeting run
        /// ("usd jul fomc 5y"). Or DATED — every leg both starts and ends on that central bank's meeting
        /// dates, which is what a Fed-dated trade IS, so a row pasted off the meetings board routes itself
        /// without needing new syntax. The schedule dates are config, not a guess.</para>
        ///
        /// <para>A bare "usd ff 3m" stays a ladder POINT query and never reaches here, and an ordinary
        /// "usd 5y" or "usd 5y5y" is untouched — USD tenor swaps and forwards are SOFR.</para></summary>
        private Ladder? PolicyLadderFor(ParsedQuery pq, CurrencyConfig cfg)
        {
            string? name = pq.CurveLadder;
            if (name == null)
            {
                var sched = MeetingsStore.Schedules.FirstOrDefault(s =>
                    s.Ccy.Equals(cfg.Ccy, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(s.PolicyLadder)
                    && (pq.AnchorRun == null || s.Name.Equals(pq.AnchorRun, StringComparison.OrdinalIgnoreCase)));
                if (sched == null) return null;
                bool anchored = pq.AnchorRun != null || pq.Legs.Any(l => l.MeetingLabel != null);
                if (anchored || IsMeetingDated(pq, sched)) name = sched.PolicyLadder;
            }
            if (string.IsNullOrWhiteSpace(name)) return null;
            return cfg.Ladders.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Every leg runs from one of this bank's meeting dates to another. Both ends must land on
        /// the schedule, so an ordinary dated swap that merely happens to start near a meeting is not
        /// swept up. Past meetings count: a Fed-dated trade stays Fed-dated once it has started.</summary>
        private static bool IsMeetingDated(ParsedQuery pq, MeetingScheduleDef sched)
        {
            if (pq.Legs.Count == 0) return false;
            var days = sched.Dates.Concat(sched.PastDates).Select(d => d.Date).ToHashSet();
            if (days.Count == 0) return false;
            foreach (var leg in pq.Legs)
            {
                if (leg.StartKind != StartKind.Date || leg.ExplicitStart == null) return false;
                var s = new DateTime(leg.ExplicitStart.year(), leg.ExplicitStart.month(), leg.ExplicitStart.Day);
                if (!days.Contains(s.Date)) return false;
                var e = leg.ExplicitEnd;
                if (e == null && leg.Tenor != null)
                    e = SwapBuilder.MaturityDate(leg.ExplicitStart, leg.Tenor);
                if (e == null) return false;
                if (!days.Contains(new DateTime(e.year(), e.month(), e.Day).Date)) return false;
            }
            return true;
        }

        /// <summary>Curve bootstrapped from a par-swap ladder's OWN quotes. The Fed Funds ladder
        /// (USSOA..USSO30) is a complete 1M-30Y OIS par-rate strip that happens to live in a ladder
        /// rather than a curve config, so its DV01 is a real fixed-leg annuity — not the undiscounted
        /// 100.T the ladder branch used to report (11% too big at 5y on live levels).
        ///
        /// Built by cloning the currency's own OIS conventions and swapping in the ladder's pillars,
        /// so the annuity comes from the same tested bootstrap + pricer every other USD swap uses.
        /// Self-discounting on the bootstrapped strip: a cleared FF OIS would discount on SOFR, but
        /// the FF/SOFR basis moves a 5y annuity by ~0.1%, i.e. nothing next to the 11% being fixed.
        /// Cached on the same version/1s rule as the main curve set. Caller holds _gate.</summary>
        private CurveSet? LadderParCurve(CurrencyConfig cfg, Ladder lad, string src)
        {
            if (cfg.Ois == null || lad.Pillars.Count < 2) return null;
            var key = (cfg.Ccy.ToUpperInvariant(), lad.Name.ToUpperInvariant(), src.ToUpperInvariant());
            long v = Snapshot.Version;
            if (_ladderCurveCache.TryGetValue(key, out var hit)
                && (hit.version == v || (DateTime.UtcNow - hit.builtUtc).TotalMilliseconds < 1000))
            {
                Settings.setEvaluationDate(hit.curves.AsOf);
                return hit.curves;
            }
            // round-trip clone so every convention (fixing days, pay lag, freq, short-ZC rule) is
            // inherited rather than hand-copied, then point the OIS curve at the ladder's strip
            var synth = System.Text.Json.JsonSerializer.Deserialize<CurrencyConfig>(
                System.Text.Json.JsonSerializer.Serialize(cfg));
            if (synth?.Ois == null) return null;
            synth.Ois.Curve = lad.Pillars;
            synth.Ois.Variants.Clear();
            synth.Ois.IndexName = lad.Name;   // label only — OvernightIndex takes the name verbatim
            // The ladder is a DIFFERENT index from the currency's OIS, so its own identifiers must
            // replace the inherited ones. Leaving cfg.Ois's behind meant a Fed Funds trade quoting
            // FEDL01 loaded SOFRRATE fixings for its elapsed accrual and cross-checked its forwards
            // against the SOFR surface.
            if (!string.IsNullOrWhiteSpace(lad.FixingTicker)) synth.Ois.OnFixingTicker = lad.FixingTicker;
            if (!string.IsNullOrWhiteSpace(lad.Dcc)) synth.Ois.IndexDcc = lad.Dcc;
            synth.Ois.FwdCurveId = lad.FwdCurveId;   // "" is correct when the ladder has no forward curve
            synth.Ois.FwdTickerStyle = "";
            synth.Irs = null;                 // the ladder is the whole curve; don't rebuild the IRS side
            synth.Ladders = new List<Ladder>();
            try
            {
                // ladder pillars are snapshotted and read with an EMPTY source ("USSO5 Curncy", not
                // "USSO5 BGN Curncy") — build with the same so the bootstrap finds the very quotes the
                // ladder itself is showing, rather than silently missing every pillar
                var curves = CurveBuilder.Build(synth, "", Snapshot, AdjustedToday(cfg),
                    datedOis: MeetingDatedPillars(cfg, lad));
                if (curves.Ois == null) return null;
                _ladderCurveCache[key] = (v, DateTime.UtcNow, curves);
                return curves;
            }
            catch { return null; }
        }

        /// <summary>Meeting-dated OIS pillars for a ladder that is a central bank's POLICY curve, or empty.
        ///
        /// <para>These are the instruments the market actually trades on decision dates - USSOFED{N} for the
        /// FOMC - and bootstrapping them is what makes the curve reprice the quoted meeting OIS. Without
        /// them the strip is a smooth 1M/2M/3M par curve that smears each policy step across the meeting:
        /// measured -2.1bp against USSOFED1 and +5.2bp against USSOFED2 on 2026-07-30.</para>
        ///
        /// <para>The mapping is derived from each ticker's OWN maturity, never from its number, because the
        /// numbers lie. Probed 2026-07-30: the family stops at USSOFED9, and USSOFED10..13 all resolve to
        /// "USD FOMC FedFund OIS 1ST" maturing 2026-10-28 - double-digit N aliases straight back to the 1st
        /// contract. Bootstrapping those as if they were 2027-28 meetings would inject four pillars at the
        /// 1st contract's rate. So a ticker is only used when its maturity IS the period end we are about to
        /// assign it, which also pins the off-by-one: USSOFED{N} spans meeting N to meeting N+1, i.e. the
        /// period ENDING at its maturity, and the run-down USSOFED0 covers spot to the first meeting.</para>
        ///
        /// <para>An unquoted run-down is normal (it was on 2026-07-30) - the config's own short tenor
        /// pillars carry the front, and only pillars maturing strictly INSIDE the dated range are
        /// displaced.</para></summary>
        private List<(Date Start, Date End, string Ticker, string Label)> MeetingDatedPillars(
            CurrencyConfig cfg, Ladder lad)
        {
            var outp = new List<(Date, Date, string, string)>();
            var sched = MeetingsStore.Schedules.FirstOrDefault(x =>
                x.Ccy.Equals(cfg.Ccy, StringComparison.OrdinalIgnoreCase)
                && lad.Name.Equals(x.PolicyLadder, StringComparison.OrdinalIgnoreCase));
            if (sched == null || sched.Tickers.Count == 0) return outp;
            try
            {
                var res = ResolveMeetingDates(sched, maxRows: 13);
                if (res.Dates.Count == 0) return outp;
                var cal = QL.QlMaps.MakeCalendar(cfg.Calendar);
                var asOf = AdjustedToday(cfg);
                var spot = SwapBuilder.SpotDate(cfg, cal, asOf);

                // meeting dates in order, so period k runs ordered[k-1] -> ordered[k] (spot for k = 0)
                var ordered = res.Dates.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
                for (int k = 0; k < ordered.Count; k++)
                {
                    var end = ordered[k];
                    var endD = new Date(end.Day, (Month)end.Month, end.Year);
                    var startD = k == 0 ? spot
                        : new Date(ordered[k - 1].Day, (Month)ordered[k - 1].Month, ordered[k - 1].Year);
                    if (endD <= startD || endD <= asOf) continue;

                    // the ticker for the period ENDING here is the k-th contract (0 = run-down)
                    var tick = MeetingTick(sched, sched.Tickers[0], k);
                    var q = Snapshot.Get(tick);
                    if (q?.Mid == null) continue;                       // unquoted run-down is normal
                    // AUTHORITATIVE: its own maturity must be this period's end, or the number is lying
                    if (q.Maturity == null || q.Maturity.Value.Date != end.Date) continue;
                    outp.Add((startD, endD, tick, $"{sched.Name}{k} {end:dd-MMM-yy}"));
                }
            }
            catch { /* a meeting-curve upgrade must never break the plain ladder build */ }
            return outp;
        }

        /// <summary>Real DV01 density per 1mm for a par-swap ladder point, off the ladder's own
        /// bootstrapped curve. Null when the curve can't be built (quote outage), so the caller can
        /// fall back to the undiscounted annuity with a note instead of a silently wrong number.</summary>
        private double? LadderParDensity(CurrencyConfig cfg, Ladder lad, string src, Period tenor,
            Period? forwardStart = null)
        {
            var curves = LadderParCurve(cfg, lad, src);
            if (curves?.Ois == null) return null;
            try
            {
                var spec = new TradeSpec
                {
                    Ccy = cfg.Ccy, Product = ProductKind.OIS, Tenor = tenor,
                    Notional = 1_000_000, Source = src,
                    StartKind = forwardStart != null ? StartKind.Forward : StartKind.Spot,
                    ForwardStart = forwardStart,
                };
                double d = Pricer.Price(spec, curves).Annuity01;
                return d > 0 ? d : null;
            }
            catch { return null; }
        }

        /// <summary>Notional for a ladder point, on the SAME rule as the headline tiles of a swap
        /// (path A) and the ticket tabs (LegNotionalFor): explicit per-leg notional wins and is dealt
        /// exactly, else an explicit or defaulted dv01 target converted through this point's own
        /// density and rounded to a dealable lot, else the legacy flat Notional. `dv01:` on a ladder
        /// used to be parsed and then silently dropped.</summary>
        private (double notional, string? note) LadderNotional(ParsedQuery pq, CurrencyConfig cfg,
            double densityPerMm)
        {
            // the exact channel (blotter positions, real deal sizes) — never rounded, never resized
            if (pq.LegNotionals != null && pq.LegNotionals.Count > 0) return (pq.LegNotionals[0], null);
            double? dv01 = pq.LegDv01s != null && pq.LegDv01s.Count > 0 ? pq.LegDv01s[0] : pq.Dv01Target;
            if (!dv01.HasValue) return (Risk.RiskSizer.RoundNotional(pq.Notional), null);
            double fx;
            try { fx = FxRiskFactor(pq.Dv01Ccy, cfg.Ccy); }
            catch (Exception ex)
            {
                // the $25k default now applies to every ladder, so a missing spot must NOT take the
                // whole query down — fall back to the flat notional and say why
                return (Risk.RiskSizer.RoundNotional(pq.Notional),
                    $"dv01 target not applied — {ex.Message}; showing the flat notional instead.");
            }
            string? note = pq.Dv01Ccy.Equals(cfg.Ccy, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"dv01 input in {pq.Dv01Ccy} × {fx:0.####} → {cfg.Ccy}/bp";
            return (Risk.RiskSizer.Resolve(densityPerMm, explicitDv01: dv01.Value * fx).Notional, note);
        }

        private InstrumentResult AnalyzeLadder(ParsedQuery pq, CurrencyConfig cfg)
        {
            var lad = cfg.Ladders.First(l => l.Name.Equals(pq.Target.LadderName, StringComparison.OrdinalIgnoreCase));
            if (pq.DatedCode != null) return AnalyzeDatedContract(pq, cfg, lad);
            if (pq.Main?.StartKind == StartKind.Forward && pq.Main.ForwardStart != null && pq.Main.Tenor != null)
                return AnalyzeLadderForward(pq, cfg, lad);
            var tenor = pq.Main?.Tenor ?? new Period(5, TimeUnit.Years);
            var pillar = NearestPillar(lad.Pillars, tenor);
            string ticker = ConfigStore.ResolveTicker(pillar.Ticker, "");
            var q = Snapshot.Get(ticker);

            bool infl = lad.Kind.Equals("INFLATION", StringComparison.OrdinalIgnoreCase);
            var r = new InstrumentResult
            {
                Query = pq.Raw,
                Label = $"{cfg.Ccy} {lad.Name} {TenorUtil.Format(TenorUtil.Parse(pillar.Tenor))}",
                Ccy = cfg.Ccy, Kind = infl ? "Inflation" : "Ladder", Unit = "%",
                Source = "composite",
                ConventionSummary = infl
                    ? $"{lad.Name} zero-coupon breakeven, {lad.Dcc}"
                    : $"{lad.Name} par rate, {lad.Dcc}",
                PrimaryTicker = ticker,
                Bid = q?.Bid, Ask = q?.Ask, Mid = q?.Mid,
                ParRatePct = q?.Mid,
            };
            if (q?.Bid != null && q.Ask != null) r.BidAskWideBp = (q.Ask - q.Bid) * 100.0;

            // dates + first-order risk (no QLNet pricing behind ladder quotes — set explicitly)
            double tYlo = TenorUtil.ApproxMonths(TenorUtil.Parse(pillar.Tenor)) / 12.0;
            var qtLo = new Date(DateTime.Today.Day, (Month)DateTime.Today.Month, DateTime.Today.Year);
            var matLo = qtLo + new Period((int)Math.Round(tYlo * 12), TimeUnit.Months);
            // every ladder family now has a real NPV sensitivity behind it: a discounted ZC inflation
            // swap, a closed-form BUS/252 DI swap, or a bootstrapped par-swap annuity. The bare
            // 100·T survives only as a labelled fallback for a quote outage.
            double densLo;
            string src = pq.Source ?? SourceFor(cfg.Ccy);
            if (infl && q?.Mid is double km)
            {
                var (df, dfNote) = NominalDf(cfg, pq, matLo);
                densLo = ZcInflationDensityPerMm(tYlo, km, df);
                if (dfNote != null) r.Notes.Add(dfNote);
            }
            else if (IsBus252(lad) && q?.Mid is double dim)
            {
                densLo = DiDensityPerMm(Bus252Years(qtLo, matLo), dim);
                r.Notes.Add($"DV01 = dNPV/dr of the par BUS/252 zero-coupon {lad.Name} swap, "
                    + "self-discounted. USD-settled offshore: the USD discount factor cancels against "
                    + "the FX forward under CIP, so $01 = DV01 × spot; the residual is the "
                    + "onshore/offshore (cupom cambial) basis, which is not modelled.");
            }
            else if (LadderParDensity(cfg, lad, src, TenorUtil.Parse(pillar.Tenor)) is double parDens)
            {
                densLo = parDens;
                r.Notes.Add($"DV01 = fixed-leg annuity off the {lad.Name} curve bootstrapped from "
                    + "these same quotes (self-discounted).");
            }
            else
            {
                densLo = 100.0 * tYlo;
                r.Notes.Add($"DV01 UNDISCOUNTED — no {cfg.Ccy} {lad.Name} curve to build an annuity from "
                    + "right now, so this is the raw N·T approximation.");
            }
            var (notLo, fxNoteLo) = LadderNotional(pq, cfg, densLo);
            if (fxNoteLo != null) r.Notes.Add(fxNoteLo);
            r.Effective = qtLo;
            r.Maturity = matLo;
            r.Dv01 = densLo * notLo / 1_000_000.0;
            r.Legs.Add(new LegResult
            {
                Label = TenorUtil.Format(TenorUtil.Parse(pillar.Tenor)), Weight = 1,
                RatePct = q?.Mid ?? 0, Notional = notLo,
                Effective = qtLo, Maturity = matLo,
                Dv01 = densLo * notLo / 1_000_000.0,
                DensityPerMm = densLo,
            });

            var pts = LiveLadderPoints(lad);
            if (pts.Count >= 2)
            {
                double tY = TenorUtil.ApproxMonths(TenorUtil.Parse(pillar.Tenor)) / 12.0;
                double? now = Interp(pts, tY);
                foreach (var (label, h) in new[] { ("3M", 0.25), ("6M", 0.5), ("1Y", 1.0) })
                {
                    double? back = tY - h > 0 ? Interp(pts, tY - h) : null;
                    if (now.HasValue && back.HasValue)
                        r.RollBp.Add(new KeyValuePair<string, double>(label, (now.Value - back.Value) * 100.0));
                }
            }
            if (History != null && !pq.SkipHistory)
            {
                var hist = CleanCombined(Hist(ticker, full: true), inBp: false);
                if (hist.Count > 5)
                {
                    r.History = SliceLookback(hist);
                    r.FullHistory = hist;
                    ApplyMidOverride(pq, r);
                    r.Stats = SeriesStats.Compute(hist, liveLast: r.Mid, changeScale: 100.0,
                        basisRef: r.MidTrue ?? r.Mid);
                    if (r.Stats?.SuppressReason is string lw) r.Notes.Add($"level stats withheld: {lw}.");

                    // roll overlays: where this point WILL BE in 3m/6m/9m/1y — the (t − h) ladder
                    // point, historically (same units/axis as the level)
                    double tY = TenorUtil.ApproxMonths(TenorUtil.Parse(pillar.Tenor)) / 12.0;
                    foreach (var (label, hMonths) in RollHorizons)
                    {
                        double rolledT = tY - hMonths / 12.0;
                        if (rolledT <= 0.05) continue;
                        var rolled = CleanCombined(LadderHistoryAt(lad, rolledT), inBp: false);
                        if (rolled.Count > 10)
                            r.RollOverlays.Add((label, rolled));
                    }
                }
            }
            // exact Δ 1d from the quote's own prev close — ladder history can lag a day
            if (r.Stats != null && q?.CoDBp is double lcod) r.Stats.Chg1d = lcod + OvrShiftBp(r);
            if (infl) r.Notes.Add("ZC breakeven from quotes; no seasonality adjustment. "
                + "DV01 = dNPV/dK of the zero-coupon inflation swap, discounted on the nominal OIS curve.");
            return r;
        }

        /// <summary>Forward point on a quoted ladder. For INFLATION (zero-coupon) ladders the forward
        /// breakeven compounds EXACTLY from the ZC quotes: (1+f)^(b-a) = (1+z_b)^b / (1+z_a)^a.
        /// For RATE ladders (Fed Funds, DI) an annuity-less approximation (b·r_b − a·r_a)/(b−a) is used.</summary>
        private InstrumentResult AnalyzeLadderForward(ParsedQuery pq, CurrencyConfig cfg, Ladder lad)
        {
            double a = TenorUtil.ApproxMonths(pq.Main!.ForwardStart!) / 12.0;
            double tn = TenorUtil.ApproxMonths(pq.Main.Tenor!) / 12.0;
            double b = a + tn;
            var pts = LiveLadderPoints(lad);
            if (pts.Count < 2) throw new InvalidOperationException($"{cfg.Ccy} {lad.Name}: not enough live quotes for forwards.");
            double? za = Interp(pts, a), zb = Interp(pts, b);
            if (za == null || zb == null || b > pts[^1].years + 0.02)
                throw new InvalidOperationException($"{cfg.Ccy} {lad.Name}: {b:0.#}y beyond quoted ladder ({pts[^1].years:0.#}y max).");

            bool infl = lad.Kind.Equals("INFLATION", StringComparison.OrdinalIgnoreCase);
            double fwd = infl
                ? (Math.Pow(Math.Pow(1 + zb.Value / 100, b) / Math.Pow(1 + za.Value / 100, a), 1.0 / tn) - 1) * 100
                : (b * zb.Value - a * za.Value) / tn;

            string startS = TenorUtil.Format(pq.Main.ForwardStart!);
            string tenorS = TenorUtil.Format(pq.Main.Tenor!);
            var r = new InstrumentResult
            {
                Query = pq.Raw,
                Label = $"{cfg.Ccy} {lad.Name} {startS}{tenorS} fwd",
                Ccy = cfg.Ccy, Kind = infl ? "Inflation fwd" : "Ladder fwd", Unit = "%", Source = "composite",
                ConventionSummary = infl
                    ? $"forward ZC breakeven, exact compounding from {lad.Name} quotes"
                    : IsBus252(lad)
                        ? $"forward from {lad.Name} quotes, BUS/252 zero-coupon dv01"
                        : $"forward from {lad.Name} quotes, dv01 off the bootstrapped {lad.Name} curve",
                Mid = fwd, ParRatePct = fwd,
            };

            // dates + first-order risk (these have no QLNet pricing behind them, so set explicitly):
            // a forward ZCIIS is still ONE net cashflow, paid at the FAR date, so dNPV/df carries
            // DF(b) — see ZcInflationDensityPerMm. Rate ladders stay the undiscounted annuity N·T·1e-4.
            var qtLf = new Date(DateTime.Today.Day, (Month)DateTime.Today.Month, DateTime.Today.Year);
            var effLf = qtLf + new Period((int)Math.Round(a * 12), TimeUnit.Months);
            var matLf = qtLf + new Period((int)Math.Round(b * 12), TimeUnit.Months);
            double densLf;
            string srcF = pq.Source ?? SourceFor(cfg.Ccy);
            if (infl)
            {
                var (dfF, dfNoteF) = NominalDf(cfg, pq, matLf);
                densLf = ZcInflationDensityPerMm(tn, fwd, dfF);
                if (dfNoteF != null) r.Notes.Add(dfNoteF);
            }
            else if (IsBus252(lad))
            {
                // forward zero-coupon DI: notional starts at a, single settlement at b, so
                // NPV = N·[DF(a) − DF(b)·(1+f)^τ] and at par dNPV/df = N·DF(a)·τ/(1+f)
                double tauF = Bus252Years(effLf, matLf);
                double dfA = Math.Pow(1 + za.Value / 100.0, -Bus252Years(qtLf, effLf));
                densLf = 100.0 * tauF * dfA / (1 + fwd / 100.0);
            }
            else if (LadderParDensity(cfg, lad, srcF, pq.Main.Tenor!, pq.Main.ForwardStart) is double parDensF)
            {
                densLf = parDensF;
            }
            else
            {
                densLf = 100.0 * tn;
                r.Notes.Add($"DV01 UNDISCOUNTED — no {cfg.Ccy} {lad.Name} curve to build a forward "
                    + "annuity from right now, so this is the raw N·T approximation.");
            }
            var (notLf, fxNoteLf) = LadderNotional(pq, cfg, densLf);
            if (fxNoteLf != null) r.Notes.Add(fxNoteLf);
            r.Effective = effLf;
            r.Maturity = matLf;
            r.Dv01 = densLf * notLf / 1_000_000.0;

            // cross-check vs the published forward ticker where one exists (FWISUS55 etc.)
            var leg = new LegResult
            {
                Label = $"{startS}{tenorS}", Weight = 1,
                RatePct = fwd, Notional = notLf,
                Effective = effLf, Maturity = matLf,
                Dv01 = densLf * notLf / 1_000_000.0,
                DensityPerMm = densLf,
            };
            if (!string.IsNullOrEmpty(lad.FwdTickerPattern)
                && Math.Abs(a - Math.Round(a)) < 1e-6 && Math.Abs(tn - Math.Round(tn)) < 1e-6)
            {
                var tk = lad.FwdTickerPattern.Replace("{A}", ((int)Math.Round(a)).ToString())
                                             .Replace("{B}", ((int)Math.Round(tn)).ToString());
                if (Snapshot.TryGetMid(tk, out var v))
                {
                    leg.BbgFwdPct = v;
                    leg.BbgFwdTicker = tk;
                }
            }
            r.Legs.Add(leg);

            // roll: same forward one horizon earlier along today's ladder
            foreach (var (lbl, h) in new[] { ("3M", 0.25), ("6M", 0.5), ("1Y", 1.0) })
            {
                double a2 = a - h;
                if (a2 < 0) continue;
                double? za2 = Interp(pts, a2), zb2 = Interp(pts, a2 + tn);
                if (za2 == null || zb2 == null) continue;
                double f2 = infl
                    ? (Math.Pow(Math.Pow(1 + zb2.Value / 100, a2 + tn) / Math.Pow(1 + za2.Value / 100, a2), 1.0 / tn) - 1) * 100
                    : ((a2 + tn) * zb2.Value - a2 * za2.Value) / tn;
                r.RollBp.Add(new KeyValuePair<string, double>(lbl, (fwd - f2) * 100.0));
            }

            // history: published forward ticker first, else approx from ladder pillar histories
            if (History != null && !pq.SkipHistory)
            {
                if (leg.BbgFwdTicker != null)
                {
                    var h = CleanCombined(Hist(leg.BbgFwdTicker, full: true), inBp: false);
                    if (h.Count > 10)
                    {
                        r.History = SliceLookback(h);
                        r.FullHistory = h;
                        ApplyMidOverride(pq, r);
                        r.Stats = SeriesStats.Compute(h, liveLast: r.Mid, changeScale: 100.0,
                            basisRef: r.MidTrue ?? r.Mid);
                        if (r.Stats?.SuppressReason is string hw) r.Notes.Add($"level stats withheld: {hw}.");
                        leg.HistoryNote = leg.BbgFwdTicker;
                    }
                }
                if (r.Stats == null)
                {
                    var ha = LadderHistoryAt(lad, a, full: true);
                    var hb = LadderHistoryAt(lad, b, full: true);
                    if (ha.Count > 10 && hb.Count > 10)
                    {
                        var combined = CleanCombined(CombineSeries(new List<IReadOnlyList<HistPoint>> { ha, hb },
                            new[] { -a / tn, b / tn }, scaleToBp: false), inBp: false);
                        r.History = SliceLookback(combined);
                        r.FullHistory = combined;
                        ApplyMidOverride(pq, r);
                        r.Stats = SeriesStats.Compute(combined, liveLast: r.Mid, changeScale: 100.0,
                            basisRef: r.MidTrue ?? r.Mid);
                        if (r.Stats?.SuppressReason is string kw) r.Notes.Add($"level stats withheld: {kw}.");
                        leg.HistoryNote = "approx from ZC pillar history";
                    }
                }

                // exact Δ 1d from the ladder quotes' prev closes (same formula as the live fwd) —
                // the forward history's last point can predate today
                if (r.Stats != null)
                {
                    var prevPts = new List<(double years, double rate)>();
                    foreach (var p in lad.Pillars.Where(p => p.Enabled && !p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase)))
                    {
                        var full = ConfigStore.ResolveTicker(p.Ticker, "");
                        if (Snapshot.Get(full)?.PrevClose is double pc)
                            prevPts.Add((TenorUtil.ApproxMonths(TenorUtil.Parse(p.Tenor)) / 12.0, pc));
                    }
                    prevPts = prevPts.OrderBy(x => x.years).ToList();
                    double? pza = Interp(prevPts, a), pzb = Interp(prevPts, b);
                    if (pza != null && pzb != null && b <= (prevPts.Count > 0 ? prevPts[^1].years + 0.02 : 0))
                    {
                        double fwdPrev = infl
                            ? (Math.Pow(Math.Pow(1 + pzb.Value / 100, b) / Math.Pow(1 + pza.Value / 100, a), 1.0 / tn) - 1) * 100
                            : (b * pzb.Value - a * pza.Value) / tn;
                        r.Stats.Chg1d = (fwd - fwdPrev) * 100.0 + OvrShiftBp(r);
                    }
                }

                // roll overlays: where the forward WILL BE in 3m/6m/9m/1y — the (a−h) x tn forward,
                // historically (annuity-less approx from ladder pillars; same units/axis as the level)
                foreach (var (label, hMonths) in RollHorizons)
                {
                    double aRolled = a - hMonths / 12.0;
                    if (r.History.Count <= 10 || aRolled <= 0.05) continue;
                    var hra = LadderHistoryAt(lad, aRolled);
                    var hrb = LadderHistoryAt(lad, aRolled + tn);
                    if (hra.Count > 10 && hrb.Count > 10)
                    {
                        var rolled = CleanCombined(CombineSeries(new List<IReadOnlyList<HistPoint>> { hra, hrb },
                            new[] { -aRolled / tn, (aRolled + tn) / tn }, scaleToBp: false), inBp: false);
                        r.RollOverlays.Add((label, rolled));
                    }
                }
            }
            if (infl) r.Notes.Add("Index-ratio exact; no seasonality adjustment. "
                + "DV01 = dNPV/df of the forward zero-coupon inflation swap, discounted at the far date.");
            return r;
        }

        /// <summary>Ladder pillar history interpolated at t years.</summary>
        private IReadOnlyList<HistPoint> LadderHistoryAt(Ladder lad, double tYears, bool full = false)
        {
            if (History == null) return Array.Empty<HistPoint>();
            var pillars = lad.Pillars.Where(p => p.Enabled && !p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase))
                .Select(p => (years: TenorUtil.ApproxMonths(TenorUtil.Parse(p.Tenor)) / 12.0,
                              ticker: ConfigStore.ResolveTicker(p.Ticker, "")))
                .OrderBy(x => x.years).ToList();
            var exact = pillars.FirstOrDefault(x => Math.Abs(x.years - tYears) < 1.0 / 24);
            if (exact.ticker != null) return Hist(exact.ticker, full);
            var lo = pillars.LastOrDefault(x => x.years < tYears);
            var hi = pillars.FirstOrDefault(x => x.years > tYears);
            if (lo.ticker == null || hi.ticker == null) return Array.Empty<HistPoint>();
            double w = (tYears - lo.years) / (hi.years - lo.years);
            return CombineSeries(new List<IReadOnlyList<HistPoint>>
            {
                Hist(lo.ticker, full),
                Hist(hi.ticker, full),
            }, new[] { 1 - w, w }, scaleToBp: false);
        }

        /// <summary>Dated ladder contract (BRL DI1 futures): jan27/f27 -> ODF27 Comdty, real quote + history.</summary>
        private InstrumentResult AnalyzeDatedContract(ParsedQuery pq, CurrencyConfig cfg, Ladder lad)
        {
            if (string.IsNullOrEmpty(lad.DatedPattern))
                throw new InvalidOperationException($"{cfg.Ccy} {lad.Name}: dated month codes (jan27/f27) are not configured for this curve.");
            string code = pq.DatedCode!;
            string ticker = lad.DatedPattern.Replace("{MY}", code);
            var q = Snapshot.Get(ticker);
            if (q?.Mid == null)
                throw new InvalidOperationException($"{ticker}: contract not listed or no price (DI lists Jan every year, Jul near years only).");

            int month = code[0] == 'F' ? 1 : 7;
            int year = 2000 + int.Parse(code[1..]);
            var cal = QL.QlMaps.MakeCalendar(cfg.Calendar);
            var expiry = cal.adjust(new Date(1, (Month)month, year), QLNet.BusinessDayConvention.Following);

            var r = new InstrumentResult
            {
                Query = pq.Raw,
                Label = $"{cfg.Ccy} {lad.Name} {code} ({(month == 1 ? "Jan" : "Jul")}-{year % 100:00})",
                Ccy = cfg.Ccy, Kind = "Dated", Unit = "%", Source = "B3",
                ConventionSummary = $"{lad.Name} dated contract {ticker}, {lad.Dcc}, expiry {expiry}",
                PrimaryTicker = ticker,
                Bid = q.Bid, Ask = q.Ask, Mid = q.Mid, ParRatePct = q.Mid,
                Maturity = expiry,
            };
            if (q.Bid != null && q.Ask != null) r.BidAskWideBp = (q.Ask - q.Bid) * 100.0;

            // a dated DI contract is a zero-coupon DI swap to the contract's expiry, so it takes the
            // same closed form as a ladder point — see DiDensityPerMm. Dated contracts used to add NO
            // leg at all, so notional/dv01/$01 were simply blank on screen.
            var qtD = new Date(DateTime.Today.Day, (Month)DateTime.Today.Month, DateTime.Today.Year);
            double densD = IsBus252(lad)
                ? DiDensityPerMm(Bus252Years(qtD, expiry), q.Mid.Value)
                : 100.0 * Bus252Years(qtD, expiry);
            var (notD, fxNoteD) = LadderNotional(pq, cfg, densD);
            if (fxNoteD != null) r.Notes.Add(fxNoteD);
            r.Effective = qtD;
            r.Dv01 = densD * notD / 1_000_000.0;
            r.Legs.Add(new LegResult
            {
                Label = code, Weight = 1,
                RatePct = q.Mid.Value, Notional = notD,
                Effective = qtD, Maturity = expiry,
                Dv01 = densD * notD / 1_000_000.0,
                DensityPerMm = densD,
            });

            if (History != null && !pq.SkipHistory)
            {
                var hist = CleanCombined(Hist(ticker, full: true), inBp: false);
                if (hist.Count > 5)
                {
                    r.History = SliceLookback(hist);
                    r.FullHistory = hist;
                    ApplyMidOverride(pq, r);
                    r.Stats = SeriesStats.Compute(hist, liveLast: r.Mid, changeScale: 100.0,
                        basisRef: r.MidTrue ?? r.Mid);
                    if (r.Stats?.SuppressReason is string dw) r.Notes.Add($"level stats withheld: {dw}.");
                    // exact Δ 1d from the contract quote's prev close
                    if (q.CoDBp is double dcod) r.Stats.Chg1d = dcod + OvrShiftBp(r);
                }
            }
            return r;
        }

        // ---------- helpers ----------

        /// <summary>Float-tenor band a leg's quotes belong to (index override else tenor rule); null for OIS.</summary>
        private static string? BandFor(CurrencyConfig cfg, ProductKind product, ParsedQuery pq, int i)
        {
            if (product != ProductKind.IRS || cfg.Irs == null || pq.Legs[i].Tenor == null) return null;
            return SwapBuilder.SelectIrsLeg(cfg.Irs, pq.Legs[i].Tenor!, IdxFor(pq, i)).FloatTenor;
        }

        private string? ResolvePillarTicker(CurrencyConfig cfg, ProductKind product, Period tenor, string source,
            string? band = null)
        {
            var list = product == ProductKind.OIS ? cfg.Ois?.Curve : cfg.Irs?.Curve;
            if (list == null) return null;
            // no band asked = the SCREEN convention: dual-band markets quote two families at one
            // tenor (AUD 4Y-9Y q/q AND s/s, ~26bp apart) and config order must not decide which one
            // the RV scan / corr universe / weekly board quote
            if (band == null && product == ProductKind.IRS && cfg.Irs is { } irs0 && irs0.Legs.Count > 1)
                band = SwapBuilder.SelectIrsLeg(irs0, tenor, null).FloatTenor;
            string want = TenorUtil.Format(tenor);
            var exact = list.FirstOrDefault(p =>
                p.Enabled && !p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase) && TenorUtil.Format(TenorUtil.Parse(p.Tenor)) == want
                && !p.Type.Equals("DEPO", StringComparison.OrdinalIgnoreCase)
                && (band == null || cfg.Irs == null
                    || (p.Band ?? SwapBuilder.SelectIrsLeg(cfg.Irs, TenorUtil.Parse(p.Tenor), null).FloatTenor)
                        .Equals(band, StringComparison.OrdinalIgnoreCase)));
            return exact != null ? ConfigStore.ResolveTicker(exact.Ticker, source) : null;
        }

        private static PillarDef NearestPillar(List<PillarDef> pillars, Period tenor)
        {
            double target = TenorUtil.ApproxMonths(tenor);
            return pillars.Where(p => p.Enabled && !p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => Math.Abs(TenorUtil.ApproxMonths(TenorUtil.Parse(p.Tenor)) - target))
                .First();
        }

        private List<(double years, double rate)> LiveLadderPoints(Ladder lad)
        {
            var pts = new List<(double, double)>();
            foreach (var p in lad.Pillars.Where(p => p.Enabled && !p.Type.Equals("FRA", StringComparison.OrdinalIgnoreCase)))
            {
                var full = ConfigStore.ResolveTicker(p.Ticker, "");
                if (Snapshot.TryGetMid(full, out var m))
                    pts.Add((TenorUtil.ApproxMonths(TenorUtil.Parse(p.Tenor)) / 12.0, m));
            }
            return pts.OrderBy(x => x.Item1).ToList();
        }

        private static double? Interp(List<(double years, double rate)> pts, double t)
        {
            if (pts.Count == 0) return null;
            if (t <= pts[0].years) return pts[0].rate;
            if (t >= pts[^1].years) return pts[^1].rate;
            for (int i = 1; i < pts.Count; i++)
                if (t <= pts[i].years)
                {
                    var (x0, y0) = pts[i - 1]; var (x1, y1) = pts[i];
                    return y0 + (y1 - y0) * (t - x0) / (x1 - x0);
                }
            return pts[^1].rate;
        }

        private static List<HistPoint> CombineSeries(List<IReadOnlyList<HistPoint>> serieses, double[] weights, bool scaleToBp)
        {
            if (serieses.Any(s => s.Count == 0)) return new();
            var maps = serieses.Select(s => s.ToDictionary(p => p.Date.Date, p => p.Value)).ToList();
            var common = maps[0].Keys.ToHashSet();
            for (int i = 1; i < maps.Count; i++) common.IntersectWith(maps[i].Keys);
            double scale = scaleToBp ? 100.0 : 1.0;
            var outp = new List<HistPoint>();
            foreach (var d in common.OrderBy(x => x))
            {
                double v = 0;
                for (int i = 0; i < maps.Count; i++) v += weights[i] * maps[i][d];
                outp.Add(new HistPoint(d, v * scale));
            }
            return outp;
        }
    }
}
