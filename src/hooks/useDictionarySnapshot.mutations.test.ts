import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useDictionarySnapshot } from './useDictionarySnapshot';
import { documentCopyTestSession } from '../test/documentCopySession';

const empty: number[] = [];
afterEach(() => { localStorage.clear(); });
describe('current snapshot mutation', () => {
  it('cancels a prior read and ignores its late response after a confirmed local mutation', async () => {
    documentCopyTestSession();
    let finish!: (value: number[]) => void;
    const load = vi.fn((signal: AbortSignal) => new Promise<number[]>(resolve => { expect(signal.aborted).toBe(false); finish = resolve; }));
    const view = renderHook(() => useDictionarySnapshot(load, empty, { ownerScope: 'client1' }, false));
    const signal = load.mock.calls[0][0];
    act(() => view.result.current.updateValue(previous => [...previous, 77]));
    expect(signal.aborted).toBe(true);
    expect(view.result.current.value).toEqual([77]);
    await act(async () => finish([66]));
    expect(view.result.current.value).toEqual([77]);
    expect(view.result.current.loading).toBe(false);
  });
  it('applies functional changes to the current snapshot without a duplicate read', async () => {
    documentCopyTestSession();
    const load = vi.fn(async () => [1, 2]);
    const view = renderHook(() => useDictionarySnapshot(load, empty, undefined, false));
    await waitFor(() => expect(view.result.current.loading).toBe(false));
    act(() => { view.result.current.updateValue(previous => [...previous, 3]); view.result.current.updateValue(previous => previous.filter(x => x !== 1)); });
    expect(view.result.current.value).toEqual([2, 3]);
    expect(load).toHaveBeenCalledOnce();
  });
  it('does not invoke an old updater after changing owner or disabling the caller', async () => {
    documentCopyTestSession();
    const load = vi.fn(async () => [1]); const forbiddenUpdate = vi.fn(() => [99]);
    const view = renderHook(({ owner, enabled }) => useDictionarySnapshot(load, empty, { ownerScope: owner, enabled }, false), { initialProps: { owner: 'first', enabled: true } });
    await waitFor(() => expect(view.result.current.loading).toBe(false)); const oldUpdate = view.result.current.updateValue;
    view.rerender({ owner: 'second', enabled: true }); await waitFor(() => expect(view.result.current.loading).toBe(false));
    act(() => oldUpdate(forbiddenUpdate)); expect(forbiddenUpdate).not.toHaveBeenCalled(); expect(view.result.current.value).toEqual([1]);
    view.rerender({ owner: 'second', enabled: false }); act(() => view.result.current.updateValue(forbiddenUpdate));
    expect(forbiddenUpdate).not.toHaveBeenCalled(); expect(view.result.current.value).toEqual([]);
  });
  it('rejects an updater after authentication changes before a parent render', async () => {
    const session = documentCopyTestSession(); const load = vi.fn(async () => [1]); const forbiddenUpdate = vi.fn(() => [99]);
    const view = renderHook(() => useDictionarySnapshot(load, empty, undefined, false));
    await waitFor(() => expect(view.result.current.loading).toBe(false));
    session.replace('actor'); act(() => view.result.current.updateValue(forbiddenUpdate));
    expect(forbiddenUpdate).not.toHaveBeenCalled();
  });
});
