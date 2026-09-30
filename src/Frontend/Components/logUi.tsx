import { Tag } from './ui';

const LABELS: Record<string, string> = {
  'logon.signin': 'Signed in', 'logon.signout': 'Signed out', 'logon.denied': 'Sign-in denied', 'logon.failed': 'Sign-in failed',
  'page.view': 'Opened page', 'api.access': 'API request',
  'ad.user.resetPassword': 'Reset password', 'ad.user.unlock': 'Unlock account', 'ad.user.enable': 'Enable account', 'ad.user.disable': 'Disable account',
  'ad.user.move': 'Move user', 'ad.user.groups.add': 'Add to group', 'ad.user.groups.remove': 'Remove from group',
  'ad.computer.enable': 'Enable computer', 'ad.computer.disable': 'Disable computer', 'ad.computer.move': 'Move computer',
  'ad.groups.member.export': 'Export group members', 'logs.export': 'Export logs', 'admin.users.export': 'Export app users',
  'admin.role.create': 'Create role', 'admin.role.clone': 'Clone role', 'admin.role.update': 'Edit role', 'admin.role.delete': 'Delete role',
  'admin.user.enable': 'Enable app access', 'admin.user.disable': 'Disable app access', 'admin.user.roles': 'Change user roles',
  'admin.mapping.create': 'Add group mapping', 'admin.mapping.delete': 'Remove group mapping',
  'settings.general.update': 'Change general settings', 'settings.ad.update': 'Change AD settings', 'settings.modules.update': 'Change modules',
  'settings.actionPolicies.update': 'Change action policies', 'settings.personalization.update': 'Change personalization',
  'settings.personalization.logo': 'Change logo', 'settings.personalization.reset': 'Reset personalization', 'settings.ad.testConnection': 'Test AD connection',
  'maintenance.backup': 'Database backup', 'maintenance.auditRetention': 'Audit retention',
};

export const actionLabel = (a: string) => LABELS[a] ?? a;

export function ResultTag({ result }: { result: string }) {
  const kind = result === 'Success' ? 'success' : result === 'Failure' ? 'error' : result === 'Denied' ? 'warning' : 'info';
  return <Tag kind={kind}>{result}</Tag>;
}

/** A short browser name from a user-agent string. */
export function browserName(ua: string | null): string {
  if (!ua) return '';
  const m = /(Edg|Chrome|Firefox|Safari|OPR)\/([\d.]+)/.exec(ua);
  const name = m ? ({ Edg: 'Edge', OPR: 'Opera' } as Record<string, string>)[m[1]] ?? m[1] : ua.slice(0, 30);
  return m ? `${name} ${m[2].split('.')[0]}` : name;
}

export interface LogRow {
  id: number; timeUtc: string; category: string; userId: string | null; userName: string | null; action: string; module: string | null;
  target: string | null; targetId: string | null; previousValue: string | null; newValue: string | null; result: string; error: string | null;
  justification: string | null; ticketNumber: string | null; ipAddress: string | null; userAgent: string | null; correlationId: string | null;
}
