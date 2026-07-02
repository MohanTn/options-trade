import axios from 'axios';

const api = axios.create({ baseURL: '/api/v1' });

api.interceptors.request.use((config) => {
  const token = localStorage.getItem('td_token');
  if (token) config.headers.Authorization = `Bearer ${token}`;
  return config;
});

export default api;

// --- Types ---
export type Strategy = 'ShortStrangle' | 'IronCondor' | 'DoubleCalendar' | 'CreditSpread';
export type PositionStatus = 'PendingFill' | 'Open' | 'AutoAdjusting' | 'ProfitTaking' | 'RiskStopping' | 'Closing' | 'Closed' | 'Settled';
export type AlertSeverity = 'Warning' | 'Critical';

export interface SessionStatus { valid: boolean; loginUrl?: string; expiresAt?: string; paperTrading?: boolean; }
export interface ProposalLeg { optionType: 'CE' | 'PE'; side: 'Buy' | 'Sell'; strike: number; tradingSymbol: string; midPrice: number; }
export interface Proposal {
  proposalId: string; strategy: Strategy; expiry: string; entryDte: number; targetExitDte: number;
  indiaVix: number; atmIv: number; ivRank: number; score: number; lots: number; qty: number;
  netCredit: number; maxLoss: number; marginBlocked: number; marginUtilPct: number;
  expectedReturnPct: number; rationale: string; status: string;
  legs: ProposalLeg[];
  limitVerdict?: { passed: boolean; violations: string[] };
}
export interface PositionLeg { id: string; strike: number; optionType: string; side: string; lots: number; qty: number; entryPrice: number; currentPrice: number; expiryDate?: string; }
export interface Position {
  id: string; strategy: Strategy; status: PositionStatus; entryDate: string; expiryDate: string;
  entryDte: number; netCredit: number; maxLoss: number; unrealisedPnl: number; realisedPnl: number;
  gttStopOrderId?: string;
  stopLossPremiumMult?: number;
  legs: PositionLeg[];
  lots?: number;
}
// Per-leg price update — entryPrice and/or exitPrice (the leg's current/closing price).
export interface LegPrice { legId: string; entryPrice?: number; exitPrice?: number; }
export interface MarginBreakdown { span: number; exposure: number; optionPremium: number; collateral: number; }
export interface PortfolioGreeks {
  netDelta: number; netGamma: number; netTheta: number; netVega: number;
  marginUtilPct: number; usedMargin: number; availableBalance: number; totalCapital: number;
  marginBreakdown: MarginBreakdown;
  unrealisedPnl: number; drawdownPct: number;
  currentNav: number; openPositionCount: number;
}
export interface Alert { id: string; kind: string; severity: AlertSeverity; message: string; raisedAtUtc: string; acknowledged: boolean; }
export interface NavSnapshot { asOf: string; nav: number; dailyPnl: number; charges: number; monthToDatePct: number; }
export interface RiskLimit { id: string; scope: string; metric: string; lowerBound?: number; upperBound?: number; hard: boolean; }
export interface MarketTicks { nifty: number | null; vix: number | null; }
export interface AuditEntry { id: string; actor: string; action: string; beforeJson: string | null; afterJson: string | null; atUtc: string; hashPrev: string | null; }
export interface FundConfig { id: string; name: string; startingCapital: number; cashBalance: number; currentNav: number; monthlyTargetPct: number; maxMarginUtilPct: number; perPositionMaxLoss: number; drawdownStopPct: number; profitTakePct: number; lotSize: number; }
// CurrentNav is broker-synced state, not operator-editable.
export type FundUpdate = Omit<FundConfig, 'id' | 'currentNav'>;
export type OrderStatus = 'Pending' | 'Open' | 'Complete' | 'Cancelled' | 'Rejected';
export interface Order {
  id: string; status: OrderStatus;
  fillPrice: number; slippage: number; placedAtUtc: string;
  side: 'Buy' | 'Sell'; qty: number; tradingSymbol: string;
}
export interface StrategyLeg { optionType: 'CE' | 'PE'; side: 'Buy' | 'Sell'; targetDelta: number; expiry: 'Near' | 'Far'; }
export interface StrategyConfig {
  id?: string; name: string; enabled: boolean; strategy: Strategy;
  vixMin: number; vixMax: number;
  entryDteMin: number; entryDteMax: number; sizingPct: number;
  weeklyCompounding: boolean;
  gttEnabled: boolean; gttPremiumPct: number;
  profitTargetPct: number; targetExitDte: number; adjustTriggerDelta: number;
  legs: StrategyLeg[];
}
export interface ChainStrikeRow {
  strike: number;
  ceOi: number; peOi: number;
  ceOiChange: number; peOiChange: number;
  ceVolume: number; peVolume: number;
  ceLtp: number; peLtp: number;
  ceIv: number; peIv: number;
}
export interface ExpiryAnalysis {
  expiry: string; dte: number; isMonthly: boolean;
  atmIv: number; pcrOi: number; pcrVolume: number;
  maxPain: number; supportStrike: number; resistanceStrike: number;
  totalCeOi: number; totalPeOi: number;
  ceOiChange: number; peOiChange: number;
  skewPct: number;
  atmStraddle: number; expectedMovePct: number; straddleChangePct: number;
  supportShift: number; resistanceShift: number;
  cePremiumSum: number; pePremiumSum: number;
  cePremiumChangePct: number; pePremiumChangePct: number;
  ceVegaSum: number; peVegaSum: number;
  ceVegaChangePct: number; peVegaChangePct: number;
  strikes: ChainStrikeRow[];
}
export interface ChainAnalysis {
  generatedAtUtc: string; spot: number; vix: number; morningVix: number;
  nearWeek: ExpiryAnalysis; nearMonth: ExpiryAnalysis;
  termSpreadPct: number; termStructure: string;
  biasScore: number; biasLabel: string;
  drivers: string[]; sellerPlaybook: string;
}

