import type { CSSProperties, ReactNode } from 'react';
import { color, font } from '../theme';

/** Uppercase column-header row for a CSS-grid table. */
export function TableHeader({ columns, cells }: { columns: string; cells: ReactNode[] }) {
  return (
    <div
      style={{
        display: 'grid',
        gridTemplateColumns: columns,
        fontSize: '.64rem',
        color: color.textSub,
        textTransform: 'uppercase',
        letterSpacing: '.06em',
        fontWeight: 600,
        fontFamily: font.sans,
        padding: '8px 0',
        borderBottom: `1px solid ${color.border}`,
      }}
    >
      {cells.map((c, i) => (
        <span key={i}>{c}</span>
      ))}
    </div>
  );
}

/** A single CSS-grid table row; clickable when `onClick` is provided. */
export function TableRow({
  columns,
  onClick,
  style,
  children,
}: {
  columns: string;
  onClick?: () => void;
  style?: CSSProperties;
  children: ReactNode;
}) {
  return (
    <div
      onClick={onClick}
      style={{
        display: 'grid',
        gridTemplateColumns: columns,
        alignItems: 'center',
        fontSize: '.8rem',
        padding: '8px 0',
        borderBottom: `1px solid ${color.border}`,
        cursor: onClick ? 'pointer' : undefined,
        ...style,
      }}
    >
      {children}
    </div>
  );
}
