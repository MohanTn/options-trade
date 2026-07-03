# Overnight gap-risk management

Context: ThetaDesk sells NIFTY credit structures for theta. The operator's goal is a steady profit
stream from decay while limiting exposure to discrete overnight/weekend moves ("gaps") that the
system cannot react to — `LifecycleManagerWorker` only runs 09:15–15:30 IST
(`Workers.cs:169-176`), so there is no code path executing between one day's close and the next
day's open. Research behind this (variance risk premium is concentrated overnight, not intraday;
GTT stops can't fill mid-gap; jump-diffusion vs. pure-diffusion option pricing) is summarized in
the chat history that produced this doc, not repeated here.

Five changes were proposed. **Items 1–2 are implemented.** Items 3–5 are designed below for later
implementation.

## 1. Gap-stressed sizing for undefined-risk structures — IMPLEMENTED

**Where:** `SignalEngine.cs` — `MaxLossPerUnit` (naked-credit branch) + new `GapStressLoss` helper.

**Problem:** the naked-strangle branch of `MaxLossPerUnit` sized max loss as
`netCredit × GttPremiumPct/100`, i.e. it assumed the GTT stop fills near its trigger price. That
assumption only holds for a continuous intraday move. A gap prints straight through the trigger
with no chance to fill there — the position's real loss is whatever the option is worth at the
opening print, which can be far worse than the stop's nominal level. Defined-risk structures
(iron condor, calendar) were already correctly capped by wing width / debit paid regardless of
gap size, so they needed no change.

**Fix:** for naked (no offsetting long leg) structures, `MaxLossPerUnit` now takes the *worse* of:
- the existing GTT-multiple estimate, and
- a **gap-stress estimate**: each short leg is re-priced with Black-76 at spot moved ±3.5%
  (`GapStressPct` constant), holding each leg's own market-implied vol constant (a real gap
  likely also expands vol, so this is a floor, not a ceiling), and the worse direction wins.

Sizing (`lots`) is computed from this larger max-loss figure, so a naked strangle now sizes down
automatically to survive a real gap within `Fund.PerPositionMaxLoss`, instead of only within the
GTT's optimistic assumption.

**Known effect:** this will shrink lot counts (or push candidate generation to wider/lower-delta
strikes, or occasionally zero out a width) for the Mid-VIX naked-strangle config specifically. That
is the intended trade — smaller, gap-survivable size in exchange for a smoother P&L, per the
operator's "steady profit stream" goal. If it proves too conservative, `GapStressPct` is the one
constant to tune (currently hardcoded; item 3 below makes it a per-strategy config field instead).

