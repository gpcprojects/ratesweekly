using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using QLNet;
using RateDesk.Core.Dates;
using RateDesk.Core.Trades;

namespace RateDesk.Core.Query
{
    /// <summary>
    /// Desk analytics query language. Examples:
    ///   mid m31-5y sofr             5y SOFR from the Jun-2031 IMM
    ///   usd 2s10s / eur 2s5s10s     spot curve spread / fly
    ///   u26 5s10s / z26-5s30s       IMM-dated curve (all legs start on the IMM)
    ///   m31-5s10s20s gbp            IMM-dated fly
    ///   usd 20-jun-29 2s5s          custom-dated curve/fly (all legs start on the date)
    ///   5y2y 7y3y 10y2y cad         forward fly: -1/+2/-1 of forward legs
    ///   aud 25-jun-31 +5y           custom-dated start + tenor
    ///   gbp 01/03/32 - 10y          swap ENDING 01-Mar-2032, 10y tenor
    ///   usd 5y dv01:25k             sized to 25k/bp
    ///   5y us cpi · usd fwd · brl 5y
    /// </summary>
    public sealed class QueryParser
    {
        private readonly IndexRegistry _registry;

        private static readonly Regex Tenor = new(@"^(\d+)(d|w|m|y|p)$", RegexOptions.IgnoreCase);
        private static readonly Regex SignedTenor = new(@"^([+-])(\d+)(d|w|m|y|p)$", RegexOptions.IgnoreCase);
        private static readonly Regex Fwd = new(@"^(\d+)(m|y)(\d+)(m|y)$", RegexOptions.IgnoreCase);
        private static readonly Regex TenorFwd = new(@"^(\d+)(m|y)f(?:wd)?$", RegexOptions.IgnoreCase);
        private static readonly Regex Spread = new(@"^(\d+)s(\d+)s$", RegexOptions.IgnoreCase);
        private static readonly Regex Fly = new(@"^(\d+)s(\d+)s(\d+)s$", RegexOptions.IgnoreCase);
        private static readonly Regex ImmTenor = new(@"^(?:imm)?([hmuz]\d{2})[-\s]?(\d+[my])$", RegexOptions.IgnoreCase);
        private static readonly Regex ImmShape = new(@"^(?:imm)?([hmuz]\d{2})[-\s]?(\d+)s(\d+)s(?:(\d+)s)?$", RegexOptions.IgnoreCase);
        /// <summary>Rolling FRA months, "3x6". Needs the separate "fra" marker to mean anything.</summary>
        private static readonly Regex FraAxB = new(@"^(\d{1,2})x(\d{1,3})$", RegexOptions.IgnoreCase);
        // the sizing vocabulary lives in SizingTokens so the ticket and meeting grammars share ONE
        // copy of it; the token-consumption rules below stay here, because only this parser has a
        // tenor-vs-notional ambiguity ("u26 3m") to weigh
        private static Regex Notional => SizingTokens.NotionalRx;
        private static Regex Dv01Rx => SizingTokens.Dv01Rx;
        private static Regex CcyRiskRx => SizingTokens.CcyRiskRx;
        private static Regex RiskSufRx => SizingTokens.RiskSufRx;
        private static readonly Regex Rate = new(@"^@?(\d+(?:\.\d+)?)%?$", RegexOptions.IgnoreCase);
        private static readonly Regex SourceRx = new(@"^src[:=](\w+)$", RegexOptions.IgnoreCase);
        private static readonly Regex WeightsRx = new(@"^w(?:t|eights?)?[:=]([+-]?\d+(?:\.\d+)?(?:[/,][+-]?\d+(?:\.\d+)?)+)$", RegexOptions.IgnoreCase);
        private static readonly Regex DigitPairRx = new(@"^\d{2,4}$");
        private static readonly Regex IndexTokRx = new(@"^[qs]{2,6}$", RegexOptions.IgnoreCase);

        private static readonly IReadOnlyDictionary<char, string> RiskSymbols = SizingTokens.RiskSymbols;

        private static readonly HashSet<string> MonthWords = new(StringComparer.OrdinalIgnoreCase)
        { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

        /// <summary>Split "52"/"102"/"1010" into (start,tenor) years; null if no sensible split.</summary>
        private static (int a, int b)? SplitDigitPair(string t)
        {
            foreach (var (la, lb) in new[] { (2, 2), (2, 1), (1, 2), (1, 1) })
            {
                if (t.Length != la + lb) continue;
                int a = int.Parse(t[..la]), b = int.Parse(t[la..]);
                if (a >= 1 && a <= 40 && b >= 1 && b <= 40) return (a, b);
            }
            return null;
        }
        private static readonly Regex DatedRx = new(@"^(f|n|jan|jul)(\d{2})$", RegexOptions.IgnoreCase);
        private static readonly Regex GluedDateTenor = new(@"^(.*?)([+-])(\d+(?:d|w|m|y|p))$", RegexOptions.IgnoreCase);
        private static readonly Regex GluedDateShape = new(@"^(.+?)-(\d+)s(\d+)s(?:(\d+)s)?$", RegexOptions.IgnoreCase);
        // "3s/6s" is the AUD 3M/6M index-tag vocabulary — must never become a 3y/6y curve
        private static readonly Regex IdxSlashRx = new(@"^[36]s(?:/[36]s)+$", RegexOptions.IgnoreCase);
        // slash between shape segments only ("5s/10s/20s"); dates like 01/03/32 are untouched
        private static readonly Regex ShapeSlashRx = new(@"(?<=\ds)/(?=\d+s(?:/|$))", RegexOptions.IgnoreCase);

        // words a trader drops between date/IMM and shape: "u26 start 5s10s20s", "16sep26 eff 2s10s"
        private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "start", "starting", "stt", "eff", "effective", "settle", "settling", "settlement",
            "from", "begin", "beginning",
        };

