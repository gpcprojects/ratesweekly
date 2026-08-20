"""Audit every OIS date the RENDERED desk email lists against Bloomberg's SW_EFF_DT.

RatesWeekly counterpart of dodgeball's audit_start_dates.py (same fault class: a row labelled
with a DERIVED date rather than the start of the period its quote covers; same discipline:
rungs alias-guarded by strictly-increasing MATURITY, rows with no quoted rung reported
UNVERIFIABLE never passed, and positive controls so a section this script fails to parse cannot
silently pass). It reads the email fragment the app persisted — the exact bytes COPY EMAIL puts
on the clipboard — so it verifies what the desk actually sees:

  1) every StartDate row in the 9 per-bank meeting cards,
  2) the CB front table's Start Date == rung 1's SW_EFF_DT,
  3) front Decision Date sanity: present, not after the start, within 10 days of it.

Run with a logged-in terminal, after the app (or `RatesWeeklyCli email`) has built the email:
  python tools\\audit_email_dates.py [path\\to\\email.html]
"""
import datetime
import io
import json
import os
import re
import sys

import blpapi

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CFG = os.path.join(REPO, "config", "meetings.json")
DEFAULT_EMAIL = os.path.join(
    os.environ.get("APPDATA", ""), "RatesWeekly", "out", "email.html")
MAXN = 14
FIELDS = ["SECURITY_DES", "SW_EFF_DT", "MATURITY", "PX_LAST"]


def bbg(tickers):
    opts = blpapi.SessionOptions()
    opts.setServerHost("localhost")
    opts.setServerPort(8194)
    s = blpapi.Session(opts)
    if not s.start() or not s.openService("//blp/refdata"):
        raise SystemExit("no bloomberg session")
    ref = s.getService("//blp/refdata")
    out = {}
    for i in range(0, len(tickers), 25):
        req = ref.createRequest("ReferenceDataRequest")
        for t in tickers[i:i + 25]:
            req.getElement("securities").appendValue(t)
        for f in FIELDS:
            req.getElement("fields").appendValue(f)
        s.sendRequest(req)
        while True:
            ev = s.nextEvent(30000)
            for msg in ev:
                if not msg.hasElement("securityData"):
                    continue
                sd = msg.getElement("securityData")
                for j in range(sd.numValues()):
                    e = sd.getValueAsElement(j)
                    tk = e.getElementAsString("security")
                    if e.hasElement("securityError"):
                        out[tk] = None
                        continue
                    fd = e.getElement("fieldData")

                    def g(f):
                        try:
                            return fd.getElementAsString(f) if fd.hasElement(f) else ""
                        except Exception:
                            return ""
                    out[tk] = {f: g(f) for f in FIELDS}
            if ev.eventType() in (blpapi.Event.RESPONSE, blpapi.Event.TIMEOUT):
                break
    s.stop()
    return out


def load_meeting_runs():
    txt = re.sub(r"//[^\n]*", "", io.open(CFG, encoding="utf-8-sig").read())
    return [r for r in json.loads(txt)["runs"] if not r.get("kind")]


def d_iso(s):
    return datetime.date.fromisoformat(s) if s else None


def london_now():
    try:
        from zoneinfo import ZoneInfo
        return datetime.datetime.now(ZoneInfo("Europe/London"))
    except Exception:
        return datetime.datetime.now()   # the desk machines run on London time anyway


def announced(dec, time_s, now_ldn):
    """Independent copy of the app's DecisionClock.Announced: out from the configured London
    time on the decision day, from the NEXT day when no time is on file."""
    if now_ldn.date() > dec:
        return True
    if now_ldn.date() < dec:
        return False
    try:
        hh, mm = (int(x) for x in time_s.split(":"))
    except Exception:
        return False
    return (now_ldn.hour, now_ldn.minute) >= (hh, mm)


def decision_for(decisions, start):
    """The decision that belongs to the period starting `start` (settlement lag <= 10d)."""
    c = [d for d in decisions if d and d <= start and (start - d).days <= 10]
    return max(c) if c else None


