import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { CaseModal } from './CaseModal';
import { downloadBlob } from '../../utils/downloadBlob';
vi.mock('../../utils/downloadBlob', () => ({ downloadBlob: vi.fn() }));
import { apiService } from '../../services/api';
import type { ClientCase, ClientCaseDocument, ClientCaseRelatedParty } from '../../types';
import { toTimeZoneDateTimeInput } from '../../utils/timeZoneDate';
import { documentCopyTestSession } from '../../test/documentCopySession';

const caseAuth = vi.hoisted(() => ({ userId: 7, roleId: 4 }));
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: caseAuth.userId, role: { id: caseAuth.roleId } } }) }));
let caseReadSession: ReturnType<typeof documentCopyTestSession>;
afterEach(() => caseReadSession?.restore());

const dialogMock = vi.hoisted(() => ({
  confirm: vi.fn(),
  prompt: vi.fn(),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => dialogMock,
}));

vi.mock('../../services/api', () => ({
  getAuthToken: () => 'owned-form-session',
  apiService: {
    retireCaseDocumentUpload: vi.fn(),
    getCaseDocuments: vi.fn(),
    getPendingCaseDocumentUploads: vi.fn(),
    getCaseDocumentUploadStates: vi.fn(),
    getCaseHearingPreparation: vi.fn(),
    updateCaseHearingPreparation: vi.fn(),
    reviewCasePosition: vi.fn(),
    getCasePositionTemplates: vi.fn(),
    createCasePositionTemplate: vi.fn(),
    updateCasePositionTemplate: vi.fn(),
    archiveCasePositionTemplate: vi.fn(),
    confirmNextHearing: vi.fn(),
    getCurrentCaseAppealDecision: vi.fn(),
    createCaseAppealTask: vi.fn(),
    createTaskJournalItem: vi.fn(),
    getCaseDocumentTemplates: vi.fn(),
    getTaskJournalItems: vi.fn(),
    getClientCaseFinanceSummaries: vi.fn(),
    getCourtRecoveries: vi.fn(),
    setCourtRecoveryPayment: vi.fn(),
    removeCourtRecoveryPayment: vi.fn(),
    getLegalEntityCaseFinanceSummaries: vi.fn(),
    markCaseActivity: vi.fn(),
    getUserSetting: vi.fn(),
    getCaseRelatedParties: vi.fn(),
    addCaseRelatedParty: vi.fn(),
    deleteCaseRelatedParty: vi.fn(),
    getContacts: vi.fn(),
    getLegalEntitiesPaged: vi.fn(),
    uploadCaseDocument: vi.fn(),
    generateCaseDocumentTemplate: vi.fn(),
    updateCaseDocument: vi.fn(),
    downloadCaseDocument: vi.fn(),
    deleteCaseDocument: vi.fn(),
  },
}));

function createCase(overrides: Partial<ClientCase> = {}): ClientCase {
  return {
    id: 1,
    clientId: 10,
    title: 'Тестовое дело',
    description: 'Описание',
    status: 'Open',
    createdAt: '2026-04-21T10:00:00Z',
    ...overrides,
  } as ClientCase;
}
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }

