import type {
  InstallmentPlanInput,
  InstallmentPlanItemInput,
  InstallmentRequest,
  InstallmentResponse,
  InstallmentRoundingMode,
  Payment,
} from '../types';
import { toDateInputValue } from './dateUtils';

export const INSTALLMENT_PAYMENT_MARKER = '[pp:installment-payment]';

export interface InstallmentPaymentMetadata {
  sequence: number;
  total: number;
  downPayment: number;
  annualRate: number;
  months: number;
  startDate: string;
  roundingMode: InstallmentRoundingMode;
  roundingStep?: number | null;
  toPay: number;
  overpay: number;
  loanAmount?: number | null;
  baseMonthlyPayment?: number | null;
  roundedMonthlyPayment?: number | null;
  item: InstallmentPlanItemInput;
}

function normalizeNumber(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0;
}

function readMetadataField(record: Record<string, unknown>, field: string): unknown {
  const pascalField = `${field.charAt(0).toUpperCase()}${field.slice(1)}`;
  return record[field] ?? record[pascalField];
}

function normalizeRoundingMode(value: unknown): InstallmentRoundingMode {
  if (value === 1 || String(value).toLowerCase() === 'down') return 'down';
  if (value === 2 || String(value).toLowerCase() === 'up') return 'up';
  return 'none';
}

export function createInstallmentPlanInput(
  request: InstallmentRequest,
  response: InstallmentResponse,
): InstallmentPlanInput {
  return {
    total: normalizeNumber(request.total),
    downPayment: normalizeNumber(request.downPayment),
    annualRate: normalizeNumber(request.annualRate),
    months: Math.max(1, Math.trunc(normalizeNumber(request.months))),
    startDate: toDateInputValue(request.startDate) || request.startDate,
    roundingMode: (request.roundingMode ?? 'none') as InstallmentRoundingMode,
    roundingStep: request.roundingStep ?? null,
    toPay: normalizeNumber(response.toPay),
    overpay: normalizeNumber(response.overpay),
    loanAmount: response.loanAmount ?? null,
    baseMonthlyPayment: response.baseMonthlyPayment ?? null,
    roundedMonthlyPayment: response.roundedMonthlyPayment ?? null,
    items: (response.items ?? []).map((item, index) => ({
      sequence: index + 1,
      date: toDateInputValue(item.date) || item.date,
      principal: normalizeNumber(item.principal),
      interest: normalizeNumber(item.interest),
      payment: normalizeNumber(item.payment),
      balance: normalizeNumber(item.balance),
    })),
  };
}

export function buildInstallmentResponseFromPlan(plan: InstallmentPlanInput): InstallmentResponse {
  return {
    overpay: normalizeNumber(plan.overpay),
    toPay: normalizeNumber(plan.toPay),
    loanAmount: plan.loanAmount ?? undefined,
    baseMonthlyPayment: plan.baseMonthlyPayment ?? undefined,
    roundedMonthlyPayment: plan.roundedMonthlyPayment ?? null,
    roundingMode: plan.roundingMode ?? 'none',
    roundingStep: plan.roundingStep ?? null,
    items: (plan.items ?? []).map((item) => ({
      date: toDateInputValue(item.date) || item.date,
      principal: normalizeNumber(item.principal),
      interest: normalizeNumber(item.interest),
      payment: normalizeNumber(item.payment),
      balance: normalizeNumber(item.balance),
    })),
  };
}

export function cloneInstallmentPlan(plan: InstallmentPlanInput | null | undefined): InstallmentPlanInput | null {
  if (!plan) return null;
  return {
    ...plan,
    items: (plan.items ?? []).map((item) => ({ ...item })),
  };
}

