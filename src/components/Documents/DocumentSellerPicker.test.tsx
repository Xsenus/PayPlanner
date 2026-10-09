import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { DocumentSellerPicker } from './DocumentSellerPicker';
import { apiService } from '../../services/api';
import type { OwnCompanyOption } from '../../types';

const { toastErrorMock } = vi.hoisted(() => ({ toastErrorMock: vi.fn() }));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({ error: toastErrorMock, success: vi.fn(), info: vi.fn() }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getOwnCompanyOptions: vi.fn(),
    setInvoiceSeller: vi.fn(),
    setActSeller: vi.fn(),
  },
}));

const twoCompanies: OwnCompanyOption[] = [
  { id: 1, shortName: 'ООО Главная', isDefault: true, branches: [{ id: 11, name: 'Офис СПб', kind: 'Office' }] },
  { id: 2, shortName: 'ИП Второй', isDefault: false, branches: [] },
];

describe('DocumentSellerPicker', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiService.getOwnCompanyOptions).mockResolvedValue(twoCompanies);
  });

  it('renders nothing when there is nothing to choose', async () => {
    vi.mocked(apiService.getOwnCompanyOptions).mockResolvedValue([
      { id: 1, shortName: 'Одна', isDefault: true, branches: [] },
    ]);
    const { container } = render(<DocumentSellerPicker kind="act" documentId={5} canEdit />);

    await waitFor(() => expect(apiService.getOwnCompanyOptions).toHaveBeenCalled());
    expect(container.textContent).toBe('');
  });

  it('saves the chosen company for an act', async () => {
    const user = userEvent.setup();
    vi.mocked(apiService.setActSeller).mockResolvedValue({ companyId: 2, branchId: null, frozen: false });
    const onChanged = vi.fn();
    render(<DocumentSellerPicker kind="act" documentId={5} canEdit onChanged={onChanged} />);

    const select = (await screen.findByLabelText('Компания-продавец')) as HTMLSelectElement;
    await user.selectOptions(select, '2');

    await waitFor(() => expect(apiService.setActSeller).toHaveBeenCalledWith(5, { companyId: 2, branchId: null }));
    expect(onChanged).toHaveBeenCalledWith({ companyId: 2, branchId: null });
  });

  it('saves a branch of the default company for an invoice', async () => {
    const user = userEvent.setup();
    vi.mocked(apiService.setInvoiceSeller).mockResolvedValue({ companyId: 1, branchId: 11, frozen: false });
    render(<DocumentSellerPicker kind="invoice" documentId={7} canEdit />);

    const branch = (await screen.findByLabelText('Подразделение-продавец')) as HTMLSelectElement;
    await user.selectOptions(branch, '11');

    await waitFor(() => expect(apiService.setInvoiceSeller).toHaveBeenCalledWith(7, { companyId: 1, branchId: 11 }));
  });

  it('reverts the selection and reports an error when saving fails', async () => {
    const user = userEvent.setup();
    vi.mocked(apiService.setActSeller).mockRejectedValue(new Error('конфликт'));
    render(<DocumentSellerPicker kind="act" documentId={5} canEdit />);

    const select = (await screen.findByLabelText('Компания-продавец')) as HTMLSelectElement;
    await user.selectOptions(select, '2');

    await waitFor(() => expect(toastErrorMock).toHaveBeenCalled());
    expect(select.value).toBe('');
  });

  it('is read-only once the document requisites are frozen', async () => {
    render(<DocumentSellerPicker kind="act" documentId={5} companyId={2} frozen canEdit />);

    const text = await screen.findByTestId('document-seller-readonly');
    expect(text.textContent).toContain('ИП Второй');
    expect(text.textContent).toContain('зафиксированы');
    expect(screen.queryByLabelText('Компания-продавец')).toBeNull();
  });
});
