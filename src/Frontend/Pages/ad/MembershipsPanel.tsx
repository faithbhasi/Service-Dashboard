import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Card, DataTable, ErrorNote, Spinner, Tag } from '../../Components/ui';
import { useAsync } from '../../Hooks/useAsync';
import { get } from '../../Services/api';
import type { AdGroup, Memberships } from '../../Services/adTypes';

interface Row { group: AdGroup; how: 'Direct' | 'Nested' | 'Primary'; via: string | null; removable: boolean; why: string | null }

/** Direct, nested and primary memberships merged into one scrollable list. Remove controls are added by the user drawer. */
export function MembershipsPanel({ path, reloadKey, canRemove, selected, onSelect, footer }: {
  path: string; reloadKey?: number; canRemove?: boolean; selected?: Set<string>; onSelect?: (id: string, on: boolean) => void;
  /** Shown under the list, inside the same card (the drawer puts the remove button here). */
  footer?: ReactNode;
}) {
  const data = useAsync(() => get<Memberships>(path), [path, reloadKey]);
  if (data.loading && !data.data) return <Spinner />;
  if (data.error) return <ErrorNote error={data.error} />;
  const m = data.data!;

  const rows: Row[] = [
    ...m.direct.map((g): Row => ({ group: g, how: 'Direct', via: null, removable: g.isManageable, why: g.blockReason })),
    ...(m.primary ? [{ group: m.primary, how: 'Primary', via: null, removable: false, why: 'The primary group (usually Domain Users) is not a normal membership.' } as Row] : []),
    ...m.nested.map((n): Row => ({ group: n.group, how: 'Nested', via: n.via, removable: false, why: 'Reached through another group, so it cannot be changed directly.' })),
  ];

  return (
    <Card title={`Group memberships (${rows.length})`}>
      <p className="muted small">
        {m.direct.length} direct, {m.nested.length} nested{m.primary ? `, and the primary group ${m.primary.name}` : ''}.
        Only direct memberships on the manageable groups list can be removed.
      </p>
      <div className="scroll-area" tabIndex={0} aria-label="Group memberships">
        <DataTable rows={rows} rowKey={(r) => r.how + r.group.id} empty="Not a member of any group."
          columns={[
            ...(canRemove ? [{
              key: 'sel', header: '', render: (r: Row) => (
                <input type="checkbox" aria-label={`Select ${r.group.name}`} disabled={!r.removable}
                  title={r.why ?? undefined} checked={selected?.has(r.group.id) ?? false} onChange={(e) => onSelect?.(r.group.id, e.target.checked)} />
              ),
            }] : []),
            { key: 'name', header: 'Group', render: (r) => <Link to={`/ad/groups/${r.group.id}`}><strong>{r.group.name}</strong></Link> },
            { key: 'tags', header: 'Scope and type', render: (r) => (
              <span className="row gap wrap">
                <Tag>{r.group.scope}</Tag>
                <Tag>{r.group.type}</Tag>
                {r.how === 'Nested' && <Tag kind="info" title={r.via ? `Through ${r.via}` : undefined}>Nested{r.via ? ` via ${r.via}` : ''}</Tag>}
                {r.how === 'Primary' && <Tag kind="info">Primary</Tag>}
                {r.group.isProtected && <Tag kind="warning" title={r.group.blockReason ?? undefined}>Protected</Tag>}
                {!r.removable && <Tag kind="muted" title={r.why ?? undefined}>Cannot be removed</Tag>}
              </span>
            ) },
            { key: 'desc', header: 'Description', render: (r) => r.group.description ?? '' },
          ]} />
      </div>
      {footer}
    </Card>
  );
}
