import type { Proposal } from '../api/client';
import { color, strategyTone, tones } from '../theme';
import { Badge, Button, MetricTile, Modal } from '../ui';

/**
 * Lists the candidate strangles returned by a scan (highest score first) so the operator can
 * compare risk/IV across expiries & widths, then drill into one to review and approve.
 */
export default function ProposalCandidates({
  candidates, onPick, onClose,
}: { candidates: Proposal[]; onPick: (p: Proposal) => void; onClose: () => void }) {
  const topIv = Math.max(...candidates.map(c => c.atmIv));

  return (
    <Modal
      title="Suggested trades"
      subtitle={`${candidates.length} candidates · ranked by IV richness & return-on-risk · pick one to review`}
      onClose={onClose}
      width={680}
    >
      <div style={{ padding: '14px 18px 20px', display: 'flex', flexDirection: 'column', gap: 12 }}>
        {candidates.map((c, i) => {
          const tone = strategyTone[c.strategy] ?? 'accent';
          const ror = c.maxLoss > 0 ? (c.netCredit / c.maxLoss) * 100 : 0;
          const blocked = c.limitVerdict && !c.limitVerdict.passed;
          const shorts = c.legs.filter(l => l.side === 'Sell').sort((a, b) => a.strike - b.strike);
          const strikeLabel = shorts.map(l => `${l.optionType} ${l.strike}`).join(' / ') || '—';
          return (
            <div
              key={c.proposalId}
              style={{ background: color.surface, border: `1px solid ${color.border}`, borderLeft: `4px solid ${tones[tone].fg}`, borderRadius: 10, padding: '12px 14px' }}
            >
              <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 8 }}>
                <span style={{ fontWeight: 800, color: color.textMuted, fontSize: '.85rem' }}>#{i + 1}</span>
                <span style={{ fontWeight: 700, fontSize: '.95rem', color: color.text }}>{c.strategy}</span>
                {c.atmIv === topIv && <Badge tone="accent">MAX IV</Badge>}
                <span style={{ marginLeft: 'auto', fontSize: '.8rem', color: color.textSub }}>Score {c.score.toFixed(2)}</span>
              </div>

              <div style={{ fontSize: '.85rem', fontWeight: 600, color: color.textSub, marginBottom: 10 }}>{strikeLabel}</div>

              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 6, marginBottom: 10 }}>
                <MetricTile surface="subtle" size="sm" label="Expiry" value={`${c.expiry} · ${c.entryDte}d`} />
                <MetricTile surface="subtle" size="sm" label="ATM IV" value={`${(c.atmIv * 100).toFixed(1)}%`} valueColor={c.atmIv === topIv ? color.accentHover : color.text} />
                <MetricTile surface="subtle" size="sm" label="Credit" value={`₹${c.netCredit.toLocaleString('en-IN')}`} valueColor={color.pos} />
                <MetricTile surface="subtle" size="sm" label="Max loss" value={`₹${c.maxLoss.toLocaleString('en-IN')}`} valueColor={color.neg} />
                <MetricTile surface="subtle" size="sm" label="Return/risk" value={`${ror.toFixed(0)}%`} />
                <MetricTile surface="subtle" size="sm" label="Lots" value={`${c.lots} (${c.qty})`} />
                <MetricTile surface="subtle" size="sm" label="Margin util" value={`${c.marginUtilPct.toFixed(0)}%`} valueColor={c.marginUtilPct > 60 ? color.neg : color.text} />
              </div>

              {blocked && (
                <div style={{ background: color.negBg, color: color.neg, border: `1px solid ${color.negBorder}`, borderRadius: 6, padding: '6px 10px', fontSize: '.76rem', fontWeight: 600, marginBottom: 10 }}>
                  ✗ {c.limitVerdict!.violations.join('; ')}
                </div>
              )}

              <Button variant={blocked ? 'secondary' : 'primary'} fullWidth onClick={() => onPick(c)}>
                {blocked ? 'Review (limit breach) →' : 'Review & approve →'}
              </Button>
            </div>
          );
        })}
      </div>
    </Modal>
  );
}
