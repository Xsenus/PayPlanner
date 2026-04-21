import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ActModal } from './ActModal';
import type { ActResponsible, Client, Invoice } from '../../types';

const { getActMock, getInvoiceMock } = vi.hoisted(() => ({
  getActMock: vi.fn(),
  getInvoiceMock: vi.fn(),
}));

vi.mock('../../hooks/useTranslation', () => ({
  useTranslation: () => ({ t: () => undefined }),
}));

vi.mock('../../hooks/useFormRequirements', () => ({
  useFormRequirements: () => ({
    isRequired: () => false,
  }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getAct: getActMock,
    getInvoice: getInvoiceMock,
  },
}));

vi.mock('../Clients/ClientStatusBadge', () => ({
  ClientStatusBadge: () => null,
}));

function createClient(id: number, name: string): Client {
  return {
    id,
    name,
    email: '',
    phone: '',
    company: '',
    address: '',
    notes: '',
    createdAt: '2026-04-20T00:00:00Z',
    isActive: true,
  } as Client;
}

function createResponsible(id: number, fullName: string): ActResponsible {
  return {
    id,
    fullName,
  };
}

describe('ActModal integration', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    getActMock.mockResolvedValue(null);
    getInvoiceMock.mockResolvedValue(null);
  });

  it('uses the invoice type, hides inn fields and submits only the selected invoice positions', async () => {
    const invoice = {
      id: 618,
      number: 'INV-618',
      date: '2026-04-10',
      description: 'Юридические услуги за апрель',
      type: 'Expense',
      clientId: 1,
      clientName: 'Базовый клиент',
      expenseClientId: 2,
      expenseClientName: 'ООО Поставщик',
      lineItems: [
        { id: 101, sourceKey: 'db:101', name: 'Подготовка документов', quantity: 2, unit: 'усл.', price: 5000, sortOrder: 0 },
        { id: 102, sourceKey: 'db:102', name: 'Выезд в суд', quantity: 1, unit: 'усл.', price: 8000, sortOrder: 1 },
      ],
      acts: [],
    } as unknown as Invoice;
    const onOpenInvoice = vi.fn();
    const onSubmit = vi.fn().mockResolvedValue(undefined);

    getInvoiceMock.mockResolvedValue(invoice);

    render(
      <ActModal
        open
        mode="create"
        act={null}
        onClose={vi.fn()}
        onSubmit={onSubmit}
        submitting={false}
        clients={[
          createClient(1, 'Базовый клиент'),
          createClient(2, 'ООО Поставщик'),
        ]}
        responsibles={[createResponsible(5, 'Мария Менеджер')]}
        lookupsLoading={false}
        defaultResponsibleId={5}
        initialValues={{ invoiceId: 618 }}
        presentation="page"
        onOpenInvoice={onOpenInvoice}
      />,
    );

    await waitFor(() => {
      expect(getInvoiceMock).toHaveBeenCalledWith(618);
    });

    expect(screen.queryByText(/ИНН/i)).toBeNull();
    expect(screen.queryByText(/ID сч[её]та/i)).toBeNull();
    expect(screen.getAllByText('Поставщик').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Мария Менеджер').length).toBeGreaterThan(0);
    expect(screen.getByText('Доступно позиций: 2')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Сч[её]т №INV-618/i }));
    expect(onOpenInvoice).toHaveBeenCalledWith(618, 'Expense');

    fireEvent.change(screen.getByLabelText('Номер акта'), { target: { value: 'ACT-618-1' } });
    fireEvent.click(screen.getByRole('button', { name: 'По счету' }));
    await waitFor(() => {
      expect(screen.getAllByRole('button', { name: 'Исключить позицию из акта' }).length).toBe(2);
    });
    fireEvent.click(screen.getAllByRole('button', { name: 'Исключить позицию из акта' })[1]);

    fireEvent.click(screen.getByRole('button', { name: 'Создать' }));

    await waitFor(() => {
      expect(onSubmit).toHaveBeenCalledTimes(1);
    });

    const payload = onSubmit.mock.calls[0]?.[0];
    expect(payload).toMatchObject({
      number: 'ACT-618-1',
      amount: 10000,
      creationMode: 'Partial',
      invoiceId: 618,
      invoiceNumber: 'INV-618',
      clientId: 2,
      responsibleId: 5,
      status: 'Created',
      comment: undefined,
      counterpartyInn: undefined,
    });
    expect(payload.lineItems).toEqual([{ sourceKey: 'db:101', quantity: 2 }]);
  });

  it('allows creating an empty manual act without inn and invoice id fields', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);

    render(
      <ActModal
        open
        mode="create"
        act={null}
        onClose={vi.fn()}
        onSubmit={onSubmit}
        submitting={false}
        clients={[createClient(7, 'Иван Иванов')]}
        responsibles={[]}
        lookupsLoading={false}
        presentation="page"
      />,
    );

    expect(screen.queryByText(/ИНН/i)).toBeNull();
    expect(screen.queryByText(/ID сч[её]та/i)).toBeNull();
    expect(screen.getAllByText('Будет указан автоматически').length).toBeGreaterThan(0);
    expect(screen.getByText('Это пустой акт без привязки к счету. Контрагента можно выбрать вручную.')).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Номер акта'), { target: { value: 'MANUAL-1' } });
    fireEvent.change(screen.getByLabelText('Сумма'), { target: { value: '4321.50' } });
    fireEvent.change(screen.getByLabelText('Контрагент'), { target: { value: '7' } });
    fireEvent.change(screen.getByLabelText('Комментарий'), { target: { value: 'Пустой акт без счета' } });
    fireEvent.click(screen.getByRole('button', { name: 'Создать' }));

    await waitFor(() => {
      expect(onSubmit).toHaveBeenCalledTimes(1);
    });

    const payload = onSubmit.mock.calls[0]?.[0];
    expect(payload).toMatchObject({
      number: 'MANUAL-1',
      amount: 4321.5,
      creationMode: 'Manual',
      invoiceId: undefined,
      clientId: 7,
      responsibleId: undefined,
      comment: 'Пустой акт без счета',
      counterpartyInn: undefined,
      status: 'Created',
    });
    expect(payload.lineItems).toEqual([]);
  });
});
