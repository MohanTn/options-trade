import { useEffect, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { analysis, type ChainAnalysis, type ExpiryAnalysis, type VegaFlowPoint } from '../api/client';
import { usePlaySound } from '../hooks/useMonitorSounds';
import { color, font, pnlColor, type Tone } from '../theme';
import { Badge, Button, Card, MetricTile, SectionLabel, TableHeader, TableRow, ViewTitle } from '../ui';

const BORDER = `1px solid ${color.border}`;
const STRIKE_COLS = '70px 1fr 1fr 56px 56px 1fr 1fr';

// OI in Indian market shorthand: lakhs / thousands.
function fmtOi(n: number) {
  const a = Math.abs(n);
  if (a >= 1e5) return `${(n / 1e5).toFixed(1)}L`;
  if (a >= 1e3) return `${(n / 1e3).toFixed(1)}K`;
  return n.toString();
}
const fmtSigned = (n: number) => (n > 0 ? `+${fmtOi(n)}` : n < 0 ? fmtOi(n) : '0');
const oiChangeColor = (n: number) => (n > 0 ? color.pos : n < 0 ? color.neg : color.textMuted);

function biasTone(score: number): Tone {
  if (score > 0.15) return 'pos';
  if (score < -0.15) return 'neg';
  return 'neutral';
}

// UI-only plain-English read of the weekly CE/PE vega-flow numbers already shown elsewhere on
// this page (the "Vega Flow (wk)" tile and the chart below). This does NOT feed BiasScore —
// it's a simpler, presentation-layer restatement of the same leading indicator for operators
// who want the one-line takeaway without parsing the pp numbers themselves.
const VEGA_FLAT_PP = 5; // |change| below this counts as "still near the morning starting value"
const VEGA_TILT_PP = 1.5; // inside the flat band, this much CE−PE skew still leans the read

function summarizeVegaRead(ceChangePct: number, peChangePct: number): { label: string; tone: Tone; note: string } {
  const bothFlat = Math.abs(ceChangePct) < VEGA_FLAT_PP && Math.abs(peChangePct) < VEGA_FLAT_PP;
  const diff = ceChangePct - peChangePct;

  if (bothFlat) {
    if (diff > VEGA_TILT_PP) return { label: 'Sideways to Bullish', tone: 'pos', note: 'Both sides are still near the morning level, with calls edging firmer — a mild upward lean inside a range-bound day.' };
    if (diff < -VEGA_TILT_PP) return { label: 'Sideways to Bearish', tone: 'neg', note: 'Both sides are still near the morning level, with puts edging firmer — a mild downward lean inside a range-bound day.' };
    return { label: 'Sideways Market', tone: 'neutral', note: 'Call- and put-side vega are both still near the morning level — no directional flow either way.' };
  }
  if (ceChangePct <= -VEGA_FLAT_PP && peChangePct <= -VEGA_FLAT_PP)
    return { label: 'Good Day for Selling', tone: 'info', note: 'Both call- and put-side vega are decaying versus the morning — a non-directional, theta-friendly session.' };
  if (ceChangePct >= VEGA_FLAT_PP && peChangePct <= -VEGA_FLAT_PP)
    return { label: 'Bullish Trend', tone: 'pos', note: 'Call-side vega is rising while put-side vega decays — calls are being bought, a bullish tilt.' };
  if (ceChangePct <= -VEGA_FLAT_PP && peChangePct >= VEGA_FLAT_PP)
    return { label: 'Bearish Trend', tone: 'neg', note: 'Put-side vega is rising while call-side vega decays — puts are being bought, a bearish tilt.' };

  // Remaining region: anything not covered above — e.g. one side flat while the other moves
  // alone, or both sides rising together. Described by net skew only, since the cause isn't
  // guaranteed the way it is in the four explicit cases.
  return diff > VEGA_TILT_PP
    ? { label: 'Sideways to Bullish', tone: 'pos', note: 'Call-side vega is outpacing put-side vega versus the morning — a mild bullish lean without a clean two-sided signal.' }
    : diff < -VEGA_TILT_PP
      ? { label: 'Sideways to Bearish', tone: 'neg', note: 'Put-side vega is outpacing call-side vega versus the morning — a mild bearish lean without a clean two-sided signal.' }
      : { label: 'Sideways Market', tone: 'neutral', note: 'Call- and put-side vega moves are roughly balanced versus the morning — no clear directional lean.' };
}

export default function ChainAnalysisView({ sessionValid }: { sessionValid: boolean }) {
  const qc = useQueryClient();
  // The API's ChainAnalysisRefresher recomputes the analysis every 60s during market hours,
  // so this poll is a cheap cache read — it just keeps the view in step with the server.
  const { data, error, isFetching } = useQuery<ChainAnalysis>({
    queryKey: ['analysis', 'chain'],
    queryFn: () => analysis.chain().then(r => r.data),
    refetchInterval: 60000,
    enabled: sessionValid,
    retry: false,
  });

  // Bypasses the server-side cache; the fresh result replaces the query's data.
  const rescan = useMutation({
    mutationFn: () => analysis.chain(true).then(r => r.data),
    onSuccess: fresh => qc.setQueryData(['analysis', 'chain'], fresh),
  });

  // Intraday vega-flow series appended by the server on each analysis run.
  const { data: flowSeries } = useQuery<VegaFlowPoint[]>({
    queryKey: ['analysis', 'vega-flow'],
    queryFn: () => analysis.vegaFlow().then(r => r.data),
    refetchInterval: 60000,
    enabled: sessionValid,
    retry: false,
  });

  if (!sessionValid) {
    return <div style={{ color: color.warn, fontSize: '.82rem' }}>⚠ Connect the Kite session to analyse the option chain.</div>;
  }
  // Only replace the view with an error when there is nothing to show — a failed background
  // refetch keeps the last good analysis on screen (its "as of" stamp shows the age).
  if (!data) {
    const err = error ?? rescan.error;
    if (err) {
      const msg = (err as { response?: { data?: { error?: string } } }).response?.data?.error ?? 'Chain analysis failed.';
      return <div style={{ color: color.neg, fontSize: '.82rem' }}>{msg}</div>;
    }
    return <div style={{ color: color.textMuted, fontSize: '.82rem' }}>Scanning option chain…</div>;
  }

  const busy = isFetching || rescan.isPending;

  const asOf = new Date(data.generatedAtUtc).toLocaleTimeString('en-IN', { timeZone: 'Asia/Kolkata', hour12: false, hour: '2-digit', minute: '2-digit' });
  const vegaRead = summarizeVegaRead(data.nearWeek.ceVegaChangePct, data.nearWeek.peVegaChangePct);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
        <ViewTitle title="Option Chain Analysis" style={{ marginBottom: 0 }} />
        <Badge tone={biasTone(data.biasScore)}>{data.biasLabel} · {data.biasScore >= 0 ? '+' : ''}{data.biasScore.toFixed(2)}</Badge>
        <Badge tone={data.termSpreadPct > 0.5 ? 'warn' : 'info'}>{data.termStructure}</Badge>
        <span style={{ marginLeft: 'auto', color: color.textMuted, fontSize: '.72rem', fontFamily: font.mono }}>as of {asOf} IST</span>
        <Button size="sm" variant="secondary" disabled={busy} onClick={() => rescan.mutate()}>
          {busy ? 'Scanning…' : '↻ Rescan'}
        </Button>
        {rescan.isError && !busy && <span style={{ color: color.neg, fontSize: '.7rem' }}>rescan failed</span>}
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(6, 1fr)', gap: 12 }}>
        <MetricTile
          surface="card" label="Vega Flow (wk)"
          value={`${data.nearWeek.ceVegaChangePct - data.nearWeek.peVegaChangePct >= 0 ? '+' : ''}${(data.nearWeek.ceVegaChangePct - data.nearWeek.peVegaChangePct).toFixed(1)}pp`}
          sub={`prem flow ${data.nearWeek.cePremiumChangePct - data.nearWeek.pePremiumChangePct >= 0 ? '+' : ''}${(data.nearWeek.cePremiumChangePct - data.nearWeek.pePremiumChangePct).toFixed(1)}pp vs am`}
          valueColor={pnlColor(data.nearWeek.ceVegaChangePct - data.nearWeek.peVegaChangePct)}
        />
        <MetricTile surface="card" label="NIFTY Spot" value={data.spot.toLocaleString('en-IN', { maximumFractionDigits: 0 })} valueColor={color.text} />
        <MetricTile
          surface="card" label="India VIX" value={data.vix.toFixed(2)}
          sub={data.morningVix > 0 ? `${data.vix < data.morningVix ? '↓' : data.vix > data.morningVix ? '↑' : '='} vs ${data.morningVix.toFixed(2)} at open` : undefined}
          valueColor={data.morningVix > 0 && data.vix > data.morningVix ? color.warn : color.accent}
        />
        <MetricTile surface="card" label="Weekly ATM IV" value={`${(data.nearWeek.atmIv * 100).toFixed(1)}%`} valueColor={color.accent} />
        <MetricTile
          surface="card" label="Expected Move (wk)"
          value={`±${data.nearWeek.expectedMovePct.toFixed(1)}%`}
          sub={`${(data.spot - data.nearWeek.atmStraddle).toLocaleString('en-IN', { maximumFractionDigits: 0 })} – ${(data.spot + data.nearWeek.atmStraddle).toLocaleString('en-IN', { maximumFractionDigits: 0 })}`}
          valueColor={color.text}
        />
        <MetricTile surface="card" label="IV Term Spread" value={`${data.termSpreadPct >= 0 ? '+' : ''}${data.termSpreadPct.toFixed(1)}pp`} sub="weekly − monthly ATM IV" valueColor={data.termSpreadPct > 0 ? color.warn : color.textSub} />
      </div>

      <Card>
        <SectionLabel>Seller's playbook</SectionLabel>
        <div style={{ fontSize: '.82rem', color: color.text, lineHeight: 1.6 }}>{data.sellerPlaybook}</div>
        <div style={{ marginTop: 10, display: 'flex', flexDirection: 'column', gap: 4 }}>
          {data.drivers.map(d => (
            <div key={d} style={{ fontSize: '.74rem', color: color.textSub, display: 'flex', gap: 8 }}>
              <span style={{ color: color.accent }}>▸</span>{d}
            </div>
          ))}
        </div>
      </Card>

      <Card>
        <SectionLabel>Vega flow — Δ Σν vs morning, near week (leading indicator)</SectionLabel>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10, flexWrap: 'wrap' }}>
          <Badge tone={vegaRead.tone}>{vegaRead.label}</Badge>
          <span style={{ fontSize: '.76rem', color: color.textSub }}>{vegaRead.note}</span>
        </div>
        <VegaFlowChart points={flowSeries ?? []} />
      </Card>

      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 16, alignItems: 'start' }}>
        <ExpiryPanel title="Near Week" exp={data.nearWeek} spot={data.spot} />
        <ExpiryPanel title="Near Month" exp={data.nearMonth} spot={data.spot} />
      </div>
    </div>
  );
}

