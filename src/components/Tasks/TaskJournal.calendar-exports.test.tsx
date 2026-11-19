import { setupTaskJournalTests, apiMock, toastMock, createTask, renderJournal } from './TaskJournal.test-support';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { TaskJournalAssignee, TaskJournalItem } from '../../utils/taskJournalStore';

type View = 'calendar' | 'timeline';
type Period = 'day' | 'week' | 'month' | 'all';

async function openCalendar(options: {
  view?: View;
  period?: Period;
  zone?: string;
  expectedZone?: string;
  preferences?: Record<string, unknown>;
  tasks?: TaskJournalItem[];
  assignees?: TaskJournalAssignee[];
} = {}) {
  const zone = options.zone ?? 'UTC';
  const view = options.view ?? 'calendar';
  apiMock.getUserSetting.mockImplementation(async (key: string) => key === 'profile.timeZoneId' ? { key, value: zone } : null);
  localStorage.setItem('pp.task_journal.filters.v1.user:7', JSON.stringify({
    quickFilter: 'all', statusFilter: 'all', viewMode: view, periodMode: options.period ?? 'day',
    ...options.preferences,
  }));
  const result = await renderJournal(options.tasks ?? [], { initialView: view, waitForTitle: null, assignees: options.assignees });
  await waitFor(() => expect(apiMock.getCaseCalendarEvents).toHaveBeenLastCalledWith(expect.objectContaining({ timeZoneId: options.expectedZone ?? zone })));
  await screen.findByTestId(view === 'timeline' ? 'task-timeline-body-grid' : 'task-calendar-layout');
  return result;
}

function taskIcsButton() {
  return within(screen.getByTestId('task-ics-reminder-preset').closest('div')!).getByRole('button', { name: 'ICS' });
}

function exportReports() {
  for (const format of ['HTML', 'Word', 'PDF']) fireEvent.click(screen.getByRole('button', { name: format }));
}

