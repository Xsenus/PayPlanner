import { setupTaskJournalTests, apiMock, toastMock, authState, permissionsState, todayAt, createTask, renderJournal, openFirstTaskForEdit } from './TaskJournal.test-support';
import { act, cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { TaskJournal } from './TaskJournal';
import { TASK_JOURNAL_REALTIME_EVENT } from '../../services/realtimeNotifications';
import { TASK_JOURNAL_EVENT, type TaskJournalItem } from '../../utils/taskJournalStore';

describe('TaskJournal', () => {
  setupTaskJournalTests();


  it('hides task export buttons when export permission is denied', async () => {
    authState.isAdmin = false;
    permissionsState.taskCanExport = false;

    await renderJournal();

    expect(screen.queryByRole('button', { name: 'CSV' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Word' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'PDF' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'HTML' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'ICS' })).toBeNull();
  });

  it('hides task creation controls when create permission is denied', async () => {
    authState.isAdmin = false;
    permissionsState.taskCanCreate = false;

    await renderJournal();

    expect(screen.queryByRole('button', { name: /Добавить задачу/i })).toBeNull();

    cleanup();
    window.localStorage.clear();
    await renderJournal(undefined, { initialView: 'calendar' });

    expect(screen.queryByTitle('Создать задачу на дату')).toBeNull();
    expect(apiMock.createTaskJournalItem).not.toHaveBeenCalled();
  });

  it('hides task delete controls when delete permission is denied', async () => {
    authState.isAdmin = false;
    authState.user = { id: 8, fullName: 'Постановщик', email: 'author@example.com' };
    permissionsState.taskCanDelete = false;
    permissionsState.taskCanDeleteOthers = false;

    await renderJournal([createTask({
      assigneeUserId: 8,
      assigneeName: 'Постановщик',
      authorUserId: 8,
      authorName: 'Постановщик',
      title: 'Задача без удаления',
    })], { waitForTitle: null });

    expect(screen.queryByTitle('Удалить')).toBeNull();

    const taskCheckbox = await screen.findByRole(
      'checkbox',
      { name: 'Выбрать задачу Задача без удаления' },
      { timeout: 15000 },
    );
    fireEvent.click(taskCheckbox);

    expect(screen.getByTestId('task-bulk-actions')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Удалить выбранные' })).toBeNull();
    expect(apiMock.deleteTaskJournalItem).not.toHaveBeenCalled();
    expect(apiMock.bulkDeleteTaskJournalItems).not.toHaveBeenCalled();
  });

  it('exports client planning report in Word format', async () => {
    await renderJournal(undefined, {
      contextFilter: {
        clientId: 10,
        objectName: 'Иванов Иван Иванович',
        title: 'Иванов Иван Иванович',
      },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Word' }));

    await waitFor(() => expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledWith(expect.objectContaining({
      format: 'word',
      status: 'active',
      clientId: 10,
    })));
    expect(toastMock.success).toHaveBeenCalledWith('Отчет по задачам выгружен в Word.', 'Отчет по задачам');
  });

  it('exports task journal CSV and planning reports with current filters', async () => {
    let exportedBlob: Blob | null = null;
    const createObjectUrl = vi.spyOn(URL, 'createObjectURL').mockImplementation((blob) => {
      exportedBlob = blob as Blob;
      return 'blob:task-export';
    });
    const revokeObjectUrl = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    const anchorClick = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);

    try {
      await renderJournal([
        createTask({
          title: 'Подготовить документы',
          clientId: 10,
          clientName: 'Иванов Иван Иванович',
          clientCaseId: 100,
          clientCaseTitle: 'БАНКРОТСТВО',
        }),
      ], {
        contextFilter: {
          clientId: 10,
          objectName: 'Иванов Иван Иванович',
          title: 'Иванов Иван Иванович',
        },
      });

      fireEvent.click(screen.getByRole('button', { name: 'CSV' }));

      expect(anchorClick).toHaveBeenCalledTimes(1);
      expect(revokeObjectUrl).toHaveBeenCalledWith('blob:task-export');
      expect(exportedBlob).not.toBeNull();
      const csv = await exportedBlob!.text();
      expect(csv).toContain('"Контрагент";"Дело";"Наименование задачи"');
      expect(csv).toContain('"Иванов Иван Иванович";"БАНКРОТСТВО";"Подготовить документы"');
      expect(toastMock.success).toHaveBeenCalledWith('Журнал задач экспортирован в CSV.', 'Журнал задач');

      fireEvent.click(screen.getByRole('button', { name: 'PDF' }));

      await waitFor(() => expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledWith(expect.objectContaining({
        format: 'pdf',
        status: 'active',
        clientId: 10,
      })));

      fireEvent.click(screen.getByRole('button', { name: 'HTML' }));

      await waitFor(() => expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledWith(expect.objectContaining({
        format: 'html',
        status: 'active',
        clientId: 10,
      })));
      expect(toastMock.success).toHaveBeenCalledWith('Отчет по задачам выгружен в PDF.', 'Отчет по задачам');
      expect(toastMock.success).toHaveBeenCalledWith('Отчет по задачам выгружен в HTML.', 'Отчет по задачам');
    } finally {
      createObjectUrl.mockRestore();
      revokeObjectUrl.mockRestore();
      anchorClick.mockRestore();
    }
  });

  it('shows case deadlines and court events in calendar mode', async () => {
    apiMock.getCaseCalendarSummary.mockResolvedValue({
      from: todayAt(0),
      to: todayAt(23),
      totalEventCount: 2,
      overdueCount: 0,
      warningCount: 1,
      courtEventCount: 1,
      proceduralDeadlineCount: 1,
      caseDeadlineCount: 0,
      taskCount: 0,
      nearestEvent: null,
    });
    apiMock.getCaseCalendarEvents.mockResolvedValue([
      {
        id: 'procedural-deadline:appeal:100',
        kind: 'procedural-deadline',
        sourceType: 'appeal',
        sourceId: null,
        title: 'Срок апелляции',
        startAt: todayAt(9),
        endAt: todayAt(10),
        allDay: true,
        clientCaseId: 100,
        caseTitle: 'БАНКРОТСТВО',
        clientName: 'Иванов Иван Иванович',
        legalEntityName: null,
        responsibleUserId: 7,
        responsibleName: 'Администратор',
        status: 'Open',
        priority: 'high',
        deadlineType: 'appeal',
        isOverdue: false,
        isWarning: true,
        courtName: 'Арбитражный суд',
        courtCaseNumber: 'А55-100/2026',
      },
    ]);

    await renderJournal([createTask()], { initialView: 'calendar', waitForTitle: null });

    expect(await screen.findByText('Сроки и суд')).toBeTruthy();
    expect(await screen.findByText('Срок апелляции')).toBeTruthy();
    expect(screen.getByText('Процессуальный срок')).toBeTruthy();
    expect(screen.getByText('2 событий')).toBeTruthy();
    expect(screen.getByText('Сроков: 1')).toBeTruthy();
    expect(screen.getByText('Судов: 1')).toBeTruthy();
    expect(apiMock.getCaseCalendarEvents).toHaveBeenCalledWith(expect.objectContaining({ includeTasks: false }));
    expect(apiMock.getCaseCalendarSummary).toHaveBeenCalledWith(expect.objectContaining({ includeTasks: false }));
    const calendarParams = apiMock.getCaseCalendarEvents.mock.calls.at(-1)?.[0];
    expect(calendarParams?.from).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(calendarParams?.to).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });

  it('formats case calendar timed events with the profile time zone', async () => {
    apiMock.getUserSetting.mockImplementation(async (key: string) => (
      key === 'profile.timeZoneId'
        ? { key, value: 'Europe/Samara' }
        : null
    ));
    apiMock.getCaseCalendarSummary.mockResolvedValue({
      from: '2026-06-05T00:00:00Z',
      to: '2026-06-05T23:59:59Z',
      totalEventCount: 1,
      overdueCount: 0,
      warningCount: 0,
      courtEventCount: 1,
      proceduralDeadlineCount: 0,
      caseDeadlineCount: 0,
      taskCount: 0,
      nearestEvent: null,
    });
    apiMock.getCaseCalendarEvents.mockResolvedValue([
      {
        id: 'court:hearing:100',
        kind: 'court',
        sourceType: 'hearing',
        sourceId: null,
        title: 'Судебное заседание',
        startAt: '2026-06-05T12:00:00Z',
        endAt: '2026-06-05T13:00:00Z',
        allDay: false,
        clientCaseId: 100,
        caseTitle: 'Чудайкин - С/З',
        clientName: 'Иванов Иван Иванович',
        legalEntityName: null,
        responsibleUserId: 7,
        responsibleName: 'Администратор',
        status: 'Open',
        priority: 'normal',
        deadlineType: 'hearing',
        isOverdue: false,
        isWarning: false,
        courtName: 'Арбитражный суд',
        courtCaseNumber: 'А55-100/2026',
      },
    ]);

    await renderJournal([createTask()], { initialView: 'calendar' });

    expect(await screen.findByText('Судебное заседание')).toBeTruthy();
    expect(screen.getByText('05 июня 2026 г. 16:00')).toBeTruthy();
  });

  it('exports case calendar events to ICS with current calendar filters', async () => {
    await renderJournal([createTask()], { initialView: 'calendar', waitForTitle: null });

    const panel = screen.getByText('Календарь дел').closest('section');
    expect(panel).not.toBeNull();
    fireEvent.change(within(panel as HTMLElement).getByTestId('case-calendar-ics-reminder-preset'), { target: { value: 'twoHours' } });
    fireEvent.click(within(panel as HTMLElement).getByRole('button', { name: 'ICS' }));

    await waitFor(() => expect(apiMock.downloadCaseCalendarIcs).toHaveBeenCalledWith(expect.objectContaining({
      includeTasks: false,
      reminderMinutes: [120, 30],
    })));
    expect(toastMock.success).toHaveBeenCalledWith('Календарь дел выгружен в ICS.', 'Календарь дел');
  });

  it('filters case calendar events by selected assignee', async () => {
    await renderJournal([createTask()], { initialView: 'calendar' });

    fireEvent.change(screen.getByLabelText('Исполнитель'), { target: { value: '7' } });

    await waitFor(() => {
      expect(apiMock.getCaseCalendarEvents).toHaveBeenLastCalledWith(expect.objectContaining({
        includeTasks: false,
        responsibleUserId: 7,
      }));
      expect(apiMock.getCaseCalendarSummary).toHaveBeenLastCalledWith(expect.objectContaining({
        includeTasks: false,
        responsibleUserId: 7,
      }));
    });
  });

  it('updates selected tasks through the bulk actions panel', async () => {
    const firstTask = createTask({ id: 101, title: 'Первая задача' });
    const secondTask = createTask({ id: 102, title: 'Вторая задача' });

    await renderJournal([firstTask, secondTask], {
      waitForTitle: 'Первая задача',
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 9, name: 'Юрист отдела', email: 'lawyer@example.com' },
      ],
    });

    fireEvent.click(screen.getByRole('checkbox', { name: 'Выбрать задачу Первая задача' }));

    expect(screen.getByTestId('task-bulk-actions')).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Массово назначить исполнителя'), { target: { value: '9' } });
    fireEvent.change(screen.getByLabelText('Массово изменить приоритет'), { target: { value: 'urgent' } });
    fireEvent.click(screen.getByRole('button', { name: 'Применить' }));

    await waitFor(() => expect(apiMock.bulkUpdateTaskJournalItems).toHaveBeenCalledTimes(1));
    expect(apiMock.bulkUpdateTaskJournalItems).toHaveBeenCalledWith({
      ids: [101],
      assigneeUserId: 9,
      priority: 'urgent',
    });
    expect(toastMock.success).toHaveBeenCalledWith(expect.stringContaining('Обновлено задач: 1'), 'Журнал задач');
  });

  it('opens shared billing for editable selected page rows with exact tokens and no unrelated bulk fields', async () => {
    const task = createTask({ id: 101, title: 'Изменить только учёт', updatedAt: '2026-10-24T01:02:03.1234567Z' });
    await renderJournal([task], { waitForTitle: task.title });
    apiMock.bulkUpdateTaskJournalItems.mockResolvedValueOnce({ requested: 1, updated: 1, unchanged: 0, skipped: 0, conflicted: 0,
      items: [{ id: 101, outcome: 'updated', reason: null, updatedAt: '2026-10-24T02:03:04.7654321Z' }] });
    fireEvent.click(screen.getByRole('checkbox', { name: `Выбрать задачу ${task.title}` }));
    fireEvent.change(screen.getByLabelText('Массово изменить приоритет'), { target: { value: 'urgent' } });
    fireEvent.click(screen.getByRole('button', { name: 'Изменить учёт выбранных' }));
    fireEvent.click(screen.getByRole('button', { name: 'Изменить учёт' }));
    await screen.findByText(/Изменено: 1.*Уже установлен нужный учёт/);
    expect(apiMock.bulkUpdateTaskJournalItems).toHaveBeenCalledExactlyOnceWith({ ids: [101], billingClassification: 'internal', includeCompleted: false,
      expectedUpdatedAtById: { '101': task.updatedAt } });
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
    expect(apiMock.updateTaskBillingClassification).not.toHaveBeenCalled();
  });

  it('allows billing of an editable foreign task with canEditOthers only', async () => {
    authState.isAdmin = false; permissionsState.taskCanEdit = false; permissionsState.taskCanEditOthers = true;
    const task = createTask({ assigneeUserId: 9, authorUserId: 8, title: 'Чужая разрешённая задача' });
    await renderJournal([task], { waitForTitle: task.title });
    apiMock.bulkUpdateTaskJournalItems.mockResolvedValueOnce({ requested: 1, updated: 1, unchanged: 0, skipped: 0, conflicted: 0,
      items: [{ id: task.id, outcome: 'updated', reason: null, updatedAt: '2026-10-24T02:03:04.7654321Z' }] });
    fireEvent.click(screen.getByRole('checkbox', { name: `Выбрать задачу ${task.title}` }));
    fireEvent.click(screen.getByRole('button', { name: 'Изменить учёт выбранных' }));
    fireEvent.click(screen.getByRole('button', { name: 'Изменить учёт' }));
    await waitFor(() => expect(apiMock.bulkUpdateTaskJournalItems).toHaveBeenCalledOnce());
    expect(apiMock.bulkUpdateTaskJournalItems.mock.calls[0][0]).toEqual(expect.objectContaining({ ids: [task.id], billingClassification: 'internal' }));
  });

  it.each(['legacy', 'modern'] as const)('refreshes billing after bulk without saving or replacing the draft in the %s card', async (taskCardWindowMode) => {
    const task = createTask({ clientBillingClassification: 'billable', billingStatus: 'unbilled' });
    await renderJournal([task], { settings: { taskCardWindowMode } });
    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.change(within(card).getByTestId('task-title-input'), { target: { value: 'Несохранённый черновик названия' } });
    apiMock.getTaskJournalItem.mockResolvedValueOnce({ ...task, clientBillingClassification: 'internal', billingStatus: 'internal', updatedAt: '2026-10-24T02:03:04.7654321Z' });
    act(() => window.dispatchEvent(new CustomEvent(TASK_JOURNAL_EVENT, { detail: { bulkBillingIds: [task.id] } })));
    await waitFor(() => expect(within(card).getAllByTestId(`task-billing-badge-${task.id}`).some((badge) => badge.textContent?.includes('НЕ ДЛЯ СЧЁТА'))).toBe(true));
    expect((within(card).getByTestId('task-title-input') as HTMLInputElement).value).toBe('Несохранённый черновик названия');
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();
  });

  describe('server billing card refresh', () => {
    async function openBillingDraft(taskCardWindowMode: 'legacy' | 'modern') {
      const task = createTask({ clientBillingClassification: 'billable', billingStatus: 'unbilled' });
      const view = await renderJournal([task], { settings: { taskCardWindowMode } });
      await openFirstTaskForEdit();
      const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
      fireEvent.change(within(card).getByTestId('task-title-input'), { target: { value: 'Несохранённый серверный черновик' } });
      apiMock.getTaskJournalItem.mockClear();
      return { task, view, card };
    }

    function expectRetainedDraft(card: HTMLElement, id: number, title = 'Несохранённый серверный черновик') {
      expect((within(card).getByTestId('task-title-input') as HTMLInputElement).value).toBe(title);
      expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
      expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();
      return within(card).getAllByTestId(`task-billing-badge-${id}`).map((badge) => badge.textContent ?? '').join(' ');
    }

    it.each(['legacy', 'modern'] as const)('refreshes billing from the real no-detail server signal while retaining the %s card draft', async (mode) => {
      const { task, card } = await openBillingDraft(mode);
      apiMock.getTaskJournalItem.mockResolvedValueOnce({ ...task, title: 'Чужое сохранённое название', result: 'Чужой сохранённый результат',
        clientBillingClassification: 'internal', billingStatus: 'internal', updatedAt: '2026-10-24T02:03:04.7654321Z' });
      act(() => window.dispatchEvent(new CustomEvent(TASK_JOURNAL_REALTIME_EVENT)));
      await waitFor(() => expect(expectRetainedDraft(card, task.id)).toContain('НЕ ДЛЯ СЧЁТА'));
      expect(apiMock.getTaskJournalItem).toHaveBeenCalledExactlyOnceWith(task.id);
    });

    it.each(['legacy', 'modern'] as const)('coalesces real/local bursts and retires an older billing response in the %s card', async (mode) => {
      const { task, card } = await openBillingDraft(mode);
      let finishOld!: (value: TaskJournalItem) => void;
      let finishFresh!: (value: TaskJournalItem) => void;
      apiMock.getTaskJournalItem
        .mockImplementationOnce(() => new Promise<TaskJournalItem>((resolve) => { finishOld = resolve; }))
        .mockImplementationOnce(() => new Promise<TaskJournalItem>((resolve) => { finishFresh = resolve; }));
      vi.useFakeTimers();
      try {
        act(() => {
          window.dispatchEvent(new CustomEvent(TASK_JOURNAL_REALTIME_EVENT));
          window.dispatchEvent(new CustomEvent(TASK_JOURNAL_REALTIME_EVENT));
          window.dispatchEvent(new CustomEvent(TASK_JOURNAL_EVENT, { detail: { bulkBillingIds: [task.id] } }));
        });
        await act(async () => { await vi.advanceTimersByTimeAsync(200); });
        expect(apiMock.getTaskJournalItem).toHaveBeenCalledTimes(1);
        act(() => {
          window.dispatchEvent(new CustomEvent(TASK_JOURNAL_REALTIME_EVENT));
          window.dispatchEvent(new CustomEvent(TASK_JOURNAL_REALTIME_EVENT));
        });
        await act(async () => { await vi.advanceTimersByTimeAsync(200); });
        expect(apiMock.getTaskJournalItem).toHaveBeenCalledTimes(1);
        await act(async () => {
          finishOld({ ...task, clientBillingClassification: 'internal', billingStatus: 'internal' });
        });
        expect(expectRetainedDraft(card, task.id)).toBe('ПЛАТНАЯ · НЕ ВЫСТАВЛЕН СЧЁТ');
        await act(async () => { await vi.advanceTimersByTimeAsync(200); });
        expect(apiMock.getTaskJournalItem).toHaveBeenCalledTimes(2);
        await act(async () => {
          finishFresh({ ...task, clientBillingClassification: 'internal', billingStatus: 'internal', updatedAt: '2026-10-24T02:04:05.7654321Z' });
        });
        expect(expectRetainedDraft(card, task.id)).toContain('НЕ ДЛЯ СЧЁТА');
        await act(async () => { await vi.advanceTimersByTimeAsync(400); });
        expect(apiMock.getTaskJournalItem.mock.calls.map(([id]) => id)).toEqual([task.id, task.id]);
      } finally {
        vi.useRealTimers();
      }
    });

    it.each(['legacy', 'modern'] as const)('does not postpone the billing read indefinitely during server signals in the %s card', async (mode) => {
      const { task, card } = await openBillingDraft(mode);
      apiMock.getTaskJournalItem.mockResolvedValueOnce({ ...task, clientBillingClassification: 'internal', billingStatus: 'internal' });
      vi.useFakeTimers();
      try {
        for (let i = 0; i < 4; i += 1) {
          act(() => window.dispatchEvent(new CustomEvent(TASK_JOURNAL_REALTIME_EVENT)));
          await act(async () => { await vi.advanceTimersByTimeAsync(50); });
        }
        expect(apiMock.getTaskJournalItem).toHaveBeenCalledExactlyOnceWith(task.id);
        expect(expectRetainedDraft(card, task.id)).toContain('НЕ ДЛЯ СЧЁТА');
      } finally {
        vi.useRealTimers();
      }
    });

    it.each(['legacy', 'modern'] as const)('retains the billing badge and draft after a real-event 403 in the %s card', async (mode) => {
      const { task, card } = await openBillingDraft(mode);
      apiMock.getTaskJournalItem.mockRejectedValueOnce(Object.assign(new Error('HTTP 403'), { status: 403 }));
      act(() => window.dispatchEvent(new CustomEvent(TASK_JOURNAL_REALTIME_EVENT)));
      await waitFor(() => expect(apiMock.getTaskJournalItem).toHaveBeenCalledExactlyOnceWith(task.id));
      await act(async () => { await Promise.resolve(); });
      expect(expectRetainedDraft(card, task.id)).toBe('ПЛАТНАЯ · НЕ ВЫСТАВЛЕН СЧЁТ');
    });

    it.each(['legacy', 'modern'] as const)('cancels a queued real-event read when the %s card unmounts', async (mode) => {
      const { view } = await openBillingDraft(mode);
      act(() => window.dispatchEvent(new CustomEvent(TASK_JOURNAL_REALTIME_EVENT)));
      view.unmount();
      await act(async () => { await new Promise((resolve) => window.setTimeout(resolve, 300)); });
      expect(apiMock.getTaskJournalItem).not.toHaveBeenCalled();
      expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
    });

    it.each((['legacy', 'modern'] as const).flatMap((mode) => (
      ['employee', 'company', 'permission', 'reopen', 'unmount'].map((scope) => [mode, scope] as const)
    )))('ignores a pending real-event billing response after the %s card changes %s scope', async (mode, scope) => {
      const { task, view, card } = await openBillingDraft(mode);
      let finish!: (value: TaskJournalItem) => void;
      apiMock.getTaskJournalItem.mockImplementationOnce(() => new Promise<TaskJournalItem>((resolve) => { finish = resolve; }));
      act(() => window.dispatchEvent(new CustomEvent(TASK_JOURNAL_REALTIME_EVENT)));
      await waitFor(() => expect(apiMock.getTaskJournalItem).toHaveBeenCalledExactlyOnceWith(task.id));
      let retainedCard = card;
      let retainedTitle = 'Несохранённый серверный черновик';
      if (scope === 'employee') {
        authState.user = { ...authState.user, id: 8 };
        view.rerender(<TaskJournal />);
      } else if (scope === 'company') {
        view.rerender(<TaskJournal contextFilter={{ legalEntityId: 20 }} />);
      } else if (scope === 'permission') {
        permissionsState.taskViewScope = 'own';
        view.rerender(<TaskJournal />);
      } else if (scope === 'reopen') {
        fireEvent.click(within(card).getByRole('button', { name: 'Отмена' }));
        await waitFor(() => expect(screen.queryByTestId(`task-${mode}-card-window`)).toBeNull());
        await openFirstTaskForEdit();
        retainedCard = await screen.findByTestId(`task-${mode}-card-window`);
        retainedTitle = 'Черновик после повторного открытия';
        fireEvent.change(within(retainedCard).getByTestId('task-title-input'), { target: { value: retainedTitle } });
      } else {
        view.unmount();
      }
      await act(async () => {
        finish({ ...task, title: 'Устаревшее название', clientBillingClassification: 'internal', billingStatus: 'internal' });
      });
      if (scope !== 'unmount') expect(expectRetainedDraft(retainedCard, task.id, retainedTitle)).toBe('ПЛАТНАЯ · НЕ ВЫСТАВЛЕН СЧЁТ');
      expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
      expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();
    });
  });

  it('sends selected tasks to done through the bulk actions panel', async () => {
    const task = createTask({ id: 101, title: 'Закрыть массово', result: '' });

    await renderJournal([task], { waitForTitle: 'Закрыть массово' });

    fireEvent.click(screen.getByRole('checkbox', { name: 'Выбрать задачу Закрыть массово' }));
    fireEvent.change(screen.getByLabelText('Массово изменить статус'), { target: { value: 'done' } });
    fireEvent.click(screen.getByRole('button', { name: 'Применить' }));

    await waitFor(() => expect(apiMock.bulkUpdateTaskJournalItems).toHaveBeenCalledTimes(1));
    expect(apiMock.bulkUpdateTaskJournalItems).toHaveBeenCalledWith({
      ids: [101],
      status: 'done',
    });
    expect(toastMock.success).toHaveBeenCalledWith(expect.stringContaining('Обновлено задач: 1'), 'Журнал задач');
  });

  it('asks for task result before quick completion', async () => {
    const task = createTask({
      id: 101,
      title: 'Закрыть с результатом',
      result: '',
      clientBillingClassification: 'billable',
      billingMode: 'Actual',
      billingStatus: 'unbilled',
    });

    await renderJournal([task], { waitForTitle: 'Закрыть с результатом' });

    fireEvent.click(screen.getByTitle('Завершить'));

    expect(await screen.findByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();
    expect(screen.getByTestId('task-completion-billing-notice').textContent).toContain('останется в очереди «Платные задачи без счёта»');
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();

    const resultInput = screen.getByPlaceholderText('Опишите подробно, что было сделано и какой результат был достигнут.') as HTMLTextAreaElement;
    expect(resultInput.className).toContain('w-full');
    expect(resultInput.className).toContain('min-h-[120px]');
    fireEvent.click(screen.getByRole('button', { name: 'Завершить задачу' }));
    expect(screen.getByText('Добавьте результат по задаче перед завершением.')).toBeTruthy();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();

    fireEvent.change(resultInput, { target: { value: 'Связались со следователем, документы переданы клиенту.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Завершить задачу' }));

    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(
      101,
      'done',
      'Связались со следователем, документы переданы клиенту.',
    ));
  });

  it('does not complete a task while the result upload is pending and retries the same selected file', async () => {
    await renderJournal([createTask({ id: 101, title: 'Задача с ожидающим файлом', result: '' })], { waitForTitle: 'Задача с ожидающим файлом' });
    apiMock.uploadTaskJournalFile.mockRejectedValueOnce(new Error('HTTP 503 Service Unavailable'));
    apiMock.getTaskFileUploadKey.mockReturnValue('pending-key');
    apiMock.getTaskFileUploadStates.mockResolvedValue([{ uploadKey: 'pending-key', fileName: 'итог.txt',
      status: 'waiting', fileId: null, message: 'Файл ожидает обязательных копий.' }]);
    fireEvent.click(screen.getByTitle('Завершить'));
    await screen.findByRole('heading', { name: 'Добавить результат по задаче' });
    const file = new File(['Результат'], 'итог.txt', { type: 'text/plain' });
    fireEvent.change(screen.getByTestId('task-completion-file-input'), { target: { files: [file] } });
    fireEvent.change(document.getElementById('task-completion-result')!, { target: { value: 'Работа выполнена.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Завершить задачу' }));
    expect(await screen.findByText(/Задача пока не завершена и не отправлена/)).toBeTruthy();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();
    apiMock.getTaskFileUploadStates.mockResolvedValue([{ uploadKey: 'pending-key', status: 'uploaded', fileId: 601 }]);
    apiMock.getTaskJournalFiles.mockResolvedValue([{ id: 601, taskJournalItemId: 101, documentKey: 'doc-1', version: 1,
      originalFileName: 'итог.txt', contentType: 'text/plain', sizeBytes: 9, uploadedByName: 'Автор', uploadedAt: todayAt(10), isLatestVersion: true }]);
    fireEvent.click(screen.getByRole('button', { name: 'Завершить задачу' }));
    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(101, 'done', 'Работа выполнена.'));
    expect(apiMock.uploadTaskJournalFile).toHaveBeenCalledTimes(1);
    expect(apiMock.uploadTaskJournalFile).toHaveBeenCalledWith(101, file, undefined);
  });

  it('keeps completion prompt open when result file upload fails', async () => {
    apiMock.uploadTaskJournalFile.mockRejectedValueOnce(new Error('Файл не загрузился'));
    await renderJournal([createTask({ id: 101, title: 'Задача с файлом', result: '' })], { waitForTitle: 'Задача с файлом' });

    fireEvent.click(screen.getByTitle('Завершить'));
    expect(await screen.findByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();

    const resultFile = new File(['Результат'], 'итог.txt', { type: 'text/plain' });
    fireEvent.change(screen.getByTestId('task-completion-file-input'), { target: { files: [resultFile] } });
    fireEvent.change(document.getElementById('task-completion-result')!, { target: { value: 'Работа выполнена.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Завершить задачу' }));

    expect(await screen.findByText('Файл не загрузился')).toBeTruthy();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();
    expect(screen.getByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();
  });

  it('completes a task without result when completion result is optional', async () => {
    const task = createTask({ id: 101, title: 'Закрыть без результата', result: '' });

    await renderJournal([task], {
      waitForTitle: 'Закрыть без результата',
      settings: { requireCompletionResult: false },
    });

    fireEvent.click(screen.getByTitle('Завершить'));

    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(101, 'done', undefined));
    expect(screen.queryByRole('heading', { name: 'Добавить результат по задаче' })).toBeNull();
  });

  it('shows a neutral skipped count after bulk actions', async () => {
    apiMock.bulkUpdateTaskJournalItems.mockResolvedValueOnce({ requested: 2, updated: 1, skipped: 1 });
    const firstTask = createTask({ id: 101, title: 'Обновить одну' });
    const secondTask = createTask({ id: 102, title: 'Пропустить одну' });

    await renderJournal([firstTask, secondTask], { waitForTitle: 'Обновить одну' });

    fireEvent.click(screen.getByRole('checkbox', { name: 'Выбрать задачу Обновить одну' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Выбрать задачу Пропустить одну' }));
    fireEvent.change(screen.getByLabelText('Массово изменить статус'), { target: { value: 'done' } });
    fireEvent.click(screen.getByRole('button', { name: 'Применить' }));

    await waitFor(() => expect(apiMock.bulkUpdateTaskJournalItems).toHaveBeenCalledTimes(1));
    expect(toastMock.success).toHaveBeenCalledWith('Обновлено задач: 1. Пропущено: 1.', 'Журнал задач');
  });

  it('deletes selected tasks through the bulk actions panel', async () => {
    const firstTask = createTask({ id: 101, title: 'Первая задача' });
    const secondTask = createTask({ id: 102, title: 'Вторая задача' });

    await renderJournal([firstTask, secondTask], { waitForTitle: 'Первая задача' });

    fireEvent.click(screen.getByRole('checkbox', { name: 'Выбрать задачу Первая задача' }));
    fireEvent.click(screen.getByRole('button', { name: 'Удалить выбранные' }));

    await waitFor(() => expect(apiMock.bulkDeleteTaskJournalItems).toHaveBeenCalledTimes(1));
    expect(apiMock.bulkDeleteTaskJournalItems).toHaveBeenCalledWith([101]);
    expect(toastMock.success).toHaveBeenCalledWith(expect.stringContaining('Удалено задач: 1'), 'Журнал задач');
  });

  it('shows a neutral skipped count after bulk delete', async () => {
    apiMock.bulkDeleteTaskJournalItems.mockResolvedValueOnce({ requested: 2, updated: 1, skipped: 1 });
    const firstTask = createTask({ id: 101, title: 'Удалить одну' });
    const secondTask = createTask({ id: 102, title: 'Оставить одну' });

    await renderJournal([firstTask, secondTask], { waitForTitle: 'Удалить одну' });

    fireEvent.click(screen.getByRole('checkbox', { name: 'Выбрать задачу Удалить одну' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Выбрать задачу Оставить одну' }));
    fireEvent.click(screen.getByRole('button', { name: 'Удалить выбранные' }));

    await waitFor(() => expect(apiMock.bulkDeleteTaskJournalItems).toHaveBeenCalledTimes(1));
    expect(toastMock.success).toHaveBeenCalledWith('Удалено задач: 1. Пропущено: 1.', 'Журнал задач');
  });

  it('opens client card from the counterparty link in the journal', async () => {
    const onOpenClient = vi.fn();
    const task = createTask({
      clientId: 42,
      clientCaseId: 314,
      clientName: 'Solomanin Oleg',
      objectName: 'Solomanin Oleg - Bankruptcy',
    });

    await renderJournal([task], { onOpenClient });

    fireEvent.click(screen.getByRole('button', { name: 'Solomanin Oleg' }));

    expect(onOpenClient).toHaveBeenCalledWith(42, 314);
  });

  it('opens case card from the case title in the journal', async () => {
    const onOpenCase = vi.fn();
    const task = createTask({
      clientId: 42,
      clientCaseId: 314,
      clientName: 'Solomanin Oleg',
      clientCaseTitle: 'Bankruptcy',
      objectName: 'Solomanin Oleg - Bankruptcy',
    });

    await renderJournal([task], { onOpenCase });

    fireEvent.click(screen.getByRole('button', { name: 'Bankruptcy' }));

    expect(onOpenCase).toHaveBeenCalledWith(314, 42);
  });

  it('opens legal entity card from the counterparty link in the journal', async () => {
    const onOpenLegalEntity = vi.fn();
    const task = createTask({
      clientId: null,
      clientName: '',
      legalEntityId: 20,
      legalEntityName: 'OOO Romashka',
      objectName: 'OOO Romashka',
    });

    await renderJournal([task], { onOpenLegalEntity });

    fireEvent.click(screen.getByRole('button', { name: 'OOO Romashka' }));

    expect(onOpenLegalEntity).toHaveBeenCalledWith(20);
  });

  it('completes an active task with an existing result from the quick action', async () => {
    await renderJournal([createTask({ result: 'Результат уже заполнен.' })]);

    apiMock.getTaskJournalItems.mockClear();
    apiMock.getTaskJournalAssignees.mockClear();
    apiMock.getTaskJournalSettings.mockClear();
    apiMock.getCalendarSyncSettings.mockClear();
    apiMock.getUserSetting.mockClear();

    fireEvent.click(screen.getAllByTitle('Завершить')[0]);

    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(101, 'done', undefined);
    expect(apiMock.getTaskJournalAssignees).not.toHaveBeenCalled();
    expect(apiMock.getTaskJournalSettings).not.toHaveBeenCalled();
    expect(apiMock.getCalendarSyncSettings).not.toHaveBeenCalled();
    expect(apiMock.getUserSetting).not.toHaveBeenCalled();
    expect(toastMock.error).not.toHaveBeenCalledWith('Добавьте результат по задаче перед завершением.', 'Журнал задач');
  });
});
