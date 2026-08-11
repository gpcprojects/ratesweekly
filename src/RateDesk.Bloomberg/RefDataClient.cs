using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Bloomberglp.Blpapi;
using RateDesk.Core.Market;

namespace RateDesk.Bloomberg
{
    public sealed class TickerStatus
    {
        public string Ticker { get; init; } = "";
        public bool Exists { get; init; }
        public bool HasPrice { get; init; }
        public double? Last { get; init; }
        public double? Bid { get; init; }
        public double? Ask { get; init; }
    }

    /// <summary>Synchronous //blp/refdata client: startup snapshots, ticker validation, and
    /// historical daily series (BDH). Implements IHistoryProvider with a session cache.</summary>
    public sealed class RefDataClient : IDisposable, IHistoryProvider
    {
        private readonly Session _session;
        private readonly Service _service;
        private readonly object _lock = new();
        // day-stamped AND window-stamped: a 10y request must not be served from a 5y cache entry
        private readonly ConcurrentDictionary<string, (DateTime day, int days, HistPoint[] data)> _histCache = new(StringComparer.OrdinalIgnoreCase);
        private long _corr;

        /// <summary>Optional per-request diagnostics (request kind, size, elapsed) — invaluable
        /// when the terminal throttles the API with a flat delay per request.</summary>
        public static Action<string>? Trace;

        /// <summary>Every request is tagged so a TIMEOUT-abandoned response can't poison the next
        /// request's receive loop (messages from stale correlations are skipped).</summary>
        private CorrelationID NextCorr() => new(System.Threading.Interlocked.Increment(ref _corr));

        private bool Matches(Message msg, CorrelationID corr) =>
            msg.CorrelationID != null && msg.CorrelationID.Value == corr.Value;

        public RefDataClient(string host = "localhost", int port = 8194)
        {
            var opts = new SessionOptions { ServerHost = host, ServerPort = port };
            _session = new Session(opts);
            if (!_session.Start())
                throw new InvalidOperationException("Cannot start Bloomberg session - is the terminal running and logged in?");
            if (!_session.OpenService("//blp/refdata"))
                throw new InvalidOperationException("Cannot open //blp/refdata");
            _service = _session.GetService("//blp/refdata");
        }

