import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useDebounced } from './useAsync';

/** Search text, filter and page kept in the URL so lists can be bookmarked and the back button works. */
export function useListParams(defaultFilter = 'All') {
  const [params, setParams] = useSearchParams();
  const q = params.get('q') ?? '';
  const filter = params.get('filter') ?? defaultFilter;
  const page = Math.max(1, Number(params.get('page') ?? 1) || 1);
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
    q, filter, page, input, setInput,
    setFilter: (f: string) => update({ filter: f === defaultFilter ? '' : f, page: '' }),
    setPage: (p: number) => update({ page: p <= 1 ? '' : String(p) }),
    /** The query string to keep when opening a drawer. */
    search: params.toString() ? '?' + params.toString() : '',
  };
}
