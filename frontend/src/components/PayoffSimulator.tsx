import { useCallback, useMemo, useState } from 'react';
import type { Position } from '../api/client';
import { color, font } from '../theme';
import { Modal } from '../ui';

const R = 0.065;            // risk-free rate, matches the backend Black-76 assumption
const DEFAULT_IV = 0.15;    // fallback when a leg has no live price to imply vol from
const SAMPLES = 121;        // payoff-curve resolution

// --- Black-Scholes (index options: forward ≈ spot, so plain BS with rate r is adequate here) ---
function normCdf(x: number) {
  const t = 1 / (1 + 0.2316419 * Math.abs(x));
  const d = 0.3989422804 * Math.exp(-x * x / 2);
  const p = d * t * (0.3193815 + t * (-0.3565638 + t * (1.781477937 + t * (-1.821255978 + t * 1.330274429))));
  return x > 0 ? 1 - p : p;
}
function bsPrice(S: number, K: number, tau: number, iv: number, isCall: boolean) {
  if (tau <= 0 || iv <= 0) return Math.max(0, isCall ? S - K : K - S); // intrinsic at/after expiry
  const sqrtT = Math.sqrt(tau);
  const d1 = (Math.log(S / K) + (R + iv * iv / 2) * tau) / (iv * sqrtT);
  const d2 = d1 - iv * sqrtT;
  return isCall
    ? S * normCdf(d1) - K * Math.exp(-R * tau) * normCdf(d2)
    : K * Math.exp(-R * tau) * normCdf(-d2) - S * normCdf(-d1);
}
// Bisection IV from a live price; null if the price can't be inverted.
function impliedVol(price: number, S: number, K: number, tau: number, isCall: boolean) {
  if (price <= 0 || tau <= 0 || S <= 0) return null;
  let lo = 0.01, hi = 3;
  for (let i = 0; i < 60; i++) {
    const mid = (lo + hi) / 2;
    if (bsPrice(S, K, tau, mid, isCall) > price) hi = mid; else lo = mid;
  }
  return (lo + hi) / 2;
}

const fmtInr = (n: number) => `${n < 0 ? '-' : ''}₹${Math.abs(Math.round(n)).toLocaleString('en-IN')}`;
const DAY = 86400000;

/**
 * Per-position payoff simulator: plots the P&L of the position's legs against the NIFTY spot, both
 * at expiry and on an operator-chosen target date, with sliders for the target date (DTE) and the
 * NIFTY level. Pricing is done client-side so the sliders respond instantly.
 */
