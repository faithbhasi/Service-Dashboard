import { Card, DataTable, Field, Note } from '../../Components/ui';
import { useAuth } from '../../Hooks/AuthContext';
import { useShell } from '../../Hooks/ShellContext';
import { useSettingsForm } from '../../Hooks/useSettingsForm';
import { get, put } from '../../Services/api';
import { Permissions } from '../../Services/permissions';
import type { ActionPolicies } from '../../Services/types';
import { SaveBar, SectionShell } from './SettingsBits';

const LABELS: Record<string, string> = {
  resetPassword: 'Reset password', unlock: 'Unlock account', enableUser: 'Enable user', disableUser: 'Disable user', moveUser: 'Move user',
  addToGroups: 'Add user to groups', removeFromGroups: 'Remove user from groups', enableComputer: 'Enable computer', disableComputer: 'Disable computer', moveComputer: 'Move computer',
};

export function validatePolicies(v: ActionPolicies): Record<string, string> {
  const e: Record<string, string> = {};
  for (const [k, p] of Object.entries(v.actions)) {
    if (p.justificationMinLength < 0 || p.justificationMinLength > 500) e[`${k}.min`] = 'Between 0 and 500.';
    else if (p.justificationRequired && p.justificationMinLength < 1) e[`${k}.min`] = 'At least 1 when a justification is required.';
    if (p.ticketPattern) { try { new RegExp(p.ticketPattern); } catch { e[`${k}.pattern`] = 'Not a valid regular expression.'; } }
  }
  if (!(v.generatedPasswordLength >= 8 && v.generatedPasswordLength <= 128)) e.length = 'Between 8 and 128.';
  return e;
}

export function ActionPoliciesSection() {
  const { can } = useAuth();
  const { reload: reloadShell } = useShell();
  const canEdit = can(Permissions.SettingsManage);
  const form = useSettingsForm<ActionPolicies>(
    () => get<ActionPolicies>('/settings/action-policies'),
    async (v) => { const saved = await put<ActionPolicies>('/settings/action-policies', v); reloadShell(); return saved; },
    validatePolicies);
  const v = form.value;
  const rows = v ? Object.keys(LABELS).filter((k) => v.actions[k]).map((k) => ({ key: k, p: v.actions[k] })) : [];
  const update = (key: string, patch: Partial<ActionPolicies['actions'][string]>) => v && form.setValue({ ...v, actions: { ...v.actions, [key]: { ...v.actions[key], ...patch } } });

  return (
    <SectionShell loading={!v} error={form.loadError}>
      {v && (
        <>
          <Note>These rules apply to every AD change, for everyone. A ticket pattern is a regular expression, for example <span className="mono">^INC-\d{'{4,}'}$</span>.</Note>
          <DataTable rows={rows} rowKey={(r) => r.key}
            columns={[
              { key: 'a', header: 'Action', render: (r) => <strong>{LABELS[r.key]}</strong> },
              { key: 'j', header: 'Justification', render: (r) => (
                <div className="row gap">
                  <label className="check"><input type="checkbox" disabled={!canEdit} checked={r.p.justificationRequired} onChange={(e) => update(r.key, { justificationRequired: e.target.checked, justificationMinLength: e.target.checked && r.p.justificationMinLength < 1 ? 10 : r.p.justificationMinLength })} /> Required</label>
                  <input type="number" aria-label={`${LABELS[r.key]} minimum justification length`} style={{ width: 70 }} disabled={!canEdit || !r.p.justificationRequired} value={r.p.justificationMinLength}
                    onChange={(e) => update(r.key, { justificationMinLength: Number(e.target.value) })} />
                  <span className="muted small">min chars</span>
                  {form.errors[`${r.key}.min`] && <span className="field-error">{form.errors[`${r.key}.min`]}</span>}
                </div>) },
              { key: 't', header: 'Ticket number', render: (r) => (
                <div className="row gap wrap">
                  <label className="check"><input type="checkbox" disabled={!canEdit} checked={r.p.ticketRequired} onChange={(e) => update(r.key, { ticketRequired: e.target.checked })} /> Required</label>
                  <input aria-label={`${LABELS[r.key]} ticket pattern`} placeholder="Format (regex, optional)" style={{ width: 200 }} disabled={!canEdit} value={r.p.ticketPattern ?? ''}
                    onChange={(e) => update(r.key, { ticketPattern: e.target.value || null })} />
                  {form.errors[`${r.key}.pattern`] && <span className="field-error">{form.errors[`${r.key}.pattern`]}</span>}
                </div>) },
              { key: 'c', header: 'Typed confirmation', render: (r) => (
                <label className="check"><input type="checkbox" disabled={!canEdit} checked={r.p.typedConfirmationRequired} onChange={(e) => update(r.key, { typedConfirmationRequired: e.target.checked })} /> Required</label>) },
            ]} />
          <div className="spacer" />
          <Card title="Password reset defaults">
            <label className="check"><input type="checkbox" disabled={!canEdit} checked={v.mustChangePasswordDefault} onChange={(e) => form.setValue({ ...v, mustChangePasswordDefault: e.target.checked })} /> "User must change password at next sign-in" is ticked by default</label>
            <div className="spacer" />
            <Field label="Generated password length (characters)" htmlFor="ap-len" error={form.errors.length}>
              <input id="ap-len" type="number" style={{ width: 100 }} disabled={!canEdit} value={v.generatedPasswordLength} min={8} max={128} onChange={(e) => form.setValue({ ...v, generatedPasswordLength: Number(e.target.value) })} />
            </Field>
            <p className="muted small">How many characters "Generate secure password" creates on the Password reset screen (8 to 128). Whoever resets a password can still adjust it for that one reset.</p>
          </Card>
          <SaveBar dirty={form.dirty} saving={form.saving} canEdit={canEdit} onSave={() => void form.save()} onCancel={form.cancel} savedAt={form.savedAt} serverError={form.serverError} />
        </>
      )}
    </SectionShell>
  );
}
