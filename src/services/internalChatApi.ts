import { getApiBaseUrl, getAuthToken } from './api';
import { apiFetch as fetch } from './apiTransport';

export type InternalChatThreadKind = 'task' | 'channel' | 'direct';
export type InternalChatNotificationMode = 'all' | 'mentions' | 'muted';

export interface InternalChatThread {
  id: number;
  kind: InternalChatThreadKind;
  name: string;
  taskJournalItemId?: number | null;
  role: string;
  notificationMode: InternalChatNotificationMode;
  isArchived: boolean;
  memberUserIds: number[];
  guestUserIds: number[];
  unreadCount: number;
  lastReadMessageId?: number | null;
  firstUnreadMessageId?: number | null;
  lastMessagePreview?: string | null;
  lastMessageAt?: string | null;
  createdAt: string;
}

export interface InternalChatPerson {
  id: number;
  name: string;
  email: string;
  photoUrl?: string | null;
}

export interface InternalChatAttachment {
  id: number;
  fileName: string;
  contentType: string;
  size: number;
}

export interface InternalChatReaction {
  emoji: string;
  count: number;
  userIds: number[];
}

export interface InternalChatMessage {
  id: number;
  threadId: number;
  authorUserId?: number | null;
  authorName: string;
  kind: 'user' | 'system' | 'task-link';
  text: string;
  replyToMessageId?: number | null;
  replyPreview?: string | null;
  replyAuthorName?: string | null;
  createdTaskJournalItemId?: number | null;
  isPinned: boolean;
  noReplyReminderEnabled: boolean;
  editedAt?: string | null;
  deletedAt?: string | null;
  createdAt: string;
  attachments: InternalChatAttachment[];
  reactions: InternalChatReaction[];
  mentionUserIds: number[];
  readByUserIds: number[];
}

export interface InternalChatMessagePage {
  items: InternalChatMessage[];
  hasMore: boolean;
  hasNewer?: boolean;
  nextBeforeId?: number | null;
}

type RequestOptions = RequestInit & { body?: BodyInit | null };

export interface InternalChatSendState {
  clientRequestId: string;
  status: 'waiting' | 'processing' | 'retrying' | 'paused' | 'needsInput' | 'failed' | 'conflict' | 'deleted' | 'published';
  automaticRecovery: boolean;
  retryAfter?: string | null;
  message?: InternalChatMessage | null;
  description: string;
}

export interface InternalChatPendingSend {
  attemptId: string;
  clientRequestId: string;
  createdAt: string;
  fileCount: number;
  totalSizeBytes: number;
  lastStatus?: string | null;
}

export interface InternalChatPendingSendPage {
  status: 'pending';
  items: InternalChatPendingSend[];
  nextAfterAttemptId?: string | null;
}

async function chatRequest<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const token = getAuthToken();
  const headers = new Headers(options.headers);
  if (token) headers.set('Authorization', `Bearer ${token}`);
  if (options.body && !(options.body instanceof FormData) && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json');
  }
  const response = await fetch(`${getApiBaseUrl()}/internal-chat${path}`, { ...options, headers });
  if (!response.ok) {
    const text = await response.text();
    let message = text || `Ошибка ${response.status}`;
    try {
      const parsed = JSON.parse(text) as { message?: string; title?: string };
      message = parsed.message || parsed.title || message;
    } catch {
      // The API may return a plain, user-readable validation message.
    }
    throw new Error(message.replace(/^"|"$/g, ''));
  }
  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}

