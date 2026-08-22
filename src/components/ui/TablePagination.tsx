import { SystemSelect } from './SystemSelect';
import { ChevronLeft, ChevronRight } from 'lucide-react';

interface TablePaginationProps {
  page: number;
  totalPages: number;
  totalItems: number;
  pageSize: number;
  pageSizeOptions: number[];
  onPageChange: (page: number) => void;
  onPageSizeChange: (pageSize: number) => void;
  className?: string;
  recordsLabel?: string;
  pageSizeLabel?: string;
  previousLabel?: string;
  nextLabel?: string;
}

export function TablePagination({
  page,
  totalPages,
  totalItems,
  pageSize,
  pageSizeOptions,
  onPageChange,
  onPageSizeChange,
  className = '',
  recordsLabel = 'Найдено записей',
  pageSizeLabel = 'Строк на странице',
  previousLabel = 'Назад',
  nextLabel = 'Вперёд',
}: TablePaginationProps) {
  if (totalItems <= 0) return null;

  return (
    <div
      className={`flex flex-col justify-between gap-4 border-t border-slate-200 bg-slate-50 px-4 py-4 text-sm text-slate-600 lg:flex-row lg:items-center ${className}`}
    >
      <div className="flex flex-wrap items-center gap-3">
        <span>
          {recordsLabel.replace(/:\s*$/, '')}: <span className="font-medium text-slate-800">{totalItems}</span>
        </span>
        <label className="flex items-center gap-2">
          <span>{pageSizeLabel}</span>
          <SystemSelect
            value={pageSize}
            onChange={(event) => onPageSizeChange(Number(event.target.value) || pageSize)}
            className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700"
          >
            {pageSizeOptions.map((size) => (
              <option key={size} value={size}>
                {size}
              </option>
            ))}
          </SystemSelect>
        </label>
      </div>

      <div className="flex items-center gap-2">
        <button
          type="button"
          data-testid="pagination-previous"
          onClick={() => onPageChange(Math.max(1, page - 1))}
          disabled={page <= 1}
          className="inline-flex items-center gap-1 rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-slate-600 transition hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50"
        >
          <ChevronLeft className="h-4 w-4" />
          {previousLabel}
        </button>
        <span className="min-w-[72px] text-center font-medium text-slate-700">
          {page} / {Math.max(1, totalPages)}
        </span>
        <button
          type="button"
          data-testid="pagination-next"
          onClick={() => onPageChange(Math.min(totalPages, page + 1))}
          disabled={page >= totalPages}
          className="inline-flex items-center gap-1 rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-slate-600 transition hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50"
        >
          {nextLabel}
          <ChevronRight className="h-4 w-4" />
        </button>
      </div>
    </div>
  );
}
