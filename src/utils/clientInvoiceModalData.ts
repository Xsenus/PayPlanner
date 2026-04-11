import type { Invoice, Payment } from '../types';
import { loadClientInvoiceRelatedPayments } from './clientInvoiceRelatedPayments';

interface RefreshClientInvoiceModalDataOptions {
  selectedInvoice: Invoice | null;
  clientId: number;
  visiblePayments: Payment[];
  refreshInvoices: () => Promise<unknown>;
  refreshPayments: () => Promise<unknown>;
  fetchInvoice: (invoiceId: number) => Promise<Invoice>;
  fetchPayments: (params: {
    from: string;
    to: string;
    clientId: number;
    caseId?: number;
  }) => Promise<Payment[]>;
}

interface RefreshedClientInvoiceModalData {
  invoice: Invoice | null;
  relatedPayments: Payment[];
}

export async function refreshClientInvoiceModalData({
  selectedInvoice,
  clientId,
  visiblePayments,
  refreshInvoices,
  refreshPayments,
  fetchInvoice,
  fetchPayments,
}: RefreshClientInvoiceModalDataOptions): Promise<RefreshedClientInvoiceModalData> {
  await Promise.all([refreshInvoices(), refreshPayments()]);

  if (!selectedInvoice?.id) {
    return {
      invoice: null,
      relatedPayments: [],
    };
  }

  try {
    const freshInvoice = await fetchInvoice(selectedInvoice.id);
    const relatedPayments = await loadClientInvoiceRelatedPayments({
      invoice: freshInvoice,
      clientId,
      visiblePayments,
      fetchPayments,
    });

    return {
      invoice: freshInvoice,
      relatedPayments,
    };
  } catch {
    const relatedPayments = await loadClientInvoiceRelatedPayments({
      invoice: selectedInvoice,
      clientId,
      visiblePayments,
      fetchPayments,
    });

    return {
      invoice: selectedInvoice,
      relatedPayments,
    };
  }
}
