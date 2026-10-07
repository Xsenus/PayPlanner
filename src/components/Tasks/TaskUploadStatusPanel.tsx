import { useEffect, useRef, useState } from 'react';
import { apiService, type TaskFileUploadState } from '../../services/api';

interface Props {
  taskId: number;
  revision: number;
  onPublished: (signal: AbortSignal) => Promise<void>;
}

const labels: Record<TaskFileUploadState['status'], string> = {
  waiting: 'Ожидание', retrying: 'Повторная попытка', paused: 'Приостановлено',
  failed: 'Не завершено', manualRetry: 'Нужен повтор', uploaded: 'Добавлен',
};

export function TaskUploadStatusPanel({ taskId, revision, onPublished }: Props) {
  const [states, setStates] = useState<TaskFileUploadState[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [navigation, setNavigation] = useState({ taskId, revision, index: 0, cursors: [undefined] as (string | undefined)[] });
  const position = navigation.taskId === taskId && navigation.revision === revision
    ? navigation : { taskId, revision, index: 0, cursors: [undefined] as (string | undefined)[] };
  const page = position.index + 1;
  const after = position.cursors[position.index];
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const hasMore = nextCursor !== null;
  const [refresh, setRefresh] = useState(0);
  const published = useRef(new Set<string>());
  const publicationTaskId = useRef(taskId);
  const callback = useRef(onPublished);
  callback.current = onPublished;

  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    let known: TaskFileUploadState[] = [];
    let firstRead = true;
    setStates([]);
    setError(null);
    setNextCursor(null);
    if (publicationTaskId.current !== taskId) {
      published.current.clear();
      publicationTaskId.current = taskId;
    }
    const read = async () => {
      try {
        const active = known.filter((item) => item.status === 'waiting' || item.status === 'retrying' || item.status === 'paused');
        const discovery = firstRead ? await apiService.getTaskFileUploadPage(taskId, controller.signal, after) : null;
        const current = discovery?.items ?? await apiService.getTaskFileUploadStates(taskId, controller.signal,
          { uploadKeys: active.map((item) => item.uploadKey) });
        if (controller.signal.aborted) return;
        // Subsequent reads use one indexed batch, never history or a request per file.
        // Disappearance is not proof of publication.
        const missing = active.filter((item) => !current.some((state) => state.uploadKey === item.uploadKey));
        const newlyPublished = current.filter((item) => item.status === 'uploaded' && item.fileId != null && !published.current.has(item.uploadKey));
        if (newlyPublished.length > 0) {
          await callback.current(controller.signal);
          if (controller.signal.aborted) return;
          newlyPublished.forEach((item) => published.current.add(item.uploadKey));
        }
        const unavailable = missing.map((item): TaskFileUploadState => ({ ...item, status: 'failed', fileId: null,
            message: 'Не удалось подтвердить состояние загрузки. Обновите список файлов перед повторной отправкой.' }));
        const retained = known.filter((item) => !active.some((state) => state.uploadKey === item.uploadKey));
        known = [...retained, ...current, ...unavailable];
        setStates(known);
        setError(null);
        if (discovery) setNextCursor(discovery.nextCursor);
        firstRead = false;
        const pending = known.filter((item) => item.status === 'waiting' || item.status === 'retrying' || item.status === 'paused');
        if (pending.length > 0) {
          timer = setTimeout(() => { void read(); }, pending.every((item) => item.status === 'paused') ? 30000 : 5000);
        }
      } catch (cause) {
        if (controller.signal.aborted) return;
        // Do not retain sensitive names when access can no longer be confirmed.
        setStates([]);
        setNextCursor(null);
        setError(cause instanceof Error && /HTTP (401|403|404)\b/.test(cause.message)
          ? 'Нет доступа к состоянию загрузок этой задачи.'
          : 'Не удалось обновить состояние загрузок. Прикреплённые файлы можно посмотреть в списке файлов.');
      }
    };
    void read();
    return () => { controller.abort(); if (timer) clearTimeout(timer); };
  }, [taskId, revision, after, refresh]);

  if (states.length === 0 && !error && page === 1 && !hasMore) return null;
  return (
    <section aria-label="Состояние ваших загрузок" className="rounded-lg border border-blue-200 bg-blue-50/40 p-3 text-sm">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="font-semibold">Состояние ваших загрузок</h3>
        <button type="button" className="text-xs font-semibold text-blue-700" onClick={() => {
          setNavigation({ taskId, revision, index: 0, cursors: [undefined] });
          setRefresh((value) => value + 1);
        }}>Обновить загрузки</button>
      </div>
      {error ? <p role="alert" className="mt-2 text-rose-700">{error}</p> : null}
      <ul className="mt-2 space-y-2" aria-live="polite">
        {states.map((item) => (
          <li key={item.uploadKey} className="rounded-md border border-blue-100 bg-white p-2">
            <div className="flex flex-wrap justify-between gap-2"><span className="break-all font-medium">{item.fileName}</span><span>{labels[item.status]}</span></div>
            <p className="mt-1 text-xs text-slate-600">{item.message}</p>
          </li>
        ))}
      </ul>
      {page > 1 || hasMore ? <div className="mt-2 flex gap-3 text-xs">
        <button type="button" disabled={page === 1} onClick={() => setNavigation({ ...position, index: position.index - 1 })}>Предыдущие загрузки</button>
        <span>Страница {page}</span>
        <button type="button" disabled={!hasMore || page >= 10000} onClick={() => setNavigation({ ...position,
          index: position.index + 1, cursors: [...position.cursors.slice(0, position.index + 1), nextCursor!] })}>Более ранние загрузки</button>
      </div> : null}
    </section>
  );
}
