import { useState } from 'react';
import { Card, ErrorNote, Field, KeyValue, Note, Spinner, Tag } from '../../Components/ui';
import { ouPath } from '../../Components/adUi';
import { useAuth } from '../../Hooks/AuthContext';
import { useAsync, useDebounced } from '../../Hooks/useAsync';
import { useSettingsForm } from '../../Hooks/useSettingsForm';
import { get, post, put, qs } from '../../Services/api';
import type { AdGroup } from '../../Services/adTypes';
import { Permissions } from '../../Services/permissions';
import type { Paged } from '../../Services/types';
import { OuPicker } from '../ad/OuPicker';
import { SaveBar, SectionShell } from './SettingsBits';

interface AdSettings {
  manageableUserOus: string[]; manageableComputerOus: string[]; protectedOus: string[]; manageableGroups: string[]; protectedGroups: string[];
  employeeIdAttribute: string; computerLastUserAttribute: string | null; searchResultLimit: number;
}
interface Response { connection: { provider: string; domain: string; server: string; port: number; securityMode: string; baseDn: string }; settings: AdSettings }
interface Step { name: string; passed: boolean; detail: string }

const ATTR = /^[A-Za-z][A-Za-z0-9-]{0,63}$/;
const DN = /^[A-Za-z][A-Za-z0-9-]*=.+/;

export function validateAd(v: AdSettings): Record<string, string> {
  const e: Record<string, string> = {};
  for (const [k, list] of Object.entries({ manageableUserOus: v.manageableUserOus, manageableComputerOus: v.manageableComputerOus, protectedOus: v.protectedOus, manageableGroups: v.manageableGroups }))
    if (list.some((dn) => !DN.test(dn.trim()))) e[k] = 'Every entry must be a distinguished name such as OU=Staff,DC=example,DC=test.';
  if (!ATTR.test(v.employeeIdAttribute)) e.employeeIdAttribute = 'A plain attribute name such as employeeID.';
  if (v.computerLastUserAttribute && !ATTR.test(v.computerLastUserAttribute)) e.computerLastUserAttribute = 'A plain attribute name, or leave empty.';
  if (!(v.searchResultLimit >= 50 && v.searchResultLimit <= 5000)) e.searchResultLimit = 'Between 50 and 5000.';
  return e;
}

