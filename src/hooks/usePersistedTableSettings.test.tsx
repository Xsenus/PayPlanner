import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
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

function TableSettingsProbe({ userId = 0, enabled = true, persistLocally = true }: { userId?: number; enabled?: boolean; persistLocally?: boolean }) {
  const { visibleColumns, orderedVisibleColumns, getColumnWidth, getTableWidth, handleResizeStart, toggleColumn, moveColumn } = usePersistedTableSettings<TestColumnKey>({
    enabled,
    persistLocally,
    storageKey: userId ? `pp.test_table_settings.${userId}` : 'pp.test_table_settings',
    settingsKey: userId ? `test_table_settings.${userId}` : 'test_table_settings',
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
      <button data-testid="hide-number" onClick={() => toggleColumn('number')} />
      <button data-testid="hide-title" onClick={() => toggleColumn('title')} />
      <button data-testid="hide-date" onClick={() => toggleColumn('date')} />
      <button data-testid="move-date-up" onClick={() => moveColumn('date', 'up')} />
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

  it('keeps local-only persistence when server synchronization is disabled', () => {
    render(<TableSettingsProbe enabled={false} />);
    fireEvent.click(screen.getByTestId('hide-title'));
    expect(JSON.parse(localStorage.getItem('pp.test_table_settings') ?? '{}').visibleColumns).toEqual(['number', 'date']);
    expect(apiService.getUserSetting).not.toHaveBeenCalled();
    expect(apiService.upsertUserSetting).not.toHaveBeenCalled();
  });

  it('can persist to the server without reading or replacing the local cache', async () => {
    const cached = JSON.stringify({ visibleColumns: ['title'] });
    localStorage.setItem('pp.test_table_settings', cached);
    vi.mocked(apiService.getUserSetting).mockResolvedValue({ key: 'test_table_settings', value: JSON.stringify({ visibleColumns: ['number', 'title', 'date'], pageSize: 20 }) });
    render(<TableSettingsProbe persistLocally={false} />);
    await waitFor(() => expect(apiService.getUserSetting).toHaveBeenCalledTimes(1));
    fireEvent.click(screen.getByTestId('hide-title'));
    await waitFor(() => expect(apiService.upsertUserSetting).toHaveBeenCalledTimes(1));
    expect(localStorage.getItem('pp.test_table_settings')).toBe(cached);
    expect(JSON.parse(vi.mocked(apiService.upsertUserSetting).mock.calls[0][1]).visibleColumns).toEqual(['number', 'date']);
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

  it('PAY-6: restores saved visibility, order and width from the server after a refresh', async () => {
    vi.mocked(apiService.getUserSetting).mockResolvedValue({
      key: 'test_table_settings',
      value: JSON.stringify({
        visibleColumns: ['date', 'number'],
        columnOrder: ['date', 'number', 'title', 'hidden'],
        columnWidths: { date: 300, unknown: 500 },
        pageSize: 10,
      }),
    });

    render(<TableSettingsProbe />);

    await waitFor(() => expect(screen.getByTestId('ordered-visible-columns').textContent).toBe('date,number'));
    expect(screen.getByTestId('visible-columns').textContent).toBe('date,number');
    expect(JSON.parse(localStorage.getItem('pp.test_table_settings') ?? '{}').columnWidths).not.toHaveProperty('unknown');
  });

  it('PAY-6: keeps settings of different accounts separate', () => {
    localStorage.setItem('pp.test_table_settings.1', JSON.stringify({
      visibleColumns: ['number'],
      columnOrder: ['number', 'title', 'date', 'hidden'],
      columnWidths: {},
      pageSize: 10,
    }));

    const first = render(<TableSettingsProbe userId={1} />);
    expect(screen.getByTestId('ordered-visible-columns').textContent).toBe('number');
    first.unmount();

    render(<TableSettingsProbe userId={2} />);
    expect(screen.getByTestId('ordered-visible-columns').textContent).toBe('number,title,date');
  });

  it('PAY-6: never hides the last visible column and persists a changed order', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      render(<TableSettingsProbe userId={3} />);
      await act(async () => { await vi.advanceTimersByTimeAsync(0); });

      fireEvent.click(screen.getByTestId('hide-number'));
      fireEvent.click(screen.getByTestId('hide-title'));
      fireEvent.click(screen.getByTestId('hide-date'));
      expect(screen.getByTestId('visible-columns').textContent).toBe('date');

      fireEvent.click(screen.getByTestId('hide-number'));
      fireEvent.click(screen.getByTestId('move-date-up'));
      expect(screen.getByTestId('ordered-visible-columns').textContent).toBe('number,date');

      await act(async () => { await vi.advanceTimersByTimeAsync(700); });
      const saved = JSON.parse(vi.mocked(apiService.upsertUserSetting).mock.calls.at(-1)?.[1] ?? '{}');
      expect(saved.visibleColumns).toEqual(expect.arrayContaining(['date', 'number']));
      expect(saved.columnOrder.slice(0, 3)).toEqual(['number', 'date', 'title']);
    } finally {
      vi.useRealTimers();
    }
  });
});
