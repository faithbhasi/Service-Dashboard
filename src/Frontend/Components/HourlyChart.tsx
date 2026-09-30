import { useState } from 'react';
import { Card, ErrorNote, Note, Spinner } from './ui';
import { useAsync } from '../Hooks/useAsync';
import { get } from '../Services/api';

export interface HourlyPoint { hourUtc: string; passwordResets: number; unlocks: number; lockouts: number }
export interface HourlyActivity {
  hours: number; points: HourlyPoint[]; totalPasswordResets: number; totalUnlocks: number; totalLockouts: number; lockoutsNote: string | null;
}

const SERIES = [
  { key: 'passwordResets', label: 'Password resets', color: 'var(--primary)' },
  { key: 'unlocks', label: 'Unlocks', color: 'var(--warning)' },
  { key: 'lockouts', label: 'Account lockouts', color: 'var(--error)' },
] as const;

const W = 960, H = 260, L = 40, R = 8, T = 10, B = 28;

const hourLabel = (iso: string) => new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

/** Grouped bars, one group per hour. The bars are plain SVG so they follow the theme colours; a table below has the same numbers. */
export function HourlyBars({ data }: { data: HourlyActivity }) {
  const pts = data.points;
  const peak = Math.max(1, ...pts.flatMap((p) => SERIES.map((s) => p[s.key])));
  const step = Math.max(1, Math.ceil(peak / 4));
  const top = step * 4;
  const plotW = W - L - R, plotH = H - T - B;
  const group = plotW / pts.length;
  const bar = Math.max(2, (group * 0.78) / SERIES.length);
  const y = (v: number) => T + plotH - (v / top) * plotH;
  const labelEvery = pts.length > 36 ? 6 : pts.length > 18 ? 3 : 2;

  return (
    <svg className="chart" viewBox={`0 0 ${W} ${H}`} role="img"
      aria-label={`Hourly counts for the last ${data.hours} hours: ${data.totalPasswordResets} password resets, ${data.totalUnlocks} unlocks, ${data.totalLockouts} account lockouts.`}>
      {[0, 1, 2, 3, 4].map((i) => (
        <g key={i}>
          <line x1={L} x2={W - R} y1={y(i * step)} y2={y(i * step)} stroke="var(--border)" strokeWidth="1" />
          <text x={L - 6} y={y(i * step) + 4} textAnchor="end" fontSize="11" fill="var(--text-muted)">{i * step}</text>
        </g>
      ))}
      {pts.map((p, i) => {
        const x0 = L + i * group + (group - bar * SERIES.length) / 2;
        return (
          <g key={p.hourUtc}>
            {SERIES.map((s, k) => {
              const v = p[s.key];
              return (
                <rect key={s.key} x={x0 + k * bar} y={y(v)} width={bar - 1} height={Math.max(0, T + plotH - y(v))} rx="1.5" fill={s.color}>
                  <title>{`${hourLabel(p.hourUtc)}: ${s.label} ${v}`}</title>
                </rect>
              );
            })}
            {i % labelEvery === 0 && <text x={L + i * group + group / 2} y={H - 8} textAnchor="middle" fontSize="11" fill="var(--text-muted)">{hourLabel(p.hourUtc)}</text>}
          </g>
        );
      })}
    </svg>
  );
}

/** Home chart: how many password resets, unlocks and lockouts happened in each hour. */
export function HourlyActivityCard() {
  const [hours, setHours] = useState(24);
  const data = useAsync(() => get<HourlyActivity>(`/modules/ad/activity/hourly?hours=${hours}`), [hours]);
  const d = data.data;
  const totals = d ? { passwordResets: d.totalPasswordResets, unlocks: d.totalUnlocks, lockouts: d.totalLockouts } : null;

  return (
    <Card title="Activity by hour" actions={
      <select value={hours} onChange={(e) => setHours(Number(e.target.value))} aria-label="Time range">
        <option value={12}>Last 12 hours</option><option value={24}>Last 24 hours</option><option value={48}>Last 48 hours</option><option value={72}>Last 72 hours</option>
      </select>
    }>
      <ErrorNote error={data.error} />
      {data.loading && !d && <Spinner />}
      {d && totals && (
        <>
          <div className="chart-legend">
            {SERIES.map((s) => (
              <span key={s.key} className="chart-key"><span className="chart-swatch" style={{ background: s.color }} /> {s.label} <strong>{totals[s.key]}</strong></span>
            ))}
          </div>
          <HourlyBars data={d} />
          <p className="muted small">
            Password resets and unlocks are the ones made through this application. Lockouts come from the lockout time Active Directory records,
            so an account that has already been unlocked may not be counted. Times are in your time zone.
          </p>
          {d.lockoutsNote && <Note kind="warning">{d.lockoutsNote}</Note>}
          <details>
            <summary>Show the numbers as a table</summary>
            <div className="table-wrap" style={{ marginTop: 8, maxHeight: 260, overflow: 'auto' }}>
              <table className="table">
                <thead><tr><th>Hour</th>{SERIES.map((s) => <th key={s.key}>{s.label}</th>)}</tr></thead>
                <tbody>
                  {[...d.points].reverse().map((p) => (
                    <tr key={p.hourUtc}><td>{new Date(p.hourUtc).toLocaleString([], { dateStyle: 'short', timeStyle: 'short' })}</td>{SERIES.map((s) => <td key={s.key}>{p[s.key]}</td>)}</tr>
                  ))}
                </tbody>
              </table>
            </div>
          </details>
        </>
      )}
    </Card>
  );
}