export function parseInstallmentPaymentMetadata(systemNotes?: string | null): InstallmentPaymentMetadata | null {
  const markerLine = (systemNotes ?? '')
    .split('\n')
    .map((line) => line.trim())
    .find((line) => line.startsWith(INSTALLMENT_PAYMENT_MARKER));

  if (!markerLine) {
    return null;
  }

  const rawJson = markerLine.slice(INSTALLMENT_PAYMENT_MARKER.length).trim();
  if (!rawJson) {
    return null;
  }

  try {
    const parsed = JSON.parse(rawJson) as Record<string, unknown>;
    if (!parsed || typeof parsed !== 'object') {
      return null;
    }

    const item = readMetadataField(parsed, 'item') as Record<string, unknown> | undefined;
    if (!item || typeof item !== 'object') {
      return null;
    }

    const sequence = Math.max(1, Math.trunc(normalizeNumber(readMetadataField(parsed, 'sequence'))));
    const itemDate = readMetadataField(item, 'date');
    const date = toDateInputValue(typeof itemDate === 'string' ? itemDate : '') || '';
    if (!date) {
      return null;
    }

    const startDate = readMetadataField(parsed, 'startDate');
    const roundingStep = readMetadataField(parsed, 'roundingStep');
    const loanAmount = readMetadataField(parsed, 'loanAmount');
    const baseMonthlyPayment = readMetadataField(parsed, 'baseMonthlyPayment');
    const roundedMonthlyPayment = readMetadataField(parsed, 'roundedMonthlyPayment');

    return {
      sequence,
      total: normalizeNumber(readMetadataField(parsed, 'total')),
      downPayment: normalizeNumber(readMetadataField(parsed, 'downPayment')),
      annualRate: normalizeNumber(readMetadataField(parsed, 'annualRate')),
      months: Math.max(1, Math.trunc(normalizeNumber(readMetadataField(parsed, 'months')))),
      startDate: toDateInputValue(typeof startDate === 'string' ? startDate : '') || '',
      roundingMode: normalizeRoundingMode(readMetadataField(parsed, 'roundingMode')),
      roundingStep:
        typeof roundingStep === 'number' && Number.isFinite(roundingStep)
          ? roundingStep
          : null,
      toPay: normalizeNumber(readMetadataField(parsed, 'toPay')),
      overpay: normalizeNumber(readMetadataField(parsed, 'overpay')),
      loanAmount:
        typeof loanAmount === 'number' && Number.isFinite(loanAmount)
          ? loanAmount
          : null,
      baseMonthlyPayment:
        typeof baseMonthlyPayment === 'number' && Number.isFinite(baseMonthlyPayment)
          ? baseMonthlyPayment
          : null,
      roundedMonthlyPayment:
        typeof roundedMonthlyPayment === 'number' && Number.isFinite(roundedMonthlyPayment)
          ? roundedMonthlyPayment
          : null,
      item: {
        sequence,
        date,
        principal: normalizeNumber(readMetadataField(item, 'principal')),
        interest: normalizeNumber(readMetadataField(item, 'interest')),
        payment: normalizeNumber(readMetadataField(item, 'payment')),
        balance: normalizeNumber(readMetadataField(item, 'balance')),
      },
    };
  } catch {
    return null;
  }
}

export function deriveInstallmentPlanFromPayments(payments: Payment[]): InstallmentPlanInput | null {
  const metadataItems = payments
    .filter((payment) => payment.documentKind !== 'Invoice')
    .map((payment) => parseInstallmentPaymentMetadata(payment.systemNotes))
    .filter((item): item is InstallmentPaymentMetadata => item !== null)
    .sort((left, right) => left.sequence - right.sequence);

  if (metadataItems.length === 0) {
    return null;
  }

  const first = metadataItems[0];

  return {
    total: first.total,
    downPayment: first.downPayment,
    annualRate: first.annualRate,
    months: first.months,
    startDate: first.startDate,
    roundingMode: first.roundingMode,
    roundingStep: first.roundingStep ?? null,
    toPay: first.toPay,
    overpay: first.overpay,
    loanAmount: first.loanAmount ?? null,
    baseMonthlyPayment: first.baseMonthlyPayment ?? null,
    roundedMonthlyPayment: first.roundedMonthlyPayment ?? null,
    items: metadataItems.map((item) => ({ ...item.item })),
  };
}
