import { contrastWarnings } from '../../src/Frontend/Themes/contrastChecks';
import { cssVariables } from '../../src/Frontend/Themes/ThemeProvider';
import { validateGeneral } from '../../src/Frontend/Pages/settings/GeneralSection';
import { validatePersonalization } from '../../src/Frontend/Pages/settings/PersonalizationSection';
import { validatePolicies } from '../../src/Frontend/Pages/settings/ActionPoliciesSection';
import { validateAd } from '../../src/Frontend/Pages/settings/AdIntegrationSection';
import type { ThemeColors } from '../../src/Frontend/Services/types';

const light: ThemeColors = {
  primary: '#1f5fbf', topBarBackground: '#ffffff', navBackground: '#1b2433', navText: '#e6ebf3', navSelected: '#2f4570',
  pageBackground: '#f3f5f9', cardBackground: '#ffffff', sectionHeader: '#1b2433', success: '#146c2e', warning: '#8a5a00', error: '#b3261e',
};

describe('personalization contrast warnings', () => {
  it('accepts the default colours', () => expect(contrastWarnings(light)).toEqual([]));

  it('warns when navigation text is unreadable on the navigation background', () => {
    const w = contrastWarnings({ ...light, navText: '#3a4a66' });
    expect(w.some((x) => x.field === 'navText' && /below the WCAG AA/.test(x.message))).toBe(true);
  });

  it('warns about pale status colours on white cards', () => {
    const fields = contrastWarnings({ ...light, success: '#9be7b0', warning: '#ffe08a', error: '#ffb3ad' }).map((x) => x.field);
    expect(fields).toEqual(expect.arrayContaining(['success', 'warning', 'error']));
  });

  it('warns when a mid-tone primary makes button text hard to read', () => {
    expect(contrastWarnings({ ...light, primary: '#777777' }).some((x) => x.field === 'primary')).toBe(true);
  });
});

describe('theme variables', () => {
  it('derives readable text colours so custom backgrounds stay legible', () => {
    const dark = cssVariables({ ...light, cardBackground: '#101820', pageBackground: '#000000' });
    expect(dark['--text']).toBe('#ffffff');
    expect(dark['--page-text']).toBe('#ffffff');
    expect(cssVariables(light)['--text']).toBe('#111827');
    expect(cssVariables(light)['--primary']).toBe('#1f5fbf');
  });
});

describe('settings validation (mirrors the server)', () => {
  const general = {
    productName: 'P', environmentLabel: 'Test', timeZone: 'UTC', dateFormat: 'yyyy-MM-dd', supportContact: '', idleTimeoutMinutes: 30, absoluteTimeoutMinutes: 480,
    banner: { enabled: false, type: 'Information' as const, text: '', startLocal: null, endLocal: null },
  };

  it('accepts valid general settings', () => expect(validateGeneral(general)).toEqual({}));

  it('rejects an unknown time zone, inverted timeouts and HTML in the banner', () => {
    expect(validateGeneral({ ...general, timeZone: 'Mars/Olympus' }).timeZone).toBeTruthy();
    expect(validateGeneral({ ...general, idleTimeoutMinutes: 120, absoluteTimeoutMinutes: 60 }).absolute).toMatch(/shorter/);
    expect(validateGeneral({ ...general, banner: { ...general.banner, enabled: true, text: '<b>Hi</b>' } }).bannerText).toMatch(/Plain text/);
    expect(validateGeneral({ ...general, banner: { ...general.banner, enabled: true, text: '' } }).bannerText).toBeTruthy();
    expect(validateGeneral({ ...general, banner: { ...general.banner, text: 'x'.repeat(301) } }).bannerText).toMatch(/300/);
    expect(validateGeneral({ ...general, banner: { ...general.banner, startLocal: '2026-03-05T10:00', endLocal: '2026-03-05T09:00' } }).bannerEnd).toBeTruthy();
  });

  it('rejects colours that are not hex values', () => {
    const ok = { productName: 'P', light, dark: light };
    expect(validatePersonalization(ok)).toEqual({});
    expect(Object.keys(validatePersonalization({ ...ok, light: { ...light, primary: 'red' } }))).toContain('light.primary');
    expect(Object.keys(validatePersonalization({ ...ok, dark: { ...light, navText: 'url(x)' } }))).toContain('dark.navText');
  });

  it('checks action policy patterns and lengths', () => {
    const base = { mustChangePasswordDefault: true, generatedPasswordLength: 16, actions: { unlock: { justificationRequired: true, justificationMinLength: 10, ticketRequired: false, ticketPattern: null, typedConfirmationRequired: false } } };
    expect(validatePolicies(base)).toEqual({});
    expect(validatePolicies({ ...base, actions: { unlock: { ...base.actions.unlock, ticketPattern: '([bad' } } })['unlock.pattern']).toBeTruthy();
    expect(validatePolicies({ ...base, generatedPasswordLength: 4 }).length).toBeTruthy();
    expect(validatePolicies({ ...base, actions: { unlock: { ...base.actions.unlock, justificationMinLength: 0 } } })['unlock.min']).toBeTruthy();
  });

  it('checks AD settings', () => {
    const ad = { manageableUserOus: ['OU=Staff,DC=x,DC=y'], manageableComputerOus: [], protectedOus: [], manageableGroups: [], protectedGroups: [], employeeIdAttribute: 'employeeID', computerLastUserAttribute: null, searchResultLimit: 1000 };
    expect(validateAd(ad)).toEqual({});
    expect(validateAd({ ...ad, manageableUserOus: ['nonsense'] }).manageableUserOus).toBeTruthy();
    expect(validateAd({ ...ad, employeeIdAttribute: 'a=*)(b' }).employeeIdAttribute).toBeTruthy();
    expect(validateAd({ ...ad, searchResultLimit: 10 }).searchResultLimit).toBeTruthy();
  });
});
