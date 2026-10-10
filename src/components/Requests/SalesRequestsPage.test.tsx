import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { SalesRequestsPage } from './SalesRequestsPage';
import { apiService } from '../../services/api';
import type { SalesRequestItem, SalesRequestStatusItem } from '../../types';

const confirm = vi.fn().mockResolvedValue(true);

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({ success: vi.fn(), error: vi.fn(), info: vi.fn() }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({ confirm, prompt: vi.fn() }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getSalesRequestStatuses: vi.fn(),
    getSalesRequests: vi.fn(),
    getSalesRequest: vi.fn(),
    getTaskJournalAssignees: vi.fn(),
    searchSalesRequestClients: vi.fn(),
    createSalesRequest: vi.fn(),
    transitionSalesRequest: vi.fn(),
    createSalesRequestTask: vi.fn(),
    getSalesRequestReport: vi.fn(),
    replaceSalesRequestRules: vi.fn(),
    saveSalesRequestStatus: vi.fn(),
  },
}));

const statuses: SalesRequestStatusItem[] = [
  { id: 1, key: 'new', title: 'Новая', sortOrder: 1, finalKind: 'None', rules: [] },
  { id: 2, key: 'paid', title: 'Оплачено', sortOrder: 2, finalKind: 'Success', rules: [] },
  { id: 3, key: 'abandoned', title: 'Брошен', sortOrder: 3, finalKind: 'Failure', rules: [] },
];

const request = (overrides: Partial<SalesRequestItem> = {}): SalesRequestItem => ({
  id: 7,
  createdAt: '2026-10-10T09:00:00Z',
  source: 'Avito',
  clientId: 3,
  clientName: 'Иванов Иван',
  clientPhone: '+7 900 111-22-33',
  linkedCaseWasClosed: false,
  type: 'New',
  summary: 'Спор с застройщиком',
  statusId: 1,
  statusEnteredAt: '2026-10-10T09:00:00Z',
  responsibleUserId: 1,
  finalKind: 'None',
  version: 0,
  ...overrides,
});

const detail = (item = request()) => ({ request: item, tasks: [], events: [] });

describe('SalesRequestsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    confirm.mockResolvedValue(true);
    vi.mocked(apiService.getSalesRequestStatuses).mockResolvedValue(statuses);
    vi.mocked(apiService.getTaskJournalAssignees).mockResolvedValue([{ id: 1, name: 'Аня', email: 'a@x' }]);
    vi.mocked(apiService.getSalesRequests).mockResolvedValue({ total: 1, page: 1, pageSize: 50, items: [request()] });
    vi.mocked(apiService.getSalesRequest).mockResolvedValue(detail());
  });

  it('lists requests with status and responsible names', async () => {
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} />);

    expect(await screen.findByText('Иванов Иван')).toBeTruthy();
    expect(await screen.findByText('Аня')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Отчёт за месяц' })).toBeNull();
  });

  it('opens the request from a task link', async () => {
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} initialRequestId={7} />);

    expect(await screen.findByTestId('sales-request-detail')).toBeTruthy();
    expect(apiService.getSalesRequest).toHaveBeenCalledWith(7);
  });

  it('hides creation without the create right', async () => {
    render(<SalesRequestsPage allowCreate={false} allowEdit={false} allowAnalytics={false} />);

    await screen.findByText('Иванов Иван');
    expect(screen.queryByRole('button', { name: 'Новая заявка' })).toBeNull();
  });

  it('creates a request for a found client and offers existing cases', async () => {
    vi.mocked(apiService.searchSalesRequestClients).mockResolvedValue([
      { id: 3, name: 'Иванов Иван', phone: '+7 900', email: '', cases: [{ id: 87, caseNumber: '87', title: 'Старое дело', status: 'Closed' }] },
    ]);
    vi.mocked(apiService.createSalesRequest).mockResolvedValue(request({ id: 8 }));
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Новая заявка' }));
    await userEvent.type(screen.getByLabelText('Поиск клиента'), 'Иванов');
    await userEvent.click(await screen.findByRole('button', { name: /Иванов Иван/ }));
    expect(await screen.findByTestId('sales-request-cases')).toBeTruthy();
    await userEvent.click(screen.getByLabelText(/Старое дело/));
    await userEvent.selectOptions(screen.getByLabelText('Источник заявки'), 'Call');
    await userEvent.type(screen.getByLabelText('Суть запроса'), 'Нужен дубликат акта');
    await userEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() =>
      expect(apiService.createSalesRequest).toHaveBeenCalledWith(
        expect.objectContaining({ source: 'Call', clientId: 3, clientCaseId: 87, summary: 'Нужен дубликат акта' }),
      ),
    );
  });

  it('requires a payment id before moving to the success status', async () => {
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} />);

    await userEvent.click(await screen.findByText('Иванов Иван'));
    const paid = await screen.findByRole('button', { name: 'Оплачено' });
    expect((paid as HTMLButtonElement).disabled).toBe(true);
    await userEvent.type(screen.getByLabelText('Номер платежа'), '55');
    expect((paid as HTMLButtonElement).disabled).toBe(false);
    vi.mocked(apiService.transitionSalesRequest).mockResolvedValue(request({ statusId: 2, finalKind: 'Success', version: 1 }));
    await userEvent.click(paid);

    await waitFor(() =>
      expect(apiService.transitionSalesRequest).toHaveBeenCalledWith(7, expect.objectContaining({ toStatusId: 2, paidPaymentId: 55, version: 0 })),
    );
  });

  it('offers forced close when open tasks block a final status', async () => {
    vi.mocked(apiService.transitionSalesRequest)
      .mockRejectedValueOnce(new Error('По заявке есть незакрытые задачи. Закройте их или подтвердите принудительное закрытие.'))
      .mockResolvedValueOnce(request({ statusId: 3, finalKind: 'Failure', version: 1 }));
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} />);

    await userEvent.click(await screen.findByText('Иванов Иван'));
    await userEvent.click(await screen.findByRole('button', { name: 'Брошен' }));

    await waitFor(() => expect(confirm).toHaveBeenCalled());
    await waitFor(() =>
      expect(apiService.transitionSalesRequest).toHaveBeenLastCalledWith(7, expect.objectContaining({ toStatusId: 3, forceCloseTasks: true })),
    );
  });
});

