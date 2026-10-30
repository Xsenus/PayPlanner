import { act, renderHook } from '@testing-library/react';
import { createElement, StrictMode } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { useDocumentDeliveryOwner } from './useDocumentDeliveryOwner';

describe('document delivery owner', () => {
  afterEach(() => { localStorage.removeItem('pp_token'); localStorage.removeItem('pp.auth-session-epoch.v1'); });
  it('admits one send and keeps reads independent across unrelated renders', () => {
    const { result, rerender } = renderHook(() => useDocumentDeliveryOwner('invoice7'));
    let operation!: NonNullable<ReturnType<typeof result.current.begin>>;
    act(() => { operation = result.current.begin()!; });
    expect(result.current.busy).toBe(true); expect(result.current.begin()).toBeNull();
    expect(result.current.capture()?.isCurrent()).toBe(true); rerender(); expect(operation.isCurrent()).toBe(true);
    act(() => operation.finish()); expect(result.current.busy).toBe(false);
  });
  it('rejects a replaced scope even when the same card is reopened, including held start callbacks', () => {
    const { result, rerender } = renderHook(({ scope }) => useDocumentDeliveryOwner(scope), { initialProps: { scope: 'invoice7' } });
    const first = result.current;
    let old!: NonNullable<ReturnType<typeof first.begin>>; act(() => { old = first.begin()!; });
    rerender({ scope: 'invoice8' }); rerender({ scope: 'invoice7' });
    expect(old.isCurrent()).toBe(false); expect(first.begin()).toBeNull(); expect(result.current.busy).toBe(false);
    let next!: NonNullable<ReturnType<typeof first.begin>>; act(() => { next = result.current.begin()!; });
    act(() => old.finish()); expect(result.current.busy).toBe(true); expect(next.isCurrent()).toBe(true);
  });
  it('invalidates before close commits and after unmount', () => {
    const { result, unmount } = renderHook(() => useDocumentDeliveryOwner('act7'));
    const owner = result.current; const read = owner.capture()!;
    act(() => owner.invalidate()); expect(read.isCurrent()).toBe(false); expect(owner.begin()).toBeNull();
    const mounted = result.current; expect(mounted.capture()).toBeNull(); expect(mounted.begin()).toBeNull();
    unmount(); expect(mounted.capture()).toBeNull(); expect(mounted.begin()).toBeNull();
  });
  it('can send under StrictMode and rejects operations after its real unmount', () => {
    const { result, unmount } = renderHook(() => useDocumentDeliveryOwner('act7'), { wrapper: ({ children }) => createElement(StrictMode, null, children) });
    let operation!: NonNullable<ReturnType<typeof result.current.begin>>;
    act(() => { operation = result.current.begin('disk')!; });
    expect(operation).not.toBeNull(); expect(operation.isCurrent()).toBe(true); expect(result.current.kind).toBe('disk');
    unmount(); expect(operation.isCurrent()).toBe(false);
  });
  it('rejects a login replaced before React rerenders', () => {
    localStorage.setItem('pp_token', `synthetic.${btoa(JSON.stringify({ sub: '7' }))}.signature`);
    localStorage.setItem('pp.auth-session-epoch.v1', 'first');
    const { result } = renderHook(() => useDocumentDeliveryOwner('contract7'));
    const owner = result.current; let operation!: NonNullable<ReturnType<typeof owner.begin>>;
    act(() => { operation = owner.begin()!; });
    localStorage.setItem('pp.auth-session-epoch.v1', 'second');
    expect(operation.isCurrent()).toBe(false); expect(owner.capture()).toBeNull(); expect(owner.begin()).toBeNull();
  });
});
