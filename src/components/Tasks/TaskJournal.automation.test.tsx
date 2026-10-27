import { setupTaskJournalTests, apiMock, toastMock, todayAt, toDatetimeLocalInputValue, createTask, renderJournal } from './TaskJournal.test-support';
import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { TASK_JOURNAL_EVENT } from '../../utils/taskJournalStore';

describe('TaskJournal', () => {
  setupTaskJournalTests();


  it('creates a control task from case automation candidate', async () => {
    apiMock.getTaskJournalCaseAutomationCandidates.mockResolvedValue([
      {
        clientId: 10,
        clientName: 'Иванов Иван Иванович',
        clientCaseId: 100,
        clientCaseTitle: 'БАНКРОТСТВО',
        reason: 'Нет активной задачи уже 20 дн.',
        activeTaskCount: 0,
        completedTaskCount: 2,
        daysWithoutActiveTask: 20,
        isEscalated: true,
        lastTaskAt: todayAt(8),
        suggestedTitle: 'Проверить движение по делу',
        suggestedDescription: 'Автоконтроль: по делу нет активной задачи.',
        suggestedDueAt: todayAt(9),
        suggestedPriority: 'urgent',
      },
    ]);
    apiMock.getTaskJournalCaseAutomationSummary
      .mockReset()
      .mockResolvedValueOnce({ totalCount: 1, readyCount: 1, criticalCount: 1, waitingCount: 0, pausedCount: 0 })
      .mockImplementation(() => new Promise(() => undefined));
    await renderJournal();

    expect(screen.getByText('Автоконтроль дел')).toBeTruthy();
    expect(await screen.findByText('Критический срок')).toBeTruthy();
    const createButton = screen.getByTestId('task-automation-create-100');
    expect(createButton.className).toContain('w-full');
    expect(createButton.closest('article')?.className).toContain('min-w-0');
    fireEvent.click(createButton);

    await waitFor(() => expect(apiMock.createTaskJournalCaseAutomationTask).toHaveBeenCalledWith(100));
    expect(apiMock.createTaskJournalItem).not.toHaveBeenCalled();
    expect(toastMock.success).toHaveBeenCalledWith('Контрольная задача создана.', 'Автоконтроль дел');
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Все: 0' })).toBeTruthy();
      expect(screen.getByRole('button', { name: 'Критические: 0' })).toBeTruthy();
      expect(screen.getByRole('button', { name: 'Пора создать: 0' })).toBeTruthy();
    });
    expect(await screen.findByRole('heading', { name: 'Просмотр задачи' })).toBeTruthy();
  });

  it('collapses the case automation panel and remembers the user preference', async () => {
    const candidate = {
      clientId: 10,
      clientName: 'Иванов Иван Иванович',
      clientCaseId: 100,
      clientCaseTitle: 'БАНКРОТСТВО',
      reason: 'Нет активной задачи уже 20 дн.',
      activeTaskCount: 0,
      completedTaskCount: 2,
      daysWithoutActiveTask: 20,
      isEscalated: false,
      isReadyForControl: true,
      automationState: 'ready' as const,
      lastTaskAt: todayAt(8),
      suggestedTitle: 'Проверить движение по делу',
      suggestedDescription: 'Автоконтроль: по делу нет активной задачи.',
      suggestedDueAt: todayAt(9),
      suggestedPriority: 'normal' as const,
    };
    apiMock.getTaskJournalCaseAutomationCandidates.mockResolvedValue([candidate]);
    apiMock.getTaskJournalCaseAutomationSummary.mockResolvedValue({
      totalCount: 1,
      readyCount: 1,
      criticalCount: 0,
      waitingCount: 0,
      pausedCount: 0,
    });

    await renderJournal();

    const collapseButton = screen.getByRole('button', { name: 'Свернуть' });
    expect(collapseButton.getAttribute('aria-expanded')).toBe('true');
    fireEvent.click(collapseButton);

    expect(screen.getByRole('button', { name: 'Развернуть' }).getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByTestId('task-automation-create-100')).toBeNull();
    expect(window.localStorage.getItem('pp.task_journal.automation_panel.v1.user:7')).toBe('collapsed');

    cleanup();
    await renderJournal();

    expect(screen.getByRole('button', { name: 'Развернуть' })).toBeTruthy();
    expect(screen.queryByTestId('task-automation-create-100')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Развернуть' }));
    expect(await screen.findByTestId('task-automation-create-100')).toBeTruthy();
    expect(window.localStorage.getItem('pp.task_journal.automation_panel.v1.user:7')).toBe('expanded');
  });

  it.each(['all', 'critical'] as const)('keeps the critical total visible when the automation panel is collapsed with %s selected', async (state) => {
    apiMock.getTaskJournalCaseAutomationCandidates.mockResolvedValue([]);
    apiMock.getTaskJournalCaseAutomationSummary.mockResolvedValue({
      totalCount: 7, readyCount: 3, criticalCount: 2, waitingCount: 4, pausedCount: 0,
    });
    await renderJournal();
    const critical = await screen.findByRole('button', { name: 'Критические: 2' });
    if (state === 'critical') fireEvent.click(critical);
    fireEvent.click(screen.getByRole('button', { name: 'Свернуть' }));
    expect(screen.getByLabelText('Критических дел: 2').textContent).toContain('2');
    expect(screen.getByRole('button', { name: 'Развернуть' }).getAttribute('aria-expanded')).toBe('false');
    expect(apiMock.createTaskJournalCaseAutomationTask).not.toHaveBeenCalled();
  });

  it('shows a stale-summary warning when the automation panel is collapsed', async () => {
    apiMock.getTaskJournalCaseAutomationCandidates.mockResolvedValue([]);
    apiMock.getTaskJournalCaseAutomationSummary.mockRejectedValue(new Error('Нет связи'));
    await renderJournal();
    await screen.findByText('Уточняем общие счётчики');
    fireEvent.click(screen.getByRole('button', { name: 'Свернуть' }));
    expect(screen.getByLabelText('Критических дел: данные не обновлены')).toBeTruthy();
    expect(screen.getByRole('status').textContent).toContain('требуют обновления');
  });

  it('refreshes the automation queue when another user creates the task first', async () => {
    const candidate = {
      clientId: 10,
      clientName: 'Иванов Иван Иванович',
      clientCaseId: 100,
      clientCaseTitle: 'БАНКРОТСТВО',
      reason: 'Нет активной задачи уже 20 дн.',
      activeTaskCount: 0,
      completedTaskCount: 2,
      daysWithoutActiveTask: 20,
      isEscalated: false,
      isReadyForControl: true,
      automationState: 'ready' as const,
      lastTaskAt: todayAt(8),
      suggestedTitle: 'Проверить движение по делу',
      suggestedDescription: 'Автоконтроль: по делу нет активной задачи.',
      suggestedDueAt: todayAt(9),
      suggestedPriority: 'normal' as const,
    };
    apiMock.getTaskJournalCaseAutomationCandidates
      .mockResolvedValueOnce([candidate])
      .mockResolvedValue([]);
    apiMock.getTaskJournalCaseAutomationSummary
      .mockResolvedValueOnce({ totalCount: 1, readyCount: 1, criticalCount: 0, waitingCount: 0, pausedCount: 0 })
      .mockResolvedValue({ totalCount: 0, readyCount: 0, criticalCount: 0, waitingCount: 0, pausedCount: 0 });
    apiMock.createTaskJournalCaseAutomationTask.mockRejectedValueOnce(
      new Error('HTTP 409 - {"message":"По делу уже есть активная задача.","taskId":999}'),
    );
    await renderJournal();

    fireEvent.click(screen.getByTestId('task-automation-create-100'));

    await waitFor(() => expect(toastMock.info).toHaveBeenCalledWith(
      'По делу уже есть активная задача. Очередь обновляется.',
      'Автоконтроль дел',
    ));
    expect(screen.queryByTestId('task-automation-create-100')).toBeNull();
    await waitFor(() => expect(apiMock.getTaskJournalCaseAutomationCandidates).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Все: 0' })).toBeTruthy());
  });

  it('keeps the complete case automation overview when the task search changes', async () => {
    apiMock.getTaskJournalCaseAutomationCandidates.mockResolvedValue([
      {
        clientId: 10,
        clientName: 'Кошелева Елена Юрьевна',
        clientCaseId: 100,
        clientCaseTitle: 'КОШЕЛЕВА - уголовное дело',
        reason: 'Нет активной задачи уже 21 дн.',
        activeTaskCount: 0,
        completedTaskCount: 2,
        daysWithoutActiveTask: 21,
        isEscalated: false,
        lastTaskAt: todayAt(8),
        suggestedTitle: 'Проверить движение по делу',
        suggestedDescription: 'Автоконтроль: по делу нет активной задачи.',
        suggestedDueAt: todayAt(9),
        suggestedPriority: 'normal',
      },
    ]);
    await renderJournal([], { waitForTitle: null });
    apiMock.getTaskJournalCaseAutomationCandidates.mockClear();

    const searchInput = screen.getByRole('textbox') as HTMLInputElement;
    fireEvent.change(searchInput, { target: { value: 'ДРУГОЙ КЛИЕНТ' } });

    expect(apiMock.getTaskJournalCaseAutomationCandidates).not.toHaveBeenCalled();
    expect(await screen.findByText('Кошелева Елена Юрьевна')).toBeTruthy();
    expect(screen.getByText('КОШЕЛЕВА - уголовное дело')).toBeTruthy();
  });

  it('creates control tasks for all automation candidates', async () => {
    apiMock.getTaskJournalCaseAutomationCandidates.mockResolvedValue([
      {
        clientId: 10,
        clientName: 'Иванов Иван Иванович',
        clientCaseId: 100,
        clientCaseTitle: 'БАНКРОТСТВО',
        reason: 'Нет активной задачи',
        activeTaskCount: 0,
        completedTaskCount: 0,
        daysWithoutActiveTask: 1,
        lastTaskAt: null,
        suggestedTitle: 'Проверить движение по делу',
        suggestedDescription: 'Автоконтроль',
        suggestedDueAt: todayAt(9),
      },
    ]);
    apiMock.getTaskJournalCaseAutomationSummary
      .mockReset()
      .mockResolvedValueOnce({ totalCount: 1, readyCount: 1, waitingCount: 0, pausedCount: 0 })
      .mockImplementation(() => new Promise(() => undefined));
    await renderJournal();

    fireEvent.click(screen.getByRole('button', { name: 'Создать наступившие (1)' }));

    await waitFor(() => expect(apiMock.createTaskJournalCaseAutomationTasks).toHaveBeenCalledWith({ limit: 1 }));
    expect(toastMock.success).toHaveBeenCalledWith('Создано контрольных задач: 1.', 'Автоконтроль дел');
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Все: 0' })).toBeTruthy();
      expect(screen.getByRole('button', { name: 'Пора создать: 0' })).toBeTruthy();
    });
  });

  it('limits bulk automation creation to the selected critical section', async () => {
    apiMock.getTaskJournalCaseAutomationCandidates.mockResolvedValue([
      {
        clientId: 10,
        clientName: 'Иванов Иван Иванович',
        clientCaseId: 100,
        clientCaseTitle: 'КРИТИЧЕСКОЕ ДЕЛО',
        reason: 'Нет активной задачи уже 40 дн.',
        activeTaskCount: 0,
        completedTaskCount: 2,
        daysWithoutActiveTask: 40,
        isEscalated: true,
        isReadyForControl: true,
        automationState: 'ready',
        lastTaskAt: todayAt(8),
        suggestedTitle: 'Проверить движение по делу',
        suggestedDescription: 'Автоконтроль',
        suggestedDueAt: todayAt(9),
      },
    ]);
    apiMock.getTaskJournalCaseAutomationSummary.mockResolvedValue({
      totalCount: 1,
      readyCount: 1,
      criticalCount: 1,
      waitingCount: 0,
      pausedCount: 0,
    });
    apiMock.createTaskJournalCaseAutomationTasks.mockResolvedValueOnce({
      createdTasks: [],
      skippedCount: 0,
    });
    await renderJournal();

    fireEvent.click(screen.getByRole('button', { name: 'Критические: 1' }));
    expect(await screen.findByRole('button', { name: 'Создать критические (1)' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Создать критические (1)' }));

    await waitFor(() => expect(apiMock.createTaskJournalCaseAutomationTasks).toHaveBeenCalledWith({
      limit: 1,
      automationState: 'critical',
    }));
  });

  it('falls back to the binding step when automatic billing lookup fails', async () => {
    apiMock.getTaskJournalCaseAutomationCandidates.mockResolvedValue([
      {
        clientId: 12,
        clientName: 'Петров Пётр Петрович',
        clientCaseId: 103,
        clientCaseTitle: 'ДЕЛО В ОЖИДАНИИ',
        reason: 'Первый контроль через 5 дн.',
        activeTaskCount: 0,
        completedTaskCount: 0,
        daysWithoutActiveTask: 9,
        isEscalated: false,
        isReadyForControl: false,
        automationState: 'waiting',
        lastTaskAt: null,
        suggestedTitle: 'Проверить движение по делу',
        suggestedDescription: 'Автоконтроль',
        suggestedDueAt: todayAt(9),
      },
    ]);
    apiMock.getTaskJournalCaseAutomationSummary.mockResolvedValue({
      totalCount: 1,
      readyCount: 0,
      criticalCount: 0,
      waitingCount: 1,
      pausedCount: 0,
    });
    apiMock.getTaskBillingContext.mockRejectedValueOnce(new Error('Нет связи с расчётом договора'));
    await renderJournal();

    fireEvent.click(screen.getByTestId('task-automation-create-103'));

    expect(await screen.findByRole('heading', { name: 'Выберите клиента и дело' })).toBeTruthy();
    expect(screen.getByText('Нет связи с расчётом договора')).toBeTruthy();
    expect(apiMock.createTaskJournalCaseAutomationTask).not.toHaveBeenCalled();
  });

  it('explains when a stale automation candidate is skipped during bulk creation', async () => {
    apiMock.getTaskJournalCaseAutomationCandidates.mockResolvedValue([
      {
        clientId: 10,
        clientName: 'Иванов Иван Иванович',
        clientCaseId: 100,
        clientCaseTitle: 'БАНКРОТСТВО',
        reason: 'Нет активной задачи',
        activeTaskCount: 0,
        completedTaskCount: 0,
        daysWithoutActiveTask: 21,
        isReadyForControl: true,
        automationState: 'ready',
        lastTaskAt: null,
        suggestedTitle: 'Проверить движение по делу',
        suggestedDescription: 'Автоконтроль',
        suggestedDueAt: todayAt(9),
      },
    ]);
    apiMock.createTaskJournalCaseAutomationTasks.mockResolvedValueOnce({
      createdTasks: [],
      skippedCount: 1,
    });
    await renderJournal();

    fireEvent.click(screen.getByRole('button', { name: 'Создать наступившие (1)' }));

    await waitFor(() => expect(toastMock.info).toHaveBeenCalledWith(
      'Новые задачи не созданы. Пропущено дел: 1 — их состояние или доступность изменились во время обработки.',
      'Автоконтроль дел',
    ));
  });

  it('shows all cases without active tasks by automation state', async () => {
    apiMock.getTaskJournalCaseAutomationCandidates.mockResolvedValue([
      {
        clientId: 10,
        clientName: 'Иванов Иван Иванович',
        responsibleUserId: 7,
        responsibleName: 'Администратор',
        clientCaseId: 100,
        clientCaseTitle: 'ГОТОВО К КОНТРОЛЮ',
        reason: 'Нет активной задачи уже 35 дн.',
        activeTaskCount: 0,
        completedTaskCount: 1,
        daysWithoutActiveTask: 35,
        isEscalated: true,
        isReadyForControl: true,
        automationState: 'ready',
        lastActivityAt: todayAt(8),
        lastActivitySource: 'task',
        lastTaskAt: null,
        nextControlAt: todayAt(9),
        daysUntilControl: 0,
        suggestedTitle: 'Проверить движение по делу',
        suggestedDescription: 'Автоконтроль',
        suggestedDueAt: todayAt(9),
      },
      {
        clientId: 11,
        clientName: 'Петров Петр Петрович',
        responsibleUserId: null,
        responsibleName: '',
        suggestedAssigneeUserId: 9,
        suggestedAssigneeName: 'Резервный исполнитель',
        suggestedAssigneeSource: 'configuredDefault',
        suggestedAssigneeTimeZoneId: 'Europe/Moscow',
        clientCaseId: 101,
        clientCaseTitle: 'ОЖИДАЕТ ПОРОГА',
        reason: 'Первый контроль через 11 дн.',
        activeTaskCount: 0,
        completedTaskCount: 0,
        daysWithoutActiveTask: 3,
        automationState: 'waiting',
        lastActivityAt: todayAt(8),
        lastActivitySource: 'document',
        lastTaskAt: null,
        nextControlAt: todayAt(9),
        daysUntilControl: 11,
        suggestedTitle: 'Проверить движение по делу',
        suggestedDescription: 'Автоконтроль',
        suggestedDueAt: todayAt(9),
      },
      {
        clientId: 12,
        clientName: 'Сидоров Сидор Сидорович',
        responsibleUserId: 8,
        responsibleName: 'Исполнитель',
        suggestedAssigneeUserId: 9,
        suggestedAssigneeName: 'Резервный исполнитель',
        suggestedAssigneeSource: 'configuredDefault',
        suggestedAssigneeTimeZoneId: 'Europe/Moscow',
        clientCaseId: 102,
        clientCaseTitle: 'ПРИОСТАНОВЛЕНО',
        reason: 'Дело приостановлено.',
        activeTaskCount: 0,
        completedTaskCount: 2,
        daysWithoutActiveTask: 30,
        automationState: 'paused',
        lastActivityAt: todayAt(8),
        lastActivitySource: 'courtEvent',
        lastTaskAt: null,
        nextControlAt: null,
        daysUntilControl: 0,
        responsibleVacationUntilDate: '2026-09-21',
        suggestedTitle: 'Проверить движение по делу',
        suggestedDescription: 'Автоконтроль',
        suggestedDueAt: todayAt(9),
      },
    ]);
    apiMock.getTaskJournalCaseAutomationSummary.mockResolvedValue({
      totalCount: 3,
      readyCount: 1,
      criticalCount: 1,
      waitingCount: 1,
      pausedCount: 1,
      nextStateChangeAt: new Date(Date.now() + 86_400_000).toISOString(),
      nextStateChangeKind: 'vacationResume',
    });

    const onOpenCase = vi.fn();
    await renderJournal([createTask()], { onOpenCase });

    expect(screen.getByRole('heading', { name: 'Все дела без активных задач' })).toBeTruthy();
    expect(await screen.findByLabelText('Всего дел без активных задач: 3')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Критические: 1' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Пора создать: 1' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Ожидают срока: 1' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Приостановлены: 1' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Создать наступившие (1)' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Обновить очередь автоконтроля' })).toBeTruthy();
    expect(screen.getByText(/Данные обновлены:/)).toBeTruthy();
    expect(screen.getByText(/возобновление после отпуска/)).toBeTruthy();
    expect(screen.getAllByText('Нет активных задач')).toHaveLength(3);
    expect(screen.getByText('Критично · без движения 35 дней')).toBeTruthy();
    expect(screen.getByText('Критический срок')).toBeTruthy();
    expect(screen.getByText('Ожидает · через 11 дней')).toBeTruthy();
    expect(screen.getByText('Приостановлено')).toBeTruthy();
    expect(screen.getAllByText('Почему дело здесь')).toHaveLength(3);
    expect(screen.getAllByText('Что произойдёт дальше')).toHaveLength(3);
    expect(screen.getByTestId('task-automation-reason-100').className).toContain('bg-rose-50/80');
    expect(screen.getByTestId('task-automation-next-step-100').className).toContain('bg-slate-50/90');
    expect(screen.getByTestId('task-automation-responsible-100').className).toContain('bg-blue-50');
    expect(screen.getByTestId('task-automation-responsible-101').className).toContain('bg-amber-50');
    expect(screen.getByTestId('task-automation-activity-100').className).toContain('bg-slate-50/90');
    expect(screen.getByTestId('task-automation-suggestion-100').className).toContain('bg-amber-50/50');
    expect(within(screen.getByTestId('task-automation-explanation-100')).getByText(
      'Срок уже наступил и стал критическим. Создайте контрольную задачу в первую очередь; при массовом действии это дело входит в «Пора создать».',
    )).toBeTruthy();
    expect(within(screen.getByTestId('task-automation-explanation-101')).getByText(
      /контрольная задача автоматически не создаётся\. Затем дело перейдёт в «Пора создать»\. Обычную задачу можно поставить вручную уже сейчас\./,
    )).toBeTruthy();
    expect(within(screen.getByTestId('task-automation-explanation-102')).getByText(
      'Автоконтроль приостановлен до 21.09.2026 включительно. После возвращения ответственного система пересчитает следующий срок.',
    )).toBeTruthy();
    expect(document.querySelector('[data-automation-state="ready"]')?.className).toContain('pp-automation-critical-card');
    expect(document.querySelector('[data-automation-critical="true"]')?.className).toContain('pp-automation-critical-card');
    expect(document.querySelector('[data-automation-state="waiting"]')?.className).toContain('border-l-amber-400');
    expect(document.querySelector('[data-automation-state="paused"]')?.className).toContain('border-l-slate-400');
    expect(screen.getByText(/Последнее движение:.*задача/)).toBeTruthy();
    expect(screen.getByText(/Последнее движение:.*документ/)).toBeTruthy();
    expect(screen.getByText(/Следующий контроль:/)).toBeTruthy();
    expect(screen.getByText('Ответственный отсутствует до 21.09.2026 включительно')).toBeTruthy();
    expect(screen.getByText('Ответственный: Администратор')).toBeTruthy();
    expect(screen.getByText('Ответственный: не назначен')).toBeTruthy();
    const waitingSuggestion = screen.getByTestId('task-automation-suggestion-101');
    expect(within(waitingSuggestion).getByText('При создании задачи')).toBeTruthy();
    expect(within(waitingSuggestion).getByText('Резервный исполнитель')).toBeTruthy();
    expect(within(waitingSuggestion).getByText('ответственный не назначен · резервный исполнитель')).toBeTruthy();
    expect(within(waitingSuggestion).getByText(/Москва \(UTC\+3\).*время исполнителя/)).toBeTruthy();
    const pausedSuggestion = screen.getByTestId('task-automation-suggestion-102');
    expect(within(pausedSuggestion).getByText('Резервный исполнитель')).toBeTruthy();
    expect(within(pausedSuggestion).getByText('ответственный недоступен · резервный исполнитель')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Открыть дело ГОТОВО К КОНТРОЛЮ' }));
    expect(onOpenCase).toHaveBeenCalledWith(100, 10);

    apiMock.getTaskJournalCaseAutomationCandidates.mockClear();
    apiMock.getTaskJournalCaseAutomationSummary.mockClear();
    fireEvent.click(screen.getByRole('button', { name: 'Критические: 1' }));
    await waitFor(() => expect(apiMock.getTaskJournalCaseAutomationCandidates).toHaveBeenCalledWith(
      expect.objectContaining({ offset: 0, automationState: 'critical', escalationDays: 30 }),
      expect.anything(),
    ));
    expect(apiMock.getTaskJournalCaseAutomationSummary).not.toHaveBeenCalled();
    expect(screen.getByRole('heading', { name: 'Критические дела без активных задач' })).toBeTruthy();
    expect(screen.getByLabelText('Критических дел: 1')).toBeTruthy();
    expect(document.querySelector('[data-automation-active-state="critical"]')?.className)
      .toContain('pp-automation-panel-critical');
    expect(screen.getByText('ГОТОВО К КОНТРОЛЮ')).toBeTruthy();
    expect(screen.queryByText('ОЖИДАЕТ ПОРОГА')).toBeNull();
    expect(screen.getByRole('button', { name: 'Создать критические (1)' })).toBeTruthy();

    apiMock.getTaskJournalCaseAutomationCandidates.mockClear();
    fireEvent.click(screen.getByRole('button', { name: 'Ожидают срока: 1' }));
    await waitFor(() => expect(apiMock.getTaskJournalCaseAutomationCandidates).toHaveBeenCalledWith(
      expect.objectContaining({ offset: 0, automationState: 'waiting' }),
      expect.anything(),
    ));
    expect(apiMock.getTaskJournalCaseAutomationSummary).not.toHaveBeenCalled();
    expect(screen.getByRole('heading', { name: 'Дела, ожидающие срока' })).toBeTruthy();
    expect(screen.getByLabelText('Дел, ожидающих срока: 1')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Срок ещё не наступил' }) as HTMLButtonElement).disabled).toBe(true);

    const waitingTaskButton = screen.getByTestId('task-automation-create-101') as HTMLButtonElement;
    expect(waitingTaskButton.disabled).toBe(false);
    expect(waitingTaskButton.textContent).toContain('Поставить задачу');

    apiMock.getTaskBillingContext.mockClear();
    fireEvent.click(waitingTaskButton);

    await waitFor(() => expect(apiMock.getTaskBillingContext).toHaveBeenCalledWith({
      clientId: 11,
      legalEntityId: undefined,
      clientCaseId: 101,
    }));
    expect(await screen.findByRole('heading', { name: 'Определите учёт задачи' })).toBeTruthy();
    expect(apiMock.createTaskJournalCaseAutomationTask).not.toHaveBeenCalled();
  });

  it('refreshes the automation queue when the next state change becomes due', async () => {
    const nextStateChangeAt = new Date(Date.now() + 100).toISOString();
    const waitingCandidate = {
      clientId: 10,
      clientName: 'Клиент ожидающего дела',
      clientCaseId: 100,
      clientCaseTitle: 'ОЖИДАЕТ АВТООБНОВЛЕНИЯ',
      reason: 'Первый контроль наступит совсем скоро.',
      activeTaskCount: 0,
      completedTaskCount: 0,
      daysWithoutActiveTask: 13,
      isReadyForControl: false,
      automationState: 'waiting' as const,
      lastActivityAt: todayAt(8),
      lastActivitySource: 'caseCreated',
      nextControlAt: nextStateChangeAt,
      daysUntilControl: 1,
      suggestedTitle: 'Проверить движение по делу',
      suggestedDescription: 'Автоконтроль',
      suggestedDueAt: todayAt(9),
    };
    const readyCandidate = {
      ...waitingCandidate,
      reason: 'Срок первого контроля наступил.',
      daysWithoutActiveTask: 14,
      isReadyForControl: true,
      automationState: 'ready' as const,
      daysUntilControl: 0,
    };
    apiMock.getTaskJournalCaseAutomationCandidates
      .mockResolvedValueOnce([waitingCandidate])
      .mockResolvedValueOnce([readyCandidate]);
    apiMock.getTaskJournalCaseAutomationSummary
      .mockResolvedValueOnce({
        totalCount: 1,
        readyCount: 0,
        criticalCount: 0,
        waitingCount: 1,
        pausedCount: 0,
        nextStateChangeAt,
        nextStateChangeKind: 'ready',
      })
      .mockResolvedValueOnce({
        totalCount: 1,
        readyCount: 1,
        criticalCount: 0,
        waitingCount: 0,
        pausedCount: 0,
        nextStateChangeAt: null,
        nextStateChangeKind: null,
      });

    await renderJournal();

    expect(await screen.findByText('Клиент ожидающего дела')).toBeTruthy();
    expect(screen.getByText(/Ближайшее изменение состояния:/)).toBeTruthy();
    expect(screen.getByText(/переход в «Пора создать»/)).toBeTruthy();
    await waitFor(
      () => expect(apiMock.getTaskJournalCaseAutomationCandidates).toHaveBeenCalledTimes(2),
      { timeout: 3_000 },
    );
    expect(await screen.findByText('Срок первого контроля наступил.')).toBeTruthy();
    expect(screen.getByText('Пора создать · без движения 14 дней')).toBeTruthy();
    expect(screen.queryByText(/Ближайшее изменение состояния:/)).toBeNull();
  });

  it('refreshes a stale automation queue after returning to the tab and on manual request', async () => {
    await renderJournal();

    const refreshButton = await screen.findByRole('button', { name: 'Обновить очередь автоконтроля' });
    await waitFor(() => expect(apiMock.getTaskJournalCaseAutomationCandidates).toHaveBeenCalledTimes(1));
    expect(screen.getByText(/Данные обновлены:/)).toBeTruthy();

    apiMock.getTaskJournalCaseAutomationCandidates.mockClear();
    apiMock.getTaskJournalCaseAutomationSummary.mockClear();
    const currentTime = Date.now();
    const nowSpy = vi.spyOn(Date, 'now').mockReturnValue(currentTime + 61_000);
    try {
      document.dispatchEvent(new Event('visibilitychange'));
      await waitFor(() => expect(apiMock.getTaskJournalCaseAutomationCandidates).toHaveBeenCalledTimes(1));
      await waitFor(() => expect((refreshButton as HTMLButtonElement).disabled).toBe(false));
    } finally {
      nowSpy.mockRestore();
    }

    apiMock.getTaskJournalCaseAutomationCandidates.mockClear();
    apiMock.getTaskJournalCaseAutomationSummary.mockClear();
    fireEvent.click(refreshButton);

    await waitFor(() => expect(apiMock.getTaskJournalCaseAutomationCandidates).toHaveBeenCalledTimes(1));
    expect(apiMock.getTaskJournalCaseAutomationSummary).toHaveBeenCalledTimes(1);
  });

  it('shows exact automation totals and loads the next bounded page', async () => {
    const candidate = (id: number) => ({
      clientId: 10,
      clientName: `Клиент ${id}`,
      clientCaseId: id,
      clientCaseTitle: `ДЕЛО ${id}`,
      reason: 'Нет активной задачи уже 20 дн.',
      activeTaskCount: 0,
      completedTaskCount: 0,
      daysWithoutActiveTask: 20,
      isReadyForControl: true,
      automationState: 'ready' as const,
      lastActivityAt: todayAt(8),
      lastActivitySource: 'caseCreated',
      lastTaskAt: null,
      nextControlAt: todayAt(9),
      daysUntilControl: 0,
      suggestedTitle: 'Проверить движение по делу',
      suggestedDescription: 'Автоконтроль',
      suggestedDueAt: todayAt(9),
    });
    apiMock.getTaskJournalCaseAutomationCandidates
      .mockResolvedValueOnce(Array.from({ length: 100 }, (_, index) => candidate(index + 1)))
      .mockResolvedValueOnce([candidate(101)]);
    apiMock.getTaskJournalCaseAutomationSummary.mockResolvedValue({
      totalCount: 101,
      readyCount: 101,
      waitingCount: 0,
      pausedCount: 0,
    });

    await renderJournal();

    expect(await screen.findByText('Загружено 100 из 101')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Показать загруженные (100)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Загрузить ещё (1)' }));

    await waitFor(() => expect(apiMock.getTaskJournalCaseAutomationCandidates).toHaveBeenCalledTimes(2));
    expect(apiMock.getTaskJournalCaseAutomationCandidates).toHaveBeenLastCalledWith(expect.objectContaining({
      limit: 100,
      offset: 100,
      automationState: 'all',
    }));
    expect(await screen.findByText('Загружено 101 из 101')).toBeTruthy();
    expect(screen.getByText('ДЕЛО 101')).toBeTruthy();
  });

  it.each(['legacy', 'modern'] as const)(
    'prefills the %s task card from a waiting automation candidate',
    async (taskCardWindowMode) => {
      const suggestedDueAt = todayAt(9);
      apiMock.getTaskJournalCaseAutomationCandidates.mockResolvedValue([{
        clientId: 10,
        clientName: 'Иванов Иван Иванович',
        responsibleUserId: null,
        responsibleName: '',
        suggestedAssigneeUserId: 7,
        suggestedAssigneeName: 'Администратор',
        suggestedAssigneeSource: 'currentUser',
        suggestedAssigneeTimeZoneId: 'Asia/Novosibirsk',
        clientCaseId: 100,
        clientCaseTitle: 'БАНКРОТСТВО',
        reason: 'Первый контроль через 5 дн.',
        activeTaskCount: 0,
        completedTaskCount: 0,
        daysWithoutActiveTask: 9,
        automationState: 'waiting',
        lastTaskAt: null,
        suggestedTitle: 'Проверить движение по делу',
        suggestedDescription: 'Уточнить текущее состояние дела и запланировать следующий шаг.',
        suggestedDueAt,
        suggestedPriority: 'high',
      }]);
      apiMock.getTaskJournalCaseAutomationSummary.mockResolvedValue({
        totalCount: 1,
        readyCount: 0,
        waitingCount: 1,
        pausedCount: 0,
      });

      await renderJournal([], {
        waitForTitle: null,
        settings: { taskCardWindowMode },
      });

      const suggestion = screen.getByTestId('task-automation-suggestion-100');
      expect(within(suggestion).getByText('При создании задачи')).toBeTruthy();
      expect(within(suggestion).getByText('Администратор')).toBeTruthy();
      expect(within(suggestion).getByText('ответственный не назначен · вы будете исполнителем')).toBeTruthy();
      expect(within(suggestion).getByText(/Новосибирск \(UTC\+7\).*время исполнителя/)).toBeTruthy();
      apiMock.getTaskBillingContext.mockClear();
      fireEvent.click(screen.getByTestId('task-automation-create-100'));
      await waitFor(() => expect(apiMock.getTaskBillingContext).toHaveBeenCalledWith({
        clientId: 10,
        legalEntityId: undefined,
        clientCaseId: 100,
      }));
      expect(await screen.findByRole('heading', { name: 'Определите учёт задачи' })).toBeTruthy();
      fireEvent.click(screen.getByRole('button', { name: /^Внутренняя задача/ }));

      const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
      expect((within(card).getByTestId('task-title-input') as HTMLInputElement).value).toBe('Проверить движение по делу');
      expect((within(card).getByTestId('task-description-field') as HTMLTextAreaElement).value).toBe(
        'Уточнить текущее состояние дела и запланировать следующий шаг.',
      );
      expect((within(card).getByTestId('task-due-at-input') as HTMLInputElement).value).toBe(
        toDatetimeLocalInputValue(new Date(suggestedDueAt)),
      );
      expect((within(card).getByTestId('task-assignee-select') as HTMLSelectElement).value).toBe('7');

      fireEvent.click(within(card).getByRole('button', { name: 'Создать задачу' }));

      await waitFor(() => expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1));
      expect(apiMock.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
        clientId: 10,
        legalEntityId: null,
        clientCaseId: 100,
        title: 'Проверить движение по делу',
        description: 'Уточнить текущее состояние дела и запланировать следующий шаг.',
        dueAt: suggestedDueAt,
        assigneeUserId: 7,
        assigneeName: 'Администратор',
        type: 'control',
        priority: 'high',
        status: 'new',
        plannedDurationMinutes: 60,
      }));
    },
  );

  it.each(['legacy', 'modern'] as const)(
    'does not prefill the %s task card with an unverified responsible user',
    async (taskCardWindowMode) => {
      apiMock.getTaskJournalCaseAutomationCandidates.mockResolvedValue([{
        clientId: 10,
        clientName: 'Клиент с недоступным ответственным',
        responsibleUserId: 8,
        responsibleName: 'Неактивный ответственный',
        clientCaseId: 100,
        clientCaseTitle: 'ДЕЛО БЕЗ БЕЗОПАСНОЙ РЕКОМЕНДАЦИИ',
        reason: 'Первый контроль через 5 дн.',
        activeTaskCount: 0,
        completedTaskCount: 0,
        daysWithoutActiveTask: 9,
        automationState: 'waiting',
        lastTaskAt: null,
        suggestedTitle: 'Проверить движение по делу',
        suggestedDescription: 'Уточнить текущее состояние дела.',
        suggestedDueAt: todayAt(9),
        suggestedPriority: 'normal',
      }]);
      apiMock.getTaskJournalCaseAutomationSummary.mockResolvedValue({
        totalCount: 1,
        readyCount: 0,
        waitingCount: 1,
        pausedCount: 0,
      });

      await renderJournal([], {
        waitForTitle: null,
        settings: { taskCardWindowMode },
      });

      const suggestion = screen.getByTestId('task-automation-suggestion-100');
      expect(within(suggestion).getByText('не определён')).toBeTruthy();
      apiMock.getTaskBillingContext.mockClear();
      fireEvent.click(screen.getByTestId('task-automation-create-100'));
      await waitFor(() => expect(apiMock.getTaskBillingContext).toHaveBeenCalledWith({
        clientId: 10,
        legalEntityId: undefined,
        clientCaseId: 100,
      }));
      expect(await screen.findByRole('heading', { name: 'Определите учёт задачи' })).toBeTruthy();
      fireEvent.click(screen.getByRole('button', { name: /^Внутренняя задача/ }));

      const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
      expect((within(card).getByTestId('task-assignee-select') as HTMLSelectElement).value).not.toBe('8');
    },
  );

  it('searches the complete automation queue on the server', async () => {
    const initialCandidate = {
      clientId: 10,
      clientName: 'Старый кандидат',
      clientCaseId: 100,
      clientCaseTitle: 'БАНКРОТСТВО',
      reason: 'Нет активной задачи',
      activeTaskCount: 0,
      completedTaskCount: 0,
      daysWithoutActiveTask: 20,
      automationState: 'ready' as const,
      suggestedTitle: 'Проверить движение по делу',
      suggestedDescription: 'Автоконтроль',
      suggestedDueAt: todayAt(9),
    };
    const searchedCandidate = {
      ...initialCandidate,
      clientId: 11,
      clientName: 'Найденный Петров',
      clientCaseId: 101,
      clientCaseTitle: 'НАСЛЕДСТВО',
    };
    apiMock.getTaskJournalCaseAutomationCandidates
      .mockResolvedValueOnce([initialCandidate])
      .mockResolvedValueOnce([searchedCandidate]);
    apiMock.getTaskJournalCaseAutomationSummary
      .mockResolvedValueOnce({ totalCount: 1, readyCount: 1, waitingCount: 0, pausedCount: 0 })
      .mockResolvedValueOnce({ totalCount: 1, readyCount: 1, waitingCount: 0, pausedCount: 0 });

    await renderJournal();
    fireEvent.change(screen.getByRole('searchbox', { name: 'Поиск в автоконтроле' }), {
      target: { value: 'Петров' },
    });
    expect(screen.queryByText('Старый кандидат')).toBeNull();
    expect((screen.getByRole('button', { name: 'Обновляем очередь...' }) as HTMLButtonElement).disabled).toBe(true);

    await waitFor(() => expect(apiMock.getTaskJournalCaseAutomationCandidates).toHaveBeenCalledTimes(2));
    expect(apiMock.getTaskJournalCaseAutomationCandidates).toHaveBeenLastCalledWith(
      expect.objectContaining({ search: 'Петров', offset: 0, automationState: 'all' }),
      expect.anything(),
    );
    expect(apiMock.getTaskJournalCaseAutomationSummary).toHaveBeenLastCalledWith(
      expect.objectContaining({ inactiveDays: 14, search: 'Петров' }),
      expect.anything(),
    );
    expect(await screen.findByText('Найденный Петров')).toBeTruthy();
    expect(screen.queryByText('Старый кандидат')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Создать наступившие (1)' }));
    await waitFor(() => expect(apiMock.createTaskJournalCaseAutomationTasks).toHaveBeenCalledWith({
      limit: 1,
      search: 'Петров',
    }));
  });

  it('shows an actionable error instead of an empty automation queue and retries loading', async () => {
    const recoveredCandidate = {
      clientId: 11,
      clientName: 'Восстановленный клиент',
      clientCaseId: 101,
      clientCaseTitle: 'ДЕЛО ПОСЛЕ ПОВТОРА',
      reason: 'Нет активной задачи уже 20 дн.',
      activeTaskCount: 0,
      completedTaskCount: 1,
      daysWithoutActiveTask: 20,
      isReadyForControl: true,
      automationState: 'ready' as const,
      lastTaskAt: null,
      suggestedTitle: 'Проверить движение по делу',
      suggestedDescription: 'Автоконтроль',
      suggestedDueAt: todayAt(9),
    };
    apiMock.getTaskJournalCaseAutomationCandidates.mockRejectedValue(new Error('network unavailable'));

    await renderJournal();

    expect((await screen.findByRole('alert')).textContent).toContain('Очередь автоконтроля временно недоступна');
    expect(screen.queryByText('В этой категории дел без активных задач нет.')).toBeNull();
    expect(screen.getAllByText('Тестовая задача').length).toBeGreaterThan(0);

    apiMock.getTaskJournalCaseAutomationCandidates.mockResolvedValue([recoveredCandidate]);
    apiMock.getTaskJournalCaseAutomationSummary.mockResolvedValue({
      totalCount: 1,
      readyCount: 1,
      waitingCount: 0,
      pausedCount: 0,
    });
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку' }));

    expect(await screen.findByText('Восстановленный клиент')).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('keeps automation cards available and labels partial counters when totals fail', async () => {
    apiMock.getTaskJournalCaseAutomationCandidates.mockResolvedValue([{
      clientId: 10,
      clientName: 'Клиент из неполной очереди',
      clientCaseId: 100,
      clientCaseTitle: 'ДЕЛО ИЗ ПЕРВОЙ СТРАНИЦЫ',
      reason: 'Нет активной задачи уже 20 дн.',
      activeTaskCount: 0,
      completedTaskCount: 1,
      daysWithoutActiveTask: 20,
      isReadyForControl: true,
      automationState: 'ready',
      lastTaskAt: null,
      suggestedTitle: 'Проверить движение по делу',
      suggestedDescription: 'Автоконтроль',
      suggestedDueAt: todayAt(9),
    }]);
    apiMock.getTaskJournalCaseAutomationSummary.mockRejectedValue(new Error('summary unavailable'));

    await renderJournal();

    expect(await screen.findByText('Уточняем общие счётчики')).toBeTruthy();
    expect(screen.getByText(/точные общие счётчики временно не получены/)).toBeTruthy();
    expect(screen.getByText('Клиент из неполной очереди')).toBeTruthy();
    expect(screen.getByText('Загружено 1 из 1')).toBeTruthy();
    expect((screen.getByTestId('task-automation-create-100') as HTMLButtonElement).disabled).toBe(false);

    apiMock.getTaskJournalCaseAutomationSummary.mockResolvedValue({
      totalCount: 1,
      readyCount: 1,
      waitingCount: 0,
      pausedCount: 0,
    });
    fireEvent.click(screen.getByRole('button', { name: 'Уточнить счётчики' }));

    await waitFor(() => expect(screen.queryByText('Уточняем общие счётчики')).toBeNull());
    expect(screen.getByText('Клиент из неполной очереди')).toBeTruthy();
  });

  it('keeps the last automation queue visible when background refresh fails', async () => {
    const candidate = {
      clientId: 10,
      clientName: 'Клиент из сохраненной очереди',
      clientCaseId: 100,
      clientCaseTitle: 'ДЕЛО БЕЗ АКТИВНОЙ ЗАДАЧИ',
      reason: 'Нет активной задачи уже 20 дн.',
      activeTaskCount: 0,
      completedTaskCount: 1,
      daysWithoutActiveTask: 20,
      isReadyForControl: true,
      automationState: 'ready' as const,
      lastTaskAt: null,
      suggestedTitle: 'Проверить движение по делу',
      suggestedDescription: 'Автоконтроль',
      suggestedDueAt: todayAt(9),
    };
    apiMock.getTaskJournalCaseAutomationCandidates
      .mockResolvedValueOnce([candidate])
      .mockRejectedValueOnce(new Error('network unavailable'));
    apiMock.getTaskJournalCaseAutomationSummary.mockResolvedValue({
      totalCount: 1,
      readyCount: 1,
      waitingCount: 0,
      pausedCount: 0,
    });

    await renderJournal();
    window.dispatchEvent(new CustomEvent(TASK_JOURNAL_EVENT));

    expect(await screen.findByText('Не удалось обновить очередь автоконтроля. Показаны ранее загруженные данные.')).toBeTruthy();
    expect(screen.getByText('Клиент из сохраненной очереди')).toBeTruthy();
    expect(screen.queryByText('В этой категории дел без активных задач нет.')).toBeNull();

    apiMock.getTaskJournalCaseAutomationCandidates.mockRejectedValueOnce(new Error('still unavailable'));
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку' }));

    expect(await screen.findByText('Не удалось обновить очередь автоконтроля. Показаны ранее загруженные данные.')).toBeTruthy();
    expect(screen.getByText('Клиент из сохраненной очереди')).toBeTruthy();

    const refreshedCandidate = {
      ...candidate,
      clientName: 'Клиент из обновленной очереди',
      reason: 'Нет активной задачи уже 21 дн.',
    };
    let resolveRetry: ((value: typeof candidate[]) => void) | undefined;
    apiMock.getTaskJournalCaseAutomationCandidates.mockImplementationOnce(() => new Promise((resolve) => {
      resolveRetry = resolve;
    }));
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку' }));

    expect(screen.getByText('Клиент из сохраненной очереди')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Обновляем очередь...' })).toBeTruthy();

    resolveRetry?.([refreshedCandidate]);
    expect(await screen.findByText('Клиент из обновленной очереди')).toBeTruthy();
    expect(screen.queryByText('Клиент из сохраненной очереди')).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
