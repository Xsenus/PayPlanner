import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { CaseModal } from './CaseModal';
import { apiService } from '../../services/api';
import type { ClientCase, ClientCaseDocument } from '../../types';
import { documentCopyTestSession } from '../../test/documentCopySession';

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
    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({ caseNumber: 'НОВЫЙ-2026', feeAmount: 123.45, courtClaimAmount: 12.34, courtAwardedAmount: 56.78, courtStateFeeAmount: null, clearCourtStateFeeAmount: true, dueDate: '2026-12-03', strategy: 'Новая позиция' }));
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
    fireEvent.keyDown(screen.getByLabelText('Название дела'), { key: 'Enter', ctrlKey: true }); await waitFor(() => expect(props.onSave).toHaveBeenCalledTimes(2)); expect(props.onSave).toHaveBeenLastCalledWith(expect.objectContaining({ title: 'Другое дело' }));
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
    vi.mocked(apiService.retireCaseDocumentUpload).mockReset();
    vi.mocked(apiService.getPendingCaseDocumentUploads).mockReset();
    vi.mocked(apiService.getPendingCaseDocumentUploads).mockResolvedValue({ items: [], nextCursor: null });
    vi.mocked(apiService.getCaseDocumentUploadStates).mockReset();
    vi.mocked(apiService.getCaseDocumentUploadStates).mockResolvedValue([]);
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
    vi.mocked(apiService.createTaskJournalItem).mockResolvedValue({ id: 91 } as never);
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
    await waitFor(() => expect(apiService.reviewCasePosition).toHaveBeenCalledWith(1));
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
    })));
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
    await waitFor(() => expect(apiService.confirmNextHearing).toHaveBeenCalledWith(1));
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
    }));
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
    }));
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
    })));
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
    expect(apiService.getCaseDocuments).toHaveBeenCalledWith(1);
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
    expect(apiService.getClientCaseFinanceSummaries).toHaveBeenCalledWith(10);
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
    });
  });
});
