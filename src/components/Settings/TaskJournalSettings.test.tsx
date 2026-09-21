import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { TaskJournalSettingsDto } from '../../services/api';
import { cloneDefaultTaskStickerStyles } from '../../utils/taskStickerStyles';
import { TaskJournalSettings } from './TaskJournalSettings';

const apiMock = vi.hoisted(() => ({
  getTaskJournalSettings: vi.fn(),
  getTaskJournalAssignees: vi.fn(),
  getTaskJournalAutomationDiagnostics: vi.fn(),
  previewTaskJournalCaseAutomation: vi.fn(),
  getTaskJournalAutomationPreviewDetails: vi.fn(),
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
  financialDocumentsResponsibleUserId: null,
  financialDocumentsResponsibleUserName: '',
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
    apiMock.previewTaskJournalCaseAutomation.mockResolvedValue({
      inactivityDays: 14,
      escalationDays: 30,
      dailyLimit: 5,
      trackedCaseCount: 40,
      candidateCount: 12,
      nextRunCandidateCount: 5,
      deferredByLimitCount: 7,
      escalationCandidateCount: 3,
      excludedByStatusCount: 8,
      pausedByVacationCount: 2,
      disabledCount: 4,
      activeTaskCount: 9,
      unassignedCount: 1,
      recentlyActiveCount: 5,
    });
    apiMock.getTaskJournalAutomationPreviewDetails.mockImplementation(async ({ category, offset = 0 }) => ({
      category,
      inactivityDays: 14,
      escalationDays: 30,
      totalCount: 1,
      offset,
      limit: 20,
      items: [{
        clientCaseId: 501,
        clientCaseTitle: 'Проверяемое дело',
        clientId: 41,
        clientName: 'Проверяемый клиент',
        legalEntityId: null,
        legalEntityName: '',
        responsibleName: 'Ответственный сотрудник',
        explanation: 'Точная причина попадания в категорию.',
        lastActivityAt: '2026-09-01T08:00:00Z',
        lastActivitySource: 'task',
      }],
    }));
    apiMock.updateTaskJournalSettings.mockImplementation(async (payload: TaskJournalSettingsDto) => payload);
  });

  it('lets an administrator choose employees who may complete assigned tasks', async () => {
    render(<TaskJournalSettings />);

    const accessList = await screen.findByTestId('executor-completion-access-list');
    const natalia = within(accessList).getByRole('checkbox', { name: /Наталья Кузьминова/i });
    const ivan = within(accessList).getByRole('checkbox', { name: /Иван Петров/i });
    expect(screen.getByText(/Исполнитель по-прежнему может добавлять комментарии и файлы/u)).toBeTruthy();
    expect(screen.getByText(/Все назначенные исполнители могут приложить результат и отправить задачу/u)).toBeTruthy();
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

  it('lets an administrator appoint the employee responsible for financial documents', async () => {
    render(<TaskJournalSettings />);

    const responsible = await screen.findByLabelText('Ответственный за счета, акты и договоры');
    fireEvent.change(responsible, { target: { value: '8' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalSettings).toHaveBeenCalledWith(
      expect.objectContaining({ financialDocumentsResponsibleUserId: 8 }),
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
    expect(screen.getByRole('spinbutton', { name: /Дневной лимит новых задач/i })).toBeTruthy();
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

  it('shows diagnostics that match the daily limit and unavailable assignees', async () => {
    apiMock.getTaskJournalAutomationDiagnostics.mockResolvedValue({
      enabled: true,
      inactivityDays: 14,
      escalationDays: 30,
      limit: 10,
      defaultAssigneeUserId: 8,
      defaultAssigneeName: 'Иван Петров',
      defaultAssigneeAvailable: false,
      escalationUserId: null,
      escalationUserName: '',
      visibleCandidateCount: 12,
      nextRunCandidateCount: 10,
      deferredByLimitCount: 2,
      visibleUnassignedCount: 3,
      visibleEscalationCandidateCount: 4,
      createdTaskCountLast7Days: 6,
      scheduleDueNow: false,
      nextScheduledRunAt: '2026-09-18T02:00:00.000Z',
      lastCreatedAt: null,
      lastCreatedDescription: null,
    });
    render(<TaskJournalSettings />);

    const cards = await screen.findByTestId('case-inactivity-diagnostics-cards');
    expect(within(cards).getByText('Готово сейчас').parentElement?.textContent).toContain('12');
    expect(within(cards).getByText('Создаст за запуск').parentElement?.textContent).toContain('10');
    expect(within(cards).getByText('Создаст за запуск').parentElement?.textContent).toContain('С учётом дневного лимита');
    expect(within(cards).getByText('Останется в очереди').parentElement?.textContent).toContain('2');
    expect(within(cards).getByText('Без исполнителя').parentElement?.textContent).toContain('3');
    expect(cards.querySelectorAll('.pp-automation-diagnostics-card-warning')).toHaveLength(2);
    const unavailableAssignee = screen.getByText('Иван Петров — сейчас недоступен');
    expect(unavailableAssignee.classList.contains('pp-automation-diagnostics-warning-text')).toBe(true);
    expect(screen.getByText('Следующий плановый запуск')).toBeTruthy();
    expect(screen.getByText(/Europe\/Moscow/)).toBeTruthy();
  });

  it('previews how many cases will match unsaved inactivity thresholds', async () => {
    apiMock.getTaskJournalSettings.mockResolvedValue({
      ...settings,
      caseInactivityAutomationEnabled: true,
      caseInactivityLimit: 5,
    });
    render(<TaskJournalSettings />);

    const inactivityDays = await screen.findByRole('spinbutton', { name: /Период без движения/i });
    fireEvent.change(inactivityDays, { target: { value: '10' } });
    fireEvent.click(screen.getByRole('button', { name: 'Рассчитать влияние' }));

    await waitFor(() => expect(apiMock.previewTaskJournalCaseAutomation).toHaveBeenCalledWith(10, 30, 0, 5));
    const result = await screen.findByTestId('case-inactivity-preview-result');
    expect(result.textContent).toContain('12');
    expect(result.textContent).toContain('3');

    const breakdown = screen.getByTestId('case-inactivity-preview-breakdown');
    const runPlan = screen.getByTestId('case-inactivity-preview-run-plan');
    const panel = screen.getByTestId('case-inactivity-preview-panel');
    expect(panel.classList.contains('pp-automation-preview')).toBe(true);
    expect(panel.querySelectorAll('.pp-automation-preview-card')).toHaveLength(9);
    expect(within(panel).getByText('Безопасный расчёт: задачи не создаются, настройки не сохраняются')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Рассчитать влияние' }).classList.contains('pp-automation-preview-action')).toBe(true);
    expect(within(breakdown).getByText('Проверено дел')).toBeTruthy();
    expect(within(breakdown).getByText('40')).toBeTruthy();
    expect(within(breakdown).getByText('Есть активная задача')).toBeTruthy();
    expect(within(breakdown).getByText('9')).toBeTruthy();
    const unassignedCard = within(breakdown).getByRole('button', { name: 'Нет исполнителя: 1. Показать дела' });
    expect(unassignedCard.classList.contains('pp-automation-preview-card-warning')).toBe(true);
    expect(within(breakdown).getByRole('button', { name: 'Было недавнее движение: 5. Показать дела' })).toBeTruthy();
    expect(within(breakdown).getByText('Исключены по статусу')).toBeTruthy();
    expect(within(breakdown).getByText('8')).toBeTruthy();
    expect(within(breakdown).getByText('Автоконтроль выключен')).toBeTruthy();
    expect(within(breakdown).getByText('4')).toBeTruthy();
    expect(within(breakdown).getByText('Ответственный в отпуске')).toBeTruthy();
    expect(within(breakdown).getByText('2')).toBeTruthy();
    expect(within(runPlan).getByText('Ближайший запуск')).toBeTruthy();
    expect(within(runPlan).getByText('5')).toBeTruthy();
    expect(within(runPlan).getByText('Останется в очереди')).toBeTruthy();
    expect(within(runPlan).getByText('7')).toBeTruthy();
    expect(within(runPlan).getByText('Новых задач при дневном лимите 5')).toBeTruthy();
    const criticalCard = within(breakdown).getByRole('button', { name: 'Достигнут критический срок: 3. Показать дела' });
    expect(criticalCard.classList.contains('pp-automation-preview-card-critical')).toBe(true);
    fireEvent.click(criticalCard);
    await waitFor(() => expect(apiMock.getTaskJournalAutomationPreviewDetails).toHaveBeenCalledWith({
      inactivityDays: 14,
      escalationDays: 30,
      defaultAssigneeUserId: 0,
      category: 'criticalCandidate',
      offset: 0,
      limit: 20,
    }));

    fireEvent.change(inactivityDays, { target: { value: '11' } });
    expect(screen.queryByTestId('case-inactivity-preview-breakdown')).toBeNull();
  });

  it('includes the unsaved fallback assignee in preview and invalidates stale results', async () => {
    apiMock.getTaskJournalSettings.mockResolvedValue({
      ...settings,
      caseInactivityAutomationEnabled: true,
    });
    render(<TaskJournalSettings />);

    const fallbackAssignee = await screen.findByRole('combobox', { name: /Резервный исполнитель/i });
    fireEvent.click(fallbackAssignee);
    fireEvent.click(screen.getByRole('option', { name: 'Иван Петров' }));
    fireEvent.click(screen.getByRole('button', { name: 'Рассчитать влияние' }));

    await waitFor(() => expect(apiMock.previewTaskJournalCaseAutomation).toHaveBeenCalledWith(14, 30, 8, 25));
    expect(await screen.findByTestId('case-inactivity-preview-breakdown')).toBeTruthy();

    fireEvent.click(fallbackAssignee);
    fireEvent.click(screen.getByRole('option', { name: 'Ответственный по делу' }));
    expect(screen.queryByTestId('case-inactivity-preview-breakdown')).toBeNull();
  });

  it('invalidates the preview when the unsaved daily limit changes', async () => {
    apiMock.getTaskJournalSettings.mockResolvedValue({
      ...settings,
      caseInactivityAutomationEnabled: true,
    });
    render(<TaskJournalSettings />);

    fireEvent.click(await screen.findByRole('button', { name: 'Рассчитать влияние' }));
    expect(await screen.findByTestId('case-inactivity-preview-run-plan')).toBeTruthy();

    fireEvent.change(screen.getByRole('spinbutton', { name: /Дневной лимит новых задач/i }), {
      target: { value: '10' },
    });
    expect(screen.queryByTestId('case-inactivity-preview-run-plan')).toBeNull();
  });

  it('pages and searches case explanations before navigating to a case', async () => {
    const onOpenCase = vi.fn();
    apiMock.getTaskJournalAutomationPreviewDetails
      .mockResolvedValueOnce({
        category: 'activeTask',
        inactivityDays: 14,
        escalationDays: 30,
        totalCount: 2,
        offset: 0,
        limit: 20,
        items: [{
          clientCaseId: 601,
          clientCaseTitle: 'Дело с первой активной задачей',
          clientId: 51,
          clientName: 'Первый клиент',
          legalEntityId: null,
          legalEntityName: '',
          responsibleName: 'Анна Ответственная',
          explanation: 'По делу есть активные задачи: 1.',
          lastActivityAt: '2026-09-10T08:00:00Z',
          lastActivitySource: 'task',
          firstControlDueAt: '2026-09-24T08:00:00Z',
          criticalControlDueAt: '2026-10-10T08:00:00Z',
        }],
      })
      .mockResolvedValueOnce({
        category: 'activeTask',
        inactivityDays: 14,
        escalationDays: 30,
        totalCount: 2,
        offset: 1,
        limit: 20,
        items: [{
          clientCaseId: 602,
          clientCaseTitle: 'Дело со второй активной задачей',
          clientId: 52,
          clientName: 'Второй клиент',
          legalEntityId: null,
          legalEntityName: '',
          responsibleName: '',
          explanation: 'По делу есть активные задачи: 2.',
          lastActivityAt: '2026-09-11T09:00:00Z',
          lastActivitySource: 'taskComment',
        }],
      })
      .mockResolvedValueOnce({
        category: 'activeTask',
        inactivityDays: 14,
        escalationDays: 30,
        totalCount: 1,
        offset: 0,
        limit: 20,
        items: [{
          clientCaseId: 602,
          clientCaseTitle: 'Дело со второй активной задачей',
          clientId: 52,
          clientName: 'Второй клиент',
          legalEntityId: null,
          legalEntityName: '',
          responsibleName: '',
          explanation: 'По делу есть активные задачи: 2.',
          lastActivityAt: '2026-09-11T09:00:00Z',
          lastActivitySource: 'taskComment',
        }],
      })
      .mockRejectedValueOnce(new Error('Сервис временно недоступен'))
      .mockResolvedValueOnce({
        category: 'activeTask',
        inactivityDays: 14,
        escalationDays: 30,
        totalCount: 1,
        offset: 0,
        limit: 20,
        items: [{
          clientCaseId: 603,
          clientCaseTitle: 'Дело после восстановления запроса',
          clientId: 53,
          clientName: 'Третий клиент',
          legalEntityId: null,
          legalEntityName: '',
          responsibleName: 'Иван Ответственный',
          explanation: 'По делу есть активная задача после восстановления.',
          lastActivityAt: '2026-09-12T10:00:00Z',
          lastActivitySource: 'task',
        }],
      });
    render(<TaskJournalSettings onOpenCase={onOpenCase} />);

    await screen.findByRole('button', { name: 'Рассчитать влияние' });
    fireEvent.click(screen.getByRole('button', { name: 'Рассчитать влияние' }));
    await screen.findByTestId('case-inactivity-preview-breakdown');
    fireEvent.click(screen.getByRole('button', { name: 'Есть активная задача: 9. Показать дела' }));

    const details = await screen.findByTestId('case-inactivity-preview-details');
    expect(within(details).getByText('Дело с первой активной задачей')).toBeTruthy();
    expect(within(details).getByText('По делу есть активные задачи: 1.')).toBeTruthy();
    expect(within(details).getByText(/Порог первого контроля:/).textContent).not.toContain('—');
    expect(within(details).getByText(/Критический порог:/).textContent).not.toContain('—');
    expect(within(details).getByText(/Последняя контрольная точка:.*задача/)).toBeTruthy();
    expect(apiMock.getTaskJournalAutomationPreviewDetails).toHaveBeenNthCalledWith(1, {
      inactivityDays: 14,
      escalationDays: 30,
      defaultAssigneeUserId: 0,
      category: 'activeTask',
      offset: 0,
      limit: 20,
    });

    fireEvent.click(within(details).getByRole('button', { name: 'Показать ещё' }));
    expect(await within(details).findByText('Дело со второй активной задачей')).toBeTruthy();
    expect(apiMock.getTaskJournalAutomationPreviewDetails).toHaveBeenNthCalledWith(2, {
      inactivityDays: 14,
      escalationDays: 30,
      defaultAssigneeUserId: 0,
      category: 'activeTask',
      offset: 1,
      limit: 20,
    });

    fireEvent.change(within(details).getByRole('searchbox', { name: 'Поиск дел в выбранной категории' }), {
      target: { value: '  Второй клиент  ' },
    });
    fireEvent.click(within(details).getByRole('button', { name: 'Найти' }));

    await waitFor(() => expect(apiMock.getTaskJournalAutomationPreviewDetails).toHaveBeenNthCalledWith(3, {
      inactivityDays: 14,
      escalationDays: 30,
      defaultAssigneeUserId: 0,
      category: 'activeTask',
      search: 'Второй клиент',
      offset: 0,
      limit: 20,
    }));
    expect(await within(details).findByText('По запросу показано 1 из 1')).toBeTruthy();
    expect(within(details).queryByText('Дело с первой активной задачей')).toBeNull();
    expect(within(details).getByText('Дело со второй активной задачей')).toBeTruthy();

    fireEvent.click(within(details).getByRole('button', { name: 'Открыть дело' }));
    expect(onOpenCase).toHaveBeenCalledWith(602, 52);

    fireEvent.change(within(details).getByRole('searchbox', { name: 'Поиск дел в выбранной категории' }), {
      target: { value: 'Недоступный запрос' },
    });
    fireEvent.click(within(details).getByRole('button', { name: 'Найти' }));

    const inlineError = await within(details).findByRole('alert');
    expect(inlineError.textContent).toContain('Сервис временно недоступен');
    expect(inlineError.classList.contains('pp-automation-preview-error')).toBe(true);
    expect(within(details).getByText('Дело со второй активной задачей')).toBeTruthy();
    expect(within(details).getByText('По запросу показано 1 из 1')).toBeTruthy();
    expect(within(details).queryByText(/По запросу «Недоступный запрос»/)).toBeNull();

    fireEvent.click(within(details).getByRole('button', { name: 'Повторить' }));
    await waitFor(() => expect(apiMock.getTaskJournalAutomationPreviewDetails).toHaveBeenNthCalledWith(5, {
      inactivityDays: 14,
      escalationDays: 30,
      defaultAssigneeUserId: 0,
      category: 'activeTask',
      search: 'Недоступный запрос',
      offset: 0,
      limit: 20,
    }));
    expect(await within(details).findByText('Дело после восстановления запроса')).toBeTruthy();
    expect(within(details).queryByRole('alert')).toBeNull();
    expect(within(details).queryByText('Дело со второй активной задачей')).toBeNull();
  });
});
