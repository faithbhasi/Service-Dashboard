import { useState } from 'react';
import { actionLabel, ResultTag, type LogRow } from '../../Components/logUi';
import { DataTable, ErrorNote, Note, Pagination } from '../../Components/ui';
import { useShell } from '../../Hooks/ShellContext';
import { useAsync } from '../../Hooks/useAsync';
import { get, qs } from '../../Services/api';
import type { Paged } from '../../Services/types';
import { LogDetail } from '../LogsPage';

const PAGE_SIZE = 20;

/** Everything done to this object through this application (Activity History tab of a drawer). */
export function ActivityHistory({ path, reloadKey }: { path: string; reloadKey?: number }) {
  const { dateTime } = useShell();
  const [page, setPage] = useState(1);
  const [open, setOpen] = useState<number | null>(null);
  const data = useAsync(() => get<Paged<LogRow>>(path + qs({ page, pageSize: PAGE_SIZE })), [path, page, reloadKey]);
  return (
    <>
      <Note>This shows changes made through this application only. Changes made with other tools are not shown.</Note>
      <ErrorNote error={data.error} />
      <DataTable rows={data.data?.items} loading={data.loading} rowKey={(r) => String(r.id)} onRowClick={(r) => setOpen(r.id)} empty="No activity has been recorded for this object."
        columns={[
          { key: 't', header: 'Time', className: 'nowrap', render: (r) => dateTime(r.timeUtc) },
          { key: 'u', header: 'By', render: (r) => r.userName ?? '' },
          { key: 'a', header: 'Action', render: (r) => actionLabel(r.action) + (r.target?.includes(' / group ') ? ` (${r.target.split(' / group ')[1]})` : '') },
          { key: 'r', header: 'Result', render: (r) => <ResultTag result={r.result} /> },
          { key: 'j', header: 'Justification', render: (r) => r.justification ?? '' },
        ]} />
      {data.data && <Pagination page={page} pageSize={PAGE_SIZE} total={data.data.total} onPage={setPage} />}
      {open !== null && <LogDetail id={open} onClose={() => setOpen(null)} />}
    </>
  );
}
