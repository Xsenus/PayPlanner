import { Children, isValidElement, useId, useState, type ReactNode } from 'react';
import { SlidersHorizontal } from 'lucide-react';
import type { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { TASK_JOURNAL_TABLE_COLUMNS, type TaskTableColumnKey } from '../../utils/taskJournalColumns';
import { TableColumnSettings } from '../ui/TableColumnSettings';

type TaskTableSettings = ReturnType<typeof usePersistedTableSettings<TaskTableColumnKey>>;

export function TaskJournalColumnSettings({ settings }: { settings: TaskTableSettings }) {
  const [open, setOpen] = useState(false);
  const panelId = useId();

  return (
    <section className="mb-3 space-y-2" aria-label="Колонки журнала задач">
      <div className="flex flex-wrap items-center justify-end gap-2">
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
