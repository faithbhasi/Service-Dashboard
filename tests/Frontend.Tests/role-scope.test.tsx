import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { isLimited, noScope, RoleScopeEditor, type AdScope, type ScopeOptions } from '../../src/Frontend/Components/RoleScopeEditor';

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

  it('limiting a section ticks everything so you untick what must not be managed', async () => {
    const u = userEvent.setup();
    const seen: AdScope[] = [];
    render(<Harness onChange={(s) => seen.push(s)} />);
    await u.click(screen.getByLabelText('Limit users'));
    const list = screen.getByRole('group', { name: /Users this role can manage/ });
    expect(within(list).getByLabelText('Corp / Staff')).toBeChecked();
    expect(within(list).getByLabelText('Corp / Contractors')).toBeChecked();
    await u.click(within(list).getByLabelText('Corp / Staff'));
    expect(seen.at(-1)).toEqual({ userOus: ['OU=Contractors,DC=x'], computerOus: null, groups: null });
    expect(isLimited(seen.at(-1))).toBe(true);
  });

  it('each of users, computers and groups is limited on its own', async () => {
    const u = userEvent.setup();
    const seen: AdScope[] = [];
    render(<Harness onChange={(s) => seen.push(s)} />);
    await u.click(screen.getByLabelText('Limit groups'));
    await u.click(within(screen.getByRole('group', { name: /Groups this role can manage/ })).getByLabelText('GG-Finance'));
    expect(seen.at(-1)).toEqual({ userOus: null, computerOus: null, groups: ['CN=GG-Sales,DC=x'] });
    await u.click(screen.getByLabelText('Limit computers'));
    expect(seen.at(-1)?.computerOus).toEqual(['OU=Laptops,DC=x']);
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

  it('cannot be changed when read only', () => {
    render(<Harness start={{ userOus: ['OU=Staff,DC=x'], computerOus: null, groups: null }} readOnly />);
    expect(screen.getByLabelText('Limit users')).toBeDisabled();
    expect(screen.getByLabelText('Corp / Staff')).toBeDisabled();
  });
});
