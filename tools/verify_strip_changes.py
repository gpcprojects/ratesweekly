"""SURE-cubed audit of the meeting-dated OIS 1w/1m changes the dashboards DISPLAY.

Two independent proofs, per the dodgeball verify_meetings.py discipline:

1) RAW-BDH RESTITCH — for every row of every rendered meeting panel, recompute the level and
   the 1w/1m changes from Bloomberg daily history directly (not the store), with an
   independently coded boundary shift (decision dates first, 14-day cluster, decision-day
   closes excluded). Any disagreement beyond display rounding is a fault.

2) FED FUNDS FUTURES CROSS-CHECK — FF futures are NON-ROLLING instruments that share nothing
   with the meeting-OIS machinery. The October implied rate is a day-weighted blend of the two
   FOMC periods that cover October, so its 1w change must reconcile with the blended 1w changes
   the page shows. Confirms the magnitudes are real, not artefacts of roll handling.

Run with a logged-in terminal AFTER a render:  python tools\\verify_strip_changes.py
"""
import datetime as dt
import io
import json
import os
import re
import sys

import blpapi

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(os.environ.get("APPDATA", ""), "RatesWeekly", "out")
CFG = os.path.join(REPO, "config", "meetings.json")
MAXN = 13
TOL_BP = 0.11          # two 1dp roundings meeting in the middle
TOL_LVL = 0.0006       # page shows 3dp %


def bbg_session():
    opts = blpapi.SessionOptions()
    opts.setServerHost("localhost")
    opts.setServerPort(8194)
    s = blpapi.Session(opts)
    if not s.start() or not s.openService("//blp/refdata"):
        raise SystemExit("no bloomberg session")
    return s


def bdh(session, tickers, days=70):
    """ticker -> {date: close}, daily PX_LAST."""
    ref = session.getService("//blp/refdata")
    out = {t: {} for t in tickers}
    # end YESTERDAY: with endDate=today Bloomberg returns today's live print as a "close",
    # while the store (correctly) holds settled closes only — everything skews a day
    start = (dt.date.today() - dt.timedelta(days=days)).strftime("%Y%m%d")
    end = (dt.date.today() - dt.timedelta(days=1)).strftime("%Y%m%d")
    for t in tickers:
        req = ref.createRequest("HistoricalDataRequest")
        req.getElement("securities").appendValue(t)
        req.getElement("fields").appendValue("PX_LAST")
        req.set("startDate", start)
        req.set("endDate", end)
        req.set("periodicitySelection", "DAILY")
        session.sendRequest(req)
        while True:
            ev = session.nextEvent(30000)
            for msg in ev:
                if not msg.hasElement("securityData"):
                    continue
                sd = msg.getElement("securityData")
                if not sd.hasElement("fieldData"):
                    continue
                fd = sd.getElement("fieldData")
                for i in range(fd.numValues()):
                    row = fd.getValueAsElement(i)
                    if row.hasElement("PX_LAST"):
                        d = row.getElementAsDatetime("date")
                        out[t][dt.date(d.year, d.month, d.day)] = row.getElementAsFloat("PX_LAST")
            if ev.eventType() in (blpapi.Event.RESPONSE, blpapi.Event.TIMEOUT):
                break
    return out


def load_runs():
    txt = re.sub(r"//[^\n]*", "", io.open(CFG, encoding="utf-8-sig").read())
    return [r for r in json.loads(txt)["runs"] if not r.get("kind")]


def cluster(dates):
    """ascending, keep-earliest 14-day clusters — mirrors RollingStrip"""
    out = []
    for d in sorted(set(dates)):
        if not out or (d - out[-1]).days > 14:
            out.append(d)
    return out


def d_iso(s):
    return dt.date.fromisoformat(s)


# ---------- page parsing ----------

ROW_RE = re.compile(
    r'<tr class="rw-row"[^>]*><td class="rw-lab">(\d{2}-\w{3}-\d{2})(\*?)</td>'
    r'<td class="rw-val">([\d.]+)</td>'
    r'<td class="rw-bp[^"]*">([+\-—][\d.]*)</td>'
    r'<td class="rw-bp[^"]*">([+\-—][\d.]*)</td></tr>')


def parse_page(ccy):
    path = os.path.join(OUT, ccy.lower() + ".html")
    if not os.path.exists(path):
        return {}
    html = io.open(path, encoding="utf-8").read()
    panels = {}
    for m in re.finditer(r'<section class="rw-panel" id="mtg-([a-z\-]+)"', html):
        name, start = m.group(1).upper(), m.start()
        end = html.find("</section>", start)
        rows = []
        for rm in ROW_RE.finditer(html, start, end):
            date = dt.datetime.strptime(rm.group(1), "%d-%b-%y").date()
            starred = rm.group(2) == "*"
            lvl = float(rm.group(3))
            def bp(s):
                return None if s.startswith("—") else float(s)
            rows.append((date, starred, lvl, bp(rm.group(4)), bp(rm.group(5))))
        panels[name] = rows
    return panels


# ---------- the audit ----------

