import { LogLevel } from '@microsoft/signalr';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  canStartNotificationsHubConnection,
  createNotificationsHubLogger,
  isExpectedRealtimeStartupError,
} from './realtimeNotifications';
import { getAuthToken } from './api';

vi.mock('./api', () => ({
  getApiBaseUrl: () => 'http://localhost:5080/api',
  getAuthToken: vi.fn(),
}));

function createConsoleSink() {
  return {
    debug: vi.fn(),
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
  };
}

describe('realtimeNotifications', () => {
  beforeEach(() => {
    vi.mocked(getAuthToken).mockReset();
  });

  it('does not start notification hub without auth token', () => {
    vi.mocked(getAuthToken).mockReturnValue(null);

    expect(canStartNotificationsHubConnection()).toBe(false);
  });

  it('allows notification hub startup when auth token exists', () => {
    vi.mocked(getAuthToken).mockReturnValue('jwt-token');

    expect(canStartNotificationsHubConnection()).toBe(true);
  });

  it('keeps expected offline negotiation logs out of console.error', () => {
    const sink = createConsoleSink();
    const logger = createNotificationsHubLogger(sink);

    logger.log(LogLevel.Error, 'Failed to complete negotiation with the server: TypeError: Failed to fetch');

    expect(sink.error).not.toHaveBeenCalled();
    expect(sink.debug).toHaveBeenCalledWith(
      expect.stringContaining('Realtime notifications are unavailable'),
      expect.stringContaining('Failed to complete negotiation'),
    );
  });

  it('keeps authorization negotiation failures visible', () => {
    const sink = createConsoleSink();
    const logger = createNotificationsHubLogger(sink);

    logger.log(LogLevel.Error, "Failed to complete negotiation with the server: Error: Status code '401'");

    expect(sink.error).toHaveBeenCalledWith(
      expect.stringContaining('Failed to complete negotiation'),
    );
    expect(sink.debug).not.toHaveBeenCalled();
  });

  it('classifies browser network startup errors as expected offline mode', () => {
    expect(isExpectedRealtimeStartupError(new TypeError('Failed to fetch'))).toBe(true);
    expect(isExpectedRealtimeStartupError(new Error('ERR_CONNECTION_REFUSED'))).toBe(true);
    expect(isExpectedRealtimeStartupError(new Error('401 Unauthorized'))).toBe(false);
  });
});
