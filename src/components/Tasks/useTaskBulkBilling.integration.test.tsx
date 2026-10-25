import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { DialogProvider } from '../../contexts/DialogContext';
import type { TaskJournalItem } from '../../utils/taskJournalStore';
import { useTaskBulkBilling } from './useTaskBulkBilling';

const bulk = vi.hoisted(() => vi.fn());
vi.mock('../../services/api', () => ({ apiService: { bulkUpdateTaskJournalItems: bulk, getTaskJournalItem: vi.fn() } }));
// Deliberate raw legacy DTO: bulk accepts a nullable token while the older store declaration requires string.
const first = [{ id: 1, title: 'Исходная задача', status: 'new', updatedAt: null }] as unknown as TaskJournalItem[];
function Harness({ tasks = first }) {
  const action = useTaskBulkBilling({ tasks, identityKey: '7', scopeKey: 'case:1:page:1', scopeLabel: 'Дело 1', canEdit: true, onApplied: vi.fn() });
  return <><button onClick={action.begin}>Открыть учёт</button>{action.dialog}</>;
}

describe('bulk billing with the real confirmation provider', () => {
  it('layers confirmation above the caller and supports cancel/confirm without bubbling Escape to the page', async () => {
    bulk.mockReset(); bulk.mockResolvedValue({ requested: 1, updated: 1, unchanged: 0, skipped: 0, conflicted: 0,
      items: [{ id: 1, outcome: 'updated', reason: null, updatedAt: '2026-10-24T02:03:04.7654321Z' }] });
    const parentEscape = vi.fn(); window.addEventListener('keydown', parentEscape);
    try {
      render(<DialogProvider><Harness /></DialogProvider>);
      fireEvent.click(screen.getByRole('button', { name: 'Открыть учёт' }));
      let caller = screen.getByRole('dialog', { name: 'Массовая смена учёта задач' });
      fireEvent.click(within(caller).getByRole('button', { name: 'Изменить учёт' }));
      let confirmation = await screen.findByRole('dialog', { name: 'Изменить учёт выбранных задач (1)?' });
      expect(caller.parentElement?.className).toContain('z-[2300]');
      expect(confirmation.parentElement?.className).toContain('z-[2400]');
      fireEvent.keyDown(within(confirmation).getByRole('button', { name: 'Отмена' }), { key: 'Escape' });
      await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Изменить учёт выбранных задач (1)?' })).toBeNull());
      expect(parentEscape).not.toHaveBeenCalled(); expect(bulk).not.toHaveBeenCalled();
      caller = screen.getByRole('dialog', { name: 'Массовая смена учёта задач' });
      fireEvent.click(within(caller).getByRole('button', { name: 'Изменить учёт' }));
      confirmation = await screen.findByRole('dialog', { name: 'Изменить учёт выбранных задач (1)?' });
      fireEvent.click(within(confirmation).getByRole('button', { name: 'Изменить учёт' }));
      await waitFor(() => expect(bulk).toHaveBeenCalledOnce());
      expect(await screen.findByText(/Изменено: 1/)).toBeTruthy();
    } finally { window.removeEventListener('keydown', parentEscape); }
  });
  it('cancels and confirms an explicit replacement using the same real provider', async () => {
    bulk.mockReset();
    const { rerender } = render(<DialogProvider><Harness /></DialogProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Открыть учёт' }));
    rerender(<DialogProvider><Harness tasks={[{ ...first[0], id: 2, title: 'Новая задача' }]} /></DialogProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Новый выбор с текущей страницы' }));
    let confirmation = await screen.findByRole('dialog', { name: 'Заменить зафиксированный выбор?' });
    fireEvent.click(within(confirmation).getByRole('button', { name: 'Отмена' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Заменить зафиксированный выбор?' })).toBeNull());
    expect(screen.getByText('#1 · Исходная задача')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Новый выбор с текущей страницы' }));
    confirmation = await screen.findByRole('dialog', { name: 'Заменить зафиксированный выбор?' });
    fireEvent.click(within(confirmation).getByRole('button', { name: 'Заменить выбор' }));
    await screen.findByText('#2 · Новая задача');
    expect(screen.queryByText('#1 · Исходная задача')).toBeNull(); expect(bulk).not.toHaveBeenCalled();
  });
});
