import { X } from 'lucide-react';
import { type FormEvent } from 'react';
import { createPortal } from 'react-dom';

interface TextPromptModalProps {
  open: boolean;
  title: string;
  description?: string;
  label: string;
  value: string;
  onValueChange: (value: string) => void;
  onCancel: () => void;
  onConfirm: () => void | Promise<void>;
  confirmLabel?: string;
  cancelLabel?: string;
  placeholder?: string;
  error?: string;
  loading?: boolean;
  multiline?: boolean;
}

export function TextPromptModal({
  open,
  title,
  description,
  label,
  value,
  onValueChange,
  onCancel,
  onConfirm,
  confirmLabel = 'Подтвердить',
  cancelLabel = 'Отмена',
  placeholder,
  error,
  loading = false,
  multiline = false,
}: TextPromptModalProps) {
  if (!open || typeof document === 'undefined') return null;

  const handleSubmit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    void onConfirm();
  };

  return createPortal(
    <div className="fixed inset-0 z-[2200] flex items-center justify-center bg-slate-900/40 p-4">
      <form
        onSubmit={handleSubmit}
        className="w-full max-w-lg rounded-2xl bg-white shadow-xl"
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

          <label className="block">
            <span className="mb-2 block text-sm font-medium text-slate-700">{label}</span>
            {multiline ? (
              <textarea
                value={value}
                onChange={(event) => onValueChange(event.target.value)}
                placeholder={placeholder}
                rows={4}
                className="w-full rounded-xl border border-slate-300 bg-slate-50 px-3 py-2 text-sm text-slate-700 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
              />
            ) : (
              <input
                value={value}
                onChange={(event) => onValueChange(event.target.value)}
                placeholder={placeholder}
                className="w-full rounded-xl border border-slate-300 bg-slate-50 px-3 py-2 text-sm text-slate-700 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
              />
            )}
          </label>

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
            {loading ? 'Сохранение...' : confirmLabel}
          </button>
        </div>
      </form>
    </div>,
    document.body,
  );
}
