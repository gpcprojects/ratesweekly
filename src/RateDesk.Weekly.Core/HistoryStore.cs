using System.Globalization;
using Microsoft.Data.Sqlite;
using RateDesk.Core.Market;

namespace RateDesk.Weekly.Core
{
    /// <summary>The maintained history store — RatesWeekly's one persistent market-data asset.
    /// Daily closes only, raw tickers only: derived series (stitched meeting histories, par-derived
    /// forwards, flies, correlations) are recomputed from raw on every build, so nothing derived can
    /// go stale on disk. Bloomberg BDH remains the source of truth: every update re-fetches a
    /// trailing overlap and upserts, so a skipped week or a restated print self-heals.
    /// Implements IHistoryProvider so RateDesk.Core analytics (SeriesStats, Correlation, the meeting
    /// stitcher) can run straight off the store with no terminal attached.</summary>
    public sealed class HistoryStore : IHistoryProvider, IDisposable
    {
        private readonly SqliteConnection _db;
        private readonly object _gate = new();

        public string Path { get; }

        public HistoryStore(string path)
        {
            Path = path;
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            _db.Open();
            Exec("PRAGMA journal_mode=WAL;");
            // a second connection (weekly + daily overlapping, a CLI run beside the app) must
            // WAIT, not throw SQLITE_BUSY into a swallowed catch and silently lose the day's
            // rows (fresh-eyes review 2026-08-26)
            Exec("PRAGMA busy_timeout=15000;");
            Exec("""
                CREATE TABLE IF NOT EXISTS daily(
                    ticker TEXT NOT NULL,
                    date   TEXT NOT NULL,   -- yyyy-MM-dd
                    value  REAL NOT NULL,
                    PRIMARY KEY(ticker, date)
                ) WITHOUT ROWID;
                """);
            // PROVENANCE (2026-08-20, integrated-history): where a close came from — 'bbg'
            // (the engine's own Bloomberg pulls) or 'xls' (rows the desk stored manually in the
            // fallback workbook during an app/API outage, ingested on the next run). Tolerant
            // migration: the column may already exist.
            try { Exec("ALTER TABLE daily ADD COLUMN source TEXT NOT NULL DEFAULT 'bbg';"); }
            catch { /* already migrated */ }
            Exec("""
                CREATE TABLE IF NOT EXISTS runs(
                    id   INTEGER PRIMARY KEY AUTOINCREMENT,
                    asof TEXT NOT NULL,     -- ISO local timestamp
                    kind TEXT NOT NULL,
                    note TEXT
                );
                """);
            // Depth watermark. Without this the update engine can only ask "does this ticker have
            // ANY row", which is true after the first shallow run forever — so raising the seed
            // depth later would silently fetch nothing and the deepening plan would be a no-op.
            Exec("""
                CREATE TABLE IF NOT EXISTS coverage(
                    ticker    TEXT PRIMARY KEY,
                    seed_days INTEGER NOT NULL   -- deepest BDH window successfully fetched
                ) WITHOUT ROWID;
                """);
            Exec("""
                CREATE TABLE IF NOT EXISTS maturity(
                    ticker   TEXT NOT NULL,
                    date     TEXT NOT NULL,      -- observation day, yyyy-MM-dd
                    maturity TEXT NOT NULL,      -- what the ticker meant that day
                    PRIMARY KEY(ticker, date)
                ) WITHOUT ROWID;
                """);
            // UNIFIED INFLATION FIXINGS HISTORY (2026-08-25): daily marks keyed by FIXING
            // IDENTITY (family + reference month), not by rolling ticker — the ticker re-points
            // a year forward when its month prints, so ticker-keyed history would splice two
            // different fixings. value is the market's native quote: CPI = forecast index level,
            // RPI/HICP = YoY in bp. source: 'xls' (validated external-pricer history) or 'bbg'.
            Exec("""
                CREATE TABLE IF NOT EXISTS fixings(
                    family TEXT NOT NULL,        -- CPI | RPI | HICP
                    fix    TEXT NOT NULL,        -- reference month, yyyy-MM
                    date   TEXT NOT NULL,        -- observation day, yyyy-MM-dd
                    value  REAL NOT NULL,
                    source TEXT NOT NULL,
                    PRIMARY KEY(family, fix, date)
                ) WITHOUT ROWID;
                """);
        }