        public List<TickerStatus> Snapshot(IEnumerable<string> tickers, RatesSnapshot snap)
        {
                var results = new List<TickerStatus>();
                var list = tickers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                // the terminal can throttle with a fixed ~20s delay PER REQUEST (daily API limit),
                // so fewer, bigger requests matter far more than per-security throughput.
                // 400 = the documented DAPI hard cap per ReferenceDataRequest.
                const int chunkSize = 400;
                for (int i = 0; i < list.Count; i += chunkSize)
                {
                    // lock per chunk, not per call: a big background warm must not block an
                    // interactive query for more than one request
                    lock (_lock)
                    {
                    var chunk = list.Skip(i).Take(chunkSize).ToList();
                    var swReq = System.Diagnostics.Stopwatch.StartNew();
                    var req = _service.CreateRequest("ReferenceDataRequest");
                    foreach (var t in chunk) req.GetElement("securities").AppendValue(t);
                    // LAST_UPDATE_DT/LAST_UPDATE ride along in the same round-trip: they are what tells
                    // a frozen market apart from a live one, since our own receive time cannot
                    // SW_EFF_DT rides along for the same reason MATURITY does: a meeting-dated OIS
                    // publishes the START of the period it quotes, and only the BOJ's differs from
                    // the previous rung's maturity. (The mnemonic is SW_EFF_DT — SW_EFFECTIVE_DT
                    // returns empty on these securities.)
                    foreach (var f in new[] { "PX_LAST", "PX_BID", "PX_ASK", "PX_CLOSE_1D", "MATURITY",
                                              "SW_EFF_DT", "LAST_UPDATE_DT", "LAST_UPDATE" })
                        req.GetElement("fields").AppendValue(f);
                    var corr = NextCorr();
                    _session.SendRequest(req, corr);

                    bool done = false;
                    while (!done)
                    {
                        Event ev = _session.NextEvent(30000);
                        foreach (Message msg in ev)
                        {
                            if (!Matches(msg, corr)) continue;
                            if (msg.MessageType.ToString() != "ReferenceDataResponse") continue;
                            if (!msg.HasElement("securityData"))
                            {
                                Trace?.Invoke("refdata responseError — chunk skipped");
                                continue;
                            }
                            var sd = msg.GetElement("securityData");
                            for (int j = 0; j < sd.NumValues; j++)
                            {
                                var s = sd.GetValueAsElement(j);
                                var ticker = s.GetElementAsString("security");
                                if (s.HasElement("securityError"))
                                {
                                    results.Add(new TickerStatus { Ticker = ticker, Exists = false });
                                    continue;
                                }
                                var fd = s.GetElement("fieldData");
                                double? last = TryGet(fd, "PX_LAST");
                                double? bid = TryGet(fd, "PX_BID");
                                double? ask = TryGet(fd, "PX_ASK");
                                double? prev = TryGet(fd, "PX_CLOSE_1D");
                                bool has = last.HasValue || bid.HasValue || ask.HasValue;
                                if (has) snap.Update(ticker, bid, ask, last);
                                if (has && prev.HasValue) snap.SetPrevClose(ticker, prev.Value);
                                // maturity is recorded even without a price (e.g. SNB meeting-dated
                                // OIS publishes the meeting calendar but no quote)
                                try
                                {
                                    if (fd.HasElement("MATURITY")
                                        && DateTime.TryParse(fd.GetElementAsString("MATURITY"), out var mat))
                                        snap.SetMaturity(ticker, mat);
                                    if (fd.HasElement("SW_EFF_DT")
                                        && DateTime.TryParse(fd.GetElementAsString("SW_EFF_DT"), out var eff))
                                        snap.SetEffective(ticker, eff);
                                }
                                catch { /* not a dated instrument */ }
                                if (has && QuoteAgeMinutes(fd) is double ageMin)
                                    snap.SetAgeMinutes(ticker, ageMin);
                                results.Add(new TickerStatus
                                {
                                    Ticker = ticker, Exists = true, HasPrice = has,
                                    Last = last, Bid = bid, Ask = ask,
                                });
                            }
                        }
                        if (ev.Type == Event.EventType.RESPONSE) done = true;
                        if (ev.Type == Event.EventType.TIMEOUT) throw new TimeoutException("Bloomberg refdata timeout");
                    }
                    Trace?.Invoke($"refdata snapshot: {chunk.Count} secs in {swReq.ElapsedMilliseconds:N0} ms");
                    }
                }
                return results;
        }

