import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import type { Client } from '../../types';
import { InstallmentPlanner } from './InstallmentPlanner';

const {
  confirmMock,
  toastErrorMock,
  toastSuccessMock,
  calculateInstallmentMock,
} = vi.hoisted(() => ({
  confirmMock: vi.fn(),
  toastErrorMock: vi.fn(),
  toastSuccessMock: vi.fn(),
  calculateInstallmentMock: vi.fn(),
}));

vi.mock('../../hooks/useClientCases', () => ({
  useClientCases: () => ({ cases: [], loading: false }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({ confirm: confirmMock }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({ error: toastErrorMock, success: toastSuccessMock }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    calculateInstallment: calculateInstallmentMock,
  },
}));

describe('InstallmentPlanner integration', () => {
  beforeEach(() => {
    confirmMock.mockReset();
    toastErrorMock.mockReset();
    toastSuccessMock.mockReset();
    calculateInstallmentMock.mockReset();
  });

  it('calculates a schedule and passes it back to the invoice editor on save', async () => {
    const user = userEvent.setup();
    const onApplyPlan = vi.fn();

    confirmMock.mockResolvedValue(true);
    calculateInstallmentMock.mockResolvedValue({
      overpay: 1200,
      toPay: 11200,
      items: [
        { date: '2026-05-10', principal: 5000, interest: 600, payment: 5600, balance: 5000 },
        { date: '2026-06-10', principal: 5000, interest: 600, payment: 5600, balance: 0 },
      ],
    });

    render(
      <InstallmentPlanner
        mode="invoice"
        initialTotal={10000}
        initialStartDate="2026-05-10"
        onApplyPlan={onApplyPlan}
        onCancel={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'Рассчитать график' }));

    await screen.findByText('Платежей в графике: 2');
    expect(calculateInstallmentMock).toHaveBeenCalledWith({
      total: 10000,
      downPayment: 0,
      annualRate: 0,
      months: 6,
      startDate: '2026-05-10',
      roundingMode: 'none',
      roundingStep: undefined,
    });

    await user.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => {
      expect(onApplyPlan).toHaveBeenCalledTimes(1);
    });

    const appliedPlan = onApplyPlan.mock.calls[0][0];
    expect(appliedPlan).toMatchObject({
      total: 10000,
      startDate: '2026-05-10',
      items: [
        expect.objectContaining({ payment: 5600, date: '2026-05-10' }),
        expect.objectContaining({ payment: 5600, date: '2026-06-10' }),
      ],
    });
  });

  it('keeps calculator mode read-only when the role cannot create payments', async () => {
    const user = userEvent.setup();
    const onSavePayments = vi.fn();

    calculateInstallmentMock.mockResolvedValue({
      overpay: 0,
      toPay: 10000,
      items: [
        { date: '2026-05-10', principal: 5000, interest: 0, payment: 5000, balance: 5000 },
        { date: '2026-06-10', principal: 5000, interest: 0, payment: 5000, balance: 0 },
      ],
    });

    render(
      <InstallmentPlanner
        mode="calculator"
        initialTotal={10000}
        initialStartDate="2026-05-10"
        clients={[]}
        canSelectCounterparty={false}
        allowedCounterpartyScopes={[]}
        canSelectCase={false}
        canSavePayments={false}
        onSavePayments={onSavePayments}
      />,
    );

    expect(screen.getByRole('button', { name: 'Выбрать контрагента' })).toHaveProperty('disabled', true);
    expect(screen.queryByLabelText('Дело')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Сохранить' })).toBeNull();

    await user.click(screen.getByRole('button', { name: 'Рассчитать график' }));

    await screen.findByText('Платежей в графике: 2');
    expect(calculateInstallmentMock).toHaveBeenCalledTimes(1);
    expect(onSavePayments).not.toHaveBeenCalled();
  });

  it('does not attach a case from calculator mode when case access is closed', async () => {
    const user = userEvent.setup();
    const onSavePayments = vi.fn();

    confirmMock.mockResolvedValue(true);
    calculateInstallmentMock.mockResolvedValue({
      overpay: 0,
      toPay: 10000,
      items: [
        { date: '2026-05-10', principal: 10000, interest: 0, payment: 10000, balance: 0 },
      ],
    });

    render(
      <InstallmentPlanner
        mode="calculator"
        initialTotal={10000}
        initialStartDate="2026-05-10"
        initialClientId={15}
        initialClientName="Андрей"
        clients={[{ id: 15, name: 'Андрей' } as Client]}
        canSelectCounterparty
        allowedCounterpartyScopes={['client']}
        canSelectCase={false}
        canSavePayments
        onSavePayments={onSavePayments}
      />,
    );

    expect(screen.queryByLabelText('Дело')).toBeNull();

    await user.click(screen.getByRole('button', { name: 'Рассчитать график' }));
    await screen.findByText('Платежей в графике: 1');
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => {
      expect(onSavePayments).toHaveBeenCalledTimes(1);
    });
    expect(onSavePayments.mock.calls[0][0]).toMatchObject({
      clientId: 15,
      legalEntityId: undefined,
      clientCaseId: undefined,
    });
  });
});
