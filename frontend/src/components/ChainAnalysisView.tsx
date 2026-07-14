import { useEffect, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { analysis, portfolio, type ChainAnalysis, type ExpiryAnalysis, type VegaFlowPoint } from '../api/client';
import { usePlaySound } from '../hooks/useMonitorSounds';
import { computeRangeBreach, isNewBreach, openingBands, type RangeBreach } from '../lib/vegaFlowRange';
import { color, font, pnlColor, type Tone } from '../theme';
import { Badge, Button, Card, MetricTile, Modal, SectionLabel, TableHeader, TableRow, ViewTitle } from '../ui';

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

  // Portfolio Greeks for unrealized P&L display.
  const { data: portfolioGreeks } = useQuery({
    queryKey: ['portfolio', 'greeks'],
    queryFn: () => portfolio.greeks().then(r => r.data),
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

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 12 }}>
        <MetricTile
          surface="card" label="Vega Flow (wk)"
          value={`${data.nearWeek.ceVegaChangePct - data.nearWeek.peVegaChangePct >= 0 ? '+' : ''}${(data.nearWeek.ceVegaChangePct - data.nearWeek.peVegaChangePct).toFixed(1)}pp`}
          sub={`prem flow ${data.nearWeek.cePremiumChangePct - data.nearWeek.pePremiumChangePct >= 0 ? '+' : ''}${(data.nearWeek.cePremiumChangePct - data.nearWeek.pePremiumChangePct).toFixed(1)}pp vs am`}
          valueColor={pnlColor(data.nearWeek.ceVegaChangePct - data.nearWeek.peVegaChangePct)}
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

      <VegaFlowSection points={flowSeries ?? []} vegaRead={vegaRead} unrealisedPnl={portfolioGreeks?.unrealisedPnl} />

      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 16, alignItems: 'start' }}>
        <ExpiryPanel title="Near Week" exp={data.nearWeek} spot={data.spot} />
        <ExpiryPanel title="Near Month" exp={data.nearMonth} spot={data.spot} />
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
    </div>
  );
}

// ─── Intraday vega-flow chart: three stacked lanes (inline SVG, no chart lib) ─
// CE ν on top, NIFTY spot in the middle, PE ν at the bottom — each lane has its
// own auto-fit y-scale with a gap between lanes, so the three series never overlap
// and relative movement is read by comparing lane shapes, not a shared axis.
// Series colors are categorical identity (CE = accent blue, PE = amber), validated
// CVD-safe against the white surface; green/red stay reserved for P&L semantics.

const CHART = { w: 720, padL: 48, padR: 62, padT: 14, padB: 20, laneH: 65, laneGap: 22 } as const;
const CHART_H = CHART.padT + CHART.laneH * 3 + CHART.laneGap * 2 + CHART.padB;
const CE_COLOR = color.accent;
const PE_COLOR = color.warn;
const SPOT_COLOR = color.info;

// Opening-range window: each side's band is the high/low its vega-change touched in
// the first N hours of the session; a later close outside its own band is the breakout
// signal that plays the sound alert (see src/lib/vegaFlowRange.ts).
const ORB_STORAGE_KEY = 'td_vegaFlowRangeHours';
const ORB_HOURS_OPTIONS = [2, 3] as const;
const DEFAULT_ORB_HOURS: number = ORB_HOURS_OPTIONS[0];

function loadOrbHours(): number {
  const raw = Number(localStorage.getItem(ORB_STORAGE_KEY));
  return (ORB_HOURS_OPTIONS as readonly number[]).includes(raw) ? raw : DEFAULT_ORB_HOURS;
}

const fmtIST = (utc: string) =>
  new Date(utc).toLocaleTimeString('en-IN', { timeZone: 'Asia/Kolkata', hour12: false, hour: '2-digit', minute: '2-digit' });

const fmtPct = (v: number) => `${v >= 0 ? '+' : ''}${v.toFixed(1)}%`;
const fmtSpot = (v: number) => v.toLocaleString('en-IN', { maximumFractionDigits: 0 });

// One lane's linear y-scale, auto-fit to its own values with breathing room. `minPad`
// keeps a flat series from collapsing the domain to a point (divide-by-zero in y()).
function laneScale(values: number[], top: number, laneH: number, minPad: number) {
  const lo = Math.min(...values), hi = Math.max(...values);
  const pad = Math.max((hi - lo) * 0.15, minPad);
  const min = lo - pad, max = hi + pad;
  return { lo, hi, min, max, y: (v: number) => top + ((max - v) / (max - min)) * laneH };
}

