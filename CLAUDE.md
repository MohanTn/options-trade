# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**ThetaDesk** — a single-operator desk for running a NIFTY 50 (Indian index) options theta-harvesting fund on Zerodha's Kite Connect broker API. It scans the option chain by VIX regime, proposes credit structures (short strangles / iron condors / double calendars), lets the operator approve one, places the basket of orders, then auto-manages the open positions (profit-take, risk-stop, delta/gamma/margin adjustments).

`arch-nifty-options-theta-fund.html` is the full design doc. `mockups/` and `data/` hold reference material, not build inputs.

## Architecture

Four moving parts that share **Postgres + Redis** but run as **separate processes** — there is no in-process state shared between the API and the Workers:

- **frontend** (`frontend/`) — React 19 + Vite SPA. `App.tsx` shows `Login` or `Cockpit` based on a JWT in `localStorage` (`td_token`). Data layer is `@tanstack/react-query` over an axios client (`src/api/client.ts`) whose base URL is **hardcoded** to `http://localhost:5085/api/v1`.
- **ThetaDesk.Api** — ASP.NET Core (net8.0) REST API on port **5085**. Owns the trading workflow, broker session, and all operator-facing endpoints (`/api/v1/...`).
- **ThetaDesk.Workers** — a separate .NET worker host with two `BackgroundService`s:
  - `MarketDataWorker` polls Kite chain + India VIX (~2s during market hours) and writes live Greeks to Redis (`greeks:{positionId}`).
  - `LifecycleManagerWorker` (~5s) reads those Greeks, persists `GreeksSnapshot` rows, and applies profit-take / risk-stop / adjustment rules. Rate-limited to 1 action per position per 60s; writes a `lifecycle:heartbeat` to Redis.
- **ThetaDesk.Data** / **ThetaDesk.Domain** / **ThetaDesk.Greeks** — EF Core DbContext + migrations, POCO entities/enums, and the Black-76 option pricer used for delta-based strike selection.

Both .NET processes select their DB provider the same way: if the `Db` connection string contains `Host=` they use **Npgsql (Postgres)**, otherwise they fall back to **SQLite**. The API auto-runs `db.Database.Migrate()` on startup and seeds a `Fund`, default `RiskLimit`s, and three default VIX-band `StrategyConfig`s (idempotently).

### Trading flow (the core path)

1. Operator exchanges a Kite request token → `POST /api/v1/system/session`. This also auto-fetches broker capital and ingests any untracked broker positions (`BrokerSyncService`).
2. `POST /api/v1/system/start` → `SignalEngine.GenerateCandidatesAsync` picks the enabled `StrategyConfig` whose VIX band contains live VIX, scans every expiry in its DTE window at several width scales, sizes each to the per-position max-loss cap, ranks by score (ATM-IV richness × return-on-risk), and persists the top N as `TradeProposal`s. `SizingEngine.ValidateProposalAsync` attaches a per-candidate margin/limit verdict.
3. `POST /api/v1/proposals/{id}/approve` (with an `Idempotency-Key` header) re-validates limits, places each leg as a basket order via `IKiteClient`, and creates a `Position` (status `Open` if fills come back priced, else `PendingFill`).
4. The Workers process then manages the position to exit.

### Chain analysis & weekly compounding

