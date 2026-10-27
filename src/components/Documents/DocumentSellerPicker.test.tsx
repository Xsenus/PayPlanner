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
    setContractSeller: vi.fn(),
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

  it.each(['invoice', 'act', 'contract'] as const)('keeps %s creation selection local until the document is created', async (kind) => {
    const user = userEvent.setup();
    const changed = vi.fn();
    render(<DocumentSellerPicker kind={kind} documentId={0} draft canEdit onChanged={changed} />);

    await user.selectOptions(await screen.findByLabelText('Подразделение-продавец'), '11');
    expect(changed).toHaveBeenLastCalledWith({ companyId: 1, branchId: 11 });
    await user.selectOptions(screen.getByLabelText('Компания-продавец'), '2');
    expect(changed).toHaveBeenLastCalledWith({ companyId: 2, branchId: null });
    expect(apiService.setInvoiceSeller).not.toHaveBeenCalled();
    expect(apiService.setActSeller).not.toHaveBeenCalled();
    expect(apiService.setContractSeller).not.toHaveBeenCalled();
  });

  it('shows frozen names and a real freeze date when the live company catalogue is unavailable', async () => {
    vi.mocked(apiService.getOwnCompanyOptions).mockRejectedValue(new Error('403'));
    render(<DocumentSellerPicker kind="invoice" documentId={7} companyId={1} branchId={11} frozen canEdit={false}
      snapshotInfo={{ companyName: 'Историческое имя', branchName: 'Прежний офис', frozenAtUtc: '2026-10-24T01:02:03Z', freezeReason: 'on-create', legacyImages: false }}
      timeZoneId="UTC" />);

    const text = await screen.findByTestId('document-seller-readonly');
    expect((text.textContent ?? '').replace(/\s+/g, ' ').trim()).toContain('Историческое имя · Прежний офис');
    expect((text.textContent ?? '').replace(/\s+/g, ' ').trim()).toContain('при создании');
    expect(text.querySelector('time')?.getAttribute('dateTime')).toBe('2026-10-24T01:02:03Z');
    expect(screen.queryByLabelText('Компания-продавец')).toBeNull();
    expect(apiService.getOwnCompanyOptions).not.toHaveBeenCalled();
  });

  it('labels legacy images honestly and never invents a freeze date', async () => {
    render(<DocumentSellerPicker kind="act" documentId={5} frozen canEdit={false}
      snapshotInfo={{ companyName: 'Старый продавец', frozenAtUtc: null, freezeReason: null, legacyImages: true }} />);

    const text = await screen.findByTestId('document-seller-readonly');
    expect((text.textContent ?? '').replace(/\s+/g, ' ').trim()).toContain('изображения могут браться из профиля');
    expect((text.textContent ?? '').replace(/\s+/g, ' ').trim()).toContain('дата фиксации неизвестна');
    expect(text.querySelector('time')).toBeNull();
  });

  it('labels an unknown date independently of already frozen images in an older snapshot', async () => {
    render(<DocumentSellerPicker kind="act" documentId={6} frozen canEdit={false}
      snapshotInfo={{ companyName: 'Продавец с сохранёнными изображениями', frozenAtUtc: null, freezeReason: null, legacyImages: false }} />);

    const text = await screen.findByTestId('document-seller-readonly');
    expect((text.textContent ?? '').replace(/\s+/g, ' ').trim()).toContain('Продавец с сохранёнными изображениями');
    expect((text.textContent ?? '').replace(/\s+/g, ' ').trim()).toContain('дата фиксации неизвестна');
    expect((text.textContent ?? '').replace(/\s+/g, ' ').trim()).not.toContain('изображения могут браться из профиля');
    expect(text.querySelector('time')).toBeNull();
    expect(screen.queryByLabelText('Компания-продавец')).toBeNull();
    expect(apiService.getOwnCompanyOptions).not.toHaveBeenCalled();
  });
});