// Card in its normal in-page spot, or the same content blown up in a modal that fills the
// browser window (not the OS-level Fullscreen API — the user wants the chart to take up the
// available page area, not take over the whole screen). VegaFlowChart's own SVG already scales
// via viewBox (no fixed pixel height), so simply giving it a much wider container is enough —
// no chart-geometry changes needed.
function VegaFlowSection({ points, vegaRead, unrealisedPnl }: { points: VegaFlowPoint[]; vegaRead: { note: string }; unrealisedPnl?: number }) {
  const [maximized, setMaximized] = useState(false);
  const toggle = () => setMaximized(m => !m);

  const pnlValue = unrealisedPnl !== undefined ? (
    <span style={{ fontWeight: 700, fontSize: '.95rem', fontFamily: font.mono, color: pnlColor(unrealisedPnl), whiteSpace: 'nowrap' }}>
      {unrealisedPnl < 0 ? '−' : ''}₹{Math.abs(unrealisedPnl).toLocaleString('en-IN', { maximumFractionDigits: 0 })}
    </span>
  ) : null;

  const body = (
    <>
      {!maximized && <SectionLabel>Vega flow — Δ Σν vs morning, near week (leading indicator)</SectionLabel>}
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10, flexWrap: 'wrap' }}>
        <span style={{ fontSize: '.76rem', color: color.textSub }}>{vegaRead.note}</span>
        {pnlValue && <div style={{ fontSize: '.76rem', color: color.textSub }}>· Unrealized P&L: {pnlValue}</div>}
        <Button
          size="sm" variant="ghost" style={{ marginLeft: 'auto' }} onClick={toggle}
          aria-label={maximized ? 'Restore chart to normal size' : 'Maximize chart to fill the window'}
        >
          {maximized ? '⤡ Restore' : '⛶ Maximize'}
        </Button>
      </div>
      <VegaFlowChart points={points} />
    </>
  );

  const subtitle = unrealisedPnl !== undefined
    ? (
      <div style={{ display: 'flex', gap: 4, alignItems: 'baseline', flexWrap: 'wrap' }}>
        <span>Δ Σν vs morning, near week · Unrealized P&L:</span>
        {pnlValue}
      </div>
    )
    : 'Δ Σν vs morning, near week (leading indicator)';

  if (!maximized) return <Card>{body}</Card>;
  return (
    <Modal
      title="Vega Flow Chart" subtitle={subtitle}
      onClose={toggle} closeOnBackdrop
      // Larger than any real viewport — Modal's own maxWidth: 100% is what actually caps it,
      // so this just needs to always lose to that cap, on any screen size.
      width={4000}
    >
      <div style={{ padding: 20 }}>{body}</div>
    </Modal>
  );
}

