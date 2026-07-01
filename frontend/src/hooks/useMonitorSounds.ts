import { useRef, useEffect, useCallback } from 'react';
import type { Position, Alert } from '../api/client';

type SoundEvent = 'takeProfit' | 'stopLoss' | 'adjustment' | 'criticalAlert' | 'warningAlert';

// [freq Hz, waveform, startOffset s, duration s, volume 0-1]
type ToneSpec = [number, OscillatorType, number, number, number];

const PATTERNS: Record<SoundEvent, ToneSpec[]> = {
  // Ascending major arpeggio — celebration, profit captured
  takeProfit: [
    [523, 'sine', 0.00, 0.30, 0.40],
    [659, 'sine', 0.13, 0.30, 0.40],
    [784, 'sine', 0.26, 0.50, 0.50],
  ],
  // Descending sawtooth alarm — stop-loss hit, urgent warning
  stopLoss: [
    [440, 'sawtooth', 0.00, 0.18, 0.50],
    [370, 'sawtooth', 0.22, 0.18, 0.50],
    [311, 'sawtooth', 0.44, 0.25, 0.60],
  ],
  // Two short square beeps — adjustment triggered, needs attention
  adjustment: [
    [660, 'square', 0.00, 0.12, 0.25],
    [660, 'square', 0.18, 0.12, 0.25],
  ],
  // Three rapid square pulses — critical alert, immediate action required
  criticalAlert: [
    [880, 'square', 0.00, 0.10, 0.40],
    [880, 'square', 0.16, 0.10, 0.40],
    [880, 'square', 0.32, 0.15, 0.50],
  ],
  // Single soft sine chime — informational warning
  warningAlert: [
    [1047, 'sine', 0.00, 0.30, 0.20],
  ],
};

function scheduleTone(
  ctx: AudioContext,
  [freq, type, offset, duration, volume]: ToneSpec,
  baseTime: number,
) {
  const osc = ctx.createOscillator();
  const gain = ctx.createGain();
  osc.connect(gain);
  gain.connect(ctx.destination);
  osc.type = type;
  osc.frequency.setValueAtTime(freq, baseTime + offset);
  gain.gain.setValueAtTime(0, baseTime + offset);
  gain.gain.linearRampToValueAtTime(volume, baseTime + offset + 0.01);
  gain.gain.exponentialRampToValueAtTime(0.001, baseTime + offset + duration);
  osc.start(baseTime + offset);
  osc.stop(baseTime + offset + duration + 0.05);
}

export function useMonitorSounds(
  positions: Position[] | undefined,
  alerts: Alert[] | undefined,
) {
  const ctxRef = useRef<AudioContext | null>(null);
  const prevStatusRef = useRef(new Map<string, string>());
  // Track whether we've seen the first alert batch (suppress sounds on initial load).
  const alertsInitRef = useRef(false);
  const prevAlertIdsRef = useRef(new Set<string>());

  const play = useCallback((event: SoundEvent) => {
    try {
      if (!ctxRef.current) ctxRef.current = new AudioContext();
      const ctx = ctxRef.current;
      if (ctx.state === 'suspended') ctx.resume();
      const t = ctx.currentTime;
      for (const spec of PATTERNS[event]) scheduleTone(ctx, spec, t);
    } catch {
      // AudioContext unavailable or blocked — silently ignore
    }
  }, []);

  // Detect position status transitions and fire the matching sound.
  useEffect(() => {
    if (!positions) return;
    const prev = prevStatusRef.current;
    const next = new Map<string, string>();
    for (const p of positions) {
      next.set(p.id, p.status);
      const was = prev.get(p.id);
      if (was !== undefined && was !== p.status) {
        if (p.status === 'ProfitTaking')  play('takeProfit');
        else if (p.status === 'RiskStopping') play('stopLoss');
        else if (p.status === 'AutoAdjusting') play('adjustment');
      }
    }
    prevStatusRef.current = next;
  }, [positions, play]);

  // Detect net-new alerts and fire the severity-matched sound.
  useEffect(() => {
    if (!alerts) return;
    if (!alertsInitRef.current) {
      // First fetch — just seed the known-IDs set; don't play any sound.
      alertsInitRef.current = true;
      prevAlertIdsRef.current = new Set(alerts.map(a => a.id));
      return;
    }
    const prev = prevAlertIdsRef.current;
    for (const a of alerts) {
      if (!prev.has(a.id)) {
        play(a.severity === 'Critical' ? 'criticalAlert' : 'warningAlert');
      }
    }
    prevAlertIdsRef.current = new Set(alerts.map(a => a.id));
  }, [alerts, play]);
}
