import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { useShell } from '../Hooks/ShellContext';
import { routeFor, type AdGroup, type AdUser, type ObjectRef } from '../Services/adTypes';
import { Tag } from './ui';

/** "OU=Sales,OU=Staff,OU=Corp,DC=x,DC=y" -> "Corp / Staff / Sales" */
export function ouPath(dn: string): string {
  const parts = splitDn(dn).filter((p) => /^(OU|CN)=/i.test(p)).map((p) => p.slice(3).replace(/\\(.)/g, '$1'));
  return parts.reverse().join(' / ') || dn;
}

export function splitDn(dn: string): string[] {
  const out: string[] = [];
  let cur = '';
  for (let i = 0; i < dn.length; i++) {
    if (dn[i] === '\\') { cur += dn[i] + (dn[i + 1] ?? ''); i++; continue; }
    if (dn[i] === ',') { out.push(cur.trim()); cur = ''; continue; }
    cur += dn[i];
  }
  if (cur) out.push(cur.trim());
  return out;
}

export function Ou({ dn }: { dn: string }) {
  return <span title={dn}>{ouPath(dn)}</span>;
}

/** "Label: value" for the pop-up headers: a small grey label and a larger value. */
export function HeaderField({ label, children, end = false }: { label: string; children: ReactNode; end?: boolean }) {
  return (
    <span className={`hf ${end ? 'hf-end' : ''}`}>
      <span className="hf-label">{label}:</span> <span className="hf-value">{children}</span>
    </span>
  );
}

export function EnabledTag({ enabled }: { enabled: boolean }) {
  return <Tag kind={enabled ? 'success' : 'error'}>{enabled ? 'Enabled' : 'Disabled'}</Tag>;
}

export function LockedTag({ locked }: { locked: boolean }) {
  return locked ? <Tag kind="warning">Locked</Tag> : <span className="muted">No</span>;
}

/** A manager or managed-by value shown as a name that opens that object's drawer. */
export function ObjectLink({ value }: { value: ObjectRef | null }) {
  if (!value) return <span className="muted">Not set</span>;
  return <Link to={routeFor(value.kind, value.id)}>{value.name}</Link>;
}

export function Text({ value }: { value: string | null | undefined }): ReactNode {
  return value ? value : <span className="muted">Not set</span>;
}

export function useAdText() {
  const { date, dateTime } = useShell();
  return {
    accountExpiry: (u: AdUser): ReactNode =>
      u.accountExpiry === 'Never' ? 'Never expires'
        : u.accountExpiry === 'Expired' ? <Tag kind="error">Expired on {date(u.accountExpiresUtc)}</Tag>
          : `Expires on ${date(u.accountExpiresUtc)}`,
    accountExpiryShort: (u: AdUser): ReactNode =>
      u.accountExpiry === 'Never' ? <span className="muted">Never</span>
        : u.accountExpiry === 'Expired' ? <Tag kind="error">Expired</Tag> : `Expires ${date(u.accountExpiresUtc)}`,
    password: (u: AdUser): ReactNode => {
      switch (u.passwordStatus) {
        case 'NeverExpires': return 'Password never expires (flag set)';
        case 'MustChange': return 'User must change password at next sign-in';
        case 'Expired': return <Tag kind="error">Password expired{u.passwordExpiresUtc ? ` on ${date(u.passwordExpiresUtc)}` : ''}</Tag>;
        case 'Expires': return `Password expires on ${date(u.passwordExpiresUtc)}`;
        default: return <span className="muted">Not available</span>;
      }
    },
    passwordShort: (u: AdUser): ReactNode => {
      switch (u.passwordStatus) {
        case 'NeverExpires': return <span className="muted">Never</span>;
        case 'MustChange': return <Tag kind="warning">Must change</Tag>;
        case 'Expired': return <Tag kind="error">Expired</Tag>;
        case 'Expires': return date(u.passwordExpiresUtc);
        default: return <span className="muted">-</span>;
      }
    },
    approx: (iso: string | null) => (iso ? dateTime(iso) : <span className="muted">Never / unknown</span>),
  };
}

export function GroupTags({ g }: { g: AdGroup }) {
  return (
    <>
      <Tag>{g.scope}</Tag> <Tag>{g.type}</Tag> {g.isProtected && <Tag kind="warning" title={g.blockReason ?? undefined}>Protected</Tag>}
    </>
  );
}
