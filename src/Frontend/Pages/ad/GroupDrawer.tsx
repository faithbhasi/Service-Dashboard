import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { GroupTags, ObjectLink, Ou, Text } from '../../Components/adUi';
import { Card, DataTable, Drawer, ErrorNote, KeyValue, Note, Pagination, Spinner, Tag } from '../../Components/ui';
import { useAuth } from '../../Hooks/AuthContext';
import { useAsync, useDebounced } from '../../Hooks/useAsync';
import { downloadFile, get, qs } from '../../Services/api';
import { routeFor, type AdGroup, type GroupMember, type MemberPage } from '../../Services/adTypes';
import { Permissions } from '../../Services/permissions';

const PAGE_SIZE = 25;

export function GroupDrawer({ id, onClose }: { id: string; onClose: () => void }) {
  const { can } = useAuth();
  const navigate = useNavigate();
  const group = useAsync(() => get<AdGroup>(`/modules/ad/groups/${id}`), [id]);
  const [input, setInput] = useState('');
  const q = useDebounced(input);
  const [kind, setKind] = useState('');
  const [page, setPage] = useState(1);
  const [exportError, setExportError] = useState<unknown>();
  useEffect(() => setPage(1), [q, kind, id]);

  // The server searches and pages; the browser never holds the whole member list.
  const members = useAsync(
    () => get<MemberPage>(`/modules/ad/groups/${id}/members` + qs({ q, kind, page, pageSize: PAGE_SIZE })),
    [id, q, kind, page]);
  const g = group.data;

  return (
    <Drawer open onClose={onClose} wide header={
      group.error ? <ErrorNote error={group.error} /> : !g ? <Spinner /> : (
        <div className="summary">
          <div className="summary-title"><h1>{g.name}</h1><GroupTags g={g} /></div>
          <div className="summary-meta">{g.description && <span>{g.description}</span>}<span>{g.memberCount ?? 0} direct members</span></div>
        </div>
      )}>
      {g && (
        <>
          {g.isProtected && <Note kind="warning">This is a protected group. Its membership cannot be changed in this application.</Note>}
          <Card title="Details">
            <KeyValue items={[
              { label: 'Name', value: g.name },
              { label: 'Description', value: <Text value={g.description} /> },
              { label: 'Scope', value: g.scope },
              { label: 'Type', value: g.type },
              { label: 'OU', value: <Ou dn={g.ou} /> },
              { label: 'Managed by', value: <ObjectLink value={g.managedBy} /> },
              { label: 'Direct members', value: g.memberCount ?? 0 },
              { label: 'Protected', value: g.isProtected ? <Tag kind="warning">Protected</Tag> : 'No' },
            ]} />
          </Card>
          <Card title="Members" actions={can(Permissions.AdGroupsMemberExport) && (
            <button className="btn btn-sm" onClick={() => downloadFile(`/modules/ad/groups/${id}/members/export` + qs({ q, kind })).catch(setExportError)}>Export CSV</button>
          )}>
            <div className="toolbar">
              <input type="search" placeholder="Search members by name, username or email" value={input}
                onChange={(e) => setInput(e.target.value)} aria-label="Search members" />
              <select value={kind} onChange={(e) => setKind(e.target.value)} aria-label="Member type">
                <option value="">All</option><option value="User">Users</option><option value="Computer">Computers</option><option value="Group">Groups</option>
              </select>
            </div>
            <ErrorNote error={members.error ?? exportError} />
            <DataTable<GroupMember> rows={members.data?.items} loading={members.loading} rowKey={(m) => m.id}
              onRowClick={(m) => navigate(routeFor(m.kind, m.id))} empty="No members match."
              columns={[
                { key: 'name', header: 'Name', render: (m) => <strong>{m.name}</strong> },
                { key: 'type', header: 'Type', render: (m) => <Tag>{m.kind}</Tag> },
                { key: 'sam', header: 'Username', render: (m) => m.samAccountName ?? '' },
                { key: 'email', header: 'Email', render: (m) => m.email ?? '' },
                { key: 'en', header: 'State', render: (m) => m.enabled == null ? '' : m.enabled ? <Tag kind="success">Enabled</Tag> : <Tag kind="error">Disabled</Tag> },
              ]} />
            {members.data && <Pagination page={page} pageSize={PAGE_SIZE} total={members.data.total} onPage={setPage} />}
          </Card>
        </>
      )}
    </Drawer>
  );
}
