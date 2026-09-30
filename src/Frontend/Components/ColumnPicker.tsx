import { useEffect, useRef, useState } from 'react';

export function ColumnPicker({ columns, visible, onToggle }: {
  columns: { key: string; header: string }[]; visible: string[]; onToggle: (key: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false); };
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [open]);
  return (
    <div ref={ref} style={{ position: 'relative' }}>
      <button className="btn" onClick={() => setOpen((o) => !o)} aria-expanded={open}>Columns</button>
      {open && (
        <div className="menu-panel" style={{ right: 0, minWidth: 220 }} role="group" aria-label="Visible columns">
          {columns.map((c) => (
            <label key={c.key} className="check">
              <input type="checkbox" checked={visible.includes(c.key)} onChange={() => onToggle(c.key)} /> {c.header}
            </label>
          ))}
        </div>
      )}
    </div>
  );
}
