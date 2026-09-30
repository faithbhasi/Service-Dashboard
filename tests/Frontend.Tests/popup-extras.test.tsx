import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { PasswordResetPanel } from '../../src/Frontend/Pages/ad/ActionPanels';
import { ComputerDrawer } from '../../src/Frontend/Pages/ad/ComputerDrawer';
import { UserDrawer } from '../../src/Frontend/Pages/ad/UserDrawer';
import { UsersPage } from '../../src/Frontend/Pages/ad/UsersPage';
import { Pagination } from '../../src/Frontend/Components/ui';
import type { AdUser } from '../../src/Frontend/Services/adTypes';

const gets: string[] = [];
const policy = { justificationRequired: true, justificationMinLength: 10, ticketRequired: true, ticketPattern: null, typedConfirmationRequired: true };
const user = {
  id: 'u1', dn: 'CN=Alice,OU=Staff,DC=x', ou: 'OU=Staff,DC=x', samAccountName: 'alice', userPrincipalName: 'alice@x', displayName: 'Alice', givenName: null, surname: null,
  email: 'alice@x.test', employeeId: null, title: 'Engineer', department: 'IT', office: null, phone: null, mobile: null, description: null, manager: null,
  enabled: true, lockedOut: false, accountExpiry: 'Never', accountExpiresUtc: null, passwordStatus: 'Expires', passwordExpiresUtc: null, passwordLastSetUtc: null,
  lastLogonUtc: null, createdUtc: null, changedUtc: null, resultantPso: null, userAccountControl: 512, uacFlags: [], primaryGroupId: 513, adminCount: false,
} as AdUser;
const computer = { id: 'c1', dn: 'CN=WS-1,OU=W,DC=x', ou: 'OU=W,DC=x', name: 'WS-1', dnsHostName: 'ws-1.corp.test', enabled: true, operatingSystem: 'Windows 11 Pro', osVersion: null, lastLogonUtc: null, passwordLastSetUtc: null, managedBy: null, description: null, changedUtc: null, createdUtc: null, lastLoggedInUser: null };

vi.mock('../../src/Frontend/Services/api', async () => {
  const actual = await vi.importActual<typeof import('../../src/Frontend/Services/api')>('../../src/Frontend/Services/api');
  return {
    ...actual,
    post: vi.fn(async () => ({})),
    get: vi.fn(async (path: string) => {
      gets.push(path);
      if (path.includes('/computers/c1')) return { computer, ouManageable: true, ouReason: null };
      if (path.includes('/users/u1')) return { user, ouManageable: true, ouReason: null };
      return { items: [], total: 120, page: 1, pageSize: 25, totalIsCapped: false };
    }),
  };
});
vi.mock('../../src/Frontend/Hooks/AuthContext', () => ({ useAuth: () => ({ can: () => true, canAny: () => true, me: { preferences: {} } }) }));
vi.mock('../../src/Frontend/Hooks/ShellContext', () => ({
  useShell: () => ({ date: (d: string) => d, dateTime: (d: string) => d, moduleEnabled: () => true,
    shell: { modules: [], actionPolicies: { mustChangePasswordDefault: true, generatedPasswordLength: 16, actions: { resetPassword: policy } } } }),
}));

beforeEach(() => { gets.length = 0; });

