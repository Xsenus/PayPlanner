import { useEffect, useRef, useState, type FormEvent } from 'react';
import { createPortal } from 'react-dom';
import { Loader2, X } from 'lucide-react';
import { useModalFocusTrap } from '../../hooks/useModalFocusTrap';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import type { DocumentImagePrintOptions } from '../../types/documentImagePrint';
import { SystemCheckbox } from '../ui/SystemCheckbox';

interface Props {
  documentName: string;
  initialOptions: DocumentImagePrintOptions;
  confirmLabel: string;
  onConfirm: (options: DocumentImagePrintOptions, signal: AbortSignal) => Promise<void>;
  onClose: () => void;
}

// Mount with a key for the document/action identity. Each mounted dialog owns its request.
export function DocumentImagePrintDialog({ documentName, initialOptions, confirmLabel, onConfirm, onClose }: Props) {
  const [options, setOptions] = useState(() => ({ ...initialOptions }));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const request = useRef<AbortController | null>(null);
  const closed = useRef(false);
  const dialog = useRef<HTMLFormElement>(null);
  const cancel = useRef<HTMLButtonElement>(null);
  const close = () => {
    if (closed.current) return;
    closed.current = true;
    request.current?.abort();
    onClose();
  };
  const onKeyDown = useModalFocusTrap(dialog, true, close, cancel);
  useEffect(() => () => { request.current?.abort(); }, []);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    event.stopPropagation();
    if (request.current || closed.current) return;
    const controller = new AbortController();
    request.current = controller;
    setBusy(true);
    setError('');
    try {
      await onConfirm({ ...options }, controller.signal);
      if (!controller.signal.aborted && !closed.current) { closed.current = true; onClose(); }
    } catch (cause) {
      if (!controller.signal.aborted) setError(extractDisplayErrorMessage(cause, 'Не удалось подготовить документ. Повторите попытку.'));
    } finally {
      if (request.current === controller) request.current = null;
      if (!controller.signal.aborted) setBusy(false);
    }
  };

  return createPortal(
    <div className="fixed inset-0 z-[3000] flex items-center justify-center bg-slate-900/40 p-3">
      <form ref={dialog} role="dialog" aria-modal="true" aria-label="Изображения в документе" aria-busy={busy}
        onSubmit={(event) => void submit(event)} onKeyDown={(event) => { event.stopPropagation(); onKeyDown(event); }}
        className="max-h-[calc(100dvh-1.5rem)] w-full max-w-md overflow-y-auto rounded-xl bg-white shadow-xl">
        <div className="flex items-start justify-between gap-3 border-b border-slate-200 p-3">
          <div className="min-w-0">
            <h2 className="font-semibold text-slate-900">Изображения в документе</h2>
            <p className="mt-1 break-words text-sm text-slate-600">{documentName}</p>
          </div>
          <button type="button" aria-label="Закрыть" onClick={close} className="rounded p-1 text-slate-500"><X size={18} /></button>
        </div>
        <div className="space-y-3 p-3">
          <p className="text-sm text-slate-600">Выберите изображения для этой копии документа.</p>
          <fieldset disabled={busy} className="space-y-2">
            <legend className="sr-only">Печатать в документе</legend>
            {([
              ['includeLogo', 'Логотип'], ['includeSeal', 'Печать'], ['includeSignature', 'Подпись'],
            ] as const).map(([key, label]) => (
              <label key={key} className="flex items-center gap-2 rounded-lg border border-slate-200 p-3 text-sm text-slate-800">
                <SystemCheckbox checked={options[key]} onChange={(event) => setOptions((current) => ({ ...current, [key]: event.target.checked }))} />
                {label}
              </label>
            ))}
          </fieldset>
          <p className="text-xs text-slate-500">Изображения печатаются, если они есть в реквизитах документа и предусмотрены выбранным шаблоном.</p>
          {error ? <p role="alert" className="rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">{error}</p> : null}
        </div>
        <div className="flex justify-end gap-2 border-t border-slate-200 p-3">
          <button ref={cancel} type="button" onClick={close} className="rounded-lg border border-slate-300 px-3 py-2 text-sm">Отмена</button>
          <button type="submit" disabled={busy} className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-50">
            {busy ? <Loader2 size={16} className="animate-spin" /> : null}{busy ? 'Готовим документ…' : confirmLabel}
          </button>
        </div>
      </form>
    </div>, document.body,
  );
}
