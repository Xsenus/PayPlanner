import { StaleUiOperationError } from '../../utils/StaleUiOperationError';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ActModal } from './ActModal';
import { useLegalEntities } from '../../hooks/useLegalEntities';
import { useFormRequirements } from '../../hooks/useFormRequirements';
import type { Act, ActInput, ActResponsible, Client, Invoice } from '../../types';

const {
  getActMock,
  getInvoiceMock,
  getInvoicesMock,
  getClientsMock,
  getClientsV2Mock,
  getLegalEntitiesMock,
  getLegalEntitiesPagedMock,
  getOwnCompanyOptionsMock,
  modalAuthState,
} = vi.hoisted(() => ({
  getActMock: vi.fn(),
  getInvoiceMock: vi.fn(),
  getInvoicesMock: vi.fn(),
  getClientsMock: vi.fn(),
  getClientsV2Mock: vi.fn(),
  getLegalEntitiesMock: vi.fn(),
  getLegalEntitiesPagedMock: vi.fn(),
  getOwnCompanyOptionsMock: vi.fn(),
  modalAuthState: { user: { id: 1, role: { id: 1 } } },
}));

vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => modalAuthState }));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({ error: vi.fn(), success: vi.fn(), info: vi.fn() }),
}));

vi.mock('../../hooks/useTranslation', () => ({
  useTranslation: () => ({ t: () => undefined }),
}));

vi.mock('../../hooks/useFormRequirements', () => ({
  useFormRequirements: vi.fn(() => ({
    isRequired: () => false,
  })),
}));

vi.mock('../../hooks/useDocumentStatusSettings', () => ({
  useDocumentStatusSettings: () => [],
}));

