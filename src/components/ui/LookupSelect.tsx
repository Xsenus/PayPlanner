import { Check, ChevronDown, Search, X } from 'lucide-react';
import { useMemo, useState } from 'react';
import { createPortal } from 'react-dom';

export interface LookupSelectOption {
  value: string;
  label: string;
  description?: string;
  keywords?: string[];
}

interface LookupSelectProps {
  label?: string;
  value: string;
  options: LookupSelectOption[];
  onChange: (value: string) => void;
  placeholder?: string;
  title?: string;
  dictionaryLabel?: string;
  searchPlaceholder?: string;
  emptyOptionLabel?: string;
  emptyOptionDescription?: string;
  helperText?: string;
  required?: boolean;
  disabled?: boolean;
  buttonClassName?: string;
  noOptionsText?: string;
}

function normalizeText(value: string) {
  return value.trim().toLocaleLowerCase('ru');
}

export function LookupSelect({
  label,
  value,
  options,
  onChange,
  placeholder = 'Не выбрано',
  title,
  dictionaryLabel = 'Справочник',
  searchPlaceholder = 'Поиск',
  emptyOptionLabel,
  emptyOptionDescription = 'Очистить значение',
  helperText,
  required = false,
  disabled = false,
  buttonClassName = '',
  noOptionsText = 'Ничего не найдено',
}: LookupSelectProps) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');

  const selectedOption = useMemo(
    () => options.find((option) => option.value === value) ?? null,
    [options, value],
  );

  const filteredOptions = useMemo(() => {
    const normalizedQuery = normalizeText(query);
    if (!normalizedQuery) {
      return options;
    }

    return options.filter((option) => {
      const haystack = normalizeText([
        option.label,
        option.description ?? '',
        ...(option.keywords ?? []),
      ].join(' '));

      return haystack.includes(normalizedQuery);
    });
  }, [options, query]);

  const handleOpen = () => {
    if (disabled) {
      return;
    }

    setQuery('');
    setOpen(true);
  };

  const handleClose = () => {
    setOpen(false);
    setQuery('');
  };

  const handleSelect = (nextValue: string) => {
    onChange(nextValue);
    handleClose();
  };

  const triggerLabel = selectedOption?.label ?? placeholder;
  const modalTitle = title ?? label ?? 'Справочник';

  return (
    <>
      <div className="space-y-2">
        {label ? (
          <div className="flex items-center justify-between gap-3">
            <span className="text-sm font-medium text-slate-700">
              {label}
              {required ? ' *' : ''}
            </span>
            <button
              type="button"
              onClick={handleOpen}
              disabled={disabled}
              className="text-xs font-medium text-blue-600 transition hover:text-blue-700 disabled:cursor-not-allowed disabled:text-slate-400"
            >
              {dictionaryLabel}
            </button>
          </div>
        ) : null}

        <button
          type="button"
          onClick={handleOpen}
          disabled={disabled}
          className={`flex w-full items-center justify-between gap-3 rounded-lg border border-slate-300 bg-white px-3 py-2 text-left text-sm transition focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:cursor-not-allowed disabled:bg-slate-100 disabled:text-slate-500 ${buttonClassName}`}
        >
          <span className={selectedOption ? 'text-slate-700' : 'text-slate-400'}>
            {triggerLabel}
          </span>
          <ChevronDown className="h-4 w-4 shrink-0 text-slate-400" />
        </button>

        {helperText ? (
          <p className="text-xs text-slate-500">{helperText}</p>
        ) : null}
      </div>

      {open && typeof document !== 'undefined'
        ? createPortal(
          <div className="fixed inset-0 z-[2300] flex items-center justify-center bg-slate-900/40 p-4">
            <div className="w-full max-w-3xl rounded-2xl bg-white shadow-2xl">
              <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4">
                <h2 className="text-xl font-semibold text-slate-900">{modalTitle}</h2>
                <button
                  type="button"
                  onClick={handleClose}
                  className="rounded-lg p-2 text-slate-400 transition hover:bg-slate-100 hover:text-slate-600"
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
                    onChange={(event) => setQuery(event.target.value)}
                    placeholder={searchPlaceholder}
                    autoFocus
                    className="w-full rounded-xl border border-slate-300 bg-white py-2 pl-10 pr-3 text-sm text-slate-700 outline-none transition focus:border-blue-500 focus:ring-2 focus:ring-blue-100"
                  />
                </div>

                <div className="text-sm text-slate-500">
                  Всего: {filteredOptions.length}
                  {query.trim() ? ` из ${options.length}` : ''}
                </div>

                <div className="max-h-[52vh] space-y-3 overflow-y-auto pr-1">
                  {emptyOptionLabel ? (
                    <button
                      type="button"
                      onClick={() => handleSelect('')}
                      className="flex w-full items-center justify-between gap-4 rounded-2xl border border-slate-200 px-4 py-3 text-left transition hover:border-blue-200 hover:bg-slate-50"
                    >
                      <div className="min-w-0">
                        <div className="font-medium text-slate-900">{emptyOptionLabel}</div>
                        <div className="mt-1 text-sm text-slate-500">{emptyOptionDescription}</div>
                      </div>
                      <span
                        className={`inline-flex items-center gap-1 rounded-full px-3 py-1 text-sm ${
                          value
                            ? 'bg-slate-100 text-slate-700'
                            : 'bg-blue-100 text-blue-700'
                        }`}
                      >
                        {!value ? <Check className="h-4 w-4" /> : null}
                        {!value ? 'Выбрано' : 'Выбрать'}
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
                          onClick={() => handleSelect(option.value)}
                          className="flex w-full items-center justify-between gap-4 rounded-2xl border border-slate-200 px-4 py-3 text-left transition hover:border-blue-200 hover:bg-slate-50"
                        >
                          <div className="min-w-0">
                            <div className="font-medium text-slate-900">{option.label}</div>
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
                  onClick={handleClose}
                  className="rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 transition hover:bg-slate-50"
                >
                  Закрыть
                </button>
              </div>
            </div>
          </div>,
          document.body,
        )
        : null}
    </>
  );
}