def main():
    runs = load_runs()
    session = bbg_session()

    ok = skipped = 0
    problems, controls = [], []
    total_rows = 0
    runs_checked = 0
    ff_needed = {}          # FOMC parsed rows for the futures check

    for run in runs:
        name, ccy = run["name"], run["ccy"]
        pat = next((t for t in run["tickers"] if "{N}" in t), None)
        if pat is None:
            continue
        panels = parse_page(ccy)
        rows = panels.get(name.upper().replace(" ", "-"), panels.get(name.upper()))
        tickers = [pat.replace("{N}", str(n)) + " Curncy" for n in range(1, MAXN + 1)]
        hist = bdh(session, tickers)
        if not rows:
            # a family with no Bloomberg history legitimately renders no panel (SNB); a family
            # WITH history and no panel is a rendering fault
            if any(h for h in hist.values()):
                controls.append(f"{name}: BDH has data but no rendered rows on {ccy.lower()}.html")
            else:
                print(f"\n{name} ({ccy}) — no composite history, no panel: consistent, skipped")
            continue
        runs_checked += 1
        bounds = cluster([d_iso(x) for x in
                          run.get("decisionDates", []) + run.get("dates", []) + run.get("pastDates", [])])
        as_of = max((max(h) for h in hist.values() if h), default=None)
        if as_of is None:
            controls.append(f"{name}: BDH returned nothing")
            continue

        def value_at(contract, then):
            """independent boundary-shifted read, decision-day closes excluded"""
            for _ in range(6):
                if then in bounds:
                    then -= dt.timedelta(days=1)
                    continue
                idx = max(1, sum(1 for b in bounds if then < b <= contract))
                if idx > MAXN:
                    return None
                h = hist[pat.replace("{N}", str(idx)) + " Curncy"]
                dates = [d for d in h if d <= then]
                if not dates:
                    return None
                hit = max(dates)
                if hit in bounds:
                    then = hit - dt.timedelta(days=1)
                    continue
                return h[hit]
            return None

        print(f"\n{name} ({ccy}) — {len(rows)} rendered rows, asOf {as_of}")
        for (contract, starred, lvl, w1, m1) in rows:
            total_rows += 1
            if starred:
                skipped += 1
                print(f"   {contract}  guarded row — level is a neighbour midpoint, skipped")
                continue
            mine_now = value_at(contract, as_of)
            if mine_now is None:
                skipped += 1
                print(f"   {contract}  no independent read (family/history ends) — skipped")
                continue
            checks = [("level", lvl, mine_now, TOL_LVL)]
            for (label, shown, days) in (("1w", w1, 7), ("1m", m1, 31)):
                if shown is None:
                    continue
                then_v = value_at(contract, as_of - dt.timedelta(days=days))
                if then_v is None:
                    continue
                checks.append((label, shown, (mine_now - then_v) * 100.0, TOL_BP))
            bad = [(lbl, shown, mine) for (lbl, shown, mine, tol) in checks
                   if abs(shown - mine) > tol]
            if bad:
                for (lbl, shown, mine) in bad:
                    problems.append(f"{name} {contract} {lbl}: page {shown} vs independent {mine:.3f}")
                print(f"   {contract}  MISMATCH: " +
                      ", ".join(f"{lbl} page {s} vs {m:.2f}" for (lbl, s, m) in bad))
            else:
                ok += 1
                print(f"   {contract}  ok  ({len(checks)} figures)")
            if name == "FOMC":
                ff_needed[contract] = (w1, m1)

    # ---- Fed Funds futures cross-check (shares nothing with the OIS machinery) ----
    print("\n" + "=" * 88)
    fomc = sorted(ff_needed.items())
    if len(fomc) >= 2 and all(v[0] is not None for v in fomc[:2]):
        (c1, (w1a, _)), (c2, (w1b, _)) = fomc[0], fomc[1]
        # October 2026 sits across the first two FOMC periods
        oct_start, oct_end = dt.date(2026, 10, 1), dt.date(2026, 10, 31)
        days_p1 = (min(c2, oct_end + dt.timedelta(days=1)) - oct_start).days
        days_p2 = 31 - days_p1
        if 0 < days_p1 <= 31:
            ff = bdh(session, ["FFV6 Comdty"])["FFV6 Comdty"]
            if ff:
                as_of = max(ff)
                week_ago = max((d for d in ff if d <= as_of - dt.timedelta(days=7)), default=None)
                if week_ago:
                    dff = ((100 - ff[as_of]) - (100 - ff[week_ago])) * 100.0
                    blend = (days_p1 * w1a + days_p2 * w1b) / 31.0
                    verdict = "ok" if abs(dff - blend) <= 1.5 else "MISMATCH"
                    if verdict != "ok":
                        problems.append(f"FF futures: FFV6 1w {dff:+.1f}bp vs blended FOMC {blend:+.1f}bp")
                    print(f"FF futures cross-check: FFV6 implied 1w {dff:+.1f}bp vs "
                          f"day-weighted FOMC rows {blend:+.1f}bp  ({days_p1}/{days_p2} day split) — {verdict}")
    else:
        controls.append("FOMC rows unavailable for the futures cross-check")

    print("=" * 88)
    print(f"RESULT: {ok} rows fully reconciled, {skipped} skipped (guarded/unreadable), "
          f"{len(problems)} MISMATCHES, {runs_checked} runs")
    for p in problems:
        print("  " + p)
    if runs_checked < 8:
        controls.append(f"only {runs_checked} runs parsed (expected >=8)")
    if total_rows < 40:
        controls.append(f"only {total_rows} rows parsed (expected >=40)")
    if controls:
        print("POSITIVE CONTROL FAILED — audit proves nothing for:")
        for c in controls:
            print("  " + c)
        return 2
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
