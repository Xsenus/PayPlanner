import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { LegalEntities } from './LegalEntities';
import { defaultRolePermissions } from '../../types/permissions';

const apiMocks = vi.hoisted(() => ({
  getLegalEntitiesPaged: vi.fn(),
}));

const permissionMocks = vi.hoisted(() => ({
  useRolePermissions: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getLegalEntitiesPaged: apiMocks.getLegalEntitiesPaged,
    getLegalEntity: vi.fn(),
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
  useToast: () => ({
    error: vi.fn(),
    success: vi.fn(),
  }),
}));

vi.mock('../ui/TablePagination', () => ({
  TablePagination: () => null,
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
  LegalEntityModal: () => null,
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
});
