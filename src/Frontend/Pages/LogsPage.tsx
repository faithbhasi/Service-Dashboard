import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { actionLabel, browserName, ResultTag, type LogRow } from '../Components/logUi';
import { PageGuard } from '../Components/PageGuard';
import { Card, DataTable, Drawer, ErrorNote, KeyValue, Note, NotSet, PageHeader, Pagination, Tabs, type Column } from '../Components/ui';
import { useAuth } from '../Hooks/AuthContext';
import { useShell } from '../Hooks/ShellContext';
import { useAsync, useDebounced } from '../Hooks/useAsync';
import { downloadFile, get, qs } from '../Services/api';
import { startOfTodayIso, zonedTimeToUtcIso } from '../Services/format';
import { Permissions } from '../Services/permissions';
import type { Paged } from '../Services/types';

const PRESETS: [string, string][] = [['', 'Any time'], ['today', 'Today'], ['24h', 'Last 24 hours'], ['7d', 'Last 7 days'], ['30d', 'Last 30 days'], ['custom', 'Custom range']];
const RESULTS = ['', 'Success', 'Failure', 'Denied', 'Validated (no change made)'];
type Tab = 'logons' | 'access' | 'admin' | 'user-activity';

export function LogsPage() {
  const { tab } = useParams();
  const navigate = useNavigate();
  const { can } = useAuth();
  const allLogs = can(Permissions.LogsRead);
  const tabs = [
    { id: 'logons', label: 'Logons' },
    { id: 'access', label: 'Application Access' },
    { id: 'admin', label: 'Admin Actions' },
    ...(allLogs ? [{ id: 'user-activity', label: 'User Activity' }] : []),
  ];
  const active = (tabs.some((t) => t.id === tab) ? tab : tabs[0].id) as Tab;

  return (
    <PageGuard page="logs" requires={[Permissions.LogsRead, Permissions.LogsReadOwn]}>
      <PageHeader title="Activity and Logs" subtitle={allLogs ? 'What people did in this application.' : 'Your own activity in this application.'} />
      <Tabs tabs={tabs} active={active} onChange={(id) => navigate(`/logs/${id}`)} />
      <LogTab key={active} tab={active} />
    </PageGuard>
  );
}

// ---------------------------------------------------------------- filters

