import { beforeEach, describe, expect, it, vi } from 'vitest';
import { loadTaskWeekSchedule } from './taskWeekSchedule';
import type { TaskJournalItem } from '../utils/taskJournalStore';

const apiMock = vi.hoisted(() => ({ getTaskJournalItems: vi.fn() }));
vi.mock('./api', () => ({ apiService: apiMock }));

const task = (id: number) => ({ id } as TaskJournalItem);
const request = () => ({
  from: '2026-09-20T16:00:00.000Z',
  to: '2026-09-27T23:59:59.999Z',
  assigneeKeys: ['user:7'],
  calendarView: false,
  signal: new AbortController().signal,
});

describe('loadTaskWeekSchedule', () => {
  beforeEach(() => apiMock.getTaskJournalItems.mockReset());

  it('loads every page of the selected week and both assignees without journal filters', async () => {
    apiMock.getTaskJournalItems
      .mockResolvedValueOnce({ items: [task(1), task(2)], total: 3 })
      .mockResolvedValueOnce({ items: [task(3)], total: 3 })
      .mockResolvedValueOnce({ items: [task(2), task(4)], total: 2 });
    const params = { ...request(), assigneeKeys: ['user:7', 'user:8', 'user:7'], calendarView: true };

    expect((await loadTaskWeekSchedule(params)).map((item) => item.id)).toEqual([1, 2, 3, 4]);
    expect(apiMock.getTaskJournalItems).toHaveBeenCalledTimes(3);
    expect(apiMock.getTaskJournalItems).toHaveBeenNthCalledWith(1, {
      from: params.from, to: params.to, status: 'active', assigneeUserId: 7,
      calendarView: true, page: 1, pageSize: 200, sortBy: 'dueAt', sortDir: 'asc',
    }, { signal: params.signal });
    expect(apiMock.getTaskJournalItems.mock.calls[1][0]).toMatchObject({ assigneeUserId: 7, page: 2 });
    expect(apiMock.getTaskJournalItems.mock.calls[2][0]).toMatchObject({ assigneeUserId: 8, page: 1 });
  });

  it('uses one permission-scoped weekly query for legacy named assignees', async () => {
    apiMock.getTaskJournalItems.mockResolvedValueOnce({ items: [task(1)], total: 1 });
    await loadTaskWeekSchedule({ ...request(), assigneeKeys: ['user:7', 'name:исполнитель'] });
    expect(apiMock.getTaskJournalItems).toHaveBeenCalledTimes(1);
    expect(apiMock.getTaskJournalItems.mock.calls[0][0]).toMatchObject({ assigneeUserId: undefined });
  });

  it('does not fetch an unassigned schedule', async () => {
    expect(await loadTaskWeekSchedule({ ...request(), assigneeKeys: [] })).toEqual([]);
    expect(apiMock.getTaskJournalItems).not.toHaveBeenCalled();
  });

  it('does not present a partial schedule as complete after a failed page', async () => {
    apiMock.getTaskJournalItems
      .mockResolvedValueOnce({ items: [task(1)], total: 2 })
      .mockRejectedValueOnce(new Error('network error'));
    await expect(loadTaskWeekSchedule(request())).rejects.toThrow('network error');
  });

  it('rejects incomplete pages rather than claiming that missing appointments are free', async () => {
    apiMock.getTaskJournalItems.mockResolvedValueOnce({ items: [], total: 2 });
    await expect(loadTaskWeekSchedule(request())).rejects.toThrow('Не удалось загрузить расписание полностью');
  });

  it('stops paging when the user closes the dialog or switches weeks', async () => {
    const controller = new AbortController();
    apiMock.getTaskJournalItems.mockImplementationOnce(async () => {
      controller.abort();
      return { items: [task(1)], total: 3 };
    });
    await expect(loadTaskWeekSchedule({ ...request(), signal: controller.signal })).rejects.toMatchObject({ name: 'AbortError' });
    expect(apiMock.getTaskJournalItems).toHaveBeenCalledTimes(1);
  });
});
