import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { TaskJournalSettingsDto } from '../../services/api';
import { cloneDefaultTaskStickerStyles } from '../../utils/taskStickerStyles';
import { TaskJournalSettings } from './TaskJournalSettings';

const apiMock = vi.hoisted(() => ({
  getTaskJournalSettings: vi.fn(),
  getTaskJournalAssignees: vi.fn(),
  getTaskJournalAutomationDiagnostics: vi.fn(),
  updateTaskJournalSettings: vi.fn(),
}));
const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));

vi.mock('../../services/api', () => ({ apiService: apiMock }));
vi.mock('../../hooks/useToast', () => ({
  useToast: () => toastMock,
}));

const settings: TaskJournalSettingsDto = {
  restrictExecutorEditing: true,
  allowExecutorDeadlineChangeRequests: true,
  executorCompletionAllowedUserIds: [7],
  requireCompletionResult: true,
  dailyOverdueReminderEnabled: true,
  allowAdminCompleteAnyTask: true,
  approvalDueDays: 5,
  taskCardWindowMode: 'modern',
  approvalQuickActionMode: 'completionButton',
  caseInactivityAutomationEnabled: false,
  caseInactivityDays: 14,
  caseInactivityLimit: 25,
  caseInactivityDefaultAssigneeUserId: null,
  caseInactivityEscalationDays: 30,
  caseInactivityEscalationUserId: null,
  caseInactivityFirstRepeatAfterDays: 2,
  caseInactivityCriticalRepeatAfterDays: 4,
  caseInactivityRunLocalTime: '09:00',
  caseInactivityScheduleTimeZone: 'Europe/Moscow',
  stickerStyles: cloneDefaultTaskStickerStyles(),
};

describe('TaskJournalSettings', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    apiMock.getTaskJournalSettings.mockResolvedValue(settings);
    apiMock.getTaskJournalAssignees.mockResolvedValue([
      { id: 7, name: 'Наталья Кузьминова', email: 'natalia@example.com' },
      { id: 8, name: 'Иван Петров', email: 'ivan@example.com' },
    ]);
    apiMock.getTaskJournalAutomationDiagnostics.mockResolvedValue(null);
    apiMock.updateTaskJournalSettings.mockImplementation(async (payload: TaskJournalSettingsDto) => payload);
  });

  it('lets an administrator choose employees who may complete assigned tasks', async () => {
    render(<TaskJournalSettings />);

    const accessList = await screen.findByTestId('executor-completion-access-list');
    const natalia = within(accessList).getByRole('checkbox', { name: /Наталья Кузьминова/i });
    const ivan = within(accessList).getByRole('checkbox', { name: /Иван Петров/i });
    expect((natalia as HTMLInputElement).checked).toBe(true);
    expect((ivan as HTMLInputElement).checked).toBe(false);

    await waitFor(() => expect((screen.getByRole('button', { name: 'Сохранить' }) as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(ivan);
    expect(await screen.findByText('Выбрано: 2')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalSettings).toHaveBeenCalledWith(
      expect.objectContaining({ executorCompletionAllowedUserIds: [7, 8] }),
    ));
  });

  it('lets an administrator choose how approval quick actions work', async () => {
    render(<TaskJournalSettings />);

    const separateButton = await screen.findByRole('radio', { name: /Отдельным значком согласования/i });
    expect(separateButton.getAttribute('aria-checked')).toBe('false');

    fireEvent.click(separateButton);
    expect(separateButton.getAttribute('aria-checked')).toBe('true');
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalSettings).toHaveBeenCalledWith(
      expect.objectContaining({ approvalQuickActionMode: 'separateButton' }),
    ));
  });

  it('lets an administrator customize task sticker labels and colors', async () => {
    render(<TaskJournalSettings />);

    const shortLabel = await screen.findByRole('textbox', { name: 'Обычный приоритет: короткое название' });
    fireEvent.change(shortLabel, { target: { value: 'СРЕДНИЙ' } });
    fireEvent.change(screen.getByLabelText('Обычный приоритет: фон'), { target: { value: '#fef3c7' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalSettings).toHaveBeenCalledWith(
      expect.objectContaining({
        stickerStyles: expect.objectContaining({
          'priority.normal': expect.objectContaining({
            shortLabel: 'СРЕДНИЙ',
            backgroundColor: '#fef3c7',
          }),
        }),
      }),
    ));
  });

  it('lets an administrator configure the daily automation schedule', async () => {
    apiMock.getTaskJournalSettings.mockResolvedValue({
      ...settings,
      caseInactivityAutomationEnabled: true,
    });
    render(<TaskJournalSettings />);

    const runTime = await screen.findByLabelText('Время автоматического запуска');
    fireEvent.change(runTime, { target: { value: '16:45' } });
    fireEvent.change(screen.getByLabelText('Часовой пояс запуска'), {
      target: { value: 'Asia/Novosibirsk' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalSettings).toHaveBeenCalledWith(
      expect.objectContaining({
        caseInactivityRunLocalTime: '16:45',
        caseInactivityScheduleTimeZone: 'Asia/Novosibirsk',
      }),
    ));
  });

  it('shows the actual first, repeat and critical escalation rules', async () => {
    render(<TaskJournalSettings />);

    const stages = await screen.findByLabelText('Этапы автоконтроля');
    expect(within(stages).getByText('Через 14 дн. без активности')).toBeTruthy();
    expect(within(stages).getByText('Через 2 дн. после контроля')).toBeTruthy();
    expect(within(stages).getByText('Через 4 дн. или при 30 дн. тишины')).toBeTruthy();
  });
});
