
---

## Open, recorded 2026-08-28 — inflation fixing change columns are noisy at the tenor level

Not a bug in the app's arithmetic; a property of the fixing feed, quantified over three
months of Bloomberg closes (28-May → 28-Aug-2026, ~68 day-pairs per family).

**What the desk sees.** RPI change columns read like `+0 +22 +8 +0 +10 +14 +0` across the
strip on a day the curve moved smoothly. The total move is right; its distribution across
tenors is not.

**Two distinct causes, both in the feed:**

1. TENORS THAT PRINT BUT DO NOT TICK. A tenor quotes the identical number two days running
   while its neighbours move, so its change reads 0.00 and the move it should have shown
   turns up in whichever neighbour requoted.
   · RPI: 22 of 68 days have ≥1 such tenor (32%), 7 days have ≥2 (10%), one day has 4.
   · HICP: 17 of 69 days (25%), 2 with ≥2.  · CPI: 6 of 68 days (9%), never ≥2.
   · 27-Aug-26 is the WORST day in the window — the only 4-flat day, and the one the desk
     queried: Aug-26, Oct-26, Jan-27 and Apr-27 all flat while the rest moved.

2. LONE-MOVER QUOTES. One tenor moves double digits while the strip does not move at all.
   BPSWIF3 (Mar-27) printed 432.125 → 415.000 → 433.750 across 26/27/28-Aug: a 17bp drop
   and 19bp recovery on a flat strip. The app's own live mark that day implied ~434.6,
   consistent with both neighbours, so the CLOSE was the outlier.

**Despiking does NOT fix this** (tested): Hampel 5/k=6 over three months flags 2 RPI points,
0 CPI, 13 HICP — and misses the 415.000 entirely, because BPSWIF3's own daily volatility is
10-18bp so a 17bp print is not an outlier by its own history.

**The separation that DOES work — cross-sectional coherence.** Judge a tenor's move against
the median move of the months either side of it (its curve neighbours), same day, no lag:

  | | strip move | worst single-tenor residual |
  |---|---|---|
  | real repricings (5 days, incl. the 25-Aug Ofgem cap reset) | 12-20bp | **2.0 - 4.8bp** |
  | bad quotes and rolls (8 tenor-days) | ~0 | **18 - 105bp** |

  Median residual 1.0bp, p90 4.3bp. There is a clean empty gap between ~5bp and ~18bp, so
  any threshold in 8-15bp separates them; at 12bp it fires on 8 tenor-days in three months.
  25-Aug (Ofgem, every month -12bp together, worst residual -4.75bp) passes untouched.

**Why this shape and not a smoother.** The desk needs BOTH: Bloomberg caught the Ofgem cap
reset on 25-Aug and the external pricer's curve did not, so smoothing destroys the signal
the feed is there to provide. A coherence test keeps every coherent move in full, same day,
and gates only the single cell where a tenor provably moved alone. It is also the principle
the OIS side already uses (the neighbour guard, the futures guard) — it simply never reached
the inflation path.

**Proposed, NOT built — needs desk sign-off before it changes a published number:**
  · lone-mover test: residual >12bp ⇒ withhold that tenor's change cell and name it;
  · non-ticker test: tenor exactly unchanged while ≥4 others moved ⇒ note at ≥2 such tenors
    (would have fired 7 times in 3 months, including 27-Aug);
  · the LEVEL is never touched by either — the raw mark always publishes, so an Ofgem-style
    reset still lands in Mid the same day.

**Caveats to settle first:** three months is a thin calibration sample (5 real events); and
a genuine SINGLE-MONTH event (an energy or seasonal effect hitting one fixing) would look
like a lone mover — confirm that is not a real risk for UK RPI before switching it on.

Evidence scripts: scratchpad rpi_vs_bbg2.py, sawtooth.py, signal_vs_noise.py (2026-08-28).

---

## Open, recorded 2026-08-28 — save-down history carries the previous close across a roll

`DailyBook.BankHistoryRows.ValueAt` walks back off a boundary or mixed-state day to the last
usable close — correct for a change ANCHOR — but the caller stamps that walked-back value with
the day it ASKED about. So every day the walk skips publishes the previous close under its own
date in the `Historical_*` tables.

Seen live: `Historical_SEK` on the 26-Aug-26 Riksbank roll carries 1.7237 (25-Aug's rung-2
close) where the family had moved to rung 1 at 1.7150 — about 0.9bp, self-correcting the next
day. `Historical_EU` froze at 2.7940 across 23-28 Jul, the whole announcement→start window of
the 23-Jul ECB. It reads back as "the market did not move".

