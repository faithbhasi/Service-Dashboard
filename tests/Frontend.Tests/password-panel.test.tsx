import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PasswordResetPanel } from '../../src/Frontend/Pages/ad/ActionPanels';
import { generatePassword } from '../../src/Frontend/Services/password';
import type { AdUser } from '../../src/Frontend/Services/adTypes';

const calls: { path: string; body: Record<string, unknown> }[] = [];

vi.mock('../../src/Frontend/Services/api', async () => {
  const actual = await vi.importActual<typeof import('../../src/Frontend/Services/api')>('../../src/Frontend/Services/api');
  return {
    ...actual,
    post: vi.fn(async (path: string, body: Record<string, unknown>) => {
      calls.push({ path, body });
      const base = { correlationId: 'corr-123', action: 'ad.user.resetPassword', target: 'dave', errorCode: null, changes: [{ field: 'Password', from: 'x', to: 'y' }], checks: [{ name: 'Target found', passed: true, detail: null }] };
      return body.validateOnly
        ? { ...base, status: 'Validated', message: 'Validated. No change was made.', dryRun: true }
        : { ...base, status: 'Success', message: 'The change was made.', dryRun: false };
    }),
  };
});
vi.mock('../../src/Frontend/Hooks/AuthContext', () => ({ useAuth: () => ({ can: () => true, canAny: () => true }) }));
vi.mock('../../src/Frontend/Hooks/ShellContext', () => ({
  useShell: () => ({
    shell: {
      actionPolicies: {
        mustChangePasswordDefault: true, generatedPasswordLength: 16,
        actions: { resetPassword: { justificationRequired: true, justificationMinLength: 10, ticketRequired: false, ticketPattern: null, typedConfirmationRequired: true } },
      },
    },
  }),
}));

const user: AdUser = {
  id: '11111111-1111-1111-1111-111111111111', dn: 'CN=Dave,OU=Staff,DC=x', ou: 'OU=Staff,DC=x', samAccountName: 'dave', userPrincipalName: null, displayName: 'Dave',
  givenName: null, surname: null, email: null, employeeId: null, title: null, department: null, office: null, phone: null, mobile: null, description: null,
  manager: null, enabled: true, lockedOut: false, accountExpiry: 'Never', accountExpiresUtc: null, passwordStatus: 'Expires', passwordExpiresUtc: null,
  passwordLastSetUtc: null, lastLogonUtc: null, createdUtc: null, changedUtc: null, resultantPso: null, userAccountControl: 512, uacFlags: [], primaryGroupId: 513, adminCount: false,
};

const SECRET = 'Tr0ub4dor&3-Unique';
const field = (label: RegExp) => screen.getByLabelText(label) as HTMLInputElement;

async function fillAndOpenDialog(u: ReturnType<typeof userEvent.setup>) {
  await u.type(field(/^New password/), SECRET);
  await u.type(field(/^Confirm password/), SECRET);
  await u.click(screen.getByRole('button', { name: 'Reset password' }));
  return screen.getByRole('dialog');
}

beforeEach(() => { calls.length = 0; });

describe('password reset panel', () => {
  it('will not enable the reset button until both fields match, and shows a warning first', async () => {
    const u = userEvent.setup();
    render(<PasswordResetPanel user={user} onChanged={() => {}} />);
    expect(screen.getByText(/may interrupt access/i)).toBeInTheDocument();
    const button = screen.getByRole('button', { name: 'Reset password' });
    expect(button).toBeDisabled();
    await u.type(field(/^New password/), 'abc');
    await u.type(field(/^Confirm password/), 'abd');
    expect(screen.getByRole('alert')).toHaveTextContent('do not match');
    expect(button).toBeDisabled();
  });

  it('keeps the password out of the dry run, sends it only on confirm, and clears both fields afterwards', async () => {
    const u = userEvent.setup();
    const changed = vi.fn();
    render(<PasswordResetPanel user={user} onChanged={changed} />);
    const dialog = await fillAndOpenDialog(u);

    await u.type(within(dialog).getByLabelText(/Justification/), 'Verified by phone, ticket approved');
    await u.type(within(dialog).getByLabelText(/Type dave to confirm/), 'dave');
    await u.click(within(dialog).getByRole('button', { name: 'Review change' }));

    await screen.findByText('Dry-run result');
    expect(calls).toHaveLength(1);
    expect(calls[0].body.validateOnly).toBe(true);
    expect(JSON.stringify(calls[0].body)).not.toContain(SECRET); // the dry run never carries the password

    await u.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Reset password' }));
    await screen.findByText('The change was made.');
    expect(calls[1].body.validateOnly).toBe(false);
    expect(calls[1].body.newPassword).toBe(SECRET);
    expect(calls[1].body.mustChangeAtNextSignIn).toBe(true);

    await u.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Close' }));
    await waitFor(() => expect(field(/^New password/).value).toBe(''));
    expect(field(/^Confirm password/).value).toBe('');
    expect(changed).toHaveBeenCalled();
  });

  it('clears the password fields when the dialog is cancelled', async () => {
    const u = userEvent.setup();
    render(<PasswordResetPanel user={user} onChanged={() => {}} />);
    const dialog = await fillAndOpenDialog(u);
    await u.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(field(/^New password/).value).toBe('');
    expect(field(/^Confirm password/).value).toBe('');
    expect(calls).toHaveLength(0);
  });

  it('requires the justification and the typed name before the change can be reviewed', async () => {
    const u = userEvent.setup();
    render(<PasswordResetPanel user={user} onChanged={() => {}} />);
    const dialog = await fillAndOpenDialog(u);
    const review = within(dialog).getByRole('button', { name: 'Review change' });
    expect(review).toBeDisabled();
    await u.type(within(dialog).getByLabelText(/Justification/), 'Verified by phone, ticket approved');
    expect(review).toBeDisabled(); // still needs the typed confirmation
    await u.type(within(dialog).getByLabelText(/Type dave to confirm/), 'dave');
    expect(review).toBeEnabled();
  });

  it('generates a strong password on request', async () => {
    const u = userEvent.setup();
    render(<PasswordResetPanel user={user} onChanged={() => {}} />);
    await u.click(screen.getByRole('button', { name: /Generate secure password/ }));
    const pw = field(/^New password/).value;
    expect(pw).toHaveLength(16);
    expect(field(/^Confirm password/).value).toBe(pw);
  });
});

describe('password generator', () => {
  it('honours the length and includes every character class', () => {
    for (let i = 0; i < 50; i++) {
      const p = generatePassword(20);
      expect(p).toHaveLength(20);
      expect(p).toMatch(/[a-z]/);
      expect(p).toMatch(/[A-Z]/);
      expect(p).toMatch(/[0-9]/);
      expect(p).toMatch(/[^A-Za-z0-9]/);
    }
  });
  it('does not repeat', () => {
    expect(new Set(Array.from({ length: 200 }, () => generatePassword(16))).size).toBe(200);
  });
});
