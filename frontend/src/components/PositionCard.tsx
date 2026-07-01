import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { positions, type LegPrice, type Position, type PositionLeg } from '../api/client';
import { color, font, pnlColor, signOf, strategyTone, tones } from '../theme';
import { Badge, Button, Input } from '../ui';

// Shared grid template — kept in sync with the ColHeader in Cockpit.
export const POS_COLS = '110px 85px 50px 42px 90px 90px 80px 1fr';

const STRAT_LABEL: Record<string, string> = {
  ShortStrangle: 'S.Strangle',
  IronCondor: 'Iron Condor',
  DoubleCalendar: 'Dbl Calendar',
  CreditSpread: 'Credit Spread',
};

const STATUS: Record<string, { color: string; prefix: string }> = {
  PendingFill: { color: color.warn, prefix: '◷' },
  Open: { color: color.pos, prefix: '●' },
  AutoAdjusting: { color: color.warn, prefix: '⟳' },
  ProfitTaking: { color: color.pos, prefix: '⚡' },
  RiskStopping: { color: color.neg, prefix: '⚠' },
  Closing: { color: color.textMuted, prefix: '○' },
};

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

function fmtExpiry(dateStr: string) {
  const d = new Date(dateStr);
  return `${d.getDate().toString().padStart(2, '0')}${MONTHS[d.getMonth()]}${d.getFullYear().toString().slice(2)}`;
}

// view = read-only; entry = confirm broker fills (PendingFill); edit = amend prices; close = enter exit fills.
type Mode = 'view' | 'entry' | 'edit' | 'close' | 'editMaxLoss' | 'editLots';
type Draft = Record<string, { entry: string; exit: string }>;

function buildDraft(legs: PositionLeg[]): Draft {
  return Object.fromEntries(legs.map(l => [l.id, {
    entry: l.entryPrice ? String(l.entryPrice) : '',
    exit: l.currentPrice ? String(l.currentPrice) : '',
  }]));
}

