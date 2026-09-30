import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { GroupTags, ObjectLink, Ou, Text } from '../../Components/adUi';
import { ChangeDialog } from '../../Components/ChangeDialog';
import { Card, CopyButton, DataTable, Drawer, ErrorNote, KeyValue, Note, Pagination, Spinner, Tag } from '../../Components/ui';
import { useAuth } from '../../Hooks/AuthContext';
import { useAsync, useDebounced } from '../../Hooks/useAsync';
import { downloadFile, get, qs } from '../../Services/api';
import { routeFor, type AdGroup, type GroupMember, type MemberPage } from '../../Services/adTypes';
import { Permissions } from '../../Services/permissions';
import { Actions } from './ActionPanels';


export function GroupDrawer({ id, onClose }: { id: string; onClose: () => void }) {
  const { can } = useAuth();
  const [version, setVersion] = useState(0);
  const navigate = useNavigate();
  const group = useAsync(() => get<AdGroup>(`/modules/ad/groups/${id}`), [id, version]);
  const [input, setInput] = useState('');
  const q = useDebounced(input);
  const [kind, setKind] = useState('');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  const [exportError, setExportError] = useState<unknown>();
  const [picked, setPicked] = useState<Set<string>>(new Set()); // members ticked for removal
  const [removeMode, setRemoveMode] = useState<null | 'confirm' | 'validate'>(null);
  useEffect(() => { setPage(1); setPicked(new Set()); }, [q, kind, id]);

  // The server searches and pages; the browser never holds the whole member list.
  const members = useAsync(
    () => get<MemberPage>(`/modules/ad/groups/${id}/members` + qs({ q, kind, page, pageSize })),
    [id, q, kind, page, pageSize, version]);
  const g = group.data;
  const changed = () => { setPicked(new Set()); setVersion((v) => v + 1); };
  // Membership changes go through the same pipeline as a user's Groups tab, so the same permissions and protections apply.
  const canAdd = !!g && g.isManageable && can(Permissions.AdUsersGroupsAdd);
  const canRemove = !!g && g.isManageable && can(Permissions.AdUsersGroupsRemove);

  return (
    <Drawer open onClose={onClose} wide header={
      group.error ? <ErrorNote error={group.error} /> : !g ? <Spinner /> : (
        <div className="summary">
          <div className="summary-title"><h1>{g.name}</h1><CopyButton value={g.name} label="Copy group name" iconOnly /><GroupTags g={g} /></div>
          <div className="summary-meta">{g.description && <span>{g.description}</span>}<span>{g.memberCount ?? 0} direct members</span></div>
        </div>
      )}>
      {g && (
        <>
          {g.isProtected && <Note kind="warning">This is a protected group. Its membership cannot be changed in this application.</Note>}
          {!g.isProtected && !g.isManageable && <Note>{g.blockReason ?? 'This group is not on the manageable groups list, so its membership cannot be changed here.'}</Note>}
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
                ...(canRemove ? [{
                  key: 'sel', header: '', render: (m: GroupMember) => (
                    <span onClick={(e) => e.stopPropagation()}>
                      <input type="checkbox" aria-label={`Select ${m.name}`} disabled={m.kind !== 'User'}
                        title={m.kind !== 'User' ? 'Only users can be removed here' : undefined} checked={picked.has(m.id)}
                        onChange={(e) => { const n = new Set(picked); e.target.checked ? n.add(m.id) : n.delete(m.id); setPicked(n); }} />
                    </span>
                  ),
                }] : []),
                { key: 'name', header: 'Name', render: (m) => <strong>{m.name}</strong> },
                { key: 'type', header: 'Type', render: (m) => <Tag>{m.kind}</Tag> },
                { key: 'sam', header: 'Username', render: (m) => m.samAccountName ?? '' },
                { key: 'email', header: 'Email', render: (m) => m.email ?? '' },
                { key: 'en', header: 'State', render: (m) => m.enabled == null ? '' : m.enabled ? <Tag kind="success">Enabled</Tag> : <Tag kind="error">Disabled</Tag> },
              ]} />
            {members.data && <Pagination page={page} pageSize={pageSize} total={members.data.total} onPage={setPage} onPageSize={(n) => { setPageSize(n); setPage(1); }} />}
            {canRemove && (
              <Actions onValidate={() => setRemoveMode('validate')} disabled={picked.size === 0}>
                <button className="btn btn-danger" disabled={picked.size === 0} onClick={() => setRemoveMode('confirm')}>
                  Remove {picked.size || ''} selected user{picked.size === 1 ? '' : 's'}
                </button>
              </Actions>
            )}
          </Card>
          {canAdd && <AddUsersCard group={g} version={version} onChanged={changed} />}
          {removeMode && (
            <ChangeDialog title="Remove users from group" policyKey="removeFromGroups" path={`/modules/ad/groups/${id}/members/remove`}
              target={`${picked.size} user${picked.size === 1 ? '' : 's'} from ${g.name}`} typedExpected={g.name} validateOnly={removeMode === 'validate'}
              extraBody={() => ({ userIds: [...picked] })} confirmLabel="Remove" danger
              warning="Removing a user from a group can take away access to files, applications and mailboxes that depend on it."
              onClose={() => setRemoveMode(null)} onFinished={(c) => c && changed()} />
          )}
        </>
      )}
    </Drawer>
  );
}

