import { setupTaskJournalTests, apiMock, toastMock, dialogMock, clientsState, casesState, authState, permissionsState, todayAt, createTask, renderJournal } from './TaskJournal.test-support';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { Client, ClientCase } from '../../types';
import { type TaskJournalItem } from '../../utils/taskJournalStore';
import { TaskJournal } from './TaskJournal';

describe('TaskJournal', () => {
  setupTaskJournalTests();

  it('synchronizes both journal search fields and keeps the second field usable during loading', async () => {
    await renderJournal([createTask({ title: 'Синтетическая задача' })]);
    const first = screen.getByRole('textbox', { name: 'Поиск по контрагенту, делу или наименованию задачи' }) as HTMLInputElement;
    const second = screen.getByRole('textbox', { name: 'Поиск задач над таблицей' }) as HTMLInputElement;
    fireEvent.change(second, { target: { value: 'договор' } });
    expect(first.value).toBe('договор');
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(expect.objectContaining({ search: 'договор', page: 1 }), expect.anything()));
    fireEvent.change(first, { target: { value: 'другое дело' } });
    expect(second.value).toBe('другое дело');
    let finish!: (value: { items: TaskJournalItem[]; total: number; page: number; pageSize: number }) => void;
    apiMock.getTaskJournalItems.mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
    fireEvent.change(second, { target: { value: 'ожидание' } });
    await waitFor(() => expect(finish).toBeDefined());
    expect(screen.getByRole('textbox', { name: 'Поиск задач над таблицей' })).toBe(second);
    fireEvent.change(second, { target: { value: 'новый поиск' } });
    expect(first.value).toBe('новый поиск');
    await act(async () => finish({ items: [], total: 0, page: 1, pageSize: 25 }));
  });

  it('clears selected tasks when the search above the table changes', async () => {
    const task = createTask({ title: 'Синтетический выбор' });
    await renderJournal([task]);
    fireEvent.click(screen.getByRole('checkbox', { name: `Выбрать задачу ${task.title}` }));
    expect(screen.getByTestId('task-bulk-actions')).toBeTruthy();
    const second = screen.getByRole('textbox', { name: 'Поиск задач над таблицей' });
    fireEvent.change(second, { target: { value: 'другая задача' } });
    fireEvent.change(second, { target: { value: '' } });
    expect(screen.queryByTestId('task-bulk-actions')).toBeNull();
    expect((screen.getByRole('checkbox', { name: `Выбрать задачу ${task.title}` }) as HTMLInputElement).checked).toBe(false);
  });

  it('returns to the first task page when searching above the table', async () => {
    const tasks = Array.from({ length: 30 }, (_, index) => createTask({ id: index + 1, title: `Задача ${index + 1}` }));
    await renderJournal(tasks, { waitForTitle: 'Задача 1' });
    fireEvent.click(screen.getByRole('button', { name: 'Вперёд' }));
    await screen.findByText('Задача 26');
    apiMock.getTaskJournalItems.mockClear();
    fireEvent.change(screen.getByRole('textbox', { name: 'Поиск задач над таблицей' }), { target: { value: 'Задача 2' } });
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(expect.objectContaining({ search: 'Задача 2', page: 1 }), expect.anything()));
  });

  it.each([
    ['authoredByMe', { authorUserId: 7 }],
    ['observer', { participantRole: 'observer' }],
    ['approver', { participantRole: 'approver' }],
    ['underControl', { underControl: true }],
    ['untriaged', { untriaged: true }],
  ] as const)('sends the active %s quick filter to planning export', async (quickFilter, expected) => {
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({ quickFilter, periodMode: 'all' }));
    await renderJournal([createTask()], { waitForTitle: null });
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(expect.objectContaining(expected), expect.anything()));
    fireEvent.click(screen.getByRole('button', { name: 'Word' }));
    await waitFor(() => expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledWith(expect.objectContaining(expected)));
  });

  it('keeps contradictory assignee filters empty in the planning export', async () => {
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({ quickFilter: 'assignedToMe', assigneeFilter: '8', periodMode: 'all' }));
    await renderJournal([createTask()], { waitForTitle: null, assignees: [
      { id: 7, name: 'Текущий', email: 'me@example.test' }, { id: 8, name: 'Другой', email: 'other@example.test' },
    ] });
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(expect.objectContaining({ assigneeUserId: -1 }), expect.anything()));
    fireEvent.click(screen.getByRole('button', { name: 'PDF' }));
    await waitFor(() => expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledWith(expect.objectContaining({ assigneeUserId: -1 })));
  });

  it('keeps the current-user approval scope in the planning export', async () => {
    await renderJournal([createTask()], { waitForTitle: null, mode: 'approvals' });
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(expect.objectContaining({ approvalScope: true }), expect.anything()));
    fireEvent.click(screen.getByRole('button', { name: 'HTML' }));
    await waitFor(() => expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledWith(expect.objectContaining({ approvalScope: true })));
  });

  it('restores filters for each employee without writing another employee search into their preferences', async () => {
    const keyA = 'pp.task_journal.filters.v1.user:7';
    const keyB = 'pp.task_journal.filters.v1.user:8';
    window.localStorage.setItem(keyA, JSON.stringify({ search: 'альфа', quickFilter: 'all' }));
    window.localStorage.setItem(keyB, JSON.stringify({ search: 'бета', quickFilter: 'all' }));
    const writes = vi.spyOn(Storage.prototype, 'setItem');
    const view = await renderJournal([createTask()], { waitForTitle: null });
    expect((screen.getByPlaceholderText('Поиск по контрагенту, делу или наименованию задачи') as HTMLInputElement).value).toBe('альфа');
    writes.mockClear();
    apiMock.getTaskJournalItems.mockClear();
    authState.user = { ...authState.user, id: 8 };
    view.rerender(<TaskJournal />);
    await waitFor(() => expect((screen.getByPlaceholderText('Поиск по контрагенту, делу или наименованию задачи') as HTMLInputElement).value).toBe('бета'));
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(expect.objectContaining({ search: 'бета' }), expect.anything()));
    expect(apiMock.getTaskJournalItems.mock.calls.some(([params]) => params?.search === 'альфа')).toBe(false);
    expect(writes.mock.calls.filter(([key]) => key === keyB).every(([, value]) => JSON.parse(value).search === 'бета')).toBe(true);
    authState.user = { ...authState.user, id: 7 };
    view.rerender(<TaskJournal />);
    await waitFor(() => expect((screen.getByPlaceholderText('Поиск по контрагенту, делу или наименованию задачи') as HTMLInputElement).value).toBe('альфа'));
    expect(JSON.parse(window.localStorage.getItem(keyB) ?? '{}').search).toBe('бета');
    writes.mockRestore();
  });

  it('clears previous employee tasks while the next employee list is loading', async () => {
    const view = await renderJournal([createTask({ title: 'Личная задача первого сотрудника' })]);
    let finish!: (response: Awaited<ReturnType<typeof apiMock.getTaskJournalItems>>) => void;
    apiMock.getTaskJournalItems.mockClear();
    apiMock.getTaskJournalItems.mockImplementation(() => new Promise((resolve) => { finish = resolve; }));
    authState.user = { ...authState.user, id: 8 };
    view.rerender(<TaskJournal />);
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalled());
    expect(screen.queryByText('Личная задача первого сотрудника')).toBeNull();
    finish({ items: [], total: 0, page: 1, pageSize: 50 });
    await waitFor(() => expect(screen.queryByText('Личная задача первого сотрудника')).toBeNull());
  });

  it('ignores the previous employee list if its response arrives after the account switch', async () => {
    const view = await renderJournal([createTask({ id: 101, title: 'Прежняя задача' })]);
    let finishOld!: (response: Awaited<ReturnType<typeof apiMock.getTaskJournalItems>>) => void;
    apiMock.getTaskJournalItems.mockReset();
    apiMock.getTaskJournalItems.mockImplementationOnce(() => new Promise((resolve) => { finishOld = resolve; }));
    apiMock.getTaskJournalItems.mockResolvedValue({ items: [createTask({ id: 102, title: 'Задача нового сотрудника' })], total: 1, page: 1, pageSize: 50 });
    fireEvent.click(screen.getByTestId('task-quick-filter-assignedToMe'));
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalledTimes(1));
    authState.user = { ...authState.user, id: 8 };
    view.rerender(<TaskJournal />);
    expect(await screen.findByText('Задача нового сотрудника')).toBeTruthy();
    await act(async () => finishOld({ items: [createTask({ id: 103, title: 'Поздний чужой ответ' })], total: 1, page: 1, pageSize: 50 }));
    expect(screen.getByText('Задача нового сотрудника')).toBeTruthy();
    expect(screen.queryByText('Поздний чужой ответ')).toBeNull();
  });


  it('opens with all tasks quick filter and remembers switching to current user tasks', async () => {
    await renderJournal([
      createTask(),
      createTask({
        id: 102,
        title: 'Чужая задача',
        assigneeUserId: 8,
        assigneeName: 'Другой исполнитель',
        participants: [
          { userId: 8, role: 'assignee', displayName: 'Другой исполнитель', approvalStatus: 'pending' },
        ],
      }),
    ]);

    expect(screen.getByText('Тестовая задача')).toBeTruthy();
    expect(screen.getByText('Чужая задача')).toBeTruthy();
    expect(screen.getByTestId('task-quick-filter-all').getAttribute('aria-pressed')).toBe('true');

    fireEvent.click(screen.getByTestId('task-quick-filter-assignedToMe'));

    await waitFor(() => expect(screen.queryByText('Чужая задача')).toBeNull());
    await waitFor(() => {
      expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
        quickFilter: 'assignedToMe',
      });
    });
  });

  it('shows only tasks without a client and assignee in the untriaged view', async () => {
    await renderJournal([
      createTask({ id: 101, title: 'Разобрать срочную задачу', clientId: null, clientName: undefined, legalEntityId: null, assigneeUserId: null, assigneeName: '', participants: [] }),
      createTask({ id: 102, title: 'Назначенная задача' }),
    ]);

    fireEvent.click(screen.getByTestId('task-quick-filter-untriaged'));

    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(expect.objectContaining({ untriaged: true }), expect.anything()));
    expect(await screen.findByText('Разобрать срочную задачу')).toBeTruthy();
    expect(screen.queryByText('Назначенная задача')).toBeNull();
  });

  it('opens the dedicated untriaged section with independent saved filters', async () => {
    await renderJournal([
      createTask({ id: 101, title: 'Нужно распределить', isInternal: true, clientId: null, clientName: undefined, clientCaseId: null, assigneeUserId: null, assigneeName: '', participants: [] }),
      createTask({ id: 102, title: 'Уже назначена' }),
    ], { mode: 'untriaged', waitForTitle: null });

    expect(await screen.findByRole('heading', { name: 'Неразобранные' })).toBeTruthy();
    expect(await screen.findByText('Нужно распределить')).toBeTruthy();
    expect(screen.queryByText('Уже назначена')).toBeNull();
    expect(screen.queryByTestId('task-quick-filter-row')).toBeNull();
    expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(expect.objectContaining({ untriaged: true }), expect.anything());
    expect(window.localStorage.getItem('pp.task_journal.filters.v1.user:7')).toBeNull();
  });

  it.each(['legacy', 'modern'] as const)('asks how to account for an untriaged task before linking it in the %s card', async (taskCardWindowMode) => {
    await renderJournal([createTask({
      isInternal: true,
      clientId: null,
      clientName: undefined,
      clientCaseId: null,
      clientCaseTitle: undefined,
      legalEntityId: null,
      objectName: 'Внутренняя задача',
      assigneeUserId: null,
      assigneeName: '',
      participants: [],
    })], { settings: { taskCardWindowMode } });

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    await screen.findByRole('heading', { name: 'Просмотр задачи' });
    fireEvent.click(screen.getAllByRole('button', { name: 'Редактировать задачу' })[0]);
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.click(within(card).getByTestId('task-internal-checkbox'));
    fireEvent.click(within(card).getByRole('button', { name: taskCardWindowMode === 'modern' ? 'Контрагент' : 'Выберите контрагента' }));
    const lookup = await screen.findByRole('dialog', { name: 'Выбор контрагента' });
    fireEvent.click(within(lookup).getByRole('button', { name: /Иванов Иван Иванович/ }));
    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить' }));

    expect(await screen.findByRole('heading', { name: 'Выберите клиента и дело' })).toBeTruthy();
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
    fireEvent.change(screen.getByTestId('task-billing-wizard-case'), { target: { value: '100' } });
    fireEvent.click(screen.getByRole('button', { name: 'Проверить договор' }));
    await screen.findByRole('heading', { name: 'Определите учёт задачи' });
    fireEvent.click(screen.getByRole('button', { name: /^Платная задача/ }));
    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      clientId: 10,
      clientCaseId: 100,
      clientBillingClassification: 'billable',
      isUntriaged: false,
    })));
  });

  it('keeps the search filter after opening a task from the filtered journal', async () => {
    await renderJournal([
      createTask({
        id: 101,
        title: 'Egor task',
        clientName: 'Egorov Sergey',
        objectName: 'EGOROV - BANKRUPTCY',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
        ],
      }),
      createTask({
        id: 102,
        title: 'Other task',
        clientName: 'Petrov Petr',
        objectName: 'PETROV - BANKRUPTCY',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
        ],
      }),
    ], { withRoute: true });

    const searchInput = screen.getByRole('textbox') as HTMLInputElement;
    fireEvent.change(searchInput, { target: { value: 'EGOROV' } });

    expect(await screen.findByText('Egor task')).toBeTruthy();
    await waitFor(() => expect(screen.queryByText('Other task')).toBeNull());

    const row = screen.getByText('Egor task').closest('tr');
    expect(row).toBeTruthy();
    fireEvent.click(within(row as HTMLTableRowElement).getByTitle('Просмотр'));

    await screen.findByRole('heading', { name: 'Просмотр задачи' });

    expect(searchInput.value).toBe('EGOROV');
    expect(screen.queryByTestId('task-table')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Журнал задач' }));

    const restoredSearchInput = await screen.findByRole('textbox') as HTMLInputElement;
    expect(restoredSearchInput.value).toBe('EGOROV');
    expect(screen.getByText('Egor task')).toBeTruthy();
    expect(screen.queryByText('Other task')).toBeNull();

    await waitFor(() => {
      expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
        search: 'EGOROV',
      });
    });
  });

  it('does not match another client by hidden task fields when searching by surname', async () => {
    await renderJournal([
      createTask({
        id: 101,
        title: 'ЕГОРОВА - счет 94',
        clientName: 'Егорова Татьяна Евгеньевна',
        clientCaseTitle: 'ОСПАРИВАНИЕ СДЕЛКИ ДОМ+ЗЕМЛЯ',
        objectName: 'Егорова Татьяна Евгеньевна - ОСПАРИВАНИЕ СДЕЛКИ',
      }),
      createTask({
        id: 102,
        title: 'ВОЯКИНА - решить вопрос по заключению договора',
        clientName: 'Воякина Елена Михайловна',
        clientCaseTitle: 'ВОЯКИНА - оформление дома и земли',
        objectName: 'Воякина Елена Михайловна - ОФОРМЛЕНИЕ ДОМА',
        assigneeName: 'Егорова Татьяна',
        authorName: 'Егорова Татьяна',
        result: 'Егорова упомянута во внутреннем результате',
        tags: ['Егорова'],
        participants: [
          { userId: 8, role: 'observer', displayName: 'Егорова Татьяна', approvalStatus: 'pending' },
        ],
      }),
    ]);

    const searchInput = screen.getByRole('textbox') as HTMLInputElement;
    fireEvent.change(searchInput, { target: { value: 'ЕГОРОВА' } });

    expect(await screen.findByText('ЕГОРОВА - счет 94')).toBeTruthy();
    await waitFor(() => expect(screen.queryByText('ВОЯКИНА - решить вопрос по заключению договора')).toBeNull());
  });

  it('filters tasks by due date range and remembers the range', async () => {
    await renderJournal([
      createTask({
        id: 101,
        title: 'May task',
        dueAt: new Date(2026, 4, 10, 10, 0, 0, 0).toISOString(),
      }),
      createTask({
        id: 102,
        title: 'June task',
        dueAt: new Date(2026, 5, 10, 10, 0, 0, 0).toISOString(),
      }),
    ], { waitForTitle: null });

    fireEvent.change(screen.getByTestId('task-date-from-filter'), { target: { value: '2026-05-01' } });
    fireEvent.change(screen.getByTestId('task-date-to-filter'), { target: { value: '2026-05-31' } });

    expect(await screen.findByText('May task')).toBeTruthy();
    await waitFor(() => expect(screen.queryByText('June task')).toBeNull());
    await waitFor(() => {
      expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
        dateFrom: '2026-05-01',
        dateTo: '2026-05-31',
      });
    });
  });

  it('filters tasks by priority and remembers the selected priority', async () => {
    await renderJournal([
      createTask({
        id: 101,
        title: 'Обычная задача',
        priority: 'normal',
      }),
      createTask({
        id: 102,
        title: 'Срочная задача',
        priority: 'urgent',
      }),
    ]);

    fireEvent.change(screen.getByTestId('task-priority-filter'), { target: { value: 'urgent' } });

    expect(await screen.findByText('Срочная задача')).toBeTruthy();
    await waitFor(() => expect(screen.queryByText('Обычная задача')).toBeNull());
    await waitFor(() => {
      expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
        priorityFilter: 'urgent',
      });
    });
  });

  it.each([true, false])('does not show previous counterparty cases (loading: %s)', async (loading) => {
    clientsState.clients.push({ id: 11, name: 'Петров Петр Петрович' } as Client);
    casesState.staleCases = [{ id: 100, clientId: 10, title: 'БАНКРОТСТВО', status: 'Open', createdAt: todayAt(8) } as ClientCase];
    casesState.loading = loading;
    await renderJournal([createTask()], { waitForTitle: null });

    fireEvent.change(screen.getByRole('textbox', { name: 'Поиск по контрагенту, делу или наименованию задачи' }), { target: { value: 'Петров' } });
    fireEvent.click(screen.getByRole('option', { name: 'Петров Петр Петрович' }));
    const caseFilter = screen.getByTestId('task-case-filter') as HTMLSelectElement;
    expect(caseFilter.disabled).toBe(loading);
    expect(within(caseFilter).queryByText('БАНКРОТСТВО', { selector: 'option' })).toBeNull();
  });

  it('does not expose a previous legal entity case in the selected entity filter', async () => {
    casesState.staleCases = [{ id: 100, clientId: 10, title: 'БАНКРОТСТВО', status: 'Open', createdAt: todayAt(8) } as ClientCase];
    await renderJournal([createTask()], { waitForTitle: null });
    fireEvent.change(screen.getByRole('textbox', { name: 'Поиск по контрагенту, делу или наименованию задачи' }), { target: { value: 'Ромашка' } });
    fireEvent.click(screen.getByRole('option', { name: 'ООО Ромашка' }));
    expect(within(screen.getByTestId('task-case-filter')).queryByText('БАНКРОТСТВО', { selector: 'option' })).toBeNull();
  });

  it('does not expose case options when the user has no case view permission', async () => {
    authState.isAdmin = false;
    permissionsState.caseCanView = false;
    casesState.staleCases = [{ id: 100, clientId: 10, title: 'БАНКРОТСТВО', status: 'Open', createdAt: todayAt(8) } as ClientCase];
    await renderJournal([createTask()], { waitForTitle: null });
    fireEvent.change(screen.getByRole('textbox', { name: 'Поиск по контрагенту, делу или наименованию задачи' }), { target: { value: 'Иванов' } });
    fireEvent.click(screen.getByRole('option', { name: 'Иванов Иван Иванович' }));
    const caseFilter = screen.getByTestId('task-case-filter') as HTMLSelectElement;
    expect(caseFilter.disabled).toBe(true);
    expect(within(caseFilter).queryByText('БАНКРОТСТВО', { selector: 'option' })).toBeNull();
  });

  it('filters tasks by case and remembers the selected case', async () => {
    clientsState.clients.push({ id: 11, name: 'Петров Петр Петрович' } as Client);
    casesState.cases = [
      { id: 100, clientId: 10, title: 'БАНКРОТСТВО', status: 'Open', createdAt: todayAt(8) } as ClientCase,
      { id: 200, clientId: 11, title: 'НАСЛЕДСТВО', status: 'Open', createdAt: todayAt(8) } as ClientCase,
      { id: 300, legalEntityId: 20, title: 'КОРПОРАТИВНЫЙ СПОР', status: 'Open', createdAt: todayAt(8) } as ClientCase,
    ];
    await renderJournal([
      createTask({
        id: 101,
        title: 'Задача по банкротству',
        clientId: 10,
        clientName: 'Иванов Иван Иванович',
        clientCaseId: 100,
        clientCaseTitle: 'БАНКРОТСТВО',
      }),
      createTask({
        id: 102,
        title: 'Задача по наследству',
        clientId: 11,
        clientName: 'Петров Петр Петрович',
        clientCaseId: 200,
        clientCaseTitle: 'НАСЛЕДСТВО',
      }),
      createTask({
        id: 103,
        title: 'Задача по корпоративному спору',
        clientId: null,
        clientName: undefined,
        legalEntityId: 20,
        legalEntityName: 'ООО Ромашка',
        clientCaseId: 300,
        clientCaseTitle: 'КОРПОРАТИВНЫЙ СПОР',
      }),
    ]);

    const caseFilter = screen.getByTestId('task-case-filter') as HTMLSelectElement;
    expect(caseFilter.disabled).toBe(true);
    expect(within(caseFilter).queryByText('БАНКРОТСТВО', { selector: 'option' })).toBeNull();
    expect(within(caseFilter).queryByText('НАСЛЕДСТВО', { selector: 'option' })).toBeNull();

    const counterpartySearch = screen.getByRole('textbox', {
      name: 'Поиск по контрагенту, делу или наименованию задачи',
    }) as HTMLInputElement;
    fireEvent.change(counterpartySearch, { target: { value: 'Петров' } });
    fireEvent.click(screen.getByRole('option', { name: 'Петров Петр Петрович' }));

    expect(caseFilter.disabled).toBe(false);
    expect(screen.getByTestId('task-counterparty-search').getAttribute('data-selected-value')).toBe('client:11');
    expect(within(caseFilter).queryByText('БАНКРОТСТВО', { selector: 'option' })).toBeNull();
    expect(within(caseFilter).getByText('НАСЛЕДСТВО', { selector: 'option' })).toBeTruthy();

    fireEvent.change(caseFilter, { target: { value: '200' } });

    expect(await screen.findByText('Задача по наследству')).toBeTruthy();
    await waitFor(() => expect(screen.queryByText('Задача по банкротству')).toBeNull());
    await waitFor(() => {
      expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
        counterpartyFilter: 'client:11',
        caseFilter: '200',
      });
    });

    fireEvent.click(screen.getByRole('button', { name: 'Word' }));
    await waitFor(() => expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledWith(expect.objectContaining({
      format: 'word',
      clientCaseId: 200,
    })));

    fireEvent.click(screen.getByTestId('task-view-calendar'));
    await waitFor(() => expect(apiMock.getCaseCalendarEvents).toHaveBeenCalledWith(expect.objectContaining({
      clientCaseId: 200,
      includeTasks: false,
    })));

    fireEvent.change(counterpartySearch, { target: { value: 'Иванов' } });
    fireEvent.click(screen.getByRole('option', { name: 'Иванов Иван Иванович' }));
    expect(caseFilter.value).toBe('all');
    expect(within(caseFilter).getByText('БАНКРОТСТВО', { selector: 'option' })).toBeTruthy();
    expect(within(caseFilter).queryByText('НАСЛЕДСТВО', { selector: 'option' })).toBeNull();

    apiMock.downloadTaskPlanningReport.mockClear();
    fireEvent.click(screen.getByRole('button', { name: 'Word' }));
    await waitFor(() => expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledWith(expect.objectContaining({
      clientId: 10,
      legalEntityId: undefined,
      clientCaseId: undefined,
    })));

    fireEvent.change(counterpartySearch, { target: { value: 'Ромашка' } });
    fireEvent.click(screen.getByRole('option', { name: 'ООО Ромашка' }));
    expect(within(caseFilter).getByText('КОРПОРАТИВНЫЙ СПОР', { selector: 'option' })).toBeTruthy();
    expect(within(caseFilter).queryByText('БАНКРОТСТВО', { selector: 'option' })).toBeNull();
  });

  it('saves, applies and deletes custom task journal views', async () => {
    dialogMock.prompt.mockResolvedValueOnce('Срочные задачи');

    try {
      await renderJournal([
        createTask({
          id: 101,
          title: 'Обычная задача',
          priority: 'normal',
        }),
        createTask({
          id: 102,
          title: 'Срочная задача',
          priority: 'urgent',
        }),
      ], { waitForTitle: null });

      const searchInput = screen.getByPlaceholderText('Поиск по контрагенту, делу или наименованию задачи') as HTMLInputElement;
      fireEvent.change(searchInput, { target: { value: 'Иванов' } });
      fireEvent.click(screen.getByRole('option', { name: 'Иванов Иван Иванович' }));
      fireEvent.change(screen.getByTestId('task-case-filter'), { target: { value: '100' } });
      fireEvent.change(screen.getByTestId('task-priority-filter'), { target: { value: 'urgent' } });
      fireEvent.change(searchInput, { target: { value: 'Срочная' } });
      fireEvent.click(screen.getByTestId('task-save-view'));

      await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith(
        'Представление «Срочные задачи» сохранено.',
        'Журнал задач',
      ));

      const rawSavedViews = window.localStorage.getItem('pp.task_journal.views.v1.user:7');
      expect(rawSavedViews).toBeTruthy();
      const savedViews = JSON.parse(rawSavedViews ?? '[]') as Array<{
        id: string;
        name: string;
        filters: { counterpartyFilter?: string; caseFilter?: string; priorityFilter?: string; search?: string };
      }>;
      expect(savedViews).toHaveLength(1);
      expect(savedViews[0]).toMatchObject({
        name: 'Срочные задачи',
        filters: {
          counterpartyFilter: 'client:10',
          caseFilter: '100',
          priorityFilter: 'urgent',
          search: 'Срочная',
        },
      });

      fireEvent.change(screen.getByTestId('task-priority-filter'), { target: { value: 'all' } });
      fireEvent.click(screen.getByRole('button', { name: 'Очистить контрагента Иванов Иван Иванович' }));
      fireEvent.change(searchInput, { target: { value: '' } });

      fireEvent.change(screen.getByTestId('task-saved-view-select'), { target: { value: savedViews[0].id } });

      await waitFor(() => {
        expect((screen.getByTestId('task-priority-filter') as HTMLSelectElement).value).toBe('urgent');
        expect(screen.getByTestId('task-counterparty-search').getAttribute('data-selected-value')).toBe('client:10');
        expect((screen.getByTestId('task-case-filter') as HTMLSelectElement).value).toBe('100');
        expect(searchInput.value).toBe('Срочная');
      });

      fireEvent.click(screen.getByTestId('task-delete-view'));

      await waitFor(() => {
        expect(JSON.parse(window.localStorage.getItem('pp.task_journal.views.v1.user:7') ?? '[]')).toHaveLength(0);
      });
    } finally {
      dialogMock.prompt.mockResolvedValue('Срочные задачи');
    }
  });

  it('keeps date filters in a separate responsive row from view controls', async () => {
    await renderJournal();

    const mainFilterRow = screen.getByTestId('task-main-filter-row');
    const dateAndViewRow = screen.getByTestId('task-date-and-view-row');
    const dateFilterRow = screen.getByTestId('task-date-filter-row');
    const viewControls = screen.getByTestId('task-view-controls');
    const dateFrom = screen.getByTestId('task-date-from-filter');
    const dateTo = screen.getByTestId('task-date-to-filter');

    expect(mainFilterRow.className).toContain('xl:grid-cols-3');
    expect(mainFilterRow.className).toContain('2xl:grid-cols-[minmax(240px,1fr)_repeat(5,minmax(140px,190px))]');
    expect(screen.queryByTestId('task-counterparty-filter')).toBeNull();
    expect(screen.getByTestId('task-counterparty-search')).toBeTruthy();
    expect(mainFilterRow.contains(dateFrom)).toBe(false);
    expect(dateFilterRow.contains(dateFrom)).toBe(true);
    expect(dateFilterRow.contains(dateTo)).toBe(true);
    expect(dateAndViewRow.contains(viewControls)).toBe(true);
    expect(dateFilterRow.contains(viewControls)).toBe(false);
    expect(dateAndViewRow.className).toContain('flex-col');
  });

  it('applies day, week, month and full period buttons to the list view', async () => {
    await renderJournal([createTask()], { periodMode: 'day', waitForTitle: null });

    const expectPeriodMode = async (periodMode: 'day' | 'week' | 'month' | 'all') => {
      await waitFor(() => {
        expect(screen.getByTestId(`task-period-${periodMode}`).getAttribute('aria-pressed')).toBe('true');
        expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
          periodMode,
        });
      });
    };

    await expectPeriodMode('day');

    fireEvent.click(screen.getByTestId('task-period-week'));
    await expectPeriodMode('week');

    fireEvent.click(screen.getByTestId('task-period-month'));
    await expectPeriodMode('month');

    fireEvent.click(screen.getByTestId('task-period-all'));
    await expectPeriodMode('all');
  });

  it('shows open unresolved tasks across all assignees for the full period', async () => {
    const oldDate = new Date();
    oldDate.setFullYear(oldDate.getFullYear() - 1);
    oldDate.setHours(11, 0, 0, 0);

    await renderJournal([
      createTask({
        id: 101,
        title: 'Старая открытая задача',
        dueAt: oldDate.toISOString(),
        assigneeUserId: 7,
        assigneeName: 'Администратор',
      }),
      createTask({
        id: 102,
        title: 'Ожидает другого исполнителя',
        dueAt: oldDate.toISOString(),
        status: 'waiting',
        assigneeUserId: 8,
        assigneeName: 'Другой исполнитель',
        participants: [
          { userId: 8, role: 'assignee', displayName: 'Другой исполнитель', approvalStatus: 'pending' },
        ],
      }),
      createTask({
        id: 103,
        title: 'Закрытая старая задача',
        dueAt: oldDate.toISOString(),
        status: 'done',
        result: 'Готово',
        assigneeUserId: 7,
        assigneeName: 'Администратор',
      }),
    ], {
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Другой исполнитель', email: 'executor@example.com' },
      ],
      waitForTitle: null,
    });

    fireEvent.click(screen.getByTestId('task-quick-filter-all'));
    fireEvent.click(screen.getByTestId('task-period-all'));

    await waitFor(() => expect(screen.getByText('Старая открытая задача')).toBeTruthy());
    expect(screen.getByText('Ожидает другого исполнителя')).toBeTruthy();
    expect(screen.queryByText('Закрытая старая задача')).toBeNull();
    expect((screen.getByLabelText('Статус') as HTMLSelectElement).value).toBe('active');
    expect((screen.getByLabelText('Исполнитель') as HTMLSelectElement).value).toBe('all');
    expect(screen.getByTestId('task-period-all').getAttribute('aria-pressed')).toBe('true');
  });

  it('switches journal presentation modes from the view controls', async () => {
    await renderJournal();

    expect(screen.getByTestId('task-table')).toBeTruthy();
    expect(screen.getByTestId('task-view-list').getAttribute('aria-pressed')).toBe('true');

    fireEvent.click(screen.getByTestId('task-view-board'));

    await waitFor(() => expect(screen.getByTestId('task-board')).toBeTruthy());
    expect(screen.queryByTestId('task-table')).toBeNull();
    expect(screen.getByTestId('task-view-board').getAttribute('aria-pressed')).toBe('true');

    fireEvent.click(screen.getByTestId('task-view-calendar'));

    await waitFor(() => expect(screen.getByTestId(`task-calendar-task-101`)).toBeTruthy());
    expect(screen.getByTestId('task-view-calendar').getAttribute('aria-pressed')).toBe('true');

    fireEvent.click(screen.getByTestId('task-view-timeline'));

    await waitFor(() => expect(screen.getByTestId('task-timeline-body-grid')).toBeTruthy());
    expect(screen.getByTestId('task-view-timeline').getAttribute('aria-pressed')).toBe('true');
    await waitFor(() => {
      expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
        viewMode: 'timeline',
      });
    });

    fireEvent.click(screen.getByTestId('task-view-list'));

    await waitFor(() => expect(screen.getByTestId('task-table')).toBeTruthy());
    expect(screen.getByTestId('task-view-list').getAttribute('aria-pressed')).toBe('true');
  });

  it('groups board cards by workflow status and opens linked client, case and task', async () => {
    const onOpenClient = vi.fn();
    const onOpenCase = vi.fn();
    await renderJournal([
      createTask({ id: 101, title: 'Новая задача', status: 'new' }),
      createTask({ id: 102, title: 'Рабочая задача', status: 'inProgress' }),
      createTask({ id: 103, title: 'Готовая задача', status: 'done', result: 'Выполнено' }),
    ], { initialView: 'board', onOpenClient, onOpenCase });

    fireEvent.click(screen.getByRole('button', { name: 'По статусу' }));

    await waitFor(() => expect(within(screen.getByTestId('task-board-column-closed')).getByText('Готовая задача')).toBeTruthy());
    expect(within(screen.getByTestId('task-board-column-new')).getByText('Новая задача')).toBeTruthy();
    expect(within(screen.getByTestId('task-board-column-inProgress')).getByText('Рабочая задача')).toBeTruthy();
    expect((screen.getByLabelText('Статус') as HTMLSelectElement).value).toBe('all');

    const card = screen.getByTestId('task-board-card-101');
    fireEvent.click(within(card).getByRole('button', { name: 'Иванов Иван Иванович' }));
    fireEvent.click(within(card).getByRole('button', { name: 'БАНКРОТСТВО' }));
    fireEvent.click(within(card).getByRole('button', { name: 'Новая задача' }));
    expect(onOpenClient).toHaveBeenCalledWith(10, 100);
    expect(onOpenCase).toHaveBeenCalledWith(100, 10);
    expect(await screen.findByTestId('task-legacy-card-window')).toBeTruthy();
  });

  it('shows an explicit accept action only on eligible board cards', async () => {
    const tasks = [
      createTask({ id: 101, title: 'Нужно принять', status: 'new' }),
      createTask({ id: 102, title: 'Уже в работе', status: 'inProgress' }),
    ];
    await renderJournal(tasks, { initialView: 'board' });
    apiMock.updateTaskJournalStatus.mockImplementation(async (id: number, status: TaskJournalItem['status']) => {
      const task = tasks.find((item) => item.id === id)!;
      task.status = status;
      return task;
    });
    fireEvent.click(screen.getByRole('button', { name: 'По статусу' }));

    expect(within(screen.getByTestId('task-board-card-101')).getByRole('button', { name: 'Принять в работу' })).toBeTruthy();
    expect(within(screen.getByTestId('task-board-card-102')).queryByRole('button', { name: 'Принять в работу' })).toBeNull();
    fireEvent.click(within(screen.getByTestId('task-board-card-101')).getByRole('button', { name: 'Принять в работу' }));
    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(101, 'inProgress', undefined));
    await waitFor(() => expect(within(screen.getByTestId('task-board-column-inProgress')).getByText('Нужно принять')).toBeTruthy());
    expect(within(screen.getByTestId('task-board-column-new')).queryByText('Нужно принять')).toBeNull();
  });

  it('lets an assigned executor accept a task without edit permission from the board and list', async () => {
    authState.isAdmin = false;
    permissionsState.taskCanEdit = false;
    permissionsState.taskCanEditOthers = false;
    await renderJournal([createTask({ status: 'new', assigneeUserId: 7, authorUserId: 8 })], { initialView: 'board' });

    fireEvent.click(within(screen.getByTestId('task-board-card-101')).getByRole('button', { name: 'Принять в работу' }));
    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(101, 'inProgress', undefined));
    fireEvent.click(screen.getByTestId('task-view-list'));
    await waitFor(() => expect(screen.getByTitle('Принять в работу')).toBeTruthy());
  });

  it('places a task due earlier today only in the overdue board column', async () => {
    await renderJournal([createTask({ dueAt: new Date(Date.now() - 60_000).toISOString() })], { initialView: 'board' });

    expect(within(screen.getByTestId('task-board-column-overdue')).getByText('Тестовая задача')).toBeTruthy();
    expect(within(screen.getByTestId('task-board-column-today')).queryByText('Тестовая задача')).toBeNull();
  });

  it('keeps task filter and view controls constrained on mobile widths', async () => {
    await renderJournal();

    expect(screen.getByTestId('task-quick-filter-row').className).toContain('max-w-full');
    expect(screen.getByTestId('task-view-controls').className).toContain('max-w-full');
    expect(screen.getByTestId('task-period-control').className).toContain('overflow-x-auto');
    expect(screen.getByTestId('task-view-mode-control').className).toContain('overflow-x-auto');
    expect(screen.getByTestId('task-journal-shell').className).toContain('w-full');
    expect(screen.getByTestId('task-journal-shell').className).toContain('min-w-0');
    expect(screen.getByTestId('task-journal-shell').className).not.toContain('max-w-[');
    expect(screen.getByTestId('task-journal-shell').className).not.toContain('mx-auto');
    expect(screen.getByTestId('task-table-card').className).toContain('min-w-0');
    expect(screen.getByTestId('task-table-card').className).toContain('max-w-full');
    expect(screen.getByTestId('task-table-scroll').className).toContain('overflow-x-auto');
    expect(screen.getByTestId('task-table-scroll').className).toContain('overscroll-x-contain');
    expect(screen.getByTestId('task-table').className).toContain('pp-task-journal-table');
    expect(screen.getByTestId('task-table').className).toContain('w-max');
    expect(screen.getByTestId('task-table').className).toContain('min-w-full');
    expect(screen.getByTestId('task-table').className).toContain('table-fixed');
    expect(screen.getByText('Действия').closest('th')?.className).toContain('sticky');
    expect(screen.getByTitle('Просмотр').closest('td')?.className).toContain('sticky');
    expect(screen.getByTestId('task-table').className).not.toContain('min-w-[920px]');
    expect(screen.getByTestId('task-column-header-author').className).not.toContain('hidden');
  });

  it('restores the saved journal presentation mode for the current user', async () => {
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'timeline',
      periodMode: 'week',
    }));

    await renderJournal([createTask({ dueAt: todayAt(11), plannedDurationMinutes: 60 })], { waitForTitle: null });

    await waitFor(() => expect(screen.getByTestId('task-timeline-body-grid')).toBeTruthy());
    expect(screen.getByTestId('task-view-timeline').getAttribute('aria-pressed')).toBe('true');
  });

  it.each([
    ['hide', 'done', false],
    ['hide', 'cancelled', false],
    ['color', 'done', true],
    ['color', 'cancelled', true],
  ] as const)('preserves %s/%s tasks in the list and applies visibility across calendar views', async (action, status, completedVisible) => {
    const settings = await apiMock.getCalendarSyncSettings();
    apiMock.getCalendarSyncSettings.mockResolvedValue({ ...settings, completedTaskCalendarAction: action });

    await renderJournal([
      createTask({ id: 101, title: 'Открытая календарная задача', status: 'inProgress', dueAt: todayAt(10) }),
      createTask({ id: 102, title: 'Закрытая календарная задача', status, dueAt: todayAt(10) }),
    ], { initialView: 'calendar', waitForTitle: null });

    await waitFor(() => expect(screen.getByTestId('task-calendar-task-101')).toBeTruthy());
    await waitFor(() => expect(apiMock.getCalendarSyncSettings).toHaveBeenCalled());
    await waitFor(() => expect(Boolean(screen.queryByTestId('task-calendar-task-102'))).toBe(completedVisible));
    expect(screen.getByText('Плановая загрузка: 1 ч')).toBeTruthy();
    expect(screen.queryByText(/пересечений 1/)).toBeNull();

    fireEvent.click(screen.getByTestId('task-view-timeline'));
    await waitFor(() => expect(screen.getByTestId('task-timeline-task-101')).toBeTruthy());
    expect(Boolean(screen.queryByTestId('task-timeline-task-102'))).toBe(completedVisible);
    expect(screen.getByText(/Администратор: активных 1 · 1 ч/)).toBeTruthy();
    if (completedVisible && status === 'cancelled') {
      expect(within(screen.getByTestId('task-timeline-task-102')).getByText('Отменена')).toBeTruthy();
    }
    fireEvent.click(screen.getByTestId('task-view-calendar'));
    await waitFor(() => expect(screen.getByTestId('task-calendar-task-101')).toBeTruthy());
    expect(Boolean(screen.queryByTestId('task-calendar-task-102'))).toBe(completedVisible);
    fireEvent.click(screen.getByTestId('task-view-list'));
    fireEvent.change(screen.getByLabelText('Статус'), { target: { value: 'all' } });
    await waitFor(() => expect(screen.getByText('Закрытая календарная задача')).toBeTruthy());
    expect(screen.getByText('Открытая календарная задача')).toBeTruthy();
  });

  it.each([
    ['cancelled', 480, '0 мин'],
    ['inProgress', 300, '10 ч'],
  ] as const)('shows total calendar workload without per-task bounds for %s', async (status, duration, expected) => {
    await renderJournal([
      createTask({ id: 101, status, plannedDurationMinutes: duration, dueAt: todayAt(10) }),
      createTask({ id: 102, title: 'Вторая задача', status, plannedDurationMinutes: duration, dueAt: todayAt(15) }),
    ], { initialView: 'calendar', waitForTitle: null });
    await waitFor(() => expect(screen.getByTestId('task-calendar-task-101')).toBeTruthy());
    expect(screen.getByText(`Плановая загрузка: ${expected}`)).toBeTruthy();
    if (status === 'cancelled') {
      expect(screen.queryByText(/пересечений 1/)).toBeNull();
    }
  });

  it('saves and applies the selected task calendar color mode', async () => {
    await renderJournal([createTask({ priority: 'high' })], { initialView: 'calendar' });

    await waitFor(() => expect(screen.getByTestId('task-calendar-task-101')).toBeTruthy());
    fireEvent.click(screen.getByTestId('task-color-mode-priority'));

    await waitFor(() => {
      expect(screen.getByTestId('task-calendar-task-101').className).toContain('border-amber-200');
      expect(JSON.parse(window.localStorage.getItem('pp.task_journal.filters.v1.user:7') ?? '{}')).toMatchObject({
        calendarColorMode: 'priority',
      });
    });
  });

  it('restores the saved task calendar color mode', async () => {
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      viewMode: 'calendar',
      periodMode: 'week',
      calendarColorMode: 'status',
    }));

    await renderJournal([createTask({ status: 'inProgress' })], { waitForTitle: null });

    await waitFor(() => expect(screen.getByTestId('task-calendar-task-101').className).toContain('border-blue-200'));
    expect(screen.getByTestId('task-color-mode-status').getAttribute('aria-pressed')).toBe('true');
  });

  it('filters tasks authored by the current user from the quick controls', async () => {
    await renderJournal([
      createTask(),
      createTask({
        id: 102,
        title: 'Поставленная мной задача',
        assigneeUserId: 8,
        assigneeName: 'Другой исполнитель',
        authorUserId: 7,
        authorName: 'Администратор',
        participants: [
          { userId: 8, role: 'assignee', displayName: 'Другой исполнитель', approvalStatus: 'pending' },
        ],
      }),
    ]);

    fireEvent.click(screen.getByTestId('task-quick-filter-authoredByMe'));

    expect(await screen.findByText('Поставленная мной задача')).toBeTruthy();
    await waitFor(() => expect(screen.queryByText('Тестовая задача')).toBeNull());
  });

  it('filters tasks where the current user observes, approves, or controls work', async () => {
    await renderJournal([
      createTask({
        id: 201,
        title: 'Задача наблюдателя',
        assigneeUserId: 8,
        participants: [{ userId: 7, role: 'observer', displayName: 'Администратор', approvalStatus: 'pending' }],
      }),
      createTask({
        id: 202,
        title: 'Задача согласующего',
        assigneeUserId: 8,
        participants: [{ userId: 7, role: 'approver', displayName: 'Администратор', approvalStatus: 'pending' }],
      }),
      createTask({
        id: 203,
        title: 'Задача на контроле',
        assigneeUserId: 8,
        isUnderControl: true,
      }),
    ]);

    const observerButton = screen.getByTestId('task-quick-filter-observer');
    expect(observerButton.getAttribute('title')).toContain('не принимаете решение');
    fireEvent.click(observerButton);
    expect(await screen.findByText('Задача наблюдателя')).toBeTruthy();
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenLastCalledWith(
      expect.objectContaining({ participantRole: 'observer' }),
      expect.anything(),
    ));

    fireEvent.click(screen.getByTestId('task-quick-filter-approver'));
    expect(await screen.findByText('Задача согласующего')).toBeTruthy();
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenLastCalledWith(
      expect.objectContaining({ participantRole: 'approver' }),
      expect.anything(),
    ));

    fireEvent.click(screen.getByTestId('task-quick-filter-underControl'));
    expect(await screen.findByText('Задача на контроле')).toBeTruthy();
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenLastCalledWith(
      expect.objectContaining({ underControl: true }),
      expect.anything(),
    ));
  });

  it('explains observer and approver roles without requiring hover', async () => {
    await renderJournal([createTask()]);

    const guide = screen.getByTestId('task-role-guide');
    fireEvent.click(within(guide).getByText('Как работают наблюдатель и согласующий?'));

    expect(guide.hasAttribute('open')).toBe(true);
    expect(within(guide).getByText(/не даёт права редактировать или завершать задачу/)).toBeTruthy();
    expect(within(guide).getByText(/когда наступает его очередь/)).toBeTruthy();
    expect(within(guide).getByText(/раздел «Согласования» — те, где решение требуется сейчас/)).toBeTruthy();
    expect(within(guide).getByText(/каналов задачи и ваших личных настроек/)).toBeTruthy();
  });

  it('uses metric cards as task filters', async () => {
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
      quickFilter: 'all',
      periodMode: 'all',
    }));

    const overdue = new Date();
    overdue.setDate(overdue.getDate() - 1);
    overdue.setHours(10, 0, 0, 0);

    const future = new Date();
    future.setDate(future.getDate() + 1);
    future.setHours(23, 30, 0, 0);

    await renderJournal([
      createTask({ title: 'Overdue task', dueAt: overdue.toISOString() }),
      createTask({
        id: 102,
        title: 'Future task',
        dueAt: future.toISOString(),
        assigneeUserId: 7,
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
        ],
      }),
    ], { waitForTitle: 'Future task' });

    expect(screen.getByText('Future task')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Просрочены/i }));

    expect(screen.getByText('Overdue task')).toBeTruthy();
    await waitFor(() => expect(screen.queryByText('Future task')).toBeNull());
  });

  it('recalculates metric counters when the status filter changes', async () => {
    await renderJournal([
      createTask({ id: 101, title: 'Открытая задача', status: 'new', dueAt: todayAt(11) }),
      createTask({
        id: 102,
        title: 'Закрытая задача',
        status: 'done',
        result: 'Готово',
        dueAt: todayAt(12),
      }),
    ]);

    await waitFor(() => {
      expect(within(screen.getByTestId('task-metric-Активные')).getByText('1')).toBeTruthy();
    });

    fireEvent.change(screen.getByLabelText('Статус'), { target: { value: 'done' } });

    await waitFor(() => {
      expect(within(screen.getByTestId('task-metric-Активные')).getByText('0')).toBeTruthy();
    });
    expect(screen.getByText('Закрытая задача')).toBeTruthy();
    expect(screen.queryByText('Открытая задача')).toBeNull();
  });

  it('shows overdue note under the current task status', async () => {
    const overdue = new Date();
    overdue.setDate(overdue.getDate() - 1);
    overdue.setHours(10, 0, 0, 0);

    await renderJournal([
      createTask({
        id: 101,
        title: 'Ожидающая просроченная задача',
        status: 'waiting',
        dueAt: overdue.toISOString(),
      }),
    ], { waitForTitle: null });

    fireEvent.click(screen.getByTestId('task-period-all'));

    const row = (await screen.findByText('Ожидающая просроченная задача')).closest('tr');
    expect(row).not.toBeNull();
    expect(within(row as HTMLTableRowElement).getByText('Ожидает', { selector: ':not(option)' })).toBeTruthy();
    expect(within(row as HTMLTableRowElement).getByText('Просрочена')).toBeTruthy();
  });
});
