import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useDocumentDeliveryOwner } from './useDocumentDeliveryOwner';
import { useNestedCaseOperationOwner } from './useNestedCaseOperationOwner';
import { documentCopyTestSession } from '../test/documentCopySession';
const actor = vi.hoisted(() => ({ role: 4 }));
vi.mock('../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: 7, role: { id: actor.role } } }) }));
let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { session = documentCopyTestSession(); actor.role = 4; });
afterEach(() => session.restore());
function useOwners({ scope = 'A', enabled = true } = {}) {
  const parent = useDocumentDeliveryOwner(JSON.stringify([scope, actor.role]));
  const child = useNestedCaseOperationOwner('child', parent, enabled);
  return { parent, child };
}
describe('nested case operation ownership', () => {
  it('reserves the parent before a child action and excludes both concurrent entry points', () => {
    const { result } = renderHook(() => useOwners()); let operation!: NonNullable<ReturnType<typeof result.current.child.begin>>;
    act(() => { operation = result.current.child.begin('upload')!; expect(result.current.parent.begin('save')).toBeNull(); expect(result.current.child.begin('upload')).toBeNull(); });
    expect(operation.isCurrent()).toBe(true); expect(result.current.parent.busy).toBe(true); expect(result.current.child.busy).toBe(true);
    act(() => operation.finish()); expect(result.current.parent.busy).toBe(false); expect(result.current.child.busy).toBe(false);
  });
  it('does not reserve a child while the parent is already busy', () => {
    const { result } = renderHook(() => useOwners()); let outer!: NonNullable<ReturnType<typeof result.current.parent.begin>>;
    act(() => { outer = result.current.parent.begin('save')!; expect(result.current.child.begin('upload')).toBeNull(); }); expect(result.current.child.kind).toBeNull(); act(() => outer.finish()); expect(result.current.child.busy).toBe(false);
  });
  it('parent invalidation synchronously prevents old reads and starts even before rerender', () => {
    const { result, rerender } = renderHook(() => useOwners()); const read = result.current.child.capture()!; const start = result.current.child.begin;
    act(() => result.current.parent.invalidate()); expect(read.isCurrent()).toBe(false); expect(start('upload')).toBeNull(); expect(result.current.child.capture()).toBeNull(); rerender(); expect(result.current.child.active).toBe(false);
  });
  it('an old finish cannot release a new case operation', () => {
    const view = renderHook(useOwners, { initialProps: { scope: 'A', enabled: true } }); let old!: NonNullable<ReturnType<typeof view.result.current.child.begin>>, current!: typeof old;
    act(() => { old = view.result.current.child.begin('upload')!; }); view.rerender({ scope: 'B', enabled: true }); act(() => { current = view.result.current.child.begin('upload')!; old.finish(); }); expect(old.isCurrent()).toBe(false); expect(current.isCurrent()).toBe(true); expect(view.result.current.parent.busy).toBe(true); act(() => current.finish());
  });
  it('role replacement invalidates reads, operations and local state scope', () => {
    const view = renderHook(() => useOwners()); const scope = view.result.current.child.scope; const read = view.result.current.child.capture()!; actor.role = 5; view.rerender(); expect(read.isCurrent()).toBe(false); expect(view.result.current.child.scope).not.toBe(scope);
  });
  it.each(['login', 'actor', 'logout'] as const)('%s prevents a write before context render', mode => {
    const { result } = renderHook(() => useOwners()); const start = result.current.child.begin; const read = result.current.child.capture()!; session.replace(mode); expect(start('save')).toBeNull(); expect(read.isCurrent()).toBe(false);
  });
  it('ordinary token refresh preserves scope and an ongoing operation', () => {
    const view = renderHook(() => useOwners()); let operation!: NonNullable<ReturnType<typeof view.result.current.child.begin>>; act(() => { operation = view.result.current.child.begin('save')!; }); const scope = view.result.current.child.scope; session.refresh(); view.rerender(); expect(view.result.current.child.scope).toBe(scope); expect(operation.isCurrent()).toBe(true); act(() => operation.finish());
  });
  it('disabled owner and unmounted owner cannot start or accept work', () => {
    const view = renderHook(useOwners, { initialProps: { scope: 'A', enabled: false } }); expect(view.result.current.child.capture()).toBeNull(); expect(view.result.current.child.begin('save')).toBeNull(); view.rerender({ scope: 'A', enabled: true }); const read = view.result.current.child.capture()!; const begin = view.result.current.child.begin; view.unmount(); expect(read.isCurrent()).toBe(false); expect(begin('save')).toBeNull();
  });
});
