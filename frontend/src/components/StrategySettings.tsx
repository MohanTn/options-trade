import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { strategies, type StrategyConfig, type StrategyLeg, type Strategy } from '../api/client';
import { color, font } from '../theme';
import { Button, Card, Field, Input, Label, Select } from '../ui';

const STRATEGY_TYPES: Strategy[] = ['ShortStrangle', 'IronCondor', 'DoubleCalendar', 'CreditSpread'];

const BLANK: StrategyConfig = {
  name: 'New Strategy', enabled: true, strategy: 'ShortStrangle',
  vixMin: 0, vixMax: 100, entryDteMin: 42, entryDteMax: 50, sizingPct: 100, weeklyCompounding: false,
  gttEnabled: false, gttPremiumPct: 200, profitTargetPct: 50, targetExitDte: 21, adjustTriggerDelta: 0.3,
  legs: [{ optionType: 'CE', side: 'Sell', targetDelta: 0.16, expiry: 'Near' }],
};

const checkboxLabel: React.CSSProperties = { display: 'flex', alignItems: 'center', gap: 6, fontSize: '.78rem', color: color.textSub, fontFamily: font.sans, cursor: 'pointer' };

export default function StrategySettings() {
  const qc = useQueryClient();
  const { data: configs } = useQuery({ queryKey: ['strategies'], queryFn: () => strategies.list().then(r => r.data) });
  const invalidate = () => qc.invalidateQueries({ queryKey: ['strategies'] });

  if (!configs) return <div style={{ color: color.textMuted, fontSize: '.82rem' }}>Loading strategies…</div>;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
      {[...configs].sort((a, b) => a.vixMin - b.vixMin).map(cfg => (
        <StrategyCard key={cfg.id} initial={cfg} onSaved={invalidate} />
      ))}
      <StrategyCard key="new" initial={BLANK} isNew onSaved={invalidate} />
    </div>
  );
}