// ─── Intraday vega-flow line chart (inline SVG, no chart lib) ────────────────
// Series colors are categorical identity (CE = accent blue, PE = amber), validated
// CVD-safe against the white surface; green/red stay reserved for P&L semantics.

const CHART = { w: 720, h: 180, padL: 44, padR: 60, padT: 12, padB: 24 } as const;
const CE_COLOR = color.accent;
const PE_COLOR = color.warn;
const THRESHOLD_STORAGE_KEY = 'td_vegaFlowAlertThresholds';
const DEFAULT_THRESHOLDS = { upper: 25, lower: -25 };
const MIN_THRESHOLD_GAP = 1; // pp — keeps the two draggable lines from crossing

const fmtIST = (utc: string) =>
  new Date(utc).toLocaleTimeString('en-IN', { timeZone: 'Asia/Kolkata', hour12: false, hour: '2-digit', minute: '2-digit' });

function loadThresholds(): { upper: number; lower: number } {
  try {
    const raw = localStorage.getItem(THRESHOLD_STORAGE_KEY);
    if (!raw) return DEFAULT_THRESHOLDS;
    const parsed = JSON.parse(raw);
    if (typeof parsed.upper === 'number' && typeof parsed.lower === 'number' && parsed.upper > parsed.lower)
      return parsed;
  } catch {
    // corrupt/foreign localStorage value — fall back to defaults
  }
  return DEFAULT_THRESHOLDS;
}