        /// <summary>Upsert unified inflation-fixing marks. Merge rule (desk 2026-08-25): a
        /// VALIDATED external-sheet row always wins ('xls' overwrites anything — where the
        /// existing data is good, keep it); a Bloomberg row fills gaps and refreshes only rows
        /// that are already Bloomberg's ('bbg' never overwrites 'xls'). Rows that fail the
        /// ingest validation never reach this method, which is how "existing data is bad →
        /// replace with Bloomberg" happens: the bad row is absent and bbg fills it.</summary>
        public int UpsertFixings(string family, string fix, IEnumerable<HistPoint> points,
            string source, bool excludeToday = true)
        {
            lock (_gate)
            {
                using var tx = _db.BeginTransaction();
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = source == "xls"
                    ? "INSERT INTO fixings(family,fix,date,value,source) VALUES(@f,@x,@d,@v,'xls') " +
                      "ON CONFLICT(family,fix,date) DO UPDATE SET value=excluded.value, source='xls';"
                    : "INSERT INTO fixings(family,fix,date,value,source) VALUES(@f,@x,@d,@v,'bbg') " +
                      "ON CONFLICT(family,fix,date) DO UPDATE SET value=excluded.value " +
                      "WHERE fixings.source='bbg';";
                cmd.Parameters.AddWithValue("@f", family);
                cmd.Parameters.AddWithValue("@x", fix);
                var pD = cmd.Parameters.Add("@d", SqliteType.Text);
                var pV = cmd.Parameters.Add("@v", SqliteType.Real);
                int n = 0;
                var today = DateTime.Today;
                foreach (var p in points)
                {
                    if (excludeToday && p.Date.Date >= today) continue;
                    pD.Value = p.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    pV.Value = p.Value;
                    n += cmd.ExecuteNonQuery();
                }
                tx.Commit();
                return n;
            }
        }

        /// <summary>Every unified fixing mark for a family, ascending by (fix, date).</summary>
        public List<(string Fix, DateTime Date, double Value, string Source)> GetFixingHistory(string family)
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT fix, date, value, source FROM fixings " +
                                  "WHERE family=@f ORDER BY fix, date;";
                cmd.Parameters.AddWithValue("@f", family);
                var res = new List<(string, DateTime, double, string)>();
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    res.Add((r.GetString(0),
                        DateTime.ParseExact(r.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                        r.GetDouble(2), r.GetString(3)));
                return res;
            }
        }

        public long FixingRowCount() { lock (_gate) return Scalar("SELECT COUNT(*) FROM fixings;"); }

