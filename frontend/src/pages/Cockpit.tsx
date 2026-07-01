import { useState, useEffect } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { portfolio, system, positions, proposals, market, orders, type Proposal, type NoCandidate, type Position } from '../api/client';
import ProposalReview from '../components/ProposalReview';
import ProposalCandidates from '../components/ProposalCandidates';
import PayoffSimulator from '../components/PayoffSimulator';
import PositionCard, { POS_COLS } from '../components/PositionCard';
import StrategySettings from '../components/StrategySettings';
import FundSettings from '../components/FundSettings';
import { useMonitorSounds } from '../hooks/useMonitorSounds';
import { color, font, pnlColor, signOf, type Tone } from '../theme';
import { Badge, Button, Card, MetricTile, SectionLabel, TableHeader, TableRow, ViewTitle } from '../ui';

function useISTClock() {
  const [t, setT] = useState('');
  useEffect(() => {
    const tick = () => {
      const now = new Date();
      const ist = new Date(now.getTime() + (now.getTimezoneOffset() + 330) * 60000);
      setT(ist.toLocaleTimeString('en-IN', { hour12: false }));
    };
    tick();
    const id = setInterval(tick, 1000);
    return () => clearInterval(id);
  }, []);
  return t;
}

function marketStatus() {
  const now = new Date();
  const ist = new Date(now.getTime() + (now.getTimezoneOffset() + 330) * 60000);
  const d = ist.getDay(), hm = ist.getHours() * 100 + ist.getMinutes();
  if (d === 0 || d === 6) return { label: 'WEEKEND', open: false };
  if (hm < 915) return { label: 'PRE-MARKET', open: false };
  if (hm < 1530) return { label: 'MARKET OPEN', open: true };
  if (hm < 1600) return { label: 'POST-CLOSE', open: false };
  return { label: 'MARKET CLOSED', open: false };
}

function vixRegime(vix: number) {
  if (vix < 12) return 'LOW · Iron Fly';
  if (vix < 18) return 'MID · Short Strangle';
  return 'HIGH · Reduce Size';
}

const BORDER = `1px solid ${color.border}`;

// Nav item has no `active` field — active state is derived from `activeView` in the parent.
type NavItem = { label: string; badge?: string };

const NAV_SECTIONS: { label: string; items: NavItem[] }[] = [
  { label: 'Monitor', items: [{ label: 'Cockpit' }, { label: 'Positions' }, { label: 'Orders' }] },
  { label: 'Analysis', items: [{ label: 'Greeks' }, { label: 'P&L' }, { label: 'Risk Limits' }] },
  { label: 'System', items: [{ label: 'Audit Log' }, { label: 'Alerts' }, { label: 'Settings' }] },
];

// Views that have real content implemented.
const IMPLEMENTED = new Set(['Cockpit', 'Positions', 'Orders', 'Greeks', 'P&L', 'Risk Limits', 'Alerts', 'Audit Log', 'Settings']);