function VegaFlowChart({ points }: { points: VegaFlowPoint[] }) {
  const svgRef = useRef<SVGSVGElement>(null);
  const [hoverIdx, setHoverIdx] = useState<number | null>(null);
  const [orbHours, setOrbHoursState] = useState(loadOrbHours);
  const play = usePlaySound();
  // Edge-trigger memory only — NOT used for rendering (see `breach` below), so the alert fires
  // once per new crossing rather than every poll while a breach persists.
  const prevBreachRef = useRef<RangeBreach | null>(null);

  const windowMs = orbHours * 3_600_000;
  const setOrbHours = (hrs: number) => {
    // Re-basing the band can flip an in-range value to out-of-range; clearing the edge-trigger
    // memory makes the next effect run a seed, so the toggle itself never plays the alert.
    prevBreachRef.current = null;
    setOrbHoursState(hrs);
    localStorage.setItem(ORB_STORAGE_KEY, String(hrs));
  };

  useEffect(() => {
    const bands = openingBands(points, windowMs);
    if (!bands) return;
    const next = computeRangeBreach(points[points.length - 1], bands);
    const prev = prevBreachRef.current;
    if (prev != null && isNewBreach(prev, next)) play('criticalAlert');
    // First sample after mount (or after a window toggle) just seeds `prev` — an
    // already-breached chart shouldn't alarm on load.
    prevBreachRef.current = next;
  }, [points, windowMs, play]);

  if (points.length < 2) {
    return (
      <div style={{ color: color.textMuted, fontSize: '.75rem', padding: '18px 0' }}>
        The series builds one point per minute during market hours — check back after the open.
      </div>
    );
  }

  const { w, padL, padR, padT, padB, laneH, laneGap } = CHART;
  const h = CHART_H;
  const plotW = w - padL - padR;
  const t0 = new Date(points[0].atUtc).getTime();
  const t1 = new Date(points[points.length - 1].atUtc).getTime();
  const span = Math.max(t1 - t0, 1);
  const x = (p: VegaFlowPoint) => padL + ((new Date(p.atUtc).getTime() - t0) / span) * plotW;

  const laneTop = { ce: padT, spot: padT + laneH + laneGap, pe: padT + 2 * (laneH + laneGap) };

  const ceScale = laneScale(points.map(p => p.weekCe), laneTop.ce, laneH, 0.5);
  const peScale = laneScale(points.map(p => p.weekPe), laneTop.pe, laneH, 0.5);
  // Spot is only captured going forward — older cached points from before this field existed
  // read as 0 — so those are dropped from the line/scale rather than plotting a false drop to zero.
  const spotValues = points.map(p => p.spot).filter(s => s > 0);
  const spotScale = spotValues.length > 0 ? laneScale(spotValues, laneTop.spot, laneH, 1) : null;

  const lanePoints = (get: (p: VegaFlowPoint) => number, y: (v: number) => number, keep: (p: VegaFlowPoint) => boolean = () => true) =>
    points.filter(keep).map(p => `${x(p).toFixed(1)},${y(get(p)).toFixed(1)}`).join(' ');

  const last = points[points.length - 1];
  const bands = openingBands(points, windowMs)!; // points.length >= 2 here, never null
  // Computed live from the current render's data — never lags behind a poll cycle the way
  // reading the edge-trigger ref during render would (that ref only updates post-render).
  const breach = computeRangeBreach(last, bands);

  const onMove = (e: React.MouseEvent<SVGSVGElement>) => {
    const rect = svgRef.current?.getBoundingClientRect();
    if (!rect) return;
    const vx = ((e.clientX - rect.left) / rect.width) * w;
    let best = 0, bestD = Infinity;
    points.forEach((p, i) => { const d = Math.abs(x(p) - vx); if (d < bestD) { bestD = d; best = i; } });
    setHoverIdx(best);
  };

  const hover = hoverIdx != null ? points[hoverIdx] : null;
  const midT = points[Math.floor(points.length / 2)];
  // Skip the middle time label while the series is too short for it to clear the edge labels.
  const timeLabels = points.length >= 6 ? [points[0], midT, last] : [points[0], last];

  // One lane: title above, faint inset panel behind, own hi/lo axis labels on the left,
  // the series line, and its latest value direct-labeled at the line's end on the right.
  const lane = (opts: {
    key: string; title: string; top: number; scale: ReturnType<typeof laneScale>;
    stroke: string; dashed?: boolean; linePts: string; lastValue: string; lastY: number;
    fmt: (v: number) => string; breached?: boolean; band?: { min: number; max: number; fill: string; edge: string };
  }) => (
    <g key={opts.key}>
      <text x={padL} y={opts.top - 5} fontSize={9} fontWeight={600} fill={color.textSub} fontFamily={font.sans}>{opts.title}</text>
      <rect x={padL} y={opts.top} width={plotW} height={laneH} fill={color.subtle} />
      <line x1={padL} x2={w - padR} y1={opts.top} y2={opts.top} stroke={color.border} strokeWidth={1} />
      <line x1={padL} x2={w - padR} y1={opts.top + laneH} y2={opts.top + laneH} stroke={color.border} strokeWidth={1} />
      {/* Opening-range band, painted behind the line. Uses a *Border token (200-step), not *Bg —
          the Bg tokens are near-white and stay washed out no matter the fill-opacity. */}
      {opts.band && opts.band.max > opts.band.min && (
        <>
          <rect
            x={padL} y={opts.scale.y(opts.band.max)} width={plotW}
            height={Math.max(opts.scale.y(opts.band.min) - opts.scale.y(opts.band.max), 0.5)}
            fill={opts.band.fill} fillOpacity={0.5}
          />
          {([opts.band.max, opts.band.min] as const).map(edge => (
            <line
              key={edge} x1={padL} x2={w - padR} y1={opts.scale.y(edge)} y2={opts.scale.y(edge)}
              stroke={opts.band!.edge} strokeOpacity={0.6} strokeWidth={1} strokeDasharray="4 3"
            />
          ))}
        </>
      )}
      {/* Zero baseline for the Δ-vs-morning lanes, when zero is in view. */}
      {opts.scale.min < 0 && opts.scale.max > 0 && (
        <line x1={padL} x2={w - padR} y1={opts.scale.y(0)} y2={opts.scale.y(0)} stroke={color.borderStrong} strokeWidth={1} />
      )}
      {/* Per-lane hi/lo axis labels at the data's own extremes. */}
      {[...new Set([opts.scale.hi, opts.scale.lo])].map(v => (
        <text key={v} x={padL - 6} y={opts.scale.y(v) + 2} textAnchor="end" fontSize={9} fill={color.textMuted} fontFamily={font.mono}>
          {opts.fmt(v)}
        </text>
      ))}
      <polyline
        points={opts.linePts} fill="none" stroke={opts.stroke} strokeWidth={2}
        strokeLinejoin="round" strokeDasharray={opts.dashed ? '4 2' : undefined}
      />
      {/* Latest value in text ink at the line's end; red only when it sits outside its band. */}
      <text
        x={w - padR + 6} y={opts.lastY + 2} fontSize={9} fontWeight={700}
        fill={opts.breached ? color.neg : color.textSub} fontFamily={font.mono}
      >
        {opts.lastValue}
      </text>
    </g>
  );

  return (
    <div style={{ position: 'relative' }}>
      <svg
        ref={svgRef} viewBox={`0 0 ${w} ${h}`} style={{ width: '100%', display: 'block' }}
        role="img"
        aria-label={`Three stacked lanes: call-side vega change versus the morning baseline on top, NIFTY spot in the middle, put-side vega change at the bottom, each on its own scale. The call and put lanes shade their first-${orbHours}-hour opening range; a value crossing outside its own range plays a sound alert.`}
        onMouseMove={onMove} onMouseLeave={() => setHoverIdx(null)}
      >
        {lane({
          key: 'ce', title: 'CE ν (call side) — Δ vs morning', top: laneTop.ce, scale: ceScale,
          stroke: CE_COLOR, linePts: lanePoints(p => p.weekCe, ceScale.y),
          lastValue: fmtPct(last.weekCe), lastY: ceScale.y(last.weekCe), fmt: fmtPct,
          breached: breach.ceAbove || breach.ceBelow,
          band: { ...bands.ce, fill: color.accentBorder, edge: CE_COLOR },
        })}
        {spotScale ? (
          lane({
            key: 'spot', title: 'NIFTY spot', top: laneTop.spot, scale: spotScale,
            stroke: SPOT_COLOR, dashed: true, linePts: lanePoints(p => p.spot, spotScale.y, p => p.spot > 0),
            lastValue: fmtSpot(last.spot), lastY: spotScale.y(last.spot > 0 ? last.spot : spotValues[spotValues.length - 1]), fmt: fmtSpot,
          })
        ) : (
          <g>
            <text x={padL} y={laneTop.spot - 5} fontSize={9} fontWeight={600} fill={color.textSub} fontFamily={font.sans}>NIFTY spot</text>
            <rect x={padL} y={laneTop.spot} width={plotW} height={laneH} fill={color.subtle} />
            <text x={padL + plotW / 2} y={laneTop.spot + laneH / 2 + 2} textAnchor="middle" fontSize={9} fill={color.textMuted}>
              no spot samples yet
            </text>
          </g>
        )}
        {lane({
          key: 'pe', title: 'PE ν (put side) — Δ vs morning', top: laneTop.pe, scale: peScale,
          stroke: PE_COLOR, linePts: lanePoints(p => p.weekPe, peScale.y),
          lastValue: fmtPct(last.weekPe), lastY: peScale.y(last.weekPe), fmt: fmtPct,
          breached: breach.peAbove || breach.peBelow,
          band: { ...bands.pe, fill: color.warnBorder, edge: PE_COLOR },
        })}

        {timeLabels.map((p, i) => (
          <text
            key={i} y={h - 6} fontSize={9} fill={color.textMuted} fontFamily={font.mono}
            x={i === 0 ? padL : i === timeLabels.length - 1 ? x(last) : x(p)}
            textAnchor={i === 0 ? 'start' : i === timeLabels.length - 1 ? 'end' : 'middle'}
          >
            {fmtIST(p.atUtc)}
          </text>
        ))}

        {/* Hover crosshair spans all three lanes so vertical alignment across them is readable. */}
        {hover && (
          <g>
            <line x1={x(hover)} x2={x(hover)} y1={padT} y2={h - padB} stroke={color.borderStrong} strokeWidth={1} strokeOpacity={0.6} />
            <circle cx={x(hover)} cy={ceScale.y(hover.weekCe)} r={2.5} fill={CE_COLOR} stroke={color.surface} strokeWidth={1.5} />
            {spotScale && hover.spot > 0 && <circle cx={x(hover)} cy={spotScale.y(hover.spot)} r={2.5} fill={SPOT_COLOR} stroke={color.surface} strokeWidth={1.5} />}
            <circle cx={x(hover)} cy={peScale.y(hover.weekPe)} r={2.5} fill={PE_COLOR} stroke={color.surface} strokeWidth={1.5} />
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
          {([['CE ν', fmtPct(hover.weekCe), CE_COLOR],
             ['Spot', hover.spot > 0 ? fmtSpot(hover.spot) : '—', SPOT_COLOR],
             ['PE ν', fmtPct(hover.weekPe), PE_COLOR]] as const).map(([k, v, c]) => (
            <div key={k} style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: '.72rem' }}>
              <span style={{ width: 8, height: 8, borderRadius: 2, background: c }} />
              <span style={{ color: color.textSub }}>{k}</span>
              <span style={{ color: color.text, fontFamily: font.mono, marginLeft: 'auto' }}>{v}</span>
            </div>
          ))}
        </div>
      )}

      <div style={{ display: 'flex', gap: 14, marginTop: 6, fontSize: '.72rem', alignItems: 'center', flexWrap: 'wrap' }}>
        <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
          <span style={{ width: 10, height: 10, borderRadius: 2, background: color.accentBorder }} />
          <span style={{ color: color.textSub }}>CE range</span>
          <span style={{ color: color.text, fontFamily: font.mono }}>{fmtPct(bands.ce.min)} … {fmtPct(bands.ce.max)}</span>
        </span>
        <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
          <span style={{ width: 10, height: 10, borderRadius: 2, background: color.warnBorder }} />
          <span style={{ color: color.textSub }}>PE range</span>
          <span style={{ color: color.text, fontFamily: font.mono }}>{fmtPct(bands.pe.min)} … {fmtPct(bands.pe.max)}</span>
        </span>
        <span style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
          <span style={{ color: color.textMuted, fontSize: '.68rem' }}>range window</span>
          {ORB_HOURS_OPTIONS.map(hrs => (
            <Button
              key={hrs} size="sm" variant={orbHours === hrs ? 'secondary' : 'ghost'}
              aria-pressed={orbHours === hrs} onClick={() => setOrbHours(hrs)}
              style={{ padding: '1px 8px', fontSize: '.68rem', fontFamily: font.mono }}
            >
              {hrs}h
            </Button>
          ))}
        </span>
        {!bands.complete && <Badge tone="info">range forming · first {orbHours}h</Badge>}
        {breach.ceAbove && <Badge tone="neg">⚠ CE ν above range</Badge>}
        {breach.ceBelow && <Badge tone="neg">⚠ CE ν below range</Badge>}
        {breach.peAbove && <Badge tone="neg">⚠ PE ν above range</Badge>}
        {breach.peBelow && <Badge tone="neg">⚠ PE ν below range</Badge>}
        <span style={{ marginLeft: 'auto', color: color.textMuted, fontSize: '.66rem' }}>
          shaded band = each side's first-{orbHours}h range · a break outside its own band plays a sound · side rising = being bought
        </span>
      </div>
    </div>
  );
}

