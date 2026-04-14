import type { Invoice, Payment } from '../types';
import {
  filterRelatedPaymentsForInvoiceWithOptions,
  filterRepresentativeInvoiceWhenInstallmentsExist,
} from './invoicePayments';

export const CLIENT_INVOICE_LOOKUP_FROM = '1900-01-01';
export const CLIENT_INVOICE_LOOKUP_TO = '2100-12-31';

export function resolveClientInvoiceCaseId(invoice?: Invoice | null): number | undefined {
  if (!invoice) {
    return undefined;
  }

  if (Array.isArray(invoice.clientCaseIds) && invoice.clientCaseIds.length === 1) {
    const onlyCaseId = invoice.clientCaseIds[0];
    return typeof onlyCaseId === 'number' && onlyCaseId > 0 ? onlyCaseId : undefined;
  }

  return typeof invoice.clientCaseId === 'number' && invoice.clientCaseId > 0
    ? invoice.clientCaseId
    : undefined;
}

export function collectClientInvoiceRelatedPayments(
  invoice: Invoice | null | undefined,
  payments: Payment[],
): Payment[] {
  if (!invoice) {
    return [];
  }

  const relatedPayments = filterRelatedPaymentsForInvoiceWithOptions(payments, invoice, {
    includeCurrentInvoiceRecord: true,
  });

  return filterRepresentativeInvoiceWhenInstallmentsExist(relatedPayments);
}

interface LoadClientInvoiceRelatedPaymentsOptions {
  invoice: Invoice | null | undefined;
  clientId: number;
  visiblePayments: Payment[];
  fetchPayments: (params: {
    from: string;
    to: string;
    clientId: number;
    caseId?: number;
  }) => Promise<Payment[]>;
}

export async function loadClientInvoiceRelatedPayments({
  invoice,
  clientId,
  visiblePayments,
  fetchPayments,
}: LoadClientInvoiceRelatedPaymentsOptions): Promise<Payment[]> {
  if (!invoice) {
    return [];
  }

  const caseId = resolveClientInvoiceCaseId(invoice);

  try {
    const paymentPool = await fetchPayments({
      from: CLIENT_INVOICE_LOOKUP_FROM,
      to: CLIENT_INVOICE_LOOKUP_TO,
      clientId,
      caseId,
    });

    return collectClientInvoiceRelatedPayments(invoice, paymentPool);
  } catch {
    return collectClientInvoiceRelatedPayments(invoice, visiblePayments);
  }
}
