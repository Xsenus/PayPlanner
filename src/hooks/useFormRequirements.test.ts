import { act, renderHook, waitFor } from '@testing-library/react';
import { createElement, StrictMode, type PropsWithChildren } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService, type FormFieldRequirementDto } from '../services/api';
import { useFormRequirements } from './useFormRequirements';
import { documentCopyTestSession } from '../test/documentCopySession';

vi.mock('../services/api', () => ({ apiService: { getFormRequirements: vi.fn() } }));
const rule = (section = 'legal-entities', fieldKey = 'Name', isRequired = true): FormFieldRequirementDto => ({ section, fieldKey, isRequired, updatedAt: '2026-11-01T00:00:00Z' });
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }

describe('owned form requirements', () => {
  let session: ReturnType<typeof documentCopyTestSession>;
  beforeEach(() => { vi.resetAllMocks(); session = documentCopyTestSession(); vi.mocked(apiService.getFormRequirements).mockResolvedValue([rule()]); });
  afterEach(() => session.restore());
  it('requirements baseline: disabling hides the old required keys immediately', async () => {
    const view = renderHook(({ enabled }) => useFormRequirements('legal-entities', enabled), { initialProps: { enabled: true } }); await waitFor(() => expect(view.result.current.isRequired('Name')).toBe(true));
    view.rerender({ enabled: false }); expect(view.result.current.requiredKeys.size).toBe(0); expect(view.result.current.isRequired('Name')).toBe(false);
  });
  it('requirements baseline: another section does not inherit the old flags while pending', async () => {
    const view = renderHook(({ section }) => useFormRequirements(section), { initialProps: { section: 'legal-entities' } }); await waitFor(() => expect(view.result.current.isRequired('Name')).toBe(true));
    vi.mocked(apiService.getFormRequirements).mockReturnValue(new Promise(() => {})); view.rerender({ section: 'payments' }); expect(view.result.current.isRequired('Name')).toBe(false);
  });
  it('requirements baseline: a new login hides previously loaded rules', async () => {
    const view = renderHook(() => useFormRequirements('legal-entities')); await waitFor(() => expect(view.result.current.isRequired('Name')).toBe(true));
    vi.mocked(apiService.getFormRequirements).mockReturnValue(new Promise(() => {})); session.replace(); view.rerender(); expect(view.result.current.requiredKeys.size).toBe(0);
  });
  it('requirements baseline: a changed caller scope cannot use an earlier role rule set', async () => {
    const view = renderHook(({ ownerScope }) => useFormRequirements('legal-entities', true, { ownerScope }), { initialProps: { ownerScope: 'all' } }); await waitFor(() => expect(view.result.current.isRequired('Name')).toBe(true));
    vi.mocked(apiService.getFormRequirements).mockReturnValue(new Promise(() => {})); view.rerender({ ownerScope: 'own' }); expect(view.result.current.requiredKeys.size).toBe(0);
  });
  it('requirements baseline: an old predicate cannot validate another caller scope', async () => {
    const view = renderHook(({ ownerScope }) => useFormRequirements('legal-entities', true, { ownerScope }), { initialProps: { ownerScope: 'all' } }); await waitFor(() => expect(view.result.current.isRequired('Name')).toBe(true));
    const old = view.result.current.isRequired; view.rerender({ ownerScope: 'own' }); expect(old('Name')).toBe(false);
  });
  it('requirements baseline: reopening does not resurrect a prior lifetime cache', async () => {
    const view = renderHook(({ enabled }) => useFormRequirements('legal-entities', enabled), { initialProps: { enabled: true } }); await waitFor(() => expect(view.result.current.isRequired('Name')).toBe(true));
    view.rerender({ enabled: false }); vi.mocked(apiService.getFormRequirements).mockReturnValue(new Promise(() => {})); view.rerender({ enabled: true }); expect(view.result.current.isRequired('Name')).toBe(false);
  });
  it('requirements baseline: a blank section does not request global rules', async () => {
    renderHook(() => useFormRequirements('')); await act(async () => {}); expect(apiService.getFormRequirements).not.toHaveBeenCalled();
  });
  it('requirements baseline: an unmounted form cannot keep validating through its predicate', async () => {
    const view = renderHook(() => useFormRequirements('legal-entities')); await waitFor(() => expect(view.result.current.isRequired('Name')).toBe(true)); const old = view.result.current.isRequired;
    view.unmount(); expect(old('Name')).toBe(false);
  });
  it('requirements regression: active flags and case-sensitive field names retain their existing meaning', async () => {
    vi.mocked(apiService.getFormRequirements).mockResolvedValue([rule(), rule('legal-entities', 'Phone', false)]); const view = renderHook(() => useFormRequirements('legal-entities'));
    await waitFor(() => expect(view.result.current.loading).toBe(false)); expect(Array.from(view.result.current.requiredKeys)).toEqual(['Name']); expect(view.result.current.isRequired('name')).toBe(false); expect(view.result.current.isRequired('Phone')).toBe(false);
  });
  it('requirements regression: an empty valid response has no mandatory fields', async () => {
    vi.mocked(apiService.getFormRequirements).mockResolvedValue([]); const view = renderHook(() => useFormRequirements('legal-entities')); await waitFor(() => expect(view.result.current.loading).toBe(false)); expect(view.result.current.requiredKeys.size).toBe(0);
  });
  it('requirements regression: equivalent trimmed section and inline scope do not refetch', async () => {
    const view = renderHook(({ section }) => useFormRequirements(section, true, { ownerScope: 'same' }), { initialProps: { section: ' legal-entities ' } }); await waitFor(() => expect(view.result.current.isRequired('Name')).toBe(true));
    view.rerender({ section: 'legal-entities' }); session.refresh(); view.rerender({ section: 'legal-entities' }); await act(async () => {}); expect(apiService.getFormRequirements).toHaveBeenCalledOnce(); expect(vi.mocked(apiService.getFormRequirements).mock.calls[0][0]).toBe('legal-entities');
  });
  it('requirements regression: an old predicate uses refreshed rules only within its own live form', async () => {
    const view = renderHook(() => useFormRequirements('legal-entities')); await waitFor(() => expect(view.result.current.isRequired('Name')).toBe(true)); const old = view.result.current.isRequired;
    vi.mocked(apiService.getFormRequirements).mockResolvedValue([rule('legal-entities', 'Phone')]); await act(async () => { await view.result.current.refresh(); });
    expect(old('Name')).toBe(false); expect(old('Phone')).toBe(true); expect(view.result.current.isRequired('Phone')).toBe(true);
  });
  it('requirements regression: changing section aborts and ignores a late earlier rule set', async () => {
    const old = deferred<FormFieldRequirementDto[]>(); vi.mocked(apiService.getFormRequirements).mockImplementation(section => section === 'legal-entities' ? old.promise : Promise.resolve([rule('clients', 'Phone')]));
    const view = renderHook(({ section }) => useFormRequirements(section), { initialProps: { section: 'legal-entities' } }); const signal = vi.mocked(apiService.getFormRequirements).mock.calls[0][1]?.signal;
    view.rerender({ section: 'clients' }); await waitFor(() => expect(view.result.current.isRequired('Phone')).toBe(true)); expect(signal?.aborted).toBe(true);
    await act(async () => old.resolve([rule()])); expect(view.result.current.isRequired('Name')).toBe(false); expect(view.result.current.isRequired('Phone')).toBe(true);
  });
  it('requirements regression: an old failure cannot clear a newer scope or finish its loading', async () => {
    const old = deferred<FormFieldRequirementDto[]>(); const current = deferred<FormFieldRequirementDto[]>(); vi.mocked(apiService.getFormRequirements).mockReturnValueOnce(old.promise).mockReturnValueOnce(current.promise);
    const view = renderHook(({ ownerScope }) => useFormRequirements('legal-entities', true, { ownerScope }), { initialProps: { ownerScope: 'all' } }); view.rerender({ ownerScope: 'own' });
    await act(async () => old.reject(new Error('HTTP 403 Old owner'))); expect(view.result.current.loading).toBe(true);
    await act(async () => current.resolve([rule('legal-entities', 'Phone')])); expect(view.result.current.loading).toBe(false); expect(view.result.current.isRequired('Phone')).toBe(true);
  });
  it.each(['Temporary network', 'HTTP 403 Forbidden'])('requirements regression: current %s keeps the established empty fallback and permits a later read', async message => {
    const view = renderHook(() => useFormRequirements('legal-entities')); await waitFor(() => expect(view.result.current.isRequired('Name')).toBe(true)); const old = view.result.current.isRequired;
    vi.mocked(apiService.getFormRequirements).mockRejectedValueOnce(new Error(message)); await act(async () => { await view.result.current.refresh(); }); expect(view.result.current.requiredKeys.size).toBe(0); expect(old('Name')).toBe(false);
    await act(async () => { await view.result.current.refresh(); }); expect(view.result.current.isRequired('Name')).toBe(true);
  });
  it.each(['section', 'duplicate', 'empty-key', 'non-boolean'] as const)('requirements regression: malformed %s data cannot create flags', async shape => {
    const rows = shape === 'section' ? [rule('payments')] : shape === 'duplicate' ? [rule(), rule('legal-entities', 'name')]
      : shape === 'empty-key' ? [rule('legal-entities', ' ')] : [{ ...rule(), isRequired: 'false' }];
    vi.mocked(apiService.getFormRequirements).mockResolvedValue(rows as FormFieldRequirementDto[]); const view = renderHook(() => useFormRequirements('legal-entities'));
    await waitFor(() => expect(view.result.current.loading).toBe(false)); expect(view.result.current.requiredKeys.size).toBe(0);
  });
  it('requirements regression: dictionary change events do not reload unrelated form settings', async () => {
    const view = renderHook(() => useFormRequirements('legal-entities')); await waitFor(() => expect(view.result.current.isRequired('Name')).toBe(true)); act(() => window.dispatchEvent(new Event('dicts:changed')));
    await act(async () => {}); expect(apiService.getFormRequirements).toHaveBeenCalledOnce(); expect(view.result.current.isRequired('Name')).toBe(true);
  });
  it('requirements regression: cached refresh and predicate reject login replacement before React renders', async () => {
    const view = renderHook(() => useFormRequirements('legal-entities')); await waitFor(() => expect(view.result.current.isRequired('Name')).toBe(true)); const old = view.result.current;
    session.replace('actor'); expect(old.isRequired('Name')).toBe(false); await act(async () => { await old.refresh(); }); expect(apiService.getFormRequirements).toHaveBeenCalledOnce();
  });
  it('requirements regression: unmount aborts its current HTTP read and cached refresh', async () => {
    vi.mocked(apiService.getFormRequirements).mockReturnValue(new Promise(() => {})); const view = renderHook(() => useFormRequirements('legal-entities')); const refresh = view.result.current.refresh;
    const signal = vi.mocked(apiService.getFormRequirements).mock.calls[0][1]?.signal; view.unmount(); expect(signal?.aborted).toBe(true); await refresh(); expect(apiService.getFormRequirements).toHaveBeenCalledOnce();
  });
  it('requirements regression: the latest refresh owns its flags and loading state', async () => {
    const first = deferred<FormFieldRequirementDto[]>(); const last = deferred<FormFieldRequirementDto[]>(); vi.mocked(apiService.getFormRequirements).mockReturnValueOnce(first.promise).mockReturnValueOnce(last.promise);
    const view = renderHook(() => useFormRequirements('legal-entities')); act(() => { void view.result.current.refresh(); }); await act(async () => first.resolve([rule()])); expect(view.result.current.loading).toBe(true); expect(view.result.current.isRequired('Name')).toBe(false);
    await act(async () => last.resolve([rule('legal-entities', 'Phone')])); expect(view.result.current.isRequired('Phone')).toBe(true); expect(view.result.current.loading).toBe(false);
  });
  it('requirements regression: StrictMode replay obtains a live predicate', async () => {
    const view = renderHook(() => useFormRequirements('legal-entities'), { wrapper: ({ children }: PropsWithChildren) => createElement(StrictMode, null, children) }); await waitFor(() => expect(view.result.current.isRequired('Name')).toBe(true)); expect(view.result.current.loading).toBe(false);
  });
  it('requirements regression: separate form instances do not share their empty mutable sets', () => {
    const view = renderHook(() => ({ a: useFormRequirements('legal-entities', false), b: useFormRequirements('clients', false) })); expect(view.result.current.a.requiredKeys).not.toBe(view.result.current.b.requiredKeys); expect(apiService.getFormRequirements).not.toHaveBeenCalled();
  });
});
