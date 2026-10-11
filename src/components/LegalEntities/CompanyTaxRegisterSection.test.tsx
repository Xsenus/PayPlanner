import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { CompanyTaxRegisterSection } from './CompanyTaxRegisterSection';
import { apiService } from '../../services/api';
import type { TaxRegisterBook, TaxRegisterEntryItem } from '../../types';

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({ success: vi.fn(), error: vi.fn(), info: vi.fn() }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({ confirm: vi.fn().mockResolvedValue(true), prompt: vi.fn().mockResolvedValue('Ошибка суммы') }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getTaxRegisterBook: vi.fn(),
    getTaxRegisterDrafts: vi.fn(),
    getTaxRegisterJournal: vi.fn(),
    getTaskJournalAssignees: vi.fn(),
    createTaxRegisterEntry: vi.fn(),
    approveTaxRegisterEntry: vi.fn(),
    archiveTaxRegisterEntry: vi.fn(),
    correctTaxRegisterEntry: vi.fn(),
    closeTaxRegisterYear: vi.fn(),
    downloadTaxRegisterExport: vi.fn(),
  },
}));

const entry = (overrides: Partial<TaxRegisterEntryItem> = {}): TaxRegisterEntryItem => ({
  id: 1,
  legalEntityId: 5,
  operationDate: '2026-03-15T00:00:00',
  type: 'Income',
  direction: 'Income',
  amount: 1000,
  sign: 1,
  status: 'Approved',
  documentRef: 'Счёт № 1',
  ...overrides,
});

const book = (overrides: Partial<TaxRegisterBook> = {}): TaxRegisterBook => ({
  legalEntityId: 5,
  year: 2026,
  isClosed: false,
  totals: { income: 1000, expense: 0, taxBase: 1000, entries: 1 },
  entries: [entry()],
  page: 1,
  pageSize: 100,
  ...overrides,
});