- `ChainAnalysisService` (`GET /api/v1/analysis/chain`, "Option Chain" view in the UI) scans the near-week **and** near-month NIFTY chains (a monthly inside 10 DTE is skipped for the next monthly): PCR (OI/volume), max pain, OI support/resistance walls, IV skew, IV term structure, ATM-straddle expected move, and intraday drifts measured against per-IST-day Redis baselines (`chain:oibase:{yyyyMMdd}` for per-token OI, `chain:daybase:{yyyyMMdd}` for straddle premium, wall levels, and per-strike morning OTM prices). The **vega-flow leading indicator** (divergence of morning-anchored per-side Σ vega — a side's vega rising = that side being bought; both decaying = non-directional seller's day) carries the top bias weight, with the raw premium-flow divergence as a lower-weighted confirmation; both are compared strike-for-strike against per-strike morning captures so window drift can't fake a signal. Wall migration also feeds the bias score ∈ [−1, +1]; straddle drift, the VIX regime read, and the intraday VIX-vs-morning trend gate are direction-neutral and feed only the seller playbook. Cached in Redis (`chain:analysis:latest`, 3-min TTL). Each market-hours run also appends a point to the intraday vega-flow series (`chain:vegaflow:{yyyyMMdd}`, served by `GET /api/v1/analysis/vega-flow`), which the Option Chain view plots as a line chart.
- `ChainAnalysisRefresher` (hosted service in the **API** process) re-runs the analysis every 60s during market hours (09:10–15:35 IST, Mon–Fri, only with a valid Kite session) and raises a `MarketBiasShift` Warning alert when the bias label changes (rate-limited to one per 15 min).
- `SignalEngine` runs that analysis during a scan (cache-aware; scan proceeds unskewed if it fails) and skews **short-leg** target deltas toward the safer side by up to ±30%.
- `StrategyConfig.WeeklyCompounding` locks candidates to the nearest eligible expiry and scales the sizing budget by `CurrentNav / StartingCapital` so weekly credits compound — always hard-capped by `Fund.PerPositionMaxLoss`.

### IKiteClient and paper trading

All broker access goes through `IKiteClient` (`backend/src/ThetaDesk.Api/Kite/`). When `Kite:PaperTrading` is true, `PaperKiteClient` wraps the real `KiteClient` — it keeps **live market data** but **simulates order fills** (and can seed its ledger from real Kite holdings). Default is `false` (live trading). Toggle in `appsettings.json` or via the `Kite__PaperTrading` env var.

### Gotchas

- **Kill-switch does not propagate between processes by itself.** The API toggles `KillSwitchState` (a singleton in the API process); the worker reads `KillSwitchShim` (a singleton in the Workers process). They are distinct objects in distinct processes — changing one does not change the other. Verify the intended propagation path (DB/Redis) before relying on it.
- `MarketDataWorker.RefreshAsync` currently writes **zeroed** `CachedGreeks` as a heartbeat placeholder; live Greek computation from quotes is a TODO marked in-code.
- `WeatherForecast*` files are leftover template scaffolding — ignore them.
- Auth is a single shared operator password (`Operator:Password`, default `changeme`) that mints a 12h JWT; there are no user accounts.

## Commands

### Everything via Docker (Postgres, Redis, api, workers, frontend)
```bash
docker compose up --build      # frontend → :5173, api → :5085, postgres → :5432
```
Requires a `.env` (copy `.env.example`). Config uses .NET's double-underscore convention: `Kite__ApiKey`, `Jwt__SigningKey`, `Operator__Password`, `ConnectionStrings__Db`, `ConnectionStrings__Redis`, `POSTGRES_PASSWORD`.

### Backend (from `backend/`)
```bash
dotnet build ThetaDesk.sln
dotnet run --project src/ThetaDesk.Api        # API on :5085
dotnet run --project src/ThetaDesk.Workers    # workers
```

### EF Core migrations (from `backend/`)
The design-time factory (`ThetaDeskDbContextFactory`) points at `Host=localhost;Database=thetadesk;Username=postgres;Password=postgres`, so have a local Postgres reachable for migration tooling.
```bash
dotnet ef migrations add <Name> --project src/ThetaDesk.Data --startup-project src/ThetaDesk.Api
dotnet ef database update      --project src/ThetaDesk.Data --startup-project src/ThetaDesk.Api
```
(Migrations also apply automatically when the API starts.)

### Frontend (from `frontend/`)
```bash
npm install
npm run dev       # Vite dev server on :5173
npm run build     # tsc -b && vite build
npm run lint      # oxlint
```

There is currently **no automated test suite** in the repo.
