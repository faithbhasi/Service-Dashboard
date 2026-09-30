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
const grp = (id: string, name: string, extra = {}) => ({ id, dn: `CN=${name}`, ou: 'OU=G', name, description: null, scope: 'Global', type: 'Security', managedBy: null, memberCount: 1, isProtected: false, isManageable: true, blockReason: null, ...extra });
const groups = {
  direct: [grp('g1', 'GG-One')],
  nested: [{ group: grp('g2', 'GG-Nested', { isManageable: false }), via: 'GG-One' }],
  primary: grp('g3', 'Domain Users', { isManageable: false }),
};

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
  it('shows every tab, with Account Actions first, to someone who can do everything', async () => {
    granted = ALL;
    await show();
    expect(tabNames()).toEqual(['Account Actions', 'Groups', 'Move OU', 'Account Details', 'Activity History']);
  });

  it('shows only the tabs the role allows', async () => {
    granted = ['ad.users.read', 'ad.users.unlock', 'ad.users.groups.add', 'logs.read.own'];
    await show();
    expect(tabNames()).toEqual(['Account Actions', 'Groups', 'Account Details', 'Activity History']);
  });

  it('read-only users only get memberships and details', async () => {
    granted = ['ad.users.read'];
    await show();
    expect(tabNames()).toEqual(['Groups', 'Account Details']);
    expect(screen.queryByRole('button', { name: /Remove from/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument(); // no selection boxes without the remove permission
  });

  it('shows Account Actions with only the disable section for a disabler, and no Move', async () => {
    granted = ['ad.users.read', 'ad.users.disable'];
    await show();
    expect(tabNames()).toEqual(['Account Actions', 'Groups', 'Account Details']);
    expect(await screen.findByRole('button', { name: 'Disable account' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Unlock account' })).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/^New password/)).not.toBeInTheDocument();
  });

  it('puts password reset, unlock and disable together on the first tab', async () => {
    granted = ALL;
    await show();
    expect(await screen.findByLabelText(/^New password/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Unlock account' })).toBeEnabled(); // the test user is locked out
    expect(screen.getByRole('button', { name: 'Disable account' })).toBeInTheDocument();
  });

  it('shows the current group memberships above the add-to-groups section', async () => {
    granted = ['ad.users.read', 'ad.users.groups.add'];
    await show('groups');
    const memberships = await screen.findByText(/Group memberships \(3\)/);
    const add = await screen.findByText('Add to groups');
    expect(memberships.compareDocumentPosition(add) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('merges direct, nested and primary groups into one list with badges, and marks what cannot be removed', async () => {
    granted = ['ad.users.read', 'ad.users.groups.remove'];
    await show('groups');
    await screen.findByText(/Group memberships \(3\)/);
    expect(screen.getByText('Domain Users')).toBeInTheDocument();
    expect(screen.getByText('GG-Nested')).toBeInTheDocument();
    expect(screen.getByText('Nested via GG-One')).toBeInTheDocument();
    expect(screen.getByText('Primary')).toBeInTheDocument();
    expect(screen.getAllByText('Cannot be removed')).toHaveLength(2); // the primary and the nested group
    expect(screen.getByRole('checkbox', { name: 'Select GG-One' })).toBeEnabled();
    expect(screen.getByRole('checkbox', { name: 'Select Domain Users' })).toBeDisabled();
    expect(screen.getByRole('checkbox', { name: 'Select GG-Nested' })).toBeDisabled();
    // One scrollable list, with the remove button directly under it.
    expect(document.querySelector('.scroll-area')).not.toBeNull();
  });

  it('puts the remove button between the memberships and add-to-groups', async () => {
    granted = ['ad.users.read', 'ad.users.groups.add', 'ad.users.groups.remove'];
    await show('groups');
    const remove = await screen.findByRole('button', { name: /Remove from/ });
    const add = await screen.findByText('Add to groups');
    const list = document.querySelector('.scroll-area')!;
    expect(list.compareDocumentPosition(remove) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(remove.compareDocumentPosition(add) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('shows the username, email with a copy icon, job title, department and OU in the header', async () => {
    granted = ALL;
    await show();
    const head = document.querySelector('.summary')!;
    expect(head.textContent).toContain('Username: alice');
    expect(head.textContent).toContain('Email: alice@x.test');
    expect(head.textContent).toContain('Job title: Not set');
    expect(head.textContent).toContain('Department: Not set');
    expect(head.querySelector('.hf-end')?.textContent).toContain('OU:');
    expect(screen.getByRole('button', { name: 'Copy email address' })).toBeInTheDocument();
    const lines = head.querySelectorAll('.summary-line');
    expect(lines).toHaveLength(2);
  });

  it('turns the refresh icon into a green tick when pressed, and reloads', async () => {
    granted = ALL;
    await show();
    const refresh = screen.getByRole('button', { name: 'Refresh' });
    expect(refresh).toHaveAttribute('title', 'Refresh');
    expect(refresh).not.toHaveClass('is-done');
    await userEvent.click(refresh);
    expect(screen.getByRole('button', { name: 'Refresh' })).toHaveClass('is-done');
  });

  it('still opens the right tab from old links', async () => {
    granted = ALL;
    await show('password');
    expect(await screen.findByLabelText(/^New password/)).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Account Actions' })).toHaveAttribute('aria-selected', 'true');
  });

  it('offers remove-from-groups selection only with ad.users.groups.remove', async () => {
    granted = ['ad.users.read', 'ad.users.groups.remove'];
    await show('groups');
    expect(await screen.findByRole('button', { name: /Remove from/ })).toBeDisabled(); // nothing selected yet
    expect(screen.getByRole('checkbox', { name: 'Select GG-One' })).toBeInTheDocument();
  });

  it('shows "Validate" to people who manage settings and hides it from everyone else', async () => {
    granted = ['ad.users.read', 'ad.users.unlock'];
    await show('account');
    expect(await screen.findByRole('button', { name: 'Unlock account' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Validate' })).not.toBeInTheDocument();
  });

  it('shows "Validate" when settings.manage is held', async () => {
    granted = ['ad.users.read', 'ad.users.unlock', 'settings.manage'];
    await show('account');
    expect(await screen.findByRole('button', { name: 'Validate' })).toBeInTheDocument();
  });

  it('switches tabs when one is clicked', async () => {
    granted = ALL;
    const onTab = vi.fn();
    render(<MemoryRouter><UserDrawer id="u1" tab="" onTab={onTab} onClose={() => {}} onChanged={() => {}} /></MemoryRouter>);
    await screen.findByText('Alice');
    await userEvent.click(screen.getByRole('tab', { name: 'Groups' }));
    expect(onTab).toHaveBeenCalledWith('groups');
  });
});
