import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from '../services/api';
import { usePersistedTableSettings } from './usePersistedTableSettings';

vi.mock('../services/api', () => ({
  apiService: {
    getUserSetting: vi.fn(),
    upsertUserSetting: vi.fn(),
  },
}));

type TestColumnKey = 'number' | 'title' | 'date' | 'hidden';

function TableSettingsProbe() {
  const { visibleColumns, orderedVisibleColumns } = usePersistedTableSettings<TestColumnKey>({
    storageKey: 'pp.test_table_settings',
    settingsKey: 'test_table_settings',
    columns: [
      { key: 'number', defaultVisible: true },
      { key: 'title', defaultVisible: true },
      { key: 'date', defaultVisible: true },
      { key: 'hidden', defaultVisible: false },
    ],
    defaultPageSize: 10,
  });

  return (
    <>
      <div data-testid="visible-columns">{visibleColumns.join(',')}</div>
      <div data-testid="ordered-visible-columns">{orderedVisibleColumns.join(',')}</div>
    </>
  );
}

describe('usePersistedTableSettings', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.clearAllMocks();
    vi.mocked(apiService.getUserSetting).mockResolvedValue(null);
    vi.mocked(apiService.upsertUserSetting).mockResolvedValue(null);
  });

  it('adds newly introduced default-visible columns to saved settings', () => {
    localStorage.setItem(
      'pp.test_table_settings',
      JSON.stringify({
        visibleColumns: ['number', 'date'],
        columnOrder: ['number', 'date', 'hidden'],
        columnWidths: {},
        pageSize: 10,
      }),
    );

    render(<TableSettingsProbe />);

    expect(screen.getByTestId('visible-columns').textContent).toBe('number,date,title');
    expect(screen.getByTestId('ordered-visible-columns').textContent).toBe('number,date,title');
  });
});
