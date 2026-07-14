import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { TaskJournalSettingsDto } from '../../services/api';
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
  allowAdminCompleteAnyTask: true,
  approvalDueDays: 5,
  taskCardWindowMode: 'modern',
  caseInactivityAutomationEnabled: false,
  caseInactivityDays: 14,
  caseInactivityLimit: 25,
  caseInactivityDefaultAssigneeUserId: null,
  caseInactivityEscalationDays: 30,
  caseInactivityEscalationUserId: null,
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
});
