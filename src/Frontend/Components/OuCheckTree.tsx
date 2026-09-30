import { useState } from 'react';
import { Icon } from './Icon';
import { ouPath, splitDn } from './adUi';
import { ErrorNote, Spinner, Tag } from './ui';
import { useAsync, useDebounced } from '../Hooks/useAsync';
import { get, qs } from '../Services/api';

export interface OuTreeNode { dn: string; name: string; hasChildren: boolean; selectable: boolean; reason: string | null }

const norm = (dn: string) => splitDn(dn).map((p) => p.trim().toLowerCase().replace(/\s*=\s*/, '='));
/** True when `dn` is the same OU as `ancestor` or sits below it. */
export function isUnderOrEqual(dn: string, ancestor: string): boolean {
  const d = norm(dn), a = norm(ancestor);
  return d.length >= a.length && a.every((part, i) => part === d[d.length - a.length + i]);
}
const same = (x: string, y: string) => norm(x).join(',') === norm(y).join(',');

/**
 * The whole OU tree with a checkbox on every OU. A ticked OU covers everything below it, so the OUs under a ticked one show as included.
 * OUs outside the manageable lists can be seen (to find your way down) but not ticked, and say why.
 */
export function OuCheckTree({ kind, selected, onChange, readOnly, label }: {
  kind: 'users' | 'computers'; selected: string[]; onChange: (next: string[]) => void; readOnly: boolean; label: string;
}) {
  const [input, setInput] = useState('');
  const q = useDebounced(input.trim(), 300);
  const roots = useAsync(() => get<OuTreeNode[]>('/admin/roles/ad-ou-tree' + qs({ kind, q })), [kind, q]);

  const toggle = (dn: string, on: boolean) =>
    onChange(on ? [...selected.filter((s) => !isUnderOrEqual(s, dn)), dn] : selected.filter((s) => !same(s, dn)));

  const rowProps = { kind, selected, toggle, readOnly };
  return (
    <div className="ou-check">
      <input type="search" placeholder="Search OUs by name" value={input} onChange={(e) => setInput(e.target.value)} aria-label={`Search ${label} OUs`} style={{ width: '100%', marginBottom: 6 }} />
      <ErrorNote error={roots.error} />
      {roots.loading && !roots.data ? <Spinner /> : (
        <div className="tree list-check" style={{ padding: 8 }} role="tree" aria-label={`${label} OU tree`}>
          {q
            ? (roots.data?.length ? roots.data.map((n) => <Row key={n.dn} node={n} flat {...rowProps} />) : <span className="muted">No OUs match.</span>)
            : <ul>{(roots.data ?? []).map((n) => <li key={n.dn}><Row node={n} {...rowProps} /></li>)}</ul>}
        </div>
      )}
      <div className="ou-selected" aria-label={`Selected ${label} OUs`}>
        {selected.length === 0
          ? <span className="muted small">No OU ticked.</span>
          : selected.map((dn) => (
            <span key={dn} className="chip" title={dn}>
              {ouPath(dn)}
              {!readOnly && <button type="button" className="chip-x" aria-label={`Remove ${ouPath(dn)}`} onClick={() => toggle(dn, false)}>&times;</button>}
            </span>
          ))}
      </div>
    </div>
  );
}

function Row({ node, flat = false, kind, selected, toggle, readOnly }: {
  node: OuTreeNode; flat?: boolean; kind: 'users' | 'computers'; selected: string[]; toggle: (dn: string, on: boolean) => void; readOnly: boolean;
}) {
  const [open, setOpen] = useState(false);
  const [children, setChildren] = useState<OuTreeNode[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [failed, setFailed] = useState(false);

  const own = selected.some((s) => same(s, node.dn));
  const included = !own && selected.some((s) => isUnderOrEqual(node.dn, s)); // covered by a ticked OU above
  const checked = own || included;
  const disabled = readOnly || included || (!node.selectable && !own);

  const expand = async () => {
    if (!open && children === null) {
      setLoading(true); setFailed(false);
      // A failure leaves the children unloaded so opening the OU again tries again (an empty list would look like "no sub-OUs").
      try { setChildren(await get<OuTreeNode[]>('/admin/roles/ad-ou-tree' + qs({ kind, parent: node.dn }))); } catch { setFailed(true); setOpen(false); setLoading(false); return; }
      setLoading(false);
    }
    setOpen(!open);
  };

  return (
    <>
      <div className={`tree-item ${checked ? 'selected' : ''}`} role="treeitem" aria-selected={checked} aria-expanded={flat || !node.hasChildren ? undefined : open}>
        {!flat && node.hasChildren
          ? <button type="button" className="btn btn-ghost btn-sm" onClick={() => void expand()} aria-label={`${open ? 'Collapse' : 'Expand'} ${node.name}`}><Icon name="chevron" size={14} /></button>
          : <span style={{ width: 30 }} />}
        <label className="check" title={node.reason ?? node.dn} style={{ opacity: node.selectable || own || included ? 1 : 0.6, cursor: disabled ? 'not-allowed' : 'pointer' }}>
          <input type="checkbox" checked={checked} disabled={disabled} onChange={(e) => toggle(node.dn, e.target.checked)} />
          {flat ? ouPath(node.dn) : node.name}
        </label>
        {included && <span className="muted small">included</span>}
        {!node.selectable && !own && !included && <Tag kind="muted" title={node.reason ?? ''}>Not allowed</Tag>}
        {loading && <span className="muted small">Loading...</span>}
        {failed && <span className="field-error" role="alert">Could not load this OU. Try again.</span>}
      </div>
      {open && children && children.length > 0 && (
        <ul>{children.map((c) => <li key={c.dn}><Row node={c} kind={kind} selected={selected} toggle={toggle} readOnly={readOnly} /></li>)}</ul>
      )}
    </>
  );
}
