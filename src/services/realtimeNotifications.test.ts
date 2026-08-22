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

  it('does not report deliberate stop during negotiation as a console error', () => {
    const sink = createConsoleSink();
    const logger = createNotificationsHubLogger(sink);

    logger.log(LogLevel.Error, 'Failed to start the connection: Error: The connection was stopped during negotiation.');

    expect(isExpectedRealtimeStartupError(new Error('The connection was stopped during negotiation.'))).toBe(true);
    expect(sink.error).not.toHaveBeenCalled();
    expect(sink.debug).toHaveBeenCalledWith(
      expect.stringContaining('Realtime notifications are unavailable'),
      expect.stringContaining('stopped during negotiation'),
    );
  });

  it('does not report deliberate HttpConnection stop during navigation as an error', () => {
    const sink = createConsoleSink();
    const logger = createNotificationsHubLogger(sink);
    const message = 'Failed to start the HttpConnection before stop() was called.';

    logger.log(LogLevel.Error, message);

    expect(isExpectedRealtimeStartupError(new Error(message))).toBe(true);
    expect(sink.error).not.toHaveBeenCalled();
    expect(sink.debug).toHaveBeenCalledWith(
      expect.stringContaining('Realtime notifications are unavailable'),
      message,
    );
  });

  it('does not print transport information containing the websocket access token', () => {
    const sink = createConsoleSink();
    const logger = createNotificationsHubLogger(sink);

    logger.log(
      LogLevel.Information,
      'WebSocket connected to ws://localhost/hubs/notifications?id=1&access_token=secret.jwt.token',
    );

    expect(sink.info).not.toHaveBeenCalled();
    expect(sink.debug).not.toHaveBeenCalled();
    expect(sink.warn).not.toHaveBeenCalled();
    expect(sink.error).not.toHaveBeenCalled();
  });

  it('masks access tokens if SignalR reports a warning or error', () => {
    const sink = createConsoleSink();
    const logger = createNotificationsHubLogger(sink);

    logger.log(
      LogLevel.Error,
      'Connection failed: wss://example.test/hub?access_token=secret.jwt.token&transport=webSockets',
    );

    expect(sink.error).toHaveBeenCalledWith(
      'Connection failed: wss://example.test/hub?access_token=[скрыто]&transport=webSockets',
    );
    expect(sink.error.mock.calls.flat().join(' ')).not.toContain('secret.jwt.token');
  });
});
