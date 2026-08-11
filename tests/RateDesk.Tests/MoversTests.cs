using RateDesk.Core.Market;
using RateDesk.Weekly.Core;
using RateDesk.Weekly.Core.Render;
using RateDesk.Weekly.Core.Series;

namespace RateDesk.Tests
{
    /// <summary>The movers scan: the data gates (thin / stale / vol-floored series must not
    /// rank), the estimated-vs-true weekly σ handover, the RMS week-vol ratio, roll-corrected
    /// meeting series, and the DM/EM ranking + hero-diversity rules end to end.</summary>
    public class MoversTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rw-mv-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static readonly DateTime AsOf = new(2026, 8, 4);   // a Tuesday

        /// <summary>Weekday series ending at AsOf: <paramref name="quiet"/> alternating ±0.5bp
        /// days, then <paramref name="burst"/> days of +burstBp/day. Values in %.</summary>
        private static List<HistPoint> Series(int quiet, int burst, double burstBp, double start = 4.0)
        {
            var days = new List<DateTime>();
            var d = AsOf;
            while (days.Count < quiet + burst)
            {
                if (d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday) days.Add(d);
                d = d.AddDays(-1);
            }
            days.Reverse();

            var pts = new List<HistPoint>();
            double v = start;
            for (int i = 0; i < days.Count; i++)
            {
                if (i > 0)
                    v += i >= days.Count - burst ? burstBp / 100.0
                       : (i % 2 == 0 ? 0.005 : -0.005);
                pts.Add(new HistPoint(days[i], v));
            }
            return pts;
        }

        // ---------------- Stats gates ----------------

        [Fact]
        public void Stats_TooFewObservations_IsNull()
            => Assert.Null(MoverScan.Stats(Series(15, 0, 0), 100.0, AsOf));

        [Fact]
        public void Stats_StaleLastClose_IsNull()
        {
            var s = Series(60, 0, 0).Where(p => p.Date <= AsOf.AddDays(-6)).ToList();
            Assert.Null(MoverScan.Stats(s, 100.0, AsOf));
        }

        [Fact]
        public void Stats_FlatSeries_IsVolFloored()
        {
            var s = Series(60, 0, 0).Select(p => new HistPoint(p.Date, 4.0)).ToList();
            Assert.Null(MoverScan.Stats(s, 100.0, AsOf));
        }

        [Fact]
        public void Stats_ShallowStore_UsesEstimatedSigma_AndFlagsIt()
        {
            var st = MoverScan.Stats(Series(55, 5, 4.0), 100.0, AsOf);
            Assert.NotNull(st);
            Assert.True(st!.ZIsEst);
            Assert.Null(st.SigmaWeeklyBp);
            Assert.InRange(st.W1Bp, 19.0, 21.0);      // five +4bp days inside the 7-calendar-day window
            Assert.True(st.Z > 3.0, $"z was {st.Z:0.00}");
        }

        [Fact]
        public void Stats_SteadyOneDirectionWeek_StillReadsAsHighWeekVol()
        {
            // +4bp EVERY day: dispersion around the week's own mean is ~0 — the RMS definition is
            // what keeps this, the definitive outsized week, from printing "0× vol"
            var st = MoverScan.Stats(Series(55, 5, 4.0), 100.0, AsOf);
            Assert.NotNull(st!.VolRatio);
            Assert.True(st.VolRatio!.Value > 3.0, $"vol ratio was {st.VolRatio:0.00}");
        }

        [Fact]
        public void Stats_DeepSeries_SwitchesToTrueWeeklySigma()
        {
            var st = MoverScan.Stats(Series(300, 0, 0), 100.0, AsOf);
            Assert.NotNull(st);
            Assert.NotNull(st!.SigmaWeeklyBp);
            Assert.False(st.ZIsEst);
        }

        [Fact]
        public void Stats_OneBadPrint_IsDespikedBeforeTheSigma()
        {
            var s = Series(60, 0, 0);
            int mid = s.Count / 2;
            s[mid] = new HistPoint(s[mid].Date, s[mid].Value + 0.50);   // +50bp single-day spike
            var st = MoverScan.Stats(s, 100.0, AsOf);
            Assert.NotNull(st);
            // with the spike in, σ_est ≈ √5 × ~9bp; despiked it stays at the ~±0.5bp base
            Assert.True(st!.SigmaEstBp < 3.0, $"σ_est was {st.SigmaEstBp:0.00}bp — spike survived");
        }

        // ---------------- roll-corrected meeting series ----------------

        [Fact]
        public void MeetingSeries_ReadsTheTickerThatPointedAtTheContract_AndSkipsBoundaryDay()
        {
            using var store = new HistoryStore(Path.Combine(_dir, "m.db"));
            var boundary = new DateTime(2026, 7, 24);        // a Friday inside the window
            var contract = new DateTime(2026, 9, 16);
            string Tk(int n) => $"MM{n} Curncy";

            for (var d = AsOf.AddDays(-40); d <= AsOf; d = d.AddDays(1))
            {
                if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                if (d < boundary)
                {
                    store.UpsertDaily(Tk(2), new[] { new HistPoint(d, 3.40) }, excludeToday: false);
                    store.UpsertDaily(Tk(1), new[] { new HistPoint(d, 3.00) }, excludeToday: false); // old contract
                }
                else
                {
                    store.UpsertDaily(Tk(1), new[] { new HistPoint(d, 3.41) }, excludeToday: false);
                    store.UpsertDaily(Tk(2), new[] { new HistPoint(d, 9.90) }, excludeToday: false); // next contract
                }
            }

            var s = MoverScan.MeetingSeries(store, new[] { boundary, contract }, Tk, contract, AsOf, 40);

            Assert.NotEmpty(s);
            Assert.DoesNotContain(s, p => p.Date.Date == boundary);          // boundary day excluded
            Assert.DoesNotContain(s, p => Math.Abs(p.Value - 3.00) < 1e-9);  // never the old contract
            Assert.DoesNotContain(s, p => Math.Abs(p.Value - 9.90) < 1e-9);  // never the next one
            for (int i = 1; i < s.Count; i++)                                // no phantom roll step
                Assert.True(Math.Abs(s[i].Value - s[i - 1].Value) < 0.05);
        }