export function AdIntegrationSection() {
  const { can } = useAuth();
  const canEdit = can(Permissions.SettingsManage);
  const [connection, setConnection] = useState<Response['connection']>();
  const form = useSettingsForm<AdSettings>(
    async () => { const r = await get<Response>('/modules/ad/settings'); setConnection(r.connection); return r.settings; },
    async (v) => (await put<{ settings: AdSettings }>('/modules/ad/settings', v)).settings,
    validateAd);
  const v = form.value;
  const [steps, setSteps] = useState<Step[]>();
  const [testError, setTestError] = useState<unknown>();
  const [testing, setTesting] = useState(false);

  const test = async () => {
    setTesting(true); setTestError(undefined); setSteps(undefined);
    try { setSteps((await post<{ steps: Step[] }>('/modules/ad/settings/test-connection')).steps); } catch (e) { setTestError(e); }
    setTesting(false);
  };

  return (
    <SectionShell loading={!v || !connection} error={form.loadError}>
      {v && connection && (
        <>
          <Card title="Connection" actions={<button className="btn btn-sm" onClick={() => void test()} disabled={testing || !canEdit}>{testing ? 'Testing...' : 'Test connection'}</button>}>
            <KeyValue items={[
              { label: 'Provider', value: <Tag kind={connection.provider === 'Fake' ? 'warning' : 'success'}>{connection.provider}</Tag> },
              { label: 'Domain', value: connection.domain || 'Not set' }, { label: 'Server', value: connection.server || 'Not set' },
              { label: 'Port', value: connection.port }, { label: 'Security mode', value: connection.securityMode }, { label: 'Base DN', value: <span className="mono">{connection.baseDn}</span> },
            ]} />
            <p className="muted small">These come from appsettings.json and cannot be changed here.</p>
            {connection.provider === 'Fake' && <Note kind="warning">The Fake provider is in use: an in-memory directory for local development.</Note>}
            <ErrorNote error={testError} />
            {steps && (
              <ul style={{ paddingLeft: 18 }}>
                {steps.map((s) => <li key={s.name}><Tag kind={s.passed ? 'success' : 'error'}>{s.passed ? 'Pass' : 'Fail'}</Tag> <strong>{s.name}</strong> - {s.detail}</li>)}
              </ul>
            )}
          </Card>

          <Card title="Manageable and protected OUs">
            <Note>An object can only be changed or moved if it sits in, and is moved to, an OU on the allowlist. Domain Controllers, Tier 0, admin and service account OUs are always blocked.</Note>
            <DnList label="Manageable OUs - users" values={v.manageableUserOus} onChange={(x) => form.setValue({ ...v, manageableUserOus: x })} canEdit={canEdit} error={form.errors.manageableUserOus} browse="User" />
            <DnList label="Manageable OUs - computers" values={v.manageableComputerOus} onChange={(x) => form.setValue({ ...v, manageableComputerOus: x })} canEdit={canEdit} error={form.errors.manageableComputerOus} browse="Computer" />
            <DnList label="Protected OUs (never manageable)" values={v.protectedOus} onChange={(x) => form.setValue({ ...v, protectedOus: x })} canEdit={canEdit} error={form.errors.protectedOus} browse="User" />
          </Card>

          <Card title="Manageable and protected groups">
            <Note>Only groups on the allowlist can be added to or removed from. Domain Admins, Enterprise Admins, Schema Admins, Administrators, Account/Backup/Server/Print Operators and any group with adminCount=1 are always protected.</Note>
            <GroupList values={v.manageableGroups} onChange={(x) => form.setValue({ ...v, manageableGroups: x })} canEdit={canEdit} error={form.errors.manageableGroups} />
            <TextList label="Additional protected groups (name or distinguished name)" values={v.protectedGroups} onChange={(x) => form.setValue({ ...v, protectedGroups: x })} canEdit={canEdit} />
          </Card>

          <Card title="Attributes and limits">
            <div className="form-grid">
              <Field label="Employee ID attribute" htmlFor="ad-emp" error={form.errors.employeeIdAttribute} hint="Shown as Employee ID and used by search.">
                <input id="ad-emp" value={v.employeeIdAttribute} disabled={!canEdit} onChange={(e) => form.setValue({ ...v, employeeIdAttribute: e.target.value })} />
              </Field>
              <Field label='"Last logged-in user" attribute for computers' htmlFor="ad-last" error={form.errors.computerLastUserAttribute}
                hint="AD does not record this. Set it only if something (for example a logon script) writes it to an attribute. Leave empty to show 'Not available in AD'.">
                <input id="ad-last" value={v.computerLastUserAttribute ?? ''} disabled={!canEdit} onChange={(e) => form.setValue({ ...v, computerLastUserAttribute: e.target.value || null })} />
              </Field>
              <Field label="Search result limit" htmlFor="ad-limit" error={form.errors.searchResultLimit} hint="The most results a search can return (50 to 5000).">
                <input id="ad-limit" type="number" value={v.searchResultLimit} disabled={!canEdit} onChange={(e) => form.setValue({ ...v, searchResultLimit: Number(e.target.value) })} />
              </Field>
            </div>
          </Card>
          <SaveBar dirty={form.dirty} saving={form.saving} canEdit={canEdit} onSave={() => void form.save()} onCancel={form.cancel} savedAt={form.savedAt} serverError={form.serverError} />
        </>
      )}
    </SectionShell>
  );
}

function DnList({ label, values, onChange, canEdit, error, browse }: {
  label: string; values: string[]; onChange: (v: string[]) => void; canEdit: boolean; error?: string; browse: 'User' | 'Computer';
}) {
  const [text, setText] = useState('');
  const [browsing, setBrowsing] = useState(false);
  const [picked, setPicked] = useState('');
  const add = (dn: string) => { const t = dn.trim(); if (t && !values.some((x) => x.toLowerCase() === t.toLowerCase())) onChange([...values, t]); };
  return (
    <Field label={label} error={error}>
      {values.length === 0 && <span className="muted">Nothing listed.</span>}
      <ul style={{ margin: 0, paddingLeft: 0, listStyle: 'none' }}>
        {values.map((dn) => (
          <li key={dn} className="row gap" style={{ padding: '3px 0' }}>
            <span title={dn}><strong>{ouPath(dn)}</strong> <span className="mono muted">{dn}</span></span>
            {canEdit && <button className="btn btn-sm" onClick={() => onChange(values.filter((x) => x !== dn))} aria-label={`Remove ${dn}`}>Remove</button>}
          </li>
        ))}
      </ul>
      {canEdit && (
        <>
          <div className="row gap wrap">
            <input placeholder="OU=Staff,DC=example,DC=test" value={text} onChange={(e) => setText(e.target.value)} style={{ minWidth: 320 }} aria-label={`${label} - add a distinguished name`} />
            <button className="btn btn-sm" disabled={!text.trim()} onClick={() => { add(text); setText(''); }}>Add</button>
            <button className="btn btn-sm" onClick={() => setBrowsing((b) => !b)}>{browsing ? 'Hide OU tree' : 'Browse OUs'}</button>
          </div>
          {browsing && (
            <div style={{ marginTop: 8 }}>
              <OuPicker kind={browse} selected={picked} onSelect={setPicked} selectAny />
              <button className="btn btn-sm btn-primary" style={{ marginTop: 8 }} disabled={!picked} onClick={() => { add(picked); setPicked(''); }}>Add selected OU</button>
            </div>
          )}
        </>
      )}
    </Field>
  );
}

