import { useState } from 'react';
import { ConfirmModal } from '../../Components/ConfirmModal';
import { DataTable, ErrorNote, Note, Tag } from '../../Components/ui';
import { useAuth } from '../../Hooks/AuthContext';
import { useShell } from '../../Hooks/ShellContext';
import { useAsync } from '../../Hooks/useAsync';
import { get, put } from '../../Services/api';
import { Permissions } from '../../Services/permissions';
import type { ModuleStatus } from '../../Services/types';

export function ModulesSection() {
  const { can } = useAuth();
  const { reload: reloadShell } = useShell();
  const canEdit = can(Permissions.SettingsManage);
  const modules = useAsync(() => get<ModuleStatus[]>('/settings/modules'), []);
  const [toggling, setToggling] = useState<ModuleStatus>();

  return (
    <>
      <Note>Disabling a module hides it from the navigation, search and Home, and its routes answer "module disabled". Nothing is deleted.</Note>
      <ErrorNote error={modules.error} />
      <DataTable rows={modules.data} loading={modules.loading} rowKey={(m) => m.id}
        columns={[
          { key: 'n', header: 'Module', render: (m) => <><strong>{m.name}</strong><div className="muted small">{m.description}</div></> },
          { key: 's', header: 'Status', render: (m) => <Tag kind={m.status === 'Active' ? 'success' : m.status === 'Disabled' ? 'warning' : 'neutral'}>{m.status}</Tag> },
          { key: 'a', header: '', className: 'right', render: (m) => m.status === 'Coming Soon'
            ? <span className="muted small">Coming Soon</span>
            : <button className="btn btn-sm" disabled={!canEdit} onClick={() => setToggling(m)}>{m.enabled ? 'Disable' : 'Enable'}</button> },
        ]} />
      {toggling && (
        <ConfirmModal title={`${toggling.enabled ? 'Disable' : 'Enable'} ${toggling.name}`} danger={toggling.enabled} confirmLabel={toggling.enabled ? 'Disable' : 'Enable'}
          onClose={() => setToggling(undefined)}
          onConfirm={async () => { await put(`/settings/modules/${toggling.id}`, { enabled: !toggling.enabled }); modules.reload(); reloadShell(); }}>
          <p>{toggling.enabled ? `People will no longer be able to use ${toggling.name}.` : `${toggling.name} will be available again.`}</p>
        </ConfirmModal>
      )}
    </>
  );
}
