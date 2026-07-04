import type { LegalEntitySummary, Payment } from '../types';

type PaymentCounterpartySource = Pick<
  Payment,
  'counterpartyName' | 'client' | 'legalEntityName' | 'legalEntity'
>;

function normalize(value?: string | null): string {
  return value?.trim() ?? '';
}

function getLegalEntityDisplayName(
  legalEntity?: Pick<LegalEntitySummary, 'shortName' | 'fullName'> | null,
  explicitName?: string | null,
): string {
  return normalize(explicitName) || normalize(legalEntity?.shortName) || normalize(legalEntity?.fullName);
}

export function getPaymentCounterpartyName(payment: PaymentCounterpartySource): string {
  return normalize(payment.counterpartyName)
    || normalize(payment.client?.name)
    || getLegalEntityDisplayName(payment.legalEntity, payment.legalEntityName);
}
