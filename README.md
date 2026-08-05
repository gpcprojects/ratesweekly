# RatesWeekly

Standalone weekly-summary app for the interest-rate derivatives desk. One click:

1. pulls Bloomberg (terminal on localhost:8194),
2. maintains a local daily-close history (`%APPDATA%\RatesWeekly\history.db`),
3. regenerates the weekly dashboards — a Movers Summary hub plus one page per currency (28) —
   and uploads them to the static site (stable public links, no sign-in),
4. builds the desk email (paste into Outlook via COPY EMAIL) with a hardwired link per currency.

Users need **only `RatesWeekly.exe` and a logged-in Bloomberg terminal**. No other install.

## Build from source

- .NET 8 SDK
- `dotnet test tests\RateDesk.Tests\RateDesk.Tests.csproj -c Release`
- `dotnet publish src\RateDesk.Weekly\RateDesk.Weekly.csproj -c Release -r win-x64 -o publish --self-contained true /p:PublishSingleFile=true`

See `DESIGN.md` for the full spec and decision log, `CLAUDE.md` for team/dev conventions.
`src/RateDesk.Core` + `src/RateDesk.Bloomberg` are a point-in-time copy from the dodgeball repo
(v7.0.0) — this repo is deliberately self-sufficient.
