using RateDesk.Core;

namespace RateDesk.Scenarios.Harness;

/// <summary>Which ticker SPELLING a piece of synthetic data lands on. Runs with a contributor
/// source (RBA/RBNZ = NABZ, BOC = BMOD) quote TWO securities: "ADSF1A NABZ Curncy" (the desk's
/// own price page, often with no date fields) and "ADSF1A Curncy" (the composite, which carries
/// the fields). Production snapshots and stores BOTH; a scenario can seed them apart to exercise
/// the merge and the fallbacks.</summary>
public enum Spell { Both, Active, Composite }

/// <summary>One rung's LIVE quote. N is the generic index (0 = the run-down contract that matures
/// at the next meeting, 1 = the first meeting period, and so on).</summary>
public sealed record RungQuote(
    int N,
    double? Mid = null,
    double? PrevClose = null,
    DateTime? Eff = null,
    DateTime? Mat = null,
    double? AgeMinutes = null,
    Spell Spelling = Spell.Both);

/// <summary>One stored daily close / London snap for a rung.</summary>
public sealed record HistPointSpec(int N, DateTime Date, double Value, Spell Spelling = Spell.Both);

/// <summary>A quote on any other security (the o/n fixing, a guard future, ...).</summary>
public sealed record ExtraQuote(string Ticker, double? Mid = null, double? PrevClose = null,
    DateTime? Mat = null, DateTime? Eff = null, double? AgeMinutes = null);

/// <summary>One bank inside a scenario. The bank NAME must be one of the nine the daily surfaces
/// know (DailyBlast.Blocks) or the blast/workbook/sheet-email blocks silently drop it. Everything
/// else - ticker root, contributor source, fixing ticker, guard future - comes from the SHIPPED
/// config\meetings.json entry for that bank; the scenario overrides only the calendar and the
/// market.</summary>
public sealed class BankSpec
{
    public required string Bank { get; init; }

    // ---- calendar overrides (always written; an empty list writes an empty list) ----
    public List<DateTime> Dates { get; } = new();          // swap-period STARTS
    public List<DateTime> PastDates { get; } = new();      // settled boundaries
    public List<DateTime> DecisionDates { get; } = new();  // announcements
    public string? DecisionTimeLondon { get; set; }        // null = keep the shipped time
    public bool? RollsAtPeriodStart { get; set; }
    public bool? MarkTurnPeriods { get; set; }
    public bool? TrustConfigDates { get; set; }

    /// <summary>null = keep the shipped contributor; "" = force the composite.</summary>
    public string? Source { get; set; }

    /// <summary>Guard futures are OFF by default: the harness seeds no futures, and an absent
    /// contract only produces a "skipped" note. Set false to keep the shipped guard wired and
    /// seed the contract through <see cref="Extras"/>.</summary>
    public bool DisableGuardFutures { get; set; } = true;

    /// <summary>false = the store holds the 45-day price seed but NO historical maturity records
    /// - a machine on which recording has only just begun. The live quotes still stamp today's,
    /// as a real run does. Use it to prove a fix stands on the price history alone.</summary>
    public bool RecordMaturities { get; set; } = true;

    // ---- market ----

    /// <summary>Live mid of the run's o/n fixing ticker (FEDL01, ESTRON, ...). null = unquoted.</summary>
    public double? Fixing { get; set; }
    public double? FixingPrevClose { get; set; }
    public List<RungQuote> Rungs { get; } = new();
    public List<HistPointSpec> Closes { get; } = new();
    /// <summary>Per-day maturity/effective records — Bloomberg's own statement of what each rung
    /// pointed at on each day. The daily run stores these for every rung on every run, so the
    /// store accumulates them; a scenario that seeds history without them models a store that
    /// has never been maintained.</summary>
    public List<(int N, DateTime Day, DateTime Eff, DateTime Mat)> Records { get; } = new();
    public List<HistPointSpec> Snaps { get; } = new();
    public List<ExtraQuote> Extras { get; } = new();

    /// <summary>Daily o/n fixing prints, for the (silent) compounded-fixing mechanics.</summary>
    public List<(DateTime Date, double Value)> FixingHistory { get; } = new();

    /// <summary>Raw daily closes for any explicitly-named security (the policy TARGET ticker's
    /// history, chiefly — the policy-delta base needs the target's last pre-decision close).</summary>
    public List<(string Ticker, DateTime Date, double Value)> RawCloses { get; } = new();

    // ---------- series helpers (business days only, like BDH) ----------

