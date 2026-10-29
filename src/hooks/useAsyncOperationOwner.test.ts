import { act, renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { useAsyncOperationOwner } from './useAsyncOperationOwner';

describe('async operation owner', () => {
  it('keeps a normal result current across unrelated renders', () => {
    const { result, rerender } = renderHook(({ scope }) => useAsyncOperationOwner(scope), { initialProps: { scope: 'company-7' } });
    const token = result.current.capture(); rerender({ scope: 'company-7' });
    expect(result.current.isCurrent(token)).toBe(true);
  });
  it('rejects an old result even when the same company is reopened', () => {
    const { result, rerender } = renderHook(({ scope }) => useAsyncOperationOwner(scope), { initialProps: { scope: 'company-7' } });
    const token = result.current.capture(); rerender({ scope: 'company-8' }); rerender({ scope: 'company-7' });
    expect(result.current.isCurrent(token)).toBe(false);
    expect(result.current.isCurrent(result.current.capture())).toBe(true);
  });
  it('invalidates synchronously on navigation before React commits another render', () => {
    const { result } = renderHook(() => useAsyncOperationOwner('company-7'));
    const token = result.current.capture(); act(() => result.current.invalidate());
    expect(result.current.isCurrent(token)).toBe(false);
  });
  it('rejects callbacks held by an unmounted screen', () => {
    const { result, unmount } = renderHook(() => useAsyncOperationOwner('company-7'));
    const owner = result.current; const token = owner.capture(); unmount();
    expect(owner.isCurrent(token)).toBe(false);
  });
});