        private static Date NextImm(Date from)
        {
            for (int y = from.year(); y <= from.year() + 2; y++)
                foreach (var m in new[] { 3, 6, 9, 12 })
                {
                    var d = ImmUtil.ThirdWednesday(m, y);
                    if (d > from) return d;
                }
            return ImmUtil.ThirdWednesday(3, from.year() + 3);
        }

        private static double SizeSuffix(double v, string suf) => SizingTokens.SizeSuffix(v, suf);

        public QueryParser(IndexRegistry registry) => _registry = registry;

        // ---------- CROSS-MARKET (BETA) ----------

        /// <summary>Separators that read as "market A against market B" when the next token names a
        /// different market. "vs"/"against" keep their intra-ccy spread meaning when followed by a
        /// tenor ("5y vs 10y"); "x" keeps its size-list meaning ("33m x 50m") because "50m" is not a
        /// market token; "3x6" is glued and never reaches here.</summary>
        private static readonly HashSet<string> CrossSeps = new(StringComparer.OrdinalIgnoreCase)
            { "vs", "v", "versus", "against", "over", "minus", "less", "x" };

        /// <summary>Region words that prefix an index alias ("us cpi", "uk rpi", "eu hicp") — they can
        /// open side B right after a separator, and they are market-ish for inheritance purposes.</summary>
        private static bool IsRegionWord(string t) => t.ToLowerInvariant()
            is "us" or "usa" or "uk" or "gb" or "eu" or "euro" or "ez";

        /// <summary>Side-local words that don't count as a side's "shape": if a side has ONLY these,
        /// it inherits the other side's shape tokens ("aud vs usd 5y5y" prices 5y5y on both).</summary>
        private static bool IsSideLocalWord(string t) => t.ToLowerInvariant()
            is "ois" or "irs" or "swap" or "qq" or "ss" or "q/q" or "s/s" or "3s" or "6s" or "1s";

        private bool IsMarketToken(string t) =>
            _registry.TryResolve(t, out _) && (t.Length == 3 || !char.IsDigit(t[0]));

        private bool IsBareCcy(string t) =>
            t.Length == 3 && t.All(char.IsLetter) && _registry.TryResolve(t, out var r)
            && t.Equals(r.Ccy, StringComparison.OrdinalIgnoreCase);

        /// <summary>Detect a cross-market query and split it into two complete single-market sides.
        /// Split points: a separator followed by a market token ("aud vs usd 5y5y", "us cpi 10y v eur
        /// hicp 20y"), two adjacent bare ccy codes ("nok sek 10y"), or a glued ccy pair ("nok/sek 10y").
        /// A side with no shape tokens of its own inherits the other side's.</summary>
        private bool TrySplitCross(string text, out string sideA, out string sideB)
        {
            sideA = sideB = "";
            var toks = DateUtil.Normalize(text)
                .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            // glued "nok/sek" -> "nok", "sek" (never touches "q/q", "3s/6s", dates)
            for (int i = 0; i < toks.Count; i++)
            {
                var parts = toks[i].Split('/');
                if (parts.Length == 2 && IsBareCcy(parts[0]) && IsBareCcy(parts[1])
                    && !parts[0].Equals(parts[1], StringComparison.OrdinalIgnoreCase))
                {
                    toks[i] = parts[0];
                    toks.Insert(i + 1, parts[1]);
                    break;
                }
            }
            if (toks.Count < 2 || !toks.Any(IsMarketToken)) return false;

            int bStart = -1;                       // index of side B's first token
            int aEnd = -1;                         // exclusive end of side A
            for (int i = 1; i < toks.Count; i++)
            {
                if (CrossSeps.Contains(toks[i]) && i + 1 < toks.Count
                    && (IsMarketToken(toks[i + 1])
                        || (IsRegionWord(toks[i + 1]) && i + 2 < toks.Count && IsMarketToken(toks[i + 2]))))
                { aEnd = i; bStart = i + 1; break; }
                if (IsBareCcy(toks[i - 1]) && IsBareCcy(toks[i])
                    && !toks[i - 1].Equals(toks[i], StringComparison.OrdinalIgnoreCase))
                { aEnd = i; bStart = i; break; }
            }
            if (bStart < 0 || aEnd < 1) return false;

            var a = toks.Take(aEnd).ToList();
            var b = toks.Skip(bStart).ToList();
            if (!a.Any(IsMarketToken) || !b.Any(IsMarketToken)) return false;

            // exactly TWO sides: "usd vs gbp vs eur 5y" must refuse loudly — silently dropping a
            // market is how someone books the wrong spread
            for (int i = 1; i < b.Count; i++)
                if (CrossSeps.Contains(b[i]) && i + 1 < b.Count
                    && (IsMarketToken(b[i + 1]) || IsRegionWord(b[i + 1])))
                    throw new FormatException(
                        "cross-market takes exactly two sides — price a third market as its own cross.");

            bool Shapeish(string t) => !IsMarketToken(t) && !IsRegionWord(t) && !IsSideLocalWord(t);
            var restA = a.Where(Shapeish).ToList();
            var restB = b.Where(Shapeish).ToList();
            if (restA.Count == 0 && restB.Count == 0) return false;   // two bare markets, nothing to price
            if (restA.Count == 0) a.AddRange(restB);
            else if (restB.Count == 0) b.AddRange(restA);

            sideA = string.Join(" ", a);
            sideB = string.Join(" ", b);
            return true;
        }

