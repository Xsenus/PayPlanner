import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { AppealDeadlineDialog } from './AppealDeadlineDialog';

const apiMock = vi.hoisted(() => ({
  getExpiringCaseAppeals: vi.fn(),
  createCaseAppealTask: vi.fn(),
  remindCaseAppealAssignee: vi.fn(),
}));
vi.mock('../../services/api', () => ({ apiService: apiMock }));
vi.mock('../../hooks/useUserTimeZone', () => ({ useUserTimeZone: () => 'Asia/Novosibirsk' }));

const appeal = {
  caseId: 24,
  caseTitle: 'Спор клиента',
  caseNumber: 'А55-24/2026',
  courtDecisionAt: '2026-09-01T00:00:00',
  appealDeadlineAt: '2026-09-26T00:00:00',
  decisionSummary: 'Иск удовлетворён частично',
};

describe('AppealDeadlineDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.localStorage.clear();
    apiMock.getExpiringCaseAppeals.mockResolvedValue([appeal]);
    apiMock.createCaseAppealTask.mockResolvedValue({ id: 7, caseId: 24, choice: 'appeal', taskId: 81, decidedAt: '2026-09-23T00:00:00' });
    apiMock.remindCaseAppealAssignee.mockResolvedValue({ sent: true, recipientUserId: 5 });
  });

  it('shows a visible case, sends a reminder and creates a linked task', async () => {
    const onOpenTask = vi.fn();
    render(<AppealDeadlineDialog userId={3} enabled onOpenCase={vi.fn()} onOpenTask={onOpenTask} />);

    expect(await screen.findByRole('dialog', { name: 'Срок обжалования истекает' })).toBeTruthy();
    expect(screen.getByText(/А55-24\/2026 · Спор клиента/)).toBeTruthy();
    expect(screen.getByText(/Решение: 01\.09\.2026/)).toBeTruthy();
    expect(screen.getByText('26.09.2026')).toBeTruthy();
    expect(screen.getByText('Иск удовлетворён частично')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Напомнить исполнителю' }));
    await waitFor(() => expect(apiMock.remindCaseAppealAssignee).toHaveBeenCalledWith(24, {
      courtDecisionAt: appeal.courtDecisionAt,
      appealDeadlineAt: appeal.appealDeadlineAt,
    }));
    expect(await screen.findByRole('button', { name: 'Напоминание отправлено' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Создать задачу на обжалование' }));
    await waitFor(() => expect(apiMock.createCaseAppealTask).toHaveBeenCalledTimes(1));
    fireEvent.click(await screen.findByRole('button', { name: 'К задаче' }));
    expect(onOpenTask).toHaveBeenCalledWith(81);
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('does not query without edit access or reopen after dismissal on the same day', async () => {
    const denied = render(<AppealDeadlineDialog userId={3} enabled={false} onOpenCase={vi.fn()} />);
    expect(apiMock.getExpiringCaseAppeals).not.toHaveBeenCalled();
    denied.unmount();

    const first = render(<AppealDeadlineDialog userId={3} enabled onOpenCase={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Позже' }));
    first.unmount();
    render(<AppealDeadlineDialog userId={3} enabled onOpenCase={vi.fn()} />);
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(apiMock.getExpiringCaseAppeals).toHaveBeenCalledTimes(2);
  });

  it('opens the case and shows an actionable server error', async () => {
    const onOpenCase = vi.fn();
    apiMock.createCaseAppealTask.mockRejectedValueOnce(new Error('Даты дела изменились'));
    render(<AppealDeadlineDialog userId={4} enabled onOpenCase={onOpenCase} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Создать задачу на обжалование' }));
    expect(await screen.findByRole('alert')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'К делу' }));
    expect(onOpenCase).toHaveBeenCalledWith(24);
  });
});
