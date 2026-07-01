import type { ReactNode } from 'react';
import { font, radius, tones, type Tone } from '../theme';

type Props = {
  tone?: Tone;
  mono?: boolean;
  children: ReactNode;
};

/** Small status pill used for statuses, regimes, flags and category labels. */
export function Badge({ tone = 'neutral', mono, children }: Props) {
  const t = tones[tone];
  return (
    <span
      style={{
        display: 'inline-block',
        padding: '2px 8px',
        borderRadius: radius.sm,
        fontSize: '.68rem',
        fontWeight: 600,
        fontFamily: mono ? font.mono : font.sans,
        background: t.bg,
        color: t.fg,
        border: `1px solid ${t.border}`,
        whiteSpace: 'nowrap',
        lineHeight: 1.5,
      }}
    >
      {children}
    </span>
  );
}
