import { beforeEach, describe, expect, it, vi } from 'vitest';
import { submitTaskFileUpload } from './taskFileUpload';
import { TaskFileUploadAcceptedError } from './taskFileUploadReceipt';
import type { TaskJournalFile } from '../utils/taskJournalStore';

const api = vi.hoisted(() => ({ uploadTaskJournalFile: vi.fn(), getTaskFileUploadKey: vi.fn(),
  getTaskFileUploadStates: vi.fn(), getTaskJournalFiles: vi.fn() }));
vi.mock('./api', () => ({ apiService: api }));
const saved: TaskJournalFile = { id: 601, taskJournalItemId: 101, documentKey: 'doc', version: 1,
  originalFileName: 'result.pdf', contentType: 'application/pdf', sizeBytes: 4, uploadedByName: 'Автор',
  uploadedAt: '2026-09-29T08:00:00Z', isLatestVersion: true };

describe('submitTaskFileUpload', () => {
  beforeEach(() => { vi.resetAllMocks(); api.getTaskFileUploadKey.mockReturnValue('exact-key'); });

  it('shows an exact accepted upload immediately but verifies current state before another action', async () => {
    api.uploadTaskJournalFile.mockRejectedValue(new TaskFileUploadAcceptedError(101, 'exact-key'));
    const file = new File(['test'], 'result.pdf');
    const waiting = await submitTaskFileUpload(101, file);
    expect(waiting.status).toBe('waiting');
    if (waiting.status !== 'waiting') throw new Error('Expected an unpublished accepted file.');
    expect(waiting.state).toMatchObject({ uploadKey: 'exact-key', fileId: null, status: 'waiting' });
    expect(api.getTaskFileUploadStates).not.toHaveBeenCalled();
    expect(api.getTaskJournalFiles).not.toHaveBeenCalled();
    api.getTaskFileUploadStates.mockRejectedValue(new Error('HTTP 403 Forbidden'));
    await expect(submitTaskFileUpload(101, file)).rejects.toThrow('HTTP 403');
    expect(api.getTaskFileUploadStates).toHaveBeenCalledWith(101, undefined, { uploadKeys: ['exact-key'] });
    expect(api.uploadTaskJournalFile).toHaveBeenCalledTimes(1);
  });

  it('does not resend a confirmed accepted file when its later status is missing', async () => {
    api.uploadTaskJournalFile.mockRejectedValue(new TaskFileUploadAcceptedError(101, 'exact-key'));
    const file = new File(['test'], 'result.pdf');
    await submitTaskFileUpload(101, file);
    api.getTaskFileUploadStates.mockResolvedValue([]);
    await expect(submitTaskFileUpload(101, file)).rejects.toThrow('Он не отправлен повторно');
    expect(api.uploadTaskJournalFile).toHaveBeenCalledTimes(1);
  });

  it.each(['task', 'key'])('does not trust an acceptance for another %s', async (mismatch) => {
    api.uploadTaskJournalFile.mockRejectedValue(new TaskFileUploadAcceptedError(mismatch === 'task' ? 999 : 101,
      mismatch === 'key' ? 'foreign-key' : 'exact-key'));
    api.getTaskFileUploadStates.mockResolvedValue([]);
    await expect(submitTaskFileUpload(101, new File(['test'], 'result.pdf'))).rejects.toThrow('HTTP 503');
    expect(api.getTaskFileUploadStates).toHaveBeenCalledWith(101, undefined, { uploadKeys: ['exact-key'] });
  });

  it('returns a normal successful upload without an extra status or files request', async () => {
    api.uploadTaskJournalFile.mockResolvedValue(saved);
    const file = new File(['test'], 'result.pdf');
    expect(await submitTaskFileUpload(101, file, 'doc')).toEqual({ status: 'uploaded', file: saved });
    expect(api.uploadTaskJournalFile).toHaveBeenCalledWith(101, file, 'doc');
    expect(api.getTaskFileUploadStates).not.toHaveBeenCalled();
    expect(api.getTaskJournalFiles).not.toHaveBeenCalled();
  });

  it('verifies the published file instead of retransmitting it after a later task-save failure', async () => {
    api.uploadTaskJournalFile.mockResolvedValue(saved);
    api.getTaskJournalFiles.mockResolvedValue([saved]);
    const file = new File(['test'], 'result.pdf');
    expect(await submitTaskFileUpload(101, file, 'doc')).toEqual({ status: 'uploaded', file: saved });
    expect(await submitTaskFileUpload(101, file, 'doc')).toEqual({ status: 'uploaded', file: saved });
    expect(api.uploadTaskJournalFile).toHaveBeenCalledTimes(1);
    expect(api.getTaskJournalFiles).toHaveBeenCalledTimes(1);
    expect(api.getTaskFileUploadStates).not.toHaveBeenCalled();
  });

  it.each(['missing', 'foreignTask', 'denied'])('does not reuse or resend a published upload when its live verification is %s', async (mode) => {
    api.uploadTaskJournalFile.mockResolvedValue(saved);
    const file = new File(['test'], 'result.pdf');
    await submitTaskFileUpload(101, file);
    api.getTaskJournalFiles.mockResolvedValue(mode === 'foreignTask' ? [{ ...saved, taskJournalItemId: 999 }] : []);
    if (mode === 'denied') api.getTaskJournalFiles.mockRejectedValue(new Error('HTTP 403 Forbidden'));
    await expect(submitTaskFileUpload(101, file)).rejects.toThrow();
    expect(api.uploadTaskJournalFile).toHaveBeenCalledTimes(1);
  });

  it('does not share a published upload between task IDs or document version scopes', async () => {
    api.uploadTaskJournalFile.mockResolvedValue(saved);
    const file = new File(['test'], 'result.pdf');
    await submitTaskFileUpload(101, file, 'doc');
    await submitTaskFileUpload(102, file, 'doc');
    await submitTaskFileUpload(101, file, 'other-doc');
    expect(api.uploadTaskJournalFile).toHaveBeenCalledTimes(3);
    expect(api.getTaskJournalFiles).not.toHaveBeenCalled();
  });

  it('uses the current attempt verification for several already published result files', async () => {
    const second = { ...saved, id: 602, originalFileName: 'second.pdf' };
    api.uploadTaskJournalFile.mockResolvedValueOnce(saved).mockResolvedValueOnce(second);
    const firstFile = new File(['one'], 'result.pdf');
    const secondFile = new File(['two'], 'second.pdf');
    await submitTaskFileUpload(101, firstFile);
    await submitTaskFileUpload(101, secondFile);
    api.getTaskJournalFiles.mockResolvedValue([saved, second]);
    let currentFiles: Promise<TaskJournalFile[]> | undefined;
    const verify = () => currentFiles ??= api.getTaskJournalFiles(101);
    expect(await submitTaskFileUpload(101, firstFile, undefined, verify)).toEqual({ status: 'uploaded', file: saved });
    expect(await submitTaskFileUpload(101, secondFile, undefined, verify)).toEqual({ status: 'uploaded', file: second });
    expect(api.uploadTaskJournalFile).toHaveBeenCalledTimes(2);
    expect(api.getTaskJournalFiles).toHaveBeenCalledTimes(1);
  });

  it.each(['waiting', 'retrying', 'paused'])('recognizes durable %s only by the exact server upload key', async (status) => {
    api.uploadTaskJournalFile.mockRejectedValue(new Error('HTTP 503 Unavailable'));
    const state = { uploadKey: 'exact-key', status, fileId: null, fileName: 'result.pdf', message: 'Ожидание' };
    api.getTaskFileUploadStates.mockResolvedValue([state]);
    expect(await submitTaskFileUpload(101, new File(['test'], 'result.pdf'))).toEqual({ status: 'waiting', state });
    expect(api.getTaskFileUploadStates).toHaveBeenCalledWith(101, undefined, { uploadKeys: ['exact-key'] });
    expect(api.getTaskJournalFiles).not.toHaveBeenCalled();
    expect(api.uploadTaskJournalFile).toHaveBeenCalledTimes(1);
  });

  it.each([500, 503])('recovers HTTP %s only after confirming the published file in this task', async (code) => {
    api.uploadTaskJournalFile.mockRejectedValue(new Error(`HTTP ${code} Failed`));
    api.getTaskFileUploadStates.mockResolvedValue([{ uploadKey: 'exact-key', status: 'uploaded', fileId: 601 }]);
    api.getTaskJournalFiles.mockResolvedValue([saved]);
    expect(await submitTaskFileUpload(101, new File(['test'], 'result.pdf'))).toEqual({ status: 'uploaded', file: saved });
    expect(api.uploadTaskJournalFile).toHaveBeenCalledTimes(1);
  });

  it('checks an accepted upload before retry and does not retransmit its bytes', async () => {
    api.uploadTaskJournalFile.mockRejectedValue(new Error('HTTP 503 Failed'));
    const state = { uploadKey: 'exact-key', status: 'waiting', fileId: null, fileName: 'result.pdf', message: 'Ожидание' };
    api.getTaskFileUploadStates.mockResolvedValue([state]);
    const file = new File(['test'], 'result.pdf');
    expect((await submitTaskFileUpload(101, file)).status).toBe('waiting');
    expect((await submitTaskFileUpload(101, file)).status).toBe('waiting');
    api.getTaskFileUploadStates.mockResolvedValue([{ ...state, status: 'uploaded', fileId: 601 }]);
    api.getTaskJournalFiles.mockResolvedValue([saved]);
    expect(await submitTaskFileUpload(101, file)).toEqual({ status: 'uploaded', file: saved });
    expect(api.uploadTaskJournalFile).toHaveBeenCalledTimes(1);
    expect(api.getTaskFileUploadStates).toHaveBeenCalledTimes(3);
  });

  it.each([400, 401, 403, 409])('does not reinterpret HTTP %s or retry a denied request', async (code) => {
    const original = new Error(`HTTP ${code} Failed`);
    api.uploadTaskJournalFile.mockRejectedValue(original);
    await expect(submitTaskFileUpload(101, new File(['test'], 'result.pdf'))).rejects.toBe(original);
    expect(api.getTaskFileUploadStates).not.toHaveBeenCalled();
  });

  it.each(['statusError', 'noKey', 'missingState', 'foreignKey', 'missingFile', 'foreignTask', 'failed'])('does not retransmit an unresolved upload when retry verification returns %s', async (mode) => {
    api.uploadTaskJournalFile.mockRejectedValue(new Error('HTTP 503 Unavailable'));
    const state = { uploadKey: 'exact-key', status: 'waiting', fileId: null, message: 'Ожидание' };
    api.getTaskFileUploadStates.mockResolvedValue([state]);
    const file = new File(['test'], 'result.pdf');
    expect((await submitTaskFileUpload(101, file)).status).toBe('waiting');

    if (mode === 'statusError') api.getTaskFileUploadStates.mockRejectedValue(new Error('HTTP 403 Forbidden'));
    if (mode === 'noKey') api.getTaskFileUploadKey.mockReturnValue(undefined);
    if (mode === 'missingState') api.getTaskFileUploadStates.mockResolvedValue([]);
    if (mode === 'foreignKey') api.getTaskFileUploadStates.mockResolvedValue([{ ...state, uploadKey: 'other-key' }]);
    if (mode === 'failed') api.getTaskFileUploadStates.mockResolvedValue([{ ...state, status: 'failed', message: 'Доступ к задаче утрачен.' }]);
    if (mode === 'missingFile' || mode === 'foreignTask') {
      api.getTaskFileUploadStates.mockResolvedValue([{ ...state, status: 'uploaded', fileId: 601 }]);
      api.getTaskJournalFiles.mockResolvedValue(mode === 'foreignTask' ? [{ ...saved, taskJournalItemId: 999 }] : []);
    }

    await expect(submitTaskFileUpload(101, file)).rejects.toThrow();
    expect(api.uploadTaskJournalFile).toHaveBeenCalledTimes(1);

    // Recover the same selected file once the exact publication becomes verifiable.
    api.getTaskFileUploadKey.mockReturnValue('exact-key');
    api.getTaskFileUploadStates.mockResolvedValue([{ ...state, status: 'uploaded', fileId: 601 }]);
    api.getTaskJournalFiles.mockResolvedValue([saved]);
    expect(await submitTaskFileUpload(101, file)).toEqual({ status: 'uploaded', file: saved });
    expect(api.uploadTaskJournalFile).toHaveBeenCalledTimes(1);
  });

  it('permits an explicit manual retry with the original file and document scope', async () => {
    api.uploadTaskJournalFile.mockRejectedValueOnce(new Error('HTTP 503 Unavailable')).mockResolvedValue(saved);
    api.getTaskFileUploadStates.mockResolvedValue([{ uploadKey: 'exact-key', status: 'paused', fileId: null }]);
    const file = new File(['test'], 'result.pdf');
    expect((await submitTaskFileUpload(101, file, 'doc')).status).toBe('waiting');
    api.getTaskFileUploadStates.mockResolvedValue([{ uploadKey: 'exact-key', status: 'manualRetry', fileId: null }]);
    expect(await submitTaskFileUpload(101, file, 'doc')).toEqual({ status: 'uploaded', file: saved });
    expect(api.uploadTaskJournalFile).toHaveBeenCalledTimes(2);
    expect(api.uploadTaskJournalFile).toHaveBeenLastCalledWith(101, file, 'doc');
  });

  it('does not block ordinary retry when no server upload was ever confirmed', async () => {
    const original = new Error('HTTP 503 Unavailable');
    api.uploadTaskJournalFile.mockRejectedValueOnce(original).mockResolvedValue(saved);
    api.getTaskFileUploadStates.mockResolvedValue([]);
    const file = new File(['test'], 'result.pdf');
    await expect(submitTaskFileUpload(101, file)).rejects.toBe(original);
    expect(await submitTaskFileUpload(101, file)).toEqual({ status: 'uploaded', file: saved });
    expect(api.uploadTaskJournalFile).toHaveBeenCalledTimes(2);
  });

  it.each(['uploaded', 'failed'])('remembers a confirmed %s receipt even when its result cannot be verified yet', async (status) => {
    const original = new Error('HTTP 503 Unavailable');
    api.uploadTaskJournalFile.mockRejectedValueOnce(original).mockResolvedValue(saved);
    api.getTaskFileUploadStates.mockResolvedValue([{ uploadKey: 'exact-key', status, fileId: 601, message: 'Не удалось завершить.' }]);
    api.getTaskJournalFiles.mockResolvedValue([]);
    const file = new File(['test'], 'result.pdf');
    await expect(submitTaskFileUpload(101, file)).rejects.toBe(original);
    api.getTaskFileUploadStates.mockResolvedValue([]);
    await expect(submitTaskFileUpload(101, file)).rejects.toThrow('не отправлен повторно');
    expect(api.uploadTaskJournalFile).toHaveBeenCalledTimes(1);
  });

  it.each(['foreignKey', 'manualRetry', 'missingFile', 'foreignTask', 'statusError', 'noKey'])('keeps the original failure for %s', async (mode) => {
    const original = new Error('HTTP 503 Failed');
    api.uploadTaskJournalFile.mockRejectedValue(original);
    api.getTaskFileUploadStates.mockResolvedValue([{ uploadKey: mode === 'foreignKey' ? 'another-key' : 'exact-key',
      status: mode === 'manualRetry' ? 'manualRetry' : 'uploaded', fileId: 601, fileName: 'result.pdf' }]);
    api.getTaskJournalFiles.mockResolvedValue(mode === 'foreignTask' ? [{ ...saved, taskJournalItemId: 999 }] : []);
    if (mode === 'statusError') api.getTaskFileUploadStates.mockRejectedValue(new Error('HTTP 403 Forbidden'));
    if (mode === 'noKey') api.getTaskFileUploadKey.mockReturnValue(undefined);
    await expect(submitTaskFileUpload(101, new File(['test'], 'result.pdf'))).rejects.toBe(original);
    expect(api.uploadTaskJournalFile).toHaveBeenCalledTimes(1);
  });
});
