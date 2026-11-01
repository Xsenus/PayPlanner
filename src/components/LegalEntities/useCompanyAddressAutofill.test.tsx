import { useState } from 'react';
import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useCompanyAddressAutofill } from './useCompanyAddressAutofill';
import { apiService } from '../../services/api';
import { documentCopyTestSession } from '../../test/documentCopySession';
import type { CleanedAddress, DadataAddressSuggestion } from '../../types';
import type { ClientAddressEntryFormValue as Entry } from '../Clients/ClientAddressEntriesForm';

vi.mock('../../services/api', () => ({ apiService: { cleanAddress: vi.fn(), suggestClientAddresses: vi.fn() } }));
const entry = (uiKey: string): Entry => ({ uiKey, fullAddress: `Адрес ${uiKey}`, type: 'legal', isPrimary: uiKey === 'a', additionalTypes: [], postalCode: '', country: '', region: '', district: '', city: '', settlement: '', street: '', house: '', flat: '', validFrom: '', validTo: '' });
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }
function setup() {
  const initial = [entry('a'), entry('b')]; const onUseAddress = vi.fn();
  const view = renderHook((props: { scope: string; enabled: boolean; resetKey: unknown }) => {
    const [entries, setEntries] = useState(initial);
    return { ...useCompanyAddressAutofill({ entries, setEntries, onUseAddress, ...props }), entries, setEntries };
  }, { initialProps: { scope: 'first', enabled: true, resetKey: 'initial' } as { scope: string; enabled: boolean; resetKey: unknown } });
  const type = (key: string, query: string) => act(() => {
    view.result.current.setEntries(previous => previous.map(row => row.uiKey === key ? { ...row, fullAddress: query } : row));
    view.result.current.schedule(key, query);
  });
  const tick = () => act(async () => { await vi.advanceTimersByTimeAsync(350); });
  return { ...view, initial, onUseAddress, type, tick };
}

