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
            Exec("""
                CREATE TABLE IF NOT EXISTS daily(
                    ticker TEXT NOT NULL,
                    date   TEXT NOT NULL,   -- yyyy-MM-dd
                    value  REAL NOT NULL,
                    PRIMARY KEY(ticker, date)
                ) WITHOUT ROWID;
                """);
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
        }

        /// <summary>Deepest BDH window already fetched for a ticker; 0 when never seeded.
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
        public int UpsertDaily(string ticker, IEnumerable<HistPoint> points, bool excludeToday = true)
        {
            lock (_gate)
            {
                using var tx = _db.BeginTransaction();
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO daily(ticker,date,value) VALUES(@t,@d,@v) " +
                                  "ON CONFLICT(ticker,date) DO UPDATE SET value=excluded.value;";
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
