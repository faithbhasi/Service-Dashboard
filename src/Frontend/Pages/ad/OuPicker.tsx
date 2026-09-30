import { useState } from 'react';
import { Icon } from '../../Components/Icon';
import { ErrorNote, Spinner, Tag } from '../../Components/ui';
import { useAsync, useDebounced } from '../../Hooks/useAsync';
import { get, qs } from '../../Services/api';
import type { OuNode } from '../../Services/adTypes';

/** Lazy-loading OU tree with search. Only OUs the backend marks as allowed can be chosen; blocked ones say why. */
export function OuPicker({ kind, selected, onSelect, selectAny = false }: { kind: 'User' | 'Computer'; selected: string; onSelect: (dn: string) => void; selectAny?: boolean }) {
  const [input, setInput] = useState('');
  const q = useDebounced(input.trim(), 300);
  const roots = useAsync(() => (q ? get<OuNode[]>('/modules/ad/ous' + qs({ kind, q })) : get<OuNode[]>('/modules/ad/ous' + qs({ kind }))), [kind, q]);

  return (
    <div>
      <input type="search" placeholder="Search OUs by name" value={input} onChange={(e) => setInput(e.target.value)} aria-label="Search OUs" style={{ width: '100%', marginBottom: 8 }} />
      <ErrorNote error={roots.error} />
      {roots.loading && !roots.data ? <Spinner /> : (
        <div className="tree list-check" style={{ padding: 8 }} role="tree" aria-label="Organisational units">
          {q
            ? (roots.data?.length ? roots.data.map((n) => <Row key={n.dn} node={n} selected={selected} onSelect={onSelect} flat kind={kind} any={selectAny} />) : <span className="muted">No OUs match.</span>)
            : <Level nodes={roots.data ?? []} selected={selected} onSelect={onSelect} kind={kind} any={selectAny} />}
        </div>
      )}
    </div>
  );
}

function Level({ nodes, selected, onSelect, kind, any }: { nodes: OuNode[]; selected: string; onSelect: (dn: string) => void; kind: 'User' | 'Computer'; any: boolean }) {
  return <ul>{nodes.map((n) => <li key={n.dn}><Row node={n} selected={selected} onSelect={onSelect} kind={kind} any={any} /></li>)}</ul>;
}

function Row({ node, selected, onSelect, flat = false, kind, any }: { node: OuNode; selected: string; onSelect: (dn: string) => void; flat?: boolean; kind: 'User' | 'Computer'; any: boolean }) {
  const usable = any || node.allowed;
  const [open, setOpen] = useState(false);
  const [children, setChildren] = useState<OuNode[] | null>(null);
  const [loading, setLoading] = useState(false);

  const toggle = async () => {
    if (!open && children === null) {
      setLoading(true);
      try { setChildren(await get<OuNode[]>('/modules/ad/ous' + qs({ kind, parent: node.dn }))); } catch { setChildren([]); }
      setLoading(false);
    }
    setOpen(!open);
  };

  return (
    <>
      <div className={`tree-item ${selected === node.dn ? 'selected' : ''}`} role="treeitem" aria-selected={selected === node.dn} aria-expanded={flat ? undefined : open}>
        {!flat && node.hasChildren
          ? <button className="btn btn-ghost btn-sm" onClick={() => void toggle()} aria-label={`${open ? 'Collapse' : 'Expand'} ${node.name}`}><Icon name="chevron" size={14} /></button>
          : <span style={{ width: 30 }} />}
        <label className="check" title={node.reason ?? undefined} style={{ opacity: usable ? 1 : 0.6, cursor: usable ? 'pointer' : 'not-allowed' }}>
          <input type="radio" name="target-ou" disabled={!usable} checked={selected === node.dn} onChange={() => onSelect(node.dn)} />
          {node.name}
        </label>
        {!usable && <Tag kind="warning" title={node.reason ?? ''}>Not allowed</Tag>}
        {loading && <span className="muted small">Loading...</span>}
      </div>
      {open && children && children.length > 0 && <Level nodes={children} selected={selected} onSelect={onSelect} kind={kind} any={any} />}
    </>
  );
}