def d_email(s):
    return datetime.datetime.strptime(s.strip(), "%d-%b-%y").date()


# ---------- rendered email ----------

TITLE_RE = re.compile(r'font-size:12\.5px;[^"]*">([^<]+?)(?: <span|</div>)')
DATE_TD_RE = re.compile(r">(\d{2}-[A-Za-z]{3}-\d{2})(?:\s*\*)?</td>")
FRONT_ROW_RE = re.compile(
    r"<b>([A-Z][\w-]*)</b>\s*<span[^>]*>(?:<a [^>]*>)?([A-Z]{3})(?:</a>)?</span></td>"
    r"<td[^>]*>([^<]+)</td><td[^>]*>([^<]+)</td>")


def parse_email(path):
    html = io.open(path, encoding="utf-8").read()
    i_front = html.find("CB Front Meeting Market Pricing")
    i_meet = html.find("Central Bank OIS Meetings")
    i_fwd = html.find("Forward Rates Summary")
    if min(i_front, i_meet, i_fwd) < 0 or not (i_front < i_meet < i_fwd):
        raise SystemExit("email.html does not contain the three sections in order — "
                         "wrong file or renderer changed; audit proves nothing")

    front = []
    for m in FRONT_ROW_RE.finditer(html, i_front, i_meet):
        bank, ccy, dec_txt, start_txt = m.groups()
        starred = "*" in dec_txt
        front.append((bank, ccy,
                      None if starred else d_email(dec_txt),
                      d_email(start_txt.replace("*", ""))))

    meet_html = html[i_meet:i_fwd]
    titles = [(m.start(), m.group(1).strip()) for m in TITLE_RE.finditer(meet_html)]
    cards = {}
    for k, (pos, title) in enumerate(titles):
        end = titles[k + 1][0] if k + 1 < len(titles) else len(meet_html)
        name = title.split("·")[0].strip()   # "FOMC · USD" -> "FOMC"
        cards[name] = [d_email(m.group(1))
                       for m in DATE_TD_RE.finditer(meet_html, pos, end)]
    return front, cards


# ---------- audit ----------

