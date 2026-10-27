import { setupTaskJournalTests, apiMock, toastMock, offlineCalendarMock, authState, legalEntitiesState, todayAt, createTask, renderJournal, openFirstTaskForEdit, openNewTaskFormAsGlobalInternal, continueTaskWizardForClient } from './TaskJournal.test-support';
import { act, cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { LegalEntitySummary } from '../../types';
import { TaskJournal } from './TaskJournal';

describe('TaskJournal', () => {
  setupTaskJournalTests();

  it.each(['legacy', 'modern'] as const)('closes the %s task card when task access is revoked', async (mode) => {
    await renderJournal([createTask({ title: 'Закрытая задача' })], { settings: { taskCardWindowMode: mode } });
    await openFirstTaskForEdit();
    expect(await screen.findByTestId(`task-${mode}-card-window`)).toBeTruthy();
    apiMock.getTaskJournalItems.mockRejectedValue(Object.assign(new Error('Denied'), { status: 403 }));
    fireEvent(window, new Event('pp.task_journal.changed'));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith(expect.stringContaining('Нет доступа'), 'Журнал задач'));
    expect(screen.queryByTestId(`task-${mode}-card-window`)).toBeNull();
  });

  it.each(['legacy', 'modern'] as const)('closes the %s task card when another tab revokes access', async (mode) => {
    await renderJournal([createTask()], { settings: { taskCardWindowMode: mode } });
    await openFirstTaskForEdit();
    expect(await screen.findByTestId(`task-${mode}-card-window`)).toBeTruthy();
    fireEvent(window, new StorageEvent('storage', { key: 'payplanner-offline-calendar-denied:7', newValue: 'revoked', storageArea: localStorage }));
    expect(screen.queryByTestId(`task-${mode}-card-window`)).toBeNull();
  });

  it.each(['legacy', 'modern'] as const)('keeps the %s task card open on a temporary network failure', async (mode) => {
    await renderJournal([createTask()], { settings: { taskCardWindowMode: mode } });
    await openFirstTaskForEdit();
    expect(await screen.findByTestId(`task-${mode}-card-window`)).toBeTruthy();
    apiMock.getTaskJournalItems.mockRejectedValue(new Error('Network unavailable'));
    fireEvent(window, new Event('pp.task_journal.changed'));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith('Не удалось загрузить журнал задач.', 'Журнал задач'));
    expect(screen.getByTestId(`task-${mode}-card-window`)).toBeTruthy();
  });

  it.each([
    ['legacy', 401], ['legacy', 403], ['legacy', 404],
    ['modern', 401], ['modern', 403], ['modern', 404],
  ] as const)('closes routed %s task card when refresh returns %s', async (mode, status) => {
    await renderJournal([createTask()], { settings: { taskCardWindowMode: mode }, withRoute: true });
    await openFirstTaskForEdit();
    expect(await screen.findByTestId(`task-${mode}-card-window`)).toBeTruthy();
    apiMock.getTaskJournalItem.mockRejectedValue(Object.assign(new Error('Denied'), { status }));
    fireEvent(window, new Event('pp.task_journal.changed'));
    await waitFor(() => expect(screen.queryByTestId(`task-${mode}-card-window`)).toBeNull());
    expect(offlineCalendarMock.revokeOfflineCalendarSnapshot).toHaveBeenCalledWith(7);
  });

  it.each(['legacy', 'modern'] as const)('keeps routed %s task card open on refresh network failure', async (mode) => {
    await renderJournal([createTask()], { settings: { taskCardWindowMode: mode }, withRoute: true });
    await openFirstTaskForEdit();
    expect(await screen.findByTestId(`task-${mode}-card-window`)).toBeTruthy();
    apiMock.getTaskJournalItem.mockClear();
    apiMock.getTaskJournalItem.mockRejectedValue(new Error('Network unavailable'));
    fireEvent(window, new Event('pp.task_journal.changed'));
    await waitFor(() => expect(apiMock.getTaskJournalItem).toHaveBeenCalledWith(101));
    expect(screen.getByTestId(`task-${mode}-card-window`)).toBeTruthy();
  });

  it('ignores a late access denial after leaving the routed task', async () => {
    await renderJournal([createTask()], { settings: { taskCardWindowMode: 'modern' }, withRoute: true });
    await openFirstTaskForEdit();
    const card = await screen.findByTestId('task-modern-card-window');
    apiMock.getTaskJournalItem.mockClear();
    let reject!: (reason: Error) => void;
    apiMock.getTaskJournalItem.mockReturnValueOnce(new Promise((_, fail) => { reject = fail; }));
    fireEvent(window, new Event('pp.task_journal.changed'));
    await waitFor(() => expect(apiMock.getTaskJournalItem).toHaveBeenCalledWith(101));
    fireEvent.click(within(card).getByRole('button', { name: 'К журналу' }));
    await waitFor(() => expect(screen.queryByTestId('task-modern-card-window')).toBeNull());
    await act(async () => { reject(Object.assign(new Error('Denied'), { status: 403 })); });
    expect(toastMock.error).not.toHaveBeenCalledWith('Задача недоступна или была удалена.', 'Журнал задач');
  });

  it('ignores a late denial for the previous task after opening another routed task', async () => {
    await renderJournal([createTask()], { settings: { taskCardWindowMode: 'modern' }, withRoute: true, nextRouteTaskId: 102 });
    await openFirstTaskForEdit();
    await screen.findByTestId('task-modern-card-window');
    let reject!: (reason: Error) => void;
    apiMock.getTaskJournalItem.mockClear();
    apiMock.getTaskJournalItem.mockReturnValueOnce(new Promise((_, fail) => { reject = fail; }));
    apiMock.getTaskJournalItem.mockResolvedValue(createTask({ id: 102, title: 'Другая задача' }));
    fireEvent(window, new Event('pp.task_journal.changed'));
    await waitFor(() => expect(apiMock.getTaskJournalItem).toHaveBeenCalledWith(101));
    fireEvent.click(screen.getByRole('button', { name: 'Перейти в другую задачу' }));
    await waitFor(() => expect(apiMock.getTaskJournalItem).toHaveBeenCalledWith(102));
    await act(async () => { reject(Object.assign(new Error('Denied'), { status: 403 })); });
    expect(screen.getByTestId('task-modern-card-window')).toBeTruthy();
    expect(toastMock.error).not.toHaveBeenCalledWith('Задача недоступна или была удалена.', 'Журнал задач');
  });

  it.each(['legacy', 'modern'] as const)('rechecks routed %s task when current role permissions change', async (mode) => {
    authState.user.role = { id: 42 };
    await renderJournal([createTask()], { settings: { taskCardWindowMode: mode }, withRoute: true });
    await openFirstTaskForEdit();
    expect(await screen.findByTestId(`task-${mode}-card-window`)).toBeTruthy();
    apiMock.getTaskJournalItem.mockRejectedValue(Object.assign(new Error('Denied'), { status: 403 }));
    fireEvent(window, new CustomEvent('pp.permissions.changed', { detail: { roleId: 42 } }));
    await waitFor(() => expect(screen.queryByTestId(`task-${mode}-card-window`)).toBeNull());
  });

  it.each(['legacy', 'modern'] as const)('closes routed %s task after department access change', async (mode) => {
    await renderJournal([createTask()], { settings: { taskCardWindowMode: mode }, withRoute: true });
    await openFirstTaskForEdit();
    expect(await screen.findByTestId(`task-${mode}-card-window`)).toBeTruthy();
    apiMock.getTaskJournalItem.mockRejectedValue(Object.assign(new Error('Denied'), { status: 403 }));
    fireEvent(window, new CustomEvent('payplanner:user-notifications-changed', { detail: { source: 'realtime', reason: 'user-role-changed' } }));
    await waitFor(() => expect(screen.queryByTestId(`task-${mode}-card-window`)).toBeNull());
  });

  it.each(['legacy', 'modern'] as const)('closes journal %s task after department access change even when list refresh succeeds', async (mode) => {
    await renderJournal([createTask()], { settings: { taskCardWindowMode: mode } });
    await openFirstTaskForEdit();
    expect(await screen.findByTestId(`task-${mode}-card-window`)).toBeTruthy();
    apiMock.getTaskJournalItem.mockRejectedValue(Object.assign(new Error('Denied'), { status: 403 }));
    fireEvent(window, new CustomEvent('payplanner:user-notifications-changed', { detail: { source: 'realtime', reason: 'user-role-changed' } }));
    await waitFor(() => expect(screen.queryByTestId(`task-${mode}-card-window`)).toBeNull());
    expect(offlineCalendarMock.revokeOfflineCalendarSnapshot).toHaveBeenCalledWith(7);
  });

  it.each(['legacy', 'modern'] as const)('keeps journal %s task draft on a temporary access recheck failure', async (mode) => {
    await renderJournal([createTask()], { settings: { taskCardWindowMode: mode } });
    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${mode}-card-window`);
    const title = within(card).getByTestId('task-title-input') as HTMLTextAreaElement;
    fireEvent.change(title, { target: { value: 'Несохранённое название' } });
    apiMock.getTaskJournalItem.mockClear();
    apiMock.getTaskJournalItem.mockRejectedValue(new Error('Network unavailable'));
    fireEvent(window, new CustomEvent('payplanner:user-notifications-changed', { detail: { source: 'realtime', reason: 'user-role-changed' } }));
    await waitFor(() => expect(apiMock.getTaskJournalItem).toHaveBeenCalledWith(101));
    expect(screen.getByTestId(`task-${mode}-card-window`)).toBeTruthy();
    expect(title.value).toBe('Несохранённое название');
  });

  it.each(['legacy', 'modern'] as const)('closes journal %s task when current role permissions are revoked', async (mode) => {
    authState.user.role = { id: 42 };
    await renderJournal([createTask()], { settings: { taskCardWindowMode: mode } });
    await openFirstTaskForEdit();
    expect(await screen.findByTestId(`task-${mode}-card-window`)).toBeTruthy();
    apiMock.getTaskJournalItem.mockRejectedValue(Object.assign(new Error('Denied'), { status: 403 }));
    fireEvent(window, new CustomEvent('pp.permissions.changed', { detail: { roleId: 42 } }));
    await waitFor(() => expect(screen.queryByTestId(`task-${mode}-card-window`)).toBeNull());
  });

  it('hides a journal task immediately when the user changes', async () => {
    const rendered = await renderJournal([createTask()], { settings: { taskCardWindowMode: 'modern' } });
    await openFirstTaskForEdit();
    await screen.findByTestId('task-modern-card-window');
    authState.user = { id: 8, fullName: 'Другой сотрудник', email: 'other@local' };
    rendered.rerender(<TaskJournal />);
    expect(screen.queryByTestId('task-modern-card-window')).toBeNull();
  });

  it('ignores a stale access denial after a newer journal recheck succeeds', async () => {
    await renderJournal([createTask()], { settings: { taskCardWindowMode: 'modern' } });
    await openFirstTaskForEdit();
    await screen.findByTestId('task-modern-card-window');
    let reject!: (reason: Error) => void;
    apiMock.getTaskJournalItem.mockClear();
    apiMock.getTaskJournalItem.mockReturnValueOnce(new Promise((_, fail) => { reject = fail; }));
    apiMock.getTaskJournalItem.mockResolvedValue(createTask());
    const signal = () => fireEvent(window, new CustomEvent('payplanner:user-notifications-changed', { detail: { source: 'realtime', reason: 'user-role-changed' } }));
    signal();
    await waitFor(() => expect(apiMock.getTaskJournalItem).toHaveBeenCalledTimes(1));
    signal();
    await waitFor(() => expect(apiMock.getTaskJournalItem).toHaveBeenCalledTimes(2));
    await act(async () => { reject(Object.assign(new Error('Denied'), { status: 403 })); });
    expect(screen.getByTestId('task-modern-card-window')).toBeTruthy();
    expect(toastMock.error).not.toHaveBeenCalledWith('Задача недоступна или была удалена.', 'Журнал задач');
  });

  it('does not recheck a routed task for an unrelated role', async () => {
    authState.user.role = { id: 42 };
    await renderJournal([createTask()], { settings: { taskCardWindowMode: 'modern' }, withRoute: true });
    await openFirstTaskForEdit();
    await screen.findByTestId('task-modern-card-window');
    apiMock.getTaskJournalItem.mockClear();
    fireEvent(window, new CustomEvent('pp.permissions.changed', { detail: { roleId: 99 } }));
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 350)); });
    expect(apiMock.getTaskJournalItem).not.toHaveBeenCalled();
  });

  it('invalidates a previous calendar snapshot after permissions change while a routed task is open', async () => {
    authState.user.role = { id: 42 };
    await renderJournal([createTask()], { settings: { taskCardWindowMode: 'modern' }, withRoute: true });
    await openFirstTaskForEdit();
    await screen.findByTestId('task-modern-card-window');
    offlineCalendarMock.revokeOfflineCalendarSnapshot.mockClear();
    apiMock.getTaskJournalItem.mockResolvedValue(createTask());
    fireEvent(window, new CustomEvent('pp.permissions.changed', { detail: { roleId: 42 } }));
    await waitFor(() => expect(offlineCalendarMock.revokeOfflineCalendarSnapshot).toHaveBeenCalledWith(7));
    expect(screen.getByTestId('task-modern-card-window')).toBeTruthy();
  });

  it('rechecks a routed task when the assigned role changes without a permissions event', async () => {
    authState.user.role = { id: 42 };
    await renderJournal([createTask()], { settings: { taskCardWindowMode: 'modern' }, withRoute: true, withRoleRerender: true });
    await openFirstTaskForEdit();
    await screen.findByTestId('task-modern-card-window');
    apiMock.getTaskJournalItem.mockRejectedValue(Object.assign(new Error('Denied'), { status: 403 }));
    authState.user.role = { id: 43 };
    fireEvent.click(screen.getByRole('button', { name: 'Обновить роль для теста' }));
    await waitFor(() => expect(screen.queryByTestId('task-modern-card-window')).toBeNull());
  });

  it('hides the previous user’s routed task immediately when the user changes', async () => {
    authState.user.role = { id: 42 };
    await renderJournal([createTask()], { settings: { taskCardWindowMode: 'modern' }, withRoute: true, withRoleRerender: true });
    await openFirstTaskForEdit();
    await screen.findByTestId('task-modern-card-window');
    apiMock.getTaskJournalItem.mockReturnValue(new Promise(() => undefined));
    authState.user = { id: 8, fullName: 'Другой сотрудник', email: 'other@local', role: { id: 42 } };
    fireEvent.click(screen.getByRole('button', { name: 'Обновить роль для теста' }));
    expect(screen.queryByTestId('task-modern-card-window')).toBeNull();
    await waitFor(() => expect(apiMock.getTaskJournalItem).toHaveBeenCalledWith(101));
  });

  it('keeps a long modern task title editable and saves it as one line', async () => {
    const title = 'Очень длинное название задачи для проверки отображения на узком экране';
    await renderJournal([createTask({ title })], { settings: { taskCardWindowMode: 'modern' } });
    await openFirstTaskForEdit();
    const card = await screen.findByTestId('task-modern-card-window');
    const field = within(card).getByTestId('task-title-input') as HTMLTextAreaElement;
    expect(field.value).toBe(title);
    fireEvent.change(field, { target: { value: `${title}\nпродолжение` } });
    expect(field.value).toBe(`${title}продолжение`);
    fireEvent.keyDown(field, { key: 'Enter' });
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({ title: `${title}продолжение` })));
  });

  it('focuses the legacy task title from the actions menu', async () => {
    await renderJournal([createTask()], { settings: { taskCardWindowMode: 'legacy' } });
    await openFirstTaskForEdit();
    const card = await screen.findByTestId('task-legacy-card-window');
    fireEvent.click(within(card).getByRole('button', { name: 'Действия с задачей' }));
    fireEvent.click(within(card).getByRole('button', { name: /^Редактировать$/ }));
    expect(document.activeElement).toBe(within(card).getByTestId('task-title-input'));
  });

  it.each(['legacy', 'modern'] as const)('uses the shared task chat in the %s card selected by global settings', async (mode) => {
    await renderJournal([createTask()], { settings: { taskCardWindowMode: mode } });
    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${mode}-card-window`);
    expect(screen.queryByTestId(`task-${mode === 'legacy' ? 'modern' : 'legacy'}-card-window`)).toBeNull();
    expect(within(card).queryByTestId('task-chat-panel')).toBeNull();
    const openChat = () => fireEvent.click(mode === 'modern'
      ? within(card).getByRole('tab', { name: 'Чат' })
      : within(card).getByRole('button', { name: /Чат задачи/ }));
    openChat();
    expect((await within(card).findByTestId('task-chat-panel')).textContent).toBe('Чат задачи 101');
    if (mode === 'modern') fireEvent.click(within(card).getByRole('tab', { name: /Комментарии/ }));
    else openChat();
    expect(within(card).queryByTestId('task-chat-panel')).toBeNull();
    openChat();
    expect((await within(card).findByTestId('task-chat-panel')).textContent).toBe('Чат задачи 101');
  });


  it.each([
    ['legacy', 'assignees', 'pending'], ['modern', 'assignees', 'pending'],
    ['legacy', 'automation', 'pending'], ['modern', 'automation', 'pending'],
    ['legacy', 'assignees', 'failed'], ['modern', 'assignees', 'failed'],
    ['legacy', 'automation', 'failed'], ['modern', 'automation', 'failed'],
  ] as const)('ancillary regression: %s card with %s %s', async (mode, dependency, outcome) => {
    let release: (() => void) | undefined;
    const delayed = new Promise<never>((_resolve, reject) => {
      release = () => reject(new Error('Synthetic ancillary metadata unavailable'));
    });
    const dependencyMock = dependency === 'assignees'
      ? apiMock.getTaskJournalAssignees : apiMock.getTaskJournalCaseAutomationCandidates;
    dependencyMock.mockImplementationOnce(() => delayed);
    if (outcome === 'failed') release?.();
    try {
      await renderJournal([createTask()], { settings: { taskCardWindowMode: mode } });
      fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
      expect(await screen.findByTestId(`task-${mode}-card-window`)).toBeTruthy();
      expect(screen.queryByTestId(`task-${mode === 'modern' ? 'legacy' : 'modern'}-card-window`)).toBeNull();
      expect(apiMock.getTaskJournalSettings).toHaveBeenCalledTimes(1);
      expect(apiMock.getTaskJournalAssignees).toHaveBeenCalledTimes(1);
    } finally {
      if (outcome === 'pending') release?.();
    }
  });
  it.each([
    ['legacy', false], ['modern', false], ['legacy', true], ['modern', true],
  ] as const)('settings readiness: %s card stays unopened until settings arrive, routed=%s', async (mode, withRoute) => {
    let release: (() => void) | undefined;
    const settingsDelay = new Promise<void>(resolve => { release = resolve; });
    try {
      await renderJournal([createTask()], { settings: { taskCardWindowMode: mode }, settingsDelay, withRoute });
      fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
      expect(screen.queryByTestId('task-legacy-card-window')).toBeNull();
      expect(screen.queryByTestId('task-modern-card-window')).toBeNull();
      expect(await screen.findByText('Загружаем настройки карточки задачи...')).toBeTruthy();
      release?.();
      expect(await screen.findByTestId(`task-${mode}-card-window`)).toBeTruthy();
      expect(apiMock.getTaskJournalSettings).toHaveBeenCalledTimes(1);
    } finally {
      release?.();
    }
  });
  it.each([
    ['legacy', false], ['modern', false], ['legacy', true], ['modern', true],
  ] as const)('settings retry: %s card never uses defaults after a settings failure, routed=%s', async (mode, withRoute) => {
    apiMock.getTaskJournalSettings.mockRejectedValueOnce(new Error('Synthetic initial settings failure'));
    await renderJournal([createTask()], { settings: { taskCardWindowMode: mode }, withRoute });
    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    expect(await screen.findByText('Не удалось загрузить настройки карточки задачи.')).toBeTruthy();
    expect(screen.queryByTestId('task-legacy-card-window')).toBeNull();
    expect(screen.queryByTestId('task-modern-card-window')).toBeNull();
    fireEvent.click(within(screen.getByTestId('task-card-settings-gate')).getByRole('button', { name: 'Повторить загрузку' }));
    expect(await screen.findByTestId(`task-${mode}-card-window`)).toBeTruthy();
    expect(apiMock.getTaskJournalSettings).toHaveBeenCalledTimes(2);
  });

  it.each([
    ['legacy', false], ['modern', false], ['legacy', true], ['modern', true],
  ] as const)('settings close: %s card stays closed when delayed settings arrive, routed=%s', async (mode, withRoute) => {
    let release: (() => void) | undefined;
    const settingsDelay = new Promise<void>(resolve => { release = resolve; });
    try {
      await renderJournal([createTask()], { settings: { taskCardWindowMode: mode }, settingsDelay, withRoute });
      fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
      const gate = await screen.findByTestId('task-card-settings-gate');
      fireEvent.click(within(gate).getByRole('button', { name: withRoute ? 'К журналу' : 'Закрыть' }));
      await act(async () => { release?.(); });
      expect(screen.queryByTestId('task-card-settings-gate')).toBeNull();
      expect(screen.queryByTestId('task-legacy-card-window')).toBeNull();
      expect(screen.queryByTestId('task-modern-card-window')).toBeNull();
    } finally { release?.(); }
  });

  it.each(['legacy', 'modern'] as const)('settings focus: %s waiting dialog focuses close and supports Escape', async mode => {
    let release: (() => void) | undefined;
    const settingsDelay = new Promise<void>(resolve => { release = resolve; });
    try {
      await renderJournal([createTask()], { settings: { taskCardWindowMode: mode }, settingsDelay });
      fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
      const gate = await screen.findByRole('dialog', { name: 'Открытие карточки задачи' });
      expect(document.activeElement).toBe(within(gate).getByRole('button', { name: 'Закрыть' }));
      fireEvent.keyDown(gate, { key: 'Escape' });
      await act(async () => { release?.(); });
      expect(screen.queryByTestId('task-card-settings-gate')).toBeNull();
      expect(screen.queryByTestId('task-legacy-card-window')).toBeNull();
      expect(screen.queryByTestId('task-modern-card-window')).toBeNull();
    } finally { release?.(); }
  });
  it.each(['legacy', 'modern'] as const)('settings retry focus: %s dialog keeps keyboard focus while retry is pending', async mode => {
    let release: (() => void) | undefined;
    const settingsDelay = new Promise<void>(resolve => { release = resolve; });
    apiMock.getTaskJournalSettings.mockRejectedValueOnce(new Error('Synthetic initial settings failure'));
    try {
      await renderJournal([createTask()], { settings: { taskCardWindowMode: mode }, settingsDelay });
      fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
      const gate = await screen.findByRole('dialog', { name: 'Открытие карточки задачи' });
      const retry = within(gate).getByRole('button', { name: 'Повторить загрузку' });
      retry.focus();
      fireEvent.click(retry);
      expect(await within(gate).findByText('Загружаем настройки карточки задачи...')).toBeTruthy();
      expect(document.activeElement).toBe(within(gate).getByRole('button', { name: 'Закрыть' }));
      fireEvent.keyDown(document.activeElement!, { key: 'Escape' });
      await act(async () => { release?.(); });
      expect(screen.queryByTestId('task-card-settings-gate')).toBeNull();
      expect(screen.queryByTestId('task-legacy-card-window')).toBeNull();
      expect(screen.queryByTestId('task-modern-card-window')).toBeNull();
    } finally { release?.(); }
  });
  it('opens the modern task card when the global setting is enabled', async () => {
    await renderJournal([createTask()], {
      settings: { taskCardWindowMode: 'modern' },
    });

    await openFirstTaskForEdit();

    expect(await screen.findByTestId('task-modern-card-window')).toBeTruthy();
    expect(screen.queryByTestId('task-legacy-card-window')).toBeNull();
  });

  it('switches between compact and expanded modern card layouts and remembers the choice', async () => {
    await renderJournal([createTask()], {
      settings: { taskCardWindowMode: 'modern' },
    });

    await openFirstTaskForEdit();

    let card = await screen.findByTestId('task-modern-card-window');
    expect(within(card).getByTestId('task-modern-layout').getAttribute('data-layout-mode')).toBe('compact');

    fireEvent.click(within(card).getByRole('button', { name: 'Еще' }));
    const expandedLayoutButton = within(card).getByRole('button', { name: 'Расширенный вид' });
    expect(expandedLayoutButton.getAttribute('aria-pressed')).toBe('false');
    fireEvent.click(expandedLayoutButton);

    expect(within(card).getByTestId('task-modern-layout').getAttribute('data-layout-mode')).toBe('expanded');
    expect(window.localStorage.getItem('pp.task_journal.card_layout.v1.user:7')).toBe('expanded');

    fireEvent.click(within(card).getByRole('button', { name: 'Отмена' }));
    await openFirstTaskForEdit();
    card = await screen.findByTestId('task-modern-card-window');
    expect(within(card).getByTestId('task-modern-layout').getAttribute('data-layout-mode')).toBe('expanded');
  });

  it('preserves unsaved fields when switching modern card layouts', async () => {
    await renderJournal([createTask()], { settings: { taskCardWindowMode: 'modern' } });
    await openFirstTaskForEdit();
    const card = await screen.findByTestId('task-modern-card-window');
    const title = within(card).getByTestId('task-title-input') as HTMLTextAreaElement;
    const description = within(card).getByTestId('task-description-field') as HTMLTextAreaElement;
    fireEvent.change(title, { target: { value: 'Несохранённое название' } });
    fireEvent.change(description, { target: { value: 'Несохранённое описание' } });
    fireEvent.click(within(card).getByRole('button', { name: 'Еще' }));
    fireEvent.click(within(card).getByRole('button', { name: 'Расширенный вид' }));
    expect(title.value).toBe('Несохранённое название');
    expect(description.value).toBe('Несохранённое описание');
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
    fireEvent.click(within(card).getByRole('button', { name: 'Еще' }));
    fireEvent.click(within(card).getByRole('button', { name: 'Компактный вид' }));
    expect(title.value).toBe('Несохранённое название');
    expect(description.value).toBe('Несохранённое описание');
    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledWith(101, expect.objectContaining({
      title: 'Несохранённое название', description: 'Несохранённое описание',
    })));
  });

  it.each(['legacy', 'modern'] as const)('keeps the %s card draft after save failure and sends it on retry', async (taskCardWindowMode) => {
    await renderJournal([createTask()], { settings: { taskCardWindowMode } });
    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const title = within(card).getByTestId('task-title-input') as HTMLInputElement | HTMLTextAreaElement;
    const description = within(card).getByTestId('task-description-field') as HTMLTextAreaElement;
    fireEvent.change(title, { target: { value: 'Название после ошибки' } });
    fireEvent.change(description, { target: { value: 'Описание после ошибки' } });
    apiMock.updateTaskJournalItem.mockRejectedValueOnce(new Error('Ошибка сохранения'));
    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalled());
    expect(title.value).toBe('Название после ошибки');
    expect(description.value).toBe('Описание после ошибки');
    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(apiMock.updateTaskJournalItem).toHaveBeenCalledTimes(2));
    expect(apiMock.updateTaskJournalItem).toHaveBeenLastCalledWith(101, expect.objectContaining({
      title: 'Название после ошибки', description: 'Описание после ошибки',
    }));
  });

  it.each(['legacy', 'modern'] as const)('restores pending server uploads in the %s task card', async (taskCardWindowMode) => {
    apiMock.getTaskFileUploadStates.mockResolvedValue([{ uploadKey: 'pending-1', fileName: 'ожидающий.pdf',
      status: 'waiting', fileId: null, message: 'Система завершит загрузку автоматически.' }]);
    await renderJournal([createTask()], { settings: { taskCardWindowMode } });
    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    await waitFor(() => expect(within(card).getByRole('region', { name: 'Состояние ваших загрузок' })).toBeTruthy());
    expect(within(card).getByText('ожидающий.pdf')).toBeTruthy();
    expect(within(card).getByText('Система завершит загрузку автоматически.')).toBeTruthy();
    expect(apiMock.getTaskFileUploadPage).toHaveBeenCalledWith(101, expect.any(AbortSignal), undefined);
    cleanup();
  });

  it.each(['legacy', 'modern'] as const)('refreshes published files without a second result toast in the %s card', async (taskCardWindowMode) => {
    apiMock.getTaskFileUploadStates.mockResolvedValue([{ uploadKey: 'pending-1', fileName: 'готовый.pdf',
      status: 'waiting', fileId: null, message: 'Ожидание загрузки.' }]);
    await renderJournal([createTask()], { settings: { taskCardWindowMode } });
    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    expect(await within(card).findByText('Ожидание загрузки.')).toBeTruthy();
    const readsBeforePublication = apiMock.getTaskJournalFiles.mock.calls.length;
    apiMock.getTaskJournalFiles.mockResolvedValue([{ id: 601, taskJournalItemId: 101, documentKey: 'doc-1', version: 1,
      originalFileName: 'готовый.pdf', contentType: 'application/pdf', sizeBytes: 9,
      uploadedByName: 'Исполнитель', uploadedAt: todayAt(10), isLatestVersion: true }]);
    apiMock.getTaskFileUploadStates.mockResolvedValue([{ uploadKey: 'pending-1', fileName: 'готовый.pdf',
      status: 'uploaded', fileId: 601, message: 'Файл прикреплён к задаче.' }]);
    await waitFor(() => expect(within(card).getByText('Файл прикреплён к задаче.')).toBeTruthy(), { timeout: 7000 });
    expect(apiMock.getTaskJournalFiles.mock.calls.length).toBeGreaterThan(readsBeforePublication);
    expect(apiMock.uploadTaskJournalFile).not.toHaveBeenCalled();
    expect(toastMock.success).not.toHaveBeenCalledWith('Список файлов обновлён после завершения загрузки.', 'Журнал задач');
    cleanup();
  }, 15000);

  it('separates comments, files, timer and system activity in the modern task card', async () => {
    apiMock.getTaskJournalComments.mockResolvedValueOnce([
      {
        id: 501,
        taskJournalItemId: 101,
        authorUserId: 7,
        authorName: 'Администратор',
        kind: 'comment',
        text: 'Комментарий пользователя',
        createdAt: todayAt(9),
      },
      {
        id: 502,
        taskJournalItemId: 101,
        authorUserId: null,
        authorName: 'Система',
        kind: 'system',
        text: 'Изменён срок задачи.',
        createdAt: todayAt(10),
      },
    ]);
    apiMock.getTaskJournalFiles.mockResolvedValueOnce([
      {
        id: 601,
        taskJournalItemId: 101,
        documentKey: 'doc-1',
        version: 1,
        originalFileName: 'доверенность.pdf',
        contentType: 'application/pdf',
        sizeBytes: 2048,
        uploadedByUserId: 7,
        uploadedByName: 'Администратор',
        uploadedAt: todayAt(10),
        isLatestVersion: true,
      },
    ]);

    await renderJournal([createTask({ plannedDurationMinutes: 75 })], {
      settings: { taskCardWindowMode: 'modern' },
    });
    await openFirstTaskForEdit();

    const card = await screen.findByTestId('task-modern-card-window');
    expect(await within(card).findByRole('tab', { name: 'Комментарии 1' })).toBeTruthy();
    expect(within(card).getByRole('tab', { name: 'Файлы 1' })).toBeTruthy();
    expect(within(card).getByRole('tab', { name: 'Активность 1' })).toBeTruthy();
    expect(within(card).getByText('Комментарий пользователя')).toBeTruthy();
    expect(within(card).queryByText('Изменён срок задачи.')).toBeNull();

    fireEvent.click(within(card).getByRole('tab', { name: 'Активность 1' }));
    expect(await within(card).findByText('Изменён срок задачи.')).toBeTruthy();
    expect(within(card).queryByText('Комментарий пользователя')).toBeNull();

    fireEvent.click(within(card).getByRole('tab', { name: 'Файлы 1' }));
    expect(await within(card).findByText('доверенность.pdf')).toBeTruthy();

    fireEvent.click(within(card).getByRole('tab', { name: 'Таймер' }));
    expect(await within(card).findByText('Плановая длительность')).toBeTruthy();
    expect(within(within(card).getByRole('tabpanel')).getByText('1 ч 15 мин')).toBeTruthy();
  });

  it('keeps result disabled in the modern task card until completion', async () => {
    await renderJournal([createTask()], {
      settings: { taskCardWindowMode: 'modern' },
    });

    await openFirstTaskForEdit();

    const resultInput = await screen.findByTestId('task-result-field') as HTMLTextAreaElement;
    expect(resultInput.disabled).toBe(true);

    fireEvent.change(screen.getByTestId('task-editor-status-select'), { target: { value: 'done' } });
    expect(resultInput.disabled).toBe(false);
  });

  it.each(['legacy', 'modern'] as const)('creates a task together with its first comment and files in the %s card', async (taskCardWindowMode) => {
    await renderJournal([], {
      waitForTitle: null,
      settings: { taskCardWindowMode },
    });
    await openNewTaskFormAsGlobalInternal();

    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.change(within(card).getByTestId('task-title-input'), { target: { value: 'Задача с материалами' } });
    fireEvent.change(within(card).getByPlaceholderText(taskCardWindowMode === 'modern'
      ? 'Напишите комментарий...'
      : 'Введите первый комментарий'), { target: { value: 'Первый комментарий' } });
    fireEvent.click(within(card).getByRole('checkbox', { name: 'Показать комментарий во внешнем календаре' }));

    const files = [
      new File(['one'], 'первый.pdf', { type: 'application/pdf' }),
      new File(['two'], 'второй.docx', { type: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document' }),
    ];
    const fileInput = card.querySelector('input[type="file"]') as HTMLInputElement;
    fireEvent.change(fileInput, { target: { files } });

    expect(await within(card).findByText('первый.pdf')).toBeTruthy();
    expect(within(card).getByText('второй.docx')).toBeTruthy();
    fireEvent.click(within(card).getByRole('button', { name: 'Создать задачу' }));

    await waitFor(() => expect(apiMock.createTaskJournalComment).toHaveBeenCalledWith(101, 'Первый комментарий', true));
    expect(apiMock.uploadTaskJournalFile).toHaveBeenCalledTimes(2);
    expect(apiMock.uploadTaskJournalFile).toHaveBeenNthCalledWith(1, 101, files[0], undefined);
    expect(apiMock.uploadTaskJournalFile).toHaveBeenNthCalledWith(2, 101, files[1], undefined);
  });

  it.each(['legacy', 'modern'] as const)('keeps a newly created task open with accepted pending files in the %s card', async (taskCardWindowMode) => {
    await renderJournal([], { waitForTitle: null, settings: { taskCardWindowMode } });
    await openNewTaskFormAsGlobalInternal();
    apiMock.uploadTaskJournalFile.mockRejectedValueOnce(new Error('HTTP 503 Service Unavailable'));
    apiMock.getTaskFileUploadKey.mockReturnValue('pending-key');
    apiMock.getTaskFileUploadStates.mockResolvedValue([{ uploadKey: 'pending-key', fileName: 'первый.pdf',
      status: 'waiting', fileId: null, message: 'Завершение загрузки выполняется автоматически.' }]);
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.change(within(card).getByTestId('task-title-input'), { target: { value: 'Задача с ожидающим файлом' } });
    const file = new File(['document'], 'первый.pdf', { type: 'application/pdf' });
    fireEvent.change(card.querySelector('input[type="file"]') as HTMLInputElement, { target: { files: [file] } });
    fireEvent.click(within(card).getByRole('button', { name: 'Создать задачу' }));
    await waitFor(() => expect(toastMock.info).toHaveBeenCalledWith(expect.stringContaining('Задача создана. Файлы ещё ожидают прикрепления'), 'Журнал задач'));
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1);
    expect(apiMock.uploadTaskJournalFile).toHaveBeenCalledTimes(1);
    expect(apiMock.getTaskFileUploadStates).toHaveBeenCalledWith(101, undefined, { uploadKeys: ['pending-key'] });
    expect(await screen.findByRole('region', { name: 'Состояние ваших загрузок' })).toBeTruthy();
    expect(toastMock.error).not.toHaveBeenCalled();
    cleanup();
  });

  it.each(['legacy', 'modern'] as const)('keeps the created task open when its first file fails in the %s card', async (taskCardWindowMode) => {
    await renderJournal([], { waitForTitle: null, settings: { taskCardWindowMode } });
    await openNewTaskFormAsGlobalInternal();
    apiMock.uploadTaskJournalFile.mockRejectedValueOnce(new Error('Загрузка недоступна'));

    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.change(within(card).getByTestId('task-title-input'), { target: { value: 'Задача с недогруженным файлом' } });
    const file = new File(['document'], 'первый.pdf', { type: 'application/pdf' });
    fireEvent.change(card.querySelector('input[type="file"]') as HTMLInputElement, { target: { files: [file] } });
    fireEvent.click(within(card).getByRole('button', { name: 'Создать задачу' }));

    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith(
      expect.stringContaining('Задача создана, но не удалось добавить: первый.pdf'),
      'Журнал задач',
    ));
    const savedCard = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    expect(within(savedCard).getAllByText('Просмотр задачи').length).toBeGreaterThan(0);
    expect(within(savedCard).queryByRole('button', { name: 'Создать задачу' })).toBeNull();
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1);
  });

  it.each(['legacy', 'modern'] as const)('keeps the created task open when its first comment fails in the %s card', async (taskCardWindowMode) => {
    await renderJournal([], { waitForTitle: null, settings: { taskCardWindowMode } });
    await openNewTaskFormAsGlobalInternal();
    apiMock.createTaskJournalComment.mockRejectedValueOnce(new Error('Комментарий недоступен'));

    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    fireEvent.change(within(card).getByTestId('task-title-input'), { target: { value: 'Задача с недобавленным комментарием' } });
    fireEvent.change(within(card).getByPlaceholderText(taskCardWindowMode === 'modern'
      ? 'Напишите комментарий...'
      : 'Введите первый комментарий'), { target: { value: 'Первый комментарий' } });
    fireEvent.click(within(card).getByRole('button', { name: 'Создать задачу' }));

    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith(
      expect.stringContaining('Задача создана, но не удалось добавить: комментарий'),
      'Журнал задач',
    ));
    const savedCard = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    expect(within(savedCard).getAllByText('Просмотр задачи').length).toBeGreaterThan(0);
    expect(within(savedCard).queryByRole('button', { name: 'Создать задачу' })).toBeNull();
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1);
  });

  it.each(['legacy', 'modern'] as const)('opens linked client and case in new tabs without discarding the %s card draft', async (taskCardWindowMode) => {
    const onOpenClient = vi.fn();
    const onOpenCase = vi.fn();
    await renderJournal([createTask()], {
      settings: { taskCardWindowMode },
      onOpenClient,
      onOpenCase,
    });

    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const input = within(card).getByTestId('task-title-input') as HTMLInputElement | HTMLTextAreaElement;
    fireEvent.change(input, { target: { value: 'Несохранённый текст' } });
    const client = within(card).getByRole('link', { name: 'Открыть карточку контрагента' }) as HTMLAnchorElement;
    const linkedCase = within(card).getByRole('link', { name: 'Открыть карточку дела' }) as HTMLAnchorElement;
    expect(client.getAttribute('href')).toBe('/clients/10');
    expect(linkedCase.getAttribute('href')).toBe('/cases/100');
    for (const link of [client, linkedCase]) {
      expect(link.target).toBe('_blank');
      expect(link.rel).toContain('noopener');
      fireEvent.click(link);
    }
    expect(input.value).toBe('Несохранённый текст');
    expect(onOpenClient).not.toHaveBeenCalled();
    expect(onOpenCase).not.toHaveBeenCalled();
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
  });

  it.each(['legacy', 'modern'] as const)('opens the linked legal entity in a new tab from the %s card', async (taskCardWindowMode) => {
    legalEntitiesState.legalEntities = [{ id: 20, shortName: 'ООО Ромашка' } as LegalEntitySummary];
    await renderJournal([createTask({ clientId: null, legalEntityId: 20, legalEntityName: 'ООО Ромашка' })], {
      settings: { taskCardWindowMode }, onOpenLegalEntity: vi.fn(),
    });
    await openFirstTaskForEdit();
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const link = within(card).getByRole('link', { name: 'Открыть карточку контрагента' }) as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('/?ppTab=legalEntities&ppLegalEntityId=20');
    expect(link.target).toBe('_blank');
    expect(link.rel).toContain('noopener');
  });

  it.each(['legacy', 'modern'] as const)('links a task to its sales request from the %s task card', async (taskCardWindowMode) => {
    await renderJournal([createTask({ salesRequestId: 77 })], { settings: { taskCardWindowMode } });

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    const link = within(card).getByRole('link', { name: 'Открыть заявку' }) as HTMLAnchorElement;

    expect(link.getAttribute('href')).toBe('/?ppTab=requests&requestId=77');
    expect(link.target).toBe('_blank');
    expect(link.rel).toContain('noopener');
  });

  it.each(['legacy', 'modern'] as const)('does not show a sales request link for an ordinary task in the %s card', async (taskCardWindowMode) => {
    await renderJournal([createTask()], { settings: { taskCardWindowMode } });

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);

    expect(within(card).queryByRole('link', { name: 'Открыть заявку' })).toBeNull();
  });

  it('shows contractor payment controls in the modern task card for an external assignee', async () => {
    await renderJournal([createTask()], {
      waitForTitle: null,
      settings: { taskCardWindowMode: 'modern' },
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 9, name: 'Привлеченный исполнитель', email: 'contractor@example.com', isExternalContractor: true },
      ],
    });

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Выберите клиента и дело' });
    await continueTaskWizardForClient();

    fireEvent.change(screen.getByTestId('task-assignee-select'), { target: { value: '9' } });
    const contractorPaymentCheckbox = screen.getByTestId('task-contractor-payment-checkbox') as HTMLInputElement;

    expect(contractorPaymentCheckbox.disabled).toBe(false);
    fireEvent.click(contractorPaymentCheckbox);
    expect(screen.getByTestId('task-contractor-payment-amount')).toBeTruthy();
    expect(screen.getByTestId('task-contractor-payment-date')).toBeTruthy();
  });

  it('shows contractor payment controls in an existing modern task card and enables them after edit mode', async () => {
    await renderJournal([
      createTask({
        assigneeUserId: 9,
        assigneeName: 'Привлеченный исполнитель',
      }),
    ], {
      settings: { taskCardWindowMode: 'modern' },
      assignees: [
        { id: 7, name: 'Администратор', email: 'admin@example.com' },
        { id: 9, name: 'Привлеченный исполнитель', email: 'contractor@example.com', isExternalContractor: true },
      ],
    });

    fireEvent.click(screen.getAllByTitle('Просмотр')[0]);
    await screen.findByRole('heading', { name: 'Просмотр задачи' });

    const readonlyContractorPaymentCheckbox = screen.getByTestId('task-contractor-payment-checkbox') as HTMLInputElement;
    expect(readonlyContractorPaymentCheckbox.disabled).toBe(true);

    fireEvent.click(screen.getAllByRole('button', { name: 'Редактировать задачу' })[0]);
    await screen.findByRole('heading', { name: 'Редактировать задачу' });

    const contractorPaymentCheckbox = screen.getByTestId('task-contractor-payment-checkbox') as HTMLInputElement;
    expect(contractorPaymentCheckbox.disabled).toBe(false);
    fireEvent.click(contractorPaymentCheckbox);
    expect(screen.getByTestId('task-contractor-payment-amount')).toBeTruthy();
    expect(screen.getByTestId('task-contractor-payment-date')).toBeTruthy();
  });

  it('loads and creates tasks inside a client context', async () => {
    await renderJournal([createTask({ clientId: 10, clientCaseId: null })], {
      embedded: true,
      contextFilter: {
        clientId: 10,
        objectName: 'Иванов Иван Иванович',
        title: 'Иванов Иван Иванович',
      },
    });

    expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(expect.objectContaining({
      clientId: 10,
      page: 1,
      pageSize: 25,
    }), expect.objectContaining({ signal: expect.any(AbortSignal) }));
    expect(screen.getByText('Задачи: Иванов Иван Иванович')).toBeTruthy();
    expect(screen.queryByText('Проверка дел без задач')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: /Добавить задачу/i }));
    await screen.findByRole('heading', { name: 'Выберите клиента и дело' });
    fireEvent.change(screen.getByTestId('task-billing-wizard-case'), { target: { value: '100' } });
    fireEvent.click(screen.getByRole('button', { name: 'Проверить договор' }));
    await screen.findByRole('heading', { name: 'Определите учёт задачи' });
    fireEvent.click(screen.getByRole('button', { name: /Платная задача/ }));
    await screen.findByRole('heading', { name: 'Новая задача' });
    fireEvent.change(screen.getByTestId('task-title-input'), { target: { value: 'Связаться с клиентом' } });
    fireEvent.click(screen.getAllByRole('button', { name: 'Создать задачу' }).at(-1)!);

    await waitFor(() => expect(apiMock.createTaskJournalItem).toHaveBeenCalledTimes(1));
    expect(apiMock.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
      clientId: 10,
      legalEntityId: null,
      clientCaseId: 100,
      clientBillingClassification: 'billable',
      title: 'Связаться с клиентом',
    }));
  });
});
