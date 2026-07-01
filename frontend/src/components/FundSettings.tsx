import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { portfolio, type FundConfig, type FundUpdate } from '../api/client';
import { color, font } from '../theme';
import { Button, Card, Field, Input } from '../ui';

// Editable fields mirror the FundUpdate payload; currentNav stays read-only.
const NUM_FIELDS: { key: keyof Omit<FundUpdate, 'name'>; label: string; step?: string }[] = [
  { key: 'startingCapital', label: 'Starting Capital (₹)' },
  { key: 'cashBalance', label: 'Cash Balance (₹)' },
  { key: 'monthlyTargetPct', label: 'Monthly Target %', step: '0.1' },
  { key: 'maxMarginUtilPct', label: 'Max Margin Util %', step: '0.1' },
  { key: 'perPositionMaxLoss', label: 'Per-Position Cap (₹)' },
  { key: 'drawdownStopPct', label: 'Drawdown Stop %', step: '0.1' },
  { key: 'profitTakePct', label: 'Profit Take At %', step: '0.1' },
  { key: 'lotSize', label: 'Lot Size', step: '1' },
];

export default function FundSettings() {
  const qc = useQueryClient();
  const { data: fund } = useQuery({ queryKey: ['fund', 'config'], queryFn: () => portfolio.fund().then(r => r.data) });
  if (!fund) return <div style={{ color: color.textMuted, fontSize: '.82rem' }}>Loading fund…</div>;
  return <FundForm key={fund.id} initial={fund} onSaved={() => qc.invalidateQueries({ queryKey: ['fund', 'config'] })} />;
}

function FundForm({ initial, onSaved }: { initial: FundConfig; onSaved: () => void }) {
  const [form, setForm] = useState<FundUpdate>(initial);
  const [error, setError] = useState<string | null>(null);

  const save = useMutation({
    mutationFn: () => portfolio.updateFund(form),
    onSuccess: () => { setError(null); onSaved(); },
    onError: (e: { response?: { data?: { error?: string } } }) => setError(e.response?.data?.error ?? 'Save failed'),
  });

  const num = (v: string) => (v === '' ? 0 : Number(v));
  const fmt = (n: number) => n.toLocaleString('en-IN', { maximumFractionDigits: 0 });

  return (
    <Card>
      <div style={{ marginBottom: 12 }}>
        <Field label="Name"><Input style={{ fontWeight: 700 }} value={form.name} onChange={e => setForm(f => ({ ...f, name: e.target.value }))} /></Field>
      </div>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 10, marginBottom: 12 }}>
        {NUM_FIELDS.map(({ key, label, step }) => (
          <Field key={key} label={label}>
            <Input type="number" step={step} value={form[key]} onChange={e => setForm(f => ({ ...f, [key]: num(e.target.value) }))} />
          </Field>
        ))}
        <Field label="Current NAV (₹)">
          <div
            style={{ width: '100%', padding: '7px 10px', background: color.subtle, border: `1px solid ${color.border}`, borderRadius: 6, color: color.accent, fontFamily: font.mono, fontSize: '.82rem', cursor: 'not-allowed' }}
            title="Mark-to-market NAV, synced from broker"
          >₹{fmt(initial.currentNav)}</div>
        </Field>
      </div>
      <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
        <Button variant="primary" disabled={save.isPending} onClick={() => save.mutate()}>
          {save.isPending ? 'Saving…' : 'Save'}
        </Button>
        {error && <span style={{ color: color.neg, fontSize: '.74rem' }}>{error}</span>}
      </div>
    </Card>
  );
}
