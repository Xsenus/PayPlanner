import { fireEvent, render, screen } from '@testing-library/react';
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
  const { visibleColumns, orderedVisibleColumns, getColumnWidth, getTableWidth, handleResizeStart } = usePersistedTableSettings<TestColumnKey>({
    storageKey: 'pp.test_table_settings',
    settingsKey: 'test_table_settings',
    columns: [
      { key: 'number', defaultVisible: true, defaultWidth: 120 },
      { key: 'title', defaultVisible: true, defaultWidth: 220 },
      { key: 'date', defaultVisible: true, defaultWidth: 160 },
      { key: 'hidden', defaultVisible: false, defaultWidth: 90 },
    ],
    defaultPageSize: 10,
  });

  return (
    <>
      <div data-testid="visible-columns">{visibleColumns.join(',')}</div>
      <div data-testid="ordered-visible-columns">{orderedVisibleColumns.join(',')}</div>
      <div data-testid="title-width">{getColumnWidth('title')}</div>
      <div data-testid="table-width">{getTableWidth(52)}</div>
      <div data-testid="resize-title" onMouseDown={(event) => handleResizeStart('title', event)} />
    </>
  );
}

describe('usePersistedTableSettings', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.clearAllMocks();
    vi.mocked(apiService.getUserSetting).mockResolvedValue(null);
    vi.mocked(apiService.upsertUserSetting).mockResolvedValue({
      key: 'test_table_settings',
      value: '{}',
    });
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

  it('recalculates total table width when a column is resized', () => {
    render(<TableSettingsProbe />);

    expect(screen.getByTestId('title-width').textContent).toBe('220');
    expect(screen.getByTestId('table-width').textContent).toBe('552');

    fireEvent.mouseDown(screen.getByTestId('resize-title'), { clientX: 100 });
    fireEvent.mouseMove(window, { clientX: 160 });
    fireEvent.mouseUp(window);

    expect(screen.getByTestId('title-width').textContent).toBe('280');
    expect(screen.getByTestId('table-width').textContent).toBe('612');
  });
});
