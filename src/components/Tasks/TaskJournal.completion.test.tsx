import { setupTaskJournalTests, apiMock, toastMock, authState, permissionsState, todayAt, createTask, renderJournal, openFirstTaskForEdit } from './TaskJournal.test-support';
import { TaskJournal } from './TaskJournal';
import { act, cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { type TaskJournalItem } from '../../utils/taskJournalStore';

describe('TaskJournal', () => {
  setupTaskJournalTests();


  it('asks for task result before quick completion sends a task to approval', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    await renderJournal([
      createTask({
        assigneeUserId: 7,
        assigneeName: 'Исполнитель',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Исполнитель', approvalStatus: 'pending' },
          { userId: 8, role: 'approver', displayName: 'Согласующий', approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { executorCompletionAllowedUserIds: [7] } });

    fireEvent.click(screen.getAllByTitle('Завершить')[0]);

    expect(await screen.findByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();
    expect(screen.getByText('Отправка на согласование')).toBeTruthy();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();

    fireEvent.click(screen.getAllByRole('button', { name: 'Отправить на согласование' }).at(-1)!);
    expect(screen.getByText('Добавьте результат по задаче перед отправкой на согласование.')).toBeTruthy();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();

    fireEvent.change(screen.getByPlaceholderText('Опишите подробно, что было сделано и какой результат был достигнут.'), {
      target: { value: 'Работа выполнена, результат готов к проверке.' },
    });
    fireEvent.click(screen.getAllByRole('button', { name: 'Отправить на согласование' }).at(-1)!);

    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(
      101,
      'onApproval',
      'Работа выполнена, результат готов к проверке.',
    ));
    expect(toastMock.success).toHaveBeenCalledWith('Задача отправлена на согласование.', 'Журнал задач');
    expect(toastMock.error).not.toHaveBeenCalledWith(
      'Задачу нельзя завершить, пока все согласующие не согласовали её.',
      'Журнал задач',
    );
  });

  it('sends an edited task to approval when approvers are configured', async () => {
    await renderJournal([
      createTask({
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
          { userId: 8, role: 'approver', displayName: 'Согласующий', approvalStatus: 'pending' },
        ],
      }),
    ], {
      settings: { approvalDueDays: 9 },
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Согласующий', email: 'approver@example.com' },
      ],
    });

    await openFirstTaskForEdit();

    fireEvent.click(screen.getAllByRole('button', { name: 'Отправить на согласование' }).at(-1)!);

    expect(await screen.findByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();

    fireEvent.click(screen.getAllByRole('button', { name: 'Отправить на согласование' }).at(-1)!);
    expect(screen.getByText('Добавьте результат по задаче перед отправкой на согласование.')).toBeTruthy();
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();

    fireEvent.change(screen.getByPlaceholderText('Опишите подробно, что было сделано и какой результат был достигнут.'), {
      target: { value: 'Документы подготовлены, проверены и приложены к задаче.' },
    });
    fireEvent.click(screen.getAllByRole('button', { name: 'Отправить на согласование' }).at(-1)!);

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    const payload = apiMock.updateTaskJournalItem.mock.calls[0][1];
    const approvalDueAt = new Date(payload.approvalDueAt).getTime();
    expect(approvalDueAt).toBeGreaterThan(Date.now() + 8 * 24 * 60 * 60 * 1000);
    expect(approvalDueAt).toBeLessThan(Date.now() + 10 * 24 * 60 * 60 * 1000);
    expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      status: 'onApproval',
      result: 'Документы подготовлены, проверены и приложены к задаче.',
      approvalDueAt: expect.any(String),
      participants: expect.arrayContaining([
        expect.objectContaining({ role: 'approver', userId: 8, approvalStatus: 'pending' }),
      ]),
    }));
  });

  it.each(['legacy', 'modern'] as const)('allows a fully editable task without approvers to enter approval in the %s card', async (taskCardWindowMode) => {
    await renderJournal([
      createTask({
        status: 'inProgress',
        result: '',
        authorUserId: 8,
        authorName: 'Постановщик',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
        ],
      }),
    ], {
      settings: {
        approvalDueDays: 5,
        taskCardWindowMode,
      },
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Постановщик', email: 'author@example.com' },
      ],
    });

    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const sendButtons = within(card).getAllByRole('button', { name: 'Отправить на согласование' });
    expect(sendButtons.length).toBeGreaterThan(0);
    fireEvent.click(sendButtons[0]);

    expect(await screen.findByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();
    fireEvent.change(screen.getByPlaceholderText('Опишите подробно, что было сделано и какой результат был достигнут.'), {
      target: { value: 'Результат отправлен постановщику.' },
    });
    fireEvent.click(screen.getAllByRole('button', { name: 'Отправить на согласование' }).at(-1)!);

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      status: 'onApproval',
      result: 'Результат отправлен постановщику.',
      approvalDueAt: expect.any(String),
      participants: expect.not.arrayContaining([
        expect.objectContaining({ role: 'approver' }),
      ]),
    }));
  });

  it.each(['legacy', 'modern'] as const)('keeps the approval action visible beside editing in the %s view card', async (taskCardWindowMode) => {
    await renderJournal([
      createTask({
        status: 'inProgress',
        authorUserId: 8,
        authorName: 'Постановщик',
        assigneeUserId: 7,
        assigneeName: 'Администратор',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { taskCardWindowMode } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);

    expect(within(card).getAllByRole('button', { name: 'Редактировать задачу' }).length).toBeGreaterThan(0);
    expect(within(card).getAllByRole('button', { name: 'Отправить на согласование' }).length).toBeGreaterThan(0);
  });

  it('asks for a result before sending a task to approval in the modern task card', async () => {
    await renderJournal([
      createTask({
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
          { userId: 8, role: 'approver', displayName: 'Согласующий', approvalStatus: 'pending' },
        ],
      }),
    ], {
      settings: { taskCardWindowMode: 'modern' },
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Согласующий', email: 'approver@example.com' },
      ],
    });

    await openFirstTaskForEdit();
    const modernCard = await screen.findByTestId('task-modern-card-window');

    fireEvent.click(within(modernCard).getAllByRole('button', { name: 'Отправить на согласование' }).at(-1)!);

    expect(await screen.findByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();

    fireEvent.click(screen.getAllByRole('button', { name: 'Отправить на согласование' }).at(-1)!);
    expect(screen.getByText('Добавьте результат по задаче перед отправкой на согласование.')).toBeTruthy();
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();

    fireEvent.change(screen.getByPlaceholderText('Опишите подробно, что было сделано и какой результат был достигнут.'), {
      target: { value: 'В новом окне результат заполнен перед согласованием.' },
    });
    fireEvent.click(screen.getAllByRole('button', { name: 'Отправить на согласование' }).at(-1)!);

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      status: 'onApproval',
      result: 'В новом окне результат заполнен перед согласованием.',
      approvalDueAt: expect.any(String),
      participants: expect.arrayContaining([
        expect.objectContaining({ role: 'approver', userId: 8, approvalStatus: 'pending' }),
      ]),
    }));
  });

  it.each([
    ['legacy', true], ['legacy', false], ['modern', true], ['modern', false],
  ] as const)('corrects a completed result from the %s card with retry and reopening (admin: %s)', async (taskCardWindowMode, admin) => {
    authState.isAdmin = admin;
    const tasks = [createTask({ status: 'done', result: 'Исходный результат с ошибкой', authorUserId: 7, assigneeUserId: 8 })];
    apiMock.getTaskJournalItem.mockImplementation(async () => tasks[0]);
    apiMock.updateTaskJournalStatus.mockImplementation(async (_id, status, result) => {
      tasks[0] = { ...tasks[0], status, result };
      return tasks[0];
    });
    apiMock.updateTaskJournalStatus.mockRejectedValueOnce(new Error('Временный отказ сохранения'));
    await renderJournal(tasks, { settings: { taskCardWindowMode }, waitForTitle: null });
    fireEvent.change(screen.getByLabelText('Статус'), { target: { value: 'all' } });
    await screen.findAllByText('Тестовая задача');
    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    let card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const input = within(card).getByTestId('task-result-field') as HTMLTextAreaElement;
    fireEvent.change(input, { target: { value: 'Исправленный результат' } });
    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить результат' }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith('Временный отказ сохранения', 'Журнал задач'));
    expect(input.value).toBe('Исправленный результат');
    expect(tasks[0].result).toBe('Исходный результат с ошибкой');
    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить результат' }));
    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledTimes(2));
    expect(apiMock.updateTaskJournalStatus).toHaveBeenLastCalledWith(101, 'done', 'Исправленный результат');
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
    card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    expect((within(card).getByTestId('task-result-field') as HTMLTextAreaElement).value).toBe('Исправленный результат');
    fireEvent.click(within(card).getByRole('button', { name: 'Отмена' }));
    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    expect((within(card).getByTestId('task-result-field') as HTMLTextAreaElement).value).toBe('Исправленный результат');
    apiMock.updateTaskJournalItem.mockImplementation(async (_id, payload) => {
      tasks[0] = { ...tasks[0], result: payload.result };
      return tasks[0];
    });
    fireEvent.click(within(card).getAllByRole('button', { name: 'Редактировать задачу' })[0]);
    await screen.findByRole('heading', { name: 'Редактировать задачу' });
    fireEvent.change(screen.getByTestId('task-result-field'), { target: { value: 'Результат из полного редактора' } });
    fireEvent.click(screen.getAllByRole('button', { name: 'Сохранить' }).at(-1)!);
    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101,
      expect.objectContaining({ result: 'Результат из полного редактора', status: 'done' })));
  });

  it.each(['legacy', 'modern'] as const)('validates a corrected result and prevents concurrent saving in the %s card', async (taskCardWindowMode) => {
    const task = createTask({ status: 'done', result: 'Исходный результат' });
    apiMock.getTaskJournalItem.mockResolvedValue(task);
    let complete!: (task: TaskJournalItem) => void;
    const pending = new Promise<TaskJournalItem>((resolve) => { complete = resolve; });
    apiMock.updateTaskJournalStatus.mockReturnValue(pending);
    await renderJournal([task], { settings: { taskCardWindowMode }, waitForTitle: null });
    fireEvent.change(screen.getByLabelText('Статус'), { target: { value: 'all' } });
    await screen.findAllByText('Тестовая задача');
    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const input = within(card).getByTestId('task-result-field') as HTMLTextAreaElement;
    fireEvent.change(input, { target: { value: '   ' } });
    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить результат' }));
    expect(within(card).getByText('Добавьте результат по задаче перед завершением.')).toBeTruthy();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();
    fireEvent.change(input, { target: { value: 'Исправленный результат' } });
    const save = within(card).getByRole('button', { name: 'Сохранить результат' });
    fireEvent.click(save);
    fireEvent.click(save);
    expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledTimes(1);
    expect(input.disabled).toBe(true);
    await act(async () => { complete({ ...task, result: 'Исправленный результат' }); await pending; });
    expect(input.disabled).toBe(false);
  });

  it.each(['legacy', 'modern'] as const)('protects a completed result without edit permission in the %s card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    permissionsState.taskCanEdit = false;
    permissionsState.taskCanEditOthers = false;
    permissionsState.taskCanCompleteOthers = false;
    const task = createTask({ status: 'done', result: 'Защищённый результат', authorUserId: 8, assigneeUserId: 9 });
    apiMock.getTaskJournalItem.mockResolvedValue(task);
    await renderJournal([task], { settings: { taskCardWindowMode }, waitForTitle: null });
    fireEvent.change(screen.getByLabelText('Статус'), { target: { value: 'all' } });
    await screen.findAllByText('Тестовая задача');
    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    expect((within(card).getByTestId('task-result-field') as HTMLTextAreaElement).disabled).toBe(true);
    expect(within(card).getByText('Нет прав на изменение')).toBeTruthy();
    expect(within(card).queryByRole('button', { name: 'Сохранить результат' })).toBeNull();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();
  });

  it('requires a result when completing an approved task from the editor', async () => {
    await renderJournal([
      createTask({
        status: 'approved',
        result: '',
        assigneeUserId: 8,
        assigneeName: 'Юрист',
        participants: [
          { userId: 8, role: 'assignee', displayName: 'Юрист', approvalStatus: 'pending' },
          { userId: 7, role: 'approver', displayName: 'Администратор', approvalStatus: 'approved' },
        ],
      }),
    ], {
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 8, name: 'Юрист', email: 'lawyer@example.com' },
      ],
      waitForTitle: null,
    });
    fireEvent.click(screen.getByTestId('task-quick-filter-all'));
    fireEvent.change(screen.getByLabelText('Статус'), { target: { value: 'all' } });
    await screen.findAllByText('Тестовая задача');

    await openFirstTaskForEdit();

    const completeButtons = screen.getAllByRole('button', { name: 'Завершить' });
    fireEvent.click(completeButtons[completeButtons.length - 1]);

    expect(screen.getByText('Добавьте результат по задаче перед завершением.')).toBeTruthy();
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();

    fireEvent.change(screen.getByPlaceholderText('Опишите подробно, что было сделано и какой результат был достигнут.'), {
      target: { value: 'Согласованная задача выполнена, результат передан клиенту.' },
    });
    fireEvent.click(completeButtons[completeButtons.length - 1]);

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      status: 'done',
      result: 'Согласованная задача выполнена, результат передан клиенту.',
    }));
  });

  it('requires a result when a task is saved as done manually', async () => {
    await renderJournal([createTask({ status: 'approved', result: '' })], { waitForTitle: null });
    fireEvent.click(screen.getByTestId('task-quick-filter-all'));
    fireEvent.change(screen.getByLabelText('Статус'), { target: { value: 'all' } });
    await screen.findAllByText('Тестовая задача');

    await openFirstTaskForEdit();

    fireEvent.change(screen.getByTestId('task-editor-status-select'), { target: { value: 'done' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    expect(screen.getByText('Добавьте результат по задаче перед завершением.')).toBeTruthy();
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();

    fireEvent.change(screen.getByPlaceholderText('Опишите подробно, что было сделано и какой результат был достигнут.'), {
      target: { value: 'Задача закрыта после проверки документов.' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      status: 'done',
      result: 'Задача закрыта после проверки документов.',
    }));
  });

  it('opens executor task in view mode and saves result only after completion starts', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    await renderJournal([
      createTask({
        assigneeUserId: 7,
        assigneeName: 'Исполнитель',
        authorUserId: 8,
        authorName: 'Постановщик',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Исполнитель', approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { executorCompletionAllowedUserIds: [7] } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    expect(await screen.findByRole('heading', { name: 'Просмотр задачи' })).toBeTruthy();

    const titleInput = screen.getByDisplayValue('Тестовая задача') as HTMLInputElement;
    expect(titleInput.disabled).toBe(true);

    const resultInput = screen.getByTestId('task-result-field') as HTMLTextAreaElement;
    expect(resultInput.disabled).toBe(true);
    fireEvent.click(screen.getAllByRole('button', { name: 'Завершить' }).at(-1)!);
    expect(await screen.findByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();
    fireEvent.change(document.getElementById('task-completion-result')!, { target: { value: 'Результат исполнителя' } });
    fireEvent.click(screen.getByRole('button', { name: 'Завершить задачу' }));

    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(101, 'done', 'Результат исполнителя'));
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
  });

  it.each(['legacy', 'modern'] as const)('allows executor to send a task without approvers to its author in the %s card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    await renderJournal([
      createTask({
        status: 'inProgress',
        assigneeUserId: 7,
        assigneeName: 'Исполнитель',
        authorUserId: 8,
        authorName: 'Постановщик',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Исполнитель', approvalStatus: 'pending' },
        ],
      }),
    ], {
      settings: {
        executorCompletionAllowedUserIds: [],
        taskCardWindowMode,
      },
    });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const sendButtons = within(card).getAllByRole('button', { name: 'Отправить на согласование' });
    expect(sendButtons.length).toBeGreaterThan(0);
    fireEvent.click(sendButtons[0]);

    expect(await screen.findByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();
    expect(screen.getByText('Отправка на согласование')).toBeTruthy();
    fireEvent.change(document.getElementById('task-completion-result')!, {
      target: { value: 'Заявление подготовлено и приложено к задаче.' },
    });
    fireEvent.click(screen.getAllByRole('button', { name: 'Отправить на согласование' }).at(-1)!);

    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(
      101,
      'onApproval',
      'Заявление подготовлено и приложено к задаче.',
    ));
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
  });

  it.each(['legacy', 'modern'] as const)('allows executor to resubmit a returned task in the %s card without general completion access', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    await renderJournal([
      createTask({
        status: 'inProgress',
        assigneeUserId: 7,
        assigneeName: 'Исполнитель',
        authorUserId: 9,
        authorName: 'Постановщик',
        result: '',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Исполнитель', approvalStatus: 'pending' },
          {
            userId: 8,
            role: 'approver',
            displayName: 'Согласующий',
            approvalStatus: 'revisionRequested',
            resolution: 'Исправьте результат задачи.',
          },
        ],
      }),
    ], {
      settings: {
        taskCardWindowMode,
        executorCompletionAllowedUserIds: [],
      },
    });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    expect(within(card).queryByRole('button', { name: 'Завершить' })).toBeNull();
    if (taskCardWindowMode === 'modern') {
      expect(within(card).queryByRole('button', { name: 'Запустить' })).toBeNull();
    }

    const submitButtons = within(card).getAllByRole('button', { name: 'Отправить на согласование' });
    fireEvent.click(submitButtons[submitButtons.length - 1]);

    expect(await screen.findByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();
    fireEvent.change(document.getElementById('task-completion-result')!, {
      target: { value: 'Замечания устранены, исправленный результат приложен.' },
    });
    fireEvent.click(screen.getAllByRole('button', { name: 'Отправить на согласование' }).at(-1)!);

    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(
      101,
      'onApproval',
      'Замечания устранены, исправленный результат приложен.',
    ));
    expect(toastMock.success).toHaveBeenCalledWith('Задача отправлена на согласование.', 'Журнал задач');
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
  });

  it.each(['legacy', 'modern'] as const)('does not offer resubmission of an already approved task in the %s card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    await renderJournal([
      createTask({
        status: 'approved',
        result: 'Результат согласован.',
        assigneeUserId: 7,
        assigneeName: 'Исполнитель',
        authorUserId: 9,
        authorName: 'Постановщик',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Исполнитель', approvalStatus: 'pending' },
          { userId: 8, role: 'approver', displayName: 'Согласующий', approvalStatus: 'approved' },
        ],
      }),
    ], {
      settings: { taskCardWindowMode, executorCompletionAllowedUserIds: [] },
    });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    expect(within(card).queryByRole('button', { name: 'Отправить на согласование' })).toBeNull();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();
  });

  it('opens observed own-scope task in view mode even when role can edit tasks', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Observer', email: 'observer@example.com' };
    permissionsState.taskCanEdit = true;
    permissionsState.taskCanEditOthers = false;
    permissionsState.taskViewScope = 'own';
    window.localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({ quickFilter: 'all', periodMode: 'all' }));
    await renderJournal([
      createTask({
        assigneeUserId: 9,
        assigneeName: 'Assignee',
        authorUserId: 8,
        authorName: 'Author',
        participants: [
          { userId: 7, role: 'observer', displayName: 'Observer', approvalStatus: 'pending' },
        ],
      }),
    ]);

    fireEvent.click(screen.getByTitle('Просмотр'));
    expect(await screen.findByRole('heading', { name: 'Просмотр задачи' })).toBeTruthy();
    expect((screen.getByDisplayValue('Тестовая задача') as HTMLInputElement).disabled).toBe(true);
    expect(screen.queryByPlaceholderText('Введите комментарий')).toBeNull();
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
  });

  it('allows executor to comment on a read-only task', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    await renderJournal([
      createTask({
        assigneeUserId: 7,
        assigneeName: 'Исполнитель',
        authorUserId: 8,
        authorName: 'Постановщик',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Исполнитель', approvalStatus: 'pending' },
        ],
      }),
    ]);

    fireEvent.click(screen.getByTitle('Просмотр'));
    expect(await screen.findByRole('heading', { name: 'Просмотр задачи' })).toBeTruthy();

    fireEvent.change(screen.getByPlaceholderText('Введите комментарий'), { target: { value: 'Комментарий исполнителя' } });
    fireEvent.click(screen.getByRole('checkbox', { name: 'Показать комментарий во внешнем календаре' }));
    fireEvent.click(screen.getByRole('button', { name: /Добавить комментарий/i }));

    await waitFor(() => expect(apiMock.createTaskJournalComment).toHaveBeenCalledWith(101, 'Комментарий исполнителя', true));
  });

  it.each([
    { taskCardWindowMode: 'legacy' as const, lostAcknowledgement: false },
    { taskCardWindowMode: 'modern' as const, lostAcknowledgement: false },
    { taskCardWindowMode: 'legacy' as const, lostAcknowledgement: true },
    { taskCardWindowMode: 'modern' as const, lostAcknowledgement: true },
  ])('keeps a confirmed attachment after a late initial file list in the $taskCardWindowMode card (lost acknowledgement: $lostAcknowledgement)', async ({ taskCardWindowMode, lostAcknowledgement }) => {
    let finishInitialRead!: (files: unknown[]) => void;
    apiMock.getTaskJournalFiles.mockImplementationOnce(() => new Promise<unknown[]>((resolve) => { finishInitialRead = resolve; }));
    const saved = { id: 601, taskJournalItemId: 101, documentKey: 'doc-1', version: 1,
      originalFileName: 'document.pdf', contentType: 'application/pdf', sizeBytes: 20,
      uploadedByUserId: 7, uploadedByName: 'Исполнитель', uploadedAt: todayAt(12), isLatestVersion: true };
    const earlier = { ...saved, id: 600, documentKey: 'earlier-document', originalFileName: 'earlier.pdf' };
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    await renderJournal([createTask({
      status: 'inProgress', assigneeUserId: 7, authorUserId: 8,
      participants: [{ userId: 7, role: 'assignee', displayName: 'Исполнитель', approvalStatus: 'pending' }],
    })], { settings: { taskCardWindowMode, executorCompletionAllowedUserIds: [] } });
    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    await waitFor(() => expect(apiMock.getTaskJournalFiles).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(apiMock.getTaskFileUploadPage).toHaveBeenCalledTimes(1));
    apiMock.getTaskJournalFiles.mockResolvedValue([earlier, saved]);
    if (lostAcknowledgement) {
      apiMock.uploadTaskJournalFile.mockRejectedValueOnce(new Error('HTTP 503 Service Unavailable'));
      apiMock.getTaskFileUploadKey.mockReturnValue('lost-response-key');
      apiMock.getTaskFileUploadStates.mockResolvedValue([{ uploadKey: 'lost-response-key', fileName: 'document.pdf',
        status: 'uploaded', fileId: 601, message: 'Файл добавлен.' }]);
    } else apiMock.uploadTaskJournalFile.mockResolvedValueOnce(saved);
    const attach = within(card).getAllByRole('button', { name: /^(Прикрепить файл|Добавить файл)$/ })
      .find((button) => !(button as HTMLButtonElement).disabled);
    expect(attach).toBeTruthy();
    fireEvent.click(attach!);
    const file = new File(['Confirmed attachment'], 'document.pdf', { type: 'application/pdf' });
    fireEvent.change(card.querySelector('input[type="file"]') as HTMLInputElement, { target: { files: [file] } });
    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith('Файл добавлен к задаче.', 'Журнал задач'));
    await act(async () => { finishInitialRead([earlier]); });
    if (taskCardWindowMode === 'modern') fireEvent.click(within(card).getByRole('tab', { name: /^Файлы/ }));
    await waitFor(() => expect(within(card).getAllByRole('button', { name: 'Скачать файл' })).toHaveLength(2));
    const articles = within(card).getAllByRole('button', { name: 'Скачать файл' }).map((button) => button.closest('article')?.textContent);
    expect(articles.some((text) => text?.includes('document.pdf'))).toBe(true);
    expect(articles.some((text) => text?.includes('earlier.pdf'))).toBe(true);
    expect(apiMock.getTaskJournalFiles).toHaveBeenCalledTimes(lostAcknowledgement ? 3 : 2);
    if (lostAcknowledgement) expect(apiMock.getTaskFileUploadStates).toHaveBeenCalledWith(101, undefined, { uploadKeys: ['lost-response-key'] });
    expect(apiMock.uploadTaskJournalFile).toHaveBeenCalledTimes(1);
    expect(toastMock.error).not.toHaveBeenCalled();
  });

  it.each([
    { taskCardWindowMode: 'legacy' as const, change: 'task' },
    { taskCardWindowMode: 'modern' as const, change: 'task' },
    { taskCardWindowMode: 'legacy' as const, change: 'user' },
    { taskCardWindowMode: 'modern' as const, change: 'user' },
  ])('isolates pending file upload after a $change change in the $taskCardWindowMode card', async ({ taskCardWindowMode, change }) => {
    const first = createTask({ id: 101, title: 'Первая задача' });
    const second = createTask({ id: 102, title: 'Вторая задача' });
    const file = { id: 600, taskJournalItemId: 101, documentKey: 'old-scope', version: 1,
      originalFileName: 'old-scope.pdf', contentType: 'application/pdf', sizeBytes: 9,
      uploadedByUserId: 7, uploadedByName: 'Исполнитель', uploadedAt: todayAt(12), isLatestVersion: true };
    apiMock.getTaskJournalFiles.mockImplementation(async (id: number) => [id === 101 && authState.user.id === 7 ? file
      : { ...file, id: 602, taskJournalItemId: id, documentKey: 'new-scope', originalFileName: 'new-scope.pdf' }]);
    let finishUpload!: (value: typeof file) => void;
    apiMock.uploadTaskJournalFile.mockImplementationOnce(() => new Promise<typeof file>((resolve) => { finishUpload = resolve; }));
    const view = await renderJournal([first, second], { settings: { taskCardWindowMode } });
    view.rerender(<TaskJournal routeTaskId={101} />);
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    if (taskCardWindowMode === 'modern') fireEvent.click(within(card).getByRole('tab', { name: /^Файлы/ }));
    await within(card).findByRole('button', { name: 'Скачать файл' });
    fireEvent.click(within(card).getByRole('button', { name: 'Добавить файл' }));
    fireEvent.change(card.querySelector('input[type="file"]') as HTMLInputElement,
      { target: { files: [new File(['old upload'], 'old-upload.pdf', { type: 'application/pdf' })] } });
    await waitFor(() => expect(apiMock.uploadTaskJournalFile).toHaveBeenCalledTimes(1));
    if (change === 'user') authState.user = { id: 8, fullName: 'Другой сотрудник', email: 'other@example.com' };
    view.rerender(<TaskJournal routeTaskId={change === 'task' ? 102 : 101} />);
    const next = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    if (taskCardWindowMode === 'modern') fireEvent.click(within(next).getByRole('tab', { name: /^Файлы/ }));
    await waitFor(() => expect(within(next).getByRole('button', { name: 'Скачать файл' }).closest('article')?.textContent).toContain('new-scope.pdf'));
    const attach = within(next).getByRole('button', { name: /^(Добавить файл|Загружаем\.\.\.)$/ }) as HTMLButtonElement;
    const enabledBeforeOldReply = !attach.disabled;
    await act(async () => { finishUpload({ ...file, id: 601, originalFileName: 'old-upload.pdf' }); });
    expect(enabledBeforeOldReply).toBe(true);
    expect((within(next).getByRole('button', { name: 'Добавить файл' }) as HTMLButtonElement).disabled).toBe(false);
    expect(within(next).queryByText('old-upload.pdf')).toBeNull();
    expect(within(next).getByRole('button', { name: 'Скачать файл' }).closest('article')?.textContent).toContain('new-scope.pdf');
    expect(apiMock.uploadTaskJournalFile).toHaveBeenCalledTimes(1);
  });

  it.each(['legacy', 'modern'] as const)('allows executor to upload a file in the %s read-only task card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    await renderJournal([
      createTask({
        status: 'inProgress',
        assigneeUserId: 7,
        assigneeName: 'Исполнитель',
        authorUserId: 8,
        authorName: 'Постановщик',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Исполнитель', approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { taskCardWindowMode, executorCompletionAllowedUserIds: [] } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const fileInput = card.querySelector('input[type="file"]') as HTMLInputElement;
    const file = new File(['Рабочий документ'], 'документ.pdf', { type: 'application/pdf' });
    fireEvent.change(fileInput, { target: { files: [file] } });

    await waitFor(() => expect(apiMock.uploadTaskJournalFile).toHaveBeenCalledWith(101, file, undefined));
    expect(within(card).queryByRole('button', { name: 'Удалить файл' })).toBeNull();
  });

  it('allows executor to upload a new file version in the modern read-only task card', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    apiMock.getTaskJournalFiles.mockResolvedValueOnce([{
      id: 600,
      taskJournalItemId: 101,
      documentKey: 'doc-1',
      version: 1,
      originalFileName: 'document.pdf',
      contentType: 'application/pdf',
      sizeBytes: 1024,
      uploadedByUserId: 8,
      uploadedByName: 'Постановщик',
      uploadedAt: todayAt(10),
      isLatestVersion: true,
      clientCaseDocumentId: null,
    }]);
    await renderJournal([
      createTask({
        status: 'inProgress',
        assigneeUserId: 7,
        authorUserId: 8,
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Исполнитель', approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { taskCardWindowMode: 'modern', executorCompletionAllowedUserIds: [] } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId('task-modern-card-window');
    fireEvent.click(within(card).getByRole('tab', { name: 'Файлы 1' }));
    const versionButton = await within(card).findByRole('button', { name: 'Загрузить новую версию document.pdf' });
    const versionInput = versionButton.previousElementSibling as HTMLInputElement;
    const file = new File(['Исправленная версия'], 'document-v2.pdf', { type: 'application/pdf' });
    fireEvent.change(versionInput, { target: { files: [file] } });

    await waitFor(() => expect(apiMock.uploadTaskJournalFile).toHaveBeenCalledWith(101, file, 'doc-1'));
  });

  it.each(['legacy', 'modern'] as const)('explains external calendar comment visibility in the %s task card', async (taskCardWindowMode) => {
    await renderJournal([createTask()], { settings: { taskCardWindowMode } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);

    expect(within(card).getByRole('checkbox', { name: 'Показать комментарий во внешнем календаре' })).toBeTruthy();
    expect(within(card).getByText(
      'Если календарь подключён, этот комментарий появится в описании события Google или Яндекс Календаря. Без отметки он останется только в CRM.',
    )).toBeTruthy();
  });

  it.each(['legacy', 'modern'] as const)('opens the current task chat in the %s task card', async (taskCardWindowMode) => {
    await renderJournal([createTask({ id: 101 })], { settings: { taskCardWindowMode } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    if (taskCardWindowMode === 'modern') {
      fireEvent.click(within(card).getByRole('tab', { name: 'Чат' }));
    } else {
      fireEvent.click(within(card).getByRole('button', { name: /Чат задачи/ }));
    }

    expect(within(card).getByTestId('task-chat-panel').textContent).toBe('Чат задачи 101');
  });

  it.each(['legacy', 'modern'] as const)('explains hearing document readiness in the %s task card', async (taskCardWindowMode) => {
    await renderJournal([createTask({
      type: 'courtHearing',
      title: 'Подготовиться к заседанию',
      tags: ['case-deadline', 'case-deadline:court-event-12'],
    })], { settings: { taskCardWindowMode } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    expect(within(card).getByTestId('court-preparation-hint').textContent).toContain('Проверьте документы к заседанию');
  });

  it.each([
    ['legacy', 'onApproval'], ['modern', 'onApproval'], ['legacy', 'done'], ['modern', 'done'],
  ] as const)('retains the executor result and does not retransmit pending files in the %s card for %s', async (taskCardWindowMode, targetStatus) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    await renderJournal([createTask({ assigneeUserId: 7, assigneeName: 'Исполнитель', authorUserId: 8, authorName: 'Постановщик', result: '',
      participants: [{ userId: 7, role: 'assignee', displayName: 'Исполнитель', approvalStatus: 'pending' }] })],
      { settings: { taskCardWindowMode, executorCompletionAllowedUserIds: targetStatus === 'done' ? [7] : [] } });
    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const actionName = targetStatus === 'done' ? 'Завершить задачу' : 'Отправить на согласование';
    fireEvent.click(within(card).getAllByRole('button', { name: targetStatus === 'done' ? 'Завершить' : actionName }).at(-1)!);
    await screen.findByRole('heading', { name: 'Добавить результат по задаче' });
    apiMock.uploadTaskJournalFile.mockRejectedValueOnce(new Error('HTTP 503 Service Unavailable'));
    apiMock.getTaskFileUploadKey.mockReturnValue('pending-key');
    apiMock.getTaskFileUploadStates.mockResolvedValue([{ uploadKey: 'pending-key', fileName: 'результат.pdf',
      status: 'waiting', fileId: null, message: 'Ожидание прикрепления файла.' }]);
    const file = new File(['Итоговый документ'], 'результат.pdf', { type: 'application/pdf' });
    fireEvent.change(screen.getByTestId('task-completion-file-input'), { target: { files: [file] } });
    fireEvent.change(document.getElementById('task-completion-result')!, { target: { value: 'Работа выполнена.' } });
    fireEvent.click(screen.getAllByRole('button', { name: actionName }).at(-1)!);
    expect(await screen.findByText(/Задача пока не завершена и не отправлена/)).toBeTruthy();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
    expect(await within(card).findByRole('region', { name: 'Состояние ваших загрузок' })).toBeTruthy();
    apiMock.getTaskFileUploadStates.mockRejectedValue(new Error('Не удалось проверить состояние файла.'));
    fireEvent.click(screen.getAllByRole('button', { name: actionName }).at(-1)!);
    expect(await screen.findByText('Не удалось проверить состояние файла.')).toBeTruthy();
    expect((document.getElementById('task-completion-result') as HTMLTextAreaElement).value).toBe('Работа выполнена.');
    expect(screen.getByTitle('результат.pdf')).toBeTruthy();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
    expect(apiMock.uploadTaskJournalFile).toHaveBeenCalledTimes(1);
    apiMock.getTaskFileUploadStates.mockResolvedValue([{ uploadKey: 'pending-key', status: 'uploaded', fileId: 601 }]);
    apiMock.getTaskJournalFiles.mockResolvedValue([{ id: 601, taskJournalItemId: 101, documentKey: 'doc-1', version: 1,
      originalFileName: 'результат.pdf', contentType: 'application/pdf', sizeBytes: 9, uploadedByName: 'Исполнитель', uploadedAt: todayAt(10), isLatestVersion: true }]);
    fireEvent.click(screen.getAllByRole('button', { name: actionName }).at(-1)!);
    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(101, targetStatus, 'Работа выполнена.'));
    expect(apiMock.uploadTaskJournalFile).toHaveBeenCalledTimes(1);
    cleanup();
  });

  it.each(['legacy', 'modern'] as const)('sends an executor result to approval from the %s read-only task card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    await renderJournal([
      createTask({
        assigneeUserId: 7,
        assigneeName: 'Исполнитель',
        authorUserId: 8,
        authorName: 'Постановщик',
        result: '',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Исполнитель', approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { taskCardWindowMode, executorCompletionAllowedUserIds: [] } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const submitButtons = within(card).getAllByRole('button', { name: 'Отправить на согласование' });
    fireEvent.click(submitButtons[submitButtons.length - 1]);

    expect(await screen.findByRole('heading', { name: 'Добавить результат по задаче' })).toBeTruthy();
    const resultFile = new File(['Итоговый документ'], 'результат.pdf', { type: 'application/pdf' });
    fireEvent.change(screen.getByTestId('task-completion-file-input'), {
      target: { files: [resultFile] },
    });
    expect(screen.getByText('результат.pdf')).toBeTruthy();
    fireEvent.change(document.getElementById('task-completion-result')!, {
      target: { value: 'Работа выполнена, результат передан постановщику.' },
    });
    fireEvent.click(screen.getAllByRole('button', { name: 'Отправить на согласование' }).at(-1)!);

    await waitFor(() => expect(apiMock.uploadTaskJournalFile).toHaveBeenCalledWith(101, resultFile, undefined));
    await waitFor(() => expect(apiMock.updateTaskJournalStatus).toHaveBeenCalledWith(
      101,
      'onApproval',
      'Работа выполнена, результат передан постановщику.',
    ));
    expect(apiMock.uploadTaskJournalFile.mock.invocationCallOrder[0])
      .toBeLessThan(apiMock.updateTaskJournalStatus.mock.invocationCallOrder[0]);
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
    expect(apiMock.createTaskJournalDeadlineChangeRequest).not.toHaveBeenCalled();
  });

  it.each(['legacy', 'modern'] as const)('offers approval submission instead of direct completion without personal access in the %s task card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    await renderJournal([
      createTask({
        assigneeUserId: 7,
        assigneeName: 'Исполнитель',
        authorUserId: 8,
        authorName: 'Постановщик',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Исполнитель', approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { taskCardWindowMode, executorCompletionAllowedUserIds: [] } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);

    expect(within(card).queryByRole('button', { name: 'Завершить' })).toBeNull();
    expect(within(card).getAllByRole('button', { name: 'Отправить на согласование' }).length).toBeGreaterThan(0);
    expect(within(card).getByText('Что делать с задачей')).toBeTruthy();
    expect(within(card).getByTestId('task-executor-attach-file')).toBeTruthy();
    expect(within(card).getByRole('button', { name: 'Работа выполнена — отправить на согласование' })).toBeTruthy();
    fireEvent.click(within(card).getByRole('button', { name: 'Написать комментарий' }));
    await waitFor(() => expect(document.activeElement?.tagName).toBe('TEXTAREA'));
    expect(within(card).getAllByPlaceholderText(/комментарий/i).length).toBeGreaterThan(0);
  });
});
