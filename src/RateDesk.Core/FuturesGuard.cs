namespace RateDesk.Core
{
    /// <summary>Independent cross-check of the meeting boards against EXCHANGE-SETTLED futures —
    /// the in-app generalisation of the Fed Funds check the audit tool has always run (desk
    /// 2026-08-20). The futures share nothing with the meeting-OIS machinery (no rolling generics,
    /// no stitcher, no calendars), settle on the SAME overnight index, and their delivery windows
    /// are day-weighted blends of the meeting periods — so a mis-rolled front, a phantom meeting,
    /// or a wrong re-base moves the blend by a step (25bp+) while the honest gap is basis noise
    /// (~1-3bp on the wired index-matched families).
    ///
    /// Wired via meetings.json per run: guardFutures (pattern), guardFuturesKind
    /// ("monthavg" = 30-day average-rate future like FF/IB; "imm3m" = 3M compounded-index future
    /// over an IMM quarter like SFI/COR/ER), guardFuturesTolBp, guardFuturesBasisBp (expected
    /// futures-minus-OIS spread for basis-bearing families) and guardFuturesDcc (365 SONIA/CORRA,
    /// 360 ESTR/Euribor). Families PROBED by NAME on the live terminal 2026-08-20: FF (avg
    /// EFFR) / IB (avg AUD cash rate) / SFI (compounded SONIA) / COR (compounded CORRA) /
    /// TKY (ICE 3M ESTR — desk-supplied root; index-matched, 0.0bp gap live). The desk's Euribor
    /// hedge (ER) was considered for EUR but carries the Euribor/ESTR basis — quoted live as
    /// TKYER{MY} Comdty; guardFuturesBasisBp supports wiring a basis-bearing family if ever
    /// wanted. Considered and REJECTED: NZD ZB (settles on 3M bank bills — a wide, unstable
    /// BKBM-vs-OCR basis), JPY (no TONA future resolves). SNB's SARON strip is its own MID
    /// SOURCE — self-referential, never a guard.
    ///
    /// A breach line starts with "FUTURES GUARD TRIGGERED" — that prefix IS the flag: it lands in
    /// the run notes, the CLI output, and the app's status log, and means the meeting rows
    /// disagree with an exchange-settled instrument — treat it as a roll/calendar/re-base fault
    /// until proven otherwise.</summary>
    public static class FuturesGuard
    {
        public const string TriggerPrefix = "FUTURES GUARD TRIGGERED";

        public static List<string> Check(PricingService svc)
        {
            var notes = new List<string>();
            foreach (var sched in MeetingsStore.Schedules.Where(s =>
                         string.IsNullOrEmpty(s.Kind) && !string.IsNullOrEmpty(s.GuardFutures)))
            {
                try { notes.Add(CheckRun(svc, sched)); }
                catch (Exception ex) { notes.Add($"futures guard {sched.Name}: {ex.Message} — skipped"); }
            }
            return notes;
        }

        public static string CheckRun(PricingService svc, MeetingScheduleDef sched)
        {
            var run = svc.MeetingRun(sched, 10);
            var rows = run.Rows;
            if (rows.Count < 2)
                return $"futures guard {sched.Name}: run too short ({rows.Count} row(s)) — skipped";

            bool imm = sched.GuardFuturesKind.Equals("imm3m", StringComparison.OrdinalIgnoreCase);

            // the first contract window that (a) has not started (a live window blends realized
            // fixings the run cannot know), (b) is fully covered by known periods on both ends,
            // and (c) actually quotes
            var probe = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            for (int i = 0; i < 12; i++)
            {
                probe = probe.AddMonths(1);
                if (imm && probe.Month % 3 != 0) continue;
                var (a, b) = imm ? (ThirdWednesday(probe), ThirdWednesday(probe.AddMonths(3)))
                                 : (probe, probe.AddMonths(1));
                if (a <= DateTime.Today) continue;
                if (rows[0].Date > a || rows[^1].Date < b) continue;

                var tk = sched.GuardFutures!.Replace("{MY}", FutMyPublic(probe));
                if (svc.Snapshot.Get(tk)?.Mid is not { } px) continue;

                double implied = 100.0 - px;
                double blend = imm ? CompoundedBlend(rows, a, b, sched.GuardFuturesDcc) : AverageBlend(rows, a, b);
                // basis-bearing guards (EUR: Euribor settles on a different index than the ESTR
                // meetings) are judged against their EXPECTED spread, not zero
                double gapBp = (implied - blend) * 100.0 - sched.GuardFuturesBasisBp;
                string basis = sched.GuardFuturesBasisBp != 0
                    ? $" (over a {sched.GuardFuturesBasisBp:+0.0;-0.0}bp expected basis)" : "";
                string window = imm ? $"{a:dd-MMM-yy}→{b:dd-MMM-yy}" : probe.ToString("MMM-yy");

                return Math.Abs(gapBp) <= sched.GuardFuturesTolBp
                    ? $"futures guard {sched.Name} ok: {tk} {window} implies {implied:0.000} vs " +
                      $"meeting blend {blend:0.000}{basis} (Δ{gapBp:+0.0;-0.0}bp ≤ {sched.GuardFuturesTolBp:0.0})"
                    // CHECK-prefixed so it reaches the pre-publish gate (desk 2026-08-27). The
                    // futures share nothing with the OIS machinery, so a breach is the strongest
                    // fault signal the app has — it used to be the only one that could not stop
                    // the press, while a +12.1bp change-on-day could.
                    : $"{OutlierGuard.Prefix}: {TriggerPrefix} — {sched.Name}: {tk} {window} implies {implied:0.000} but the " +
                      $"meeting rows blend to {blend:0.000}{basis} (Δ{gapBp:+0.0;-0.0}bp > {sched.GuardFuturesTolBp:0.0}bp " +
                      "tolerance). The futures share nothing with the OIS machinery — treat this as a " +
                      "roll/calendar/re-base fault until proven otherwise (run tools\\verify_strip_changes.py).";
            }
            return $"futures guard {sched.Name}: no covered, quoted contract window found — skipped " +
                   "(short run or futures missing from the snapshot)";
        }

        /// <summary>Calendar-day average of the period mids over [a, b) — how 30-day average-rate
        /// futures settle (weekend days carry the prevailing rate, which a per-period-constant
        /// blend reproduces exactly).</summary>
        public static double AverageBlend(IReadOnlyList<MeetingRow> rows, DateTime a, DateTime b)
        {
            double sum = 0; int n = 0;
            for (var d = a; d < b; d = d.AddDays(1)) { sum += MidAt(rows, d); n++; }
            return sum / n;
        }

        /// <summary>Piecewise compounding of the period mids over [a, b), annualized the way the
        /// 3M futures settle — <paramref name="dcc"/> 365 for SONIA/CORRA, 360 for Euribor/ESTR.
        /// Simple growth within a constant-rate segment, compounded across segments — sub-0.1bp of
        /// the exact daily compounding at policy-rate levels, far inside the guard tolerance.</summary>
        public static double CompoundedBlend(IReadOnlyList<MeetingRow> rows, DateTime a, DateTime b, int dcc = 365)
        {
            double growth = 1.0;
            var d = a;
            while (d < b)
            {
                double r = MidAt(rows, d);
                var next = rows.FirstOrDefault(x => x.Date > d)?.Date ?? b;
                if (next > b) next = b;
                growth *= 1.0 + r / 100.0 * (next - d).TotalDays / dcc;
                d = next;
            }
            return (growth - 1.0) * dcc / (b - a).TotalDays * 100.0;
        }

        /// <summary>The mid whose period contains <paramref name="d"/> — the last row at or before
        /// it. Callers guarantee coverage (rows[0] ≤ window start).</summary>
        private static double MidAt(IReadOnlyList<MeetingRow> rows, DateTime d)
        {
            for (int i = rows.Count - 1; i >= 0; i--)
                if (rows[i].Date <= d) return rows[i].MidPct;
            throw new InvalidOperationException($"no meeting period covers {d:yyyy-MM-dd}");
        }

        public static DateTime ThirdWednesday(DateTime month)
        {
            var first = new DateTime(month.Year, month.Month, 1);
            int wed = ((int)DayOfWeek.Wednesday - (int)first.DayOfWeek + 7) % 7;
            return first.AddDays(wed + 14);
        }

        internal static string FutMyPublic(DateTime month) =>
            "FGHJKMNQUVXZ"[month.Month - 1] + (month.Year % 10).ToString();
    }
}
