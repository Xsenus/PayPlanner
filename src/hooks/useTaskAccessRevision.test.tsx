import { act, renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { USER_NOTIFICATIONS_EVENT } from '../utils/userNotifications';
import { useTaskAccessRevision } from './useTaskAccessRevision';

describe('useTaskAccessRevision', () => {
  it('invalidates task data on targeted access signals and reconnect, not normal messages', () => {
    const { result, unmount } = renderHook(() => useTaskAccessRevision());
    const send = (reason: string, source = 'realtime') => act(() => {
      window.dispatchEvent(new CustomEvent(USER_NOTIFICATIONS_EVENT, { detail: { source, reason } }));
    });
    send('created');
    send('user-role-changed', 'inbox');
    expect(result.current).toBe(0);
    send('user-role-changed');
    send('role-permissions-changed');
    send('reconnected');
    expect(result.current).toBe(2);
    unmount();
    send('user-role-changed');
  });
});
