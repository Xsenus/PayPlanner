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
import { ToastContext, type ToastContextValue, type ToastDomain, type ToastOptions, type ToastVariant } from './toastContextCore';

type ToastRecord = Required<Pick<ToastOptions, 'description' | 'durationMs' | 'variant'>> & {
  id: string;
  title: string;
  actionLabel?: string;
  onAction?: () => void;
  dedupeKey?: string;
  domain?: ToastDomain;
};

const MAX_VISIBLE_TOASTS = 3;

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

function toastStyles(variant: ToastVariant): { icon: LucideIcon; wrapper: string; iconColor: string; progress: string } {
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

function toastDomainStyles(domain?: ToastDomain): {
  icon?: LucideIcon;
  label?: string;
  iconColor?: string;
  borderColor?: string;
  progress?: string;
  badge?: string;
} {
  switch (domain) {
    case 'task':
      return {
        icon: ClipboardList,
        label: 'Задача',
        iconColor: 'text-blue-600',
        borderColor: 'border-l-blue-500',
        progress: 'bg-blue-500',
        badge: 'bg-blue-50 text-blue-700 ring-blue-100',
      };
    case 'calendar':
      return {
        icon: CalendarDays,
        label: 'Календарь',
        iconColor: 'text-cyan-600',
        borderColor: 'border-l-cyan-500',
        progress: 'bg-cyan-500',
        badge: 'bg-cyan-50 text-cyan-700 ring-cyan-100',
      };
    case 'note':
      return {
        icon: StickyNote,
        label: 'Заметка',
        iconColor: 'text-amber-600',
        borderColor: 'border-l-amber-500',
        progress: 'bg-amber-500',
        badge: 'bg-amber-50 text-amber-700 ring-amber-100',
      };
    case 'payment':
      return {
        icon: CreditCard,
        label: 'Деньги',
        iconColor: 'text-emerald-600',
        borderColor: 'border-l-emerald-500',
        progress: 'bg-emerald-500',
        badge: 'bg-emerald-50 text-emerald-700 ring-emerald-100',
      };
    case 'case':
      return {
        icon: Scale,
        label: 'Дело',
        iconColor: 'text-violet-600',
        borderColor: 'border-l-violet-500',
        progress: 'bg-violet-500',
        badge: 'bg-violet-50 text-violet-700 ring-violet-100',
      };
    case 'system':
      return {
        icon: ShieldAlert,
        label: 'Система',
        iconColor: 'text-slate-600',
        borderColor: 'border-l-slate-500',
        progress: 'bg-slate-500',
        badge: 'bg-slate-100 text-slate-700 ring-slate-200',
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
      const visible = next.slice(-MAX_VISIBLE_TOASTS);
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
                const domainStyles = toastDomainStyles(toast.domain);
                const Icon = domainStyles.icon ?? styles.icon;
                const iconColor = toast.variant === 'info' && domainStyles.iconColor
                  ? domainStyles.iconColor
                  : styles.iconColor;
                const progress = toast.variant === 'info' && domainStyles.progress
                  ? domainStyles.progress
                  : styles.progress;
                return (
                  <div
                    key={toast.id}
                    role={toast.variant === 'error' ? 'alert' : 'status'}
                    className={`pointer-events-auto overflow-hidden rounded-2xl border border-l-4 shadow-xl backdrop-blur ${styles.wrapper} ${domainStyles.borderColor ?? 'border-l-sky-400'}`}
                  >
                    <div className="flex items-start gap-3 px-4 py-3">
                      <div className={`mt-0.5 shrink-0 ${iconColor}`}>
                        <Icon className="h-5 w-5" />
                      </div>
                      <div className="min-w-0 flex-1">
                        <div className="flex min-w-0 flex-wrap items-center gap-2">
                          {domainStyles.label ? (
                            <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-semibold ring-1 ${domainStyles.badge}`}>
                              {domainStyles.label}
                            </span>
                          ) : null}
                          <div className="min-w-0 text-sm font-semibold text-slate-900">{toast.title}</div>
                        </div>
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
                        className={`h-full ${progress}`}
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
