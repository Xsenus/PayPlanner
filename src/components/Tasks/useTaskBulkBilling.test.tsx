import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { TaskJournalBulkUpdatePayload, TaskJournalItem } from '../../utils/taskJournalStore';
import { useTaskBulkBilling } from './useTaskBulkBilling';

const mocks = vi.hoisted(() => ({ confirm: vi.fn(), bulk: vi.fn(), get: vi.fn(), settings: vi.fn(), applied: vi.fn() }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm: mocks.confirm }) }));
vi.mock('../../services/api', () => ({ apiService: { bulkUpdateTaskJournalItems: mocks.bulk, getTaskJournalItem: mocks.get, getTaskJournalSettings: mocks.settings } }));
const rows = [1, 2].map((id) => ({ id, title: `Выбранная ${id}`, status: 'new', updatedAt: id === 1 ? '2026-10-24T01:02:03.1234567Z' : null })) as TaskJournalItem[];
function Harness({ tasks = rows, scope = 'company1:page1', actor = '7', canEdit = true, loading = false }) {
  const action = useTaskBulkBilling({ tasks, scopeKey: scope, scopeLabel: scope, identityKey: actor, canEdit, loading, onApplied: mocks.applied });
  return <><button onClick={action.begin}>Открыть учёт</button><span>{action.busy ? 'busy' : 'idle'}</span>{action.dialog}{action.error ? <p role="alert">{action.error}</p> : null}</>;
}
const open = async () => {
  fireEvent.click(screen.getByRole('button', { name: 'Открыть учёт' }));
  await waitFor(() => expect(screen.queryByText('Проверяем настройку учёта договорных задач…')).toBeNull());
};
const submit = () => fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: /^(Изменить учёт|Подтвердить повтор оставшихся)$/ }));
const result = (ids: number[], outcome: 'updated' | 'unchanged' | 'skipped' | 'conflict' = 'updated') => ({
  requested: ids.length, updated: outcome === 'updated' ? ids.length : 0, unchanged: outcome === 'unchanged' ? ids.length : 0,
  skipped: outcome === 'skipped' ? ids.length : 0, conflicted: outcome === 'conflict' ? ids.length : 0,
  items: ids.map((id) => ({ id, outcome, reason: outcome === 'conflict' ? 'versionConflict' : outcome === 'skipped' ? 'contractual' : null, updatedAt: '2026-10-24T02:03:04.7654321Z' })),
});

beforeEach(() => {
  vi.clearAllMocks(); mocks.confirm.mockResolvedValue(true);
  mocks.settings.mockReset(); mocks.settings.mockResolvedValue({ contractTaskBillingMode: 'retainContractTasks' });
  mocks.bulk.mockImplementation(async (payload: TaskJournalBulkUpdatePayload) => result(payload.ids));
  mocks.get.mockImplementation(async (id: number) => ({ ...rows.find((task) => task.id === id), id, updatedAt: '2026-10-24T03:04:05.1111111Z' }));
});