function GroupList({ values, onChange, canEdit, error }: { values: string[]; onChange: (v: string[]) => void; canEdit: boolean; error?: string }) {
  const [input, setInput] = useState('');
  const q = useDebounced(input.trim(), 300);
  const results = useAsync(() => (q.length >= 2 ? get<Paged<AdGroup>>('/modules/ad/settings/groups' + qs({ q })) : Promise.resolve(undefined)), [q]);
  const cn = (dn: string) => /^CN=((?:\\.|[^,])+)/i.exec(dn)?.[1].replace(/\\(.)/g, '$1') ?? dn;
  return (
    <Field label="Manageable groups" error={error}>
      {values.length === 0 && <span className="muted">No groups are manageable yet.</span>}
      <ul style={{ margin: 0, paddingLeft: 0, listStyle: 'none' }}>
        {values.map((dn) => (
          <li key={dn} className="row gap" style={{ padding: '3px 0' }}>
            <strong>{cn(dn)}</strong> <span className="mono muted">{dn}</span>
            {canEdit && <button className="btn btn-sm" onClick={() => onChange(values.filter((x) => x !== dn))} aria-label={`Remove ${cn(dn)}`}>Remove</button>}
          </li>
        ))}
      </ul>
      {canEdit && (
        <>
          <input type="search" placeholder="Search groups to add" value={input} onChange={(e) => setInput(e.target.value)} aria-label="Search groups to add" style={{ minWidth: 320 }} />
          {results.loading && q.length >= 2 && <Spinner />}
          {results.data && (
            <div className="list-check" style={{ marginTop: 6 }}>
              {results.data.items.length === 0 && <div className="empty">No groups found.</div>}
              {results.data.items.map((g) => (
                <div key={g.id} className="check" style={{ justifyContent: 'space-between' }}>
                  <span><strong>{g.name}</strong> {g.isProtected && <Tag kind="warning">Protected</Tag>} <span className="muted small">{g.description}</span></span>
                  <button className="btn btn-sm" disabled={g.isProtected || values.some((x) => x.toLowerCase() === g.dn.toLowerCase())} onClick={() => onChange([...values, g.dn])}>Add</button>
                </div>
              ))}
            </div>
          )}
        </>
      )}
    </Field>
  );
}

function TextList({ label, values, onChange, canEdit }: { label: string; values: string[]; onChange: (v: string[]) => void; canEdit: boolean }) {
  const [text, setText] = useState('');
  return (
    <Field label={label}>
      {values.length === 0 && <span className="muted">Nothing listed.</span>}
      <ul style={{ margin: 0, paddingLeft: 0, listStyle: 'none' }}>
        {values.map((x) => (
          <li key={x} className="row gap" style={{ padding: '3px 0' }}>
            <span>{x}</span>{canEdit && <button className="btn btn-sm" onClick={() => onChange(values.filter((y) => y !== x))} aria-label={`Remove ${x}`}>Remove</button>}
          </li>
        ))}
      </ul>
      {canEdit && (
        <div className="row gap">
          <input value={text} onChange={(e) => setText(e.target.value)} aria-label={`${label} - add`} style={{ minWidth: 280 }} />
          <button className="btn btn-sm" disabled={!text.trim()} onClick={() => { const t = text.trim(); if (!values.some((y) => y.toLowerCase() === t.toLowerCase())) onChange([...values, t]); setText(''); }}>Add</button>
        </div>
      )}
    </Field>
  );
}
