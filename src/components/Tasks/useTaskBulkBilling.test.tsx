import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { TaskJournalBulkUpdatePayload, TaskJournalItem } from '../../utils/taskJournalStore';
import { useTaskBulkBilling } from './useTaskBulkBilling';

const mocks = vi.hoisted(() => ({ confirm: vi.fn(), bulk: vi.fn(), get: vi.fn(), applied: vi.fn() }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm: mocks.confirm }) }));
vi.mock('../../services/api', () => ({ apiService: { bulkUpdateTaskJournalItems: mocks.bulk, getTaskJournalItem: mocks.get } }));
const rows = [1, 2].map((id) => ({ id, title: `Выбранная ${id}`, status: 'new', updatedAt: id === 1 ? '2026-10-24T01:02:03.1234567Z' : null })) as TaskJournalItem[];
function Harness({ tasks = rows, scope = 'company1:page1', actor = '7', canEdit = true, loading = false }) {
  const action = useTaskBulkBilling({ tasks, scopeKey: scope, scopeLabel: scope, identityKey: actor, canEdit, loading, onApplied: mocks.applied });
  return <><button onClick={action.begin}>Открыть учёт</button><span>{action.busy ? 'busy' : 'idle'}</span>{action.dialog}{action.error ? <p role="alert">{action.error}</p> : null}</>;
}
const open = () => fireEvent.click(screen.getByRole('button', { name: 'Открыть учёт' }));
const submit = () => fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: /^(Изменить учёт|Подтвердить повтор оставшихся)$/ }));
const result = (ids: number[], outcome: 'updated' | 'unchanged' | 'skipped' | 'conflict' = 'updated') => ({
  requested: ids.length, updated: outcome === 'updated' ? ids.length : 0, unchanged: outcome === 'unchanged' ? ids.length : 0,
  skipped: outcome === 'skipped' ? ids.length : 0, conflicted: outcome === 'conflict' ? ids.length : 0,
  items: ids.map((id) => ({ id, outcome, reason: outcome === 'conflict' ? 'versionConflict' : outcome === 'skipped' ? 'contractual' : null, updatedAt: '2026-10-24T02:03:04.7654321Z' })),
});

beforeEach(() => {
  vi.clearAllMocks(); mocks.confirm.mockResolvedValue(true);
  mocks.bulk.mockImplementation(async (payload: TaskJournalBulkUpdatePayload) => result(payload.ids));
  mocks.get.mockImplementation(async (id: number) => ({ ...rows.find((task) => task.id === id), id, updatedAt: '2026-10-24T03:04:05.1111111Z' }));
});

