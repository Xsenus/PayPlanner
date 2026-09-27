import {
  AlertCircle,
  CalendarDays,
  CheckCircle2,
  ClipboardList,
  CreditCard,
  Info,
  Scale,
  ShieldAlert,
  StickyNote,
  X,
  type LucideIcon,
} from 'lucide-react';
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { ToastContext, type ToastAction, type ToastContextValue, type ToastDomain, type ToastOptions, type ToastVariant } from './toastContextCore';

type ToastRecord = Required<Pick<ToastOptions, 'description' | 'durationMs' | 'variant'>> & {
  id: string;
  title: string;
  actionLabel?: string;
  onAction?: () => void;
  actions?: ToastAction[];
  dedupeKey?: string;
  domain?: ToastDomain;
};

const MAX_VISIBLE_TOASTS = 3;
const VISUAL_AUDIT_EVENT = 'payplanner:visual-toast';

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

function toastStyles(variant: ToastVariant): { icon: LucideIcon; className: string } {
  switch (variant) {
    case 'success':
      return {
        icon: CheckCircle2,
        className: 'pp-toast-variant-success',
      };
    case 'error':
      return {
        icon: AlertCircle,
        className: 'pp-toast-variant-error',
      };
    default:
      return {
        icon: Info,
        className: 'pp-toast-variant-info',
      };
  }
}

