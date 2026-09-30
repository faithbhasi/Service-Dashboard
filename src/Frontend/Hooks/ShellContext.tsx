import { createContext, useContext, useMemo, type ReactNode } from 'react';
import { get } from '../Services/api';
import { defaultFormat, formatDate, formatDateTime, type FormatSettings } from '../Services/format';
import type { Shell } from '../Services/types';
import { useAsync } from './useAsync';

interface ShellValue {
  shell: Shell | undefined;
  reload: () => void;
  fmt: FormatSettings;
  dateTime: (iso?: string | null) => string;
  date: (iso?: string | null) => string;
  moduleEnabled: (id: string) => boolean;
}

const ShellContext = createContext<ShellValue | null>(null);

export function ShellProvider({ children }: { children: ReactNode }) {
  const { data, reload } = useAsync(() => get<Shell>('/settings/shell'), []);
  const value = useMemo<ShellValue>(() => {
    const fmt = data ? { timeZone: data.timeZone, dateFormat: data.dateFormat } : defaultFormat;
    return {
      shell: data,
      reload,
      fmt,
      dateTime: (iso) => formatDateTime(iso, fmt),
      date: (iso) => formatDate(iso, fmt),
      moduleEnabled: (id) => data?.modules.find((m) => m.id === id)?.enabled ?? false,
    };
  }, [data, reload]);
  return <ShellContext.Provider value={value}>{children}</ShellContext.Provider>;
}

export function useShell(): ShellValue {
  const v = useContext(ShellContext);
  if (!v) throw new Error('useShell must be used inside ShellProvider');
  return v;
}
