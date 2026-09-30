import { useEffect, useRef, useState, type ReactNode } from 'react';
import { ApiError } from '../Services/api';
import { Icon } from './Icon';

export type TagKind = 'neutral' | 'success' | 'warning' | 'error' | 'info' | 'muted';

export function Tag({ kind = 'neutral', children, title }: { kind?: TagKind; children: ReactNode; title?: string }) {
  return <span className={`tag tag-${kind}`} title={title}>{children}</span>;
}

export function Spinner({ label = 'Loading' }: { label?: string }) {
  return <div className="spinner" role="status" aria-label={label}><span /></div>;
}

export function ErrorNote({ error }: { error: unknown }) {
  if (!error) return null;
  const e = error as ApiError;
  return (
    <div className="note note-error" role="alert">
      <div>{e.message || 'Something went wrong.'}</div>
      {e.correlationId && <div className="muted small">Correlation ID: {e.correlationId}</div>}
    </div>
  );
}

export function Note({ kind = 'info', children }: { kind?: 'info' | 'warning' | 'error' | 'success'; children: ReactNode }) {
  return <div className={`note note-${kind}`} role={kind === 'error' ? 'alert' : undefined}>{children}</div>;
}

export function Empty({ children }: { children: ReactNode }) {
  return <div className="empty">{children}</div>;
}

export function Card({ title, actions, children, className = '' }: { title?: ReactNode; actions?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <section className={`card ${className}`}>
      {(title || actions) && (
        <header className="card-header">
          <h2>{title}</h2>
          <div className="row gap">{actions}</div>
        </header>
      )}
      {children}
    </section>
  );
}

export function PageHeader({ title, subtitle, actions }: { title: string; subtitle?: ReactNode; actions?: ReactNode }) {
  return (
    <div className="page-header">
      <div>
        <h1>{title}</h1>
        {subtitle && <p className="muted">{subtitle}</p>}
      </div>
      <div className="row gap">{actions}</div>
    </div>
  );
}

/** Shows the green tick for a moment after something was done. */
function useTick(ms = 1500) {
  const [done, setDone] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);
  useEffect(() => () => clearTimeout(timer.current), []);
  return { done, flash: () => { setDone(true); clearTimeout(timer.current); timer.current = setTimeout(() => setDone(false), ms); } };
}

/**
 * Copies to the clipboard and shows a green tick. `iconOnly` gives a small round icon (with the label as its tooltip);
 * `variant="button"` looks like the other small buttons, for places such as the password tools.
 */
export function CopyButton({ value, label = 'Copy', iconOnly = false, variant = 'ghost' }: {
  value: string; label?: string; iconOnly?: boolean; variant?: 'ghost' | 'button';
}) {
  const { done, flash } = useTick();
  return (
    <button type="button" className={`btn btn-sm ${variant === 'ghost' ? 'btn-ghost' : ''} ${iconOnly ? 'btn-icon' : ''} ${done ? 'is-done' : ''}`}
      aria-label={label} title={label}
      onClick={async () => {
        try { await navigator.clipboard.writeText(value); flash(); } catch { /* clipboard blocked */ }
      }}>
      <Icon name={done ? 'check' : 'copy'} size={14} />{!iconOnly && <> {done ? 'Copied' : label}</>}
      <span className="sr-only" role="status">{done ? 'Copied' : ''}</span>
    </button>
  );
}

/** A round icon button (refresh) that turns into a green tick for a moment once it has been pressed. */
export function RefreshButton({ onRefresh, label = 'Refresh' }: { onRefresh: () => void; label?: string }) {
  const { done, flash } = useTick();
  return (
    <button type="button" className={`btn btn-icon btn-round ${done ? 'is-done' : ''}`} aria-label={label} title={label}
      onClick={() => { onRefresh(); flash(); }}>
      <Icon name={done ? 'check' : 'refresh'} size={16} />
    </button>
  );
}

export function Drawer({ open, onClose, header, children, wide = false }: {
  open: boolean; onClose: () => void; header: ReactNode; children: ReactNode; wide?: boolean;
}) {
  const ref = useRef<HTMLElement>(null);
  useEffect(() => {
    if (!open) return;
    // Esc closes the top-most layer only: a confirmation dialog open on top of this pop-up takes the key for itself.
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape' && !ref.current?.querySelector('.modal-root')) onClose(); };
    document.addEventListener('keydown', onKey);
    ref.current?.focus();
    return () => document.removeEventListener('keydown', onKey);
  }, [open, onClose]);
  if (!open) return null;
  return (
    <div className="drawer-root">
      <div className="drawer-overlay" onClick={onClose} />
      <aside ref={ref} tabIndex={-1} className={`drawer ${wide ? 'drawer-wide' : ''}`} role="dialog" aria-modal="true">
        <button className="btn btn-ghost drawer-close" onClick={onClose} aria-label="Close panel"><Icon name="close" /></button>
        <div className="drawer-header">{header}</div>
        <div className="drawer-body">{children}</div>
      </aside>
    </div>
  );
}

