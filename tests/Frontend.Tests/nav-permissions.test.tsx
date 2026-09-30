import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { Nav } from '../../src/Frontend/Layouts/Nav';

let granted: string[] = [];

vi.mock('../../src/Frontend/Hooks/AuthContext', () => ({
  useAuth: () => ({ canAny: (...p: string[]) => p.some((x) => granted.includes(x)), can: (p: string) => granted.includes(p) }),
}));
vi.mock('../../src/Frontend/Hooks/ShellContext', () => ({
  useShell: () => ({
    moduleEnabled: () => true,
    shell: { modules: [{ id: 'okta', status: 'Coming Soon', enabled: false }] },
  }),
}));

const renderNav = () => render(<MemoryRouter><Nav collapsed={false} /></MemoryRouter>);

describe('navigation is driven by permissions', () => {
  it('shows only Home and the disabled Okta entry to a user with no permissions', () => {
    granted = [];
    renderNav();
    expect(screen.getByText('Home')).toBeInTheDocument();
    expect(screen.getByText('Okta')).toBeInTheDocument();
    expect(screen.getByText('Coming Soon')).toBeInTheDocument();
    expect(screen.queryByText('Users')).not.toBeInTheDocument();
    expect(screen.queryByText('Settings')).not.toBeInTheDocument();
    expect(screen.queryByText('Activity and Logs')).not.toBeInTheDocument();
    expect(screen.queryByText('Users and Groups')).not.toBeInTheDocument();
  });

  it('shows Active Directory entries and sections that the permissions allow', () => {
    granted = ['ad.users.read', 'ad.groups.read', 'logs.read.own'];
    renderNav();
    expect(screen.getByRole('link', { name: /Users/ })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Groups/ })).toBeInTheDocument();
    expect(screen.queryByText('Read-Only')).not.toBeInTheDocument(); // groups can now have members added and removed
    expect(screen.queryByRole('link', { name: /Computers/ })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Activity and Logs/ })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /^Settings/ })).not.toBeInTheDocument();
  });

  it('shows Settings and Users and Groups to those who manage them', () => {
    granted = ['settings.read', 'admin.roles.manage'];
    renderNav();
    expect(screen.getByRole('link', { name: /^Settings/ })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Users and Groups/ })).toBeInTheDocument();
  });

  it('keeps Activity and Logs, Users and Groups and Settings together at the bottom', () => {
    granted = ['ad.users.read', 'logs.read', 'admin.roles.manage', 'settings.read'];
    const { container } = renderNav();
    const bottom = container.querySelector('.nav-bottom') as HTMLElement;
    expect(bottom).not.toBeNull();
    expect([...bottom.querySelectorAll('a')].map((a) => a.textContent)).toEqual(['Activity and Logs', 'Users and Groups', 'Settings']);
    expect(bottom.querySelector('a[href="/ad/users"]')).toBeNull();
    expect(container.querySelector('.nav')!.lastElementChild).toBe(bottom);
  });

  it('adds no bottom block when none of those areas are available', () => {
    granted = ['ad.users.read'];
    expect(renderNav().container.querySelector('.nav-bottom')).toBeNull();
  });
});
