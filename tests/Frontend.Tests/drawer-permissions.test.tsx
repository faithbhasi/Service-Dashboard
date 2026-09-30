import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { UserDrawer } from '../../src/Frontend/Pages/ad/UserDrawer';

let granted: string[] = [];

const detail = {
  user: {
    id: 'u1', dn: 'CN=Alice,OU=Staff,DC=x', ou: 'OU=Staff,DC=x', samAccountName: 'alice', userPrincipalName: 'alice@x', displayName: 'Alice', givenName: null, surname: null,
    email: 'alice@x.test', employeeId: null, title: null, department: null, office: null, phone: null, mobile: null, description: null, manager: null,
    enabled: true, lockedOut: true, accountExpiry: 'Never', accountExpiresUtc: null, passwordStatus: 'Expires', passwordExpiresUtc: null, passwordLastSetUtc: null,
    lastLogonUtc: null, createdUtc: null, changedUtc: null, resultantPso: null, userAccountControl: 512, uacFlags: [], primaryGroupId: 513, adminCount: false,
  },
  ouManageable: true, ouReason: null,
};
const groups = { direct: [{ id: 'g1', dn: 'CN=G1', ou: 'OU=G', name: 'GG-One', description: null, scope: 'Global', type: 'Security', managedBy: null, memberCount: 1, isProtected: false, isManageable: true, blockReason: null }], nested: [], primary: null };

vi.mock('../../src/Frontend/Services/api', async () => {
  const actual = await vi.importActual<typeof import('../../src/Frontend/Services/api')>('../../src/Frontend/Services/api');
  return { ...actual, get: vi.fn(async (path: string) => (path.includes('addable-groups') ? [] : path.endsWith('/groups') ? groups : detail)) };
});
vi.mock('../../src/Frontend/Hooks/AuthContext', () => ({
  useAuth: () => ({ can: (p: string) => granted.includes(p), canAny: (...p: string[]) => p.some((x) => granted.includes(x)) }),
}));
vi.mock('../../src/Frontend/Hooks/ShellContext', () => ({
  useShell: () => ({
    date: (d: string) => d, dateTime: (d: string) => d,
    shell: { actionPolicies: { mustChangePasswordDefault: true, generatedPasswordLength: 16, actions: {} } },
  }),
}));

const ALL = [
  'ad.users.read', 'ad.users.resetPassword', 'ad.users.unlock', 'ad.users.enable', 'ad.users.disable', 'ad.users.move',
  'ad.users.groups.add', 'ad.users.groups.remove', 'logs.read', 'settings.manage',
];

const show = async (tab = '') => {
  render(<MemoryRouter><UserDrawer id="u1" tab={tab} onTab={() => {}} onClose={() => {}} onChanged={() => {}} /></MemoryRouter>);
  await screen.findByText('Alice');
};
const tabNames = () => screen.getAllByRole('tab').map((t) => t.textContent);

describe('user drawer tabs follow permissions', () => {
  it('shows every tab, with Add to Groups first, to someone who can do everything', async () => {
    granted = ALL;
    await show();
    expect(tabNames()).toEqual(['Add to Groups', 'Group Memberships', 'Password Reset', 'Unlock Account', 'Enable / Disable', 'Move OU', 'Account Details', 'Activity History']);
  });

  it('shows only the tabs the role allows', async () => {
    granted = ['ad.users.read', 'ad.users.unlock', 'ad.users.groups.add', 'logs.read.own'];
    await show();
    expect(tabNames()).toEqual(['Add to Groups', 'Group Memberships', 'Unlock Account', 'Account Details', 'Activity History']);
  });

  it('read-only users only get memberships and details', async () => {
    granted = ['ad.users.read'];
    await show();
    expect(tabNames()).toEqual(['Group Memberships', 'Account Details']);
    expect(screen.queryByRole('button', { name: /Remove from/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument(); // no selection boxes without the remove permission
  });

  it('shows the enable/disable tab when either permission is held, and no Unlock or Move for a disabler', async () => {
    granted = ['ad.users.read', 'ad.users.disable'];
    await show();
    expect(tabNames()).toEqual(['Group Memberships', 'Enable / Disable', 'Account Details']);
  });

  it('offers remove-from-groups selection only with ad.users.groups.remove', async () => {
    granted = ['ad.users.read', 'ad.users.groups.remove'];
    await show('groups');
    expect(await screen.findByRole('button', { name: /Remove from/ })).toBeDisabled(); // nothing selected yet
    expect(screen.getByRole('checkbox', { name: 'Select GG-One' })).toBeInTheDocument();
  });

  it('shows "Validate only" to people who manage settings and hides it from everyone else', async () => {
    granted = ['ad.users.read', 'ad.users.unlock'];
    await show('unlock');
    expect(await screen.findByRole('button', { name: 'Unlock account' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Validate only' })).not.toBeInTheDocument();
  });

  it('shows "Validate only" when settings.manage is held', async () => {
    granted = ['ad.users.read', 'ad.users.unlock', 'settings.manage'];
    await show('unlock');
    expect(await screen.findByRole('button', { name: 'Validate only' })).toBeInTheDocument();
  });

  it('switches tabs when one is clicked', async () => {
    granted = ALL;
    const onTab = vi.fn();
    render(<MemoryRouter><UserDrawer id="u1" tab="" onTab={onTab} onClose={() => {}} onChanged={() => {}} /></MemoryRouter>);
    await screen.findByText('Alice');
    await userEvent.click(screen.getByRole('tab', { name: 'Password Reset' }));
    expect(onTab).toHaveBeenCalledWith('password');
  });
});