type BreachState = { ceUpper: boolean; ceLower: boolean; peUpper: boolean; peLower: boolean };
const computeBreach = (last: VegaFlowPoint, threshold: { upper: number; lower: number }): BreachState => ({
  ceUpper: last.weekCe >= threshold.upper, ceLower: last.weekCe <= threshold.lower,
  peUpper: last.weekPe >= threshold.upper, peLower: last.weekPe <= threshold.lower,
});

function VegaFlowChart({ points }: { points: VegaFlowPoint[] }) {
  const svgRef = useRef<SVGSVGElement>(null);
  const [hoverIdx, setHoverIdx] = useState<number | null>(null);
  const [threshold, setThreshold] = useState(loadThresholds);
  const dragRef = useRef<'upper' | 'lower' | null>(null);
  const play = usePlaySound();
  // Edge-trigger memory only — NOT used for rendering (see `breach` below), so the alert fires
  // once per new crossing rather than every poll while a breach persists.
  const prevBreachRef = useRef<BreachState | null>(null);

  // Checked only when new data arrives (not on every drag frame) so dragging the line to a value
  // the series already passed doesn't itself fire an alert — only a genuine new crossing does.
  // `threshold` is intentionally omitted from the deps: this closure still reads its current
  // value (captured fresh each render), so a drag alone can't re-trigger the effect.
  useEffect(() => {
    if (points.length === 0) return;
    const next = computeBreach(points[points.length - 1], threshold);
    const prev = prevBreachRef.current;
    if (prev != null &&
        ((next.ceUpper && !prev.ceUpper) || (next.ceLower && !prev.ceLower) ||
         (next.peUpper && !prev.peUpper) || (next.peLower && !prev.peLower))) {
      play('criticalAlert');
    }
    // First sample after mount just seeds `prev` — an already-breached chart shouldn't alarm on load.
    prevBreachRef.current = next;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [points, play]);

  if (points.length < 2) {
    return (
      <div style={{ color: color.textMuted, fontSize: '.75rem', padding: '18px 0' }}>
        The series builds one point per minute during market hours — check back after the open.
      </div>
    );
  }

  const { w, h, padL, padR, padT, padB } = CHART;
  const t0 = new Date(points[0].atUtc).getTime();
  const t1 = new Date(points[points.length - 1].atUtc).getTime();
  const span = Math.max(t1 - t0, 1);
  const x = (p: VegaFlowPoint) => padL + ((new Date(p.atUtc).getTime() - t0) / span) * (w - padL - padR);

  // Threshold values are folded into the domain so a dragged line is always visible, even past
  // the data's own range — the chart auto-scales to keep both the series and the alert band in view.
  const values = [...points.flatMap(p => [p.weekCe, p.weekPe]), threshold.upper, threshold.lower];
  let yMin = Math.min(0, ...values), yMax = Math.max(0, ...values);
  const pad = Math.max((yMax - yMin) * 0.12, 0.5);
  yMin -= pad; yMax += pad;
  const y = (v: number) => padT + ((yMax - v) / (yMax - yMin)) * (h - padT - padB);
  const yInv = (py: number) => yMax - ((py - padT) / (h - padT - padB)) * (yMax - yMin);

  const line = (get: (p: VegaFlowPoint) => number) => points.map(p => `${x(p).toFixed(1)},${y(get(p)).toFixed(1)}`).join(' ');

  const last = points[points.length - 1];
  // Direct end labels; nudge apart when the lines end close together.
  let ceLabelY = y(last.weekCe), peLabelY = y(last.weekPe);
  if (Math.abs(ceLabelY - peLabelY) < 12) {
    const ceOnTop = ceLabelY <= peLabelY;
    const mid = (ceLabelY + peLabelY) / 2;
    ceLabelY = mid + (ceOnTop ? -6 : 6);
    peLabelY = mid + (ceOnTop ? 6 : -6);
  }

  const onMove = (e: React.MouseEvent<SVGSVGElement>) => {
    if (dragRef.current) return; // dragging owns the pointer via capture; skip hover recompute
    const rect = svgRef.current?.getBoundingClientRect();
    if (!rect) return;
    const vx = ((e.clientX - rect.left) / rect.width) * w;
    let best = 0, bestD = Infinity;
    points.forEach((p, i) => { const d = Math.abs(x(p) - vx); if (d < bestD) { bestD = d; best = i; } });
    setHoverIdx(best);
  };

  const pixelToViewBoxY = (clientY: number) => {
    const rect = svgRef.current?.getBoundingClientRect();
    return rect ? ((clientY - rect.top) / rect.height) * h : padT;
  };

  const startDrag = (which: 'upper' | 'lower') => (e: React.PointerEvent<SVGLineElement>) => {
    e.currentTarget.setPointerCapture(e.pointerId);
    dragRef.current = which;
  };
  const onDragMove = (e: React.PointerEvent<SVGLineElement>) => {
    if (!dragRef.current) return;
    const raw = Math.round(yInv(pixelToViewBoxY(e.clientY)) * 10) / 10;
    const clamped = Math.max(-100, Math.min(100, raw));
    setThreshold(prev =>
      dragRef.current === 'upper'
        ? { ...prev, upper: Math.max(clamped, prev.lower + MIN_THRESHOLD_GAP) }
        : { ...prev, lower: Math.min(clamped, prev.upper - MIN_THRESHOLD_GAP) });
  };
  const endDrag = (e: React.PointerEvent<SVGLineElement>) => {
    if (!dragRef.current) return;
    e.currentTarget.releasePointerCapture(e.pointerId);
    dragRef.current = null;
    localStorage.setItem(THRESHOLD_STORAGE_KEY, JSON.stringify(threshold));
  };
  // Keyboard alternative to dragging: Arrow = 0.5pp, Shift+Arrow = 5pp. Reads the closed-over
  // `threshold` directly (safe for a discrete keypress, unlike a rapid pointer drag) so the
  // localStorage write stays a plain statement instead of a side effect inside the setState
  // updater, which React may invoke twice under StrictMode.
  const onHandleKeyDown = (which: 'upper' | 'lower') => (e: React.KeyboardEvent<SVGCircleElement>) => {
    if (e.key !== 'ArrowUp' && e.key !== 'ArrowDown') return;
    e.preventDefault();
    const step = (e.key === 'ArrowUp' ? 1 : -1) * (e.shiftKey ? 5 : 0.5);
    const next = which === 'upper'
      ? { ...threshold, upper: Math.max(-100, Math.min(100, Math.round((threshold.upper + step) * 10) / 10)) }
      : { ...threshold, lower: Math.max(-100, Math.min(100, Math.round((threshold.lower + step) * 10) / 10)) };
    next.upper = Math.max(next.upper, next.lower + MIN_THRESHOLD_GAP);
    next.lower = Math.min(next.lower, next.upper - MIN_THRESHOLD_GAP);
    setThreshold(next);
    localStorage.setItem(THRESHOLD_STORAGE_KEY, JSON.stringify(next));
  };

  const hover = hoverIdx != null ? points[hoverIdx] : null;
  const ticks = [...new Set([yMax - pad, 0, yMin + pad].map(v => Math.round(v * 10) / 10))];
  const midT = points[Math.floor(points.length / 2)];
  // Skip the middle time label while the series is too short for it to clear the edge labels.
  const timeLabels = points.length >= 6 ? [points[0], midT, last] : [points[0], last];

  // Computed live from the current render's data + threshold — never lags behind a poll cycle
  // the way reading the edge-trigger ref during render would (that ref only updates post-render).
  const breach = computeBreach(last, threshold);
  const anyBreached = breach.ceUpper || breach.ceLower || breach.peUpper || breach.peLower;

  const thresholdLine = (which: 'upper' | 'lower', value: number, breached: boolean) => (
    <g key={which}>
      <line
        x1={padL} x2={w - padR} y1={y(value)} y2={y(value)}
        stroke={breached ? color.neg : color.textFaint} strokeWidth={1.5} strokeDasharray="5 3"
      />
      {/* Wide transparent hit-area for an easy grab, per the ≥8px touch-target rule. */}
      <line
        x1={padL} x2={w - padR} y1={y(value)} y2={y(value)}
        stroke="transparent" strokeWidth={16} style={{ cursor: 'ns-resize' }}
        onPointerDown={startDrag(which)} onPointerMove={onDragMove} onPointerUp={endDrag} onPointerCancel={endDrag}
      />
      <circle
        cx={padL + 8} cy={y(value)} r={4} fill={color.surface} stroke={breached ? color.neg : color.textSub} strokeWidth={1.5}
        style={{ cursor: 'ns-resize' }} tabIndex={0} onKeyDown={onHandleKeyDown(which)}
        role="slider" aria-label={`${which === 'upper' ? 'Upper' : 'Lower'} vega-flow alert threshold`}
        aria-valuemin={-100} aria-valuemax={100} aria-valuenow={value} aria-valuetext={`${value.toFixed(1)} percentage points`}
      />
      <text x={padL + 16} y={y(value) - 4} fontSize={10} fontWeight={700} fill={breached ? color.neg : color.textSub} fontFamily={font.mono}>
        {value >= 0 ? '+' : ''}{value.toFixed(1)}pp
      </text>
    </g>
  );

  return (
    <div style={{ position: 'relative' }}>
      {/* No role="img": the sliders below are real interactive descendants, which "img" disallows exposing to AT. */}
      <svg
        ref={svgRef} viewBox={`0 0 ${w} ${h}`} style={{ width: '100%', display: 'block' }}
        aria-label="Intraday change in call-side and put-side vega sums versus the morning baseline, with two draggable alert-threshold sliders"
        onMouseMove={onMove} onMouseLeave={() => setHoverIdx(null)}
      >
        {/* Recessive grid: one line per tick; the zero baseline is slightly stronger. */}
        {ticks.map(v => (
          <g key={v}>
            <line x1={padL} x2={w - padR} y1={y(v)} y2={y(v)} stroke={v === 0 ? color.borderStrong : color.border} strokeWidth={1} />
            <text x={padL - 6} y={y(v) + 3} textAnchor="end" fontSize={10} fill={color.textMuted} fontFamily={font.mono}>
              {v > 0 ? `+${v}` : v}%
            </text>
          </g>
        ))}
        {timeLabels.map((p, i) => (
          <text
            key={i} y={h - 8} fontSize={10} fill={color.textMuted} fontFamily={font.mono}
            x={i === 0 ? padL : i === timeLabels.length - 1 ? x(last) : x(p)}
            textAnchor={i === 0 ? 'start' : i === timeLabels.length - 1 ? 'end' : 'middle'}
          >
            {fmtIST(p.atUtc)}
          </text>
        ))}

        <polyline points={line(p => p.weekCe)} fill="none" stroke={CE_COLOR} strokeWidth={2} strokeLinejoin="round" />
        <polyline points={line(p => p.weekPe)} fill="none" stroke={PE_COLOR} strokeWidth={2} strokeLinejoin="round" />

        {/* Direct end labels in text ink; the adjacent line end carries the identity color. */}
        <text x={x(last) + 6} y={ceLabelY + 3} fontSize={10} fill={color.textSub} fontFamily={font.sans}>CE ν</text>
        <text x={x(last) + 6} y={peLabelY + 3} fontSize={10} fill={color.textSub} fontFamily={font.sans}>PE ν</text>

        {thresholdLine('upper', threshold.upper, breach.ceUpper || breach.peUpper)}
        {thresholdLine('lower', threshold.lower, breach.ceLower || breach.peLower)}

        {hover && (
          <g>
            <line x1={x(hover)} x2={x(hover)} y1={padT} y2={h - padB} stroke={color.borderStrong} strokeWidth={1} />
            <circle cx={x(hover)} cy={y(hover.weekCe)} r={4} fill={CE_COLOR} stroke={color.surface} strokeWidth={2} />
            <circle cx={x(hover)} cy={y(hover.weekPe)} r={4} fill={PE_COLOR} stroke={color.surface} strokeWidth={2} />
          </g>
        )}
      </svg>

      {hover && (
        <div style={{
          position: 'absolute', top: 4,
          left: `${(x(hover) / w) * 100}%`,
          transform: x(hover) > w * 0.6 ? 'translateX(calc(-100% - 10px))' : 'translateX(10px)',
          background: color.surface, border: `1px solid ${color.border}`, borderRadius: 6,
          boxShadow: '0 4px 16px rgba(15,23,42,.12)', padding: '6px 10px', pointerEvents: 'none', whiteSpace: 'nowrap',
        }}>
          <div style={{ fontSize: '.66rem', color: color.textMuted, fontFamily: font.mono }}>{fmtIST(hover.atUtc)} IST</div>
          {([['CE ν', hover.weekCe, CE_COLOR], ['PE ν', hover.weekPe, PE_COLOR]] as const).map(([k, v, c]) => (
            <div key={k} style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: '.72rem' }}>
              <span style={{ width: 8, height: 8, borderRadius: 2, background: c }} />
              <span style={{ color: color.textSub }}>{k}</span>
              <span style={{ color: color.text, fontFamily: font.mono, marginLeft: 'auto' }}>{v >= 0 ? '+' : ''}{v.toFixed(1)}%</span>
            </div>
          ))}
        </div>
      )}

      <div style={{ display: 'flex', gap: 16, marginTop: 6, fontSize: '.72rem', alignItems: 'center', flexWrap: 'wrap' }}>
        {([['CE ν (call side)', last.weekCe, CE_COLOR], ['PE ν (put side)', last.weekPe, PE_COLOR]] as const).map(([k, v, c]) => (
          <span key={k} style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            <span style={{ width: 8, height: 8, borderRadius: 2, background: c }} />
            <span style={{ color: color.textSub }}>{k}</span>
            <span style={{ color: color.text, fontFamily: font.mono }}>{v >= 0 ? '+' : ''}{v.toFixed(1)}%</span>
          </span>
        ))}
        {anyBreached && <Badge tone="neg">⚠ threshold breached</Badge>}
        <span style={{ marginLeft: anyBreached ? undefined : 'auto', color: color.textMuted, fontSize: '.66rem' }}>
          drag the dashed lines to set alert levels (±25pp default) · side rising = being bought
        </span>
        {(threshold.upper !== DEFAULT_THRESHOLDS.upper || threshold.lower !== DEFAULT_THRESHOLDS.lower) && (
          <Button
            size="sm" variant="ghost"
            onClick={() => { setThreshold(DEFAULT_THRESHOLDS); localStorage.setItem(THRESHOLD_STORAGE_KEY, JSON.stringify(DEFAULT_THRESHOLDS)); }}
          >
            Reset ±25pp
          </Button>
        )}
      </div>
    </div>
  );
}

