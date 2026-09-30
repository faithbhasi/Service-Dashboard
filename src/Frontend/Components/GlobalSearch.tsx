import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useDebounced } from '../Hooks/useAsync';
import { get, qs, ApiError } from '../Services/api';
import { Tag } from './ui';

interface Item { id: string; title: string; subtitle: string | null; tags: string[]; route: string }
interface Category { key: string; label: string; items: Item[]; total: number; totalIsCapped: boolean; seeAllRoute: string }
interface SearchResponse { query: string; modules: { moduleId: string; categories: Category[] }[] }

type Row = { kind: 'item'; item: Item; cat: Category } | { kind: 'more'; cat: Category };

export const MIN_SEARCH_LENGTH = 2;

/** Top-bar search across every enabled module. Ctrl+K focuses it, arrow keys move, Enter opens. */
export function GlobalSearch() {
  const navigate = useNavigate();
  const listId = useId();
  const inputRef = useRef<HTMLInputElement>(null);
  const boxRef = useRef<HTMLDivElement>(null);
  const [input, setInput] = useState('');
  const query = useDebounced(input.trim(), 250);
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(-1);
  const [categories, setCategories] = useState<Category[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const seq = useRef(0);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        inputRef.current?.focus();
        inputRef.current?.select();
        setOpen(true);
      }
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, []);

  useEffect(() => {
    const close = (e: MouseEvent) => { if (!boxRef.current?.contains(e.target as Node)) setOpen(false); };
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, []);

  useEffect(() => {
    if (query.length < MIN_SEARCH_LENGTH) { setCategories([]); setError(''); setLoading(false); return; }
    const mine = ++seq.current;
    setLoading(true);
    get<SearchResponse>('/search' + qs({ q: query })).then(
      (r) => {
        if (mine !== seq.current) return;
        setCategories(r.modules.flatMap((m) => m.categories));
        setActive(-1); setError(''); setLoading(false);
      },
      (e) => {
        if (mine !== seq.current) return;
        setError(e instanceof ApiError ? e.message : 'Search failed.'); setLoading(false);
      });
  }, [query]);

  const rows: Row[] = useMemo(() => categories.flatMap((cat): Row[] => [
    ...cat.items.map((item): Row => ({ kind: 'item', item, cat })),
    ...(cat.total > cat.items.length ? [{ kind: 'more', cat } as Row] : []),
  ]), [categories]);

  const go = (route: string) => { setOpen(false); setInput(''); inputRef.current?.blur(); navigate(route); };

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setOpen(true); setActive((a) => Math.min(rows.length - 1, a + 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive((a) => Math.max(-1, a - 1)); }
    else if (e.key === 'Escape') { setOpen(false); inputRef.current?.blur(); }
    else if (e.key === 'Enter') {
      const row = rows[active];
      if (row) go(row.kind === 'item' ? row.item.route : row.cat.seeAllRoute);
      else if (categories.length > 0) go((categories.find((c) => c.total > 0) ?? categories[0]).seeAllRoute);
    }
  };

  const tooShort = input.trim().length < MIN_SEARCH_LENGTH;
  const noResults = !loading && !error && !tooShort && query === input.trim() && categories.every((c) => c.items.length === 0);
  let rowIndex = -1;

  return (
    <div className="gsearch" ref={boxRef}>
      <input ref={inputRef} className="search-input" type="search" role="combobox" aria-expanded={open} aria-controls={listId}
        aria-activedescendant={active >= 0 ? `${listId}-${active}` : undefined} aria-label="Global search"
        placeholder="Search users, computers, groups (Ctrl+K)" value={input} autoComplete="off"
        onChange={(e) => { setInput(e.target.value); setOpen(true); }} onFocus={() => setOpen(true)} onKeyDown={onKeyDown} />
      {open && input.length > 0 && (
        <div className="gsearch-panel" id={listId} role="listbox">
          {tooShort && <div className="empty">Type at least {MIN_SEARCH_LENGTH} characters.</div>}
          {!tooShort && loading && <div className="empty">Searching...</div>}
          {error && <div className="empty" role="alert">{error}</div>}
          {noResults && <div className="empty">No results.</div>}
          {!tooShort && !error && categories.filter((c) => c.items.length > 0).map((cat) => (
            <div key={cat.key} className="gsearch-group" role="group" aria-label={cat.label}>
              <h4><span>{cat.label}</span><span>{cat.total}{cat.totalIsCapped ? '+' : ''}</span></h4>
              {cat.items.map((item) => {
                const i = ++rowIndex;
                return (
                  <div key={item.id} id={`${listId}-${i}`} role="option" aria-selected={active === i}
                    className={`gsearch-item ${active === i ? 'active' : ''}`} onMouseEnter={() => setActive(i)} onClick={() => go(item.route)}>
                    <div className="row"><strong>{item.title}</strong><span className="row gap">{item.tags.map((t) => <Tag key={t} kind={t === 'Disabled' ? 'error' : 'warning'}>{t}</Tag>)}</span></div>
                    {item.subtitle && <span className="muted small">{item.subtitle}</span>}
                  </div>
                );
              })}
              {cat.total > cat.items.length && (() => {
                const i = ++rowIndex;
                return (
                  <div id={`${listId}-${i}`} role="option" aria-selected={active === i} className={`gsearch-item ${active === i ? 'active' : ''}`}
                    onMouseEnter={() => setActive(i)}>
                    <Link to={cat.seeAllRoute} onClick={(e) => { e.preventDefault(); go(cat.seeAllRoute); }}>See all {cat.total}{cat.totalIsCapped ? '+' : ''} {cat.label.toLowerCase()}</Link>
                  </div>
                );
              })()}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