        /// <summary>Probe which contributor sources return a live price for a base ticker root
        /// (e.g. "USOSFR5"). Returns the mnemonics that priced, in candidate order ("" = composite).</summary>
        public List<string> DiscoverSources(string baseRoot, IEnumerable<string> candidates)
        {
            var cand = candidates.ToList();
            var full2src = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var src in cand)
            {
                var full = string.IsNullOrEmpty(src) ? $"{baseRoot} Curncy" : $"{baseRoot} {src} Curncy";
                full2src[full] = src;
            }
            var livePx = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_lock)
            {
                var req = _service.CreateRequest("ReferenceDataRequest");
                foreach (var f in full2src.Keys) req.GetElement("securities").AppendValue(f);
                foreach (var fld in new[] { "PX_LAST", "PX_BID", "PX_ASK" }) req.GetElement("fields").AppendValue(fld);
                var corr = NextCorr();
                    _session.SendRequest(req, corr);
                bool done = false;
                while (!done)
                {
                    Event ev = _session.NextEvent(30000);
                    foreach (Message msg in ev)
                    {
                        if (!Matches(msg, corr)) continue;
                        if (msg.MessageType.ToString() != "ReferenceDataResponse") continue;
                        var sd = msg.GetElement("securityData");
                        for (int j = 0; j < sd.NumValues; j++)
                        {
                            var s = sd.GetValueAsElement(j);
                            if (s.HasElement("securityError")) continue;
                            var fd = s.GetElement("fieldData");
                            if (TryGet(fd, "PX_LAST").HasValue || TryGet(fd, "PX_BID").HasValue || TryGet(fd, "PX_ASK").HasValue)
                                livePx.Add(s.GetElementAsString("security"));
                        }
                    }
                    if (ev.Type == Event.EventType.RESPONSE) done = true;
                    if (ev.Type == Event.EventType.TIMEOUT) break;
                }
            }
            var result = new List<string>();
            foreach (var src in cand)
            {
                var full = string.IsNullOrEmpty(src) ? $"{baseRoot} Curncy" : $"{baseRoot} {src} Curncy";
                if (livePx.Contains(full)) result.Add(src);
            }
            return result;
        }

        /// <summary>Like DiscoverSources but also returns each priced source's last-update age in
        /// minutes (null when Bloomberg gives no usable timestamp). For "is this feed stale?" tags.</summary>
        public List<(string Src, double? AgeMinutes)> DiscoverSourcesWithAge(string baseRoot, IEnumerable<string> candidates)
        {
            var cand = candidates.ToList();
            var full2src = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var src in cand)
                full2src[string.IsNullOrEmpty(src) ? $"{baseRoot} Curncy" : $"{baseRoot} {src} Curncy"] = src;
            var live = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
            lock (_lock)
            {
                var req = _service.CreateRequest("ReferenceDataRequest");
                foreach (var f in full2src.Keys) req.GetElement("securities").AppendValue(f);
                foreach (var fld in new[] { "PX_LAST", "PX_BID", "PX_ASK", "LAST_UPDATE_DT", "LAST_UPDATE" })
                    req.GetElement("fields").AppendValue(fld);
                var corr = NextCorr();
                _session.SendRequest(req, corr);
                bool done = false;
                while (!done)
                {
                    Event ev = _session.NextEvent(30000);
                    foreach (Message msg in ev)
                    {
                        if (!Matches(msg, corr)) continue;
                        if (msg.MessageType.ToString() != "ReferenceDataResponse") continue;
                        var sd = msg.GetElement("securityData");
                        for (int j = 0; j < sd.NumValues; j++)
                        {
                            var s = sd.GetValueAsElement(j);
                            if (s.HasElement("securityError")) continue;
                            var fd = s.GetElement("fieldData");
                            if (!(TryGet(fd, "PX_LAST").HasValue || TryGet(fd, "PX_BID").HasValue || TryGet(fd, "PX_ASK").HasValue))
                                continue;
                            live[s.GetElementAsString("security")] = QuoteAgeMinutes(fd);
                        }
                    }
                    if (ev.Type == Event.EventType.RESPONSE) done = true;
                    if (ev.Type == Event.EventType.TIMEOUT) break;
                }
            }
            var result = new List<(string, double?)>();
            foreach (var src in cand)
            {
                var full = string.IsNullOrEmpty(src) ? $"{baseRoot} Curncy" : $"{baseRoot} {src} Curncy";
                if (live.TryGetValue(full, out var age)) result.Add((src, age));
            }
            return result;
        }

        // ---------- IHistoryProvider (BDH) ----------

        public IReadOnlyList<HistPoint> GetDaily(string ticker, int lookbackDays)
        {
            // day-stamped: an app left running overnight refreshes histories on the new day;
            // window-stamped: only serve from cache when the cached fetch covered the request
            if (_histCache.TryGetValue(ticker, out var cached) && cached.day == DateTime.Today
                && cached.days >= lookbackDays)
                return cached.data;
            try
            {
                var data = FetchDaily(ticker, lookbackDays);
                _histCache[ticker] = (DateTime.Today, lookbackDays, data);
                return data;
            }
            catch
            {
                // transient failure: leave uncached so the next call retries
                return Array.Empty<HistPoint>();
            }
        }

        /// <summary>Batched BDH: one HistoricalDataRequest carries many securities (each PARTIAL
        /// response holds one security's data). Turns N sequential round-trips into N/20 — this is
        /// what makes the first analyze and the RV scans fast.</summary>
        public void Prefetch(IEnumerable<string> tickers, int lookbackDays)
        {
            var need = tickers.Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(t => !_histCache.TryGetValue(t, out var c) || c.day != DateTime.Today
                            || c.days < lookbackDays)
                .ToList();
            if (need.Count == 0) return;
                var end = DateTime.Today;
                var start = end.AddDays(-Math.Max(30, lookbackDays));
                // throttled terminals penalise each REQUEST (~20s flat), so pack as many series
                // as the ~24k-point response budget allows: 6m lookback → ~100/request,
                // 5y RV scans → 20/request (the proven envelope).
                int pointsPerSeries = Math.Max(1, (int)(Math.Max(30, lookbackDays) * 5.0 / 7.0));
                int chunk = Math.Clamp(24000 / pointsPerSeries, 20, 100);
                for (int i = 0; i < need.Count; i += chunk)
                {
                    // lock per batch (see Snapshot) so long warms interleave with live queries
                    lock (_lock)
                    {
                    var batch = need.Skip(i).Take(chunk).ToList();
                    var swReq = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var req = _service.CreateRequest("HistoricalDataRequest");
                        foreach (var t in batch) req.GetElement("securities").AppendValue(t);
                        req.GetElement("fields").AppendValue("PX_LAST");
                        req.Set("periodicitySelection", "DAILY");
                        req.Set("periodicityAdjustment", "ACTUAL");
                        req.Set("nonTradingDayFillOption", "ACTIVE_DAYS_ONLY");
                        req.Set("startDate", start.ToString("yyyyMMdd"));
                        req.Set("endDate", end.ToString("yyyyMMdd"));
                        req.Set("maxDataPoints", 5000);
                        var corr = NextCorr();
                    _session.SendRequest(req, corr);

                        bool done = false;
                        while (!done)
                        {
                            Event ev = _session.NextEvent(45000);
                            foreach (Message msg in ev)
                            {
                                if (!Matches(msg, corr)) continue;
                                if (msg.MessageType.ToString() != "HistoricalDataResponse") continue;
                                if (!msg.HasElement("securityData")) continue;
                                var sd = msg.GetElement("securityData");
                                string name = sd.GetElementAsString("security");
                                if (sd.HasElement("securityError"))
                                {
                                    _histCache[name] = (DateTime.Today, lookbackDays, Array.Empty<HistPoint>());
                                    continue;
                                }
                                var pts = new List<HistPoint>();
                                if (sd.HasElement("fieldData"))
                                {
                                    var fda = sd.GetElement("fieldData");
                                    for (int j = 0; j < fda.NumValues; j++)
                                    {
                                        var row = fda.GetValueAsElement(j);
                                        if (!row.HasElement("date") || !row.HasElement("PX_LAST")) continue;
                                        var d = row.GetElementAsDatetime("date");
                                        pts.Add(new HistPoint(new DateTime(d.Year, d.Month, d.DayOfMonth),
                                            row.GetElementAsFloat64("PX_LAST")));
                                    }
                                }
                                pts.Sort((a, b) => a.Date.CompareTo(b.Date));
                                _histCache[name] = (DateTime.Today, lookbackDays, pts.ToArray());
                            }
                            if (ev.Type == Event.EventType.RESPONSE) done = true;
                            if (ev.Type == Event.EventType.TIMEOUT) break; // uncached tickers retry singly later
                        }
                    }
                    catch { /* uncached tickers fall back to per-ticker GetDaily */ }
                    Trace?.Invoke($"bdh prefetch: {batch.Count} secs x {Math.Max(30, lookbackDays)}d in {swReq.ElapsedMilliseconds:N0} ms");
                    }
                }
        }

        private HistPoint[] FetchDaily(string ticker, int lookbackDays)
        {
            lock (_lock)
            {
                var swReq = System.Diagnostics.Stopwatch.StartNew();
                var end = DateTime.Today;
                var start = end.AddDays(-Math.Max(30, lookbackDays));
                var req = _service.CreateRequest("HistoricalDataRequest");
                req.GetElement("securities").AppendValue(ticker);
                req.GetElement("fields").AppendValue("PX_LAST");
                req.Set("periodicitySelection", "DAILY");
                req.Set("periodicityAdjustment", "ACTUAL");
                req.Set("nonTradingDayFillOption", "ACTIVE_DAYS_ONLY");
                req.Set("startDate", start.ToString("yyyyMMdd"));
                req.Set("endDate", end.ToString("yyyyMMdd"));
                req.Set("maxDataPoints", 5000);
                var corr = NextCorr();
                    _session.SendRequest(req, corr);

                var pts = new List<HistPoint>();
                bool done = false;
                while (!done)
                {
                    Event ev = _session.NextEvent(30000);
                    foreach (Message msg in ev)
                    {
                        if (!Matches(msg, corr)) continue;
                        if (msg.MessageType.ToString() != "HistoricalDataResponse") continue;
                        if (!msg.HasElement("securityData")) continue;
                        var sd = msg.GetElement("securityData");
                        if (sd.HasElement("securityError")) { done = true; break; }
                        if (!sd.HasElement("fieldData")) continue;
                        var fda = sd.GetElement("fieldData");
                        for (int i = 0; i < fda.NumValues; i++)
                        {
                            var row = fda.GetValueAsElement(i);
                            if (!row.HasElement("date") || !row.HasElement("PX_LAST")) continue;
                            var d = row.GetElementAsDatetime("date");
                            double v = row.GetElementAsFloat64("PX_LAST");
                            pts.Add(new HistPoint(new DateTime(d.Year, d.Month, d.DayOfMonth), v));
                        }
                    }
                    if (ev.Type == Event.EventType.RESPONSE) done = true;
                    if (ev.Type == Event.EventType.TIMEOUT)
                        throw new TimeoutException("BDH timeout — partial history discarded");
                }
                pts.Sort((a, b) => a.Date.CompareTo(b.Date));
                Trace?.Invoke($"bdh single: {ticker} in {swReq.ElapsedMilliseconds:N0} ms");
                return pts.ToArray();
            }
        }

        // ---------- fixed-London-time snaps (IntradayBarRequest) ----------

        private readonly ConcurrentDictionary<string, (DateTime day, TimeSpan tod, HistPoint[] data)> _snapCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeZoneInfo LondonTz = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");

        /// <summary>Per-day value at a fixed LONDON wall-clock time, from 30-min TRADE bars (one
        /// IntradayBarRequest per ticker — the API takes a single security per bar request). For
        /// each London day the last bar ENDING at or before the snap time supplies the value, so
        /// a 16:30 snap is the 16:00-16:30 bar's close. DST is handled per-day by real tz
        /// conversion. Intraday depth is ~140 trading days — plenty for 1w/1m lookbacks.</summary>
        public IReadOnlyList<HistPoint> GetLondonSnaps(string ticker, int lookbackDays, TimeSpan londonTimeOfDay)
        {
            if (_snapCache.TryGetValue(ticker, out var c) && c.day == DateTime.Today && c.tod == londonTimeOfDay)
                return c.data;
            const int barMin = 30;
            var byDay = new Dictionary<DateTime, (DateTime end, double close)>();
            try
            {
                lock (_lock)
                {
                    var swReq = System.Diagnostics.Stopwatch.StartNew();
                    var req = _service.CreateRequest("IntradayBarRequest");
                    req.Set("security", ticker);
                    req.Set("eventType", "TRADE");
                    req.Set("interval", barMin);
                    var endUtc = DateTime.UtcNow;
                    var startUtc = endUtc.AddDays(-lookbackDays);
                    req.Set("startDateTime", new Datetime(startUtc));
                    req.Set("endDateTime", new Datetime(endUtc));
                    var corr = NextCorr();
                    _session.SendRequest(req, corr);
                    bool done = false;
                    while (!done)
                    {
                        Event ev = _session.NextEvent(45000);
                        foreach (Message msg in ev)
                        {
                            if (!Matches(msg, corr)) continue;
                            if (!msg.HasElement("barData")) continue;
                            var bd = msg.GetElement("barData");
                            if (!bd.HasElement("barTickData")) continue;
                            var bars = bd.GetElement("barTickData");
                            for (int i = 0; i < bars.NumValues; i++)
                            {
                                var b = bars.GetValueAsElement(i);
                                var t = b.GetElementAsDatetime("time"); // bar START, UTC
                                var barEndUtc = new DateTime(t.Year, t.Month, t.DayOfMonth,
                                    t.Hour, t.Minute, 0, DateTimeKind.Utc).AddMinutes(barMin);
                                var lon = TimeZoneInfo.ConvertTimeFromUtc(barEndUtc, LondonTz);
                                if (lon.TimeOfDay > londonTimeOfDay) continue;
                                double close = b.GetElementAsFloat64("close");
                                if (!byDay.TryGetValue(lon.Date, out var cur) || barEndUtc > cur.end)
                                    byDay[lon.Date] = (barEndUtc, close);
                            }
                        }
                        if (ev.Type == Event.EventType.RESPONSE) done = true;
                        if (ev.Type == Event.EventType.TIMEOUT) throw new TimeoutException("intraday bar timeout");
                    }
                    Trace?.Invoke($"bars snap: {ticker} {byDay.Count}d in {swReq.ElapsedMilliseconds:N0} ms");
                }
            }
            catch
            {
                return Array.Empty<HistPoint>(); // uncached: next call retries; callers use closes meanwhile
            }
            var pts = byDay.OrderBy(kv => kv.Key)
                .Select(kv => new HistPoint(kv.Key, kv.Value.close)).ToArray();
            _snapCache[ticker] = (DateTime.Today, londonTimeOfDay, pts);
            return pts;
        }

        private static double? TryGet(Element fieldData, string field)
        {
            try
            {
                if (!fieldData.HasElement(field)) return null;
                return fieldData.GetElementAsFloat64(field);
            }
            catch { return null; }
        }

        /// <summary>Minutes since the quote's own LAST_UPDATE_DT + LAST_UPDATE, per Bloomberg. Null when
        /// the fields aren't published. Shared by source discovery and the staleness flagging.</summary>
        private static double? QuoteAgeMinutes(Element fieldData)
        {
            try
            {
                string d = fieldData.HasElement("LAST_UPDATE_DT")
                    ? fieldData.GetElement("LAST_UPDATE_DT").GetValueAsString() : "";
                string t = fieldData.HasElement("LAST_UPDATE")
                    ? fieldData.GetElement("LAST_UPDATE").GetValueAsString() : "";
                if (d.Length > 0 && DateTime.TryParse(d + (t.Length > 0 ? " " + t : ""),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal, out var ts))
                    return Math.Max(0, (DateTime.Now - ts).TotalMinutes);
            }
            catch { /* age is best-effort */ }
            return null;
        }

        public void Dispose()
        {
            try { _session.Stop(); } catch { /* shutting down */ }
        }
    }
}
