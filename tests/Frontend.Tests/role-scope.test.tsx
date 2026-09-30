import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { isUnderOrEqual } from '../../src/Frontend/Components/OuCheckTree';
import { isLimited, noScope, RoleScopeEditor, type AdScope, type ScopeOptions } from '../../src/Frontend/Components/RoleScopeEditor';

const tree: Record<string, { dn: string; name: string; hasChildren: boolean; selectable: boolean; reason: string | null }[]> = {
  '': [{ dn: 'OU=Corp,DC=x', name: 'Corp', hasChildren: true, selectable: false, reason: 'Not on the manageable OU list.' }],
  'OU=Corp,DC=x': [
    { dn: 'OU=Staff,OU=Corp,DC=x', name: 'Staff', hasChildren: true, selectable: true, reason: null },
    { dn: 'OU=Servers,OU=Corp,DC=x', name: 'Servers', hasChildren: false, selectable: false, reason: 'Not on the manageable OU list.' },
  ],
  'OU=Staff,OU=Corp,DC=x': [{ dn: 'OU=Sales,OU=Staff,OU=Corp,DC=x', name: 'Sales', hasChildren: false, selectable: true, reason: null }],
};
const urls: string[] = [];
vi.mock('../../src/Frontend/Services/api', async () => {
  const actual = await vi.importActual<typeof import('../../src/Frontend/Services/api')>('../../src/Frontend/Services/api');
  return { ...actual, get: vi.fn(async (path: string) => {
    urls.push(path);
    const parent = new URLSearchParams(path.split('?')[1]).get('parent') ?? '';
    return tree[parent] ?? [];
  }) };
});

const options: ScopeOptions = {
  userOus: [{ dn: 'OU=Staff,DC=x', label: 'Corp / Staff' }, { dn: 'OU=Contractors,DC=x', label: 'Corp / Contractors' }],
  computerOus: [{ dn: 'OU=Laptops,DC=x', label: 'Corp / Laptops' }],
  groups: [{ dn: 'CN=GG-Sales,DC=x', label: 'GG-Sales' }, { dn: 'CN=GG-Finance,DC=x', label: 'GG-Finance' }],
};

function Harness({ start = noScope, readOnly = false, onChange }: { start?: AdScope; readOnly?: boolean; onChange?: (s: AdScope) => void }) {
  const [v, setV] = useState(start);
  return <RoleScopeEditor value={v} options={options} readOnly={readOnly} onChange={(n) => { setV(n); onChange?.(n); }} />;
}

