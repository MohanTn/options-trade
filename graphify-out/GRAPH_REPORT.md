# Graph Report - .  (2026-06-29)

## Corpus Check
- Corpus is ~48,091 words - fits in a single context window. You may not need a graph.

## Summary
- 661 nodes · 1106 edges · 46 communities (41 shown, 5 thin omitted)
- Extraction: 98% EXTRACTED · 2% INFERRED · 0% AMBIGUOUS · INFERRED: 20 edges (avg confidence: 0.86)
- Token cost: 134,348 input · 0 output

## Community Hubs (Navigation)
- [[_COMMUNITY_Kite Client Interface|Kite Client Interface]]
- [[_COMMUNITY_.NET Project Dependencies|.NET Project Dependencies]]
- [[_COMMUNITY_Trading Strategy Domain|Trading Strategy Domain]]
- [[_COMMUNITY_Orders & Positions Controllers|Orders & Positions Controllers]]
- [[_COMMUNITY_Kite Market Data Models|Kite Market Data Models]]
- [[_COMMUNITY_EF Core DbContext|EF Core DbContext]]
- [[_COMMUNITY_API Launch Settings|API Launch Settings]]
- [[_COMMUNITY_Kite Client Implementation|Kite Client Implementation]]
- [[_COMMUNITY_Portfolio Controller|Portfolio Controller]]
- [[_COMMUNITY_Frontend npm Dependencies|Frontend npm Dependencies]]
- [[_COMMUNITY_Cockpit Dashboard UI|Cockpit Dashboard UI]]
- [[_COMMUNITY_Strategies Controller|Strategies Controller]]
- [[_COMMUNITY_Black-76 Options Pricing|Black-76 Options Pricing]]
- [[_COMMUNITY_EF Core Migrations|EF Core Migrations]]
- [[_COMMUNITY_TS App Compiler Config|TS App Compiler Config]]
- [[_COMMUNITY_TS Node Compiler Config|TS Node Compiler Config]]
- [[_COMMUNITY_System & Kill-Switch Controller|System & Kill-Switch Controller]]
- [[_COMMUNITY_Domain Entities|Domain Entities]]
- [[_COMMUNITY_Frontend API Client|Frontend API Client]]
- [[_COMMUNITY_Proposals Controller|Proposals Controller]]
- [[_COMMUNITY_Paper Broker & Enums|Paper Broker & Enums]]
- [[_COMMUNITY_Broker Sync Service|Broker Sync Service]]
- [[_COMMUNITY_Sizing & Limit Engine|Sizing & Limit Engine]]
- [[_COMMUNITY_Strategy Settings UI|Strategy Settings UI]]
- [[_COMMUNITY_Fund Settings UI|Fund Settings UI]]
- [[_COMMUNITY_Proposal Review UI|Proposal Review UI]]
- [[_COMMUNITY_Payoff Simulator UI|Payoff Simulator UI]]
- [[_COMMUNITY_Workers Launch Settings|Workers Launch Settings]]
- [[_COMMUNITY_App Shell & Auth|App Shell & Auth]]
- [[_COMMUNITY_Social Icon Sprite|Social Icon Sprite]]
- [[_COMMUNITY_Monitor Sound Hook|Monitor Sound Hook]]
- [[_COMMUNITY_Position Card UI|Position Card UI]]
- [[_COMMUNITY_Audit Service|Audit Service]]
- [[_COMMUNITY_DbContext Model Snapshot|DbContext Model Snapshot]]
- [[_COMMUNITY_Oxlint Config|Oxlint Config]]
- [[_COMMUNITY_Initial Migration Design|Initial Migration Design]]
- [[_COMMUNITY_Configurable Strategies Migration|Configurable Strategies Migration]]
- [[_COMMUNITY_Add Fund Lot Size Migration|Add Fund Lot Size Migration]]
- [[_COMMUNITY_Favicon Branding|Favicon Branding]]
- [[_COMMUNITY_Hero Graphic Branding|Hero Graphic Branding]]
- [[_COMMUNITY_TS Root Config|TS Root Config]]
- [[_COMMUNITY_Audit Log & NAV|Audit Log & NAV]]
- [[_COMMUNITY_Theta-Harvesting Mandate|Theta-Harvesting Mandate]]
- [[_COMMUNITY_Program Entrypoint & Login|Program Entrypoint & Login]]
- [[_COMMUNITY_Vite Build Tool|Vite Build Tool]]

