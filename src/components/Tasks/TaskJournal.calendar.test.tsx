import { setupTaskJournalTests, apiMock, toastMock, authState, permissionsState, todayAt, dateKey, dateAt, getSameWeekDateOutsideTodayForTest, createDataTransfer, createTask, renderJournal } from './TaskJournal.test-support';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { fromTimeZoneDateTimeInput, getZonedParts, toTimeZoneDateKey } from '../../utils/timeZoneDate';
import { loadAllTaskJournalItems } from './loadAllTaskJournalItems';
import { TASK_JOURNAL_EVENT } from '../../utils/taskJournalStore';

describe('TaskJournal', () => {
  setupTaskJournalTests();

  it('rejects a calendar load when an empty page arrives before the reported total', async () => {
    apiMock.getTaskJournalItems.mockResolvedValueOnce({ items: [createTask()], page: 1, pageSize: 200, total: 2 });
    apiMock.getTaskJournalItems.mockResolvedValueOnce({ items: [], page: 2, pageSize: 200, total: 2 });
    await expect(loadAllTaskJournalItems()).rejects.toThrow('неполную выборку');
  });

  it('rejects a changing total and stops paging an aborted calendar request', async () => {
    apiMock.getTaskJournalItems.mockResolvedValueOnce({ items: [createTask()], page: 1, pageSize: 200, total: 2 });
    apiMock.getTaskJournalItems.mockResolvedValueOnce({ items: [], page: 2, pageSize: 200, total: 1 });
    await expect(loadAllTaskJournalItems()).rejects.toThrow('изменилась');
    apiMock.getTaskJournalItems.mockReset();
    const controller = new AbortController();
    apiMock.getTaskJournalItems.mockImplementationOnce(async () => {
      controller.abort();
      return { items: [createTask()], page: 1, pageSize: 200, total: 2 };
    });
    await expect(loadAllTaskJournalItems(undefined, false, true, 'dueAt', 'asc', false, controller.signal)).rejects.toMatchObject({ name: 'AbortError' });
    expect(apiMock.getTaskJournalItems).toHaveBeenCalledTimes(1);
  });

  it('rejects repeated task IDs across calendar pages instead of counting them twice', async () => {
    const first = createTask({ id: 101 });
    apiMock.getTaskJournalItems.mockResolvedValueOnce({ items: [first], page: 1, pageSize: 200, total: 2 });
    apiMock.getTaskJournalItems.mockResolvedValueOnce({ items: [first], page: 2, pageSize: 200, total: 2 });
    await expect(loadAllTaskJournalItems()).rejects.toThrow('повторилась');
  });

  it('rejects a growing total and duplicate IDs on a single calendar page', async () => {
    apiMock.getTaskJournalItems.mockResolvedValueOnce({ items: [createTask({ id: 101 })], page: 1, pageSize: 200, total: 2 });
    apiMock.getTaskJournalItems.mockResolvedValueOnce({ items: [createTask({ id: 102 })], page: 2, pageSize: 200, total: 3 });
    await expect(loadAllTaskJournalItems()).rejects.toThrow('изменилась');
    apiMock.getTaskJournalItems.mockReset();
    const task = createTask({ id: 101 });
    apiMock.getTaskJournalItems.mockResolvedValueOnce({ items: [task, task], page: 1, pageSize: 200, total: 2 });
    await expect(loadAllTaskJournalItems()).rejects.toThrow('повторилась');
  });

  it('loads distinct tasks across all calendar pages in order', async () => {
    apiMock.getTaskJournalItems.mockResolvedValueOnce({ items: [createTask({ id: 101 })], page: 1, pageSize: 200, total: 2 });
    apiMock.getTaskJournalItems.mockResolvedValueOnce({ items: [createTask({ id: 102 })], page: 2, pageSize: 200, total: 2 });
    const result = await loadAllTaskJournalItems();
    expect(result.map((task) => task.id)).toEqual([101, 102]);
  });

  it('warns when a calendar refresh returns an incomplete page and clears the warning on retry', async () => {
    await renderJournal([createTask({ id: 101, title: 'Прежняя задача' })], { initialView: 'calendar', waitForTitle: null });
    await screen.findByTestId('task-calendar-task-101');
    apiMock.getTaskJournalItems.mockReset();
    apiMock.getTaskJournalItems.mockResolvedValueOnce({ items: [createTask({ id: 102, title: 'Частичная задача' })], page: 1, pageSize: 200, total: 2 });
    apiMock.getTaskJournalItems.mockResolvedValueOnce({ items: [], page: 2, pageSize: 200, total: 2 });
    fireEvent.click(screen.getByRole('button', { name: /Нагрузка/i }));
    expect((await screen.findByRole('alert')).textContent).toContain('могут быть неполными или устаревшими');
    expect(screen.queryByTestId('task-timeline-task-102')).toBeNull();
    apiMock.getTaskJournalItems.mockResolvedValueOnce({ items: [createTask({ id: 103, title: 'Полная задача' })], page: 1, pageSize: 200, total: 1 });
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку' }));
    await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
    expect(await screen.findByTestId('task-timeline-task-103')).toBeTruthy();
  });


  it('renders task calendar as a journal subgroup', async () => {
    await renderJournal();

    fireEvent.click(screen.getByRole('button', { name: /Календарь/i }));

    expect(screen.getByText('Календарь задач')).toBeTruthy();
    const calendarTask = await screen.findByTestId('task-calendar-task-101');
    expect(screen.getAllByText('Тестовая задача').length).toBeGreaterThan(0);
    expect(screen.getByTestId('task-calendar-mobile-strip')).toBeTruthy();
    expect(screen.getByTestId('task-calendar-layout').className).toContain('grid-cols-[minmax(0,1fr)]');
    expect(screen.getByTestId('task-calendar-layout').className).toContain('min-w-0');
    expect(screen.getByTestId('task-calendar-grid-scroll').className).toContain('max-w-full');
    expect(screen.getByTestId('task-calendar-grid-scroll').className).toContain('overflow-x-auto');
    expect(calendarTask.getAttribute('title')).toContain('Статус: Новая');
    expect(calendarTask.getAttribute('title')).toContain('Исполнитель: Администратор');
    expect(calendarTask.getAttribute('title')).toContain('Поручил: Постановщик');
    expect(screen.getByTestId('task-calendar-tooltip-101').textContent).toContain('Дело: БАНКРОТСТВО');
    expect(screen.getByTestId('task-calendar-tooltip-101').textContent).toContain('Поручил: Постановщик');
  });

  it('can open directly in calendar mode from the journal submenu', async () => {
    await renderJournal([createTask()], { initialView: 'calendar' });

    expect(screen.getByText('Календарь задач')).toBeTruthy();
    expect(screen.getByText('Загрузка за день')).toBeTruthy();
  });

  it('opens quick create form from a calendar date', async () => {
    await renderJournal([createTask()], { initialView: 'calendar' });

    fireEvent.click(screen.getAllByTitle('Создать задачу на дату')[0]);

    expect(await screen.findByRole('heading', { name: 'Выберите клиента и дело' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Создать общую внутреннюю задачу без клиента' }));
    expect(await screen.findByRole('heading', { name: 'Новая задача' })).toBeTruthy();
    const dueInput = document.querySelector('input[type="datetime-local"]') as HTMLInputElement | null;
    expect(dueInput?.value).toMatch(/T09:00$/);
  });

  it('moves a task to another calendar day by drag and drop', async () => {
    await renderJournal([createTask()], { initialView: 'calendar' });

    fireEvent.click(screen.getByRole('button', { name: 'Неделя' }));
    const currentKey = dateKey(new Date());
    const targetDay = Array.from(document.querySelectorAll<HTMLElement>('[data-testid^="task-calendar-day-"]'))
      .find((element) => element.dataset.testid !== `task-calendar-day-${currentKey}`);
    expect(targetDay).toBeTruthy();
    const dataTransfer = createDataTransfer();

    fireEvent.dragStart(screen.getByTestId('task-calendar-task-101'), { dataTransfer });
    fireEvent.dragOver(targetDay!, { dataTransfer });
    fireEvent.drop(targetDay!, { dataTransfer });

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      clientId: 10,
      title: 'Тестовая задача',
      dueAt: expect.any(String),
    }));
    expect(apiMock.updateTaskJournalItem.mock.calls[0][1].dueAt).not.toEqual(createTask().dueAt);
  });

  it('keeps the local task time when calendar drag-and-drop uses the profile time zone', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-06-03T13:30:00.000Z'));
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'calendar',
      periodMode: 'week',
    }));
    apiMock.getUserSetting.mockImplementation(async (key: string) => (
      key === 'profile.timeZoneId'
        ? { key, value: 'Asia/Kamchatka' }
        : null
    ));
    const dueAt = fromTimeZoneDateTimeInput('2026-06-04T23:30', 'Asia/Kamchatka');

    await renderJournal([
      createTask({
        dueAt,
      }),
    ], { initialView: 'calendar', waitForTitle: null });

    const targetDay = await screen.findByTestId('task-calendar-day-2026-06-05');
    const dataTransfer = createDataTransfer();

    fireEvent.dragStart(screen.getByTestId('task-calendar-task-101'), { dataTransfer });
    fireEvent.dragOver(targetDay, { dataTransfer });
    fireEvent.drop(targetDay, { dataTransfer });

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    const [, payload] = apiMock.updateTaskJournalItem.mock.calls[0];
    expect(payload.dueAt).toBe(fromTimeZoneDateTimeInput('2026-06-05T23:30', 'Asia/Kamchatka'));
    expect(getZonedParts(payload.dueAt, 'Asia/Kamchatka')).toMatchObject({
      year: 2026,
      month: 6,
      day: 5,
      hour: 23,
      minute: 30,
    });
  });

  it('groups calendar tasks by the user profile time zone', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-06-03T13:30:00.000Z'));
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'calendar',
      periodMode: 'week',
      statusFilter: 'all',
    }));
    apiMock.getUserSetting.mockImplementation(async (key: string) => (
      key === 'profile.timeZoneId'
        ? { key, value: 'Europe/Moscow' }
        : null
    ));
    const profileDateKey = toTimeZoneDateKey(new Date(), 'Europe/Moscow');
    const dueAt = fromTimeZoneDateTimeInput(`${profileDateKey}T23:30`, 'Europe/Moscow');

    await renderJournal([
      createTask({
        title: 'Поздняя задача профиля',
        dueAt,
      }),
    ], { initialView: 'calendar', waitForTitle: null });

    const profileDay = await screen.findByTestId(`task-calendar-day-${profileDateKey}`);
    await waitFor(
      () => expect(within(profileDay).getByTestId('task-calendar-task-101')).toBeTruthy(),
      { timeout: 5000 },
    );
  });

  it('opens the task calendar on today from the user profile time zone', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-06-03T13:30:00.000Z'));
    apiMock.getUserSetting.mockImplementation(async (key: string) => (
      key === 'profile.timeZoneId'
        ? { key, value: 'Asia/Kamchatka' }
        : null
    ));
    const profileDateKey = toTimeZoneDateKey(new Date(), 'Asia/Kamchatka');
    const dueAt = fromTimeZoneDateTimeInput(`${profileDateKey}T10:00`, 'Asia/Kamchatka');

    await renderJournal([
      createTask({
        title: 'Profile timezone task',
        dueAt,
      }),
    ], { initialView: 'calendar', periodMode: 'day' });

    const profileDay = await screen.findByTestId(`task-calendar-day-${profileDateKey}`);
    expect(profileDay.className).toContain('border-blue-300');
    expect(within(profileDay).getByTestId('task-calendar-task-101')).toBeTruthy();
    await waitFor(() => expect(apiMock.getCaseCalendarEvents).toHaveBeenCalledWith(expect.objectContaining({
      from: profileDateKey,
      to: profileDateKey,
    })));
  });

  it('uses the selected assignee profile time zone when an admin views the task calendar', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-06-03T13:30:00.000Z'));
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'calendar',
      periodMode: 'day',
      assigneeFilter: '8',
    }));
    apiMock.getUserSetting.mockImplementation(async (key: string) => (
      key === 'profile.timeZoneId'
        ? { key, value: 'Europe/Moscow' }
        : null
    ));
    const assigneeDateKey = toTimeZoneDateKey(new Date(), 'Asia/Kamchatka');
    const dueAt = fromTimeZoneDateTimeInput(`${assigneeDateKey}T10:00`, 'Asia/Kamchatka');

    await renderJournal([
      createTask({
        assigneeUserId: 8,
        assigneeName: 'Исполнитель Камчатка',
        dueAt,
        participants: [
          { userId: 8, role: 'assignee', displayName: 'Исполнитель Камчатка', approvalStatus: 'pending' },
        ],
      }),
    ], {
      initialView: 'calendar',
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com', timeZoneId: 'Europe/Moscow' },
        { id: 8, name: 'Исполнитель Камчатка', email: 'kamchatka@example.com', timeZoneId: 'Asia/Kamchatka' },
      ],
      waitForTitle: null,
    });

    const assigneeDay = await screen.findByTestId(`task-calendar-day-${assigneeDateKey}`);
    expect(assigneeDay.className).toContain('border-blue-300');
    expect(within(assigneeDay).getByTestId('task-calendar-task-101')).toBeTruthy();
    await waitFor(() => expect(apiMock.getCaseCalendarEvents).toHaveBeenCalledWith(expect.objectContaining({
      from: assigneeDateKey,
      to: assigneeDateKey,
      responsibleUserId: 8,
    })));
  });

  it('uses the selected assignee profile time zone when the role can view employee task calendars', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-06-03T13:30:00.000Z'));
    authState.isAdmin = false;
    permissionsState.calendarViewScope = 'all';
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'calendar',
      periodMode: 'day',
      assigneeFilter: '8',
    }));
    apiMock.getUserSetting.mockImplementation(async (key: string) => (
      key === 'profile.timeZoneId'
        ? { key, value: 'Europe/Moscow' }
        : null
    ));
    const assigneeDateKey = toTimeZoneDateKey(new Date(), 'Asia/Kamchatka');
    const dueAt = fromTimeZoneDateTimeInput(`${assigneeDateKey}T10:00`, 'Asia/Kamchatka');

    await renderJournal([
      createTask({
        assigneeUserId: 8,
        assigneeName: 'Исполнитель Камчатка',
        dueAt,
        participants: [
          { userId: 8, role: 'assignee', displayName: 'Исполнитель Камчатка', approvalStatus: 'pending' },
        ],
      }),
    ], {
      initialView: 'calendar',
      assignees: [
        { id: 7, name: 'Секретарь', email: 'secretary@example.com', timeZoneId: 'Europe/Moscow' },
        { id: 8, name: 'Исполнитель Камчатка', email: 'kamchatka@example.com', timeZoneId: 'Asia/Kamchatka' },
      ],
      waitForTitle: null,
    });

    const assigneeDay = await screen.findByTestId(`task-calendar-day-${assigneeDateKey}`);
    expect(assigneeDay.className).toContain('border-blue-300');
    expect(within(assigneeDay).getByTestId('task-calendar-task-101')).toBeTruthy();
    await waitFor(() => expect(apiMock.getCaseCalendarEvents).toHaveBeenCalledWith(expect.objectContaining({
      from: assigneeDateKey,
      to: assigneeDateKey,
      responsibleUserId: 8,
    })));
  });

  it('limits task calendar rows to the current user when the calendar role scope is own', async () => {
    authState.isAdmin = false;
    permissionsState.taskViewScope = 'all';
    permissionsState.calendarViewScope = 'own';

    await renderJournal([
      createTask({
        id: 101,
        title: 'Своя задача',
        assigneeUserId: 7,
        assigneeName: 'Секретарь',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Секретарь', approvalStatus: 'pending' },
        ],
      }),
      createTask({
        id: 102,
        title: 'Чужая задача',
        assigneeUserId: 8,
        assigneeName: 'Другой исполнитель',
        participants: [
          { userId: 8, role: 'assignee', displayName: 'Другой исполнитель', approvalStatus: 'pending' },
        ],
      }),
    ], {
      initialView: 'calendar',
      waitForTitle: null,
    });

    await waitFor(() => expect(screen.getByTestId('task-calendar-task-101')).toBeTruthy());
    expect(screen.queryByTestId('task-calendar-task-102')).toBeNull();
    await waitFor(() => expect(apiMock.getCaseCalendarEvents).toHaveBeenCalledWith(expect.objectContaining({
      responsibleUserId: 7,
    })));
  });

  it('limits task calendar rows to the current department when the calendar role scope is department', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Секретарь', email: 'secretary@example.com', departmentId: 1, department: 'Офис' };
    permissionsState.taskViewScope = 'all';
    permissionsState.calendarViewScope = 'department';
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'calendar',
      periodMode: 'day',
      assigneeFilter: '8',
    }));

    await renderJournal([
      createTask({
        id: 101,
        title: 'Задача отдела',
        assigneeUserId: 8,
        assigneeName: 'Сотрудник отдела',
        participants: [
          { userId: 8, role: 'assignee', displayName: 'Сотрудник отдела', approvalStatus: 'pending' },
        ],
      }),
      createTask({
        id: 102,
        title: 'Задача другого отдела',
        assigneeUserId: 9,
        assigneeName: 'Другой отдел',
        participants: [
          { userId: 9, role: 'assignee', displayName: 'Другой отдел', approvalStatus: 'pending' },
        ],
      }),
    ], {
      initialView: 'calendar',
      assignees: [
        { id: 7, name: 'Секретарь', email: 'secretary@example.com', departmentId: 1, department: 'Офис' },
        { id: 8, name: 'Сотрудник отдела', email: 'team@example.com', departmentId: 1, department: 'Офис' },
        { id: 9, name: 'Другой отдел', email: 'other@example.com', departmentId: 2, department: 'Юристы' },
      ],
      waitForTitle: null,
    });

    await waitFor(() => expect(screen.getByTestId('task-calendar-task-101')).toBeTruthy());
    expect(screen.queryByTestId('task-calendar-task-102')).toBeNull();
    await waitFor(() => expect(apiMock.getCaseCalendarEvents).toHaveBeenCalledWith(expect.objectContaining({
      responsibleUserId: 8,
    })));
  });

  it('links an administrator from task calendar to the exact shared visibility setting', async () => {
    await renderJournal([], { initialView: 'calendar', waitForTitle: null });
    const link = await screen.findByRole('link', { name: 'Выполненные и отменённые задачи в календарях' });
    expect(link.getAttribute('href')).toBe('/?ppTab=settings&settingsSection=integrations&integrationTab=calendars#completed-task-calendar');
    fireEvent.click(screen.getByTestId('task-view-list'));
    expect(screen.queryByRole('link', { name: 'Выполненные и отменённые задачи в календарях' })).toBeNull();
  });

  it('does not expose the administrative visibility link to an employee', async () => {
    authState.isAdmin = false;
    await renderJournal([], { initialView: 'calendar', waitForTitle: null });
    expect(screen.queryByRole('link', { name: 'Выполненные и отменённые задачи в календарях' })).toBeNull();
  });

  it('hides completed and cancelled tasks without hiding active tasks when the calendar setting says to hide them', async () => {
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'calendar',
      periodMode: 'week',
      statusFilter: 'all',
    }));
    apiMock.getCalendarSyncSettings.mockResolvedValueOnce({
      externalCalendarSyncEnabled: false,
      googleCalendarSyncEnabled: false,
      yandexCalendarSyncEnabled: false,
      googleSyncIntervalMinutes: 10,
      yandexSyncIntervalMinutes: 10,
      manualFullSyncCooldownMinutes: 10,
      completedTaskCalendarAction: 'hide',
      completedTaskGoogleColorId: '8',
      syncMode: 'notConfigured',
      conflictResolutionMode: 'manualConfirmation',
      userControlMode: 'userManaged',
      defaultSyncFromDate: null,
      inactiveUserAction: 'cleanupExternalData',
      requireManualConfirmation: true,
      dryRunConfirmed: false,
      notes: '',
      updatedAt: null,
    });

    await renderJournal([
      createTask({ id: 101, status: 'done', result: 'Готово', dueAt: todayAt(11) }),
      createTask({ id: 102, status: 'cancelled', dueAt: todayAt(12) }),
      createTask({ id: 103, status: 'new', dueAt: todayAt(13) }),
    ], { initialView: 'calendar', waitForTitle: null });

    expect(await screen.findByTestId('task-calendar-task-103')).toBeTruthy();
    expect(screen.queryByTestId('task-calendar-task-101')).toBeNull();
    expect(screen.queryByTestId('task-calendar-task-102')).toBeNull();
  });

  it.each(['done', 'cancelled'] as const)('shows a reopened %s task after the calendar refresh when completed tasks are hidden', async (status) => {
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all', viewMode: 'calendar', periodMode: 'week', statusFilter: 'all',
    }));
    apiMock.getCalendarSyncSettings.mockResolvedValueOnce({
      completedTaskCalendarAction: 'hide',
    });
    const tasks = [
      createTask({ id: 101, status, dueAt: todayAt(11) }),
      createTask({ id: 102, status: 'new', dueAt: todayAt(12) }),
    ];
    await renderJournal(tasks, { initialView: 'calendar', waitForTitle: null });
    await screen.findByTestId('task-calendar-task-102');
    expect(screen.queryByTestId('task-calendar-task-101')).toBeNull();

    tasks[0] = createTask({ id: 101, status: 'inProgress', dueAt: todayAt(11) });
    fireEvent(window, new CustomEvent(TASK_JOURNAL_EVENT));
    const reopened = await screen.findByTestId('task-calendar-task-101');
    expect(reopened.getAttribute('title')).toContain('В работе');
    expect(reopened.className).not.toContain('bg-slate-200');
  });

  it('colors completed tasks in the task calendar when the calendar setting says to color them', async () => {
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'calendar',
      periodMode: 'week',
      statusFilter: 'all',
    }));

    await renderJournal([
      createTask({ status: 'done', result: 'Готово', dueAt: todayAt(11) }),
    ], { initialView: 'calendar', waitForTitle: null });

    const taskCard = await screen.findByTestId('task-calendar-task-101');
    expect(taskCard.className).toContain('border-slate-300');
    expect(taskCard.className).toContain('bg-slate-200');
  });

  it('colors cancelled tasks while keeping active tasks distinguishable', async () => {
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all', viewMode: 'calendar', periodMode: 'week', statusFilter: 'all',
    }));
    await renderJournal([
      createTask({ id: 101, status: 'cancelled', dueAt: todayAt(11) }),
      createTask({ id: 102, status: 'inProgress', dueAt: todayAt(12) }),
    ], { initialView: 'calendar', waitForTitle: null });

    const cancelled = await screen.findByTestId('task-calendar-task-101');
    const active = await screen.findByTestId('task-calendar-task-102');
    expect(cancelled.className).toContain('bg-slate-200');
    expect(active.className).not.toContain('bg-slate-200');
  });

  it('restores the active-only filter after switching from the completed-task calendar to the list', async () => {
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all', viewMode: 'calendar', periodMode: 'week', statusFilter: 'active',
    }));
    await renderJournal([
      createTask({ id: 101, title: 'Завершённая календарная задача', status: 'done', result: 'Готово', dueAt: todayAt(11) }),
      createTask({ id: 102, title: 'Активная календарная задача', status: 'new', dueAt: todayAt(12) }),
    ], { initialView: 'calendar', waitForTitle: null });
    expect(await screen.findByTestId('task-calendar-task-101')).toBeTruthy();
    fireEvent.click(screen.getByTestId('task-view-list'));
    expect(await screen.findByText('Активная календарная задача')).toBeTruthy();
    await waitFor(() => expect(screen.queryByText('Завершённая календарная задача')).toBeNull());
  });

  it('updates task duration from the calendar load panel', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    // Полдень UTC сохраняет один календарный день и в UTC окружения CI,
    // и в часовом поясе пользователя, поэтому тест проверяет изменение
    // длительности, а не зависит от локального часового пояса runner.
    vi.setSystemTime(new Date('2026-06-04T08:00:00.000Z'));
    const dueAt = fromTimeZoneDateTimeInput('2026-06-04T11:00', 'Asia/Kamchatka');
    apiMock.getUserSetting.mockImplementation(async (key: string) => (
      key === 'profile.timeZoneId'
        ? { key, value: 'Asia/Kamchatka' }
        : null
    ));

    await renderJournal([createTask({ dueAt, plannedDurationMinutes: 60 })], { initialView: 'calendar', periodMode: 'day' });

    fireEvent.click(screen.getByRole('button', { name: '+15 мин' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      dueAt,
      plannedDurationMinutes: 75,
    }));
  });

  it('highlights overlapping tasks in the task calendar', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-06-04T04:00:00.000Z'));
    await renderJournal([
      createTask({ dueAt: todayAt(11), plannedDurationMinutes: 60, assigneeUserId: 7, assigneeName: 'Администратор' }),
      createTask({ id: 102, title: 'Вторая задача', dueAt: todayAt(11, 30), plannedDurationMinutes: 60, assigneeUserId: 7, assigneeName: 'Администратор' }),
    ], { initialView: 'calendar' });

    expect(screen.getByText(/Есть риск конфликта/i)).toBeTruthy();
    expect(screen.getAllByText('Конфликт').length).toBeGreaterThan(0);
    expect(screen.getByTestId('task-calendar-task-101').className).toContain('border-rose-200');
    expect(screen.getByTestId('task-calendar-task-101').getAttribute('title')).toContain('Конфликт: задача пересекается');
  });

  it('saves the selected calendar conflict policy', async () => {
    await renderJournal([createTask()], { initialView: 'calendar' });

    fireEvent.click(screen.getByTestId('task-conflict-policy-block'));

    await waitFor(() => {
      expect(screen.getByTestId('task-conflict-policy-block').getAttribute('aria-pressed')).toBe('true');
      expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
        calendarConflictPolicy: 'block',
      });
    });
  });

  it('blocks calendar drag-and-drop when the selected conflict policy forbids overlaps', async () => {
    const targetDate = getSameWeekDateOutsideTodayForTest(new Date());
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'calendar',
      periodMode: 'week',
      calendarConflictPolicy: 'block',
    }));

    await renderJournal([
      createTask({ dueAt: todayAt(11), plannedDurationMinutes: 60, assigneeUserId: 7, assigneeName: 'Администратор' }),
      createTask({
        id: 102,
        title: 'Занятый слот',
        dueAt: dateAt(targetDate, 11),
        plannedDurationMinutes: 60,
        assigneeUserId: 7,
        assigneeName: 'Администратор',
      }),
    ], { waitForTitle: null });

    const targetDay = await screen.findByTestId(`task-calendar-day-${dateKey(targetDate)}`);
    const dataTransfer = createDataTransfer();

    fireEvent.dragStart(screen.getByTestId('task-calendar-task-101'), { dataTransfer });
    fireEvent.dragOver(targetDay, { dataTransfer });
    fireEvent.drop(targetDay, { dataTransfer });

    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith(
      'Перенос заблокирован: задача пересекается с "Занятый слот".',
      'Календарь задач',
    ));
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
  });

  it('saves and applies the selected calendar load warning mode', async () => {
    await renderJournal([
      createTask({ dueAt: todayAt(9), plannedDurationMinutes: 60 }),
      createTask({ id: 102, title: 'Вторая задача', dueAt: todayAt(10), plannedDurationMinutes: 60 }),
      createTask({ id: 103, title: 'Третья задача', dueAt: todayAt(11), plannedDurationMinutes: 60 }),
    ], { initialView: 'calendar' });

    expect(screen.queryByText('Высокая загрузка')).toBeNull();

    fireEvent.click(screen.getByTestId('task-load-warning-strict'));

    await waitFor(() => {
      expect(screen.getByText('Высокая загрузка')).toBeTruthy();
      expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
        calendarLoadWarningMode: 'strict',
      });
    });
  });

  it('restores the saved relaxed calendar load warning mode', async () => {
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'calendar',
      periodMode: 'week',
      calendarLoadWarningMode: 'relaxed',
    }));

    await renderJournal([
      createTask({ dueAt: todayAt(9), plannedDurationMinutes: 60 }),
      createTask({ id: 102, title: 'Вторая задача', dueAt: todayAt(10), plannedDurationMinutes: 60 }),
      createTask({ id: 103, title: 'Третья задача', dueAt: todayAt(11), plannedDurationMinutes: 60 }),
      createTask({ id: 104, title: 'Четвертая задача', dueAt: todayAt(12), plannedDurationMinutes: 60 }),
      createTask({ id: 105, title: 'Пятая задача', dueAt: todayAt(13), plannedDurationMinutes: 60 }),
    ], { waitForTitle: null });

    await waitFor(() => expect(screen.getByTestId('task-load-warning-relaxed').getAttribute('aria-pressed')).toBe('true'));
    expect(screen.queryByText('Высокая загрузка')).toBeNull();
    expect(screen.queryByText(/Есть риск конфликта/i)).toBeNull();
  });

  it('renders a weekly workload timeline with color legend', async () => {
    await renderJournal([
      createTask({ assigneeUserId: 7, assigneeName: 'Администратор' }),
      createTask({ id: 102, title: 'Закрытая задача', status: 'done', result: 'Готово', assigneeUserId: 7, assigneeName: 'Администратор' }),
    ]);

    fireEvent.change(screen.getByLabelText('Статус'), { target: { value: 'all' } });
    fireEvent.click(screen.getByRole('button', { name: /Нагрузка/i }));

    expect(screen.getByText('Календарь нагрузки')).toBeTruthy();
    expect(screen.getByText('Мои / выбранный исполнитель')).toBeTruthy();
    expect(screen.getByText('Выполненные/отмененные')).toBeTruthy();
    expect(await screen.findByTestId('task-timeline-task-101')).toBeTruthy();
    expect(await screen.findByTestId('task-timeline-task-102')).toBeTruthy();
  });

  it('keeps weekly workload separate for different employees with identical names', async () => {
    await renderJournal([
      createTask({ id: 101, title: 'Первая задача', assigneeUserId: 7, assigneeName: 'Иван Иванов', dueAt: todayAt(10), plannedDurationMinutes: 60, participants: [{ userId: 7, role: 'assignee', displayName: 'Иван Иванов', approvalStatus: 'pending' }] }),
      createTask({ id: 102, title: 'Вторая задача', assigneeUserId: 8, assigneeName: 'Иван Иванов', dueAt: todayAt(11), plannedDurationMinutes: 120, participants: [{ userId: 8, role: 'assignee', displayName: 'Иван Иванов', approvalStatus: 'pending' }] }),
    ], { initialView: 'timeline', waitForTitle: null });
    expect(await screen.findByText(/Иван Иванов \(№7\): активных 1/)).toBeTruthy();
    expect(screen.getByText(/Иван Иванов \(№8\): активных 1/)).toBeTruthy();
    expect(screen.queryByText(/Иван Иванов: активных 2/)).toBeNull();
  });

  it('reveals remaining employee workloads on demand', async () => {
    const tasks = Array.from({ length: 9 }, (_, index) => createTask({
      id: 201 + index, title: `Задача ${index + 1}`, dueAt: todayAt(10),
      assigneeUserId: 101 + index, assigneeName: `Исполнитель ${index + 1}`,
      participants: [{ userId: 101 + index, role: 'assignee', displayName: `Исполнитель ${index + 1}`, approvalStatus: 'pending' }],
    }));
    await renderJournal(tasks, { initialView: 'timeline', waitForTitle: null });
    const showMore = await screen.findByRole('button', { name: 'Ещё исполнителей: 1' });
    expect(showMore.getAttribute('aria-expanded')).toBe('false');
    fireEvent.click(showMore);
    expect(screen.getByText(/Исполнитель 9: активных 1/)).toBeTruthy();
    const showLess = screen.getByRole('button', { name: 'Показать меньше' });
    expect(showLess.getAttribute('aria-expanded')).toBe('true');
    fireEvent.click(showLess);
    expect(screen.queryByText(/Исполнитель 9: активных 1/)).toBeNull();
  });

  it('keeps overlapping workload timeline tasks wide enough to show text', async () => {
    const overlappingTasks = Array.from({ length: 6 }, (_, index) => createTask({
      id: 200 + index,
      title: `Пересекающаяся задача ${index + 1}`,
      dueAt: todayAt(13),
      plannedDurationMinutes: 60,
      assigneeUserId: 7,
      assigneeName: 'Администратор',
    }));

    await renderJournal(overlappingTasks, {
      initialView: 'timeline',
    });

    const bodyGrid = screen.getByTestId('task-timeline-body-grid') as HTMLElement;

    expect(bodyGrid.style.gridTemplateColumns).toContain('minmax(744px, 1fr)');
    expect(screen.getByTestId('task-timeline-task-200').className).toContain('min-w-[7.25rem]');
    expect(screen.getByText('Пересекающаяся задача 1')).toBeTruthy();
  });

  it('moves a task to another time slot in the workload timeline', async () => {
    await renderJournal([createTask({ dueAt: todayAt(11), plannedDurationMinutes: 60 })], { initialView: 'timeline' });

    const targetDay = screen.getByTestId(`task-timeline-day-${dateKey(new Date())}`);
    targetDay.getBoundingClientRect = vi.fn(() => ({
      x: 0,
      y: 0,
      top: 0,
      left: 0,
      bottom: 832,
      right: 180,
      width: 180,
      height: 832,
      toJSON: () => ({}),
    }));
    const dataTransfer = createDataTransfer();

    fireEvent.dragStart(screen.getByTestId('task-timeline-task-101'), { dataTransfer });
    fireEvent.dragOver(targetDay, { dataTransfer, clientY: 96 });
    fireEvent.drop(targetDay, { dataTransfer, clientY: 96 });

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    const [, payload] = apiMock.updateTaskJournalItem.mock.calls[0];
    const movedDueAt = new Date(payload.dueAt);
    expect(movedDueAt.getHours()).toBe(8);
    expect(movedDueAt.getMinutes()).toBe(0);
  });

  it('moves a timeline task to the selected local slot in the profile time zone', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-06-03T13:30:00.000Z'));
    apiMock.getUserSetting.mockImplementation(async (key: string) => (
      key === 'profile.timeZoneId'
        ? { key, value: 'Asia/Kamchatka' }
        : null
    ));
    const dueAt = fromTimeZoneDateTimeInput('2026-06-04T11:00', 'Asia/Kamchatka');

    await renderJournal([
      createTask({
        dueAt,
        plannedDurationMinutes: 60,
      }),
    ], { initialView: 'timeline' });

    const targetDay = await screen.findByTestId('task-timeline-day-2026-06-04');
    targetDay.getBoundingClientRect = vi.fn(() => ({
      x: 0,
      y: 0,
      top: 0,
      left: 0,
      bottom: 832,
      right: 180,
      width: 180,
      height: 832,
      toJSON: () => ({}),
    }));
    const dataTransfer = createDataTransfer();

    fireEvent.dragStart(screen.getByTestId('task-timeline-task-101'), { dataTransfer });
    fireEvent.dragOver(targetDay, { dataTransfer, clientY: 96 });
    fireEvent.drop(targetDay, { dataTransfer, clientY: 96 });

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    const [, payload] = apiMock.updateTaskJournalItem.mock.calls[0];
    expect(payload.dueAt).toBe(fromTimeZoneDateTimeInput('2026-06-04T08:00', 'Asia/Kamchatka'));
    expect(getZonedParts(payload.dueAt, 'Asia/Kamchatka')).toMatchObject({
      year: 2026,
      month: 6,
      day: 4,
      hour: 8,
      minute: 0,
    });
  });
});
