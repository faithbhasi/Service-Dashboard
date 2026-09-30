import { useState, type ReactNode } from 'react';
import { ApiError, post } from '../Services/api';
import { useShell } from '../Hooks/ShellContext';
import { CopyButton, Field, Modal, Note, RequiredMark, Spinner, Tag } from './ui';

export interface DirectoryChange { field: string; from: string | null; to: string | null }
export interface DryRunCheck { name: string; passed: boolean; detail: string | null }
export interface ChangeResult {
  status: 'Success' | 'NoChange' | 'Failed' | 'Denied' | 'Validated'; message: string; correlationId: string; action: string; target: string;
  errorCode: string | null; changes: DirectoryChange[]; checks: DryRunCheck[]; dryRun: boolean; groupId?: string | null; groupName?: string | null;
}

/** Endpoints answer with a ChangeResult (or, for group changes, { results: [...] }). 403 and 422 carry the same body. */
function normalize(body: unknown): ChangeResult[] {
  const b = body as { results?: ChangeResult[] } & Partial<ChangeResult>;
  if (Array.isArray(b?.results)) return b.results;
  if (b && typeof b.status === 'string') return [b as ChangeResult];
  return [];
}

async function call(path: string, body: object): Promise<ChangeResult[]> {
  try {
    return normalize(await post(path, body));
  } catch (e) {
    if (e instanceof ApiError && (e.status === 403 || e.status === 422)) {
      const results = normalize(e.data);
      if (results.length) return results;
    }
    throw e;
  }
}

const ok = (r: ChangeResult) => r.status === 'Validated' || r.status === 'Success' || r.status === 'NoChange';

type Step = 'form' | 'review' | 'done';

/**
 * The confirmation dialog every AD change goes through. Step 1 collects justification, ticket number and (when the
 * Action Policy asks) a typed confirmation. Step 2 shows the action, target, current and new state and the dry-run result
 * from the server; nothing is written until the user confirms. Step 3 shows the outcome with the correlation ID.
 */
