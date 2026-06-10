import { X } from 'lucide-react';
import { useRef, type FormEvent } from 'react';
import { createPortal } from 'react-dom';
import { useModalFocusTrap } from '../../hooks/useModalFocusTrap';
import { LookupSelect, type LookupSelectOption } from './LookupSelect';

export interface FormPromptField {
  key: string;
  label: string;
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  type?: 'text' | 'date' | 'select' | 'lookup';
  options?: LookupSelectOption[];
  required?: boolean;
  autoFocus?: boolean;
  disabled?: boolean;
  lookupTitle?: string;
  searchPlaceholder?: string;
  emptyOptionLabel?: string;
  helperText?: string;
  dictionaryLabel?: string;
  noOptionsText?: string;
}

interface FormPromptModalProps {
  open: boolean;
  title: string;
  description?: string;
  fields: FormPromptField[];
  onCancel: () => void;
  onConfirm: () => void | Promise<void>;
  confirmLabel?: string;
  cancelLabel?: string;
  error?: string;
  loading?: boolean;
}

export function FormPromptModal({
  open,
  title,
  description,
  fields,
  onCancel,
  onConfirm,
  confirmLabel = 'Подтвердить',
  cancelLabel = 'Отмена',
  error,
  loading = false,
}: FormPromptModalProps) {
  const dialogRef = useRef<HTMLFormElement>(null);
  const handleDialogKeyDown = useModalFocusTrap(dialogRef, open, onCancel);

  if (!open || typeof document === 'undefined') return null;

  const handleSubmit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    void onConfirm();
  };

  return createPortal(
    <div className="fixed inset-0 z-[2200] flex items-center justify-center bg-slate-900/40 p-4">
      <form
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-label={title}
        onSubmit={handleSubmit}
        onKeyDown={handleDialogKeyDown}
        className="w-full max-w-xl rounded-2xl bg-white shadow-xl"
      >
        <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4">
          <h2 className="text-lg font-semibold text-slate-900">{title}</h2>
          <button
            type="button"
            onClick={onCancel}
            className="rounded-lg p-2 text-slate-400 transition hover:bg-slate-100 hover:text-slate-600"
            aria-label="Закрыть"
          >
            <X className="h-4 w-4" />
          </button>
        </div>

        <div className="space-y-4 px-6 py-4">
          {description ? <p className="text-sm text-slate-600">{description}</p> : null}

          <div className="grid gap-4 md:grid-cols-2">
            {fields.map((field) => {
              const fieldDisabled = loading || field.disabled;

              if (field.type === 'lookup') {
                return (
                  <div key={field.key} className="md:col-span-2">
                    <LookupSelect
                      label={field.label}
                      value={field.value}
                      options={field.options ?? []}
                      onChange={field.onChange}
                      placeholder={field.placeholder}
                      title={field.lookupTitle}
                      searchPlaceholder={field.searchPlaceholder}
                      emptyOptionLabel={field.emptyOptionLabel}
                      helperText={field.helperText}
                      required={field.required}
                      disabled={fieldDisabled}
                      dictionaryLabel={field.dictionaryLabel}
                      noOptionsText={field.noOptionsText}
                      buttonClassName="rounded-xl bg-slate-50"
                    />
                  </div>
                );
              }

              return (
                <label key={field.key} className="block">
                  <span className="mb-2 block text-sm font-medium text-slate-700">
                    {field.label}
                    {field.required ? ' *' : ''}
                  </span>
                  {field.type === 'select' ? (
                    <select
                      value={field.value}
                      onChange={(event) => field.onChange(event.target.value)}
                      required={field.required}
                      autoFocus={field.autoFocus}
                      disabled={fieldDisabled}
                      className="w-full rounded-xl border border-slate-300 bg-slate-50 px-3 py-2 text-sm text-slate-700 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100 disabled:cursor-not-allowed disabled:bg-slate-100"
                    >
                      {(field.options ?? []).map((option) => (
                        <option key={option.value} value={option.value}>
                          {option.label}
                        </option>
                      ))}
                    </select>
                  ) : (
                    <input
                      type={field.type ?? 'text'}
                      value={field.value}
                      onChange={(event) => field.onChange(event.target.value)}
                      placeholder={field.placeholder}
                      required={field.required}
                      autoFocus={field.autoFocus}
                      disabled={fieldDisabled}
                      className="w-full rounded-xl border border-slate-300 bg-slate-50 px-3 py-2 text-sm text-slate-700 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100 disabled:cursor-not-allowed disabled:bg-slate-100"
                    />
                  )}
                </label>
              );
            })}
          </div>

          {error ? (
            <div className="rounded-xl border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
              {error}
            </div>
          ) : null}
        </div>

        <div className="flex items-center justify-end gap-3 border-t border-slate-200 px-6 py-4">
          <button
            type="button"
            onClick={onCancel}
            className="rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 transition hover:bg-slate-50"
          >
            {cancelLabel}
          </button>
          <button
            type="submit"
            disabled={loading}
            className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {loading ? 'Сохраняем...' : confirmLabel}
          </button>
        </div>
      </form>
    </div>,
    document.body,
  );
}