## God Nodes (most connected - your core abstractions)
1. `KiteClient` - 21 edges
2. `PaperKiteClient` - 21 edges
3. `IKiteClient` - 18 edges
4. `compilerOptions` - 17 edges
5. `PortfolioController` - 15 edges
6. `compilerOptions` - 15 edges
7. `KiteInstrument` - 14 edges
8. `SignalEngine` - 13 edges
9. `StrategiesController` - 11 edges
10. `KiteQuote` - 10 edges

## Surprising Connections (you probably didn't know these)
- `ThetaDesk Dashboard Cockpit Mock` --references--> `Signal/Proposal Engine`  [INFERRED]
  mockups/ui-mocks-dashboard.html → arch-nifty-options-theta-fund.html
- `workers service (ThetaDesk.Workers)` --implements--> `Position Lifecycle Manager`  [INFERRED]
  docker-compose.yml → arch-nifty-options-theta-fund.html
- `workers service (ThetaDesk.Workers)` --implements--> `Market Data Worker`  [INFERRED]
  docker-compose.yml → arch-nifty-options-theta-fund.html
- `api service (ThetaDesk.Api)` --implements--> `thetadesk-api (.NET 8 Web API)`  [INFERRED]
  docker-compose.yml → arch-nifty-options-theta-fund.html
- `frontend service` --implements--> `thetadesk-web (React 18 SPA)`  [INFERRED]
  docker-compose.yml → arch-nifty-options-theta-fund.html

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **ThetaDesk Docker Compose Runtime Stack** — docker_compose_api, docker_compose_workers, docker_compose_frontend, docker_compose_db, docker_compose_redis [EXTRACTED 1.00]
- **System Proposes, Operator Approves, System Executes Flow** — arch_nifty_options_theta_fund_signal_engine, arch_nifty_options_theta_fund_thetadesk_api, arch_nifty_options_theta_fund_lifecycle_manager, arch_nifty_options_theta_fund_kite_connect [EXTRACTED 1.00]
- **VIX-Regime to Strategy Mapping** — arch_nifty_options_theta_fund_vix_regime_strategy, arch_nifty_options_theta_fund_double_calendar, arch_nifty_options_theta_fund_short_strangle, arch_nifty_options_theta_fund_iron_condor [EXTRACTED 1.00]

## Communities (46 total, 5 thin omitted)

### Community 0 - "Kite Client Interface"
Cohesion: 0.10
Nodes (19): BasketLeg, CancellationToken, IEnumerable, IReadOnlyList, Task, GttRequest, IKiteClient, KiteGttResult (+11 more)

### Community 1 - ".NET Project Dependencies"
Cohesion: 0.07
Nodes (28): net8.0, Serilog.Extensions.Hosting (10.0.0), Serilog.Sinks.File (7.0.0), StackExchange.Redis (3.0.7), net8.0, Microsoft.EntityFrameworkCore.Sqlite (8.0.10), Npgsql.EntityFrameworkCore.PostgreSQL (8.0.10), Microsoft.NET.Sdk (+20 more)

### Community 2 - "Trading Strategy Domain"
Cohesion: 0.10
Nodes (30): Capital-Management Limit Engine, Double Calendar Strategy, Greeks Engine (Black-76), Mandatory GTT Stop-Loss (200% premium), Iron Condor Strategy, Operator Kill-Switch, Kite Connect Broker, Position Lifecycle Manager (+22 more)

### Community 3 - "Orders & Positions Controllers"
Cohesion: 0.10
Nodes (20): CancellationToken, HttpGet, IActionResult, Task, OrdersController, CloseRequest, CancellationToken, Guid (+12 more)

### Community 4 - "Kite Market Data Models"
Cohesion: 0.15
Nodes (20): KiteInstrument, KiteQuote, CancellationToken, DateOnly, Dictionary, double, Guid, IsDefinedRisk (+12 more)

### Community 5 - "EF Core DbContext"
Cohesion: 0.11
Nodes (16): ModelBuilder, ThetaDeskDbContext, ThetaDeskDbContextFactory, Position, AdjustmentKind, CachedGreeks, CancellationToken, Dictionary (+8 more)

### Community 6 - "API Launch Settings"
Cohesion: 0.07
Nodes (28): ASPNETCORE_ENVIRONMENT, applicationUrl, commandName, dotnetRunMessages, environmentVariables, launchBrowser, launchUrl, applicationUrl (+20 more)

