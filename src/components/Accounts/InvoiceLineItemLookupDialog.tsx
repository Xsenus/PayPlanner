import { Check, Search, X } from 'lucide-react';
import { useEffect, useMemo } from 'react';
import { createPortal } from 'react-dom';

export interface InvoiceLineItemLookupOption {
  value: string;
  label: string;
  description?: string;
  keywords?: string[];
}

interface InvoiceLineItemLookupDialogProps {
  open: boolean;
  title: string;
  description?: string;
  query: string;
  value?: string;
  options: InvoiceLineItemLookupOption[];
  searchPlaceholder: string;
  noOptionsText?: string;
  maxWidthClassName?: string;
  clearActionLabel?: string;
  clearActionDescription?: string;
  customActionLabel?: string;
  customActionDescription?: string;
  onQueryChange: (value: string) => void;
  onClose: () => void;
  onSelect: (value: string) => void;
  onClear?: () => void;
  onSelectCustomQuery?: () => void;
}

function normalizeLookupQuery(value: string): string {
  return value.trim().toLocaleLowerCase('ru');
}

export function InvoiceLineItemLookupDialog({
  open,
  title,
  description,
  query,
  value,
  options,
  searchPlaceholder,
  noOptionsText = 'Ничего не найдено',
  maxWidthClassName = 'max-w-3xl',
  clearActionLabel,
  clearActionDescription,
  customActionLabel,
  customActionDescription,
  onQueryChange,
  onClose,
  onSelect,
  onClear,
  onSelectCustomQuery,
}: InvoiceLineItemLookupDialogProps) {
  useEffect(() => {
    if (!open) {
      return undefined;
    }

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        onClose();
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => {
      window.removeEventListener('keydown', handleKeyDown);
    };
  }, [open, onClose]);

  const filteredOptions = useMemo(() => {
    const normalizedQuery = normalizeLookupQuery(query);
    if (!normalizedQuery) {
      return options;
    }

    return options.filter((option) => {
      const haystack = normalizeLookupQuery(
        [option.label, option.description ?? '', ...(option.keywords ?? [])].join(' '),
      );
      return haystack.includes(normalizedQuery);
    });
  }, [options, query]);

  if (!open || typeof document === 'undefined') {
    return null;
  }

  return createPortal(
    <div
      className="fixed inset-0 z-[2400] flex items-center justify-center bg-slate-900/50 p-4"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) {
          onClose();
        }
      }}
    >
      <div className={`w-full overflow-hidden rounded-[28px] bg-white shadow-2xl ${maxWidthClassName}`}>
        <div className="flex items-center justify-between border-b border-slate-200 px-6 py-5">
          <div className="min-w-0">
            <h2 className="truncate text-xl font-semibold text-slate-900">{title}</h2>
            {description ? (
              <p className="mt-1 text-sm text-slate-500">{description}</p>
            ) : null}
          </div>
          <button
            type="button"
            onClick={onClose}
            className="rounded-xl p-2 text-slate-400 transition hover:bg-slate-100 hover:text-slate-600"
            aria-label="Закрыть"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="space-y-4 px-6 py-5">
          <div className="relative">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
            <input
              type="text"
              value={query}
              onChange={(event) => onQueryChange(event.target.value)}
              placeholder={searchPlaceholder}
              autoFocus
              className="w-full rounded-2xl border border-slate-300 bg-slate-50 py-3 pl-10 pr-3 text-sm text-slate-700 outline-none transition focus:border-blue-500 focus:bg-white focus:ring-2 focus:ring-blue-100"
            />
          </div>

          <div className="text-sm text-slate-500">
            Найдено: {filteredOptions.length}
            {query.trim() ? ` из ${options.length}` : ''}
          </div>

          <div className="max-h-[56vh] space-y-3 overflow-y-auto pr-1">
            {onClear && clearActionLabel ? (
              <button
                type="button"
                onClick={onClear}
                className="flex w-full items-center justify-between gap-4 rounded-2xl border border-slate-200 bg-slate-50/70 px-4 py-3 text-left transition hover:border-blue-200 hover:bg-slate-50"
              >
                <div className="min-w-0">
                  <div className="font-medium text-slate-900">{clearActionLabel}</div>
                  {clearActionDescription ? (
                    <div className="mt-1 text-sm text-slate-500">{clearActionDescription}</div>
                  ) : null}
                </div>
                {!value ? (
                  <span className="inline-flex shrink-0 items-center gap-1 rounded-full bg-blue-100 px-3 py-1 text-sm text-blue-700">
                    <Check className="h-4 w-4" />
                    Выбрано
                  </span>
                ) : null}
              </button>
            ) : null}

            {onSelectCustomQuery && customActionLabel ? (
              <button
                type="button"
                onClick={onSelectCustomQuery}
                className="flex w-full items-center justify-between gap-4 rounded-2xl border border-blue-200 bg-blue-50/70 px-4 py-3 text-left transition hover:bg-blue-50"
              >
                <div className="min-w-0">
                  <div className="font-medium text-slate-900">{customActionLabel}</div>
                  {customActionDescription ? (
                    <div className="mt-1 text-sm text-slate-500">{customActionDescription}</div>
                  ) : null}
                </div>
                <span className="inline-flex shrink-0 rounded-full bg-white px-3 py-1 text-sm text-blue-700">
                  Применить
                </span>
              </button>
            ) : null}

            {filteredOptions.length === 0 ? (
              <div className="rounded-2xl border border-dashed border-slate-200 px-4 py-10 text-center text-sm text-slate-500">
                {noOptionsText}
              </div>
            ) : (
              filteredOptions.map((option) => {
                const isSelected = option.value === value;

                return (
                  <button
                    key={option.value}
                    type="button"
                    onClick={() => onSelect(option.value)}
                    className={`flex w-full items-center justify-between gap-4 rounded-2xl border px-4 py-3 text-left transition ${
                      isSelected
                        ? 'border-blue-200 bg-blue-50/80'
                        : 'border-slate-200 bg-white hover:border-blue-200 hover:bg-slate-50'
                    }`}
                  >
                    <div className="min-w-0">
                      <div className="truncate font-medium text-slate-900">{option.label}</div>
                      {option.description ? (
                        <div className="mt-1 text-sm text-slate-500">{option.description}</div>
                      ) : null}
                    </div>
                    <span
                      className={`inline-flex shrink-0 items-center gap-1 rounded-full px-3 py-1 text-sm ${
                        isSelected
                          ? 'bg-blue-100 text-blue-700'
                          : 'bg-slate-100 text-slate-700'
                      }`}
                    >
                      {isSelected ? <Check className="h-4 w-4" /> : null}
                      {isSelected ? 'Выбрано' : 'Выбрать'}
                    </span>
                  </button>
                );
              })
            )}
          </div>
        </div>

        <div className="flex items-center justify-end border-t border-slate-200 px-6 py-4">
          <button
            type="button"
            onClick={onClose}
            className="rounded-xl border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 transition hover:bg-slate-50"
          >
            Закрыть
          </button>
        </div>
      </div>
    </div>,
    document.body,
  );
}