        // ---------------- Scan + page, end to end ----------------

        private HistoryStore ScanFixture(out RateDesk.Core.Config.ConfigStore configs)
        {
            var cfgDir = Path.Combine(_dir, "cfg");
            Directory.CreateDirectory(cfgDir);
            File.WriteAllText(Path.Combine(cfgDir, "usd.json"),
                System.Text.Json.JsonSerializer.Serialize(TestConfigs.Usd()));
            File.WriteAllText(Path.Combine(cfgDir, "mxn.json"),
                System.Text.Json.JsonSerializer.Serialize(TestConfigs.Mxn()));
            configs = RateDesk.Core.Config.ConfigStore.LoadFromDirectory(cfgDir);

            var store = new HistoryStore(Path.Combine(_dir, "s.db"));
            foreach (var cfg in configs.Enabled)
            {
                bool em = cfg.Ccy.Equals("MXN", StringComparison.OrdinalIgnoreCase);
                foreach (var (years, ticker, _) in WeeklyCurves.NaturalPillarLadder(cfg, ""))
                {
                    bool isTen = Math.Abs(years - 10) < 0.15;
                    // USD 10Y is the DM story (+4bp/day); MXN 10Y the EM one (+2bp/day);
                    // every other pillar stays quiet
                    var pts = Series(55, isTen ? 5 : 0, isTen ? (em ? 2.0 : 4.0) : 0);
                    store.UpsertDaily(ticker, pts, excludeToday: false);
                }
            }
            return store;
        }

        [Fact]
        public void Scan_RanksTheBurstInstrumentsFirst_AndSplitsDmFromEm()
        {
            using var store = ScanFixture(out var configs);
            var mv = MoverScan.Scan(configs, _ => "", store, AsOf);

            Assert.NotEmpty(mv.DmRanked);
            Assert.NotEmpty(mv.EmRanked);
            Assert.All(mv.DmRanked, m => Assert.Equal("USD", m.Ccy));
            Assert.All(mv.EmRanked, m => Assert.Equal("MXN", m.Ccy));

            // the 10Y outright and the 2s10s it drags are the two DM stories; both must rank
            // ahead of the quiet pillars, in either order
            var top2 = mv.DmRanked.Take(2).Select(m => m.Label).ToList();
            Assert.Contains("USD 10Y", top2);
            Assert.Contains("USD 2s10s", top2);
            Assert.Equal("MXN", mv.EmRanked[0].Ccy);

            var tenY = mv.DmRanked.First(m => m.Label == "USD 10Y");
            Assert.InRange(tenY.W1Bp, 19.0, 21.0);
            Assert.True(tenY.ZIsEst);

            // a slope of two quiet pillars is flat — the vol floor must have excluded it
            Assert.DoesNotContain(mv.DmRanked, m => m.Label == "USD 5s30s");
        }

        [Fact]
        public void Scan_HeroDiversity_CapsTwoPerCurrency()
        {
            using var store = ScanFixture(out var configs);
            var mv = MoverScan.Scan(configs, _ => "", store, AsOf);

            // only USD qualifies in DM, so the two-per-ccy cap must stop the cards at two
            Assert.Equal(2, mv.DmHeroes.Count);
            Assert.Equal(2, mv.DmHeroes.Select(h => h.Kind).Distinct().Count());
        }

        [Fact]
        public void MoversPage_RendersSections_LinksAndHeadline()
        {
            using var store = ScanFixture(out var configs);
            var mv = MoverScan.Scan(configs, _ => "", store, AsOf);

            var html = MoversPage.Build(mv);
            Assert.Contains("DM — outsized movers", html);
            Assert.Contains("EM — outsized movers", html);
            Assert.Contains("href=\"usd.html\"", html);
            Assert.Contains("href=\"mxn.html\"", html);
            Assert.Contains("<div class=\"rw-panels\">", html); // shell still wraps standalone pages
            // the desk's no-blurb rule (2026-08-11): no week line, no methodology paragraph, no
            // gate counts, no pending-feature panel — sections only
            Assert.DoesNotContain("Week to", html);
            Assert.DoesNotContain("Ranked by |z|", html);
            Assert.DoesNotContain("excluded by the data gates", html);
            Assert.DoesNotContain("Beta-conditional flags", html);

            var json = MoverScan.ToJson(mv);
            Assert.Contains("\"headline\"", json);
            Assert.Contains("DM:", json);
            Assert.Contains("USD", json);
        }

        [Fact]
        public void SiteFile_CarriesEveryPageBehindHashNav_AndTheRouter()
        {
            using var store = ScanFixture(out var configs);
            var mv = MoverScan.Scan(configs, _ => "", store, AsOf);

            var html = SiteFile.Build(configs, _ => "", store, AsOf, mv);

            Assert.Contains("id=\"pg-movers\"", html);
            Assert.Contains("id=\"pg-usd\" hidden", html);      // only the hub starts visible
            Assert.Contains("id=\"pg-mxn\" hidden", html);
            Assert.Contains("href=\"#usd\"", html);             // nav switched to hash anchors
            Assert.Contains("href=\"#movers\"", html);
            Assert.Contains("hashchange", html);                // the router is aboard
            Assert.DoesNotContain("href=\"usd.html\"", html);   // no file links inside the pack
        }
    }
}
