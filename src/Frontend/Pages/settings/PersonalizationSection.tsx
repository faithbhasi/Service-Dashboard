import { useEffect, useMemo, useState } from 'react';
import { ConfirmModal } from '../../Components/ConfirmModal';
import { Card, ErrorNote, Field, Note, Tabs } from '../../Components/ui';
import { useAuth } from '../../Hooks/AuthContext';
import { useShell } from '../../Hooks/ShellContext';
import { useSettingsForm } from '../../Hooks/useSettingsForm';
import { del, get, post, put, upload } from '../../Services/api';
import { Permissions } from '../../Services/permissions';
import type { ThemeColors } from '../../Services/types';
import { isHexColor } from '../../Themes/contrast';
import { contrastWarnings } from '../../Themes/contrastChecks';
import { useTheme } from '../../Themes/ThemeProvider';
import { SaveBar, SectionShell } from './SettingsBits';

interface Personalization {
  productName: string; light: ThemeColors; dark: ThemeColors; defaults: { light: ThemeColors; dark: ThemeColors };
  logos: Record<string, { fileName: string; contentType: string; sizeBytes: number; updatedUtc: string }>; maxLogoBytes: number; allowedTypes: string[];
}
type FormValue = Pick<Personalization, 'productName' | 'light' | 'dark'>;
type Kind = 'light' | 'dark' | 'favicon';

const COLOR_LABELS: [keyof ThemeColors, string][] = [
  ['primary', 'Primary colour'], ['topBarBackground', 'Top bar background'], ['navBackground', 'Left navigation background'], ['navText', 'Left navigation text'],
  ['navSelected', 'Left navigation selected item'], ['pageBackground', 'Page background'], ['cardBackground', 'Card background'], ['sectionHeader', 'Section header'],
  ['success', 'Status: success'], ['warning', 'Status: warning'], ['error', 'Status: error'],
];
const KIND_KEY: Record<Kind, string> = { light: 'logoLight', dark: 'logoDark', favicon: 'favicon' };

