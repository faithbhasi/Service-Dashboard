import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { get } from '../Services/api';
import type { Branding, ThemeColors } from '../Services/types';
import { mix, readableOn } from './contrast';

export type ThemePreference = 'light' | 'dark' | 'system';

const fallbackLight: ThemeColors = {
  primary: '#1f5fbf', topBarBackground: '#ffffff', navBackground: '#1b2433', navText: '#e6ebf3', navSelected: '#2f4570',
  pageBackground: '#f3f5f9', cardBackground: '#ffffff', sectionHeader: '#1b2433', success: '#146c2e', warning: '#8a5a00', error: '#b3261e',
};
const fallbackDark: ThemeColors = {
  primary: '#7aa7f5', topBarBackground: '#161c28', navBackground: '#0f141d', navText: '#d5dbe6', navSelected: '#26385a',
  pageBackground: '#0b0f16', cardBackground: '#171e2b', sectionHeader: '#e6ebf3', success: '#5fd08a', warning: '#e5b25d', error: '#ff8a80',
};

/** Turns the configurable colours into the CSS variables the stylesheet uses. Text colours are derived so they stay readable. */
export function cssVariables(c: ThemeColors): Record<string, string> {
  const text = readableOn(c.cardBackground);
  const pageText = readableOn(c.pageBackground);
  return {
    '--primary': c.primary,
    '--primary-text': readableOn(c.primary),
    '--topbar-bg': c.topBarBackground,
    '--topbar-text': readableOn(c.topBarBackground),
    '--nav-bg': c.navBackground,
    '--nav-text': c.navText,
    '--nav-selected': c.navSelected,
    '--page-bg': c.pageBackground,
    '--page-text': pageText,
    '--card-bg': c.cardBackground,
    '--text': text,
    '--text-muted': mix(text, c.cardBackground, 0.35),
    '--border': mix(c.cardBackground, text, 0.16),
    '--hover': mix(c.cardBackground, text, 0.06),
    '--section-header': c.sectionHeader,
    '--success': c.success,
    '--warning': c.warning,
    '--error': c.error,
  };
}

interface ThemeValue {
  branding: Branding | undefined;
  mode: 'light' | 'dark';
  preference: ThemePreference;
  setUserPreference: (p: ThemePreference) => void;
  reloadBranding: () => Promise<void>;
  /** Live preview from the Personalization page; pass null to stop previewing. */
  setPreview: (p: { light: ThemeColors; dark: ThemeColors } | null) => void;
}

const ThemeContext = createContext<ThemeValue | null>(null);

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [branding, setBranding] = useState<Branding>();
  const [preference, setUserPreference] = useState<ThemePreference>('system');
  const [systemDark, setSystemDark] = useState(() => window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false);
  const [preview, setPreview] = useState<{ light: ThemeColors; dark: ThemeColors } | null>(null);

  const reloadBranding = useCallback(async () => {
    try { setBranding(await get<Branding>('/settings/branding')); } catch { /* defaults apply */ }
  }, []);

  useEffect(() => { void reloadBranding(); }, [reloadBranding]);

  useEffect(() => {
    const mq = window.matchMedia?.('(prefers-color-scheme: dark)');
    if (!mq) return;
    const fn = (e: MediaQueryListEvent) => setSystemDark(e.matches);
    mq.addEventListener('change', fn);
    return () => mq.removeEventListener('change', fn);
  }, []);

  const mode: 'light' | 'dark' = preference === 'system' ? (systemDark ? 'dark' : 'light') : preference;

  useEffect(() => {
    const src = preview ?? branding;
    const colors = mode === 'dark' ? (src?.dark ?? fallbackDark) : (src?.light ?? fallbackLight);
    const root = document.documentElement;
    for (const [k, v] of Object.entries(cssVariables(colors))) root.style.setProperty(k, v);
    root.dataset.theme = mode;
    root.style.colorScheme = mode;
  }, [branding, preview, mode]);

  useEffect(() => {
    if (!branding) return;
    document.title = branding.productName;
    const link = document.getElementById('app-favicon') as HTMLLinkElement | null;
    if (link) link.href = branding.hasFavicon ? `/api/settings/personalization/logo/favicon?v=${branding.assetVersion ?? ''}` : 'data:,';
  }, [branding]);

  const value = useMemo<ThemeValue>(
    () => ({ branding, mode, preference, setUserPreference, reloadBranding, setPreview }),
    [branding, mode, preference, reloadBranding],
  );
  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>;
}

export function useTheme(): ThemeValue {
  const v = useContext(ThemeContext);
  if (!v) throw new Error('useTheme must be used inside ThemeProvider');
  return v;
}