function toastDomainStyles(domain?: ToastDomain): {
  icon?: LucideIcon;
  label?: string;
  className?: string;
} {
  switch (domain) {
    case 'task':
      return {
        icon: ClipboardList,
        label: 'Задача',
        className: 'pp-toast-domain-task',
      };
    case 'calendar':
      return {
        icon: CalendarDays,
        label: 'Календарь',
        className: 'pp-toast-domain-calendar',
      };
    case 'note':
      return {
        icon: StickyNote,
        label: 'Заметка',
        className: 'pp-toast-domain-note',
      };
    case 'payment':
      return {
        icon: CreditCard,
        label: 'Деньги',
        className: 'pp-toast-domain-payment',
      };
    case 'case':
      return {
        icon: Scale,
        label: 'Дело',
        className: 'pp-toast-domain-case',
      };
    case 'system':
      return {
        icon: ShieldAlert,
        label: 'Система',
        className: 'pp-toast-domain-system',
      };
    default:
      return {};
  }
}

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<ToastRecord[]>([]);
  const timersRef = useRef<Map<string, number>>(new Map());

  const clearToastTimer = useCallback((id: string) => {
    const timer = timersRef.current.get(id);
    if (timer) {
      window.clearTimeout(timer);
      timersRef.current.delete(id);
    }
  }, []);

  const dismissToast = useCallback((id: string) => {
    clearToastTimer(id);
    setToasts((prev) => prev.filter((toast) => toast.id !== id));
  }, [clearToastTimer]);

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
      actions: normalized.actions,
      dedupeKey: normalized.dedupeKey,
      domain: normalized.domain,
    };

    setToasts((prev) => {
      if (toast.dedupeKey) {
        const existing = prev.find((item) => item.dedupeKey === toast.dedupeKey);
        if (existing) {
          clearToastTimer(existing.id);
          return prev.map((item) => (item.id === existing.id ? toast : item));
        }
      }

      const next = [...prev, toast];
      const latestTimedIds = new Set(next
        .filter((item) => item.durationMs > 0)
        .slice(-MAX_VISIBLE_TOASTS)
        .map((item) => item.id));
      // A toast awaiting an explicit decision must not disappear when short-lived toasts arrive.
      const visible = next.filter((item) => item.durationMs === 0 || latestTimedIds.has(item.id));
      const visibleIds = new Set(visible.map((item) => item.id));
      next.forEach((item) => {
        if (!visibleIds.has(item.id)) {
          clearToastTimer(item.id);
        }
      });
      return visible;
    });

    if (toast.durationMs > 0) {
      const timer = window.setTimeout(() => {
        dismissToast(id);
      }, toast.durationMs);
      timersRef.current.set(id, timer);
    }

    return id;
  }, [clearToastTimer, dismissToast]);

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
    if (!import.meta.env.DEV || typeof window === 'undefined') return undefined;

    const visualAuditEnabled = new URLSearchParams(window.location.search).get('ppVisualAudit') === 'true';
    if (!visualAuditEnabled) return undefined;

    const handleVisualAuditToast = (event: Event) => {
      const detail = (event as CustomEvent<ToastOptions | string>).detail;
      if (detail) showToast(detail);
    };

    window.addEventListener(VISUAL_AUDIT_EVENT, handleVisualAuditToast);
    return () => window.removeEventListener(VISUAL_AUDIT_EVENT, handleVisualAuditToast);
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
            <div className="pointer-events-auto fixed bottom-3 left-3 right-3 z-[2000] flex max-h-[80vh] flex-col-reverse gap-3 overflow-y-auto sm:left-auto sm:right-4 sm:bottom-4 sm:w-[min(100vw-2rem,26rem)]" aria-live="polite" aria-atomic="true">
              {toasts.map((toast) => {
                const styles = toastStyles(toast.variant);
                const domainStyles = toastDomainStyles(toast.domain);
                const Icon = domainStyles.icon ?? styles.icon;
                return (
                  <div
                    key={toast.id}
                    role={toast.variant === 'error' ? 'alert' : 'status'}
                    data-testid="toast-card"
                    data-toast-variant={toast.variant}
                    data-toast-domain={toast.domain ?? 'general'}
                    className={`pp-toast-card pointer-events-auto shrink-0 ${styles.className} ${toast.variant === 'info' ? domainStyles.className ?? '' : ''}`}
                  >
                    <div className="grid grid-cols-[minmax(0,1fr)_auto] items-start gap-3 px-3.5 py-3.5 sm:px-4">
                      <div className="min-w-0">
                        <div data-testid="toast-header" className="flex min-w-0 flex-wrap items-center gap-2">
                          {domainStyles.label ? (
                            <span data-testid="toast-domain-pill" className="pp-toast-domain-pill inline-flex h-7 shrink-0 items-center gap-1.5 rounded-full px-2.5 text-[11px] font-semibold leading-none">
                              <Icon className="h-3.5 w-3.5" />
                              {domainStyles.label}
                            </span>
                          ) : (
                            <span className="pp-toast-icon inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-full">
                              <Icon className="h-4 w-4" />
                            </span>
                          )}
                          <div className="pp-toast-title min-w-0 flex-1 break-words text-sm font-semibold leading-5 sm:truncate">{toast.title}</div>
                        </div>
                        <div className="pp-toast-description mt-1.5 break-words text-sm leading-5">{toast.description}</div>
                        {toast.actions?.length ? (
                          <div data-testid="toast-actions" className="mt-3 grid grid-cols-2 gap-2 sm:grid-cols-[minmax(0,1fr)_auto_auto]">
                            {toast.actions.map((action, actionIndex) => (
                              <button
                                key={action.label}
                                type="button"
                                onClick={() => {
                                  void action.onClick();
                                  dismissToast(toast.id);
                                }}
                                className={
                                  action.variant === 'secondary'
                                    ? `${actionIndex === 0 || toast.actions?.length === 2 ? 'col-span-2' : 'col-span-1'} pp-toast-action-secondary inline-flex min-h-8 w-full items-center justify-center rounded-lg px-3 py-1.5 text-center text-xs font-semibold leading-4 transition sm:col-span-1 sm:w-auto`
                                    : `${actionIndex === 0 ? 'col-span-2' : 'col-span-1'} pp-toast-action-primary inline-flex min-h-8 w-full items-center justify-center rounded-lg px-3 py-1.5 text-center text-xs font-semibold leading-4 transition sm:col-span-1`
                                }
                              >
                                {action.label}
                              </button>
                            ))}
                          </div>
                        ) : toast.actionLabel && toast.onAction ? (
                          <button
                            type="button"
                            onClick={() => {
                              toast.onAction?.();
                              dismissToast(toast.id);
                            }}
                            className="pp-toast-action-primary mt-3 inline-flex min-h-8 w-full items-center justify-center rounded-lg px-3 py-1.5 text-center text-xs font-semibold leading-4 transition sm:w-auto"
                          >
                            {toast.actionLabel}
                          </button>
                        ) : null}
                      </div>
                      <button
                        type="button"
                        onClick={() => dismissToast(toast.id)}
                        className="pp-toast-close rounded-full p-1 transition"
                        aria-label="Закрыть уведомление"
                      >
                        <X className="h-4 w-4" />
                      </button>
                    </div>
                    {toast.durationMs > 0 ? (
                      <div className="pp-toast-progress-track h-1 w-full">
                        <div
                          className="pp-toast-progress h-full"
                          style={{
                            width: '100%',
                            animation: `toast-progress ${toast.durationMs}ms linear forwards`,
                          }}
                        />
                      </div>
                    ) : null}
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