        /// <summary>Daily closes WITH provenance, ascending — for surfaces that must show where a
        /// number came from ('bbg' engine pull vs 'xls' manual fallback entry).</summary>
        public List<(DateTime Date, double Value, string Source)> GetDailyWithSource(string ticker, int lookbackDays)
        {
            lock (_gate)
            {
                var cutoff = DateTime.Today.AddDays(-Math.Max(1, lookbackDays))
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT date, value, source FROM daily " +
                                  "WHERE ticker=@t AND date>=@c ORDER BY date;";
                cmd.Parameters.AddWithValue("@t", ticker);
                cmd.Parameters.AddWithValue("@c", cutoff);
                var res = new List<(DateTime, double, string)>();
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    res.Add((DateTime.ParseExact(r.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                        r.GetDouble(1), r.IsDBNull(2) ? "bbg" : r.GetString(2)));
                return res;
            }
        }

        /// <summary>A security's MATURITY as observed on a given day. Rolling generics re-point
        /// without warning — a CPI fixing ticker jumps a whole year the day its month publishes —
        /// and the maturity is the only field that says which contract a ticker means TODAY.
        /// Storing it daily lets a later run detect that a roll happened inside a lookback window,
        /// which is the difference between a real change and a year's worth of nonsense.</summary>
        public void SetMaturity(string ticker, DateTime asOf, DateTime maturity)
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "INSERT INTO maturity(ticker,date,maturity) VALUES(@t,@d,@m) " +
                                  "ON CONFLICT(ticker,date) DO UPDATE SET maturity=excluded.maturity;";
                cmd.Parameters.AddWithValue("@t", ticker);
                cmd.Parameters.AddWithValue("@d", asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@m", maturity.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>The most recent day a maturity was RECORDED for this ticker — null if never.
        /// The hard-data rule's discriminator: a rung is Bloomberg-documented right now only if
        /// its record day matches the family front rung's (a stale historical record means the
        /// field has since gone dark).</summary>
        public DateTime? MaturityRecordDay(string ticker)
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT MAX(date) FROM maturity WHERE ticker=@t;";
                cmd.Parameters.AddWithValue("@t", ticker);
                return cmd.ExecuteScalar() is string s2
                    ? DateTime.ParseExact(s2, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : null;
            }
        }

        /// <summary>What the ticker means NOW — the most recently recorded maturity, whatever day
        /// that was. Use this to identify and order contracts; use <see cref="MaturityAsOf"/> only
        /// to ask what it meant on some past date (i.e. to detect a roll).</summary>
        public DateTime? MaturityLatest(string ticker)
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT maturity FROM maturity WHERE ticker=@t ORDER BY date DESC LIMIT 1;";
                cmd.Parameters.AddWithValue("@t", ticker);
                return cmd.ExecuteScalar() is string s
                    ? DateTime.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : null;
            }
        }

        /// <summary>Windows in which a ticker's RECORDED maturity changed: the change was OBSERVED
        /// on Date, and the previous observation (still on the old maturity) was PrevSeen — the
        /// actual re-point happened somewhere in (PrevSeen, Date]. Grows a measured boundary
        /// history as updates accumulate; CalendarHealth validates the configured calendars
        /// against it, so a re-point the calendar doesn't know about is flagged on the very next
        /// update instead of silently mis-shifting a lookback. The window matters: updates are
        /// WEEKLY, so a roll at an 11-Aug decision is often first observed on the 20th — judging
        /// the observation date alone false-flags every roll seen late (live RBA/NORGES,
        /// 2026-08-20).</summary>
        public List<(DateTime Date, DateTime PrevSeen)> MaturityChanges(string ticker, int sinceDays = 400)
        {
            lock (_gate)
            {
                var cutoff = DateTime.Today.AddDays(-Math.Max(1, sinceDays))
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT date, maturity FROM maturity WHERE ticker=@t AND date>=@c ORDER BY date;";
                cmd.Parameters.AddWithValue("@t", ticker);
                cmd.Parameters.AddWithValue("@c", cutoff);
                var changes = new List<(DateTime, DateTime)>();
                string? prev = null;
                DateTime prevDate = default;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var mat = r.GetString(1);
                    var d = DateTime.ParseExact(r.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (prev != null && mat != prev)
                        changes.Add((d, prevDate));
                    prev = mat;
                    prevDate = d;
                }
                return changes;
            }
        }

        /// <summary>Maturity recorded at or before <paramref name="asOf"/>, or null if never seen.</summary>
        public DateTime? MaturityAsOf(string ticker, DateTime asOf)
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT maturity FROM maturity WHERE ticker=@t AND date<=@d " +
                                  "ORDER BY date DESC LIMIT 1;";
                cmd.Parameters.AddWithValue("@t", ticker);
                cmd.Parameters.AddWithValue("@d", asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return cmd.ExecuteScalar() is string s
                    ? DateTime.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : null;
            }
        }

        /// <summary>Deepest BDH window already fetched for a ticker; 0 when never seeded.</summary>
        /// An existing store predating the coverage table reports 0 and simply re-seeds at the
        /// current depth on the next run — which costs the same as a maintain pass at 45d.</summary>
        public int SeededDepth(string ticker)
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT seed_days FROM coverage WHERE ticker=@t;";
                cmd.Parameters.AddWithValue("@t", ticker);
                return cmd.ExecuteScalar() is long d ? (int)d : 0;
            }
        }

        /// <summary>Record a successful fetch depth. Monotone: a shallow maintain pass can never
        /// lower the watermark a deep seed established.</summary>
        public void SetSeededDepth(string ticker, int days)
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "INSERT INTO coverage(ticker,seed_days) VALUES(@t,@d) " +
                                  "ON CONFLICT(ticker) DO UPDATE SET seed_days=MAX(seed_days,excluded.seed_days);";
                cmd.Parameters.AddWithValue("@t", ticker);
                cmd.Parameters.AddWithValue("@d", days);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>How many tickers hold a close at or before <paramref name="asOf"/> — the
        /// operational health number: it answers "can the 1w/1m lookbacks actually resolve".</summary>
        public long TickersCoveringDate(DateTime asOf)
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM (SELECT ticker FROM daily WHERE date<=@d GROUP BY ticker);";
                cmd.Parameters.AddWithValue("@d", asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return (long)(cmd.ExecuteScalar() ?? 0L);
            }
        }

        /// <summary>Insert-or-update daily closes for one ticker. Today's point is excluded by
        /// default — an intraday PX_LAST is not a settled close and would be booked as one.</summary>
        /// <summary>Upsert daily closes. <paramref name="source"/> is the provenance:
        /// 'bbg' rows always win (a real Bloomberg pull supersedes a manual entry for the same
        /// day); 'xls' rows are INSERT-ONLY — a manual fallback entry must never overwrite
        /// engine data.</summary>
        public int UpsertDaily(string ticker, IEnumerable<HistPoint> points, bool excludeToday = true,
            string source = "bbg")
        {
            lock (_gate)
            {
                using var tx = _db.BeginTransaction();
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = source == "bbg"
                    ? "INSERT INTO daily(ticker,date,value,source) VALUES(@t,@d,@v,'bbg') " +
                      "ON CONFLICT(ticker,date) DO UPDATE SET value=excluded.value, source='bbg';"
                    : "INSERT INTO daily(ticker,date,value,source) VALUES(@t,@d,@v,@s) " +
                      "ON CONFLICT(ticker,date) DO NOTHING;";
                if (source != "bbg") cmd.Parameters.AddWithValue("@s", source);
                var pT = cmd.Parameters.Add("@t", SqliteType.Text);
                var pD = cmd.Parameters.Add("@d", SqliteType.Text);
                var pV = cmd.Parameters.Add("@v", SqliteType.Real);
                pT.Value = ticker;
                int n = 0;
                var today = DateTime.Today;
                foreach (var p in points)
                {
                    if (excludeToday && p.Date.Date >= today) continue;
                    pD.Value = p.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    pV.Value = p.Value;
                    n += cmd.ExecuteNonQuery();
                }
                tx.Commit();
                return n;
            }
        }

        /// <summary>Ascending daily closes over the last N calendar days (IHistoryProvider).</summary>
        public IReadOnlyList<HistPoint> GetDaily(string ticker, int lookbackDays)
        {
            lock (_gate)
            {
                var cutoff = DateTime.Today.AddDays(-Math.Max(1, lookbackDays))
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT date, value FROM daily WHERE ticker=@t AND date>=@c ORDER BY date;";
                cmd.Parameters.AddWithValue("@t", ticker);
                cmd.Parameters.AddWithValue("@c", cutoff);
                var list = new List<HistPoint>();
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new HistPoint(
                        DateTime.ParseExact(r.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                        r.GetDouble(1)));
                return list;
            }
        }

        /// <summary>The close AT OR BEFORE <paramref name="asOf"/> — the primitive every "today /
        /// 1w ago / 1m ago" line is built from. Walking back to the last available close (rather
        /// than requiring an exact date) is what makes weekends, holidays and thin markets work:
        /// a 1w lookback landing on a Saturday reads Friday's close, as the desk would.</summary>
        public double? ValueAsOf(string ticker, DateTime asOf)
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText =
                    "SELECT value FROM daily WHERE ticker=@t AND date<=@d ORDER BY date DESC LIMIT 1;";
                cmd.Parameters.AddWithValue("@t", ticker);
                cmd.Parameters.AddWithValue("@d", asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                var v = cmd.ExecuteScalar();
                return v is null or DBNull ? null : Convert.ToDouble(v, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Most recent close date across the whole store — the "as of" a weekly build
        /// stamps its pages with. Null on an empty store.</summary>
        public DateTime? LatestDate()
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT MAX(date) FROM daily;";
                var v = cmd.ExecuteScalar();
                return v is string s
                    ? DateTime.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : null;
            }
        }

        /// <summary>Latest stored close date for a ticker, or null when the ticker is unseeded.</summary>
        public DateTime? LastDate(string ticker)
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT MAX(date) FROM daily WHERE ticker=@t;";
                cmd.Parameters.AddWithValue("@t", ticker);
                var v = cmd.ExecuteScalar();
                return v is string s
                    ? DateTime.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : null;
            }
        }

        public long RowCount() { lock (_gate) return Scalar("SELECT COUNT(*) FROM daily;"); }
        public long TickerCount() { lock (_gate) return Scalar("SELECT COUNT(DISTINCT ticker) FROM daily;"); }

        public void RecordRun(string kind, string? note = null)
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "INSERT INTO runs(asof,kind,note) VALUES(@a,@k,@n);";
                cmd.Parameters.AddWithValue("@a", DateTime.Now.ToString("s", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@k", kind);
                cmd.Parameters.AddWithValue("@n", (object?)note ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        private long Scalar(string sql)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }

        private void Exec(string sql)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public void Dispose() => _db.Dispose();
    }
}