function StrategyCard({ initial, isNew, onSaved }: { initial: StrategyConfig; isNew?: boolean; onSaved: () => void }) {
  const [cfg, setCfg] = useState<StrategyConfig>(initial);
  const [error, setError] = useState<string | null>(null);

  const save = useMutation({
    mutationFn: () => (isNew ? strategies.create(cfg) : strategies.update(cfg.id!, cfg)),
    onSuccess: () => { setError(null); if (isNew) setCfg(BLANK); onSaved(); },
    onError: (e: { response?: { data?: { error?: string } } }) => setError(e.response?.data?.error ?? 'Save failed'),
  });
  const del = useMutation({ mutationFn: () => strategies.remove(cfg.id!), onSuccess: onSaved });

  const set = <K extends keyof StrategyConfig>(k: K, v: StrategyConfig[K]) => setCfg(c => ({ ...c, [k]: v }));
  const num = (v: string) => (v === '' ? 0 : Number(v));
  const setLeg = (i: number, patch: Partial<StrategyLeg>) =>
    setCfg(c => ({ ...c, legs: c.legs.map((l, j) => (j === i ? { ...l, ...patch } : l)) }));
  const addLeg = () => setCfg(c => ({ ...c, legs: [...c.legs, { optionType: 'PE', side: 'Sell', targetDelta: 0.16, expiry: 'Near' }] }));
  const removeLeg = (i: number) => setCfg(c => ({ ...c, legs: c.legs.filter((_, j) => j !== i) }));

  return (
    <Card style={{ opacity: cfg.enabled ? 1 : 0.6 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 12 }}>
        <Input style={{ flex: 1, fontSize: '.85rem', fontWeight: 700 }} value={cfg.name} onChange={e => set('name', e.target.value)} />
        <label style={checkboxLabel} title="Trade only the nearest eligible expiry and scale size with NAV so weekly credits compound into the principal (per-position max-loss cap still applies)">
          <input type="checkbox" checked={cfg.weeklyCompounding} onChange={e => set('weeklyCompounding', e.target.checked)} /> Weekly compounding
        </label>
        <label style={checkboxLabel}>
          <input type="checkbox" checked={cfg.enabled} onChange={e => set('enabled', e.target.checked)} /> Enabled
        </label>
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 10, marginBottom: 12 }}>
        <Field label="Strategy">
          <Select value={cfg.strategy} onChange={e => set('strategy', e.target.value as Strategy)}>
            {STRATEGY_TYPES.map(s => <option key={s} value={s}>{s}</option>)}
          </Select>
        </Field>
        <Field label="VIX min"><Input type="number" value={cfg.vixMin} onChange={e => set('vixMin', num(e.target.value))} /></Field>
        <Field label="VIX max"><Input type="number" value={cfg.vixMax} onChange={e => set('vixMax', num(e.target.value))} /></Field>
        <Field label="Sizing %"><Input type="number" value={cfg.sizingPct} onChange={e => set('sizingPct', num(e.target.value))} /></Field>
        <Field label="Entry DTE min"><Input type="number" value={cfg.entryDteMin} onChange={e => set('entryDteMin', num(e.target.value))} /></Field>
        <Field label="Entry DTE max"><Input type="number" value={cfg.entryDteMax} onChange={e => set('entryDteMax', num(e.target.value))} /></Field>
        <Field label="Profit target %"><Input type="number" value={cfg.profitTargetPct} onChange={e => set('profitTargetPct', num(e.target.value))} /></Field>
        <Field label="Target exit DTE"><Input type="number" value={cfg.targetExitDte} onChange={e => set('targetExitDte', num(e.target.value))} /></Field>
        <Field label="Adjust trigger Δ"><Input type="number" step="0.01" value={cfg.adjustTriggerDelta} onChange={e => set('adjustTriggerDelta', num(e.target.value))} /></Field>
        <Field label="GTT stop">
          <label style={{ ...checkboxLabel, padding: '7px 0' }}>
            <input type="checkbox" checked={cfg.gttEnabled} onChange={e => set('gttEnabled', e.target.checked)} /> Needed
          </label>
        </Field>
        {cfg.gttEnabled && (
          <Field label="GTT % of premium"><Input type="number" value={cfg.gttPremiumPct} onChange={e => set('gttPremiumPct', num(e.target.value))} /></Field>
        )}
      </div>

      <Label>Legs</Label>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6, marginBottom: 12 }}>
        {cfg.legs.map((leg, i) => (
          <div key={i} style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr 1fr 34px', gap: 8, alignItems: 'center' }}>
            <Select value={leg.side} onChange={e => setLeg(i, { side: e.target.value as StrategyLeg['side'] })}>
              <option value="Sell">Sell</option><option value="Buy">Buy</option>
            </Select>
            <Select value={leg.optionType} onChange={e => setLeg(i, { optionType: e.target.value as StrategyLeg['optionType'] })}>
              <option value="CE">CE</option><option value="PE">PE</option>
            </Select>
            <Input type="number" step="0.01" placeholder="target Δ" value={leg.targetDelta} onChange={e => setLeg(i, { targetDelta: num(e.target.value) })} />
            <Select value={leg.expiry} onChange={e => setLeg(i, { expiry: e.target.value as StrategyLeg['expiry'] })}>
              <option value="Near">Near</option><option value="Far">Far</option>
            </Select>
            <Button variant="danger" size="sm" style={{ padding: '6px 0' }} onClick={() => removeLeg(i)} title="Remove leg">✕</Button>
          </div>
        ))}
        <Button variant="secondary" size="sm" style={{ alignSelf: 'flex-start' }} onClick={addLeg}>+ Add leg</Button>
      </div>

      <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
        <Button variant="primary" disabled={save.isPending} onClick={() => save.mutate()}>
          {save.isPending ? 'Saving…' : isNew ? '+ Create strategy' : 'Save'}
        </Button>
        {!isNew && <Button variant="danger" onClick={() => { if (window.confirm(`Delete strategy "${cfg.name}"?`)) del.mutate(); }} disabled={del.isPending}>Delete</Button>}
        {error && <span style={{ color: color.neg, fontSize: '.74rem' }}>{error}</span>}
      </div>
    </Card>
  );
}
