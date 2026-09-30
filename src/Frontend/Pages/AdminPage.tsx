import { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { ConfirmModal } from '../Components/ConfirmModal';
import { isLimited, noScope, RoleScopeEditor, type AdScope, type ScopeOptions } from '../Components/RoleScopeEditor';
import { PageGuard } from '../Components/PageGuard';
import { Card, DataTable, Drawer, ErrorNote, Field, Modal, Note, PageHeader, Pagination, Spinner, Tabs, Tag } from '../Components/ui';
import { useAuth } from '../Hooks/AuthContext';
import { useShell } from '../Hooks/ShellContext';
import { useAsync, useDebounced } from '../Hooks/useAsync';
import { del, downloadFile, get, post, put, qs } from '../Services/api';
import { Permissions } from '../Services/permissions';
import type { Paged, RoleRef } from '../Services/types';

interface AppUser {
  id: string; displayName: string; email: string; isEnabled: boolean; createdUtc: string; lastSignInUtc: string | null;
  roles: RoleRef[]; permissions: string[]; oktaGroups: string[];
}
interface RoleDto {
  id: string; name: string; description: string | null; isSystem: boolean; isLocked: boolean;
  permissions: string[]; userCount: number; mappingCount: number; adScope?: AdScope;
}
interface PermissionDto { id: string; group: string; description: string; roles: string[] }
interface Mapping { id: string; oktaGroup: string; roleId: string; roleName: string }

export function AdminPage() {
  const { tab } = useParams();
  const navigate = useNavigate();
  const { can } = useAuth();
  const canUsers = can(Permissions.AdminUsersManage);

  const tabs = useMemo(() => [
    ...(canUsers ? [{ id: 'users', label: 'App Users' }, { id: 'mappings', label: 'Group Mappings' }] : []),
    { id: 'roles', label: 'Roles' },
    { id: 'permissions', label: 'Permissions' },
  ], [canUsers]);
  const active = tabs.some((t) => t.id === tab) ? tab! : tabs[0].id;

  return (
    <PageGuard page="admin" requires={[Permissions.AdminUsersManage, Permissions.AdminRolesManage]}>
      <PageHeader title="Users and Groups" subtitle="Who can use this application and what they can do. This does not change Active Directory or Okta." />
      <Tabs tabs={tabs} active={active} onChange={(id) => navigate(`/admin/${id}`)} />
      {active === 'users' && <AppUsersTab />}
      {active === 'mappings' && <MappingsTab />}
      {active === 'roles' && <RolesTab />}
      {active === 'permissions' && <PermissionsTab />}
    </PageGuard>
  );
}

// ---------------------------------------------------------------- App users

function AppUsersTab() {
  const { dateTime } = useShell();
  const { can } = useAuth();
  const [q, setQ] = useState('');
  const dq = useDebounced(q);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  const [selected, setSelected] = useState<AppUser>();
  const [exportError, setExportError] = useState<unknown>();
  useEffect(() => setPage(1), [dq]);
  const users = useAsync(() => get<Paged<AppUser>>('/admin/users' + qs({ q: dq, page, pageSize })), [dq, page, pageSize]);

  return (
    <>
      <div className="toolbar">
        <input type="search" placeholder="Search name or email" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search app users" />
        <span className="grow" />
        <button className="btn" onClick={() => downloadFile('/admin/users/export' + qs({ q: dq })).catch(setExportError)}>Export CSV</button>
      </div>
      <ErrorNote error={users.error ?? exportError} />
      <DataTable rows={users.data?.items} loading={users.loading} rowKey={(u) => u.id} onRowClick={setSelected}
        columns={[
          { key: 'name', header: 'Name', render: (u) => <strong>{u.displayName}</strong> },
          { key: 'email', header: 'Email', render: (u) => u.email },
          { key: 'status', header: 'App access', render: (u) => <Tag kind={u.isEnabled ? 'success' : 'error'}>{u.isEnabled ? 'Enabled' : 'Disabled'}</Tag> },
          { key: 'last', header: 'Last sign-in', render: (u) => dateTime(u.lastSignInUtc) || <span className="muted">Never</span> },
          { key: 'roles', header: 'Roles', render: (u) => u.roles.length ? u.roles.map((r) => <Tag key={r.id} kind="info">{r.name}</Tag>) : <span className="muted">None</span> },
          { key: 'perms', header: 'Permissions', className: 'right', render: (u) => u.permissions.length },
        ]} />
      {users.data && <Pagination page={page} pageSize={pageSize} total={users.data.total} onPage={setPage} onPageSize={(n) => { setPageSize(n); setPage(1); }} />}
      {selected && (
        <UserDrawer user={selected} canEdit={can(Permissions.AdminUsersManage)} onClose={() => setSelected(undefined)}
          onChanged={() => { setSelected(undefined); users.reload(); }} />
      )}
    </>
  );
}

function UserDrawer({ user, canEdit, onClose, onChanged }: { user: AppUser; canEdit: boolean; onClose: () => void; onChanged: () => void }) {
  const { can, me } = useAuth();
  const { dateTime } = useShell();
  const roles = useAsync(() => get<RoleDto[]>('/admin/roles'), []);
  const [chosen, setChosen] = useState<Set<string>>(new Set(user.roles.filter((r) => r.source === 'Assigned in this app').map((r) => r.id)));
  const [error, setError] = useState<unknown>();
  const [busy, setBusy] = useState(false);
  const [confirmToggle, setConfirmToggle] = useState(false);
  const isSelf = me?.id === user.id;

  const saveRoles = async () => {
    setBusy(true); setError(undefined);
    try { await put(`/admin/users/${user.id}/roles`, { roleIds: [...chosen] }); onChanged(); } catch (e) { setError(e); setBusy(false); }
  };

  return (
    <Drawer open onClose={onClose} header={
      <div className="summary">
        <div className="summary-title"><h1>{user.displayName}</h1><Tag kind={user.isEnabled ? 'success' : 'error'}>{user.isEnabled ? 'Enabled' : 'Disabled'}</Tag></div>
        <div className="summary-meta"><span>{user.email}</span><span>Last sign-in: {dateTime(user.lastSignInUtc) || 'Never'}</span></div>
      </div>}>
      {isSelf && <Note kind="info">This is you. You cannot change your own roles or access.</Note>}
      <h3>Roles assigned in this app</h3>
      {roles.loading ? <Spinner /> : (
        <div className="list-check">
          {roles.data?.map((r) => (
            <label key={r.id} className="check">
              <input type="checkbox" disabled={!canEdit || isSelf} checked={chosen.has(r.id)}
                onChange={(e) => { const n = new Set(chosen); e.target.checked ? n.add(r.id) : n.delete(r.id); setChosen(n); }} />
              <span><strong>{r.name}</strong> <span className="muted small">{r.description}</span></span>
            </label>
          ))}
        </div>
      )}
      {user.roles.filter((r) => r.source !== 'Assigned in this app').length > 0 && (
        <p className="muted small">Also from Okta group mappings: {user.roles.filter((r) => r.source !== 'Assigned in this app').map((r) => `${r.name} (${r.source})`).join(', ')}</p>
      )}
      <ErrorNote error={error} />
      {canEdit && !isSelf && (
        <div className="form-actions">
          <button className="btn btn-primary" onClick={() => void saveRoles()} disabled={busy}>Save roles</button>
          <button className={`btn ${user.isEnabled ? 'btn-danger' : ''}`} onClick={() => setConfirmToggle(true)} disabled={busy}>
            {user.isEnabled ? 'Disable app access' : 'Enable app access'}
          </button>
        </div>
      )}
      <h3>Okta groups at last sign-in</h3>
      <p>{user.oktaGroups.length ? user.oktaGroups.map((g) => <Tag key={g}>{g}</Tag>) : <span className="muted">None</span>}</p>
      <h3>Effective permissions ({user.permissions.length})</h3>
      <p className="mono" style={{ lineHeight: 1.8 }}>{user.permissions.join(', ') || 'None'}</p>
      {can(Permissions.LogsRead) && <p><Link to={`/logs/user-activity?user=${user.id}`}>View this user's activity</Link></p>}
      {confirmToggle && (
        <ConfirmModal title={user.isEnabled ? 'Disable app access' : 'Enable app access'} danger={user.isEnabled}
          confirmLabel={user.isEnabled ? 'Disable' : 'Enable'}
          onClose={() => setConfirmToggle(false)}
          onConfirm={async () => { await put(`/admin/users/${user.id}/status`, { isEnabled: !user.isEnabled }); onChanged(); }}>
          {user.isEnabled
            ? <p>{user.displayName} will be signed out and denied access to this application. Their Active Directory account is not affected.</p>
            : <p>{user.displayName} will be able to sign in again.</p>}
        </ConfirmModal>
      )}
    </Drawer>
  );
}

// ---------------------------------------------------------------- group mappings

function MappingsTab() {
  const mappings = useAsync(() => get<Mapping[]>('/admin/group-mappings'), []);
  const roles = useAsync(() => get<RoleDto[]>('/admin/roles'), []);
  const [group, setGroup] = useState('');
  const [roleId, setRoleId] = useState('');
  const [error, setError] = useState<unknown>();
  const [removing, setRemoving] = useState<Mapping>();

  const add = async () => {
    setError(undefined);
    try { await post('/admin/group-mappings', { oktaGroup: group, roleId }); setGroup(''); mappings.reload(); } catch (e) { setError(e); }
  };

  return (
    <>
      <Note>Members of an Okta group get the mapped role at their next sign-in. The group names come from the Okta groups claim.</Note>
      <Card title="Add mapping">
        <div className="toolbar">
          <input type="text" placeholder="Okta group name" value={group} onChange={(e) => setGroup(e.target.value)} aria-label="Okta group name" />
          <select value={roleId} onChange={(e) => setRoleId(e.target.value)} aria-label="Role">
            <option value="">Choose a role</option>
            {roles.data?.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
          </select>
          <button className="btn btn-primary" disabled={!group.trim() || !roleId} onClick={() => void add()}>Add mapping</button>
        </div>
        <ErrorNote error={error} />
      </Card>
      <ErrorNote error={mappings.error} />
      <DataTable rows={mappings.data} loading={mappings.loading} rowKey={(m) => m.id} empty="No Okta groups are mapped yet."
        columns={[
          { key: 'g', header: 'Okta group', render: (m) => <strong>{m.oktaGroup}</strong> },
          { key: 'r', header: 'Role', render: (m) => <Tag kind="info">{m.roleName}</Tag> },
          { key: 'x', header: '', className: 'right', render: (m) => <button className="btn btn-sm" onClick={() => setRemoving(m)}>Remove</button> },
        ]} />
      {removing && (
        <ConfirmModal title="Remove mapping" danger confirmLabel="Remove" onClose={() => setRemoving(undefined)}
          onConfirm={async () => { await del(`/admin/group-mappings/${removing.id}`); mappings.reload(); }}>
          <p>Remove the mapping <strong>{removing.oktaGroup}</strong> to <strong>{removing.roleName}</strong>? Members lose this role at their next request.</p>
        </ConfirmModal>
      )}
    </>
  );
}

// ---------------------------------------------------------------- roles

function RolesTab() {
  const { can } = useAuth();
  const canEdit = can(Permissions.AdminRolesManage);
  const roles = useAsync(() => get<RoleDto[]>('/admin/roles'), []);
  const perms = useAsync(() => get<PermissionDto[]>('/admin/permissions'), []);
  const [editing, setEditing] = useState<{ role?: RoleDto; mode: 'view' | 'edit' | 'new' | 'clone' }>();
  const [deleting, setDeleting] = useState<RoleDto>();

  return (
    <>
      <div className="toolbar">
        <span className="grow" />
        {canEdit && <button className="btn btn-primary" onClick={() => setEditing({ mode: 'new' })}>New role</button>}
      </div>
      <ErrorNote error={roles.error} />
      <DataTable rows={roles.data} loading={roles.loading} rowKey={(r) => r.id}
        columns={[
          { key: 'n', header: 'Role', render: (r) => <><strong>{r.name}</strong> {r.isSystem && <Tag>Default</Tag>} {isLimited(r.adScope) && <Tag kind="info" title="This role can only manage some OUs or groups in Active Directory">Limited AD scope</Tag>}<div className="muted small">{r.description}</div></> },
          { key: 'p', header: 'Permissions', className: 'right', render: (r) => r.permissions.length },
          { key: 'u', header: 'Users', className: 'right', render: (r) => r.userCount },
          { key: 'm', header: 'Okta groups', className: 'right', render: (r) => r.mappingCount },
          {
            key: 'a', header: '', className: 'right nowrap', render: (r) => (
              <div className="row gap" style={{ justifyContent: 'flex-end' }}>
                <button className="btn btn-sm" onClick={() => setEditing({ role: r, mode: canEdit && !r.isLocked ? 'edit' : 'view' })}>{canEdit && !r.isLocked ? 'Edit' : 'View'}</button>
                {canEdit && <button className="btn btn-sm" onClick={() => setEditing({ role: r, mode: 'clone' })}>Clone</button>}
                {canEdit && !r.isSystem && <button className="btn btn-sm" onClick={() => setDeleting(r)}>Delete</button>}
              </div>
            ),
          },
        ]} />
      {editing && perms.data && (
        <RoleEditor state={editing} permissions={perms.data} onClose={() => setEditing(undefined)} onSaved={() => { setEditing(undefined); roles.reload(); perms.reload(); }} />
      )}
      {deleting && (
        <ConfirmModal title="Delete role" danger confirmLabel="Delete" onClose={() => setDeleting(undefined)}
          onConfirm={async () => { await del(`/admin/roles/${deleting.id}`); roles.reload(); }}>
          <p>Delete the role <strong>{deleting.name}</strong>? A role that is still assigned cannot be deleted.</p>
        </ConfirmModal>
      )}
    </>
  );
}

function RoleEditor({ state, permissions, onClose, onSaved }: {
  state: { role?: RoleDto; mode: 'view' | 'edit' | 'new' | 'clone' }; permissions: PermissionDto[]; onClose: () => void; onSaved: () => void;
}) {
  const { role, mode } = state;
  const readOnly = mode === 'view' || role?.isLocked === true;
  const [name, setName] = useState(mode === 'clone' ? `${role!.name} (copy)` : role?.name ?? '');
  const [description, setDescription] = useState(role?.description ?? '');
  const [chosen, setChosen] = useState<Set<string>>(new Set(role?.permissions ?? []));
  const [error, setError] = useState<unknown>();
  const [busy, setBusy] = useState(false);
  const groups = useMemo(() => [...new Set(permissions.map((p) => p.group))], [permissions]);
  const [scope, setScope] = useState<AdScope>(role?.adScope ?? noScope);
  const scopeOptions = useAsync(() => get<ScopeOptions>('/admin/roles/ad-scope-options'), []);

  const save = async () => {
    setBusy(true); setError(undefined);
    const body = { name, description, permissions: [...chosen], adScope: scope };
    try {
      if (mode === 'edit') await put(`/admin/roles/${role!.id}`, body);
      else if (mode === 'clone') await post(`/admin/roles/${role!.id}/clone`, { name });
      else await post('/admin/roles', body);
      onSaved();
    } catch (e) { setError(e); setBusy(false); }
  };

  const title = mode === 'new' ? 'New role' : mode === 'clone' ? `Clone ${role!.name}` : role!.name;
  return (
    <Modal title={title} onClose={onClose} busy={busy} footer={
      <>
        <button className="btn" onClick={onClose} disabled={busy}>{readOnly ? 'Close' : 'Cancel'}</button>
        {!readOnly && <button className="btn btn-primary" onClick={() => void save()} disabled={busy || !name.trim()}>Save</button>}
      </>}>
      {role?.isLocked && <Note>The Admins role always holds every permission and cannot be edited.</Note>}
      <Field label="Name" htmlFor="role-name">
        <input id="role-name" value={name} onChange={(e) => setName(e.target.value)} disabled={readOnly || (mode === 'edit' && role?.isSystem)} maxLength={100} />
      </Field>
      {mode !== 'clone' && (
        <Field label="Description" htmlFor="role-desc">
          <input id="role-desc" value={description} onChange={(e) => setDescription(e.target.value)} disabled={readOnly} />
        </Field>
      )}
      {mode === 'clone'
        ? <p className="muted">The clone starts with the same {role!.permissions.length} permissions. Edit it afterwards.</p>
        : groups.map((g) => (
          <div key={g} className="perm-group">
            <strong>{g}</strong>
            {permissions.filter((p) => p.group === g).map((p) => (
              <label key={p.id} className="check">
                <input type="checkbox" checked={chosen.has(p.id)} disabled={readOnly}
                  onChange={(e) => { const n = new Set(chosen); e.target.checked ? n.add(p.id) : n.delete(p.id); setChosen(n); }} />
                <span><span className="mono">{p.id}</span> <span className="muted small">{p.description}</span></span>
              </label>
            ))}
          </div>
        ))}
      {mode !== 'clone' && scopeOptions.data && (
        <RoleScopeEditor value={scope} onChange={setScope} options={scopeOptions.data} readOnly={readOnly} />
      )}
      {mode === 'clone' && isLimited(role?.adScope) && <p className="muted">The clone also keeps this role's limits on what it can manage in Active Directory.</p>}
      <ErrorNote error={error} />
    </Modal>
  );
}

// ---------------------------------------------------------------- permissions

function PermissionsTab() {
  const perms = useAsync(() => get<PermissionDto[]>('/admin/permissions'), []);
  return (
    <>
      <ErrorNote error={perms.error} />
      <DataTable rows={perms.data} loading={perms.loading} rowKey={(p) => p.id}
        columns={[
          { key: 'g', header: 'Area', render: (p) => p.group },
          { key: 'i', header: 'Permission', render: (p) => <span className="mono">{p.id}</span> },
          { key: 'd', header: 'Description', render: (p) => p.description },
          { key: 'r', header: 'Included in roles', render: (p) => p.roles.map((r) => <Tag key={r}>{r}</Tag>) },
        ]} />
    </>
  );
}
