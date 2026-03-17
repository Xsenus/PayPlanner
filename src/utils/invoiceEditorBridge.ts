import type { ActStatus, PaymentKind, PaymentStatus } from '../types';

export const INVOICE_EDITOR_STORAGE_KEY = 'pp.invoice-editor-draft';

export interface InvoiceEditorFormDraft {
  number: string;
  date: string;
  dueDate: string;
  amount: string;
  type: PaymentKind;
  status: PaymentStatus;
  clientId: string;
  contractId: string;
  clientCaseReference: string;
  description: string;
  actReference: string;
  paidDate: string;
  responsibleIds: string[];
}

export interface InvoiceEditorLineItemDraft {
  id: string;
  name: string;
  quantity: string;
  unit: string;
  price: string;
}

export interface InvoiceEditorActDraft {
  id: number;
  number: string;
  title?: string | null;
  date: string;
  amount: number;
  invoiceNumber?: string | null;
  clientId?: number | null;
  clientName?: string | null;
  status: ActStatus;
}

export interface InvoiceEditorDraftState {
  invoiceId?: number | null;
  mode: 'create' | 'edit';
  presentation: 'modal' | 'page';
  pageViewMode: boolean;
  form: InvoiceEditorFormDraft;
  lineItems: InvoiceEditorLineItemDraft[];
  selectedActs: InvoiceEditorActDraft[];
  selectedClientCaseIds: number[];
}

export function storeInvoiceEditorDraft(draft: InvoiceEditorDraftState) {
  if (typeof window === 'undefined') return;
  try {
    window.localStorage.setItem(INVOICE_EDITOR_STORAGE_KEY, JSON.stringify(draft));
  } catch {
    /* ignore storage errors */
  }
}

export function peekInvoiceEditorDraft(): InvoiceEditorDraftState | null {
  if (typeof window === 'undefined') return null;
  try {
    const raw = window.localStorage.getItem(INVOICE_EDITOR_STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as InvoiceEditorDraftState;
    return parsed ?? null;
  } catch {
    return null;
  }
}

export function clearInvoiceEditorDraft() {
  if (typeof window === 'undefined') return;
  try {
    window.localStorage.removeItem(INVOICE_EDITOR_STORAGE_KEY);
  } catch {
    /* ignore storage errors */
  }
}