export default function PayoffSimulator(
  { position, spot, lotSize, vix, onClose }:
  { position: Position; spot: number | null; lotSize: number; vix: number | null; onClose: () => void },
) {
  // Far expiry = max across all leg expiry dates. For single-expiry strategies all legs share the
  // same date so this equals position.expiryDate. For calendars/diagonals, position.expiryDate is
  // the near (short) leg expiry; the BUY legs carry a later date that must drive the slider range.
  const farExpiryMs = position.legs.reduce(
    (max, l) => (l.expiryDate ? Math.max(max, new Date(l.expiryDate).getTime()) : max),
    new Date(position.expiryDate).getTime(),
  );
  const currentDte = Math.max(0, Math.round((farExpiryMs - Date.now()) / DAY));

  // Resolve each leg's contract size, implied vol, and how many days earlier it expires than the far leg.
  // For single-expiry strategies (strangles, iron condors) daysFromFarExpiry is 0 for all legs — no change.
  // For calendars/diagonals the short near leg expires daysFromFarExpiry days before the far (long) leg,
  // so at any slider position `dte` the near leg's tau is max(0, dte - daysFromFarExpiry)/365.
  const { legs, center } = useMemo(() => {
    const fallbackIv = vix && vix > 0 ? vix / 100 : DEFAULT_IV;
    const meanStrike = position.legs.reduce((s, l) => s + l.strike, 0) / Math.max(position.legs.length, 1);
    const c = spot && spot > 0 ? spot : meanStrike;
    const ls = position.legs.map(l => {
      const legExpiryMs = l.expiryDate ? new Date(l.expiryDate).getTime() : farExpiryMs;
      const daysFromFarExpiry = Math.max(0, Math.round((farExpiryMs - legExpiryMs) / DAY));
      const legDte0 = Math.max(0, Math.round((legExpiryMs - Date.now()) / DAY));
      const isCall = l.optionType === 'CE';
      const iv = impliedVol(l.currentPrice, c, l.strike, legDte0 / 365, isCall) ?? fallbackIv;
      return {
        isCall,
        isSell: l.side === 'Sell',
        strike: l.strike,
        entryPrice: l.entryPrice,
        qty: l.lots * lotSize,
        iv,
        daysFromFarExpiry,
      };
    });
    return { legs: ls, center: c };
  }, [position.legs, spot, lotSize, vix, farExpiryMs]);

  // 1 standard-deviation move over the remaining life, from the average leg IV.
  const sd = useMemo(() => {
    const avgIv = legs.reduce((s, l) => s + l.iv, 0) / Math.max(legs.length, 1);
    return center * avgIv * Math.sqrt(Math.max(currentDte, 1) / 365);
  }, [legs, center, currentDte]);

  const [sMin, sMax] = [Math.round(center * 0.88), Math.round(center * 1.12)];
  const [targetDte, setTargetDte] = useState(currentDte);
  const [targetSpot, setTargetSpot] = useState(Math.round(center));

  const pnlAt = useCallback((S: number, dte: number) => {
    return legs.reduce((sum, l) => {
      const tau = Math.max(0, dte - l.daysFromFarExpiry) / 365;
      const v = bsPrice(S, l.strike, tau, l.iv, l.isCall);
      const per = l.isSell ? l.entryPrice - v : v - l.entryPrice;
      return sum + per * l.qty;
    }, 0);
  }, [legs]);

  // Sample both curves across the price range; recompute the target curve when the date slider moves.
  const { expiry, target, yAbs } = useMemo(() => {
    const exp: { x: number; y: number }[] = [];
    const tgt: { x: number; y: number }[] = [];
    let maxAbs = 1;
    for (let i = 0; i < SAMPLES; i++) {
      const S = sMin + (sMax - sMin) * (i / (SAMPLES - 1));
      const ey = pnlAt(S, 0);
      const ty = pnlAt(S, targetDte);
      exp.push({ x: S, y: ey });
      tgt.push({ x: S, y: ty });
      maxAbs = Math.max(maxAbs, Math.abs(ey), Math.abs(ty));
    }
    return { expiry: exp, target: tgt, yAbs: maxAbs * 1.1 };
  }, [pnlAt, targetDte, sMin, sMax]);

  const projected = pnlAt(targetSpot, targetDte);
  const movePct = ((targetSpot - center) / center) * 100;
  const targetDate = new Date(farExpiryMs - targetDte * DAY);

  // --- SVG geometry ---
  const W = 760, H = 320, PL = 52, PR = 16, PT = 22, PB = 30;
  const innerW = W - PL - PR, innerH = H - PT - PB;
  const sx = (S: number) => PL + ((S - sMin) / (sMax - sMin)) * innerW;
  const sy = (p: number) => PT + (1 - (p + yAbs) / (2 * yAbs)) * innerH;
  const toLine = (pts: { x: number; y: number }[]) => pts.map(p => `${sx(p.x).toFixed(1)},${sy(p.y).toFixed(1)}`).join(' ');

  // Split the expiry curve into profit (green) / loss (red) segments at each zero crossing.
  const segments = useMemo(() => {
    const out: { profit: boolean; pts: { x: number; y: number }[] }[] = [];
    let pts = [expiry[0]]; let profit = expiry[0].y >= 0;
    for (let i = 1; i < expiry.length; i++) {
      const a = expiry[i - 1], b = expiry[i];
      if ((a.y >= 0) !== (b.y >= 0)) {
        const t = Math.abs(a.y) / (Math.abs(a.y) + Math.abs(b.y) || 1);
        const cross = { x: a.x + (b.x - a.x) * t, y: 0 };
        pts.push(cross); out.push({ profit, pts }); pts = [cross, b]; profit = b.y >= 0;
      } else pts.push(b);
    }
    out.push({ profit, pts });
    return out;
  }, [expiry]);

  const priceTicks = Array.from({ length: 6 }, (_, i) => Math.round((sMin + (sMax - sMin) * (i / 5)) / 50) * 50);
  const pnlTicks = [yAbs, yAbs / 2, 0, -yAbs / 2, -yAbs];

  return (
    <Modal
      title="Payoff Graph"
      subtitle={`${position.strategy} · expiry ${fmtExpiry(position.expiryDate)}`}
      onClose={onClose}
      closeOnBackdrop
      width={840}
    >
      <div style={{ padding: '0 20px 18px' }}>
        <div style={s.legend}>
          <span><i style={{ ...s.swatch, background: color.pos }} /> On Expiry</span>
          <span><i style={{ ...s.swatch, background: color.accent }} /> On Target Date ({targetDte}d)</span>
          <span style={{ marginLeft: 'auto', color: color.textSub }}>Spot {Math.round(center).toLocaleString('en-IN')}{!spot && ' (est.)'}</span>
        </div>

        <svg width={W} height={H} style={{ display: 'block', margin: '0 auto', maxWidth: '100%' }}>
          {/* P&L grid + axis labels */}
          {pnlTicks.map((p, i) => (
            <g key={i}>
              <line x1={PL} y1={sy(p)} x2={W - PR} y2={sy(p)} stroke={p === 0 ? color.borderStrong : color.border} strokeWidth={1} />
              <text x={PL - 6} y={sy(p) + 3} textAnchor="end" style={s.axisLabel}>{p === 0 ? '0' : fmtInr(p)}</text>
            </g>
          ))}
          {priceTicks.map((px, i) => (
            <text key={i} x={sx(px)} y={H - 10} textAnchor="middle" style={s.axisLabel}>{px.toLocaleString('en-IN')}</text>
          ))}

          {/* ±1SD bands */}
          {[center - sd, center + sd].map((sv, i) => sv > sMin && sv < sMax && (
            <g key={i}>
              <line x1={sx(sv)} y1={PT} x2={sx(sv)} y2={H - PB} stroke={color.borderStrong} strokeDasharray="4 4" />
              <text x={sx(sv)} y={PT - 6} textAnchor="middle" style={{ ...s.axisLabel, fill: color.textMuted }}>{i === 0 ? '-1SD' : '+1SD'}</text>
            </g>
          ))}

          {/* Expiry curve: shaded area + two-tone line */}
          {segments.map((seg, i) => (
            <g key={i}>
              <polygon
                points={`${sx(seg.pts[0].x)},${sy(0)} ${toLine(seg.pts)} ${sx(seg.pts[seg.pts.length - 1].x)},${sy(0)}`}
                fill={seg.profit ? color.pos : color.neg} fillOpacity={0.1}
              />
              <polyline points={toLine(seg.pts)} fill="none" stroke={seg.profit ? color.pos : color.neg} strokeWidth={2} />
            </g>
          ))}

          {/* Target-date curve */}
          <polyline points={toLine(target)} fill="none" stroke={color.accent} strokeWidth={2} />

          {/* Current spot line */}
          <line x1={sx(center)} y1={PT} x2={sx(center)} y2={H - PB} stroke={color.pos} strokeWidth={1} strokeOpacity={0.5} />

          {/* NIFTY-target readout line + projected-P&L marker */}
          <line x1={sx(targetSpot)} y1={PT} x2={sx(targetSpot)} y2={H - PB} stroke={color.text} strokeDasharray="3 3" />
          <circle cx={sx(targetSpot)} cy={sy(projected)} r={4} fill={color.accent} />
        </svg>

        {/* Projected P&L badge */}
        <div style={{ textAlign: 'center', marginTop: -6, marginBottom: 10 }}>
          <span style={{ ...s.projBadge, background: projected >= 0 ? color.pos : color.neg }}>
            Projected P&L at {targetSpot.toLocaleString('en-IN')}: {fmtInr(projected)} ({movePct >= 0 ? '+' : ''}{movePct.toFixed(1)}% move)
          </span>
        </div>

        {/* Controls */}
        <div style={s.controls}>
          <div style={s.control}>
            <div style={s.controlTop}>
              <span style={s.controlLabel}>NIFTY Target</span>
              <span style={{ color: movePct >= 0 ? color.pos : color.neg, fontWeight: 700 }}>{movePct >= 0 ? '+' : ''}{movePct.toFixed(1)}%</span>
              <div style={s.stepper}>
                <button style={s.stepBtn} onClick={() => setTargetSpot(v => Math.max(sMin, v - 50))}>−</button>
                <span style={s.stepVal}>{targetSpot.toLocaleString('en-IN')}</span>
                <button style={s.stepBtn} onClick={() => setTargetSpot(v => Math.min(sMax, v + 50))}>+</button>
              </div>
            </div>
            <input type="range" min={sMin} max={sMax} step={5} value={targetSpot}
              onChange={e => setTargetSpot(Number(e.target.value))} style={s.slider} />
            <button style={s.reset} onClick={() => setTargetSpot(Math.round(center))}>Reset</button>
          </div>

          <div style={s.control}>
            <div style={s.controlTop}>
              <span style={s.controlLabel}>Date</span>
              <span style={{ color: color.accent, fontWeight: 700 }}>{targetDte}d to expiry</span>
              <span style={{ marginLeft: 'auto', color: color.textSub, fontSize: '.78rem' }}>{fmtDate(targetDate)}</span>
            </div>
            <input type="range" min={0} max={currentDte} step={1} value={targetDte}
              onChange={e => setTargetDte(Number(e.target.value))} style={s.slider} />
            <button style={s.reset} onClick={() => setTargetDte(currentDte)}>Reset</button>
          </div>
        </div>
      </div>
    </Modal>
  );
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const fmtExpiry = (d: string) => { const x = new Date(d); return `${x.getDate()} ${MONTHS[x.getMonth()]} ${x.getFullYear()}`; };
const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const fmtDate = (x: Date) => `${DAYS[x.getDay()]}, ${x.getDate()} ${MONTHS[x.getMonth()]}`;

const s: Record<string, React.CSSProperties> = {
  legend: { display: 'flex', gap: 18, alignItems: 'center', padding: '12px 0', fontSize: '.78rem', color: color.textSub },
  swatch: { display: 'inline-block', width: 14, height: 3, borderRadius: 2, marginRight: 6, verticalAlign: 'middle' },
  axisLabel: { fontSize: '10px', fill: color.textSub, fontFamily: font.sans },
  projBadge: { color: '#fff', fontWeight: 700, fontSize: '.82rem', padding: '5px 14px', borderRadius: 16 },
  controls: { display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 24, padding: '4px 4px 0' },
  control: { display: 'flex', flexDirection: 'column', gap: 6 },
  controlTop: { display: 'flex', alignItems: 'center', gap: 10, fontSize: '.85rem' },
  controlLabel: { fontWeight: 700, color: color.text },
  stepper: { marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 8, border: `1px solid ${color.border}`, borderRadius: 8, padding: '2px 6px' },
  stepBtn: { border: 'none', background: 'transparent', cursor: 'pointer', fontSize: '1rem', color: color.accent, width: 18, lineHeight: 1 },
  stepVal: { fontVariantNumeric: 'tabular-nums', fontWeight: 600, color: color.text, minWidth: 56, textAlign: 'center' },
  slider: { width: '100%', accentColor: color.accent },
  reset: { alignSelf: 'flex-start', background: 'none', border: 'none', color: color.accent, cursor: 'pointer', fontSize: '.76rem', padding: 0 },
};
