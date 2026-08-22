import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { LegalEntities } from './LegalEntities';
import { defaultRolePermissions } from '../../types/permissions';

const apiMocks = vi.hoisted(() => ({
  getLegalEntitiesPaged: vi.fn(),
  getLegalEntity: vi.fn(),
}));

const permissionMocks = vi.hoisted(() => ({
  useRolePermissions: vi.fn(),
}));

const toastMocks = vi.hoisted(() => ({
  error: vi.fn(),
  success: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getLegalEntitiesPaged: apiMocks.getLegalEntitiesPaged,
    getLegalEntity: apiMocks.getLegalEntity,
    createLegalEntity: vi.fn(),
    updateLegalEntity: vi.fn(),
    deleteLegalEntity: vi.fn(),
    getUserSetting: vi.fn().mockResolvedValue(null),
    upsertUserSetting: vi.fn().mockResolvedValue(null),
    suggestLegalEntities: vi.fn(),
    findLegalEntityById: vi.fn(),
  },
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { id: 1, role: { id: 10, name: 'limited-lawyer' } },
  }),
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: permissionMocks.useRolePermissions,
}));

vi.mock('../../hooks/useTranslation', () => ({
  useTranslation: () => ({ t: () => '' }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({ confirm: vi.fn() }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => toastMocks,
}));

vi.mock('../ui/TablePagination', () => ({
  TablePagination: ({ page, totalPages, onPageChange }: {
    page: number;
    totalPages: number;
    onPageChange: (page: number) => void;
  }) => (
    <button type="button" disabled={page >= totalPages} onClick={() => onPageChange(page + 1)}>
      Вперед
    </button>
  ),
}));

vi.mock('../ui/TableColumnSettings', () => ({
  TableColumnSettings: ({ columns }: { columns: Array<{ key: string; label: string }> }) => (
    <div aria-label="Настройки колонок">
      {columns.map((column) => (
        <span key={column.key}>{column.label}</span>
      ))}
    </div>
  ),
}));

vi.mock('./LegalEntityModal', () => ({
  LegalEntityModal: ({ isOpen, onBack }: { isOpen: boolean; onBack: () => void }) => (
    isOpen ? <button type="button" onClick={onBack}>Назад из карточки</button> : null
  ),
}));

const legalEntity = {
  id: 7,
  shortName: 'ООО Ромашка',
  fullName: 'Общество с ограниченной ответственностью Ромашка',
  inn: '5400000000',
  kpp: '540001001',
  ogrn: '1234567890123',
  director: 'Иванов И.И.',
  phone: '+7 999 000-00-00',
  email: 'mail@example.test',
  address: 'Новосибирск',
  clientsCount: 1,
  casesCount: 1,
  contractsCount: 1,
  invoicesCount: 1,
  actsCount: 1,
  paymentsCount: 1,
  clients: [{ id: 1, name: 'Клиент' }],
  relatedCases: [{ id: 11, label: 'Дело №11', clientId: 1 }],
  relatedContracts: [{ id: 21, label: 'Договор №21' }],
  relatedInvoices: [{ id: 31, label: 'Счет №31', type: 'income' }],
  relatedActs: [{ id: 41, label: 'Акт №41' }],
  relatedPayments: [{ id: 51, label: 'Платеж №51', type: 'income' }],
  createdAt: '2026-06-25T00:00:00Z',
  updatedAt: '2026-06-25T01:00:00Z',
};

describe('LegalEntities permissions', () => {
  beforeEach(() => {
    apiMocks.getLegalEntitiesPaged.mockReset();
    apiMocks.getLegalEntity.mockReset();
    apiMocks.getLegalEntity.mockResolvedValue(legalEntity);
    apiMocks.getLegalEntitiesPaged.mockResolvedValue({
      items: [legalEntity],
      total: 1,
      page: 1,
      pageSize: 12,
    });
    permissionMocks.useRolePermissions.mockReturnValue({
      ...defaultRolePermissions,
      legalEntities: {
        ...defaultRolePermissions.legalEntities,
        canView: true,
        canCreate: true,
        canEdit: true,
        canDelete: true,
      },
      cases: { ...defaultRolePermissions.cases, canView: false },
      contracts: { ...defaultRolePermissions.contracts, canView: false },
      accounts: { ...defaultRolePermissions.accounts, canView: false },
      acts: { ...defaultRolePermissions.acts, canView: false },
      payments: { ...defaultRolePermissions.payments, canView: false },
    });
    window.localStorage.clear();
  });

  it('opens a legal entity directly without loading or showing the registry', async () => {
    const onDetailClose = vi.fn();

    render(<LegalEntities initialLegalEntityId={legalEntity.id} detailOnly onDetailClose={onDetailClose} />);

    await waitFor(() => expect(apiMocks.getLegalEntity).toHaveBeenCalledWith(legalEntity.id));
    expect(apiMocks.getLegalEntitiesPaged).not.toHaveBeenCalled();
    expect(screen.queryByRole('heading', { name: 'Юридические лица' })).toBeNull();

    await userEvent.click(await screen.findByRole('button', { name: 'Назад из карточки' }));
    expect(onDetailClose).toHaveBeenCalledTimes(1);
  });

  it('hides related finance and case columns when role cannot view those sections', async () => {
    const openPayment = vi.fn();

    render(<LegalEntities onOpenPayment={openPayment} />);

    await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalled());
    await userEvent.click(screen.getByRole('button', { name: /Таблица/ }));

    expect(screen.queryByRole('columnheader', { name: /Дела/ })).toBeNull();
    expect(screen.queryByRole('columnheader', { name: /Договоры/ })).toBeNull();
    expect(screen.queryByRole('columnheader', { name: /Счета/ })).toBeNull();
    expect(screen.queryByRole('columnheader', { name: /Акты/ })).toBeNull();
    expect(screen.queryByRole('columnheader', { name: /Платежи/ })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Платеж №51' })).toBeNull();
    expect(openPayment).not.toHaveBeenCalled();

    await userEvent.click(screen.getByRole('button', { name: /Колонки/ }));
    const columnSettings = screen.getByLabelText('Настройки колонок');
    expect(columnSettings.textContent).not.toContain('Дела');
    expect(columnSettings.textContent).not.toContain('Договоры');
    expect(columnSettings.textContent).not.toContain('Счета');
    expect(columnSettings.textContent).not.toContain('Акты');
    expect(columnSettings.textContent).not.toContain('Платежи');
  });

  it('combines rapid search input into one request with an abort signal', async () => {
    render(<LegalEntities />);
    await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(1));
    apiMocks.getLegalEntitiesPaged.mockClear();

    const searchInput = screen.getByPlaceholderText('Поиск по названию, ИНН, директору');
    fireEvent.change(searchInput, { target: { value: 'ро' } });
    fireEvent.change(searchInput, { target: { value: 'рома' } });
    fireEvent.change(searchInput, { target: { value: 'ромашка' } });

    expect(apiMocks.getLegalEntitiesPaged).not.toHaveBeenCalled();
    await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(1));
    expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledWith(
      expect.objectContaining({ search: 'ромашка', page: 1, pageSize: 12 }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
  });

  it('aborts an active request when the search changes', async () => {
    render(<LegalEntities />);
    await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(1));
    apiMocks.getLegalEntitiesPaged.mockClear();
    const signals: AbortSignal[] = [];
    const resolvePending: Array<(response: {
      items: Array<typeof legalEntity>;
      total: number;
      page: number;
      pageSize: number;
    }) => void> = [];
    apiMocks.getLegalEntitiesPaged.mockImplementation((_params, options) => {
      if (options?.signal) signals.push(options.signal);
      return new Promise((resolve) => resolvePending.push(resolve));
    });

    const searchInput = screen.getByPlaceholderText('Поиск по названию, ИНН, директору');
    fireEvent.change(searchInput, { target: { value: 'ромашка' } });
    await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(1));
    expect(signals[0]?.aborted).toBe(false);

    fireEvent.change(searchInput, { target: { value: 'ромашка сибирь' } });

    await waitFor(() => expect(signals[0]?.aborted).toBe(true));
    await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(2));
    resolvePending.forEach((resolve) => resolve({ items: [], total: 0, page: 1, pageSize: 12 }));
  });

  it('loads a changed search directly on the first page', async () => {
    apiMocks.getLegalEntitiesPaged.mockResolvedValue({
      items: [legalEntity],
      total: 24,
      page: 1,
      pageSize: 12,
    });
    render(<LegalEntities />);
    await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(1));

    fireEvent.click(screen.getByRole('button', { name: 'Вперед' }));
    await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(2));
    expect(apiMocks.getLegalEntitiesPaged).toHaveBeenLastCalledWith(
      expect.objectContaining({ page: 2 }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
    apiMocks.getLegalEntitiesPaged.mockClear();

    fireEvent.change(screen.getByPlaceholderText('Поиск по названию, ИНН, директору'), {
      target: { value: 'новое юрлицо' },
    });

    await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(1));
    expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledWith(
      expect.objectContaining({ search: 'новое юрлицо', page: 1 }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
  });
});
