import { documentCopyTestSession } from '../../test/documentCopySession';
import { onTestFinished } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CaseDocumentUploadStatusPanel } from './CaseDocumentUploadStatusPanel';
import type { CaseDocumentUploadState } from '../../services/api';

const api = vi.hoisted(() => ({ getCaseDocumentUploadStates: vi.fn(), getPendingCaseDocumentUploads: vi.fn(), retireCaseDocumentUpload: vi.fn() }));
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: 7, role: { id: 10 } } }) }));
const dialog = vi.hoisted(() => ({ confirm: vi.fn() }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => dialog }));
vi.mock('../../services/api', () => ({ apiService: api, getAuthToken: () => 'owned-ui-session' }));
const waiting: CaseDocumentUploadState = { uploadKey: 'key-1', fileName: 'document.pdf', status: 'waiting', documentId: null, version: null, sha256: null, contentType: null, documentKey: null, requestedDocumentKey: null, documentType: null, requestedStatus: null, message: 'Ожидаем копии.' };
const flush = async () => { await act(async () => { await Promise.resolve(); }); };

describe('CaseDocumentUploadStatusPanel', () => {
  it.each(['login', 'actor', 'logout'] as const)('upload status owner baseline: %s invalidates a pending retirement confirmation', async mode => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    let finish!: (approved: boolean) => void;
    dialog.confirm.mockReturnValue(new Promise<boolean>(resolve => { finish = resolve; }));
    api.getPendingCaseDocumentUploads.mockResolvedValue({ items: [waiting], nextCursor: null });
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={vi.fn()} />);
    await flush();
    fireEvent.click(screen.getByRole('button', { name: 'Подготовить новую загрузку' }));
    session.replace(mode);
    await act(async () => { finish(true); });
    expect(api.retireCaseDocumentUpload).not.toHaveBeenCalled();
  });

  it.each(['login', 'actor', 'logout'] as const)('upload status owner baseline: %s invalidates a pending discovery reply', async mode => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    let finish!: (value: { items: typeof waiting[]; nextCursor: null; localPublicationConfirmed: boolean }) => void;
    api.getPendingCaseDocumentUploads.mockReturnValue(new Promise(resolve => { finish = resolve; }));
    const published = vi.fn();
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={published} />);
    await flush();
    session.replace(mode);
    await act(async () => { finish({ items: [waiting], nextCursor: null, localPublicationConfirmed: true }); });
    expect(published).not.toHaveBeenCalled();
    expect(screen.queryByText('document.pdf')).toBeNull();
  });

  it.each(['login', 'actor', 'logout'] as const)('upload status owner polling: %s stops a scheduled batch before render', async mode => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    api.getPendingCaseDocumentUploads.mockResolvedValue({ items: [waiting], nextCursor: null });
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={vi.fn()} />);
    await flush(); session.replace(mode);
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(api.getCaseDocumentUploadStates).not.toHaveBeenCalled();
  });

  it('upload status owner parent: a shared pending mutation blocks a retirement prompt', async () => {
    const begin = vi.fn(() => null);
    const parent = { scope: 'busy-parent', busy: true, capture: () => ({ isCurrent: () => true }), begin };
    api.getPendingCaseDocumentUploads.mockResolvedValue({ items: [waiting], nextCursor: null });
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} actionOwner={parent} onPublished={vi.fn()} />);
    await flush(); fireEvent.click(screen.getByRole('button', { name: 'Подготовить новую загрузку' }));
    expect(dialog.confirm).not.toHaveBeenCalled();
    expect(api.retireCaseDocumentUpload).not.toHaveBeenCalled();
  });

  it('upload status owner parent: narrowing the parent scope aborts pending discovery', async () => {
    let finish!: (value: { items: typeof waiting[]; nextCursor: null; localPublicationConfirmed: boolean }) => void;
    api.getPendingCaseDocumentUploads.mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
    const callback = vi.fn();
    const parent = { scope: 'before', busy: false, capture: () => ({ isCurrent: () => true }), begin: vi.fn(() => null) };
    const view = render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} actionOwner={parent} onPublished={callback} />);
    await flush(); const signal = api.getPendingCaseDocumentUploads.mock.calls[0][2] as AbortSignal;
    view.rerender(<CaseDocumentUploadStatusPanel caseId={101} revision={0} actionOwner={{ ...parent, scope: 'after' }} onPublished={callback} />);
    await flush(); expect(signal.aborted).toBe(true);
    await act(async () => { finish({ items: [waiting], nextCursor: null, localPublicationConfirmed: true }); });
    expect(callback).not.toHaveBeenCalled();
    expect(screen.queryByText('document.pdf')).toBeNull();
  });

  it('refreshes documents after local ready proof without adding published history or changing unsaved fields', async () => {
    api.getPendingCaseDocumentUploads.mockResolvedValue({ items: [], nextCursor: null, localPublicationConfirmed: true });
    const onPublished = vi.fn().mockResolvedValue(undefined);
    render(<><input aria-label="Несохранённая стратегия" defaultValue="Ручная стратегия" />
      <CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={onPublished} /></>); await flush();
    expect(onPublished).toHaveBeenCalledTimes(1);
    expect((screen.getByLabelText('Несохранённая стратегия') as HTMLInputElement).value).toBe('Ручная стратегия');
    expect(screen.queryByRole('region', { name: 'Состояние ваших загрузок' })).toBeNull();
    expect(api.getCaseDocumentUploadStates).not.toHaveBeenCalled(); expect(api.retireCaseDocumentUpload).not.toHaveBeenCalled();
  });

  it('retries document refresh after local ready proof without another proof read or business action', async () => {
    api.getPendingCaseDocumentUploads.mockResolvedValue({ items: [], nextCursor: null, localPublicationConfirmed: true });
    const onPublished = vi.fn().mockRejectedValueOnce(new Error('temporary document list failure')).mockResolvedValue(undefined);
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={onPublished} />); await flush();
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(onPublished).toHaveBeenCalledTimes(2); expect(api.getPendingCaseDocumentUploads).toHaveBeenCalledTimes(1);
    expect(api.getCaseDocumentUploadStates).not.toHaveBeenCalled(); expect(api.retireCaseDocumentUpload).not.toHaveBeenCalled();
  });
  it.each(['retired', 'uploaded', 'cancelled'])('handles generated %s without offering file selection or starting generation', async (outcome) => {
    const generated = { ...waiting, fileName: 'claim.html', generation: { kind: 'template', requestSha256: 'a'.repeat(64), templateKey: 'custom', documentType: 'Иск', status: 'Подготовлен', sourceVerified: true } };
    api.getPendingCaseDocumentUploads.mockResolvedValueOnce({ items: [generated], nextCursor: null })
      .mockResolvedValue({ items: [], nextCursor: null });
    api.retireCaseDocumentUpload.mockResolvedValue({ ...waiting, status: outcome, documentId: outcome === 'uploaded' ? 601 : null, version: outcome === 'uploaded' ? 1 : null });
    dialog.confirm.mockResolvedValue(outcome !== 'cancelled');
    const onPublished = vi.fn();
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={onPublished} />); await flush();
    expect(screen.queryByRole('button', { name: 'Подготовить новую загрузку' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Прекратить формирование' })); await flush();
    expect(dialog.confirm).toHaveBeenCalledWith(expect.objectContaining({ title: 'Прекратить формирование' }));
    expect(dialog.confirm.mock.calls[0][0].description).not.toContain('Новый файл');
    expect(api.retireCaseDocumentUpload).toHaveBeenCalledTimes(outcome === 'cancelled' ? 0 : 1);
    expect(onPublished).toHaveBeenCalledTimes(outcome === 'uploaded' ? 1 : 0);
    if (outcome === 'retired') expect(screen.getByText(/Прежняя попытка формирования прекращена/)).toBeTruthy();
    if (outcome === 'uploaded') expect(screen.getByText(/Новое формирование запустите отдельно/)).toBeTruthy();
  });
  it('requires approval and blocks duplicate clicks without sending any file', async () => {
    api.getPendingCaseDocumentUploads.mockResolvedValue({ items: [waiting], nextCursor: null });
    let approve!: (value: boolean) => void;
    dialog.confirm.mockImplementation(() => new Promise((resolve) => { approve = resolve; }));
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={vi.fn()} />); await flush();
    const button = screen.getByRole('button', { name: 'Подготовить новую загрузку' });
    fireEvent.click(button); fireEvent.click(button);
    expect(dialog.confirm).toHaveBeenCalledTimes(1); expect(api.retireCaseDocumentUpload).not.toHaveBeenCalled();
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(api.getCaseDocumentUploadStates).not.toHaveBeenCalled();
    await act(async () => approve(false));
    expect(api.retireCaseDocumentUpload).not.toHaveBeenCalled();
  });
  it('confirms retirement and leaves new file submission as a separate action', async () => {
    const retired = { ...waiting, status: 'retired' };
    api.getPendingCaseDocumentUploads.mockResolvedValueOnce({ items: [waiting], nextCursor: null })
      .mockResolvedValue({ items: [retired], nextCursor: null });
    api.retireCaseDocumentUpload.mockResolvedValue(retired); dialog.confirm.mockResolvedValue(true);
    const onPublished = vi.fn(); render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={onPublished} />); await flush();
    fireEvent.click(screen.getByRole('button', { name: 'Подготовить новую загрузку' })); await flush();
    expect(api.retireCaseDocumentUpload).toHaveBeenCalledExactlyOnceWith(101, waiting.uploadKey, expect.any(AbortSignal), 'owned-ui-session');
    expect(screen.getByText(/Прежняя попытка прекращена\. Новый файл/)).toBeTruthy();
    expect(screen.getByText('Попытка прекращена')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Подготовить новую загрузку' })).toBeNull();
    expect(onPublished).not.toHaveBeenCalled(); expect(vi.getTimerCount()).toBe(0);
  });
  it('refreshes an already published old document and never labels the selected new file as uploaded', async () => {
    api.getPendingCaseDocumentUploads.mockResolvedValueOnce({ items: [waiting], nextCursor: null }).mockResolvedValue({ items: [], nextCursor: null });
    api.retireCaseDocumentUpload.mockResolvedValue({ ...waiting, status: 'uploaded', documentId: 601, version: 2 });
    dialog.confirm.mockResolvedValue(true); const onPublished = vi.fn().mockResolvedValue(undefined);
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={onPublished} />); await flush();
    fireEvent.click(screen.getByRole('button', { name: 'Подготовить новую загрузку' })); await flush();
    expect(onPublished).toHaveBeenCalledTimes(1);
    expect(screen.getByText(/Прежняя попытка уже добавила документ/)).toBeTruthy();
    expect(api.retireCaseDocumentUpload).toHaveBeenCalledTimes(1);
  });
  it('clears a failed document-list warning after successful read recovery without another retirement POST', async () => {
    api.getPendingCaseDocumentUploads.mockResolvedValueOnce({ items: [waiting], nextCursor: null }).mockResolvedValue({ items: [], nextCursor: null });
    api.retireCaseDocumentUpload.mockResolvedValue({ ...waiting, status: 'uploaded', documentId: 601, version: 2 });
    dialog.confirm.mockResolvedValue(true);
    const onPublished = vi.fn().mockRejectedValueOnce(new Error('list read failure')).mockResolvedValue(undefined);
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={onPublished} />); await flush();
    fireEvent.click(screen.getByRole('button', { name: 'Подготовить новую загрузку' })); await flush();
    expect(onPublished).toHaveBeenCalledTimes(2); expect(api.retireCaseDocumentUpload).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('alert')).toBeNull(); expect(screen.getByText(/Прежняя попытка уже добавила документ/)).toBeTruthy();
  });
  it.each(['confirmation', 'POST'])('aborts an old %s when the case changes and ignores late completion', async (stage) => {
    api.getPendingCaseDocumentUploads.mockResolvedValueOnce({ items: [waiting], nextCursor: null })
      .mockResolvedValue({ items: [{ ...waiting, fileName: 'new-case.pdf' }], nextCursor: null });
    let finish!: (value: unknown) => void;
    if (stage === 'confirmation') dialog.confirm.mockImplementation(() => new Promise((resolve) => { finish = resolve; }));
    else { dialog.confirm.mockResolvedValue(true); api.retireCaseDocumentUpload.mockImplementation(() => new Promise((resolve) => { finish = resolve; })); }
    const onPublished = vi.fn(); const view = render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={onPublished} />); await flush();
    fireEvent.click(screen.getByRole('button', { name: 'Подготовить новую загрузку' }));
    await flush();
    const signal = stage === 'POST' ? api.retireCaseDocumentUpload.mock.calls[0][2] as AbortSignal : null;
    view.rerender(<CaseDocumentUploadStatusPanel caseId={102} revision={0} onPublished={onPublished} />); await flush();
    await act(async () => finish(stage === 'confirmation' ? true : { ...waiting, status: 'uploaded', documentId: 601, version: 2 }));
    if (signal) expect(signal.aborted).toBe(true); else expect(api.retireCaseDocumentUpload).not.toHaveBeenCalled();
    expect(screen.queryByText(/Прежняя попытка/)).toBeNull(); expect(screen.getByText('new-case.pdf')).toBeTruthy(); expect(onPublished).not.toHaveBeenCalled();
  });
  it('keeps an unconfirmed action error after the read-only refresh and hides names on lost access', async () => {
    api.getPendingCaseDocumentUploads.mockResolvedValueOnce({ items: [waiting], nextCursor: null }).mockRejectedValue(new Error('HTTP 403'));
    api.retireCaseDocumentUpload.mockRejectedValue(new Error('HTTP 403')); dialog.confirm.mockResolvedValue(true);
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={vi.fn()} />); await flush();
    fireEvent.click(screen.getByRole('button', { name: 'Подготовить новую загрузку' })); await flush();
    expect(screen.queryByText('document.pdf')).toBeNull(); expect(screen.getByText('Нет доступа к этой попытке. Обновите состояние загрузок.')).toBeTruthy();
    expect(api.retireCaseDocumentUpload).toHaveBeenCalledTimes(1);
  });
  it('retries a failed document refresh using the receipt without another metadata or upload request', async () => {
    api.getPendingCaseDocumentUploads.mockResolvedValue({ items: [{ ...waiting, status: 'uploaded', documentId: 601, version: 2 }], nextCursor: null });
    const onPublished = vi.fn().mockRejectedValueOnce(new Error('network')).mockResolvedValue(undefined);
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={onPublished} />);
    await flush();
    expect(screen.getByRole('alert')).toBeTruthy();
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(onPublished).toHaveBeenCalledTimes(2);
    expect(api.getPendingCaseDocumentUploads).toHaveBeenCalledTimes(1);
    expect(api.getCaseDocumentUploadStates).not.toHaveBeenCalled();
    expect(screen.getByText('Добавлен')).toBeTruthy();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('does not confirm an uploaded state without its document version', async () => {
    api.getPendingCaseDocumentUploads.mockResolvedValue({ items: [{ ...waiting, status: 'uploaded', documentId: 601, version: null }], nextCursor: null });
    const onPublished = vi.fn();
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={onPublished} />);
    await flush();
    expect(screen.getByText('Не подтверждено')).toBeTruthy();
    expect(screen.queryByText('Добавлен')).toBeNull();
    expect(onPublished).not.toHaveBeenCalled();
  });
  beforeEach(() => {
    const session = documentCopyTestSession(); onTestFinished(() => session.restore());
    vi.useFakeTimers(); api.getCaseDocumentUploadStates.mockReset(); api.getPendingCaseDocumentUploads.mockReset();
    api.retireCaseDocumentUpload.mockReset(); dialog.confirm.mockReset(); dialog.confirm.mockResolvedValue(false);
    // Existing scenarios supply the same discovery data; polling still uses exact-key states.
    api.getPendingCaseDocumentUploads.mockImplementation(async () => ({ items: await api.getCaseDocumentUploadStates(), nextCursor: null }));
  });
  afterEach(() => { cleanup(); vi.useRealTimers(); });

  it('polls a wholly paused page at thirty seconds without rediscovering it', async () => {
    const paused = { ...waiting, status: 'paused' };
    api.getPendingCaseDocumentUploads.mockResolvedValue({ items: [paused], nextCursor: null });
    api.getCaseDocumentUploadStates.mockResolvedValue([paused]);
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={vi.fn()} />);
    await flush();
    await act(async () => { await vi.advanceTimersByTimeAsync(29999); });
    expect(api.getCaseDocumentUploadStates).not.toHaveBeenCalled();
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    expect(api.getCaseDocumentUploadStates).toHaveBeenCalledExactlyOnceWith(101, ['key-1'], expect.any(AbortSignal), { acknowledgePublishedAttempts: true });
    expect(api.getPendingCaseDocumentUploads).toHaveBeenCalledTimes(1);
  });

  it('polls fifty active attempts in one batch and retains terminal rows without polling them', async () => {
    const active = Array.from({ length: 48 }, (_, index) => ({ ...waiting, uploadKey: `active-${index}` }));
    const terminal = [{ ...waiting, uploadKey: 'manual', status: 'manualRetry' }, { ...waiting, uploadKey: 'failed', status: 'failed' }];
    api.getPendingCaseDocumentUploads.mockResolvedValue({ items: [...active, ...terminal], nextCursor: null });
    api.getCaseDocumentUploadStates.mockResolvedValue(active);
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={vi.fn()} />);
    await flush();
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(api.getCaseDocumentUploadStates).toHaveBeenCalledExactlyOnceWith(101, active.map((item) => item.uploadKey), expect.any(AbortSignal), { acknowledgePublishedAttempts: true });
    expect(screen.getByText('Нужен повтор')).toBeTruthy();
    expect(screen.getByText('Не завершено')).toBeTruthy();
    expect(api.getPendingCaseDocumentUploads).toHaveBeenCalledTimes(1);
    expect(screen.getAllByRole('listitem')[0].textContent).toContain('Ожидание');
    expect(screen.getAllByRole('listitem').at(-1)?.textContent).toContain('Не завершено');
  });

  it('refreshes two confirmed versions of one document once and retains both attempts', async () => {
    const attempts = [waiting, { ...waiting, uploadKey: 'key-2' }];
    api.getPendingCaseDocumentUploads.mockResolvedValue({ items: attempts, nextCursor: null });
    api.getCaseDocumentUploadStates.mockResolvedValue(attempts.map((item, index) => ({ ...item, status: 'uploaded', documentId: 601, version: index + 2 })));
    const onPublished = vi.fn().mockResolvedValue(undefined);
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={onPublished} />);
    await flush();
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(onPublished).toHaveBeenCalledTimes(1);
    expect(screen.getAllByText('Добавлен')).toHaveLength(2);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('stops refreshing a confirmed receipt and hides its name when document access is revoked', async () => {
    api.getPendingCaseDocumentUploads.mockResolvedValue({ items: [{ ...waiting, status: 'uploaded', documentId: 601, version: 2 }], nextCursor: null });
    const onPublished = vi.fn().mockRejectedValue(new Error('HTTP 403 Forbidden'));
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={onPublished} />);
    await flush();
    expect(screen.queryByText('document.pdf')).toBeNull();
    expect(screen.getByRole('alert').textContent).toContain('Нет доступа');
    await act(async () => { await vi.advanceTimersByTimeAsync(60000); });
    expect(onPublished).toHaveBeenCalledTimes(1);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('does not retry a late failed refresh after unmounting', async () => {
    api.getPendingCaseDocumentUploads.mockResolvedValue({ items: [{ ...waiting, status: 'uploaded', documentId: 601, version: 2 }], nextCursor: null });
    let reject!: (reason: Error) => void;
    const onPublished = vi.fn<(signal: AbortSignal) => Promise<void>>(() => new Promise<void>((_, fail) => { reject = fail; }));
    const view = render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={onPublished} />);
    await flush();
    const signal = onPublished.mock.calls[0][0];
    view.unmount();
    await act(async () => { reject(new Error('network')); });
    expect(signal.aborted).toBe(true);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('rejects an uploaded receipt with an invalid document id', async () => {
    api.getPendingCaseDocumentUploads.mockResolvedValue({ items: [{ ...waiting, status: 'uploaded', documentId: 0, version: 2 }], nextCursor: null });
    const onPublished = vi.fn();
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={onPublished} />);
    await flush();
    expect(screen.getByText('Не подтверждено')).toBeTruthy();
    expect(onPublished).not.toHaveBeenCalled();
  });

  it('restores pending metadata and confirms publication by exact key before refreshing files', async () => {
    api.getCaseDocumentUploadStates.mockResolvedValueOnce([waiting])
      .mockResolvedValueOnce([{ ...waiting, status: 'uploaded', documentId: 601, version: 2, message: 'Файл добавлен.' }]).mockResolvedValue([]);
    const onPublished = vi.fn().mockResolvedValue(undefined);
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={onPublished} />);
    await flush();
    expect(screen.getByText('document.pdf')).toBeTruthy();
    expect(onPublished).not.toHaveBeenCalled();
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(api.getCaseDocumentUploadStates).toHaveBeenLastCalledWith(101, ['key-1'], expect.any(AbortSignal), { acknowledgePublishedAttempts: true });
    expect(onPublished).toHaveBeenCalledTimes(1);
    expect(screen.getByText('Добавлен')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Обновить загрузки' }));
    await flush();
    expect(onPublished).toHaveBeenCalledTimes(1);
  });

  it('does not treat missing metadata as successful publication', async () => {
    api.getCaseDocumentUploadStates.mockResolvedValueOnce([waiting]).mockResolvedValue([]);
    const onPublished = vi.fn();
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={onPublished} />);
    await flush();
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(screen.getByText(/Не удалось подтвердить состояние/)).toBeTruthy();
    expect(onPublished).not.toHaveBeenCalled();
  });

  it('removes names and stops polling when permission is revoked', async () => {
    api.getCaseDocumentUploadStates.mockResolvedValueOnce([waiting]).mockRejectedValue(new Error('HTTP 403 Forbidden'));
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={vi.fn()} />);
    await flush();
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(screen.queryByText('document.pdf')).toBeNull();
    expect(screen.getByRole('alert').textContent).toContain('Нет доступа');
    const calls = api.getCaseDocumentUploadStates.mock.calls.length;
    await act(async () => { await vi.advanceTimersByTimeAsync(60000); });
    expect(api.getCaseDocumentUploadStates).toHaveBeenCalledTimes(calls);
  });

  it('aborts the request and ignores a late response after closing the card', async () => {
    let finish!: (states: CaseDocumentUploadState[]) => void;
    api.getCaseDocumentUploadStates.mockImplementation(() => new Promise((resolve) => { finish = resolve; }));
    const onPublished = vi.fn();
    const view = render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={onPublished} />);
    const signal = api.getPendingCaseDocumentUploads.mock.calls[0][2] as AbortSignal;
    view.unmount();
    expect(signal.aborted).toBe(true);
    await act(async () => { finish([waiting]); });
    expect(onPublished).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('does not poll or display a block when there are no pending uploads', async () => {
    api.getCaseDocumentUploadStates.mockResolvedValue([]);
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={vi.fn()} />);
    await flush();
    expect(screen.queryByRole('region', { name: 'Состояние ваших загрузок' })).toBeNull();
    await act(async () => { await vi.advanceTimersByTimeAsync(60000); });
    expect(api.getCaseDocumentUploadStates).toHaveBeenCalledTimes(1);
  });

  it('fetches older pending pages only on demand', async () => {
    api.getPendingCaseDocumentUploads.mockResolvedValueOnce({ items: [], nextCursor: 'raw-window-cursor' })
      .mockResolvedValueOnce({ items: [waiting], nextCursor: null });
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={vi.fn()} />);
    await flush();
    fireEvent.click(screen.getByRole('button', { name: 'Следующая страница' }));
    await flush();
    expect(api.getPendingCaseDocumentUploads).toHaveBeenLastCalledWith(101, 'raw-window-cursor', expect.any(AbortSignal));
    expect(screen.getByText('document.pdf')).toBeTruthy();
  });

  it('resets cursor navigation on refresh, revision and task changes', async () => {
    api.getPendingCaseDocumentUploads.mockResolvedValue({ items: [], nextCursor: 'older-cursor' });
    const view = render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={vi.fn()} />);
    await flush();
    fireEvent.click(screen.getByRole('button', { name: 'Следующая страница' }));
    await flush();
    expect(api.getPendingCaseDocumentUploads).toHaveBeenLastCalledWith(101, 'older-cursor', expect.any(AbortSignal));
    fireEvent.click(screen.getByRole('button', { name: 'Обновить загрузки' }));
    await flush();
    expect(api.getPendingCaseDocumentUploads).toHaveBeenLastCalledWith(101, undefined, expect.any(AbortSignal));
    fireEvent.click(screen.getByRole('button', { name: 'Следующая страница' }));
    await flush();
    view.rerender(<CaseDocumentUploadStatusPanel caseId={101} revision={1} onPublished={vi.fn()} />);
    await flush();
    expect(api.getPendingCaseDocumentUploads).toHaveBeenLastCalledWith(101, undefined, expect.any(AbortSignal));
    fireEvent.click(screen.getByRole('button', { name: 'Следующая страница' }));
    await flush();
    view.rerender(<CaseDocumentUploadStatusPanel caseId={202} revision={1} onPublished={vi.fn()} />);
    await flush();
    expect(api.getPendingCaseDocumentUploads).toHaveBeenLastCalledWith(202, undefined, expect.any(AbortSignal));
    expect(screen.getByText('Страница 1')).toBeTruthy();
  });

  it('ignores an old page response after returning to the first page', async () => {
    let finish!: (page: { items: CaseDocumentUploadState[]; nextCursor: string | null }) => void;
    api.getPendingCaseDocumentUploads.mockResolvedValueOnce({ items: [], nextCursor: 'older-cursor' })
      .mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }))
      .mockResolvedValueOnce({ items: [], nextCursor: 'fresh-cursor' });
    const onPublished = vi.fn();
    render(<CaseDocumentUploadStatusPanel caseId={101} revision={0} onPublished={onPublished} />);
    await flush();
    fireEvent.click(screen.getByRole('button', { name: 'Следующая страница' }));
    await flush();
    const oldSignal = api.getPendingCaseDocumentUploads.mock.calls[1][2] as AbortSignal;
    fireEvent.click(screen.getByRole('button', { name: 'Предыдущая страница' }));
    await flush();
    expect(oldSignal.aborted).toBe(true);
    await act(async () => { finish({ items: [{ ...waiting, status: 'uploaded', documentId: 601, version: 2 }], nextCursor: null }); });
    expect(screen.queryByText('document.pdf')).toBeNull();
    expect(onPublished).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Следующая страница' }));
    await flush();
    expect(api.getPendingCaseDocumentUploads).toHaveBeenLastCalledWith(101, 'fresh-cursor', expect.any(AbortSignal));
  });
});