def main():
    email_path = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_EMAIL
    if not os.path.exists(email_path):
        raise SystemExit(f"no email at {email_path} — run the app's UPDATE or `RatesWeeklyCli email` first")

    runs = load_meeting_runs()
    tickers = []
    for r in runs:
        sfx = (" " + r["source"] if r.get("source") else "") + " Curncy"
        tickers += [p.replace("{N}", str(n)) + sfx for p in r["tickers"] for n in range(MAXN + 1)]
    data = bbg(tickers)

    # truth: meeting N (1-based) -> (SW_EFF_DT, ticker), alias-guarded on strictly-increasing maturity
    truth = {}
    for r in runs:
        sfx = (" " + r["source"] if r.get("source") else "") + " Curncy"
        eff, last_mat = {}, ""
        for n in range(MAXN + 1):
            hit = None
            for p in r["tickers"]:
                q = data.get(p.replace("{N}", str(n)) + sfx)
                if q and (q["MATURITY"] or q["PX_LAST"]):
                    hit = (p.replace("{N}", str(n)) + sfx, q)
                    break
            if not hit:
                continue
            tk, q = hit
            if q["MATURITY"] and last_mat and q["MATURITY"] <= last_mat:
                break                                   # alias / end of family
            if q["MATURITY"]:
                last_mat = q["MATURITY"]
            if n >= 1 and q["SW_EFF_DT"]:
                eff[n] = (d_iso(q["SW_EFF_DT"]), tk)
        truth[r["name"]] = eff

    # TIME-GATED FRONT ROLL (app v0.5.0): once a period's decision is ANNOUNCED (decisionDates +
    # decisionTimeLondon, London clock) the app rolls it off the boards even while Bloomberg's
    # generics still point at it — the live RIKSBANK case, 20-Aug-26 08:30. On such a day the
    # rendered rows pair with rung N+shift, where shift counts the leading rungs whose own
    # SW_EFF_DT period is already decided. Any other day shift is 0 and this is a no-op.
    now_ldn = london_now()
    shifts = {}
    for r in runs:
        eff, s = truth[r["name"]], 0
        decs = [d_iso(x) for x in r.get("decisionDates", [])]
        t = r.get("decisionTimeLondon", "")
        while (1 + s) in eff:
            dec = decision_for(decs, eff[1 + s][0])
            if dec is None or not announced(dec, t, now_ldn):
                break
            s += 1
        shifts[r["name"]] = s
        if s:
            print(f"NOTE: {r['name']} decision announced (per {t} London) but the family has not "
                  f"re-pointed — rendered rows pair with rung N+{s} (time-gated front roll)")

    front, cards = parse_email(email_path)
    ok = unver = 0
    problems = []

    def check(surface, run, idx, shown, exp_tk):
        nonlocal ok, unver
        if exp_tk is None:
            unver += 1
            return "UNVERIFIABLE (no quoted rung)"
        exp, tk = exp_tk
        if shown == exp:
            ok += 1
            return f"ok   {tk}"
        problems.append(f"{surface:<14} {run:<10} row {idx}: shows {shown}, {tk} SW_EFF_DT={exp}")
        return f"MISMATCH vs {tk} {exp}"

    print("=" * 96)
    print(f"EMAIL MEETING CARDS — StartDate vs each rung's own SW_EFF_DT   ({email_path})")
    print("=" * 96)
    for name, dates in cards.items():
        known = name in truth
        print(f"\n{name}  ({len(dates)} rows{'' if known else ' — NOT A CONFIG RUN'})")
        for i, sd in enumerate(dates, start=1):
            print(f"   {i:>2} {sd}  "
                  f"{check('meeting card', name, i, sd, truth.get(name, {}).get(i + shifts.get(name, 0)))}")

    print("\n" + "=" * 96)
    print("EMAIL CB FRONT — start must be rung 1's SW_EFF_DT; decision sane vs start")
    print("=" * 96)
    for bank, ccy, dec, start in front:
        line = f"{bank:<10} {ccy}  start {start}  "
        line += check("front", bank, 1, start, truth.get(bank, {}).get(1 + shifts.get(bank, 0)))
        if dec is None:
            line += "   decision: start shown with * (no calendar)"
        elif dec > start:
            problems.append(f"front          {bank:<10}: decision {dec} AFTER start {start}")
            line += f"   decision {dec} AFTER START"
        elif (start - dec).days > 10:
            problems.append(f"front          {bank:<10}: decision {dec} {(start - dec).days}d before start")
            line += f"   decision {dec} ({(start - dec).days}d gap)"
        else:
            line += f"   decision {dec} ok"
        print("  " + line)

    print("\n" + "=" * 96)
    print(f"RESULT: {ok} dates verified against SW_EFF_DT, {unver} unverifiable, "
          f"{len(problems)} MISMATCHES")
    print("=" * 96)
    for p in problems:
        print("  " + p)

    # POSITIVE CONTROLS — an unparsed section is a failed audit, not a pass (the dodgeball
    # script's first run "passed" the weekly having parsed zero tables).
    ctrl = []
    expected_cards = len(runs) - 1              # email drops SNB by spec
    if len(cards) != expected_cards:
        ctrl.append(f"parsed {len(cards)} meeting cards (expected {expected_cards})")
    if any(n not in truth for n in cards):
        ctrl.append(f"card titles not in config: {[n for n in cards if n not in truth]}")
    if len(front) != expected_cards:
        ctrl.append(f"parsed {len(front)} front rows (expected {expected_cards})")
    if sum(len(v) for v in cards.values()) < 4 * max(1, len(cards)):
        ctrl.append("suspiciously few dated rows per card — parser drift?")
    if ctrl:
        print("POSITIVE CONTROL FAILED — audit proves nothing for:")
        for c in ctrl:
            print("  " + c)
        return 2
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