function ExpiryPanel({ title, exp, spot }: { title: string; exp: ExpiryAnalysis; spot: number }) {
  const [open, setOpen] = useState(false);
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
      <div
        role="button" tabIndex={0} aria-expanded={open}
        onClick={() => setOpen(o => !o)}
        onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setOpen(o => !o); } }}
        style={{ padding: '10px 16px', borderBottom: open ? BORDER : 'none', display: 'flex', alignItems: 'center', gap: 10, cursor: 'pointer', userSelect: 'none' }}
      >
        <span style={{ color: color.textMuted, fontSize: '.7rem', width: 10, textAlign: 'center' }}>{open ? '▾' : '▸'}</span>
        <span style={{ fontSize: '.8rem', fontWeight: 700, color: color.text }}>{title}</span>
        <span style={{ color: color.textSub, fontSize: '.75rem', fontFamily: font.mono }}>{exp.expiry.slice(0, 10)} · {exp.dte}d</span>
        <Badge tone={exp.isMonthly ? 'info' : 'accent'}>{exp.isMonthly ? 'MONTHLY' : 'WEEKLY'}</Badge>
        {!open && (
          <span style={{ marginLeft: 'auto', color: color.textMuted, fontSize: '.72rem', fontFamily: font.mono }}>
            ATM IV {(exp.atmIv * 100).toFixed(1)}%
          </span>
        )}
      </div>

      {open && (
        <>
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
        </>
      )}
    </Card>
  );
}
