import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { SpreadsheetImportReport } from '../types';
import { apiService } from '../services/api';
import { useSpreadsheetImportReport } from './useSpreadsheetImportReport';

const { getSpreadsheetImportReportMock } = vi.hoisted(() => ({
  getSpreadsheetImportReportMock: vi.fn(),
}));

vi.mock('../services/api', () => ({
  apiService: {
    getSpreadsheetImportReport: getSpreadsheetImportReportMock,
  },
}));

const emptyReport: SpreadsheetImportReport = {
  from: '2026-05-01',
  to: '2026-05-31',
  batchCount: 0,
  fileCount: 0,
  totalRows: 0,
  createdRows: 0,
  updatedRows: 0,
  skippedRows: 0,
  errorRows: 0,
  rollbackRows: 0,
  appliedCount: 0,
  appliedWithErrorsCount: 0,
  rolledBackCount: 0,
  rolledBackWithConflictsCount: 0,
  statuses: [],
};

function SpreadsheetImportReportProbe({ enabled }: { enabled: boolean }) {
  const { report, loading, error } = useSpreadsheetImportReport(
    { from: '2026-05-01', to: '2026-05-31' },
    { enabled },
  );

  return (
    <div>
      <div data-testid="loading">{loading ? 'loading' : 'ready'}</div>
      <div data-testid="error">{error ?? ''}</div>
      <div data-testid="batch-count">{report?.batchCount ?? 'none'}</div>
    </div>
  );
}

describe('useSpreadsheetImportReport', () => {
  beforeEach(() => {
    getSpreadsheetImportReportMock.mockReset();
  });

  it('does not request the report when disabled', async () => {
    render(<SpreadsheetImportReportProbe enabled={false} />);

    await waitFor(() => {
      expect(screen.getByTestId('loading').textContent).toBe('ready');
    });
    expect(screen.getByTestId('batch-count').textContent).toBe('none');
    expect(apiService.getSpreadsheetImportReport).not.toHaveBeenCalled();
  });

  it('loads the report when enabled', async () => {
    getSpreadsheetImportReportMock.mockResolvedValue(emptyReport);

    render(<SpreadsheetImportReportProbe enabled />);

    await waitFor(() => {
      expect(screen.getByTestId('batch-count').textContent).toBe('0');
    });
    expect(apiService.getSpreadsheetImportReport).toHaveBeenCalledWith({
      from: '2026-05-01',
      to: '2026-05-31',
    });
  });
});
