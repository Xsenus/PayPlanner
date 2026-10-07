import { beforeEach, describe, expect, it, vi } from 'vitest';
import { internalChatApi } from './internalChatApi';

const fetchMock = vi.hoisted(() => vi.fn());

vi.mock('./api', () => ({ getApiBaseUrl: () => '/api', getAuthToken: () => null }));
vi.mock('./apiTransport', () => ({ apiFetch: fetchMock }));

describe('internalChatApi.sendMessage', () => {
  beforeEach(() => {
    fetchMock.mockReset();
    fetchMock.mockResolvedValue({ ok: true, status: 201, json: async () => ({ id: 7 }) });
  });

  it('sends the stable client request ID with text, reply and attachments', async () => {
    const file = new File(['document'], 'proof.txt', { type: 'text/plain' });
    const requestId = '9914bb8f-e85a-4cc8-a4a1-b6d8eef2cc1c';

    await internalChatApi.sendMessage(12, 'Ответ', [file], 4, [8], requestId);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/api/internal-chat/threads/12/messages');
    expect(options.method).toBe('POST');
    const body = options.body as FormData;
    expect(body.get('text')).toBe('Ответ');
    expect(body.get('clientRequestId')).toBe(requestId);
    expect(body.get('replyToMessageId')).toBe('4');
    expect(body.getAll('mentionUserIds')).toEqual(['8']);
    expect(body.get('files')).toBe(file);
    expect((options.headers as Headers).has('Content-Type')).toBe(false);
  });
});

describe('internalChatApi managed send continuation', () => {
  const key = '9914bb8f-e85a-4cc8-a4a1-b6d8eef2cc1c';
  const waiting = { clientRequestId: key, status: 'waiting', automaticRecovery: true,
    retryAfter: null, message: null, description: 'Подготавливаются копии файлов.' };

  beforeEach(() => {
    fetchMock.mockReset();
    fetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => waiting });
  });

  it('reads the saved send state without cache and propagates cancellation', async () => {
    const abort = new AbortController();
    expect(await internalChatApi.getSendStatus(12, key, abort.signal)).toEqual(waiting);
    expect(fetchMock).toHaveBeenCalledWith(`/api/internal-chat/threads/12/sends/${key}`,
      expect.objectContaining({ cache: 'no-store', signal: abort.signal }));
  });

  it('returns accepted multipart sends as pending state rather than a published message', async () => {
    fetchMock.mockResolvedValue({ ok: true, status: 202, json: async () => waiting });
    expect(await internalChatApi.sendMessage(12, 'Ответ', [new File(['document'], 'proof.txt')], null, [], key)).toEqual(waiting);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect((fetchMock.mock.calls[0][1].body as FormData).get('clientRequestId')).toBe(key);
  });

  it('accepts pending state when the server omits null message and retry fields', async () => {
    const paused = { clientRequestId: key, status: 'paused', automaticRecovery: false, description: 'Обработка приостановлена.' };
    fetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => paused });
    const state = await internalChatApi.getSendStatus(12, key);
    expect(state.status).toBe('paused');
    expect(state.message).toBeUndefined();
    expect(state.retryAfter).toBeUndefined();
  });

  it('uses the bounded discovery cursor inside the selected thread', async () => {
    const page = { status: 'pending', items: [], nextAfterAttemptId: null };
    fetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => page });
    const abort = new AbortController();
    expect(await internalChatApi.getPendingSends(12, { limit: 2, afterAttemptId: key, signal: abort.signal })).toEqual(page);
    expect(fetchMock).toHaveBeenCalledWith(`/api/internal-chat/threads/12/sends?limit=2&afterAttemptId=${key}`,
      expect.objectContaining({ cache: 'no-store', signal: abort.signal }));
  });

  it('resumes the existing identity without sending files or allocating a new identity', async () => {
    expect(await internalChatApi.resumeSend(12, key)).toEqual(waiting);
    const [url, options] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe(`/api/internal-chat/threads/12/sends/${key}/resume`);
    expect(options.method).toBe('POST');
    expect(options.body).toBeUndefined();
    expect((options.headers as Headers).has('Content-Type')).toBe(false);
  });

  it('preserves a friendly backend error instead of treating a failed continuation as a message', async () => {
    fetchMock.mockResolvedValue({ ok: false, status: 503,
      text: async () => JSON.stringify({ message: 'Продолжение отправки временно недоступно.' }) });
    await expect(internalChatApi.resumeSend(12, key)).rejects.toThrow('Продолжение отправки временно недоступно.');
  });
});
