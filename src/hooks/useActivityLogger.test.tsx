import { act, renderHook } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiServiceMock, resetApiServiceMock } from '../test/apiMock';
import { useActivityLogger } from './useActivityLogger';

vi.mock('../services/api', async () => ({
  apiService: (await import('../test/apiMock')).apiServiceMock,
}));

describe('useActivityLogger', () => {
  beforeEach(() => {
    resetApiServiceMock('logUserActivity');
    apiServiceMock.logUserActivity.mockResolvedValue(undefined);
  });

  it('merges defaults and fills info status before sending activity', () => {
    const { result } = renderHook(() =>
      useActivityLogger({
        section: 'tasks',
        objectType: 'TaskJournalItem',
      }),
    );

    act(() => {
      result.current({
        category: 'Task',
        action: 'Open',
        objectId: '42',
      });
    });

    expect(apiServiceMock.logUserActivity).toHaveBeenCalledWith({
      section: 'tasks',
      objectType: 'TaskJournalItem',
      category: 'Task',
      action: 'Open',
      objectId: '42',
      status: 'Info',
    });
  });

  it('does not send incomplete activity events', () => {
    const { result } = renderHook(() => useActivityLogger());

    act(() => {
      result.current({ category: 'Task', action: '' });
      result.current({ category: '', action: 'Open' });
    });

    expect(apiServiceMock.logUserActivity).not.toHaveBeenCalled();
  });

  it('keeps the UI flow alive when activity logging fails', async () => {
    const warningSpy = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    apiServiceMock.logUserActivity.mockRejectedValue(new Error('activity api down'));
    const { result } = renderHook(() => useActivityLogger());

    act(() => {
      result.current({ category: 'Task', action: 'Open', status: 'Failure' });
    });

    await vi.waitFor(() => {
      expect(apiServiceMock.logUserActivity).toHaveBeenCalledTimes(1);
    });

    warningSpy.mockRestore();
  });
});
