# RatesWeekly

Standalone weekly-summary app for the interest-rate derivatives desk — the weekly email AND the
dashboards, one project (consolidated out of dodgeball 2026-08-11). One click:

1. pulls Bloomberg (terminal on localhost:8194),
2. maintains a local daily-close history (`%APPDATA%\RatesWeekly\history.db`),
3. regenerates the weekly dashboards — the Movers Summary hub (`index.html`, top outsized movers
   for DM and EM, |z|-ranked) plus one page per currency (28) — and uploads them to the static
   site (stable public links, no sign-in),
4. builds the desk email (CB front table, meeting cards, forward grid) and persists it to
   `%APPDATA%\RatesWeekly\out\` — COPY EMAIL pastes it into Outlook, currency headers linking to
   the dashboards when `publish.json` carries a `siteBase`.

Users need **only `RatesWeekly.exe` and a logged-in Bloomberg terminal**. No other install.

## Build from source

- .NET 8 SDK
- `dotnet test tests\RateDesk.Tests\RateDesk.Tests.csproj -c Release`
- `dotnet publish src\RateDesk.Weekly\RateDesk.Weekly.csproj -c Release -r win-x64 -o publish --self-contained true /p:PublishSingleFile=true`

See `DESIGN.md` for the full spec and decision log, `CLAUDE.md` for team/dev conventions.
`src/RateDesk.Core` + `src/RateDesk.Bloomberg` are a point-in-time copy from the dodgeball repo
(v7.0.0) — this repo is deliberately self-sufficient.
