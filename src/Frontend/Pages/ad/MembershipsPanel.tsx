import { Link } from 'react-router-dom';
import { GroupTags } from '../../Components/adUi';
import { Card, DataTable, ErrorNote, Note, Spinner, Tag } from '../../Components/ui';
import { useAsync } from '../../Hooks/useAsync';
import { get } from '../../Services/api';
import type { AdGroup, Memberships } from '../../Services/adTypes';

/** Direct, nested and primary group memberships. Remove controls are added by the user drawer. */
export function MembershipsPanel({ path, reloadKey, canRemove, selected, onSelect }: {
  path: string; reloadKey?: number; canRemove?: boolean; selected?: Set<string>; onSelect?: (id: string, on: boolean) => void;
}) {
  const data = useAsync(() => get<Memberships>(path), [path, reloadKey]);
  if (data.loading && !data.data) return <Spinner />;
  if (data.error) return <ErrorNote error={data.error} />;
  const m = data.data!;

  const nameCell = (g: AdGroup) => <Link to={`/ad/groups/${g.id}`}><strong>{g.name}</strong></Link>;
  return (
    <>
      <Card title={`Direct groups (${m.direct.length})`}>
        <DataTable rows={m.direct} rowKey={(g) => g.id} empty="Not a direct member of any group (other than the primary group)."
          columns={[
            ...(canRemove ? [{
              key: 'sel', header: '', render: (g: AdGroup) => (
                <input type="checkbox" aria-label={`Select ${g.name}`} disabled={!g.isManageable}
                  title={g.blockReason ?? undefined} checked={selected?.has(g.id) ?? false} onChange={(e) => onSelect?.(g.id, e.target.checked)} />
              ),
            }] : []),
            { key: 'name', header: 'Group', render: nameCell },
            { key: 'tags', header: 'Scope and type', render: (g) => <GroupTags g={g} /> },
            { key: 'desc', header: 'Description', render: (g) => g.description ?? '' },
          ]} />
      </Card>
      <Card title={`Nested groups (${m.nested.length})`}>
        <p className="muted small">Groups reached through other groups. These cannot be changed directly.</p>
        <DataTable rows={m.nested.map((n) => ({ ...n.group, via: n.via }))} rowKey={(g) => g.id} empty="No nested memberships."
          columns={[
            { key: 'name', header: 'Group', render: nameCell },
            { key: 'via', header: 'Through', render: (g) => g.via ?? '' },
            { key: 'tags', header: 'Scope and type', render: (g) => <GroupTags g={g} /> },
          ]} />
      </Card>
      <Card title="Primary group">
        {m.primary
          ? <div className="row gap wrap">{nameCell(m.primary)} <GroupTags g={m.primary} /> <Tag>Cannot be removed</Tag></div>
          : <span className="muted">Not available</span>}
        <Note>The primary group (usually Domain Users) comes from primaryGroupID and is not a normal membership.</Note>
      </Card>
    </>
  );
}
