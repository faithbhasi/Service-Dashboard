import { useEffect, useState, type ReactNode } from 'react';
import { ChangeDialog } from '../../Components/ChangeDialog';
import { Icon } from '../../Components/Icon';
import { Ou } from '../../Components/adUi';
import { CopyButton, Card, ErrorNote, Note, Spinner, Tag } from '../../Components/ui';
import { useAuth } from '../../Hooks/AuthContext';
import { useShell } from '../../Hooks/ShellContext';
import { useAsync, useDebounced } from '../../Hooks/useAsync';
import { get, qs } from '../../Services/api';
import { generatePassword } from '../../Services/password';
import type { AdComputer, AdUser } from '../../Services/adTypes';
import { Permissions } from '../../Services/permissions';
import { OuPicker } from './OuPicker';

/** Buttons for one action. "Validate" (a dry run that changes nothing) is a troubleshooting aid for people who manage settings. */
export function Actions({ children, onValidate, disabled }: { children: ReactNode; onValidate: () => void; disabled?: boolean }) {
  const { can } = useAuth();
  return (
    <div className="form-actions">
      {children}
      {can(Permissions.SettingsManage) && <button className="btn" onClick={onValidate} disabled={disabled} title="Check that the change would work, without making it">Validate</button>}
    </div>
  );
}

type Mode = null | 'confirm' | 'validate';

// ---------------------------------------------------------------------------------------------------- password

/** An eye (password hidden, click to show) or crossed-out eye (password visible, click to hide) inside the field. */
function EyeToggle({ show, onToggle }: { show: boolean; onToggle: () => void }) {
  return (
    <button type="button" className="btn btn-ghost btn-icon eye" onClick={onToggle} aria-pressed={show}
      aria-label={show ? 'Hide password' : 'Show password'} title={show ? 'Hide password' : 'Show password'}>
      <Icon name={show ? 'eyeOff' : 'eye'} size={18} />
    </button>
  );
}

export function PasswordResetPanel({ user, onChanged }: { user: AdUser; onChanged: () => void }) {
  const { shell } = useShell();
  const { can } = useAuth();
  const [pw, setPw] = useState('');
  // The generated length is one global setting (Settings > Action Policies), not chosen per reset.
  const generatedLength = shell?.actionPolicies.generatedPasswordLength ?? 16;
  const [confirm, setConfirm] = useState('');
  const [show, setShow] = useState(false);
  const [mustChange, setMustChange] = useState(shell?.actionPolicies.mustChangePasswordDefault ?? true);
  // Unlocking here is an unlock, so it needs the unlock permission, and it only applies while the account is locked out.
  const [unlockChoice, setUnlockChoice] = useState(true);
  const canUnlock = can(Permissions.AdUsersUnlock) && user.lockedOut;
  const unlock = canUnlock && unlockChoice;
  const [mode, setMode] = useState<Mode>(null);

  const clear = () => { setPw(''); setConfirm(''); setShow(false); };
  // Closing the tab or the drawer unmounts this panel, which drops the state; clear explicitly as well.
  useEffect(() => clear, []);

  const mismatch = confirm.length > 0 && pw !== confirm;
  const ready = pw.length > 0 && pw === confirm;

  return (
    <Card title="Password reset">
      <Note kind="warning">
        Resetting this password may interrupt access, require the user to authenticate again, and affect services or applications using the previous password.
        Confirm identity, approval, and applicable password policy before proceeding.
      </Note>
      <div className="form-grid">
        <div className="field">
          <label htmlFor="pw-new">New password</label>
          <div className="input-eye">
            <input id="pw-new" type={show ? 'text' : 'password'} value={pw} onChange={(e) => setPw(e.target.value)} autoComplete="new-password" spellCheck={false} />
            <EyeToggle show={show} onToggle={() => setShow((v) => !v)} />
          </div>
        </div>
        <div className="field">
          <label htmlFor="pw-confirm">Confirm password</label>
          <div className="input-eye">
            <input id="pw-confirm" type={show ? 'text' : 'password'} value={confirm} onChange={(e) => setConfirm(e.target.value)} autoComplete="new-password" spellCheck={false} />
            <EyeToggle show={show} onToggle={() => setShow((v) => !v)} />
          </div>
          {mismatch && <div className="field-error" role="alert">The passwords do not match.</div>}
        </div>
      </div>
      <div className="pwd-row">
        <button className="btn btn-sm" type="button" onClick={() => { const g = generatePassword(generatedLength); setPw(g); setConfirm(g); setShow(true); }}>
          Generate secure password
        </button>
        {pw && <CopyButton value={pw} label="Copy password" variant="button" />}
      </div>
      <div className="muted small">Generated passwords are {generatedLength} characters long. Administrators set this under Settings &gt; Action Policies.</div>
      <div className="spacer" />
      <label className="check"><input type="checkbox" checked={mustChange} onChange={(e) => setMustChange(e.target.checked)} /> User must change password at next sign-in</label>
      {canUnlock && (
        <label className="check"><input type="checkbox" checked={unlockChoice} onChange={(e) => setUnlockChoice(e.target.checked)} /> Also unlock the account (it is currently locked out)</label>
      )}
      {!user.enabled && <Note>This account is disabled. The password can be reset, but the user cannot sign in until the account is enabled.</Note>}
      <p className="muted small">Active Directory enforces the domain password policy. If it rejects the password you will be told, without the password being repeated.</p>
      <Actions onValidate={() => setMode('validate')}>
        <button className="btn btn-primary" disabled={!ready} onClick={() => setMode('confirm')}>Reset password</button>
      </Actions>
      {mode && (
        <ChangeDialog title="Reset password" policyKey="resetPassword" path={`/modules/ad/users/${user.id}/reset-password`}
          target={`${user.samAccountName} (${user.displayName ?? user.samAccountName})`} typedExpected={user.samAccountName} validateOnly={mode === 'validate'}
          extraBody={() => ({ mustChangeAtNextSignIn: mustChange, unlockAccount: unlock })}
          secretBody={() => ({ newPassword: pw })} confirmLabel="Reset password" danger
          warning="The new password is sent once, over the secure connection, and is never stored, logged or shown again."
          onClose={() => { setMode(null); clear(); }}
          onFinished={(changed) => { clear(); if (changed) onChanged(); }} />
      )}
    </Card>
  );
}

