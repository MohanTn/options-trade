import type { CSSProperties, ReactNode } from 'react';
import { color, font, radius } from '../theme';

type Size = 'sm' | 'md' | 'lg';
type Surface = 'none' | 'subtle' | 'card';

type Props = {
  label: ReactNode;
  value: ReactNode;
  sub?: ReactNode;
  /** Colour of the value text; defaults to primary text. Use pnlColor() for P&L. */
  valueColor?: string;
  subColor?: string;
  size?: Size;
  surface?: Surface;
  style?: CSSProperties;
};

const valueSize: Record<Size, string> = { sm: '.95rem', md: '1.1rem', lg: '1.6rem' };
const pad: Record<Size, string> = { sm: '8px 10px', md: '10px 14px', lg: '16px 18px' };

/**
 * Label / value / optional-sub tile. The single primitive behind the dashboard
 * metric strip, modal stat grids and the P&L summary cards — `surface` controls
 * whether it renders bare, on a subtle inset, or as its own card.
 */
export function MetricTile({
  label,
  value,
  sub,
  valueColor = color.text,
  subColor = color.textMuted,
  size = 'md',
  surface = 'none',
  style,
}: Props) {
  return (
    <div
      style={{
        padding: surface === 'none' ? 0 : pad[size],
        background: surface === 'card' ? color.surface : surface === 'subtle' ? color.subtle : undefined,
        border: surface === 'card' ? `1px solid ${color.border}` : undefined,
        borderRadius: surface === 'none' ? undefined : radius.md,
        ...style,
      }}
    >
      <div
        style={{
          fontSize: '.62rem',
          color: color.textSub,
          letterSpacing: '.06em',
          textTransform: 'uppercase',
          fontFamily: font.sans,
          fontWeight: 600,
          marginBottom: 3,
        }}
      >
        {label}
      </div>
      <div style={{ fontSize: valueSize[size], fontWeight: 700, color: valueColor, fontVariantNumeric: 'tabular-nums' }}>
        {value}
      </div>
      {sub != null && <div style={{ fontSize: '.66rem', color: subColor, marginTop: 2 }}>{sub}</div>}
    </div>
  );
}
