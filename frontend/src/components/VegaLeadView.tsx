import { useEffect, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { analysis, type VegaFlowPoint } from '../api/client';
import { color, font } from '../theme';
import { Card, SectionLabel, ViewTitle } from '../ui';

// Archive page: overlays one trading day's weekly CE/PE vega-flow leading indicator (left axis,
// % vs morning) against NIFTY spot (right axis). Reads the permanent Postgres snapshot for the
// selected day, NOT the live Redis series — a day only appears here once its snapshot is written
// at market close (15:30 IST), by design: today's row doesn't exist until after the close, so
// "today" only becomes selectable from 3:30pm onward. Snapshots are kept forever until the
// operator deletes them manually; there is no auto-expiry. The live intraday chart (with
// draggable alert thresholds) is a separate view on the Option Chain page.

const CE_COLOR = color.accent;
const PE_COLOR = color.warn;
const SPOT_COLOR = color.info;
const CHART = { w: 760, h: 220, padL: 48, padR: 64, padT: 12, padB: 24 } as const;

const fmtIST = (utc: string) =>
  new Date(utc).toLocaleTimeString('en-IN', { timeZone: 'Asia/Kolkata', hour12: false, hour: '2-digit', minute: '2-digit' });

const fmtDateLabel = (isoDate: string) =>
  new Date(`${isoDate}T00:00:00`).toLocaleDateString('en-IN', { weekday: 'short', day: '2-digit', month: 'short', year: 'numeric' });

export default function VegaLeadView({ sessionValid }: { sessionValid: boolean }) {
  // Lightweight — just the list of archived dates — so it's cheap to poll for a newly-written
  // snapshot (e.g. the operator has the page open across the 3:30pm close).
  const { data: dates, error: datesError } = useQuery<string[]>({
    queryKey: ['analysis', 'vega-flow', 'history-dates'],
    queryFn: () => analysis.vegaFlowHistoryDates().then(r => r.data),
    refetchInterval: 60000,
    enabled: sessionValid,
    retry: false,
  });

  const [selectedDate, setSelectedDate] = useState<string | null>(null);
  // Default to the most recent archived day on first load only — once the operator picks a
  // date, a later poll finding a new snapshot should not yank them off what they're reviewing.
  useEffect(() => {
    if (selectedDate == null && dates && dates.length > 0) setSelectedDate(dates[0]);
  }, [dates, selectedDate]);

  const { data: points, error: pointsError, isFetching } = useQuery<VegaFlowPoint[]>({
    queryKey: ['analysis', 'vega-flow', 'history', selectedDate],
    queryFn: () => analysis.vegaFlowHistory(selectedDate!).then(r => r.data),
    enabled: sessionValid && !!selectedDate,
    retry: false,
  });

  if (!sessionValid) {
    return <div style={{ color: color.warn, fontSize: '.82rem' }}>⚠ Connect the Kite session to load the vega/spot archive.</div>;
  }
  if (datesError) {
    return <div style={{ color: color.neg, fontSize: '.82rem' }}>Failed to load the archived-session list.</div>;
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
        <ViewTitle title="Vega Lead vs NIFTY Spot" style={{ marginBottom: 0 }} />
        {dates && dates.length > 0 && (
          <select
            value={selectedDate ?? ''}
            onChange={e => setSelectedDate(e.target.value)}
            aria-label="Select archived trading day"
            style={{
              marginLeft: 'auto', fontSize: '.78rem', fontFamily: font.mono, color: color.text,
              background: color.surface, border: `1px solid ${color.border}`, borderRadius: 6, padding: '5px 8px',
            }}
          >
            {dates.map(d => <option key={d} value={d}>{fmtDateLabel(d)}</option>)}
          </select>
        )}
      </div>

      <Card>
        <SectionLabel>Weekly CE/PE vega Δ vs morning (left) · NIFTY spot (right)</SectionLabel>
        <div style={{ fontSize: '.76rem', color: color.textSub, marginBottom: 10 }}>
          Archived once per trading day at market close (15:30 IST) — today's session appears here only after that snapshot is written. A CE/PE line moving before a corresponding turn in the spot line is the lead you're looking for.
        </div>
        {dates === undefined ? (
          <div style={{ color: color.textMuted, fontSize: '.75rem', padding: '18px 0' }}>Loading…</div>
        ) : dates.length === 0 ? (
          <div style={{ color: color.textMuted, fontSize: '.75rem', padding: '18px 0' }}>
            No archived sessions yet — a snapshot is captured once per trading day at market close (15:30 IST).
          </div>
        ) : pointsError ? (
          <div style={{ color: color.neg, fontSize: '.82rem' }}>Failed to load that day's snapshot.</div>
        ) : isFetching && !points ? (
          <div style={{ color: color.textMuted, fontSize: '.75rem', padding: '18px 0' }}>Loading…</div>
        ) : (
          <OverlayChart points={points ?? []} />
        )}
      </Card>
    </div>
  );
}

function OverlayChart({ points }: { points: VegaFlowPoint[] }) {
  const svgRef = useRef<SVGSVGElement>(null);
  const [hoverIdx, setHoverIdx] = useState<number | null>(null);

  if (points.length < 2) {
    return (
      <div style={{ color: color.textMuted, fontSize: '.75rem', padding: '18px 0' }}>
        This session has too few samples to chart.
      </div>
    );
  }

  const { w, h, padL, padR, padT, padB } = CHART;
  const t0 = new Date(points[0].atUtc).getTime();
  const t1 = new Date(points[points.length - 1].atUtc).getTime();
  const span = Math.max(t1 - t0, 1);
  const x = (p: VegaFlowPoint) => padL + ((new Date(p.atUtc).getTime() - t0) / span) * (w - padL - padR);

  const vegaValues = points.flatMap(p => [p.weekCe, p.weekPe]);
  let vMin = Math.min(0, ...vegaValues), vMax = Math.max(0, ...vegaValues);
  const vPad = Math.max((vMax - vMin) * 0.12, 0.5);
  vMin -= vPad; vMax += vPad;
  const yVega = (v: number) => padT + ((vMax - v) / (vMax - vMin)) * (h - padT - padB);

  const spotValues = points.map(p => p.spot).filter(s => s > 0);
  const sMin0 = spotValues.length > 0 ? Math.min(...spotValues) : 0;
  const sMax0 = spotValues.length > 0 ? Math.max(...spotValues) : 1;
  const sPad = Math.max((sMax0 - sMin0) * 0.12, 1);
  const sMin = sMin0 - sPad, sMax = sMax0 + sPad;
  const ySpot = (v: number) => padT + ((sMax - v) / (sMax - sMin)) * (h - padT - padB);

  const line = (get: (p: VegaFlowPoint) => number, y: (v: number) => number) =>
    points.map(p => `${x(p).toFixed(1)},${y(get(p)).toFixed(1)}`).join(' ');
  // Spot is only captured going forward (older cached points from before this field existed
  // read as 0) — drop those from the spot line rather than plotting a false drop to zero.
  const spotLine = points
    .filter(p => p.spot > 0)
    .map(p => `${x(p).toFixed(1)},${ySpot(p.spot).toFixed(1)}`)
    .join(' ');

  const onMove = (e: React.MouseEvent<SVGSVGElement>) => {
    const rect = svgRef.current?.getBoundingClientRect();
    if (!rect) return;
    const vx = ((e.clientX - rect.left) / rect.width) * w;
    let best = 0, bestD = Infinity;
    points.forEach((p, i) => { const d = Math.abs(x(p) - vx); if (d < bestD) { bestD = d; best = i; } });
    setHoverIdx(best);
  };

  const vegaTicks = [...new Set([vMax - vPad, 0, vMin + vPad].map(v => Math.round(v * 10) / 10))];
  const midT = points[Math.floor(points.length / 2)];
  const last = points[points.length - 1];
  const timeLabels = points.length >= 6 ? [points[0], midT, last] : [points[0], last];
  const hover = hoverIdx != null ? points[hoverIdx] : null;

  return (
    <div style={{ position: 'relative' }}>
      <svg
        ref={svgRef} viewBox={`0 0 ${w} ${h}`} style={{ width: '100%', display: 'block' }}
        role="img" aria-label="Weekly call-side and put-side vega change versus the morning baseline, overlaid with NIFTY spot"
        onMouseMove={onMove} onMouseLeave={() => setHoverIdx(null)}
      >
        {vegaTicks.map(v => (
          <g key={v}>
            <line x1={padL} x2={w - padR} y1={yVega(v)} y2={yVega(v)} stroke={v === 0 ? color.borderStrong : color.border} strokeWidth={1} />
            <text x={padL - 6} y={yVega(v) + 3} textAnchor="end" fontSize={10} fill={color.textMuted} fontFamily={font.mono}>
              {v > 0 ? `+${v}` : v}%
            </text>
          </g>
        ))}
        {spotValues.length > 0 && [...new Set([sMax0, (sMax0 + sMin0) / 2, sMin0])].map(v => (
          <text key={v} x={w - padR + 6} y={ySpot(v) + 3} fontSize={10} fill={color.textMuted} fontFamily={font.mono}>
            {v.toLocaleString('en-IN', { maximumFractionDigits: 0 })}
          </text>
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

        <polyline points={line(p => p.weekCe, yVega)} fill="none" stroke={CE_COLOR} strokeWidth={2} strokeLinejoin="round" />
        <polyline points={line(p => p.weekPe, yVega)} fill="none" stroke={PE_COLOR} strokeWidth={2} strokeLinejoin="round" />
        {spotLine && <polyline points={spotLine} fill="none" stroke={SPOT_COLOR} strokeWidth={2} strokeDasharray="4 2" strokeLinejoin="round" />}

        {hover && (
          <g>
            <line x1={x(hover)} x2={x(hover)} y1={padT} y2={h - padB} stroke={color.borderStrong} strokeWidth={1} />
            <circle cx={x(hover)} cy={yVega(hover.weekCe)} r={4} fill={CE_COLOR} stroke={color.surface} strokeWidth={2} />
            <circle cx={x(hover)} cy={yVega(hover.weekPe)} r={4} fill={PE_COLOR} stroke={color.surface} strokeWidth={2} />
            {hover.spot > 0 && <circle cx={x(hover)} cy={ySpot(hover.spot)} r={4} fill={SPOT_COLOR} stroke={color.surface} strokeWidth={2} />}
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
          {([['CE ν', `${hover.weekCe >= 0 ? '+' : ''}${hover.weekCe.toFixed(1)}%`, CE_COLOR],
             ['PE ν', `${hover.weekPe >= 0 ? '+' : ''}${hover.weekPe.toFixed(1)}%`, PE_COLOR],
             ['Spot', hover.spot > 0 ? hover.spot.toLocaleString('en-IN', { maximumFractionDigits: 0 }) : '—', SPOT_COLOR]] as const).map(([k, v, c]) => (
            <div key={k} style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: '.72rem' }}>
              <span style={{ width: 8, height: 8, borderRadius: 2, background: c }} />
              <span style={{ color: color.textSub }}>{k}</span>
              <span style={{ color: color.text, fontFamily: font.mono, marginLeft: 'auto' }}>{v}</span>
            </div>
          ))}
        </div>
      )}

      <div style={{ display: 'flex', gap: 16, marginTop: 6, fontSize: '.72rem', alignItems: 'center', flexWrap: 'wrap' }}>
        {([['CE ν (call side)', CE_COLOR, 'solid'], ['PE ν (put side)', PE_COLOR, 'solid'], ['NIFTY spot', SPOT_COLOR, 'dashed']] as const).map(([k, c, style]) => (
          <span key={k} style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            <span style={{ width: 14, height: 0, borderTop: `2px ${style} ${c}` }} />
            <span style={{ color: color.textSub }}>{k}</span>
          </span>
        ))}
      </div>
    </div>
  );
}
