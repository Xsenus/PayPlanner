export const INSTALLMENT_STORAGE_KEY = 'pp.installment-draft';

export interface InstallmentDraftPayload {
  total?: number;
  startDate?: string;
  clientId?: number;
  clientName?: string;
  invoiceNumber?: string;
  roundingMode?: string;
  roundingStep?: number;
  timestamp?: number;
}

export function storeInstallmentDraft(draft: InstallmentDraftPayload) {
  if (typeof window === 'undefined') return;
  try {
    const payload = { ...draft, timestamp: Date.now() } satisfies InstallmentDraftPayload;
    window.localStorage.setItem(INSTALLMENT_STORAGE_KEY, JSON.stringify(payload));
  } catch {
    /* ignore storage errors */
  }
}

export function consumeInstallmentDraft(): InstallmentDraftPayload | null {
  if (typeof window === 'undefined') return null;
  try {
    const raw = window.localStorage.getItem(INSTALLMENT_STORAGE_KEY);
    if (!raw) return null;
    window.localStorage.removeItem(INSTALLMENT_STORAGE_KEY);
    const parsed = JSON.parse(raw) as InstallmentDraftPayload;
    return parsed ?? null;
  } catch {
    return null;
  }
}
