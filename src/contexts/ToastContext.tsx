import { AlertCircle, CheckCircle2, Info, X } from 'lucide-react';
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { ToastContext, type ToastContextValue, type ToastOptions, type ToastVariant } from './toastContextCore';

type ToastRecord = Required<Pick<ToastOptions, 'description' | 'durationMs' | 'variant'>> & {
  id: string;
  title: string;
  actionLabel?: string;
  onAction?: () => void;
};

function toToastOptions(options: ToastOptions | string): ToastOptions {
  if (typeof options === 'string') {
    return {
      description: options,
      variant: 'info',
    };
  }

  return options;
}

function defaultTitle(variant: ToastVariant): string {
  switch (variant) {
    case 'success':
      return 'Успешно';
    case 'error':
      return 'Ошибка';
    default:
      return 'Уведомление';
  }
}

function toastStyles(variant: ToastVariant): { icon: typeof Info; wrapper: string; iconColor: string; progress: string } {
  switch (variant) {
    case 'success':
      return {
        icon: CheckCircle2,
        wrapper: 'border-emerald-200 bg-emerald-50/95',
        iconColor: 'text-emerald-600',
        progress: 'bg-emerald-500',
      };
    case 'error':
      return {
        icon: AlertCircle,
        wrapper: 'border-rose-200 bg-rose-50/95',
        iconColor: 'text-rose-600',
        progress: 'bg-rose-500',
      };
    default:
      return {
        icon: Info,
        wrapper: 'border-sky-200 bg-white/95',
        iconColor: 'text-sky-600',
        progress: 'bg-sky-500',
      };
  }
}

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<ToastRecord[]>([]);
  const timersRef = useRef<Map<string, number>>(new Map());

  const dismissToast = useCallback((id: string) => {
    const timer = timersRef.current.get(id);
    if (timer) {
      window.clearTimeout(timer);
      timersRef.current.delete(id);
    }
    setToasts((prev) => prev.filter((toast) => toast.id !== id));
  }, []);

  const showToast = useCallback((options: ToastOptions | string) => {
    const normalized = toToastOptions(options);
    const variant = normalized.variant ?? 'info';
    const id = `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    const toast: ToastRecord = {
      id,
      title: normalized.title?.trim() || defaultTitle(variant),
      description: normalized.description,
      variant,
      durationMs: normalized.durationMs ?? (variant === 'error' ? 5200 : 3400),
      actionLabel: normalized.actionLabel,
      onAction: normalized.onAction,
    };

    setToasts((prev) => [...prev.slice(-3), toast]);

    if (toast.durationMs > 0) {
      const timer = window.setTimeout(() => {
        dismissToast(id);
      }, toast.durationMs);
      timersRef.current.set(id, timer);
    }

    return id;
  }, [dismissToast]);

  const success = useCallback((description: string, title?: string) => {
    return showToast({ title, description, variant: 'success' });
  }, [showToast]);

  const error = useCallback((description: string, title?: string) => {
    return showToast({ title, description, variant: 'error' });
  }, [showToast]);

  const info = useCallback((description: string, title?: string) => {
    return showToast({ title, description, variant: 'info' });
  }, [showToast]);

  useEffect(() => {
    const timers = timersRef.current;
    return () => {
      for (const timer of timers.values()) {
        window.clearTimeout(timer);
      }
      timers.clear();
    };
  }, []);

  const value = useMemo<ToastContextValue>(
    () => ({ showToast, dismissToast, success, error, info }),
    [dismissToast, error, info, showToast, success],
  );

  return (
    <ToastContext.Provider value={value}>
      {children}
      {typeof document !== 'undefined'
        ? createPortal(
            <div className="pointer-events-none fixed right-4 top-4 z-[2000] flex w-[min(100vw-2rem,24rem)] flex-col gap-3" aria-live="polite" aria-atomic="true">
              {toasts.map((toast) => {
                const styles = toastStyles(toast.variant);
                const Icon = styles.icon;
                return (
                  <div
                    key={toast.id}
                    role={toast.variant === 'error' ? 'alert' : 'status'}
                    className={`pointer-events-auto overflow-hidden rounded-2xl border shadow-xl backdrop-blur ${styles.wrapper}`}
                  >
                    <div className="flex items-start gap-3 px-4 py-3">
                      <div className={`mt-0.5 shrink-0 ${styles.iconColor}`}>
                        <Icon className="h-5 w-5" />
                      </div>
                      <div className="min-w-0 flex-1">
                        <div className="text-sm font-semibold text-slate-900">{toast.title}</div>
                        <div className="mt-1 break-words text-sm text-slate-700">{toast.description}</div>
                        {toast.actionLabel && toast.onAction ? (
                          <button
                            type="button"
                            onClick={() => {
                              toast.onAction?.();
                              dismissToast(toast.id);
                            }}
                            className="mt-3 rounded-lg bg-slate-900 px-3 py-1.5 text-xs font-semibold text-white transition hover:bg-slate-800"
                          >
                            {toast.actionLabel}
                          </button>
                        ) : null}
                      </div>
                      <button
                        type="button"
                        onClick={() => dismissToast(toast.id)}
                        className="rounded-full p-1 text-slate-400 transition hover:bg-white/70 hover:text-slate-600"
                        aria-label="Закрыть уведомление"
                      >
                        <X className="h-4 w-4" />
                      </button>
                    </div>
                    <div className="h-1 w-full bg-black/5">
                      <div
                        className={`h-full ${styles.progress}`}
                        style={{
                          width: '100%',
                          animation: `toast-progress ${toast.durationMs}ms linear forwards`,
                        }}
                      />
                    </div>
                  </div>
                );
              })}
            </div>,
            document.body,
          )
        : null}
    </ToastContext.Provider>
  );
}