export function ChangeDialog({ title, policyKey, path, target, typedExpected, warning, extraBody, secretBody, validateOnly = false, confirmLabel, danger, onClose, onFinished }: {
  title: string;
  policyKey: string;
  path: string;
  /** Who or what is being changed, for example "alice.smith (Alice Smith)". */
  target: string;
  /** What the user must type to confirm when the policy requires it (the account or computer name). */
  typedExpected: string;
  warning?: ReactNode;
  /** Non-secret fields for the request (target OU, group ids, checkbox options...). */
  extraBody: () => object;
  /** Fields that are only sent when actually confirming, never in the dry run (the new password). */
  secretBody?: () => object;
  /** "Validate only" troubleshooting mode: the dry run is the whole point and nothing can be confirmed. */
  validateOnly?: boolean;
  confirmLabel?: string;
  danger?: boolean;
  onClose: () => void;
  /** Called once when the dialog ends after an attempt, whether or not it worked (for example to clear a password field). */
  onFinished?: (changed: boolean) => void;
}) {
  const { shell } = useShell();
  const policy = shell?.actionPolicies.actions[policyKey];
  const [step, setStep] = useState<Step>('form');
  const [justification, setJustification] = useState('');
  const [ticket, setTicket] = useState('');
  const [typed, setTyped] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [results, setResults] = useState<ChangeResult[]>([]);
  const [changed, setChanged] = useState(false);

  const base = () => ({ justification: justification.trim(), ticketNumber: ticket.trim(), typedConfirmation: typed.trim() });
  const finish = () => { onFinished?.(changed); onClose(); };

  const minJust = policy?.justificationRequired ? Math.max(1, policy.justificationMinLength) : 0;
  const problems: string[] = [];
  if (policy?.justificationRequired && justification.trim().length < minJust) problems.push(`Justification needs at least ${minJust} characters.`);
  if (policy?.ticketRequired && !ticket.trim()) problems.push('A ticket number is required.');
  if (policy?.typedConfirmationRequired && !validateOnly && typed.trim().toLowerCase() !== typedExpected.toLowerCase()) problems.push(`Type ${typedExpected} to confirm.`);

  const review = async () => {
    setBusy(true); setError('');
    try {
      setResults(await call(path, { ...extraBody(), ...base(), validateOnly: true }));
      setStep('review');
    } catch (e) { setError(e instanceof ApiError ? e.message : 'The check failed.'); }
    finally { setBusy(false); }
  };

  const confirm = async () => {
    setBusy(true); setError('');
    try {
      const res = await call(path, { ...extraBody(), ...(secretBody?.() ?? {}), ...base(), validateOnly: false });
      setResults(res);
      setChanged(res.some((r) => r.status === 'Success'));
      setStep('done');
    } catch (e) { setError(e instanceof ApiError ? e.message : 'The change failed.'); }
    finally { setBusy(false); }
  };

  const reviewOk = results.length > 0 && results.some(ok);

  return (
    <Modal title={validateOnly ? `Validate only: ${title}` : title} busy={busy} onClose={() => { if (step === 'done') finish(); else { onFinished?.(false); onClose(); } }}
      footer={
        step === 'form' ? (
          <>
            <button className="btn" onClick={() => { onFinished?.(false); onClose(); }} disabled={busy}>Cancel</button>
            <button className="btn btn-primary" disabled={busy || problems.length > 0} onClick={() => void review()}>{validateOnly ? 'Validate' : 'Review change'}</button>
          </>
        ) : step === 'review' ? (
          <>
            <button className="btn" onClick={() => { onFinished?.(false); onClose(); }} disabled={busy}>Cancel</button>
            <button className="btn" onClick={() => { setStep('form'); setResults([]); }} disabled={busy}>Back</button>
            {!validateOnly && (
              <button className={`btn ${danger ? 'btn-danger' : 'btn-primary'}`} disabled={busy || !reviewOk} onClick={() => void confirm()}>{confirmLabel ?? 'Confirm'}</button>
            )}
          </>
        ) : <button className="btn btn-primary" onClick={finish}>Close</button>
      }>
      {step === 'form' && (
        <>
          <p><strong>Target:</strong> {target}</p>
          {warning && <Note kind="warning">{warning}</Note>}
          {(policy?.justificationRequired || policy?.ticketRequired || (policy?.typedConfirmationRequired && !validateOnly)) && (
            <p className="muted small">Fields marked <RequiredMark /> are required.</p>
          )}
          <Field label={`Justification${policy?.justificationRequired ? ` (at least ${minJust} characters)` : ' (optional)'}`} htmlFor="cd-just" required={!!policy?.justificationRequired}>
            <textarea id="cd-just" rows={3} value={justification} onChange={(e) => setJustification(e.target.value)} maxLength={2000} aria-required={!!policy?.justificationRequired} />
          </Field>
          <Field label={`Ticket number${policy?.ticketRequired ? ' (required)' : ' (optional)'}`} htmlFor="cd-ticket" required={!!policy?.ticketRequired}
            hint={policy?.ticketPattern ? `Format: ${policy.ticketPattern}` : undefined}>
            <input id="cd-ticket" value={ticket} onChange={(e) => setTicket(e.target.value)} maxLength={100} aria-required={!!policy?.ticketRequired} />
          </Field>
          {policy?.typedConfirmationRequired && !validateOnly && (
            <Field label={`Type ${typedExpected} to confirm`} htmlFor="cd-typed" required
              labelExtra={<CopyButton value={typedExpected} label={`Copy ${typedExpected}`} iconOnly />}>
              <input id="cd-typed" value={typed} onChange={(e) => setTyped(e.target.value)} autoComplete="off" aria-required="true" />
            </Field>
          )}
          {error && <Note kind="error">{error}</Note>}
        </>
      )}

      {step === 'review' && (
        <>
          {warning && <Note kind="warning">{warning}</Note>}
          <p><strong>Action:</strong> {title}<br /><strong>Target:</strong> {target}</p>
          {justification.trim() && <p><strong>Justification:</strong> {justification.trim()}</p>}
          {ticket.trim() && <p><strong>Ticket:</strong> {ticket.trim()}</p>}
          {results.map((r, i) => <ResultView key={i} r={r} showGroup={results.length > 1 || !!r.groupName} />)}
          {validateOnly && reviewOk && <Note kind="success">Validated. No change was made.</Note>}
          {!validateOnly && !reviewOk && <Note kind="error">The change cannot be made. Nothing has been changed.</Note>}
          {error && <Note kind="error">{error}</Note>}
        </>
      )}

      {step === 'done' && (
        <>
          {results.map((r, i) => (
            <div key={i}>
              {r.groupName && <h3>{r.groupName}</h3>}
              <Note kind={r.status === 'Success' ? 'success' : r.status === 'NoChange' ? 'info' : 'error'}>{r.message}</Note>
              <p className="muted small">Correlation ID: <span className="mono">{r.correlationId}</span></p>
            </div>
          ))}
        </>
      )}
      {busy && <Spinner />}
    </Modal>
  );
}

function ResultView({ r, showGroup }: { r: ChangeResult; showGroup: boolean }) {
  return (
    <div style={{ marginBottom: 14 }}>
      {showGroup && r.groupName && <h3>{r.groupName}</h3>}
      {r.changes.length > 0 && (
        <div role="table" aria-label="Current and new state">
          <div className="diff-row muted small"><span>Field</span><span>Current</span><span>New</span></div>
          {r.changes.map((c) => (
            <div key={c.field} className="diff-row"><strong>{c.field}</strong><span>{c.from ?? '-'}</span><span>{c.to ?? '-'}</span></div>
          ))}
        </div>
      )}
      {r.checks.length > 0 && (
        <>
          <h3>Dry-run result</h3>
          <ul style={{ margin: 0, paddingLeft: 18 }}>
            {r.checks.map((c) => (
              <li key={c.name}><Tag kind={c.passed ? 'success' : 'error'}>{c.passed ? 'Pass' : 'Fail'}</Tag> {c.name}{c.detail ? ` - ${c.detail}` : ''}</li>
            ))}
          </ul>
        </>
      )}
      <Note kind={ok(r) ? (r.status === 'NoChange' ? 'info' : 'success') : 'error'}>{r.message}</Note>
    </div>
  );
}
