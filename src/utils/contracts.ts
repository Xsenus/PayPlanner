import type { Contract, ContractBillingMode } from '../types';

export type ContractExecutionState = 'actual' | 'pending' | 'in_progress' | 'completed' | 'overrun';

export type ContractReconciliationKind = 'invoices' | 'acts';

export type ContractReconciliationWarning = {
  kind: ContractReconciliationKind;
  label: string;
  amount: number;
  difference: number;
  percent: number;
};

export function getContractBillingModeLabel(mode: ContractBillingMode): string {
  return mode === 'Actual' ? 'Оплата по факту' : 'Фиксированная сумма';
}

export function getContractEffectiveAmount(contract: Pick<Contract, 'billingMode' | 'amount' | 'invoiceAmountTotal'>): number | null {
  if (contract.billingMode === 'Actual') {
    return contract.invoiceAmountTotal ?? 0;
  }

  return contract.amount ?? null;
}

export function getContractExecutionMetrics(contract: Pick<Contract, 'billingMode' | 'amount' | 'invoiceAmountTotal'>): {
  state: ContractExecutionState;
  label: string;
  percent: number | null;
  delta: number | null;
} {
  if (contract.billingMode === 'Actual') {
    return {
      state: 'actual',
      label: 'По факту',
      percent: null,
      delta: null,
    };
  }

  const target = contract.amount ?? 0;
  const used = contract.invoiceAmountTotal ?? 0;

  if (target <= 0) {
    return {
      state: used > 0 ? 'overrun' : 'pending',
      label: used > 0 ? 'Сверх суммы' : 'Не начат',
      percent: null,
      delta: target - used,
    };
  }

  const percent = Math.round((used / target) * 1000) / 10;
  const delta = target - used;

  if (used <= 0) {
    return { state: 'pending', label: 'Не начат', percent: 0, delta };
  }

  if (used < target) {
    return { state: 'in_progress', label: 'В работе', percent, delta };
  }

  if (used === target) {
    return { state: 'completed', label: 'Исполнен', percent: 100, delta: 0 };
  }

  return { state: 'overrun', label: 'Превышен', percent, delta };
}

export function getContractReconciliationWarnings(
  contract: Pick<Contract, 'billingMode' | 'amount' | 'invoiceAmountTotal' | 'actAmountTotal'>,
  thresholdPercent = 5,
): ContractReconciliationWarning[] {
  if (contract.billingMode !== 'Fixed' || !contract.amount || contract.amount <= 0) {
    return [];
  }

  const target = contract.amount;
  return [
    buildReconciliationWarning('invoices', 'счетов', contract.invoiceAmountTotal ?? 0, target, thresholdPercent),
    buildReconciliationWarning('acts', 'актов', contract.actAmountTotal ?? 0, target, thresholdPercent),
  ].filter((warning): warning is ContractReconciliationWarning => warning !== null);
}

function buildReconciliationWarning(
  kind: ContractReconciliationKind,
  entityLabel: string,
  amount: number,
  target: number,
  thresholdPercent: number,
): ContractReconciliationWarning | null {
  const difference = amount - target;
  const percent = Math.round((Math.abs(difference) / target) * 1000) / 10;
  if (percent <= thresholdPercent) {
    return null;
  }

  const direction = difference > 0 ? 'выше' : 'ниже';
  return {
    kind,
    label: `Сумма ${entityLabel} ${direction} суммы договора на ${percent}%`,
    amount,
    difference,
    percent,
  };
}
