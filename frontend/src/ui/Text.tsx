import type { CSSProperties, ReactNode } from 'react';
import { color, font } from '../theme';

/** Page / view heading. */
export function ViewTitle({ title, style }: { title: ReactNode; style?: CSSProperties }) {
  return (
    <h2 style={{ margin: '0 0 14px', fontSize: '1.05rem', fontWeight: 700, color: color.text, fontFamily: font.sans, ...style }}>
      {title}
    </h2>
  );
}

/** Small uppercase label that introduces a group of content. */
export function SectionLabel({ children }: { children: ReactNode }) {
  return (
    <div
      style={{
        fontSize: '.64rem',
        color: color.textSub,
        textTransform: 'uppercase',
        letterSpacing: '.08em',
        fontWeight: 600,
        fontFamily: font.sans,
        marginBottom: 8,
      }}
    >
      {children}
    </div>
  );
}