### Community 7 - "Kite Client Implementation"
Cohesion: 0.22
Nodes (7): CancellationToken, IEnumerable, IReadOnlyList, string, Task, KiteClient, HttpClient

### Community 8 - "Portfolio Controller"
Cohesion: 0.22
Nodes (12): CancellationToken, DateOnly, double, Guid, HttpGet, HttpPost, HttpPut, IActionResult (+4 more)

### Community 9 - "Frontend npm Dependencies"
Cohesion: 0.09
Nodes (22): dependencies, axios, react, react-dom, @tanstack/react-query, devDependencies, oxlint, @types/node (+14 more)

### Community 10 - "Cockpit Dashboard UI"
Cohesion: 0.11
Nodes (13): market, NoCandidate, orders, system, useMonitorSounds(), Cockpit(), IMPLEMENTED, marketStatus() (+5 more)

### Community 11 - "Strategies Controller"
Cohesion: 0.21
Nodes (12): CancellationToken, Guid, HttpDelete, HttpGet, HttpPost, HttpPut, IActionResult, List (+4 more)

### Community 12 - "Black-76 Options Pricing"
Cohesion: 0.18
Nodes (7): Black76, GreeksResult, IEnumerable, GreeksAggregator, LegGreeksInput, PortfolioGreeks, Iv

### Community 13 - "EF Core Migrations"
Cohesion: 0.12
Nodes (10): MigrationBuilder, InitialThetaDesk, ThetaDesk.Data.Migrations, ConfigurableStrategies, MigrationBuilder, ThetaDesk.Data.Migrations, AddFundLotSize, MigrationBuilder (+2 more)

### Community 14 - "TS App Compiler Config"
Cohesion: 0.11
Nodes (18): compilerOptions, allowImportingTsExtensions, erasableSyntaxOnly, jsx, lib, module, moduleDetection, moduleResolution (+10 more)

### Community 15 - "TS Node Compiler Config"
Cohesion: 0.12
Nodes (16): compilerOptions, allowImportingTsExtensions, erasableSyntaxOnly, lib, module, moduleDetection, noEmit, noFallthroughCasesInSwitch (+8 more)

### Community 16 - "System & Kill-Switch Controller"
Cohesion: 0.24
Nodes (10): CancellationToken, HttpDelete, HttpGet, HttpPost, IActionResult, Task, KillSwitchRequest, KillSwitchState (+2 more)

### Community 17 - "Domain Entities"
Cohesion: 0.13
Nodes (13): StrategyLegDto, Adjustment, Alert, AuditEntry, GreeksSnapshot, Instrument, MarginSnapshot, NavSnapshot (+5 more)

### Community 18 - "Frontend API Client"
Cohesion: 0.13
Nodes (14): AlertSeverity, api, AuditEntry, CandidateSet, MarginBreakdown, MarketTicks, NavSnapshot, Order (+6 more)

### Community 19 - "Proposals Controller"
Cohesion: 0.31
Nodes (8): CancellationToken, Guid, HttpGet, HttpPost, IActionResult, Task, ProposalsController, RejectRequest

### Community 20 - "Paper Broker & Enums"
Cohesion: 0.14
Nodes (10): PaperFill, AlertSeverity, ExpiryRank, LimitScope, OptionType, OrderStatus, ProposalStatus, Side (+2 more)

### Community 21 - "Broker Sync Service"
Cohesion: 0.26
Nodes (10): BrokerPosition, BrokerSyncService, CancellationToken, Dictionary, IsDefinedRisk, List, Task, Fund (+2 more)

### Community 22 - "Sizing & Limit Engine"
Cohesion: 0.20
Nodes (9): CancellationToken, Task, LimitVerdict, SizingEngine, TradeProposal, PositionStatus, MarginBlocked, MarginUtilPct (+1 more)

### Community 23 - "Strategy Settings UI"
Cohesion: 0.18
Nodes (9): strategies, StrategyConfig, StrategyLeg, BLANK, btn(), inputStyle, labelStyle, STRATEGY_TYPES (+1 more)

### Community 24 - "Fund Settings UI"
Cohesion: 0.20
Nodes (8): FundConfig, FundUpdate, portfolio, btn(), FundForm(), inputStyle, labelStyle, NUM_FIELDS

