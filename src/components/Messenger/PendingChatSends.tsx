import { useCallback, useEffect, useRef, useState } from 'react';
import { internalChatApi, type InternalChatMessage, type InternalChatPendingSend, type InternalChatSendState } from '../../services/internalChatApi';

type Props = {
  threadId: number;
  actorId: number;
  initialKey: string | null;
  statusRevision?: number;
  incoming: InternalChatSendState | null;
  onPublished: (key: string, message: InternalChatMessage) => void;
  onObserved?: (key: string) => void;
};

/** Discovery reads bounded metadata; only the selected send is polled. */
export function PendingChatSends({ threadId, actorId, initialKey, statusRevision = 0, incoming, onPublished, onObserved }: Props) {
  const [items, setItems] = useState<InternalChatPendingSend[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [state, setState] = useState<InternalChatSendState | null>(null);
  const [busy, setBusy] = useState(false);
  const [listing, setListing] = useState(false);
  const [error, setError] = useState('');
  const request = useRef<AbortController | null>(null);
  const listRequest = useRef<AbortController | null>(null);
  const incomingKey = useRef<string | null>(null);
  const settledKeys = useRef(new Set<string>());
  const published = useRef(onPublished);
  useEffect(() => { published.current = onPublished; }, [onPublished]);
  const observed = useRef(onObserved);
  useEffect(() => { observed.current = onObserved; }, [onObserved]);

  const accept = useCallback((key: string, result: InternalChatSendState) => {
    if (result.clientRequestId.toLowerCase() !== key.toLowerCase()) throw new Error('Не удалось подтвердить идентификатор отправки.');
    if (result.status === 'published') {
      if (!result.message || result.message.threadId !== threadId || result.message.authorUserId !== actorId)
        throw new Error('Не удалось подтвердить отправленное сообщение.');
      settledKeys.current.add(key.toLowerCase());
      setItems((current) => current.filter((item) => item.clientRequestId.toLowerCase() !== key.toLowerCase()));
      setState((current) => current?.clientRequestId.toLowerCase() === key.toLowerCase() ? null : current);
      published.current(key, result.message);
    } else {
      observed.current?.(key);
      setState(result);
    }
    setError('');
  }, [actorId, threadId]);

  const read = useCallback(async (key: string, resume = false, silent = false) => {
    request.current?.abort();
    const controller = new AbortController();
    request.current = controller;
    setBusy(true);
    try {
      const result = resume ? await internalChatApi.resumeSend(threadId, key, controller.signal)
        : await internalChatApi.getSendStatus(threadId, key, controller.signal);
      if (!controller.signal.aborted) accept(key, result);
    } catch (cause) {
      if (!controller.signal.aborted && !silent) setError(cause instanceof Error ? cause.message : 'Не удалось получить состояние отправки.');
    } finally {
      if (request.current === controller) { request.current = null; setBusy(false); }
    }
  }, [accept, threadId]);

  const discover = useCallback(async (after?: string) => {
    listRequest.current?.abort();
    const controller = new AbortController();
    listRequest.current = controller;
    setListing(true);
    try {
      const page = await internalChatApi.getPendingSends(threadId, { limit: 20, afterAttemptId: after, signal: controller.signal });
      if (!controller.signal.aborted) {
        setItems((current) => Array.from(new Map([...(after ? current : []), ...page.items]
          .filter((item) => !settledKeys.current.has(item.clientRequestId.toLowerCase()))
          .map((item) => [item.clientRequestId.toLowerCase(), item])).values()));
        setCursor(page.nextAfterAttemptId ?? null);
      }
    } catch (cause) {
      if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : 'Не удалось получить незавершённые отправки.');
    } finally {
      if (listRequest.current === controller) { listRequest.current = null; setListing(false); }
    }
  }, [threadId]);

  useEffect(() => {
    void discover();
    return () => { listRequest.current?.abort(); };
  }, [discover]);

  useEffect(() => {
    // This also resolves a lost success response after re-opening the chat.
    // Legacy sends have no managed header, so an absent header is quiet here.
    if (initialKey) void read(initialKey, false, true);
    return () => { request.current?.abort(); };
  }, [initialKey, read, statusRevision]);

  useEffect(() => {
    if (!incoming) {
      const previous = incomingKey.current;
      incomingKey.current = null;
      if (previous) setState((current) => current?.clientRequestId === previous ? null : current);
      return;
    }
    incomingKey.current = incoming.clientRequestId;
    request.current?.abort();
    setBusy(false);
    setState(incoming);
    setError('');
  }, [incoming]);

  useEffect(() => {
    if (!state?.automaticRecovery || !['waiting', 'processing', 'retrying'].includes(state.status) || busy || error) return;
    const next = state.retryAfter ? Date.parse(state.retryAfter) - Date.now() : 5000;
    const delay = Number.isFinite(next) ? Math.max(5000, Math.min(60000, next)) : 5000;
    const timer = window.setTimeout(() => {
      if (document.visibilityState === 'visible') void read(state.clientRequestId);
    }, delay);
    const visible = () => { if (document.visibilityState === 'visible') void read(state.clientRequestId); };
    document.addEventListener('visibilitychange', visible);
    return () => { window.clearTimeout(timer); document.removeEventListener('visibilitychange', visible); };
  }, [busy, error, read, state]);

  if (!items.length && !state && !error) return null;
  const canResume = state && ['waiting', 'retrying', 'paused'].includes(state.status);
  return <div className="mb-2 space-y-2 rounded-lg border border-amber-200 bg-amber-50 p-2 text-xs text-amber-900" aria-label="Незавершённые отправки">
    {items.length ? <details><summary className="cursor-pointer font-medium">Незавершённые отправки: {items.length}</summary>
      <ul className="mt-2 max-h-40 space-y-1 overflow-auto">{items.map((item) => <li key={item.clientRequestId} className="flex items-center justify-between gap-2">
        <span>{new Date(item.createdAt).toLocaleString('ru-RU')} · файлов: {item.fileCount}</span>
        <button type="button" disabled={busy} onClick={() => void read(item.clientRequestId)} className="rounded px-2 py-1 underline disabled:opacity-50">Проверить отправку</button>
      </li>)}</ul>
      {cursor ? <button type="button" disabled={listing} onClick={() => void discover(cursor)} className="mt-1 rounded px-2 py-1 underline">Показать ещё</button> : null}
    </details> : null}
    {state ? <div role="status" className="space-y-1"><p>{state.description}</p>
      {state.status === 'needsInput' ? <p>Выберите исходные файлы снова и отправьте прежний текст сообщения.</p> : null}
      <div className="flex flex-wrap gap-2"><button type="button" disabled={busy} onClick={() => void read(state.clientRequestId)} className="rounded px-2 py-1 underline disabled:opacity-50">Обновить состояние</button>
        {canResume ? <button type="button" disabled={busy} onClick={() => void read(state.clientRequestId, true)} className="rounded bg-amber-100 px-2 py-1 font-medium disabled:opacity-50">Продолжить отправку</button> : null}
      </div>
    </div> : null}
    {error ? <p role="alert">{error}</p> : null}
    {error && !state ? <button type="button" disabled={listing} onClick={() => { setError(''); void discover(); }} className="rounded px-2 py-1 underline">Повторить проверку</button> : null}
  </div>;
}