describe('shared safe bulk billing dialog', () => {
  it('sends one isolated bulk request, including explicit null/raw tokens and the completed opt-in', async () => {
    render(<Harness />); open();
    fireEvent.change(screen.getByLabelText('Новый тип учёта выбранных задач'), { target: { value: 'billable' } });
    fireEvent.click(screen.getByRole('checkbox', { name: /Также включить выбранные завершённые/ }));
    submit(); await waitFor(() => expect(mocks.applied).toHaveBeenCalledOnce());
    expect(mocks.bulk).toHaveBeenCalledExactlyOnceWith({ ids: [1, 2], billingClassification: 'billable', includeCompleted: true,
      expectedUpdatedAtById: { '1': '2026-10-24T01:02:03.1234567Z', '2': null } });
    expect(screen.getByRole('status').textContent).toContain('Изменено: 2');
  });
  it('keeps parameters and the frozen selection after cancelled confirmation, close/reopen and UI selection changes', async () => {
    mocks.confirm.mockResolvedValueOnce(false);
    const { rerender } = render(<Harness />); open();
    fireEvent.change(screen.getByLabelText('Новый тип учёта выбранных задач'), { target: { value: 'billable' } });
    submit(); await waitFor(() => expect(screen.getByText('idle')).toBeTruthy());
    fireEvent.click(screen.getByRole('button', { name: 'Закрыть' }));
    rerender(<Harness tasks={[{ ...rows[0], id: 99, title: 'Новая строка' }]} />); open();
    expect((screen.getByLabelText('Новый тип учёта выбранных задач', { selector: 'select' }) as HTMLSelectElement).value).toBe('billable');
    expect(screen.getByLabelText('Построчный итог смены учёта').textContent).toContain('Выбранная 2');
    submit(); await waitFor(() => expect(mocks.bulk).toHaveBeenCalledOnce());
    expect(mocks.bulk.mock.calls[0][0].ids).toEqual([1, 2]);
  });
  it('blocks duplicate clicks and closing while a response is pending', async () => {
    let resolve!: (value: ReturnType<typeof result>) => void;
    mocks.bulk.mockImplementation(() => new Promise((done) => { resolve = done; }));
    render(<Harness />); open(); submit();
    await waitFor(() => expect(mocks.bulk).toHaveBeenCalledOnce());
    fireEvent.click(screen.getByRole('button', { name: 'Ожидание операции…' }));
    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    expect(screen.getByRole('dialog')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Закрыть' }) as HTMLButtonElement).disabled).toBe(true);
    await act(async () => resolve(result([1, 2])));
    expect(mocks.bulk).toHaveBeenCalledOnce();
  });
  it('retries only the frozen failures after a fresh read and new confirmation, retaining earlier success', async () => {
    mocks.bulk.mockResolvedValueOnce({ requested: 2, updated: 1, unchanged: 0, skipped: 0, conflicted: 1,
      items: [result([1]).items[0], result([2], 'conflict').items[0]] }).mockResolvedValueOnce(result([2], 'unchanged'));
    render(<Harness />); open(); submit();
    await screen.findByText(/Изменено: 1.*Конфликтов: 1/);
    expect((screen.getByRole('button', { name: 'Подтвердить повтор оставшихся' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Обновить оставшиеся задачи' }));
    await waitFor(() => expect((screen.getByRole('button', { name: 'Подтвердить повтор оставшихся' }) as HTMLButtonElement).disabled).toBe(false));
    expect(mocks.get).toHaveBeenCalledExactlyOnceWith(2); submit();
    await screen.findByText(/Изменено: 1.*Уже установлен нужный учёт: 1.*Конфликтов: 0/);
    expect(mocks.bulk.mock.calls[1][0]).toEqual({ ids: [2], billingClassification: 'internal', includeCompleted: false,
      expectedUpdatedAtById: { '2': '2026-10-24T03:04:05.1111111Z' } });
    expect(mocks.confirm).toHaveBeenCalledTimes(2);
  });
  it.each([403, 409, 500])('preserves the operation on HTTP %i and never reports partial success', async (code) => {
    mocks.bulk.mockRejectedValueOnce(new Error(`HTTP ${code} - {"message":"Отказ операции"}`));
    render(<Harness />); open(); submit(); await screen.findByText(/Выбор и параметры сохранены|Весь запрос отменён/);
    expect(mocks.applied).not.toHaveBeenCalled(); expect(screen.queryByRole('status')).toBeNull();
    expect(screen.getByLabelText('Построчный итог смены учёта').children).toHaveLength(2);
    expect((screen.getByRole('button', { name: 'Подтвердить повтор оставшихся' }) as HTMLButtonElement).disabled).toBe(true);
  });
  it('treats a malformed HTTP 200 as unknown, retaining IDs until a fresh read', async () => {
    mocks.bulk.mockResolvedValueOnce({ requested: 2, updated: 2, skipped: 0 });
    render(<Harness />); open(); submit(); await screen.findByText(/неподтверждённый итог/);
    expect(mocks.applied).not.toHaveBeenCalled(); expect(screen.queryByRole('status')).toBeNull();
    expect(screen.getByRole('button', { name: 'Обновить оставшиеся задачи' })).toBeTruthy();
  });
  it.each(['scope', 'permission', 'actor'])('rechecks %s after confirmation and never sends an outdated decision', async (change) => {
    let resolve!: (answer: boolean) => void;
    mocks.confirm.mockImplementation(() => new Promise((done) => { resolve = done; }));
    const { rerender } = render(<Harness />); open(); submit();
    rerender(<Harness scope={change === 'scope' ? 'company2:page2' : undefined} canEdit={change !== 'permission'} actor={change === 'actor' ? '8' : '7'} />);
    await act(async () => resolve(true)); expect(mocks.bulk).not.toHaveBeenCalled();
    if (change === 'actor') expect(screen.queryByText(/Выбранная 1/)).toBeNull();
  });
  it('retains the fixed result but does not refresh a new company/page after an old response', async () => {
    let resolve!: (value: ReturnType<typeof result>) => void;
    mocks.bulk.mockImplementation(() => new Promise((done) => { resolve = done; }));
    const { rerender } = render(<Harness />); open(); submit(); await waitFor(() => expect(mocks.bulk).toHaveBeenCalledOnce());
    rerender(<Harness scope="company2:page2" tasks={[{ ...rows[0], id: 99 }]} />);
    await act(async () => resolve(result([1, 2])));
    expect(mocks.applied).not.toHaveBeenCalled(); expect(screen.getByRole('status').textContent).toContain('Изменено: 2');
    expect(screen.getByLabelText('Построчный итог смены учёта').textContent).not.toContain('#99');
  });
  it('keeps refresh-required state on a failed fresh read and sends nothing automatically', async () => {
    mocks.bulk.mockResolvedValueOnce(result([1, 2], 'conflict')); mocks.get.mockRejectedValue(new Error('HTTP 403 - Forbidden'));
    render(<Harness />); open(); submit(); await screen.findByText(/Конфликтов: 2/);
    fireEvent.click(screen.getByRole('button', { name: 'Обновить оставшиеся задачи' }));
    await screen.findByText(/Обновлены сведения 0 из 2/);
    expect((screen.getByRole('button', { name: 'Подтвердить повтор оставшихся' }) as HTMLButtonElement).disabled).toBe(true);
    expect(mocks.bulk).toHaveBeenCalledOnce();
  });
  it('refreshes readable frozen rows even when another row disappears from the current page or returns 403', async () => {
    mocks.bulk.mockResolvedValueOnce(result([1, 2], 'conflict')).mockResolvedValueOnce(result([2]));
    mocks.get.mockImplementation(async (id: number) => {
      if (id === 1) throw new Error('HTTP 403 - Forbidden');
      return { ...rows[1], updatedAt: '2026-10-24T03:04:05.1111111Z' };
    });
    const { rerender } = render(<Harness />); open(); submit(); await screen.findByText(/Конфликтов: 2/);
    rerender(<Harness tasks={[rows[1]]} />);
    fireEvent.click(screen.getByRole('button', { name: 'Обновить оставшиеся задачи' }));
    await screen.findByText(/Готово к повтору после свежего чтения: 1/);
    expect(mocks.get.mock.calls.map(([id]) => id).sort()).toEqual([1, 2]);
    expect(screen.getByText(/Задача недоступна для свежего чтения/)).toBeTruthy();
    submit(); await waitFor(() => expect(mocks.bulk).toHaveBeenCalledTimes(2));
    expect(mocks.bulk.mock.calls[1][0].ids).toEqual([2]);
    expect(mocks.bulk.mock.calls[1][0].expectedUpdatedAtById).toEqual({ '2': '2026-10-24T03:04:05.1111111Z' });
    expect(screen.getByLabelText('Построчный итог смены учёта').textContent).toContain('#1');
  });
  it('requires explicit confirmation before replacing the fixed operation with a new selection', async () => {
    const { rerender } = render(<Harness />); open();
    rerender(<Harness tasks={[{ ...rows[0], id: 99, title: 'Новая строка' }]} />);
    fireEvent.click(screen.getByRole('button', { name: 'Новый выбор с текущей страницы' }));
    await screen.findByText('#99 · Новая строка');
    expect(screen.queryByText('#2 · Выбранная 2')).toBeNull();
    expect(mocks.confirm).toHaveBeenCalledWith(expect.objectContaining({ title: 'Заменить зафиксированный выбор?' }));
    expect(mocks.bulk).not.toHaveBeenCalled();
  });
});