// --- API helpers ---
export const auth = {
  login: (password: string) => api.post<{ token: string }>('/auth/login', { password }),
};
export interface NoCandidate { noCandidate: true; rejectReason: string; }
export interface CandidateSet { candidates: Proposal[]; }
export const system = {
  session: () => api.get<SessionStatus>('/system/session'),
  setSession: (requestToken: string) => api.post('/system/session', { requestToken }),
  clearSession: () => api.delete('/system/session'),
  start: () => api.post<CandidateSet | NoCandidate>('/system/start'),
  killSwitch: (enable: boolean) => api.post('/system/kill-switch', { enable }),
};
export const proposals = {
  current: () => api.get<Proposal>('/proposals/current'),
  candidates: () => api.get<Proposal[]>('/proposals/candidates'),
  approve: (id: string) => api.post(`/proposals/${id}/approve`, null, { headers: { 'Idempotency-Key': crypto.randomUUID() } }),
  reject: (id: string, reason?: string) => api.post(`/proposals/${id}/reject`, { reason }),
};
export const positions = {
  list: (status?: string) => api.get<Position[]>(`/positions${status ? `?status=${status}` : ''}`),
  adjustments: (id: string) => api.get(`/positions/${id}/adjustments`),
  confirmEntry: (id: string, legs: LegPrice[]) => api.post(`/positions/${id}/confirm-entry`, { legs }),
  editLegs: (id: string, legs: LegPrice[]) => api.put(`/positions/${id}/legs`, { legs }),
  close: (id: string, reason: string, legs: LegPrice[] = []) => api.post(`/positions/${id}/close`, { reason, legs }),
  updateMaxLoss: (id: string, maxLoss: number) => api.patch(`/positions/${id}/max-loss`, { maxLoss }),
  updateLots: (id: string, lots: number) => api.patch(`/positions/${id}/lots`, { lots }),
};
export const market = {
  ticks: () => api.get<MarketTicks>('/market/ticks'),
};
export const portfolio = {
  greeks: () => api.get<PortfolioGreeks>('/portfolio/greeks'),
  alerts: (ack?: boolean) => api.get<Alert[]>(`/alerts${ack !== undefined ? `?ack=${ack}` : ''}`),
  ackAlert: (id: string) => api.post(`/alerts/${id}/ack`),
  navHistory: (from?: string, to?: string) => api.get<{ currentNav: number; snapshots: NavSnapshot[] }>('/nav/history', { params: { from, to } }),
  limits: () => api.get<RiskLimit[]>('/risk/limits'),
  updateLimit: (id: string, data: Partial<RiskLimit>) => api.put(`/risk/limits/${id}`, data),
  auditLog: (limit = 100) => api.get<AuditEntry[]>(`/audit-log?limit=${limit}`),
  fund: () => api.get<FundConfig>('/fund'),
  updateFund: (data: FundUpdate) => api.put<FundConfig>('/fund', data),
};
export const orders = {
  list: (limit = 200) => api.get<Order[]>(`/orders?limit=${limit}`),
};
// One intraday sample of per-side Σ-vega change vs morning (percent), sampled ~60s.
export interface VegaFlowPoint { atUtc: string; weekCe: number; weekPe: number; monthCe: number; monthPe: number; spot: number; }
export const analysis = {
  chain: (refresh = false) => api.get<ChainAnalysis>(`/analysis/chain${refresh ? '?refresh=true' : ''}`),
  vegaFlow: () => api.get<VegaFlowPoint[]>('/analysis/vega-flow'),
  // Permanent EOD archive (Postgres): one snapshot per trading day, written at market close (15:30 IST).
  vegaFlowHistoryDates: () => api.get<string[]>('/analysis/vega-flow/history-dates'),
  vegaFlowHistory: (date: string) => api.get<VegaFlowPoint[]>(`/analysis/vega-flow/history/${date}`),
};
export const strategies = {
  list: () => api.get<StrategyConfig[]>('/strategies'),
  create: (cfg: StrategyConfig) => api.post<StrategyConfig>('/strategies', cfg),
  update: (id: string, cfg: StrategyConfig) => api.put<StrategyConfig>(`/strategies/${id}`, cfg),
  remove: (id: string) => api.delete(`/strategies/${id}`),
};
