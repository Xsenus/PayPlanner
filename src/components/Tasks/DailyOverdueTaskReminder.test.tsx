import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { DailyOverdueTaskReminder } from './DailyOverdueTaskReminder';

const apiMock = vi.hoisted(() => ({
  getTaskJournalSettings: vi.fn(),
  getTaskJournalOverdueSummary: vi.fn(),
}));

vi.mock('../../services/api', () => ({ apiService: apiMock }));

describe('DailyOverdueTaskReminder', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.localStorage.clear();
    apiMock.getTaskJournalSettings.mockResolvedValue({ dailyOverdueReminderEnabled: true });
    apiMock.getTaskJournalOverdueSummary.mockResolvedValue({ count: 35 });
  });

  it('shows the employee overdue count and opens the filtered task list', async () => {
    const onOpenTasks = vi.fn();
    render(<DailyOverdueTaskReminder userId={7} enabled onOpenTasks={onOpenTasks} />);

    expect(await screen.findByRole('dialog', { name: 'Внимание!' })).toBeTruthy();
    expect(screen.getByText('35')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /К задачам/i }));

    expect(onOpenTasks).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(window.localStorage.getItem('pp.tasks.daily-overdue-reminder.7')).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });

  it('does not show again after it was closed on the same day', async () => {
    const first = render(<DailyOverdueTaskReminder userId={7} enabled onOpenTasks={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Закрыть' }));
    first.unmount();

    render(<DailyOverdueTaskReminder userId={7} enabled onOpenTasks={vi.fn()} />);
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(apiMock.getTaskJournalOverdueSummary).toHaveBeenCalledTimes(1);
  });

  it('respects the global administrator switch', async () => {
    apiMock.getTaskJournalSettings.mockResolvedValue({ dailyOverdueReminderEnabled: false });
    render(<DailyOverdueTaskReminder userId={7} enabled onOpenTasks={vi.fn()} />);

    await waitFor(() => expect(apiMock.getTaskJournalSettings).toHaveBeenCalled());
    expect(screen.queryByRole('dialog')).toBeNull();
  });
});
