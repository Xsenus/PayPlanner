import { apiService, type TaskFileUploadState } from './api';
import { TaskFileUploadAcceptedError } from './taskFileUploadReceipt';
import type { TaskJournalFile } from '../utils/taskJournalStore';

export type TaskFileUploadAttempt =
  | { status: 'uploaded'; file: TaskJournalFile }
  | { status: 'waiting'; state: TaskFileUploadState };

// A true value records a confirmed accepted upload; false means only its HTTP response was lost.
const unresolved = new WeakMap<File, Map<string, boolean>>();
const published = new WeakMap<File, Map<string, TaskJournalFile>>();

function rememberPublished(file: File, scope: string, saved: TaskJournalFile) {
  const files = published.get(file) ?? new Map<string, TaskJournalFile>();
  files.set(scope, saved);
  published.set(file, files);
  unresolved.get(file)?.delete(scope);
}

export class TaskFileUploadWaitingError extends Error {
  constructor(public readonly state: TaskFileUploadState) {
    super(`${state.message} Задача пока не завершена и не отправлена на согласование. После прикрепления файла повторите действие с тем же выбранным файлом.`);
    this.name = 'TaskFileUploadWaitingError';
  }
}

async function readAccepted(taskId: number, key: string, requireRetryDecision = false, rememberAccepted?: () => void): Promise<TaskFileUploadAttempt | undefined> {
  const states = await apiService.getTaskFileUploadStates(taskId, undefined, { uploadKeys: [key] });
  const state = states.find((item) => item.uploadKey === key);
  if (state) rememberAccepted?.();
  if (state?.status === 'uploaded' && state.fileId != null) {
    const files = await apiService.getTaskJournalFiles(taskId);
    const saved = files.find((item) => item.id === state.fileId && item.taskJournalItemId === taskId);
    if (saved) return { status: 'uploaded', file: saved };
  }
  if (state && ['waiting', 'retrying', 'paused'].includes(state.status)) return { status: 'waiting', state };
  if ((requireRetryDecision || state !== undefined) && state?.status !== 'manualRetry') {
    throw new Error(state?.status === 'failed'
      ? state.message
      : 'Не удалось подтвердить состояние ранее отправленного файла. Он не отправлен повторно. Повторите действие позже.');
  }
}

// A failed HTTP response does not prove that an accepted upload failed.
// Recover only an exact existing upload key, never by a matching file name.
export async function submitTaskFileUpload(taskId: number, file: File, documentKey?: string,
  readPublishedFiles?: () => Promise<TaskJournalFile[]>): Promise<TaskFileUploadAttempt> {
  const scope = `${taskId}:${documentKey ?? ''}`;
  const previous = published.get(file)?.get(scope);
  if (previous) {
    // Recheck the live task and current access; a remembered ID is not authorization.
    const files = await (readPublishedFiles?.() ?? apiService.getTaskJournalFiles(taskId));
    const saved = files.find((item) => item.id === previous.id && item.taskJournalItemId === taskId);
    if (!saved) throw new Error('Ранее прикреплённый файл больше не доступен в задаче. Проверьте вложения и доступ к задаче перед повторным действием.');
    return { status: 'uploaded', file: saved };
  }
  if (unresolved.get(file)?.has(scope)) {
    const key = apiService.getTaskFileUploadKey(taskId, file, documentKey);
    if (!key) {
      throw new Error('Не удалось определить ранее отправленный файл. Он не отправлен повторно. Повторите действие позже.');
    }
    // A failed status read is not permission to retransmit accepted bytes.
    // For a confirmed accepted upload, only manualRetry permits another attempt.
    const accepted = await readAccepted(taskId, key, unresolved.get(file)?.get(scope) === true,
      () => unresolved.get(file)?.set(scope, true));
    if (accepted) {
      if (accepted.status === 'uploaded') rememberPublished(file, scope, accepted.file);
      else unresolved.get(file)?.set(scope, true);
      return accepted;
    }
  }
  try {
    const saved = await apiService.uploadTaskJournalFile(taskId, file, documentKey);
    rememberPublished(file, scope, saved);
    return { status: 'uploaded', file: saved };
  } catch (original) {
    const key = apiService.getTaskFileUploadKey(taskId, file, documentKey);
    if (original instanceof TaskFileUploadAcceptedError && original.taskId === taskId && original.uploadKey === key) {
      const scopes = unresolved.get(file) ?? new Map<string, boolean>();
      scopes.set(scope, true);
      unresolved.set(file, scopes);
      return { status: 'waiting', state: { uploadKey: key, fileName: file.name, status: 'waiting', fileId: null,
        message: 'Файл сохранён. Вложение будет доступно после подготовки обязательных копий. Повторная отправка не требуется; состояние загрузки можно проверить в карточке задачи.' } };
    }
    if (!key || !(original instanceof Error) || !/HTTP (500|503)\b/.test(original.message)) throw original;
    const scopes = unresolved.get(file) ?? new Map<string, boolean>();
    scopes.set(scope, scopes.get(scope) ?? false);
    unresolved.set(file, scopes);
    try {
      const accepted = await readAccepted(taskId, key, false, () => scopes.set(scope, true));
      if (accepted) {
        if (accepted.status === 'uploaded') rememberPublished(file, scope, accepted.file);
        else scopes.set(scope, true);
        return accepted;
      }
    } catch { /* Do not replace an upload failure with an unrelated status-read failure. */ }
    throw original;
  }
}