export function validatePersonalization(v: FormValue): Record<string, string> {
  const e: Record<string, string> = {};
  if (!v.productName.trim() || v.productName.length > 100) e.productName = 'Enter a product name of up to 100 characters.';
  for (const theme of ['light', 'dark'] as const)
    for (const [k, label] of COLOR_LABELS)
      if (!isHexColor(v[theme][k]) || !/^#[0-9a-fA-F]{6}$/.test(v[theme][k])) e[`${theme}.${k}`] = `${label} must be a hex colour such as #1f5fbf.`;
  return e;
}

export function PersonalizationSection() {
  const { can } = useAuth();
  const { reload: reloadShell } = useShell();
  const theme = useTheme();
  const canEdit = can(Permissions.SettingsPersonalizationManage);
  const [meta, setMeta] = useState<Pick<Personalization, 'defaults' | 'logos' | 'maxLogoBytes' | 'allowedTypes'>>();
  const [pending, setPending] = useState<Partial<Record<Kind, { file: File; url: string }>>>({});
  const [fileError, setFileError] = useState('');
  const [tab, setTab] = useState<'light' | 'dark'>('light');
  const [resetLogo, setResetLogo] = useState<Kind>();
  const [confirmColors, setConfirmColors] = useState(false);

  const form = useSettingsForm<FormValue>(
    async () => {
      const r = await get<Personalization>('/settings/personalization');
      setMeta({ defaults: r.defaults, logos: r.logos, maxLogoBytes: r.maxLogoBytes, allowedTypes: r.allowedTypes });
      return { productName: r.productName, light: r.light, dark: r.dark };
    },
    async (v) => {
      await put('/settings/personalization', v);
      for (const [kind, p] of Object.entries(pending) as [Kind, { file: File; url: string }][]) await upload(`/settings/personalization/logo/${kind}`, p.file);
      Object.values(pending).forEach((p) => URL.revokeObjectURL(p.url));
      setPending({});
      const fresh = await get<Personalization>('/settings/personalization');
      setMeta({ defaults: fresh.defaults, logos: fresh.logos, maxLogoBytes: fresh.maxLogoBytes, allowedTypes: fresh.allowedTypes });
      await theme.reloadBranding();
      theme.setPreview(null);
      reloadShell();
      return { productName: fresh.productName, light: fresh.light, dark: fresh.dark };
    },
    validatePersonalization);
  const v = form.value;

  // Live preview: the whole app takes the edited colours immediately, but nothing is saved until Save.
  useEffect(() => {
    if (v && Object.keys(validatePersonalization(v)).filter((k) => k.includes('.')).length === 0) theme.setPreview({ light: v.light, dark: v.dark });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [v]);
  useEffect(() => () => theme.setPreview(null), []); // eslint-disable-line react-hooks/exhaustive-deps

  const warnings = useMemo(() => (v ? { light: contrastWarnings(v.light), dark: contrastWarnings(v.dark) } : { light: [], dark: [] }), [v]);
  const pendingCount = Object.keys(pending).length;

  const choose = (kind: Kind, file: File | undefined) => {
    setFileError('');
    if (!file || !meta) return;
    if (!meta.allowedTypes.includes(file.type)) { setFileError('Only PNG, JPEG and WebP images are accepted. SVG is not allowed because it can contain scripts.'); return; }
    if (file.size > meta.maxLogoBytes) { setFileError(`The image is larger than the limit of ${Math.round(meta.maxLogoBytes / 1024)} KB.`); return; }
    if (pending[kind]) URL.revokeObjectURL(pending[kind]!.url);
    setPending({ ...pending, [kind]: { file, url: URL.createObjectURL(file) } });
  };

  const cancel = () => { Object.values(pending).forEach((p) => URL.revokeObjectURL(p.url)); setPending({}); setFileError(''); form.cancel(); theme.setPreview(null); };

  return (
    <SectionShell loading={!v || !meta} error={form.loadError}>
      {v && meta && (
        <>
          <Card title="Branding">
            <Field label="Product name" htmlFor="p-name" error={form.errors.productName}>
              <input id="p-name" value={v.productName} disabled={!canEdit} maxLength={100} onChange={(e) => form.setValue({ ...v, productName: e.target.value })} />
            </Field>
            <p className="muted small">PNG, JPEG or WebP, up to {Math.round(meta.maxLogoBytes / 1024)} KB. SVG is not accepted because it can contain scripts.</p>
            {fileError && <Note kind="error">{fileError}</Note>}
            <div className="form-grid">
              {(['light', 'dark', 'favicon'] as Kind[]).map((kind) => {
                const stored = meta.logos[KIND_KEY[kind]];
                const label = kind === 'light' ? 'Logo (light theme)' : kind === 'dark' ? 'Logo (dark theme)' : 'Favicon';
                return (
                  <div key={kind} className="field">
                    <label htmlFor={`logo-${kind}`}>{label}</label>
                    <div className="row gap" style={{ minHeight: 48, background: kind === 'dark' ? '#171e2b' : '#f3f5f9', borderRadius: 8, padding: 8, justifyContent: 'center' }}>
                      {pending[kind]
                        ? <img src={pending[kind]!.url} alt={`New ${label} preview`} style={{ maxHeight: 40, maxWidth: '100%' }} />
                        : stored ? <img src={`/api/settings/personalization/logo/${kind}?v=${new Date(stored.updatedUtc).getTime()}`} alt={`Current ${label}`} style={{ maxHeight: 40, maxWidth: '100%' }} />
                          : <span className="muted small">Default</span>}
                    </div>
                    {pending[kind] && <span className="muted small">New image selected - not saved yet.</span>}
                    {canEdit && (
                      <div className="row gap wrap">
                        <input id={`logo-${kind}`} type="file" accept={meta.allowedTypes.join(',')} onChange={(e) => choose(kind, e.target.files?.[0])} style={{ maxWidth: 220 }} />
                        {stored && !pending[kind] && <button className="btn btn-sm" onClick={() => setResetLogo(kind)}>Reset to default</button>}
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          </Card>

          <Card title="Colours" actions={canEdit && <button className="btn btn-sm" onClick={() => setConfirmColors(true)}>Reset to defaults</button>}>
            <Note>Changes preview live across the whole app. Use the theme button in the top bar to see the other theme. Nothing is saved until you press Save.</Note>
            <Tabs tabs={[{ id: 'light', label: 'Light theme' }, { id: 'dark', label: 'Dark theme' }]} active={tab} onChange={(t) => setTab(t as 'light' | 'dark')} />
            {COLOR_LABELS.map(([key, label]) => {
              const value = v[tab][key];
              const warn = warnings[tab].filter((w) => w.field === key);
              const err = form.errors[`${tab}.${key}`];
              return (
                <div key={key}>
                  <div className="color-row">
                    <label htmlFor={`c-${tab}-${key}`}>{label}</label>
                    <input type="color" aria-label={`${label} picker`} value={isHexColor(value) && value.length === 7 ? value : '#000000'} disabled={!canEdit}
                      onChange={(e) => form.setValue({ ...v, [tab]: { ...v[tab], [key]: e.target.value } })} />
                    <input id={`c-${tab}-${key}`} value={value} disabled={!canEdit} className="mono" onChange={(e) => form.setValue({ ...v, [tab]: { ...v[tab], [key]: e.target.value } })} />
                    <span className="muted small">Default {meta.defaults[tab][key]}</span>
                  </div>
                  {err && <div className="field-error" role="alert">{err}</div>}
                  {warn.map((w) => <div key={w.message} className="note note-warning small" role="status">Contrast warning: {w.message}</div>)}
                </div>
              );
            })}
          </Card>
          <SaveBar dirty={form.dirty || pendingCount > 0} saving={form.saving} canEdit={canEdit} onSave={() => void form.save()} onCancel={cancel} savedAt={form.savedAt} serverError={form.serverError} />
        </>
      )}
      <ErrorNote error={undefined} />
      {resetLogo && (
        <ConfirmModal title="Reset logo to default" danger confirmLabel="Reset" onClose={() => setResetLogo(undefined)}
          onConfirm={async () => {
            await del(`/settings/personalization/logo/${resetLogo}`);
            const fresh = await get<Personalization>('/settings/personalization');
            setMeta({ defaults: fresh.defaults, logos: fresh.logos, maxLogoBytes: fresh.maxLogoBytes, allowedTypes: fresh.allowedTypes });
            await theme.reloadBranding();
          }}><p>The custom image is removed and the default is used again.</p></ConfirmModal>
      )}
      {confirmColors && (
        <ConfirmModal title="Reset colours to defaults" danger confirmLabel="Reset colours" onClose={() => setConfirmColors(false)}
          onConfirm={async () => { await post('/settings/personalization/reset-colors'); form.reload(); await theme.reloadBranding(); theme.setPreview(null); }}>
          <p>Both the light and dark theme colours go back to the built-in defaults, for everyone. This is saved immediately.</p>
        </ConfirmModal>
      )}
    </SectionShell>
  );
}
