import { useState } from 'react';
import { auth } from '../api/client';
import { color, font, radius, shadow } from '../theme';
import { Button, Card, Field, Input } from '../ui';

export default function Login({ onLogin }: { onLogin: () => void }) {
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setLoading(true);
    setError('');
    try {
      const { data } = await auth.login(password);
      localStorage.setItem('td_token', data.token);
      onLogin();
    } catch {
      setError('Invalid password');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div style={{ minHeight: '100vh', display: 'flex', alignItems: 'center', justifyContent: 'center', background: color.appBg, padding: 16 }}>
      <Card style={{ width: 360, padding: '32px 36px', boxShadow: shadow.pop }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 4 }}>
          <div
            style={{
              width: 32,
              height: 32,
              background: color.accent,
              borderRadius: radius.md,
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              color: '#fff',
              fontSize: 16,
              fontWeight: 800,
            }}
          >
            Θ
          </div>
          <h1 style={{ margin: 0, fontSize: '1.4rem', color: color.text, fontFamily: font.sans }}>ThetaDesk</h1>
        </div>
        <p style={{ color: color.textSub, margin: '0 0 24px', fontSize: '.85rem' }}>NIFTY 50 Theta-Harvesting Fund</p>

        <form onSubmit={submit} style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <Field label="Operator password">
            <Input
              type="password"
              placeholder="••••••••"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoFocus
            />
          </Field>
          {error && <p style={{ color: color.neg, fontSize: '.82rem', margin: 0 }}>{error}</p>}
          <Button type="submit" variant="primary" fullWidth disabled={loading} style={{ marginTop: 4, padding: '10px' }}>
            {loading ? 'Signing in…' : 'Sign in'}
          </Button>
        </form>
      </Card>
    </div>
  );
}