export interface TabDef { id: string; label: string; badge?: ReactNode }

export function Tabs({ tabs, active, onChange }: { tabs: TabDef[]; active: string; onChange: (id: string) => void }) {
  return (
    <div className="tabs" role="tablist">
      {tabs.map((t) => (
        <button key={t.id} role="tab" aria-selected={t.id === active} className={`tab ${t.id === active ? 'active' : ''}`}
          onClick={() => onChange(t.id)}>
          {t.label}{t.badge}
        </button>
      ))}
    </div>
  );
}

export function Modal({ title, children, onClose, footer, busy = false }: {
  title: string; children: ReactNode; onClose: () => void; footer?: ReactNode; busy?: boolean;
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape' && !busy) onClose(); };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onClose, busy]);
  return (
    <div className="modal-root">
      <div className="modal-overlay" onClick={() => !busy && onClose()} />
      <div className="modal" role="dialog" aria-modal="true" aria-label={title}>
        <h2>{title}</h2>
        <div className="modal-body">{children}</div>
        {footer && <div className="modal-footer">{footer}</div>}
      </div>
    </div>
  );
}

export const PAGE_SIZES = [25, 50, 100];

export function Pagination({ page, pageSize, total, capped, onPage, onPageSize, pageSizes = PAGE_SIZES }: {
  page: number; pageSize: number; total: number; capped?: boolean; onPage: (p: number) => void;
  /** When given, a "Rows per page" choice is shown at the right. */
  onPageSize?: (n: number) => void; pageSizes?: number[];
}) {
  const pages = Math.max(1, Math.ceil(total / pageSize));
  const from = total === 0 ? 0 : (page - 1) * pageSize + 1;
  const to = Math.min(total, page * pageSize);
  return (
    <div className="pagination">
      <span className="muted">{from}-{to} of {total}{capped ? '+' : ''}</span>
      <div className="row gap wrap">
        {onPageSize && (
          <label className="row gap small">
            <span className="muted">Rows per page</span>
            <select value={pageSize} onChange={(e) => onPageSize(Number(e.target.value))} aria-label="Rows per page">
              {(pageSizes.includes(pageSize) ? pageSizes : [...pageSizes, pageSize].sort((x, y) => x - y)).map((n) => <option key={n} value={n}>{n}</option>)}
            </select>
          </label>
        )}
        <button className="btn btn-sm" disabled={page <= 1} onClick={() => onPage(page - 1)}>Previous</button>
        <span>Page {page} of {pages}</span>
        <button className="btn btn-sm" disabled={page >= pages} onClick={() => onPage(page + 1)}>Next</button>
      </div>
    </div>
  );
}

export interface Column<T> { key: string; header: string; render: (row: T) => ReactNode; className?: string }

export function DataTable<T>({ columns, rows, rowKey, onRowClick, loading, empty = 'Nothing to show.' }: {
  columns: Column<T>[]; rows: T[] | undefined; rowKey: (r: T) => string; onRowClick?: (r: T) => void; loading?: boolean; empty?: ReactNode;
}) {
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>{columns.map((c) => <th key={c.key} className={c.className}>{c.header}</th>)}</tr>
        </thead>
        <tbody>
          {rows?.map((r) => (
            <tr key={rowKey(r)} className={onRowClick ? 'clickable' : ''} tabIndex={onRowClick ? 0 : undefined}
              onClick={() => onRowClick?.(r)}
              onKeyDown={(e) => { if (onRowClick && e.key === 'Enter') onRowClick(r); }}>
              {columns.map((c) => <td key={c.key} className={c.className}>{c.render(r)}</td>)}
            </tr>
          ))}
          {!loading && rows?.length === 0 && (
            <tr><td colSpan={columns.length}><Empty>{empty}</Empty></td></tr>
          )}
        </tbody>
      </table>
      {loading && <Spinner />}
    </div>
  );
}

/** A red asterisk that marks a field as required. It is decoration for the eye; the input itself carries aria-required. */
export const RequiredMark = () => <span className="req" aria-hidden="true" title="Required"> *</span>;

export function Field({ label, htmlFor, hint, error, children, required = false, labelExtra }: {
  label: string; htmlFor?: string; hint?: ReactNode; error?: string; children: ReactNode;
  required?: boolean; labelExtra?: ReactNode;
}) {
  return (
    <div className="field">
      <div className="field-label-row">
        <label htmlFor={htmlFor}>{label}{required && <RequiredMark />}</label>
        {labelExtra}
      </div>
      {children}
      {hint && <div className="muted small">{hint}</div>}
      {error && <div className="field-error" role="alert">{error}</div>}
    </div>
  );
}

export function KeyValue({ items }: { items: { label: string; value: ReactNode }[] }) {
  return (
    <dl className="kv">
      {items.map((i) => (
        <div key={i.label} className="kv-row"><dt>{i.label}</dt><dd>{i.value ?? <span className="muted">Not set</span>}</dd></div>
      ))}
    </dl>
  );
}

export const NotSet = () => <span className="muted">Not set</span>;