### Community 25 - "Proposal Review UI"
Cohesion: 0.18
Nodes (5): Proposal, proposals, s, strategyColor, s

### Community 26 - "Payoff Simulator UI"
Cohesion: 0.27
Nodes (10): bsPrice(), DAYS, fmtDate(), fmtExpiry(), fmtInr(), impliedVol(), MONTHS, normCdf() (+2 more)

### Community 27 - "Workers Launch Settings"
Cohesion: 0.25
Nodes (7): DOTNET_ENVIRONMENT, profiles, ThetaDesk.Workers, $schema, commandName, dotnetRunMessages, environmentVariables

### Community 28 - "App Shell & Auth"
Cohesion: 0.32
Nodes (5): auth, App(), queryClient, Login(), styles

### Community 29 - "Social Icon Sprite"
Cohesion: 0.38
Nodes (7): Bluesky Icon, Discord Icon, Documentation Icon, GitHub Icon, Social Icon, Icons SVG Sprite, X (Twitter) Icon

### Community 30 - "Monitor Sound Hook"
Cohesion: 0.29
Nodes (5): Alert, Position, PATTERNS, SoundEvent, ToneSpec

### Community 31 - "Position Card UI"
Cohesion: 0.33
Nodes (6): positions, fmtExpiry(), MONTHS, PositionCard(), STATUS, STRAT

### Community 32 - "Audit Service"
Cohesion: 0.33
Nodes (4): AuditService, CancellationToken, Guid, Task

### Community 33 - "DbContext Model Snapshot"
Cohesion: 0.33
Nodes (4): ModelBuilder, ThetaDesk.Data.Migrations, ThetaDeskDbContextModelSnapshot, ModelSnapshot

### Community 34 - "Oxlint Config"
Cohesion: 0.33
Nodes (5): plugins, rules, react/only-export-components, react/rules-of-hooks, $schema

### Community 35 - "Initial Migration Design"
Cohesion: 0.40
Nodes (3): ModelBuilder, InitialThetaDesk, ThetaDesk.Data.Migrations

### Community 36 - "Configurable Strategies Migration"
Cohesion: 0.40
Nodes (3): ConfigurableStrategies, ModelBuilder, ThetaDesk.Data.Migrations

### Community 37 - "Add Fund Lot Size Migration"
Cohesion: 0.40
Nodes (3): AddFundLotSize, ModelBuilder, ThetaDesk.Data.Migrations

### Community 38 - "Favicon Branding"
Cohesion: 1.00
Nodes (3): ThetaDesk Brand Purple Palette, ThetaDesk Favicon (Z-shaped Lightning Bolt), Lightning Bolt / Theta Energy Mark

### Community 39 - "Hero Graphic Branding"
Cohesion: 1.00
Nodes (3): Hero Graphic, Isometric Layered Cards, Purple Gradient Brand Accent

## Knowledge Gaps
- **188 isolated node(s):** `StrategyLegDto`, `PaperFill`, `LoginRequest`, `$schema`, `windowsAuthentication` (+183 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **5 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Proposal` connect `Proposal Review UI` to `Frontend API Client`, `Cockpit Dashboard UI`, `Kite Market Data Models`?**
  _High betweenness centrality (0.120) - this node is a cross-community bridge._
- **Why does `KiteInstrument` connect `Kite Market Data Models` to `Kite Client Interface`, `Broker Sync Service`, `Kite Client Implementation`?**
  _High betweenness centrality (0.047) - this node is a cross-community bridge._
- **Why does `Fund` connect `Broker Sync Service` to `Portfolio Controller`, `Domain Entities`, `Kite Market Data Models`, `Sizing & Limit Engine`?**
  _High betweenness centrality (0.046) - this node is a cross-community bridge._
- **What connects `StrategyLegDto`, `PaperFill`, `LoginRequest` to the rest of the system?**
  _190 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Kite Client Interface` be split into smaller, more focused modules?**
  _Cohesion score 0.10014513788098693 - nodes in this community are weakly interconnected._
- **Should `.NET Project Dependencies` be split into smaller, more focused modules?**
  _Cohesion score 0.06890756302521009 - nodes in this community are weakly interconnected._
- **Should `Trading Strategy Domain` be split into smaller, more focused modules?**
  _Cohesion score 0.09655172413793103 - nodes in this community are weakly interconnected._