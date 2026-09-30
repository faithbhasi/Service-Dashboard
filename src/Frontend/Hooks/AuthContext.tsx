import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { get, put, setCsrfToken, setUnauthorizedHandler } from '../Services/api';
import type { Me } from '../Services/types';
import { useTheme } from '../Themes/ThemeProvider';

interface AuthValue {
  me: Me | null;
  loading: boolean;
  can: (permission: string) => boolean;
  canAny: (...permissions: string[]) => boolean;
  refresh: () => Promise<void>;
  updatePreferences: (p: Partial<Me['preferences']>) => Promise<void>;
}

const AuthContext = createContext<AuthValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null>(null);
  const [loading, setLoading] = useState(true);
  const theme = useTheme();

  const refresh = useCallback(async () => {
    try {
      const m = await get<Me>('/auth/me');
      setCsrfToken(m.csrfToken);
      setMe(m);
      theme.setUserPreference(m.preferences.theme);
    } catch {
      setMe(null);
    } finally {
      setLoading(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    setUnauthorizedHandler(() => setMe(null));
    void refresh();
    return () => setUnauthorizedHandler(null);
  }, [refresh]);

  const value = useMemo<AuthValue>(() => {
    const perms = new Set(me?.permissions ?? []);
    return {
      me,
      loading,
      can: (p) => perms.has(p),
      canAny: (...ps) => ps.some((p) => perms.has(p)),
      refresh,
      updatePreferences: async (p) => {
        if (!me) return;
        const next = { ...me.preferences, ...p };
        setMe({ ...me, preferences: next });
        if (p.theme) theme.setUserPreference(p.theme);
        await put('/auth/preferences', { theme: next.theme, navCollapsed: next.navCollapsed });
      },
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [me, loading, refresh]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthValue {
  const v = useContext(AuthContext);
  if (!v) throw new Error('useAuth must be used inside AuthProvider');
  return v;
}

/** Renders children only when the user holds at least one of the permissions. Hiding is a convenience; the API enforces access. */
export function Can({ any, children, fallback = null }: { any: string[]; children: ReactNode; fallback?: ReactNode }) {
  const { canAny } = useAuth();
  return <>{canAny(...any) ? children : fallback}</>;
}
