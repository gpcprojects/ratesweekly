using Bloomberglp.Blpapi;

// Prints PX_LAST/BID/ASK (+ SECURITY_DES) for tickers given as args — quick instrument probe.
var tickers = args.Length > 0 ? args : new[] { "USOSFR5 Curncy" };

var opts = new SessionOptions { ServerHost = "localhost", ServerPort = 8194 };
var session = new Session(opts);
if (!session.Start()) { Console.WriteLine("no session — terminal running?"); return 1; }
if (!session.OpenService("//blp/refdata")) { Console.WriteLine("FAIL: open refdata"); return 1; }
var svc = session.GetService("//blp/refdata");

var req = svc.CreateRequest("ReferenceDataRequest");
foreach (var t in tickers) req.GetElement("securities").AppendValue(t);
foreach (var f in new[] { "PX_LAST", "PX_BID", "PX_ASK", "PX_MID", "SECURITY_DES", "NAME", "MATURITY" })
    req.GetElement("fields").AppendValue(f);
session.SendRequest(req, new CorrelationID(1));

bool done = false;
while (!done)
{
    Event ev = session.NextEvent(60000);
    foreach (Message msg in ev)
    {
        if (msg.MessageType.ToString() != "ReferenceDataResponse") continue;
        var sd = msg.GetElement("securityData");
        for (int i = 0; i < sd.NumValues; i++)
        {
            var s = sd.GetValueAsElement(i);
            var name = s.GetElementAsString("security");
            if (s.HasElement("securityError")) { Console.WriteLine($"{name,-24} UNKNOWN"); continue; }
            var fd = s.GetElement("fieldData");
            string G(string f) => fd.HasElement(f) ? fd.GetElement(f).GetValueAsString() : "-";
            Console.WriteLine($"{name,-24} last {G("PX_LAST"),-10} bid {G("PX_BID"),-10} ask {G("PX_ASK"),-8} pxmid {G("PX_MID"),-9} mat {G("MATURITY"),-11} | {G("NAME")}");
        }
    }
    if (ev.Type == Event.EventType.RESPONSE) done = true;
    if (ev.Type == Event.EventType.TIMEOUT) { Console.WriteLine("TIMEOUT"); return 1; }
}
session.Stop();
return 0;
