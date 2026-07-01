import type { ButtonHTMLAttributes, CSSProperties } from 'react';
import { color, font, radius } from '../theme';

type Variant = 'primary' | 'secondary' | 'ghost' | 'danger';
type Size = 'sm' | 'md';

const base: CSSProperties = {
  fontFamily: font.sans,
  fontWeight: 600,
  borderRadius: radius.md,
  cursor: 'pointer',
  border: '1px solid transparent',
  lineHeight: 1.2,
  whiteSpace: 'nowrap',
  transition: 'background .12s, border-color .12s, opacity .12s',
};

const sizes: Record<Size, CSSProperties> = {
  sm: { padding: '4px 10px', fontSize: '.74rem' },
  md: { padding: '8px 14px', fontSize: '.82rem' },
};

const variants: Record<Variant, CSSProperties> = {
  primary: { background: color.accent, color: '#fff', borderColor: color.accent },
  secondary: { background: color.surface, color: color.textSub, borderColor: color.border },
  ghost: { background: 'transparent', color: color.accentHover, borderColor: 'transparent' },
  danger: { background: color.surface, color: color.neg, borderColor: color.negBorder },
};

type Props = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: Variant;
  size?: Size;
  fullWidth?: boolean;
};

export function Button({ variant = 'secondary', size = 'md', fullWidth, style, disabled, ...rest }: Props) {
  return (
    <button
      disabled={disabled}
      style={{
        ...base,
        ...sizes[size],
        ...variants[variant],
        width: fullWidth ? '100%' : undefined,
        cursor: disabled ? 'default' : 'pointer',
        opacity: disabled ? 0.55 : 1,
        ...style,
      }}
      {...rest}
    />
  );
}
