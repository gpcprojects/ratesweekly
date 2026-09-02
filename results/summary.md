| id | scenario | verdict | findings |
|---:|---|---|---:|
| 01 | Quiet week, no decision in the window | PASS | 0 |
| 02 | FOMC cuts 25bp TODAY, feed not re-pointed (same-day start) | PASS | 0 |
| 03 | ECB hikes 25bp TODAY, period starts in 6 days (lagged start) | PASS | 0 |
| 04 | Decision TODAY but BEFORE the announcement time | PASS | 0 |
| 05 | Decision TODAY, statement out, feed ALREADY re-pointed | PASS | 0 |
| 06 | Decision TODAY, NO decisionTimeLondon on file | PASS | 0 |
| 07 | RBA cuts 25bp TODAY, period starts TOMORROW (1-day lag) | PASS | 0 |
| 08 | BOJ hikes 25bp TODAY, period starts in 6 days (non-contiguous family) | PASS | 0 |
| 09 | Two announced decisions on the board at once, and a run out of meetings | PASS | 0 |
| 14 | ECB hiked 3 days ago - the run sits INSIDE the mixed-state window | PASS | 0 |
| 16 | ECB hikes 25bp TODAY, fully priced (lagged start) | PASS | 0 |
| 17 | ECB cuts 25bp TODAY, fully priced (lagged start) | PASS | 0 |
| 18 | ECB SURPRISE 25bp cut - nothing was priced | PASS | 0 |
| 19 | ECB SURPRISE 50bp cut - twice the size, nothing priced | PASS | 0 |
| 20 | ECB HOLDS - a 25bp cut was priced (hawkish hold) | PASS | 0 |
| 22 | Phantom-step trap: flat market across a renumber inside 1w and 1m | PASS | 0 |
| 23 | Renumber day, feed re-pointed: mid(N) vs PrevClose(N+1) | PASS | 0 |
| 24 | Staleness cap: a three-week hole must blank the 1w, not stretch it | PASS | 0 |
| 25 | Boundary-day sources: the 16:15 snap anchors, the close never does | PASS | 0 |
| 27 | No pre-roll history: 1d falls back to CoD, 1w and 1m stay blank | PASS | 0 |
| 28 | RIKSBANK hikes TODAY, SKSF renumbers in 6 days (announcement day) | PASS | 0 |
| 29 | RIKSBANK period-start day — SKSF renumbers, decision was 6 days ago | PASS | 0 |
| 30 | RIKSBANK cuts TODAY with a Y/E turn period on the board | PASS | 0 |
| 31 | RIKSBANK step chain skips the Y/E turn row | PASS | 0 |
| 32 | RIKSBANK trustConfigDates — priced rungs with no date fields | PASS | 0 |
| 33 | RIKSBANK Y/E turn period IS the front row | PASS | 0 |
| 34 | RBA cuts TODAY - source page prices, composite dates | PASS | 0 |
| 35 | Nobody prices the decided rung - the re-base falls back to closes | PASS | 0 |
| 36 | A far rung goes unquoted on a decision day - the run truncates | PASS | 0 |
| 37 | A published rung's feed is >1h quiet on decision day | PASS | 0 |
| 38 | Neighbour misprint guard on a decision day - interior yes, FRONT never | PASS | 0 |
| 39 | The o/n fixing ticker is unquoted on a decision day | PASS | 0 |
| 40 | OutlierGuard ABSOLUTE bars — a surprise hike breaches 12/30/50bp | PASS | 0 |
| 41 | OutlierGuard CROSS-SECTIONAL — one body row flags, the front is exempt | PASS | 0 |
| 42 | FUTURES GUARD agrees — exchange-settled FF matches the meeting blend | PASS | 0 |
| 43 | FUTURES GUARD disagrees — FF a full step away from the blend | PASS | 0 |
| 44 | COMPLETENESS GATE — a bank that publishes nothing on its decision day | PASS | 0 |
| 45 | NOTES ARE NOT CONTENT — four note families, none of them in the output | PASS | 0 |
| 46 | Nine banks, ONE decides today (ECB cuts) - the report as sent | PASS | 0 |
| 48 | Empty decisionDates with the period start 3 days out | PASS | 0 |
| 49 | Front table: order by decision-or-start, and the sign of '% of 25bp' | PASS | 0 |
| 50 | One report, every surface: surprise cut + re-base + Y/E turn + truncated run | PASS | 0 |
| 51 | Dashboard strip on a decision day | PASS | 0 |
| 52 | Dashboard strip on the morning of a decision day (control) | PASS | 0 |
| 53 | Dashboard strip rendered the day after a decision | PASS | 0 |
| 54 | The day after a Fed cut: what is Priced measured against? | PASS | 0 |
| 55 | Fed and ECB both cut today - one table, two bases | PASS | 0 |
| 56 | Unstable settlement lag (BOJ shape): a 1w anchor inside the gap | PASS | 0 |
| 57 | Save-down history tables across a decision | PASS | 0 |
| 58 | FOMC day: the 16:15 close precedes the 19:00 statement | PASS | 0 |
| 59 | Dashboard strip after an ECB announcement, feed not re-pointed by the close | PASS | 0 |
| 61 | Morning after a SURPRISE hike, feed re-pointed - what is the re-based fixing? | PASS | 0 |
| 62 | A legitimately gapping front row on a decision day - guarded in history, not live | PASS | 0 |
| 63 | Emergency cut, price history only - no maturity records at all | PASS | 0 |
| 64 | No emergency, price history only - the detector stays out of the way | PASS | 0 |
| 99 | POSITIVE CONTROL - the harness must be able to fail | PASS (control) | 15 |

56/56 passed.