    /// <summary>A flat stored series on rung <paramref name="n"/> over [from, to], business days
    /// only. The store drops today's point by design, so a same-day close is never booked.</summary>
    public BankSpec Close(int n, DateTime from, DateTime to, double value, Spell sp = Spell.Both)
    {
        foreach (var d in Cal.BusinessDays(from, to)) Closes.Add(new HistPointSpec(n, d, value, sp));
        return this;
    }

    /// <summary>A stored series that STEPS on <paramref name="stepOn"/>: <paramref name="before"/>
    /// up to the day before, <paramref name="after"/> from that day on. A persistent level shift
    /// survives the Hampel despike filter by construction; an isolated one-day bump does not, which
    /// is why the harness refuses those (see Hygiene).</summary>
    public BankSpec CloseStep(int n, DateTime from, DateTime to, DateTime stepOn,
        double before, double after, Spell sp = Spell.Both)
    {
        foreach (var d in Cal.BusinessDays(from, to))
            Closes.Add(new HistPointSpec(n, d, d.Date < stepOn.Date ? before : after, sp));
        return this;
    }

    public BankSpec Snap(int n, DateTime from, DateTime to, double value, Spell sp = Spell.Both)
    {
        foreach (var d in Cal.BusinessDays(from, to)) Snaps.Add(new HistPointSpec(n, d, value, sp));
        return this;
    }

    public BankSpec SnapStep(int n, DateTime from, DateTime to, DateTime stepOn,
        double before, double after, Spell sp = Spell.Both)
    {
        foreach (var d in Cal.BusinessDays(from, to))
            Snaps.Add(new HistPointSpec(n, d, d.Date < stepOn.Date ? before : after, sp));
        return this;
    }

    /// <summary>Stored closes AND London snaps at the same level - the ordinary case (the desk's
    /// marks are the snaps; on a quiet tape the closes agree).</summary>
    public BankSpec Level(int n, DateTime from, DateTime to, double value, Spell sp = Spell.Both)
        => Close(n, from, to, value, sp).Snap(n, from, to, value, sp);

    public BankSpec LevelStep(int n, DateTime from, DateTime to, DateTime stepOn,
        double before, double after, Spell sp = Spell.Both)
        => CloseStep(n, from, to, stepOn, before, after, sp)
            .SnapStep(n, from, to, stepOn, before, after, sp);

    // ---------- CONTRACT-level seeding (roll-aware) ----------

    /// <summary>Which generic index carried <paramref name="contract"/> on <paramref name="day"/>:
    /// the number of RENUMBER BOUNDARIES strictly after that day and at or before the contract's
    /// own start. A day that IS a boundary is read under the numbering in force the day before -
    /// the family renumbers through the day, so the boundary day's own marks are old-numbered.
    ///
    /// BOUNDARIES here are the dates the family renumbers on: the ANNOUNCEMENT for every bank
    /// except the Riksbank, the PERIOD START for the Riksbank (rollsAtPeriodStart). Pass past
    /// boundaries too - a 1m lookback usually crosses one.</summary>
    public static int RungOn(DateTime day, DateTime contract, IEnumerable<DateTime> boundaries)
    {
        var b = boundaries.Select(x => x.Date).OrderBy(x => x).ToList();
        var d = day.Date;
        if (b.Contains(d)) d = d.AddDays(-1);
        return b.Count(x => x > d && x <= contract.Date);
    }

    /// <summary>Seed ONE CONTRACT's quoted level over time, onto whichever rung carried it on
    /// each day. This is how a quiet market looks in the raw data: the contract's rate is
    /// constant, but the ticker number it lives under steps down at every boundary. A surface
    /// that reads the rung naively then books the inter-contract gap as a market move - which is
    /// exactly what these scenarios are here to catch.</summary>
    public BankSpec Contract(DateTime contractStart, IEnumerable<DateTime> boundaries,
        DateTime from, DateTime to, double level, Spell sp = Spell.Both)
        => ContractStep(contractStart, boundaries, from, to, DateTime.MaxValue, level, level, sp);

    /// <summary>As <see cref="Contract"/>, with the contract REPRICING on
    /// <paramref name="stepOn"/> (the day a decision changes what this period is worth).</summary>
    public BankSpec ContractStep(DateTime contractStart, IEnumerable<DateTime> boundaries,
        DateTime from, DateTime to, DateTime stepOn, double before, double after,
        Spell sp = Spell.Both)
    {
        var b = boundaries.Select(x => x.Date).OrderBy(x => x).ToList();
        var end = b.FirstOrDefault(x => x > contractStart.Date);
        if (end == default) end = contractStart.AddDays(42);
        foreach (var d in Cal.BusinessDays(from, to))
        {
            int n = RungOn(d, contractStart, b);
            if (n < 1 || n > 13) continue;
            double v = d < stepOn.Date ? before : after;
            Closes.Add(new HistPointSpec(n, d, v, sp));
            Snaps.Add(new HistPointSpec(n, d, v, sp));
            Records.Add((n, d, contractStart.Date, end));
        }
        return this;
    }