describe('CompanyTaxRegisterSection', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiService.getTaxRegisterBook).mockResolvedValue(book());
    vi.mocked(apiService.getTaxRegisterDrafts).mockResolvedValue({ total: 0, page: 1, pageSize: 200, items: [] });
  });

  it('renders nothing when the server refuses access', async () => {
    vi.mocked(apiService.getTaxRegisterBook).mockRejectedValue(new Error('HTTP 403'));
    const { container } = render(<CompanyTaxRegisterSection companyId={5} />);

    await waitFor(() => expect(apiService.getTaxRegisterBook).toHaveBeenCalled());
    expect(container.textContent).toBe('');
  });

  it('shows a retry after a technical failure instead of hiding the section', async () => {
    vi.mocked(apiService.getTaxRegisterBook).mockRejectedValueOnce(new Error('Failed to fetch'));
    render(<CompanyTaxRegisterSection companyId={5} />);
    expect(await screen.findByRole('alert')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Повторить загрузку' }));
    expect(await screen.findByTestId('tax-register-totals')).toBeTruthy();
  });

  it('can load drafts beyond the first 200 entries', async () => {
    vi.mocked(apiService.getTaxRegisterDrafts).mockResolvedValue({ total: 201, page: 1, pageSize: 200, items: [entry({ status: 'Draft' })] });
    render(<CompanyTaxRegisterSection companyId={5} />);
    await userEvent.click(await screen.findByRole('button', { name: 'Следующие черновики' }));
    await waitFor(() => expect(apiService.getTaxRegisterDrafts).toHaveBeenCalledWith(5, 2026, 2));
  });

  it('ignores an old company response after switching companies', async () => {
    let finishOld!: (value: TaxRegisterBook) => void;
    vi.mocked(apiService.getTaxRegisterBook).mockImplementationOnce(() => new Promise((resolve) => { finishOld = resolve; }));
    const { rerender } = render(<CompanyTaxRegisterSection companyId={5} />);
    vi.mocked(apiService.getTaxRegisterBook).mockResolvedValue(book({ legalEntityId: 6, entries: [entry({ documentRef: 'Компания 6' })] }));
    rerender(<CompanyTaxRegisterSection companyId={6} />);
    expect(await screen.findByText('Компания 6')).toBeTruthy();
    finishOld(book({ entries: [entry({ documentRef: 'Компания 5' })] }));
    await waitFor(() => expect(screen.queryByText('Компания 5')).toBeNull());
    expect(screen.getByText('Компания 6')).toBeTruthy();
  });

  it('shows totals and the approved entry', async () => {
    render(<CompanyTaxRegisterSection companyId={5} />);

    expect((await screen.findByTestId('tax-register-totals')).textContent).toContain('Доходы');
    expect(screen.getByText('Счёт № 1')).toBeTruthy();
  });

  it('approves a draft only when it has a primary document', async () => {
    vi.mocked(apiService.getTaxRegisterDrafts).mockResolvedValue({
      total: 2,
      page: 1,
      pageSize: 200,
      items: [entry({ id: 2, status: 'Draft', documentRef: null }), entry({ id: 3, status: 'Draft' })],
    });
    vi.mocked(apiService.approveTaxRegisterEntry).mockResolvedValue(entry({ id: 3 }));
    render(<CompanyTaxRegisterSection companyId={5} />);

    const buttons = await screen.findAllByRole('button', { name: 'Утвердить' });
    expect((buttons[0] as HTMLButtonElement).disabled).toBe(true);
    await userEvent.click(buttons[1]);

    expect(apiService.approveTaxRegisterEntry).toHaveBeenCalledWith(3);
  });

  it('creates a draft from the form', async () => {
    vi.mocked(apiService.createTaxRegisterEntry).mockResolvedValue(entry({ status: 'Draft' }));
    render(<CompanyTaxRegisterSection companyId={5} />);

    await screen.findByTestId('company-tax-register');
    const submit = screen.getByRole('button', { name: 'Добавить черновик' });
    expect((submit as HTMLButtonElement).disabled).toBe(true);
    await userEvent.type(screen.getByLabelText('Дата операции'), '2026-04-01');
    await userEvent.type(screen.getByLabelText('Сумма'), '250,5');
    await userEvent.type(screen.getByLabelText('Первичный документ'), 'Акт № 3');
    await userEvent.click(submit);

    expect(apiService.createTaxRegisterEntry).toHaveBeenCalledWith(
      expect.objectContaining({ legalEntityId: 5, type: 'Income', operationDate: '2026-04-01', amount: 250.5, documentRef: 'Акт № 3' }),
    );
  });

  it('corrects an approved entry with a reason from the dialog', async () => {
    vi.mocked(apiService.correctTaxRegisterEntry).mockResolvedValue(entry({ id: 9, type: 'Correction', sign: -1 }));
    render(<CompanyTaxRegisterSection companyId={5} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Исправить' }));

    await waitFor(() => expect(apiService.correctTaxRegisterEntry).toHaveBeenCalledWith(1, 'Ошибка суммы'));
  });

  it('downloads the book as Excel', async () => {
    vi.mocked(apiService.downloadTaxRegisterExport).mockResolvedValue(new Blob(['x']));
    URL.createObjectURL = vi.fn(() => 'blob:x');
    URL.revokeObjectURL = vi.fn();
    render(<CompanyTaxRegisterSection companyId={5} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Выгрузить Excel' }));

    await waitFor(() => expect(apiService.downloadTaxRegisterExport).toHaveBeenCalledWith(5, new Date().getFullYear(), 'xlsx'));
  });

  it('hides editing for a closed year', async () => {
    vi.mocked(apiService.getTaxRegisterBook).mockResolvedValue(book({ isClosed: true }));
    render(<CompanyTaxRegisterSection companyId={5} />);

    expect(((await screen.findByRole('button', { name: 'Год закрыт' })) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByRole('button', { name: 'Добавить черновик' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Исправить' })).toBeNull();
  });
});

describe('CompanyTaxRegisterSection journal and book pages', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiService.getTaxRegisterBook).mockResolvedValue(book());
    vi.mocked(apiService.getTaxRegisterDrafts).mockResolvedValue({ total: 0, page: 1, pageSize: 200, items: [] });
    vi.mocked(apiService.getTaskJournalAssignees).mockResolvedValue([{ id: 7, name: 'Анна', email: 'a@x' }]);
  });

  const journalItem = (id: number, action = 'Approved') => ({
    eventId: id,
    entryId: 10 + id,
    action,
    userId: 7,
    createdAt: '2026-10-11T09:30:00',
    direction: 'Income' as const,
    signedAmount: 1000,
    comment: null,
  });

  it('loads the journal with filters and shows author names', async () => {
    vi.mocked(apiService.getTaxRegisterJournal).mockResolvedValue({ total: 1, page: 1, pageSize: 20, items: [journalItem(1)] });
    render(<CompanyTaxRegisterSection companyId={5} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Журнал событий' }));
    await waitFor(() => expect(screen.getByTestId('tax-register-journal-list').textContent).toContain('Утверждена'));
    await waitFor(() => expect(screen.getByTestId('tax-register-journal-list').textContent).toContain('Анна'));
    await userEvent.selectOptions(screen.getByLabelText('Журнал: действие'), 'Corrected');

    await waitFor(() =>
      expect(apiService.getTaxRegisterJournal).toHaveBeenLastCalledWith(5, expect.objectContaining({ action: 'Corrected', page: 1, pageSize: 20 })),
    );
  });

  it('offers a retry after a journal failure', async () => {
    vi.mocked(apiService.getTaxRegisterJournal)
      .mockRejectedValueOnce(new Error('Сеть недоступна'))
      .mockResolvedValueOnce({ total: 1, page: 1, pageSize: 20, items: [journalItem(2, 'Created')] });
    render(<CompanyTaxRegisterSection companyId={5} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Журнал событий' }));
    expect((await screen.findByRole('alert')).textContent).toContain('Сеть недоступна');
    await userEvent.click(screen.getByRole('button', { name: 'Повторить загрузку' }));

    await waitFor(() => expect(screen.getByTestId('tax-register-journal-list').textContent).toContain('Создана'));
  });

  it('ignores a stale journal response that arrives after a newer filter', async () => {
    let resolveFirst: (value: unknown) => void = () => {};
    vi.mocked(apiService.getTaxRegisterJournal)
      .mockImplementationOnce(() => new Promise((resolve) => { resolveFirst = resolve; }))
      .mockResolvedValueOnce({ total: 1, page: 1, pageSize: 20, items: [journalItem(3, 'Archived')] });
    render(<CompanyTaxRegisterSection companyId={5} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Журнал событий' }));
    await userEvent.selectOptions(screen.getByLabelText('Журнал: действие'), 'Archived');
    await waitFor(() => expect(screen.getByTestId('tax-register-journal-list').textContent).toContain('Отправлена в архив'));
    resolveFirst({ total: 1, page: 1, pageSize: 20, items: [journalItem(4, 'Created')] });
    await new Promise((resolve) => setTimeout(resolve, 20));

    expect(screen.getByTestId('tax-register-journal-list').textContent).not.toContain('Создана');
  });

  it('requests the next page of a large book', async () => {
    vi.mocked(apiService.getTaxRegisterBook).mockResolvedValue(book({ totals: { income: 1, expense: 0, taxBase: 1, entries: 250 } }));
    render(<CompanyTaxRegisterSection companyId={5} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Следующие записи' }));

    await waitFor(() => expect(apiService.getTaxRegisterBook).toHaveBeenLastCalledWith(5, expect.any(Number), 2));
  });
});
