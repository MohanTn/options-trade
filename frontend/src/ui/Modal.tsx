import type { ReactNode } from 'react';
import { color, font, radius, shadow } from '../theme';

type Props = {
  title: ReactNode;
  subtitle?: ReactNode;
  /** Optional node rendered on the right of the header (e.g. a Badge). */
  headerRight?: ReactNode;
  onClose: () => void;
  /** Close when the backdrop is clicked. Off by default for review dialogs. */
  closeOnBackdrop?: boolean;
  width?: number;
  children: ReactNode;
};

/** Centered dialog with a consistent header, close affordance and scroll body. */
export function Modal({ title, subtitle, headerRight, onClose, closeOnBackdrop, width = 560, children }: Props) {
  return (
    <div
      onClick={closeOnBackdrop ? onClose : undefined}
      style={{
        position: 'fixed',
        inset: 0,
        background: 'rgba(15,23,42,.45)',
        zIndex: 100,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        padding: 16,
      }}
    >
      <div
        onClick={(e) => e.stopPropagation()}
        style={{
          background: color.surface,
          borderRadius: radius.xl,
          width,
          maxWidth: '100%',
          maxHeight: '92vh',
          overflowY: 'auto',
          boxShadow: shadow.modal,
        }}
      >
        <div
          style={{
            position: 'sticky',
            top: 0,
            zIndex: 1,
            background: color.surface,
            padding: '16px 20px',
            borderBottom: `1px solid ${color.border}`,
            borderRadius: `${radius.xl}px ${radius.xl}px 0 0`,
            display: 'flex',
            alignItems: 'flex-start',
            gap: 12,
          }}
        >
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ fontFamily: font.sans, fontWeight: 700, fontSize: '1.05rem', color: color.text }}>{title}</div>
            {subtitle && <div style={{ fontSize: '.78rem', color: color.textSub, marginTop: 2 }}>{subtitle}</div>}
          </div>
          {headerRight}
          <button
            onClick={onClose}
            aria-label="Close"
            style={{
              flexShrink: 0,
              width: 30,
              height: 30,
              border: `1px solid ${color.border}`,
              background: color.subtle,
              borderRadius: radius.md,
              cursor: 'pointer',
              color: color.textSub,
              fontSize: '.9rem',
              lineHeight: 1,
            }}
          >
            ✕
          </button>
        </div>
        {children}
      </div>
    </div>
  );
}