    public BankSpec Quote(int n, double? mid, double? prevClose = null, DateTime? eff = null,
        DateTime? mat = null, double? age = null, Spell sp = Spell.Both)
    { Rungs.Add(new RungQuote(n, mid, prevClose, eff, mat, age, sp)); return this; }

    public BankSpec Fix(double mid, double? prev = null)
    { Fixing = mid; FixingPrevClose = prev; return this; }

    public BankSpec FixHist(DateTime from, DateTime to, double value)
    { foreach (var d in Cal.BusinessDays(from, to)) FixingHistory.Add((d, value)); return this; }

    public BankSpec FixHistStep(DateTime from, DateTime to, DateTime stepOn, double before, double after)
    {
        foreach (var d in Cal.BusinessDays(from, to))
            FixingHistory.Add((d, d.Date < stepOn.Date ? before : after));
        return this;
    }
}

/// <summary>Sentinels: NaN = "do not check this number", <see cref="Date"/> = same for a date.</summary>
public static class Any
{
    public const double Num = double.NaN;
    public static readonly DateTime Date = DateTime.MaxValue;
    public static bool Is(double? v) => v is double d && double.IsNaN(d);
    public static bool Is(DateTime? d) => d.HasValue && d.Value == Date;
}

/// <summary>One published run row, as the desk reads it off the email / blast / sheet.</summary>
public sealed record RowExpect(
    DateTime Start,
    DateTime? End,
    double? Mid,
    double? Priced,
    double? Step,
    double? D1,
    double? W1,
    double? M1,
    bool Turn = false);

/// <summary>The bank's line in the CB Front Meeting table.</summary>
public sealed record FrontExpect(
    DateTime? Decision,     // null = the table must print "{start}*" (no decision on file)
    DateTime Start,
    double Mid,
    double? Fixing,
    double? Priced,
    bool Rebased,
    bool Turn = false);

public sealed class BankExpect
{
    public required string Bank { get; init; }
    public FrontExpect? Front { get; set; }
    /// <summary>The bank must NOT appear in the front table at all.</summary>
    public bool NoFront { get; set; }
    /// <summary>Exact published rows, in order. null = not checked.</summary>
    public List<RowExpect>? Rows { get; set; }
    public int? RowCount { get; set; }
    /// <summary>The run must be absent from the report entirely (it published no rows).</summary>
    public bool NoRun { get; set; }
    public bool? Rebased { get; set; }
    public double? Fixing { get; set; }
}

public sealed class ScenarioSpec
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    /// <summary>The desk question this scenario answers, in one line.</summary>
    public required string Question { get; init; }
    public List<BankSpec> Banks { get; } = new();
    public List<BankExpect> Expect { get; } = new();
    public List<string> NotesContain { get; } = new();
    public List<string> NotesNotContain { get; } = new();
    /// <summary>Bespoke assertions: return one failure string per problem, empty when clean.</summary>
    public List<Func<Surfaces, IEnumerable<string>>> Custom { get; } = new();
    /// <summary>Positive control: this scenario is EXPECTED to report failures. A suite that
    /// cannot go red proves nothing.</summary>
    public bool MustFail { get; set; }
    /// <summary>Quote age stamped on every rung that does not name its own. Staleness is judged
    /// as (age - the snapshot's 10th-percentile baseline), so a scenario testing the >1h stale
    /// warning must leave most rungs on this default and age only the rung under test.</summary>
    public double? DefaultAgeMinutes { get; set; } = 5.0;
    /// <summary>Time of day (London) the published marks were taken, when the run models a
    /// PINNED snap. Production sets this from SnapDiscipline once the 16:15 snap owns the marks,
    /// so the decision gates read the clock the PRICES belong to. null = live marks.</summary>
    public TimeSpan? MarksAsOfLondon { get; set; }
    /// <summary>Also walk the save-down history tables (DailyBook.BankHistoryRows).</summary>
    public bool CheckHistoryRows { get; set; }
    public int HistoryDays { get; set; } = 15;
    /// <summary>Expected history rows. Only the listed keys are checked; a missing key fails.</summary>
    public List<(string Bank, DateTime Day, DateTime Start, double Rate, double? D1)> HistoryRowExpect { get; } = new();
}
