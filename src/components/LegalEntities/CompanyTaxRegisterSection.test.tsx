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
    createTaxRegisterEntry: vi.fn(),
    approveTaxRegisterEntry: vi.fn(),
    archiveTaxRegisterEntry: vi.fn(),
    correctTaxRegisterEntry: vi.fn(),
    closeTaxRegisterYear: vi.fn(),
    downloadTaxRegisterCsv: vi.fn(),
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
  ...overrides,
});

describe('CompanyTaxRegisterSection', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiService.getTaxRegisterBook).mockResolvedValue(book());
    vi.mocked(apiService.getTaxRegisterDrafts).mockResolvedValue({ total: 0, page: 1, pageSize: 200, items: [] });
  });

  it('renders nothing when the server refuses access', async () => {
    vi.mocked(apiService.getTaxRegisterBook).mockRejectedValue(new Error('403'));
    const { container } = render(<CompanyTaxRegisterSection companyId={5} />);

    await waitFor(() => expect(apiService.getTaxRegisterBook).toHaveBeenCalled());
    expect(container.textContent).toBe('');
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

  it('hides editing for a closed year', async () => {
    vi.mocked(apiService.getTaxRegisterBook).mockResolvedValue(book({ isClosed: true }));
    render(<CompanyTaxRegisterSection companyId={5} />);

    expect(((await screen.findByRole('button', { name: 'Год закрыт' })) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByRole('button', { name: 'Добавить черновик' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Исправить' })).toBeNull();
  });
});
