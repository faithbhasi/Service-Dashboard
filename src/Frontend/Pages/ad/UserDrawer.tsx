import { useState } from 'react';
import { CopyButton, Drawer, ErrorNote, KeyValue, NotSet, Spinner, Tabs, Tag, Card, type TabDef } from '../../Components/ui';
import { EnabledTag, LockedTag, Ou, ObjectLink, useAdText } from '../../Components/adUi';
import { useAuth } from '../../Hooks/AuthContext';
import { useShell } from '../../Hooks/ShellContext';
import { useAsync } from '../../Hooks/useAsync';
import { get } from '../../Services/api';
import type { UserDetail } from '../../Services/adTypes';
import { Permissions } from '../../Services/permissions';
import { ActivityHistory } from './ActivityHistory';
import { MembershipsPanel } from './MembershipsPanel';
import { AddToGroupsPanel, EnableDisablePanel, MoveOuPanel, PasswordResetPanel, RemoveFromGroupsBar, UnlockPanel } from './ActionPanels';

/** Only the tabs the user's permissions allow are shown. Add to Groups is the default when permitted. */
export function UserDrawer({ id, tab, onTab, onClose, onChanged }: {
  id: string; tab: string; onTab: (t: string) => void; onClose: () => void; onChanged: () => void;
}) {
  const { can, canAny } = useAuth();
  const [version, setVersion] = useState(0);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const detail = useAsync(() => get<UserDetail>(`/modules/ad/users/${id}`), [id, version]);
  const refresh = () => { setVersion((v) => v + 1); onChanged(); };

  const tabs: TabDef[] = [
    ...(can(Permissions.AdUsersGroupsAdd) ? [{ id: 'add-groups', label: 'Add to Groups' }] : []),
    { id: 'groups', label: 'Group Memberships' },
    ...(can(Permissions.AdUsersResetPassword) ? [{ id: 'password', label: 'Password Reset' }] : []),
    ...(can(Permissions.AdUsersUnlock) ? [{ id: 'unlock', label: 'Unlock Account' }] : []),
    ...(canAny(Permissions.AdUsersEnable, Permissions.AdUsersDisable) ? [{ id: 'enable', label: 'Enable / Disable' }] : []),
    ...(can(Permissions.AdUsersMove) ? [{ id: 'move', label: 'Move OU' }] : []),
    { id: 'details', label: 'Account Details' },
    ...(canAny(Permissions.LogsRead, Permissions.LogsReadOwn) ? [{ id: 'activity', label: 'Activity History' }] : []),
  ];
  const active = tabs.some((t) => t.id === tab) ? tab : tabs[0].id;
  const u = detail.data?.user;

  return (
    <Drawer open onClose={onClose} wide header={
      detail.error ? <ErrorNote error={detail.error} /> : !u ? <Spinner /> : (
        <div className="summary">
          <div className="summary-title">
            <h1>{u.displayName ?? u.samAccountName}</h1>
            <EnabledTag enabled={u.enabled} />
            {u.lockedOut && <LockedTag locked />}
            <button className="btn btn-sm" onClick={refresh}>Refresh</button>
          </div>
          <div className="summary-meta">
            {u.title && <span>{u.title}</span>}
            {u.department && <span>{u.department}</span>}
            <span>{u.samAccountName}</span>
            {u.userPrincipalName && <span>{u.userPrincipalName}</span>}
            {u.email && <span>{u.email}</span>}
            <span>OU: <Ou dn={u.ou} /></span>
          </div>
        </div>
      )}>
      {u && (
        <>
          <Tabs tabs={tabs} active={active} onChange={onTab} />
          {active === 'add-groups' && <AddToGroupsPanel user={u} onChanged={refresh} />}
          {active === 'groups' && (
            <>
              <MembershipsPanel path={`/modules/ad/users/${id}/groups`} reloadKey={version} canRemove={can(Permissions.AdUsersGroupsRemove)} selected={selected}
                onSelect={(gid, on) => { const n = new Set(selected); on ? n.add(gid) : n.delete(gid); setSelected(n); }} />
              {can(Permissions.AdUsersGroupsRemove) && <RemoveFromGroupsBar user={u} selected={selected} onDone={() => { setSelected(new Set()); refresh(); }} />}
            </>
          )}
          {active === 'password' && <PasswordResetPanel user={u} onChanged={refresh} />}
          {active === 'unlock' && <UnlockPanel user={u} onChanged={refresh} />}
          {active === 'enable' && <EnableDisablePanel kind="User" obj={u} onChanged={refresh} />}
          {active === 'move' && <MoveOuPanel kind="User" obj={u} manageable={detail.data!.ouManageable} reason={detail.data!.ouReason} onChanged={refresh} />}
          {active === 'details' && <UserDetails user={u} />}
          {active === 'activity' && <ActivityHistory path={`/modules/ad/users/${id}/activity`} reloadKey={version} />}
        </>
      )}
    </Drawer>
  );
}

function UserDetails({ user: u }: { user: NonNullable<ReturnType<typeof useAsync<UserDetail>>['data']>['user'] }) {
  const { date, dateTime } = useShell();
  const t = useAdText();
  return (
    <>
      <Card title="Contact and organisation">
        <KeyValue items={[
          { label: 'Email', value: u.email ? <span className="row gap">{u.email} <CopyButton value={u.email} label="Copy email" /></span> : <NotSet /> },
          { label: 'Manager', value: <ObjectLink value={u.manager} /> },
          { label: 'Department', value: u.department ?? <NotSet /> },
          { label: 'Title', value: u.title ?? <NotSet /> },
          { label: 'Office', value: u.office ?? <NotSet /> },
          { label: 'Phone', value: u.phone ?? <NotSet /> },
          { label: 'Mobile', value: u.mobile ?? <NotSet /> },
          { label: 'Employee ID', value: u.employeeId ?? <NotSet /> },
        ]} />
      </Card>
      <Card title="Account and password status">
        <KeyValue items={[
          { label: 'Account expiry', value: t.accountExpiry(u) },
          { label: 'Password status', value: t.password(u) },
          { label: 'Password policy', value: u.resultantPso ? <>Fine-grained policy: <strong>{u.resultantPso}</strong></> : 'Default domain policy' },
          { label: 'Password last set', value: u.passwordLastSetUtc ? dateTime(u.passwordLastSetUtc) : <NotSet /> },
          { label: 'Last logon (approximate)', value: t.approx(u.lastLogonUtc) },
          { label: 'Created', value: date(u.createdUtc) || <NotSet /> },
          { label: 'Last changed', value: u.changedUtc ? `${dateTime(u.changedUtc)} (as reported by the domain controller queried)` : <NotSet /> },
          { label: 'Description', value: u.description ?? <NotSet /> },
          { label: 'objectGUID', value: <span className="mono">{u.id}</span> },
          { label: 'Distinguished name', value: <span className="mono">{u.dn}</span> },
        ]} />
        <p className="muted small">Last logon uses lastLogonTimestamp, which replicates with a delay of up to about 14 days.</p>
      </Card>
      <Card title={`userAccountControl flags (${u.userAccountControl})`}>
        <ul style={{ margin: 0, paddingLeft: 18 }}>
          {u.uacFlags.map((f) => <li key={f.name}><Tag>{f.name}</Tag> {f.meaning}</li>)}
        </ul>
      </Card>
    </>
  );
}
