import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { SpreadsheetImportSettings } from './SpreadsheetImportSettings';

const mocks = vi.hoisted(() => ({
  getSpreadsheetImportBatches: vi.fn(),
  getSpreadsheetImportBatch: vi.fn(),
  getDatabaseStatus: vi.fn(),
  confirm: vi.fn(),
  toast: {
    success: vi.fn(),
    error: vi.fn(),
  },
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getSpreadsheetImportBatches: mocks.getSpreadsheetImportBatches,
    getSpreadsheetImportBatch: mocks.getSpreadsheetImportBatch,
    getDatabaseStatus: mocks.getDatabaseStatus,
  },
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({ confirm: mocks.confirm }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => mocks.toast,
}));

vi.mock('../../hooks/useUserTimeZone', () => ({
  useUserTimeZone: () => 'Europe/Samara',
}));

describe('SpreadsheetImportSettings', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.getSpreadsheetImportBatches.mockResolvedValue([{
      id: 7,
      title: 'Тестовая партия',
      status: 'Applied',
      totalRows: 60,
      createdRows: 60,
      updatedRows: 0,
      errorRows: 0,
      rollbackRows: 0,
      createdAt: '2026-08-16T01:00:00Z',
      rolledBackAt: null,
    }]);
    mocks.getDatabaseStatus.mockResolvedValue({
      provider: 'postgresql',
      displayName: 'PostgreSQL',
      supportsSqliteMaintenance: false,
      supportsSqliteImport: true,
    });
    const operations = Array.from({ length: 60 }, (_, index) => ({
      id: index + 1,
      sourceTitle: `Операция ${index + 1}`,
      sectionKey: 'acts',
      sourceFileName: 'audit.xml',
      sourceRowNumber: index + 1,
      action: 'Created',
      message: 'Создано.',
      rollbackAction: null,
      rollbackMessage: null,
      createdAt: '2026-08-16T01:00:00Z',
      rolledBackAt: null,
    }));
    mocks.getSpreadsheetImportBatch.mockImplementation((_batchId: number, page = 1, pageSize = 25) => Promise.resolve({
      id: 7,
      title: 'Тестовая партия',
      status: 'Applied',
      totalRows: 60,
      createdRows: 60,
      updatedRows: 0,
      errorRows: 0,
      rollbackRows: 0,
      createdAt: '2026-08-16T01:00:00Z',
      rolledBackAt: null,
      operationsTotal: 60,
      operationsPage: page,
      operationsPageSize: pageSize,
      operations: operations.slice((page - 1) * pageSize, page * pageSize),
    }));
  });

  it('renders large batch details page by page', async () => {
    render(<SpreadsheetImportSettings />);

    fireEvent.click(await screen.findByRole('button', { name: 'Детали' }));

    await waitFor(() => expect(mocks.getSpreadsheetImportBatch).toHaveBeenCalledWith(7, 1, 25));
    expect(await screen.findByText('Операция 1')).toBeTruthy();
    expect(screen.getByText('Операция 25')).toBeTruthy();
    expect(screen.queryByText('Операция 26')).toBeNull();
    expect(screen.getByText('1 / 3')).toBeTruthy();

    fireEvent.click(screen.getByTestId('pagination-next'));

    expect(await screen.findByText('Операция 26')).toBeTruthy();
    expect(screen.queryByText('Операция 1')).toBeNull();
    expect(screen.getByText('2 / 3')).toBeTruthy();
  });
});
