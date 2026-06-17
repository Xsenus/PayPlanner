import { createContext } from 'react';

export type ToastVariant = 'info' | 'success' | 'error';

export interface ToastOptions {
  title?: string;
  description: string;
  variant?: ToastVariant;
  durationMs?: number;
  actionLabel?: string;
  onAction?: () => void;
  dedupeKey?: string;
}

export interface ToastContextValue {
  showToast: (options: ToastOptions | string) => string;
  dismissToast: (id: string) => void;
  success: (description: string, title?: string) => string;
  error: (description: string, title?: string) => string;
  info: (description: string, title?: string) => string;
}

export const ToastContext = createContext<ToastContextValue | null>(null);
