import { apiService } from '../../services/api';
import type { TaskJournalSortKey } from '../../utils/taskJournalColumns';
import type { TaskJournalItem } from '../../utils/taskJournalStore';

export async function loadAllTaskJournalItems(
  contextFilter?: { clientId?: number | null; legalEntityId?: number | null },
  underControlOnly = false,
  calendarView = false,
  sortBy: TaskJournalSortKey = 'dueAt',
  sortDir: 'asc' | 'desc' = 'asc',
  includeDeadlineChangeRequests = false,
  signal?: AbortSignal,
) {
  const pageSize = 200;
  let page = 1;
  let total = Number.POSITIVE_INFINITY;
  const items: TaskJournalItem[] = [];
  const seenIds = new Set<number>();

  while (items.length < total) {
    signal?.throwIfAborted();
    const response = await apiService.getTaskJournalItems({
      page,
      pageSize,
      underControl: underControlOnly || undefined,
      clientId: contextFilter?.clientId ?? undefined,
      legalEntityId: contextFilter?.legalEntityId ?? undefined,
      calendarView,
      includeDeadlineChangeRequests,
      sortBy,
      sortDir,
    }, { signal });
    if (page > 1 && response.total !== total) {
      throw new Error('Выборка задач изменилась во время загрузки календаря.');
    }
    for (const task of response.items) {
      if (seenIds.has(task.id)) throw new Error('Задача повторилась на страницах календаря.');
      seenIds.add(task.id);
    }
    items.push(...response.items);
    total = response.total;

    if (response.items.length === 0) {
      if (items.length < total) throw new Error('Сервер вернул неполную выборку задач календаря.');
      break;
    }

    page += 1;
  }

  return items;
}

