import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from '../../services/api';
import { SalesRequestInvoice } from './SalesRequestInvoice';

vi.mock('../../services/api', () => ({ apiService: {
  getSalesRequestServiceInvoice: vi.fn(), getSalesRequestInvoiceCandidates: vi.fn(), setSalesRequestServiceInvoice: vi.fn(),
} }));

const first = { invoiceId: 10, number: 'ОБЩИЙ', date: '2026-10-27', sellerCompanyId: 1, sellerBranchId: null };
const second = { invoiceId: 11, number: 'ОБЩИЙ', date: '2026-10-27', sellerCompanyId: 2, sellerBranchId: null };

describe('SalesRequestInvoice', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    vi.mocked(apiService.getSalesRequestServiceInvoice).mockResolvedValue({ state: 'none', invoice: null });
    vi.mocked(apiService.getSalesRequestInvoiceCandidates).mockResolvedValue({ items: [first, second], hasMore: false });
  });

  it('distinguishes equal invoice numbers and saves the selected canonical id and version', async () => {
    const onSaved = vi.fn();
    vi.mocked(apiService.setSalesRequestServiceInvoice).mockResolvedValue({ id: 7, version: 5 } as never);
    render(<SalesRequestInvoice requestId={7} version={4} canEdit onSaved={onSaved} />);
    await userEvent.click(screen.getByRole('button', { name: 'Счёт услуги заявки' }));
    expect(await screen.findByText('Счёт не выбран.')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Найти счёт' }));
    expect(await screen.findByText(/организация №1.*запись №10/)).toBeTruthy();
    expect(screen.getByText(/организация №2.*запись №11/)).toBeTruthy();
    await userEvent.click(screen.getAllByRole('button', { name: 'Выбрать' })[1]);
    await waitFor(() => expect(apiService.setSalesRequestServiceInvoice).toHaveBeenCalledWith(7, 4, 11, null));
    expect(onSaved).toHaveBeenCalledWith({ id: 7, version: 5 }, 4);
  });

  it('does not expose or replace an unavailable link', async () => {
    vi.mocked(apiService.getSalesRequestServiceInvoice).mockResolvedValue({ state: 'unavailable', invoice: null });
    render(<SalesRequestInvoice requestId={7} version={4} canEdit onSaved={vi.fn()} />);
    await userEvent.click(screen.getByRole('button', { name: 'Счёт услуги заявки' }));
    expect(await screen.findByText(/Связанный счёт недоступен/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Найти счёт' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Убрать связь' })).toBeNull();
  });

  it('requires an explicit share for a common invoice and saves its amount', async () => {
    const onSaved = vi.fn();
    vi.mocked(apiService.setSalesRequestServiceInvoice).mockResolvedValue({ id: 7, version: 5 } as never);
    render(<SalesRequestInvoice requestId={7} version={4} canEdit onSaved={onSaved} />);
    await userEvent.click(screen.getByRole('button', { name: 'Счёт услуги заявки' }));
    await screen.findByText('Счёт не выбран.');
    await userEvent.click(screen.getByLabelText('Общий для нескольких заявок'));
    await userEvent.click(screen.getByRole('button', { name: 'Найти счёт' }));
    await userEvent.click((await screen.findAllByRole('button', { name: 'Выбрать' }))[0]);
    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(apiService.setSalesRequestServiceInvoice).not.toHaveBeenCalled();
    await userEvent.type(screen.getByLabelText('Доля этой заявки'), '60,50');
    await userEvent.click(screen.getAllByRole('button', { name: 'Выбрать' })[0]);
    await waitFor(() => expect(apiService.setSalesRequestServiceInvoice).toHaveBeenCalledWith(7, 4, 10, 60.5));
    expect(await screen.findByText(/Общий счёт · доля заявки/)).toBeTruthy();
  });

  it('hides editing when the request is read-only', async () => {
    vi.mocked(apiService.getSalesRequestServiceInvoice).mockResolvedValue({ state: 'available', invoice: first });
    render(<SalesRequestInvoice requestId={7} version={4} canEdit={false} onSaved={vi.fn()} />);
    await userEvent.click(screen.getByRole('button', { name: 'Счёт услуги заявки' }));
    expect(await screen.findByText(/запись №10/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Найти счёт' })).toBeNull();
  });

  it('does not show an outdated search result after the query changes', async () => {
    let resolve!: (page: { items: typeof first[]; hasMore: boolean }) => void;
    vi.mocked(apiService.getSalesRequestInvoiceCandidates).mockReturnValue(new Promise((done) => { resolve = done; }));
    render(<SalesRequestInvoice requestId={7} version={4} canEdit onSaved={vi.fn()} />);
    await userEvent.click(screen.getByRole('button', { name: 'Счёт услуги заявки' }));
    await screen.findByText('Счёт не выбран.');
    await userEvent.click(screen.getByRole('button', { name: 'Найти счёт' }));
    await userEvent.type(screen.getByLabelText('Поиск счёта услуги'), 'новый');
    await act(async () => resolve({ items: [first], hasMore: false }));
    expect(screen.queryByText(/организация №1.*запись №10/)).toBeNull();
  });

  it('shows a revoked-access error without displaying invoice data', async () => {
    vi.mocked(apiService.getSalesRequestServiceInvoice).mockRejectedValue(new Error('Нет доступа к счёту.'));
    render(<SalesRequestInvoice requestId={7} version={4} canEdit onSaved={vi.fn()} />);
    await userEvent.click(screen.getByRole('button', { name: 'Счёт услуги заявки' }));
    expect((await screen.findByRole('alert')).textContent).toContain('Нет доступа к счёту.');
    expect(screen.queryByRole('button', { name: 'Найти счёт' })).toBeNull();
  });
});
