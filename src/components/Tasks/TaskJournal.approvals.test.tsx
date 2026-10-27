import { setupTaskJournalTests, apiMock, toastMock, authState, permissionsState, todayAt, toDatetimeLocalInputValue, createTask, renderJournal, openFirstTaskForEdit } from './TaskJournal.test-support';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { type TaskJournalItem } from '../../utils/taskJournalStore';
import { getForcedTaskSnoozeStorageKey, getForcedTaskApprovalSnoozeKey, getForcedTaskDeadlineChangeSnoozeKey } from '../../utils/forcedTaskActionSnoozes';

describe('TaskJournal', () => {
  setupTaskJournalTests();


  it('shows only tasks that reached the current approver in approvals mode', async () => {
    authState.isAdmin = false;

    const approvalTask = createTask({
      id: 201,
      title: 'Согласовать проект',
      status: 'onApproval',
      assigneeUserId: 8,
      participants: [
        { userId: 7, role: 'approver', displayName: 'Администратор', sortOrder: 0, approvalStatus: 'pending' },
      ],
    });
    const laterApprovalTask = createTask({
      id: 202,
      title: 'Ждет предыдущего согласующего',
      status: 'onApproval',
      assigneeUserId: 8,
      participants: [
        { userId: 9, role: 'approver', displayName: 'Первый согласующий', sortOrder: 0, approvalStatus: 'pending' },
        { userId: 7, role: 'approver', displayName: 'Администратор', sortOrder: 1, approvalStatus: 'pending' },
      ],
    });
    const regularTask = createTask({
      id: 203,
      title: 'Обычная задача',
      status: 'new',
      assigneeUserId: 7,
    });

    await renderJournal([approvalTask, laterApprovalTask, regularTask], {
      mode: 'approvals',
      waitForTitle: approvalTask.title,
    });

    expect(screen.getByRole('heading', { name: 'Согласования' })).toBeTruthy();
    expect(screen.getAllByText(approvalTask.title).length).toBeGreaterThan(0);
    expect(screen.queryByText(laterApprovalTask.title)).toBeNull();
    expect(screen.queryByText(regularTask.title)).toBeNull();
  });

  it('shows pending deadline change requests in approvals mode for the reviewer', async () => {
    authState.isAdmin = false;
    authState.user = { id: 8, fullName: 'Постановщик', email: 'author@example.com' };

    const deadlineRequestTask = createTask({
      id: 204,
      title: 'Перенести срок подготовки документов',
      status: 'new',
      authorUserId: 8,
      authorName: 'Постановщик',
      assigneeUserId: 7,
      assigneeName: 'Исполнитель',
      activeDeadlineChangeRequests: [
        {
          id: 901,
          taskJournalItemId: 204,
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
      ],
    });
    const regularTask = createTask({
      id: 205,
      title: 'Обычная задача без согласования',
      status: 'new',
      authorUserId: 8,
      assigneeUserId: 7,
    });

    await renderJournal([deadlineRequestTask, regularTask], {
      mode: 'approvals',
      waitForTitle: deadlineRequestTask.title,
    });

    expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(
      expect.objectContaining({ includeDeadlineChangeRequests: true }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
    expect(screen.getAllByText(deadlineRequestTask.title).length).toBeGreaterThan(0);
    expect(screen.getByTestId('task-deadline-change-request-badge-204')).toBeTruthy();
    expect(screen.getAllByText('Запрос переноса срока').length).toBeGreaterThan(0);
    expect(screen.queryByText(regularTask.title)).toBeNull();
  });

  describe.each(['legacy', 'modern'] as const)('routed decisions: %s', (mode) => {
    const deadlineRequest = () => ({ id: 902, taskJournalItemId: 101, requestedByUserId: 8,
      requestedByName: 'Исполнитель', reviewerUserId: 7, reviewerName: 'Постановщик',
      currentDueAt: todayAt(18), requestedDueAt: todayAt(20), reason: 'Синтетическая причина',
      status: 'pending', decisionDueAt: null, decisionComment: null, decidedByUserId: null,
      decidedByName: null, decidedAt: null, createdCommentId: 501, decisionCommentId: null,
      createdAt: todayAt(12), updatedAt: todayAt(12) });
    async function openDeadline(options: { nextRouteTaskId?: number } = {}) {
      apiMock.getTaskJournalDeadlineChangeRequests.mockResolvedValue([deadlineRequest()]);
      await renderJournal([createTask({ authorUserId: 7, assigneeUserId: 8 }), createTask({ id: 102, title: 'Другая задача' })],
        { withRoute: true, unmountOnTaskClose: true, nextRouteTaskId: options.nextRouteTaskId, settings: { taskCardWindowMode: mode } });
      fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
      const card = await screen.findByTestId(`task-${mode}-card-window`);
      fireEvent.click(await within(card).findByRole('button', { name: 'Рассмотреть запрос: согласовать или отказать' }));
      const prompt = await screen.findByTestId('deadline-change-decision-prompt');
      expect(screen.getByTestId(`task-${mode}-card-window`)).toBeTruthy();
      expect(screen.queryByTestId('returned-to-case')).toBeNull();
      return prompt;
    }
    it.each(['approve', 'reject'] as const)('keeps the card until a successful deadline %s and then returns to the case', async (decision) => {
      const prompt = await openDeadline();
      fireEvent.change(within(prompt).getByLabelText('Комментарий к решению'), { target: { value: 'Синтетическое решение' } });
      fireEvent.click(within(prompt).getByRole('button', { name: decision === 'approve' ? 'Согласовать перенос' : 'Отказать' }));
      await screen.findByTestId('returned-to-case');
      expect(decision === 'approve' ? apiMock.approveTaskJournalDeadlineChangeRequest : apiMock.rejectTaskJournalDeadlineChangeRequest)
        .toHaveBeenCalledWith(101, 902, expect.objectContaining({ comment: 'Синтетическое решение' }));
    });
    it('keeps the card on error and after snoozing the decision', async () => {
      apiMock.approveTaskJournalDeadlineChangeRequest.mockRejectedValueOnce(new Error('Синтетический отказ сети'));
      const prompt = await openDeadline();
      fireEvent.click(within(prompt).getByRole('button', { name: 'Согласовать перенос' }));
      await within(prompt).findByText('Синтетический отказ сети');
      expect(screen.queryByTestId('returned-to-case')).toBeNull();
      fireEvent.click(within(prompt).getByText('Отложить')); fireEvent.click(within(prompt).getByRole('button', { name: '10 мин' }));
      await waitFor(() => expect(screen.queryByTestId('deadline-change-decision-prompt')).toBeNull());
      expect(screen.getByTestId(`task-${mode}-card-window`)).toBeTruthy();
    });
    it('does not close another routed task from a delayed deadline response', async () => {
      let finish!: (task: TaskJournalItem) => void;
      apiMock.approveTaskJournalDeadlineChangeRequest.mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }));
      apiMock.getTaskJournalItem.mockImplementation(async (id: number) => id === 102
        ? createTask({ id: 102, title: 'Другая задача' }) : createTask({ authorUserId: 7, assigneeUserId: 8 }));
      const prompt = await openDeadline({ nextRouteTaskId: 102 });
      fireEvent.click(within(prompt).getByRole('button', { name: 'Согласовать перенос' }));
      await waitFor(() => expect(apiMock.approveTaskJournalDeadlineChangeRequest).toHaveBeenCalledOnce());
      fireEvent.click(screen.getByRole('button', { name: 'Перейти в другую задачу' }));
      await waitFor(() => expect((within(screen.getByTestId(`task-${mode}-card-window`)).getByTestId('task-title-input') as HTMLInputElement).value).toBe('Другая задача'));
      await act(async () => finish(createTask({ dueAt: todayAt(20) })));
      expect(screen.queryByTestId('returned-to-case')).toBeNull();
      expect(screen.getByTestId(`task-${mode}-card-window`)).toBeTruthy();
      expect(screen.queryByTestId('deadline-change-decision-prompt')).toBeNull();
      expect((screen.getByTestId('task-title-input') as HTMLInputElement).value).toBe('Другая задача');
    });
    it('ignores a late approval response after navigation to another routed task', async () => {
      let finish!: (task: TaskJournalItem) => void;
      const pendingTask = createTask({ status: 'onApproval', result: 'Работа выполнена', authorUserId: 8, assigneeUserId: 8,
        participants: [{ id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' }] });
      const otherTask = createTask({ id: 102, title: 'Другая задача' });
      apiMock.updateTaskJournalApproval.mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }));
      apiMock.getTaskJournalItem.mockImplementation(async (id: number) => id === 102 ? otherTask : pendingTask);
      await renderJournal([pendingTask, otherTask], { withRoute: true, unmountOnTaskClose: true, nextRouteTaskId: 102,
        settings: { taskCardWindowMode: mode } });
      fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
      const card = await screen.findByTestId(`task-${mode}-card-window`);
      fireEvent.click(within(card).getByTestId('task-open-approval-decision'));
      const prompt = await screen.findByTestId('task-approval-decision-prompt');
      fireEvent.click(within(prompt).getByRole('button', { name: 'Согласовать и завершить' }));
      await waitFor(() => expect(apiMock.updateTaskJournalApproval).toHaveBeenCalledOnce());
      fireEvent.click(screen.getByRole('button', { name: 'Перейти в другую задачу' }));
      await waitFor(() => expect((screen.getByTestId('task-title-input') as HTMLInputElement).value).toBe('Другая задача'));
      await act(async () => finish({ ...pendingTask, status: 'done' }));
      expect(screen.queryByTestId('returned-to-case')).toBeNull();
      expect((screen.getByTestId('task-title-input') as HTMLInputElement).value).toBe('Другая задача');
      expect(screen.queryByTestId('task-approval-decision-prompt')).toBeNull();
    });
    it('clears the routed decision after returning to the journal in the same component', async () => {
      const task = createTask({ status: 'onApproval', result: 'Работа выполнена', authorUserId: 8, assigneeUserId: 8,
        participants: [{ id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' }] });
      await renderJournal([task], { withRoute: true, settings: { taskCardWindowMode: mode } });
      fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
      const card = await screen.findByTestId(`task-${mode}-card-window`);
      fireEvent.click(within(card).getByTestId('task-open-approval-decision'));
      const prompt = await screen.findByTestId('task-approval-decision-prompt');
      fireEvent.click(within(prompt).getByRole('button', { name: 'Согласовать и завершить' }));
      await screen.findByTestId('task-view-controls');
      await waitFor(() => expect(screen.queryByTestId('task-approval-decision-prompt')).toBeNull());
      expect(screen.queryByTestId(`task-${mode}-card-window`)).toBeNull();
    });
    it('preserves the modal deadline decision in the approvals journal with a route callback', async () => {
      const request = deadlineRequest(); const task = createTask({ authorUserId: 7, assigneeUserId: 8, activeDeadlineChangeRequests: [request] });
      apiMock.getTaskJournalDeadlineChangeRequests.mockResolvedValue([request]);
      await renderJournal([task], { withRoute: true, mode: 'approvals', settings: { taskCardWindowMode: mode } });
      const automatic = await screen.findByTestId('deadline-change-decision-prompt');
      fireEvent.click(within(automatic).getByText('Отложить')); fireEvent.click(within(automatic).getByRole('button', { name: '10 мин' }));
      await waitFor(() => expect(screen.queryByTestId('deadline-change-decision-prompt')).toBeNull());
      fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
      const card = await screen.findByTestId(`task-${mode}-card-window`);
      fireEvent.click(await within(card).findByRole('button', { name: 'Рассмотреть запрос: согласовать или отказать' }));
      const prompt = await screen.findByTestId('deadline-change-decision-prompt');
      expect(screen.queryByTestId(`task-${mode}-card-window`)).toBeNull();
      fireEvent.click(within(prompt).getByRole('button', { name: 'Согласовать перенос' }));
      await waitFor(() => expect(screen.queryByTestId('deadline-change-decision-prompt')).toBeNull());
      expect(apiMock.approveTaskJournalDeadlineChangeRequest).toHaveBeenCalledWith(101, 902, expect.any(Object));
    });
    it('keeps the routed approval decision available until the approver finishes', async () => {
      await renderJournal([createTask({ status: 'onApproval', result: 'Работа выполнена', authorUserId: 8, assigneeUserId: 8,
        participants: [{ id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' }] })],
      { withRoute: true, unmountOnTaskClose: true, settings: { taskCardWindowMode: mode } });
      fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
      const card = await screen.findByTestId(`task-${mode}-card-window`);
      fireEvent.click(within(card).getByTestId('task-open-approval-decision'));
      const prompt = await screen.findByTestId('task-approval-decision-prompt');
      expect(screen.queryByTestId('returned-to-case')).toBeNull();
      expect(screen.getByTestId(`task-${mode}-card-window`)).toBeTruthy();
      fireEvent.click(within(prompt).getByRole('button', { name: 'Согласовать и завершить' }));
      await screen.findByTestId('returned-to-case');
      expect(apiMock.updateTaskJournalApproval).toHaveBeenCalledWith(101, 'approved', 'Работа выполнена', 77, 'complete');
    });
  });

  it('shows a forced decision prompt for deadline change requests in approvals mode', async () => {
    authState.isAdmin = false;
    authState.user = { id: 8, fullName: 'Постановщик', email: 'author@example.com' };
    const requestedDueAt = todayAt(20);

    const deadlineRequestTask = createTask({
      id: 206,
      title: 'Согласовать перенос срока документов',
      status: 'new',
      authorUserId: 8,
      authorName: 'Постановщик',
      assigneeUserId: 7,
      assigneeName: 'Исполнитель',
      activeDeadlineChangeRequests: [
        {
          id: 902,
          taskJournalItemId: 206,
          requestedByUserId: 7,
          requestedByName: 'Исполнитель',
          reviewerUserId: 8,
          reviewerName: 'Постановщик',
          currentDueAt: todayAt(18),
          requestedDueAt,
          reason: 'Документы будут готовы завтра',
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
      ],
    });

    await renderJournal([deadlineRequestTask], {
      mode: 'approvals',
      waitForTitle: deadlineRequestTask.title,
    });

    const prompt = await screen.findByTestId('deadline-change-decision-prompt');
    expect(within(prompt).getByText('У вас запросили перенос срока по задаче')).toBeTruthy();
    expect(within(prompt).getByText('Документы будут готовы завтра')).toBeTruthy();

    const approvedDueAtInput = within(prompt).getByLabelText('Новый срок задачи') as HTMLInputElement;
    expect(approvedDueAtInput.value).toBe(toDatetimeLocalInputValue(new Date(requestedDueAt)));
    const changedDueAt = new Date(requestedDueAt);
    changedDueAt.setHours(changedDueAt.getHours() + 2);
    fireEvent.change(approvedDueAtInput, {
      target: { value: toDatetimeLocalInputValue(changedDueAt) },
    });

    fireEvent.click(within(prompt).getByRole('button', { name: 'Согласовать перенос' }));

    await waitFor(() => expect(apiMock.approveTaskJournalDeadlineChangeRequest).toHaveBeenCalledWith(206, 902, {
      approvedDueAt: expect.not.stringMatching(requestedDueAt),
      comment: null,
    }));
  });

  it('closes a forced deadline prompt when its task was deleted elsewhere', async () => {
    authState.isAdmin = false;
    authState.user = { id: 8, fullName: 'Постановщик', email: 'author@example.com' };
    const deletedTask = createTask({
      id: 208,
      title: 'Удалённая задача с переносом срока',
      authorUserId: 8,
      assigneeUserId: 7,
      activeDeadlineChangeRequests: [{
        id: 904,
        taskJournalItemId: 208,
        requestedByUserId: 7,
        requestedByName: 'Исполнитель',
        reviewerUserId: 8,
        reviewerName: 'Постановщик',
        currentDueAt: todayAt(18),
        requestedDueAt: todayAt(20),
        reason: 'Ожидаем документы',
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
      }],
    });
    apiMock.getTaskJournalItem.mockRejectedValueOnce(new Error('HTTP 404 Not Found at /api/tasks/208'));

    await renderJournal([deletedTask], { mode: 'approvals' });

    await screen.findByTestId('deadline-change-decision-prompt');
    await waitFor(() => expect(screen.queryByTestId('deadline-change-decision-prompt')).toBeNull());
    expect(toastMock.info).toHaveBeenCalledWith(
      expect.stringContaining('Задача уже удалена'),
      'Согласования',
    );
    expect(apiMock.approveTaskJournalDeadlineChangeRequest).not.toHaveBeenCalled();
    expect(apiMock.rejectTaskJournalDeadlineChangeRequest).not.toHaveBeenCalled();
  });

  it('opens the deadline change decision from a journal row without entering the task', async () => {
    authState.isAdmin = false;
    authState.user = { id: 8, fullName: 'Постановщик', email: 'author@example.com' };
    const task = createTask({
      id: 207,
      title: 'Быстро рассмотреть перенос',
      authorUserId: 8,
      assigneeUserId: 7,
      activeDeadlineChangeRequests: [{
        id: 903,
        taskJournalItemId: 207,
        requestedByUserId: 7,
        requestedByName: 'Исполнитель',
        reviewerUserId: 8,
        reviewerName: 'Постановщик',
        currentDueAt: todayAt(18),
        requestedDueAt: todayAt(20),
        reason: 'Ожидаем документы',
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
      }],
    });

    await renderJournal([task], { waitForTitle: task.title });

    expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(
      expect.objectContaining({ includeDeadlineChangeRequests: true }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Рассмотреть запрос переноса срока' }));

    const prompt = await screen.findByTestId('deadline-change-decision-prompt');
    expect(within(prompt).getByText('Ожидаем документы')).toBeTruthy();
    expect(within(prompt).getByRole('button', { name: 'Согласовать перенос' })).toBeTruthy();
    expect(within(prompt).getByRole('button', { name: 'Отказать' })).toBeTruthy();
  });

  it('lets the reviewer snooze a deadline change decision in the approvals journal', async () => {
    authState.isAdmin = false;
    authState.user = { id: 8, fullName: 'Постановщик', email: 'author@example.com' };
    const deadlineRequestTask = createTask({
      id: 206,
      title: 'Согласовать перенос срока документов',
      status: 'new',
      authorUserId: 8,
      activeDeadlineChangeRequests: [
        {
          id: 902,
          taskJournalItemId: 206,
          requestedByUserId: 7,
          requestedByName: 'Исполнитель',
          reviewerUserId: 8,
          reviewerName: 'Постановщик',
          currentDueAt: todayAt(18),
          requestedDueAt: todayAt(20),
          reason: 'Документы будут готовы завтра',
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
      ],
    });

    await renderJournal([deadlineRequestTask], { mode: 'approvals' });

    const prompt = await screen.findByTestId('deadline-change-decision-prompt');
    fireEvent.click(within(prompt).getByText('Отложить'));
    fireEvent.click(within(prompt).getByRole('button', { name: '30 мин' }));

    await waitFor(() => expect(screen.queryByTestId('deadline-change-decision-prompt')).toBeNull());
    expect(apiMock.approveTaskJournalDeadlineChangeRequest).not.toHaveBeenCalled();
    expect(apiMock.rejectTaskJournalDeadlineChangeRequest).not.toHaveBeenCalled();
    const snoozes = JSON.parse(window.localStorage.getItem(getForcedTaskSnoozeStorageKey(8)!) || '{}') as Record<string, number>;
    expect(snoozes[getForcedTaskDeadlineChangeSnoozeKey(206, 902)]).toBeGreaterThan(Date.now());
  });

  it('shows approval action buttons only for the current approver in queue', async () => {
    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { userId: 9, role: 'approver', displayName: 'Первый согласующий', sortOrder: 0, approvalStatus: 'pending' },
          { userId: 7, role: 'approver', displayName: 'Администратор', sortOrder: 1, approvalStatus: 'pending' },
        ],
      }),
    ]);

    await openFirstTaskForEdit();

    expect(screen.queryByRole('button', { name: 'Согласовать' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'На доработку' })).toBeNull();
  });

  it('opens the work approval window from a journal row without entering the task', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    const task = createTask({
      status: 'onApproval',
      result: 'Работа сдана исполнителем.',
      participants: [
        { id: 78, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
      ],
    });

    await renderJournal([task], { settings: { approvalQuickActionMode: 'separateButton' } });

    fireEvent.click(screen.getByRole('button', { name: 'Рассмотреть согласование' }));
    const prompt = await screen.findByTestId('task-approval-decision-prompt');
    expect(within(prompt).getByText('Работа сдана исполнителем.')).toBeTruthy();
    expect(within(prompt).getByRole('button', { name: 'Согласовать и завершить' })).toBeTruthy();
    expect(within(prompt).getByRole('button', { name: 'Вернуть на доработку' })).toBeTruthy();
  });

  it('shows a forced approval decision prompt for the current approver in approvals mode', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;

    await renderJournal([
      createTask({
        status: 'onApproval',
        result: 'Исполнитель подготовил документы и приложил результат.',
        participants: [
          { id: 76, userId: 6, role: 'approver', displayName: 'Первый согласующий', sortOrder: 0, approvalStatus: 'approved' },
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 1, approvalStatus: 'pending' },
          { id: 78, userId: 8, role: 'approver', displayName: 'Следующий согласующий', sortOrder: 2, approvalStatus: 'pending' },
        ],
      }),
    ], { mode: 'approvals' });

    const prompt = await screen.findByTestId('task-approval-decision-prompt');
    expect(within(prompt).getByRole('heading', { name: 'У вас задача на согласовании' })).toBeTruthy();
    expect(within(prompt).getByText('Исполнитель подготовил документы и приложил результат.')).toBeTruthy();

    fireEvent.click(within(prompt).getByRole('button', { name: 'Согласовать' }));

    await waitFor(() => expect(apiMock.updateTaskJournalApproval).toHaveBeenCalledWith(
      101,
      'approved',
      'Исполнитель подготовил документы и приложил результат.',
      77,
      undefined,
    ));
  });

  it('keeps the approvals journal accessible when the same task was snoozed in the global prompt', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    window.localStorage.setItem(getForcedTaskSnoozeStorageKey(7)!, JSON.stringify({
      [getForcedTaskApprovalSnoozeKey(101)]: Date.now() + 10 * 60_000,
    }));

    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { mode: 'approvals' });

    expect(screen.queryByTestId('task-approval-decision-prompt')).toBeNull();
    expect(screen.getByTestId('task-view-controls')).toBeTruthy();
  });

  it('lets the approver snooze the journal decision prompt without resolving the task', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;

    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { mode: 'approvals' });

    const prompt = await screen.findByTestId('task-approval-decision-prompt');
    fireEvent.click(within(prompt).getByText('Отложить'));
    fireEvent.click(within(prompt).getByRole('button', { name: '10 мин' }));

    await waitFor(() => expect(screen.queryByTestId('task-approval-decision-prompt')).toBeNull());
    expect(apiMock.updateTaskJournalApproval).not.toHaveBeenCalled();
    const snoozes = JSON.parse(window.localStorage.getItem(getForcedTaskSnoozeStorageKey(7)!) || '{}') as Record<string, number>;
    expect(snoozes[getForcedTaskApprovalSnoozeKey(101)]).toBeGreaterThan(Date.now());
  });

  it('does not show the forced approval decision prompt to admin when admin is not the current approver', async () => {
    authState.isAdmin = true;
    authState.user = { id: 99, fullName: 'Admin viewer', email: 'admin-viewer@example.com' };
    permissionsState.taskCanEdit = false;

    await renderJournal([
      createTask({
        title: 'Task for another approver',
        status: 'onApproval',
        result: 'Work result is ready.',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Current approver', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { mode: 'approvals' });

    expect(screen.queryByTestId('task-approval-decision-prompt')).toBeNull();
  });

  it('requires a revision comment in the forced approval decision prompt', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;

    await renderJournal([
      createTask({
        status: 'onApproval',
        result: 'Результат на проверку.',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { mode: 'approvals' });

    const prompt = await screen.findByTestId('task-approval-decision-prompt');
    fireEvent.click(within(prompt).getByRole('button', { name: 'Вернуть на доработку' }));

    expect(within(prompt).getByText('Напишите, что нужно исправить перед повторным согласованием.')).toBeTruthy();
    expect(apiMock.updateTaskJournalApproval).not.toHaveBeenCalled();

    fireEvent.change(within(prompt).getByLabelText('Комментарий для возврата на доработку'), {
      target: { value: 'Нужно приложить акт и уточнить дату.' },
    });
    fireEvent.change(within(prompt).getByLabelText('Срок доработки'), {
      target: { value: '2099-01-10T10:30' },
    });
    fireEvent.click(within(prompt).getByRole('button', { name: 'Вернуть на доработку' }));

    await waitFor(() => expect(apiMock.updateTaskJournalApproval).toHaveBeenCalledWith(
      101,
      'revisionRequested',
      'Нужно приложить акт и уточнить дату.',
      77,
      undefined,
      expect.any(String),
    ));
  });

  it('lets the final approver complete the task from the forced approval decision prompt', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;

    await renderJournal([
      createTask({
        status: 'onApproval',
        result: 'Все работы выполнены.',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { mode: 'approvals' });

    const prompt = await screen.findByTestId('task-approval-decision-prompt');
    fireEvent.click(within(prompt).getByRole('button', { name: 'Согласовать и завершить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalApproval).toHaveBeenCalledWith(
      101,
      'approved',
      'Все работы выполнены.',
      77,
      'complete',
    ));
  });

  it.each(['legacy', 'modern'] as const)('opens the shared approval window from the %s task card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    await renderJournal([
      createTask({
        status: 'onApproval',
        result: 'Итог работы передан на проверку.',
        participants: [
          { id: 79, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { taskCardWindowMode, approvalQuickActionMode: 'separateButton' } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const buttonName = taskCardWindowMode === 'modern' ? 'Открыть окно согласования' : 'Открыть окно решения';
    fireEvent.click(within(card).getByRole('button', { name: buttonName }));

    const prompt = await screen.findByTestId('task-approval-decision-prompt');
    expect(within(prompt).getByText('Итог работы передан на проверку.')).toBeTruthy();
  });

  it.each(['legacy', 'modern'] as const)('keeps save available while an editable task awaits approval in the %s task card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = true;
    await renderJournal([
      createTask({
        status: 'onApproval',
        assigneeUserId: 7,
        assigneeName: 'Согласующий',
        result: 'Готово к согласованию.',
        participants: [
          { id: 79, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { taskCardWindowMode, approvalQuickActionMode: 'completionButton', restrictExecutorEditing: false } });

    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const description = within(card).getByTestId('task-description-field');
    fireEvent.change(description, { target: { value: 'Исправленное описание перед решением.' } });

    expect(within(card).getByRole('button', { name: 'Сохранить' })).toBeTruthy();
    expect(within(card).getAllByRole('button', { name: 'Открыть окно согласования' }).length).toBeGreaterThan(0);
    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.updateTaskJournalItem.mock.calls[0][1]).toEqual(expect.objectContaining({
      description: 'Исправленное описание перед решением.',
      status: 'onApproval',
    }));
  });

  it('opens approval from the green action in completion-button mode', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { approvalQuickActionMode: 'completionButton' } });

    expect(screen.queryByRole('button', { name: 'Рассмотреть согласование' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Действие по согласованию' }));

    expect(await screen.findByTestId('task-approval-decision-prompt')).toBeTruthy();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();
  });

  it('disables completion and shows a separate approval action in separate-button mode', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { approvalQuickActionMode: 'separateButton' } });

    const completion = screen.getByRole('button', { name: 'Действие по согласованию' });
    expect((completion as HTMLButtonElement).disabled).toBe(true);
    const approval = screen.getByRole('button', { name: 'Рассмотреть согласование' });
    fireEvent.click(approval);

    expect(await screen.findByTestId('task-approval-decision-prompt')).toBeTruthy();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();
  });

  it.each(['legacy', 'modern'] as const)('routes the completion action to approval in the %s task card', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { taskCardWindowMode, approvalQuickActionMode: 'completionButton' } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.click(within(card).getAllByRole('button', { name: 'Открыть окно согласования' })[0]);

    expect(screen.getByText('Завершить согласование')).toBeTruthy();
    expect(apiMock.updateTaskJournalStatus).not.toHaveBeenCalled();
  });

  it.each(['legacy', 'modern'] as const)('disables completion in the %s task card when a separate approval action is configured', async (taskCardWindowMode) => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ], { settings: { taskCardWindowMode, approvalQuickActionMode: 'separateButton' } });

    fireEvent.click(screen.getByTitle('Просмотр'));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const completionButtons = within(card).getAllByRole('button', { name: 'Задача находится на согласовании' });
    expect(completionButtons.length).toBeGreaterThan(0);
    completionButtons.forEach((button) => expect((button as HTMLButtonElement).disabled).toBe(true));
  });

  it('lets the current final approver choose whether to complete the task from view mode', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ]);

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    expect(await screen.findByRole('heading', { name: 'Просмотр задачи' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Согласовать' }));
    expect(screen.getByText('Завершить согласование')).toBeTruthy();
    expect(screen.getByText(
      'Исполнитель получит уведомление о согласовании, а задача будет закрыта и перейдет в статус «Готово».',
    )).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Согласовать и завершить задачу/ }));

    await waitFor(() => expect(apiMock.updateTaskJournalApproval).toHaveBeenCalledWith(
      101,
      'approved',
      '',
      77,
      'complete',
    ));
  });

  it('lets the current final approver approve and return the task to the assignee', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ]);

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    expect(await screen.findByRole('heading', { name: 'Просмотр задачи' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Согласовать' }));
    fireEvent.click(screen.getByRole('button', { name: /Согласовать и отправить исполнителю/ }));

    await waitFor(() => expect(apiMock.updateTaskJournalApproval).toHaveBeenCalledWith(
      101,
      'approved',
      '',
      77,
      'returnToAssignee',
    ));
  });

  it('requires a review before returning an approval task to revision', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ]);

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    expect(await screen.findByRole('heading', { name: 'Просмотр задачи' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'На доработку' }));
    expect(screen.getByText('Рецензия на доработку')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Отправить на доработку с поправками' }));
    expect(screen.getByText('Напишите рецензию: что именно нужно исправить перед повторным согласованием.')).toBeTruthy();
    expect(apiMock.updateTaskJournalApproval).not.toHaveBeenCalled();

    fireEvent.change(screen.getByLabelText('Комментарий для исполнителя'), {
      target: { value: 'Исправить дату и приложить документ.' },
    });
    fireEvent.change(screen.getByTestId('task-revision-due-at-input'), {
      target: { value: '' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить на доработку с поправками' }));

    await waitFor(() => expect(apiMock.updateTaskJournalApproval).toHaveBeenCalledWith(
      101,
      'revisionRequested',
      'Исправить дату и приложить документ.',
      77,
      undefined,
      undefined,
    ));
  });

  it('passes a revision due date when the approver fills it in', async () => {
    authState.isAdmin = false;
    authState.user = { id: 7, fullName: 'Согласующий', email: 'approver@example.com' };
    permissionsState.taskCanEdit = false;
    await renderJournal([
      createTask({
        status: 'onApproval',
        participants: [
          { id: 77, userId: 7, role: 'approver', displayName: 'Согласующий', sortOrder: 0, approvalStatus: 'pending' },
        ],
      }),
    ]);

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    expect(await screen.findByRole('heading', { name: 'Просмотр задачи' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'На доработку' }));
    fireEvent.change(screen.getByLabelText('Комментарий для исполнителя'), {
      target: { value: 'Исправить дату.' },
    });
    fireEvent.change(screen.getByTestId('task-revision-due-at-input'), {
      target: { value: '2099-01-10T10:30' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить на доработку с поправками' }));

    await waitFor(() => expect(apiMock.updateTaskJournalApproval).toHaveBeenCalledWith(
      101,
      'revisionRequested',
      'Исправить дату.',
      77,
      undefined,
      expect.any(String),
    ));
  });

  it('shows agreed tasks as approved for the executor after approval', async () => {
    await renderJournal([
      createTask({
        status: 'approved',
        participants: [
          { userId: 7, role: 'assignee', displayName: 'Администратор', approvalStatus: 'pending' },
          { userId: 8, role: 'approver', displayName: 'Согласующий', approvalStatus: 'approved' },
        ],
      }),
    ]);

    expect(screen.getByText('Согласовано: Согласующий')).toBeTruthy();
    expect(screen.getByDisplayValue('Согласовано')).toBeTruthy();
  });
});
