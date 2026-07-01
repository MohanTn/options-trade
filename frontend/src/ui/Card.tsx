import type { CSSProperties, ReactNode } from 'react';
import { color, radius, shadow } from '../theme';

type Props = {
  children: ReactNode;
  padded?: boolean;
  style?: CSSProperties;
};

/** White elevated surface used to group related content. */
export function Card({ children, padded = true, style }: Props) {
  return (
    <div
      style={{
        background: color.surface,
        border: `1px solid ${color.border}`,
        borderRadius: radius.lg,
        boxShadow: shadow.card,
        padding: padded ? 16 : 0,
        ...style,
      }}
    >
      {children}
    </div>
  );
}
