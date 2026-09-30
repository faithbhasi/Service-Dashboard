import { GroupTags, Ou, Text } from '../../Components/adUi';
import { PageGuard } from '../../Components/PageGuard';
import { DataTable, ErrorNote, PageHeader, Pagination } from '../../Components/ui';
import { useAsync } from '../../Hooks/useAsync';
import { useDrawerRoute } from '../../Hooks/useDrawerRoute';
import { useListParams } from '../../Hooks/useListParams';
import { get, qs } from '../../Services/api';
import type { GroupPage } from '../../Services/adTypes';
import { Permissions } from '../../Services/permissions';
import { GroupDrawer } from './GroupDrawer';


export function GroupsPage() {
  const list = useListParams();
  const drawer = useDrawerRoute('/ad/groups', 'members');
  const groups = useAsync(() => get<GroupPage>('/modules/ad/groups' + qs({ q: list.q, page: list.page, pageSize: list.pageSize })), [list.q, list.page, list.pageSize]);

  return (
    <PageGuard page="ad.groups" requires={[Permissions.AdGroupsRead]}>
      <PageHeader title="Active Directory groups" subtitle="Open a group to see its members, and to add or remove users if your role allows it." />
      <div className="toolbar">
        <input type="search" placeholder="Search group name or description" value={list.input}
          onChange={(e) => list.setInput(e.target.value)} aria-label="Search groups" />
      </div>
      <ErrorNote error={groups.error} />
      <DataTable rows={groups.data?.items} loading={groups.loading} rowKey={(g) => g.id} onRowClick={(g) => drawer.open(g.id)}
        empty="No groups match your search."
        columns={[
          { key: 'name', header: 'Name', render: (g) => <strong>{g.name}</strong> },
          { key: 'desc', header: 'Description', render: (g) => <Text value={g.description} /> },
          { key: 'tags', header: 'Scope and type', render: (g) => <GroupTags g={g} /> },
          { key: 'ou', header: 'OU', render: (g) => <Ou dn={g.ou} /> },
        ]} />
      {groups.data && <Pagination page={list.page} pageSize={list.pageSize} onPageSize={list.setPageSize} total={groups.data.total} capped={groups.data.totalIsCapped} onPage={list.setPage} />}
      {drawer.id && <GroupDrawer id={drawer.id} onClose={drawer.close} />}
    </PageGuard>
  );
}
