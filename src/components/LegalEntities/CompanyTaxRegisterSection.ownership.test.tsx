import { StrictMode } from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CompanyTaxRegisterSection } from './CompanyTaxRegisterSection';
import { apiService } from '../../services/api';
import { documentCopyTestSession } from '../../test/documentCopySession';
import type { TaxRegisterBook, TaxRegisterEntryItem } from '../../types';

const mocks = vi.hoisted(() => ({ confirm: vi.fn(), prompt: vi.fn(), error: vi.fn(), success: vi.fn() }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => mocks }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => mocks }));
vi.mock('../../services/api', () => ({ apiService: { getTaxRegisterBook: vi.fn(), getTaxRegisterDrafts: vi.fn(), getTaxRegisterJournal: vi.fn(), getTaskJournalAssignees: vi.fn(), createTaxRegisterEntry: vi.fn(), approveTaxRegisterEntry: vi.fn(), archiveTaxRegisterEntry: vi.fn(), correctTaxRegisterEntry: vi.fn(), closeTaxRegisterYear: vi.fn(), downloadTaxRegisterExport: vi.fn() } }));
const year = new Date().getFullYear();
const entry = (companyId = 5, y = year, patch: Partial<TaxRegisterEntryItem> = {}): TaxRegisterEntryItem => ({ id: 1, legalEntityId: companyId, operationDate: `${y}-03-15T00:00:00`, type: 'Income', direction: 'Income', amount: 1000, sign: 1, status: 'Approved', documentRef: 'INV-1', ...patch });
const book = (companyId = 5, y = year, patch: Partial<TaxRegisterBook> = {}): TaxRegisterBook => ({ legalEntityId: companyId, year: y, isClosed: false, totals: { income: 1000, expense: 0, taxBase: 1000, entries: 1 }, entries: [entry(companyId, y)], page: 1, pageSize: 100, ...patch });
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }
const props = { companyId: 5, readOnly: false };
function fillDraft(value = '250,5') { fireEvent.change(screen.getByLabelText('Дата операции'), { target: { value: `${year}-04-01` } }); fireEvent.change(screen.getByLabelText('Сумма'), { target: { value } }); }

