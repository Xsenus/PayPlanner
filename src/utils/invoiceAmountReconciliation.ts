import type { Payment } from '../types';
import { getStatusSourcePaymentsForInvoice } from './invoicePayments';
import { parseInstallmentPaymentMetadata } from './installmentPlan';

export type InvoiceAmountMismatchDirection = 'increase' | 'decrease';

export interface InvoiceAmountReconciliation {
  direction: InvoiceAmountMismatchDirection;
  invoiceAmount: number;
  previousAmount: number | null;
  plannedAmount: number;
  paidAmount: number;
  difference: number;
  sourcePaymentCount: number;
}

const MONEY_EPSILON = 0.01;

function normalizeMoney(value: string | number | null | undefined): number {
  const parsed = typeof value === 'number'
    ? value
    : Number.parseFloat(String(value ?? '').replace(/\s+/g, '').replace(',', '.'));

  return Number.isFinite(parsed) ? Math.round(parsed * 100) / 100 : 0;
}

function resolvePaidAmount(payment: Payment): number {
  const installmentPrincipal = resolveInstallmentPrincipal(payment);
  const paidAmount = normalizeMoney(payment.paidAmount);
  if (paidAmount > 0) {
    return installmentPrincipal > 0 ? Math.min(paidAmount, installmentPrincipal) : paidAmount;
  }

  if (payment.isPaid || payment.status === 'Completed') {
    return installmentPrincipal > 0 ? installmentPrincipal : Math.max(normalizeMoney(payment.amount), 0);
  }

  return 0;
}

function resolveOutstandingAmount(payment: Payment): number {
  const installmentPrincipal = resolveInstallmentPrincipal(payment);
  if (payment.status === 'Completed' || payment.status === 'Cancelled') {
    return 0;
  }

  const outstandingAmount = normalizeMoney(payment.outstandingAmount);
  if (outstandingAmount > 0) {
    return outstandingAmount;
  }

  const expectedAmount = installmentPrincipal > 0 ? installmentPrincipal : normalizeMoney(payment.amount);
  return Math.max(expectedAmount - resolvePaidAmount(payment), 0);
}

function resolvePlannedAmount(payment: Payment): number {
  const installmentPrincipal = resolveInstallmentPrincipal(payment);
  if (installmentPrincipal > 0) {
    return installmentPrincipal;
  }

  return Math.max(
    normalizeMoney(payment.amount),
    resolvePaidAmount(payment) + resolveOutstandingAmount(payment),
  );
}

function resolveInstallmentPrincipal(payment: Payment): number {
  const metadata = parseInstallmentPaymentMetadata(payment.systemNotes);
  return normalizeMoney(metadata?.item?.principal);
}

export function buildInvoiceAmountReconciliation(
  payments: Payment[],
  previousAmount: number | string | null | undefined,
  nextAmount: number | string | null | undefined,
): InvoiceAmountReconciliation | null {
  const sourcePayments = getStatusSourcePaymentsForInvoice(payments)
    .filter((payment) => payment.status !== 'Cancelled');

  if (sourcePayments.length === 0) {
    return null;
  }

  const invoiceAmount = normalizeMoney(nextAmount);
  const normalizedPreviousAmount = previousAmount === null || previousAmount === undefined
    ? null
    : normalizeMoney(previousAmount);
  const plannedAmount = normalizeMoney(sourcePayments.reduce((sum, payment) => sum + resolvePlannedAmount(payment), 0));
  const paidAmount = normalizeMoney(sourcePayments.reduce((sum, payment) => sum + resolvePaidAmount(payment), 0));
  const expectedPaymentAmount = Math.max(plannedAmount, paidAmount);
  const rawDifference = normalizeMoney(invoiceAmount - expectedPaymentAmount);

  if (Math.abs(rawDifference) <= MONEY_EPSILON) {
    return null;
  }

  return {
    direction: rawDifference > 0 ? 'increase' : 'decrease',
    invoiceAmount,
    previousAmount: normalizedPreviousAmount,
    plannedAmount,
    paidAmount,
    difference: Math.abs(rawDifference),
    sourcePaymentCount: sourcePayments.length,
  };
}