describe('SalesRequestFunnelSettings', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiService.getSalesRequestStatuses).mockResolvedValue([
      { id: 1, key: 'new', title: 'Новая', sortOrder: 1, finalKind: 'None', rules: [{ id: 5, title: 'Принять заявку', dueInMinutes: 30 }] },
    ]);
    vi.mocked(apiService.getTaskJournalAssignees).mockResolvedValue([{ id: 1, name: 'Аня', email: 'a@x' }]);
    vi.mocked(apiService.getSalesRequests).mockResolvedValue({ total: 0, page: 1, pageSize: 50, items: [] });
  });

  it('is hidden without the configure right and saves edited rules for admins', async () => {
    const { unmount } = render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} />);
    await screen.findByText('Заявок нет.');
    expect(screen.queryByRole('button', { name: 'Настройка воронки' })).toBeNull();
    unmount();

    vi.mocked(apiService.replaceSalesRequestRules).mockResolvedValue([]);
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} allowConfigure />);
    await userEvent.click(await screen.findByRole('button', { name: 'Настройка воронки' }));
    await userEvent.click(await screen.findByRole('button', { name: /правил: 1/ }));
    await userEvent.selectOptions(screen.getByLabelText('Исполнитель правила'), '1');
    await userEvent.click(screen.getByRole('button', { name: 'Сохранить правила' }));

    await waitFor(() =>
      expect(apiService.replaceSalesRequestRules).toHaveBeenCalledWith(1, [{ title: 'Принять заявку', assigneeUserId: 1, dueInMinutes: 30, amountGreaterThan: null, source: null, requestType: null }]),
    );
  });
});

describe('SalesRequestFunnelSettings auto advance', () => {
  it('saves the auto advance target of a working status', async () => {
    vi.clearAllMocks();
    vi.mocked(apiService.getSalesRequestStatuses).mockResolvedValue([
      { id: 1, key: 'prep', title: 'Подготовка', sortOrder: 1, finalKind: 'None', rules: [] },
      { id: 2, key: 'sent', title: 'Отправлено', sortOrder: 2, finalKind: 'None', rules: [] },
    ]);
    vi.mocked(apiService.getTaskJournalAssignees).mockResolvedValue([]);
    vi.mocked(apiService.getSalesRequests).mockResolvedValue({ total: 0, page: 1, pageSize: 50, items: [] });
    vi.mocked(apiService.saveSalesRequestStatus).mockResolvedValue(statuses[0]);
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} allowConfigure />);

    await userEvent.click(await screen.findByRole('button', { name: 'Настройка воронки' }));
    await userEvent.click(await screen.findByRole('button', { name: /Подготовка/ }));
    await userEvent.selectOptions(screen.getByLabelText('Автопереход'), '2');

    await waitFor(() => expect(apiService.saveSalesRequestStatus).toHaveBeenCalledWith(1, { autoAdvanceToStatusId: 2 }));
  });
});

describe('SalesRequestFunnelSettings conditions and SLA', () => {
  it('saves a rule condition and the stage SLA', async () => {
    vi.clearAllMocks();
    vi.mocked(apiService.getSalesRequestStatuses).mockResolvedValue([
      { id: 1, key: 'new', title: 'Новая', sortOrder: 1, finalKind: 'None', rules: [{ id: 5, title: 'Принять заявку', dueInMinutes: 30 }] },
    ]);
    vi.mocked(apiService.getTaskJournalAssignees).mockResolvedValue([]);
    vi.mocked(apiService.getSalesRequests).mockResolvedValue({ total: 0, page: 1, pageSize: 50, items: [] });
    vi.mocked(apiService.replaceSalesRequestRules).mockResolvedValue([]);
    vi.mocked(apiService.saveSalesRequestStatus).mockResolvedValue(statuses[0]);
    render(<SalesRequestsPage allowCreate allowEdit allowAnalytics={false} allowConfigure />);

    await userEvent.click(await screen.findByRole('button', { name: 'Настройка воронки' }));
    await userEvent.click(await screen.findByRole('button', { name: /правил: 1/ }));
    await userEvent.type(screen.getByLabelText('Условие: сумма больше'), '50000');
    await userEvent.selectOptions(screen.getByLabelText('Условие: источник'), 'Avito');
    await userEvent.type(screen.getByLabelText('SLA в минутах'), '120');
    await userEvent.tab();
    await userEvent.click(screen.getByRole('button', { name: 'Сохранить правила' }));

    await waitFor(() =>
      expect(apiService.replaceSalesRequestRules).toHaveBeenCalledWith(1, [
        { title: 'Принять заявку', assigneeUserId: null, dueInMinutes: 30, amountGreaterThan: 50000, source: 'Avito', requestType: null },
      ]),
    );
    await waitFor(() => expect(apiService.saveSalesRequestStatus).toHaveBeenCalledWith(1, { slaMinutes: 120 }));
  });
});