describe('tax register ownership', () => {
  let session: ReturnType<typeof documentCopyTestSession>;
  const oldCreate = Object.getOwnPropertyDescriptor(URL, 'createObjectURL');
  const oldRevoke = Object.getOwnPropertyDescriptor(URL, 'revokeObjectURL');
  beforeEach(() => {
    vi.resetAllMocks(); session = documentCopyTestSession(); mocks.confirm.mockResolvedValue(true); mocks.prompt.mockResolvedValue('Reason');
    vi.mocked(apiService.getTaxRegisterBook).mockImplementation(async (id, y) => book(id, y));
    vi.mocked(apiService.getTaxRegisterDrafts).mockImplementation(async (id, y, page) => ({ items: [entry(id, y, { id: 2, status: 'Draft' })], total: 1, page: page ?? 1, pageSize: 200 }));
    vi.mocked(apiService.getTaxRegisterJournal).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 20 });
    vi.mocked(apiService.getTaskJournalAssignees).mockResolvedValue([]);
    URL.createObjectURL = vi.fn(() => 'blob:tax-test'); URL.revokeObjectURL = vi.fn();
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
  });
  afterEach(() => {
    session.restore(); vi.restoreAllMocks();
    if (oldCreate) Object.defineProperty(URL, 'createObjectURL', oldCreate); else Reflect.deleteProperty(URL, 'createObjectURL');
    if (oldRevoke) Object.defineProperty(URL, 'revokeObjectURL', oldRevoke); else Reflect.deleteProperty(URL, 'revokeObjectURL');
  });

  for (const operation of ['close', 'correct'] as const) it.each(['company', 'year', 'readonly', 'authority', 'session', 'unmount'] as const)(`tax owner: ${operation} cannot start after %s changes while the dialog waits`, async change => {
    const pending = deferred<boolean | string>(); if (operation === 'close') mocks.confirm.mockReturnValue(pending.promise); else mocks.prompt.mockReturnValue(pending.promise);
    const view = render(<CompanyTaxRegisterSection {...props} />);
    fireEvent.click(await screen.findByRole('button', { name: operation === 'close' ? 'Закрыть год' : 'Исправить' }));
    const call = operation === 'close' ? mocks.confirm : mocks.prompt; const signal = call.mock.calls[0][0].signal as AbortSignal;
    if (change === 'company') view.rerender(<CompanyTaxRegisterSection {...props} companyId={6} />);
    else if (change === 'year') fireEvent.change(screen.getByLabelText('Год'), { target: { value: year - 1 } });
    else if (change === 'readonly') view.rerender(<CompanyTaxRegisterSection {...props} readOnly />);
    else if (change === 'authority') view.rerender(<CompanyTaxRegisterSection {...props} ownerScope="new-role" />);
    else if (change === 'session') session.replace(); else view.unmount();
    await act(async () => pending.resolve(operation === 'close' ? true : 'Old private reason'));
    expect(apiService.closeTaxRegisterYear).not.toHaveBeenCalled(); expect(apiService.correctTaxRegisterEntry).not.toHaveBeenCalled(); expect(mocks.error).not.toHaveBeenCalled();
    if (change !== 'session') expect(signal.aborted).toBe(true);
  });

  it('tax owner: confirmation is a single operation and cancel does not refresh or write', async () => {
    const pending = deferred<boolean>(); mocks.confirm.mockReturnValue(pending.promise); render(<CompanyTaxRegisterSection {...props} />);
    const close = await screen.findByRole('button', { name: 'Закрыть год' }); const reads = vi.mocked(apiService.getTaxRegisterBook).mock.calls.length;
    fireEvent.click(close); fireEvent.click(close); fireEvent.click(screen.getByRole('button', { name: 'Утвердить' }));
    expect(mocks.confirm).toHaveBeenCalledTimes(1); expect(apiService.approveTaxRegisterEntry).not.toHaveBeenCalled();
    await act(async () => pending.resolve(false)); expect(apiService.closeTaxRegisterYear).not.toHaveBeenCalled(); expect(apiService.getTaxRegisterBook).toHaveBeenCalledTimes(reads);
  });

  it('tax owner: accepted correction trims the reason and reloads only once', async () => {
    mocks.prompt.mockResolvedValue('  Verified reason  '); vi.mocked(apiService.correctTaxRegisterEntry).mockResolvedValue(entry(5, year, { type: 'Correction', sign: -1 }));
    render(<CompanyTaxRegisterSection {...props} />); fireEvent.click(await screen.findByRole('button', { name: 'Исправить' }));
    await waitFor(() => expect(apiService.getTaxRegisterBook).toHaveBeenCalledTimes(2));
    expect(apiService.correctTaxRegisterEntry).toHaveBeenCalledExactlyOnceWith(1, 'Verified reason');
  });

  it.each(['success', 'failure'] as const)('tax owner: late create %s cannot clear a new company draft or notify it', async outcome => {
    const pending = deferred<TaxRegisterEntryItem>(); vi.mocked(apiService.createTaxRegisterEntry).mockReturnValue(pending.promise);
    const view = render(<CompanyTaxRegisterSection {...props} />); await screen.findByLabelText('Сумма'); fillDraft(); fireEvent.click(screen.getByRole('button', { name: 'Добавить черновик' }));
    view.rerender(<CompanyTaxRegisterSection {...props} companyId={6} />); await screen.findByLabelText('Сумма'); fillDraft('321');
    const reads = vi.mocked(apiService.getTaxRegisterBook).mock.calls.length;
    await act(async () => { if (outcome === 'success') pending.resolve(entry()); else pending.reject(new Error('Private old error')); });
    expect(screen.getByLabelText('Сумма')).toHaveProperty('value', '321'); expect(mocks.error).not.toHaveBeenCalled(); expect(apiService.getTaxRegisterBook).toHaveBeenCalledTimes(reads);
  });

  it('tax owner: pending create locks the submitted form and duplicate write', async () => {
    const pending = deferred<TaxRegisterEntryItem>(); vi.mocked(apiService.createTaxRegisterEntry).mockReturnValue(pending.promise);
    render(<CompanyTaxRegisterSection {...props} />); await screen.findByLabelText('Сумма'); fillDraft(); const add = screen.getByRole('button', { name: 'Добавить черновик' });
    fireEvent.click(add); fireEvent.click(add); fireEvent.change(screen.getByLabelText('Сумма'), { target: { value: '999' } });
    expect(apiService.createTaxRegisterEntry).toHaveBeenCalledTimes(1); expect(screen.getByLabelText('Сумма')).toHaveProperty('value', '250,5');
    await act(async () => pending.resolve(entry())); expect(screen.getByLabelText('Сумма')).toHaveProperty('value', '');
  });

  it.each(['company', 'year', 'session'] as const)('tax owner: delayed export does not download after %s changes', async change => {
    const pending = deferred<Blob>(); vi.mocked(apiService.downloadTaxRegisterExport).mockReturnValue(pending.promise);
    const view = render(<CompanyTaxRegisterSection {...props} />); fireEvent.click(await screen.findByRole('button', { name: 'Выгрузить Excel' }));
    if (change === 'company') view.rerender(<CompanyTaxRegisterSection {...props} companyId={6} />);
    else if (change === 'year') fireEvent.change(screen.getByLabelText('Год'), { target: { value: year - 1 } }); else session.replace();
    await act(async () => pending.resolve(new Blob(['Private old export'])));
    expect(URL.createObjectURL).not.toHaveBeenCalled(); expect(HTMLAnchorElement.prototype.click).not.toHaveBeenCalled(); expect(mocks.error).not.toHaveBeenCalled();
  });

  it('tax owner: readonly default keeps reading and export but blocks every mutation', async () => {
    vi.mocked(apiService.downloadTaxRegisterExport).mockResolvedValue(new Blob(['export'])); render(<CompanyTaxRegisterSection companyId={5} />);
    await screen.findByTestId('company-tax-register'); const reads = vi.mocked(apiService.getTaxRegisterBook).mock.calls.length;
    expect(screen.queryByRole('button', { name: 'Исправить' })).toBeNull(); expect(screen.queryByRole('button', { name: 'Добавить черновик' })).toBeNull();
    for (const name of ['Закрыть год', 'Утвердить', 'В архив']) { const button = screen.getByRole('button', { name }); expect(button).toHaveProperty('disabled', true); fireEvent.click(button); }
    fireEvent.click(screen.getByRole('button', { name: 'Выгрузить Excel' })); await waitFor(() => expect(URL.createObjectURL).toHaveBeenCalledTimes(1));
    expect(URL.revokeObjectURL).toHaveBeenCalledExactlyOnceWith('blob:tax-test'); expect(apiService.getTaxRegisterBook).toHaveBeenCalledTimes(reads);
    expect(apiService.closeTaxRegisterYear).not.toHaveBeenCalled(); expect(apiService.approveTaxRegisterEntry).not.toHaveBeenCalled(); expect(apiService.archiveTaxRegisterEntry).not.toHaveBeenCalled();
  });

  it('tax owner: a closed year still permits draft archiving in edit mode as the server does', async () => {
    vi.mocked(apiService.getTaxRegisterBook).mockResolvedValue(book(5, year, { isClosed: true }));
    vi.mocked(apiService.archiveTaxRegisterEntry).mockResolvedValue(entry(5, year, { id: 2, status: 'Archived' }));
    render(<CompanyTaxRegisterSection {...props} />); const archive = await screen.findByRole('button', { name: 'В архив' });
    expect(archive).toHaveProperty('disabled', false); expect(screen.getByRole('button', { name: 'Утвердить' })).toHaveProperty('disabled', true);
    fireEvent.click(archive); await waitFor(() => expect(apiService.getTaxRegisterBook).toHaveBeenCalledTimes(2));
    expect(apiService.archiveTaxRegisterEntry).toHaveBeenCalledExactlyOnceWith(2); expect(apiService.approveTaxRegisterEntry).not.toHaveBeenCalled();
  });

  it.each(['foreign-book', 'foreign-draft', 'invalid-totals'] as const)('tax owner: rejects %s data before enabling actions', async kind => {
    if (kind === 'foreign-book') vi.mocked(apiService.getTaxRegisterBook).mockResolvedValue(book(6));
    else if (kind === 'foreign-draft') vi.mocked(apiService.getTaxRegisterDrafts).mockResolvedValue({ items: [entry(6)], total: 1, page: 1, pageSize: 200 });
    else vi.mocked(apiService.getTaxRegisterBook).mockResolvedValue(book(5, year, { totals: { income: NaN, expense: 0, taxBase: 0, entries: 0 } }));
    render(<CompanyTaxRegisterSection {...props} />); expect(await screen.findByRole('alert')).toBeTruthy(); expect(screen.queryByRole('button', { name: 'Закрыть год' })).toBeNull();
  });

  it('tax owner: StrictMode keeps one write and one post-write refresh', async () => {
    render(<StrictMode><CompanyTaxRegisterSection {...props} /></StrictMode>); const close = await screen.findByRole('button', { name: 'Закрыть год' }); const reads = vi.mocked(apiService.getTaxRegisterBook).mock.calls.length;
    fireEvent.click(close); await waitFor(() => expect(apiService.getTaxRegisterBook).toHaveBeenCalledTimes(reads + 1)); expect(apiService.closeTaxRegisterYear).toHaveBeenCalledTimes(1);
  });

  it('tax journal owner: an old session journal result is ignored without a render', async () => {
    const pending = deferred<Awaited<ReturnType<typeof apiService.getTaxRegisterJournal>>>(); vi.mocked(apiService.getTaxRegisterJournal).mockReturnValue(pending.promise);
    render(<CompanyTaxRegisterSection {...props} />); fireEvent.click(await screen.findByRole('button', { name: 'Журнал событий' })); session.replace();
    await act(async () => pending.resolve({ items: [{ eventId: 1, entryId: 1, action: 'Approved', userId: 7, createdAt: `${year}-03-15T00:00:00`, direction: 'Income', signedAmount: 1000, comment: 'Private old journal' }], total: 1, page: 1, pageSize: 20 }));
    expect(screen.queryByText('Private old journal')).toBeNull();
  });
  it('tax journal owner: delayed author names cannot follow a replaced session', async () => {
    const pending = deferred<Awaited<ReturnType<typeof apiService.getTaskJournalAssignees>>>(); vi.mocked(apiService.getTaskJournalAssignees).mockReturnValue(pending.promise);
    vi.mocked(apiService.getTaxRegisterJournal).mockResolvedValue({ items: [{ eventId: 1, entryId: 1, action: 'Approved', userId: 7, createdAt: `${year}-03-15T00:00:00`, direction: 'Income', signedAmount: 1000 }], total: 1, page: 1, pageSize: 20 });
    render(<CompanyTaxRegisterSection {...props} />); fireEvent.click(await screen.findByRole('button', { name: 'Журнал событий' })); await screen.findByText(/Пользователь №7/); session.replace();
    await act(async () => pending.resolve([{ id: 7, name: 'Private author name', email: 'fixture@example.local' }])); expect(screen.queryByText(/Private author name/)).toBeNull();
  });
});