export default function Cockpit() {
  const qc = useQueryClient();
  const [killOn, setKillOn] = useState(false);
  const [activeProposal, setActiveProposal] = useState<Proposal | null>(null);
  const [showChooser, setShowChooser] = useState(false);
  const [simPosition, setSimPosition] = useState<Position | null>(null);
  const [reqToken, setReqToken] = useState('');
  const [rightTab, setRightTab] = useState<'greeks' | 'signal' | 'alerts'>('greeks');
  const [activeView, setActiveView] = useState('Cockpit');
  const clock = useISTClock();
  const mkt = marketStatus();

  const { data: greeks } = useQuery({ queryKey: ['greeks'], queryFn: () => portfolio.greeks().then(r => r.data), refetchInterval: 5000 });
  const { data: alerts } = useQuery({ queryKey: ['alerts'], queryFn: () => portfolio.alerts(false).then(r => r.data), refetchInterval: 10000 });
  // "active" = everything not yet Closed/Settled, so PendingFill positions awaiting manual fills show too.
  const { data: openPositions } = useQuery({ queryKey: ['positions', 'active'], queryFn: () => positions.list('active').then(r => r.data), refetchInterval: 5000 });
  const { data: session } = useQuery({ queryKey: ['session'], queryFn: () => system.session().then(r => r.data), refetchInterval: 60000 });
  const { data: marketTicks } = useQuery({ queryKey: ['market', 'ticks'], queryFn: () => market.ticks().then(r => r.data), refetchInterval: 5000, enabled: !!session?.valid });
  const { data: curProposal } = useQuery({
    queryKey: ['proposal', 'current'],
    queryFn: () => proposals.current().then(r => r.data).catch(() => null),
    refetchInterval: 30000,
  });
  // The current scan's suggested candidates — kept server-side so they survive a page reload.
  const { data: candidates } = useQuery({
    queryKey: ['proposals', 'candidates'],
    queryFn: () => proposals.candidates().then(r => r.data).catch(() => [] as Proposal[]),
  });
  const { data: navHistory } = useQuery({
    queryKey: ['nav', 'history'],
    queryFn: () => portfolio.navHistory().then(r => r.data),
    refetchInterval: 60000,
    enabled: activeView === 'P&L',
  });
  const { data: riskLimits } = useQuery({
    queryKey: ['risk', 'limits'],
    queryFn: () => portfolio.limits().then(r => r.data),
    enabled: activeView === 'Risk Limits',
  });
  const { data: auditLog } = useQuery({
    queryKey: ['audit', 'log'],
    queryFn: () => portfolio.auditLog().then(r => r.data),
    enabled: activeView === 'Audit Log',
    refetchOnWindowFocus: false,
  });
  const { data: fundConfig } = useQuery({
    queryKey: ['fund', 'config'],
    queryFn: () => portfolio.fund().then(r => r.data),
  });
  const { data: orderList } = useQuery({
    queryKey: ['orders'],
    queryFn: () => orders.list().then(r => r.data),
    enabled: activeView === 'Orders',
    refetchInterval: activeView === 'Orders' ? 10000 : false,
  });

  const startMutation = useMutation({
    mutationFn: () => system.start().then(r => r.data),
    onSuccess: (data) => {
      if (data && 'candidates' in data) setShowChooser(true);
      qc.invalidateQueries({ queryKey: ['greeks'] });
      qc.invalidateQueries({ queryKey: ['proposal', 'current'] });
      qc.invalidateQueries({ queryKey: ['proposals', 'candidates'] });
    },
  });
  const killMutation = useMutation({
    mutationFn: (en: boolean) => system.killSwitch(en),
    onSuccess: (_, en) => setKillOn(en),
  });
  const connectSession = useMutation({
    mutationFn: (tok: string) => system.setSession(tok),
    onSuccess: () => { setReqToken(''); qc.invalidateQueries({ queryKey: ['session'] }); },
  });
  const ackAlert = useMutation({
    mutationFn: (id: string) => portfolio.ackAlert(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['alerts'] }),
  });
  const disconnectSession = useMutation({
    mutationFn: () => system.clearSession(),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['session'] }),
  });

  useMonitorSounds(openPositions, alerts);

  const fmt = (n: number) => n.toLocaleString('en-IN', { maximumFractionDigits: 0 });
  const startNav = fundConfig?.startingCapital ?? 0;
  // Live NAV = persisted (realised) NAV + open-position mark-to-market
  const nav = (greeks?.currentNav ?? startNav) + (greeks?.unrealisedPnl ?? 0);
  const navGain = nav - startNav;
  const mtdPct = startNav > 0 ? ((navGain / startNav) * 100).toFixed(2) : '0.00';
  const marginCap = fundConfig?.maxMarginUtilPct ?? 0;
  const monthlyTarget = fundConfig?.monthlyTargetPct ?? 0;
  const drawdownStop = fundConfig?.drawdownStopPct ?? 0;
  const ddPct = greeks?.drawdownPct?.toFixed(2) ?? '0.00';
  const unrlPnl = greeks?.unrealisedPnl ?? (openPositions?.reduce((s, p) => s + p.unrealisedPnl, 0) ?? 0);
  const marginPct = greeks?.marginUtilPct?.toFixed(1) ?? '0';
  // Risk budget = 2% of capital; allocated = sum of open positions' max-loss caps.
  const riskAllowance = nav * 0.02;
  const riskAllocated = openPositions?.reduce((s, p) => s + p.maxLoss, 0) ?? 0;
  const riskPct = riskAllowance > 0 ? (riskAllocated / riskAllowance) * 100 : 0;
  const theta = greeks?.netTheta?.toFixed(0) ?? '0';
  const vixVal = marketTicks?.vix ?? curProposal?.indiaVix;
  const ivRank = curProposal?.ivRank;
  const regime = vixVal != null ? vixRegime(vixVal) : null;
  const posCount = openPositions?.length ?? 0;
  const critAlerts = alerts?.filter(a => a.severity === 'Critical') ?? [];
  const warnAlerts = alerts?.filter(a => a.severity === 'Warning') ?? [];
  const allAlerts = [...critAlerts, ...warnAlerts];

  const metrics = [
    { label: 'NAV', value: `₹${fmt(nav)}`, sub: `${signOf(navGain)}₹${fmt(navGain)}`, valColor: color.text, subColor: pnlColor(navGain) },
    { label: 'Unrealised P&L', value: `${signOf(unrlPnl)}₹${fmt(unrlPnl)}`, sub: `${posCount} positions`, valColor: pnlColor(unrlPnl) },
    { label: 'Risk Allocated', value: `₹${fmt(riskAllocated)}`, sub: `of ₹${fmt(riskAllowance)} (2%) · ${riskPct.toFixed(0)}%`, valColor: riskPct > 100 ? color.neg : color.accent },
    { label: 'Margin Used', value: `${marginPct}%`, sub: greeks?.availableBalance != null ? `₹${fmt(greeks.availableBalance)} avail · Cap ${marginCap}%` : `Cap ${marginCap}%`, valColor: color.accent },
    { label: 'MTD Return', value: `${signOf(Number(mtdPct))}${mtdPct}%`, sub: `Target ${monthlyTarget}%`, valColor: pnlColor(Number(mtdPct)) },
    { label: 'Drawdown', value: `-${ddPct}%`, sub: `Stop ${drawdownStop}%`, valColor: drawdownStop > 0 && Number(ddPct) > drawdownStop / 2 ? color.neg : color.warn },
    { label: 'Net Θ / day', value: `+₹${fmt(Number(theta))}`, sub: `Δ ${greeks?.netDelta?.toFixed(2) ?? '—'}  Γ ${greeks?.netGamma?.toFixed(2) ?? '—'}`, valColor: color.pos },
  ] satisfies { label: string; value: string; sub: string; valColor: string; subColor?: string }[];

  const navItemStyle = (label: string): React.CSSProperties => {
    const active = activeView === label;
    return {
      padding: '7px 18px', cursor: 'pointer', display: 'flex', alignItems: 'center', gap: 10,
      color: active ? color.accentHover : color.textSub,
      fontWeight: active ? 600 : 500,
      borderLeft: `3px solid ${active ? color.accent : 'transparent'}`,
      background: active ? color.accentBg : 'transparent',
      fontFamily: font.sans, fontSize: '.8rem',
    };
  };

  // Badge for Alerts nav item reflects unacknowledged critical count.
  const navBadge = (label: string) => label === 'Alerts' && critAlerts.length > 0
    ? critAlerts.length.toString()
    : undefined;

  return (
    <div style={{ display: 'grid', gridTemplateColumns: '220px 1fr', height: '100vh', background: color.appBg, color: color.text, fontFamily: font.sans, fontSize: '.82rem', overflow: 'hidden' }}>

      {/* ─── SIDEBAR ─── */}
      <aside style={{ background: color.surface, borderRight: BORDER, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        <div style={{ padding: '16px 18px', borderBottom: BORDER, display: 'flex', alignItems: 'center', gap: 10, flexShrink: 0 }}>
          <div style={{ width: 28, height: 28, background: color.accent, borderRadius: 6, display: 'flex', alignItems: 'center', justifyContent: 'center', color: '#fff', fontSize: 14, fontWeight: 800 }}>Θ</div>
          <span style={{ fontSize: '.9rem', fontWeight: 700, color: color.text, letterSpacing: '.02em' }}>ThetaDesk</span>
        </div>

        <div style={{ flex: 1, overflowY: 'auto' }}>
          {NAV_SECTIONS.map(section => (
            <div key={section.label} style={{ padding: '10px 0', borderBottom: BORDER }}>
              <div style={{ padding: '4px 18px', fontSize: '.62rem', letterSpacing: '.12em', color: color.textMuted, textTransform: 'uppercase', fontWeight: 600 }}>{section.label}</div>
              {section.items.map(item => {
                const badge = navBadge(item.label);
                return (
                  <div key={item.label} style={navItemStyle(item.label)} onClick={() => setActiveView(item.label)}>
                    <div style={{ width: 6, height: 6, borderRadius: '50%', background: 'currentColor', flexShrink: 0, opacity: 0.7 }} />
                    {item.label}
                    {badge && <span style={{ marginLeft: 'auto' }}><Badge tone="neg">{badge}</Badge></span>}
                  </div>
                );
              })}
            </div>
          ))}
        </div>

        <div style={{ padding: '12px 18px', borderTop: BORDER, fontSize: '.72rem', color: color.textSub, flexShrink: 0 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            <span>Kill-switch:</span>
            <span onClick={() => killMutation.mutate(!killOn)} style={{ cursor: 'pointer' }}>
              <Badge tone={killOn ? 'neg' : 'pos'} mono>{killOn ? 'ON' : 'OFF'}</Badge>
            </span>
          </div>
          <div style={{ marginTop: 8 }}>NIFTY <span style={{ color: marketTicks?.nifty != null ? color.text : color.textFaint, fontFamily: font.mono }}>{marketTicks?.nifty != null ? fmt(marketTicks.nifty) : '—'}</span></div>
          <button
            style={{ marginTop: 8, padding: '2px 0', background: 'transparent', border: 'none', color: color.textSub, cursor: 'pointer', fontSize: '.72rem', fontFamily: font.sans }}
            onClick={() => { localStorage.removeItem('td_token'); location.reload(); }}
          >⎋ Logout</button>
        </div>
      </aside>

      {/* ─── MAIN ─── */}
      <div style={{ display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>

        {/* Topbar — persistent across all views */}
        <div style={{ background: color.surface, borderBottom: BORDER, padding: '8px 20px', display: 'flex', alignItems: 'center', gap: 14, fontSize: '.75rem', color: color.textSub, flexShrink: 0 }}>
          <Badge tone={mkt.open ? 'pos' : 'neutral'} mono>● {mkt.label}</Badge>
          {session?.paperTrading && <Badge tone="info" mono>◆ PAPER</Badge>}
          {critAlerts.length > 0 && <Badge tone="warn">⚑ {critAlerts.length} ALERT{critAlerts.length !== 1 ? 'S' : ''}</Badge>}
          {marketTicks?.nifty != null && <span>NIFTY <b style={{ color: color.text, fontFamily: font.mono }}>{fmt(marketTicks.nifty)}</b></span>}
          {vixVal != null && <span>VIX <b style={{ color: color.text, fontFamily: font.mono }}>{vixVal.toFixed(1)}</b></span>}
          {regime && <span>Regime <b style={{ color: color.text }}>{regime}</b></span>}
          {ivRank != null && <span>IV-Rank <b style={{ color: color.accent, fontFamily: font.mono }}>{(ivRank * 100).toFixed(0)}%</b></span>}
          {session && !session.valid && <span style={{ color: color.warn, fontWeight: 700 }}>⚠ SESSION EXPIRED</span>}
          {session?.valid && (
            <Button size="sm" variant="secondary" style={{ marginLeft: 'auto' }} disabled={disconnectSession.isPending} onClick={() => disconnectSession.mutate()}>
              {disconnectSession.isPending ? 'Disconnecting…' : '⏻ Disconnect'}
            </Button>
          )}
          <span style={{ marginLeft: session?.valid ? 12 : 'auto', color: color.textMuted, fontFamily: font.mono }}>IST {clock}</span>
        </div>

        {/* Session banner — persistent across all views */}
        {session && !session.valid && (
          <div style={{ background: color.warnBg, color: color.warn, borderBottom: `1px solid ${color.warnBorder}`, padding: '8px 20px', display: 'flex', alignItems: 'center', gap: 12, fontSize: '.82rem', flexWrap: 'wrap', flexShrink: 0 }}>
            <span>⚠ Kite session expired —{' '}
              <a href={session.loginUrl} target="_blank" rel="noreferrer" style={{ color: color.accentHover, fontWeight: 700 }}>Open Kite login →</a>
            </span>
            <input
              style={{ flex: 1, minWidth: 180, padding: '5px 10px', borderRadius: 6, border: `1px solid ${color.warnBorder}`, background: color.surface, color: color.text, fontSize: '.8rem', outline: 'none', fontFamily: font.mono }}
              placeholder="Paste request_token"
              value={reqToken}
              onChange={e => setReqToken(e.target.value)}
            />
            <Button variant="primary" size="sm" disabled={connectSession.isPending || !reqToken} onClick={() => connectSession.mutate(reqToken)}>
              {connectSession.isPending ? 'Connecting…' : 'Connect'}
            </Button>
            {connectSession.isError && <span style={{ color: color.neg, fontSize: '.78rem' }}>Token exchange failed</span>}
          </div>
        )}

        {/* ── Cockpit view ── */}
        {activeView === 'Cockpit' && (
          <>
            {/* Metrics strip */}
            <div style={{ display: 'grid', gridTemplateColumns: `repeat(${metrics.length}, 1fr)`, background: color.surface, borderBottom: BORDER, flexShrink: 0 }}>
              {metrics.map((m, i) => (
                <div key={m.label} style={{ padding: '10px 14px', borderRight: i < metrics.length - 1 ? BORDER : 'none' }}>
                  <MetricTile label={m.label} value={m.value} sub={m.sub} valueColor={m.valColor} subColor={m.subColor} />
                </div>
              ))}
            </div>

            {/* Body: positions table + right panel */}
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 280px', flex: 1, overflow: 'hidden' }}>
              <div style={{ overflowY: 'auto', display: 'flex', flexDirection: 'column' }}>
                <PositionsHeader posCount={posCount} adjusting={openPositions?.some(p => p.status === 'AutoAdjusting') ?? false} riskAllocated={riskAllocated} riskAllowance={riskAllowance} riskPct={riskPct} />
                <ColHeader />
                {openPositions?.map(p => (
                  <PositionCard key={p.id} position={p} onSimulate={setSimPosition} onClose={() => qc.invalidateQueries({ queryKey: ['positions'] })} />
                ))}
                {(!openPositions || posCount === 0) && (
                  <div style={{ padding: '20px', color: color.textMuted, fontSize: '.82rem' }}>No open positions.</div>
                )}
                <div style={{ marginTop: 'auto', padding: '8px 20px', color: color.textMuted, fontSize: '.72rem', borderTop: BORDER, flexShrink: 0 }}>
                  ▶ Click row to expand legs &nbsp;·&nbsp; All times IST
                </div>
              </div>

              {/* Right panel */}
              <div style={{ borderLeft: BORDER, display: 'flex', flexDirection: 'column', background: color.surface, overflow: 'hidden' }}>
                <div style={{ padding: '8px 14px', borderBottom: BORDER, fontSize: '.7rem', display: 'flex', gap: 16, fontFamily: font.sans, flexShrink: 0 }}>
                  {(['greeks', 'signal', 'alerts'] as const).map(tab => (
                    <span key={tab} onClick={() => setRightTab(tab)} style={{ cursor: 'pointer', textTransform: 'uppercase', letterSpacing: '.04em', fontWeight: 600, color: rightTab === tab ? color.accentHover : color.textMuted, borderBottom: rightTab === tab ? `2px solid ${color.accent}` : '2px solid transparent', paddingBottom: 3 }}>
                      {tab}
                      {tab === 'alerts' && critAlerts.length > 0 && ` (${critAlerts.length})`}
                    </span>
                  ))}
                </div>

                {rightTab === 'greeks' && (
                  <>
                    <GreeksGrid greeks={greeks} />
                    <MarginPanel greeks={greeks} fmt={fmt} />
                    <div style={{ flex: 1, overflowY: 'auto', padding: '10px 14px' }}>
                      <SectionLabel>Per-position P&L</SectionLabel>
                      {openPositions?.map(p => (
                        <div key={p.id} style={{ display: 'flex', justifyContent: 'space-between', padding: '5px 0', borderBottom: BORDER, fontSize: '.78rem' }}>
                          <span style={{ color: color.textSub }}>{p.strategy}</span>
                          <span style={{ color: pnlColor(p.unrealisedPnl), fontWeight: 600, fontFamily: font.mono }}>{signOf(p.unrealisedPnl)}₹{fmt(p.unrealisedPnl)}</span>
                        </div>
                      ))}
                      {posCount === 0 && <div style={{ color: color.textMuted, fontSize: '.75rem' }}>No positions.</div>}
                    </div>
                  </>
                )}

                {rightTab === 'signal' && (
                  <div style={{ flex: 1, overflowY: 'auto', padding: '12px 14px', display: 'flex', flexDirection: 'column', gap: 10 }}>
                    <SectionLabel>Signal Engine</SectionLabel>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: '.72rem' }}>
                      <span style={{ color: color.textSub }}>VIX</span>
                      <div style={{ flex: 1, height: 6, background: color.inset, borderRadius: 3, overflow: 'hidden' }}>
                        <div style={{ height: '100%', background: color.accent, borderRadius: 3, width: vixVal != null ? `${Math.min(vixVal / 30 * 100, 100)}%` : '0%', transition: 'width .5s' }} />
                      </div>
                      <b style={{ color: color.text, fontFamily: font.mono }}>{vixVal?.toFixed(1) ?? '—'}</b>
                    </div>
                    {regime && <div style={{ textAlign: 'center' }}><Badge tone="accent">{regime}</Badge></div>}
                    {curProposal && (
                      <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                        {[
                          { k: 'ATM IV', v: `${(curProposal.atmIv * 100).toFixed(1)}%` },
                          { k: 'IV-Rank', v: `${(curProposal.ivRank * 100).toFixed(0)}%` },
                          { k: 'DTE target', v: `${curProposal.entryDte}–${curProposal.targetExitDte}d` },
                        ].map(({ k, v }) => (
                          <div key={k} style={{ display: 'flex', justifyContent: 'space-between', fontSize: '.72rem' }}>
                            <span style={{ color: color.textSub }}>{k}</span>
                            <span style={{ color: color.text, fontWeight: 600, fontFamily: font.mono }}>{v}</span>
                          </div>
                        ))}
                      </div>
                    )}
                    {!curProposal && <div style={{ fontSize: '.72rem', color: color.textMuted }}>Run engine to load VIX data</div>}
                    <div style={{ marginTop: 'auto' }}>
                      <Button variant="primary" fullWidth disabled={startMutation.isPending || !session?.valid} onClick={() => startMutation.mutate()}>
                        {startMutation.isPending ? '● Scanning chains…' : '▶ Scan & suggest trades'}
                      </Button>
                      {candidates && candidates.length > 0 && !showChooser && !activeProposal && (
                        <Button variant="ghost" fullWidth size="sm" style={{ marginTop: 6, border: `1px solid ${color.accentBorder}` }} onClick={() => setShowChooser(true)}>
                          ▤ View {candidates.length} suggestion{candidates.length > 1 ? 's' : ''}
                        </Button>
                      )}
                      {startMutation.isError && <div style={{ marginTop: 6, fontSize: '.72rem', color: color.neg }}>Signal engine error — check logs</div>}
                      {startMutation.data && 'noCandidate' in (startMutation.data as NoCandidate) && (
                        <div style={{ marginTop: 6, fontSize: '.72rem', color: color.warn, lineHeight: 1.4 }}>
                          {(startMutation.data as NoCandidate).rejectReason}
                        </div>
                      )}
                      {session && !session.valid && <div style={{ marginTop: 6, fontSize: '.72rem', color: color.warn }}>⚠ Connect Kite session first</div>}
                    </div>
                  </div>
                )}

                {rightTab === 'alerts' && <AlertsList alerts={allAlerts} onAck={id => ackAlert.mutate(id)} />}
              </div>
            </div>
          </>
        )}

        {/* ── Positions view ── */}
        {activeView === 'Positions' && (
          <div style={{ flex: 1, overflowY: 'auto', display: 'flex', flexDirection: 'column', background: color.surface }}>
            <PositionsHeader posCount={posCount} adjusting={openPositions?.some(p => p.status === 'AutoAdjusting') ?? false} riskAllocated={riskAllocated} riskAllowance={riskAllowance} riskPct={riskPct} />
            <ColHeader />
            {openPositions?.map(p => (
              <PositionCard key={p.id} position={p} onSimulate={setSimPosition} onClose={() => qc.invalidateQueries({ queryKey: ['positions'] })} />
            ))}
            {(!openPositions || posCount === 0) && (
              <div style={{ padding: '20px', color: color.textMuted, fontSize: '.82rem' }}>No open positions.</div>
            )}
            <div style={{ marginTop: 'auto', padding: '8px 20px', color: color.textMuted, fontSize: '.72rem', borderTop: BORDER, flexShrink: 0 }}>
              ▶ Click row to expand legs &nbsp;·&nbsp; All times IST
            </div>
          </div>
        )}

        {/* ── Orders view ── */}
        {activeView === 'Orders' && (
          <div style={{ flex: 1, overflowY: 'auto', padding: '20px' }}>
            <ViewTitle title="Orders" />
            {orderList && orderList.length > 0 ? (
              <Card padded={false} style={{ padding: '0 16px' }}>
                <TableHeader columns={ORDER_COLS} cells={['Time (IST)', 'Symbol', 'Side', 'Qty', 'Fill', 'Slippage', 'Status']} />
                {orderList.map(o => (
                  <TableRow key={o.id} columns={ORDER_COLS}>
                    <span style={{ color: color.textMuted, fontFamily: font.mono, fontSize: '.7rem' }}>{toIST(o.placedAtUtc)}</span>
                    <span style={{ color: color.text, fontFamily: font.mono, fontSize: '.74rem' }}>{o.tradingSymbol}</span>
                    <span style={{ color: o.side === 'Sell' ? color.warn : color.accent, fontWeight: 600 }}>{o.side}</span>
                    <span style={{ color: color.textSub }}>{o.qty}</span>
                    <span style={{ color: color.text, fontFamily: font.mono }}>{o.fillPrice > 0 ? `₹${o.fillPrice.toFixed(2)}` : '—'}</span>
                    <span style={{ color: o.slippage > 0 ? color.neg : color.textMuted, fontFamily: font.mono }}>{o.slippage > 0 ? `₹${o.slippage.toFixed(2)}` : '—'}</span>
                    <span><Badge tone={orderStatusTone(o.status)}>{o.status}</Badge></span>
                  </TableRow>
                ))}
              </Card>
            ) : (
              <div style={{ color: color.textMuted, fontSize: '.82rem' }}>{orderList ? 'No orders yet.' : 'Loading…'}</div>
            )}
          </div>
        )}

        {/* ── Greeks view ── */}
        {activeView === 'Greeks' && (
          <div style={{ flex: 1, overflowY: 'auto', padding: '20px' }}>
            <ViewTitle title="Portfolio Greeks" />
            <Card padded={false} style={{ overflow: 'hidden', marginBottom: 16 }}><GreeksGrid greeks={greeks} large /></Card>
            <SectionLabel>Per-position breakdown</SectionLabel>
            <Card padded={false} style={{ padding: '0 16px' }}>
              {openPositions?.map(p => (
                <div key={p.id} style={{ display: 'flex', justifyContent: 'space-between', padding: '8px 0', borderBottom: BORDER, fontSize: '.82rem' }}>
                  <span style={{ color: color.textSub }}>{p.strategy}</span>
                  <span style={{ color: color.textMuted, fontSize: '.75rem' }}>Exp {p.expiryDate.slice(0, 10)}</span>
                  <span style={{ color: pnlColor(p.unrealisedPnl), fontWeight: 700, fontFamily: font.mono }}>{signOf(p.unrealisedPnl)}₹{fmt(p.unrealisedPnl)}</span>
                </div>
              ))}
              {posCount === 0 && <div style={{ color: color.textMuted, fontSize: '.78rem', padding: '12px 0' }}>No positions.</div>}
            </Card>
          </div>
        )}

        {/* ── P&L view ── */}
        {activeView === 'P&L' && (
          <div style={{ flex: 1, overflowY: 'auto', padding: '20px' }}>
            <ViewTitle title="P&L History" />
            {navHistory && (
              <>
                <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: 12, marginBottom: 20 }}>
                  <MetricTile surface="card" size="lg" label="Current NAV" value={`₹${fmt(nav)}`} valueColor={color.accent} />
                  <MetricTile surface="card" size="lg" label="MTD P&L" value={`${signOf(navGain)}₹${fmt(navGain)}`} valueColor={pnlColor(navGain)} />
                  <MetricTile surface="card" size="lg" label="MTD Return" value={`${signOf(Number(mtdPct))}${mtdPct}%`} valueColor={pnlColor(Number(mtdPct))} />
                </div>
                {navHistory.snapshots.length > 0 && (
                  <>
                    <SectionLabel>Daily Snapshots</SectionLabel>
                    <Card padded={false} style={{ padding: '0 16px' }}>
                      <TableHeader columns={SNAP_COLS} cells={['Date', 'NAV', 'Daily P&L', 'Charges', 'MTD %']} />
                      {navHistory.snapshots.map(s => (
                        <TableRow key={s.asOf} columns={SNAP_COLS}>
                          <span style={{ color: color.textSub }}>{s.asOf.slice(0, 10)}</span>
                          <span style={{ fontFamily: font.mono }}>₹{fmt(s.nav)}</span>
                          <span style={{ color: pnlColor(s.dailyPnl), fontFamily: font.mono }}>{signOf(s.dailyPnl)}₹{fmt(s.dailyPnl)}</span>
                          <span style={{ color: color.neg, fontFamily: font.mono }}>-₹{fmt(s.charges)}</span>
                          <span style={{ color: pnlColor(s.monthToDatePct), fontFamily: font.mono }}>{signOf(s.monthToDatePct)}{s.monthToDatePct.toFixed(2)}%</span>
                        </TableRow>
                      ))}
                    </Card>
                  </>
                )}
              </>
            )}
            {!navHistory && <div style={{ color: color.textMuted, fontSize: '.82rem' }}>Loading…</div>}
          </div>
        )}

        {/* ── Risk Limits view ── */}
        {activeView === 'Risk Limits' && (
          <div style={{ flex: 1, overflowY: 'auto', padding: '20px' }}>
            <ViewTitle title="Risk Limits" />
            {riskLimits && riskLimits.length > 0 ? (
              <Card padded={false} style={{ padding: '0 16px' }}>
                <TableHeader columns={LIMIT_COLS} cells={['Scope', 'Metric', 'Lower', 'Upper', 'Hard']} />
                {riskLimits.map(l => (
                  <TableRow key={l.id} columns={LIMIT_COLS}>
                    <span style={{ color: color.textSub }}>{l.scope}</span>
                    <span>{l.metric}</span>
                    <span style={{ color: color.accent, fontFamily: font.mono }}>{l.lowerBound ?? '—'}</span>
                    <span style={{ color: color.accent, fontFamily: font.mono }}>{l.upperBound ?? '—'}</span>
                    <span><Badge tone={l.hard ? 'neg' : 'pos'}>{l.hard ? 'Hard' : 'Soft'}</Badge></span>
                  </TableRow>
                ))}
              </Card>
            ) : (
              <div style={{ color: color.textMuted, fontSize: '.82rem' }}>{riskLimits ? 'No limits configured.' : 'Loading…'}</div>
            )}
          </div>
        )}

        {/* ── Alerts view ── */}
        {activeView === 'Alerts' && (
          <div style={{ flex: 1, overflowY: 'auto', padding: '20px' }}>
            <ViewTitle title={`Alerts${allAlerts.length > 0 ? ` — ${allAlerts.length} active` : ''}`} />
            <Card padded={false}><AlertsList alerts={allAlerts} onAck={id => ackAlert.mutate(id)} /></Card>
          </div>
        )}

        {/* ── Audit Log view ── */}
        {activeView === 'Audit Log' && (
          <div style={{ flex: 1, overflowY: 'auto', padding: '20px' }}>
            <ViewTitle title="Audit Log" />
            {auditLog && auditLog.length > 0 ? (
              <Card padded={false} style={{ padding: '0 16px' }}>
                <TableHeader columns={AUDIT_COLS} cells={['Time (IST)', 'Actor', 'Action', 'Details']} />
                {auditLog.map((entry) => {
                  const details = entry.afterJson ?? entry.beforeJson;
                  return (
                    <TableRow key={entry.id} columns={AUDIT_COLS} style={{ alignItems: 'start' }}>
                      <span style={{ color: color.textMuted, fontFamily: font.mono, fontSize: '.7rem' }}>{toIST(entry.atUtc)}</span>
                      <span style={{ color: entry.actor === 'System' ? color.accent : color.warn }}>{entry.actor}</span>
                      <span><Badge tone={auditActionTone(entry.action)}>{entry.action}</Badge></span>
                      <span style={{ color: color.textSub, fontFamily: font.mono, fontSize: '.66rem', wordBreak: 'break-all' }}>
                        {details ? details.slice(0, 100) : '—'}{details && details.length > 100 ? '…' : ''}
                      </span>
                    </TableRow>
                  );
                })}
              </Card>
            ) : (
              <div style={{ color: color.textMuted, fontSize: '.82rem' }}>{auditLog ? 'No audit entries.' : 'Loading…'}</div>
            )}
          </div>
        )}

        {/* ── Settings view ── */}
        {activeView === 'Settings' && (
          <div style={{ flex: 1, overflowY: 'auto', padding: '20px' }}>
            <ViewTitle title="Settings" />
            <SectionLabel>Fund</SectionLabel>
            <div style={{ marginBottom: 20 }}><FundSettings /></div>
            <SectionLabel>Strategies by VIX Regime</SectionLabel>
            <div style={{ marginBottom: 20 }}><StrategySettings /></div>

            <SectionLabel>Kite Session</SectionLabel>
            <Card padded={false} style={{ overflow: 'hidden' }}>
              {([
                { k: 'Status', v: session?.valid ? 'Connected' : 'Not connected', color: session?.valid ? color.pos : color.neg },
                { k: 'Expires At', v: session?.expiresAt ? toIST(session.expiresAt) : '—', color: color.text },
              ] as { k: string; v: string; color: string }[]).map(({ k, v, color: c }) => (
                <div key={k} style={{ display: 'flex', borderBottom: BORDER }}>
                  <div style={{ width: 180, padding: '8px 14px', background: color.subtle, fontSize: '.75rem', color: color.textSub, flexShrink: 0 }}>{k}</div>
                  <div style={{ flex: 1, padding: '8px 14px', fontSize: '.78rem', color: c, fontFamily: font.mono }}>{v}</div>
                </div>
              ))}
            </Card>
          </div>
        )}

        {/* ── Placeholder for unimplemented views ── */}
        {!IMPLEMENTED.has(activeView) && (
          <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', flexDirection: 'column', gap: 8, color: color.textMuted }}>
            <div style={{ fontSize: '.65rem', textTransform: 'uppercase', letterSpacing: '.12em' }}>{activeView}</div>
            <div style={{ fontSize: '.82rem' }}>Not yet implemented.</div>
          </div>
        )}
      </div>

      {/* Per-position payoff simulator. */}
      {simPosition && (
        <PayoffSimulator
          position={simPosition}
          spot={marketTicks?.nifty ?? null}
          vix={marketTicks?.vix ?? curProposal?.indiaVix ?? null}
          lotSize={fundConfig?.lotSize ?? 65}
          onClose={() => setSimPosition(null)}
        />
      )}

      {/* Candidate chooser — shown until the operator drills into one to review. */}
      {showChooser && candidates && candidates.length > 0 && !activeProposal && (
        <ProposalCandidates
          candidates={candidates}
          onPick={setActiveProposal}
          onClose={() => setShowChooser(false)}
        />
      )}

      {activeProposal && (
        <ProposalReview
          proposal={activeProposal}
          onClose={() => { // returns here after a Reject; drop the rejected one from the list
            setActiveProposal(null);
            qc.invalidateQueries({ queryKey: ['proposals', 'candidates'] });
          }}
          onApproved={() => {
            setActiveProposal(null);
            setShowChooser(false);
            qc.invalidateQueries({ queryKey: ['positions'] });
            qc.invalidateQueries({ queryKey: ['proposal', 'current'] });
            qc.invalidateQueries({ queryKey: ['proposals', 'candidates'] }); // siblings expired server-side
          }}
        />
      )}
    </div>
  );
}

// ─── Shared sub-components ───────────────────────────────────────────────────

function toIST(utcStr: string) {
  return new Date(utcStr).toLocaleString('en-IN', {
    timeZone: 'Asia/Kolkata', day: '2-digit', month: 'short',
    hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false,
  });
}

const ORDER_COLS = '150px 1fr 64px 56px 88px 88px 96px';
const SNAP_COLS = '120px 120px 110px 100px 1fr';
const LIMIT_COLS = '90px 130px 100px 100px 70px';
const AUDIT_COLS = '160px 80px 160px 1fr';

function orderStatusTone(status: string): Tone {
  if (status === 'Complete') return 'pos';
  if (status === 'Rejected' || status === 'Cancelled') return 'neg';
  return 'warn'; // Pending / Open — awaiting fill
}

function auditActionTone(action: string): Tone {
  if (/close|kill|stop|risk/i.test(action)) return 'neg';
  if (/approv|profit/i.test(action)) return 'pos';
  if (/proposal|generat/i.test(action)) return 'info';
  return 'neutral';
}

function PositionsHeader({ posCount, adjusting, riskAllocated, riskAllowance, riskPct }: {
  posCount: number; adjusting: boolean;
  riskAllocated: number; riskAllowance: number; riskPct: number;
}) {
  const f = (n: number) => n.toLocaleString('en-IN', { maximumFractionDigits: 0 });
  const barColor = riskPct >= 95 ? color.neg : riskPct >= 75 ? color.warn : color.pos;
  return (
    <div style={{ padding: '8px 20px 10px', background: color.surface, borderBottom: BORDER, flexShrink: 0 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
        <span style={{ fontSize: '.8rem', fontWeight: 700, color: color.text }}>Positions</span>
        <Badge tone={adjusting ? 'warn' : 'neutral'}>{posCount} open{adjusting ? ' · adjusting' : ''}</Badge>
        {riskPct >= 95 && <Badge tone="neg">Risk Full — no new trades</Badge>}
      </div>
      <div style={{ marginTop: 6, display: 'flex', alignItems: 'center', gap: 8, fontSize: '.68rem' }}>
        <span style={{ color: color.textSub, whiteSpace: 'nowrap' }}>Risk ₹{f(riskAllocated)} / ₹{f(riskAllowance)}</span>
        <div style={{ flex: 1, height: 4, background: color.inset, borderRadius: 2, overflow: 'hidden' }}>
          <div style={{ height: '100%', width: `${Math.min(riskPct, 100)}%`, background: barColor, borderRadius: 2, transition: 'width .4s' }} />
        </div>
        <span style={{ color: barColor, fontFamily: font.mono, fontWeight: 700, whiteSpace: 'nowrap' }}>{riskPct.toFixed(0)}%</span>
      </div>
    </div>
  );
}

function ColHeader() {
  return (
    <div style={{ padding: '0 20px', background: color.surface }}>
      <TableHeader columns={POS_COLS} cells={['Strategy', 'Expiry', 'DTE', 'Lots', 'Credit', 'P&L', 'MaxLoss', 'Status']} />
    </div>
  );
}

function GreeksGrid({ greeks, large }: { greeks: { netDelta: number; netGamma: number; netTheta: number; netVega: number } | undefined; large?: boolean }) {
  const cells = [
    { name: 'Delta (Δ)', val: greeks?.netDelta?.toFixed(2) ?? '—', c: color.text },
    { name: 'Gamma (Γ)', val: greeks?.netGamma?.toFixed(3) ?? '—', c: (greeks?.netGamma ?? 0) < -1.2 ? color.neg : color.text },
    { name: 'Theta (Θ)', val: greeks ? `+${greeks.netTheta.toFixed(0)}` : '—', c: color.pos },
    { name: 'Vega (ν)', val: greeks?.netVega?.toFixed(0) ?? '—', c: color.accent },
  ];
  return (
    <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 1, background: color.border, borderBottom: BORDER, flexShrink: 0 }}>
      {cells.map(g => (
        <div key={g.name} style={{ background: color.surface }}>
          <MetricTile label={g.name} value={g.val} valueColor={g.c} size={large ? 'lg' : 'md'} surface="none" style={{ padding: large ? '16px 20px' : '10px 12px' }} />
        </div>
      ))}
    </div>
  );
}

function MarginPanel({ greeks, fmt }: { greeks: import('../api/client').PortfolioGreeks | undefined; fmt: (n: number) => string }) {
  if (!greeks) return null;
  const mb = greeks.marginBreakdown;
  const rows: { label: string; value: string; color: string }[] = [
    { label: 'Available', value: `₹${fmt(greeks.availableBalance)}`, color: color.pos },
    { label: 'SPAN', value: `₹${fmt(mb.span)}`, color: color.textSub },
    { label: 'Exposure', value: `₹${fmt(mb.exposure)}`, color: color.textSub },
    ...(mb.optionPremium > 0 ? [{ label: 'Option Prem.', value: `₹${fmt(mb.optionPremium)}`, color: color.textSub }] : []),
    ...(mb.collateral > 0 ? [{ label: 'Collateral', value: `₹${fmt(mb.collateral)}`, color: color.textSub }] : []),
    { label: 'Total Capital', value: `₹${fmt(greeks.totalCapital)}`, color: color.text },
  ];
  return (
    <div style={{ padding: '8px 14px', borderBottom: BORDER, flexShrink: 0 }}>
      <SectionLabel>Margin</SectionLabel>
      {rows.map(r => (
        <div key={r.label} style={{ display: 'flex', justifyContent: 'space-between', fontSize: '.72rem', padding: '2px 0' }}>
          <span style={{ color: color.textSub }}>{r.label}</span>
          <span style={{ color: r.color, fontFamily: font.mono }}>{r.value}</span>
        </div>
      ))}
    </div>
  );
}

function AlertsList({ alerts, onAck }: { alerts: { id: string; kind: string; severity: string; message: string; raisedAtUtc: string }[]; onAck: (id: string) => void }) {
  if (alerts.length === 0) {
    return <div style={{ padding: '16px 14px', color: color.textMuted, fontSize: '.78rem' }}>No active alerts.</div>;
  }
  return (
    <>
      {alerts.map(a => {
        const crit = a.severity === 'Critical';
        return (
          <div key={a.id} style={{ padding: '10px 14px', borderBottom: BORDER, fontSize: '.72rem', display: 'flex', gap: 8, alignItems: 'flex-start' }}>
            <div style={{ width: 6, height: 6, borderRadius: '50%', flexShrink: 0, marginTop: 5, background: crit ? color.neg : color.warn }} />
            <div style={{ flex: 1 }}>
              <div style={{ color: crit ? color.neg : color.warn, fontWeight: 600 }}>{a.kind}</div>
              <div style={{ color: color.textSub, marginTop: 2 }}>{a.message}</div>
              <div style={{ color: color.textMuted, fontSize: '.65rem', marginTop: 4, display: 'flex', alignItems: 'center', gap: 8 }}>
                <span>{new Date(a.raisedAtUtc).toLocaleTimeString('en-IN', { hour12: false, hour: '2-digit', minute: '2-digit' })} IST</span>
                <Button size="sm" variant="secondary" onClick={() => onAck(a.id)}>Ack</Button>
              </div>
            </div>
          </div>
        );
      })}
    </>
  );
}
