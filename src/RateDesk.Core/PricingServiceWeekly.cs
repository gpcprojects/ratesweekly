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
        public DateTime Date { get; init; }
        public double MidPct { get; init; }
        public double? PricedBp { get; init; }
        public double? StepBp { get; init; }
        public double? W1Bp { get; set; }
        public double? M1Bp { get; set; }
    }

    public sealed class WeeklyRun
    {
        public string Title { get; init; } = "";   // "FOMC · USD" — no terminal flag tokens
        public string RefName { get; init; } = "";
        public double? RefPct { get; init; }
        public List<WeeklyMeeting> Rows { get; } = new();
    }

    public sealed class WeeklyReport
    {
        public DateTime AsOf { get; init; } = DateTime.Now;
        public List<WeeklySection> Sections { get; } = new();
        public List<WeeklyRun> Runs { get; } = new();
        public List<string> Notes { get; } = new();
    }

    public sealed partial class PricingService
    {
        /// <summary>The weekly email's currency universe — the desk's grouping, fixed by spec.
        /// A currency with no config (or disabled) drops out with a note rather than a crash.</summary>
        private static readonly (string title, string[] ccys)[] WeeklyGroups =
        {
            ("DM", new[] { "USD", "EUR", "GBP", "JPY", "CAD", "SEK", "NOK", "DKK", "CHF", "AUD", "NZD" }),
            ("EM", new[] { "HUF", "CZK", "PLN", "ZAR", "ILS" }),
            ("LATAM", new[] { "COP", "CLP", "MXN", "BRL" }),
            ("ASIA EM", new[] { "TWD", "THB", "MYR", "INR", "CNY", "HKD", "SGD", "KRW" }),
        };

        /// <summary>Tenors FETCHED: 1Y and 20Y are needed to derive the 1y1y forward and keep
        /// MonitorFor's fwd list populated — only DisplayTenors become columns.</summary>
        private static readonly string[] WeeklyTenors = { "1Y", "2Y", "5Y", "10Y", "20Y", "30Y" };
        private static readonly string[] DisplayTenors = { "2Y", "5Y", "10Y", "30Y" };
        private static readonly (string a, string b)[] WeeklySpreads = { ("2Y", "10Y"), ("5Y", "30Y") };

        /// <summary>Build the WEEKLY report: the monitor's own numbers (same MonitorFor path — same
        /// mids, same pillar-history changes) at 1w and 1m horizons, plus every central-bank run with
        /// roll-safe 1w/1m changes from the stitched meeting series. Blank cells are DELIBERATE:
        /// a currency without a quoted 30Y pillar publishes nothing there rather than an
        /// extrapolation — we would rather leave it blank than publish a number that is wrong.</summary>
        public WeeklyReport BuildWeekly(int meetingsPerRun = 8)
        {
            var rep = new WeeklyReport { AsOf = DateTime.Now };

            foreach (var (title, ccys) in WeeklyGroups)
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
                        var now = MonitorFor(ccy, WeeklyTenors, WeeklySpreads, 1);
                        var w1 = MonitorFor(ccy, WeeklyTenors, WeeklySpreads, 7);
                        var m1 = MonitorFor(ccy, WeeklyTenors, WeeklySpreads, 31);

                        var col = new WeeklyCcy { Ccy = ccy.ToUpperInvariant() };
                        void Add(string label, Func<MonitorColumn, MonitorCell?> pick, bool spread = false)
                        {
                            var c0 = pick(now);
                            var cell = new WeeklyCell { Label = label, IsSpread = spread, Mid = c0?.MidPct };
                            if (c0?.MidPct != null)
                            {
                                cell.W1Bp = pick(w1)?.CoDBp;
                                cell.M1Bp = pick(m1)?.CoDBp;
                            }
                            col.Cells.Add(cell);
                        }
                        foreach (var t in DisplayTenors)
                            Add(t.ToLowerInvariant(), c => c.Tenors.FirstOrDefault(x => x.Label == t));
                        Add("1y1y", c => c.Fwds.FirstOrDefault(x => x.Label == "1y1y"));
                        Add("5y5y", c => c.Fwds.FirstOrDefault(x => x.Label == "5y5y"));
                        Add("2s10s", c => c.Spreads.FirstOrDefault(x => x.Label == "2s10s"), spread: true);
                        Add("5s30s", c => c.Spreads.FirstOrDefault(x => x.Label == "5s30s"), spread: true);
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
                    var series = MeetingSeriesBuilder(sched, run.Rows.Select(r => r.Date));
                    foreach (var row in run.Rows)
                    {
                        var wm = new WeeklyMeeting { Date = row.Date, MidPct = row.MidPct, PricedBp = row.PricedBp, StepBp = row.StepBp };
                        try
                        {
                            // the stitched series is meeting-CONSTANT across ticker rolls, so a 1w
                            // change straddling a decision compares the same meeting on both sides
                            var s = series(row.Date);
                            wm.W1Bp = ChangeToBp(s, row.MidPct, 7);
                            wm.M1Bp = ChangeToBp(s, row.MidPct, 31);
                        }
                        catch { /* changes are best-effort per meeting */ }
                        wr.Rows.Add(wm);
                    }
                    rep.Runs.Add(wr);
                }
                catch (Exception ex) { rep.Notes.Add($"{sched.Name}: {ex.Message}"); }
            }
            return rep;
        }

        /// <summary>Live mid vs the stitched series' close at/before N calendar days back, in bp.</summary>
        private static double? ChangeToBp(IReadOnlyList<HistPoint> s, double liveMid, int daysBack)
        {
            if (s.Count == 0) return null;
            var target = DateTime.Today.AddDays(-daysBack);
            for (int i = s.Count - 1; i >= 0; i--)
                if (s[i].Date <= target)
                    return (liveMid - s[i].Value) * 100.0;
            return null;
        }
    }
}
