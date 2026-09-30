import type { ReactNode } from 'react';
import { ErrorNote, Note, Spinner } from '../../Components/ui';
import { useAuth } from '../../Hooks/AuthContext';
import { UnsavedChangesGuard } from '../../Hooks/useSettingsForm';

/** Save and Cancel bar plus the unsaved-changes warning, shared by every section. */
export function SaveBar({ dirty, saving, canEdit, onSave, onCancel, savedAt, serverError, disabledReason }: {
  dirty: boolean; saving: boolean; canEdit: boolean; onSave: () => void; onCancel: () => void; savedAt?: number; serverError?: Error; disabledReason?: string;
}) {
  const { can } = useAuth();
  void can;
  return (
    <>
      <UnsavedChangesGuard dirty={dirty} />
      <ErrorNote error={serverError} />
      {!canEdit && <Note>You can view these settings but not change them.</Note>}
      {dirty && canEdit && <Note kind="warning">You have unsaved changes.</Note>}
      {!dirty && savedAt && <Note kind="success">Saved.</Note>}
      <div className="form-actions">
        <button className="btn btn-primary" disabled={!dirty || saving || !canEdit} onClick={onSave} title={disabledReason}>{saving ? 'Saving...' : 'Save'}</button>
        <button className="btn" disabled={!dirty || saving} onClick={onCancel}>Cancel</button>
      </div>
    </>
  );
}

export function SectionShell({ loading, error, children }: { loading: boolean; error?: Error; children: ReactNode }) {
  if (error) return <ErrorNote error={error} />;
  if (loading) return <Spinner />;
  return <>{children}</>;
}
