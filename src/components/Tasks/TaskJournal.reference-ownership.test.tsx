import { setupTaskJournalTests, apiMock, authState, permissionsState, createTask, renderJournal } from './TaskJournal.test-support';
import { TaskJournal } from './TaskJournal';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useClientCases } from '../../hooks/useClientCases';
import { documentCopyTestSession } from '../../test/documentCopySession';
import type { ClientCase } from '../../types';
import type { TaskJournalAssignee } from '../../utils/taskJournalStore';

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(finish => { resolve = finish; });
  return { promise, resolve };
}
const caseRow = (id: number, title: string): ClientCase => ({ id, title, clientId: 10, status: 'Active', createdAt: '2026-11-01T00:00:00Z' });
const names = (card: HTMLElement, label: string) => [...card.querySelectorAll<HTMLSelectElement>(`select[aria-label="${label}"]`)].flatMap(select => [...select.options].map(option => option.text));
async function openCard(mode: 'legacy' | 'modern', assignees?: TaskJournalAssignee[]) {
  await renderJournal([createTask({ authorUserId: 7 })], { withRoute: true, withRoleRerender: true, assignees, settings: { taskCardWindowMode: mode } });
  fireEvent.click(screen.getByTitle('Просмотр'));
  const card = await screen.findByTestId(`task-${mode}-card-window`);
  fireEvent.click(within(card).getAllByRole('button', { name: 'Редактировать задачу' })[0]);
  return screen.findByTestId(`task-${mode}-card-window`);
}

