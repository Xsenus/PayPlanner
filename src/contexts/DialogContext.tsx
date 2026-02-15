import { useCallback, useMemo, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { DialogContext, type AlertOptions, type ConfirmOptions, type DialogContextValue, type DialogVariant } from './dialogContextCore';

interface ConfirmRequest extends ConfirmOptions {
  resolve: (result: boolean) => void;
}

interface AlertRequest extends AlertOptions {
  resolve: () => void;
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

  const value = useMemo<DialogContextValue>(() => ({ confirm, alert }), [alert, confirm]);

  return (
    <DialogContext.Provider value={value}>
      {children}
      {confirmRequest
        ? createPortal(
            <div className="fixed inset-0 z-[120] flex items-center justify-center bg-slate-900/40 p-4">
              <div className="w-full max-w-md rounded-2xl bg-white shadow-xl">
                <div className="border-b border-slate-200 px-6 py-4">
                  <h2 className="text-lg font-semibold text-slate-900">{confirmRequest.title}</h2>
                </div>
                <div className="px-6 py-4">
                  {confirmRequest.description ? <p className="text-sm text-slate-600">{confirmRequest.description}</p> : null}
                </div>
                <div className="flex items-center justify-end gap-3 border-t border-slate-200 px-6 py-4">
                  <button
                    onClick={() => {
                      confirmRequest.resolve(false);
                      setConfirmRequest(null);
                    }}
                    className="rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50">
                    {confirmRequest.cancelLabel ?? 'Отмена'}
                  </button>
                  <button
                    onClick={() => {
                      confirmRequest.resolve(true);
                      setConfirmRequest(null);
                    }}
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
            <div className="fixed inset-0 z-[120] flex items-center justify-center bg-slate-900/40 p-4">
              <div className="w-full max-w-md rounded-2xl bg-white shadow-xl">
                <div className="border-b border-slate-200 px-6 py-4">
                  <h2 className="text-lg font-semibold text-slate-900">{alertRequest.title ?? 'Уведомление'}</h2>
                </div>
                <div className="px-6 py-4">
                  <p className="text-sm text-slate-600">{alertRequest.description}</p>
                </div>
                <div className="flex items-center justify-end border-t border-slate-200 px-6 py-4">
                  <button
                    onClick={() => {
                      alertRequest.resolve();
                      setAlertRequest(null);
                    }}
                    className={`rounded-lg px-4 py-2 text-sm font-semibold text-white ${variantButtonClass(alertRequest.variant ?? 'primary')}`}>
                    {alertRequest.confirmLabel ?? 'ОК'}
                  </button>
                </div>
              </div>
            </div>,
            document.body,
          )
        : null}
    </DialogContext.Provider>
  );
}
