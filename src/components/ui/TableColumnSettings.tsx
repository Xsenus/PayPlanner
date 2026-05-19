import {
  ArrowDown,
  ArrowUp,
  GripVertical,
  RotateCcw,
  type LucideIcon,
} from 'lucide-react';
import type { DragEvent, ReactNode } from 'react';

type TableColumnSettingsColumn<K extends string> = {
  key: K;
  label: string;
  description?: string;
};

interface TableColumnSettingsProps<K extends string> {
  title?: string;
  description?: string;
  resetLabel?: string;
  columns: TableColumnSettingsColumn<K>[];
  columnOrder: K[];
  visibleColumns: K[];
  draggingColumn: K | null;
  onToggleColumn: (key: K) => void;
  onMoveColumn: (key: K, direction: 'up' | 'down') => void;
  onDragStart: (key: K, event: DragEvent<HTMLElement>) => void;
  onDragOver: (event: DragEvent<HTMLElement>) => void;
  onDrop: (key: K, event: DragEvent<HTMLElement>) => void;
  onDragEnd: () => void;
  onReset: () => void;
  icon?: LucideIcon;
  footer?: ReactNode;
}

export function TableColumnSettings<K extends string>({
  title = 'Настройка таблицы',
  description = 'Выбирайте видимые колонки, меняйте порядок и сохраняйте настройку под пользователя.',
  resetLabel = 'Сбросить настройки',
  columns,
  columnOrder,
  visibleColumns,
  draggingColumn,
  onToggleColumn,
  onMoveColumn,
  onDragStart,
  onDragOver,
  onDrop,
  onDragEnd,
  onReset,
  icon: Icon = RotateCcw,
  footer,
}: TableColumnSettingsProps<K>) {
  const columnMap = new Map(columns.map((column) => [column.key, column]));

  return (
    <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
      <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold text-slate-900">{title}</h2>
          <p className="mt-1 text-xs text-slate-500">{description}</p>
        </div>
        <button
          type="button"
          onClick={onReset}
          className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-600 transition hover:bg-slate-50"
        >
          <Icon className="h-4 w-4" />
          {resetLabel}
        </button>
      </div>

      <div className="grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
        {columnOrder.map((columnKey, index) => {
          const column = columnMap.get(columnKey);
          if (!column) return null;
          const isVisible = visibleColumns.includes(columnKey);

          return (
            <div
              key={columnKey}
              className={`flex items-center gap-3 rounded-lg border px-3 py-2 ${
                draggingColumn === columnKey
                  ? 'border-blue-200 bg-blue-50'
                  : 'border-slate-100 bg-slate-50'
              }`}
              onDragOver={onDragOver}
              onDrop={(event) => onDrop(columnKey, event)}
            >
              <button
                type="button"
                draggable
                onDragStart={(event) => onDragStart(columnKey, event)}
                onDragEnd={onDragEnd}
                className="cursor-grab rounded p-1 text-slate-400 active:cursor-grabbing"
                aria-label={`Перетащите колонку ${column.label}, чтобы изменить порядок`}
              >
                <GripVertical className="h-4 w-4" />
              </button>

              <input
                type="checkbox"
                aria-label={`Показать колонку ${column.label}`}
                checked={isVisible}
                onChange={() => onToggleColumn(columnKey)}
                className="h-4 w-4 rounded border-slate-300 text-blue-600"
              />

              <div className="min-w-0 flex-1">
                <div className="truncate text-sm text-slate-700">{column.label}</div>
                {column.description ? (
                  <div className="truncate text-xs text-slate-400">{column.description}</div>
                ) : null}
              </div>

              <div className="flex items-center gap-1">
                <button
                  type="button"
                  onClick={() => onMoveColumn(columnKey, 'up')}
                  disabled={index === 0}
                  className="rounded-lg border border-slate-200 bg-white p-1.5 text-slate-500 transition hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-40"
                  aria-label={`Переместить колонку ${column.label} вверх`}
                >
                  <ArrowUp className="h-3.5 w-3.5" />
                </button>
                <button
                  type="button"
                  onClick={() => onMoveColumn(columnKey, 'down')}
                  disabled={index === columnOrder.length - 1}
                  className="rounded-lg border border-slate-200 bg-white p-1.5 text-slate-500 transition hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-40"
                  aria-label={`Переместить колонку ${column.label} вниз`}
                >
                  <ArrowDown className="h-3.5 w-3.5" />
                </button>
              </div>
            </div>
          );
        })}
      </div>

      {footer ? <div className="mt-4 border-t border-slate-100 pt-4">{footer}</div> : null}
    </div>
  );
}
