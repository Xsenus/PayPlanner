import { act, renderHook, waitFor } from '@testing-library/react';
import { createElement, StrictMode, type PropsWithChildren } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useDictionaries } from './useDictionaries';
import { useClientStatuses } from './useClientStatuses';
import { apiService } from '../services/api';
import { documentCopyTestSession } from '../test/documentCopySession';

vi.mock('../services/api', () => ({ apiService: { getDict: vi.fn(), getIncomeTypes: vi.fn(), getClientStatuses: vi.fn() } }));
const row = (id = 1, name = `Справочник ${id}`, isActive = true) => ({ id, name, description: '', colorHex: '', createdAt: '2026-11-01T00:00:00Z', isActive, paymentType: 'Income' as const });
type Options = { enabled?: boolean; ownerScope?: string };
const fixtures = [
  { name: 'dictionaries', target: () => vi.mocked(apiService.getDict), signal: () => vi.mocked(apiService.getDict).mock.calls[0]?.[1]?.signal, useHook(options: Options) { const state = useDictionaries(options); return { ...state, rows: state.dealTypes }; } },
  { name: 'statuses', target: () => vi.mocked(apiService.getClientStatuses), signal: () => vi.mocked(apiService.getClientStatuses).mock.calls[0]?.[0]?.signal, useHook(options: Options) { const state = useClientStatuses(options); return { ...state, rows: state.statuses }; } },
];
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }

describe('dictionary ownership', () => {
  let session: ReturnType<typeof documentCopyTestSession>;
  beforeEach(() => { vi.resetAllMocks(); session = documentCopyTestSession(); vi.mocked(apiService.getDict).mockResolvedValue([row()]); vi.mocked(apiService.getIncomeTypes).mockResolvedValue([]); vi.mocked(apiService.getClientStatuses).mockResolvedValue([row()]); });
  afterEach(() => session.restore());
  for (const fixture of fixtures) {
    it(`dictionary baseline: ${fixture.name} hides the prior caller scope synchronously`, async () => {
      const view = renderHook((options: Options) => fixture.useHook(options), { initialProps: { ownerScope: 'all' } }); await waitFor(() => expect(view.result.current.rows).toHaveLength(1));
      fixture.target().mockReturnValue(new Promise(() => {})); view.rerender({ ownerScope: 'own' }); expect(view.result.current.rows).toEqual([]);
    });
    it(`dictionary baseline: ${fixture.name} discards cached data on login replacement`, async () => {
      const view = renderHook(() => fixture.useHook({})); await waitFor(() => expect(view.result.current.rows).toHaveLength(1)); fixture.target().mockReturnValue(new Promise(() => {}));
      session.replace(); view.rerender(); expect(view.result.current.rows).toEqual([]);
    });
    it(`dictionary baseline: ${fixture.name} accepts only the newest refresh and its loading flag`, async () => {
      const first = deferred<ReturnType<typeof row>[]>(); const last = deferred<ReturnType<typeof row>[]>();
      // getDict is called for several groups; route by the round, not by one invocation.
      let round = 0; fixture.target().mockImplementation(() => (round === 0 ? first.promise : last.promise));
      const view = renderHook(() => fixture.useHook({})); act(() => { round = 1; void view.result.current.refresh(); });
      await act(async () => first.resolve([row(2, 'Прежний ответ')])); expect(view.result.current.rows).toEqual([]); expect(view.result.current.loading).toBe(true);
      await act(async () => last.resolve([row(3, 'Текущий ответ')])); expect(view.result.current.rows[0]?.id).toBe(3); expect(view.result.current.loading).toBe(false);
    });
    it(`dictionary baseline: ${fixture.name} cached refresh cannot start after disable`, async () => {
      const view = renderHook((options: Options) => fixture.useHook(options), { initialProps: { enabled: true } }); await waitFor(() => expect(view.result.current.loading).toBe(false));
      const refresh = view.result.current.refresh; view.rerender({ enabled: false }); const reads = fixture.target().mock.calls.length;
      await act(async () => { await refresh(); }); expect(fixture.target()).toHaveBeenCalledTimes(reads); expect(view.result.current.rows).toEqual([]); expect(view.result.current.loading).toBe(false);
    });
    it(`dictionary regression: ${fixture.name} event refresh supersedes an old read and error`, async () => {
      const old = deferred<ReturnType<typeof row>[]>(); fixture.target().mockReturnValueOnce(old.promise).mockResolvedValue([row(3)]);
      const view = renderHook(() => fixture.useHook({})); act(() => window.dispatchEvent(new Event('dicts:changed')));
      await waitFor(() => expect(view.result.current.rows[0]?.id).toBe(3)); await act(async () => old.reject(new Error('HTTP 403 Old refresh')));
      expect(view.result.current.rows[0]?.id).toBe(3); expect(view.result.current.error).toBeNull(); expect(view.result.current.loading).toBe(false); expect(fixture.signal()?.aborted).toBe(true);
    });
    it(`dictionary regression: ${fixture.name} disabled events do not fetch and keep a stable empty list`, async () => {
      const view = renderHook(() => fixture.useHook({ enabled: false })); const rows = view.result.current.rows;
      act(() => window.dispatchEvent(new Event('dicts:changed'))); view.rerender(); await act(async () => {});
      expect(view.result.current.rows).toBe(rows); expect(view.result.current.loading).toBe(false); expect(fixture.target()).not.toHaveBeenCalled();
    });
    it(`dictionary regression: ${fixture.name} verifies the session before cached callbacks without a render`, async () => {
      const view = renderHook(() => fixture.useHook({})); await waitFor(() => expect(view.result.current.loading).toBe(false)); const count = fixture.target().mock.calls.length;
      session.replace('actor'); await act(async () => { await view.result.current.refresh(); }); expect(fixture.target()).toHaveBeenCalledTimes(count);
    });
    it(`dictionary regression: ${fixture.name} unmount aborts and removes its event subscription`, async () => {
      fixture.target().mockReturnValue(new Promise(() => {})); const view = renderHook(() => fixture.useHook({})); const count = fixture.target().mock.calls.length;
      view.unmount(); expect(fixture.signal()?.aborted).toBe(true); window.dispatchEvent(new Event('dicts:changed')); expect(fixture.target()).toHaveBeenCalledTimes(count);
    });
    it(`dictionary regression: ${fixture.name} equivalent scope and ordinary token refresh do not refetch`, async () => {
      const view = renderHook(() => fixture.useHook({ enabled: true, ownerScope: 'same' })); await waitFor(() => expect(view.result.current.loading).toBe(false)); const count = fixture.target().mock.calls.length;
      session.refresh(); view.rerender(); await act(async () => {}); expect(fixture.target()).toHaveBeenCalledTimes(count);
    });
    it(`dictionary regression: ${fixture.name} retains data on a network failure but clears it on current denial`, async () => {
      const view = renderHook(() => fixture.useHook({})); await waitFor(() => expect(view.result.current.loading).toBe(false)); fixture.target().mockRejectedValueOnce(new Error('Temporary network'));
      await act(async () => { await view.result.current.refresh(); }); expect(view.result.current.rows).toHaveLength(1); expect(view.result.current.error).toContain('network');
      fixture.target().mockRejectedValueOnce(new Error('HTTP 401 Unauthorized')); await act(async () => { await view.result.current.refresh(); }); expect(view.result.current.rows).toEqual([]); expect(view.result.current.error).toContain('401');
      await act(async () => { await view.result.current.refresh(); }); expect(view.result.current.rows).toHaveLength(1); expect(view.result.current.error).toBeNull();
    });
    it(`dictionary regression: ${fixture.name} invalid identities do not overwrite a trusted list`, async () => {
      const view = renderHook(() => fixture.useHook({})); await waitFor(() => expect(view.result.current.loading).toBe(false)); fixture.target().mockResolvedValueOnce([row(2), row(2)]);
      await act(async () => { await view.result.current.refresh(); }); expect(view.result.current.rows[0]?.id).toBe(1); expect(view.result.current.error).toContain('подтвердить');
    });
    it(`dictionary regression: ${fixture.name} survives StrictMode cleanup replay`, async () => {
      const view = renderHook(() => fixture.useHook({}), { wrapper: ({ children }: PropsWithChildren) => createElement(StrictMode, null, children) });
      await waitFor(() => expect(view.result.current.rows[0]?.id).toBe(1)); expect(view.result.current.loading).toBe(false);
    });
    it(`dictionary reopen: ${fixture.name} cannot revive a disabled lifetime cache on reenable`, async () => {
      const view = renderHook((options: Options) => fixture.useHook(options), { initialProps: { enabled: true } }); await waitFor(() => expect(view.result.current.rows).toHaveLength(1));
      view.rerender({ enabled: false }); fixture.target().mockReturnValue(new Promise(() => {})); view.rerender({ enabled: true });
      expect(view.result.current.rows).toEqual([]); expect(view.result.current.loading).toBe(true);
    });
  }
  it('dictionary regression: default groups preserve the complete 17-request legacy catalogue and active filters', async () => {
    vi.mocked(apiService.getDict).mockResolvedValue([row(1, 'Active'), row(2, 'Inactive', false)]);
    const view = renderHook(() => useDictionaries()); await waitFor(() => expect(view.result.current.loading).toBe(false));
    expect(apiService.getDict).toHaveBeenCalledTimes(15); expect(apiService.getIncomeTypes).toHaveBeenCalledTimes(2);
    for (const group of ['dealTypes', 'paymentSources', 'paymentStatuses'] as const) expect(view.result.current[group]).toHaveLength(2);
    for (const group of ['services', 'serviceCategories', 'clientStatuses', 'countries', 'baseCurrencies', 'serviceLanguages', 'organizationForms', 'timezones', 'taxSystems', 'taxSystemModes', 'invoiceUnits', 'invoiceItemNames'] as const) expect(view.result.current[group].map(item => item.id)).toEqual([1]);
  });
  it('dictionary regression: income and expense merge retains inactive entries and the legacy last value for a shared id', async () => {
    vi.mocked(apiService.getIncomeTypes).mockResolvedValueOnce([row(1, 'Income'), row(2, 'Inactive income', false)])
      .mockResolvedValueOnce([{ ...row(1, 'Expense'), paymentType: 'Expense' }, { ...row(3, 'Second expense'), paymentType: 'Expense' }]);
    const view = renderHook(() => useDictionaries({ include: ['incomeTypes'] })); await waitFor(() => expect(view.result.current.loading).toBe(false));
    expect(view.result.current.incomeTypes.map(item => [item.id, item.name, item.isActive, item.paymentType])).toEqual([[1, 'Expense', true, 'Expense'], [2, 'Inactive income', false, 'Income'], [3, 'Second expense', true, 'Expense']]);
    expect(apiService.getDict).not.toHaveBeenCalled(); expect(vi.mocked(apiService.getIncomeTypes).mock.calls.map(call => call.slice(0, 2))).toEqual([['Income', undefined], ['Expense', undefined]]);
  });
  it('dictionary regression: profile groups load exactly seven dictionaries and one independent status list', async () => {
    const view = renderHook(() => ({ dictionaries: useDictionaries({ include: ['countries', 'baseCurrencies', 'serviceLanguages', 'organizationForms', 'timezones', 'taxSystems', 'taxSystemModes'] }), statuses: useClientStatuses() }));
    await waitFor(() => expect(view.result.current.dictionaries.loading || view.result.current.statuses.loading).toBe(false));
    expect(apiService.getDict).toHaveBeenCalledTimes(7); expect(apiService.getIncomeTypes).not.toHaveBeenCalled(); expect(apiService.getClientStatuses).toHaveBeenCalledOnce(); expect(view.result.current.dictionaries.dealTypes).toEqual([]);
  });
  it('dictionary regression: equivalent selections with duplicates and different order do not refetch', async () => {
    const view = renderHook(({ reverse }) => useDictionaries({ include: reverse ? ['services', 'countries', 'countries'] : ['countries', 'services'] }), { initialProps: { reverse: false } });
    await waitFor(() => expect(view.result.current.loading).toBe(false)); view.rerender({ reverse: true }); await act(async () => {}); expect(apiService.getDict).toHaveBeenCalledTimes(2);
  });
  it('dictionary regression: a narrowed group selection hides former groups and rejects its cached refresh', async () => {
    const view = renderHook(({ narrow }) => useDictionaries(narrow ? { include: ['countries'] } : {}), { initialProps: { narrow: false } }); await waitFor(() => expect(view.result.current.loading).toBe(false)); const refresh = view.result.current.refresh;
    const pending = deferred<ReturnType<typeof row>[]>(); vi.mocked(apiService.getDict).mockReturnValueOnce(pending.promise); view.rerender({ narrow: true }); expect(view.result.current.dealTypes).toEqual([]); expect(view.result.current.countries).toEqual([]);
    const count = vi.mocked(apiService.getDict).mock.calls.length; await act(async () => { await refresh(); }); expect(apiService.getDict).toHaveBeenCalledTimes(count);
    await act(async () => pending.resolve([row(3)])); expect(view.result.current.countries[0]?.id).toBe(3); expect(view.result.current.services).toEqual([]);
  });
  it('dictionary regression: an empty group selection is idle and stable through events', async () => {
    const view = renderHook(() => useDictionaries({ include: [] })); const rows = view.result.current.countries; view.rerender(); act(() => window.dispatchEvent(new Event('dicts:changed')));
    expect(view.result.current.countries).toBe(rows); expect(view.result.current.loading).toBe(false); expect(apiService.getDict).not.toHaveBeenCalled(); expect(apiService.getIncomeTypes).not.toHaveBeenCalled();
  });
  it('dictionary regression: a failed batch publishes no partial groups and aborts all remaining reads', async () => {
    vi.mocked(apiService.getDict).mockImplementation(kind => kind === 'deal-types' ? Promise.reject(new Error('HTTP 403 Batch denied')) : new Promise(() => {})); vi.mocked(apiService.getIncomeTypes).mockReturnValue(new Promise(() => {}));
    const view = renderHook(() => useDictionaries()); await waitFor(() => expect(view.result.current.loading).toBe(false)); expect(view.result.current.countries).toEqual([]); expect(view.result.current.error).toContain('403');
    const signals = [...vi.mocked(apiService.getDict).mock.calls.map(call => call[1]?.signal), ...vi.mocked(apiService.getIncomeTypes).mock.calls.map(call => call[2]?.signal)];
    expect(signals).toHaveLength(17); expect(signals.every(signal => signal?.aborted)).toBe(true);
  });
  it('dictionary regression: statuses retain active filtering and case-insensitive name sorting', async () => {
    vi.mocked(apiService.getClientStatuses).mockResolvedValue([row(1, 'Zulu'), row(2, 'alpha'), row(3, 'Hidden', false)]);
    const view = renderHook(() => useClientStatuses()); await waitFor(() => expect(view.result.current.loading).toBe(false)); expect(view.result.current.statuses.map(item => item.name)).toEqual(['alpha', 'Zulu']);
  });
});
