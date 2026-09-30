import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { ComputersPage } from '../../src/Frontend/Pages/ad/ComputersPage';
import { UsersPage } from '../../src/Frontend/Pages/ad/UsersPage';

const gets: string[] = [];
vi.mock('../../src/Frontend/Services/api', async () => {
  const actual = await vi.importActual<typeof import('../../src/Frontend/Services/api')>('../../src/Frontend/Services/api');
  return { ...actual, get: vi.fn(async (path: string) => { gets.push(path); return { items: [], total: 0, page: 1, pageSize: 25, totalIsCapped: false }; }) };
});
vi.mock('../../src/Frontend/Hooks/AuthContext', () => ({ useAuth: () => ({ can: () => true, canAny: () => true, me: { preferences: {} } }) }));
vi.mock('../../src/Frontend/Hooks/ShellContext', () => ({ useShell: () => ({ date: (d: string) => d, dateTime: (d: string) => d, moduleEnabled: () => true, shell: { modules: [] } }) }));

beforeEach(() => { gets.length = 0; });

describe('user filters', () => {
  it('filters by department and job title and can clear them', async () => {
    const u = userEvent.setup();
    render(<MemoryRouter initialEntries={['/ad/users']}><UsersPage /></MemoryRouter>);
    await u.type(screen.getByLabelText('Filter by department'), 'Sales');
    await waitFor(() => expect(gets.some((g) => g.includes('department=Sales'))).toBe(true));
    await u.type(screen.getByLabelText('Filter by job title'), 'Director');
    await waitFor(() => expect(gets.some((g) => g.includes('department=Sales') && g.includes('title=Director'))).toBe(true));
    await u.click(screen.getByRole('button', { name: 'Clear filters' }));
    await waitFor(() => expect(gets.at(-1)).not.toContain('department='));
    expect(gets.at(-1)).not.toContain('title=');
    expect((screen.getByLabelText('Filter by department') as HTMLInputElement).value).toBe('');
  });
});

describe('computer filters', () => {
  it('filters by operating system', async () => {
    const u = userEvent.setup();
    render(<MemoryRouter initialEntries={['/ad/computers']}><ComputersPage /></MemoryRouter>);
    const os = screen.getByLabelText('Filter by operating system');
    expect(Array.from((os as HTMLSelectElement).options).map((o) => o.text)).toEqual(['All operating systems', 'Windows 11', 'Windows 10', 'Windows Server', 'macOS', 'Linux']);
    await u.selectOptions(os, 'windowsserver');
    await waitFor(() => expect(gets.some((g) => g.includes('os=windowsserver'))).toBe(true));
    await u.selectOptions(os, '');
    await waitFor(() => expect(gets.at(-1)).not.toContain('os='));
  });
});