describe('CaseModal', () => {
  it.each(['bank', 'fssp'] as const)('case draft boundary: keeps an untouched historical writ destination without a date (%s)', async destination => {
    const onSave = vi.fn();
    render(<CaseModal isOpen onClose={vi.fn()} caseData={createCase({ executiveWritSubmittedAt: null, executiveWritSubmittedTo: destination })} onSave={onSave} />);
    await act(async () => {});
    fireEvent.change(screen.getByLabelText('Название дела'), { target: { value: 'Другое название' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(onSave).toHaveBeenCalled());
    expect(onSave.mock.calls[0][0]).toMatchObject({ title: 'Другое название', executiveWritSubmittedAt: null, executiveWritSubmittedTo: destination, clearExecutiveWritSubmission: false });
  });

  it.each(['metadata', 'business'])('case draft boundary: retains input and its original snapshot on same-case %s refresh', async refresh => {
    const onSave = vi.fn(); const original = createCase({ title: 'Исходное название', nextHearingAt: '2099-12-01T10:00:00Z' });
    const props = { isOpen: true, onClose: vi.fn(), caseData: original, onSave };
    const view = render(<CaseModal {...props} />); await act(async () => {});
    fireEvent.change(screen.getByLabelText('Название дела'), { target: { value: 'Мой черновик' } });
    fireEvent.change(screen.getByLabelText('Следующее заседание'), { target: { value: '2099-12-02T11:30' } });
    view.rerender(<CaseModal {...props} caseData={{ ...original, title: refresh === 'business' ? 'Чужая новая правка' : original.title, positionReviewedAt: '2026-11-03T01:00:00Z', positionReviewedByName: 'Юрист' }} />);
    expect((screen.getByLabelText('Название дела') as HTMLInputElement).value).toBe('Мой черновик');
    expect((screen.getByLabelText('Следующее заседание') as HTMLInputElement).value).toBe('2099-12-02T11:30');
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(onSave).toHaveBeenCalled());
    expect(onSave.mock.calls[0][0].title).toBe('Мой черновик');
    expect(onSave.mock.calls[0][1]).toMatchObject({ id: original.id, title: original.title, nextHearingAt: original.nextHearingAt });
  });

  it('caller lookup retry: a failed responsible catalogue keeps its selected ID and draft while retrying', async () => {
    const retry = vi.fn();
    const props = { isOpen: true, caseData: createCase({ responsibleUserId: 5, responsibleUserName: 'Прежний ответственный' }), onClose: vi.fn(), onSave: vi.fn(),
      responsibles: [], responsiblesLoading: false, responsiblesError: 'Ответственные временно недоступны', onRetryResponsibles: retry };
    const view = render(<CaseModal {...props} />); await act(async () => {});
    fireEvent.change(screen.getByLabelText('Название дела'), { target: { value: 'Сохраняемый черновик дела' } });
    const select = document.querySelector<HTMLSelectElement>('select[aria-label="Ответственный"]')!;
    expect(select.value).toBe('5'); expect(select.selectedOptions[0].textContent).toBe('Прежний ответственный'); expect(select.disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку ответственных' })); expect(retry).toHaveBeenCalledOnce();
    view.rerender(<CaseModal {...props} responsiblesLoading />);
    expect(screen.getByRole('button', { name: 'Повторить загрузку ответственных' })).toHaveProperty('disabled', true);
    expect(screen.getByLabelText('Название дела')).toHaveProperty('value', 'Сохраняемый черновик дела'); expect(props.onSave).not.toHaveBeenCalled();
  });

  it('caller lookup selection: a responsible ID outside the current catalogue cannot replace the saved ID', async () => {
    const save = vi.fn();
    render(<CaseModal isOpen caseData={createCase({ responsibleUserId: 5 })} onClose={vi.fn()} onSave={save} responsibles={[{ id: 7, fullName: 'Разрешённый ответственный' }]} />); await act(async () => {});
    const select = document.querySelector<HTMLSelectElement>('select[aria-label="Ответственный"]')!;
    fireEvent.change(select, { target: { value: '7' } });
    const obsoleteOption = document.createElement('option'); obsoleteOption.value = '99'; obsoleteOption.textContent = 'Прежняя строка'; select.append(obsoleteOption);
    fireEvent.change(select, { target: { value: '99' } });
    fireEvent.keyDown(screen.getByLabelText('Название дела'), { key: 'Enter', ctrlKey: true });
    await waitFor(() => expect(save).toHaveBeenCalledOnce()); expect(save.mock.calls[0][0]).toMatchObject({ responsibleUserId: 7 });
  });
  it.each(['ctrlKey', 'metaKey'] as const)('case draft baseline: %s saves current fields rather than the earlier keyboard snapshot', async key => {
    const onSave = vi.fn(); render(<CaseModal isOpen caseData={createCase({ feeAmount: 10, courtStateFeeAmount: 5 })} onClose={vi.fn()} onSave={onSave} />); await act(async () => {});
    fireEvent.change(screen.getByLabelText('Номер дела'), { target: { value: 'НОВЫЙ-2026' } });
    fireEvent.change(screen.getByLabelText('Сумма гонорара'), { target: { value: '123.45' } });
    fireEvent.change(screen.getByLabelText('Сумма иска'), { target: { value: '12.34' } });
    fireEvent.change(screen.getByLabelText('Присуждено судом'), { target: { value: '56.78' } });
    fireEvent.change(screen.getByLabelText('Госпошлина по делу'), { target: { value: '' } });
    fireEvent.change(screen.getByLabelText('Крайний срок'), { target: { value: '2026-12-03' } });
    fireEvent.change(screen.getByLabelText('Стратегия и план действий'), { target: { value: 'Новая позиция' } });
    fireEvent.keyDown(screen.getByLabelText('Название дела'), { key: 'Enter', [key]: true }); await waitFor(() => expect(onSave).toHaveBeenCalledOnce());
    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({ caseNumber: 'НОВЫЙ-2026', feeAmount: 123.45, courtClaimAmount: 12.34, courtAwardedAmount: 56.78, courtStateFeeAmount: null, clearCourtStateFeeAmount: true, dueDate: '2026-12-03', strategy: 'Новая позиция' }), expect.objectContaining({ id: 1 }));
  });
  it('case draft baseline: keyboard save respects a newly revoked edit permission', async () => {
    const props = { isOpen: true, caseData: createCase(), onClose: vi.fn(), onSave: vi.fn() }; const view = render(<CaseModal {...props} />); await act(async () => {}); view.rerender(<CaseModal {...props} allowEdit={false} />);
    fireEvent.keyDown(screen.getByLabelText('Название дела'), { key: 'Enter', ctrlKey: true }); await act(async () => {}); expect(props.onSave).not.toHaveBeenCalled(); await screen.findByText('Недостаточно прав для редактирования дела.');
  });
  it.each(['save', 'close'] as const)('case draft baseline: keyboard %s uses the current parent callback', async kind => {
    const props = { isOpen: true, caseData: createCase(), onClose: vi.fn(), onSave: vi.fn() }; const latest = { onClose: vi.fn(), onSave: vi.fn() }; const view = render(<CaseModal {...props} />); await act(async () => {}); view.rerender(<CaseModal {...props} {...latest} />);
    fireEvent.keyDown(screen.getByLabelText('Название дела'), kind === 'save' ? { key: 'Enter', metaKey: true } : { key: 'Escape' }); await act(async () => {});
    expect(props.onSave).not.toHaveBeenCalled(); expect(props.onClose).not.toHaveBeenCalled(); expect(kind === 'save' ? latest.onSave : latest.onClose).toHaveBeenCalledOnce();
  });
  it('case draft baseline: rapid keyboard saves dispatch only one pending save', async () => {
    const onSave = vi.fn().mockReturnValue(new Promise<void>(() => {})); render(<CaseModal isOpen caseData={createCase()} onClose={vi.fn()} onSave={onSave} />); await act(async () => {});
    act(() => { fireEvent.keyDown(screen.getByLabelText('Название дела'), { key: 'Enter', ctrlKey: true }); fireEvent.keyDown(screen.getByLabelText('Название дела'), { key: 'Enter', metaKey: true }); }); expect(onSave).toHaveBeenCalledOnce();
  });
  it.each(['success', 'error'] as const)('case draft boundary: closing suppresses the pending save %s', async outcome => {
    const pending = deferred<void>(); const props = { isOpen: true, caseData: createCase(), onClose: vi.fn(), onSave: vi.fn().mockReturnValue(pending.promise) }; render(<CaseModal {...props} />); await act(async () => {});
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' })); await waitFor(() => expect(props.onSave).toHaveBeenCalledOnce()); fireEvent.click(screen.getByRole('button', { name: 'Закрыть' }));
    await act(async () => { if (outcome === 'success') pending.resolve(undefined); else pending.reject(new Error('Прежняя ошибка')); }); expect(props.onClose).toHaveBeenCalledOnce(); expect(screen.queryByText('Прежняя ошибка')).toBeNull();
    fireEvent.keyDown(screen.getByLabelText('Название дела'), { key: 'Enter', ctrlKey: true }); expect(props.onSave).toHaveBeenCalledOnce();
  });
  it.each(['login', 'actor', 'logout'] as const)('case draft boundary: %s prevents a keyboard write before render', async mode => {
    const session = documentCopyTestSession();
    try { const onSave = vi.fn(); render(<CaseModal isOpen caseData={createCase()} onClose={vi.fn()} onSave={onSave} />); await act(async () => {}); session.replace(mode); fireEvent.keyDown(screen.getByLabelText('Название дела'), { key: 'Enter', ctrlKey: true }); await act(async () => {}); expect(onSave).not.toHaveBeenCalled(); }
    finally { session.restore(); }
  });
  it('case draft boundary: a previous case save cannot close the new case or block its save', async () => {
    const pending = deferred<void>(); const props = { isOpen: true, caseData: createCase(), onClose: vi.fn(), onSave: vi.fn().mockReturnValueOnce(pending.promise).mockResolvedValue(undefined) }; const view = render(<CaseModal {...props} />); await act(async () => {});
    fireEvent.keyDown(screen.getByLabelText('Название дела'), { key: 'Enter', ctrlKey: true }); view.rerender(<CaseModal {...props} caseData={createCase({ id: 2, title: 'Другое дело' })} />); await act(async () => pending.resolve(undefined)); expect(props.onClose).not.toHaveBeenCalled();
    fireEvent.keyDown(screen.getByLabelText('Название дела'), { key: 'Enter', ctrlKey: true }); await waitFor(() => expect(props.onSave).toHaveBeenCalledTimes(2)); expect(props.onSave).toHaveBeenLastCalledWith(expect.objectContaining({ title: 'Другое дело' }), expect.objectContaining({ id: 2 }));
  });
  it.each(['sync', 'async'] as const)('case draft boundary: a current %s failure permits one explicit retry', async mode => {
    const onSave = vi.fn().mockResolvedValue(undefined); if (mode === 'sync') onSave.mockImplementationOnce(() => { throw new Error('Повторите сохранение'); }); else onSave.mockRejectedValueOnce(new Error('Повторите сохранение'));
    render(<CaseModal isOpen caseData={createCase()} onClose={vi.fn()} onSave={onSave} />); await act(async () => {}); fireEvent.keyDown(screen.getByLabelText('Название дела'), { key: 'Enter', ctrlKey: true }); await screen.findByText('Повторите сохранение'); expect(onSave).toHaveBeenCalledOnce(); fireEvent.click(screen.getByRole('button', { name: 'Сохранить' })); await waitFor(() => expect(onSave).toHaveBeenCalledTimes(2));
  });
  it('case draft boundary: an existing save error is hidden when edit authority changes', async () => {
    const props = { isOpen: true, caseData: createCase(), onClose: vi.fn(), onSave: vi.fn().mockRejectedValue(new Error('Прежний доступ')) }; const view = render(<CaseModal {...props} />); await act(async () => {}); fireEvent.keyDown(screen.getByLabelText('Название дела'), { key: 'Enter', ctrlKey: true }); await screen.findByText('Прежний доступ'); view.rerender(<CaseModal {...props} allowEdit={false} />); expect(screen.queryByText('Прежний доступ')).toBeNull();
  });
  it.each(['close', 'rights'] as const)('case draft boundary: %s cancels a pending delete confirmation', async mode => {
    const pending = deferred<boolean>(); dialogMock.confirm.mockReturnValue(pending.promise); const props = { isOpen: true, caseData: createCase(), onClose: vi.fn(), onSave: vi.fn(), onDelete: vi.fn() }; const view = render(<CaseModal {...props} />); await act(async () => {}); fireEvent.click(screen.getByRole('button', { name: 'Удалить' })); const signal = dialogMock.confirm.mock.calls[0][0].signal as AbortSignal;
    fireEvent.keyDown(screen.getByLabelText('Название дела'), { key: 'Enter', ctrlKey: true }); expect(props.onSave).not.toHaveBeenCalled();
    if (mode === 'close') fireEvent.click(screen.getByRole('button', { name: 'Закрыть' })); else view.rerender(<CaseModal {...props} allowEdit={false} />);
    expect(signal.aborted).toBe(true); await act(async () => pending.resolve(true)); expect(props.onDelete).not.toHaveBeenCalled();
  });
  it('case draft boundary: an ordinary delete confirms once and closes its own window', async () => {
    const props = { isOpen: true, caseData: createCase(), onClose: vi.fn(), onSave: vi.fn(), onDelete: vi.fn().mockResolvedValue(undefined) }; render(<CaseModal {...props} />); await act(async () => {}); fireEvent.click(screen.getByRole('button', { name: 'Удалить' })); await waitFor(() => expect(props.onClose).toHaveBeenCalledOnce()); expect(props.onDelete).toHaveBeenCalledOnce(); expect(dialogMock.confirm).toHaveBeenCalledOnce();
  });
  it('case draft boundary: current delete cancellation unlocks the save action', async () => {
    dialogMock.confirm.mockResolvedValue(false); const props = { isOpen: true, caseData: createCase(), onClose: vi.fn(), onSave: vi.fn(), onDelete: vi.fn() }; render(<CaseModal {...props} />); await act(async () => {}); fireEvent.click(screen.getByRole('button', { name: 'Удалить' })); await waitFor(() => expect((screen.getByRole('button', { name: 'Сохранить' }) as HTMLButtonElement).disabled).toBe(false)); fireEvent.keyDown(screen.getByLabelText('Название дела'), { key: 'Enter', metaKey: true }); await waitFor(() => expect(props.onSave).toHaveBeenCalledOnce()); expect(props.onDelete).not.toHaveBeenCalled();
  });
  it('case draft boundary: keys outside the case window cannot save or close it', async () => {
    const props = { isOpen: true, caseData: createCase(), onClose: vi.fn(), onSave: vi.fn() }; render(<CaseModal {...props} />); await act(async () => {}); fireEvent.keyDown(window, { key: 'Enter', ctrlKey: true }); fireEvent.keyDown(window, { key: 'Escape' }); await act(async () => {}); expect(props.onSave).not.toHaveBeenCalled(); expect(props.onClose).not.toHaveBeenCalled();
  });
  it.each(['composition', 'repeat'] as const)('case draft boundary: ignores %s keyboard submission', async kind => {
    const onSave = vi.fn(); render(<CaseModal isOpen caseData={createCase()} onClose={vi.fn()} onSave={onSave} />); await act(async () => {}); fireEvent.keyDown(screen.getByLabelText('Название дела'), { key: 'Enter', ctrlKey: true, isComposing: kind === 'composition', repeat: kind === 'repeat' }); await act(async () => {}); expect(onSave).not.toHaveBeenCalled();
  });
  it('case draft boundary: focus starts in the title and stays in the window with Tab', async () => {
    render(<CaseModal isOpen caseData={createCase()} onClose={vi.fn()} onSave={vi.fn()} />); await act(async () => {}); expect(document.activeElement).toBe(screen.getByLabelText('Название дела'));
    const first = screen.getByRole('button', { name: 'Закрыть' }); const last = screen.getByRole('button', { name: 'Сохранить' }); first.focus(); fireEvent.keyDown(first, { key: 'Tab', shiftKey: true }); expect(document.activeElement).toBe(last); fireEvent.keyDown(last, { key: 'Tab' }); expect(document.activeElement).toBe(first);
  });
  it.each(['-1', '12.345'])('case draft boundary: keyboard validation rejects current court amount %s', async amount => {
    const onSave = vi.fn(); render(<CaseModal isOpen caseData={createCase()} onClose={vi.fn()} onSave={onSave} />); await act(async () => {}); fireEvent.change(screen.getByLabelText('Сумма иска'), { target: { value: amount } }); fireEvent.keyDown(screen.getByLabelText('Название дела'), { key: 'Enter', ctrlKey: true }); await screen.findByText('Судебные суммы укажите неотрицательным числом с точностью до копеек.'); expect(onSave).not.toHaveBeenCalled();
  });
  it.each(['Escape', 'Tab', 'Enter'])('case popup boundary: %s in the calendar does not act on the case window', async key => {
    const props = { isOpen: true, caseData: createCase(), onClose: vi.fn(), onSave: vi.fn() }; render(<CaseModal {...props} />); await act(async () => {});
    const trigger = screen.getByRole('button', { name: 'Выбрать: Крайний срок' }); fireEvent.click(trigger); const nextMonth = screen.getByRole('button', { name: 'Следующий месяц' }); nextMonth.focus();
    fireEvent.keyDown(key === 'Escape' ? trigger : nextMonth, { key, ctrlKey: key === 'Enter' }); await act(async () => {}); expect(props.onSave).not.toHaveBeenCalled(); expect(props.onClose).not.toHaveBeenCalled();
    if (key === 'Escape') expect(screen.queryByRole('dialog', { name: 'Выбор даты' })).toBeNull(); else { expect(screen.getByRole('dialog', { name: 'Выбор даты' })).toBeTruthy(); expect(document.activeElement).toBe(nextMonth); }
  });
  it('case popup boundary: a background case cannot receive keyboard save behind another dialog', async () => {
    const first = vi.fn(); const second = vi.fn(); render(<><CaseModal isOpen caseData={createCase()} onClose={vi.fn()} onSave={first} /><CaseModal isOpen caseData={createCase({ id: 2 })} onClose={vi.fn()} onSave={second} /></>); await act(async () => {});
    const titles = screen.getAllByLabelText('Название дела'); fireEvent.keyDown(titles[0], { key: 'Enter', ctrlKey: true }); expect(first).not.toHaveBeenCalled(); expect(second).not.toHaveBeenCalled(); fireEvent.keyDown(titles[1], { key: 'Enter', ctrlKey: true }); await waitFor(() => expect(second).toHaveBeenCalledOnce());
  });
  it.each(['ctrlKey', 'metaKey'] as const)('case date shortcut: %s on a closed date control saves instead of opening the calendar', async modifier => {
    const onSave = vi.fn(); render(<CaseModal isOpen caseData={createCase()} onClose={vi.fn()} onSave={onSave} />); await act(async () => {});
    fireEvent.keyDown(screen.getByRole('button', { name: 'Выбрать: Крайний срок' }), { key: 'Enter', [modifier]: true }); await waitFor(() => expect(onSave).toHaveBeenCalledOnce()); expect(screen.queryByRole('dialog', { name: 'Выбор даты' })).toBeNull();
  });
  it.each(['retired', 'uploaded'] as const)('handles explicit %s proof without resetting unsaved position or transmitting a file', async (outcome) => {
    const pending = { uploadKey: '11111111-1111-4111-8111-111111111111', fileName: 'own.pdf', status: 'waiting' as const,
      documentId: null, version: null, sha256: null, contentType: null, requestedDocumentKey: null, documentKey: null,
      documentType: null, requestedStatus: null, message: 'Ожидаем копии.' };
    const terminal = { ...pending, status: outcome, documentId: outcome === 'uploaded' ? 601 : null, version: outcome === 'uploaded' ? 2 : null };
    vi.mocked(apiService.getPendingCaseDocumentUploads).mockResolvedValueOnce({ items: [pending], nextCursor: null })
      .mockResolvedValue({ items: [terminal], nextCursor: null });
    vi.mocked(apiService.retireCaseDocumentUpload).mockResolvedValue(terminal);
    const onSave = vi.fn(); render(<CaseModal isOpen onClose={vi.fn()} caseData={createCase({ strategy: 'Исходная позиция' })} onSave={onSave} />);
    const button = await screen.findByRole('button', { name: 'Подготовить новую загрузку' });
    fireEvent.change(screen.getByLabelText('Стратегия и план действий'), { target: { value: 'Несохранённая новая позиция' } });
    const reads = vi.mocked(apiService.getCaseDocuments).mock.calls.length;
    fireEvent.click(button);
    await screen.findByText(outcome === 'uploaded' ? /Прежняя попытка уже добавила документ/ : /Прежняя попытка прекращена\. Новый файл/);
    await waitFor(() => expect(apiService.getCaseDocuments).toHaveBeenCalledTimes(reads + (outcome === 'uploaded' ? 1 : 0)));
    expect((screen.getByLabelText('Стратегия и план действий') as HTMLTextAreaElement).value).toBe('Несохранённая новая позиция');
    expect(apiService.retireCaseDocumentUpload).toHaveBeenCalledExactlyOnceWith(1, pending.uploadKey, expect.any(AbortSignal), 'owned-form-session');
    expect(apiService.uploadCaseDocument).not.toHaveBeenCalled(); expect(onSave).not.toHaveBeenCalled();
  });
  it('refreshes confirmed documents without resetting unsaved position or sending another upload', async () => {
    let finish!: (page: Awaited<ReturnType<typeof apiService.getPendingCaseDocumentUploads>>) => void;
    vi.mocked(apiService.getPendingCaseDocumentUploads).mockImplementation(() => new Promise((resolve) => { finish = resolve; }));
    render(<CaseModal isOpen onClose={vi.fn()} caseData={createCase({ strategy: 'Исходная позиция' })} onSave={vi.fn()} />);
    await waitFor(() => expect(apiService.getPendingCaseDocumentUploads).toHaveBeenCalledTimes(1));
    fireEvent.change(screen.getByLabelText('Стратегия и план действий'), { target: { value: 'Несохранённая позиция' } });
    const reads = vi.mocked(apiService.getCaseDocuments).mock.calls.length;
    await act(async () => finish({ items: [{ uploadKey: 'receipt', fileName: 'ready.pdf', status: 'uploaded', documentId: 601,
      version: 2, sha256: null, contentType: null, requestedDocumentKey: null, documentKey: null, documentType: null,
      requestedStatus: null, message: 'Документ добавлен.' }], nextCursor: null }));
    await waitFor(() => expect(apiService.getCaseDocuments).toHaveBeenCalledTimes(reads + 1));
    expect((screen.getByLabelText('Стратегия и план действий') as HTMLTextAreaElement).value).toBe('Несохранённая позиция');
    expect(apiService.getCaseDocuments).toHaveBeenLastCalledWith(1, expect.any(AbortSignal));
    expect(apiService.uploadCaseDocument).not.toHaveBeenCalled();
  });
  beforeEach(() => {
    caseReadSession = documentCopyTestSession(); Object.assign(caseAuth, { userId: 7, roleId: 4 });
    vi.mocked(apiService.retireCaseDocumentUpload).mockReset();
    vi.mocked(apiService.getPendingCaseDocumentUploads).mockReset();
    vi.mocked(apiService.getPendingCaseDocumentUploads).mockResolvedValue({ items: [], nextCursor: null });
    vi.mocked(apiService.getCaseDocumentUploadStates).mockReset();
    vi.mocked(apiService.getCaseDocumentUploadStates).mockResolvedValue([]);
    vi.mocked(downloadBlob).mockClear();
    dialogMock.confirm.mockReset();
    dialogMock.confirm.mockResolvedValue(true);
    dialogMock.prompt.mockReset();
    dialogMock.prompt.mockResolvedValue(null);
    vi.mocked(apiService.getCaseDocuments).mockReset();
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([]);
    vi.mocked(apiService.getCaseHearingPreparation).mockReset();
    vi.mocked(apiService.getCaseHearingPreparation).mockResolvedValue({
      hearingAt: '2099-10-20T15:00:00Z', contractCheckedAt: null, contractCheckedByName: '',
      authorityCheckedAt: null, authorityCheckedByName: '', materialsCheckedAt: null,
      materialsCheckedByName: '', documentIds: [],
    });
    vi.mocked(apiService.updateCaseHearingPreparation).mockReset();
    vi.mocked(apiService.reviewCasePosition).mockReset();
    vi.mocked(apiService.getCasePositionTemplates).mockReset();
    vi.mocked(apiService.getCasePositionTemplates).mockResolvedValue({ items: [], canManageShared: false });
    vi.mocked(apiService.reviewCasePosition).mockResolvedValue({ positionReviewedAt: '2026-09-23T10:00:00Z', positionReviewedByUserId: 7, positionReviewedByName: 'Юрист' });
    vi.mocked(apiService.confirmNextHearing).mockReset();
    vi.mocked(apiService.getCurrentCaseAppealDecision).mockReset();
    vi.mocked(apiService.getCurrentCaseAppealDecision).mockResolvedValue(null);
    vi.mocked(apiService.createCaseAppealTask).mockReset();
    vi.mocked(apiService.createTaskJournalItem).mockReset();
    vi.mocked(apiService.createTaskJournalItem).mockResolvedValue({ id: 91, clientCaseId: 1 } as never);
    vi.mocked(apiService.getTaskJournalItems).mockReset();
    vi.mocked(apiService.getTaskJournalItems).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 25 });
    vi.mocked(apiService.getClientCaseFinanceSummaries).mockReset();
    vi.mocked(apiService.getClientCaseFinanceSummaries).mockResolvedValue([]);
    vi.mocked(apiService.getCourtRecoveries).mockResolvedValue({ awardedAmount: null, receivedAmount: 0, remainingAmount: null, linkedPayments: [], availablePayments: [], hasMoreAvailablePayments: false, hasCompletePaymentVisibility: true });
    vi.mocked(apiService.getLegalEntityCaseFinanceSummaries).mockReset();
    vi.mocked(apiService.getLegalEntityCaseFinanceSummaries).mockResolvedValue([]);
    vi.mocked(apiService.markCaseActivity).mockReset();
    vi.mocked(apiService.markCaseActivity).mockResolvedValue({
      lastViewedAt: '2026-05-18T10:00:00Z',
      lastViewedByUserId: 7,
      lastViewedByUserName: 'Кожеваткин Андрей',
      activityConfirmedAt: null,
      activityConfirmedByUserId: null,
      activityConfirmedByUserName: '',
      inactivityRemindersEnabled: true,
      eventHistory: '',
    });
    vi.mocked(apiService.getUserSetting).mockReset();
    vi.mocked(apiService.getUserSetting).mockResolvedValue(null);
    vi.mocked(apiService.getCaseRelatedParties).mockReset();
    vi.mocked(apiService.getCaseRelatedParties).mockResolvedValue([]);
    vi.mocked(apiService.addCaseRelatedParty).mockReset();
    vi.mocked(apiService.addCaseRelatedParty).mockResolvedValue({
      id: 5,
      clientCaseId: 1,
      sourceType: 'client',
      sourceId: 10,
      clientId: 10,
      displayName: 'Земцов Виктор Александрович',
      role: 'Свидетель',
      organizationName: '',
      phone: '89990000000',
      email: '',
      notes: '',
      createdAt: '2026-05-18T10:00:00Z',
    });
    vi.mocked(apiService.deleteCaseRelatedParty).mockReset();
    vi.mocked(apiService.deleteCaseRelatedParty).mockResolvedValue(undefined);
    vi.mocked(apiService.getContacts).mockReset();
    vi.mocked(apiService.getContacts).mockResolvedValue([]);
    vi.mocked(apiService.getLegalEntitiesPaged).mockReset();
    vi.mocked(apiService.getLegalEntitiesPaged).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 100 });
    vi.mocked(apiService.getCaseDocumentTemplates).mockReset();
    vi.mocked(apiService.getCaseDocumentTemplates).mockResolvedValue([
      { key: 'case-summary', label: 'Справка по делу', documentType: 'Документ', heading: 'Справка по делу' },
      { key: 'claim', label: 'Исковое заявление', documentType: 'Иск', heading: 'Исковое заявление' },
      { key: 'motion', label: 'Ходатайство', documentType: 'Ходатайство', heading: 'Ходатайство' },
    ]);
    vi.mocked(apiService.uploadCaseDocument).mockReset();
    vi.mocked(apiService.uploadCaseDocument).mockResolvedValue({
      id: 2,
      clientCaseId: 1,
      documentKey: 'doc-key',
      version: 1,
      documentType: 'Документ',
      status: 'Подготовлен',
      originalFileName: 'file.pdf',
      contentType: 'application/pdf',
      sizeBytes: 10,
      uploadedByName: 'Тест',
      uploadedAt: '2026-05-18T10:00:00Z',
      isLatestVersion: true,
    });
    vi.mocked(apiService.generateCaseDocumentTemplate).mockReset();
    vi.mocked(apiService.generateCaseDocumentTemplate).mockResolvedValue({
      id: 3,
      clientCaseId: 1,
      documentKey: 'template-claim',
      version: 1,
      documentType: 'Иск',
      status: 'Подготовлен',
      originalFileName: 'Исковое заявление - дело 1.html',
      contentType: 'text/html; charset=utf-8',
      sizeBytes: 128,
      uploadedByName: 'Тест',
      uploadedAt: '2026-05-18T10:00:00Z',
      isLatestVersion: true,
    });
    vi.mocked(apiService.updateCaseDocument).mockReset();
    vi.mocked(apiService.updateCaseDocument).mockResolvedValue({
      id: 1,
      clientCaseId: 1,
      documentKey: 'claim',
      version: 1,
      documentType: 'Иск',
      status: 'Отправлен',
      originalFileName: 'Исковое заявление.pdf',
      contentType: 'application/pdf',
      sizeBytes: 2048,
      uploadedByUserId: 7,
      uploadedByName: 'Кожеваткин Андрей',
      uploadedAt: '2026-05-18T10:00:00Z',
      isLatestVersion: true,
    });
    vi.mocked(apiService.downloadCaseDocument).mockReset();
    vi.mocked(apiService.downloadCaseDocument).mockResolvedValue({ blob: new Blob(['test']), fileName: 'file.pdf' });
    vi.mocked(apiService.deleteCaseDocument).mockReset();
    vi.mocked(apiService.deleteCaseDocument).mockResolvedValue(undefined);
  });

  it('confirms only a saved position and shows who checked it', async () => {
    const onPositionReviewed = vi.fn();
    render(<CaseModal isOpen onClose={vi.fn()} caseData={createCase({ strategy: 'Подать ходатайство' })} onSave={vi.fn()} onPositionReviewed={onPositionReviewed} />);

    fireEvent.click(screen.getByRole('button', { name: 'Подтвердить проверку позиции' }));
    await waitFor(() => expect(apiService.reviewCasePosition).toHaveBeenCalledWith(1, 'Подать ходатайство'));
    expect(onPositionReviewed).toHaveBeenCalledWith(expect.objectContaining({ positionReviewedByName: 'Юрист' }));
    expect(screen.getByText(/Позиция проверена.*Юрист/)).toBeTruthy();
  });

  it('requires saving an edited position before confirming it', () => {
    render(<CaseModal isOpen onClose={vi.fn()} caseData={createCase({ strategy: 'Исходная позиция' })} onSave={vi.fn()} />);

    fireEvent.change(screen.getByLabelText('Стратегия и план действий'), { target: { value: 'Новая позиция' } });
    expect(screen.getByText(/Позиция изменена: сохраните дело/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Подтвердить проверку позиции' }).hasAttribute('disabled')).toBe(true);
    expect(apiService.reviewCasePosition).not.toHaveBeenCalled();
  });

  it('copies a selected position template into the legacy card and saves the edited text', async () => {
    vi.mocked(apiService.getCasePositionTemplates).mockResolvedValue({
      items: [{ id: 5, title: 'Личная позиция', text: 'Изучить материалы', scope: 'personal', updatedAt: '2026-09-25T00:00:00Z', canManage: true }],
      canManageShared: false,
    });
    const onSave = vi.fn().mockResolvedValue(undefined);
    render(<CaseModal isOpen onClose={vi.fn()} caseData={createCase({ strategy: 'Исходная позиция' })} onSave={onSave} />);
    await screen.findByRole('option', { name: 'Личный · Личная позиция' });
    fireEvent.change(screen.getByRole('combobox', { name: 'Шаблон позиции' }), { target: { value: '5' } });
    fireEvent.click(screen.getByRole('button', { name: 'Вставить в позицию' }));
    expect((screen.getByLabelText('Стратегия и план действий') as HTMLTextAreaElement).value).toBe('Исходная позиция\n\nИзучить материалы');
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(onSave).toHaveBeenCalledWith(expect.objectContaining({
      strategy: 'Исходная позиция\n\nИзучить материалы',
    }), expect.objectContaining({ id: 1 })));
  });

  it('does not offer position editing or templates in a read-only legacy card', () => {
    render(<CaseModal isOpen onClose={vi.fn()} caseData={createCase({ strategy: 'Сохранённая позиция' })} onSave={vi.fn()} allowEdit={false} />);

    expect((screen.getByLabelText('Стратегия и план действий') as HTMLTextAreaElement).disabled).toBe(true);
    expect(screen.queryByRole('combobox', { name: 'Шаблон позиции' })).toBeNull();
    expect(apiService.getCasePositionTemplates).not.toHaveBeenCalled();
  });

  it('requires a saved hearing date and shows who confirmed it', async () => {
    const nextHearingAt = new Date(Date.now() + 7 * 86400000).toISOString();
    const onNextHearingConfirmed = vi.fn();
    vi.mocked(apiService.confirmNextHearing).mockResolvedValue({
      nextHearingConfirmedForAt: nextHearingAt,
      nextHearingConfirmedAt: new Date().toISOString(),
      nextHearingConfirmedByUserId: 7,
      nextHearingConfirmedByName: 'Юрист',
    });
    render(<CaseModal isOpen onClose={vi.fn()} caseData={createCase({ nextHearingAt })} onSave={vi.fn()} onNextHearingConfirmed={onNextHearingConfirmed} />);

    const dateInput = screen.getByLabelText('Следующее заседание') as HTMLInputElement;
    const savedInputValue = dateInput.value;
    fireEvent.change(screen.getByLabelText('Следующее заседание'), { target: { value: '2099-01-01T12:00' } });
    expect(screen.getByRole('button', { name: 'Подтвердить дату заседания' }).hasAttribute('disabled')).toBe(true);
    expect(screen.getByText(/сначала сохраните дело/)).toBeTruthy();
    fireEvent.change(dateInput, { target: { value: savedInputValue } });
    fireEvent.click(screen.getByRole('button', { name: 'Подтвердить дату заседания' }));
    await waitFor(() => expect(apiService.confirmNextHearing).toHaveBeenCalledWith(1, nextHearingAt));
    expect(onNextHearingConfirmed).toHaveBeenCalledWith(expect.objectContaining({ nextHearingConfirmedByName: 'Юрист' }));
    expect(screen.getByText(/Дата подтверждена.*Юрист/)).toBeTruthy();
  });

  it('lets a lawyer request authority for an upcoming hearing without editing task details', async () => {
    const nextHearingAt = new Date(Date.now() + 7 * 86400000).toISOString();
    render(<CaseModal isOpen onClose={vi.fn()} caseData={createCase({ nextHearingAt, responsibleUserId: 7 })} onSave={vi.fn()} canCreateTasks />);

    fireEvent.click(screen.getByRole('button', { name: 'Запросить доверенность' }));

    await waitFor(() => expect(apiService.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
      clientCaseId: 1,
      assigneeUserId: 7,
      title: 'Оформить доверенность для участия в заседании',
    })));
    expect(await screen.findByText(/Задача создана/)).toBeTruthy();
  });

  it('applies a custom overlay class when provided', () => {
    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase()}
        onSave={vi.fn()}
        overlayClassName="z-[2300]"
      />,
    );

    const overlay = screen.getByRole('dialog').parentElement;
    expect(overlay).toBeTruthy();
    expect(overlay?.className).toContain('z-[2300]');
  });

  it('uses the default overlay class when no override is provided', () => {
    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase()}
        onSave={vi.fn()}
      />,
    );

    const overlay = screen.getByRole('dialog').parentElement;
    expect(overlay).toBeTruthy();
    expect(overlay?.className).toContain('z-[1000]');
  });

  it('marks view and confirms activity for an opened case', async () => {
    vi.mocked(apiService.markCaseActivity)
      .mockResolvedValueOnce({
        lastViewedAt: '2026-05-18T10:00:00Z',
        lastViewedByUserId: 7,
        lastViewedByUserName: 'Кожеваткин Андрей',
        activityConfirmedAt: null,
        activityConfirmedByUserId: null,
        activityConfirmedByUserName: '',
        inactivityRemindersEnabled: true,
        eventHistory: '',
      })
      .mockResolvedValueOnce({
        lastViewedAt: '2026-05-18T10:05:00Z',
        lastViewedByUserId: 7,
        lastViewedByUserName: 'Кожеваткин Андрей',
        activityConfirmedAt: '2026-05-18T10:05:00Z',
        activityConfirmedByUserId: 7,
        activityConfirmedByUserName: 'Кожеваткин Андрей',
        inactivityRemindersEnabled: true,
        eventHistory: '18.05.2026 10:05: Активность по делу подтверждена.',
      });

    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase({ lastViewedAt: '2026-05-15T10:00:00Z' })}
        onSave={vi.fn()}
      />,
    );

    await waitFor(() => {
      expect(apiService.markCaseActivity).toHaveBeenCalledWith(1, { confirmed: false });
    });

    fireEvent.click(await screen.findByRole('button', { name: 'Подтвердить активность' }));

    await waitFor(() => {
      expect(apiService.markCaseActivity).toHaveBeenLastCalledWith(1, { confirmed: true });
    });
    expect(await screen.findByText(/Подтверждено:/)).toBeTruthy();
  });

  it('adds a related party from the directory', async () => {
    vi.mocked(apiService.getContacts).mockResolvedValue([
      {
        sourceType: 'client',
        category: 'clients',
        sourceId: 10,
        contactType: 'Person',
        roles: ['Client'],
        hasFullProfile: true,
        fullName: 'Земцов Виктор Александрович',
        organizationName: null,
        phones: [],
        emails: [],
        isActive: true,
        syncToPhonebook: true,
      },
    ]);

    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase()}
        onSave={vi.fn()}
      />,
    );

    await waitFor(() => {
      expect(screen.getByText('Земцов Виктор Александрович', { selector: 'option' })).toBeTruthy();
    });

    fireEvent.change(screen.getByLabelText('Связанное лицо'), { target: { value: 'client:10' } });
    fireEvent.change(screen.getByLabelText('Роль связанного участника'), { target: { value: 'Свидетель' } });
    fireEvent.click(screen.getByRole('button', { name: 'Добавить' }));

    await waitFor(() => {
      expect(apiService.addCaseRelatedParty).toHaveBeenCalledWith(1, {
        sourceType: 'client',
        sourceId: 10,
        role: 'Свидетель',
      });
    });
    expect((await screen.findAllByText('Земцов Виктор Александрович')).length).toBeGreaterThan(1);
  });

  it('does not load related party directories when clients and legal entities are hidden', async () => {
    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase()}
        onSave={vi.fn()}
        canUseContactRelatedParties={false}
        canUseLegalEntityRelatedParties={false}
      />,
    );

    expect(await screen.findByText('Справочники клиентов и организаций закрыты для вашей роли.')).toBeTruthy();
    expect(apiService.getContacts).not.toHaveBeenCalled();
    expect(apiService.getLegalEntitiesPaged).not.toHaveBeenCalled();
    expect((screen.getByLabelText('Тип связанного участника') as HTMLSelectElement).disabled).toBe(true);
    expect((screen.getByLabelText('Роль связанного участника') as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Добавить' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('saves registry, deadline and court fields', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);

    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase()}
        onSave={onSave}
        responsibles={[{ id: 7, fullName: 'Кожеваткин Андрей Владимирович' }]}
      />,
    );

    fireEvent.change(screen.getByLabelText('Название дела'), { target: { value: 'Земцов - банкротство' } });
    fireEvent.change(screen.getByLabelText('Номер дела'), { target: { value: ' 2026-001 ' } });
    fireEvent.change(screen.getByLabelText('Тип дела'), { target: { value: 'Банкротство' } });
    fireEvent.change(screen.getByLabelText('Текущий этап'), { target: { value: 'Подготовка заявления' } });
    fireEvent.change(screen.getByLabelText('Ответственный'), { target: { value: '7' } });
    fireEvent.change(screen.getByLabelText('Приоритет'), { target: { value: 'high' } });
    fireEvent.change(screen.getByLabelText('Дата начала'), { target: { value: '2026-05-10' } });
    fireEvent.change(screen.getByLabelText('Крайний срок'), { target: { value: '2026-06-15' } });
    fireEvent.change(screen.getByLabelText('Сумма гонорара'), { target: { value: '150000.50' } });
    fireEvent.change(screen.getByLabelText('Сумма иска'), { target: { value: '200000,50' } });
    expect(screen.getByText('Фактически получено по решению суда')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Присуждено судом'), { target: { value: '175000' } });
    fireEvent.change(screen.getByLabelText('Госпошлина по делу'), { target: { value: '3500.25' } });
    fireEvent.change(screen.getByLabelText('Суд'), { target: { value: 'Арбитражный суд Самарской области' } });
    fireEvent.change(screen.getByLabelText('Номер в суде'), { target: { value: 'А55-12345/2026' } });
    fireEvent.change(screen.getByLabelText('Следующее заседание'), { target: { value: '2026-05-20T12:30' } });
    fireEvent.change(screen.getByLabelText('Ссылка на карточку суда'), { target: { value: 'https://kad.arbitr.ru/card' } });
    fireEvent.change(screen.getByLabelText('Истец / заявитель'), { target: { value: ' Земцов Виктор Александрович ' } });
    fireEvent.change(screen.getByLabelText('ИНН истца'), { target: { value: '123456789012' } });
    fireEvent.change(screen.getByLabelText('ОГРН/ОГРНИП истца'), { target: { value: '123456789012345' } });
    fireEvent.change(screen.getByLabelText('Наша роль в деле'), { target: { value: 'claimant' } });
    fireEvent.change(screen.getByLabelText('Ответчик / должник'), { target: { value: ' Банк Точка ' } });
    fireEvent.change(screen.getByLabelText('ИНН ответчика'), { target: { value: '1234567890' } });
    fireEvent.change(screen.getByLabelText('ОГРН/ОГРНИП ответчика'), { target: { value: '1234567890123' } });
    fireEvent.change(screen.getByLabelText('Третьи лица'), { target: { value: 'ФНС\nСРО' } });
    fireEvent.change(screen.getByLabelText('Представители'), { target: { value: 'Кожеваткин А.В.' } });
    fireEvent.change(screen.getByLabelText('Судья'), { target: { value: ' Иванова И.И. ' } });
    fireEvent.change(screen.getByLabelText('Зал / кабинет'), { target: { value: ' зал 12 ' } });
    fireEvent.change(screen.getByLabelText('Контакты секретаря суда'), { target: { value: ' secretary@example.test ' } });
    fireEvent.change(screen.getByLabelText('Дата подачи заявления'), { target: { value: '2026-05-11' } });
    fireEvent.change(screen.getByLabelText('Дата регистрации в суде'), { target: { value: '2026-05-13' } });
    fireEvent.change(screen.getByLabelText('Дата решения суда'), { target: { value: '2026-04-01' } });
    fireEvent.change(screen.getByLabelText('Дата вступления решения в силу'), { target: { value: '2026-05-06' } });
    fireEvent.change(screen.getByLabelText('Дата получения исполнительного листа'), { target: { value: '2026-05-10' } });
    fireEvent.change(screen.getByLabelText('Дата подачи исполнительного листа'), { target: { value: '2026-05-12' } });
    fireEvent.change(screen.getByLabelText('Куда подан исполнительный лист'), { target: { value: 'fssp' } });
    expect((screen.getByLabelText('Срок апелляции') as HTMLInputElement).value).toBe('2026-05-04');
    expect((screen.getByLabelText('Срок кассации') as HTMLInputElement).value).toBe('2026-07-01');
    fireEvent.change(screen.getByLabelText('Срок апелляции'), { target: { value: '2026-07-01' } });
    fireEvent.change(screen.getByLabelText('Срок подачи ходатайств'), { target: { value: '2026-06-01' } });
    fireEvent.change(screen.getByLabelText('Срок возражений'), { target: { value: '2026-06-05' } });
    fireEvent.change(screen.getByLabelText('Документы по делу'), { target: { value: 'Паспорт, договор' } });
    fireEvent.change(screen.getByLabelText('История событий'), { target: { value: 'Заявление принято' } });
    fireEvent.change(screen.getByLabelText('Стратегия и план действий'), { target: { value: 'Подать ходатайство' } });

    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Земцов - банкротство',
      caseNumber: '2026-001',
      caseType: 'Банкротство',
      stage: 'Подготовка заявления',
      responsibleUserId: 7,
      priority: 'high',
      startDate: '2026-05-10',
      dueDate: '2026-06-15',
      feeAmount: 150000.5,
      courtClaimAmount: 200000.5,
      courtAwardedAmount: 175000,
      courtStateFeeAmount: 3500.25,
      courtName: 'Арбитражный суд Самарской области',
      courtCaseNumber: 'А55-12345/2026',
      courtUrl: 'https://kad.arbitr.ru/card',
      claimant: 'Земцов Виктор Александрович',
      claimantInn: '123456789012',
      claimantOgrn: '123456789012345',
      defendantInn: '1234567890',
      defendantOgrn: '1234567890123',
      ourCourtRole: 'claimant',
      defendant: 'Банк Точка',
      thirdParties: 'ФНС\nСРО',
      representatives: 'Кожеваткин А.В.',
      judgeName: 'Иванова И.И.',
      courtroom: 'зал 12',
      courtSecretaryContacts: 'secretary@example.test',
      courtRegisteredAt: '2026-05-13',
      clearCourtRegisteredAt: false,
      courtDecisionAt: '2026-04-01',
      courtDecisionEffectiveAt: '2026-05-06',
      executiveWritReceivedAt: '2026-05-10',
      executiveWritSubmittedAt: '2026-05-12',
      executiveWritSubmittedTo: 'fssp',
      clearExecutiveWritSubmission: false,
      cassationDeadlineAt: '2026-07-01',
      documentsSummary: 'Паспорт, договор',
      eventHistory: 'Заявление принято',
      strategy: 'Подать ходатайство',
    }), expect.objectContaining({ id: 1 }));
  });

  it('requires and saves a reason when inactivity control is disabled', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    dialogMock.prompt.mockResolvedValueOnce('Работа временно ведётся вне CRM');

    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase({ inactivityRemindersEnabled: true })}
        onSave={onSave}
      />,
    );

    fireEvent.click(screen.getByLabelText('Автоконтроль дела'));

    expect(await screen.findByText('Причина: Работа временно ведётся вне CRM')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({
      inactivityRemindersEnabled: false,
      inactivityRemindersDisabledReason: 'Работа временно ведётся вне CRM',
    }), expect.objectContaining({ id: 1 }));
  });

  it('loads case documents and uploads selected file with type and status', async () => {
    const documents: ClientCaseDocument[] = [
      {
        id: 1,
        clientCaseId: 1,
        documentKey: 'claim',
        version: 1,
        documentType: 'Иск',
        status: 'Подготовлен',
        originalFileName: 'Исковое заявление.pdf',
        contentType: 'application/pdf',
        sizeBytes: 2048,
        uploadedByUserId: 7,
        uploadedByName: 'Кожеваткин Андрей',
        uploadedAt: '2026-05-18T10:00:00Z',
        isLatestVersion: true,
      },
    ];
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue(documents);

    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase()}
        onSave={vi.fn()}
      />,
    );

    expect(await screen.findByText('Исковое заявление.pdf')).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Тип файла дела'), { target: { value: 'Иск' } });
    fireEvent.change(screen.getByLabelText('Статус файла дела'), { target: { value: 'Отправлен' } });

    const file = new File(['claim'], 'new-claim.pdf', { type: 'application/pdf' });
    fireEvent.change(screen.getByLabelText('Загрузить файл дела'), { target: { files: [file] } });

    await waitFor(() => {
      expect(apiService.uploadCaseDocument).toHaveBeenCalledWith(1, file, {
        documentType: 'Иск',
        status: 'Отправлен',
        documentKey: undefined,
      });
    });

    fireEvent.change(screen.getByLabelText('Статус документа Исковое заявление.pdf'), {
      target: { value: 'Отправлен' },
    });

    await waitFor(() => {
      expect(apiService.updateCaseDocument).toHaveBeenCalledWith(1, 1, { status: 'Отправлен' });
    });

    fireEvent.change(screen.getByLabelText('Тип документа Исковое заявление.pdf'), {
      target: { value: 'Решение суда' },
    });

    await waitFor(() => {
      expect(apiService.updateCaseDocument).toHaveBeenCalledWith(1, 1, { documentType: 'Решение суда' });
    });
  });

  it('uploads an executive writ scan directly from the case workflow', async () => {
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([{
      id: 7, clientCaseId: 1, documentKey: 'writ', version: 1,
      documentType: 'Исполнительный лист', status: 'Получен', originalFileName: 'Лист.pdf',
      contentType: 'application/pdf', sizeBytes: 1024, uploadedByName: 'Юрист',
      uploadedAt: '2026-09-27T10:00:00Z', isLatestVersion: true,
    }]);
    render(<CaseModal isOpen onClose={vi.fn()} caseData={createCase()} onSave={vi.fn()} />);
    expect(await screen.findByRole('button', { name: 'Скачать скан Лист.pdf' })).toBeTruthy();

    const file = new File(['writ'], 'Исполнительный лист.pdf', { type: 'application/pdf' });
    fireEvent.change(screen.getByLabelText('Загрузить скан исполнительного листа'), { target: { files: [file] } });

    await waitFor(() => expect(apiService.uploadCaseDocument).toHaveBeenCalledWith(1, file, {
      documentType: 'Исполнительный лист',
      status: 'Получен',
    }));
    await waitFor(() => expect(apiService.getCaseDocuments).toHaveBeenCalledTimes(2));
  });

  it('does not allow a read-only case viewer to upload an executive writ', async () => {
    render(<CaseModal isOpen onClose={vi.fn()} caseData={createCase()} onSave={vi.fn()} allowEdit={false} />);
    expect(await screen.findByText('Скан пока не прикреплён.')).toBeTruthy();
    expect(screen.queryByLabelText('Загрузить скан исполнительного листа')).toBeNull();
  });

  it('clears the writ submission mark without deleting the receipt date', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    render(<CaseModal isOpen onClose={vi.fn()} caseData={createCase({
      executiveWritReceivedAt: '2026-10-03', executiveWritSubmittedAt: '2026-10-04', executiveWritSubmittedTo: 'bank',
    })} onSave={onSave} />);
    fireEvent.change(screen.getByLabelText('Дата подачи исполнительного листа'), { target: { value: '' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(onSave).toHaveBeenCalledWith(expect.objectContaining({
      executiveWritReceivedAt: '2026-10-03',
      executiveWritSubmittedAt: null,
      clearExecutiveWritSubmission: true,
    }), expect.objectContaining({ id: 1 })));
  });

  it('prevents saving a writ submission dated before its receipt', async () => {
    const onSave = vi.fn();
    render(<CaseModal isOpen onClose={vi.fn()} caseData={createCase({ executiveWritReceivedAt: '2026-10-03' })} onSave={onSave} />);
    fireEvent.change(screen.getByLabelText('Дата подачи исполнительного листа'), { target: { value: '2026-10-02' } });
    fireEvent.change(screen.getByLabelText('Куда подан исполнительный лист'), { target: { value: 'bank' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    expect(await screen.findByText('Дата подачи исполнительного листа должна быть не раньше даты получения.')).toBeTruthy();
    expect(onSave).not.toHaveBeenCalled();
  });

  it('shows document upload time in the profile time zone', async () => {
    vi.mocked(apiService.getUserSetting).mockResolvedValue({ key: 'profile.timeZoneId', value: 'Europe/Samara' });
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([
      {
        id: 1,
        clientCaseId: 1,
        documentKey: 'claim',
        version: 1,
        documentType: 'Иск',
        status: 'Подготовлен',
        originalFileName: 'claim.pdf',
        contentType: 'application/pdf',
        sizeBytes: 2048,
        uploadedByUserId: 7,
        uploadedByName: 'Кожеваткин Андрей',
        uploadedAt: '2026-05-18T10:00:00Z',
        isLatestVersion: true,
      },
    ]);

    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase()}
        onSave={vi.fn()}
      />,
    );

    expect(await screen.findByText('claim.pdf')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getAllByText((_text, element) => {
        const value = element?.textContent ?? '';
        return element?.tagName === 'DIV'
          && element.className === 'text-xs text-gray-500'
          && value.includes('18.05.2026')
          && value.includes('14:00');
      }).length).toBeGreaterThan(0);
    });
  });

  it('generates a case document from the selected template', async () => {
    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase()}
        onSave={vi.fn()}
      />,
    );

    fireEvent.change(screen.getByLabelText('Шаблон документа дела'), { target: { value: 'claim' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сформировать' }));

    await waitFor(() => {
      expect(apiService.generateCaseDocumentTemplate).toHaveBeenCalledWith(1, {
        templateKey: 'claim',
        documentType: 'Иск',
        status: 'Подготовлен',
      });
    });
    expect(apiService.getCaseDocuments).toHaveBeenCalledWith(1, expect.any(AbortSignal));
  });

  it('shows finance summary for the current case', async () => {
    vi.mocked(apiService.getClientCaseFinanceSummaries).mockResolvedValue([
      {
        caseId: 1,
        clientId: 10,
        clientName: 'Земцов Виктор Александрович',
        caseTitle: 'Тестовое дело',
        caseStatus: 'Open',
        caseCreatedAt: '2026-04-21T10:00:00Z',
        incomeTotal: 25000,
        incomePaid: 15000,
        incomeOutstanding: 10000,
        incomeCount: 2,
        expenseTotal: 0,
        expensePaid: 0,
        expenseOutstanding: 0,
        expenseCount: 0,
        plannedMargin: 25000,
        paidMargin: 15000,
        outstandingBalance: 10000,
      },
    ]);

    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase({ feeAmount: 30000 })}
        onSave={vi.fn()}
      />,
    );

    expect(await screen.findByText('Финансы по делу')).toBeTruthy();
    expect(apiService.getClientCaseFinanceSummaries).toHaveBeenCalledWith(10, undefined, { signal: expect.any(AbortSignal) });
    expect(screen.getByText('Счета по делу')).toBeTruthy();
    expect(screen.getByText((text) => text.includes('25') && text.includes('000'))).toBeTruthy();
    expect(screen.getByText((text) => text.includes('10') && text.includes('000'))).toBeTruthy();
  });

  it('explains that a limited finance summary is incomplete', async () => {
    vi.mocked(apiService.getClientCaseFinanceSummaries).mockResolvedValue([{
      caseId: 1,
      hasCompletePaymentVisibility: false,
      incomeTotal: 10_000,
      incomePaid: 10_000,
      incomeOutstanding: 0,
      incomeCount: 1,
      expenseTotal: 0,
      expensePaid: 0,
      expenseOutstanding: 0,
      expenseCount: 0,
      plannedMargin: 10_000,
      paidMargin: 10_000,
      outstandingBalance: 0,
    }]);

    render(<CaseModal isOpen onClose={vi.fn()} caseData={createCase()} onSave={vi.fn()} />);

    expect(await screen.findByText(/Показаны только доступные вам платежи/u)).toBeTruthy();
  });

  it('shows tasks linked to the case', async () => {
    vi.mocked(apiService.getTaskJournalItems).mockResolvedValue({
      items: [
        {
          id: 11,
          clientId: 10,
          clientCaseId: 1,
          objectName: 'Земцов - банкротство',
          title: 'Подготовить заявление',
          description: '',
          assigneeUserId: 7,
          assigneeName: 'Кожеваткин Андрей',
          authorUserId: 1,
          authorName: 'Администратор',
          dueAt: '2026-05-20T09:30:00Z',
          plannedDurationMinutes: 60,
          type: 'task',
          priority: 'high',
          status: 'inProgress',
          result: '',
          reminders: ['atDue'],
          channels: ['inApp'],
          tags: [],
          createdAt: '2026-05-18T09:00:00Z',
          updatedAt: '2026-05-18T09:00:00Z',
        },
      ],
      total: 1,
      page: 1,
      pageSize: 25,
    });

    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase()}
        onSave={vi.fn()}
      />,
    );

    expect(await screen.findByText('Подготовить заявление')).toBeTruthy();
    expect(screen.getByText('В работе')).toBeTruthy();
    expect(apiService.getTaskJournalItems).toHaveBeenCalledWith({
      clientCaseId: 1,
      page: 1,
      pageSize: 25,
    }, { signal: expect.any(AbortSignal) });
  });
  it('case read baseline: a late profile timezone cannot replace a manually entered hearing', async () => {
    const original = Intl.DateTimeFormat.prototype.resolvedOptions;
    const spy = vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockImplementation(function (this: Intl.DateTimeFormat) { return { ...original.call(this), timeZone: 'UTC' }; });
    try {
      const pending = deferred<{ key: string; value: string }>(); vi.mocked(apiService.getUserSetting).mockReturnValueOnce(pending.promise);
      const save = vi.fn(); render(<CaseModal isOpen caseData={createCase({ nextHearingAt: '2026-12-01T10:00:00Z' })} onClose={vi.fn()} onSave={save} />);
      fireEvent.change(screen.getByLabelText('Следующее заседание'), { target: { value: '2026-12-03T12:34' } });
      await act(async () => pending.resolve({ key: 'profile.timeZoneId', value: 'Europe/Samara' }));
      expect((screen.getByLabelText('Следующее заседание') as HTMLInputElement).value).toBe('2026-12-03T12:34');
      fireEvent.click(screen.getByRole('button', { name: 'Сохранить' })); await waitFor(() => expect(save).toHaveBeenCalledOnce());
      expect(Date.parse(save.mock.calls[0][0].nextHearingAt)).toBe(Date.UTC(2026, 11, 3, 12, 34));
    } finally { spy.mockRestore(); }
  });
  it('case read baseline: another case window cannot change the first windows timezone conversion', async () => {
    vi.mocked(apiService.getUserSetting).mockResolvedValueOnce({ key: 'profile.timeZoneId', value: 'UTC' }).mockResolvedValueOnce({ key: 'profile.timeZoneId', value: 'Europe/Samara' });
    const first = { isOpen: true, caseData: createCase({ nextHearingAt: '2026-12-01T10:00:00Z' }), onClose: vi.fn(), onSave: vi.fn() };
    const second = { isOpen: true, caseData: createCase({ id: 2, nextHearingAt: '2026-12-02T10:00:00Z' }), onClose: vi.fn(), onSave: vi.fn() };
    const view = render(<><CaseModal {...first} /><CaseModal {...second} /></>); await act(async () => {});
    view.rerender(<><CaseModal {...first} /></>);
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(first.onSave).toHaveBeenCalledOnce()); expect(Date.parse(first.onSave.mock.calls[0][0].nextHearingAt)).toBe(Date.UTC(2026, 11, 1, 10));
  });

  const ownedReadMethods = {
    documents: 'getCaseDocuments', tasks: 'getTaskJournalItems', finance: 'getClientCaseFinanceSummaries',
    parties: 'getCaseRelatedParties', contacts: 'getContacts', entities: 'getLegalEntitiesPaged', templates: 'getCaseDocumentTemplates',
    activity: 'markCaseActivity', timezone: 'getUserSetting',
  } as const;
  function oldReadValue(kind: keyof typeof ownedReadMethods) {
    const marker = 'ПРЕЖНИЕ ДАННЫЕ ЧТЕНИЯ';
    if (kind === 'documents') return [{ id: 91, clientCaseId: 1, originalFileName: marker, documentKey: 'old', version: 1, isLatestVersion: true }];
    if (kind === 'tasks') return { items: [{ id: 91, title: marker, status: 'new', priority: 'normal' }], total: 1, page: 1, pageSize: 25 };
    if (kind === 'finance') return [{ caseId: 1, incomeTotal: 987654321.23, incomePaid: 0 }];
    if (kind === 'parties') return [{ id: 91, clientCaseId: 1, displayName: marker, sourceType: 'client', sourceId: 1 }];
    if (kind === 'contacts') return [{ sourceType: 'client', sourceId: 91, fullName: marker, isActive: true }];
    if (kind === 'entities') return { items: [{ id: 91, shortName: marker, clientsCount: 0 }], total: 1, page: 1, pageSize: 100 };
    if (kind === 'templates') return [{ key: 'old-template', label: marker, heading: marker, documentType: 'Документ' }];
    if (kind === 'activity') return { lastViewedAt: null, eventHistory: marker, inactivityRemindersEnabled: true };
    return { key: 'profile.timeZoneId', value: Intl.DateTimeFormat().resolvedOptions().timeZone === 'Europe/Samara' ? 'UTC' : 'Europe/Samara' };
  }
  it.each(Object.keys(ownedReadMethods) as Array<keyof typeof ownedReadMethods>)('case read boundary: discards an old %s response after case replacement', async kind => {
    const pending = deferred<unknown>(); vi.mocked(apiService[ownedReadMethods[kind]]).mockReturnValueOnce(pending.promise as never);
    const props = { isOpen: true, caseData: createCase({ nextHearingAt: '2026-12-01T10:00:00Z' }), onClose: vi.fn(), onSave: vi.fn() };
    const view = render(<CaseModal {...props} />); await act(async () => {});
    view.rerender(<CaseModal {...props} caseData={createCase({ id: 2, title: 'Текущее дело', nextHearingAt: '2026-12-01T10:00:00Z' })} />);
    await act(async () => pending.resolve(oldReadValue(kind)));
    if (kind === 'entities') fireEvent.change(screen.getByLabelText('Тип связанного участника'), { target: { value: 'legalEntity' } });
    expect(document.body.textContent).not.toContain('ПРЕЖНИЕ ДАННЫЕ ЧТЕНИЯ'); expect(document.body.textContent?.replace(/\s/g, '')).not.toContain('987654321');
    expect((screen.getByLabelText('История событий') as HTMLTextAreaElement).value).not.toContain('ПРЕЖНИЕ');
    if (kind === 'timezone') expect((screen.getByLabelText('Следующее заседание') as HTMLInputElement).value).toBe(toTimeZoneDateTimeInput('2026-12-01T10:00:00Z', Intl.DateTimeFormat().resolvedOptions().timeZone));
  });
  it.each(['documents', 'tasks', 'finance'] as const)('case read boundary: discards an old %s error after role replacement', async kind => {
    const pending = deferred<unknown>(); vi.mocked(apiService[ownedReadMethods[kind]]).mockReturnValueOnce(pending.promise as never);
    const props = { isOpen: true, caseData: createCase(), onClose: vi.fn(), onSave: vi.fn() }; const view = render(<CaseModal {...props} />); await act(async () => {});
    caseAuth.roleId++; view.rerender(<CaseModal {...props} />); await act(async () => pending.reject(new Error('HTTP 403 - Прежняя ошибка роли')));
    expect(screen.queryByText(/Прежняя ошибка роли/)).toBeNull();
  });
  it.each(['success', 'error'])('case read boundary: closing before parent unmount ignores pending document %s', async outcome => {
    const pending = deferred<unknown>(); vi.mocked(apiService.getCaseDocuments).mockReturnValueOnce(pending.promise as never);
    const close = vi.fn(); render(<CaseModal isOpen caseData={createCase()} onClose={close} onSave={vi.fn()} />); await act(async () => {});
    const signal = vi.mocked(apiService.getCaseDocuments).mock.calls[0][1]; fireEvent.click(screen.getByRole('button', { name: 'Закрыть' }));
    await act(async () => { if (outcome === 'error') pending.reject(new Error('HTTP 403 - Прежняя ошибка файла')); else pending.resolve(oldReadValue('documents')); });
    expect(close).toHaveBeenCalledOnce(); expect(signal?.aborted).toBe(true); expect(document.body.textContent).not.toMatch(/ПРЕЖНИЕ ДАННЫЕ|Прежняя ошибка файла/);
  });
  it('case read boundary: finance permission removal cancels the read and hides prior values', async () => {
    const pending = deferred<unknown>(); vi.mocked(apiService.getClientCaseFinanceSummaries).mockReturnValueOnce(pending.promise as never);
    const props = { isOpen: true, caseData: createCase(), onClose: vi.fn(), onSave: vi.fn() }; const view = render(<CaseModal {...props} />); await act(async () => {});
    const signal = vi.mocked(apiService.getClientCaseFinanceSummaries).mock.calls[0][2]?.signal;
    view.rerender(<CaseModal {...props} canViewFinance={false} />); await act(async () => pending.resolve(oldReadValue('finance')));
    expect(signal?.aborted).toBe(true); expect(screen.queryByText('Финансы по делу')).toBeNull(); expect(document.body.textContent?.replace(/\s/g, '')).not.toContain('987654321');
  });
  it('case read boundary: a late activity read keeps manually edited history and reminder settings', async () => {
    const pending = deferred<unknown>(); vi.mocked(apiService.markCaseActivity).mockReturnValueOnce(pending.promise as never); dialogMock.prompt.mockResolvedValueOnce('Мой контроль');
    const save = vi.fn(); render(<CaseModal isOpen caseData={createCase()} onClose={vi.fn()} onSave={save} />); await act(async () => {});
    fireEvent.change(screen.getByLabelText('История событий'), { target: { value: 'Моя история' } }); fireEvent.click(screen.getByLabelText('Автоконтроль дела')); await screen.findByText('Причина: Мой контроль');
    await act(async () => pending.resolve({ ...oldReadValue('activity') as object, inactivityRemindersEnabled: true, inactivityRemindersDisabledReason: '' }));
    expect((screen.getByLabelText('История событий') as HTMLTextAreaElement).value).toBe('Моя история'); expect(screen.getByText('Причина: Мой контроль')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' })); await waitFor(() => expect(save).toHaveBeenCalledOnce()); expect(save.mock.calls[0][0]).toMatchObject({ eventHistory: 'Моя история', inactivityRemindersEnabled: false, inactivityRemindersDisabledReason: 'Мой контроль' });
  });
  it('case read boundary: a guest never loads private case collections or marks activity', async () => {
    caseReadSession.replace('logout'); render(<CaseModal isOpen caseData={createCase()} onClose={vi.fn()} onSave={vi.fn()} />); await act(async () => {});
    for (const method of Object.values(ownedReadMethods)) expect(apiService[method]).not.toHaveBeenCalled();
  });
  it('case read boundary: token refresh and the same parent render do not repeat reads', async () => {
    const props = { isOpen: true, caseData: createCase(), onClose: vi.fn(), onSave: vi.fn() }; const view = render(<CaseModal {...props} />); await act(async () => {});
    const before = Object.fromEntries(Object.entries(ownedReadMethods).map(([kind, method]) => [kind, vi.mocked(apiService[method]).mock.calls.length])); caseReadSession.refresh(); view.rerender(<CaseModal {...props} />); await act(async () => {});
    for (const [kind, method] of Object.entries(ownedReadMethods)) expect(vi.mocked(apiService[method]).mock.calls.length).toBe(before[kind]);
  });

  it.each(['success', 'error'])('case read session: a previous login document %s is ignored before context rerender', async outcome => {
    const pending = deferred<unknown>(); vi.mocked(apiService.getCaseDocuments).mockReturnValueOnce(pending.promise as never);
    render(<CaseModal isOpen caseData={createCase()} onClose={vi.fn()} onSave={vi.fn()} />); await act(async () => {}); caseReadSession.replace();
    await act(async () => { if (outcome === 'error') pending.reject(new Error('HTTP 403 - Прежний вход')); else pending.resolve(oldReadValue('documents')); });
    expect(document.body.textContent).not.toMatch(/ПРЕЖНИЕ ДАННЫЕ|Прежний вход/);
  });

  it('case command baseline: late position confirmation cannot notify a different case', async () => {
    const pending = deferred<Awaited<ReturnType<typeof apiService.reviewCasePosition>>>(); vi.mocked(apiService.reviewCasePosition).mockReturnValueOnce(pending.promise);
    const props = { isOpen: true, caseData: createCase({ strategy: 'Сохранённая позиция' }), onClose: vi.fn(), onSave: vi.fn(), onPositionReviewed: vi.fn() };
    const view = render(<CaseModal {...props} />); await act(async () => {}); fireEvent.click(screen.getByRole('button', { name: 'Подтвердить проверку позиции' }));
    expect(apiService.reviewCasePosition).toHaveBeenCalledOnce(); view.rerender(<CaseModal {...props} caseData={createCase({ id: 2, strategy: 'Другая позиция' })} />);
    await act(async () => pending.resolve({ positionReviewedAt: '2026-11-02T10:00:00Z', positionReviewedByUserId: 7, positionReviewedByName: 'ПРЕЖНЯЯ ПОЗИЦИЯ' }));
    expect(props.onPositionReviewed).not.toHaveBeenCalled(); expect(document.body.textContent).not.toContain('ПРЕЖНЯЯ ПОЗИЦИЯ');
  });
  it('case command baseline: old reminder prompt cannot replace a new case setting', async () => {
    const pending = deferred<string | null>(); dialogMock.prompt.mockReturnValueOnce(pending.promise);
    const props = { isOpen: true, caseData: createCase(), onClose: vi.fn(), onSave: vi.fn() }; const view = render(<CaseModal {...props} />); await act(async () => {});
    fireEvent.click(screen.getByRole('checkbox', { name: 'Автоконтроль дела' })); expect(dialogMock.prompt).toHaveBeenCalledOnce();
    view.rerender(<CaseModal {...props} caseData={createCase({ id: 2 })} />); await act(async () => pending.resolve('ПРЕЖНЯЯ ПРИЧИНА'));
    expect((screen.getByRole('checkbox', { name: 'Автоконтроль дела' }) as HTMLInputElement).checked).toBe(true); expect(document.body.textContent).not.toContain('ПРЕЖНЯЯ ПРИЧИНА');
  });

  type DirectCommand = 'position' | 'hearing' | 'activity';
  const commandLabels = { position: 'Подтвердить проверку позиции', hearing: 'Подтвердить дату заседания', activity: 'Подтвердить активность' };
  const commandCase = () => createCase({ strategy: 'Сохранённая позиция', nextHearingAt: '2099-12-03T12:00:00Z' });
  const commandReply = (command: DirectCommand) => command === 'position'
    ? { positionReviewedAt: '2026-11-02T10:00:00Z', positionReviewedByUserId: 7, positionReviewedByName: 'ПРЕЖНИЙ ОТВЕТ' }
    : command === 'hearing' ? { nextHearingConfirmedForAt: '2099-12-03T12:00:00Z', nextHearingConfirmedAt: '2026-11-02T10:00:00Z', nextHearingConfirmedByUserId: 7, nextHearingConfirmedByName: 'ПРЕЖНИЙ ОТВЕТ' }
      : { activityConfirmedAt: '2026-11-02T10:00:00Z', activityConfirmedByUserId: 7, activityConfirmedByUserName: 'ПРЕЖНИЙ ОТВЕТ', inactivityRemindersEnabled: true, eventHistory: 'ПРЕЖНИЙ ОТВЕТ' };
  const stubCommand = (command: DirectCommand, promise: Promise<unknown>) => {
    if (command === 'position') vi.mocked(apiService.reviewCasePosition).mockReturnValue(promise as never);
    else if (command === 'hearing') vi.mocked(apiService.confirmNextHearing).mockReturnValue(promise as never);
    else vi.mocked(apiService.markCaseActivity).mockImplementation((_id, payload) => (payload?.confirmed ? promise : Promise.resolve({ inactivityRemindersEnabled: true })) as never);
  };
  const commandCount = (command: DirectCommand) => command === 'position' ? vi.mocked(apiService.reviewCasePosition).mock.calls.length
    : command === 'hearing' ? vi.mocked(apiService.confirmNextHearing).mock.calls.length
      : vi.mocked(apiService.markCaseActivity).mock.calls.filter(([, payload]) => payload?.confirmed).length;

  it.each((['position', 'hearing', 'activity'] as const).flatMap(command => (['case', 'role', 'rights', 'close', 'login', 'unmount'] as const).flatMap(change => ['success', 'error'].map(outcome => ({ command, change, outcome })))))('case command boundary: $command ignores $outcome after $change', async ({ command, change, outcome }) => {
    const pending = deferred<unknown>(); stubCommand(command, pending.promise);
    const props = { isOpen: true, caseData: commandCase(), onClose: vi.fn(), onSave: vi.fn(), onPositionReviewed: vi.fn(), onNextHearingConfirmed: vi.fn() };
    const view = render(<CaseModal {...props} />); await act(async () => {}); fireEvent.click(screen.getByRole('button', { name: commandLabels[command] })); expect(commandCount(command)).toBe(1);
    if (change === 'case') view.rerender(<CaseModal {...props} caseData={{ ...props.caseData, id: 2 }} />);
    else if (change === 'role') { caseAuth.roleId++; view.rerender(<CaseModal {...props} />); }
    else if (change === 'rights') view.rerender(<CaseModal {...props} allowEdit={false} />);
    else if (change === 'close') fireEvent.click(screen.getByRole('button', { name: 'Закрыть' }));
    else if (change === 'login') caseReadSession.replace();
    else view.unmount();
    await act(async () => { if (outcome === 'success') pending.resolve(commandReply(command)); else pending.reject(new Error('ПРЕЖНЯЯ ОШИБКА')); });
    expect(props.onPositionReviewed).not.toHaveBeenCalled(); expect(props.onNextHearingConfirmed).not.toHaveBeenCalled(); expect(document.body.textContent).not.toMatch(/ПРЕЖНИЙ ОТВЕТ|ПРЕЖНЯЯ ОШИБКА/); expect(props.onSave).not.toHaveBeenCalled();
  });
  it.each(['position', 'hearing', 'activity'] as const)('case command boundary: %s owns one operation and excludes save/delete until settled', async command => {
    const pending = deferred<unknown>(); stubCommand(command, pending.promise);
    const props = { isOpen: true, caseData: commandCase(), onClose: vi.fn(), onSave: vi.fn(), onDelete: vi.fn() };
    render(<CaseModal {...props} />); await act(async () => {}); const button = screen.getByRole('button', { name: commandLabels[command] });
    act(() => { fireEvent.click(button); fireEvent.click(button); fireEvent.keyDown(screen.getByLabelText('Название дела'), { key: 'Enter', ctrlKey: true }); fireEvent.click(screen.getByRole('button', { name: 'Удалить' })); });
    expect(commandCount(command)).toBe(1); expect(props.onSave).not.toHaveBeenCalled(); expect(props.onDelete).not.toHaveBeenCalled(); expect(dialogMock.confirm).not.toHaveBeenCalled();
    await act(async () => pending.resolve(commandReply(command))); expect((screen.getByRole('button', { name: 'Сохранить' }) as HTMLButtonElement).disabled).toBe(false);
  });
  it.each(['position', 'hearing', 'activity'] as const)('case command boundary: %s current failure preserves draft and permits explicit retry', async command => {
    const pending = deferred<unknown>(); stubCommand(command, pending.promise); render(<CaseModal isOpen caseData={commandCase()} onSave={vi.fn()} onClose={vi.fn()} />); await act(async () => {});
    fireEvent.change(screen.getByLabelText('Название дела'), { target: { value: 'Ручной черновик' } }); fireEvent.click(screen.getByRole('button', { name: commandLabels[command] }));
    await act(async () => pending.reject(new Error('Доступ к действию отклонён'))); await screen.findByText(/Доступ к действию отклонён/); expect(screen.getByLabelText('Название дела')).toHaveProperty('value', 'Ручной черновик');
    stubCommand(command, Promise.resolve(commandReply(command))); fireEvent.click(screen.getByRole('button', { name: commandLabels[command] })); await waitFor(() => expect(commandCount(command)).toBe(2)); await act(async () => {}); expect(screen.queryByText(/Доступ к действию отклонён/)).toBeNull();
  });
  it.each(['position', 'hearing', 'activity'] as const)('case command boundary: %s cannot start from an obsolete login before render', async command => {
    render(<CaseModal isOpen caseData={commandCase()} onSave={vi.fn()} onClose={vi.fn()} />); await act(async () => {}); caseReadSession.replace();
    fireEvent.click(screen.getByRole('button', { name: commandLabels[command] })); await act(async () => {}); expect(commandCount(command)).toBe(0);
  });
  it.each(['case', 'role', 'rights', 'close', 'unmount'] as const)('case command prompt: %s aborts pending reason and ignores late acceptance', async change => {
    const pending = deferred<string | null>(); dialogMock.prompt.mockReturnValueOnce(pending.promise);
    const props = { isOpen: true, caseData: commandCase(), onClose: vi.fn(), onSave: vi.fn() }; const view = render(<CaseModal {...props} />); await act(async () => {});
    fireEvent.click(screen.getByRole('checkbox', { name: 'Автоконтроль дела' })); const signal = dialogMock.prompt.mock.calls[0][0].signal as AbortSignal; expect(signal).toBeInstanceOf(AbortSignal);
    if (change === 'case') view.rerender(<CaseModal {...props} caseData={{ ...props.caseData, id: 2 }} />);
    else if (change === 'role') { caseAuth.roleId++; view.rerender(<CaseModal {...props} />); }
    else if (change === 'rights') view.rerender(<CaseModal {...props} allowEdit={false} />);
    else if (change === 'close') fireEvent.click(screen.getByRole('button', { name: 'Закрыть' })); else view.unmount();
    expect(signal.aborted).toBe(true); await act(async () => pending.resolve('ПРЕЖНЯЯ ПРИЧИНА')); expect(document.body.textContent).not.toContain('ПРЕЖНЯЯ ПРИЧИНА'); expect(props.onSave).not.toHaveBeenCalled();
  });
  it('case command prompt: cancellation/blank/rejection unlock and preserve the existing draft', async () => {
    dialogMock.prompt.mockResolvedValueOnce(null).mockResolvedValueOnce('   ').mockRejectedValueOnce(new Error('Диалог недоступен')).mockResolvedValueOnce('  Согласованная причина  ');
    const props = { isOpen: true, caseData: commandCase(), onClose: vi.fn(), onSave: vi.fn() }; render(<CaseModal {...props} />); await act(async () => {});
    const toggle = screen.getByRole('checkbox', { name: 'Автоконтроль дела' }); fireEvent.change(screen.getByLabelText('Название дела'), { target: { value: 'Ручной черновик' } });
    fireEvent.click(toggle); await act(async () => {}); expect(toggle).toHaveProperty('checked', true);
    fireEvent.click(toggle); await screen.findByText('Укажите причину выключения автоконтроля.'); expect(toggle).toHaveProperty('checked', true);
    fireEvent.click(toggle); await screen.findByText('Диалог недоступен'); expect(toggle).toHaveProperty('checked', true);
    fireEvent.click(toggle); await screen.findByText('Причина: Согласованная причина'); expect(toggle).toHaveProperty('checked', false); expect(screen.getByLabelText('Название дела')).toHaveProperty('value', 'Ручной черновик');
    fireEvent.click(toggle); expect(toggle).toHaveProperty('checked', true); expect(screen.queryByText(/Причина: Согласованная причина/)).toBeNull(); expect(props.onSave).not.toHaveBeenCalled();
  });
  it('case command prompt: pending prompt excludes other confirmations and save', async () => {
    const pending = deferred<string | null>(); dialogMock.prompt.mockReturnValueOnce(pending.promise); const onSave = vi.fn(); render(<CaseModal isOpen caseData={commandCase()} onSave={onSave} onClose={vi.fn()} />); await act(async () => {});
    const toggle = screen.getByRole('checkbox', { name: 'Автоконтроль дела' }); act(() => { fireEvent.click(toggle); fireEvent.click(toggle); fireEvent.click(screen.getByRole('button', { name: commandLabels.position })); fireEvent.click(screen.getByRole('button', { name: commandLabels.activity })); fireEvent.keyDown(screen.getByLabelText('Название дела'), { key: 'Enter', metaKey: true }); });
    expect(dialogMock.prompt).toHaveBeenCalledOnce(); expect(apiService.reviewCasePosition).not.toHaveBeenCalled(); expect(commandCount('activity')).toBe(0); expect(onSave).not.toHaveBeenCalled(); await act(async () => pending.resolve(null));
  });
  it('case command permissions: read-only activity remains available, edit commands and settings stay unavailable', async () => {
    stubCommand('activity', Promise.resolve(commandReply('activity'))); render(<CaseModal isOpen allowEdit={false} caseData={commandCase()} onSave={vi.fn()} onClose={vi.fn()} />); await act(async () => {});
    expect(screen.queryByRole('button', { name: commandLabels.position })).toBeNull(); expect(screen.queryByRole('button', { name: commandLabels.hearing })).toBeNull(); expect(screen.getByRole('checkbox', { name: 'Автоконтроль дела' })).toHaveProperty('disabled', true);
    fireEvent.click(screen.getByRole('button', { name: commandLabels.activity })); await screen.findByText(/Подтверждено:.*ПРЕЖНИЙ ОТВЕТ/); expect(commandCount('activity')).toBe(1); expect(dialogMock.prompt).not.toHaveBeenCalled();
  });
  it.each(['position', 'hearing'] as const)('case command concurrency: %s ignores confirmation of changed saved content in the same case', async command => {
    const pending = deferred<unknown>(); stubCommand(command, pending.promise); const props = { isOpen: true, caseData: commandCase(), onSave: vi.fn(), onClose: vi.fn(), onPositionReviewed: vi.fn(), onNextHearingConfirmed: vi.fn() }; const view = render(<CaseModal {...props} />); await act(async () => {});
    fireEvent.click(screen.getByRole('button', { name: commandLabels[command] })); view.rerender(<CaseModal {...props} caseData={{ ...props.caseData, strategy: 'Обновлённая позиция', nextHearingAt: '2099-12-04T12:00:00Z' }} />); await act(async () => pending.resolve(commandReply(command)));
    expect(props.onPositionReviewed).not.toHaveBeenCalled(); expect(props.onNextHearingConfirmed).not.toHaveBeenCalled(); expect(document.body.textContent).not.toContain('ПРЕЖНИЙ ОТВЕТ');
  });
  it('case command validation: a confirmation for another saved hearing does not notify the parent', async () => {
    const props = { isOpen: true, caseData: commandCase(), onSave: vi.fn(), onClose: vi.fn(), onNextHearingConfirmed: vi.fn() }; vi.mocked(apiService.confirmNextHearing).mockResolvedValue({ ...commandReply('hearing'), nextHearingConfirmedForAt: '2099-12-04T12:00:00Z' } as never);
    render(<CaseModal {...props} />); await act(async () => {}); fireEvent.click(screen.getByRole('button', { name: commandLabels.hearing })); await screen.findByText('Дата заседания изменилась или не подтверждена. Обновите карточку дела.'); expect(props.onNextHearingConfirmed).not.toHaveBeenCalled();
  });
  it('case command validation: malformed review does not notify the parent and permits retry', async () => {
    vi.mocked(apiService.reviewCasePosition).mockResolvedValue({ positionReviewedAt: 'invalid', positionReviewedByName: 'Чужой ответ' } as never); const onPositionReviewed = vi.fn(); render(<CaseModal isOpen caseData={commandCase()} onSave={vi.fn()} onClose={vi.fn()} onPositionReviewed={onPositionReviewed} />); await act(async () => {});
    fireEvent.click(screen.getByRole('button', { name: commandLabels.position })); await screen.findByText('Не удалось подтвердить сведения о проверке позиции.'); expect(onPositionReviewed).not.toHaveBeenCalled(); expect(screen.getByRole('button', { name: commandLabels.position })).toHaveProperty('disabled', false);
  });

  const actionFile = (): ClientCaseDocument => ({ id: 41, clientCaseId: 1, documentKey: 'file-41', version: 1, documentType: 'Документ', status: 'Подготовлен', originalFileName: 'Файл проверки.pdf', contentType: 'application/pdf', sizeBytes: 10, uploadedByName: '', uploadedAt: '2026-11-02T00:00:00Z', isLatestVersion: true });
  it('case file baseline: edit revocation during confirmation prevents a file delete', async () => {
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([actionFile()]); const pending = deferred<boolean>(); dialogMock.confirm.mockReturnValueOnce(pending.promise); const props = { isOpen: true, caseData: createCase(), onClose: vi.fn(), onSave: vi.fn() };const view=render(<CaseModal {...props} />); await screen.findByText('Файл проверки.pdf');
    fireEvent.click(screen.getByRole('button', { name: 'Удалить Файл проверки.pdf' })); expect(dialogMock.confirm).toHaveBeenCalledOnce();view.rerender(<CaseModal {...props} allowEdit={false} />);await act(async()=>pending.resolve(true));expect(apiService.deleteCaseDocument).not.toHaveBeenCalled();
  });
  it('case file baseline: a late download cannot start after changing role', async () => {
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([actionFile()]);const pending=deferred<{blob:Blob;fileName:string}>();vi.mocked(apiService.downloadCaseDocument).mockReturnValueOnce(pending.promise);const props={isOpen:true,caseData:createCase(),onClose:vi.fn(),onSave:vi.fn()};const view=render(<CaseModal {...props}/>);await screen.findByText('Файл проверки.pdf');fireEvent.click(screen.getByRole('button',{name:'Скачать Файл проверки.pdf'}));caseAuth.roleId++;view.rerender(<CaseModal {...props}/>);await act(async()=>pending.resolve({blob:new Blob(['late']),fileName:'old.pdf'}));expect(downloadBlob).not.toHaveBeenCalled();
  });
  it('case party baseline: a forged choice outside the current catalogue cannot create a link', async () => {
    render(<CaseModal isOpen caseData={createCase()} onClose={vi.fn()} onSave={vi.fn()}/>);await act(async()=>{});const select=screen.getByLabelText('Связанное лицо');const option=document.createElement('option');option.value='client:999';option.textContent='Прежний клиент';select.append(option);fireEvent.change(select,{target:{value:'client:999'}});fireEvent.click(screen.getByRole('button',{name:'Добавить'}));await act(async()=>{});expect(apiService.addCaseRelatedParty).not.toHaveBeenCalled();
  });

  type FileCommand = 'upload' | 'generate' | 'download' | 'delete' | 'update';
  const fileCommandMock = (kind: FileCommand) => kind === 'upload' ? vi.mocked(apiService.uploadCaseDocument) : kind === 'generate' ? vi.mocked(apiService.generateCaseDocumentTemplate) : kind === 'download' ? vi.mocked(apiService.downloadCaseDocument) : kind === 'delete' ? vi.mocked(apiService.deleteCaseDocument) : vi.mocked(apiService.updateCaseDocument);
  const fileCommandReply = (kind: FileCommand) => kind === 'download' ? { blob: new Blob(['contents']), fileName: 'current.pdf' } : kind === 'delete' ? undefined : actionFile();
  const startFileCommand = (kind: FileCommand) => {
    if (kind === 'upload') fireEvent.change(screen.getByLabelText('Загрузить файл дела'), { target: { files: [new File(['x'], 'file.pdf', { type: 'application/pdf' })] } });
    else if (kind === 'generate') fireEvent.click(screen.getByRole('button', { name: 'Сформировать' }));
    else if (kind === 'update') fireEvent.change(screen.getByLabelText('Статус документа Файл проверки.pdf'), { target: { value: 'Отправлен' } });
    else fireEvent.click(screen.getByRole('button', { name: kind === 'download' ? 'Скачать Файл проверки.pdf' : 'Удалить Файл проверки.pdf' }));
  };
  it.each((['upload', 'generate', 'download', 'delete', 'update'] as const).flatMap(kind => (['case', 'role', 'close', 'login'] as const).flatMap(change => ['success', 'error'].map(outcome => ({ kind, change, outcome })))))('case file boundary: $kind ignores $outcome after $change without old followups', async ({ kind, change, outcome }) => {
    const pending = deferred<unknown>(); fileCommandMock(kind).mockReturnValueOnce(pending.promise as never); vi.mocked(apiService.getCaseDocuments).mockImplementation(async id => [{ ...actionFile(), clientCaseId: id }]);
    const props = { isOpen: true, caseData: createCase(), onClose: vi.fn(), onSave: vi.fn() }; const view = render(<CaseModal {...props} />); await screen.findByText('Файл проверки.pdf'); await act(async () => {}); startFileCommand(kind); await waitFor(() => expect(fileCommandMock(kind)).toHaveBeenCalledOnce());
    const signal = kind === 'download' ? vi.mocked(apiService.downloadCaseDocument).mock.calls[0][2]?.signal : undefined;
    if (change === 'case') view.rerender(<CaseModal {...props} caseData={createCase({ id: 2, title: 'Новое дело' })} />);
    else if (change === 'role') { caseAuth.roleId++; view.rerender(<CaseModal {...props} />); }
    else if (change === 'close') fireEvent.click(screen.getByRole('button', { name: 'Закрыть' })); else caseReadSession.replace();
    await act(async () => {}); const reads = vi.mocked(apiService.getCaseDocuments).mock.calls.length;
    if (signal && change !== 'login') expect(signal.aborted).toBe(true);
    await act(async () => { if (outcome === 'error') pending.reject(new Error('ПРЕЖНЯЯ ФАЙЛОВАЯ ОШИБКА')); else pending.resolve(fileCommandReply(kind)); });
    expect(apiService.getCaseDocuments).toHaveBeenCalledTimes(reads); expect(downloadBlob).not.toHaveBeenCalled(); expect(document.body.textContent).not.toContain('ПРЕЖНЯЯ ФАЙЛОВАЯ ОШИБКА'); expect(props.onSave).not.toHaveBeenCalled();
  });
  it.each(['upload', 'generate', 'download', 'delete', 'update'] as const)('case file serialization: %s dispatches once and excludes save and activity', async kind => {
    const pending = deferred<unknown>(); fileCommandMock(kind).mockReturnValue(pending.promise as never); vi.mocked(apiService.getCaseDocuments).mockResolvedValue([actionFile()]); const props = { isOpen: true, caseData: createCase(), onClose: vi.fn(), onSave: vi.fn() }; render(<CaseModal {...props} />); await screen.findByText('Файл проверки.pdf'); await act(async () => {});
    act(() => { startFileCommand(kind); startFileCommand(kind); fireEvent.keyDown(screen.getByLabelText('Название дела'), { key: 'Enter', ctrlKey: true }); fireEvent.click(screen.getByRole('button', { name: 'Подтвердить активность' })); }); await waitFor(() => expect(fileCommandMock(kind)).toHaveBeenCalledOnce()); expect(props.onSave).not.toHaveBeenCalled(); expect(vi.mocked(apiService.markCaseActivity).mock.calls.some(([, payload]) => payload?.confirmed)).toBe(false);
    const reads = vi.mocked(apiService.getCaseDocuments).mock.calls.length; await act(async () => pending.resolve(fileCommandReply(kind))); expect(apiService.getCaseDocuments).toHaveBeenCalledTimes(reads + (kind === 'download' ? 0 : 1)); expect(downloadBlob).toHaveBeenCalledTimes(kind === 'download' ? 1 : 0); expect(screen.getByRole('button', { name: 'Сохранить' })).toHaveProperty('disabled', false);
  });
  it.each(['upload', 'generate', 'download', 'delete', 'update'] as const)('case file failure: %s retains draft and permits one explicit retry', async kind => {
    const pending = deferred<unknown>(); fileCommandMock(kind).mockReturnValueOnce(pending.promise as never); vi.mocked(apiService.getCaseDocuments).mockResolvedValue([actionFile()]); render(<CaseModal isOpen caseData={createCase()} onClose={vi.fn()} onSave={vi.fn()} />); await screen.findByText('Файл проверки.pdf'); await act(async () => {});
    fireEvent.change(screen.getByLabelText('Название дела'), { target: { value: 'Ручной черновик файла' } }); startFileCommand(kind); await waitFor(() => expect(fileCommandMock(kind)).toHaveBeenCalledOnce()); await act(async () => pending.reject(new Error('Файловая операция отклонена'))); await screen.findByText('Файловая операция отклонена'); expect(screen.getByLabelText('Название дела')).toHaveProperty('value', 'Ручной черновик файла');
    fileCommandMock(kind).mockResolvedValueOnce(fileCommandReply(kind) as never); startFileCommand(kind); await waitFor(() => expect(fileCommandMock(kind)).toHaveBeenCalledTimes(2)); await act(async () => {}); expect(screen.queryByText('Файловая операция отклонена')).toBeNull();
  });
  it.each(['upload', 'generate', 'download', 'delete', 'update'] as const)('case file session: %s cannot start after a login change before render', async kind => {
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([actionFile()]); render(<CaseModal isOpen caseData={createCase()} onClose={vi.fn()} onSave={vi.fn()} />); await screen.findByText('Файл проверки.pdf'); await act(async () => {}); caseReadSession.replace(); startFileCommand(kind); await act(async () => {}); expect(fileCommandMock(kind)).not.toHaveBeenCalled();
  });
  it.each(['case', 'rights', 'close', 'unmount'] as const)('case file confirmation: %s cancels deletion before dispatch', async change => {
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([actionFile()]); const pending = deferred<boolean>(); dialogMock.confirm.mockReturnValueOnce(pending.promise); const props = { isOpen: true, caseData: createCase(), onClose: vi.fn(), onSave: vi.fn() }; const view = render(<CaseModal {...props} />); await screen.findByText('Файл проверки.pdf'); startFileCommand('delete'); const signal = dialogMock.confirm.mock.calls[0][0].signal as AbortSignal;
    if (change === 'case') view.rerender(<CaseModal {...props} caseData={createCase({ id: 2 })} />); else if (change === 'rights') view.rerender(<CaseModal {...props} allowEdit={false} />); else if (change === 'close') fireEvent.click(screen.getByRole('button', { name: 'Закрыть' })); else view.unmount(); expect(signal.aborted).toBe(true); await act(async () => pending.resolve(true)); expect(apiService.deleteCaseDocument).not.toHaveBeenCalled();
  });
  it('case file permissions: read-only viewer can download but cannot mutate files', async () => {
    vi.mocked(apiService.getCaseDocuments).mockResolvedValue([actionFile()]); render(<CaseModal isOpen allowEdit={false} caseData={createCase()} onClose={vi.fn()} onSave={vi.fn()} />); await screen.findByText('Файл проверки.pdf'); expect(screen.getByLabelText('Статус документа Файл проверки.pdf')).toHaveProperty('disabled', true); expect(screen.getByLabelText('Удалить Файл проверки.pdf')).toHaveProperty('disabled', true); startFileCommand('download'); await waitFor(() => expect(downloadBlob).toHaveBeenCalledOnce()); expect(apiService.deleteCaseDocument).not.toHaveBeenCalled();
  });
  const actionContact = () => ({ sourceType: 'client', category: 'clients', sourceId: 10, contactType: 'Person', roles: ['Client'], hasFullProfile: true, fullName: 'Текущий участник', phones: [], emails: [], isActive: true, syncToPhonebook: true } as const);
  const actionParty = (): ClientCaseRelatedParty => ({ id: 51, clientCaseId: 1, sourceType: 'client', sourceId: 10, displayName: 'Текущая связь', role: 'Свидетель', createdAt: '2026-11-02T00:00:00Z' });
  const preparePartyAction = (kind: 'add' | 'delete') => { if (kind === 'add') vi.mocked(apiService.getContacts).mockResolvedValue([actionContact()] as never); else vi.mocked(apiService.getCaseRelatedParties).mockImplementation(async id => [{ ...actionParty(), clientCaseId: id }]); };
  const partyCommandMock = (kind: 'add' | 'delete') => kind === 'add' ? vi.mocked(apiService.addCaseRelatedParty) : vi.mocked(apiService.deleteCaseRelatedParty);
  const startPartyAction = async (kind: 'add' | 'delete') => {
    if (kind === 'add') { await screen.findByText('Текущий участник', { selector: 'option' }); fireEvent.change(screen.getByLabelText('Связанное лицо'), { target: { value: 'client:10' } }); fireEvent.click(screen.getByRole('button', { name: 'Добавить' })); }
    else { await screen.findByText('Текущая связь'); fireEvent.click(screen.getByRole('button', { name: 'Удалить' })); }
  };
  it.each((['add', 'delete'] as const).flatMap(kind => (['case', 'rights', 'close', 'login'] as const).flatMap(change => ['success', 'error'].map(outcome => ({ kind, change, outcome })))))('case party boundary: $kind ignores $outcome after $change', async ({ kind, change, outcome }) => {
    preparePartyAction(kind); const pending = deferred<unknown>(); partyCommandMock(kind).mockReturnValueOnce(pending.promise as never); const props = { isOpen: true, caseData: createCase(), onClose: vi.fn(), onSave: vi.fn() }; const view = render(<CaseModal {...props} />); await startPartyAction(kind); await waitFor(() => expect(partyCommandMock(kind)).toHaveBeenCalledOnce());
    if (change === 'case') view.rerender(<CaseModal {...props} caseData={createCase({ id: 2 })} />); else if (change === 'rights') view.rerender(<CaseModal {...props} allowEdit={false} />); else if (change === 'close') fireEvent.click(screen.getByRole('button', { name: 'Закрыть' })); else caseReadSession.replace();
    await act(async () => { if (outcome === 'error') pending.reject(new Error('ПРЕЖНЯЯ ОШИБКА СВЯЗИ')); else pending.resolve(kind === 'add' ? { ...actionParty(), displayName: 'ПРЕЖНЯЯ ДОБАВЛЕННАЯ СВЯЗЬ' } : undefined); }); expect(document.body.textContent).not.toMatch(/ПРЕЖНЯЯ ОШИБКА СВЯЗИ|ПРЕЖНЯЯ ДОБАВЛЕННАЯ СВЯЗЬ/); if (kind === 'delete' && change !== 'case' && change !== 'close') expect(screen.queryByText('Текущая связь')).not.toBeNull(); expect(props.onSave).not.toHaveBeenCalled();
  });
  it.each(['add', 'delete'] as const)('case party serialization: %s reserves before confirmation and excludes other writes', async kind => {
    preparePartyAction(kind); const pending = deferred<unknown>(); partyCommandMock(kind).mockReturnValueOnce(pending.promise as never); const onSave = vi.fn(); render(<CaseModal isOpen caseData={createCase()} onClose={vi.fn()} onSave={onSave} />); await startPartyAction(kind); await waitFor(() => expect(partyCommandMock(kind)).toHaveBeenCalledOnce()); fireEvent.keyDown(screen.getByLabelText('Название дела'), { key: 'Enter', metaKey: true }); fireEvent.click(screen.getByRole('button', { name: 'Подтвердить активность' })); expect(onSave).not.toHaveBeenCalled(); expect(vi.mocked(apiService.markCaseActivity).mock.calls.some(([, p]) => p?.confirmed)).toBe(false);
    await act(async () => pending.resolve(kind === 'add' ? actionParty() : undefined)); expect(screen.getByRole('button', { name: 'Сохранить' })).toHaveProperty('disabled', false); expect(!!screen.queryByText('Текущая связь')).toBe(kind === 'add');
  });
  it.each(['rights', 'close', 'unmount'] as const)('case party confirmation: %s aborts pending deletion without a write', async change => {
    preparePartyAction('delete'); const pending = deferred<boolean>(); dialogMock.confirm.mockReturnValueOnce(pending.promise); const props = { isOpen: true, caseData: createCase(), onClose: vi.fn(), onSave: vi.fn() }; const view = render(<CaseModal {...props} />); await startPartyAction('delete'); const signal = dialogMock.confirm.mock.calls[0][0].signal as AbortSignal;
    if (change === 'rights') view.rerender(<CaseModal {...props} allowEdit={false} />); else if (change === 'close') fireEvent.click(screen.getByRole('button', { name: 'Закрыть' })); else view.unmount(); expect(signal.aborted).toBe(true); await act(async () => pending.resolve(true)); expect(apiService.deleteCaseRelatedParty).not.toHaveBeenCalled();
  });
  it('case party catalogue: current failure has an explicit retry that preserves draft and chosen role', async () => {
    vi.mocked(apiService.getContacts).mockRejectedValueOnce(new Error('Справочник временно недоступен')).mockResolvedValueOnce([actionContact()] as never); const props = { isOpen: true, caseData: createCase(), onClose: vi.fn(), onSave: vi.fn() }; render(<CaseModal {...props} />); await screen.findByText('Справочник временно недоступен'); fireEvent.change(screen.getByLabelText('Название дела'), { target: { value: 'Ручной черновик связи' } }); fireEvent.change(screen.getByLabelText('Роль связанного участника'), { target: { value: 'Эксперт' } }); expect(screen.getByRole('button', { name: 'Добавить' })).toHaveProperty('disabled', true);
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку участников' })); await screen.findByText('Текущий участник', { selector: 'option' }); expect(apiService.getContacts).toHaveBeenCalledTimes(2); expect(screen.getByLabelText('Название дела')).toHaveProperty('value', 'Ручной черновик связи'); expect(screen.getByLabelText('Роль связанного участника')).toHaveProperty('value', 'Эксперт'); expect(props.onSave).not.toHaveBeenCalled(); expect(apiService.addCaseRelatedParty).not.toHaveBeenCalled();
  });
  it('case party validation: a response for another source or case is not adopted', async () => {
    preparePartyAction('add'); vi.mocked(apiService.addCaseRelatedParty).mockResolvedValue({ ...actionParty(), clientCaseId: 2, sourceId: 999, displayName: 'ЧУЖАЯ СВЯЗЬ' }); render(<CaseModal isOpen caseData={createCase()} onClose={vi.fn()} onSave={vi.fn()} />); await startPartyAction('add'); await screen.findByText('Не удалось подтвердить добавленную связь. Обновите список связей перед повторным действием.'); expect(screen.queryByText('ЧУЖАЯ СВЯЗЬ')).toBeNull(); expect(screen.getByLabelText('Связанное лицо')).toHaveProperty('value', 'client:10');
  });

  it.each([{ sourceType: 'legal', normalized: 'legalEntity' }, { sourceType: 'external', normalized: 'externalContact' }] as const)('case party source mapping: $sourceType retains its supported server alias and confirmed result', async ({ sourceType, normalized }) => {
    vi.mocked(apiService.getContacts).mockResolvedValue([{ ...actionContact(), sourceType }] as never); vi.mocked(apiService.addCaseRelatedParty).mockResolvedValue({ ...actionParty(), sourceType: normalized }); const onSave = vi.fn(); render(<CaseModal isOpen caseData={createCase()} onClose={vi.fn()} onSave={onSave} />); await screen.findByText('Текущий участник', { selector: 'option' }); fireEvent.change(screen.getByLabelText('Связанное лицо'), { target: { value: `${sourceType}:10` } }); fireEvent.click(screen.getByRole('button', { name: 'Добавить' })); await screen.findByText('Текущая связь'); expect(apiService.addCaseRelatedParty).toHaveBeenCalledExactlyOnceWith(1, { sourceType: normalized, sourceId: 10, role: 'Участник' }); expect(onSave).not.toHaveBeenCalled();
  });

  it('case party entity: an organization uses its current independent catalogue and confirmed identity', async () => {
    vi.mocked(apiService.getLegalEntitiesPaged).mockResolvedValue({ items: [{ id: 17, shortName: 'ООО Проверка' }], total: 1, page: 1, pageSize: 100 } as never); vi.mocked(apiService.addCaseRelatedParty).mockResolvedValue({ ...actionParty(), sourceType: 'legalEntity', sourceId: 17, displayName: 'Связанная организация' }); render(<CaseModal isOpen caseData={createCase()} onClose={vi.fn()} onSave={vi.fn()} />); await act(async () => {}); fireEvent.change(screen.getByLabelText('Тип связанного участника'), { target: { value: 'legalEntity' } }); await screen.findByText('ООО Проверка', { selector: 'option' }); fireEvent.change(screen.getByLabelText('Связанная организация'), { target: { value: '17' } }); fireEvent.click(screen.getByRole('button', { name: 'Добавить' })); await screen.findByText('Связанная организация', { selector: 'span' }); expect(apiService.addCaseRelatedParty).toHaveBeenCalledExactlyOnceWith(1, { sourceType: 'legalEntity', sourceId: 17, role: 'Участник' });
  });
  it('case party entity: an injected organization ID cannot create a link', async () => {
    render(<CaseModal isOpen caseData={createCase()} onClose={vi.fn()} onSave={vi.fn()} />); await act(async () => {}); fireEvent.change(screen.getByLabelText('Тип связанного участника'), { target: { value: 'legalEntity' } }); const select = screen.getByLabelText('Связанная организация'); const option = document.createElement('option'); option.value = '999'; option.textContent = 'Прежняя организация'; select.append(option); fireEvent.change(select, { target: { value: '999' } }); fireEvent.click(screen.getByRole('button', { name: 'Добавить' })); await act(async () => {}); expect(apiService.addCaseRelatedParty).not.toHaveBeenCalled();
  });
  it('case party entity: retry reloads only the organization catalogue without clearing its role', async () => {
    vi.mocked(apiService.getLegalEntitiesPaged).mockRejectedValueOnce(new Error('Организации временно недоступны')).mockResolvedValueOnce({ items: [{ id: 17, shortName: 'ООО Проверка' }], total: 1, page: 1, pageSize: 100 } as never); render(<CaseModal isOpen caseData={createCase()} onClose={vi.fn()} onSave={vi.fn()} />); await act(async () => {}); fireEvent.change(screen.getByLabelText('Тип связанного участника'), { target: { value: 'legalEntity' } }); await screen.findByText('Организации временно недоступны'); fireEvent.change(screen.getByLabelText('Роль связанного участника'), { target: { value: 'Поручитель' } }); const contactReads = vi.mocked(apiService.getContacts).mock.calls.length; fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку участников' })); await screen.findByText('ООО Проверка', { selector: 'option' }); expect(apiService.getLegalEntitiesPaged).toHaveBeenCalledTimes(2); expect(apiService.getContacts).toHaveBeenCalledTimes(contactReads); expect(screen.getByLabelText('Роль связанного участника')).toHaveProperty('value', 'Поручитель');
  });

  it('nested modal boundary: writ upload shares the case save lock and preserves manual strategy',async()=>{
    const pending=deferred<ClientCaseDocument>();vi.mocked(apiService.uploadCaseDocument).mockReturnValueOnce(pending.promise);const onSave=vi.fn();render(<CaseModal isOpen caseData={createCase({strategy:'Сохранённая позиция'})} onClose={vi.fn()} onSave={onSave}/>);await act(async()=>{});fireEvent.change(screen.getByLabelText('Стратегия и план действий'),{target:{value:'Ручная позиция скана'}});fireEvent.change(screen.getByLabelText('Загрузить скан исполнительного листа'),{target:{files:[new File(['x'],'writ.pdf')]}});await waitFor(()=>expect(apiService.uploadCaseDocument).toHaveBeenCalledOnce());expect(screen.getByRole('button',{name:'Сохранить'})).toHaveProperty('disabled',true);fireEvent.keyDown(screen.getByLabelText('Название дела'),{key:'Enter',ctrlKey:true});expect(onSave).not.toHaveBeenCalled();await act(async()=>pending.resolve({...actionFile(),documentType:'Исполнительный лист',status:'Получен'}));expect(screen.getByLabelText('Стратегия и план действий')).toHaveProperty('value','Ручная позиция скана');expect(screen.getByRole('button',{name:'Сохранить'})).toHaveProperty('disabled',false);
  });
  it('nested modal boundary: closing before unmount prevents old child upload callbacks',async()=>{
    const pending=deferred<ClientCaseDocument>();vi.mocked(apiService.uploadCaseDocument).mockReturnValueOnce(pending.promise);const props={isOpen:true,caseData:createCase(),onClose:vi.fn(),onSave:vi.fn()};render(<CaseModal {...props}/>);await act(async()=>{});fireEvent.change(screen.getByLabelText('Загрузить скан исполнительного листа'),{target:{files:[new File(['x'],'writ.pdf')]}});fireEvent.click(screen.getByRole('button',{name:'Закрыть'}));await act(async()=>{});const reads=vi.mocked(apiService.getCaseDocuments).mock.calls.length;await act(async()=>pending.resolve({...actionFile(),documentType:'Исполнительный лист',status:'Получен'}));expect(apiService.getCaseDocuments).toHaveBeenCalledTimes(reads);expect(screen.queryByLabelText('Загрузить скан исполнительного листа')).toBeNull();expect(props.onClose).toHaveBeenCalledOnce();
  });
  it('nested modal prerequisites: changing responsible disables preparation until the case is saved',async()=>{
    render(<CaseModal isOpen caseData={createCase({nextHearingAt:'2099-10-20T15:00:00Z',responsibleUserId:5})} onClose={vi.fn()} onSave={vi.fn()} responsibles={[{id:5,fullName:'Сохранённый'},{id:7,fullName:'Новый'}]}/>);await screen.findByText('Договор и реквизиты');fireEvent.change(document.querySelector('select[aria-label="Ответственный"]')!,{target:{value:'7'}});await screen.findByText('Сначала сохраните новую дату заседания и ответственного по делу.');expect(screen.queryByRole('button',{name:'Подтвердить'})).toBeNull();expect(apiService.updateCaseHearingPreparation).not.toHaveBeenCalled();
  });
  it('nested modal template: its own pending request stays mounted and blocks case save',async()=>{
    vi.mocked(apiService.createCasePositionTemplate).mockReset();const pending=deferred<Awaited<ReturnType<typeof apiService.createCasePositionTemplate>>>();vi.mocked(apiService.createCasePositionTemplate).mockReturnValueOnce(pending.promise);const onSave=vi.fn();render(<CaseModal isOpen caseData={createCase({strategy:'Позиция для шаблона'})} onClose={vi.fn()} onSave={onSave}/>);await act(async()=>{});fireEvent.click(screen.getByRole('button',{name:'Новый шаблон'}));fireEvent.change(screen.getByLabelText('Название шаблона позиции'),{target:{value:'Текущий шаблон'}});fireEvent.click(screen.getByRole('button',{name:'Сохранить шаблон'}));await waitFor(()=>expect(apiService.createCasePositionTemplate).toHaveBeenCalledOnce());expect(screen.getByLabelText('Название шаблона позиции')).toHaveProperty('disabled',true);expect(screen.getByRole('button',{name:'Сохранить'})).toHaveProperty('disabled',true);await act(async()=>pending.resolve({id:401,title:'Текущий шаблон',text:'Позиция для шаблона',scope:'personal',canManage:true,updatedAt:'2026-11-02T00:00:00Z'}));await screen.findByRole('option',{name:'Личный · Текущий шаблон'});expect(screen.getByRole('button',{name:'Сохранить'})).toHaveProperty('disabled',false);expect(onSave).not.toHaveBeenCalled();
  });
  it('nested modal template: closed parent rejects late template creation',async()=>{
    vi.mocked(apiService.createCasePositionTemplate).mockReset();const pending=deferred<Awaited<ReturnType<typeof apiService.createCasePositionTemplate>>>();vi.mocked(apiService.createCasePositionTemplate).mockReturnValueOnce(pending.promise);render(<CaseModal isOpen caseData={createCase({strategy:'Позиция'})} onClose={vi.fn()} onSave={vi.fn()}/>);await act(async()=>{});fireEvent.click(screen.getByRole('button',{name:'Новый шаблон'}));fireEvent.change(screen.getByLabelText('Название шаблона позиции'),{target:{value:'Закрытый шаблон'}});fireEvent.click(screen.getByRole('button',{name:'Сохранить шаблон'}));await waitFor(()=>expect(apiService.createCasePositionTemplate).toHaveBeenCalledOnce());fireEvent.click(screen.getByRole('button',{name:'Закрыть'}));await act(async()=>pending.resolve({id:402,title:'Закрытый шаблон',text:'Позиция',scope:'personal',canManage:true,updatedAt:'2026-11-02T00:00:00Z'}));expect(document.body.textContent).not.toContain('Закрытый шаблон');
  });

  it.each(['hearing', 'appeal'] as const)('task child bridge: %s holds parent save and drops completion after rights change', async kind => {
    const pending = deferred<never>();
    if (kind === 'hearing') vi.mocked(apiService.createTaskJournalItem).mockReturnValueOnce(pending.promise);
    else vi.mocked(apiService.createCaseAppealTask).mockReturnValueOnce(pending.promise);
    const props = { isOpen: true, caseData: createCase({ nextHearingAt: '2099-10-20T15:00:00Z', responsibleUserId: 7, courtDecisionAt: '2026-09-20', appealDeadlineAt: '2099-10-20' }), onClose: vi.fn(), onSave: vi.fn(), canCreateTasks: true, allowEdit: true, onOpenTask: vi.fn() };
    const view = render(<CaseModal {...props} />); fireEvent.click(await screen.findByRole('button', { name: kind === 'hearing' ? 'Запросить доверенность' : 'Обжаловать — создать задачу' }));
    expect(screen.getByRole('button', { name: 'Сохранить' })).toHaveProperty('disabled', true);
    fireEvent.keyDown(screen.getByLabelText('Название дела'), { key: 'Enter', ctrlKey: true }); expect(props.onSave).not.toHaveBeenCalled();
    view.rerender(<CaseModal {...props} allowEdit={false} canCreateTasks={false} />); await act(async () => {}); const reads = vi.mocked(apiService.getTaskJournalItems).mock.calls.length;
    await act(async () => pending.resolve((kind === 'hearing' ? { id: 91, clientCaseId: 1 } : { id: 8, caseId: 1, choice: 'appeal', taskId: 91, decidedAt: '2026-11-02T00:00:00Z' }) as never));
    expect(apiService.getTaskJournalItems).toHaveBeenCalledTimes(reads); expect(screen.queryByRole('button', { name: 'Открыть задачу' })).toBeNull();
  });
  it('task child bridge: changing the responsible disables authority until saving', async () => {
    render(<CaseModal isOpen caseData={createCase({ nextHearingAt: '2099-10-20T15:00:00Z', responsibleUserId: 7 })} onClose={vi.fn()} onSave={vi.fn()} canCreateTasks responsibles={[{ id: 7, fullName: 'Сотрудник 7' }, { id: 8, fullName: 'Сотрудник 8' }]} />);
    fireEvent.change(document.querySelector('select[aria-label="Ответственный"]')!, { target: { value: '8' } });
    expect(screen.getByRole('button', { name: 'Запросить доверенность' })).toHaveProperty('disabled', true); expect(apiService.createTaskJournalItem).not.toHaveBeenCalled();
  });

  it('recovery parent bridge: a pending allocation blocks parent save and cannot refresh after finance access is removed',async()=>{
    const pending=deferred<void>();vi.mocked(apiService.setCourtRecoveryPayment).mockReset().mockReturnValueOnce(pending.promise);vi.mocked(apiService.getCourtRecoveries).mockResolvedValue({awardedAmount:100000,receivedAmount:0,remainingAmount:100000,linkedPayments:[],availablePayments:[{paymentId:7,date:'2026-09-24',description:'Поступление',paidAmount:25000,allocatedAmount:null,effectiveAmount:0,warning:null}],hasMoreAvailablePayments:false,hasCompletePaymentVisibility:true});
    const props={isOpen:true,caseData:createCase({courtAwardedAmount:100000}),onClose:vi.fn(),onSave:vi.fn(),canViewFinance:true};const view=render(<CaseModal {...props}/>);fireEvent.change(await screen.findByRole('combobox',{name:'Платёж для взыскания'}),{target:{value:'7'}});fireEvent.click(screen.getByRole('button',{name:'Сохранить связь'}));await waitFor(()=>expect(apiService.setCourtRecoveryPayment).toHaveBeenCalledOnce());expect(screen.getByRole('button',{name:'Сохранить'})).toHaveProperty('disabled',true);fireEvent.keyDown(screen.getByLabelText('Название дела'),{key:'Enter',ctrlKey:true});expect(props.onSave).not.toHaveBeenCalled();view.rerender(<CaseModal {...props} canViewFinance={false}/>);await act(async()=>{});const reads=vi.mocked(apiService.getCourtRecoveries).mock.calls.length;await act(async()=>pending.resolve());expect(apiService.getCourtRecoveries).toHaveBeenCalledTimes(reads);expect(screen.queryByText(/Связь с платежом сохранена/)).toBeNull();
  });

  it.each(['position', 'hearing'] as const)('checked confirmation source: legacy %s conflict does not mark or retry', async kind => {
    const onPositionReviewed = vi.fn(); const onNextHearingConfirmed = vi.fn();
    const source = createCase({ strategy: '  Сохранённая позиция  ', nextHearingAt: '2099-11-03T12:00:00.1234567' });
    const failure = new Error('HTTP 409 - {"message":"Данные изменены. Обновите карточку дела."}');
    if (kind === 'position') vi.mocked(apiService.reviewCasePosition).mockRejectedValueOnce(failure);
    else vi.mocked(apiService.confirmNextHearing).mockRejectedValueOnce(failure);
    render(<CaseModal isOpen onClose={vi.fn()} caseData={source} onSave={vi.fn()} onPositionReviewed={onPositionReviewed} onNextHearingConfirmed={onNextHearingConfirmed} />);
    fireEvent.click(screen.getByRole('button', { name: kind === 'position' ? 'Подтвердить проверку позиции' : 'Подтвердить дату заседания' }));
    await screen.findByText('Данные изменены. Обновите карточку дела.');
    if (kind === 'position') expect(apiService.reviewCasePosition).toHaveBeenCalledExactlyOnceWith(source.id, source.strategy);
    else expect(apiService.confirmNextHearing).toHaveBeenCalledExactlyOnceWith(source.id, source.nextHearingAt);
    expect(onPositionReviewed).not.toHaveBeenCalled(); expect(onNextHearingConfirmed).not.toHaveBeenCalled();
    expect((screen.getByLabelText('Стратегия и план действий') as HTMLTextAreaElement).value).toBe(source.strategy);
  });

  it('checked confirmation source: legacy naive UTC proof accepts an equivalent Z hearing', async () => {
    const onNextHearingConfirmed = vi.fn();
    vi.mocked(apiService.confirmNextHearing).mockResolvedValueOnce({ nextHearingConfirmedAt: '2026-11-03T00:00:00', nextHearingConfirmedForAt: '2099-11-03T12:00:00Z', nextHearingConfirmedByName: 'Юрист', nextHearingConfirmedByUserId: 7 });
    render(<CaseModal isOpen onClose={vi.fn()} caseData={createCase({ nextHearingAt: '2099-11-03T12:00:00' })} onSave={vi.fn()} onNextHearingConfirmed={onNextHearingConfirmed} />);
    fireEvent.click(screen.getByRole('button', { name: 'Подтвердить дату заседания' }));
    await waitFor(() => expect(onNextHearingConfirmed).toHaveBeenCalledOnce());
    expect(apiService.confirmNextHearing).toHaveBeenCalledExactlyOnceWith(1, '2099-11-03T12:00:00');
    expect(screen.queryByRole('button', { name: 'Подтвердить дату заседания' })).toBeNull();
  });

  it.each([
    [409, 'Дело изменилось после открытия редактора. Обновите данные перед сохранением.', 'Дело изменилось после открытия редактора. Обновите данные перед сохранением.'],
    [403, 'Forbidden', 'Недостаточно прав для выполнения действия.'],
    [503, 'Сервис временно недоступен.', 'Сервис временно недоступен.'],
  ])('case save error: HTTP %s shows a readable message and retains the draft without replay', async (status, message, visible) => {
    const onClose = vi.fn(); const onSave = vi.fn().mockRejectedValue(new Error(`HTTP ${status} at https://synthetic.local/api/cases/1/checked - ${message}`));
    render(<CaseModal isOpen onClose={onClose} caseData={createCase()} onSave={onSave} />);
    fireEvent.change(screen.getByLabelText('Название дела'), { target: { value: 'Сохраняемый черновик' } });
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/ }));
    await screen.findByText(visible); expect(onSave).toHaveBeenCalledOnce(); expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Название дела')).toHaveProperty('value', 'Сохраняемый черновик');
    expect(screen.queryByText(/https:\/\/synthetic\.local/)).toBeNull(); expect(screen.queryByText(/^HTTP \d/)).toBeNull();
  });

  it.each([false, true])('untouched case values baseline: modal preserves raw text and date precision, change back %s', async changeBack => {
    vi.mocked(apiService.getUserSetting).mockResolvedValue({ key: 'profile.timeZoneId', value: 'UTC' });
    const source = createCase({ strategy: '  Исходная позиция  ', eventHistory: '  История\nсотрудника  ', documentsSummary: '  Материалы  ', nextHearingAt: '2099-11-03T12:34:56.1234567', startDate: '2026-10-01T12:34:56.1234567Z' });
    vi.mocked(apiService.markCaseActivity).mockResolvedValue({ eventHistory: source.eventHistory, inactivityRemindersEnabled: true } as Awaited<ReturnType<typeof apiService.markCaseActivity>>);
    const save = vi.fn(); render(<CaseModal isOpen caseData={source} onClose={vi.fn()} onSave={save} />);
    await waitFor(() => expect(screen.getByLabelText('Следующее заседание')).toHaveProperty('value', '2099-11-03T12:34'));
    if (changeBack) {
      fireEvent.change(screen.getByLabelText('Следующее заседание'), { target: { value: '2099-11-04T12:34' } });
      fireEvent.change(screen.getByLabelText('Следующее заседание'), { target: { value: '2099-11-03T12:34' } });
      fireEvent.change(screen.getByLabelText('Стратегия и план действий'), { target: { value: 'Другая позиция' } });
      fireEvent.change(screen.getByLabelText('Стратегия и план действий'), { target: { value: source.strategy } });
    }
    fireEvent.change(screen.getByLabelText('Название дела'), { target: { value: 'Новое название' } });
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/ })); await waitFor(() => expect(save).toHaveBeenCalledOnce());
    expect(save.mock.calls[0][0]).toMatchObject({ strategy: source.strategy, eventHistory: source.eventHistory, documentsSummary: source.documentsSummary, nextHearingAt: source.nextHearingAt, startDate: source.startDate });
  });

  it.each(['2099-11-04T13:45', ''])('case exact explicit edit: modal saves hearing %s and normalized new text', async hearing => {
    vi.mocked(apiService.getUserSetting).mockResolvedValue({ key: 'profile.timeZoneId', value: 'UTC' });
    const source = createCase({ strategy: '  Исходная позиция  ', eventHistory: '  История  ', nextHearingAt: '2099-11-03T12:34:56.1234567Z' });
    const save = vi.fn(); render(<CaseModal isOpen caseData={source} onClose={vi.fn()} onSave={save} />);
    await waitFor(() => expect(screen.getByLabelText('Следующее заседание')).toHaveProperty('value', '2099-11-03T12:34'));
    fireEvent.change(screen.getByLabelText('Следующее заседание'), { target: { value: hearing } });
    fireEvent.change(screen.getByLabelText('Стратегия и план действий'), { target: { value: '  Новая позиция  ' } });
    fireEvent.change(screen.getByLabelText('История событий'), { target: { value: '  Новая история  ' } });
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/ })); await waitFor(() => expect(save).toHaveBeenCalledOnce()); const patch = save.mock.calls[0][0];
    expect(patch.nextHearingAt).toBe(hearing ? '2099-11-04T13:45:00.000Z' : null); expect(patch.strategy).toBe('Новая позиция'); expect(patch.eventHistory).toBe('Новая история');
  });

  it('case exact activity: fresh server history remains exact when the user has not edited it', async () => {
    const history = '  Запись коллеги\nБез потери пробелов  ';
    vi.mocked(apiService.markCaseActivity).mockResolvedValue({ eventHistory: history, inactivityRemindersEnabled: true } as Awaited<ReturnType<typeof apiService.markCaseActivity>>);
    const save = vi.fn(); render(<CaseModal isOpen caseData={createCase({ eventHistory: 'Старая история' })} onClose={vi.fn()} onSave={save} />);
    await waitFor(() => expect(screen.getByLabelText('История событий')).toHaveProperty('value', history));
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/ })); await waitFor(() => expect(save).toHaveBeenCalledOnce());
    expect(save.mock.calls[0][0].eventHistory).toBe(history);
  });

  it('case exact nullable: modal retains absent identifiers and court role as null', async () => {
    const source = createCase({ claimantInn: null, claimantOgrn: null, defendantInn: null, defendantOgrn: null, ourCourtRole: null });
    const save = vi.fn(); render(<CaseModal isOpen caseData={source} onClose={vi.fn()} onSave={save} />); await act(async () => {});
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/ })); await waitFor(() => expect(save).toHaveBeenCalledOnce()); const body = save.mock.calls[0][0];
    expect(body).toMatchObject({ claimantInn: null, claimantOgrn: null, defendantInn: null, defendantOgrn: null, ourCourtRole: null });
  });

  it('case exact newline: modal keeps original CRLF after a visible edit and return', async () => {
    const source = createCase({ strategy: '  Первая строка\r\nВторая строка  ', eventHistory: '  Запись\r\nИстория  ' });
    vi.mocked(apiService.markCaseActivity).mockResolvedValue({ eventHistory: source.eventHistory, inactivityRemindersEnabled: true } as Awaited<ReturnType<typeof apiService.markCaseActivity>>); const save = vi.fn(); render(<CaseModal isOpen caseData={source} onClose={vi.fn()} onSave={save} />); await act(async () => {});
    for (const [label, original] of [['Стратегия и план действий', source.strategy!], ['История событий', source.eventHistory!]]) {
      fireEvent.change(screen.getByLabelText(label), { target: { value: 'Временный текст' } });
      fireEvent.change(screen.getByLabelText(label), { target: { value: original.replace(/\r\n/g, '\n') } });
    }
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/ })); await waitFor(() => expect(save).toHaveBeenCalledOnce()); const body = save.mock.calls[0][0];
    expect(body.strategy).toBe(source.strategy); expect(body.eventHistory).toBe(source.eventHistory);
  });

});