describe('TaskJournal reference ownership', () => {
  setupTaskJournalTests();
  beforeEach(async () => {
    const actual = await vi.importActual<typeof import('../../hooks/useClientCases')>('../../hooks/useClientCases');
    vi.mocked(useClientCases).mockImplementation(actual.useClientCases);
    authState.isAdmin = false;
  });

  it.each(['legacy', 'modern'] as const)('reference baseline: %s task never loads cases without case access', async mode => {
    permissionsState.caseCanView = false;
    const card = await openCard(mode);
    expect(apiMock.getCasesV1).not.toHaveBeenCalled();
    expect((card.querySelector('select[aria-label="Дело"]') as HTMLSelectElement).disabled).toBe(true);
  });

  it.each(['legacy', 'modern'] as const)('reference baseline: %s task refreshes cases on a new role and rejects the old pending response', async mode => {
    const old = deferred<ClientCase[]>();
    const current = deferred<ClientCase[]>();
    apiMock.getCasesV1.mockReturnValue(old.promise);
    const card = await openCard(mode);
    const count = apiMock.getCasesV1.mock.calls.length;
    const signal = apiMock.getCasesV1.mock.lastCall![1].signal as AbortSignal;
    apiMock.getCasesV1.mockReturnValue(current.promise);
    fireEvent.change(within(card).getByTestId('task-title-input'), { target: { value: 'Сохранённый черновик' } });
    authState.user = { ...authState.user, role: { id: 42 } };
    fireEvent.click(screen.getByRole('button', { name: 'Обновить роль для теста' }));
    await waitFor(() => expect(apiMock.getCasesV1).toHaveBeenCalledTimes(count + 1));
    expect(signal.aborted).toBe(true);
    await act(async () => current.resolve([caseRow(102, 'Текущее дело')]));
    await waitFor(() => expect(names(card, 'Дело')).toContain('Текущее дело'));
    await act(async () => old.resolve([caseRow(103, 'Прежнее дело')]));
    expect(names(card, 'Дело')).not.toContain('Прежнее дело');
    expect((within(card).getByTestId('task-title-input') as HTMLTextAreaElement).value).toBe('Сохранённый черновик');
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
  });

  it.each(['legacy', 'modern'] as const)('reference baseline: %s task retires the assignee list on a new role without resetting the draft', async mode => {
    const card = await openCard(mode);
    const count = apiMock.getTaskJournalAssignees.mock.calls.length;
    const pending = deferred<{ id: number; name: string; email: string }[]>();
    apiMock.getTaskJournalAssignees.mockReturnValueOnce(pending.promise);
    fireEvent.change(within(card).getByTestId('task-title-input'), { target: { value: 'Черновик исполнителя' } });
    authState.user = { ...authState.user, role: { id: 43 } };
    fireEvent.click(screen.getByRole('button', { name: 'Обновить роль для теста' }));
    await waitFor(() => expect(apiMock.getTaskJournalAssignees).toHaveBeenCalledTimes(count + 1));
    expect(names(card, 'Основной исполнитель')).not.toContain('Администратор — admin@example.com');
    await act(async () => pending.resolve([{ id: 7, name: 'Текущий исполнитель', email: 'current@local.invalid' }]));
    await waitFor(() => expect(names(card, 'Основной исполнитель').some(name => name.includes('Текущий исполнитель'))).toBe(true));
    expect((within(card).getByTestId('task-title-input') as HTMLTextAreaElement).value).toBe('Черновик исполнителя');
    expect(apiMock.getTaskJournalSettings).toHaveBeenCalledTimes(1);
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
  });

  it.each(['legacy', 'modern'] as const)('reference expanded: %s case denial clears options and retry preserves the saved association', async mode => {
    apiMock.getCasesV1.mockResolvedValue([caseRow(103, 'Прежний каталог дел')]);
    const card = await openCard(mode);
    const field = card.querySelector('select[aria-label="Дело"]') as HTMLSelectElement;
    await waitFor(() => expect(names(card, 'Дело')).toContain('Прежний каталог дел'));
    const count = apiMock.getCasesV1.mock.calls.length;
    permissionsState.caseCanView = false;
    fireEvent.click(screen.getByRole('button', { name: 'Обновить роль для теста' }));
    expect(field.disabled).toBe(true); expect(field.value).toBe('100');
    expect(names(card, 'Дело')).not.toContain('Прежний каталог дел');
    expect(apiMock.getCasesV1).toHaveBeenCalledTimes(count);
    apiMock.getCasesV1.mockRejectedValueOnce(new Error('HTTP 403 Forbidden (http://synthetic.local/api/catalogue): denied'));
    permissionsState.caseCanView = true;
    fireEvent.click(screen.getByRole('button', { name: 'Обновить роль для теста' }));
    const retry = await within(card).findByRole('button', { name: 'Повторить загрузку дел' });
    expect(within(card).getByRole('alert').textContent).toContain('Недостаточно прав');
    expect(within(card).getByRole('alert').textContent).not.toContain('http://');
    expect(field.disabled).toBe(true); expect(field.value).toBe('100');
    apiMock.getCasesV1.mockResolvedValueOnce([caseRow(104, 'Разрешённое дело')]);
    fireEvent.click(retry);
    await waitFor(() => expect(names(card, 'Дело')).toContain('Разрешённое дело'));
    expect(field.disabled).toBe(false); expect(field.value).toBe('100');
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
  });

  it.each(['legacy', 'modern'] as const)('reference expanded: %s cases reject a response after a login replacement before rerender', async mode => {
    const session = documentCopyTestSession();
    const old = deferred<ClientCase[]>();
    apiMock.getCasesV1.mockReturnValue(old.promise);
    const card = await openCard(mode);
    const count = apiMock.getCasesV1.mock.calls.length;
    session.replace('login');
    await act(async () => old.resolve([caseRow(103, 'Чужой ответ сеанса')]));
    expect(names(card, 'Дело')).not.toContain('Чужой ответ сеанса');
    apiMock.getCasesV1.mockResolvedValue([caseRow(104, 'Дело нового входа')]);
    fireEvent.click(screen.getByRole('button', { name: 'Обновить роль для теста' }));
    await waitFor(() => expect(names(card, 'Дело')).toContain('Дело нового входа'));
    expect(apiMock.getCasesV1).toHaveBeenCalledTimes(count + 1);
    session.restore();
  });

  it.each(['legacy', 'modern'] as const)('reference expanded: %s assignees reject an old scope, handle 403 and retry without losing IDs', async mode => {
    const card = await openCard(mode);
    const field = within(card).getByTestId('task-assignee-select') as HTMLSelectElement;
    const old = deferred<{ id: number; name: string; email: string }[]>();
    apiMock.getTaskJournalAssignees.mockReturnValueOnce(old.promise);
    permissionsState.taskViewScope = 'department';
    fireEvent.click(screen.getByRole('button', { name: 'Обновить роль для теста' }));
    await waitFor(() => expect(field.disabled).toBe(true));
    const signal = apiMock.getTaskJournalAssignees.mock.lastCall![1].signal as AbortSignal;
    apiMock.getTaskJournalAssignees.mockRejectedValueOnce(new Error('HTTP 403 Forbidden (http://synthetic.local/api/catalogue): denied'));
    permissionsState.taskViewScope = 'own';
    fireEvent.click(screen.getByRole('button', { name: 'Обновить роль для теста' }));
    const retry = await within(card).findByRole('button', { name: 'Повторить загрузку исполнителей' });
    expect(within(card).getByRole('alert').textContent).toContain('Недостаточно прав');
    expect(within(card).getByRole('alert').textContent).not.toContain('http://');
    expect(signal.aborted).toBe(true); expect(field.disabled).toBe(true); expect(field.value).toBe('7');
    await act(async () => old.resolve([{ id: 9, name: 'Прежний исполнитель', email: 'old@local.invalid' }]));
    expect(names(card, 'Основной исполнитель').some(name => name.includes('Прежний исполнитель'))).toBe(false);
    apiMock.getTaskJournalAssignees.mockResolvedValueOnce([{ id: 7, name: 'Актуальный исполнитель', email: 'current@local.invalid' }]);
    fireEvent.click(retry);
    await waitFor(() => expect(field.disabled).toBe(false));
    expect(field.value).toBe('7');
    expect(names(card, 'Основной исполнитель').some(name => name.includes('Актуальный исполнитель'))).toBe(true);
    expect(apiMock.getTaskJournalSettings).toHaveBeenCalledTimes(1);
    expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
  });

  it.each(['legacy', 'modern'] as const)('reference expanded: %s assignees ignore same-user replaced login and refresh without resetting the form', async mode => {
    const session = documentCopyTestSession();
    const card = await openCard(mode);
    const count = apiMock.getTaskJournalAssignees.mock.calls.length;
    session.refresh();
    fireEvent.click(screen.getByRole('button', { name: 'Обновить роль для теста' }));
    expect(apiMock.getTaskJournalAssignees).toHaveBeenCalledTimes(count);
    const old = deferred<{ id: number; name: string; email: string }[]>();
    apiMock.getTaskJournalAssignees.mockReturnValueOnce(old.promise);
    authState.user = { ...authState.user, role: { id: 44 } };
    fireEvent.click(screen.getByRole('button', { name: 'Обновить роль для теста' }));
    await waitFor(() => expect(apiMock.getTaskJournalAssignees).toHaveBeenCalledTimes(count + 1));
    session.replace('login');
    await act(async () => old.resolve([{ id: 9, name: 'Другой сеанс', email: 'old@local.invalid' }]));
    expect(names(card, 'Основной исполнитель').some(name => name.includes('Другой сеанс'))).toBe(false);
    apiMock.getTaskJournalAssignees.mockResolvedValueOnce([{ id: 7, name: 'Новый сеанс', email: 'current@local.invalid' }]);
    fireEvent.click(screen.getByRole('button', { name: 'Обновить роль для теста' }));
    await waitFor(() => expect(names(card, 'Основной исполнитель').some(name => name.includes('Новый сеанс'))).toBe(true));
    session.restore();
  });

  it.each(['legacy', 'modern'] as const)('reference final: %s assignees stop reading on lost task access and restore separately', async mode => {
    const card = await openCard(mode);
    const count = apiMock.getTaskJournalAssignees.mock.calls.length;
    permissionsState.taskCanView = false;
    fireEvent.click(screen.getByRole('button', { name: 'Обновить роль для теста' }));
    const field = within(card).getByTestId('task-assignee-select') as HTMLSelectElement;
    expect(names(card, 'Основной исполнитель')).toEqual(['Без привязки к пользователю', 'Администратор']);
    expect(field.disabled).toBe(true);
    expect(field.value).toBe('7');
    expect(apiMock.getTaskJournalAssignees).toHaveBeenCalledTimes(count);
    apiMock.getTaskJournalAssignees.mockResolvedValueOnce([{ id: 7, name: 'Разрешённый сотрудник', email: 'current@local.invalid' }]);
    permissionsState.taskCanView = true;
    fireEvent.click(screen.getByRole('button', { name: 'Обновить роль для теста' }));
    await waitFor(() => expect(names(card, 'Основной исполнитель').some(name => name.includes('Разрешённый сотрудник'))).toBe(true));
    expect(apiMock.getTaskJournalAssignees).toHaveBeenCalledTimes(count + 1);
    expect(apiMock.getTaskJournalSettings).toHaveBeenCalledTimes(1);
  });

  it.each(['legacy', 'modern'] as const)('reference final: %s calendar mode refreshes assignees without reloading settings', async mode => {
    const setup = await renderJournal([createTask()], { settings: { taskCardWindowMode: mode } });
    setup.unmount(); vi.clearAllMocks();
    const view = render(<TaskJournal routeTaskId={101} />);
    await screen.findByTestId(`task-${mode}-card-window`);
    await waitFor(() => expect(apiMock.getTaskJournalAssignees).toHaveBeenCalledTimes(1));
    view.rerender(<TaskJournal routeTaskId={101} initialView="calendar" />);
    await waitFor(() => expect(apiMock.getTaskJournalAssignees).toHaveBeenCalledTimes(2));
    expect(apiMock.getTaskJournalAssignees.mock.lastCall?.[0]).toEqual({ calendarView: true });
    expect(apiMock.getTaskJournalSettings).toHaveBeenCalledTimes(1);
  });

  it.each(['legacy', 'modern'] as const)('reference author boundary: %s accepts current author choice but rejects an old login callback', async mode => {
    const session = documentCopyTestSession();
    authState.isAdmin = true;
    try {
      const card = await openCard(mode, [{ id: 7, name: 'Автор задачи', email: 'current@local.invalid' }, { id: 9, name: 'Другой постановщик', email: 'other@local.invalid' }]);
      const field = within(card).getByTestId('task-author-select') as HTMLSelectElement;
      fireEvent.change(field, { target: { value: '9' } });
      expect(field.value).toBe('9');
      fireEvent.change(field, { target: { value: '7' } });
      expect(field.value).toBe('7');
      session.replace('login');
      fireEvent.change(field, { target: { value: '9' } });
      expect(field.value).toBe('7');
      expect(apiMock.updateTaskJournalItem).not.toHaveBeenCalled();
    } finally { session.restore(); }
  });
});
