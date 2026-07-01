// Central design tokens — the single source of truth for ThetaDesk's UI.
//
// Light "corporate" theme: white surfaces, slate text, a single blue accent.
// Green/red are reserved for semantic meaning (P&L, risk, pass/fail) and are
// never used for decoration, so a glance at colour always carries information.

export const color = {
  // Surfaces
  appBg: '#f1f5f9', // page background (slate-100)
  surface: '#ffffff', // cards, panels, modals
  subtle: '#f8fafc', // inset tiles, table headers (slate-50)
  inset: '#f1f5f9', // hover / pressed rows

  // Borders
  border: '#e2e8f0', // slate-200
  borderStrong: '#cbd5e1', // slate-300

  // Text
  text: '#0f172a', // slate-900
  textSub: '#475569', // slate-600
  textMuted: '#94a3b8', // slate-400
  textFaint: '#cbd5e1', // slate-300

  // Brand accent
  accent: '#2563eb', // blue-600
  accentHover: '#1d4ed8', // blue-700
  accentBg: '#eff6ff', // blue-50
  accentBorder: '#bfdbfe', // blue-200

  // Semantic
  pos: '#16a34a',
  posBg: '#f0fdf4',
  posBorder: '#bbf7d0',
  neg: '#dc2626',
  negBg: '#fef2f2',
  negBorder: '#fecaca',
  warn: '#b45309',
  warnBg: '#fffbeb',
  warnBorder: '#fde68a',
  info: '#0e7490',
  infoBg: '#ecfeff',
  infoBorder: '#a5f3fc',
} as const;

export const font = {
  sans: "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif",
  mono: "'SF Mono', 'Roboto Mono', 'Courier New', monospace",
} as const;

export const radius = { sm: 4, md: 6, lg: 8, xl: 12 } as const;

export const shadow = {
  card: '0 1px 2px rgba(15,23,42,.06)',
  pop: '0 4px 16px rgba(15,23,42,.12)',
  modal: '0 20px 60px rgba(15,23,42,.25)',
} as const;

// Tone = a semantic colour role shared by Badge, MetricTile, Stat, banners, …
export type Tone = 'neutral' | 'accent' | 'pos' | 'neg' | 'warn' | 'info';

export const tones: Record<Tone, { fg: string; bg: string; border: string }> = {
  neutral: { fg: color.textSub, bg: color.subtle, border: color.border },
  accent: { fg: color.accentHover, bg: color.accentBg, border: color.accentBorder },
  pos: { fg: color.pos, bg: color.posBg, border: color.posBorder },
  neg: { fg: color.neg, bg: color.negBg, border: color.negBorder },
  warn: { fg: color.warn, bg: color.warnBg, border: color.warnBorder },
  info: { fg: color.info, bg: color.infoBg, border: color.infoBorder },
};

// Categorical (not semantic) tone per strategy, for labels/badges only.
export const strategyTone: Record<string, Tone> = {
  ShortStrangle: 'accent',
  IronCondor: 'info',
  DoubleCalendar: 'warn',
  CreditSpread: 'pos',
};

// Helpers for the most common signed-number formatting in a trading UI.
export const pnlColor = (n: number) => (n >= 0 ? color.pos : color.neg);
export const signOf = (n: number) => (n >= 0 ? '+' : '');
