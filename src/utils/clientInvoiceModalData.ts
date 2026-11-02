import type { Invoice, Payment } from '../types';
import { loadClientInvoiceRelatedPayments } from './clientInvoiceRelatedPayments';
import { assertFreshClientInvoice, readClientInvoice, isClientInvoiceReadCurrent, type ClientInvoiceReadContext } from './clientInvoiceRead';
import { StaleUiOperationError } from './StaleUiOperationError';
import type { ClientInvoiceModalPaymentsParams } from './clientInvoiceModalApi';

interface RefreshClientInvoiceModalDataOptions {
  selectedInvoice: Invoice | null;
  clientId: number;
  visiblePayments: Payment[];
  refreshInvoices: () => Promise<unknown>;
  refreshPayments: () => Promise<unknown>;
  fetchInvoice: (invoiceId: number, read?: ClientInvoiceReadContext) => Promise<Invoice>;
  fetchPayments: (params: ClientInvoiceModalPaymentsParams, read?: ClientInvoiceReadContext) => Promise<Payment[]>;
  read?: ClientInvoiceReadContext;
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
  read,
}: RefreshClientInvoiceModalDataOptions): Promise<RefreshedClientInvoiceModalData> {
  if (selectedInvoice) assertFreshClientInvoice(selectedInvoice, selectedInvoice, clientId);
  await readClientInvoice(refreshInvoices, read);
  if (!selectedInvoice?.id) {
    if (read?.canReadPayments?.() !== false) await readClientInvoice(refreshPayments, read);
    return {
      invoice: null,
      relatedPayments: [],
    };
  }

  const freshInvoice = await readClientInvoice(() => read ? fetchInvoice(selectedInvoice.id, read) : fetchInvoice(selectedInvoice.id), read);
  assertFreshClientInvoice(selectedInvoice, freshInvoice, clientId);
  const relatedPayments = read?.canReadPayments?.() === false ? [] : await loadClientInvoiceRelatedPayments({
    invoice: freshInvoice,
    clientId,
    visiblePayments,
    fetchPayments,
    read,
  });
  if (read?.canReadPayments?.() !== false) await readClientInvoice(refreshPayments, read);
  if (!isClientInvoiceReadCurrent(read)) throw new StaleUiOperationError();
  return {
    invoice: freshInvoice,
    relatedPayments,
  };
}