// ---------------------------------------------------------------------------------------------------- unlock

export function UnlockPanel({ user, onChanged }: { user: AdUser; onChanged: () => void }) {
  const [mode, setMode] = useState<Mode>(null);
  return (
    <Card title="Unlock account">
      <p>Current state: {user.lockedOut ? <Tag kind="warning">Locked out</Tag> : <Tag kind="success">Not locked</Tag>}</p>
      <p className="muted small">
        Unlock is always available, because the lockout state shown here can be a little behind the domain controller.
        It is checked again just before unlocking, and if the account is no longer locked nothing is changed.
      </p>
      <Actions onValidate={() => setMode('validate')}>
        <button className="btn btn-primary" onClick={() => setMode('confirm')}>Unlock account</button>
      </Actions>
      {mode && (
        <ChangeDialog title="Unlock account" policyKey="unlock" path={`/modules/ad/users/${user.id}/unlock`}
          target={`${user.samAccountName} (${user.displayName ?? user.samAccountName})`} typedExpected={user.samAccountName} validateOnly={mode === 'validate'}
          extraBody={() => ({})} confirmLabel="Unlock" onClose={() => setMode(null)} onFinished={(c) => c && onChanged()} />
      )}
    </Card>
  );
}

// ---------------------------------------------------------------------------------------------------- account actions (primary tab)