vi.mock('../../hooks/useLegalEntities', () => ({
  useLegalEntities: vi.fn(() => ({
    legalEntities: [
      {
        id: 301,
        shortName: 'ООО Юрклиент',
        fullName: 'Общество с ограниченной ответственностью Юрклиент',
        inn: '5400000001',
        kpp: '540001001',
        clients: [],
        clientsCount: 0,
        createdAt: '2026-04-01T00:00:00Z',
      },
      {
        id: 302,
        shortName: 'ООО Поставщик ЮЛ',
        inn: '5400000002',
        clients: [],
        clientsCount: 0,
        createdAt: '2026-04-01T00:00:00Z',
      },
    ],
    loading: false,
    error: null,
  })),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getAct: getActMock,
    getClients: getClientsMock,
    getClientsV2: getClientsV2Mock,
    getInvoice: getInvoiceMock,
    getInvoices: getInvoicesMock,
    getLegalEntities: getLegalEntitiesMock,
    getLegalEntitiesPaged: getLegalEntitiesPagedMock,
    getOwnCompanyOptions: getOwnCompanyOptionsMock,
    getRolePermissions: vi.fn().mockResolvedValue({}),
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

function getPickerOptionAction(label: HTMLElement): HTMLButtonElement {
  const option = label.closest('[data-counterparty-option]');
  if (option instanceof HTMLElement) {
    const actions = within(option).queryAllByRole('button');
    if (actions.length > 0) {
      return actions[0] as HTMLButtonElement;
    }
  }

  let current: HTMLElement | null = label;

  while (current && current !== document.body) {
    const actions = within(current).queryAllByRole('button');
    if (actions.length === 1) {
      return actions[0] as HTMLButtonElement;
    }
    current = current.parentElement;
  }

  throw new Error(`Не найдена кнопка выбора для ${label.textContent ?? 'контрагента'}`);
}

async function selectPickerOption(name: string): Promise<void> {
  const option = await waitFor(() => {
    const found = Array.from(document.querySelectorAll('[data-counterparty-option]'))
      .find((element) => element.textContent?.includes(name));
    if (!(found instanceof HTMLElement)) {
      throw new Error(`Не найден вариант выбора ${name}`);
    }
    return found;
  });

  fireEvent.click(getPickerOptionAction(option));
}

describe('ActModal integration', () => {
  it('caller lookup retry: an explicit retry preserves the act draft and is disabled during loading', async () => {
    const retry = vi.fn();
    const props = { open: true, mode: 'create' as const, act: null, onClose: vi.fn(), onSubmit: vi.fn().mockResolvedValue(undefined), submitting: false,
      clients: [createClient(1, 'Текущий клиент')], responsibles: [], lookupsLoading: false, lookupsError: 'Справочники временно недоступны', onRetryLookups: retry, defaultClientId: 1, presentation: 'page' as const };
    const view = render(<ActModal {...props} />); await act(async () => {});
    fireEvent.change(screen.getByLabelText('Номер акта'), { target: { value: 'DRAFT-RETRY' } });
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку справочников' })); expect(retry).toHaveBeenCalledOnce();
    view.rerender(<ActModal {...props} lookupsLoading />);
    expect(screen.getByRole('button', { name: 'Повторить загрузку справочников' })).toHaveProperty('disabled', true);
    expect(screen.getByLabelText('Номер акта')).toHaveProperty('value', 'DRAFT-RETRY'); expect(props.onSubmit).not.toHaveBeenCalled();
  });
  it('requirements caller: act rules follow the editor access scope and closing', async () => {
    const p = { open: true, mode: 'create' as const, act: null, onClose: vi.fn(), onSubmit: vi.fn(), submitting: false, clients: [], responsibles: [], lookupsLoading: false, presentation: 'page' as const, accessScopeKey: 'all' };
    const view = render(<ActModal {...p} />); const first = vi.mocked(useFormRequirements).mock.lastCall; expect(first?.[0]).toBe('acts'); expect(first?.[1]).toBe(true);
    view.rerender(<ActModal {...p} accessScopeKey="own" />); expect(vi.mocked(useFormRequirements).mock.lastCall?.[2]?.ownerScope).not.toBe(first?.[2]?.ownerScope);
    view.rerender(<ActModal {...p} open={false} />); expect(vi.mocked(useFormRequirements).mock.lastCall?.[1]).toBe(false); await act(async () => {});
  });
  it('directory caller: act editor changes lookup owner with access and closes reads', async () => {
    const props = { open: true, mode: 'create' as const, act: null, onClose: vi.fn(), onSubmit: vi.fn(), submitting: false, clients: [], responsibles: [], lookupsLoading: false, presentation: 'page' as const, accessScopeKey: 'all' };
    const view = render(<ActModal {...props} />); const first = vi.mocked(useLegalEntities).mock.lastCall?.[0]; expect(first?.enabled).toBe(true);
    view.rerender(<ActModal {...props} accessScopeKey="own" />); expect(vi.mocked(useLegalEntities).mock.lastCall?.[0]?.ownerScope).not.toBe(first?.ownerScope);
    view.rerender(<ActModal {...props} open={false} />); expect(vi.mocked(useLegalEntities).mock.lastCall?.[0]?.enabled).toBe(false);
    await act(async () => undefined);
  });
  beforeEach(() => {
    vi.clearAllMocks();
    modalAuthState.user = { id: 1, role: { id: 1 } };
    getActMock.mockResolvedValue(null);
    getOwnCompanyOptionsMock.mockResolvedValue([]);
    const clientLookups = [
      createClient(1, 'Базовый клиент'),
      createClient(2, 'ООО Поставщик'),
      createClient(7, 'Иван Иванов'),
      createClient(9, 'Другой поставщик'),
    ];
    getClientsMock.mockResolvedValue(clientLookups);
    getClientsV2Mock.mockResolvedValue({
      items: clientLookups,
      total: 4,
      page: 1,
      pageSize: 25,
    });
    getInvoiceMock.mockResolvedValue(null);
    getInvoicesMock.mockResolvedValue({ items: [] });
    const legalEntityLookups = [
      {
        id: 301,
        shortName: 'ООО Юрклиент',
        fullName: 'Общество с ограниченной ответственностью Юрклиент',
        inn: '5400000001',
        kpp: '540001001',
        clients: [],
        clientsCount: 0,
        createdAt: '2026-04-01T00:00:00Z',
      },
      {
        id: 302,
        shortName: 'ООО Поставщик ЮЛ',
        inn: '5400000002',
        clients: [],
        clientsCount: 0,
        createdAt: '2026-04-01T00:00:00Z',
      },
    ];
    getLegalEntitiesMock.mockResolvedValue(legalEntityLookups);
    getLegalEntitiesPagedMock.mockResolvedValue({
      items: legalEntityLookups,
      total: 2,
      page: 1,
      pageSize: 25,
    });
  });

  it('shows one document status field and derives the legacy status on submit', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);

    render(
      <ActModal
        open
        mode="create"
        act={null}
        onClose={vi.fn()}
        onSubmit={onSubmit}
        submitting={false}
        clients={[createClient(1, 'Базовый клиент')]}
        responsibles={[createResponsible(5, 'Мария Менеджер')]}
        lookupsLoading={false}
        defaultClientId={1}
        defaultResponsibleId={5}
        presentation="page"
      />,
    );

    expect(screen.queryByLabelText('Статус')).toBeNull();
    const documentStatus = screen.getByLabelText('Статус документа') as HTMLSelectElement;
    fireEvent.change(documentStatus, { target: { value: 'Signed' } });
    fireEvent.change(screen.getByLabelText('Номер акта'), { target: { value: 'ACT-STATUS-1' } });
    fireEvent.change(screen.getByLabelText('Сумма'), { target: { value: '25000' } });
    fireEvent.click(screen.getByRole('button', { name: 'Создать' }));

    await waitFor(() => {
      expect(onSubmit).toHaveBeenCalledTimes(1);
    });

    expect(onSubmit.mock.calls[0]?.[0]).toMatchObject({
      number: 'ACT-STATUS-1',
      status: 'Signed',
      workflowStatus: 'Signed',
    });
  });

  it('sends the chosen seller only in the new act creation payload', async () => {
    getOwnCompanyOptionsMock.mockResolvedValue([
      { id: 41, shortName: 'Своя компания', isDefault: true, branches: [{ id: 42, name: 'Офис', kind: 'Office' }] },
    ]);
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    render(<ActModal open mode="create" act={null} onClose={vi.fn()} onSubmit={onSubmit} submitting={false}
      clients={[createClient(1, 'Базовый клиент')]} responsibles={[]} lookupsLoading={false} defaultClientId={1} presentation="page" />);

    fireEvent.change(await screen.findByLabelText('Подразделение-продавец'), { target: { value: '42' } });
    fireEvent.change(screen.getByLabelText('Номер акта'), { target: { value: 'ACT-SELLER' } });
    fireEvent.change(screen.getByLabelText('Сумма'), { target: { value: '25000' } });
    fireEvent.click(screen.getByRole('button', { name: 'Создать' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit.mock.calls[0]?.[0]).toMatchObject({ sellerCompanyId: 41, sellerBranchId: 42, number: 'ACT-SELLER', amount: 25000 });
    expect(getActMock).not.toHaveBeenCalled();
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

    fireEvent.click(screen.getAllByRole('button', { name: /Сч[её]т №INV-618/i })[1]);
    expect(onOpenInvoice).toHaveBeenCalledWith(618, 'Expense');

    fireEvent.change(screen.getByLabelText('Номер акта'), { target: { value: 'ACT-618-1' } });
    fireEvent.click(screen.getByRole('button', { name: 'По счету' }));
    await waitFor(() => {
      expect(screen.getAllByRole('button', { name: 'Удалить позицию' }).length).toBe(2);
    });
    fireEvent.click(screen.getAllByRole('button', { name: 'Удалить позицию' })[1]);

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

  it('filters invoices by selected counterparty and recalculates the amount by selected positions', async () => {
    const invoice = {
      id: 618,
      number: 'INV-618',
      date: '2026-04-10',
      amount: 18000,
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
    const foreignInvoice = {
      id: 999,
      number: 'INV-999',
      date: '2026-04-11',
      amount: 7000,
      type: 'Expense',
      clientId: 1,
      clientName: 'Базовый клиент',
      expenseClientId: 9,
      expenseClientName: 'Другой поставщик',
    } as unknown as Invoice;

    getInvoicesMock.mockResolvedValue({ items: [invoice, foreignInvoice] });
    getInvoiceMock.mockResolvedValue(invoice);

    render(
      <ActModal
        open
        mode="create"
        act={null}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[
          createClient(1, 'Базовый клиент'),
          createClient(2, 'ООО Поставщик'),
          createClient(9, 'Другой поставщик'),
        ]}
        responsibles={[createResponsible(5, 'Мария Менеджер')]}
        lookupsLoading={false}
        defaultResponsibleId={5}
        presentation="page"
      />,
    );

    await waitFor(() => {
      expect(getInvoicesMock).toHaveBeenCalledWith({ page: 1, pageSize: 200 });
    });

    fireEvent.click(screen.getByRole('button', { name: 'Выбрать' }));
    await selectPickerOption('ООО Поставщик');

    fireEvent.click(screen.getByRole('button', { name: 'Справочник' }));
    expect(await screen.findByText(/Счет №INV-618 от 2026-04-10/i)).toBeTruthy();
    expect(screen.queryByText(/Счет №INV-999 от 2026-04-11/i)).toBeNull();
    fireEvent.click((await screen.findByText(/Счет №INV-618 от 2026-04-10/i)).closest('button') as HTMLButtonElement);

    await waitFor(() => {
      expect(getInvoiceMock).toHaveBeenCalledWith(618);
    });

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Выбрать все' })).toBeTruthy();
    });

    fireEvent.click(screen.getAllByRole('button', { name: 'Очистить' }).at(-1)!);
    expect(screen.getByText('Пока в акт не добавлена ни одна позиция счета.')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: '+ Добавить' }));
    fireEvent.click((await screen.findByText(/Подготовка документов/i)).closest('button') as HTMLButtonElement);

    await waitFor(() => {
      expect(screen.getAllByRole('button', { name: 'Удалить позицию' }).length).toBe(1);
    });

    expect(screen.getByDisplayValue('10000').hasAttribute('readonly')).toBe(true);
    fireEvent.change(screen.getByDisplayValue('2'), { target: { value: '1' } });
    expect(screen.getByDisplayValue('5000').hasAttribute('readonly')).toBe(true);
    expect(screen.getByText(/Выбрано позиций:/i).textContent ?? '').toContain('1');
    expect(screen.getAllByText('Поставщик').length).toBeGreaterThan(0);
  });

  it('uses invoice positions from the seed list when the detail response arrives without line items', async () => {
    const invoiceFromList = {
      id: 777,
      number: 'INV-777',
      date: '2026-04-12',
      amount: 12000,
      description: 'Услуги по договору',
      type: 'Income',
      clientId: 1,
      clientName: 'Тестовый клиент',
      lineItems: [
        { id: 301, sourceKey: 'db:301', name: 'Подготовка документов', quantity: 2, unit: 'усл.', price: 6000, sortOrder: 0 },
      ],
      acts: [],
    } as unknown as Invoice;
    const invoiceWithoutLines = {
      ...invoiceFromList,
      lineItems: [],
      acts: [],
    } as unknown as Invoice;

    getInvoicesMock.mockResolvedValue({ items: [invoiceFromList] });
    getInvoiceMock.mockResolvedValue(invoiceWithoutLines);

    render(
      <ActModal
        open
        mode="create"
        act={null}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[createClient(1, 'Тестовый клиент')]}
        responsibles={[createResponsible(5, 'Мария Менеджер')]}
        lookupsLoading={false}
        defaultResponsibleId={5}
        initialValues={{ invoiceId: 777 }}
        presentation="page"
      />,
    );

    await waitFor(() => {
      expect(getInvoicesMock).toHaveBeenCalledWith({ page: 1, pageSize: 200 });
      expect(getInvoiceMock).toHaveBeenCalledWith(777);
    });

    expect(await screen.findByText('Доступно позиций: 1')).toBeTruthy();
    expect(screen.getByText('Подготовка документов')).toBeTruthy();
    expect(screen.queryByText('У этого счета пока нет строк, поэтому акт можно сохранить только как пустой.')).toBeNull();
  });

  it('selects a legal entity for a manual act and submits legalEntityId instead of clientId', async () => {
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

    fireEvent.click(screen.getByRole('button', { name: 'Выбрать' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Юридические лица' }));
    await selectPickerOption('ООО Юрклиент');
    fireEvent.change(screen.getByLabelText('Номер акта'), { target: { value: 'LEGAL-ACT-1' } });
    fireEvent.change(screen.getByLabelText('Сумма'), { target: { value: '6400' } });
    fireEvent.click(screen.getByRole('button', { name: 'Создать' }));

    await waitFor(() => {
      expect(onSubmit).toHaveBeenCalledTimes(1);
    });

    const payload = onSubmit.mock.calls[0]?.[0];
    expect(payload).toMatchObject({
      number: 'LEGAL-ACT-1',
      amount: 6400,
      creationMode: 'Manual',
      clientId: null,
      legalEntityId: 301,
      status: 'Created',
    });
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
    expect(
      screen.getByText('Можно выбрать счет из справочника или оставить акт пустым и заполнить его вручную.'),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Выбрать' }));
    await selectPickerOption('Иван Иванов');

    fireEvent.change(screen.getByLabelText('Номер акта'), { target: { value: 'MANUAL-1' } });
    fireEvent.change(screen.getByLabelText('Сумма'), { target: { value: '4321.50' } });
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

  it.each([
    { quantity: '2', price: '1500', vatRate: '', amount: 3000, valid: true },
    { quantity: '1.0075', price: '10', vatRate: '20', amount: 10.08, valid: true },
    { quantity: '1.5', price: '10.075', vatRate: '20', amount: 15.11, valid: false },
    { quantity: '1.50001', price: '10', vatRate: '20', amount: 15, valid: false },
  ])('keeps displayed totals and validates stored precision: $quantity × $price, VAT $vatRate', async ({ quantity, price, vatRate, amount, valid }) => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    const invoiceWithoutLines = {
      id: 880,
      number: 'INV-880',
      date: '2026-04-15',
      amount: 0,
      description: 'Пустой счет',
      type: 'Income',
      clientId: 7,
      clientName: 'Иван Иванов',
      lineItems: [],
      acts: [],
    } as unknown as Invoice;

    getInvoiceMock.mockResolvedValue(invoiceWithoutLines);

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
        initialValues={{ invoiceId: 880 }}
        presentation="page"
      />,
    );

    await waitFor(() => {
      expect(getInvoiceMock).toHaveBeenCalledWith(880);
    });

    expect(screen.getByText('Позиции акта')).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Номер акта'), { target: { value: 'ACT-880' } });
    fireEvent.change(screen.getByLabelText('Название позиции акта'), { target: { value: 'Консультация' } });
    fireEvent.change(screen.getByLabelText('Количество позиции акта'), { target: { value: quantity } });
    fireEvent.change(screen.getByLabelText('Единица измерения позиции акта'), { target: { value: 'час' } });
    fireEvent.change(screen.getByLabelText('Цена позиции акта'), { target: { value: price } });
    fireEvent.change(screen.getByTestId('line-item-vat-rate'), { target: { value: vatRate } });

    expect(screen.getByDisplayValue(String(amount)).hasAttribute('readonly')).toBe(true);
    expect(screen.getByText(String(amount), { selector: 'output' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Создать' }));

    if (!valid) {
      expect(screen.getByText('В цене допускается до двух знаков после запятой, в количестве — до четырёх.')).toBeTruthy();
      expect(onSubmit).not.toHaveBeenCalled();
      return;
    }

    await waitFor(() => {
      expect(onSubmit).toHaveBeenCalledTimes(1);
    });

    const payload = onSubmit.mock.calls[0]?.[0];
    expect(payload).toMatchObject({
      number: 'ACT-880',
      amount,
      creationMode: 'Manual',
      invoiceId: 880,
      invoiceNumber: 'INV-880',
      clientId: 7,
      status: 'Created',
    });
    expect(payload.lineItems).toEqual([
      expect.objectContaining({
        name: 'Консультация',
        quantity: Number(quantity),
        unit: 'час',
        price: Number(price),
      }),
    ]);
  });

  it.each(['invoice', 'manual'])('submits exact cents for two rated lines in %s mode', async (mode) => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    getInvoiceMock.mockResolvedValue({
      id: 881, number: 'INV-881', date: '2026-04-15', amount: 0.3, type: 'Income', clientId: 7,
      lineItems: mode === 'invoice' ? [
        { id: 8811, sourceKey: 'db:8811', name: 'Первая', quantity: 1, price: 0.1, vatRate: '20', unit: 'шт', sortOrder: 0 },
        { id: 8812, sourceKey: 'db:8812', name: 'Вторая', quantity: 1, price: 0.2, vatRate: '20', unit: 'шт', sortOrder: 1 },
      ] : [], acts: [],
    } as unknown as Invoice);
    render(<ActModal open mode="create" act={null} onClose={vi.fn()} onSubmit={onSubmit} submitting={false}
      clients={[createClient(7, 'Иван Иванов')]} responsibles={[]} lookupsLoading={false}
      initialValues={{ invoiceId: 881 }} presentation="page" />);
    await waitFor(() => expect(getInvoiceMock).toHaveBeenCalledWith(881));
    fireEvent.change(screen.getByLabelText('Номер акта'), { target: { value: 'ACT-CENTS' } });
    if (mode === 'manual') {
      fireEvent.click(screen.getByRole('button', { name: /Добавить позицию/ }));
      for (let i = 0; i < 2; i++) {
        fireEvent.change(screen.getAllByLabelText('Название позиции акта')[i], { target: { value: `Услуга ${i}` } });
        fireEvent.change(screen.getAllByLabelText('Количество позиции акта')[i], { target: { value: '1' } });
        fireEvent.change(screen.getAllByLabelText('Цена позиции акта')[i], { target: { value: i === 0 ? '0.1' : '0.2' } });
        fireEvent.change(screen.getAllByTestId('line-item-vat-rate')[i], { target: { value: '20' } });
      }
    } else {
      fireEvent.click(screen.getByRole('button', { name: 'По счету' }));
    }
    fireEvent.click(screen.getByRole('button', { name: 'Создать' }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit.mock.calls[0][0].amount).toBe(0.3);
  });

  it('does not submit stale invoice positions when linked act is switched to manual mode', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    const invoice = {
      id: 951,
      number: '200',
      date: '2026-04-22',
      amount: 5000,
      type: 'Income',
      clientId: 7,
      clientName: 'Мурсалимов Радик Рауфович',
      lineItems: [
        { id: 2540, sourceKey: 'db:2540', name: 'Старая позиция счета', quantity: 1, unit: 'шт', price: 5000, sortOrder: 0 },
      ],
      acts: [],
    } as unknown as Invoice;
    const act = {
      id: 951,
      number: 'ACT-951',
      title: '',
      date: '2026-04-23',
      amount: 5000,
      creationMode: 'Full',
      invoiceId: 951,
      invoiceNumber: '200',
      clientId: 7,
      status: 'Created',
      workflowStatus: 'Draft',
      lineItems: [
        {
          id: 1,
          sourceKey: 'db:2540',
          invoiceLineItemId: 2540,
          sourceSortOrder: 0,
          name: 'Старая позиция счета',
          quantity: 1,
          unit: 'шт',
          price: 5000,
          sortOrder: 0,
        },
      ],
    } as unknown as Act;

    getInvoiceMock.mockResolvedValue(invoice);
    getActMock.mockResolvedValue(act);

    render(
      <ActModal
        open
        mode="edit"
        act={act}
        onClose={vi.fn()}
        onSubmit={onSubmit}
        submitting={false}
        clients={[createClient(7, 'Мурсалимов Радик Рауфович')]}
        responsibles={[]}
        lookupsLoading={false}
        presentation="page"
      />,
    );

    await waitFor(() => {
      expect(getInvoiceMock).toHaveBeenCalledWith(951);
    });

    fireEvent.click(screen.getByRole('button', { name: 'Ручной акт' }));
    fireEvent.change(screen.getByLabelText('Сумма'), { target: { value: '7000' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => {
      expect(onSubmit).toHaveBeenCalledTimes(1);
    });

    const payload = onSubmit.mock.calls[0]?.[0];
    expect(payload).toMatchObject({
      number: 'ACT-951',
      amount: 7000,
      creationMode: 'Manual',
      invoiceId: 951,
      clientId: 7,
    });
    expect(payload.lineItems).toEqual([]);
  });

  describe('mandatory act access checks', () => {
    const sourceAct = (overrides: Partial<Act> = {}): Act => ({
      id: 61, number: 'ACT-PRIVATE-61', date: '2026-04-20', amount: 12345.67,
      creationMode: 'Manual', status: 'Created', workflowStatus: 'Draft', type: 'Income',
      clientId: 901, clientName: 'Клиент из разрешённого акта', invoiceId: 618,
      invoiceNumber: 'INV-618', lineItems: [], createdAt: '2026-04-20T00:00:00Z', ...overrides,
    });
    const deferred = <T,>() => {
      let resolve!: (value: T) => void;
      let reject!: (reason: unknown) => void;
      const promise = new Promise<T>((accept, decline) => { resolve = accept; reject = decline; });
      return { promise, resolve, reject };
    };
    const element = (value: Act, submit: (payload: ActInput) => Promise<void>, unavailable: (id: number, status: 401 | 403 | 404) => void = vi.fn(), open = true) => (
      <ActModal open={open} mode="edit" act={value} onClose={vi.fn()} onSubmit={submit}
        submitting={false} clients={[]} responsibles={[]} lookupsLoading={false}
        canViewInvoices={false} presentation="page" onActUnavailable={unavailable} />
    );
    const saveButton = () => screen.getByRole('button', { name: 'Сохранить' }) as HTMLButtonElement;
    const ready = async () => { await waitFor(() => expect(saveButton().disabled).toBe(false)); };

    beforeEach(() => { getActMock.mockReset(); });

    it.each([401, 403, 404] as const)('removes cached act data after mandatory GET %s and never submits it', async (status) => {
      const pending = deferred<Act>();
      getActMock.mockReturnValue(pending.promise);
      const submit = vi.fn<(payload: ActInput) => Promise<void>>().mockResolvedValue(undefined);
      const unavailable = vi.fn();
      render(element(sourceAct(), submit, unavailable));
      const form = screen.getByLabelText('Номер акта').closest('form');
      expect(form).not.toBeNull();
      expect(saveButton().disabled).toBe(true);
      fireEvent.submit(form!);
      expect(submit).not.toHaveBeenCalled();

      await act(async () => { pending.reject(new Error(`HTTP ${status} Forbidden - secret internal URL`)); });

      expect(await screen.findByRole('alert')).toBeTruthy();
      expect(screen.queryByDisplayValue('ACT-PRIVATE-61')).toBeNull();
      expect(screen.queryByDisplayValue('Клиент из разрешённого акта')).toBeNull();
      expect(screen.queryByRole('button', { name: 'Сохранить' })).toBeNull();
      expect(screen.queryByText(/secret internal URL/)).toBeNull();
      expect(unavailable).toHaveBeenCalledTimes(1);
      expect(unavailable).toHaveBeenCalledWith(61, status);
      expect(submit).not.toHaveBeenCalled();
      expect(getClientsMock).not.toHaveBeenCalled();
      expect(getClientsV2Mock).not.toHaveBeenCalled();
    });

    it.each(['null', 'wrong-id'] as const)('does not authorise cached data after a %s mandatory response', async (response) => {
      getActMock.mockResolvedValue(response === 'null' ? null : sourceAct({ id: 62 }));
      const submit = vi.fn<(payload: ActInput) => Promise<void>>().mockResolvedValue(undefined);
      const unavailable = vi.fn();
      render(element(sourceAct(), submit, unavailable));
      expect(await screen.findByRole('alert')).toBeTruthy();
      expect(screen.queryByDisplayValue('ACT-PRIVATE-61')).toBeNull();
      expect(screen.queryByRole('button', { name: 'Сохранить' })).toBeNull();
      expect(unavailable).toHaveBeenCalledTimes(1);
      expect(unavailable).toHaveBeenCalledWith(61, 404);
      expect(submit).not.toHaveBeenCalled();
    });

    it('keeps a same-session draft after network failure and retries GET before allowing one save', async () => {
      const retry = deferred<Act>();
      getActMock.mockRejectedValueOnce(new Error('HTTP 503 Service Unavailable - network failure')).mockReturnValueOnce(retry.promise);
      const submit = vi.fn<(payload: ActInput) => Promise<void>>().mockResolvedValue(undefined);
      const unavailable = vi.fn();
      render(element(sourceAct(), submit, unavailable));
      await screen.findByRole('button', { name: 'Повторить загрузку акта' });
      fireEvent.change(screen.getByLabelText('Комментарий'), { target: { value: 'Несохранённый черновик' } });
      fireEvent.submit(screen.getByLabelText('Номер акта').closest('form')!);
      expect(saveButton().disabled).toBe(true);
      expect(submit).not.toHaveBeenCalled();

      fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку акта' }));
      expect(getActMock).toHaveBeenCalledTimes(2);
      expect(screen.getByDisplayValue('Несохранённый черновик')).toBeTruthy();
      await act(async () => { retry.resolve(sourceAct()); });
      await ready();
      expect(screen.getByDisplayValue('Несохранённый черновик')).toBeTruthy();
      fireEvent.click(saveButton());
      await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));
      expect(submit.mock.calls[0][0]).toMatchObject({ clientId: 901, invoiceId: 618, amount: 12345.67, comment: 'Несохранённый черновик' });
      expect(unavailable).not.toHaveBeenCalled();
    });

    it('keeps a freshly available act editable when only its optional client catalog is forbidden', async () => {
      getActMock.mockResolvedValue(sourceAct());
      const submit = vi.fn<(payload: ActInput) => Promise<void>>().mockResolvedValue(undefined);
      render(<ActModal open mode="edit" act={sourceAct()} onClose={vi.fn()} onSubmit={submit}
        submitting={false} clients={[]} responsibles={[]} lookupsLoading={false}
        lookupsError="HTTP 403 Forbidden - Справочник клиентов недоступен" canViewInvoices={false} presentation="page" />);
      await ready();
      expect((screen.getByPlaceholderText('Выберите контрагента') as HTMLInputElement).value).toBe('Клиент из разрешённого акта');
      fireEvent.click(saveButton());
      await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));
      expect(submit.mock.calls[0][0]).toMatchObject({ clientId: 901, invoiceId: 618, amount: 12345.67 });
      expect(getClientsMock).not.toHaveBeenCalled();
      expect(getClientsV2Mock).not.toHaveBeenCalled();
    });

    it.each(['resolve', 'reject'] as const)('ignores the first A %s after an A-to-B-to-A route change', async (outcome) => {
      const first = deferred<Act>();
      const latest = deferred<Act>();
      const a = sourceAct();
      getActMock.mockReturnValueOnce(first.promise).mockReturnValueOnce(new Promise<Act>(() => {})).mockReturnValueOnce(latest.promise);
      const submit = vi.fn<(payload: ActInput) => Promise<void>>().mockResolvedValue(undefined);
      const unavailable = vi.fn();
      const { rerender } = render(element(a, submit, unavailable));
      rerender(element(sourceAct({ id: 62, number: 'B' }), submit, unavailable));
      rerender(element(a, submit, unavailable));
      await act(async () => {
        if (outcome === 'resolve') first.resolve(a);
        else first.reject(new Error('HTTP 403 Forbidden - old A'));
      });
      expect(saveButton().disabled).toBe(true);
      fireEvent.submit(screen.getByLabelText('Номер акта').closest('form')!);
      expect(submit).not.toHaveBeenCalled();
      expect(unavailable).not.toHaveBeenCalled();
      await act(async () => { latest.resolve(a); });
      await ready();
      expect(screen.queryByRole('alert')).toBeNull();
      expect(getActMock).toHaveBeenCalledTimes(3);
    });

    it.each(['resolve', 'reject'] as const)('does not reuse the previous actor data or late %s for the same act', async (outcome) => {
      const first = deferred<Act>();
      const current = deferred<Act>();
      const value = sourceAct();
      getActMock.mockReturnValueOnce(first.promise).mockReturnValueOnce(current.promise);
      const unavailable = vi.fn();
      const submit = vi.fn<(payload: ActInput) => Promise<void>>().mockResolvedValue(undefined);
      const { rerender } = render(element(value, submit, unavailable));
      modalAuthState.user = { id: 2, role: { id: 2 } };
      rerender(element(value, submit, unavailable));
      expect(screen.queryByDisplayValue('ACT-PRIVATE-61')).toBeNull();
      await act(async () => {
        if (outcome === 'resolve') first.resolve(value);
        else first.reject(new Error('HTTP 403 Forbidden - old actor'));
      });
      expect(screen.queryByDisplayValue('ACT-PRIVATE-61')).toBeNull();
      expect(unavailable).not.toHaveBeenCalled();
      await act(async () => { current.resolve(sourceAct({ number: 'CURRENT-61', clientName: 'Текущий клиент' })); });
      await ready();
      expect(screen.getByDisplayValue('CURRENT-61')).toBeTruthy();
      expect(screen.queryByDisplayValue('ACT-PRIVATE-61')).toBeNull();
    });

    it('ignores a late denial after closing and reopening the same act', async () => {
      const first = deferred<Act>();
      const value = sourceAct();
      getActMock.mockReturnValueOnce(first.promise).mockResolvedValueOnce(value);
      const submit = vi.fn<(payload: ActInput) => Promise<void>>().mockResolvedValue(undefined);
      const unavailable = vi.fn();
      const { rerender } = render(element(value, submit, unavailable));
      rerender(element(value, submit, unavailable, false));
      rerender(element(value, submit, unavailable));
      await ready();
      await act(async () => { first.reject(new Error('HTTP 404 Not Found - previous opening')); });
      expect(saveButton().disabled).toBe(false);
      expect(unavailable).not.toHaveBeenCalled();
    });

    it('ignores invoice data requested by the previous actor for the same linked invoice', async () => {
      const previousInvoice = deferred<object>();
      const value = sourceAct();
      getActMock.mockResolvedValue(value);
      getInvoiceMock.mockReturnValueOnce(previousInvoice.promise).mockResolvedValueOnce({
        id: 618, number: 'INV-618', date: '2026-04-20', amount: 12345.67,
        type: 'Income', clientId: 901, lineItems: [], acts: [],
      });
      const submit = vi.fn<(payload: ActInput) => Promise<void>>().mockResolvedValue(undefined);
      const view = () => <ActModal open mode="edit" act={value} onClose={vi.fn()} onSubmit={submit}
        submitting={false} clients={[]} responsibles={[]} lookupsLoading={false} presentation="page" />;
      const { rerender } = render(view());
      await waitFor(() => expect(getInvoiceMock).toHaveBeenCalledTimes(1));
      modalAuthState.user = { id: 2, role: { id: 2 } };
      rerender(view());
      await ready();
      await waitFor(() => expect(getInvoiceMock).toHaveBeenCalledTimes(2));

      await act(async () => { previousInvoice.resolve({
        id: 618, number: 'OLD-PRIVATE-INVOICE', date: '2026-04-20', amount: 999999,
        type: 'Income', clientId: 777, clientName: 'Прежний закрытый клиент',
        lineItems: [{ id: 1, name: 'Прежняя закрытая позиция', quantity: 1, price: 999999 }], acts: [],
      }); });

      expect(screen.queryByText('OLD-PRIVATE-INVOICE')).toBeNull();
      expect(screen.queryByDisplayValue('OLD-PRIVATE-INVOICE')).toBeNull();
      expect(screen.queryByText('Прежняя закрытая позиция')).toBeNull();
      expect(screen.queryByDisplayValue('Прежний закрытый клиент')).toBeNull();
      expect(screen.getByDisplayValue('12345.67')).toBeTruthy();
      expect(submit).not.toHaveBeenCalled();
    });


    it.each(['stale', 'ordinary'])('caller ownership result: %s preserves correct error visibility', async kind => {
      const error = kind === 'stale' ? new StaleUiOperationError() : new Error('Обычная ошибка документа');
      const value = sourceAct(); getActMock.mockResolvedValue(value); const submit = vi.fn().mockRejectedValue(error);
      render(element(value, submit)); await ready(); fireEvent.click(saveButton());
      await waitFor(() => expect(submit).toHaveBeenCalledOnce());
      await waitFor(() => expect(Boolean(screen.queryByText(error.message))).toBe(kind === 'ordinary'));
    });
    it('keeps the next actor draft when an older save rejects late', async () => {
      const save = deferred<void>();
      const value = sourceAct();
      getActMock.mockResolvedValue(value);
      const submit = vi.fn<(payload: ActInput) => Promise<void>>().mockReturnValue(save.promise);
      const { rerender } = render(element(value, submit));
      await ready();
      fireEvent.click(saveButton());
      expect(submit).toHaveBeenCalledTimes(1);
      modalAuthState.user = { id: 2, role: { id: 2 } };
      getActMock.mockResolvedValue(sourceAct({ number: 'CURRENT-61' }));
      rerender(element(value, submit));
      await ready();
      fireEvent.change(screen.getByLabelText('Комментарий'), { target: { value: 'Черновик текущего сотрудника' } });

      await act(async () => { save.reject(new Error('Ошибка прежнего сохранения')); });

      expect(screen.getByDisplayValue('CURRENT-61')).toBeTruthy();
      expect(screen.getByDisplayValue('Черновик текущего сотрудника')).toBeTruthy();
      expect(screen.queryByText('Ошибка прежнего сохранения')).toBeNull();
      expect(submit).toHaveBeenCalledTimes(1);
    });

    it('removes an open counterparty picker when mandatory access is denied', async () => {
      const pending = deferred<Act>();
      getActMock.mockReturnValue(pending.promise);
      render(element(sourceAct(), vi.fn<(payload: ActInput) => Promise<void>>().mockResolvedValue(undefined)));
      fireEvent.click(screen.getByRole('button', { name: 'Выбрать' }));
      await screen.findByText('Базовый клиент');
      await act(async () => { pending.reject(new Error('HTTP 403 Forbidden')); });
      expect(await screen.findByRole('alert')).toBeTruthy();
      expect(screen.queryByText('Базовый клиент')).toBeNull();
      expect(screen.queryByPlaceholderText('Выберите контрагента')).toBeNull();
    });
  });

  describe('retained counterparty labels from an available act', () => {
    const createRetainedAct = (overrides: Partial<Act> = {}): Act => ({
      id: 41, number: 'ACT-41', date: '2026-04-20', amount: 12345.67,
      creationMode: 'Manual', status: 'Created', workflowStatus: 'Draft', type: 'Income',
      clientId: 901, clientName: 'Клиент из доступного акта', legalEntityId: null,
      invoiceId: 618, invoiceNumber: 'INV-618', lineItems: [], createdAt: '2026-04-20T00:00:00Z',
      ...overrides,
    });
    const editorElement = (act: Act | null, onSubmit: (payload: ActInput) => Promise<void>, clients: Client[] = []) => (
      <ActModal
        open mode={act ? 'edit' : 'create'} act={act} onClose={vi.fn()} onSubmit={onSubmit}
        submitting={false} clients={clients} responsibles={[]} lookupsLoading={false}
        canViewInvoices={false} presentation="page"
      />
    );
    const counterpartyInput = () => screen.getByPlaceholderText('Выберите контрагента') as HTMLInputElement;
    const saveAct = async () => {
      const button = screen.getByRole('button', { name: 'Сохранить' }) as HTMLButtonElement;
      await waitFor(() => expect(button.disabled).toBe(false));
      fireEvent.click(button);
    };

    beforeEach(() => {
      getActMock.mockReset().mockResolvedValue(createRetainedAct());
    });

    it.each([
      ['Income', 'client', 901, 'Клиент из доступного акта'],
      ['Expense', 'client', 902, 'Поставщик из доступного акта'],
      ['Income', 'legalEntity', 903, 'ООО Клиент из доступного акта'],
      ['Expense', 'legalEntity', 904, 'ООО Поставщик из доступного акта'],
    ] as const)('shows the retained %s %s label and preserves IDs and money on submit', async (type, kind, id, name) => {
      const act = createRetainedAct({
        type, clientId: kind === 'client' ? id : null, legalEntityId: kind === 'legalEntity' ? id : null,
        clientName: kind === 'client' ? name : null, legalEntityName: kind === 'legalEntity' ? name : null,
      });
      getActMock.mockResolvedValue(act);
      const onSubmit = vi.fn().mockResolvedValue(undefined);
      render(editorElement(act, onSubmit));

      expect(counterpartyInput().value).toBe(name);
      await saveAct();

      await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
      expect(onSubmit.mock.calls[0]?.[0]).toMatchObject({
        number: 'ACT-41', amount: 12345.67, invoiceId: 618, invoiceNumber: 'INV-618',
        clientId: act.clientId, legalEntityId: act.legalEntityId, lineItems: [],
      });
      expect(getClientsMock).not.toHaveBeenCalled();
      expect(getClientsV2Mock).not.toHaveBeenCalled();
      expect(getLegalEntitiesMock).not.toHaveBeenCalled();
      expect(getLegalEntitiesPagedMock).not.toHaveBeenCalled();
      expect(getInvoiceMock).not.toHaveBeenCalled();
    });

    it('retains the legal entity label with the existing legal entity priority over a legacy client ID', async () => {
      const act = createRetainedAct({ legalEntityId: 903, legalEntityName: 'Приоритетная организация' });
      getActMock.mockResolvedValue(act);
      const onSubmit = vi.fn().mockResolvedValue(undefined);
      render(editorElement(act, onSubmit, [createClient(901, 'Клиент из каталога')]));

      expect(counterpartyInput().value).toBe('Приоритетная организация');
      await saveAct();

      await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
      expect(onSubmit.mock.calls[0]?.[0]).toMatchObject({ clientId: null, legalEntityId: 903, amount: 12345.67, invoiceId: 618 });
    });

    it('uses the fresh detail name for the same act and owner instead of the older prop name', async () => {
      const act = createRetainedAct();
      getActMock.mockResolvedValue({ ...act, clientName: 'Актуальное имя клиента' });
      render(editorElement(act, vi.fn()));

      await waitFor(() => expect(counterpartyInput().value).toBe('Актуальное имя клиента'));

      expect(getActMock).toHaveBeenCalledTimes(1);
      expect(getClientsMock).not.toHaveBeenCalled();
      expect(getClientsV2Mock).not.toHaveBeenCalled();
    });

    it('does not reuse a prop name when fresh detail has a different owner kind with the same numeric ID', async () => {
      const act = createRetainedAct();
      getActMock.mockResolvedValue({ ...act, clientId: null, legalEntityId: 901, legalEntityName: 'Другая организация' });
      render(editorElement(act, vi.fn()));

      await waitFor(() => expect(counterpartyInput().value).toBe('Не выбран'));

      expect(screen.queryByDisplayValue('Клиент из доступного акта')).toBeNull();
      expect(screen.queryByDisplayValue('Другая организация')).toBeNull();
    });

    it('does not resurrect an older prop name when fresh detail no longer contains that name', async () => {
      const act = createRetainedAct();
      getActMock.mockResolvedValue({ ...act, clientName: null });
      render(editorElement(act, vi.fn()));

      await waitFor(() => expect(counterpartyInput().value).toBe('Не выбран'));
    });

    it('replaces the retained name after an explicit selection without reusing the previous owner', async () => {
      const onSubmit = vi.fn().mockResolvedValue(undefined);
      render(editorElement(createRetainedAct(), onSubmit, [createClient(7, 'Иван Иванов')]));
      expect(counterpartyInput().value).toBe('Клиент из доступного акта');
      fireEvent.click(screen.getByRole('button', { name: 'Выбрать' }));
      await selectPickerOption('Иван Иванов');

      expect(counterpartyInput().value).toBe('Иван Иванов');
      await saveAct();

      await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
      expect(onSubmit.mock.calls[0]?.[0]).toMatchObject({ clientId: 7, legalEntityId: null, amount: 12345.67 });
      expect(onSubmit.mock.calls[0]?.[0].invoiceId).toBe(618);
    });

    it('removes the retained name after clearing the owner and keeps the existing empty manual act behavior', async () => {
      const onSubmit = vi.fn().mockResolvedValue(undefined);
      render(editorElement(createRetainedAct(), onSubmit));
      fireEvent.click(screen.getByRole('button', { name: 'Очистить' }));

      expect(counterpartyInput().value).toBe('');
      expect(screen.queryByDisplayValue('Клиент из доступного акта')).toBeNull();
      await saveAct();

      await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
      expect(onSubmit.mock.calls[0]?.[0]).toMatchObject({ clientId: null, legalEntityId: null, amount: 12345.67 });
      expect(onSubmit.mock.calls[0]?.[0].invoiceId).toBe(618);
    });

    it('uses the next act prop while its detail is pending instead of the previous act detail with the same owner', async () => {
      const initialAct = createRetainedAct();
      getActMock.mockResolvedValue({ ...initialAct, clientName: 'Имя из прежней детали' });
      const onSubmit = vi.fn();
      const { rerender } = render(editorElement(initialAct, onSubmit));
      await waitFor(() => expect(counterpartyInput().value).toBe('Имя из прежней детали'));
      getActMock.mockReturnValue(new Promise(() => {}));
      const nextAct = createRetainedAct({ id: 42, clientName: 'Имя из нового акта' });

      rerender(editorElement(nextAct, onSubmit));

      expect(counterpartyInput().value).toBe('Имя из нового акта');
      expect(screen.queryByDisplayValue('Имя из прежней детали')).toBeNull();
    });

    it('does not invent an act fallback when first creating a document from initial values', () => {
      render(
        <ActModal
          open mode="create" act={null} onClose={vi.fn()} onSubmit={vi.fn()} submitting={false}
          clients={[]} responsibles={[]} lookupsLoading={false} presentation="page" canViewInvoices={false}
          initialValues={{ clientId: 901, clientName: 'Имя из начальных значений' } as Partial<Act>}
        />,
      );

      expect(counterpartyInput().value).toBe('Не выбран');
      expect(screen.queryByDisplayValue('Имя из начальных значений')).toBeNull();
      expect(getActMock).not.toHaveBeenCalled();
      expect(getClientsMock).not.toHaveBeenCalled();
      expect(getClientsV2Mock).not.toHaveBeenCalled();
    });
  });
});
