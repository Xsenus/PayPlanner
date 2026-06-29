import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import type { Payment } from '../../types';
import { PaymentsTable } from './PaymentsTable';

vi.mock('../../hooks/useTranslation', () => ({
  useTranslation: () => ({
    t: () => undefined,
    formatDate: (value: string) => value,
  }),
}));

function createPayment(overrides: Partial<Payment> = {}): Payment {
  return {
    id: 77,
    amount: 6000,
    paidAmount: 0,
    outstandingAmount: 6000,
    hasPartialPayment: false,
    date: '2026-05-07',
    plannedDate: '2026-05-07',
    type: 'Income',
    status: 'Pending',
    description: 'Open me',
    notes: '',
    systemNotes: '',
    isPaid: false,
    rescheduleCount: 0,
    documentKind: 'Payment',
    ...overrides,
  } as unknown as Payment;
}

describe('PaymentsTable', () => {
  it('opens a payment on row click', () => {
    const payment = createPayment();
    const onEditPayment = vi.fn();

    render(
      <PaymentsTable
        payments={[payment]}
        onEditPayment={onEditPayment}
        canAdd={false}
        canEdit
        canDelete={false}
      />,
    );

    fireEvent.click(screen.getByText('Open me').closest('tr')!);

    expect(onEditPayment).toHaveBeenCalledWith(payment);
  });

  it('opens a payment from the keyboard', () => {
    const payment = createPayment();
    const onEditPayment = vi.fn();

    render(
      <PaymentsTable
        payments={[payment]}
        onEditPayment={onEditPayment}
        canAdd={false}
        canEdit
        canDelete={false}
      />,
    );

    fireEvent.keyDown(screen.getByText('Open me').closest('tr')!, { key: 'Enter' });

    expect(onEditPayment).toHaveBeenCalledWith(payment);
  });

  it('opens a client link without opening the payment row', () => {
    const payment = createPayment({
      clientId: 12,
      clientCaseId: 34,
      client: { id: 12, name: 'Client A' },
    } as Partial<Payment>);
    const onEditPayment = vi.fn();
    const onOpenClient = vi.fn();

    render(
      <PaymentsTable
        payments={[payment]}
        onEditPayment={onEditPayment}
        onOpenClient={onOpenClient}
        canAdd={false}
        canEdit
        canDelete={false}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /Client A/ }));

    expect(onOpenClient).toHaveBeenCalledWith(12, 34);
    expect(onEditPayment).not.toHaveBeenCalled();
  });

  it('renders client as plain text when client section is hidden', () => {
    const payment = createPayment({
      clientId: 12,
      clientCaseId: 34,
      client: { id: 12, name: 'Client A' },
    } as Partial<Payment>);
    const onEditPayment = vi.fn();
    const onOpenClient = vi.fn();

    render(
      <PaymentsTable
        payments={[payment]}
        onEditPayment={onEditPayment}
        onOpenClient={onOpenClient}
        canAdd={false}
        canEdit
        canDelete={false}
        canOpenClients={false}
      />,
    );

    expect(screen.getByText('Client A')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Client A/ })).toBeNull();
    fireEvent.click(screen.getByText('Client A').closest('tr')!);

    expect(onOpenClient).not.toHaveBeenCalled();
    expect(onEditPayment).toHaveBeenCalledWith(payment);
  });

  it('renders expense counterparty as payment recipient when supplier client is not linked', () => {
    const payment = createPayment({
      type: 'Expense',
      counterpartyName: 'Гордик Сергей',
      expenseClient: { id: 71, name: 'Винокурова Нина Тариеловна' },
      clientCase: { id: 501, title: 'ВИНОКУРОВА - РАЗДЕЛ ИМУЩЕСТВА' },
    } as Partial<Payment>);

    render(
      <PaymentsTable
        payments={[payment]}
        onEditPayment={vi.fn()}
        canAdd={false}
        canEdit
        canDelete={false}
      />,
    );

    expect(screen.getByText('Получатель: Гордик Сергей')).toBeTruthy();
    expect(screen.getByText('Клиент расхода: Винокурова Нина Тариеловна')).toBeTruthy();
    expect(screen.getByText('Дело расхода: ВИНОКУРОВА - РАЗДЕЛ ИМУЩЕСТВА')).toBeTruthy();
  });

  it('calls sort handler when a sortable column header is clicked', () => {
    const onSortChange = vi.fn();

    render(
      <PaymentsTable
        payments={[createPayment()]}
        onEditPayment={vi.fn()}
        canAdd={false}
        canEdit
        canDelete={false}
        sortBy="date"
        sortDir="asc"
        onSortChange={onSortChange}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Сортировать по колонке Сумма' }));

    expect(onSortChange).toHaveBeenCalledWith('amount');
  });

  it('does not pass case id when cases section is hidden', () => {
    const payment = createPayment({
      clientId: 12,
      clientCaseId: 34,
      client: { id: 12, name: 'Client A' },
    } as Partial<Payment>);
    const onEditPayment = vi.fn();
    const onOpenClient = vi.fn();

    render(
      <PaymentsTable
        payments={[payment]}
        onEditPayment={onEditPayment}
        onOpenClient={onOpenClient}
        canAdd={false}
        canEdit
        canDelete={false}
        canOpenCases={false}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /Client A/ }));

    expect(onOpenClient).toHaveBeenCalledWith(12, undefined);
    expect(onEditPayment).not.toHaveBeenCalled();
  });
});
