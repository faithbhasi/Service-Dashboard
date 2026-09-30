import { Icon } from '../../Components/Icon';
import { Card, Field } from '../../Components/ui';
import { useAuth } from '../../Hooks/AuthContext';
import { useShell } from '../../Hooks/ShellContext';
import { useSettingsForm } from '../../Hooks/useSettingsForm';
import { get, put } from '../../Services/api';
import { DATE_FORMATS } from '../../Services/format';
import { Permissions } from '../../Services/permissions';
import { readableOn } from '../../Themes/contrast';
import { SaveBar, SectionShell } from './SettingsBits';

interface Banner { enabled: boolean; type: 'Information' | 'Warning' | 'Maintenance'; text: string; startLocal: string | null; endLocal: string | null }
interface General {
  productName: string; environmentLabel: string; environmentLabelColor: string; timeZone: string; dateFormat: string; supportContact: string;
  idleTimeoutMinutes: number; absoluteTimeoutMinutes: number; banner: Banner;
}
export const BANNER_MAX = 300;

export function validateGeneral(v: General): Record<string, string> {
  const e: Record<string, string> = {};
  if (!v.productName.trim() || v.productName.length > 100) e.productName = 'Enter a product name of up to 100 characters.';
  if (v.environmentLabel.length > 30) e.environmentLabel = 'Up to 30 characters.';
  if (v.environmentLabelColor && !/^#[0-9a-fA-F]{6}$/.test(v.environmentLabelColor)) e.environmentLabelColor = 'Use a colour like #1f5fbf.';
  try { new Intl.DateTimeFormat('en', { timeZone: v.timeZone }); } catch { e.timeZone = 'Not a known time zone. Use an IANA name such as Europe/London or UTC.'; }
  if (v.supportContact.length > 200) e.supportContact = 'Up to 200 characters.';
  if (!(v.idleTimeoutMinutes >= 5 && v.idleTimeoutMinutes <= 1440)) e.idle = 'Between 5 and 1440 minutes.';
  if (!(v.absoluteTimeoutMinutes >= 15 && v.absoluteTimeoutMinutes <= 10080)) e.absolute = 'Between 15 minutes and 7 days (10080).';
  else if (v.absoluteTimeoutMinutes < v.idleTimeoutMinutes) e.absolute = 'Cannot be shorter than the idle timeout.';
  const b = v.banner;
  if (/[<>]/.test(b.text)) e.bannerText = 'Plain text only. Remove any HTML.';
  else if (b.text.length > BANNER_MAX) e.bannerText = `Up to ${BANNER_MAX} characters.`;
  else if (b.enabled && !b.text.trim()) e.bannerText = 'Enter the banner text, or turn the banner off.';
  if (b.startLocal && b.endLocal && b.endLocal <= b.startLocal) e.bannerEnd = 'The end must be after the start.';
  return e;
}

export function GeneralSection() {
  const { can } = useAuth();
  const { reload: reloadShell } = useShell();
  const canEdit = can(Permissions.SettingsManage);
  const form = useSettingsForm<General>(
    () => get<General>('/settings/general'),
    async (v) => { const saved = await put<General>('/settings/general', v); reloadShell(); return saved; },
    validateGeneral);
  const v = form.value;
  const zones = typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : ['UTC'];

  return (
    <SectionShell loading={!v} error={form.loadError}>
      {v && (
        <>
          <Card title="General">
            <div className="form-grid">
              <Field label="Product name" htmlFor="g-name" error={form.errors.productName}>
                <input id="g-name" value={v.productName} disabled={!canEdit} maxLength={100} onChange={(e) => form.setValue({ ...v, productName: e.target.value })} />
              </Field>
              <Field label="Environment label" htmlFor="g-env" error={form.errors.environmentLabel} hint="Shown as a badge in the top bar, for example Test or Production.">
                <input id="g-env" value={v.environmentLabel} disabled={!canEdit} maxLength={30} onChange={(e) => form.setValue({ ...v, environmentLabel: e.target.value })} />
              </Field>
              <Field label="Environment label colour" htmlFor="g-envcolor" error={form.errors.environmentLabelColor}
                hint="The colour of the badge. Leave on automatic to use red for Production, blue for Test and amber for anything else.">
                <div className="row gap wrap">
                  <input id="g-envcolor" type="color" style={{ width: 52, height: 34, padding: 2 }} disabled={!canEdit} aria-label="Environment label colour"
                    value={/^#[0-9a-fA-F]{6}$/.test(v.environmentLabelColor) ? v.environmentLabelColor : '#b26a00'}
                    onChange={(e) => form.setValue({ ...v, environmentLabelColor: e.target.value })} />
                  <input aria-label="Environment label colour (hex)" style={{ width: 110 }} placeholder="Automatic" disabled={!canEdit} maxLength={7}
                    value={v.environmentLabelColor} onChange={(e) => form.setValue({ ...v, environmentLabelColor: e.target.value.trim() })} />
                  <button type="button" className="btn btn-sm" disabled={!canEdit || !v.environmentLabelColor} onClick={() => form.setValue({ ...v, environmentLabelColor: '' })}>Use automatic colour</button>
                  {v.environmentLabel && (
                    <span className={`env-badge env-${v.environmentLabel.toLowerCase()}`} aria-label="Preview"
                      style={/^#[0-9a-fA-F]{6}$/.test(v.environmentLabelColor) ? { background: v.environmentLabelColor, color: readableOn(v.environmentLabelColor) } : undefined}>
                      {v.environmentLabel}
                    </span>
                  )}
                </div>
              </Field>
              <Field label="Time zone" htmlFor="g-tz" error={form.errors.timeZone} hint="Dates are stored in UTC and shown in this zone.">
                <input id="g-tz" list="zones" value={v.timeZone} disabled={!canEdit} onChange={(e) => form.setValue({ ...v, timeZone: e.target.value })} />
                <datalist id="zones">{zones.map((z) => <option key={z} value={z} />)}</datalist>
              </Field>
              <Field label="Date format" htmlFor="g-df">
                <select id="g-df" value={v.dateFormat} disabled={!canEdit} onChange={(e) => form.setValue({ ...v, dateFormat: e.target.value })}>
                  {DATE_FORMATS.map((f) => <option key={f} value={f}>{f}</option>)}
                </select>
              </Field>
              <Field label="Support contact" htmlFor="g-support" error={form.errors.supportContact} hint='Shown on the "No access assigned" page.'>
                <input id="g-support" value={v.supportContact} disabled={!canEdit} maxLength={200} onChange={(e) => form.setValue({ ...v, supportContact: e.target.value })} />
              </Field>
            </div>
          </Card>

          <Card title="Session timeouts">
            <div className="form-grid">
              <Field label="Idle timeout (minutes)" htmlFor="g-idle" error={form.errors.idle}>
                <input id="g-idle" type="number" value={v.idleTimeoutMinutes} disabled={!canEdit} onChange={(e) => form.setValue({ ...v, idleTimeoutMinutes: Number(e.target.value) })} />
              </Field>
              <Field label="Absolute session lifetime (minutes)" htmlFor="g-abs" error={form.errors.absolute}>
                <input id="g-abs" type="number" value={v.absoluteTimeoutMinutes} disabled={!canEdit} onChange={(e) => form.setValue({ ...v, absoluteTimeoutMinutes: Number(e.target.value) })} />
              </Field>
            </div>
          </Card>

          <Card title="Application banner">
            <label className="check"><input type="checkbox" checked={v.banner.enabled} disabled={!canEdit} onChange={(e) => form.setValue({ ...v, banner: { ...v.banner, enabled: e.target.checked } })} /> Show the banner</label>
            <div className="spacer" />
            <div className="form-grid">
              <Field label="Type" htmlFor="b-type" hint="Information banners can be dismissed by the user for their session. Warning and Maintenance banners cannot.">
                <select id="b-type" value={v.banner.type} disabled={!canEdit} onChange={(e) => form.setValue({ ...v, banner: { ...v.banner, type: e.target.value as Banner['type'] } })}>
                  <option>Information</option><option>Warning</option><option>Maintenance</option>
                </select>
              </Field>
              <Field label={`Start (${v.timeZone})`} htmlFor="b-start">
                <input id="b-start" type="datetime-local" value={v.banner.startLocal ?? ''} disabled={!canEdit} onChange={(e) => form.setValue({ ...v, banner: { ...v.banner, startLocal: e.target.value || null } })} />
              </Field>
              <Field label={`End (${v.timeZone})`} htmlFor="b-end" error={form.errors.bannerEnd}>
                <input id="b-end" type="datetime-local" value={v.banner.endLocal ?? ''} disabled={!canEdit} onChange={(e) => form.setValue({ ...v, banner: { ...v.banner, endLocal: e.target.value || null } })} />
              </Field>
            </div>
            <Field label="Text (plain text only)" htmlFor="b-text" error={form.errors.bannerText} hint={`${v.banner.text.length} / ${BANNER_MAX} characters. Example: Maintenance tonight 10pm-11pm`}>
              <input id="b-text" value={v.banner.text} disabled={!canEdit} maxLength={BANNER_MAX + 50} onChange={(e) => form.setValue({ ...v, banner: { ...v.banner, text: e.target.value } })} />
            </Field>
            <p className="muted small">Preview</p>
            <div className={`banner banner-${v.banner.type.toLowerCase()}`} style={{ borderRadius: 8 }}>
              <Icon name={v.banner.type === 'Information' ? 'info' : v.banner.type === 'Warning' ? 'warning' : 'wrench'} />
              <span className="banner-text">{v.banner.text || 'Banner text appears here'}</span>
            </div>
            <p className="muted small">It appears above the top bar for all signed-in users between the start and end times, and hides itself afterwards. Leave both empty to show it until you turn it off.</p>
          </Card>
          <SaveBar dirty={form.dirty} saving={form.saving} canEdit={canEdit} onSave={() => void form.save()} onCancel={form.cancel} savedAt={form.savedAt} serverError={form.serverError} />
        </>
      )}
    </SectionShell>
  );
}
