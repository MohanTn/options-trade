// Opening-range logic for the intraday vega-flow chart: each side's band is the
// high/low its vega-change touched inside the session's opening window, and a
// later close outside that band is the "a move is coming" breakout signal that
// drives the chart's sound alert. Kept free of React/DOM so it can be unit-tested.

export type VegaFlowSample = { atUtc: string; weekCe: number; weekPe: number };
export type Band = { min: number; max: number };

export type OpeningBands = {
  ce: Band;
  pe: Band;
  /** True once the series extends past the opening window, i.e. the bands are frozen. */
  complete: boolean;
};

export function openingBands(points: VegaFlowSample[], windowMs: number): OpeningBands | null {
  if (points.length === 0) return null;
  const t0 = new Date(points[0].atUtc).getTime();
  const cutoff = t0 + windowMs;
  // The first point is always <= cutoff, so `opening` is never empty.
  const opening = points.filter(p => new Date(p.atUtc).getTime() <= cutoff);
  return {
    ce: { min: Math.min(...opening.map(p => p.weekCe)), max: Math.max(...opening.map(p => p.weekCe)) },
    pe: { min: Math.min(...opening.map(p => p.weekPe)), max: Math.max(...opening.map(p => p.weekPe)) },
    complete: new Date(points[points.length - 1].atUtc).getTime() > cutoff,
  };
}

export type RangeBreach = { ceAbove: boolean; ceBelow: boolean; peAbove: boolean; peBelow: boolean };

// Strict comparisons matter: while the range is still forming, the latest point is
// itself part of the band (it *is* the new high/low), so equality never reads as a
// breach — no special-casing of the formation window is needed.
export function computeRangeBreach(last: VegaFlowSample, bands: OpeningBands): RangeBreach {
  return {
    ceAbove: last.weekCe > bands.ce.max,
    ceBelow: last.weekCe < bands.ce.min,
    peAbove: last.weekPe > bands.pe.max,
    peBelow: last.weekPe < bands.pe.min,
  };
}

/** True when `next` entered a breach state that `prev` was not already in — the sound edge-trigger. */
export function isNewBreach(prev: RangeBreach, next: RangeBreach): boolean {
  return (
    (next.ceAbove && !prev.ceAbove) || (next.ceBelow && !prev.ceBelow) ||
    (next.peAbove && !prev.peAbove) || (next.peBelow && !prev.peBelow)
  );
}