/** Password reset, unlock and enable/disable in one place. Each section only appears when the role holds its permission. */
export function AccountActionsPanel({ user, onChanged }: { user: AdUser; onChanged: () => void }) {
  const { can, canAny } = useAuth();
  return (
    <div className="stack">
      {can(Permissions.AdUsersResetPassword) && <PasswordResetPanel user={user} onChanged={onChanged} />}
      {can(Permissions.AdUsersUnlock) && <UnlockPanel user={user} onChanged={onChanged} />}
      {canAny(Permissions.AdUsersEnable, Permissions.AdUsersDisable) && <EnableDisablePanel kind="User" obj={user} onChanged={onChanged} />}
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------- enable / disable

export function EnableDisablePanel({ kind, obj, onChanged }: { kind: 'User' | 'Computer'; obj: AdUser | AdComputer; onChanged: () => void }) {
  const { can } = useAuth();
  const [mode, setMode] = useState<Mode>(null);
  const isUser = kind === 'User';
  const enabled = obj.enabled;
  const name = isUser ? (obj as AdUser).samAccountName : (obj as AdComputer).name;
  const label = isUser ? `${name} (${(obj as AdUser).displayName ?? name})` : name;
  const enable = !enabled;
  const allowed = can(enable ? (isUser ? Permissions.AdUsersEnable : Permissions.AdComputersEnable) : (isUser ? Permissions.AdUsersDisable : Permissions.AdComputersDisable));
  const base = `/modules/ad/${isUser ? 'users' : 'computers'}/${obj.id}`;

  return (
    <Card title={isUser ? 'Enable / Disable account' : 'Enable / Disable computer'}>
      <p>Current state: {enabled ? <Tag kind="success">Enabled</Tag> : <Tag kind="error">Disabled</Tag>} &rarr; New state: {enabled ? <Tag kind="error">Disabled</Tag> : <Tag kind="success">Enabled</Tag>}</p>
      {enabled && (
        <Note kind="warning">
          {isUser
            ? 'Disabling this account prevents normal sign-in and may interrupt access to connected resources. This does not remove licences, revoke all cloud sessions, transfer ownership, or complete the leaver process.'
            : 'Disabling this computer account may cause it to lose access to domain resources, and users may be unable to sign in to it.'}
        </Note>
      )}
      {!allowed && <Note>You do not have permission to {enable ? 'enable' : 'disable'} this {isUser ? 'account' : 'computer'}.</Note>}
      <Actions onValidate={() => setMode('validate')} disabled={!allowed}>
        <button className={`btn ${enable ? 'btn-primary' : 'btn-danger'}`} disabled={!allowed} onClick={() => setMode('confirm')}>
          {enable ? 'Enable' : 'Disable'} {isUser ? 'account' : 'computer'}
        </button>
      </Actions>
      {mode && (
        <ChangeDialog title={`${enable ? 'Enable' : 'Disable'} ${isUser ? 'account' : 'computer'}`}
          policyKey={isUser ? (enable ? 'enableUser' : 'disableUser') : (enable ? 'enableComputer' : 'disableComputer')}
          path={`${base}/${enable ? 'enable' : 'disable'}`} target={label} typedExpected={name} validateOnly={mode === 'validate'}
          extraBody={() => ({})} confirmLabel={enable ? 'Enable' : 'Disable'} danger={!enable}
          onClose={() => setMode(null)} onFinished={(c) => c && onChanged()} />
      )}
    </Card>
  );
}

// ---------------------------------------------------------------------------------------------------- move

export function MoveOuPanel({ kind, obj, manageable, reason, onChanged }: {
  kind: 'User' | 'Computer'; obj: AdUser | AdComputer; manageable: boolean; reason: string | null; onChanged: () => void;
}) {
  const [target, setTarget] = useState('');
  const [mode, setMode] = useState<Mode>(null);
  const isUser = kind === 'User';
  const name = isUser ? (obj as AdUser).samAccountName : (obj as AdComputer).name;
  const label = isUser ? `${name} (${(obj as AdUser).displayName ?? name})` : name;

  return (
    <Card title="Move OU">
      <p>Current OU: <strong><Ou dn={obj.ou} /></strong></p>
      {!manageable && <Note kind="warning">This object cannot be moved: {reason}</Note>}
      <Note>Moving an object can change which Group Policies apply to it and whether it synchronises to the cloud.</Note>
      {manageable && (
        <>
          <h3>Choose the target OU</h3>
          <OuPicker kind={kind} selected={target} onSelect={setTarget} />
          {target && <p>Target OU: <strong><Ou dn={target} /></strong></p>}
        </>
      )}
      <Actions onValidate={() => setMode('validate')} disabled={!manageable || !target}>
        <button className="btn btn-primary" disabled={!manageable || !target} onClick={() => setMode('confirm')}>Move</button>
      </Actions>
      {mode && (
        <ChangeDialog title={`Move ${isUser ? 'user' : 'computer'}`} policyKey={isUser ? 'moveUser' : 'moveComputer'}
          path={`/modules/ad/${isUser ? 'users' : 'computers'}/${obj.id}/move`} target={label} typedExpected={name} validateOnly={mode === 'validate'}
          extraBody={() => ({ targetOu: target })} confirmLabel="Move"
          warning="Moving can change which Group Policies apply and whether the object syncs to the cloud."
          onClose={() => setMode(null)} onFinished={(c) => { if (c) { setTarget(''); onChanged(); } }} />
      )}
    </Card>
  );
}

// ---------------------------------------------------------------------------------------------------- groups

interface Addable { id: string; name: string; description: string | null; scope: string; type: string; alreadyMember: boolean }

export function AddToGroupsPanel({ user, onChanged }: { user: AdUser; onChanged: () => void }) {
  const [input, setInput] = useState('');
  const q = useDebounced(input.trim(), 300);
  const [version, setVersion] = useState(0);
  const groups = useAsync(() => get<Addable[]>(`/modules/ad/users/${user.id}/addable-groups` + qs({ q })), [user.id, q, version]);
  const [chosen, setChosen] = useState<Set<string>>(new Set());
  const [mode, setMode] = useState<Mode>(null);

  return (
    <Card title="Add to groups">
      <p className="muted small">Only groups on the manageable groups allowlist can be added. Protected groups can never be changed here.</p>
      <input type="search" placeholder="Search manageable groups" value={input} onChange={(e) => setInput(e.target.value)} aria-label="Search groups" style={{ width: '100%', marginBottom: 8 }} />
      <ErrorNote error={groups.error} />
      {groups.loading && !groups.data ? <Spinner /> : (
        <div className="list-check">
          {groups.data?.length === 0 && <div className="empty">No manageable groups match.</div>}
          {groups.data?.map((g) => (
            <label key={g.id} className="check">
              <input type="checkbox" disabled={g.alreadyMember} checked={chosen.has(g.id)}
                onChange={(e) => { const n = new Set(chosen); e.target.checked ? n.add(g.id) : n.delete(g.id); setChosen(n); }} />
              <span><strong>{g.name}</strong> {g.alreadyMember && <Tag>Already a member</Tag>} <span className="muted small">{g.description}</span></span>
            </label>
          ))}
        </div>
      )}
      <Actions onValidate={() => setMode('validate')} disabled={chosen.size === 0}>
        <button className="btn btn-primary" disabled={chosen.size === 0} onClick={() => setMode('confirm')}>Add to {chosen.size || ''} selected group{chosen.size === 1 ? '' : 's'}</button>
      </Actions>
      {mode && (
        <ChangeDialog title="Add to groups" policyKey="addToGroups" path={`/modules/ad/users/${user.id}/groups/add`}
          target={`${user.samAccountName} (${user.displayName ?? user.samAccountName})`} typedExpected={user.samAccountName} validateOnly={mode === 'validate'}
          extraBody={() => ({ groupIds: [...chosen] })} confirmLabel="Add to groups"
          onClose={() => setMode(null)} onFinished={(c) => { if (c) { setChosen(new Set()); setVersion((v) => v + 1); onChanged(); } }} />
      )}
    </Card>
  );
}

/** Remove-from-groups button and dialog for the Group Memberships tab. */
export function RemoveFromGroupsBar({ user, selected, onDone }: { user: AdUser; selected: Set<string>; onDone: () => void }) {
  const [mode, setMode] = useState<Mode>(null);
  return (
    <>
      <Actions onValidate={() => setMode('validate')} disabled={selected.size === 0}>
        <button className="btn btn-danger" disabled={selected.size === 0} onClick={() => setMode('confirm')}>Remove from {selected.size || ''} selected group{selected.size === 1 ? '' : 's'}</button>
      </Actions>
      {mode && (
        <ChangeDialog title="Remove from groups" policyKey="removeFromGroups" path={`/modules/ad/users/${user.id}/groups/remove`}
          target={`${user.samAccountName} (${user.displayName ?? user.samAccountName})`} typedExpected={user.samAccountName} validateOnly={mode === 'validate'}
          extraBody={() => ({ groupIds: [...selected] })} confirmLabel="Remove" danger
          warning="Removing a group can take away access to files, applications and mailboxes that depend on it."
          onClose={() => setMode(null)} onFinished={(c) => c && onDone()} />
      )}
    </>
  );
}