function ExpiryPanel({ title, exp, spot }: { title: string; exp: ExpiryAnalysis; spot: number }) {
  const atmStrike = exp.strikes.length > 0
    ? exp.strikes.reduce((best, r) => (Math.abs(r.strike - spot) < Math.abs(best - spot) ? r.strike : best), exp.strikes[0].strike)
    : 0;

  const wallNote = (shift: number) => (shift !== 0 ? ` (${shift > 0 ? '↑' : '↓'}${Math.abs(shift)})` : '');
  const stats: { k: string; v: string; c?: string }[] = [
    { k: 'ATM IV', v: `${(exp.atmIv * 100).toFixed(1)}%`, c: color.accent },
    { k: 'CE ν Σ (Δ am)', v: `₹${exp.ceVegaSum.toFixed(0)} (${exp.ceVegaChangePct >= 0 ? '+' : ''}${exp.ceVegaChangePct.toFixed(1)}%)`, c: pnlColor(exp.ceVegaChangePct) },
    { k: 'PE ν Σ (Δ am)', v: `₹${exp.peVegaSum.toFixed(0)} (${exp.peVegaChangePct >= 0 ? '+' : ''}${exp.peVegaChangePct.toFixed(1)}%)`, c: pnlColor(exp.peVegaChangePct) },
    { k: 'OTM CE Σ (Δ am)', v: `₹${exp.cePremiumSum.toFixed(0)} (${exp.cePremiumChangePct >= 0 ? '+' : ''}${exp.cePremiumChangePct.toFixed(1)}%)`, c: pnlColor(exp.cePremiumChangePct) },
    { k: 'OTM PE Σ (Δ am)', v: `₹${exp.pePremiumSum.toFixed(0)} (${exp.pePremiumChangePct >= 0 ? '+' : ''}${exp.pePremiumChangePct.toFixed(1)}%)`, c: pnlColor(exp.pePremiumChangePct) },
    { k: 'ATM straddle / move', v: `₹${exp.atmStraddle.toFixed(0)} · ±${exp.expectedMovePct.toFixed(1)}%` },
    { k: 'Straddle drift today', v: `${exp.straddleChangePct >= 0 ? '+' : ''}${exp.straddleChangePct.toFixed(1)}%`, c: exp.straddleChangePct > 5 ? color.neg : exp.straddleChangePct < -5 ? color.pos : color.textSub },
    { k: 'PCR (OI)', v: exp.pcrOi.toFixed(2), c: exp.pcrOi > 1 ? color.pos : color.neg },
    { k: 'PCR (Vol)', v: exp.pcrVolume.toFixed(2) },
    { k: 'Max Pain', v: exp.maxPain.toLocaleString('en-IN') },
    { k: 'Support (PE wall)', v: exp.supportStrike.toLocaleString('en-IN') + wallNote(exp.supportShift), c: color.pos },
    { k: 'Resistance (CE wall)', v: exp.resistanceStrike.toLocaleString('en-IN') + wallNote(exp.resistanceShift), c: color.neg },
    { k: 'ΔOI PE today', v: fmtSigned(exp.peOiChange), c: oiChangeColor(exp.peOiChange) },
    { k: 'ΔOI CE today', v: fmtSigned(exp.ceOiChange), c: oiChangeColor(exp.ceOiChange) },
    { k: 'IV skew (put−call)', v: `${exp.skewPct >= 0 ? '+' : ''}${exp.skewPct.toFixed(1)}pp` },
  ];

  return (
    <Card padded={false} style={{ overflow: 'hidden' }}>
      <div style={{ padding: '10px 16px', borderBottom: BORDER, display: 'flex', alignItems: 'center', gap: 10 }}>
        <span style={{ fontSize: '.8rem', fontWeight: 700, color: color.text }}>{title}</span>
        <span style={{ color: color.textSub, fontSize: '.75rem', fontFamily: font.mono }}>{exp.expiry.slice(0, 10)} · {exp.dte}d</span>
        <Badge tone={exp.isMonthly ? 'info' : 'accent'}>{exp.isMonthly ? 'MONTHLY' : 'WEEKLY'}</Badge>
      </div>

      <div style={{ padding: '8px 16px', borderBottom: BORDER }}>
        {stats.map(s => (
          <div key={s.k} style={{ display: 'flex', justifyContent: 'space-between', fontSize: '.74rem', padding: '3px 0' }}>
            <span style={{ color: color.textSub }}>{s.k}</span>
            <span style={{ color: s.c ?? color.text, fontFamily: font.mono, fontWeight: 600 }}>{s.v}</span>
          </div>
        ))}
      </div>

      <div style={{ padding: '0 16px 8px' }}>
        <TableHeader columns={STRIKE_COLS} cells={['Strike', 'CE OI', 'ΔCE', 'CE IV', 'PE IV', 'ΔPE', 'PE OI']} />
        {exp.strikes.map(r => {
          const isAtm = r.strike === atmStrike;
          const marker = r.strike === exp.supportStrike ? 'S' : r.strike === exp.resistanceStrike ? 'R' : null;
          return (
            <TableRow key={r.strike} columns={STRIKE_COLS} style={isAtm ? { background: color.accentBg } : undefined}>
              <span style={{ fontFamily: font.mono, fontWeight: isAtm ? 700 : 500, color: color.text }}>
                {r.strike.toLocaleString('en-IN')}{marker && <span style={{ color: marker === 'S' ? color.pos : color.neg, marginLeft: 4, fontSize: '.65rem' }}>{marker}</span>}
              </span>
              <span style={{ fontFamily: font.mono, color: color.textSub }}>{fmtOi(r.ceOi)}</span>
              <span style={{ fontFamily: font.mono, color: oiChangeColor(r.ceOiChange), fontSize: '.7rem' }}>{fmtSigned(r.ceOiChange)}</span>
              <span style={{ fontFamily: font.mono, color: color.textMuted, fontSize: '.7rem' }}>{r.ceIv > 0 ? (r.ceIv * 100).toFixed(1) : '—'}</span>
              <span style={{ fontFamily: font.mono, color: color.textMuted, fontSize: '.7rem' }}>{r.peIv > 0 ? (r.peIv * 100).toFixed(1) : '—'}</span>
              <span style={{ fontFamily: font.mono, color: oiChangeColor(r.peOiChange), fontSize: '.7rem' }}>{fmtSigned(r.peOiChange)}</span>
              <span style={{ fontFamily: font.mono, color: color.textSub }}>{fmtOi(r.peOi)}</span>
            </TableRow>
          );
        })}
      </div>
    </Card>
  );
}
