export type TaskJournalSortKey = 'counterparty' | 'title' | 'assignee' | 'author' | 'dueAt' | 'type' | 'result' | 'status';
export type TaskTableColumnKey = TaskJournalSortKey | 'actions';

export const TASK_JOURNAL_TABLE_COLUMNS: Array<{ key: TaskTableColumnKey; label: string; defaultWidth: number }> = [
  { key: 'counterparty', label: 'Контрагент / дело', defaultWidth: 200 },
  { key: 'title', label: 'Наименование задачи', defaultWidth: 290 },
  { key: 'assignee', label: 'Исполнитель', defaultWidth: 180 },
  { key: 'author', label: 'Поручил', defaultWidth: 170 },
  { key: 'dueAt', label: 'Дата исполнения', defaultWidth: 180 },
  { key: 'type', label: 'Тип', defaultWidth: 130 },
  { key: 'result', label: 'Результат', defaultWidth: 260 },
  { key: 'status', label: 'Статус', defaultWidth: 160 },
  { key: 'actions', label: 'Действия', defaultWidth: 156 },
];
