import type { Invoice, Payment } from '../types';
import {
  filterRelatedPaymentsForInvoiceWithOptions,
  filterRepresentativeInvoiceWhenInstallmentsExist,
} from './invoicePayments';

export const CLIENT_INVOICE_LOOKUP_FROM = '1900-01-01';
export const CLIENT_INVOICE_LOOKUP_TO = '2100-12-31';
export const CLIENT_INVOICE_RELATED_PAYMENTS_PAGE_SIZE = 500;

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
    from?: string;
    to?: string;
    clientId?: number;
    caseId?: number;
    invoiceId?: number;
    excludeInvoiceDocuments?: boolean;
    page?: number;
    pageSize?: number;
    sortBy?: 'date' | 'amount' | 'createdAt';
    sortDir?: 'asc' | 'desc';
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
  const invoicePaymentId = typeof invoice.id === 'number' && invoice.id > 0 ? invoice.id : undefined;

  if (invoicePaymentId) {
    try {
      const invoicePaymentPool = await fetchPayments({
        invoiceId: invoicePaymentId,
        excludeInvoiceDocuments: false,
        page: 1,
        pageSize: CLIENT_INVOICE_RELATED_PAYMENTS_PAGE_SIZE,
        sortBy: 'date',
        sortDir: 'asc',
      });
      const relatedPayments = collectClientInvoiceRelatedPayments(invoice, invoicePaymentPool);

      if (relatedPayments.length > 0) {
        return relatedPayments;
      }
    } catch {
      // If the direct invoice lookup is unavailable, keep the older client-wide lookup as a fallback.
    }
  }

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
