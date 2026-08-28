namespace RateDesk.Scenarios.Harness;

/// <summary>Date helpers for scenario authoring. EVERYTHING is anchored on the REAL
/// <see cref="DateTime.Today"/> and the REAL London clock: the app reads both directly
/// (PricingService.ResolveMeetingDates, BuildWeekly's change anchors, DecisionClock.LondonNow),
/// and the harness deliberately does NOT fake them. A scenario that wants "the decision is
/// today" puts a decision on today's date; one that wants "before the announcement" gives the
/// bank a decision time later today. That way every path under test is the shipping path, with
/// no test-only branch anywhere in the product.</summary>
public static class Cal
{
    public static DateTime Today => DateTime.Today;

    /// <summary>Calendar-day offset from today.</summary>
    public static DateTime D(int days) => DateTime.Today.AddDays(days);

    public static bool IsBd(DateTime d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    public static DateTime NextBd(DateTime d)
    {
        var x = d.AddDays(1);
        while (!IsBd(x)) x = x.AddDays(1);
        return x;
    }

    public static DateTime PrevBd(DateTime d)
    {
        var x = d.AddDays(-1);
        while (!IsBd(x)) x = x.AddDays(-1);
        return x;
    }

    /// <summary>Business-day offset from today (negative = back). 0 = today, or the previous
    /// business day when today is a weekend.</summary>
    public static DateTime Bd(int n)
    {
        var d = DateTime.Today;
        while (!IsBd(d)) d = d.AddDays(-1);
        for (int i = 0; i < Math.Abs(n); i++) d = n > 0 ? NextBd(d) : PrevBd(d);
        return d;
    }

    /// <summary>Every business day in [from, to] inclusive, ascending.</summary>
    public static IEnumerable<DateTime> BusinessDays(DateTime from, DateTime to)
    {
        for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
            if (IsBd(d)) yield return d;
    }

    /// <summary>The app's own 1m convention: SAME DAY LAST MONTH (clamped at month ends).</summary>
    public static DateTime MonthAgo(DateTime d) => d.AddMonths(-1);

    /// <summary>London wall clock now - the same conversion DecisionClock uses.</summary>
    public static DateTime LondonNow()
    {
        try
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"));
        }
        catch { return DateTime.UtcNow; }
    }

    /// <summary>A decision time on today's London clock that has NOT yet passed.</summary>
    public const string TimeNotYetPassed = "23:50";

    /// <summary>A decision time on today's London clock that HAS passed.</summary>
    public const string TimePassed = "00:05";

    /// <summary>The harness only makes sense inside the window where both of the above are
    /// unambiguous. Returns the reason it is not, or null.</summary>
    public static string? ClockWindowProblem()
    {
        var now = LondonNow().TimeOfDay;
        if (now < TimeSpan.Parse(TimePassed).Add(TimeSpan.FromMinutes(5)))
            return $"London time is {now:hh\\:mm} - too close to midnight for the " +
                   $"'already announced today' scenarios (needs > 00:10). Re-run later.";
        if (now >= TimeSpan.Parse(TimeNotYetPassed))
            return $"London time is {now:hh\\:mm} - too late for the 'not yet announced today' " +
                   $"scenarios (needs < 23:50). Re-run tomorrow.";
        return null;
    }
}
