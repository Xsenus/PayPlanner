import type { Contract, ContractBillingMode } from '../types';

export type ContractExecutionState = 'actual' | 'pending' | 'in_progress' | 'completed' | 'overrun';

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
