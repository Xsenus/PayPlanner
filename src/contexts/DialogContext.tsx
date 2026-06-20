import { useCallback, useMemo, useState, type FormEvent, type KeyboardEvent, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { DialogContext, type AlertOptions, type ConfirmOptions, type DialogContextValue, type DialogVariant, type PromptOptions } from './dialogContextCore';

interface ConfirmRequest extends ConfirmOptions {
  resolve: (result: boolean) => void;
}

interface AlertRequest extends AlertOptions {
  resolve: () => void;
}

interface PromptRequest extends PromptOptions {
  value: string;
  resolve: (result: string | null) => void;
}

function toConfirmOptions(options: ConfirmOptions | string): ConfirmOptions {
  if (typeof options === 'string') {
    return { title: 'Подтверждение', description: options, variant: 'primary' };
  }
  return options;
}

function toAlertOptions(options: AlertOptions | string): AlertOptions {
  if (typeof options === 'string') {
    return { title: 'Уведомление', description: options, variant: 'primary' };
  }
  return options;
}

function toPromptOptions(options: PromptOptions | string): PromptOptions {
  if (typeof options === 'string') {
    return { title: 'Введите значение', label: options };
  }
  return options;
}

function variantButtonClass(variant: DialogVariant): string {
  switch (variant) {
    case 'danger':
    case 'error':
      return 'bg-red-600 hover:bg-red-700';
    case 'success':
      return 'bg-emerald-600 hover:bg-emerald-700';
    default:
      return 'bg-blue-600 hover:bg-blue-700';
  }
}

export function DialogProvider({ children }: { children: ReactNode }) {
  const [confirmRequest, setConfirmRequest] = useState<ConfirmRequest | null>(null);
  const [alertRequest, setAlertRequest] = useState<AlertRequest | null>(null);
  const [promptRequest, setPromptRequest] = useState<PromptRequest | null>(null);

  const confirm = useCallback((options: ConfirmOptions | string) => {
    return new Promise<boolean>((resolve) => {
      setConfirmRequest({ ...toConfirmOptions(options), resolve });
    });
  }, []);

  const alert = useCallback((options: AlertOptions | string) => {
    return new Promise<void>((resolve) => {
      setAlertRequest({ ...toAlertOptions(options), resolve });
    });
  }, []);

  const prompt = useCallback((options: PromptOptions | string) => {
    return new Promise<string | null>((resolve) => {
      const promptOptions = toPromptOptions(options);
      setPromptRequest({
        ...promptOptions,
        value: promptOptions.defaultValue ?? '',
        resolve,
      });
    });
  }, []);

  const value = useMemo<DialogContextValue>(() => ({ confirm, alert, prompt }), [alert, confirm, prompt]);

  const cancelConfirmRequest = () => {
    setConfirmRequest((current) => {
      current?.resolve(false);
      return null;
    });
  };

  const acceptConfirmRequest = () => {
    setConfirmRequest((current) => {
      current?.resolve(true);
      return null;
    });
  };

  const closeAlertRequest = () => {
    setAlertRequest((current) => {
      current?.resolve();
      return null;
    });
  };

  const handleConfirmKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key !== 'Escape') return;

    event.preventDefault();
    cancelConfirmRequest();
  };

  const handleAlertKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key !== 'Escape') return;

    event.preventDefault();
    closeAlertRequest();
  };

  const handlePromptSubmit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!promptRequest) return;
    promptRequest.resolve(promptRequest.value);
    setPromptRequest(null);
  };

  const cancelPromptRequest = () => {
    if (!promptRequest) return;
    promptRequest.resolve(null);
    setPromptRequest(null);
  };

  const handlePromptKeyDown = (event: KeyboardEvent<HTMLFormElement>) => {
    if (event.key !== 'Escape') return;

    event.preventDefault();
    cancelPromptRequest();
  };

  return (
    <DialogContext.Provider value={value}>
      {children}
      {confirmRequest
        ? createPortal(
            <div className="fixed inset-0 z-[2400] flex items-center justify-center bg-slate-900/40 p-4">
              <div
                role="dialog"
                aria-modal="true"
                aria-label={confirmRequest.title}
                onKeyDown={handleConfirmKeyDown}
                className="w-full max-w-md rounded-2xl bg-white shadow-xl">
                <div className="border-b border-slate-200 px-6 py-4">
                  <h2 className="text-lg font-semibold text-slate-900">{confirmRequest.title}</h2>
                </div>
                <div className="px-6 py-4">
                  {confirmRequest.description ? <p className="text-sm text-slate-600">{confirmRequest.description}</p> : null}
                </div>
                <div className="flex items-center justify-end gap-3 border-t border-slate-200 px-6 py-4">
                  <button
                    type="button"
                    autoFocus
                    onClick={cancelConfirmRequest}
                    className="rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50">
                    {confirmRequest.cancelLabel ?? 'Отмена'}
                  </button>
                  <button
                    type="button"
                    onClick={acceptConfirmRequest}
                    className={`rounded-lg px-4 py-2 text-sm font-semibold text-white ${variantButtonClass(confirmRequest.variant ?? 'primary')}`}>
                    {confirmRequest.confirmLabel ?? 'Подтвердить'}
                  </button>
                </div>
              </div>
            </div>,
            document.body,
          )
        : null}
      {alertRequest
        ? createPortal(
            <div className="fixed inset-0 z-[2400] flex items-center justify-center bg-slate-900/40 p-4">
              <div
                role="dialog"
                aria-modal="true"
                aria-label={alertRequest.title ?? 'Уведомление'}
                onKeyDown={handleAlertKeyDown}
                className="w-full max-w-md rounded-2xl bg-white shadow-xl">
                <div className="border-b border-slate-200 px-6 py-4">
                  <h2 className="text-lg font-semibold text-slate-900">{alertRequest.title ?? 'Уведомление'}</h2>
                </div>
                <div className="px-6 py-4">
                  <p className="text-sm text-slate-600">{alertRequest.description}</p>
                </div>
                <div className="flex items-center justify-end border-t border-slate-200 px-6 py-4">
                  <button
                    type="button"
                    autoFocus
                    onClick={closeAlertRequest}
                    className={`rounded-lg px-4 py-2 text-sm font-semibold text-white ${variantButtonClass(alertRequest.variant ?? 'primary')}`}>
                    {alertRequest.confirmLabel ?? 'ОК'}
                  </button>
                </div>
              </div>
            </div>,
            document.body,
          )
        : null}
      {promptRequest
        ? createPortal(
            <div className="fixed inset-0 z-[2400] flex items-center justify-center bg-slate-900/40 p-4">
              <form
                onSubmit={handlePromptSubmit}
                onKeyDown={handlePromptKeyDown}
                role="dialog"
                aria-modal="true"
                aria-label={promptRequest.title}
                className="w-full max-w-md rounded-2xl bg-white shadow-xl">
                <div className="border-b border-slate-200 px-6 py-4">
                  <h2 className="text-lg font-semibold text-slate-900">{promptRequest.title}</h2>
                </div>
                <div className="space-y-3 px-6 py-4">
                  {promptRequest.description ? <p className="text-sm text-slate-600">{promptRequest.description}</p> : null}
                  <label className="block">
                    <span className="mb-2 block text-sm font-medium text-slate-700">{promptRequest.label ?? 'Значение'}</span>
                    {promptRequest.multiline ? (
                      <textarea
                        autoFocus
                        value={promptRequest.value}
                        onChange={(event) => {
                          const nextValue = event.target.value;
                          setPromptRequest((current) => (current ? { ...current, value: nextValue } : current));
                        }}
                        placeholder={promptRequest.placeholder}
                        rows={4}
                        className="w-full rounded-xl border border-slate-300 bg-slate-50 px-3 py-2 text-sm text-slate-700 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                      />
                    ) : (
                      <input
                        autoFocus
                        type={promptRequest.inputType ?? 'text'}
                        value={promptRequest.value}
                        onChange={(event) => {
                          const nextValue = event.target.value;
                          setPromptRequest((current) => (current ? { ...current, value: nextValue } : current));
                        }}
                        placeholder={promptRequest.placeholder}
                        className="w-full rounded-xl border border-slate-300 bg-slate-50 px-3 py-2 text-sm text-slate-700 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                      />
                    )}
                  </label>
                </div>
                <div className="flex items-center justify-end gap-3 border-t border-slate-200 px-6 py-4">
                  <button
                    type="button"
                    onClick={cancelPromptRequest}
                    className="rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50">
                    {promptRequest.cancelLabel ?? 'Отмена'}
                  </button>
                  <button type="submit" className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white hover:bg-blue-700">
                    {promptRequest.confirmLabel ?? 'Подтвердить'}
                  </button>
                </div>
              </form>
            </div>,
            document.body,
          )
        : null}
    </DialogContext.Provider>
  );
}
