import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { GroupDrawer } from '../../src/Frontend/Pages/ad/GroupDrawer';

let granted: string[] = [];
const posts: { path: string; body: Record<string, unknown> }[] = [];
const gets: string[] = [];

const group = { id: 'g1', dn: 'CN=GG-Sales', ou: 'OU=G', name: 'GG-Sales', description: null, scope: 'Global', type: 'Security', managedBy: null, memberCount: 2, isProtected: false, isManageable: true, blockReason: null };
const members = { items: [
  { id: 'u1', kind: 'User', name: 'Alice', samAccountName: 'alice', email: 'a@x', dn: 'CN=Alice', enabled: true },
  { id: 'c1', kind: 'Computer', name: 'WS-1', samAccountName: null, email: null, dn: 'CN=WS-1', enabled: true },
], total: 2, page: 1, pageSize: 25, totalIsCapped: false };

vi.mock('../../src/Frontend/Services/api', async () => {
  const actual = await vi.importActual<typeof import('../../src/Frontend/Services/api')>('../../src/Frontend/Services/api');
  return {
    ...actual,
    get: vi.fn(async (path: string) => {
      gets.push(path);
      if (path.includes('addable-users')) return [
        { id: 'u2', name: 'Bob Jones', samAccountName: 'bob', email: 'b@x', enabled: true, alreadyMember: false },
        { id: 'u1', name: 'Alice', samAccountName: 'alice', email: 'a@x', enabled: true, alreadyMember: true },
      ];
      if (path.includes('/members')) return members;
      return group;
    }),
    post: vi.fn(async (path: string, body: Record<string, unknown>) => {
      posts.push({ path, body });
      return { results: [{ status: body.validateOnly ? 'Validated' : 'Success', message: 'The change was made.', correlationId: 'c-1', action: 'ad.user.groups.add', target: 'bob', errorCode: null, changes: [], checks: [], dryRun: !!body.validateOnly, groupName: 'GG-Sales' }] };
    }),
  };
});
vi.mock('../../src/Frontend/Hooks/AuthContext', () => ({ useAuth: () => ({ can: (p: string) => granted.includes(p), canAny: (...p: string[]) => p.some((x) => granted.includes(x)) }) }));
vi.mock('../../src/Frontend/Hooks/ShellContext', () => ({
  useShell: () => ({ date: (d: string) => d, dateTime: (d: string) => d, shell: { actionPolicies: { mustChangePasswordDefault: true, generatedPasswordLength: 16,
    actions: { addToGroups: { justificationRequired: false, justificationMinLength: 0, ticketRequired: false, ticketPattern: null, typedConfirmationRequired: false },
               removeFromGroups: { justificationRequired: false, justificationMinLength: 0, ticketRequired: false, ticketPattern: null, typedConfirmationRequired: false } } } } }),
}));

const open = async () => {
  render(<MemoryRouter><GroupDrawer id="g1" onClose={() => {}} /></MemoryRouter>);
  await screen.findByText('Alice');
};

beforeEach(() => { posts.length = 0; gets.length = 0; });

describe('adding and removing users from a group', () => {
  it('offers no add or remove controls without the permissions', async () => {
    granted = ['ad.groups.read'];
    await open();
    expect(screen.queryByText('Add users to this group')).not.toBeInTheDocument();
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
  });

  it('offers no controls on a group that is not manageable', async () => {
    granted = ['ad.groups.read', 'ad.users.groups.add', 'ad.users.groups.remove'];
    const { get } = await import('../../src/Frontend/Services/api');
    vi.mocked(get).mockImplementationOnce(async () => ({ ...group, isManageable: false, blockReason: 'Not on the manageable groups list.' }));
    await open();
    expect(screen.queryByText('Add users to this group')).not.toBeInTheDocument();
    expect(screen.getByText(/manageable groups list/)).toBeInTheDocument();
  });

  it('removes the ticked users, only lets users be ticked, and sends their ids', async () => {
    granted = ['ad.groups.read', 'ad.users.groups.remove'];
    const u = userEvent.setup();
    await open();
    expect(screen.getByRole('checkbox', { name: 'Select WS-1' })).toBeDisabled(); // a computer cannot be removed from here
    const button = screen.getByRole('button', { name: /^Remove/ });
    expect(button).toBeDisabled();
    await u.click(screen.getByRole('checkbox', { name: 'Select Alice' }));
    expect(screen.getByRole('button', { name: 'Remove 1 selected user' })).toBeEnabled();
    await u.click(screen.getByRole('button', { name: 'Remove 1 selected user' }));
    const dialog = screen.getByRole('dialog', { name: 'Remove users from group' });
    await u.click(within(dialog).getByRole('button', { name: 'Review change' }));
    await waitFor(() => expect(posts).toHaveLength(1));
    expect(posts[0].path).toBe('/modules/ad/groups/g1/members/remove');
    expect(posts[0].body).toMatchObject({ userIds: ['u1'], validateOnly: true });
    await u.click(await within(screen.getByRole('dialog', { name: 'Remove users from group' })).findByRole('button', { name: 'Remove' }));
    await waitFor(() => expect(posts).toHaveLength(2));
    expect(posts[1].body).toMatchObject({ userIds: ['u1'], validateOnly: false });
  });

  it('searches for users to add, marks existing members, and sends the ticked ids', async () => {
    granted = ['ad.groups.read', 'ad.users.groups.add'];
    const u = userEvent.setup();
    await open();
    expect(screen.getByText('Type a name to find users.')).toBeInTheDocument();
    await u.type(screen.getByLabelText('Search users to add'), 'bo');
    await screen.findByText('Bob Jones');
    expect(gets.some((g) => g.includes('/addable-users?q=bo'))).toBe(true);
    expect(screen.getByRole('checkbox', { name: /Alice/ })).toBeDisabled(); // already a member
    expect(screen.getByText('Already a member')).toBeInTheDocument();
    await u.click(screen.getByRole('checkbox', { name: /Bob Jones/ }));
    await u.click(screen.getByRole('button', { name: 'Add 1 selected user' }));
    const dialog = screen.getByRole('dialog', { name: 'Add users to group' });
    await u.click(within(dialog).getByRole('button', { name: 'Review change' }));
    await waitFor(() => expect(posts).toHaveLength(1));
    expect(posts[0].path).toBe('/modules/ad/groups/g1/members/add');
    expect(posts[0].body).toMatchObject({ userIds: ['u2'] });
  });
});