describe('TaskJournal visible-calendar exports', () => {
  setupTaskJournalTests();
  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-06-03T13:30:00.000Z'));
    vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:calendar-export');
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
  });
  afterEach(() => { vi.restoreAllMocks(); vi.useRealTimers(); });

  it('exports the displayed day through all formats and keeps CSV endpoints inclusive, including UTC timestamps without a suffix', async () => {
    const tasks = [
      createTask({ id: 101, title: 'До периода', dueAt: '2026-06-02T23:59:59.999Z' }),
      createTask({ id: 102, title: 'Первая граница', dueAt: '2026-06-03T00:00:00.000Z' }),
      createTask({ id: 103, title: 'Внутри без суффикса', dueAt: '2026-06-03T13:00:00' }),
      createTask({ id: 104, title: 'Последняя граница', dueAt: '2026-06-03T23:59:59.999Z' }),
      createTask({ id: 105, title: 'После периода', dueAt: '2026-06-04T00:00:00.000Z' }),
    ];
    await openCalendar({ tasks });
    const blobSpy = vi.mocked(URL.createObjectURL);
    fireEvent.click(screen.getByRole('button', { name: 'CSV' }));
    const csv = await (blobSpy.mock.calls[0][0] as Blob).text();
    expect(csv).toContain('Первая граница');
    expect(csv).toContain('Внутри без суффикса');
    expect(csv).toContain('Последняя граница');
    expect(csv).not.toContain('До периода');
    expect(csv).not.toContain('После периода');
    fireEvent.click(taskIcsButton());
    exportReports();
    const range = { from: '2026-06-03T00:00:00.000Z', to: '2026-06-03T23:59:59.999Z', calendarView: true };
    expect(apiMock.downloadTaskJournalCalendar).toHaveBeenCalledWith(expect.objectContaining(range));
    for (const format of ['html', 'word', 'pdf']) expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledWith(expect.objectContaining({ ...range, format, timeZoneId: 'UTC' }));
  });

  it('uses the navigated day instead of today in task ICS and planning reports', async () => {
    await openCalendar();
    fireEvent.click(screen.getByRole('button', { name: 'Следующий период' }));
    fireEvent.click(taskIcsButton());
    fireEvent.click(screen.getByRole('button', { name: 'Word' }));
    const range = { from: '2026-06-04T00:00:00.000Z', to: '2026-06-04T23:59:59.999Z' };
    expect(apiMock.downloadTaskJournalCalendar).toHaveBeenCalledWith(expect.objectContaining(range));
    expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledWith(expect.objectContaining(range));
  });

  it('exports the displayed timeline week despite an inherited day preference and applies calendar visibility', async () => {
    await openCalendar({ view: 'timeline', period: 'day' });
    expect(apiMock.getTaskJournalItems).toHaveBeenCalledWith(expect.objectContaining({ calendarView: true }), expect.anything());
    expect(apiMock.getTaskJournalAssignees).toHaveBeenCalledWith({ calendarView: true }, expect.anything());
    fireEvent.click(screen.getByRole('button', { name: 'Следующая неделя' }));
    fireEvent.click(taskIcsButton());
    exportReports();
    const range = { from: '2026-06-08T00:00:00.000Z', to: '2026-06-14T23:59:59.999Z', calendarView: true };
    expect(apiMock.downloadTaskJournalCalendar).toHaveBeenCalledWith(expect.objectContaining(range));
    for (const format of ['html', 'word', 'pdf']) expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledWith(expect.objectContaining({ ...range, format }));
  });

  it.each(['month', 'all'] as const)('includes adjacent-month grid dates in every %s export and in case events, summary and ICS', async period => {
    vi.setSystemTime(new Date('2026-10-15T10:00:00.000Z'));
    await openCalendar({ period, zone: 'Asia/Novosibirsk' });
    const range = { from: '2026-09-27T17:00:00.000Z', to: '2026-11-08T16:59:59.999Z' };
    fireEvent.click(taskIcsButton());
    exportReports();
    expect(apiMock.downloadTaskJournalCalendar).toHaveBeenCalledWith(expect.objectContaining(range));
    for (const format of ['html', 'word', 'pdf']) expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledWith(expect.objectContaining({ ...range, format }));
    const caseRange = { from: '2026-09-28', to: '2026-11-08', timeZoneId: 'Asia/Novosibirsk' };
    expect(apiMock.getCaseCalendarEvents).toHaveBeenLastCalledWith(expect.objectContaining(caseRange));
    expect(apiMock.getCaseCalendarSummary).toHaveBeenLastCalledWith(expect.objectContaining(caseRange));
    const casePanel = screen.getByText('Календарь дел').closest('section')!;
    await waitFor(() => expect((within(casePanel).getByRole('button', { name: 'ICS' }) as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(within(casePanel).getByRole('button', { name: 'ICS' }));
    expect(apiMock.downloadCaseCalendarIcs).toHaveBeenCalledWith(expect.objectContaining(caseRange));
  });

  it.each([{ dateFrom: '2026-06-02', dateTo: '2026-06-04' }, { dateFrom: '2026-06-04', dateTo: '2026-06-02' }])('intersects the visible week with manual dates and preserves reversed-input normalization $dateFrom $dateTo', async dates => {
    await openCalendar({ period: 'week', preferences: dates });
    fireEvent.click(taskIcsButton());
    exportReports();
    const range = { from: '2026-06-02T00:00:00.000Z', to: '2026-06-04T23:59:59.999Z' };
    expect(apiMock.downloadTaskJournalCalendar).toHaveBeenCalledWith(expect.objectContaining(range));
    for (const format of ['html', 'word', 'pdf']) expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledWith(expect.objectContaining({ ...range, format }));
  });

  it('keeps a disjoint manual range empty without issuing an unbounded export', async () => {
    await openCalendar({ preferences: { dateFrom: '2026-07-01', dateTo: '2026-07-31' }, tasks: [createTask({ title: 'Не должна выгружаться', dueAt: '2026-07-03T10:00:00Z' })] });
    fireEvent.click(taskIcsButton());
    exportReports();
    expect(apiMock.downloadTaskJournalCalendar).not.toHaveBeenCalled();
    expect(apiMock.downloadTaskPlanningReport).not.toHaveBeenCalled();
    expect(toastMock.info).toHaveBeenCalledWith('Выбранные даты не пересекаются с периодом календаря.', 'Календарь задач');
    fireEvent.click(screen.getByRole('button', { name: 'CSV' }));
    const csv = await (vi.mocked(URL.createObjectURL).mock.calls[0][0] as Blob).text();
    expect(csv).toContain('Наименование задачи');
    expect(csv).not.toContain('Не должна выгружаться');
  });

  it('uses the immediate calendar search for all server exports before its debounce elapses', async () => {
    await openCalendar({ tasks: [createTask({ title: 'Новая работа', dueAt: '2026-06-03T11:00:00Z' })] });
    fireEvent.change(screen.getByRole('textbox', { name: 'Поиск по контрагенту, делу или наименованию задачи' }), { target: { value: 'Новая' } });
    fireEvent.click(taskIcsButton());
    exportReports();
    expect(apiMock.downloadTaskJournalCalendar.mock.calls[0][0].search).toBe('Новая');
    for (const [params] of apiMock.downloadTaskPlanningReport.mock.calls) expect(params.search).toBe('Новая');
  });

  it('delivers pending calendar exports after the same visible search finishes debouncing', async () => {
    await openCalendar();
    let finish!: (value: { blob: Blob; fileName: string }) => void;
    const pending = new Promise<{ blob: Blob; fileName: string }>(resolve => { finish = resolve; });
    apiMock.downloadTaskJournalCalendar.mockReturnValue(pending);
    apiMock.downloadTaskPlanningReport.mockReturnValue(pending);
    fireEvent.change(screen.getByRole('textbox', { name: 'Поиск по контрагенту, делу или наименованию задачи' }), { target: { value: 'Новая' } });
    fireEvent.click(taskIcsButton());
    exportReports();
    await act(async () => { await vi.advanceTimersByTimeAsync(350); });
    await act(async () => finish({ blob: new Blob(['current search']), fileName: 'current.ics' }));
    expect(URL.createObjectURL).toHaveBeenCalledTimes(4);
    expect(HTMLAnchorElement.prototype.click).toHaveBeenCalledTimes(4);
    expect(toastMock.success).toHaveBeenCalledTimes(4);
  });

  it('matches the overdue metric to the real current instant for visible tasks and CSV', async () => {
    await openCalendar({ tasks: [
      createTask({ id: 101, title: 'Уже просрочена', dueAt: '2026-06-03T13:15:00' }),
      createTask({ id: 102, title: 'Ещё впереди', dueAt: '2026-06-03T13:45:00' }),
    ] });
    fireEvent.click(screen.getByRole('button', { name: /Просрочены/ }));
    expect(screen.getByTestId('task-calendar-task-101')).toBeTruthy();
    expect(screen.queryByTestId('task-calendar-task-102')).toBeNull();
    fireEvent.click(taskIcsButton());
    expect(apiMock.downloadTaskJournalCalendar).toHaveBeenCalledWith(expect.objectContaining({ metric: 'overdue' }));
    fireEvent.click(screen.getByRole('button', { name: 'CSV' }));
    const csv = await (vi.mocked(URL.createObjectURL).mock.calls[0][0] as Blob).text();
    expect(csv).toContain('Уже просрочена');
    expect(csv).not.toContain('Ещё впереди');
  });

  it.each([
    { ids: [8], zone: 'Asia/Kamchatka', from: '2026-06-03T12:00:00.000Z', to: '2026-06-04T11:59:59.999Z', todayTo: '2026-06-04T12:00:00.000Z' },
    { ids: [7, 8], zone: 'Europe/Moscow', from: '2026-06-02T21:00:00.000Z', to: '2026-06-03T20:59:59.999Z', todayTo: '2026-06-03T21:00:00.000Z' },
  ])('uses selected-calendar timezone $zone for date boundaries, today metric and report formatting', async ({ ids, zone, from, to, todayTo }) => {
    await openCalendar({ zone: 'Europe/Moscow', expectedZone: zone, preferences: { calendarSelection: { all: false, userIds: ids }, metricFilter: 'today' }, assignees: [
      { id: 7, name: 'Администратор', email: 'a@local', timeZoneId: 'Europe/Moscow' },
      { id: 8, name: 'Исполнитель', email: 'b@local', timeZoneId: 'Asia/Kamchatka' },
    ], tasks: [
      createTask({ id: 101, title: 'Сегодня в выбранном поясе', assigneeUserId: 8, dueAt: new Date(Date.parse(from) + 15 * 60_000).toISOString() }),
      createTask({ id: 102, title: 'Вчера в выбранном поясе', assigneeUserId: 8, dueAt: new Date(Date.parse(from) - 15 * 60_000).toISOString() }),
    ] });
    expect(await screen.findByTestId('task-calendar-task-101')).toBeTruthy();
    expect(screen.queryByTestId('task-calendar-task-102')).toBeNull();
    fireEvent.click(taskIcsButton());
    exportReports();
    const params = { from, to, todayFrom: from, todayTo, assigneeUserIds: ids, metric: 'today', calendarView: true };
    expect(apiMock.downloadTaskJournalCalendar).toHaveBeenCalledWith(expect.objectContaining(params));
    for (const format of ['html', 'word', 'pdf']) expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledWith(expect.objectContaining({ ...params, format, timeZoneId: zone }));
  });

  it('exports native browser-day bounds when an unsupported profile timezone falls back to the browser', async () => {
    vi.setSystemTime(new Date('2026-03-08T12:00:00.000Z'));
    const browserZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
    const start = new Date();
    start.setHours(0, 0, 0, 0);
    const end = new Date(start);
    end.setDate(end.getDate() + 1);
    end.setMilliseconds(-1);
    await openCalendar({ zone: 'Unsupported/Calendar-Zone', expectedZone: browserZone });
    fireEvent.click(taskIcsButton());
    exportReports();
    const range = { from: start.toISOString(), to: end.toISOString() };
    expect(apiMock.downloadTaskJournalCalendar).toHaveBeenCalledWith(expect.objectContaining(range));
    for (const format of ['html', 'word', 'pdf']) expect(apiMock.downloadTaskPlanningReport).toHaveBeenCalledWith(expect.objectContaining({ ...range, format, timeZoneId: browserZone }));
  });

  it.each(['success', 'error'] as const)('discards pending task ICS and reports after a period change on late %s', async outcome => {
    await openCalendar();
    let resolve!: (value: { blob: Blob; fileName: string }) => void;
    let reject!: (error: Error) => void;
    const pending = new Promise<{ blob: Blob; fileName: string }>((finish, fail) => { resolve = finish; reject = fail; });
    apiMock.downloadTaskJournalCalendar.mockReturnValue(pending);
    apiMock.downloadTaskPlanningReport.mockReturnValue(pending);
    fireEvent.click(taskIcsButton());
    exportReports();
    fireEvent.click(screen.getByRole('button', { name: 'Следующий период' }));
    vi.mocked(URL.createObjectURL).mockClear();
    vi.mocked(HTMLAnchorElement.prototype.click).mockClear();
    toastMock.success.mockClear(); toastMock.error.mockClear();
    await act(async () => {
      if (outcome === 'success') resolve({ blob: new Blob(['old period']), fileName: 'old.ics' });
      else reject(new Error('Old period failed'));
    });
    expect(URL.createObjectURL).not.toHaveBeenCalled();
    expect(HTMLAnchorElement.prototype.click).not.toHaveBeenCalled();
    expect(toastMock.success).not.toHaveBeenCalled();
    expect(toastMock.error).not.toHaveBeenCalled();
  });

  it.each(['list', 'board'] as const)('keeps ordinary task %s export scope and legacy ICS dates unchanged', async view => {
    apiMock.getUserSetting.mockImplementation(async (key: string) => key === 'profile.timeZoneId' ? { key, value: 'UTC' } : null);
    await renderJournal([createTask({ dueAt: '2026-06-03T10:00:00Z' })], { initialView: view, periodMode: 'day' });
    fireEvent.click(taskIcsButton());
    exportReports();
    const icsParams = apiMock.downloadTaskJournalCalendar.mock.calls[0][0];
    expect(icsParams.from).toBeUndefined();
    expect(icsParams.to).toBeUndefined();
    expect(icsParams.calendarView).toBeUndefined();
    for (const [params] of apiMock.downloadTaskPlanningReport.mock.calls) {
      expect(params.calendarView).toBeUndefined();
      expect(params.timeZoneId).toBeUndefined();
      expect(params.from).toBeTruthy();
      expect(params.to).toBeTruthy();
    }
  });

  it('retains calendar permission scope after switching the calendar module to its list view', async () => {
    await openCalendar({ tasks: [createTask({ dueAt: '2026-06-03T10:00:00Z' })] });
    fireEvent.click(screen.getByRole('button', { name: 'Следующий период' }));
    fireEvent.click(screen.getByTestId('task-view-list'));
    await screen.findByTitle('Просмотр');
    expect(apiMock.getTaskJournalItems).toHaveBeenLastCalledWith(expect.objectContaining({ calendarView: true }), expect.anything());
    fireEvent.click(taskIcsButton());
    exportReports();
    expect(apiMock.downloadTaskJournalCalendar).toHaveBeenCalledWith(expect.objectContaining({ calendarView: true }));
    for (const [params] of apiMock.downloadTaskPlanningReport.mock.calls) {
      expect(params.calendarView).toBe(true);
      expect(params.timeZoneId).toBeUndefined();
      expect(params.from).toBe('2026-06-03T00:00:00.000Z');
    }
  });
});
