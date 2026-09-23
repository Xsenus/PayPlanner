import { apiService, type TaskJournalListParams } from './api';
import type { TaskJournalItem } from '../utils/taskJournalStore';

/** Loads only the requested week, independently of journal filters and pagination. */
export async function loadTaskWeekSchedule({
  from,
  to,
  assigneeKeys,
  calendarView,
  signal,
}: {
  from: string;
  to: string;
  assigneeKeys: readonly string[];
  calendarView: boolean;
  signal: AbortSignal;
}): Promise<TaskJournalItem[]> {
  if (assigneeKeys.length === 0) return [];

  // Legacy participants can have a name without a user account. The server still
  // enforces visibility; the dialog matches these participants by name afterward.
  const userIds = assigneeKeys.some((key) => key.startsWith('name:'))
    ? [undefined]
    : [...new Set(assigneeKeys.filter((key) => key.startsWith('user:')).map((key) => Number(key.slice(5))))]
      .filter((id) => Number.isInteger(id) && id > 0);
  const result = new Map<number, TaskJournalItem>();

  for (const assigneeUserId of userIds) {
    let page = 1;
    let loaded = 0;
    while (true) {
      signal.throwIfAborted();
      const params: TaskJournalListParams = {
        status: 'active', from, to, assigneeUserId, calendarView,
        page, pageSize: 200, sortBy: 'dueAt', sortDir: 'asc',
      };
      const response = await apiService.getTaskJournalItems(params, { signal });
      signal.throwIfAborted();
      for (const task of response.items) result.set(task.id, task);
      loaded += response.items.length;
      if (loaded >= response.total) break;
      if (response.items.length === 0) {
        throw new Error('Не удалось загрузить расписание полностью. Повторите загрузку.');
      }
      page += 1;
    }
  }

  return [...result.values()];
}
