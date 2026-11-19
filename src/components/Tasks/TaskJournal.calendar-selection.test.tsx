import { setupTaskJournalTests, apiMock, toastMock, authState, permissionsState, createTask, renderJournal } from './TaskJournal.test-support';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { toTimeZoneDateKey } from '../../utils/timeZoneDate';
import type { TaskJournalAssignee } from '../../utils/taskJournalStore';
import { documentCopyTestSession } from '../../test/documentCopySession';
import type { CaseCalendarEvent } from '../../types';

const assignees: TaskJournalAssignee[] = [
  { id: 7, name: 'Администратор', email: 'a@local', roleId: 1, roleName: 'Администраторы', departmentId: 1, department: 'Офис' },
  { id: 8, name: 'Первый юрист', email: 'b@local', roleId: 2, roleName: 'Юристы', departmentId: 1, department: 'Офис' },
  { id: 9, name: 'Второй юрист', email: 'c@local', roleId: 2, roleName: 'Юристы', departmentId: 2, department: 'Филиал' },
];
const tasks = () => assignees.map((assignee, index) => createTask({
  id: 101 + index, title: `Работа ${assignee.id}`, assigneeUserId: assignee.id, assigneeName: assignee.name,
  authorUserId: 7,
  participants: [{ userId: assignee.id, role: 'assignee', displayName: assignee.name, approvalStatus: 'pending' }],
}));
const selection = () => within(screen.getByTestId('task-calendar-selection'));
function openSelection() { fireEvent.click(selection().getByText(/Календари сотрудников/)); }
const event = (title: string): CaseCalendarEvent => ({ id: title, kind: 'court', sourceType: 'court', title,
  startAt: new Date().toISOString(), endAt: new Date().toISOString(), allDay: false, clientCaseId: 100,
  caseTitle: 'Дело', responsibleUserId: 8, responsibleName: 'Первый юрист', status: 'scheduled', priority: 'normal', isOverdue: false, isWarning: false });

