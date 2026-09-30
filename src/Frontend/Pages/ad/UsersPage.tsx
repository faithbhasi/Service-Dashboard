import { useMemo } from 'react';
import { ColumnPicker } from '../../Components/ColumnPicker';
import { EnabledTag, LockedTag, Ou, Text, useAdText } from '../../Components/adUi';
import { PageGuard } from '../../Components/PageGuard';
import { DataTable, ErrorNote, PageHeader, Pagination, type Column } from '../../Components/ui';
import { useAsync } from '../../Hooks/useAsync';
import { useDrawerRoute } from '../../Hooks/useDrawerRoute';
import { useListParams } from '../../Hooks/useListParams';
import { useTableColumns } from '../../Hooks/useTableColumns';
import { get, qs } from '../../Services/api';
import type { AdUser, UserPage } from '../../Services/adTypes';
import { Permissions } from '../../Services/permissions';
import { UserDrawer } from './UserDrawer';

const FILTERS: [string, string][] = [['All', 'All users'], ['Enabled', 'Enabled'], ['Disabled', 'Disabled'], ['Locked', 'Locked'], ['AccountExpired', 'Expired accounts']];
const PAGE_SIZE = 25;

export function UsersPage() {
  const list = useListParams();
  const drawer = useDrawerRoute('/ad/users', 'groups');
  const t = useAdText();

  const columns: Column<AdUser>[] = useMemo(() => [
    { key: 'displayName', header: 'Display name', render: (u) => <strong>{u.displayName ?? u.samAccountName}</strong> },
    { key: 'username', header: 'Username', render: (u) => u.samAccountName },
    { key: 'upn', header: 'UPN', render: (u) => <Text value={u.userPrincipalName} /> },
    { key: 'email', header: 'Email', render: (u) => <Text value={u.email} /> },
    { key: 'title', header: 'Title', render: (u) => <Text value={u.title} /> },
    { key: 'department', header: 'Department', render: (u) => <Text value={u.department} /> },
    { key: 'enabled', header: 'Enabled', render: (u) => <EnabledTag enabled={u.enabled} /> },
    { key: 'locked', header: 'Locked', render: (u) => <LockedTag locked={u.lockedOut} /> },
    { key: 'accountExpiry', header: 'Account expiry', render: (u) => t.accountExpiryShort(u) },
    { key: 'passwordExpiry', header: 'Password expiry', render: (u) => t.passwordShort(u) },
    { key: 'lastLogon', header: 'Last logon (approximate)', render: (u) => t.approx(u.lastLogonUtc) },
    { key: 'ou', header: 'OU', render: (u) => <Ou dn={u.ou} /> },
  // eslint-disable-next-line react-hooks/exhaustive-deps
  ], [t.accountExpiryShort, t.passwordShort]);

  const { visible, toggle } = useTableColumns('cols:ad-users', columns.map((c) => c.key),
    ['displayName', 'username', 'email', 'department', 'enabled', 'locked', 'lastLogon', 'ou']);

  const users = useAsync(
    () => get<UserPage>('/modules/ad/users' + qs({ q: list.q, filter: list.filter, page: list.page, pageSize: PAGE_SIZE })),
    [list.q, list.filter, list.page]);

  return (
    <PageGuard page="ad.users" requires={[Permissions.AdUsersRead]}>
      <PageHeader title="Active Directory users" subtitle="Search, view and manage user accounts." />
      <div className="toolbar">
        <input type="search" placeholder="Search name, username, UPN, email or employee ID" value={list.input}
          onChange={(e) => list.setInput(e.target.value)} aria-label="Search users" />
        <select value={list.filter} onChange={(e) => list.setFilter(e.target.value)} aria-label="Filter users">
          {FILTERS.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
        </select>
        <span className="grow" />
        <ColumnPicker columns={columns} visible={visible} onToggle={toggle} />
      </div>
      <ErrorNote error={users.error} />
      <DataTable rows={users.data?.items} loading={users.loading} rowKey={(u) => u.id}
        columns={columns.filter((c) => visible.includes(c.key))} onRowClick={(u) => drawer.open(u.id)}
        empty="No users match your search." />
      {users.data && (
        <Pagination page={list.page} pageSize={PAGE_SIZE} total={users.data.total} capped={users.data.totalIsCapped} onPage={list.setPage} />
      )}
      {drawer.id && <UserDrawer id={drawer.id} tab={drawer.tab} onTab={drawer.setTab} onClose={drawer.close} onChanged={users.reload} />}
    </PageGuard>
  );
}