describe('required fields on the confirmation screen', () => {
  it('marks justification, ticket and typed confirmation with a red asterisk and aria-required', async () => {
    const u = userEvent.setup();
    render(<PasswordResetPanel user={user} onChanged={() => {}} />);
    await u.type(screen.getByLabelText(/^New password/), 'Abcdef1!xyz');
    await u.type(screen.getByLabelText(/^Confirm password/), 'Abcdef1!xyz');
    await u.click(screen.getByRole('button', { name: 'Reset password' }));
    const dialog = screen.getByRole('dialog');
    expect(dialog.querySelectorAll('label .req')).toHaveLength(3);
    for (const l of [/Justification/, /Ticket number/, /Type alice to confirm/]) {
      const label = within(dialog).getByLabelText(l);
      expect(label).toHaveAttribute('aria-required', 'true');
      expect(label.closest('.field')!.querySelector('.req')!.textContent).toContain('*');
    }
    expect(within(dialog).getByText(/are required/)).toBeInTheDocument();
  });

  it('shows the copy icon next to the name to type, which turns into a green tick and back', async () => {
    const u = userEvent.setup();
    render(<PasswordResetPanel user={user} onChanged={() => {}} />);
    await u.type(screen.getByLabelText(/^New password/), 'Abcdef1!xyz');
    await u.type(screen.getByLabelText(/^Confirm password/), 'Abcdef1!xyz');
    await u.click(screen.getByRole('button', { name: 'Reset password' }));
    const copy = within(screen.getByRole('dialog')).getByRole('button', { name: 'Copy alice' });
    expect(copy).not.toHaveClass('is-done');
    await u.click(copy);
    expect(await navigator.clipboard.readText()).toBe('alice');
    expect(screen.getByRole('button', { name: 'Copy alice' })).toHaveClass('is-done');
    await waitFor(() => expect(screen.getByRole('button', { name: 'Copy alice' })).not.toHaveClass('is-done'), { timeout: 3000 }); // back to the copy icon
  });
});

describe('copy icons in the pop-up headers', () => {
  it('has copy icons for the username and email on a user, and the computer name and FQDN on a computer', async () => {
    const u = userEvent.setup();
    const { unmount } = render(<MemoryRouter><UserDrawer id="u1" tab="" onTab={() => {}} onClose={() => {}} onChanged={() => {}} /></MemoryRouter>);
    await screen.findByText('Alice');
    await u.click(screen.getByRole('button', { name: 'Copy username' }));
    expect(await navigator.clipboard.readText()).toBe('alice');
    expect(screen.getByRole('button', { name: 'Copy username' })).toHaveClass('is-done');
    await u.click(screen.getByRole('button', { name: 'Copy email address' }));
    expect(await navigator.clipboard.readText()).toBe('alice@x.test');
    unmount();

    render(<MemoryRouter><ComputerDrawer id="c1" tab="" onTab={() => {}} onClose={() => {}} onChanged={() => {}} /></MemoryRouter>);
    await screen.findByText('Windows 11 Pro', { selector: '.hf-value' });
    await u.click(screen.getByRole('button', { name: 'Copy computer name' }));
    expect(await navigator.clipboard.readText()).toBe('WS-1');
    await u.click(screen.getByRole('button', { name: 'Copy full computer name (FQDN)' }));
    expect(await navigator.clipboard.readText()).toBe('ws-1.corp.test');
  });
});

describe('rows per page', () => {
  it('offers 25, 50 and 100 at the bottom right and reports the choice', async () => {
    const u = userEvent.setup();
    const onSize = vi.fn();
    render(<Pagination page={1} pageSize={25} total={120} onPage={() => {}} onPageSize={onSize} />);
    const select = screen.getByLabelText('Rows per page') as HTMLSelectElement;
    expect(Array.from(select.options).map((o) => o.value)).toEqual(['25', '50', '100']);
    await u.selectOptions(select, '50');
    expect(onSize).toHaveBeenCalledWith(50);
    expect(screen.getByText('Page 1 of 5')).toBeInTheDocument();
  });

  it('has no selector unless the list supports it', () => {
    render(<Pagination page={1} pageSize={10} total={30} onPage={() => {}} />);
    expect(screen.queryByLabelText('Rows per page')).not.toBeInTheDocument();
  });

  it('the users list asks the server for the chosen page size and goes back to page 1', async () => {
    const u = userEvent.setup();
    render(<MemoryRouter initialEntries={['/ad/users?page=3']}><UsersPage /></MemoryRouter>);
    await screen.findByText('Rows per page');
    expect(gets.at(-1)).toContain('pageSize=25');
    await u.selectOptions(screen.getByLabelText('Rows per page'), '100');
    await waitFor(() => expect(gets.at(-1)).toContain('pageSize=100'));
    expect(gets.at(-1)).toContain('page=1');
  });
});
