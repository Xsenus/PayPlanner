import { setupTaskJournalTests, apiMock, authState, permissionsState, todayAt, toDatetimeLocalInputValue, createTask, renderJournal } from './TaskJournal.test-support';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { useLegalEntities } from '../../hooks/useLegalEntities';
import { useClients } from '../../hooks/useClients';

describe('TaskJournal', () => {
  setupTaskJournalTests();
  it.each(['legacy', 'modern'] as const)('client caller: %s task card client lookup follows the current role', async taskCardWindowMode => {
    await renderJournal([createTask()], { withRoute: true, withRoleRerender: true, settings: { taskCardWindowMode } });
    fireEvent.click(screen.getByTitle('Просмотр')); await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const first = vi.mocked(useClients).mock.lastCall?.[0]; expect(first?.enabled).toBe(true); expect(first?.ownerScope).toBeTruthy();
    authState.user = { ...authState.user, role: { id: 43 } }; fireEvent.click(screen.getByRole('button', { name: 'Обновить роль для теста' }));
    expect(vi.mocked(useClients).mock.lastCall?.[0]?.ownerScope).not.toBe(first?.ownerScope);
  });
  it.each(['legacy', 'modern'] as const)('directory caller: %s task card uses the current role lookup scope', async taskCardWindowMode => {
    await renderJournal([createTask()], { withRoute: true, withRoleRerender: true, settings: { taskCardWindowMode } });
    fireEvent.click(screen.getByTitle('Просмотр')); await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const first = vi.mocked(useLegalEntities).mock.lastCall?.[0]; expect(first?.enabled).toBe(true);
    authState.user = { ...authState.user, role: { id: 42 } }; fireEvent.click(screen.getByRole('button', { name: 'Обновить роль для теста' }));
    expect(vi.mocked(useLegalEntities).mock.lastCall?.[0]?.ownerScope).not.toBe(first?.ownerScope);
  });


  it.each(['legacy', 'modern'] as const)('separately allows editing another user task in the %s task card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    permissionsState.taskCanEdit = false;
    permissionsState.taskCanDelete = false;
    permissionsState.taskCanEditOthers = true;
    permissionsState.taskCanCompleteOthers = false;
    permissionsState.taskCanDeleteOthers = false;
    await renderJournal([
      createTask({
        assigneeUserId: 9,
        assigneeName: 'Другой исполнитель',
        authorUserId: 8,
        authorName: 'Другой постановщик',
        participants: [],
        status: 'inProgress',
      }),
    ], { settings: { taskCardWindowMode } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);

    expect(within(card).getAllByRole('button', { name: 'Редактировать задачу' }).length).toBeGreaterThan(0);
    expect(within(card).queryByRole('button', { name: 'Завершить' })).toBeNull();
  });

  it.each(['legacy', 'modern'] as const)('separately allows completing another user task in the %s task card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    permissionsState.taskCanEdit = false;
    permissionsState.taskCanDelete = false;
    permissionsState.taskCanEditOthers = false;
    permissionsState.taskCanCompleteOthers = true;
    permissionsState.taskCanDeleteOthers = false;
    await renderJournal([
      createTask({
        assigneeUserId: 9,
        assigneeName: 'Другой исполнитель',
        authorUserId: 8,
        authorName: 'Другой постановщик',
        participants: [],
        status: 'inProgress',
      }),
    ], { settings: { taskCardWindowMode } });

    expect(screen.queryByTitle('Принять в работу')).toBeNull();
    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);

    expect(within(card).queryByRole('button', { name: 'Редактировать задачу' })).toBeNull();
    expect(within(card).getAllByRole('button', { name: 'Завершить' }).length).toBeGreaterThan(0);
  });

  it.each(['legacy', 'modern'] as const)('separately allows deleting another user task in the %s task card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    permissionsState.taskCanEdit = false;
    permissionsState.taskCanDelete = false;
    permissionsState.taskCanEditOthers = false;
    permissionsState.taskCanCompleteOthers = false;
    permissionsState.taskCanDeleteOthers = true;
    await renderJournal([
      createTask({
        assigneeUserId: 9,
        assigneeName: 'Другой исполнитель',
        authorUserId: 8,
        authorName: 'Другой постановщик',
        participants: [],
        status: 'inProgress',
      }),
    ], { settings: { taskCardWindowMode } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);

    expect(within(card).queryByRole('button', { name: 'Редактировать задачу' })).toBeNull();
    expect(within(card).queryByRole('button', { name: 'Завершить' })).toBeNull();
    const actionMenuButton = within(card).queryByRole('button', { name: 'Еще' });
    if (actionMenuButton) {
      fireEvent.click(actionMenuButton);
    }
    expect(within(card).getByRole('button', { name: 'Удалить задачу' })).toBeTruthy();
  });

  it.each(['legacy', 'modern'] as const)('allows executor to request deadline change from the %s task card without moving task directly', async (taskCardWindowMode) => {
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
    ], { settings: { taskCardWindowMode } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);

    fireEvent.change(within(card).getByLabelText('Новый срок для запроса переноса'), { target: { value: '2026-05-15T10:30' } });
    fireEvent.click(within(card).getByRole('button', { name: 'Запросить перенос' }));

    const reasonDialog = await screen.findByTestId('deadline-request-reason-dialog');
    expect(apiMock.createTaskJournalDeadlineChangeRequest).not.toHaveBeenCalled();
    fireEvent.change(within(reasonDialog).getByLabelText('Причина запроса'), { target: { value: 'Нужны документы от клиента' } });
    fireEvent.click(within(reasonDialog).getByRole('button', { name: 'Отправить запрос' }));

    await waitFor(() => expect(apiMock.createTaskJournalDeadlineChangeRequest).toHaveBeenCalledWith(101, {
      requestedDueAt: expect.any(String),
      reason: 'Нужны документы от клиента',
    }));
    const requestPayload = apiMock.createTaskJournalDeadlineChangeRequest.mock.calls.at(-1)?.[1];
    expect(toDatetimeLocalInputValue(new Date(requestPayload.requestedDueAt))).toBe('2026-05-15T10:30');
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
  });

  it.each(['legacy', 'modern'] as const)('shows deadline change request to an executor with edit access in the %s task card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Исполнитель', email: 'assignee@example.com' };
    await renderJournal([
      createTask({ assigneeUserId: 7, authorUserId: 8, authorName: 'Постановщик' }),
    ], { settings: { taskCardWindowMode, restrictExecutorEditing: false } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);

    expect(within(card).getAllByRole('button', { name: 'Редактировать задачу' }).length).toBeGreaterThan(0);
    expect(within(card).getByRole('button', { name: 'Запросить перенос' })).toBeTruthy();
  });

  it.each(['legacy', 'modern'] as const)('does not offer deadline change request to the task author in the %s task card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Постановщик', email: 'author@example.com' };
    await renderJournal([
      createTask({ assigneeUserId: 7, authorUserId: 7, authorName: 'Постановщик' }),
    ], { settings: { taskCardWindowMode, restrictExecutorEditing: false } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    expect(within(card).queryByRole('button', { name: 'Запросить перенос' })).toBeNull();
  });

  it('shows deadline change requests in the task card', async () => {
    apiMock.getTaskJournalDeadlineChangeRequests.mockResolvedValue([
      {
        id: 901,
        taskJournalItemId: 101,
        requestedByUserId: 7,
        requestedByName: 'Исполнитель',
        reviewerUserId: 8,
        reviewerName: 'Постановщик',
        currentDueAt: todayAt(18),
        requestedDueAt: todayAt(20),
        reason: 'Нужно дождаться документы от клиента',
        status: 'pending',
        decisionDueAt: null,
        decisionComment: null,
        decidedByUserId: null,
        decidedByName: null,
        decidedAt: null,
        createdCommentId: 501,
        decisionCommentId: null,
        createdAt: todayAt(12),
        updatedAt: todayAt(12),
      },
    ]);

    await renderJournal([
      createTask({
        assigneeUserId: 7,
        assigneeName: 'Исполнитель',
        authorUserId: 8,
        authorName: 'Постановщик',
      }),
    ]);

    fireEvent.click(screen.getByTitle('Просмотр'));

    const requestsSection = await screen.findByTestId('task-deadline-change-requests');
    expect(within(requestsSection).getByText('Запросы переноса срока')).toBeTruthy();
    expect(within(requestsSection).getByText('Ожидает решения')).toBeTruthy();
    expect(within(requestsSection).getByText('Исполнитель')).toBeTruthy();
    expect(within(requestsSection).getByText('Ожидает решения: Постановщик')).toBeTruthy();
    expect(within(requestsSection).getByText(/Нужно дождаться документы от клиента/)).toBeTruthy();
  });

  it.each(['legacy', 'modern'] as const)('opens the deadline decision window from the %s task card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 8, fullName: 'Постановщик', email: 'author@example.com' };
    apiMock.getTaskJournalDeadlineChangeRequests.mockResolvedValue([{
      id: 904,
      taskJournalItemId: 101,
      requestedByUserId: 7,
      requestedByName: 'Исполнитель',
      reviewerUserId: 8,
      reviewerName: 'Постановщик',
      currentDueAt: todayAt(18),
      requestedDueAt: todayAt(20),
      reason: 'Нужно получить ответ суда',
      status: 'pending',
      decisionDueAt: null,
      decisionComment: null,
      decidedByUserId: null,
      decidedByName: null,
      decidedAt: null,
      createdCommentId: 501,
      decisionCommentId: null,
      createdAt: todayAt(12),
      updatedAt: todayAt(12),
    }]);

    await renderJournal([createTask({ authorUserId: 8, authorName: 'Постановщик' })], {
      settings: { taskCardWindowMode },
    });
    fireEvent.click(screen.getByTitle('Просмотр'));
    const section = await screen.findByTestId('task-deadline-change-requests');
    fireEvent.click(within(section).getByRole('button', { name: 'Рассмотреть запрос: согласовать или отказать' }));

    const prompt = await screen.findByTestId('deadline-change-decision-prompt');
    expect(within(prompt).getByText('Нужно получить ответ суда')).toBeTruthy();
    const approvedDueAtInput = within(prompt).getByLabelText('Новый срок задачи') as HTMLInputElement;
    expect(approvedDueAtInput.value).toBe(toDatetimeLocalInputValue(new Date(todayAt(20))));
  });
});
