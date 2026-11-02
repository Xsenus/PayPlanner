import { act, render, renderHook, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../services/api';
import { useUserTimeZone } from './useUserTimeZone';
import { documentCopyTestSession } from '../test/documentCopySession';
let session: ReturnType<typeof documentCopyTestSession>;
let sessionSequence = 0;
beforeEach(() => { session = documentCopyTestSession(); localStorage.setItem('pp.auth-session-epoch.v1', `timezone-test-${++sessionSequence}`); });
afterEach(() => { expect(apiService.upsertUserSetting).not.toHaveBeenCalled(); session.restore(); });

vi.mock('../services/api', () => ({
  apiService: {
    getUserSetting: vi.fn(),
    upsertUserSetting: vi.fn(),
    initializeUserSetting: vi.fn(),
  },
}));

function TimeZoneProbe() {
  const timeZoneId = useUserTimeZone();
  return <div data-testid="time-zone">{timeZoneId}</div>;
}

describe('useUserTimeZone', () => {
  it('baseline: an unmounted missing-setting read cannot autosave for the next login', async () => {
    let reject!: (error: Error) => void; vi.mocked(apiService.getUserSetting).mockReturnValueOnce(new Promise((_resolve, no) => { reject = no; }));
    const view = render(<TimeZoneProbe />); await waitFor(() => expect(apiService.getUserSetting).toHaveBeenCalledOnce());
    view.unmount(); session.replace(); await act(async () => reject(new Error('HTTP 404 Not Found')));
    expect(apiService.initializeUserSetting).not.toHaveBeenCalled();
  });

  beforeEach(() => {
    vi.resetAllMocks();
  });

  it('loads the user profile time zone setting', async () => {
    vi.mocked(apiService.getUserSetting).mockResolvedValue({ key: 'profile.timeZoneId', value: 'Europe/Samara' });

    render(<TimeZoneProbe />);

    await waitFor(() => {
      expect(screen.getByTestId('time-zone').textContent).toBe('Europe/Samara');
    });
    expect(apiService.initializeUserSetting).not.toHaveBeenCalled();
  });

  it('saves the detected time zone when the profile setting is missing', async () => {
    vi.mocked(apiService.getUserSetting).mockRejectedValue(new Error('HTTP 404 Not Found'));
    vi.mocked(apiService.initializeUserSetting).mockResolvedValue({
      key: 'profile.timeZoneId',
      value: 'Europe/Samara',
      updatedAt: '2026-06-05T10:00:00Z',
    });

    render(<TimeZoneProbe />);

    await waitFor(() => {
      expect(apiService.getUserSetting).toHaveBeenCalledWith('profile.timeZoneId', { signal: expect.any(AbortSignal) });
    });
    await waitFor(() => {
      expect(apiService.initializeUserSetting).toHaveBeenCalledWith(
        'profile.timeZoneId',
        screen.getByTestId('time-zone').textContent,
        { signal: expect.any(AbortSignal) },
      );
    });
    expect(screen.getByTestId('time-zone').textContent).toBeTruthy();
  });

  it('keeps a normalized fallback without saving when the setting cannot be loaded for another reason', async () => {
    vi.mocked(apiService.getUserSetting).mockRejectedValue(new Error('HTTP 500 Server Error'));

    render(<TimeZoneProbe />);

    await waitFor(() => {
      expect(apiService.getUserSetting).toHaveBeenCalledWith('profile.timeZoneId', { signal: expect.any(AbortSignal) });
    });
    expect(apiService.initializeUserSetting).not.toHaveBeenCalled();
    expect(screen.getByTestId('time-zone').textContent).toBeTruthy();
  });
  it.each(['login', 'actor', 'logout', 'unmount'].flatMap(change => ['success', 'missing', 'error'].map(outcome => ({ change, outcome }))))(
    'refuses late $outcome after $change without saving', async ({ change, outcome }) => {
      let finish!: (value: { key: string; value: string }) => void; let fail!: (error: Error) => void;
      vi.mocked(apiService.getUserSetting).mockReturnValueOnce(new Promise((yes, no) => { finish = yes; fail = no; }));
      const view = renderHook(() => useUserTimeZone()); await waitFor(() => expect(apiService.getUserSetting).toHaveBeenCalledOnce());
      if (change === 'unmount') view.unmount(); else session.replace(change as 'login' | 'actor' | 'logout');
      await act(async () => { if (outcome === 'success') finish({ key: 'profile.timeZoneId', value: 'Europe/Kaliningrad' });
        else fail(new Error(outcome === 'missing' ? 'HTTP 404 Not Found' : 'HTTP 500 Server Error')); });
      expect(apiService.initializeUserSetting).not.toHaveBeenCalled();
      if (change !== 'unmount') expect(view.result.current).not.toBe('Europe/Kaliningrad');
    });
  it('masks a settled previous timezone synchronously and reads the new login without adopting its old value', async () => {
    vi.mocked(apiService.getUserSetting).mockResolvedValueOnce({ key: 'profile.timeZoneId', value: 'Europe/Samara' });
    const view = renderHook(() => useUserTimeZone()); await waitFor(() => expect(view.result.current).toBe('Europe/Samara'));
    vi.mocked(apiService.getUserSetting).mockReturnValueOnce(new Promise(() => {})); session.replace(); view.rerender();
    expect(view.result.current).not.toBe('Europe/Samara'); expect(apiService.getUserSetting).toHaveBeenCalledTimes(2);
  });
  it('initializes a missing nullable setting once across concurrent consumers and later mounts', async () => {
    vi.mocked(apiService.getUserSetting).mockResolvedValue(null); vi.mocked(apiService.initializeUserSetting).mockResolvedValue({ key: 'profile.timeZoneId', value: 'UTC' });
    const first = renderHook(() => useUserTimeZone()); const second = renderHook(() => useUserTimeZone());
    await waitFor(() => expect(apiService.initializeUserSetting).toHaveBeenCalledOnce()); first.unmount(); second.unmount(); renderHook(() => useUserTimeZone());
    await waitFor(() => expect(apiService.getUserSetting).toHaveBeenCalledTimes(3)); expect(apiService.initializeUserSetting).toHaveBeenCalledOnce();
  });
  it('keeps a pending old initialization independent of a new login and never lets its completion release the new one', async () => {
    vi.mocked(apiService.getUserSetting).mockResolvedValue(null); let oldFinish!: (value: { key: string; value: string }) => void;
    let newFinish!: (value: { key: string; value: string }) => void;
    vi.mocked(apiService.initializeUserSetting).mockReturnValueOnce(new Promise(resolve => { oldFinish = resolve; })).mockReturnValueOnce(new Promise(resolve => { newFinish = resolve; }));
    const view = renderHook(() => useUserTimeZone()); await waitFor(() => expect(apiService.initializeUserSetting).toHaveBeenCalledTimes(1));
    session.replace(); view.rerender(); await waitFor(() => expect(apiService.initializeUserSetting).toHaveBeenCalledTimes(2));
    await act(async () => oldFinish({ key: 'profile.timeZoneId', value: 'UTC' })); renderHook(() => useUserTimeZone());
    await waitFor(() => expect(apiService.getUserSetting).toHaveBeenCalledTimes(3)); expect(apiService.initializeUserSetting).toHaveBeenCalledTimes(2);
    await act(async () => newFinish({ key: 'profile.timeZoneId', value: 'UTC' }));
  });
  it('does not repeat a failed or unknown autosave from another consumer', async () => {
    vi.mocked(apiService.getUserSetting).mockResolvedValue(null); vi.mocked(apiService.initializeUserSetting).mockRejectedValue(new Error('Response lost'));
    renderHook(() => useUserTimeZone()); await waitFor(() => expect(apiService.initializeUserSetting).toHaveBeenCalledOnce()); renderHook(() => useUserTimeZone());
    await waitFor(() => expect(apiService.getUserSetting).toHaveBeenCalledTimes(2)); expect(apiService.initializeUserSetting).toHaveBeenCalledOnce();
  });
  it('preserves the same-login token refresh and ignores dictionary events for a personal setting', async () => {
    vi.mocked(apiService.getUserSetting).mockResolvedValue({ key: 'profile.timeZoneId', value: 'Europe/Samara' }); const view = renderHook(() => useUserTimeZone());
    await waitFor(() => expect(view.result.current).toBe('Europe/Samara')); session.refresh(); view.rerender();
    act(() => window.dispatchEvent(new Event('dicts:changed'))); await act(async () => {}); expect(apiService.getUserSetting).toHaveBeenCalledOnce();
  });
  it('does not fetch or save for a logged out owner', async () => {
    session.replace('logout'); renderHook(() => useUserTimeZone()); await act(async () => {}); expect(apiService.getUserSetting).not.toHaveBeenCalled(); expect(apiService.initializeUserSetting).not.toHaveBeenCalled();
  });
  it.each([undefined, [], { key: 'another-setting', value: 'Europe/Samara' }, { key: 'profile.timeZoneId', value: {} }])('does not initialize from malformed %j', async data => {
    vi.mocked(apiService.getUserSetting).mockResolvedValueOnce(data as never); renderHook(() => useUserTimeZone()); await act(async () => {});
    expect(apiService.getUserSetting).toHaveBeenCalledOnce(); expect(apiService.initializeUserSetting).not.toHaveBeenCalled();
  });
  it('does not confuse denied settings mentioning another HTTP code with a missing timezone', async () => {
    vi.mocked(apiService.getUserSetting).mockRejectedValue(new Error('HTTP 403 Forbidden: upstream HTTP 404')); renderHook(() => useUserTimeZone()); await act(async () => {});
    expect(apiService.initializeUserSetting).not.toHaveBeenCalled();
  });

});
