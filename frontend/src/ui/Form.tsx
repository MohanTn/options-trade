import type { CSSProperties, InputHTMLAttributes, ReactNode, SelectHTMLAttributes } from 'react';
import { color, font, radius } from '../theme';

/** Uppercase micro-label that sits above a control or section. */
export function Label({ children }: { children: ReactNode }) {
  return (
    <div
      style={{
        fontSize: '.64rem',
        color: color.textSub,
        textTransform: 'uppercase',
        letterSpacing: '.06em',
        fontFamily: font.sans,
        fontWeight: 600,
        marginBottom: 4,
      }}
    >
      {children}
    </div>
  );
}

/** A labelled form control wrapper. */
export function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <Label>{label}</Label>
      {children}
    </div>
  );
}

const control: CSSProperties = {
  width: '100%',
  padding: '7px 10px',
  background: color.surface,
  border: `1px solid ${color.border}`,
  borderRadius: radius.md,
  color: color.text,
  fontFamily: font.mono,
  fontSize: '.82rem',
  outline: 'none',
};

export function Input(props: InputHTMLAttributes<HTMLInputElement>) {
  return <input {...props} style={{ ...control, ...props.style }} />;
}

export function Select(props: SelectHTMLAttributes<HTMLSelectElement>) {
  return <select {...props} style={{ ...control, fontFamily: font.sans, ...props.style }} />;
}
