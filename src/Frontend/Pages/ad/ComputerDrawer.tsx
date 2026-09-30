import { useState } from 'react';
import { EnabledTag, HeaderField, ObjectLink, Ou, Text, useAdText } from '../../Components/adUi';
import { Card, CopyButton, Drawer, ErrorNote, KeyValue, Note, NotSet, RefreshButton, Spinner, Tabs, type TabDef } from '../../Components/ui';
import { useAuth } from '../../Hooks/AuthContext';
import { useShell } from '../../Hooks/ShellContext';
import { useAsync } from '../../Hooks/useAsync';
import { get } from '../../Services/api';
import type { ComputerDetail } from '../../Services/adTypes';
import { Permissions } from '../../Services/permissions';
import { ActivityHistory } from './ActivityHistory';
import { MembershipsPanel } from './MembershipsPanel';
import { EnableDisablePanel, MoveOuPanel } from './ActionPanels';

export function ComputerDrawer({ id, tab, onTab, onClose, onChanged }: {
  id: string; tab: string; onTab: (t: string) => void; onClose: () => void; onChanged: () => void;
}) {
  const { can, canAny } = useAuth();
  const { date, dateTime } = useShell();
  const t = useAdText();
  const [version, setVersion] = useState(0);
  const detail = useAsync(() => get<ComputerDetail>(`/modules/ad/computers/${id}`), [id, version]);
  const refresh = () => { setVersion((v) => v + 1); onChanged(); };

  const tabs: TabDef[] = [
    { id: 'details', label: 'Details' },
    ...(canAny(Permissions.AdComputersEnable, Permissions.AdComputersDisable) ? [{ id: 'enable', label: 'Enable / Disable' }] : []),
    ...(can(Permissions.AdComputersMove) ? [{ id: 'move', label: 'Move OU' }] : []),
    { id: 'groups', label: 'Group Memberships' },
    ...(canAny(Permissions.LogsRead, Permissions.LogsReadOwn) ? [{ id: 'activity', label: 'Activity History' }] : []),
  ];
  const active = tabs.some((x) => x.id === tab) ? tab : tabs[0].id;
  const c = detail.data?.computer;

  return (
    <Drawer open onClose={onClose} wide header={
      detail.error ? <ErrorNote error={detail.error} /> : !c ? <Spinner /> : (
        <div className="summary">
          <div className="summary-title"><h1>{c.name}</h1><CopyButton value={c.name} label="Copy computer name" iconOnly /><EnabledTag enabled={c.enabled} /><RefreshButton onRefresh={refresh} /></div>
          <div className="summary-line">
            <HeaderField label="Name">{c.dnsHostName ?? c.name} <CopyButton value={c.dnsHostName ?? c.name} label="Copy full computer name (FQDN)" iconOnly /></HeaderField>
            <HeaderField label="OS">{c.operatingSystem ?? <NotSet />}</HeaderField>
            <HeaderField label="OU" end><Ou dn={c.ou} /></HeaderField>
          </div>
        </div>
      )}>
      {c && (
        <>
          <Tabs tabs={tabs} active={active} onChange={onTab} />
          {active === 'details' && (
            <Card>
              <KeyValue items={[
                { label: 'Computer name', value: c.name },
                { label: 'DNS host name', value: <Text value={c.dnsHostName} /> },
                { label: 'Enabled', value: <EnabledTag enabled={c.enabled} /> },
                { label: 'Operating system', value: <Text value={c.operatingSystem} /> },
                { label: 'OS version', value: <Text value={c.osVersion} /> },
                { label: 'Last logon (approximate)', value: t.approx(c.lastLogonUtc) },
                { label: 'Password last set', value: c.passwordLastSetUtc ? dateTime(c.passwordLastSetUtc) : <NotSet /> },
                { label: 'Managed by', value: <ObjectLink value={c.managedBy} /> },
                { label: 'Description', value: <Text value={c.description} /> },
                { label: 'Last logged-in user', value: c.lastLoggedInUser ?? <span className="muted">Not available in AD</span> },
                { label: 'Last changed', value: c.changedUtc ? `${dateTime(c.changedUtc)} (as reported by the domain controller queried)` : <NotSet /> },
                { label: 'Created', value: date(c.createdUtc) || <NotSet /> },
                { label: 'OU', value: <Ou dn={c.ou} /> },
                { label: 'Distinguished name', value: <span className="mono">{c.dn}</span> },
                { label: 'objectGUID', value: <span className="mono">{c.id}</span> },
              ]} />
              <Note>Active Directory does not record the last logged-in user. It is only shown when an attribute is configured in Settings that holds it.</Note>
            </Card>
          )}
          {active === 'enable' && <EnableDisablePanel kind="Computer" obj={c} onChanged={refresh} />}
          {active === 'move' && <MoveOuPanel kind="Computer" obj={c} manageable={detail.data!.ouManageable} reason={detail.data!.ouReason} onChanged={refresh} />}
          {active === 'groups' && <MembershipsPanel path={`/modules/ad/computers/${id}/groups`} reloadKey={version} />}
          {active === 'activity' && <ActivityHistory path={`/modules/ad/computers/${id}/activity`} reloadKey={version} />}
        </>
      )}
    </Drawer>
  );
}
