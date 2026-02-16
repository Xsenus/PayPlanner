import { createContext } from 'react';

export type DialogVariant = 'primary' | 'danger' | 'success' | 'error';

export interface ConfirmOptions {
  title: string;
  description?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  variant?: DialogVariant;
}

export interface AlertOptions {
  title?: string;
  description: string;
  confirmLabel?: string;
  variant?: DialogVariant;
}

export interface DialogContextValue {
  confirm: (options: ConfirmOptions | string) => Promise<boolean>;
  alert: (options: AlertOptions | string) => Promise<void>;
}

export const DialogContext = createContext<DialogContextValue | null>(null);