interface AddableUser { id: string; name: string; samAccountName: string; email: string | null; enabled: boolean; alreadyMember: boolean }

/** Search users and add the ticked ones to this group. The server re-checks every user, the group and the allowlists. */
function AddUsersCard({ group, version, onChanged }: { group: AdGroup; version: number; onChanged: () => void }) {
  const [input, setInput] = useState('');
  const q = useDebounced(input.trim());
  const [chosen, setChosen] = useState<Set<string>>(new Set());
  const [mode, setMode] = useState<null | 'confirm' | 'validate'>(null);
  const found = useAsync(
    () => (q.length >= 2 ? get<AddableUser[]>(`/modules/ad/groups/${group.id}/addable-users` + qs({ q })) : Promise.resolve([] as AddableUser[])),
    [group.id, q, version]);

  return (
    <Card title="Add users to this group">
      <p className="muted small">Search by name, username or email (at least 2 characters), tick the people to add, then review the change.</p>
      <input type="search" placeholder="Search users to add" value={input} onChange={(e) => setInput(e.target.value)} aria-label="Search users to add" style={{ width: '100%', marginBottom: 8 }} />
      <ErrorNote error={found.error} />
      {found.loading && q.length >= 2 && !found.data ? <Spinner /> : (
        <div className="list-check">
          {q.length < 2 && <div className="empty">Type a name to find users.</div>}
          {q.length >= 2 && found.data?.length === 0 && <div className="empty">No users match.</div>}
          {found.data?.map((u) => (
            <label key={u.id} className="check">
              <input type="checkbox" disabled={u.alreadyMember} checked={chosen.has(u.id)}
                onChange={(e) => { const n = new Set(chosen); e.target.checked ? n.add(u.id) : n.delete(u.id); setChosen(n); }} />
              <span><strong>{u.name}</strong> <span className="muted small">{u.samAccountName}{u.email ? ` - ${u.email}` : ''}</span> {u.alreadyMember && <Tag>Already a member</Tag>} {!u.enabled && <Tag kind="error">Disabled</Tag>}</span>
            </label>
          ))}
        </div>
      )}
      <Actions onValidate={() => setMode('validate')} disabled={chosen.size === 0}>
        <button className="btn btn-primary" disabled={chosen.size === 0} onClick={() => setMode('confirm')}>
          Add {chosen.size || ''} selected user{chosen.size === 1 ? '' : 's'}
        </button>
      </Actions>
      {mode && (
        <ChangeDialog title="Add users to group" policyKey="addToGroups" path={`/modules/ad/groups/${group.id}/members/add`}
          target={`${chosen.size} user${chosen.size === 1 ? '' : 's'} to ${group.name}`} typedExpected={group.name} validateOnly={mode === 'validate'}
          extraBody={() => ({ userIds: [...chosen] })} confirmLabel="Add to group"
          onClose={() => setMode(null)} onFinished={(c) => { if (c) { setChosen(new Set()); onChanged(); } }} />
      )}
    </Card>
  );
}
