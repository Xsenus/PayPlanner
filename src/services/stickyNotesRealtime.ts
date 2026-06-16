import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import { getApiBaseUrl, getAuthToken, type StickyNoteRealtimeEvent } from './api';
import { createNotificationsHubLogger, isExpectedRealtimeStartupError } from './realtimeNotifications';

export const STICKY_NOTE_BOARD_CHANGED_EVENT = 'stickyNoteBoardChanged';

function getNotesHubUrl(): string {
  const apiBaseUrl = getApiBaseUrl().replace(/\/+$/, '');
  return apiBaseUrl.replace(/\/api$/i, '') + '/hubs/notes';
}

export function canStartNotesHubConnection(): boolean {
  return Boolean(getAuthToken());
}

export function createNotesHubConnection(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(getNotesHubUrl(), {
      accessTokenFactory: () => getAuthToken() ?? '',
      withCredentials: false,
    })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(createNotificationsHubLogger())
    .build();
}

export async function startNotesHubConnection(connection: HubConnection): Promise<boolean> {
  if (connection.state !== HubConnectionState.Disconnected) {
    return true;
  }

  try {
    await connection.start();
    return true;
  } catch (error) {
    if (isExpectedRealtimeStartupError(error)) {
      console.debug('Sticky notes realtime is unavailable; retrying in background.', error);
      return false;
    }

    console.warn('Sticky notes realtime startup failed.', error);
    return false;
  }
}

export async function joinNotesBoard(connection: HubConnection | null, boardId: number): Promise<void> {
  if (!connection || connection.state !== HubConnectionState.Connected || boardId <= 0) {
    return;
  }

  await connection.invoke('JoinBoard', boardId);
}

export async function leaveNotesBoard(connection: HubConnection | null, boardId: number): Promise<void> {
  if (!connection || connection.state !== HubConnectionState.Connected || boardId <= 0) {
    return;
  }

  await connection.invoke('LeaveBoard', boardId);
}

export async function stopNotesHubConnection(connection: HubConnection | null): Promise<void> {
  if (!connection || connection.state === HubConnectionState.Disconnected) {
    return;
  }

  await connection.stop();
}

export type StickyNotesRealtimeHandler = (event: StickyNoteRealtimeEvent) => void;

export { HubConnectionState, LogLevel };
