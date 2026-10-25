import { setupTaskJournalTests, apiMock, authState, todayAt, createTask, renderJournal, openFirstTaskForEdit, openNewTaskFormAsGlobalInternal } from './TaskJournal.test-support';
import { TaskJournal } from './TaskJournal';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { StrictMode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import type { TaskJournalSettingsDto } from '../../services/api';
import { TASK_JOURNAL_REALTIME_EVENT } from '../../services/realtimeNotifications';
import { TASK_JOURNAL_EVENT, type TaskJournalItem } from '../../utils/taskJournalStore';
import { USER_NOTIFICATIONS_EVENT } from '../../utils/userNotifications';

describe('TaskJournal', () => {
  setupTaskJournalTests();


  it.each(['legacy', 'modern'] as const)('starts routed task data before ancillary metadata in the %s card', async (taskCardWindowMode) => {
    const setup = await renderJournal([createTask()], { settings: { taskCardWindowMode } });
    setup.unmount();
    vi.clearAllMocks();
    apiMock.getTaskJournalAssignees.mockImplementationOnce(() => new Promise(() => {}));

    render(<StrictMode><TaskJournal routeTaskId={101} /></StrictMode>);

    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    expect(card).toBeTruthy();
    expect(screen.queryByTestId(`task-${taskCardWindowMode === 'modern' ? 'legacy' : 'modern'}-card-window`)).toBeNull();
    expect(apiMock.getTaskJournalItem).toHaveBeenCalledTimes(1);
    for (const metadataRead of [apiMock.getTaskJournalSettings, apiMock.getTaskJournalAssignees,
      apiMock.getUserSetting, apiMock.getCalendarSyncSettings, apiMock.getFeatureFlags]) {
      expect(metadataRead).toHaveBeenCalled();
      expect(apiMock.getTaskJournalItem.mock.invocationCallOrder[0]).toBeLessThan(metadataRead.mock.invocationCallOrder[0]);
    }
  });

  it.each(['legacy', 'modern'] as const)('loads journal reads only when returning from the routed %s card', async (taskCardWindowMode) => {
    const setup = await renderJournal([createTask()], { settings: { taskCardWindowMode } });
    setup.unmount();
    vi.clearAllMocks();
    apiMock.getTaskFileUploadPage.mockResolvedValue({ items: [{ uploadKey: 'pending-route', fileName: 'копия.pdf',
      status: 'waiting', fileId: null, message: 'Ожидание обязательных копий.' }], nextCursor: null });
    const view = render(<StrictMode><TaskJournal routeTaskId={101} /></StrictMode>);
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    expect(await within(card).findByText('Ожидание обязательных копий.')).toBeTruthy();
    expect(apiMock.getTaskJournalItem).toHaveBeenCalledTimes(1);
    expect(apiMock.getTaskJournalSettings).toHaveBeenCalled();
    expect(apiMock.getTaskJournalItems).not.toHaveBeenCalled();
    expect(apiMock.getTaskJournalSummary).not.toHaveBeenCalled();
    expect(apiMock.getTaskJournalCaseAutomationCandidates).not.toHaveBeenCalled();
    expect(apiMock.getTaskJournalCaseAutomationSummary).not.toHaveBeenCalled();
    await act(async () => { window.dispatchEvent(new Event(TASK_JOURNAL_REALTIME_EVENT)); });
    // Let the existing real-time debounce elapse; a hidden journal must remain idle.
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 300)); });
    expect(apiMock.getTaskJournalItems).not.toHaveBeenCalled();
    expect(apiMock.getTaskJournalCaseAutomationCandidates).not.toHaveBeenCalled();

    view.rerender(<StrictMode><TaskJournal routeTaskId={null} /></StrictMode>);
    expect(await screen.findByTestId('task-table')).toBeTruthy();
    await waitFor(() => expect(apiMock.getTaskJournalCaseAutomationSummary).toHaveBeenCalledTimes(1));
    expect(apiMock.getTaskJournalItems).toHaveBeenCalledTimes(1);
    expect(apiMock.getTaskJournalSummary).toHaveBeenCalledTimes(1);
    expect(apiMock.getTaskJournalCaseAutomationCandidates).toHaveBeenCalledTimes(1);
    await act(async () => { window.dispatchEvent(new Event(TASK_JOURNAL_REALTIME_EVENT)); });
    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(apiMock.getTaskJournalCaseAutomationCandidates).toHaveBeenCalledTimes(2));
  });

  describe('in-flight list route transition', () => {
    async function pendingList(mode: 'legacy' | 'modern') {
      const setup = await renderJournal([createTask()], { settings: { taskCardWindowMode: mode } });
      setup.unmount();
      apiMock.getTaskJournalItems.mockClear();
      apiMock.getTaskJournalSummary.mockClear();
      const obsolete = { items: [createTask({ title: 'Устаревший список задач' })], total: 1, page: 1, pageSize: 100 };
      const summary = { active: 99, overdue: 99, today: 99, highPriority: 99 };
      let finishItems!: (value: typeof obsolete) => void;
      let failItems!: (error: Error) => void;
      let finishSummary!: (value: typeof summary) => void;
      const signals: AbortSignal[] = [];
      apiMock.getTaskJournalItems.mockImplementationOnce((_params, options) => {
        signals.push(options.signal);
        return new Promise<typeof obsolete>((resolve, reject) => { finishItems = resolve; failItems = reject; });
      });
      apiMock.getTaskJournalSummary.mockImplementationOnce((_params, options) => {
        signals.push(options.signal);
        return new Promise<typeof summary>((resolve) => { finishSummary = resolve; });
      });
      const view = render(<TaskJournal />);
      await waitFor(() => expect(signals).toHaveLength(2));
      return { view, signals, fail: async () => {
        await act(async () => { failItems(new Error('Synthetic obsolete list failure')); finishSummary(summary); });
      }, finish: async () => {
        await act(async () => { finishItems(obsolete); finishSummary(summary); });
      } };
    }

    it.each(['legacy', 'modern'] as const)('retires the list read when opening the %s card without applying a stale result on return', async mode => {
      const { view, signals, finish } = await pendingList(mode);
      view.rerender(<TaskJournal routeTaskId={101} />);
      await screen.findByTestId(`task-${mode}-card-window`);
      expect(signals.every(signal => !signal.aborted)).toBe(true);
      expect(apiMock.getTaskJournalItems).toHaveBeenCalledTimes(1);
      view.rerender(<TaskJournal routeTaskId={null} />);
      await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalledTimes(2));
      await finish();
      expect(screen.queryByText('Устаревший список задач')).toBeNull();
      expect(apiMock.getTaskJournalSummary).toHaveBeenCalledTimes(2);
    });

    it.each(['legacy', 'modern'] as const)('aborts a retained list read on unmount from the %s card', async mode => {
      const { view, signals, finish } = await pendingList(mode);
      view.rerender(<TaskJournal routeTaskId={101} />);
      await screen.findByTestId(`task-${mode}-card-window`);
      expect(signals.every(signal => !signal.aborted)).toBe(true);
      view.unmount();
      expect(signals.every(signal => signal.aborted)).toBe(true);
      await finish();
    });

    it.each(['legacy', 'modern'] as const)('aborts a retained list read when the employee changes in the %s card', async mode => {
      const { view, signals, finish } = await pendingList(mode);
      view.rerender(<TaskJournal routeTaskId={101} />);
      await screen.findByTestId(`task-${mode}-card-window`);
      expect(signals.every(signal => !signal.aborted)).toBe(true);
      authState.user = { ...authState.user, id: 8 };
      view.rerender(<TaskJournal routeTaskId={101} />);
      await waitFor(() => expect(signals.every(signal => signal.aborted)).toBe(true));
      await finish();
      expect(screen.queryByText('Устаревший список задач')).toBeNull();
    });
  });

  describe('in-flight list invalidation', () => {
    it.each(['legacy', 'modern'] as const)('still cancels the pending list when the filter changes in the %s journal', async mode => {
      const view = await renderJournal([createTask()], { settings: { taskCardWindowMode: mode } });
      let finish!: (value: unknown) => void;
      let signal!: AbortSignal;
      apiMock.getTaskJournalItems.mockImplementationOnce((_params, options) => {
        signal = options.signal;
        return new Promise(resolve => { finish = resolve; });
      });
      await act(async () => { window.dispatchEvent(new Event(TASK_JOURNAL_REALTIME_EVENT)); });
      await waitFor(() => expect(signal).toBeTruthy());
      fireEvent.change(screen.getByTestId('task-priority-filter'), { target: { value: 'urgent' } });
      await waitFor(() => expect(signal.aborted).toBe(true));
      await act(async () => { finish({ items: [createTask({ title: 'Устаревший фильтр' })], total: 1, page: 1, pageSize: 100 }); });
      expect(screen.queryByText('Устаревший фильтр')).toBeNull();
      view.unmount();
    });
  });

  describe('in-flight automation route transition', () => {
    async function pendingAutomation(taskCardWindowMode: 'legacy' | 'modern', realtime = false) {
      const setup = await renderJournal([createTask()], { settings: { taskCardWindowMode } });
      if (!realtime) setup.unmount();
      apiMock.getTaskJournalCaseAutomationCandidates.mockClear();
      apiMock.getTaskJournalCaseAutomationSummary.mockClear();
      const obsolete = [{ clientId: 10, clientName: 'Старый ответ автоконтроля', clientCaseId: 900,
        clientCaseTitle: 'Устаревшее дело', reason: 'Нет активной задачи', activeTaskCount: 0,
        completedTaskCount: 2, daysWithoutActiveTask: 20, isEscalated: true, lastTaskAt: todayAt(8),
        suggestedTitle: 'Старая подсказка', suggestedDescription: '', suggestedDueAt: todayAt(9), suggestedPriority: 'urgent' }];
      const summary = { totalCount: 99, readyCount: 99, criticalCount: 99, waitingCount: 0, pausedCount: 0 };
      let finishCandidates!: (value: typeof obsolete) => void;
      let finishSummary!: (value: typeof summary) => void;
      const signals: AbortSignal[] = [];
      apiMock.getTaskJournalCaseAutomationCandidates.mockImplementationOnce((_params, options) => {
        signals.push(options.signal);
        return new Promise<typeof obsolete>((resolve) => { finishCandidates = resolve; });
      });
      apiMock.getTaskJournalCaseAutomationSummary.mockImplementationOnce((_params, options) => {
        signals.push(options.signal);
        return new Promise<typeof summary>((resolve) => { finishSummary = resolve; });
      });
      const view = realtime ? setup : render(<TaskJournal />);
      if (realtime) await act(async () => { window.dispatchEvent(new Event(TASK_JOURNAL_REALTIME_EVENT)); });
      await waitFor(() => expect(signals).toHaveLength(2));
      return { view, signals, finish: async () => {
        await act(async () => { finishCandidates(obsolete); finishSummary(summary); });
      } };
    }

    it.each(['legacy', 'modern'] as const)('finishes an already started automation read without applying its obsolete response in the %s card', async (mode) => {
      const { view, signals, finish } = await pendingAutomation(mode);
      view.rerender(<TaskJournal routeTaskId={101} />);
      await screen.findByTestId(`task-${mode}-card-window`);
      expect(signals.every((signal) => !signal.aborted)).toBe(true);
      expect(apiMock.getTaskJournalCaseAutomationCandidates).toHaveBeenCalledTimes(1);
      view.rerender(<TaskJournal routeTaskId={null} />);
      await waitFor(() => expect(apiMock.getTaskJournalCaseAutomationCandidates).toHaveBeenCalledTimes(2));
      await finish();
      expect(screen.queryByText('Устаревшее дело')).toBeNull();
      expect(screen.queryByText('Старый ответ автоконтроля')).toBeNull();
      expect(apiMock.getTaskJournalCaseAutomationSummary).toHaveBeenCalledTimes(2);
    });

    it.each(['legacy', 'modern'] as const)('cancels retained automation on unmount from the %s card', async (mode) => {
      const { view, signals, finish } = await pendingAutomation(mode);
      view.rerender(<TaskJournal routeTaskId={101} />);
      await screen.findByTestId(`task-${mode}-card-window`);
      expect(signals.every((signal) => !signal.aborted)).toBe(true);
      view.unmount();
      expect(signals.every((signal) => signal.aborted)).toBe(true);
      await finish();
    });

    it.each(['legacy', 'modern'] as const)('retires a realtime automation refresh safely when opening the %s card', async (mode) => {
      const { view, signals, finish } = await pendingAutomation(mode, true);
      view.rerender(<TaskJournal routeTaskId={101} />);
      await screen.findByTestId(`task-${mode}-card-window`);
      expect(signals.every((signal) => !signal.aborted)).toBe(true);
      view.unmount();
      expect(signals.every((signal) => signal.aborted)).toBe(true);
      await finish();
    });

    it.each(['legacy', 'modern'] as const)('cancels retained automation when the employee changes in the %s card', async (mode) => {
      const { view, signals, finish } = await pendingAutomation(mode);
      view.rerender(<TaskJournal routeTaskId={101} />);
      await screen.findByTestId(`task-${mode}-card-window`);
      expect(signals.every((signal) => !signal.aborted)).toBe(true);
      authState.user = { ...authState.user, id: 8 };
      view.rerender(<TaskJournal routeTaskId={101} />);
      await waitFor(() => expect(signals.every((signal) => signal.aborted)).toBe(true));
      await finish();
      view.rerender(<TaskJournal routeTaskId={null} />);
      await waitFor(() => expect(apiMock.getTaskJournalCaseAutomationCandidates).toHaveBeenCalledTimes(2));
      expect(screen.queryByText('Устаревшее дело')).toBeNull();
    });

    it.each(['legacy', 'modern'] as const)('still cancels obsolete automation when its filter changes in the %s journal', async (mode) => {
      const { view, signals, finish } = await pendingAutomation(mode);
      fireEvent.click(screen.getByRole('button', { name: 'Пора создать: 0' }));
      await waitFor(() => expect(signals.every((signal) => signal.aborted)).toBe(true));
      await waitFor(() => expect(apiMock.getTaskJournalCaseAutomationCandidates).toHaveBeenCalledTimes(2));
      await finish();
      expect(screen.queryByText('Устаревшее дело')).toBeNull();
      view.unmount();
    });
  });

  it.each(['legacy', 'modern'] as const)('does not refresh the hidden journal after routed creation with a pending file in the %s card', async (taskCardWindowMode) => {
    await renderJournal([], { settings: { taskCardWindowMode }, waitForTitle: null, withRoute: true });
    await openNewTaskFormAsGlobalInternal();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.change(within(card).getByTestId('task-title-input'), { target: { value: 'Задача с обязательными копиями' } });
    fireEvent.change(card.querySelector('input[type="file"]') as HTMLInputElement, { target: {
      files: [new File(['content'], 'очередь.pdf', { type: 'application/pdf' })],
    } });
    apiMock.uploadTaskJournalFile.mockRejectedValueOnce(new Error('HTTP 503'));
    apiMock.getTaskFileUploadKey.mockReturnValue('created-pending');
    apiMock.getTaskFileUploadStates.mockResolvedValue([{ uploadKey: 'created-pending', fileName: 'очередь.pdf',
      status: 'waiting', fileId: null, message: 'Ожидание обязательных копий.' }]);
    vi.clearAllMocks();
    fireEvent.click(within(card).getByRole('button', { name: 'Создать задачу' }));
    await waitFor(() => expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1));
    const saved = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    await waitFor(() => expect(within(saved).queryByRole('button', { name: 'Создать задачу' })).toBeNull());
    expect(await within(saved).findByText('Ожидание обязательных копий.')).toBeTruthy();
    expect(screen.queryByTestId('task-table')).toBeNull();
    expect(apiMock.getTaskJournalItems).not.toHaveBeenCalled();
    expect(apiMock.getTaskJournalSummary).not.toHaveBeenCalled();
    expect(apiMock.getTaskJournalCaseAutomationCandidates).not.toHaveBeenCalled();
  });

  it.each(['legacy', 'modern'] as const)('ignores obsolete card metadata after an employee change while returning to the journal in the %s mode', async (taskCardWindowMode) => {
    const setup = await renderJournal([createTask()], { settings: { taskCardWindowMode } });
    setup.unmount();
    const currentSettings = await apiMock.getTaskJournalSettings();
    vi.clearAllMocks();
    let finishOldSettings!: (settings: TaskJournalSettingsDto) => void;
    apiMock.getTaskJournalSettings.mockImplementationOnce(() => new Promise<TaskJournalSettingsDto>((resolve) => { finishOldSettings = resolve; }));
    const view = render(<TaskJournal routeTaskId={101} />);
    await screen.findByTestId('task-card-settings-gate');
    authState.user = { ...authState.user, id: 8 };
    view.rerender(<TaskJournal routeTaskId={null} />);
    await screen.findByTestId('task-table');
    await waitFor(() => expect(apiMock.getTaskJournalSettings).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(apiMock.getTaskJournalCaseAutomationSummary).toHaveBeenCalledTimes(1));
    await act(async () => finishOldSettings({ ...currentSettings, taskCardWindowMode: taskCardWindowMode === 'modern' ? 'legacy' : 'modern' }));
    await openFirstTaskForEdit();
    expect(await screen.findByTestId(`task-${taskCardWindowMode}-card-window`)).toBeTruthy();
    expect(screen.queryByTestId(`task-${taskCardWindowMode === 'modern' ? 'legacy' : 'modern'}-card-window`)).toBeNull();
    expect(apiMock.getTaskJournalCaseAutomationCandidates).toHaveBeenCalledTimes(1);
  });

  it.each(['legacy', 'modern'] as const)('shares only the pending routed task read in the %s card', async (taskCardWindowMode) => {
    const view = await renderJournal([], { settings: { taskCardWindowMode }, waitForTitle: null });
    let finishRead!: (task: TaskJournalItem) => void;
    apiMock.getTaskJournalItem.mockImplementation(() => new Promise<TaskJournalItem>((resolve) => { finishRead = resolve; }));

    view.rerender(<StrictMode><TaskJournal routeTaskId={101} /></StrictMode>);
    await waitFor(() => expect(apiMock.getTaskJournalItem).toHaveBeenCalled());
    expect(apiMock.getTaskJournalItem).toHaveBeenCalledTimes(1);
    await act(async () => finishRead(createTask({ status: 'done', title: 'Завершённая задача из ссылки' })));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    expect(within(card).getByDisplayValue('Завершённая задача из ссылки')).toBeTruthy();
    expect(apiMock.getTaskJournalItem).toHaveBeenCalledTimes(1);
  });

  it.each([
    { taskCardWindowMode: 'legacy' as const, change: 'task' },
    { taskCardWindowMode: 'modern' as const, change: 'task' },
    { taskCardWindowMode: 'legacy' as const, change: 'user' },
    { taskCardWindowMode: 'modern' as const, change: 'user' },
  ])('does not reuse or apply a pending routed read after a $change change in the $taskCardWindowMode card', async ({ taskCardWindowMode, change }) => {
    const view = await renderJournal([], { settings: { taskCardWindowMode }, waitForTitle: null });
    const reads: { taskId: number; finish: (task: TaskJournalItem) => void }[] = [];
    apiMock.getTaskJournalItem.mockImplementation((taskId: number) => new Promise<TaskJournalItem>((finish) => { reads.push({ taskId, finish }); }));
    view.rerender(<StrictMode><TaskJournal routeTaskId={101} /></StrictMode>);
    await waitFor(() => expect(reads).toHaveLength(1));

    if (change === 'user') authState.user = { ...authState.user, id: 8 };
    const nextId = change === 'task' ? 102 : 101;
    view.rerender(<StrictMode><TaskJournal routeTaskId={nextId} /></StrictMode>);
    await waitFor(() => expect(reads).toHaveLength(2));
    expect(reads.map((read) => read.taskId)).toEqual([101, nextId]);
    await act(async () => reads[0].finish(createTask({ status: 'done', title: 'Прежняя карточка' })));
    expect(screen.queryByDisplayValue('Прежняя карточка')).toBeNull();
    expect(screen.queryByTestId(`task-${taskCardWindowMode}-card-window`)).toBeNull();

    await act(async () => reads[1].finish(createTask({ id: nextId, status: 'done', title: 'Текущая карточка' })));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    expect(within(card).getByDisplayValue('Текущая карточка')).toBeTruthy();
    expect(screen.queryByDisplayValue('Прежняя карточка')).toBeNull();
    expect(apiMock.getTaskJournalItem).toHaveBeenCalledTimes(2);
  });

  it('opens an existing task as a full page when the route is synced', async () => {
    await renderJournal(undefined, { withRoute: true });

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);

    await screen.findByRole('heading', { name: 'Просмотр задачи' });

    expect(screen.queryByTestId('task-table')).toBeNull();
    expect(screen.getByRole('button', { name: 'Журнал задач' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Журнал задач' }));

    await screen.findByTestId('task-table');
  });

  it('refreshes once when another task journal instance reports a change', async () => {
    await renderJournal();
    apiMock.getTaskJournalItems.mockClear();
    apiMock.getTaskJournalCaseAutomationCandidates.mockClear();

    window.dispatchEvent(new CustomEvent(TASK_JOURNAL_EVENT, {
      detail: { source: {} },
    }));

    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(apiMock.getTaskJournalCaseAutomationCandidates).toHaveBeenCalledTimes(1));
  });

  it('coalesces realtime task notifications into one journal and automation refresh', async () => {
    await renderJournal();
    apiMock.getTaskJournalItems.mockClear();
    apiMock.getTaskJournalCaseAutomationCandidates.mockClear();

    const detail = {
      source: 'realtime',
      reason: 'created',
      notificationType: 'taskChanged',
      objectType: 'task',
      objectId: 101,
      taskJournalItemId: 101,
    };
    window.dispatchEvent(new CustomEvent(USER_NOTIFICATIONS_EVENT, { detail }));
    window.dispatchEvent(new CustomEvent(TASK_JOURNAL_REALTIME_EVENT));
    window.dispatchEvent(new CustomEvent(USER_NOTIFICATIONS_EVENT, { detail }));

    await waitFor(() => expect(apiMock.getTaskJournalItems).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(apiMock.getTaskJournalCaseAutomationCandidates).toHaveBeenCalledTimes(1));
  });
});
