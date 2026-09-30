import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HourlyActivityCard, HourlyBars, type HourlyActivity } from '../../src/Frontend/Components/HourlyChart';

const hour = (h: number) => new Date(Date.UTC(2026, 0, 1, h)).toISOString();
const data = (n = 24): HourlyActivity => ({
  hours: n, lockoutsNote: null,
  points: Array.from({ length: n }, (_, i) => ({ hourUtc: hour(i), passwordResets: i === 5 ? 3 : 0, unlocks: i === 6 ? 2 : 0, lockouts: i === 7 ? 4 : 0 })),
  totalPasswordResets: 3, totalUnlocks: 2, totalLockouts: 4,
});
const urls: string[] = [];
vi.mock('../../src/Frontend/Services/api', async () => {
  const actual = await vi.importActual<typeof import('../../src/Frontend/Services/api')>('../../src/Frontend/Services/api');
  return { ...actual, get: vi.fn(async (path: string) => { urls.push(path); return data(Number(/hours=(\d+)/.exec(path)?.[1] ?? 24)); }) };
});

describe('hourly chart', () => {
  it('draws one bar per series per hour with an accessible summary', () => {
    const { container } = render(<HourlyBars data={data()} />);
    expect(container.querySelectorAll('rect')).toHaveLength(24 * 3);
    expect(screen.getByRole('img')).toHaveAttribute('aria-label', expect.stringContaining('3 password resets, 2 unlocks, 4 account lockouts'));
    expect(container.querySelectorAll('rect title')[5 * 3].textContent).toContain('Password resets 3');
  });

  it('shows the totals, a table of the numbers, and reloads for another range', async () => {
    const u = userEvent.setup();
    render(<HourlyActivityCard />);
    const legend = (await screen.findByText(/Account lockouts/, { selector: '.chart-key' })).closest('.chart-legend') as HTMLElement;
    expect(legend).toHaveTextContent('Password resets 3');
    expect(legend).toHaveTextContent('Unlocks 2');
    expect(legend).toHaveTextContent('Account lockouts 4');
    expect(screen.getByText('Show the numbers as a table')).toBeInTheDocument();
    await u.selectOptions(screen.getByLabelText('Time range'), '48');
    await waitFor(() => expect(urls.at(-1)).toContain('hours=48'));
  });
});
