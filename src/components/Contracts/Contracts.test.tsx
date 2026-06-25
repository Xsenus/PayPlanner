import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { defaultRolePermissions } from '../../types/permissions';
import type { Contract } from '../../types';
import { Contracts } from './Contracts';

const { useRolePermissionsMock } = vi.hoisted(() => ({
  useRolePermissionsMock: vi.fn(),
}));

function createPersistedTableSettings(
  columns: Array<{ key: string; defaultWidth?: number; defaultVisible?: boolean }>,
  defaultPageSize: number,
) {
  const orderedKeys = columns
    .filter((column) => column.defaultVisible !== false)
    .map((column) => column.key);

  return {
    columnOrder: orderedKeys,
    orderedVisibleColumns: orderedKeys,
    visibleColumns: orderedKeys,
    pageSize: defaultPageSize,
    setPageSize: vi.fn(),
    draggingColumn: null,
    toggleColumn: vi.fn(),
    moveColumn: vi.fn(),
    handleColumnDragStart: vi.fn(),
    handleColumnDragOver: vi.fn(),
    handleColumnDrop: vi.fn(),
    handleColumnDragEnd: vi.fn(),
    handleResizeStart: vi.fn(),
    getColumnWidth: (key: string) =>
      columns.find((column) => column.key === key)?.defaultWidth ?? 180,
    getTableWidth: (extraWidth = 0, minimumWidth = 0) =>
      Math.max(
        minimumWidth,
        orderedKeys.reduce(
          (sum, key) => sum + (columns.find((column) => column.key === key)?.defaultWidth ?? 180),
          extraWidth,
        ),
      ),
    resetTableSettings: vi.fn(),
  };
}

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({ user: { id: 7, role: { id: 1 } } }),
}));

vi.mock('../../contexts/TabContext', () => ({
  useTabNavigation: () => ({ setActiveTab: vi.fn() }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({ confirm: vi.fn().mockResolvedValue(true) }),
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: useRolePermissionsMock,
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({
    success: vi.fn(),
    error: vi.fn(),
    info: vi.fn(),
    warning: vi.fn(),
  }),
}));

vi.mock('../../hooks/useTranslation', () => ({
  useTranslation: () => ({ t: () => undefined }),
}));

vi.mock('../../hooks/useDebouncedValue', () => ({
  useDebouncedValue: <T,>(value: T) => value,
}));

vi.mock('../../hooks/useDocumentStatusSettings', () => ({
  useDocumentStatusSettings: () => [],
}));

vi.mock('../../hooks/usePersistedTableSettings', () => ({
  usePersistedTableSettings: ({
    columns,
    defaultPageSize,
  }: {
    columns: Array<{ key: string; defaultWidth?: number; defaultVisible?: boolean }>;
    defaultPageSize: number;
  }) => createPersistedTableSettings(columns, defaultPageSize),
}));

vi.mock('../ui/LookupSelect', () => ({
  LookupSelect: ({
    label,
    value,
    onChange,
    emptyOptionLabel,
  }: {
    label: string;
    value: string;
    onChange: (value: string) => void;
    emptyOptionLabel?: string;
  }) => (
    <label>
      {label}
      <select
        aria-label={label}
        value={value}
        onChange={(event) => onChange(event.target.value)}
      >
        <option value="">{emptyOptionLabel ?? 'Все'}</option>
      </select>
    </label>
  ),
}));

vi.mock('../ui/TableColumnSettings', () => ({
  TableColumnSettings: () => <div data-testid="table-column-settings" />,
}));

vi.mock('../ui/TablePagination', () => ({
  TablePagination: () => <div data-testid="table-pagination" />,
}));

vi.mock('../ui/StatusChangeModal', () => ({
  StatusChangeModal: () => null,
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getContracts: vi.fn(),
    getClientsV1: vi.fn(),
    deleteContract: vi.fn(),
    updateContractStatus: vi.fn(),
  },
}));

import { apiService } from '../../services/api';

const concludedContract: Contract = {
  id: 17,
  number: 'DOG-17',
  title: 'Договор с клиентом',
  date: '2026-06-17',
  startDate: '2026-06-18',
  description: 'Проверка статуса договора',
  billingMode: 'Fixed',
  status: 'Concluded',
  isOpenEnded: false,
  amount: 21000,
  validUntil: '2026-12-31',
  createdAt: '2026-06-17T08:00:00Z',
  updatedAt: '2026-06-17T09:00:00Z',
  invoiceAmountTotal: 0,
  actAmountTotal: 0,
  paymentAmountTotal: 0,
  clients: [
    {
      id: 77,
      name: 'Баннова Мария Александровна',
    },
  ],
};

describe('Contracts', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useRolePermissionsMock.mockReturnValue(defaultRolePermissions);
    vi.mocked(apiService.getClientsV1).mockResolvedValue([]);
    vi.mocked(apiService.getContracts).mockResolvedValue({
      items: [concludedContract],
      page: 1,
      pageSize: 20,
      total: 1,
    });
  });

  it('shows the concluded contract status and sends it to the API filter', async () => {
    render(<Contracts />);

    expect(await screen.findByText('DOG-17')).toBeTruthy();
    expect(screen.getAllByText('Подписан клиентом').length).toBeGreaterThanOrEqual(2);

    fireEvent.change(screen.getByLabelText('Статус'), {
      target: { value: 'Concluded' },
    });

    await waitFor(() => {
      expect(apiService.getContracts).toHaveBeenLastCalledWith(
        expect.objectContaining({ status: 'Concluded' }),
      );
    });
  });

  it('keeps client links and client filter closed when clients section is hidden', async () => {
    useRolePermissionsMock.mockReturnValue({
      ...defaultRolePermissions,
      clients: {
        ...defaultRolePermissions.clients,
        canView: false,
      },
    });
    const onOpenClient = vi.fn();

    render(<Contracts onOpenClient={onOpenClient} />);

    expect(await screen.findByText('DOG-17')).toBeTruthy();
    expect(screen.getByText('Баннова Мария Александровна')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Баннова Мария Александровна/ })).toBeNull();
    expect(screen.queryByLabelText('Клиент')).toBeNull();
    expect(apiService.getClientsV1).not.toHaveBeenCalled();
    expect(onOpenClient).not.toHaveBeenCalled();
  });
});