export const internalChatApi = {
  getSendStatus: (threadId: number, clientRequestId: string, signal?: AbortSignal) =>
    chatRequest<InternalChatSendState>(`/threads/${threadId}/sends/${encodeURIComponent(clientRequestId)}`, { cache: 'no-store', signal }),
  getPendingSends: (threadId: number, params: { limit?: number; afterAttemptId?: string; signal?: AbortSignal } = {}) => {
    const query = new URLSearchParams();
    if (params.limit !== undefined) query.set('limit', String(params.limit));
    if (params.afterAttemptId) query.set('afterAttemptId', params.afterAttemptId);
    return chatRequest<InternalChatPendingSendPage>(`/threads/${threadId}/sends${query.size ? `?${query}` : ''}`, { cache: 'no-store', signal: params.signal });
  },
  resumeSend: (threadId: number, clientRequestId: string, signal?: AbortSignal) =>
    chatRequest<InternalChatSendState>(`/threads/${threadId}/sends/${encodeURIComponent(clientRequestId)}/resume`, { method: 'POST', signal }),
  getThreads: () => chatRequest<InternalChatThread[]>('/threads'),
  getPeople: () => chatRequest<InternalChatPerson[]>('/people'),
  createChannel: (name: string, memberUserIds: number[]) => chatRequest<InternalChatThread>('/channels', {
    method: 'POST', body: JSON.stringify({ name, memberUserIds }),
  }),
  openDirect: (userId: number) => chatRequest<InternalChatThread>(`/direct/${userId}`, { method: 'POST' }),
  openTask: (taskId: number) => chatRequest<InternalChatThread>(`/tasks/${taskId}`, { method: 'POST' }),
  updateMembers: (threadId: number, memberUserIds: number[]) => chatRequest<void>(`/threads/${threadId}/members`, {
    method: 'PUT', body: JSON.stringify({ memberUserIds }),
  }),
  updateTaskGuests: (threadId: number, guestUserIds: number[]) => chatRequest<void>(`/threads/${threadId}/task-guests`, {
    method: 'PUT', body: JSON.stringify({ guestUserIds }),
  }),
  setPersonalState: (threadId: number, state: 'visible' | 'hidden' | 'archived') => chatRequest<void>(`/threads/${threadId}/personal-state`, {
    method: 'PUT', body: JSON.stringify({ state }),
  }),
  getMessages: (threadId: number, params: { beforeId?: number; afterId?: number; search?: string; authorUserId?: number; from?: string; to?: string; mentionedMe?: boolean } = {}) => {
    const query = new URLSearchParams();
    if (params.beforeId) query.set('beforeId', String(params.beforeId));
    if (params.afterId !== undefined) query.set('afterId', String(params.afterId));
    if (params.search) query.set('search', params.search);
    if (params.authorUserId) query.set('authorUserId', String(params.authorUserId));
    if (params.from) query.set('from', params.from);
    if (params.to) query.set('to', params.to);
    if (params.mentionedMe) query.set('mentionedMe', 'true');
    return chatRequest<InternalChatMessagePage>(`/threads/${threadId}/messages${query.size ? `?${query}` : ''}`);
  },
  sendMessage: (threadId: number, text: string, files: File[], replyToMessageId?: number | null, mentionUserIds: number[] = [], clientRequestId?: string) => {
    const body = new FormData();
    body.append('text', text);
    if (clientRequestId) body.append('clientRequestId', clientRequestId);
    if (replyToMessageId) body.append('replyToMessageId', String(replyToMessageId));
    mentionUserIds.forEach((id) => body.append('mentionUserIds', String(id)));
    files.forEach((file) => body.append('files', file));
    return chatRequest<InternalChatMessage | InternalChatSendState>(`/threads/${threadId}/messages`, { method: 'POST', body });
  },
  editMessage: (messageId: number, text: string, mentionUserIds: number[] = []) => chatRequest<InternalChatMessage>(`/messages/${messageId}`, {
    method: 'PUT', body: JSON.stringify({ text, mentionUserIds }),
  }),
  deleteMessage: (messageId: number) => chatRequest<void>(`/messages/${messageId}`, { method: 'DELETE' }),
  toggleReaction: (messageId: number, emoji: string) => chatRequest<void>(`/messages/${messageId}/reactions`, {
    method: 'POST', body: JSON.stringify({ emoji }),
  }),
  setPinned: (messageId: number, pinned: boolean) => chatRequest<void>(`/messages/${messageId}/pin`, {
    method: 'POST', body: JSON.stringify({ pinned }),
  }),
  setNoReplyReminder: (messageId: number, enabled: boolean) => chatRequest<InternalChatMessage>(`/messages/${messageId}/no-reply-reminder`, {
    method: 'POST', body: JSON.stringify({ enabled }),
  }),
  markRead: (threadId: number, messageId?: number) => chatRequest<void>(`/threads/${threadId}/read`, {
    method: 'POST', body: JSON.stringify({ messageId: messageId ?? null }),
  }),
  setNotificationMode: (threadId: number, mode: InternalChatNotificationMode) => chatRequest<void>(`/threads/${threadId}/notification-mode`, {
    method: 'PUT', body: JSON.stringify({ mode }),
  }),
  createTask: (messageId: number, request: { title: string; description?: string; assigneeUserId?: number | null; dueAt: string }) => chatRequest<InternalChatMessage>(`/messages/${messageId}/task`, {
    method: 'POST', body: JSON.stringify(request),
  }),
  attachmentUrl: (attachmentId: number) => `${getApiBaseUrl()}/internal-chat/attachments/${attachmentId}`,
  getAttachmentBlob: async (attachmentId: number) => {
    const token = getAuthToken();
    const response = await fetch(`${getApiBaseUrl()}/internal-chat/attachments/${attachmentId}`, {
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });
    if (!response.ok) throw new Error('Не удалось загрузить файл.');
    return response.blob();
  },
  downloadAttachment: async (attachment: InternalChatAttachment) => {
    const blob = await internalChatApi.getAttachmentBlob(attachment.id);
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = attachment.fileName;
    link.click();
    URL.revokeObjectURL(url);
  },
};
