import { Loader2, X } from 'lucide-react';
import { createPortal } from 'react-dom';

export interface StatusChangeOption<T extends string = string> {
  value: T;
  label: string;
  description?: string;
  className?: string;
}

interface StatusChangeModalProps<T extends string = string> {
  open: boolean;
  title: string;
  subject: string;
  currentLabel: string;
  currentValue?: T;
  value: T;
  options: StatusChangeOption<T>[];
  onChange: (value: T) => void;
  onCancel: () => void;
  onConfirm: () => void | Promise<void>;
  confirmLabel: string;
  consequences?: string[];
  error?: string | null;
  loading?: boolean;
  reason?: string;
  reasonRequired?: boolean;
  reasonLabel?: string;
  reasonPlaceholder?: string;
  onReasonChange?: (value: string) => void;
}

export function StatusChangeModal<T extends string = string>({
  open,
  title,
  subject,
  currentLabel,
  currentValue,
  value,
  options,
  onChange,
  onCancel,
  onConfirm,
  confirmLabel,
  consequences = [],
  error,
  loading = false,
  reason = '',
  reasonRequired = false,
  reasonLabel = 'Причина действия',
  reasonPlaceholder = 'Опишите, почему меняется статус',
  onReasonChange,
}: StatusChangeModalProps<T>) {
  if (!open || typeof document === 'undefined') return null;

  const selectedOption = options.find((option) => option.value === value);
  const isSameStatus = !selectedOption || (currentValue ? value === currentValue : selectedOption.label === currentLabel);
  const missingRequiredReason = reasonRequired && !isSameStatus && reason.trim().length === 0;

  return createPortal(
    <div className="fixed inset-0 z-[2300] flex items-center justify-center bg-slate-900/45 p-2 sm:p-4">
      <div
        role="dialog"
        aria-modal="true"
        aria-label={title}
        data-testid="status-change-dialog"
        className="flex max-h-[calc(100vh-1rem)] w-full max-w-4xl flex-col overflow-hidden rounded-lg bg-white shadow-xl sm:max-h-[calc(100vh-2rem)]"
      >
        <div className="flex shrink-0 items-start justify-between gap-4 border-b border-slate-200 px-4 py-3 sm:px-6 sm:py-4">
          <div>
            <h2 className="text-lg font-semibold text-slate-900">{title}</h2>
            <p className="mt-1 text-sm text-slate-600">{subject}</p>
          </div>
          <button
            type="button"
            onClick={onCancel}
            disabled={loading}
            className="rounded-lg p-2 text-slate-400 transition hover:bg-slate-100 hover:text-slate-600 disabled:cursor-not-allowed disabled:opacity-60"
            aria-label="Закрыть"
          >
            <X className="h-4 w-4" />
          </button>
        </div>

        <div data-testid="status-change-dialog-body" className="min-h-0 flex-1 space-y-5 overflow-y-auto px-4 py-4 sm:px-6 sm:py-5">
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="rounded-lg border border-slate-200 bg-slate-50 px-4 py-3">
              <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">Сейчас</div>
              <div className="mt-1 text-sm font-semibold text-slate-900">{currentLabel}</div>
            </div>
            <div className="rounded-lg border border-blue-200 bg-blue-50 px-4 py-3">
              <div className="text-xs font-semibold uppercase tracking-wide text-blue-600">Будет</div>
              <div className="mt-1 text-sm font-semibold text-blue-900">{selectedOption?.label ?? 'Не выбрано'}</div>
            </div>
          </div>

          <div>
            <div className="mb-2 text-sm font-semibold text-slate-800">Выберите новый статус</div>
            <div className="grid gap-2 md:grid-cols-2 xl:grid-cols-3">
              {options.map((option) => {
                const active = option.value === value;
                return (
                  <button
                    key={option.value}
                    type="button"
                    onClick={() => onChange(option.value)}
                    disabled={loading}
                    className={`min-h-[4.25rem] rounded-lg border px-3 py-2 text-left transition focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:cursor-not-allowed disabled:opacity-60 ${
                      active ? 'border-blue-300 bg-blue-50 shadow-sm' : 'border-slate-200 bg-white hover:bg-slate-50'
                    }`}
                  >
                    <span className={`inline-flex rounded-full px-2.5 py-1 text-xs font-semibold ${option.className ?? 'bg-slate-100 text-slate-700'}`}>
                      {option.label}
                    </span>
                    {option.description ? (
                      <span className="mt-1 block text-xs leading-5 text-slate-500">{option.description}</span>
                    ) : null}
                  </button>
                );
              })}
            </div>
          </div>

          {onReasonChange || reasonRequired ? (
            <label className="block space-y-2">
              <span className="text-sm font-semibold text-slate-800">
                {reasonLabel}
                {reasonRequired ? <span className="text-rose-600"> *</span> : null}
              </span>
              <textarea
                value={reason}
                onChange={(event) => onReasonChange?.(event.target.value)}
                disabled={loading || !onReasonChange}
                placeholder={reasonPlaceholder}
                rows={3}
                className="w-full resize-y rounded-lg border border-slate-300 px-3 py-2 text-sm text-slate-800 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200 disabled:bg-slate-100 disabled:text-slate-500"
              />
              {missingRequiredReason ? (
                <span className="text-xs font-medium text-rose-600">Причина обязательна для записи в журнал.</span>
              ) : null}
            </label>
          ) : null}

          {consequences.length > 0 ? (
            <div className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900">
              <div className="font-semibold">Что произойдет после подтверждения</div>
              <ul className="mt-2 list-disc space-y-1 pl-5">
                {consequences.map((item) => (
                  <li key={item}>{item}</li>
                ))}
              </ul>
            </div>
          ) : null}

          {error ? (
            <div className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
              {error}
            </div>
          ) : null}
        </div>

        <div data-testid="status-change-dialog-actions" className="flex shrink-0 items-center justify-end gap-3 border-t border-slate-200 px-4 py-3 sm:px-6 sm:py-4">
          <button
            type="button"
            onClick={onCancel}
            disabled={loading}
            className="rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
          >
            Отмена
          </button>
          <button
            type="button"
            onClick={() => void onConfirm()}
            disabled={loading || isSameStatus || missingRequiredReason}
            className="inline-flex items-center justify-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {loading ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
            {loading ? 'Сохраняем...' : confirmLabel}
          </button>
        </div>
      </div>
    </div>,
    document.body,
  );
}
