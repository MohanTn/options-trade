import { describe, expect, it } from 'vitest';
import { computeRangeBreach, isNewBreach, openingBands, type VegaFlowSample } from './vegaFlowRange';

const HOUR = 3_600_000;
const T0 = Date.parse('2026-07-08T03:45:00Z'); // 09:15 IST

const sample = (minutesAfterOpen: number, weekCe: number, weekPe: number): VegaFlowSample => ({
  atUtc: new Date(T0 + minutesAfterOpen * 60_000).toISOString(),
  weekCe,
  weekPe,
});

describe('openingBands', () => {
  it('returns null for an empty series', () => {
    expect(openingBands([], 2 * HOUR)).toBeNull();
  });

  it('spans each side\'s high/low within the opening window only', () => {
    const points = [
      sample(0, 0, 0),
      sample(30, 4, -2),
      sample(60, -3, 1),
      sample(150, 20, -30), // past the 2h cutoff — must not widen the bands
    ];
    const bands = openingBands(points, 2 * HOUR)!;
    expect(bands.ce).toEqual({ min: -3, max: 4 });
    expect(bands.pe).toEqual({ min: -2, max: 1 });
  });

  it('is incomplete while the series is still inside the window, complete after', () => {
    const forming = [sample(0, 0, 0), sample(60, 1, 1)];
    expect(openingBands(forming, 2 * HOUR)!.complete).toBe(false);
    const frozen = [...forming, sample(121, 1, 1)];
    expect(openingBands(frozen, 2 * HOUR)!.complete).toBe(true);
  });

  it('widens with a longer window', () => {
    const points = [sample(0, 0, 0), sample(150, 9, -9), sample(200, 1, 1)];
    expect(openingBands(points, 2 * HOUR)!.ce.max).toBe(0);
    expect(openingBands(points, 3 * HOUR)!.ce.max).toBe(9);
  });
});

describe('computeRangeBreach', () => {
  const bands = { ce: { min: -3, max: 4 }, pe: { min: -2, max: 1 }, complete: true };

  it('flags a value strictly outside its own band, per side and direction', () => {
    expect(computeRangeBreach(sample(180, 4.1, 0), bands)).toEqual({ ceAbove: true, ceBelow: false, peAbove: false, peBelow: false });
    expect(computeRangeBreach(sample(180, -3.1, 0), bands)).toEqual({ ceAbove: false, ceBelow: true, peAbove: false, peBelow: false });
    expect(computeRangeBreach(sample(180, 0, 1.5), bands)).toEqual({ ceAbove: false, ceBelow: false, peAbove: true, peBelow: false });
    expect(computeRangeBreach(sample(180, 0, -2.5), bands)).toEqual({ ceAbove: false, ceBelow: false, peAbove: false, peBelow: true });
  });

  it('does not flag a value sitting exactly on a band edge', () => {
    const b = computeRangeBreach(sample(180, 4, -2), bands);
    expect(b).toEqual({ ceAbove: false, ceBelow: false, peAbove: false, peBelow: false });
  });

  it('never fires while the range is still forming (latest point defines the band edge)', () => {
    const points = [sample(0, 0, 0), sample(30, 7, -5)]; // 7/-5 are the current extremes
    const forming = openingBands(points, 2 * HOUR)!;
    const b = computeRangeBreach(points[points.length - 1], forming);
    expect(b).toEqual({ ceAbove: false, ceBelow: false, peAbove: false, peBelow: false });
  });
});

describe('isNewBreach', () => {
  const none = { ceAbove: false, ceBelow: false, peAbove: false, peBelow: false };

  it('fires on a fresh crossing', () => {
    expect(isNewBreach(none, { ...none, ceAbove: true })).toBe(true);
    expect(isNewBreach(none, { ...none, peBelow: true })).toBe(true);
  });

  it('stays silent while an existing breach persists', () => {
    const breached = { ...none, ceAbove: true };
    expect(isNewBreach(breached, breached)).toBe(false);
  });

  it('fires when a second side breaches while the first persists', () => {
    const first = { ...none, ceAbove: true };
    expect(isNewBreach(first, { ...first, peBelow: true })).toBe(true);
  });

  it('stays silent when a breach resolves back inside the band', () => {
    expect(isNewBreach({ ...none, ceAbove: true }, none)).toBe(false);
  });
});
