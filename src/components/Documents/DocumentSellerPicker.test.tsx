import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { DocumentSellerPicker } from './DocumentSellerPicker';
import { apiService } from '../../services/api';
import type { OwnCompanyOption } from '../../types';

const { toastErrorMock, actor, access } = vi.hoisted(() => ({
  toastErrorMock: vi.fn(), actor: { id: 7, roleId: 3 }, access: { canView: true, canEdit: true, viewScope: 'all' },
}));

vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: actor.id, role: { id: actor.roleId } } }) }));
vi.mock('../../hooks/useRolePermissions', () => ({ useRolePermissions: () => ({ accounts: access, acts: access, contracts: access }) }));

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
    vi.resetAllMocks();
    Object.assign(actor, { id: 7, roleId: 3 });
    Object.assign(access, { canView: true, canEdit: true, viewScope: 'all' });
    vi.mocked(apiService.getOwnCompanyOptions).mockResolvedValue(twoCompanies);
  });

  const ownerChanges = ['document', 'kind', 'user', 'role', 'scope', 'readonly', 'frozen', 'selection', 'draft'] as const;
  it.each(ownerChanges.flatMap(change => [true, false].map(success => ({ change, success }))))(
    'ownership: ignores a late save after $change changes (success=$success)', async ({ change, success }) => {
      let finish!: () => void;
      vi.mocked(apiService.setActSeller).mockImplementation(() => new Promise((resolve, reject) => {
        finish = () => success ? resolve({ companyId: 2, branchId: null, frozen: false }) : reject(new Error('Старый отказ'));
      }));
      const changed = vi.fn();
      const props = { kind: 'act' as const, documentId: 5, canEdit: true, onChanged: changed };
      const view = render(<DocumentSellerPicker {...props} />);
      await userEvent.setup().selectOptions(await screen.findByLabelText('Компания-продавец'), '2');
      expect(apiService.setActSeller).toHaveBeenCalledTimes(1);
      if (change === 'user') actor.id++;
      if (change === 'role') actor.roleId++;
      if (change === 'scope') access.viewScope = 'own';
      view.rerender(<DocumentSellerPicker {...props}
        documentId={change === 'document' ? 6 : 5} kind={change === 'kind' ? 'invoice' : 'act'}
        canEdit={change !== 'readonly'} frozen={change === 'frozen'} draft={change === 'draft'}
        companyId={change === 'selection' ? 1 : null} />);
      await act(async () => finish());
      expect(changed).not.toHaveBeenCalled();
      expect(toastErrorMock).not.toHaveBeenCalled();
      if (change === 'readonly' || change === 'frozen') expect(screen.queryByLabelText('Компания-продавец')).toBeNull();
      else expect((await screen.findByLabelText('Компания-продавец') as HTMLSelectElement).value).toBe(change === 'selection' ? '1' : '');
      expect(apiService.setActSeller).toHaveBeenCalledTimes(1);
    },
  );

  it.each([true, false])('ownership: ignores save completion after unmount (success=%s)', async success => {
    let finish!: () => void;
    vi.mocked(apiService.setActSeller).mockImplementation(() => new Promise((resolve, reject) => {
      finish = () => success ? resolve({ companyId: 2, branchId: null, frozen: false }) : reject(new Error('Старый отказ'));
    }));
    const changed = vi.fn();
    const view = render(<DocumentSellerPicker kind="act" documentId={5} canEdit onChanged={changed} />);
    await userEvent.setup().selectOptions(await screen.findByLabelText('Компания-продавец'), '2');
    view.unmount(); await act(async () => finish());
    expect(changed).not.toHaveBeenCalled(); expect(toastErrorMock).not.toHaveBeenCalled();
  });

  it('ownership: discards a previous catalogue after the actor changes', async () => {
    let finish!: (items: OwnCompanyOption[]) => void;
    vi.mocked(apiService.getOwnCompanyOptions).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
    const view = render(<DocumentSellerPicker kind="act" documentId={5} canEdit />);
    actor.id++;
    view.rerender(<DocumentSellerPicker kind="act" documentId={5} canEdit />);
    await screen.findByLabelText('Компания-продавец');
    await act(async () => finish([{ ...twoCompanies[0], shortName: 'Чужой старый каталог' }]));
    expect(screen.queryByText(/Чужой старый каталог/)).toBeNull();
    expect(apiService.getOwnCompanyOptions).toHaveBeenCalledTimes(2);
  });

  it.each([true, false])('ownership: an old completion cannot finish the new document save (success=%s)', async success => {
    let finishOld!: () => void; let finishNew!: () => void;
    vi.mocked(apiService.setActSeller)
      .mockImplementationOnce(() => new Promise((resolve, reject) => {
        finishOld = () => success ? resolve({ companyId: 2, branchId: null, frozen: false }) : reject(new Error('Старый отказ'));
      }))
      .mockImplementationOnce(() => new Promise(resolve => { finishNew = () => resolve({ companyId: 1, branchId: null, frozen: false }); }));
    const changed = vi.fn(); const user = userEvent.setup();
    const view = render(<DocumentSellerPicker kind="act" documentId={5} canEdit onChanged={changed} />);
    await user.selectOptions(await screen.findByLabelText('Компания-продавец'), '2');
    view.rerender(<DocumentSellerPicker kind="act" documentId={6} canEdit onChanged={changed} />);
    await user.selectOptions(await screen.findByLabelText('Компания-продавец'), '1');
    await act(async () => finishOld());
    expect((screen.getByLabelText('Компания-продавец') as HTMLSelectElement).disabled).toBe(true);
    expect(changed).not.toHaveBeenCalled(); expect(toastErrorMock).not.toHaveBeenCalled();
    await act(async () => finishNew());
    expect(changed).toHaveBeenCalledExactlyOnceWith({ companyId: 1, branchId: null });
    expect((screen.getByLabelText('Компания-продавец') as HTMLSelectElement).disabled).toBe(false);
    expect(vi.mocked(apiService.setActSeller).mock.calls).toEqual([[5, { companyId: 2, branchId: null }], [6, { companyId: 1, branchId: null }]]);
  });

  it('ownership: accepts a refreshed selection without reloading the catalogue', async () => {
    vi.mocked(apiService.setContractSeller).mockResolvedValue({ companyId: 2, branchId: null, frozen: false });
    const changed = vi.fn();
    const view = render(<DocumentSellerPicker kind="contract" documentId={8} canEdit onChanged={changed} />);
    await userEvent.setup().selectOptions(await screen.findByLabelText('Компания-продавец'), '2');
    await waitFor(() => expect(changed).toHaveBeenCalledWith({ companyId: 2, branchId: null }));
    view.rerender(<DocumentSellerPicker kind="contract" documentId={8} companyId={2} canEdit onChanged={changed} />);
    expect((screen.getByLabelText('Компания-продавец') as HTMLSelectElement).value).toBe('2');
    expect(apiService.getOwnCompanyOptions).toHaveBeenCalledTimes(1);
    expect(apiService.setContractSeller).toHaveBeenCalledExactlyOnceWith(8, { companyId: 2, branchId: null });
  });

  it.each([true, false])('ownership: rejects a result from a replaced login before React rerenders (success=%s)', async success => {
    const tokenKey = 'pp_token'; const epochKey = 'pp.auth-session-epoch.v1';
    const oldToken = localStorage.getItem(tokenKey); const oldEpoch = localStorage.getItem(epochKey);
    try {
      localStorage.setItem(tokenKey, `synthetic.${btoa(JSON.stringify({ sub: '7' }))}.signature`);
      localStorage.setItem(epochKey, 'seller-test-first-login');
      let finish!: () => void;
      vi.mocked(apiService.setActSeller).mockImplementation(() => new Promise((resolve, reject) => {
        finish = () => success ? resolve({ companyId: 2, branchId: null, frozen: false }) : reject(new Error('Старый отказ'));
      }));
      const changed = vi.fn();
      render(<DocumentSellerPicker kind="act" documentId={5} canEdit onChanged={changed} />);
      await userEvent.setup().selectOptions(await screen.findByLabelText('Компания-продавец'), '2');
      localStorage.setItem(epochKey, 'seller-test-second-login');
      await act(async () => finish());
      expect(changed).not.toHaveBeenCalled(); expect(toastErrorMock).not.toHaveBeenCalled();
    } finally {
      if (oldToken === null) localStorage.removeItem(tokenKey); else localStorage.setItem(tokenKey, oldToken);
      if (oldEpoch === null) localStorage.removeItem(epochKey); else localStorage.setItem(epochKey, oldEpoch);
    }
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
