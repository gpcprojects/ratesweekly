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
            ff_needed[name] = [(c, l, w) for (c, s, l, w, m) in rows if not s]

    # ---- exchange-settled futures cross-check (shares nothing with the OIS machinery) ----
    # Generalised 2026-08-20 from the hardcoded FFV6/Oct-26 check to every run that carries
    # guardFutures in meetings.json (FF/IB month-average, SFI/COR IMM-quarter compounded — the
    # same families the in-app FuturesGuard watches). Two figures per family:
    #   level: futures-implied rate vs the day-weighted blend of the page LEVELS over the window
    #   1w:    futures close-to-close 1w change vs the day-weighted blend of the page 1w columns
    def third_wed(y, m):
        d = dt.date(y, m, 1)
        return d + dt.timedelta(days=(2 - d.weekday()) % 7 + 14)

    def fut_my(y, m):
        return "FGHJKMNQUVXZ"[m - 1] + str(y % 10)

    print("\n" + "=" * 88)
    guards_run = 0
    for run in load_runs():
        pat_g, name = run.get("guardFutures"), run["name"]
        if not pat_g:
            continue
        rows3 = ff_needed.get(name) or []
        if len(rows3) < 2:
            controls.append(f"{name}: guardFutures configured but no usable page rows")
            continue
        imm = run.get("guardFuturesKind", "monthavg") == "imm3m"
        today = dt.date.today()
        picked = None
        y, m = today.year, today.month
        for _ in range(12):
            m += 1
            if m > 12: y, m = y + 1, 1
            if imm and m % 3:
                continue
            a = third_wed(y, m) if imm else dt.date(y, m, 1)
            b = third_wed(y + (1 if m + 3 > 12 else 0), (m + 2) % 12 + 1) if imm \
                else (dt.date(y + 1, 1, 1) if m == 12 else dt.date(y, m + 1, 1))
            if a <= today or rows3[0][0] > a or rows3[-1][0] < b:
                continue
            picked = (a, b, pat_g.replace("{MY}", fut_my(y, m)))
            break
        if picked is None:
            controls.append(f"{name}: no covered future window for the guard")
            continue
        a, b, tk = picked
        h = bdh(session, [tk])[tk]
        if not h:
            controls.append(f"{name}: {tk} returned no history")
            continue
        f_asof = max(h)
        f_week = max((d for d in h if d <= f_asof - dt.timedelta(days=7)), default=None)

        def blend(sel):
            """day-weighted blend of sel(row) over [a, b) using the page's own periods"""
            total = wsum = 0.0
            d = a
            while d < b:
                covering = [r for r in rows3 if r[0] <= d]
                if not covering:
                    return None
                r = covering[-1]
                nxt = min([x[0] for x in rows3 if x[0] > d] + [b])
                v = sel(r)
                if v is None:
                    return None
                days = (nxt - d).days
                wsum += v * days
                total += days
                d = nxt
            return wsum / total

        lvl_blend = blend(lambda r: r[1])
        w1_blend = blend(lambda r: r[2])
        tol_lvl = float(run.get("guardFuturesTolBp", 8.0))
        parts, bad = [], []
        if lvl_blend is not None:
            gap = ((100.0 - h[f_asof]) - lvl_blend) * 100.0
            parts.append(f"level d{gap:+.1f}bp")
            if abs(gap) > tol_lvl:
                bad.append(f"level gap {gap:+.1f}bp > {tol_lvl}")
        if w1_blend is not None and f_week is not None:
            dfut = (h[f_week] - h[f_asof]) * 100.0     # price down = rate up
            gap = dfut - w1_blend
            parts.append(f"1w d{gap:+.1f}bp")
            # 2.5bp: two 0.1bp-rounded columns blended + futures-close vs 16:30-snap timing skew
            if abs(gap) > 2.5:
                bad.append(f"1w gap {gap:+.1f}bp > 2.5")
        window = f"{a}..{b}" if imm else a.strftime("%b-%y")
        if bad:
            problems.append(f"{name} futures guard {tk} {window}: " + "; ".join(bad))
        else:
            guards_run += 1
        print(f"{name} futures guard: {tk} {window} — {', '.join(parts) or 'nothing comparable'} — "
              f"{'MISMATCH' if bad else 'ok'}")
    if guards_run == 0:
        controls.append("no futures guard produced a verdict — cross-check proves nothing")

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
