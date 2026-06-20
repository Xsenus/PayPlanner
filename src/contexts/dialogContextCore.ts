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

export interface PromptOptions {
  title: string;
  description?: string;
  label?: string;
  defaultValue?: string;
  placeholder?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  multiline?: boolean;
  inputType?: 'text' | 'email' | 'date';
}

export interface DialogContextValue {
  confirm: (options: ConfirmOptions | string) => Promise<boolean>;
  alert: (options: AlertOptions | string) => Promise<void>;
  prompt: (options: PromptOptions | string) => Promise<string | null>;
}

export const DialogContext = createContext<DialogContextValue | null>(null);
