import { useCallback, useState } from 'react';

/** Which optional table columns are visible, remembered per browser (a convenience only; failures are ignored). */
export function useTableColumns(storageKey: string, all: string[], defaults: string[]) {
  const [visible, setVisible] = useState<string[]>(() => {
    try {
      const raw = localStorage.getItem(storageKey);
      if (raw) {
        const parsed = (JSON.parse(raw) as string[]).filter((k) => all.includes(k));
        if (parsed.length) return parsed;
      }
    } catch { /* storage unavailable */ }
    return defaults;
  });
  const toggle = useCallback((key: string) => {
    setVisible((cur) => {
      const next = cur.includes(key) ? cur.filter((k) => k !== key) : all.filter((k) => cur.includes(k) || k === key);
      try { localStorage.setItem(storageKey, JSON.stringify(next)); } catch { /* ignore */ }
      return next;
    });
  }, [storageKey, all]);
  return { visible, toggle };
}