describe('company address owned operations', () => {
  let session: ReturnType<typeof documentCopyTestSession>;
  beforeEach(() => { vi.resetAllMocks(); vi.useFakeTimers(); session = documentCopyTestSession(); vi.mocked(apiService.cleanAddress).mockResolvedValue({ result: 'Уточнённый адрес', cityWithType: 'Новый город' }); vi.mocked(apiService.suggestClientAddresses).mockResolvedValue([{ value: 'Выбранный адрес' }]); });
  afterEach(() => { vi.clearAllTimers(); vi.useRealTimers(); session.restore(); });

  it('normalizes one row, keeps its key/metadata and leaves its peer untouched', async () => {
    const view = setup(); await act(async () => view.result.current.clean('a'));
    expect(view.result.current.entries[0]).toMatchObject({ uiKey: 'a', type: 'legal', isPrimary: true, city: 'Новый город' });
    expect(view.result.current.entries[1]).toBe(view.initial[1]); expect(view.onUseAddress).toHaveBeenCalledOnce();
    expect(apiService.cleanAddress).toHaveBeenCalledWith('Адрес a', expect.any(AbortSignal));
  });
  it('coalesces repeated pending clean clicks and permits an explicit retry after failure', async () => {
    const pending = deferred<CleanedAddress>(); vi.mocked(apiService.cleanAddress).mockReturnValueOnce(pending.promise);
    const view = setup(); act(() => { void view.result.current.clean('a'); void view.result.current.clean('a'); }); expect(apiService.cleanAddress).toHaveBeenCalledOnce();
    await act(async () => pending.reject(new Error('offline'))); expect(view.result.current.errorsByIndex[0]).toContain('Введённые данные сохранены');
    await act(async () => view.result.current.clean('a')); expect(apiService.cleanAddress).toHaveBeenCalledTimes(2); expect(view.result.current.errorsByIndex).toEqual({});
  });
  it.each(['success', 'error'] as const)('a newer address owns normalization after old %s', async outcome => {
    const old = deferred<CleanedAddress>(); vi.mocked(apiService.cleanAddress).mockReturnValueOnce(old.promise);
    const view = setup(); act(() => { void view.result.current.clean('a'); }); const signal = vi.mocked(apiService.cleanAddress).mock.calls[0][1]!;
    act(() => { view.result.current.invalidate('a'); view.result.current.setEntries(previous => previous.map(row => row.uiKey === 'a' ? { ...row, fullAddress: 'Новый ввод' } : row)); });
    await act(async () => view.result.current.clean('a')); expect(signal.aborted).toBe(true);
    await act(async () => { if (outcome === 'success') old.resolve({ result: 'Старый адрес', cityWithType: 'Старый город' }); else old.reject(new Error('old failure')); });
    expect(view.result.current.entries[0].city).toBe('Новый город'); expect(view.result.current.errorsByIndex).toEqual({});
  });
  it('follows a surviving row through reorder and preserves other rows', async () => {
    const pending = deferred<CleanedAddress>(); vi.mocked(apiService.cleanAddress).mockReturnValueOnce(pending.promise); const view = setup();
    act(() => { void view.result.current.clean('b'); }); act(() => view.result.current.setEntries(previous => [previous[1], previous[0]]));
    await act(async () => pending.resolve({ result: 'Адрес b', cityWithType: 'Город b' }));
    expect(view.result.current.entries[0]).toMatchObject({ uiKey: 'b', city: 'Город b' }); expect(view.result.current.entries[1]).toBe(view.initial[0]);
  });
  it('preserves metadata changes and an appended row while cleaning', async () => {
    const pending = deferred<CleanedAddress>(); vi.mocked(apiService.cleanAddress).mockReturnValueOnce(pending.promise); const view = setup();
    act(() => { void view.result.current.clean('a'); }); act(() => view.result.current.setEntries(previous => [{ ...previous[0], type: 'actual', isPrimary: false }, previous[1], entry('c')]));
    await act(async () => pending.resolve({ result: 'Адрес a', cityWithType: 'Новый город' }));
    expect(view.result.current.entries[0]).toMatchObject({ type: 'actual', isPrimary: false, city: 'Новый город' }); expect(view.result.current.entries[2].uiKey).toBe('c');
  });
  it.each(['delete', 'edit', 'aba'] as const)('a pending clean cannot cross row %s', async change => {
    const pending = deferred<CleanedAddress>(); vi.mocked(apiService.cleanAddress).mockReturnValueOnce(pending.promise); const view = setup();
    act(() => { void view.result.current.clean('a'); }); const signal = vi.mocked(apiService.cleanAddress).mock.calls[0][1]!;
    act(() => {
      if (change === 'aba') view.result.current.invalidate('a');
      view.result.current.setEntries(previous => change === 'delete' ? [previous[1]] : [{ ...previous[0], postalCode: change === 'edit' ? '630000' : '' }, previous[1]]);
    });
    await act(async () => pending.resolve({ result: 'Старый адрес', cityWithType: 'Старый город' }));
    expect(signal.aborted).toBe(true); expect(view.result.current.entries.every(row => row.city !== 'Старый город')).toBe(true);
  });
  it.each(['scope', 'readonly', 'reset', 'logout', 'login', 'actor'] as const)('pending normalization cannot cross %s', async change => {
    const pending = deferred<CleanedAddress>(); vi.mocked(apiService.cleanAddress).mockReturnValueOnce(pending.promise); const view = setup();
    act(() => { void view.result.current.clean('a'); });
    if (change === 'scope') view.rerender({ scope: 'next', enabled: true, resetKey: 'initial' });
    else if (change === 'readonly') view.rerender({ scope: 'first', enabled: false, resetKey: 'initial' });
    else if (change === 'reset') view.rerender({ scope: 'first', enabled: true, resetKey: {} });
    else session.replace(change);
    await act(async () => pending.resolve({ result: 'Устаревший адрес', cityWithType: 'Устаревший город' }));
    expect(view.result.current.entries[0]).toEqual(view.initial[0]); expect(view.result.current.errorsByIndex).toEqual({});
  });
  it('accepts a same-session token refresh', async () => {
    const pending = deferred<CleanedAddress>(); vi.mocked(apiService.cleanAddress).mockReturnValueOnce(pending.promise); const view = setup();
    act(() => { void view.result.current.clean('a'); }); session.refresh(); view.rerender({ scope: 'first', enabled: true, resetKey: 'initial' });
    await act(async () => pending.resolve({ result: 'Адрес a', cityWithType: 'Новый город' })); expect(view.result.current.entries[0].city).toBe('Новый город');
  });
  it('never starts reads for readonly, unknown or empty rows', async () => {
    const view = setup(); view.rerender({ scope: 'first', enabled: false, resetKey: 'initial' });
    await act(async () => { await view.result.current.clean('a'); await view.result.current.clean('absent'); view.result.current.schedule('a', 'Запрос'); });
    await view.tick(); expect(apiService.cleanAddress).not.toHaveBeenCalled(); expect(apiService.suggestClientAddresses).not.toHaveBeenCalled();
  });
  it('debounces each row independently and cancels only the edited row', async () => {
    const view = setup(); view.type('a', 'Первая строка'); view.type('b', 'Вторая строка'); act(() => view.result.current.invalidate('a')); await view.tick();
    expect(apiService.suggestClientAddresses).toHaveBeenCalledExactlyOnceWith('Вторая строка', 7, expect.any(AbortSignal)); expect(view.result.current.suggestionsByIndex[1]).toHaveLength(1);
  });
  it.each(['success', 'error'] as const)('old suggestion %s cannot replace a newer query', async outcome => {
    const old = deferred<DadataAddressSuggestion[]>(); vi.mocked(apiService.suggestClientAddresses).mockReturnValueOnce(old.promise);
    const view = setup(); view.type('a', 'Первый запрос'); await view.tick(); const signal = vi.mocked(apiService.suggestClientAddresses).mock.calls[0][2]!;
    view.type('a', 'Второй запрос'); await view.tick();
    await act(async () => { if (outcome === 'success') old.resolve([{ value: 'Старый результат' }]); else old.reject(new Error('old failure')); });
    expect(signal.aborted).toBe(true); expect(view.result.current.suggestionsByIndex[0]).toEqual([{ value: 'Выбранный адрес' }]); expect(view.result.current.errorsByIndex).toEqual({});
  });
  it('cancels a removed timer and does not search short text', async () => {
    const view = setup(); view.type('a', 'Удаляемая строка'); act(() => view.result.current.setEntries(previous => [previous[1]])); view.type('b', 'ab'); await view.tick();
    expect(apiService.suggestClientAddresses).not.toHaveBeenCalled(); expect(view.result.current.suggestionsByIndex).toEqual({});
  });
  it('applies a current suggestion, keeps fallback on failure and rejects a cached old item', async () => {
    const view = setup(); view.type('a', 'Запрос адреса'); await view.tick(); const suggestion = view.result.current.suggestionsByIndex[0][0];
    vi.mocked(apiService.cleanAddress).mockRejectedValueOnce(new Error('offline'));
    await act(async () => view.result.current.apply('a', suggestion));
    expect(view.result.current.entries[0].fullAddress).toBe('Выбранный адрес'); expect(view.result.current.errorsByIndex[0]).toContain('Введённые данные сохранены');
    await act(async () => view.result.current.apply('a', suggestion)); expect(apiService.cleanAddress).toHaveBeenCalledOnce();
  });
  it('normalizes an immediately resolved chosen suggestion without waiting for a React render', async () => {
    const view = setup(); view.type('a', 'Запрос адреса'); await view.tick(); const suggestion = view.result.current.suggestionsByIndex[0][0];
    await act(async () => view.result.current.apply('a', suggestion)); expect(view.result.current.entries[0].city).toBe('Новый город'); expect(view.result.current.entries[0].uiKey).toBe('a');
  });
  it('does not apply cached suggestions after session replacement before rerender', async () => {
    const view = setup(); view.type('a', 'Запрос адреса'); await view.tick(); const suggestion = view.result.current.suggestionsByIndex[0][0]; session.replace();
    await act(async () => view.result.current.apply('a', suggestion)); expect(apiService.cleanAddress).not.toHaveBeenCalled();
  });
  it('shows an owned suggestion error without changing the address', async () => {
    vi.mocked(apiService.suggestClientAddresses).mockRejectedValueOnce(new Error('offline')); const view = setup(); view.type('a', 'Запрос адреса'); await view.tick();
    expect(view.result.current.entries[0].fullAddress).toBe('Запрос адреса'); expect(view.result.current.errorsByIndex[0]).toContain('Введённые данные сохранены');
  });
});
