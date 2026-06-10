import { describe, expect, it } from 'vitest';

import { apiMockMethod, apiServiceMock, clearApiServiceMock, resetApiServiceMock } from './apiMock';

describe('apiMock', () => {
  it('creates stable mock methods on demand', () => {
    expect(apiServiceMock.getFeatureFlags).toBe(apiMockMethod('getFeatureFlags'));

    apiServiceMock.getFeatureFlags.mockResolvedValue({ legal: {} });

    expect(apiServiceMock.getFeatureFlags).toHaveBeenCalledTimes(0);
  });

  it('clears and resets selected api methods', async () => {
    apiServiceMock.getUsersOnline.mockResolvedValue({ users: [] });
    await apiServiceMock.getUsersOnline();

    expect(apiServiceMock.getUsersOnline).toHaveBeenCalledTimes(1);

    clearApiServiceMock('getUsersOnline');

    expect(apiServiceMock.getUsersOnline).toHaveBeenCalledTimes(0);
    await expect(apiServiceMock.getUsersOnline()).resolves.toEqual({ users: [] });

    resetApiServiceMock('getUsersOnline');

    expect(apiServiceMock.getUsersOnline).toHaveBeenCalledTimes(0);
    expect(apiServiceMock.getUsersOnline()).toBeUndefined();
  });
});
