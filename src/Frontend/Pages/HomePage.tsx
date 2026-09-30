import { Link } from 'react-router-dom';
import { HourlyActivityCard } from '../Components/HourlyChart';
import { PageGuard } from '../Components/PageGuard';
import { Card, DataTable, ErrorNote, Note, PageHeader, Spinner, Tag } from '../Components/ui';
import { useAuth } from '../Hooks/AuthContext';
import { useShell } from '../Hooks/ShellContext';
import { useEffect, useState } from 'react';
import { useAsync } from '../Hooks/useAsync';
import { get } from '../Services/api';
import { Permissions } from '../Services/permissions';

interface CardData { key: string; title: string; count: number; route: string; tone: 'neutral' | 'warning' | 'error'; updatedUtc: string }
interface ActionRow { id: number; timeUtc: string; userName: string | null; action: string; target: string | null; result: string }
interface Dashboard { cards: CardData[]; lastActions: { title: string; items: ActionRow[] } | null }

export const resultKind = (r: string) => (r === 'Success' ? 'success' : r === 'Failure' ? 'error' : r === 'Denied' ? 'warning' : 'neutral') as 'success' | 'error' | 'warning' | 'neutral';

/** Wall-clock time in the application's time zone (Settings > General), HH:MM:SS, ticking every second. */
export function SystemClock({ timeZone }: { timeZone: string }) {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const t = setInterval(() => setNow(new Date()), 1000);
    return () => clearInterval(t);
  }, []);
  const format = (zone: string) => new Intl.DateTimeFormat('en-GB', { timeZone: zone, hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' }).format(now);
  let text: string; let zoneShown = timeZone;
  try { text = format(timeZone); } catch { text = format('UTC'); zoneShown = 'UTC'; } // an unknown zone name must never break the page, nor show a time under the wrong label
  return (
    <div className="system-clock" role="timer" aria-label={`System time, ${zoneShown}`} title={`System time (${zoneShown})`}>
      <span className="clock-time">{text}</span>
      <span className="muted small"> {zoneShown}</span>
    </div>
  );
}

export function HomePage() {
  const { me, can } = useAuth();
  const { dateTime, moduleEnabled, shell } = useShell();
  const allowed = can(Permissions.DashboardRead);
  const data = useAsync(() => (allowed ? get<Dashboard>('/dashboard') : Promise.resolve(undefined)), [allowed]);
  const d = data.data;

  return (
    <PageGuard page="home" requires={[]}>
      <PageHeader title="Home" subtitle={`Welcome, ${me?.displayName}.`} actions={
        <div className="home-actions">
          <button className="btn btn-sm" onClick={data.reload}>Refresh</button>
          <SystemClock timeZone={shell?.timeZone ?? 'UTC'} />
        </div>} />
      {!allowed && <Note>Your role does not include the dashboard. Use the navigation or the search box to get started.</Note>}
      <ErrorNote error={data.error} />
      {allowed && data.loading && !d && <Spinner />}
      {d && (
        <>
          <div className="cards">
            {d.cards.map((c) => (
              <Link key={c.key} to={c.route} className={`stat ${c.tone === 'neutral' ? '' : 'stat-' + c.tone}`}>
                <div className="stat-value">{c.count.toLocaleString()}</div>
                <div className="stat-label">{c.title}</div>
                <div className="muted small">Updated {dateTime(c.updatedUtc)}</div>
              </Link>
            ))}
          </div>
          {can(Permissions.AdUsersRead) && moduleEnabled('ad') && <HourlyActivityCard />}
          {d.lastActions && (
            <Card title={d.lastActions.title}>
              <DataTable rows={d.lastActions.items} rowKey={(a) => String(a.id)} empty="No admin actions have been recorded yet."
                columns={[
                  { key: 'time', header: 'Time', className: 'nowrap', render: (a) => dateTime(a.timeUtc) },
                  { key: 'who', header: 'Who', render: (a) => a.userName ?? '' },
                  { key: 'what', header: 'What', render: (a) => <span className="mono">{a.action}</span> },
                  { key: 'target', header: 'Target', render: (a) => a.target ?? '' },
                  { key: 'result', header: 'Result', render: (a) => <Tag kind={resultKind(a.result)}>{a.result}</Tag> },
                ]} />
            </Card>
          )}
        </>
      )}
    </PageGuard>
  );
}
