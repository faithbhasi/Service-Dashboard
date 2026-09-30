import { OuCheckTree } from './OuCheckTree';
import { Note } from './ui';

export interface AdScope { userOus: string[] | null; computerOus: string[] | null; groups: string[] | null }
export interface ScopeOption { dn: string; label: string }
export interface ScopeOptions { userOus: ScopeOption[]; computerOus: ScopeOption[]; groups: ScopeOption[] }

export const noScope: AdScope = { userOus: null, computerOus: null, groups: null };
export const isLimited = (s: AdScope | undefined | null) => !!s && (s.userOus !== null || s.computerOus !== null || s.groups !== null);

type Key = keyof AdScope;
const SECTIONS: { key: Key; optionsKey: keyof ScopeOptions; title: string; what: string }[] = [
  { key: 'userOus', optionsKey: 'userOus', title: 'Users', what: 'OUs whose users this role can change' },
  { key: 'computerOus', optionsKey: 'computerOus', title: 'Computers', what: 'OUs whose computers this role can change' },
  { key: 'groups', optionsKey: 'groups', title: 'Groups', what: 'groups this role can add users to or remove them from' },
];

/**
 * Which OUs and groups a role may manage. The lists to choose from are the manageable OUs and groups in Settings > AD Integration,
 * which stay the ceiling: this can only narrow what is allowed there for people who hold the role.
 */
export function RoleScopeEditor({ value, onChange, options, readOnly }: {
  value: AdScope; onChange: (next: AdScope) => void; options: ScopeOptions; readOnly: boolean;
}) {
  const set = (key: Key, list: string[] | null) => onChange({ ...value, [key]: list });

  return (
    <fieldset className="scope-editor">
      <legend>What this role can manage in Active Directory</legend>
      <p className="muted small">
        Everything here is also limited by the manageable OUs and groups in Settings &gt; AD Integration, and protected objects can never be changed.
        Tick a section to limit this role, then tick the OUs (any level of the tree) or groups it may manage; a ticked OU includes everything below it. If two roles are given to one person, the person can manage what either allows.
      </p>
      {SECTIONS.map(({ key, optionsKey, title, what }) => {
        const all = options[optionsKey];
        const list = value[key];
        const limited = list !== null;
        return (
          <div key={key} className="scope-section">
            <label className="check">
              <input type="checkbox" checked={limited} disabled={readOnly} aria-label={`Limit ${title.toLowerCase()}`}
                onChange={(e) => set(key, e.target.checked ? all.map((o) => o.dn) : null)} />
              <strong>Limit {title.toLowerCase()}</strong>
              <span className="muted small"> {limited ? `Only the ${what} that are ticked below.` : 'No limit from this role.'}</span>
            </label>
            {limited && (key === 'groups' ? (
              <div className="scope-list list-check" role="group" aria-label={`${title} this role can manage`}>
                {all.length === 0 && <div className="empty">Nothing is on the manageable list in Settings &gt; AD Integration yet.</div>}
                {all.map((o) => (
                  <label key={o.dn} className="check" title={o.dn}>
                    <input type="checkbox" checked={list.some((d) => d.toLowerCase() === o.dn.toLowerCase())} disabled={readOnly}
                      onChange={(e) => set(key, e.target.checked ? [...list, o.dn] : list.filter((d) => d.toLowerCase() !== o.dn.toLowerCase()))} />
                    <span>{o.label}</span>
                  </label>
                ))}
              </div>
            ) : (
              <div className="scope-list" role="group" aria-label={`${title} this role can manage`}>
                <OuCheckTree kind={key === 'userOus' ? 'users' : 'computers'} label={title.toLowerCase()} selected={list} onChange={(next) => set(key, next)} readOnly={readOnly} />
              </div>
            ))}
            {limited && list.length === 0 && <Note kind="warning">Nothing is ticked, so this role cannot change any {title.toLowerCase()}.</Note>}
          </div>
        );
      })}
    </fieldset>
  );
}