describe('role scope editor', () => {
  it('starts with no limit and shows no lists', () => {
    render(<Harness />);
    expect(screen.getByLabelText('Limit users')).not.toBeChecked();
    expect(screen.queryByRole('group', { name: /Users this role can manage/ })).not.toBeInTheDocument();
    expect(isLimited(noScope)).toBe(false);
  });

  it('limiting users starts with the manageable OUs ticked, shown as chips, and untick removes one', async () => {
    const u = userEvent.setup();
    const seen: AdScope[] = [];
    render(<Harness onChange={(s) => seen.push(s)} />);
    await u.click(screen.getByLabelText('Limit users'));
    expect(seen.at(-1)?.userOus).toEqual(['OU=Staff,DC=x', 'OU=Contractors,DC=x']);
    const chips = screen.getByLabelText('Selected users OUs');
    expect(within(chips).getByText('Staff')).toBeInTheDocument();
    await u.click(within(chips).getByRole('button', { name: 'Remove Staff' }));
    expect(seen.at(-1)?.userOus).toEqual(['OU=Contractors,DC=x']);
    expect(isLimited(seen.at(-1))).toBe(true);
  });

  it('shows the whole OU tree, lets every allowed OU be ticked at any depth, and greys out the rest', async () => {
    const u = userEvent.setup();
    const seen: AdScope[] = [];
    render(<Harness start={{ userOus: [], computerOus: null, groups: null }} onChange={(s) => seen.push(s)} />);
    const treeEl = await screen.findByRole('tree', { name: /users OU tree/ });
    const corp = await within(treeEl).findByLabelText('Corp');
    expect(corp).toBeDisabled(); // visible, not allowed to be chosen
    await u.click(within(treeEl).getByRole('button', { name: 'Expand Corp' }));
    expect(await within(treeEl).findByLabelText('Servers')).toBeDisabled();
    expect(within(treeEl).getAllByText('Not allowed').length).toBeGreaterThanOrEqual(2);
    await u.click(within(treeEl).getByRole('button', { name: 'Expand Staff' }));
    await u.click(await within(treeEl).findByLabelText('Sales')); // a sub-OU
    expect(seen.at(-1)?.userOus).toEqual(['OU=Sales,OU=Staff,OU=Corp,DC=x']);
  });

  it('ticking an OU covers what is below it (shown as included) and replaces the narrower entries', async () => {
    const u = userEvent.setup();
    const seen: AdScope[] = [];
    render(<Harness start={{ userOus: ['OU=Sales,OU=Staff,OU=Corp,DC=x'], computerOus: null, groups: null }} onChange={(s) => seen.push(s)} />);
    const treeEl = await screen.findByRole('tree', { name: /users OU tree/ });
    await u.click(await within(treeEl).findByRole('button', { name: 'Expand Corp' }));
    await u.click(await within(treeEl).findByLabelText('Staff'));
    expect(seen.at(-1)?.userOus).toEqual(['OU=Staff,OU=Corp,DC=x']);
    await u.click(within(treeEl).getByRole('button', { name: 'Expand Staff' }));
    const sales = await within(treeEl).findByLabelText('Sales');
    expect(sales).toBeChecked();
    expect(sales).toBeDisabled();
    expect(within(treeEl).getByText('included')).toBeInTheDocument();
  });

  it('computers and groups are limited on their own', async () => {
    const u = userEvent.setup();
    const seen: AdScope[] = [];
    render(<Harness onChange={(s) => seen.push(s)} />);
    await u.click(screen.getByLabelText('Limit groups'));
    await u.click(within(screen.getByRole('group', { name: /Groups this role can manage/ })).getByLabelText('GG-Finance'));
    expect(seen.at(-1)).toEqual({ userOus: null, computerOus: null, groups: ['CN=GG-Sales,DC=x'] });
    await u.click(screen.getByLabelText('Limit computers'));
    expect(seen.at(-1)?.computerOus).toEqual(['OU=Laptops,DC=x']);
    expect(await screen.findByRole('tree', { name: /computers OU tree/ })).toBeInTheDocument();
  });

  it('warns when nothing is ticked, and going back to "no limit" clears the list', async () => {
    const u = userEvent.setup();
    const seen: AdScope[] = [];
    render(<Harness start={{ userOus: [], computerOus: null, groups: null }} onChange={(s) => seen.push(s)} />);
    expect(screen.getByText(/cannot change any users/)).toBeInTheDocument();
    await u.click(screen.getByLabelText('Limit users'));
    expect(seen.at(-1)?.userOus).toBeNull();
    expect(screen.queryByText(/cannot change any users/)).not.toBeInTheDocument();
  });

  it('cannot be changed when read only', async () => {
    render(<Harness start={{ userOus: ['OU=Staff,OU=Corp,DC=x'], computerOus: null, groups: null }} readOnly />);
    expect(screen.getByLabelText('Limit users')).toBeDisabled();
    expect(screen.queryByRole('button', { name: /^Remove/ })).not.toBeInTheDocument();
    const treeEl = await screen.findByRole('tree');
    await userEvent.click(within(treeEl).getByRole('button', { name: 'Expand Corp' }));
    expect(await within(treeEl).findByLabelText('Staff')).toBeDisabled();
  });
});

describe('OU tree loading', () => {
  it('shows an error when opening an OU fails and tries again the next time', async () => {
    const u = userEvent.setup();
    const { get } = await import('../../src/Frontend/Services/api');
    render(<Harness start={{ userOus: [], computerOus: null, groups: null }} />);
    const treeEl = await screen.findByRole('tree', { name: /users OU tree/ });
    await within(treeEl).findByLabelText('Corp');
    vi.mocked(get).mockRejectedValueOnce(new Error('boom'));
    await u.click(within(treeEl).getByRole('button', { name: 'Expand Corp' }));
    expect(await within(treeEl).findByText(/Could not load this OU/)).toBeInTheDocument();
    await u.click(within(treeEl).getByRole('button', { name: 'Expand Corp' })); // the same click fetches again
    expect(await within(treeEl).findByLabelText('Staff')).toBeInTheDocument();
    expect(within(treeEl).queryByText(/Could not load this OU/)).not.toBeInTheDocument();
  });
});

describe('OU matching', () => {
  it('treats an OU as inside another only when it is the same or below it', () => {
    expect(isUnderOrEqual('OU=Sales,OU=Staff,DC=x', 'OU=Staff,DC=x')).toBe(true);
    expect(isUnderOrEqual('ou=staff, dc=x', 'OU=Staff,DC=x')).toBe(true);
    expect(isUnderOrEqual('OU=Staff,DC=x', 'OU=Sales,OU=Staff,DC=x')).toBe(false);
    expect(isUnderOrEqual('OU=Contractors,DC=x', 'OU=Staff,DC=x')).toBe(false);
    expect(isUnderOrEqual('OU=XStaff,DC=x', 'OU=Staff,DC=x')).toBe(false);
    expect(isUnderOrEqual('OU = Sales , OU=Staff,DC=x', 'OU=Staff,DC=x')).toBe(true); // spaces around = and , do not matter
  });
});