describe('TaskJournal calendar selection', () => {
  setupTaskJournalTests();

  it('combines selected roles and departments and applies the same user IDs to calendar requests', async () => {
    await renderJournal(tasks(), { initialView: 'calendar', assignees });
    await screen.findByTestId('task-calendar-task-103');
    openSelection();
    fireEvent.click(selection().getByRole('button', { name: 'Снять выбор календарей' }));
    fireEvent.click(selection().getByRole('checkbox', { name: 'Юристы' }));
    await waitFor(() => {
      expect(screen.queryByTestId('task-calendar-task-101')).toBeNull();
      expect(screen.getByTestId('task-calendar-task-102')).toBeTruthy();
      expect(screen.getByTestId('task-calendar-task-103')).toBeTruthy();
      expect(screen.getByRole('button', { name: /Активные/ }).textContent).toContain('2');
    });
    await waitFor(() => expect(apiMock.getCaseCalendarEvents).toHaveBeenLastCalledWith(expect.objectContaining({ responsibleUserIds: [8, 9] })));
    expect(apiMock.getCaseCalendarSummary).toHaveBeenLastCalledWith(expect.objectContaining({ responsibleUserIds: [8, 9] }));
    fireEvent.click(selection().getByRole('checkbox', { name: 'Офис' }));
    expect(await screen.findByTestId('task-calendar-task-101')).toBeTruthy();
    await waitFor(() => expect(apiMock.getCaseCalendarEvents).toHaveBeenLastCalledWith(expect.objectContaining({ responsibleUserIds: [7, 8, 9] })));
    const saved = JSON.parse(localStorage.getItem('pp.task_journal.filters.v1.user:7')!);
    expect(saved.calendarSelection).toEqual({ all: false, userIds: [], roleIds: [2], departmentIds: [1], departmentNames: [] });
  });

  it('sends no broad read after clearing selection and rejects an old response', async () => {
    await renderJournal(tasks(), { initialView: 'calendar', assignees });
    await screen.findByTestId('task-calendar-task-103');
    openSelection();
    let resolve!: (events: CaseCalendarEvent[]) => void;
    apiMock.getCaseCalendarEvents.mockImplementationOnce(() => new Promise<CaseCalendarEvent[]>(finish => { resolve = finish; }));
    fireEvent.click(selection().getByRole('checkbox', { name: 'Администратор' }));
    await waitFor(() => expect(resolve).toBeTruthy());
    const reads = apiMock.getCaseCalendarEvents.mock.calls.length;
    fireEvent.click(selection().getByRole('button', { name: 'Снять выбор календарей' }));
    await act(async () => resolve([event('Запоздалое судебное событие')]));
    expect(apiMock.getCaseCalendarEvents).toHaveBeenCalledTimes(reads);
    expect(screen.queryByText('Запоздалое судебное событие')).toBeNull();
    expect(screen.queryByTestId('task-calendar-task-102')).toBeNull();
    expect(screen.getByText('0 событий')).toBeTruthy();
  });

  it('restores only this user selection and uses it in both ICS downloads', async () => {
    localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({ viewMode: 'calendar', calendarSelection: { all: false, userIds: [8, 9], roleIds: [], departmentIds: [] } }));
    await renderJournal(tasks(), { initialView: 'calendar', assignees, waitForTitle: null });
    await screen.findByTestId('task-calendar-task-102');
    expect(screen.queryByTestId('task-calendar-task-101')).toBeNull();
    await waitFor(() => expect(screen.getAllByRole('button', { name: 'ICS' }).every(button => !(button as HTMLButtonElement).disabled)).toBe(true));
    for (const button of screen.getAllByRole('button', { name: 'ICS' })) fireEvent.click(button);
    await waitFor(() => expect(apiMock.downloadTaskJournalCalendar).toHaveBeenCalledWith(expect.objectContaining({ assigneeUserIds: [8, 9] })));
    expect(apiMock.downloadCaseCalendarIcs).toHaveBeenCalledWith(expect.objectContaining({ responsibleUserIds: [8, 9] }));
  });

  it('does not inherit selected calendars after an account replacement', async () => {
    const view = await renderJournal(tasks(), { initialView: 'calendar', assignees, withRoute: true, withRoleRerender: true });
    await screen.findByTestId('task-calendar-task-103');
    openSelection();
    fireEvent.click(selection().getByRole('button', { name: 'Снять выбор календарей' }));
    fireEvent.click(selection().getByRole('checkbox', { name: 'Первый юрист' }));
    authState.user = { id: 9, fullName: 'Другой администратор', email: 'other@local' };
    fireEvent.click(screen.getByRole('button', { name: 'Обновить роль для теста' }));
    await waitFor(() => expect(screen.getByTestId('task-calendar-selection').textContent).toContain('Календари сотрудников · все'));
    const saved = JSON.parse(localStorage.getItem('pp.task_journal.filters.v1.user:9')!);
    expect(saved.calendarSelection.all).toBe(true);
    expect(saved.calendarSelection.userIds).toEqual([]);
    view.unmount();
  });

  it('intersects the saved choice with the current active employee dictionary', async () => {
    localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({ viewMode: 'calendar', calendarSelection: { all: false, userIds: [8, 99], roleIds: [], departmentIds: [] } }));
    await renderJournal(tasks(), { initialView: 'calendar', assignees, waitForTitle: null });
    await screen.findByTestId('task-calendar-task-102');
    expect(screen.queryByTestId('task-calendar-task-101')).toBeNull();
    await waitFor(() => expect(apiMock.getCaseCalendarEvents).toHaveBeenLastCalledWith(expect.objectContaining({ responsibleUserIds: [8] })));
  });

  it.each(['selection', 'session'] as const)('suppresses all pending calendar exports after %s changes', async mode => {
    const session = documentCopyTestSession();
    try {
      await renderJournal(tasks(), { initialView: 'calendar', assignees });
      await screen.findByTestId('task-calendar-task-103');
      openSelection();
      let finish!: (value: { blob: Blob; fileName: string }) => void;
      const pending = new Promise<{ blob: Blob; fileName: string }>(resolve => { finish = resolve; });
      apiMock.downloadTaskJournalCalendar.mockReturnValue(pending);
      apiMock.downloadCaseCalendarIcs.mockReturnValue(pending);
      apiMock.downloadTaskPlanningReport.mockReturnValue(pending);
      await waitFor(() => expect(screen.getAllByRole('button', { name: 'ICS' }).every(button => !(button as HTMLButtonElement).disabled)).toBe(true));
      for (const button of screen.getAllByRole('button', { name: 'ICS' })) fireEvent.click(button);
      fireEvent.click(screen.getByRole('button', { name: 'Word' }));
      await waitFor(() => expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledOnce());
      toastMock.success.mockClear();
      toastMock.error.mockClear();
      if (mode === 'selection') fireEvent.click(selection().getByRole('button', { name: 'Снять выбор календарей' }));
      else session.replace('login');
      await act(async () => finish({ blob: new Blob(['calendar']), fileName: 'calendar.ics' }));
      expect(toastMock.success).not.toHaveBeenCalled();
      expect(toastMock.error).not.toHaveBeenCalled();
      if (mode === 'selection') {
        for (const button of screen.getAllByRole('button', { name: 'ICS' })) fireEvent.click(button);
        fireEvent.click(screen.getByRole('button', { name: 'Word' }));
        expect(apiMock.downloadTaskJournalCalendar).toHaveBeenCalledOnce();
        expect(apiMock.downloadCaseCalendarIcs).toHaveBeenCalledOnce();
        expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledOnce();
      }
    } finally { session.restore(); }
  });


  it.each([{ ids: [8], zone: 'Asia/Kamchatka' }, { ids: [8, 9], zone: 'Europe/Moscow' }])('uses the correct date range for selected calendars $ids', async ({ ids, zone }) => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-06-03T13:30:00.000Z'));
    localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({ viewMode: 'calendar', calendarSelection: { all: false, userIds: ids } }));
    apiMock.getUserSetting.mockImplementation(async (key: string) => key === 'profile.timeZoneId' ? { key, value: 'Europe/Moscow' } : null);
    await renderJournal([], { initialView: 'calendar', waitForTitle: null, assignees: assignees.map(row => ({ ...row, timeZoneId: row.id === 8 ? 'Asia/Kamchatka' : 'Europe/Moscow' })) });
    const date = toTimeZoneDateKey(new Date(), zone);
    await waitFor(() => expect(apiMock.getCaseCalendarEvents).toHaveBeenLastCalledWith(expect.objectContaining({ from: date, to: date, responsibleUserIds: ids, timeZoneId: zone })));
  });


  it.each(['success', 'error'] as const)('rejects pending task exports after same-role export revocation on late %s', async outcome => {
    authState.isAdmin = false;
    authState.user = { ...authState.user, role: { id: 3 } };
    const oldCreateObjectUrl = Object.getOwnPropertyDescriptor(URL, 'createObjectURL');
    const createObjectUrl = vi.fn(() => 'blob:synthetic-calendar');
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectUrl });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    try {
      await renderJournal(tasks(), { initialView: 'calendar', assignees, withRoute: true, withRoleRerender: true });
      await screen.findByTestId('task-calendar-task-103');
      let finish!: (value: { blob: Blob; fileName: string }) => void;
      let fail!: (error: Error) => void;
      const pending = new Promise<{ blob: Blob; fileName: string }>((resolve, reject) => { finish = resolve; fail = reject; });
      apiMock.downloadTaskJournalCalendar.mockReturnValue(pending);
      apiMock.downloadTaskPlanningReport.mockReturnValue(pending);
      const taskIcs = screen.getByTestId('task-ics-reminder-preset').closest('div')!;
      fireEvent.click(within(taskIcs).getByRole('button', { name: 'ICS' }));
      fireEvent.click(screen.getByRole('button', { name: 'Word' }));
      await waitFor(() => expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledOnce());
      expect(apiMock.downloadTaskJournalCalendar).toHaveBeenCalledOnce();
      permissionsState.taskCanExport = false;
      fireEvent.click(screen.getByRole('button', { name: 'Обновить роль для теста' }));
      expect(authState.user.id).toBe(7);
      expect(authState.user.role?.id).toBe(3);
      createObjectUrl.mockClear(); click.mockClear(); toastMock.success.mockClear(); toastMock.error.mockClear();
      await act(async () => {
        if (outcome === 'success') finish({ blob: new Blob(['calendar']), fileName: 'calendar.ics' });
        else fail(new Error('Ответ после отзыва права'));
      });
      expect(createObjectUrl).not.toHaveBeenCalled();
      expect(click).not.toHaveBeenCalled();
      expect(toastMock.success).not.toHaveBeenCalled();
      expect(toastMock.error).not.toHaveBeenCalled();
    } finally {
      click.mockRestore();
      if (oldCreateObjectUrl) Object.defineProperty(URL, 'createObjectURL', oldCreateObjectUrl);
      else Reflect.deleteProperty(URL, 'createObjectURL');
    }
  });


  it('keeps a saved selection while the employee list fails and restores it after an explicit retry', async () => {
    localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({ viewMode: 'calendar', calendarSelection: { all: false, userIds: [8] } }));
    apiMock.getTaskJournalAssignees.mockRejectedValueOnce(new Error('Unavailable dictionary'));
    await renderJournal(tasks(), { initialView: 'calendar', assignees, waitForTitle: null });
    await waitFor(() => expect(screen.getByTestId('task-calendar-selection').textContent).toContain('список недоступен'));
    openSelection();
    expect(await selection().findByRole('alert')).toBeTruthy();
    expect(screen.getByTestId('task-calendar-selection-unavailable')).toBeTruthy();
    expect(screen.queryByText('0 событий')).toBeNull();
    expect(screen.queryByRole('button', { name: /Активные/ })).toBeNull();
    expect(apiMock.getCaseCalendarEvents).not.toHaveBeenCalled();
    for (const button of screen.getAllByRole('button', { name: 'ICS' })) fireEvent.click(button);
    fireEvent.click(screen.getByRole('button', { name: 'Word' }));
    fireEvent.click(screen.getByRole('button', { name: 'CSV' }));
    expect(apiMock.downloadTaskJournalCalendar).not.toHaveBeenCalled();
    expect(apiMock.downloadTaskPlanningReport).not.toHaveBeenCalled();
    fireEvent.click(selection().getByRole('button', { name: 'Повторить загрузку календарей' }));
    expect(await screen.findByTestId('task-calendar-task-102')).toBeTruthy();
    expect(screen.queryByTestId('task-calendar-task-101')).toBeNull();
    expect(screen.queryByTestId('task-calendar-selection-unavailable')).toBeNull();
    expect(apiMock.getCaseCalendarEvents).toHaveBeenLastCalledWith(expect.objectContaining({ responsibleUserIds: [8] }));
    expect(JSON.parse(localStorage.getItem('pp.task_journal.filters.v1.user:7')!).calendarSelection.userIds).toEqual([8]);
  });

  it('distinguishes a confirmed empty employee list from a failed request', async () => {
    localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({ viewMode: 'calendar', calendarSelection: { all: false, userIds: [8] } }));
    await renderJournal([], { initialView: 'calendar', assignees: [], waitForTitle: null });
    await waitFor(() => expect(screen.queryByTestId('task-calendar-selection-unavailable')).toBeNull());
    openSelection();
    expect(selection().getByText('Нет доступных сотрудников.')).toBeTruthy();
    expect(selection().queryByRole('alert')).toBeNull();
    expect(apiMock.getCaseCalendarEvents).not.toHaveBeenCalled();
  });


  it.each(['events', 'summary'] as const)('clears previous case-calendar data after the current %s request fails and reloads the next choice', async member => {
    apiMock.getCaseCalendarEvents.mockResolvedValue([event('Прежнее заседание')]);
    await renderJournal(tasks(), { initialView: 'calendar', assignees });
    await screen.findByText('Прежнее заседание');
    openSelection();
    const failedRead = member === 'events' ? apiMock.getCaseCalendarEvents : apiMock.getCaseCalendarSummary;
    failedRead.mockRejectedValueOnce(new Error('Calendar read failed'));
    fireEvent.click(selection().getByRole('checkbox', { name: 'Администратор' }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith('Не удалось загрузить сроки и судебные события.', 'Календарь дел'));
    expect(screen.queryByText('Прежнее заседание')).toBeNull();
    apiMock.getCaseCalendarEvents.mockResolvedValue([event('Новое заседание')]);
    fireEvent.click(selection().getByRole('checkbox', { name: 'Администратор' }));
    expect(await screen.findByText('Новое заседание')).toBeTruthy();
  });

  it('ignores a late case-calendar read error after selecting no calendars', async () => {
    await renderJournal(tasks(), { initialView: 'calendar', assignees });
    await screen.findByTestId('task-calendar-task-103');
    openSelection();
    let reject!: (error: Error) => void;
    apiMock.getCaseCalendarEvents.mockImplementationOnce(() => new Promise<CaseCalendarEvent[]>((_resolve, fail) => { reject = fail; }));
    fireEvent.click(selection().getByRole('checkbox', { name: 'Администратор' }));
    await waitFor(() => expect(reject).toBeTruthy());
    fireEvent.click(selection().getByRole('button', { name: 'Снять выбор календарей' }));
    toastMock.error.mockClear();
    await act(async () => reject(new Error('Late failure')));
    expect(toastMock.error).not.toHaveBeenCalled();
    expect(screen.getByText('0 событий')).toBeTruthy();
  });

});
