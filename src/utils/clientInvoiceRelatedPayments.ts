import type { Invoice, Payment } from '../types';
import { filterRelatedPaymentsForInvoiceWithOptions } from './invoicePayments';

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

  return filterRelatedPaymentsForInvoiceWithOptions(payments, invoice, {
    includeCurrentInvoiceRecord: true,
  });
}