export default function PositionCard({ position, onClose, onSimulate }: { position: Position; onClose: () => void; onSimulate?: (p: Position) => void }) {
  const qc = useQueryClient();
  const [expanded, setExpanded] = useState(false);
  const [mode, setMode] = useState<Mode>('view');
  const [draft, setDraft] = useState<Draft>(() => buildDraft(position.legs));

  const { data: adjustments } = useQuery({
    queryKey: ['adjustments', position.id],
    queryFn: () => positions.adjustments(position.id).then(r => r.data),
    enabled: expanded,
  });

  const [maxLossDraft, setMaxLossDraft] = useState('');
  const [lotsDraft, setLotsDraft] = useState('');
  const afterWrite = () => { setMode('view'); onClose(); qc.invalidateQueries({ queryKey: ['positions'] }); };
  const confirmEntry = useMutation({ mutationFn: (legs: LegPrice[]) => positions.confirmEntry(position.id, legs), onSuccess: afterWrite });
  const editLegs = useMutation({ mutationFn: (legs: LegPrice[]) => positions.editLegs(position.id, legs), onSuccess: afterWrite });
  const closeMutation = useMutation({ mutationFn: (legs: LegPrice[]) => positions.close(position.id, 'Manual operator override', legs), onSuccess: afterWrite });
  const updateMaxLoss = useMutation({ mutationFn: (v: number) => positions.updateMaxLoss(position.id, v), onSuccess: afterWrite });
  const updateLots = useMutation({ mutationFn: (v: number) => positions.updateLots(position.id, v), onSuccess: afterWrite });
  const busy = confirmEntry.isPending || editLegs.isPending || closeMutation.isPending || updateMaxLoss.isPending || updateLots.isPending;

  const enter = (next: Mode) => { setDraft(buildDraft(position.legs)); setMode(next); };
  const setLeg = (id: string, field: 'entry' | 'exit', value: string) =>
    setDraft(d => ({ ...d, [id]: { ...d[id], [field]: value } }));

  const num = (v: string) => Number(v);
  const allPositive = (field: 'entry' | 'exit') => position.legs.every(l => num(draft[l.id]?.[field]) > 0);

  const submit = () => {
    if (mode === 'entry') confirmEntry.mutate(position.legs.map(l => ({ legId: l.id, entryPrice: num(draft[l.id].entry) })));
    else if (mode === 'close') closeMutation.mutate(position.legs.map(l => ({ legId: l.id, exitPrice: num(draft[l.id].exit) })));
    else if (mode === 'edit') editLegs.mutate(position.legs.map(l => ({
      legId: l.id,
      entryPrice: num(draft[l.id].entry) || undefined,
      exitPrice: num(draft[l.id].exit) || undefined,
    })));
  };

  const fmt = (n: number) => n.toLocaleString('en-IN', { maximumFractionDigits: 0 });
  const today = new Date();
  const expiry = new Date(position.expiryDate);
  const dte = Math.round((expiry.getTime() - today.getTime()) / 86400000);
  const dteColor = dte <= 14 ? color.neg : dte <= 25 ? color.warn : color.pos;
  const lots = position.legs[0]?.lots ?? 1;
  const stratTone = strategyTone[position.strategy] ?? 'neutral';
  const stratLabel = STRAT_LABEL[position.strategy] ?? position.strategy;
  const statusInfo = STATUS[position.status] ?? { color: color.textMuted, prefix: '●' };
  const pending = position.status === 'PendingFill';
  const editing = mode !== 'view' && mode !== 'editMaxLoss' && mode !== 'editLots';

  return (
    <>
      <div
        onClick={() => setExpanded(e => !e)}
        style={{
          display: 'grid',
          gridTemplateColumns: POS_COLS,
          alignItems: 'center',
          padding: '8px 20px',
          borderBottom: `1px solid ${color.border}`,
          fontSize: '.78rem',
          cursor: 'pointer',
          background: expanded ? color.subtle : color.surface,
        }}
      >
        <span><Badge tone={stratTone}>{stratLabel}</Badge></span>
        <span style={{ color: color.text, fontFamily: font.mono }}>{fmtExpiry(position.expiryDate)}</span>
        <span style={{ color: dteColor, fontWeight: 700 }}>{dte}d</span>
        <span style={{ color: color.textSub, fontFamily: font.mono, fontWeight: 600 }}>{lots}L</span>
        <span style={{ color: pnlColor(position.netCredit), fontFamily: font.mono }}>₹{fmt(position.netCredit)}</span>
        <span style={{ color: pnlColor(position.unrealisedPnl), fontWeight: 700, fontFamily: font.mono }}>{signOf(position.unrealisedPnl)}₹{fmt(position.unrealisedPnl)}</span>
        <span style={{ color: color.text, fontFamily: font.mono }}>₹{fmt(position.maxLoss)}</span>
        <span style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 8 }}>
          <span>
            <span style={{ color: statusInfo.color }}>{statusInfo.prefix} {position.status}</span>
            {position.gttStopOrderId && <span style={{ marginLeft: 8, fontSize: '.65rem', color: color.textMuted }}>GTT✓</span>}
          </span>
          {onSimulate && (
            <Button
              size="sm"
              variant="ghost"
              title="Payoff simulator"
              style={{ border: `1px solid ${color.accentBorder}` }}
              onClick={e => { e.stopPropagation(); onSimulate(position); }}
            >📈 Payoff</Button>
          )}
        </span>
      </div>

      {expanded && (
        <div style={{ padding: '8px 20px 12px 28px', background: color.subtle, borderBottom: `1px solid ${color.border}` }}>
          <div style={{ display: 'grid', gridTemplateColumns: editing ? '1fr 1fr' : 'repeat(4, 1fr)', gap: 6, marginBottom: 10 }}>
            {position.legs.map(leg => (
              <div key={leg.id} style={{ fontSize: '.68rem', padding: '6px 8px', background: color.surface, border: `1px solid ${color.border}`, borderRadius: 4, display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: '4px 6px' }}>
                <span style={{ color: leg.side === 'Sell' ? color.neg : color.pos, fontWeight: 700 }}>{leg.side.toUpperCase()}</span>
                <span style={{ color: color.text }}>NIFTY {leg.strike} {leg.optionType}</span>
                {!editing && <>
                  <span style={{ color: color.textMuted, fontFamily: font.mono }}>₹{leg.entryPrice.toFixed(0)}</span>
                  {leg.currentPrice > 0 && <span style={{ color: color.textSub, fontFamily: font.mono }}>→₹{leg.currentPrice.toFixed(0)}</span>}
                  {leg.side === 'Sell' && leg.entryPrice > 0 && (() => {
                    const mult = position.stopLossPremiumMult ?? 2;
                    const trigger = Math.round(leg.entryPrice * mult);
                    const limit = Math.round(trigger * 1.05);
                    return (
                      <span style={{ display: 'block', width: '100%', color: color.warn, fontFamily: font.mono, fontSize: '.63rem', marginTop: 2 }}>
                        GTT trig ₹{trigger} / lim ₹{limit}
                      </span>
                    );
                  })()}
                </>}
                {editing && (
                  <span style={{ display: 'flex', gap: 4, marginLeft: 'auto', alignItems: 'center' }}>
                    {(mode === 'entry' || mode === 'edit') && (
                      <Input
                        type="number" step="0.05" placeholder="entry" value={draft[leg.id]?.entry ?? ''}
                        onChange={e => setLeg(leg.id, 'entry', e.target.value)}
                        style={{ width: 70, padding: '3px 6px', fontSize: '.7rem' }}
                      />
                    )}
                    {(mode === 'close' || mode === 'edit') && (
                      <Input
                        type="number" step="0.05" placeholder="exit" value={draft[leg.id]?.exit ?? ''}
                        onChange={e => setLeg(leg.id, 'exit', e.target.value)}
                        style={{ width: 70, padding: '3px 6px', fontSize: '.7rem' }}
                      />
                    )}
                  </span>
                )}
              </div>
            ))}
          </div>
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', fontSize: '.72rem', color: color.textSub, gap: 10 }}>
            <div>
              {mode === 'entry' && <span style={{ color: color.warn }}>Enter the actual broker fill price for each leg, then confirm.</span>}
              {mode === 'close' && <span style={{ color: color.warn }}>Enter the exit fill price for each leg to realise P&amp;L.</span>}
              {mode === 'edit' && <span>Amend entry / exit prices.</span>}
              {mode === 'view' && <>
                {'Max loss '}
                <span
                  title="Click to edit"
                  onClick={e => { e.stopPropagation(); setMaxLossDraft(String(position.maxLoss)); setMode('editMaxLoss'); }}
                  style={{ cursor: 'pointer', color: color.accent, textDecoration: 'underline dotted', textUnderlineOffset: 2 }}
                >₹{fmt(position.maxLoss)}</span>
                {' · Lots '}
                <span
                  title="Click to edit lots"
                  onClick={e => { e.stopPropagation(); setLotsDraft(String(lots)); setMode('editLots'); }}
                  style={{ cursor: 'pointer', color: color.accent, textDecoration: 'underline dotted', textUnderlineOffset: 2 }}
                >{lots}L</span>
                {' · GTT '}
                {position.gttStopOrderId
                  ? <span style={{ color: color.pos }}>Armed</span>
                  : <span style={{ color: color.textMuted }}>Manual</span>}
                {adjustments && (adjustments as { id: string }[]).length > 0 && (
                  <span style={{ marginLeft: 10, color: tones.accent.fg }}>
                    {(adjustments as { id: string; kind: string; automated: boolean }[]).slice(0, 2).map(a => (
                      <span key={a.id} style={{ marginRight: 6 }}>{a.kind}{a.automated ? '·AUTO' : ''}</span>
                    ))}
                  </span>
                )}
              </>}
            </div>
            <div style={{ display: 'flex', gap: 8, alignItems: 'center' }} onClick={e => e.stopPropagation()}>
              {mode === 'editMaxLoss' && (
                <>
                  <Input
                    type="number" step="100" min="1" placeholder="max loss"
                    value={maxLossDraft}
                    onChange={e => setMaxLossDraft(e.target.value)}
                    style={{ width: 90, padding: '3px 6px', fontSize: '.7rem' }}
                  />
                  <Button size="sm" variant="ghost" onClick={() => setMode('view')} disabled={busy}>✕</Button>
                  <Button
                    size="sm" variant="primary"
                    onClick={() => { const v = Number(maxLossDraft); if (v > 0) updateMaxLoss.mutate(v); }}
                    disabled={busy || !(Number(maxLossDraft) > 0)}
                  >{busy ? 'Saving…' : '✓ Set'}</Button>
                </>
              )}
              {mode === 'editLots' && (
                <>
                  <Input
                    type="number" step="1" min="1" placeholder="lots"
                    value={lotsDraft}
                    onChange={e => setLotsDraft(e.target.value)}
                    style={{ width: 70, padding: '3px 6px', fontSize: '.7rem' }}
                  />
                  <Button size="sm" variant="ghost" onClick={() => setMode('view')} disabled={busy}>✕</Button>
                  <Button
                    size="sm" variant="primary"
                    onClick={() => { const v = Math.round(Number(lotsDraft)); if (v > 0) updateLots.mutate(v); }}
                    disabled={busy || !(Number(lotsDraft) >= 1)}
                  >{busy ? 'Saving…' : '✓ Set'}</Button>
                </>
              )}
              {mode === 'view' && pending && (
                <Button size="sm" variant="primary" onClick={() => enter('entry')}>Enter Fills</Button>
              )}
              {mode === 'view' && !pending && (
                <>
                  <Button size="sm" variant="secondary" onClick={() => enter('edit')}>Edit Prices</Button>
                  <Button size="sm" variant="danger" onClick={() => enter('close')}>Close</Button>
                </>
              )}
              {editing && (
                <>
                  <Button size="sm" variant="ghost" onClick={() => setMode('view')} disabled={busy}>Cancel</Button>
                  <Button
                    size="sm"
                    variant={mode === 'close' ? 'danger' : 'primary'}
                    onClick={submit}
                    disabled={busy
                      || (mode === 'entry' && !allPositive('entry'))
                      || (mode === 'close' && !allPositive('exit'))}
                  >
                    {busy ? 'Saving…' : mode === 'entry' ? 'Confirm Entry' : mode === 'close' ? 'Confirm Close' : 'Save'}
                  </Button>
                </>
              )}
            </div>
          </div>
        </div>
      )}
    </>
  );
}