        public ParsedQuery Parse(string text)
        {
            if (TrySplitCross(text, out var aTxt, out var bTxt))
            {
                var qa = ParseSingle(aTxt);
                var qb = ParseSingle(bTxt);
                // a real cross needs two DIFFERENT markets — "aud vs aud 5y" pricing a bare outright
                // as if the query were fine is a silent misread, so refuse instead
                if (!qa.Target.Ccy.Equals(qb.Target.Ccy, StringComparison.OrdinalIgnoreCase)
                    || qa.Target.Kind != qb.Target.Kind
                    || !string.Equals(qa.Target.LadderName, qb.Target.LadderName, StringComparison.OrdinalIgnoreCase))
                {
                    qa.Cross = qb;
                    qa.Raw = text;
                    return qa;
                }
                throw new FormatException(
                    "cross-market needs two DIFFERENT markets — for one market use its own grammar (\"usd 5s10s\", \"usd 5y vs 10y\").");
            }
            return ParseSingle(text);
        }

        private ParsedQuery ParseSingle(string text)
        {
            var q = new ParsedQuery { Raw = text };
            var normalized = DateUtil.Normalize(text);
            var toks = normalized.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            CurveTarget? target = null;
            ProductKind? prodOverride = null;
            bool targetFromAlias = false;
            bool fwdGrid = false;
            bool wings = false;
            bool belly = false;
            bool sawImmWord = false;   // bare "imm" = front IMM
            bool sawVs = false;        // "5y vs 10y" = spread, not a 5y10y forward
            bool pendingRisk = false;  // spaced "dv01 25k"
            bool sawX = false;         // "33m x 50m" list separator — everything is a size
            bool sawFra = false;       // the mandatory "fra" marker
            (int s, int e)? fraMonths = null;  // rolling "3x6", order-independent with the marker
            bool tenorBeforeShape = false; // "1y 5s10s15s": bare tenor ahead of the shape = forward start
            var bareTenors = new List<Period>();
            var legs = new List<Leg>();
            var sizeList = new List<double>();
            var riskList = new List<double>();
            string? riskCcy = null;
            void SetRiskCcy(string c)
            {
                if (riskCcy != null && riskCcy != c)
                    throw new FormatException($"mixed dv01 currencies ({riskCcy} and {c}) — use one.");
                riskCcy = c;
            }
            // ordered size tokens; Months set when "Nm" could be a tenor (N<=36) — resolved at assembly.
            // tenorsBefore = bare tenors seen BEFORE the token: "3m 2y" is a forward start
            // (start-then-tenor, like "1y 5y"), "5y 25m" is a notional (size follows the trade)
            var sizeEntries = new List<(double value, int? months, int tenorsBefore)>();
            List<Period?>? idxOverrides = null;

            // date-leg builder state
            Date? pendingDate = null;
            char pendingSign = '+';
            bool awaitingSignedTenor = false;

            void FlushDateLeg(Period tenor, char sign)
            {
                var d = pendingDate!;
                var leg = new Leg { StartKind = StartKind.Date, Tenor = tenor };
                if (sign == '+') { leg.ExplicitStart = d; }
                else { leg.ExplicitEnd = d; leg.ExplicitStart = d - tenor; }
                legs.Add(leg);
                pendingDate = null;
                awaitingSignedTenor = false;
            }

            // a date with no tenor of its own binds to what came before it:
            // "usd 2s5s 20-jun-29" starts the curve on the date; "usd 5y eff 16sep26" dates the outright
            bool TryResolvePendingDate()
            {
                if (pendingDate == null) return true;
                if (legs.Count >= 2 && legs.All(l => l.StartKind == StartKind.Spot && l.Tenor != null))
                {
                    foreach (var l in legs) { l.StartKind = StartKind.Date; l.ExplicitStart = pendingDate; }
                    pendingDate = null;
                    return true;
                }
                if (legs.Count == 0 && bareTenors.Count == 1)
                {
                    legs.Add(new Leg { StartKind = StartKind.Date, ExplicitStart = pendingDate, Tenor = bareTenors[0] });
                    bareTenors.Clear();
                    pendingDate = null;
                    return true;
                }
                return false;
            }

            foreach (var raw in toks)
            {
                var t = raw.Trim();

                // "3s/6s" per-leg index tags stay index tags
                if (IdxSlashRx.IsMatch(t))
                {
                    idxOverrides ??= new List<Period?>();
                    foreach (var part in t.Split('/'))
                        idxOverrides.Add(new Period(part[0] == '3' ? 3 : 6, TimeUnit.Months));
                    continue;
                }
                // slash-separated shapes: "5s/10s/20s" ≡ "5s10s20s" (also u26-5s/10s, 16sep26-5s/10s)
                if (t.Contains('/')) t = ShapeSlashRx.Replace(t, "");

                // ----- date-leg continuation -----
                if (pendingDate != null)
                {
                    if (NoiseWords.Contains(t) || t.ToLowerInvariant() is "curve" or "fly" or "spread" or "butterfly")
                        continue; // "16sep26 start 5s10s20s" — date stays pending across noise
                    if (t is "+" or "-") { pendingSign = t[0]; awaitingSignedTenor = true; continue; }
                    var mst = SignedTenor.Match(t);
                    if (mst.Success)
                    {
                        FlushDateLeg(TenorUtil.Parse(mst.Groups[2].Value + mst.Groups[3].Value), mst.Groups[1].Value[0]);
                        continue;
                    }
                    var mtc = Tenor.Match(t);
                    if (mtc.Success && !(char.ToLowerInvariant(t[^1]) == 'm' && int.Parse(mtc.Groups[1].Value) > 36))
                    {
                        // "100m" after a date is a notional (millions), not a 100-month tenor
                        FlushDateLeg(TenorUtil.Parse(t), awaitingSignedTenor ? pendingSign : '+');
                        continue;
                    }
                    if (DateUtil.TryParseDate(t, out var d2)) // date .. date = start/end
                    {
                        var start = pendingDate;
                        int days = d2 - start;
                        if (days <= 0) throw new FormatException($"End date {d2} is not after start {start}.");
                        legs.Add(new Leg { StartKind = StartKind.Date, ExplicitStart = start, ExplicitEnd = d2, Tenor = new Period(days, TimeUnit.Days) });
                        pendingDate = null;
                        continue;
                    }
                    // date + curve/fly: "20-jun-29 2s5s" — every leg starts on the date
                    var mds = Fly.Match(t).Success ? Fly.Match(t) : Spread.Match(t);
                    if (mds.Success)
                    {
                        for (int g = 1; g < mds.Groups.Count && mds.Groups[g].Success; g++)
                            legs.Add(new Leg
                            {
                                StartKind = StartKind.Date, ExplicitStart = pendingDate,
                                Tenor = new Period(int.Parse(mds.Groups[g].Value), TimeUnit.Years),
                            });
                        pendingDate = null;
                        continue;
                    }
                    // no tenor followed the date — bind it to earlier legs/tenor and let this
                    // token fall through to normal handling ("usd 2s5s 20-jun-29 $50k roll")
                    if (!TryResolvePendingDate())
                        throw new FormatException($"Date {pendingDate} needs a tenor, e.g. \"{pendingDate:dd-MMM-yy} +5y\" (got '{t}').");
                }

                // lone separator between legs/shapes: "u26 - 5s10s". NOT skipped between bare
                // tenors ("5y - 10y") — that stays an error rather than guessing fwd vs curve.
                if (t is "-" or "+" && bareTenors.Count == 0 && legs.Count > 0) continue;

                if (NoiseWords.Contains(t)) continue;

                // ----- keywords -----
                switch (t.ToLowerInvariant())
                {
                    case "x": case "×": sawX = true; continue; // list separator: "33m x 50m x 20m"
                    case "wing": case "wings": wings = true; continue;
                    case "body": case "belly": belly = true; continue;
                    case "mid": case "px": case "price": q.Focus = Focus.Mid; continue;
                    case "roll": case "rolldown": q.Focus = Focus.Roll; continue;
                    case "carry": q.Focus = Focus.Carry; continue;
                    case "hist": case "history": q.Focus = Focus.History; continue;
                    case "z": case "zscore": case "z-score": q.Focus = Focus.ZScore; continue;
                    case "vol": q.Focus = Focus.Vol; continue;
                    case "bid": case "ask": case "offer": q.Focus = Focus.Mid; continue;
                    case "pay": case "payer": q.PayFixed = true; continue;
                    case "rec": case "receiver": case "receive": q.PayFixed = false; continue;
                    case "fwd": case "forward": case "grid": case "matrix": fwdGrid = true; continue;
                    case "us": case "usa": case "uk": case "gb": case "eu": case "euro":
                    case "ez": case "govt": case "gov":
                    case "curve": case "fly": case "spread": case "butterfly": continue;
                    // product FORCE — "nok ois 5y" must hit the NOWA curve, not the IRS default
                    case "ois": prodOverride = ProductKind.OIS; continue;
                    case "irs": case "swap": prodOverride = ProductKind.IRS; continue;
                    // FRA marker, MANDATORY. Not just to disambiguate AxB: a bare "u26" is already
                    // valid grammar (a tenor-less IMM leg awaiting its tenor), so "sek u26" without
                    // the marker would be ambiguous. Kept an independent keyword — no combined regex —
                    // so the spaced-vs-glued trap that broke "u26 3m" cannot recur here.
                    case "fra": sawFra = true; continue;
                    case "imm": sawImmWord = true; continue;                       // "usd imm 5y" = front IMM
                    case "vs": case "versus": case "against": sawVs = true; continue;
                    case "dv01": case "risk": pendingRisk = true; continue;        // spaced "dv01 25k"
                    case "bgn": case "cmpn": case "cmpl": case "cmpt": case "cbbt":
                        q.Source = t.ToUpperInvariant(); continue;                 // bare source token
                }

                // ----- index / currency -----
                if (_registry.TryResolve(t, out var resolved) && (t.Length == 3 || !char.IsDigit(t[0])))
                {
                    // "nowa nok 5y": a bare ccy token after an index alias of the SAME ccy
                    // confirms the alias — it must not silently downgrade OIS back to the default
                    if (target != null && targetFromAlias && resolved.Ccy == target.Ccy
                        && t.Equals(resolved.Ccy, StringComparison.OrdinalIgnoreCase))
                        continue;
                    target = resolved;
                    // an INDEX alias ("sofr", "nowa", "nibor") names the product explicitly —
                    // a missing curve must then error, not silently fall back
                    targetFromAlias = !t.Equals(resolved.Ccy, StringComparison.OrdinalIgnoreCase)
                                      && resolved.Kind != TargetKind.Ladder;
                    continue;
                }

                var msrc = SourceRx.Match(t);
                if (msrc.Success) { q.Source = msrc.Groups[1].Value.ToUpperInvariant(); continue; }

                var mdv = Dv01Rx.Match(t);
                if (mdv.Success)
                {
                    if (mdv.Groups[1].Success) SetRiskCcy(mdv.Groups[1].Value.ToUpperInvariant());
                    riskList.Add(SizeSuffix(double.Parse(mdv.Groups[2].Value), mdv.Groups[3].Value));
                    continue;
                }
                // ccy-tagged risk: $25k (USD), ¥25k / jpy25k (JPY), €1m ...
                var mdr = CcyRiskRx.Match(t);
                if (mdr.Success)
                {
                    var tag = mdr.Groups[1].Value;
                    string? tagCcy = tag.Length == 1
                        ? RiskSymbols.GetValueOrDefault(tag[0])
                        : tag.Length == 3 && tag.All(char.IsLetter) && !MonthWords.Contains(tag)
                            ? (_registry.TryResolve(tag, out var rc) ? rc.Ccy : tag.ToUpperInvariant())
                            : null;
                    if (tagCcy != null)
                    {
                        SetRiskCcy(tagCcy);
                        riskList.Add(SizeSuffix(double.Parse(mdr.Groups[2].Value), mdr.Groups[3].Value));
                        continue;
                    }
                }

                var mw = WeightsRx.Match(t);
                if (mw.Success)
                {
                    q.Weights = mw.Groups[1].Value.Split('/', ',')
                        .Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToList();
                    continue;
                }

                // AUD-style per-leg index tokens: qq / ss / q/s / q/q / qq/ss/ss  (q=3M, s=6M)
                if (t.Contains('/') && t.Split('/').All(p => Regex.IsMatch(p, "^[qs]{1,2}$", RegexOptions.IgnoreCase)))
                {
                    // slash-separated: ONE tag per part ("q/s" = 3M near, 6M far)
                    idxOverrides ??= new List<Period?>();
                    foreach (var p in t.Split('/'))
                        idxOverrides.Add(new Period(char.ToLowerInvariant(p[0]) == 'q' ? 3 : 6, TimeUnit.Months));
                    continue;
                }
                if (IndexTokRx.IsMatch(t) && t.Length % 2 == 0)
                {
                    idxOverrides ??= new List<Period?>();
                    for (int i = 0; i < t.Length; i += 2)
                        idxOverrides.Add(new Period(char.ToLowerInvariant(t[i]) == 'q' ? 3 : 6, TimeUnit.Months));
                    continue;
                }
                if (t.Equals("3s", StringComparison.OrdinalIgnoreCase) || t.Equals("6s", StringComparison.OrdinalIgnoreCase))
                {
                    idxOverrides ??= new List<Period?>();
                    idxOverrides.Add(new Period(t[0] == '3' ? 3 : 6, TimeUnit.Months));
                    continue;
                }

                // dated ladder contract: jan27 / jul30 / f31 / n32 (BRL DI)
                var mdat = DatedRx.Match(t);
                if (mdat.Success)
                {
                    var mth = mdat.Groups[1].Value.ToLowerInvariant();
                    q.DatedCode = (mth is "f" or "jan" ? "F" : "N") + mdat.Groups[2].Value;
                    continue;
                }

                // ----- shapes -----
                var mfly = Fly.Match(t);
                if (mfly.Success)
                {
                    foreach (var g in new[] { 1, 2, 3 })
                        legs.Add(new Leg { StartKind = StartKind.Spot, Tenor = new Period(int.Parse(mfly.Groups[g].Value), TimeUnit.Years) });
                    continue;
                }
                var msp = Spread.Match(t);
                if (msp.Success)
                {
                    foreach (var g in new[] { 1, 2 })
                        legs.Add(new Leg { StartKind = StartKind.Spot, Tenor = new Period(int.Parse(msp.Groups[g].Value), TimeUnit.Years) });
                    continue;
                }

                // IMM-dated curve/fly glued to the code: z26-5s30s / m31-5s10s20s
                var mish = ImmShape.Match(t);
                if (mish.Success && ImmUtil.TryParse(mish.Groups[1].Value, out var immSh))
                {
                    var code = mish.Groups[1].Value.ToUpperInvariant();
                    foreach (var g in new[] { 2, 3, 4 })
                    {
                        if (!mish.Groups[g].Success) continue;
                        legs.Add(new Leg
                        {
                            StartKind = StartKind.Imm, ImmDate = immSh, ImmCode = code,
                            Tenor = new Period(int.Parse(mish.Groups[g].Value), TimeUnit.Years),
                        });
                    }
                    continue;
                }

                var mit = ImmTenor.Match(t);
                if (mit.Success && ImmUtil.TryParse(mit.Groups[1].Value, out var immD))
                {
                    legs.Add(new Leg
                    {
                        StartKind = StartKind.Imm, ImmDate = immD,
                        ImmCode = mit.Groups[1].Value.ToUpperInvariant(),
                        Tenor = TenorUtil.Parse(mit.Groups[2].Value),
                    });
                    continue;
                }
                if (ImmUtil.TryParse(t, out var immOnly))
                {
                    legs.Add(new Leg { StartKind = StartKind.Imm, ImmDate = immOnly, ImmCode = t.ToUpperInvariant().Replace("IMM", "") });
                    continue;
                }
                // glued signed tenor completing an IMM leg: "u26 +5y" (sign irrelevant — starts on the IMM)
                var mist = SignedTenor.Match(t);
                if (mist.Success && legs.Any(l => l.StartKind == StartKind.Imm && l.Tenor == null))
                {
                    bareTenors.Add(TenorUtil.Parse(mist.Groups[2].Value + mist.Groups[3].Value));
                    continue;
                }

                var mfwd = Fwd.Match(t);
                if (mfwd.Success)
                {
                    legs.Add(new Leg
                    {
                        StartKind = StartKind.Forward,
                        ForwardStart = TenorUtil.Parse(mfwd.Groups[1].Value + mfwd.Groups[2].Value),
                        Tenor = TenorUtil.Parse(mfwd.Groups[3].Value + mfwd.Groups[4].Value),
                    });
                    continue;
                }

                // ----- dates -----
                if (DateUtil.TryParseDate(t, out var dt)) { pendingDate = dt; pendingSign = '+'; continue; }
                var mg = GluedDateTenor.Match(t);
                if (mg.Success && DateUtil.TryParseDate(mg.Groups[1].Value, out var dg))
                {
                    pendingDate = dg;
                    // GLUED "15sep27-1y" reads like the IMM form (z27-1y): the hyphen is a
                    // separator and the swap STARTS on the date. End-anchoring needs the spaced
                    // form ("15sep27 -1y" or "15sep27 - 1y").
                    FlushDateLeg(TenorUtil.Parse(mg.Groups[3].Value),
                        mg.Groups[2].Value[0] == '-' ? '+' : mg.Groups[2].Value[0]);
                    continue;
                }
                // glued date+shape: "16sep26-2s10s" / "16sep26-5s10s20s" — legs start on the date
                var mgs = GluedDateShape.Match(t);
                if (mgs.Success && DateUtil.TryParseDate(mgs.Groups[1].Value, out var dgs))
                {
                    for (int g = 2; g <= 4; g++)
                    {
                        if (!mgs.Groups[g].Success) continue;
                        legs.Add(new Leg
                        {
                            StartKind = StartKind.Date, ExplicitStart = dgs,
                            Tenor = new Period(int.Parse(mgs.Groups[g].Value), TimeUnit.Years),
                        });
                    }
                    continue;
                }

                // rolling FRA months "3x6" — an independent token, so "aud 3x6 fra" and "aud fra 3x6"
                // both work and neither depends on a glued form
                var mab = FraAxB.Match(t);
                if (mab.Success)
                {
                    int fs = int.Parse(mab.Groups[1].Value), fe = int.Parse(mab.Groups[2].Value);
                    if (fe <= fs)
                        throw new FormatException($"FRA '{t}': the end month must be after the start, e.g. 3x6.");
                    fraMonths = (fs, fe);
                    continue;
                }

                // ----- bare tenor / notional / rate -----
                // ccy-SUFFIX risk: "25keur" / "25kEUR" = EUR dv01 (prefix forms handled by CcyRiskRx)
                var msf = RiskSufRx.Match(t);
                if (msf.Success && !MonthWords.Contains(msf.Groups[3].Value))
                {
                    SetRiskCcy(msf.Groups[3].Value.ToUpperInvariant());
                    riskList.Add(SizeSuffix(double.Parse(msf.Groups[1].Value), msf.Groups[2].Value));
                    continue;
                }

                var mn = Notional.Match(t);
                if (mn.Success)
                {
                    double v = double.Parse(mn.Groups[1].Value);
                    var suf = mn.Groups[2].Value.ToLowerInvariant();
                    if (pendingRisk) { riskList.Add(SizeSuffix(v, suf)); pendingRisk = false; continue; }
                    // bare "25k" = USD dv01 by desk convention (k-amounts are risk, m/b are notionals)
                    if (suf == "k") { SetRiskCcy("USD"); riskList.Add(SizeSuffix(v, suf)); continue; }
                    bool ambiguous = suf == "m" && v <= 36 && v == Math.Floor(v);
                    // an IMM leg still waiting for its tenor claims an ambiguous "Nm" as MONTHS:
                    // spaced "u26 3m" must read exactly like the glued "u26-3m" (which ImmTenor
                    // matches). Same idea as the date-leg continuation above, which already lets a
                    // bare tenor complete a pending date. Only when NO tenor has been seen yet —
                    // in "u26 5y 20m" the 5y already completes the leg, so 20m stays a notional.
                    if (ambiguous && !sawX && bareTenors.Count == 0
                        && legs.Any(l => l.StartKind == StartKind.Imm && l.Tenor == null))
                    {
                        bareTenors.Add(new Period((int)v, TimeUnit.Months));
                        continue;
                    }
                    sizeEntries.Add((SizeSuffix(v, suf), ambiguous ? (int)v : null, bareTenors.Count));
                    continue;
                }
                // "1yf" / "6mf" / "1yfwd": glued forward-start marker — same as "1y fwd"
                var mtf = TenorFwd.Match(t);
                if (mtf.Success)
                {
                    bareTenors.Insert(0, TenorUtil.Parse(mtf.Groups[1].Value + mtf.Groups[2].Value));
                    fwdGrid = true;
                    continue;
                }
                var mt = Tenor.Match(t);
                if (mt.Success)
                {
                    if (legs.Count == 0) tenorBeforeShape = true;
                    bareTenors.Add(TenorUtil.Parse(t));
                    continue;
                }

                var mr = Rate.Match(t);
                if (mr.Success && (t.StartsWith("@") || t.EndsWith("%") || t.Contains('.')))
                {
                    q.FixedRate = double.Parse(mr.Groups[1].Value) / 100.0;
                    continue;
                }

                // "dv01 25" (no suffix): the number is the size, not a 2y5y digit-pair leg
                if (pendingRisk && DigitPairRx.IsMatch(t))
                {
                    riskList.Add(double.Parse(t));
                    pendingRisk = false;
                    continue;
                }

                // bare digit-pair legs: "52" = 5y2y, "102" = 10y2y, "1010" = 10y10y
                if (DigitPairRx.IsMatch(t) && SplitDigitPair(t) is (int da, int db))
                {
                    legs.Add(new Leg
                    {
                        StartKind = StartKind.Forward,
                        ForwardStart = new Period(da, TimeUnit.Years),
                        Tenor = new Period(db, TimeUnit.Years),
                    });
                    continue;
                }

                throw new FormatException($"Cannot parse '{t}'. Examples: usd 5y · 52 73 102 cad · aud 25-jun-31 +5y · usd 2s10s w:1/1.5 · ¥25k wings");
            }

            if (!TryResolvePendingDate())
                throw new FormatException($"Date {pendingDate} needs a tenor: \"{pendingDate:dd-MMM-yy} +5y\", \"-10y\", or a second date.");
            if (target == null)
                throw new FormatException("No currency/index recognised (usd, sofr, estr, cpi, cad...).");
            q.Target = target;
            if (targetFromAlias) q.ProductExplicit = true;
            if (prodOverride is { } po && !q.Target.IsLadder)
            {
                q.Target = new CurveTarget(q.Target.Ccy,
                    po == ProductKind.OIS ? TargetKind.PrimaryOis : TargetKind.PrimaryIrs, null, po);
                q.ProductExplicit = true;
            }

            if (pendingRisk)
                throw new FormatException("dv01 needs a size, e.g. \"dv01 25k\".");

            // "20m"-style tokens: months when nothing else provides a tenor, else millions
            if (sizeEntries.Count > 0)
            {
                bool noLegs = legs.Count == 0 && bareTenors.Count == 0 && q.DatedCode == null && !sawX;
                if (noLegs && sizeEntries.Count(e => e.months.HasValue) >= 2)
                    throw new FormatException(
                        "ambiguous months vs millions — write tenors in months (18m) and sizes with 'mm' or a ccy tag (20mm, $20m).");
                int frontIns = 0;
                foreach (var (value, months, tenorsBefore) in sizeEntries)
                {
                    if (noLegs && months.HasValue)
                        bareTenors.Add(new Period(months.Value, TimeUnit.Months));
                    else if (months.HasValue && legs.Count == 0 && !sawX
                             && tenorsBefore == 0 && bareTenors.Count > 0)
                        // "3m 2y": months BEFORE the tenor = forward start, ordered first
                        bareTenors.Insert(frontIns++, new Period(months.Value, TimeUnit.Months));
                    else sizeList.Add(value);
                }
            }

            if (fwdGrid && legs.Count == 0 && bareTenors.Count == 0
                && sizeList.Count == 0 && riskList.Count == 0 && idxOverrides == null)
            { q.Shape = QueryShape.ForwardGrid; return q; }

            // "usd 1y fwd 2s10s" / "usd 1y 5s10s15s" / "usd 1yf 5s10s15s": one bare tenor ahead of a
            // spot curve/fly (or with the fwd marker) = every leg starts that far forward
            if ((fwdGrid || tenorBeforeShape) && bareTenors.Count == 1 && legs.Count >= 2
                && legs.All(l => l.StartKind == StartKind.Spot && l.Tenor != null))
            {
                foreach (var l in legs) { l.StartKind = StartKind.Forward; l.ForwardStart = bareTenors[0]; }
                bareTenors.Clear();
                fwdGrid = false;
            }

            // fold bare tenors into legs
            if (bareTenors.Count > 0)
            {
                var immsNoTenor = legs.Where(l => l.StartKind == StartKind.Imm && l.Tenor == null).ToList();
                if (immsNoTenor.Count > 0 && bareTenors.Count == immsNoTenor.Count)
                {
                    for (int i = 0; i < immsNoTenor.Count; i++) immsNoTenor[i].Tenor = bareTenors[i];
                }
                else if (immsNoTenor.Count > 1 && bareTenors.Count == 1)
                {
                    // "u26 z26 5y" = the 5y IMM roll — one tenor broadcast to every IMM leg
                    foreach (var l in immsNoTenor) l.Tenor = bareTenors[0];
                }
                else if (legs.Count == 0 && bareTenors.Count == 1)
                {
                    legs.Add(new Leg { StartKind = StartKind.Spot, Tenor = bareTenors[0] });
                }
                else if (legs.Count == 0 && bareTenors.Count == 2)
                {
                    if (sawVs)
                        // "5y vs 10y" = the curve, never a 5y10y forward
                        foreach (var bt in bareTenors)
                            legs.Add(new Leg { StartKind = StartKind.Spot, Tenor = bt });
                    else
                        // "1y 5y" = forward start (desk shorthand; use 2s10s for a curve)
                        legs.Add(new Leg { StartKind = StartKind.Forward, ForwardStart = bareTenors[0], Tenor = bareTenors[1] });
                }
                else if (legs.Count > 0)
                {
                    foreach (var bt in bareTenors)
                        legs.Add(new Leg { StartKind = StartKind.Spot, Tenor = bt });
                }
                else throw new FormatException("Too many bare tenors — use NsMs for curves or NyMy legs for forwards.");
            }

            // "usd 1y fwd" used to DROP the marker and silently price spot 1y. If the fwd/grid marker
            // survived to here with only SPOT legs, nothing consumed it — refuse rather than misprice.
            if (fwdGrid && legs.Count > 0 && legs.All(l => l.StartKind == StartKind.Spot))
                throw new FormatException(
                    "\"fwd\" didn't attach to anything — use \"usd fwd\" for the grid, \"1yf 5y\" (or \"1y 5y\") for a forward start, or \"1y1y\".");

            // a rolling FRA has no leg yet — build it BEFORE the bare-currency grid fallback below,
            // or "aud 3x6 fra" silently renders the forward grid instead of the FRA
            if (fraMonths is { } rollFm)
            {
                if (!sawFra)
                    throw new FormatException(
                        "FRAs need the 'fra' marker, e.g. \"aud 3x6 fra\" — without it \"3x6\" has no meaning here.");
                if (legs.Count > 0 || bareTenors.Count > 0)
                    throw new FormatException("Price a rolling FRA on its own, e.g. \"aud 3x6 fra\".");
                legs.Add(new Leg { IsFra = true, FraStartMonths = rollFm.s, FraEndMonths = rollFm.e });
            }

            if (legs.Count == 0 && q.DatedCode == null)
            {
                // sizing or index tags with no trade must ERROR, not silently render the grid
                if (sizeList.Count > 0 || riskList.Count > 0 || idxOverrides != null)
                    throw new FormatException("sizing/index tags but no trade — give a tenor or structure (usd 5y, usd 2s10s).");
                if (sawFra)
                    throw new FormatException(
                        "FRA needs rolling months (\"aud 3x6 fra\") or an IMM contract (\"sek u26 fra\").");
                q.Shape = QueryShape.ForwardGrid; // bare currency = forward grid
                return q;
            }

            // "u26 5s10s": a lone IMM date next to a curve/fly starts every leg on that IMM
            var immSolo = legs.FirstOrDefault(l => l.StartKind == StartKind.Imm && l.Tenor == null);
            if (immSolo != null && legs.Count >= 3
                && legs.Where(l => l != immSolo).All(l => l.StartKind == StartKind.Spot && l.Tenor != null))
            {
                foreach (var l in legs)
                {
                    if (l == immSolo) continue;
                    l.StartKind = StartKind.Imm;
                    l.ImmDate = immSolo.ImmDate;
                    l.ImmCode = immSolo.ImmCode;
                }
                legs.Remove(immSolo);
            }

            // bare "imm" = front IMM: "usd imm 5y" / "jpy imm 2s10s" start on the next quarterly IMM
            if (sawImmWord && !legs.Any(l => l.StartKind == StartKind.Imm))
            {
                var today = new Date(DateTime.Today.Day, (Month)DateTime.Today.Month, DateTime.Today.Year);
                var front = NextImm(today);
                var code = ImmUtil.CodeFor(front)!;
                foreach (var l in legs.Where(l => l.StartKind == StartKind.Spot))
                {
                    l.StartKind = StartKind.Imm;
                    l.ImmDate = front;
                    l.ImmCode = code;
                }
            }

            // ----- FRA assembly, part 2: the IMM-dated form -----
            if (sawFra)
            {
                if (legs.Count == 1 && legs[0].IsFra)
                {
                    // rolling, already built above
                }
                else if (legs.Count == 1 && legs[0].StartKind == StartKind.Imm && legs[0].Tenor == null)
                {
                    // "sek u26 fra": the IMM leg keeps its date and takes its length from the index
                    legs[0].IsFra = true;
                }
                else
                    throw new FormatException(
                        "FRA needs rolling months (\"aud 3x6 fra\") or an IMM contract (\"sek u26 fra\").");
                // set the target directly: the prodOverride block above has already run
                q.Target = new CurveTarget(q.Target.Ccy, TargetKind.PrimaryIrs, null, ProductKind.FRA);
                q.ProductExplicit = true;
            }

            // FRA legs are exempt: a rolling FRA carries AxB months and an IMM FRA takes its length
            // from the float index, so neither has (or needs) a Tenor
            if (legs.Any(l => !l.IsFra && l.Tenor == null && l.ExplicitEnd == null))
                throw new FormatException("Every leg needs a tenor (e.g. m31-5y).");
            if (legs.Count > 3)
                throw new FormatException($"{legs.Count} legs — max is 3 (fly).");

            foreach (var l in legs) q.Legs.Add(l);
            q.Shape = legs.Count switch { 0 or 1 => QueryShape.Outright, 2 => QueryShape.Spread, _ => QueryShape.Fly };

            // ----- distribute sizing lists -----
            int n = Math.Max(legs.Count, 1);
            if (sizeList.Count == 1 && wings && n == 3)
                // "50k wings" with plain notional: 1/2/1 fly sizing
                q.LegNotionals = new List<double> { sizeList[0], sizeList[0] * 2, sizeList[0] };
            else if (sizeList.Count == 1) q.Notional = sizeList[0];
            else if (sizeList.Count == n && n > 1) q.LegNotionals = sizeList;
            else if (sizeList.Count > 1)
                throw new FormatException($"{sizeList.Count} notionals for {n} legs — give one, or one per leg.");

            if (riskList.Count == 1)
            {
                q.Dv01Target = riskList[0];
                // fly dv01 defaults to WING risk; "belly"/"body" targets the body instead
                q.WingsSizing = n == 3 && !belly;
                q.BellySizing = n == 3 && belly;
            }
            else if (riskList.Count == n && n > 1) q.LegDv01s = riskList;
            else if (riskList.Count > 1)
                throw new FormatException($"{riskList.Count} dv01s for {n} legs — give one (optionally 'wings'), or one per leg.");
            if (q.LegNotionals != null && (q.LegDv01s != null || q.Dv01Target != null))
                throw new FormatException("Give notionals OR dv01s, not both.");

            if (idxOverrides != null)
            {
                // "s/s"/"q/q" on an outright is convention notation for one swap — one tag
                if (n == 1 && idxOverrides.Count == 2
                    && idxOverrides[0]?.length() == idxOverrides[1]?.length()
                    && idxOverrides[0]?.units() == idxOverrides[1]?.units())
                    idxOverrides.RemoveAt(1);
                if (idxOverrides.Count == 1 && n > 1)
                    while (idxOverrides.Count < n) idxOverrides.Add(idxOverrides[0]);
                if (idxOverrides.Count != n)
                    throw new FormatException($"{idxOverrides.Count} index tags for {n} legs — e.g. qq/ss/ss.");
                q.IndexOverrides = idxOverrides;
            }

            if (riskCcy != null) q.Dv01Ccy = riskCcy;
            // unsized queries default to $25k dv01 ($25k wings on flies). Ladders and dated contracts
            // are included: every ladder family now has a real dv01 behind it (discounted ZC inflation
            // swap, closed-form BUS/252 DI swap, bootstrapped par-swap annuity), so there is a genuine
            // density to size off — they used to be excluded because there wasn't.
            if ((legs.Count > 0 || q.Target.IsLadder || q.DatedCode != null)
                && q.Dv01Target == null && q.LegDv01s == null && q.LegNotionals == null && sizeList.Count == 0)
            {
                q.Dv01Target = Risk.RiskSizer.DefaultDv01Usd;
                q.Dv01Ccy = "USD";
                q.WingsSizing = legs.Count == 3;
            }
            if (q.Weights != null && q.Weights.Count != n)
                throw new FormatException($"{q.Weights.Count} weights for {n} legs — e.g. w:1/2/1.");
            return q;
        }
    }
}