**Known limitation:** `GapStressLoss` only sums short legs' buy-back cost. If a `StrategyConfig`
were ever set up with an asymmetric leg shape (e.g. a naked short call *and* a wing-protected put
side in the same structure — not something any of the three seeded configs do today, but the leg
model doesn't forbid it), the stress figure would ignore any offsetting long leg's value. That
makes the estimate too conservative for such a shape, never too permissive, so it's not a safety
gap — just a precision gap worth knowing about if custom leg templates are ever built.

## 2. VIX-morning-trend as an actual size brake — IMPLEMENTED

**Where:** `SignalEngine.cs` — new `VixDriftSizeMultiplier`, wired into `GenerateCandidatesAsync`
and `TryBuildCandidate`'s budget calculation.

**Problem:** `ChainAnalysisService.Compose` already computes "VIX rising vs. this morning's open —
stay conservative, size writing down" (`ChainAnalysisService.cs:599-603`), but only renders it as
advisory text in `SellerPlaybook`. Nothing downstream actually reduced size from it.

**Fix:** `SignalEngine` now reads `chain.Vix` / `chain.MorningVix` itself and computes a size
multiplier: flat/falling VIX → `1.0` (no boost — this is a brake only, sizing *up* on a "green
light" read is left as an operator decision, not automated), VIX up more than 1% from the morning
print (same dead-zone the advisory text already uses) → linearly cuts the position budget, floored
at 0.5×. Applied to the same `budget` variable that `WeeklyCompounding` already scales, so it
composes with existing sizing logic rather than replacing it. Shown in the proposal's `Rationale`
string when active, same as the existing bias-skew note.

## 3. `GapRiskMultiplier` field on `StrategyConfig` — NOT YET IMPLEMENTED

**Goal:** make `GapStressPct` (item 1) tunable per VIX-band strategy instead of a hardcoded
constant, since risk appetite for a gap should differ between, e.g., a Low-VIX calendar (already
defined-risk, doesn't need it) and the Mid-VIX naked strangle (does).

**Design:**
- Add `decimal GapStressPct { get; set; } = 3.5m` to `StrategyConfig` (`Entities.cs`), same pattern
  as `GttPremiumPct`. New EF Core migration required (`dotnet ef migrations add
  StrategyGapStressPct`).
- `SignalEngine.GapStressLoss` takes this from `config.GapStressPct / 100m` instead of the private
  `GapStressPct` constant.
- Surface it in `StrategySettings.tsx` next to the existing GTT % field, with the same kind of
  inline help text.
- Optional refinement once real data exists (see item 5): default it per VIX band from observed
  gap-P&L history rather than a single flat guess for every regime.

## 4. Weekend/event-aware position handling — NOT YET IMPLEMENTED

**Goal:** act on the fact that weekend and pre-holiday overnight variance is elevated relative to
a normal weekday overnight (per the French 1980 weekend-effect / Papagelis variance-risk-premium
research), by tightening exposure specifically going into those windows rather than uniformly.

**Design (needs an operator decision before building — this is a policy choice, not just an
engineering one):**
- A `StrategyConfig` toggle, e.g. `ReduceBeforeWeekend` (bool) or a numeric `WeekendSizeMultiplier`
  applied the same way `vixSizeMultiplier` is applied in item 2, active only for candidates
  generated on a Friday (or the last trading day before a market holiday — would need a trading
  calendar, none exists in the repo today; simplest v1 is Friday-only via `DayOfWeek`).
- Separately (and orthogonally): should `LifecycleManagerWorker`'s profit-take threshold tighten
  for positions already open into a Friday close, to lock in gains before the weekend gap window
  rather than holding for the full target? That changes exit behavior, not just entry sizing —
  needs explicit sign-off since it trades expected return for smoothness.
- Out of scope for now: a full market-holiday calendar and pre-scheduled-macro-event awareness
  (US Fed, Union Budget day, etc.) — no data source for this exists in the codebase yet and it's a
  materially bigger scope than the weekend case.

## 5. Split gap P&L from decay P&L — NOT YET IMPLEMENTED

**Goal:** visibility, not risk reduction — quantify how much of a position's (and the fund's)
daily P&L variance is gap-driven noise vs. theta-decay signal, to make tuning items 1, 3, and 4
data-driven instead of guesswork (e.g. is 3.5% the right `GapStressPct`? this would tell you).

**Design:**
- `GreeksSnapshot` rows already capture `UnderlyingSpot` and are persisted per position
  (`Entities.cs:137-150`). On the first snapshot of each trading day for a given position, diff its
  implied MTM against the last snapshot taken before the prior close. That delta is "gap P&L" for
  the day; the rest of the day's movement is "decay/intraday P&L".
- Cheapest home for the computation: `LifecycleManagerWorker`'s existing per-position cycle
  (`Workers.cs`, `EvaluatePositionAsync`) — detect "is this the first evaluation since market
  open" and compute/store the split then, rather than adding a new worker.
  Needs a small new column or table (e.g. `Position.GapPnlToday`, reset daily) or an `Adjustment`-
  style log row — exact storage shape TBD when this is picked up.
- Surface as a stat on the Cockpit (e.g. next to existing P&L figures) — no chart/UI design done
  yet, deferred until the backend split exists.
