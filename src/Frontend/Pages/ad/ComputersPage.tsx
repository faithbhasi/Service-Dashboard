import { useMemo } from 'react';
import { ColumnPicker } from '../../Components/ColumnPicker';
import { EnabledTag, Ou, Text, useAdText } from '../../Components/adUi';
import { PageGuard } from '../../Components/PageGuard';
import { DataTable, ErrorNote, PageHeader, Pagination, type Column } from '../../Components/ui';
import { useAsync } from '../../Hooks/useAsync';
import { useDrawerRoute } from '../../Hooks/useDrawerRoute';
import { useListParams } from '../../Hooks/useListParams';
import { useTableColumns } from '../../Hooks/useTableColumns';
import { get, qs } from '../../Services/api';
import type { AdComputer, ComputerPage } from '../../Services/adTypes';
import { Permissions } from '../../Services/permissions';
import { ComputerDrawer } from './ComputerDrawer';

const FILTERS: [string, string][] = [['All', 'All computers'], ['Enabled', 'Enabled'], ['Disabled', 'Disabled']];
const OS_TYPES: [string, string][] = [['', 'All operating systems'], ['windows11', 'Windows 11'], ['windows10', 'Windows 10'], ['windowsserver', 'Windows Server'], ['macos', 'macOS'], ['linux', 'Linux']];

export function ComputersPage() {
  const list = useListParams();
  const drawer = useDrawerRoute('/ad/computers', 'details');
  const t = useAdText();

  const columns: Column<AdComputer>[] = useMemo(() => [
    { key: 'name', header: 'Name', render: (c) => <strong>{c.name}</strong> },
    { key: 'dns', header: 'DNS host name', render: (c) => <Text value={c.dnsHostName} /> },
    { key: 'enabled', header: 'Enabled', render: (c) => <EnabledTag enabled={c.enabled} /> },
    { key: 'os', header: 'Operating system', render: (c) => <Text value={c.operatingSystem} /> },
    { key: 'ver', header: 'OS version', render: (c) => <Text value={c.osVersion} /> },
    { key: 'lastLogon', header: 'Last logon (approximate)', render: (c) => t.approx(c.lastLogonUtc) },
    { key: 'ou', header: 'OU', render: (c) => <Ou dn={c.ou} /> },
  // eslint-disable-next-line react-hooks/exhaustive-deps
  ], [t.approx]);
  const { visible, toggle } = useTableColumns('cols:ad-computers', columns.map((c) => c.key), columns.map((c) => c.key));

  const computers = useAsync(
    () => get<ComputerPage>('/modules/ad/computers' + qs({ q: list.q, filter: list.filter, os: list.param('os'), page: list.page, pageSize: list.pageSize })),
    [list.q, list.filter, list.param('os'), list.page, list.pageSize]);

  return (
    <PageGuard page="ad.computers" requires={[Permissions.AdComputersRead]}>
      <PageHeader title="Active Directory computers" subtitle="Search and view computer accounts." />
      <div className="toolbar">
        <input type="search" placeholder="Search computer name or DNS host name" value={list.input}
          onChange={(e) => list.setInput(e.target.value)} aria-label="Search computers" />
        <select value={list.filter} onChange={(e) => list.setFilter(e.target.value)} aria-label="Filter computers">
          {FILTERS.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
        </select>
        <select value={list.param('os')} onChange={(e) => list.setParam('os', e.target.value)} aria-label="Filter by operating system">
          {OS_TYPES.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
        </select>
        <span className="grow" />
        <ColumnPicker columns={columns} visible={visible} onToggle={toggle} />
      </div>
      <ErrorNote error={computers.error} />
      <DataTable rows={computers.data?.items} loading={computers.loading} rowKey={(c) => c.id}
        columns={columns.filter((c) => visible.includes(c.key))} onRowClick={(c) => drawer.open(c.id)} empty="No computers match your search." />
      {computers.data && (
        <Pagination page={list.page} pageSize={list.pageSize} onPageSize={list.setPageSize} total={computers.data.total} capped={computers.data.totalIsCapped} onPage={list.setPage} />
      )}
      {drawer.id && <ComputerDrawer id={drawer.id} tab={drawer.tab} onTab={drawer.setTab} onClose={drawer.close} onChanged={computers.reload} />}
    </PageGuard>
  );
}
