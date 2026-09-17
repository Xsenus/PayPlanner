import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  ILogger,
  LogLevel,
} from '@microsoft/signalr';
import { getApiBaseUrl, getAuthToken } from './api';

export const REALTIME_USER_NOTIFICATION_CREATED_EVENT = 'userNotificationCreated';
export const REALTIME_TASK_JOURNAL_CHANGED_EVENT = 'taskJournalChanged';
export const TASK_JOURNAL_REALTIME_EVENT = 'payplanner:task-journal-realtime-changed';

export interface RealtimeUserNotification {
  id: number;
  recipientUserId: number;
  type: string;
  eventKey: string;
  title: string;
  bodyPreview: string;
  tone: string;
  toastEnabled?: boolean;
  objectType: string;
  objectId?: number | null;
  taskJournalItemId?: number | null;
  metadataJson?: string | null;
  createdAt: string;
}

function getNotificationsHubUrl(): string {
  const apiBaseUrl = getApiBaseUrl().replace(/\/+$/, '');
  return apiBaseUrl.replace(/\/api$/i, '') + '/hubs/notifications';
}

type RealtimeConsoleSink = Pick<Console, 'debug' | 'info' | 'warn' | 'error'>;

const REALTIME_OFFLINE_MESSAGE = 'Realtime notifications are unavailable; retrying in background.';
const EXPECTED_NETWORK_ERROR_PATTERNS = [
  /failed to fetch/i,
  /load failed/i,
  /networkerror/i,
  /err_connection_refused/i,
  /err_name_not_resolved/i,
  /err_internet_disconnected/i,
  /connection was stopped during negotiation/i,
  /failed to start the httpconnection before stop\(\) was called/i,
];
const VISIBLE_STATUS_ERROR_PATTERN = /\b(401|403|404|429|500|502|503|504)\b|status code/i;
const REALTIME_ACCESS_TOKEN_PATTERN = /([?&]access_token=)[^&\s'"`]+/gi;
const REALTIME_BEARER_TOKEN_PATTERN = /(\bBearer\s+)[A-Za-z0-9._~-]+/gi;

export function canStartNotificationsHubConnection(): boolean {
  return Boolean(getAuthToken());
}

export function isExpectedRealtimeStartupError(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error ?? '');

  if (VISIBLE_STATUS_ERROR_PATTERN.test(message)) {
    return false;
  }

  return EXPECTED_NETWORK_ERROR_PATTERNS.some((pattern) => pattern.test(message));
}

function isExpectedRealtimeNegotiationLog(logLevel: LogLevel, message: string): boolean {
  if (logLevel < LogLevel.Error || VISIBLE_STATUS_ERROR_PATTERN.test(message)) {
    return false;
  }

  return (
    /failed to complete negotiation/i.test(message) ||
    /failed to start the connection/i.test(message) ||
    /failed to start the transport/i.test(message) ||
    /failed to start the httpconnection before stop\(\) was called/i.test(message)
  ) && EXPECTED_NETWORK_ERROR_PATTERNS.some((pattern) => pattern.test(message));
}

export function createNotificationsHubLogger(sink: RealtimeConsoleSink = console): ILogger {
  return {
    log(logLevel, message) {
      const safeMessage = message
        .replace(REALTIME_ACCESS_TOKEN_PATTERN, '$1[скрыто]')
        .replace(REALTIME_BEARER_TOKEN_PATTERN, '$1[скрыто]');

      if (isExpectedRealtimeNegotiationLog(logLevel, message)) {
        sink.debug(REALTIME_OFFLINE_MESSAGE, safeMessage);
        return;
      }

      if (logLevel >= LogLevel.Error) {
        sink.error(safeMessage);
      } else if (logLevel >= LogLevel.Warning) {
        sink.warn(safeMessage);
      }
    },
  };
}

export function createNotificationsHubConnection(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(getNotificationsHubUrl(), {
      accessTokenFactory: () => getAuthToken() ?? '',
      withCredentials: false,
    })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(createNotificationsHubLogger())
    .build();
}

export async function stopNotificationsHubConnection(connection: HubConnection | null): Promise<void> {
  if (!connection || connection.state === HubConnectionState.Disconnected) {
    return;
  }

  await connection.stop();
}
