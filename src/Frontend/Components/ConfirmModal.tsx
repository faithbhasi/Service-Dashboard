import { useState, type ReactNode } from 'react';
import { ErrorNote, Modal } from './ui';

/** Simple confirm dialog for in-app actions (AD changes use the richer ChangeDialog). */
export function ConfirmModal({ title, children, confirmLabel = 'Confirm', danger = false, onConfirm, onClose }: {
  title: string; children: ReactNode; confirmLabel?: string; danger?: boolean; onConfirm: () => Promise<void>; onClose: () => void;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>();
  return (
    <Modal title={title} onClose={onClose} busy={busy} footer={
      <>
        <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
        <button className={`btn ${danger ? 'btn-danger' : 'btn-primary'}`} disabled={busy} onClick={async () => {
          setBusy(true); setError(undefined);
          try { await onConfirm(); onClose(); } catch (e) { setError(e); setBusy(false); }
        }}>{confirmLabel}</button>
      </>
    }>
      {children}
      <ErrorNote error={error} />
    </Modal>
  );
}
