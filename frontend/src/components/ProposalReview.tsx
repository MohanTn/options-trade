import { useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import type { AxiosError } from 'axios';
import { proposals, type Proposal } from '../api/client';
import { color, font } from '../theme';
import { Button, MetricTile, Modal } from '../ui';

export default function ProposalReview(
  { proposal, onClose, onApproved }: { proposal: Proposal; onClose: () => void; onApproved?: () => void },
) {
  const [done, setDone] = useState<'approved' | 'rejected' | null>(null);

  const approveMutation = useMutation({
    mutationFn: () => proposals.approve(proposal.proposalId).then(r => r.data),
    onSuccess: () => { setDone('approved'); setTimeout(() => (onApproved ?? onClose)(), 2000); },
  });

  const rejectMutation = useMutation({
    mutationFn: () => proposals.reject(proposal.proposalId, 'Operator rejected').then(r => r.data),
    onSuccess: () => { setDone('rejected'); setTimeout(onClose, 1200); },
  });

  const passed = !!proposal.limitVerdict?.passed;

  return (
    <Modal
      title={proposal.strategy}
      subtitle={`Score ${proposal.score.toFixed(2)} · ${proposal.expiry} · DTE ${proposal.entryDte}`}
      onClose={onClose}
      width={560}
    >
      <div style={{ padding: '20px 22px 24px' }}>
        {done && (
          <div style={{ color: '#fff', padding: '10px 14px', borderRadius: 8, fontWeight: 700, marginBottom: 14, textAlign: 'center', background: done === 'approved' ? color.pos : color.textSub }}>
            {done === 'approved' ? '✓ Approved — place the legs at your broker, then enter the fills' : 'Rejected'}
          </div>
        )}

        <div style={{ background: color.subtle, border: `1px solid ${color.border}`, borderRadius: 8, padding: '10px 12px', fontSize: '.84rem', color: color.textSub, marginBottom: 14, lineHeight: 1.6 }}>
          {proposal.rationale}
        </div>

        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(5, 1fr)', gap: 8, marginBottom: 14 }}>
          <MetricTile surface="subtle" size="sm" label="India VIX" value={proposal.indiaVix.toFixed(1)} />
          <MetricTile surface="subtle" size="sm" label="ATM IV" value={`${(proposal.atmIv * 100).toFixed(1)}%`} />
          <MetricTile surface="subtle" size="sm" label="IV-rank" value={proposal.ivRank.toFixed(2)} />
          <MetricTile surface="subtle" size="sm" label="Lots" value={String(proposal.lots)} />
          <MetricTile surface="subtle" size="sm" label="Qty" value={String(proposal.qty)} />
          <MetricTile surface="subtle" size="sm" label="Net credit" value={`₹${proposal.netCredit.toLocaleString('en-IN')}`} valueColor={color.pos} />
          <MetricTile surface="subtle" size="sm" label="Max loss (GTT)" value={`₹${proposal.maxLoss.toLocaleString('en-IN')}`} valueColor={color.neg} />
          <MetricTile surface="subtle" size="sm" label="Margin" value={`₹${proposal.marginBlocked.toLocaleString('en-IN')}`} />
          <MetricTile surface="subtle" size="sm" label="Util after" value={`${proposal.marginUtilPct.toFixed(1)}%`} valueColor={proposal.marginUtilPct > 60 ? color.neg : color.text} />
          <MetricTile surface="subtle" size="sm" label="Expected return" value={`${proposal.expectedReturnPct.toFixed(1)}%`} valueColor={color.pos} />
        </div>

        <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: '.85rem', marginBottom: 14 }}>
          <thead>
            <tr style={{ background: color.subtle, color: color.textSub }}>
              <th style={thStyle}>Type</th><th style={thStyle}>Side</th><th style={thStyle}>Strike</th><th style={thStyle}>Symbol</th><th style={thStyle}>Mid</th>
            </tr>
          </thead>
          <tbody>
            {proposal.legs.map((l, i) => (
              <tr key={i} style={{ color: l.side === 'Sell' ? color.neg : color.pos }}>
                <td style={tdStyle}>{l.optionType}</td>
                <td style={tdStyle}>{l.side}</td>
                <td style={tdStyle}>{l.strike}</td>
                <td style={{ ...tdStyle, fontFamily: font.mono }}>{l.tradingSymbol}</td>
                <td style={{ ...tdStyle, fontFamily: font.mono }}>₹{l.midPrice.toFixed(1)}</td>
              </tr>
            ))}
          </tbody>
        </table>

        {proposal.limitVerdict && (
          <div
            style={{
              borderRadius: 8, padding: '10px 14px', fontSize: '.85rem', fontWeight: 600, marginBottom: 16,
              background: passed ? color.posBg : color.negBg,
              border: `1px solid ${passed ? color.posBorder : color.negBorder}`,
              color: passed ? color.pos : color.neg,
            }}
          >
            {passed
              ? '✓ Within all capital limits — orders are placed manually at the broker'
              : `✗ Limit breach: ${proposal.limitVerdict.violations.join('; ')}`}
          </div>
        )}

        <div style={{ display: 'flex', gap: 12 }}>
          <Button variant="secondary" style={{ flex: 1, padding: 12 }} onClick={() => rejectMutation.mutate()} disabled={!!done || rejectMutation.isPending}>
            Reject
          </Button>
          <Button
            variant="primary"
            style={{ flex: 2, padding: 12, background: color.pos, borderColor: color.pos }}
            onClick={() => approveMutation.mutate()}
            disabled={!passed || !!done || approveMutation.isPending}
          >
            {approveMutation.isPending ? 'Approving…' : 'Approve → place manually & track'}
          </Button>
        </div>
        {approveMutation.isError && (
          <p style={{ color: color.neg, textAlign: 'center', fontSize: '.85rem', marginTop: 10, wordBreak: 'break-word' }}>
            {(approveMutation.error as AxiosError<{ error?: string }>)?.response?.data?.error ?? 'Approval failed — check backend logs'}
          </p>
        )}
      </div>
    </Modal>
  );
}

const thStyle: React.CSSProperties = { padding: '6px 10px', fontWeight: 600, fontSize: '.78rem', textAlign: 'left' };
const tdStyle: React.CSSProperties = { padding: '6px 10px', borderBottom: `1px solid ${color.border}` };