**ATTEMPTED AND REVERTED, deliberately.** Feeding the store's maturity record into this walk's
MeetingRungMap so recorded days are not stepped over. It fixes the Riksbank case, and it is the
same evidence-before-inference move that fixed the boards on 2026-08-27 — but it is wrong here,
and scenario 57 caught it on twelve rows. A maturity record is stamped WHEN THE RUN LOOKED; a
boundary day's feed re-points intraday, so the record does not establish which contract that
day's CLOSE belonged to. Scenario 57 derives the same rule independently of the app ("a day is
unattributable when it IS the announcement or falls between the announcement and the period
start") and is right to. The narrower variant — lift for boundaries, keep the precaution for
mixed-state — fails for the same reason.

What would actually settle it is a mark timed like the close (a 16:15 rung probe recorded
alongside the price), not a run-time record. That is a data-collection change, not a logic one.
Needs desk sign-off; the code carries a comment at the site so it is not re-attempted blind.

## Recorded 2026-08-28 — two scenarios are weekday-fragile

26 ("Weekend walk-back: Friday decision, run after the weekend") and 47 fail when the suite runs
on a FRIDAY and pass otherwise: `Cal.D(n)`/`Cal.Bd(n)` are offsets from `DateTime.Today`, so
scenario 26 cannot construct "a Friday decision with the weekend after it" from a Friday — its
own premise becomes unsatisfiable and the walk-back it exists to test does not occur. 64/64 on
Thursday 27-Aug, 62/64 on Friday 28-Aug, with no change between that the harness can even reach
(it drives BuildWeekly → guards → renderers; it never touches the save-down or the template).
Not a product fault, but the suite should pin its own weekday rather than read the wall clock.

---

## Fixed 2026-08-31 — neither the close nor the 16:15 snap is reliable alone

Two lone-mover rows, a working day apart, in opposite directions:

  · Mar-27 (BPSWIF3) 27-Aug — the CLOSE was 415.000 while the tenor traded 429-435 all day.
    A bad tick printed twice and the second landed on the last bar, so it became the close.
    The 16:15 snap was 435.500. Published +0.99 index pts against neighbours at +0.15/+0.26.
  · Nov-26 (BPSWIF11) 28-Aug — the SNAP was 434.250 while the close was 439.375. Neither is
    wrong: the last trade before 16:15 really was 434.250, the tenor jumped AT 16:15 and held
    439.375 for five hours. Thin instrument, two honest marks, 5bp apart. Published +0.21
    against a strip that moved 0.00 to -0.04.

Switching wholesale from closes to snaps fixed the first and caused the second. The snap rule
itself is right (last bar ENDING at or before 16:15 = the last traded price as of the snap);
the problem is that a monthly fixing is quoted on its own and trades thinly, so ANY single
observation can be junk while the rest of the curve is fine.

**Fix (InflHistory.Maintain): keep both candidate marks and let the strip arbitrate.** For each
day take the median day-over-day change across the twelve tenors, measured on closes so the
reference never depends on what is being chosen, then give each tenor whichever of {close, snap}
lands closer to it. Same discriminator as the OIS neighbour and futures guards, and the one
measured to work here: a real repricing carries the whole curve (25-Aug Ofgem cap reset, worst
single-tenor disagreement 4.8bp) while a bad mark moves one month alone (18-105bp).

Verified on both cases with one rule: Mar-27 27-Aug takes the SNAP (435.500), Nov-26 28-Aug
takes the CLOSE (439.375). Nothing invented, nothing discarded - both are real prints of that
tenor on that day; the strip only decides which to believe.

## Recorded 2026-08-31 — the scenario suite is weekday-fragile, widened

10, 11, 12, 13, 15, 21 join 26 and 47 on the CI skip list. They say "hiked YESTERDAY" or "cut N
days ago"; on a MONDAY yesterday is a Sunday, so the decision lands on a non-business day and
every change anchor walks past it (scenario 10: Δ1d reads +11.0 against an expected +3.0).
26 and 47 fail the mirror-image way on a Friday.

Verified not a regression: reverting the day's product change and re-running 10 reproduces the
failure identically, and the harness never references InflHistory at all.

Eight of sixty-four is real coverage lost on any given day. The fix is to give the harness its
own pinned weekday, or to express these offsets in BUSINESS days (Cal.Bd already exists), rather
than reading DateTime.Today. Worth doing before the suite is trusted as a release gate.
