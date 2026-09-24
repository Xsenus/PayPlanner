import { Children, isValidElement, useId, useState, type ReactNode } from 'react';
import { SlidersHorizontal } from 'lucide-react';
import type { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { TASK_JOURNAL_TABLE_COLUMNS, type TaskJournalSortKey, type TaskTableColumnKey } from '../../utils/taskJournalColumns';
import { TableColumnSettings } from '../ui/TableColumnSettings';

type TaskTableSettings = ReturnType<typeof usePersistedTableSettings<TaskTableColumnKey>>;

type SortDirection = 'asc' | 'desc';

export function TaskJournalColumnSettings({ settings, sortBy, sortDir, onSortSelect }: {
  settings: TaskTableSettings;
  sortBy: TaskJournalSortKey;
  sortDir: SortDirection;
  onSortSelect: (key: TaskJournalSortKey, direction: SortDirection) => void;
}) {
  const [open, setOpen] = useState(false);
  const panelId = useId();

  return (
    <section className="mb-3 space-y-2" aria-label="Колонки журнала задач">
      <div className="flex flex-wrap items-center justify-end gap-2">
        <label className="flex items-center gap-2 text-sm text-slate-600">
          Сортировка
          <select
            aria-label="Сортировка журнала задач"
            value={`${sortBy}:${sortDir}`}
            onChange={(event) => {
              const [key, direction] = event.target.value.split(':') as [TaskJournalSortKey, SortDirection];
              onSortSelect(key, direction);
            }}
            className="rounded-lg border border-slate-200 bg-white px-2 py-2 text-sm text-slate-700"
          >
            <option value="dueAt:asc">По дате исполнения: ближайшие сначала</option>
            <option value="dueAt:desc">По дате исполнения: поздние сначала</option>
            <option value="counterparty:asc">По контрагенту: А–Я</option>
            <option value="counterparty:desc">По контрагенту: Я–А</option>
            <option value="title:asc">По названию задачи: А–Я</option>
            <option value="title:desc">По названию задачи: Я–А</option>
            <option value="assignee:asc">По исполнителю: А–Я</option>
            <option value="assignee:desc">По исполнителю: Я–А</option>
            <option value="author:asc">По постановщику: А–Я</option>
            <option value="author:desc">По постановщику: Я–А</option>
            <option value="type:asc">По типу: А–Я</option>
            <option value="type:desc">По типу: Я–А</option>
            <option value="result:asc">По результату: А–Я</option>
            <option value="result:desc">По результату: Я–А</option>
            <option value="status:asc">По статусу: А–Я</option>
            <option value="status:desc">По статусу: Я–А</option>
          </select>
        </label>
        <button
          type="button"
          onClick={() => setOpen((current) => !current)}
          aria-expanded={open}
          aria-controls={panelId}
          className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-semibold text-slate-600 transition hover:bg-slate-50"
          title="Выбрать колонки, изменить их порядок и ширину"
        >
          <SlidersHorizontal className="h-4 w-4" />
          Колонки
        </button>
      </div>
      {open ? (
        <div id={panelId}>
          <TableColumnSettings
            title="Колонки журнала задач"
            description="Отметьте нужные колонки. Меняйте порядок стрелками или перетаскиванием, а ширину — за границу заголовка в таблице. Настройки сохраняются для вас."
            columns={TASK_JOURNAL_TABLE_COLUMNS}
            columnOrder={settings.columnOrder}
            visibleColumns={settings.visibleColumns}
            draggingColumn={settings.draggingColumn}
            onToggleColumn={settings.toggleColumn}
            onMoveColumn={settings.moveColumn}
            onDragStart={settings.handleColumnDragStart}
            onDragOver={settings.handleColumnDragOver}
            onDrop={settings.handleColumnDrop}
            onDragEnd={settings.handleColumnDragEnd}
            onReset={settings.resetTableSettings}
          />
        </div>
      ) : null}
    </section>
  );
}

/** Keep the existing cell actions intact while applying the same layout to every row. */
export function TaskTableColumns({ columns, children }: { columns: TaskTableColumnKey[]; children: ReactNode }) {
  const cells = new Map(Children.toArray(children).flatMap((child) => (
    isValidElement<{ 'data-task-column': TaskTableColumnKey }>(child)
      ? [[child.props['data-task-column'], child] as const]
      : []
  )));
  return <>{columns.map((key) => cells.get(key))}</>;
}
