using System;
using System.Collections.Generic;
using System.Linq;
using Bloomberglp.Blpapi;
using RateDesk.Core.Market;

namespace RateDesk.Bloomberg
{
    /// <summary>Async //blp/mktdata subscriber pushing ticks into the RatesSnapshot.</summary>
    public sealed class LiveSubscriber : IDisposable
    {
        private readonly RatesSnapshot _snap;
        private readonly Session _session;
        private readonly HashSet<string> _subscribed = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();

        public event Action<string>? StatusChanged;
        public bool Connected { get; private set; }

        public LiveSubscriber(RatesSnapshot snap, string host = "localhost", int port = 8194)
        {
            _snap = snap;
            var opts = new SessionOptions { ServerHost = host, ServerPort = port };
            _session = new Session(opts, ProcessEvent);
            if (!_session.Start())
                throw new InvalidOperationException("Cannot start Bloomberg market-data session");
            if (!_session.OpenService("//blp/mktdata"))
                throw new InvalidOperationException("Cannot open //blp/mktdata");
            Connected = true;
            StatusChanged?.Invoke("Connected to Bloomberg (live)");
        }

        /// <summary>Desktop API concurrent-subscription ceiling — refuse (loudly) rather than fail opaquely.</summary>
        private const int MaxSubscriptions = 3200;

        public void Subscribe(IEnumerable<string> tickers)
        {
            lock (_gate)
            {
                var fresh = tickers.Where(t => !_subscribed.Contains(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (fresh.Count == 0) return;
                if (_subscribed.Count + fresh.Count > MaxSubscriptions)
                {
                    int room = Math.Max(0, MaxSubscriptions - _subscribed.Count);
                    StatusChanged?.Invoke($"subscription cap: keeping {room}/{fresh.Count} new tickers ({MaxSubscriptions} limit)");
                    fresh = fresh.Take(room).ToList();
                    if (fresh.Count == 0) return;
                }
                var subs = fresh
                    .Select(t => new Subscription(t, "LAST_PRICE,BID,ASK", "", new CorrelationID(t)))
                    .ToList();
                _session.Subscribe(subs);
                foreach (var t in fresh) _subscribed.Add(t);
                StatusChanged?.Invoke($"Subscribed {fresh.Count} tickers ({_subscribed.Count} total)");
            }
        }

        /// <summary>Re-establish every subscription — a terminal logout TERMINATES active subs and
        /// they do not come back on their own when the session reports up again.</summary>
        public void ResubscribeAll()
        {
            lock (_gate)
            {
                if (_subscribed.Count == 0) return;
                var subs = _subscribed
                    .Select(t => new Subscription(t, "LAST_PRICE,BID,ASK", "", new CorrelationID(t)))
                    .ToList();
                try
                {
                    _session.Subscribe(subs);
                    StatusChanged?.Invoke($"re-subscribed {subs.Count} tickers after reconnect");
                }
                catch (Exception ex) { StatusChanged?.Invoke("resubscribe failed: " + ex.Message); }
            }
        }

        private int _terminated;

        private void ProcessEvent(Event ev, Session session)
        {
            try
            {
                switch (ev.Type)
                {
                    case Event.EventType.SUBSCRIPTION_DATA:
                        _terminated = 0; // data flowing = healthy; scattered terminations don't accumulate
                        if (!Connected)
                        {
                            Connected = true;
                            StatusChanged?.Invoke("Bloomberg data flowing again");
                        }
                        foreach (Message msg in ev)
                        {
                            var ticker = msg.CorrelationID?.Object as string;
                            if (ticker == null) continue;
                            double? bid = TryGet(msg, "BID");
                            double? ask = TryGet(msg, "ASK");
                            double? last = TryGet(msg, "LAST_PRICE");
                            if (bid.HasValue || ask.HasValue || last.HasValue)
                                _snap.Update(ticker, bid, ask, last);
                        }
                        break;
                    case Event.EventType.SUBSCRIPTION_STATUS:
                        // Terminal logout leaves the local session "up" but TERMINATES the active
                        // subscriptions in a burst — that is the feed-down signal. SubscriptionFailure
                        // (unknown/unentitled security at subscribe time) is NOT: probing speculative
                        // tickers (meeting numbers, future quarters) fails a handful every load.
                        foreach (Message msg in ev)
                        {
                            if (msg.MessageType.ToString() != "SubscriptionTerminated") continue;
                            if (++_terminated >= 8 && Connected)
                            {
                                Connected = false;
                                StatusChanged?.Invoke("Bloomberg subscriptions terminated (terminal logged out?)");
                            }
                        }
                        break;
                    case Event.EventType.SESSION_STATUS:
                        foreach (Message msg in ev)
                        {
                            var mt = msg.MessageType.ToString();
                            if (mt == "SessionTerminated" || mt == "SessionConnectionDown")
                            {
                                Connected = false;
                                StatusChanged?.Invoke("Bloomberg session DOWN");
                            }
                            else if (mt == "SessionConnectionUp")
                            {
                                Connected = true;
                                _terminated = 0;
                                StatusChanged?.Invoke("Bloomberg session up");
                            }
                        }
                        break;
                }
            }
            catch
            {
                // never let a tick kill the event pump
            }
        }

        private static double? TryGet(Message msg, string field)
        {
            try
            {
                if (!msg.HasElement(field)) return null;
                var el = msg.GetElement(field);
                if (el.IsNull) return null;
                return el.GetValueAsFloat64();
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            try { _session.Stop(); } catch { /* shutting down */ }
        }
    }
}
