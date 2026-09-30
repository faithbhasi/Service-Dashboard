import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useDebounced } from './useAsync';

/** Search text, filter and page kept in the URL so lists can be bookmarked and the back button works. */
export function useListParams(defaultFilter = 'All') {
  const [params, setParams] = useSearchParams();
  const q = params.get('q') ?? '';
  const filter = params.get('filter') ?? defaultFilter;
  const page = Math.max(1, Number(params.get('page') ?? 1) || 1);
  const asked = Number(params.get('pageSize'));
  const pageSize = [25, 50, 100].includes(asked) ? asked : 25;
  const [input, setInput] = useState(q);
  const debounced = useDebounced(input, 300);

  useEffect(() => {
    if (debounced !== q) update({ q: debounced, page: '' });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [debounced]);

  function update(changes: Record<string, string>) {
    setParams((prev) => {
      const next = new URLSearchParams(prev);
      for (const [k, v] of Object.entries(changes)) v ? next.set(k, v) : next.delete(k);
      return next;
    }, { replace: true });
  }

  return {
    q, filter, page, pageSize, input, setInput,
    /** Any extra filter kept in the URL (department, title, os...). Changing one goes back to page 1. */
    param: (key: string) => params.get(key) ?? '',
    setParam: (key: string, value: string) => update({ [key]: value, page: '' }),
    /** Several at once in a single URL change (two separate changes in the same moment would overwrite each other). */
    setParams: (changes: Record<string, string>) => update({ ...changes, page: '' }),
    setFilter: (f: string) => update({ filter: f === defaultFilter ? '' : f, page: '' }),
    setPageSize: (n: number) => update({ pageSize: n === 25 ? '' : String(n), page: '' }),
    setPage: (p: number) => update({ page: p <= 1 ? '' : String(p) }),
    /** The query string to keep when opening a drawer. */
    search: params.toString() ? '?' + params.toString() : '',
  };
}

/** A text filter box whose value is kept in the URL after a short pause in typing. */
export function useTextParam(list: ReturnType<typeof useListParams>, key: string) {
  const current = list.param(key);
  const [input, setInput] = useState(current);
  const debounced = useDebounced(input.trim(), 300);
  useEffect(() => {
    if (debounced !== current) list.setParam(key, debounced);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [debounced]);
  return [input, setInput] as const;
}