describe('shared safe bulk billing dialog', () => {
  it('refuses conflicting versions of one selected task before opening or sending', () => {
    const conflicting = [rows[0], { ...rows[0], updatedAt: null as unknown as string }];
    render(<Harness tasks={conflicting} />);
    fireEvent.click(screen.getByRole('button', { name: 'Открыть учёт' }));
    expect(screen.getByRole('alert').textContent).toMatch(/разные версии одной задачи/i);
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(mocks.bulk).not.toHaveBeenCalled(); expect(mocks.settings).not.toHaveBeenCalled();
  });
  it('keeps the original selection if its proposed replacement has conflicting versions', async () => {
    const { rerender } = render(<Harness />); await open();
    rerender(<Harness tasks={[{ ...rows[0], id: 99 }, { ...rows[0], id: 99, updatedAt: null as unknown as string }]} />);
    fireEvent.click(screen.getByRole('button', { name: 'Новый выбор с текущей страницы' }));
    expect(await screen.findByText(/разные версии одной задачи/i)).toBeTruthy();
    expect(screen.getByText('#1 · Выбранная 1')).toBeTruthy();
    expect(screen.getByText('#2 · Выбранная 2')).toBeTruthy();
    expect(mocks.confirm).not.toHaveBeenCalled(); expect(mocks.bulk).not.toHaveBeenCalled();
  });
  it('sends one isolated bulk request, including explicit null/raw tokens and the completed opt-in', async () => {
    render(<Harness />); await open();
    fireEvent.change(screen.getByLabelText('Новый тип учёта выбранных задач'), { target: { value: 'billable' } });
    fireEvent.click(screen.getByRole('checkbox', { name: /Также включить выбранные завершённые/ }));
    submit(); await waitFor(() => expect(mocks.applied).toHaveBeenCalledOnce());
    expect(mocks.bulk).toHaveBeenCalledExactlyOnceWith({ ids: [1, 2], billingClassification: 'billable', includeCompleted: true,
      expectedUpdatedAtById: { '1': '2026-10-24T01:02:03.1234567Z', '2': null } });
    expect(screen.getByRole('status').textContent).toContain('Изменено: 2');
  });
  it('keeps parameters and the frozen selection after cancelled confirmation, close/reopen and UI selection changes', async () => {
    mocks.confirm.mockResolvedValueOnce(false);
    const { rerender } = render(<Harness />); await open();
    fireEvent.change(screen.getByLabelText('Новый тип учёта выбранных задач'), { target: { value: 'billable' } });
    submit(); await waitFor(() => expect(screen.getByText('idle')).toBeTruthy());
    fireEvent.click(screen.getByRole('button', { name: 'Закрыть' }));
    rerender(<Harness tasks={[{ ...rows[0], id: 99, title: 'Новая строка' }]} />); await open();
    expect((screen.getByLabelText('Новый тип учёта выбранных задач', { selector: 'select' }) as HTMLSelectElement).value).toBe('billable');
    expect(screen.getByLabelText('Построчный итог смены учёта').textContent).toContain('Выбранная 2');
    submit(); await waitFor(() => expect(mocks.bulk).toHaveBeenCalledOnce());
    expect(mocks.bulk.mock.calls[0][0].ids).toEqual([1, 2]);
  });
  it('blocks duplicate clicks and closing while a response is pending', async () => {
    let resolve!: (value: ReturnType<typeof result>) => void;
    mocks.bulk.mockImplementation(() => new Promise((done) => { resolve = done; }));
    render(<Harness />); await open(); submit();
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
    render(<Harness />); await open(); submit();
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
    render(<Harness />); await open(); submit(); await screen.findByText(/Выбор и параметры сохранены|Весь запрос отменён/);
    expect(mocks.applied).not.toHaveBeenCalled(); expect(screen.queryByRole('status')).toBeNull();
    expect(screen.getByLabelText('Построчный итог смены учёта').children).toHaveLength(2);
    expect((screen.getByRole('button', { name: 'Подтвердить повтор оставшихся' }) as HTMLButtonElement).disabled).toBe(true);
  });
  it('treats a malformed HTTP 200 as unknown, retaining IDs until a fresh read', async () => {
    mocks.bulk.mockResolvedValueOnce({ requested: 2, updated: 2, skipped: 0 });
    render(<Harness />); await open(); submit(); await screen.findByText(/неподтверждённый итог/);
    expect(mocks.applied).not.toHaveBeenCalled(); expect(screen.queryByRole('status')).toBeNull();
    expect(screen.getByRole('button', { name: 'Обновить оставшиеся задачи' })).toBeTruthy();
  });
  it.each(['scope', 'permission', 'actor'])('rechecks %s after confirmation and never sends an outdated decision', async (change) => {
    let resolve!: (answer: boolean) => void;
    mocks.confirm.mockImplementation(() => new Promise((done) => { resolve = done; }));
    const { rerender } = render(<Harness />); await open(); submit();
    rerender(<Harness scope={change === 'scope' ? 'company2:page2' : undefined} canEdit={change !== 'permission'} actor={change === 'actor' ? '8' : '7'} />);
    await act(async () => resolve(true)); expect(mocks.bulk).not.toHaveBeenCalled();
    if (change === 'actor') expect(screen.queryByText(/Выбранная 1/)).toBeNull();
  });
  it('retains the fixed result but does not refresh a new company/page after an old response', async () => {
    let resolve!: (value: ReturnType<typeof result>) => void;
    mocks.bulk.mockImplementation(() => new Promise((done) => { resolve = done; }));
    const { rerender } = render(<Harness />); await open(); submit(); await waitFor(() => expect(mocks.bulk).toHaveBeenCalledOnce());
    rerender(<Harness scope="company2:page2" tasks={[{ ...rows[0], id: 99 }]} />);
    await act(async () => resolve(result([1, 2])));
    expect(mocks.applied).not.toHaveBeenCalled(); expect(screen.getByRole('status').textContent).toContain('Изменено: 2');
    expect(screen.getByLabelText('Построчный итог смены учёта').textContent).not.toContain('#99');
  });
  it('keeps refresh-required state on a failed fresh read and sends nothing automatically', async () => {
    mocks.bulk.mockResolvedValueOnce(result([1, 2], 'conflict')); mocks.get.mockRejectedValue(new Error('HTTP 403 - Forbidden'));
    render(<Harness />); await open(); submit(); await screen.findByText(/Конфликтов: 2/);
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
    const { rerender } = render(<Harness />); await open(); submit(); await screen.findByText(/Конфликтов: 2/);
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
  it('retries over 50 frozen tasks with bounded reads and never resends successful or inaccessible rows', async () => {
    const many = Array.from({ length: 60 }, (_, index) => ({ id: index + 1, title: `Задача ${index + 1}`,
      status: 'new', updatedAt: '2026-10-24T01:02:03.1234567Z' })) as TaskJournalItem[];
    const updated = many.slice(0, 5).map((task) => task.id);
    const remainingIds = many.slice(5).map((task) => task.id);
    mocks.bulk.mockResolvedValueOnce({ requested: 60, updated: 5, unchanged: 0, skipped: 0, conflicted: 55,
      items: [...result(updated).items, ...result(remainingIds, 'conflict').items] });
    let active = 0; let peak = 0;
    mocks.get.mockImplementation(async (id: number) => {
      active++; peak = Math.max(peak, active);
      try {
        await new Promise((resolve) => setTimeout(resolve, 1));
        if (id === 6) throw new Error('HTTP 403 - Forbidden');
        return { ...many[id - 1], updatedAt: '2026-10-24T03:04:05.1111111Z' };
      } finally { active--; }
    });
    render(<Harness tasks={many} />); await open(); submit();
    await screen.findByText(/Изменено: 5.*Конфликтов: 55/);
    fireEvent.click(screen.getByRole('button', { name: 'Обновить оставшиеся задачи' }));
    await screen.findByText(/Готово к повтору после свежего чтения: 54/);
    expect(mocks.get).toHaveBeenCalledTimes(55);
    expect(peak).toBeGreaterThan(1); expect(peak).toBeLessThanOrEqual(4);
    expect(mocks.get.mock.calls.map(([id]) => id).sort((a, b) => a - b)).toEqual(remainingIds);
    submit(); await waitFor(() => expect(mocks.bulk).toHaveBeenCalledTimes(2));
    const retried = mocks.bulk.mock.calls[1][0];
    expect(retried.ids).toEqual(remainingIds.filter((id) => id !== 6));
    expect(Object.keys(retried.expectedUpdatedAtById)).toHaveLength(54);
    expect(retried.expectedUpdatedAtById['7']).toBe('2026-10-24T03:04:05.1111111Z');
    expect(screen.getByLabelText('Построчный итог смены учёта').textContent).toContain('#6');
  });
  it('stops starting reads after a server failure and retries only confirmed remaining tasks', async () => {
    const many = Array.from({ length: 60 }, (_, index) => ({ id: index + 1, title: `Задача ${index + 1}`,
      status: 'new', updatedAt: '2026-10-24T01:02:03.1234567Z' })) as TaskJournalItem[];
    const completedIds = many.slice(0, 5).map((task) => task.id);
    const conflictIds = many.slice(5).map((task) => task.id);
    mocks.bulk.mockResolvedValueOnce({ requested: 60, updated: 5, unchanged: 0, skipped: 0, conflicted: 55,
      items: [...result(completedIds).items, ...result(conflictIds, 'conflict').items] });
    mocks.get.mockImplementation(async (id: number) => {
      await new Promise((resolve) => setTimeout(resolve, 1));
      if (id === 6) throw new Error('HTTP 503 - Unavailable');
      return { ...many[id - 1], updatedAt: '2026-10-24T03:04:05.1111111Z' };
    });
    render(<Harness tasks={many} />); await open(); submit();
    await screen.findByText(/Изменено: 5.*Конфликтов: 55/);
    fireEvent.click(screen.getByRole('button', { name: 'Обновить оставшиеся задачи' }));
    await screen.findByText(/Готово к повтору после свежего чтения: 3/);
    expect(mocks.get.mock.calls.map(([id]) => id).sort((a, b) => a - b)).toEqual([6, 7, 8, 9]);
    expect(screen.getByLabelText('Построчный итог смены учёта').textContent).toContain('Свежие сведения не получены');
    submit(); await waitFor(() => expect(mocks.bulk).toHaveBeenCalledTimes(2));
    expect(mocks.bulk.mock.calls[1][0].ids).toEqual([7, 8, 9]);
    await screen.findByText(/Перед повтором оставшихся 52 задач/);
    mocks.get.mockImplementation(async (id: number) => ({ ...many[id - 1], updatedAt: '2026-10-24T04:04:05.1111111Z' }));
    fireEvent.click(screen.getByRole('button', { name: 'Обновить оставшиеся задачи' }));
    await waitFor(() => expect((screen.getByRole('button', { name: 'Подтвердить повтор оставшихся' }) as HTMLButtonElement).disabled).toBe(false));
    submit(); await waitFor(() => expect(mocks.bulk).toHaveBeenCalledTimes(3));
    const finalIds = mocks.bulk.mock.calls[2][0].ids as number[];
    expect(finalIds).toEqual(conflictIds.filter((id) => ![7, 8, 9].includes(id)));
    expect(finalIds).not.toContain(1); expect(finalIds).not.toContain(7);
    expect(mocks.bulk.mock.calls[2][0].expectedUpdatedAtById['6']).toBe('2026-10-24T04:04:05.1111111Z');
  });
  it('requires explicit confirmation before replacing the fixed operation with a new selection', async () => {
    const { rerender } = render(<Harness />); await open();
    rerender(<Harness tasks={[{ ...rows[0], id: 99, title: 'Новая строка' }]} />);
    fireEvent.click(screen.getByRole('button', { name: 'Новый выбор с текущей страницы' }));
    await screen.findByText('#99 · Новая строка');
    expect(screen.queryByText('#2 · Выбранная 2')).toBeNull();
    expect(mocks.confirm).toHaveBeenCalledWith(expect.objectContaining({ title: 'Заменить зафиксированный выбор?' }));
    expect(mocks.bulk).not.toHaveBeenCalled();
  });
  it('waits for a fresh policy before enabling submission and never opts in automatically', async () => {
    let resolve!: (value: { contractTaskBillingMode: string }) => void;
    mocks.settings.mockImplementationOnce(() => new Promise((done) => { resolve = done; }));
    render(<Harness />); fireEvent.click(screen.getByRole('button', { name: 'Открыть учёт' }));
    const contract = screen.getByRole('checkbox', { name: /Также включить договорные задачи/ }) as HTMLInputElement;
    expect(contract.checked).toBe(false); expect(contract.disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Изменить учёт' }) as HTMLButtonElement).disabled).toBe(true);
    expect(mocks.bulk).not.toHaveBeenCalled();
    await act(async () => resolve({ contractTaskBillingMode: 'allowFutureClassification' }));
    expect(contract.checked).toBe(false); expect(contract.disabled).toBe(false);
    submit(); await waitFor(() => expect(mocks.bulk).toHaveBeenCalledOnce());
    expect(mocks.bulk.mock.calls[0][0]).not.toHaveProperty('allowContractClassification');
  });
  it('sends contract permission only after an explicit checkbox and confirms preservation', async () => {
    mocks.settings.mockResolvedValue({ contractTaskBillingMode: 'allowFutureClassification' });
    render(<Harness />); await open();
    fireEvent.click(screen.getByRole('checkbox', { name: /Также включить договорные задачи/ }));
    submit(); await waitFor(() => expect(mocks.bulk).toHaveBeenCalledOnce());
    expect(mocks.bulk.mock.calls[0][0]).toMatchObject({ ids: [1, 2], allowContractClassification: true });
    expect(mocks.confirm).toHaveBeenCalledWith(expect.objectContaining({ description: expect.stringContaining('договор, его сумма и способ расчёта сохраняются') }));
    expect(mocks.settings).toHaveBeenCalledTimes(2);
  });
  it.each([undefined, null, 'unknown', 'AllowFutureClassification'])('does not enable contractual tasks for mode %s', async mode => {
    mocks.settings.mockResolvedValue({ contractTaskBillingMode: mode });
    render(<Harness />); await open();
    const contract = screen.getByRole('checkbox', { name: /Также включить договорные задачи/ }) as HTMLInputElement;
    expect(contract.disabled).toBe(true); expect(contract.checked).toBe(false);
    submit(); await waitFor(() => expect(mocks.bulk).toHaveBeenCalledOnce());
    expect(mocks.bulk.mock.calls[0][0]).not.toHaveProperty('allowContractClassification');
  });
  it('fails closed on a settings read error and retries that read without sending task writes', async () => {
    mocks.settings.mockRejectedValueOnce(new Error('Нет сети'));
    render(<Harness />); await open();
    expect((screen.getByRole('button', { name: 'Изменить учёт' }) as HTMLButtonElement).disabled).toBe(true);
    expect(mocks.bulk).not.toHaveBeenCalled(); expect(mocks.get).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Повторить проверку настройки' }));
    await waitFor(() => expect((screen.getByRole('button', { name: 'Изменить учёт' }) as HTMLButtonElement).disabled).toBe(false));
    expect(mocks.bulk).not.toHaveBeenCalled();
    submit(); await waitFor(() => expect(mocks.bulk).toHaveBeenCalledOnce());
  });
  it('rechecks revoked policy after confirmation without POST or changing the frozen choice', async () => {
    mocks.settings.mockResolvedValueOnce({ contractTaskBillingMode: 'allowFutureClassification' }).mockResolvedValue({ contractTaskBillingMode: 'retainContractTasks' });
    render(<Harness />); await open();
    fireEvent.click(screen.getByRole('checkbox', { name: /Также включить договорные задачи/ }));
    submit(); await screen.findByText(/Запрос не отправлен; выбор и параметры сохранены/);
    expect(mocks.bulk).not.toHaveBeenCalled();
    expect((screen.getByRole('checkbox', { name: /Также включить договорные задачи/ }) as HTMLInputElement).checked).toBe(true);
    expect(screen.getByLabelText('Построчный итог смены учёта').children).toHaveLength(2);
  });
  it.each(['actor', 'scope', 'permission'])('ignores a settings response after an intervening %s change, including return to the original authority', async change => {
    let resolve!: (value: { contractTaskBillingMode: string }) => void;
    mocks.settings.mockImplementationOnce(() => new Promise((done) => { resolve = done; }));
    const { rerender } = render(<Harness />); fireEvent.click(screen.getByRole('button', { name: 'Открыть учёт' }));
    rerender(<Harness actor={change === 'actor' ? '8' : '7'} scope={change === 'scope' ? 'elsewhere' : undefined} canEdit={change !== 'permission'} />);
    rerender(<Harness />);
    await waitFor(() => expect(mocks.settings).toHaveBeenCalledTimes(2));
    await act(async () => resolve({ contractTaskBillingMode: 'allowFutureClassification' }));
    expect((screen.getByRole('checkbox', { name: /Также включить договорные задачи/ }) as HTMLInputElement).disabled).toBe(true);
    expect(mocks.bulk).not.toHaveBeenCalled();
  });
  it('reloads policy for an explicitly replaced selection and discards the older pending response', async () => {
    let resolve!: (value: { contractTaskBillingMode: string }) => void;
    mocks.settings.mockImplementationOnce(() => new Promise((done) => { resolve = done; }));
    const { rerender } = render(<Harness />); fireEvent.click(screen.getByRole('button', { name: 'Открыть учёт' }));
    rerender(<Harness tasks={[{ ...rows[0], id: 99 }]} />);
    fireEvent.click(screen.getByRole('button', { name: 'Новый выбор с текущей страницы' }));
    await screen.findByText('#99 · Выбранная 1');
    await waitFor(() => expect(mocks.settings).toHaveBeenCalledTimes(2));
    await act(async () => resolve({ contractTaskBillingMode: 'allowFutureClassification' }));
    expect((screen.getByRole('checkbox', { name: /Также включить договорные задачи/ }) as HTMLInputElement).disabled).toBe(true);
    submit(); await waitFor(() => expect(mocks.bulk).toHaveBeenCalledOnce());
    expect(mocks.bulk.mock.calls[0][0].ids).toEqual([99]);
    expect(mocks.bulk.mock.calls[0][0]).not.toHaveProperty('allowContractClassification');
  });
  it('keeps contract parameters fixed on retry and never resends a successful row after policy revocation', async () => {
    mocks.settings.mockResolvedValue({ contractTaskBillingMode: 'allowFutureClassification' });
    mocks.bulk.mockResolvedValueOnce({ requested: 2, updated: 1, unchanged: 0, skipped: 0, conflicted: 1,
      items: [result([1]).items[0], result([2], 'conflict').items[0]] }).mockResolvedValueOnce(result([2]));
    render(<Harness />); await open();
    fireEvent.click(screen.getByRole('checkbox', { name: /Также включить договорные задачи/ }));
    submit(); await screen.findByText(/Изменено: 1.*Конфликтов: 1/);
    expect((screen.getByRole('checkbox', { name: /Также включить договорные задачи/ }) as HTMLInputElement).disabled).toBe(true);
    mocks.settings.mockResolvedValue({ contractTaskBillingMode: 'retainContractTasks' });
    fireEvent.click(screen.getByRole('button', { name: 'Обновить оставшиеся задачи' }));
    await screen.findByText(/Повтор не отправлен; параметры сохранены/);
    expect(mocks.get).not.toHaveBeenCalled(); expect(mocks.bulk).toHaveBeenCalledOnce();
    mocks.settings.mockResolvedValue({ contractTaskBillingMode: 'allowFutureClassification' });
    fireEvent.click(screen.getByRole('button', { name: 'Обновить оставшиеся задачи' }));
    await waitFor(() => expect((screen.getByRole('button', { name: 'Подтвердить повтор оставшихся' }) as HTMLButtonElement).disabled).toBe(false));
    submit(); await waitFor(() => expect(mocks.bulk).toHaveBeenCalledTimes(2));
    expect(mocks.get).toHaveBeenCalledExactlyOnceWith(2);
    expect(mocks.bulk.mock.calls[1][0]).toEqual({ ids: [2], billingClassification: 'internal', includeCompleted: false,
      expectedUpdatedAtById: { '2': '2026-10-24T03:04:05.1111111Z' }, allowContractClassification: true });
  });

  it('does not POST when the post-confirmation policy read fails', async () => {
    mocks.settings.mockResolvedValueOnce({ contractTaskBillingMode: 'allowFutureClassification' }).mockRejectedValueOnce(new Error('HTTP 503 - Нет связи'));
    render(<Harness />); await open();
    fireEvent.click(screen.getByRole('checkbox', { name: /Также включить договорные задачи/ }));
    submit(); await screen.findByText(/Нет связи/);
    expect(mocks.bulk).not.toHaveBeenCalled(); expect(mocks.get).not.toHaveBeenCalled();
    expect((screen.getByRole('button', { name: 'Изменить учёт' }) as HTMLButtonElement).disabled).toBe(true);
  });
  it('resets the contract checkbox only for an explicitly confirmed replacement and reloads its policy', async () => {
    mocks.settings.mockResolvedValue({ contractTaskBillingMode: 'allowFutureClassification' });
    const { rerender } = render(<Harness />); await open();
    fireEvent.click(screen.getByRole('checkbox', { name: /Также включить договорные задачи/ }));
    rerender(<Harness tasks={[{ ...rows[0], id: 99 }]} />);
    fireEvent.click(screen.getByRole('button', { name: 'Новый выбор с текущей страницы' }));
    await screen.findByText('#99 · Выбранная 1');
    await waitFor(() => expect(mocks.settings).toHaveBeenCalledTimes(2));
    expect((screen.getByRole('checkbox', { name: /Также включить договорные задачи/ }) as HTMLInputElement).checked).toBe(false);
    expect(mocks.bulk).not.toHaveBeenCalled();
  });

  it('allows explicitly removing a revoked contract opt-in before any attempted POST and requires a new confirmation', async () => {
    mocks.settings.mockResolvedValueOnce({ contractTaskBillingMode: 'allowFutureClassification' }).mockResolvedValue({ contractTaskBillingMode: 'retainContractTasks' });
    render(<Harness />); await open();
    const contract = screen.getByRole('checkbox', { name: /Также включить договорные задачи/ }) as HTMLInputElement;
    fireEvent.click(contract); submit(); await screen.findByText(/Запрос не отправлен; выбор и параметры сохранены/);
    expect(contract.checked).toBe(true); expect(contract.disabled).toBe(false); expect(mocks.bulk).not.toHaveBeenCalled();
    fireEvent.click(contract);
    expect(contract.checked).toBe(false); expect(contract.disabled).toBe(true); expect(mocks.bulk).not.toHaveBeenCalled();
    submit(); await waitFor(() => expect(mocks.bulk).toHaveBeenCalledOnce());
    expect(mocks.bulk.mock.calls[0][0]).not.toHaveProperty('allowContractClassification');
    expect(mocks.confirm).toHaveBeenCalledTimes(2);
  });
  it('does not clear an already loaded policy when begin is repeated for the same open draft', async () => {
    render(<Harness />); await open();
    fireEvent.click(screen.getByRole('button', { name: 'Открыть учёт' }));
    expect(mocks.settings).toHaveBeenCalledOnce();
    expect((screen.getByRole('button', { name: 'Изменить учёт' }) as HTMLButtonElement).disabled).toBe(false);
    submit(); await waitFor(() => expect(mocks.bulk).toHaveBeenCalledOnce());
  });

});