function useLogFilters() {
  const { shell } = useShell();
  const tz = shell?.timeZone ?? 'UTC';
  const [params, setParams] = useSearchParams();
  const range = params.get('range') ?? '';
  const get1 = (k: string) => params.get(k) ?? '';
  const [action, setAction] = useState(get1('action'));
  const [target, setTarget] = useState(get1('target'));
  const [ticket, setTicket] = useState(get1('ticket'));
  const dAction = useDebounced(action); const dTarget = useDebounced(target); const dTicket = useDebounced(ticket);

  useEffect(() => {
    setParams((prev) => {
      const n = new URLSearchParams(prev);
      for (const [k, v] of Object.entries({ action: dAction, target: dTarget, ticket: dTicket })) v ? n.set(k, v) : n.delete(k);
      if (n.toString() !== prev.toString()) n.delete('page');
      return n;
    }, { replace: true });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [dAction, dTarget, dTicket]);

  const set = (changes: Record<string, string>) => setParams((prev) => {
    const n = new URLSearchParams(prev);
    for (const [k, v] of Object.entries(changes)) v ? n.set(k, v) : n.delete(k);
    if (!('page' in changes)) n.delete('page');
    return n;
  }, { replace: true });

  // The date range as UTC instants for the API.
  const { from, to } = useMemo(() => {
    const now = Date.now();
    switch (range) {
      case 'today': return { from: startOfTodayIso(tz), to: '' };
      case '24h': return { from: new Date(now - 24 * 3600e3).toISOString(), to: '' };
      case '7d': return { from: new Date(now - 7 * 24 * 3600e3).toISOString(), to: '' };
      case '30d': return { from: new Date(now - 30 * 24 * 3600e3).toISOString(), to: '' };
      case 'custom': return { from: get1('from') ? zonedTimeToUtcIso(get1('from'), tz) : '', to: get1('to') ? zonedTimeToUtcIso(get1('to'), tz) : '' };
      default: return { from: '', to: '' };
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [range, params.get('from'), params.get('to'), tz]);

  const apiParams = { from, to, userId: get1('userId'), action: get1('action'), result: get1('result'), target: get1('target'), ticket: get1('ticket') };
  return { params, range, tz, set, action, setAction, target, setTarget, ticket, setTicket, apiParams, page: Math.max(1, Number(get1('page')) || 1) };
}

type Filters = ReturnType<typeof useLogFilters>;

function FilterBar({ f, users, showUser, showAction = true }: { f: Filters; users?: { id: string; displayName: string }[]; showUser?: boolean; showAction?: boolean }) {
  return (
    <Card>
      <div className="toolbar" style={{ marginBottom: 0 }}>
        <select value={f.range} onChange={(e) => f.set({ range: e.target.value })} aria-label="Date range">
          {PRESETS.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
        </select>
        {f.range === 'custom' && (
          <>
            <input type="datetime-local" aria-label="From" value={f.params.get('from') ?? ''} onChange={(e) => f.set({ from: e.target.value })} />
            <input type="datetime-local" aria-label="To" value={f.params.get('to') ?? ''} onChange={(e) => f.set({ to: e.target.value })} />
            <span className="muted small">in {f.tz}</span>
          </>
        )}
        {showUser && (
          <select value={f.params.get('userId') ?? ''} onChange={(e) => f.set({ userId: e.target.value })} aria-label="User">
            <option value="">Any user</option>
            {users?.map((u) => <option key={u.id} value={u.id}>{u.displayName}</option>)}
          </select>
        )}
        {showAction && <input type="text" placeholder="Action (e.g. ad.user)" value={f.action} onChange={(e) => f.setAction(e.target.value)} aria-label="Action" style={{ minWidth: 160 }} />}
        <select value={f.params.get('result') ?? ''} onChange={(e) => f.set({ result: e.target.value })} aria-label="Result">
          {RESULTS.map((r) => <option key={r} value={r}>{r || 'Any result'}</option>)}
        </select>
        <input type="text" placeholder="Target" value={f.target} onChange={(e) => f.setTarget(e.target.value)} aria-label="Target" style={{ minWidth: 160 }} />
        <input type="text" placeholder="Ticket" value={f.ticket} onChange={(e) => f.setTicket(e.target.value)} aria-label="Ticket number" style={{ minWidth: 120 }} />
      </div>
    </Card>
  );
}

// ---------------------------------------------------------------- one tab

function LogTab({ tab }: { tab: Tab }) {
  const f = useLogFilters();
  const { can } = useAuth();
  const { dateTime } = useShell();
  const allLogs = can(Permissions.LogsRead);
  const [open, setOpen] = useState<number | null>(null);
  const [exportError, setExportError] = useState<unknown>();
  const [chosenUser, setChosenUser] = useState('');

  const users = useAsync(() => (allLogs ? get<{ id: string; displayName: string }[]>('/logs/users') : Promise.resolve([])), [allLogs]);
  const userId = tab === 'user-activity' ? (f.params.get('user') ?? f.apiParams.userId) : f.apiParams.userId;
  const query = { ...f.apiParams, userId };
  const askedSize = Number(f.params.get('pageSize'));
  const pageSize = [25, 50, 100].includes(askedSize) ? askedSize : 25;
  const list = useAsync(() => get<Paged<LogRow>>(`/logs/${tab}` + qs({ ...query, page: f.page, pageSize })),
    [tab, JSON.stringify(query), f.page, pageSize]);

  const columns = useMemo((): Column<LogRow>[] => {
    const time: Column<LogRow> = { key: 'time', header: 'Time', className: 'nowrap', render: (r) => dateTime(r.timeUtc) };
    const user: Column<LogRow> = { key: 'user', header: 'User', render: (r) => r.userName ?? <NotSet /> };
    const result: Column<LogRow> = { key: 'result', header: 'Result', render: (r) => <ResultTag result={r.result} /> };
    if (tab === 'logons') return [time, user, { key: 'ev', header: 'Event', render: (r) => actionLabel(r.action) }, result,
      { key: 'why', header: 'Reason', render: (r) => r.error ?? '' }, { key: 'ip', header: 'IP address', render: (r) => r.ipAddress ?? '' },
      { key: 'ua', header: 'Browser', render: (r) => browserName(r.userAgent) }];
    if (tab === 'access') return [time, user, { key: 'mod', header: 'Module', render: (r) => r.module ?? '' },
      { key: 'page', header: 'Page or endpoint', render: (r) => <span className="mono">{r.target}</span> }, result];
    if (tab === 'admin') return [time, user, { key: 'act', header: 'Action', render: (r) => actionLabel(r.action) },
      { key: 'target', header: 'Target', render: (r) => r.target ?? '' }, result, { key: 'ticket', header: 'Ticket', render: (r) => r.ticketNumber ?? '' }];
    return [time, { key: 'cat', header: 'Area', render: (r) => r.category }, { key: 'act', header: 'Action', render: (r) => actionLabel(r.action) },
      { key: 'target', header: 'Target', render: (r) => r.target ?? '' }, result];
  }, [tab, dateTime]);

  const exportUrl = `/logs/${tab}/export` + qs(query);

  return (
    <>
      {tab === 'logons' && <Note>This shows sign-ins to this application only. It is not the full Okta System Log.</Note>}
      {tab === 'user-activity' && (
        <UserActivityPicker chosen={userId} onChoose={(id) => { setChosenUser(id); f.set({ user: id, userId: '' }); }} users={users.data ?? []} chosenName={users.data?.find((u) => u.id === (chosenUser || userId))?.displayName} />
      )}
      <FilterBar f={f} showUser={tab !== 'user-activity' && allLogs} users={users.data} />
      <div className="toolbar">
        <span className="muted">{list.data ? `${list.data.total.toLocaleString()} record${list.data.total === 1 ? '' : 's'}` : ''}</span>
        <span className="grow" />
        {can(Permissions.LogsExport) && (
          <button className="btn" onClick={() => { setExportError(undefined); downloadFile(exportUrl).catch(setExportError); }}>Export CSV</button>
        )}
      </div>
      <ErrorNote error={list.error ?? exportError} />
      {tab === 'user-activity' && !userId && allLogs && <Note>Choose a person above to see their full timeline.</Note>}
      {(tab !== 'user-activity' || userId || !allLogs) && (
        <>
          <DataTable rows={list.data?.items} loading={list.loading} rowKey={(r) => String(r.id)} columns={columns} onRowClick={(r) => setOpen(r.id)} empty="No records match these filters." />
          {list.data && <Pagination page={f.page} pageSize={pageSize} total={list.data.total} onPage={(p) => f.set({ page: p <= 1 ? '' : String(p) })}
            onPageSize={(n) => f.set({ pageSize: n === 25 ? '' : String(n) })} />}
        </>
      )}
      {open !== null && <LogDetail id={open} onClose={() => setOpen(null)} />}
    </>
  );
}

function UserActivityPicker({ users, chosen, chosenName, onChoose }: {
  users: { id: string; displayName: string }[]; chosen: string; chosenName?: string; onChoose: (id: string) => void;
}) {
  const { dateTime } = useShell();
  const [module, setModule] = useState('');
  const [action, setAction] = useState('');
  const [range, setRange] = useState('7d');
  const from = useMemo(() => new Date(Date.now() - ({ '24h': 1, '7d': 7, '30d': 30, '365d': 365 } as Record<string, number>)[range] * 24 * 3600e3).toISOString(), [range]);
  const who = useAsync(() => (module || action ? get<{ userId: string; userName: string; count: number; lastUtc: string }[]>('/logs/users-by' + qs({ module, action, from })) : Promise.resolve([])), [module, action, from]);

  return (
    <>
      <Card title="Person">
        <div className="toolbar" style={{ marginBottom: 0 }}>
          <select value={chosen} onChange={(e) => onChoose(e.target.value)} aria-label="Pick a person">
            <option value="">Choose a person...</option>
            {users.map((u) => <option key={u.id} value={u.id}>{u.displayName}</option>)}
          </select>
          {chosen && <strong>{chosenName}</strong>}
        </div>
      </Card>
      <Card title="Who accessed a module or did an action?">
        <div className="toolbar">
          <select value={module} onChange={(e) => setModule(e.target.value)} aria-label="Module">
            <option value="">Any module</option><option value="core">Core</option><option value="ad">Active Directory</option>
          </select>
          <input type="text" placeholder="Action (e.g. ad.user.resetPassword)" value={action} onChange={(e) => setAction(e.target.value)} aria-label="Action to look for" style={{ minWidth: 260 }} />
          <select value={range} onChange={(e) => setRange(e.target.value)} aria-label="Range">
            <option value="24h">Last 24 hours</option><option value="7d">Last 7 days</option><option value="30d">Last 30 days</option><option value="365d">Last year</option>
          </select>
        </div>
        {(module || action) && (
          <DataTable rows={who.data} loading={who.loading} rowKey={(r) => r.userId} onRowClick={(r) => onChoose(r.userId)} empty="Nobody matched."
            columns={[
              { key: 'u', header: 'User', render: (r) => <strong>{r.userName}</strong> },
              { key: 'n', header: 'Times', className: 'right', render: (r) => r.count },
              { key: 'l', header: 'Most recent', render: (r) => dateTime(r.lastUtc) },
            ]} />
        )}
      </Card>
    </>
  );
}

export function LogDetail({ id, onClose }: { id: number; onClose: () => void }) {
  const { dateTime } = useShell();
  const detail = useAsync(() => get<LogRow>(`/logs/entry/${id}`), [id]);
  const r = detail.data;
  return (
    <Drawer open onClose={onClose} header={<div className="summary"><h1>{r ? actionLabel(r.action) : 'Record'}</h1>{r && <div className="summary-meta"><ResultTag result={r.result} /><span>{dateTime(r.timeUtc)}</span></div>}</div>}>
      <ErrorNote error={detail.error} />
      {r && (
        <KeyValue items={[
          { label: 'Time', value: dateTime(r.timeUtc) },
          { label: 'Area', value: r.category },
          { label: 'User', value: r.userName ?? <NotSet /> },
          { label: 'Action', value: <span className="mono">{r.action}</span> },
          { label: 'Module', value: r.module ?? <NotSet /> },
          { label: 'Target', value: r.target ?? <NotSet /> },
          { label: 'Target ID', value: r.targetId ? <span className="mono">{r.targetId}</span> : <NotSet /> },
          { label: 'Previous value', value: r.previousValue ? <span className="mono" style={{ whiteSpace: 'pre-wrap' }}>{r.previousValue}</span> : <NotSet /> },
          { label: 'New value', value: r.newValue ? <span className="mono" style={{ whiteSpace: 'pre-wrap' }}>{r.newValue}</span> : <NotSet /> },
          { label: 'Result', value: <ResultTag result={r.result} /> },
          { label: 'Error', value: r.error ?? <NotSet /> },
          { label: 'Justification', value: r.justification ?? <NotSet /> },
          { label: 'Ticket number', value: r.ticketNumber ?? <NotSet /> },
          { label: 'IP address', value: r.ipAddress ?? <NotSet /> },
          { label: 'Browser', value: r.userAgent ?? <NotSet /> },
          { label: 'Correlation ID', value: r.correlationId ? <span className="mono">{r.correlationId}</span> : <NotSet /> },
        ]} />
      )}
      {r && <p className="muted small">Stored as UTC: {r.timeUtc}</p>}
    </Drawer>
  );
}
