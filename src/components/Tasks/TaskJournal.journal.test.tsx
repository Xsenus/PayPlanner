import { setupTaskJournalTests, apiMock, toastMock, offlineCalendarMock, authState, permissionsState, dateAt, createTask, renderJournal, openFirstTaskForEdit } from './TaskJournal.test-support';
import { TaskJournal } from './TaskJournal';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { cloneDefaultTaskStickerStyles } from '../../utils/taskStickerStyles';
import { TASK_JOURNAL_TABLE_COLUMNS } from '../../utils/taskJournalColumns';

describe('TaskJournal', () => {
  setupTaskJournalTests();


  it('shows client, case and renamed task title field in the journal', async () => {
    await renderJournal();

    expect(screen.getByText('Контрагент / дело')).toBeTruthy();
    expect(screen.getByText('Наименование задачи')).toBeTruthy();
    expect(screen.getAllByText('Иванов Иван Иванович').length).toBeGreaterThan(0);
    expect(screen.getByText('БАНКРОТСТВО')).toBeTruthy();
  });

  it('PAY-19 review: shows active constraints while the filter panel is collapsed', async () => {
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all', periodMode: 'all', search: 'договор', priorityFilter: 'high',
    }));
    await renderJournal([], { waitForTitle: null });
    fireEvent.click(screen.getByTestId('task-filters-toggle'));
    const summary = screen.getByLabelText('Действующие фильтры задач');
    expect(summary.textContent).toContain('Поиск: договор');
    expect(summary.textContent).toContain('Приоритет: Высокий');
    expect(summary.closest('[hidden]')).toBeNull();
    fireEvent.click(screen.getByTestId('task-filters-toggle'));
    fireEvent.change(screen.getByRole('textbox', { name: 'Поиск по контрагенту, делу или наименованию задачи' }), { target: { value: '' } });
    fireEvent.click(screen.getByTestId('task-filters-toggle'));
    expect(screen.getByLabelText('Действующие фильтры задач').textContent).not.toContain('Поиск:');
  });

  it('PAY-19 review: links each filter toggle to its own panel when two journals are mounted', async () => {
    await renderJournal([], { waitForTitle: null });
    render(<TaskJournal />);
    const toggles = await screen.findAllByTestId('task-filters-toggle');
    const ids = toggles.map((toggle) => toggle.getAttribute('aria-controls'));
    expect(new Set(ids).size).toBe(2);
    for (const id of ids) expect(document.getElementById(id!)).toBeTruthy();
  });

  it('PAY-19 review: restores collapse state independently after switching users', async () => {
    window.localStorage.setItem('pp.task_journal.filters_panel.v1.user:7', 'collapsed');
    window.localStorage.setItem('pp.task_journal.filters_panel.v1.user:9', 'expanded');
    const view = await renderJournal([], { waitForTitle: null });
    expect(screen.getByTestId('task-filters-toggle').getAttribute('aria-expanded')).toBe('false');
    authState.user = { ...authState.user, id: 9 };
    view.rerender(<TaskJournal />);
    await waitFor(() => expect(screen.getByTestId('task-filters-toggle').getAttribute('aria-expanded')).toBe('true'));
    fireEvent.click(screen.getByTestId('task-filters-toggle'));
    expect(window.localStorage.getItem('pp.task_journal.filters_panel.v1.user:9')).toBe('collapsed');
    expect(window.localStorage.getItem('pp.task_journal.filters_panel.v1.user:7')).toBe('collapsed');
    fireEvent.click(screen.getByTestId('task-filters-toggle'));
    authState.user = { ...authState.user, id: 7 };
    view.rerender(<TaskJournal />);
    await waitFor(() => expect(screen.getByTestId('task-filters-toggle').getAttribute('aria-expanded')).toBe('false'));
  });

  it('PAY-19: collapses the filters and views panel without losing filter state and remembers the choice', async () => {
    await renderJournal([], { waitForTitle: null });

    const toggle = screen.getByTestId('task-filters-toggle');
    const body = document.getElementById(toggle.getAttribute('aria-controls')!) as HTMLElement;
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(body.hidden).toBe(false);
    expect(within(body).getByTestId('task-saved-view-select')).toBeTruthy();

    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(body.hidden).toBe(true);
    expect(window.localStorage.getItem('pp.task_journal.filters_panel.v1.user:7')).toBe('collapsed');

    cleanup();
    await renderJournal([], { waitForTitle: null });
    expect(screen.getByTestId('task-filters-toggle').getAttribute('aria-expanded')).toBe('false');

    fireEvent.click(screen.getByTestId('task-filters-toggle'));
    expect(window.localStorage.getItem('pp.task_journal.filters_panel.v1.user:7')).toBe('expanded');
  });

  it('creates a quick internal task without choosing a client or case', async () => {
    await renderJournal([], { waitForTitle: null });

    fireEvent.click(screen.getByRole('button', { name: 'Быстрая задача' }));
    expect(await screen.findByRole('heading', { name: 'Быстрая задача' })).toBeTruthy();
    fireEvent.change(screen.getByTestId('quick-task-title-input'), { target: { value: 'Срочно проверить документ' } });
    fireEvent.change(screen.getByTestId('quick-task-description-input'), { target: { value: 'Кратко сверить реквизиты' } });
    fireEvent.change(screen.getByTestId('quick-task-due-at-input'), { target: { value: '2026-09-22T11:30' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
      isInternal: true,
      isUntriaged: true,
      clientId: null,
      legalEntityId: null,
      clientCaseId: null,
      objectName: 'Внутренняя задача',
      clientBillingClassification: 'internal',
      assigneeUserId: null,
      assigneeName: '',
      participants: [],
      billingContractId: null,
      title: 'Срочно проверить документ',
      description: 'Кратко сверить реквизиты',
      dueAt: expect.any(String),
    }));
    await waitFor(() => expect(screen.queryByRole('heading', { name: 'Быстрая задача' })).toBeNull());
    expect(toastMock.success).toHaveBeenCalledWith(expect.stringContaining('Неразобранные'), 'Журнал задач');
  });

  it('shows a valid cached calendar as read-only when the task API is unavailable', async () => {
    const cachedTask = createTask({ title: 'Сохранённая задача' });
    apiMock.getFeatureFlags.mockResolvedValue({
      legal: {
        externalCourtSync: false,
        externalCalendarSync: false,
        courtEvents: false,
        documentTemplateLibrary: false,
        externalWorkOrderApproval: false,
        clientPortal: false,
        offlineCalendarReadOnly: true,
      },
    });
    apiMock.getTaskJournalItems.mockRejectedValue(new Error('offline'));
    offlineCalendarMock.loadOfflineCalendarSnapshot.mockResolvedValue({
      schemaVersion: 1,
      userId: 7,
      trustedDevice: false,
      syncedAt: '2026-07-31T08:00:00.000Z',
      expiresAt: '2026-08-07T08:00:00.000Z',
      items: [cachedTask],
    });

    render(<TaskJournal initialView="calendar" />);

    expect(await screen.findByText('Офлайн-календарь · только просмотр')).toBeTruthy();
    expect(offlineCalendarMock.loadOfflineCalendarSnapshot).toHaveBeenCalledWith(7);
    expect(screen.queryByRole('button', { name: 'Добавить задачу' })).toBeNull();
    expect(toastMock.info).toHaveBeenCalledWith(
      expect.stringContaining('Изменения недоступны'),
      'Офлайн-календарь',
    );
  });

  it('hides an already open offline calendar when another tab revokes this user', async () => {
    apiMock.getFeatureFlags.mockResolvedValue({ legal: { offlineCalendarReadOnly: true } });
    apiMock.getTaskJournalItems.mockRejectedValue(new Error('offline'));
    offlineCalendarMock.loadOfflineCalendarSnapshot.mockResolvedValue({
      schemaVersion: 1, userId: 7, trustedDevice: true,
      syncedAt: '2026-07-31T08:00:00.000Z', expiresAt: '2026-08-07T08:00:00.000Z',
      items: [createTask({ title: 'Данные отозванной задачи' })],
    });
    render(<TaskJournal initialView="calendar" />);
    expect(await screen.findByText('Офлайн-календарь · только просмотр')).toBeTruthy();
    expect(screen.getByTestId('task-calendar-task-101')).toBeTruthy();
    fireEvent(window, new StorageEvent('storage', { key: 'payplanner-offline-calendar-denied:7', newValue: 'revoked', storageArea: localStorage }));
    await waitFor(() => expect(screen.queryByTestId('task-calendar-task-101')).toBeNull());
    expect(screen.queryByText('Офлайн-календарь · только просмотр')).toBeNull();
  });

  it('keeps the visible calendar when another user is revoked or a denial is removed', async () => {
    await renderJournal([createTask()], { initialView: 'calendar', waitForTitle: null });
    await screen.findByTestId('task-calendar-task-101');
    fireEvent(window, new StorageEvent('storage', { key: 'payplanner-offline-calendar-denied:8', newValue: 'revoked', storageArea: localStorage }));
    fireEvent(window, new StorageEvent('storage', { key: 'payplanner-offline-calendar-denied:7', oldValue: 'revoked', newValue: null, storageArea: localStorage }));
    expect(screen.getByTestId('task-calendar-task-101')).toBeTruthy();
    expect(toastMock.error).not.toHaveBeenCalledWith(expect.stringContaining('Доступ к сохранённому календарю изменился'), 'Журнал задач');
  });

  it('ignores a late calendar response after access is revoked in another tab', async () => {
    await renderJournal([createTask()], { initialView: 'calendar', waitForTitle: null });
    await screen.findByTestId('task-calendar-task-101');
    let resolve!: (response: { items: ReturnType<typeof createTask>[]; page: number; pageSize: number; total: number }) => void;
    apiMock.getTaskJournalItems.mockClear();
    apiMock.getTaskJournalItems.mockReturnValueOnce(new Promise((done) => { resolve = done; }));
    fireEvent(window, new Event('pp.task_journal.changed'));
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalled());
    fireEvent(window, new StorageEvent('storage', { key: 'payplanner-offline-calendar-denied:7', newValue: 'revoked', storageArea: localStorage }));
    expect(screen.queryByTestId('task-calendar-task-101')).toBeNull();
    await act(async () => { resolve({ items: [createTask()], page: 1, pageSize: 200, total: 1 }); });
    expect(screen.queryByTestId('task-calendar-task-101')).toBeNull();
  });

  it.each([null, '8'])('hides an open calendar when another tab changes its owner to %s', async (newOwner) => {
    await renderJournal([createTask()], { initialView: 'calendar', waitForTitle: null });
    await screen.findByTestId('task-calendar-task-101');
    fireEvent(window, new StorageEvent('storage', { key: 'payplanner-offline-calendar-owner', oldValue: '7', newValue: '7', storageArea: localStorage }));
    expect(screen.getByTestId('task-calendar-task-101')).toBeTruthy();
    fireEvent(window, new StorageEvent('storage', { key: 'payplanner-offline-calendar-owner', oldValue: '7', newValue: newOwner, storageArea: localStorage }));
    expect(screen.queryByTestId('task-calendar-task-101')).toBeNull();
  });

  it.each([401, 403])('does not expose cached calendar after access denial %s', async (status) => {
    apiMock.getFeatureFlags.mockResolvedValue({ legal: { offlineCalendarReadOnly: true } });
    apiMock.getTaskJournalItems.mockRejectedValue(Object.assign(new Error('Denied'), { status }));
    offlineCalendarMock.loadOfflineCalendarSnapshot.mockResolvedValue({
      schemaVersion: 1, userId: 7, trustedDevice: true,
      syncedAt: '2026-07-31T08:00:00.000Z', expiresAt: '2026-08-07T08:00:00.000Z',
      items: [createTask({ title: 'Закрытые сведения' })],
    });
    render(<TaskJournal initialView="calendar" />);
    await waitFor(() => expect(toastMock.error).toHaveBeenCalled());
    expect(offlineCalendarMock.loadOfflineCalendarSnapshot).not.toHaveBeenCalled();
    expect(offlineCalendarMock.revokeOfflineCalendarSnapshot).toHaveBeenCalledWith(7);
    expect(screen.queryByText('Закрытые сведения')).toBeNull();
    expect(screen.queryByText('Офлайн-календарь · только просмотр')).toBeNull();
  });

  it('removes previously visible calendar tasks when access is revoked during refresh', async () => {
    await renderJournal([createTask({ title: 'Прежде доступная задача' })], { initialView: 'calendar', waitForTitle: null });
    expect(await screen.findByTestId('task-calendar-task-101')).toBeTruthy();
    apiMock.getTaskJournalItems.mockRejectedValue(Object.assign(new Error('Denied'), { status: 403 }));
    fireEvent.click(screen.getByRole('button', { name: /Нагрузка/i }));
    await waitFor(() => expect(offlineCalendarMock.revokeOfflineCalendarSnapshot).toHaveBeenCalledWith(7));
    expect(screen.queryByText('Прежде доступная задача')).toBeNull();
    expect(screen.queryByTestId('task-timeline-task-101')).toBeNull();
  });

  it('rechecks the task list when the current role permissions change', async () => {
    authState.user.role = { id: 42 };
    await renderJournal([createTask()], { initialView: 'calendar', waitForTitle: null });
    await screen.findByTestId('task-calendar-task-101');
    apiMock.getTaskJournalItems.mockRejectedValue(Object.assign(new Error('Denied'), { status: 403 }));
    fireEvent(window, new CustomEvent('pp.permissions.changed', { detail: { roleId: 42 } }));
    await waitFor(() => expect(offlineCalendarMock.revokeOfflineCalendarSnapshot).toHaveBeenCalledWith(7));
    expect(screen.queryByTestId('task-calendar-task-101')).toBeNull();
  });

  it('replaces the old offline snapshot when a permission refresh returns an empty calendar', async () => {
    authState.user.role = { id: 42 };
    apiMock.getFeatureFlags.mockResolvedValue({ legal: { offlineCalendarReadOnly: true } });
    await renderJournal([createTask()], { initialView: 'calendar', waitForTitle: null });
    await waitFor(() => expect(offlineCalendarMock.saveOfflineCalendarSnapshot).toHaveBeenCalled());
    offlineCalendarMock.saveOfflineCalendarSnapshot.mockClear();
    offlineCalendarMock.revokeOfflineCalendarSnapshot.mockClear();
    apiMock.getTaskJournalItems.mockResolvedValue({ items: [], page: 1, pageSize: 200, total: 0 });
    fireEvent(window, new CustomEvent('pp.permissions.changed', { detail: { roleId: 42 } }));
    await waitFor(() => expect(offlineCalendarMock.revokeOfflineCalendarSnapshot).toHaveBeenCalledWith(7));
    await waitFor(() => expect(offlineCalendarMock.saveOfflineCalendarSnapshot).toHaveBeenCalledWith(7, [], false));
  });

  it('keeps the offline snapshot on a network failure while permissions are being refreshed', async () => {
    authState.user.role = { id: 42 };
    apiMock.getFeatureFlags.mockResolvedValue({ legal: { offlineCalendarReadOnly: true } });
    offlineCalendarMock.loadOfflineCalendarSnapshot.mockResolvedValue({
      schemaVersion: 1, userId: 7, trustedDevice: false,
      syncedAt: '2026-07-31T08:00:00.000Z', expiresAt: '2026-08-07T08:00:00.000Z',
      items: [createTask({ title: 'Сохранённая задача' })],
    });
    await renderJournal([createTask()], { initialView: 'calendar', waitForTitle: null });
    await screen.findByTestId('task-calendar-task-101');
    offlineCalendarMock.revokeOfflineCalendarSnapshot.mockClear();
    apiMock.getTaskJournalItems.mockRejectedValue(new Error('Network unavailable'));
    fireEvent(window, new CustomEvent('pp.permissions.changed', { detail: { roleId: 42 } }));
    expect(await screen.findByText('Офлайн-календарь · только просмотр')).toBeTruthy();
    expect(offlineCalendarMock.revokeOfflineCalendarSnapshot).not.toHaveBeenCalled();
  });

  it('replaces the old snapshot before changing trusted-device disclosure', async () => {
    apiMock.getFeatureFlags.mockResolvedValue({ legal: { offlineCalendarReadOnly: true } });
    await renderJournal([createTask()], { initialView: 'calendar', waitForTitle: null });
    await waitFor(() => expect(offlineCalendarMock.saveOfflineCalendarSnapshot).toHaveBeenCalled());
    offlineCalendarMock.revokeOfflineCalendarSnapshot.mockClear();
    offlineCalendarMock.restoreOfflineCalendarAccess.mockClear();
    fireEvent.click(screen.getByRole('switch', { name: 'Сохранять названия задач на доверенном устройстве' }));
    await waitFor(() => expect(offlineCalendarMock.setOfflineCalendarTrustedDevice).toHaveBeenCalledWith(7, true));
    await waitFor(() => expect(offlineCalendarMock.revokeOfflineCalendarSnapshot).toHaveBeenCalledWith(7));
    await waitFor(() => expect(offlineCalendarMock.restoreOfflineCalendarAccess).toHaveBeenCalledWith(7));
    await waitFor(() => expect(offlineCalendarMock.saveOfflineCalendarSnapshot).toHaveBeenCalledWith(7, expect.any(Array), true));
  });

  it('does not save the old snapshot if trusted-device settings change during a permissions refresh', async () => {
    authState.user.role = { id: 42 };
    apiMock.getFeatureFlags.mockResolvedValue({ legal: { offlineCalendarReadOnly: true } });
    await renderJournal([createTask()], { initialView: 'calendar', waitForTitle: null });
    await waitFor(() => expect(offlineCalendarMock.saveOfflineCalendarSnapshot).toHaveBeenCalled());
    offlineCalendarMock.saveOfflineCalendarSnapshot.mockClear();
    apiMock.getTaskJournalItems.mockClear();
    let resolve!: (value: { items: []; page: number; pageSize: number; total: number }) => void;
    apiMock.getTaskJournalItems.mockReturnValueOnce(new Promise((done) => { resolve = done; }));
    fireEvent(window, new CustomEvent('pp.permissions.changed', { detail: { roleId: 42 } }));
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalled());
    fireEvent.click(screen.getByRole('switch', { name: 'Сохранять названия задач на доверенном устройстве' }));
    await waitFor(() => expect(offlineCalendarMock.setOfflineCalendarTrustedDevice).toHaveBeenCalledWith(7, true));
    expect(offlineCalendarMock.saveOfflineCalendarSnapshot).not.toHaveBeenCalled();
    resolve({ items: [], page: 1, pageSize: 200, total: 0 });
    await waitFor(() => expect(offlineCalendarMock.saveOfflineCalendarSnapshot).toHaveBeenCalledWith(7, [], true));
  });

  it('opens a task from the list title and exposes full hover text for clipped fields', async () => {
    const taskTitle = 'ИГНАТЬЕВ - ДОВЕРЕННОСТЬ';
    const taskDescription = 'Проект доверенности, отправить нотариусу, договориться с клиентом о подписании.';
    await renderJournal([createTask({ title: taskTitle, description: taskDescription })]);

    const titleLink = screen.getByTestId('task-title-link-101');
    expect(titleLink.getAttribute('title')).toBe(taskTitle);
    expect(titleLink.getAttribute('aria-label')).toBe(`Открыть задачу ${taskTitle}`);
    expect(screen.getByText(taskDescription).getAttribute('title')).toBe(taskDescription);

    fireEvent.click(titleLink);

    expect(await screen.findByRole('heading', { name: 'Просмотр задачи' })).toBeTruthy();
  });

  it('loads the manual control section with the under-control filter', async () => {
    await renderJournal([createTask({ isUnderControl: true })], { underControlOnly: true });

    expect(screen.getByRole('heading', { name: 'На контроле' })).toBeTruthy();
    expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(expect.objectContaining({
      underControl: true,
    }), expect.objectContaining({ signal: expect.any(AbortSignal) }));
    expect(apiMock.getTaskJournalCaseAutomationCandidates).not.toHaveBeenCalled();
  });

  it('loads assignees and tasks with calendar visibility in calendar view', async () => {
    authState.isAdmin = false;
    permissionsState.calendarViewScope = 'specific';
    permissionsState.calendarVisibleUserIds = [11];

    await renderJournal([createTask({ assigneeUserId: 11, assigneeName: 'Выбранный сотрудник' })], {
      initialView: 'calendar',
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 11, name: 'Выбранный сотрудник', email: 'selected@example.com' },
      ],
    });

    await waitFor(() => {
      expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(expect.objectContaining({
        calendarView: true,
      }), expect.objectContaining({ signal: expect.any(AbortSignal) }));
      expect(apiMock.getTaskJournalAssignees).toHaveBeenCalledWith({ calendarView: true },
        expect.objectContaining({ signal: expect.any(AbortSignal) }));
    });
  });

  it('does not request case-only task extras when the role cannot view cases', async () => {
    authState.isAdmin = false;
    permissionsState.taskCanCreate = true;
    permissionsState.caseCanView = false;
    permissionsState.caseCanExport = false;

    await renderJournal([createTask()], { initialView: 'calendar' });

    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalled());
    expect(apiMock.getTaskJournalCaseAutomationCandidates).not.toHaveBeenCalled();
    expect(apiMock.getCaseCalendarEvents).not.toHaveBeenCalled();
    expect(apiMock.getCaseCalendarSummary).not.toHaveBeenCalled();
  });

  it('filters active overdue tasks from the status selector', async () => {
    const yesterday = new Date();
    yesterday.setDate(yesterday.getDate() - 1);
    const tomorrow = new Date();
    tomorrow.setDate(tomorrow.getDate() + 1);

    await renderJournal([
      createTask({ id: 101, title: 'Просроченная задача', status: 'new', dueAt: dateAt(yesterday, 10) }),
      createTask({ id: 102, title: 'Будущая задача', status: 'new', dueAt: dateAt(tomorrow, 10) }),
      createTask({ id: 103, title: 'Закрытая просроченная задача', status: 'done', dueAt: dateAt(yesterday, 10) }),
    ]);

    const statusSelect = screen.getByLabelText('Статус');
    expect(within(statusSelect).getByText('Просроченные', { selector: 'option' })).toBeTruthy();

    fireEvent.change(statusSelect, { target: { value: 'overdue' } });

    expect(await screen.findByText('Просроченная задача')).toBeTruthy();
    expect(screen.queryByText('Будущая задача')).toBeNull();
    expect(screen.queryByText('Закрытая просроченная задача')).toBeNull();
  });

  it('keeps the task table compact and row actions pinned on smaller workspaces', async () => {
    await renderJournal([createTask()]);

    expect(screen.getByTestId('task-table').className).toContain('w-max');
    expect(screen.getByTestId('task-table').className).toContain('min-w-full');
    expect(screen.getByTestId('task-table').className).toContain('table-fixed');
    expect(screen.getByText('Действия').closest('th')?.className).toContain('sticky');
    expect(screen.getByTitle('Просмотр').closest('td')?.className).toContain('sticky');
    expect(screen.getByTestId('task-actions-101').className).toContain('grid-cols-[repeat(auto-fit,minmax(2.25rem,1fr))]');
    expect(screen.getByTestId('task-column-header-result').className).not.toContain('hidden');
    expect(screen.getByRole('button', { name: 'Колонки' })).toBeTruthy();
  });

  it('lets users hide, restore and reorder task columns in both headers and rows', async () => {
    await renderJournal([createTask({ result: 'Подготовлен документ' })]);
    const table = screen.getByTestId('task-table');
    const controls = screen.getByRole('button', { name: 'Колонки' });
    expect(controls.getAttribute('aria-expanded')).toBe('false');
    fireEvent.click(controls);

    fireEvent.click(screen.getByRole('checkbox', { name: 'Показать колонку Результат' }));
    expect(screen.queryByTestId('task-column-header-result')).toBeNull();
    expect(within(table).queryByText('Подготовлен документ')).toBeNull();
    expect(table.querySelector('th[scope="rowgroup"]')?.getAttribute('colspan')).toBe('9');

    fireEvent.click(screen.getByRole('button', { name: 'Переместить колонку Наименование задачи вверх' }));
    expect(Array.from(table.querySelectorAll('thead th[data-testid]')).map((cell) => cell.getAttribute('data-testid')).slice(0, 2))
      .toEqual(['task-column-header-title', 'task-column-header-counterparty']);
    expect(Array.from(table.querySelectorAll('tbody td[data-task-column]')).map((cell) => cell.getAttribute('data-task-column')).slice(0, 2))
      .toEqual(['title', 'counterparty']);

    fireEvent.click(screen.getByRole('checkbox', { name: 'Показать колонку Результат' }));
    expect(screen.getByTestId('task-column-header-result').className).not.toContain('hidden');
    expect(within(table).getByText('Подготовлен документ')).toBeTruthy();
    fireEvent.click(controls);
    expect(controls.getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByRole('checkbox', { name: 'Показать колонку Результат' })).toBeNull();
  });

  it('saves personal task columns and restores them after reopening the journal', async () => {
    await renderJournal([createTask()]);
    fireEvent.click(screen.getByRole('button', { name: 'Колонки' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Показать колонку Поручил' }));
    fireEvent.click(screen.getByRole('button', { name: 'Переместить колонку Наименование задачи вверх' }));

    await waitFor(() => expect(apiMock.upsertUserSetting).toHaveBeenCalledWith(
      'task_journal_table_settings_v1',
      expect.any(String),
    ));
    const savedCall = apiMock.upsertUserSetting.mock.calls.filter(([key]) => key === 'task_journal_table_settings_v1').at(-1)!;
    const saved = JSON.parse(savedCall[1]);
    expect(saved.visibleColumns).not.toContain('author');
    expect(saved.columnOrder.slice(0, 2)).toEqual(['title', 'counterparty']);
    expect(JSON.parse(localStorage.getItem('pp.task_journal.table_settings.v1.user:7')!)).toEqual(saved);

    cleanup();
    await renderJournal([createTask()]);
    expect(screen.queryByTestId('task-column-header-author')).toBeNull();
    expect(screen.getByTestId('task-table').querySelector('thead th[data-testid]')?.getAttribute('data-testid')).toBe('task-column-header-title');
  });

  it('loads server task columns and resets visibility order and width together', async () => {
    const keys = TASK_JOURNAL_TABLE_COLUMNS.map((column) => column.key);
    apiMock.getUserSetting.mockImplementation(async (key: string) => key === 'task_journal_table_settings_v1' ? {
      key,
      value: JSON.stringify({ visibleColumns: ['title', 'actions'], columnOrder: ['actions', ...keys.filter((column) => column !== 'actions')], columnWidths: { title: 400 }, pageSize: 25 }),
    } : null);
    await renderJournal([createTask()]);

    expect(screen.queryByTestId('task-column-header-counterparty')).toBeNull();
    expect(screen.getByTestId('task-column-header-title').style.width).toBe('400px');
    expect(screen.getByTestId('task-column-header-actions').className).not.toContain('sticky');
    expect(screen.getByTitle('Просмотр').closest('td')?.className).not.toContain('sticky');
    fireEvent.click(screen.getByRole('button', { name: 'Колонки' }));
    fireEvent.click(screen.getByRole('button', { name: 'Сбросить настройки' }));

    expect(screen.getByTestId('task-column-header-counterparty')).toBeTruthy();
    expect(screen.getByTestId('task-column-header-title').style.width).toBe('290px');
    expect(screen.getByTestId('task-column-header-actions').className).toContain('sticky');
    expect(screen.getByTestId('task-table').querySelectorAll('thead th')).toHaveLength(keys.length + 1);
  });

  it('keeps at least one task column and matches empty state colspan to its layout', async () => {
    await renderJournal([], { waitForTitle: null });
    await screen.findByTestId('task-table');
    fireEvent.click(screen.getByRole('button', { name: 'Колонки' }));
    for (const column of TASK_JOURNAL_TABLE_COLUMNS) {
      fireEvent.click(screen.getByRole('checkbox', { name: `Показать колонку ${column.label}` }));
    }

    const table = screen.getByTestId('task-table');
    expect(table.querySelectorAll('thead th')).toHaveLength(2);
    expect(within(table).getByText('По текущим фильтрам задач нет.').getAttribute('colspan')).toBe('2');
    expect((screen.getByRole('checkbox', { name: 'Показать колонку Действия' }) as HTMLInputElement).checked).toBe(true);
  });

  it('separates tasks by execution date when the journal is ordered by date', async () => {
    await renderJournal([
      createTask({ id: 301, title: 'Первая задача на 7 июня', dueAt: '2026-06-07T10:00:00Z' }),
      createTask({ id: 302, title: 'Вторая задача на 7 июня', dueAt: '2026-06-07T12:00:00Z' }),
      createTask({ id: 303, title: 'Задача на 8 июня', dueAt: '2026-06-08T10:00:00Z' }),
    ]);

    expect(screen.getByRole('separator', { name: 'Задачи на 7 июня 2026 года' })).toBeTruthy();
    expect(screen.getByRole('separator', { name: 'Задачи на 8 июня 2026 года' })).toBeTruthy();
    expect(screen.getAllByText('7 июня 2026 года')).toHaveLength(1);

    fireEvent.click(screen.getByRole('button', { name: 'Сортировать по колонке Наименование задачи' }));

    await waitFor(() => {
      expect(screen.queryByRole('separator', { name: 'Задачи на 7 июня 2026 года' })).toBeNull();
    });
  });

  it('allows users to resize task columns and keeps the selected width', async () => {
    await renderJournal([createTask()]);

    const titleHeader = screen.getByTestId('task-column-header-title');
    expect(titleHeader.style.width).toBe('290px');

    fireEvent.mouseDown(screen.getByRole('separator', { name: 'Изменить ширину колонки Наименование задачи' }), {
      clientX: 290,
    });
    fireEvent.mouseMove(window, { clientX: 350 });
    fireEvent.mouseUp(window);

    expect(titleHeader.style.width).toBe('350px');
    expect(
      JSON.parse(window.localStorage.getItem('pp.task_journal.table_settings.v1.user:7') ?? '{}').columnWidths.title,
    ).toBe(350);
  });

  it('opens the daily reminder target as current employees overdue task list', async () => {
    const yesterday = new Date();
    yesterday.setDate(yesterday.getDate() - 1);
    const tomorrow = new Date();
    tomorrow.setDate(tomorrow.getDate() + 1);

    await renderJournal([
      createTask({ id: 201, title: 'Моя просроченная задача', assigneeUserId: 7, dueAt: dateAt(yesterday, 10) }),
      createTask({ id: 202, title: 'Чужая просроченная задача', assigneeUserId: 8, dueAt: dateAt(yesterday, 10) }),
      createTask({ id: 203, title: 'Моя будущая задача', assigneeUserId: 7, dueAt: dateAt(tomorrow, 10) }),
    ], { overdueFilterSignal: 1 });

    expect(await screen.findByText('Моя просроченная задача')).toBeTruthy();
    await waitFor(() => {
      expect(screen.queryByText('Чужая просроченная задача')).toBeNull();
      expect(screen.queryByText('Моя будущая задача')).toBeNull();
      expect(screen.getByTestId('task-quick-filter-assignedToMe').getAttribute('aria-pressed')).toBe('true');
      expect((screen.getByLabelText('Статус') as HTMLSelectElement).value).toBe('overdue');
    });
  });

  it('passes selected table sorting to the backend task journal endpoint', async () => {
    await renderJournal([
      createTask({ id: 101, title: 'Первая задача', assigneeName: 'Анна' }),
      createTask({ id: 102, title: 'Вторая задача', assigneeName: 'Сергей' }),
    ]);

    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(expect.objectContaining({
      sortBy: 'dueAt',
      sortDir: 'asc',
    }), expect.objectContaining({ signal: expect.any(AbortSignal) })));

    fireEvent.click(screen.getByRole('button', { name: 'Сортировать по колонке Исполнитель' }));

    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenLastCalledWith(expect.objectContaining({
      sortBy: 'assignee',
      sortDir: 'asc',
    }), expect.objectContaining({ signal: expect.any(AbortSignal) })));

    fireEvent.click(screen.getByRole('button', { name: 'Сортировать по колонке Исполнитель' }));

    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenLastCalledWith(expect.objectContaining({
      sortBy: 'assignee',
      sortDir: 'desc',
    }), expect.objectContaining({ signal: expect.any(AbortSignal) })));
  });

  it('opens the journal by execution date despite a previously selected column and offers an explicit sort control', async () => {
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      periodMode: 'all',
      sortBy: 'counterparty',
      sortDir: 'desc',
    }));

    await renderJournal([createTask()]);

    const sortControl = screen.getByRole('combobox', { name: 'Сортировка журнала задач' }) as HTMLSelectElement;
    expect(sortControl.value).toBe('dueAt:asc');
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenLastCalledWith(
      expect.objectContaining({ sortBy: 'dueAt', sortDir: 'asc' }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    ));

    fireEvent.change(sortControl, { target: { value: 'counterparty:desc' } });
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenLastCalledWith(
      expect.objectContaining({ sortBy: 'counterparty', sortDir: 'desc' }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    ));

    cleanup();
    await renderJournal([createTask()]);
    expect((screen.getByRole('combobox', { name: 'Сортировка журнала задач' }) as HTMLSelectElement).value).toBe('dueAt:asc');
  });

  it('loads one server page and moves to the next page without reloading metadata', async () => {
    const tasks = Array.from({ length: 60 }, (_, index) => createTask({
      id: index + 1,
      title: `Задача страницы ${index + 1}`,
    }));
    await renderJournal(tasks, { waitForTitle: 'Задача страницы 1' });

    const totalLabel = screen.getByText('Задач найдено:');
    expect(within(totalLabel.parentElement as HTMLElement).getByText('60')).toBeTruthy();
    expect(screen.queryByText('Задача страницы 26')).toBeNull();
    expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(
      expect.objectContaining({ page: 1, pageSize: 25 }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );

    apiMock.getTaskJournalAssignees.mockClear();
    apiMock.getTaskJournalSettings.mockClear();
    apiMock.getCalendarSyncSettings.mockClear();
    fireEvent.click(screen.getByRole('button', { name: 'Вперёд' }));

    expect(await screen.findByText('Задача страницы 26')).toBeTruthy();
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenLastCalledWith(
      expect.objectContaining({ page: 2, pageSize: 25 }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    ));
    expect(apiMock.getTaskJournalAssignees).not.toHaveBeenCalled();
    expect(apiMock.getTaskJournalSettings).not.toHaveBeenCalled();
    expect(apiMock.getCalendarSyncSettings).not.toHaveBeenCalled();
  });

  it('debounces server search and keeps global unbilled counter independent of the page', async () => {
    const tasks = Array.from({ length: 30 }, (_, index) => createTask({
      id: index + 1,
      title: index === 29 ? 'Наследство найдено' : `Обычная задача ${index + 1}`,
      billingStatus: 'unbilled',
    }));
    await renderJournal(tasks, { billingAnalysisMode: true, waitForTitle: 'Обычная задача 1' });

    expect(screen.getByText('30 задач без счёта у 1 клиента')).toBeTruthy();
    apiMock.getTaskJournalItems.mockClear();
    fireEvent.change(screen.getByRole('textbox', {
      name: 'Поиск по контрагенту, делу или наименованию задачи',
    }), { target: { value: 'Наследство' } });

    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(
      expect.objectContaining({ search: 'Наследство', page: 1, pageSize: 25 }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    ), { timeout: 2000 });
    expect(await screen.findByText('Наследство найдено')).toBeTruthy();
    expect(screen.queryByText('Обычная задача 1')).toBeNull();
  });

  it('marks a task for manual control from the row actions', async () => {
    await renderJournal([createTask({ isUnderControl: false })]);

    fireEvent.click(screen.getByTitle('Поставить на контроль'));

    await waitFor(() => {
      expect(apiMock.updateTaskJournalControl).toHaveBeenCalledWith(101, true);
    });
    expect(toastMock.success).toHaveBeenCalledWith('Задача поставлена на контроль.', 'Журнал задач');
  });

  it('accepts a task into work from the row actions', async () => {
    await renderJournal([createTask({ status: 'new' })]);

    fireEvent.click(screen.getByTitle('Принять в работу'));

    await waitFor(() => {
      expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(101, 'inProgress', undefined);
    });
  });

  it('sets a new due date when a stale row is returned to revision', async () => {
    await renderJournal([createTask({
      status: 'new',
      dueAt: new Date(Date.now() - 24 * 60 * 60 * 1000).toISOString(),
    })]);

    const statusSelect = Array.from(document.querySelectorAll('select'))
      .find((element) => element.value === 'new') as HTMLSelectElement | undefined;
    expect(statusSelect).toBeTruthy();

    fireEvent.change(statusSelect!, { target: { value: 'revision' } });

    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(
      101,
      'revision',
      undefined,
      expect.any(String),
    ));
  });

  it('keeps priority and billing badges in one compact metadata row', async () => {
    await renderJournal([
      createTask({
        clientBillingClassification: 'internal',
        billingStatus: 'internal',
      }),
    ]);

    const badges = screen.getByTestId('task-meta-badges-101');
    const billingBadge = within(badges).getByTestId('task-billing-badge-101');

    expect(badges.className).toContain('flex');
    expect(badges.className).toContain('flex-nowrap');
    expect(badges.className).toContain('items-center');
    expect(within(badges).getByText('Обычный')).toBeTruthy();
    expect(within(billingBadge).getByText('НЕ ДЛЯ СЧЁТА')).toBeTruthy();
    expect(billingBadge.className).toContain('text-[10px]');
    expect(billingBadge.getAttribute('title')).toBe('НЕОПЛАЧИВАЕМАЯ · НЕ ДЛЯ СЧЁТА');
  });

  it('applies administrator sticker labels and colors in the journal', async () => {
    const stickerStyles = cloneDefaultTaskStickerStyles();
    stickerStyles['priority.normal'] = {
      ...stickerStyles['priority.normal'],
      shortLabel: 'СРЕДНИЙ',
      backgroundColor: '#fef3c7',
      textColor: '#92400e',
      borderColor: '#f59e0b',
    };

    await renderJournal([createTask()], { settings: { stickerStyles } });

    const badges = screen.getByTestId('task-meta-badges-101');
    const priorityBadge = within(badges).getByText('СРЕДНИЙ').closest('span[title]');
    expect(priorityBadge).toBeTruthy();
    expect(priorityBadge?.getAttribute('style')).toContain('background-color: rgb(254, 243, 199)');
    expect(priorityBadge?.getAttribute('style')).toContain('color: rgb(146, 64, 14)');
  });

  it('shows who assigned the task in the journal and task card', async () => {
    await renderJournal([
      createTask({
        assigneeName: 'Кожеваткин Андрей Владимирович',
        authorName: 'Диденко Анна Владимировна',
      }),
    ]);

    expect(screen.getByText('Поручил')).toBeTruthy();
    expect(screen.getByText('Диденко Анна Владимировна')).toBeTruthy();

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    await screen.findByRole('heading', { name: 'Просмотр задачи' });

    expect((screen.getByTestId('task-author-select') as HTMLSelectElement).selectedOptions[0]?.textContent).toBe('Диденко Анна Владимировна');
  });

  it.each(['legacy', 'modern'] as const)('allows an administrator to change the task author in the %s task card', async (taskCardWindowMode) => {
    await renderJournal([
      createTask({
        authorUserId: 8,
        authorName: 'Старый постановщик',
      }),
    ], {
      settings: { taskCardWindowMode },
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Старый постановщик', email: 'old-author@example.com' },
        { id: 9, name: 'Новый постановщик', email: 'new-author@example.com' },
      ],
    });

    await openFirstTaskForEdit();
    const card = screen.getByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.change(within(card).getByTestId('task-author-select'), { target: { value: '9' } });
    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      authorUserId: 9,
    })));
  });
});
