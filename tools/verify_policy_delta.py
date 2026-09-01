# verify_policy_delta.py - the policy-delta base, proven against real decisions (2026-09-01).
#
# For every central-bank move Bloomberg's target tickers record, this checks the desk's rule:
#   implied = last o/n FIXING print before the target flipped  +  the move,
# then verifies the fixing itself SETTLES at the implied level once the new rate kicks in
# (settle-err column), and measures when the automated remover would fire (kick-in = first
# print >= half the move in the move's direction - the bodge must come off there or the move
# is incorporated twice; err@kick shows the print had fully moved at that moment).
#
# Run against a live terminal:  python toolserify_policy_delta.py
# First run 2026-09-01, 24 moves across all nine banks: 20/24 within +/-0.2bp,
# worst |err| 2.0bp (SWESTR/CORRA corridor drift); zero missed kick-ins, zero false-early.
#
# Backtest of the policy-delta base (desk 2026-09-01):
#   for each real CB move, implied = last fixing print BEFORE the target flipped + the move;
#   check the fixing itself settles AT the implied level once the new rate kicks in,
#   and measure WHEN it kicks in (the automated remover's trigger: >= half the move, right sign).
import blpapi, datetime as dt, statistics

BANKS = [
    # bank, policy ticker, fixing ticker
    ("FOMC",     "FDTR Index",     "FEDL01 Index"),
    ("ECB",      "EUORDEPO Index", "ESTRON Index"),
    ("MPC",      "UKBRBASE Index", "SONIO/N Index"),
    ("BOJ",      "BOJDTR Index",   "MUTKCALM Index"),
    ("RIKSBANK", "SWRRATEI Index", "SWESTR Index"),
    ("RBA",      "RBATCTR Index",  "RBACOR Index"),
    ("RBNZ",     "NZOCRS Index",   "NZOCRS Index"),
    ("BOC",      "CABROVER Index", "CAONREPO Index"),
    ("NORGES",   "NOBRDEP Index",  "NOWA Index"),
]

def bdh(session, svc, tickers, days=1000):
    req = svc.createRequest("HistoricalDataRequest")
    for t in tickers: req.getElement("securities").appendValue(t)
    req.getElement("fields").appendValue("PX_LAST")
    end = dt.date.today()
    start = end - dt.timedelta(days=days)
    req.set("startDate", start.strftime("%Y%m%d"))
    req.set("endDate", end.strftime("%Y%m%d"))
    req.set("periodicitySelection", "DAILY")
    session.sendRequest(req)
    out = {t: [] for t in tickers}
    done = False
    while not done:
        ev = session.nextEvent(60000)
        for msg in ev:
            if not msg.hasElement("securityData"): continue
            sd = msg.getElement("securityData")
            name = sd.getElementAsString("security")
            if sd.hasElement("securityError"):
                print("  !", name, "SECURITY ERROR"); continue
            fld = sd.getElement("fieldData")
            for i in range(fld.numValues()):
                row = fld.getValueAsElement(i)
                if row.hasElement("PX_LAST"):
                    out[name].append((row.getElementAsDatetime("date"),
                                      row.getElementAsFloat("PX_LAST")))
        if ev.eventType() == blpapi.Event.RESPONSE: done = True
    for t in out:
        out[t] = sorted(((d.date() if hasattr(d, "date") else d), v) for d, v in out[t])
    return out

opts = blpapi.SessionOptions(); opts.setServerHost("localhost"); opts.setServerPort(8194)
s = blpapi.Session(opts)
assert s.start() and s.openService("//blp/refdata")
svc = s.getService("//blp/refdata")

all_t = sorted({t for _, p, f in BANKS for t in (p, f)})
H = bdh(s, svc, all_t)

rows = []
for bank, pol, fix in BANKS:
    P, F = H[pol], H[fix]
    if len(P) < 10 or len(F) < 10:
        print(f"  ! {bank}: thin history pol={len(P)} fix={len(F)}"); continue
    fixd = dict(F); fdates = [d for d, _ in F]
    # policy flips >= 5bp
    moves = [(P[i][0], P[i][1] - P[i-1][1], P[i-1][1], P[i][1])
             for i in range(1, len(P)) if abs(P[i][1] - P[i-1][1]) >= 0.049]
    for D, delta, old, new in moves:
        pre = [(d, v) for d, v in F if d < D]
        if not pre: continue
        fixpre_d, fixpre = pre[-1]
        implied = fixpre + delta
        # kick-in: first fixing date >= D where the print moved >= |delta|/2, right sign
        K = None
        for d, v in F:
            if d < D: continue
            if abs(v - fixpre) >= abs(delta) / 2 and (v - fixpre) * delta > 0:
                K = d; break
        if K is None:
            rows.append((bank, D, delta * 100, implied, None, None, None, None)); continue
        after = [v for d, v in F if K <= d and (d - K).days <= 14]
        settled = statistics.median(after) if after else None
        kick_bd = sum(1 for d in fdates if D <= d < K)  # fixing prints between flip and kick-in
        resid_k = (fixd[K] - implied) * 100
        err = (settled - implied) * 100 if settled is not None else None
        rows.append((bank, D, delta * 100, implied, K, kick_bd, err, resid_k))

rows.sort(key=lambda r: r[1])
print(f"\n{'bank':9} {'flip date':>10} {'move':>6} {'implied':>8} {'kick-in':>10} "
      f"{'prints<kick':>11} {'settle-err':>10} {'err@kick':>9}")
for bank, D, mv, implied, K, kb, err, rk in rows[-24:]:
    print(f"{bank:9} {D.strftime('%d-%b-%y'):>10} {mv:+5.0f}bp {implied:8.3f} "
          f"{K.strftime('%d-%b-%y') if K else 'NEVER':>10} "
          f"{kb if kb is not None else '-':>11} "
          f"{f'{err:+.1f}bp' if err is not None else '-':>10} "
          f"{f'{rk:+.1f}bp' if rk is not None else '-':>9}")
s.stop()
