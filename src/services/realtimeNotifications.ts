import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import { getApiBaseUrl, getAuthToken } from './api';

export const REALTIME_USER_NOTIFICATION_CREATED_EVENT = 'userNotificationCreated';

export interface RealtimeUserNotification {
  id: number;
  recipientUserId: number;
  type: string;
  eventKey: string;
  title: string;
  bodyPreview: string;
  tone: string;
  objectType: string;
  objectId?: number | null;
  taskJournalItemId?: number | null;
  createdAt: string;
}

function getNotificationsHubUrl(): string {
  const apiBaseUrl = getApiBaseUrl().replace(/\/+$/, '');
  return apiBaseUrl.replace(/\/api$/i, '') + '/hubs/notifications';
}

export function createNotificationsHubConnection(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(getNotificationsHubUrl(), {
      accessTokenFactory: () => getAuthToken() ?? '',
      withCredentials: false,
    })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(import.meta.env.DEV ? LogLevel.Information : LogLevel.Warning)
    .build();
}

export async function stopNotificationsHubConnection(connection: HubConnection | null): Promise<void> {
  if (!connection || connection.state === HubConnectionState.Disconnected) {
    return;
  }

  await connection.stop();
}
