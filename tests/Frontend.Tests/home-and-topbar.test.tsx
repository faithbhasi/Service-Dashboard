import { act, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { SystemClock } from '../../src/Frontend/Pages/HomePage';
import { TopBar } from '../../src/Frontend/Layouts/TopBar';

let shellData: Record<string, unknown> = {};

vi.mock('../../src/Frontend/Hooks/AuthContext', () => ({
  useAuth: () => ({ me: { displayName: 'Dev Admin', initials: 'DA', email: 'a@x', roleName: 'Admins', preferences: { navCollapsed: false, theme: 'light' } }, updatePreferences: vi.fn() }),
}));
vi.mock('../../src/Frontend/Hooks/ShellContext', () => ({ useShell: () => ({ shell: shellData }) }));
vi.mock('../../src/Frontend/Themes/ThemeProvider', () => ({ useTheme: () => ({ branding: { productName: 'IT Dash', hasLogoLight: false, hasLogoDark: false }, mode: 'light', preference: 'light' }) }));
vi.mock('../../src/Frontend/Components/GlobalSearch', () => ({ GlobalSearch: () => <div /> }));

describe('system clock', () => {
  beforeEach(() => { vi.useFakeTimers(); vi.setSystemTime(new Date('2026-03-05T14:05:09Z')); });
  afterEach(() => vi.useRealTimers());

  it('shows HH:MM:SS in the application time zone and ticks every second', () => {
    render(<SystemClock timeZone="UTC" />);
    expect(screen.getByRole('timer')).toHaveTextContent('14:05:09');
    act(() => { vi.advanceTimersByTime(1000); });
    expect(screen.getByRole('timer')).toHaveTextContent('14:05:10');
    act(() => { vi.advanceTimersByTime(55_000); });
    expect(screen.getByRole('timer')).toHaveTextContent('14:06:05');
  });

  it('follows the configured time zone, including one with an offset', () => {
    render(<SystemClock timeZone="Asia/Kolkata" />);
    expect(screen.getByRole('timer')).toHaveTextContent('19:35:09');
    expect(screen.getByRole('timer')).toHaveTextContent('Asia/Kolkata');
  });

  it('does not break the page on an unknown time zone, and stops ticking when removed', () => {
    const { unmount } = render(<SystemClock timeZone="Mars/Olympus" />);
    expect(screen.getByRole('timer')).toHaveTextContent('14:05:09'); // falls back to UTC ...
    expect(screen.getByRole('timer')).toHaveTextContent('UTC'); // ... and says so, instead of naming a zone it is not using
    expect(screen.getByRole('timer')).not.toHaveTextContent('Mars');
    unmount();
    expect(vi.getTimerCount()).toBe(0);
  });
});

describe('top bar', () => {
  const top = () => render(<MemoryRouter initialEntries={['/ad/users']}><TopBar onToggleNav={() => {}} /></MemoryRouter>);

  it('takes you back to Home when the logo or name is clicked', () => {
    shellData = { productName: 'IT Dash', environmentLabel: '' };
    top();
    const link = screen.getByRole('link', { name: /go to Home/ });
    expect(link).toHaveAttribute('href', '/');
    expect(link).toHaveTextContent('IT Dash');
  });

  it('uses the chosen environment colour with readable text, or the automatic class', () => {
    shellData = { productName: 'IT Dash', environmentLabel: 'Test', environmentLabelColor: '#ffeb3b' };
    const { unmount } = top();
    const badge = screen.getByText('Test');
    expect(badge).toHaveStyle({ background: '#ffeb3b' });
    expect(badge.style.color).not.toBe('rgb(255, 255, 255)'); // dark text on a light colour
    unmount();

    shellData = { productName: 'IT Dash', environmentLabel: 'Production', environmentLabelColor: '#1b2433' };
    const dark = top();
    expect(screen.getByText('Production').style.color).toBe('rgb(255, 255, 255)'); // white on a dark colour
    dark.unmount();

    shellData = { productName: 'IT Dash', environmentLabel: 'Production', environmentLabelColor: '' };
    top();
    expect(screen.getByText('Production')).toHaveClass('env-production');
    expect(screen.getByText('Production').getAttribute('style')).toBeNull();
  });
});
