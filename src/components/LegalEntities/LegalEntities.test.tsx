import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { LegalEntities } from './LegalEntities';
import { defaultRolePermissions } from '../../types/permissions';
import type { LegalEntityDetail, LegalEntityInput } from '../../types';
import { StaleUiOperationError } from '../../utils/StaleUiOperationError';
import { documentCopyTestSession } from '../../test/documentCopySession';

type OwnerModalProps = { isOpen: boolean; initial?: LegalEntityDetail | null; onBack: () => void; onClose: () => void; onDelete?: () => Promise<void>; onSubmit: (payload: LegalEntityInput) => Promise<LegalEntityDetail> };
const modalSpy = vi.hoisted(() => vi.fn());
const authUser = vi.hoisted(() => ({ id: 1, role: { id: 10, name: 'limited-lawyer' } }));

const apiMocks = vi.hoisted(() => ({
  getLegalEntitiesPaged: vi.fn(),
  getLegalEntity: vi.fn(),
  createLegalEntity: vi.fn(),
  updateLegalEntity: vi.fn(),
  deleteLegalEntity: vi.fn(),
}));

const permissionMocks = vi.hoisted(() => ({
  useRolePermissions: vi.fn(),
}));

const toastMocks = vi.hoisted(() => ({
  error: vi.fn(),
  success: vi.fn(),
}));

const dialogMocks = vi.hoisted(() => ({
  confirm: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getLegalEntitiesPaged: apiMocks.getLegalEntitiesPaged,
    getLegalEntity: apiMocks.getLegalEntity,
    createLegalEntity: apiMocks.createLegalEntity,
    updateLegalEntity: apiMocks.updateLegalEntity,
    deleteLegalEntity: apiMocks.deleteLegalEntity,
    getUserSetting: vi.fn().mockResolvedValue(null),
    upsertUserSetting: vi.fn().mockResolvedValue(null),
    suggestLegalEntities: vi.fn(),
    findLegalEntityById: vi.fn(),
  },
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: authUser,
  }),
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: permissionMocks.useRolePermissions,
}));

vi.mock('../../hooks/useTranslation', () => ({
  useTranslation: () => ({ t: () => '' }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => dialogMocks,
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => toastMocks,
}));

vi.mock('../ui/TablePagination', () => ({
  TablePagination: ({ page, totalPages, onPageChange }: {
    page: number;
    totalPages: number;
    onPageChange: (page: number) => void;
  }) => (
    <button type="button" disabled={page >= totalPages} onClick={() => onPageChange(page + 1)}>
      Вперед
    </button>
  ),
}));

vi.mock('../ui/TableColumnSettings', () => ({
  TableColumnSettings: ({ columns }: { columns: Array<{ key: string; label: string }> }) => (
    <div aria-label="Настройки колонок">
      {columns.map((column) => (
        <span key={column.key}>{column.label}</span>
      ))}
    </div>
  ),
}));

vi.mock('./LegalEntityModal', () => ({
  LegalEntityModal: (props: OwnerModalProps) => {
    modalSpy(props);
    return props.isOpen ? <button type="button" onClick={props.onBack}>Назад из карточки</button> : null;
  },
}));

const legalEntity = {
  id: 7,
  shortName: 'ООО Ромашка',
  fullName: 'Общество с ограниченной ответственностью Ромашка',
  inn: '5400000000',
  kpp: '540001001',
  ogrn: '1234567890123',
  director: 'Иванов И.И.',
  phone: '+7 999 000-00-00',
  email: 'mail@example.test',
  address: 'Новосибирск',
  clientsCount: 1,
  casesCount: 1,
  contractsCount: 1,
  invoicesCount: 1,
  actsCount: 1,
  paymentsCount: 1,
  clients: [{ id: 1, name: 'Клиент' }],
  relatedCases: [{ id: 11, label: 'Дело №11', clientId: 1 }],
  relatedContracts: [{ id: 21, label: 'Договор №21' }],
  relatedInvoices: [{ id: 31, label: 'Счет №31', type: 'income' }],
  relatedActs: [{ id: 41, label: 'Акт №41' }],
  relatedPayments: [{ id: 51, label: 'Платеж №51', type: 'income' }],
  createdAt: '2026-06-25T00:00:00Z',
  updatedAt: '2026-06-25T01:00:00Z',
};

describe('LegalEntities permissions', () => {
  describe('deletion guard', () => {
    let session: ReturnType<typeof documentCopyTestSession>;
    beforeEach(() => {
      session = documentCopyTestSession();
      apiMocks.getLegalEntity.mockImplementation(async (id: number) => ({ ...legalEntity, id, shortName: `Company ${id} user ${authUser.id}` }));
    });
    afterEach(() => session.restore());
    const permissions = () => permissionMocks.useRolePermissions.mock.results.at(-1)!.value as typeof defaultRolePermissions;
    const changePermission = (values: Partial<typeof defaultRolePermissions.legalEntities>) => {
      const current = permissions(); permissionMocks.useRolePermissions.mockReturnValue({ ...current, legalEntities: { ...current.legalEntities, ...values } });
    };
    const opened = async () => {
      await waitFor(() => expect((modalSpy.mock.lastCall?.[0] as OwnerModalProps)?.onDelete).toBeTypeOf('function'));
      return modalSpy.mock.lastCall![0] as OwnerModalProps;
    };

    it('deletion generation: a changed role name invalidates its callback without changing the role ID', async () => {
      const props = { initialLegalEntityId: 7, detailOnly: true }; const view = render(<LegalEntities {...props} />); const original = await opened();
      const scope = (original as OwnerModalProps & { deleteOwnerScope: string }).deleteOwnerScope;
      authUser.role.name = 'Changed role'; view.rerender(<LegalEntities {...props} />);
      expect((modalSpy.mock.lastCall![0] as OwnerModalProps & { deleteOwnerScope: string }).deleteOwnerScope).not.toBe(scope);
      await act(async () => { await original.onDelete!().catch(() => undefined); }); expect(apiMocks.deleteLegalEntity).not.toHaveBeenCalled();
    });

    it('deletion generation: an old detail response cannot clear a newer request lock', async () => {
      let finishFirst!: () => void; let finishSecond!: () => void;
      apiMocks.deleteLegalEntity.mockReturnValueOnce(new Promise<void>(resolve => { finishFirst = resolve; })).mockReturnValueOnce(new Promise<void>(resolve => { finishSecond = resolve; }));
      const props = { initialLegalEntityId: 7, detailOnly: true }; const view = render(<LegalEntities {...props} />); const first = await opened();
      let a!: Promise<unknown>; act(() => { a = first.onDelete!().catch(error => error); });
      view.rerender(<LegalEntities {...props} initialLegalEntityId={8} />);
      await waitFor(() => expect((modalSpy.mock.lastCall![0] as OwnerModalProps).initial?.id).toBe(8)); const second = await opened();
      let b!: Promise<void>; act(() => { b = second.onDelete!(); });
      await act(async () => { finishFirst(); expect(await a).toBeInstanceOf(StaleUiOperationError); });
      await expect(second.onDelete!()).rejects.toThrow('уже выполняется'); expect(apiMocks.deleteLegalEntity).toHaveBeenCalledTimes(2);
      await act(async () => { finishSecond(); await b; }); expect(toastMocks.success).toHaveBeenCalledOnce();
    });

    it.each(['close', 'company', 'permission', 'visibility', 'user', 'same-id-data', 'permission-aba', 'session'])('blocks a retained detail callback after %s', async change => {
      const props = { initialLegalEntityId: 7, detailOnly: true }; const view = render(<LegalEntities {...props} />); const original = await opened();
      if (change === 'close') act(() => original.onBack());
      if (change === 'company') view.rerender(<LegalEntities {...props} initialLegalEntityId={8} />);
      if (change === 'permission') { changePermission({ canDelete: false }); view.rerender(<LegalEntities {...props} />); }
      if (change === 'visibility') { changePermission({ viewScope: 'own' }); view.rerender(<LegalEntities {...props} />); }
      if (change === 'user') { authUser.id++; view.rerender(<LegalEntities {...props} />); }
      if (change === 'same-id-data') {
        apiMocks.updateLegalEntity.mockResolvedValue({ ...legalEntity, shortName: 'Changed same company' });
        await act(async () => { await original.onSubmit({ shortName: 'Changed same company', clientIds: [] }); });
        toastMocks.success.mockClear();
      }
      if (change === 'permission-aba') { changePermission({ canDelete: false }); view.rerender(<LegalEntities {...props} />); changePermission({ canDelete: true }); view.rerender(<LegalEntities {...props} />); }
      if (change === 'session') session.replace();
      const before = apiMocks.getLegalEntitiesPaged.mock.calls.length;
      let result: unknown;
      await act(async () => { result = await original.onDelete!().catch(error => error); });
      expect(result).toBeInstanceOf(StaleUiOperationError); expect(apiMocks.deleteLegalEntity).not.toHaveBeenCalled();
      expect(toastMocks.success).not.toHaveBeenCalled(); expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(before);
    });

    for (const outcome of ['success', 'failure']) it.each(['company', 'user', 'permission', 'session'])(`discards a late detail ${outcome} after %s`, async change => {
      let finish!: () => void; let fail!: (error: Error) => void;
      apiMocks.deleteLegalEntity.mockReturnValue(new Promise<void>((resolve, reject) => { finish = resolve; fail = reject; }));
      const onDetailClose = vi.fn(); const props = { initialLegalEntityId: 7, detailOnly: true, onDetailClose };
      const view = render(<LegalEntities {...props} />); const original = await opened();
      let pending!: Promise<unknown>;
      act(() => { pending = original.onDelete!().catch(error => error); });
      expect(apiMocks.deleteLegalEntity).toHaveBeenCalledOnce();
      if (change === 'company') view.rerender(<LegalEntities {...props} initialLegalEntityId={8} />);
      if (change === 'user') { authUser.id++; view.rerender(<LegalEntities {...props} />); }
      if (change === 'permission') { changePermission({ canDelete: false }); view.rerender(<LegalEntities {...props} />); }
      if (change === 'session') session.replace();
      await act(async () => { if (outcome === 'success') finish(); else fail(new Error('Old parent deletion failed')); expect(await pending).toBeInstanceOf(StaleUiOperationError); });
      expect(onDetailClose).not.toHaveBeenCalled(); expect(toastMocks.success).not.toHaveBeenCalled(); expect(toastMocks.error).not.toHaveBeenCalled();
      expect(apiMocks.getLegalEntitiesPaged).not.toHaveBeenCalled();
    });

    it('lets only the owned form close a detail-only deletion, without loading a hidden registry', async () => {
      const onDetailClose = vi.fn(); render(<LegalEntities initialLegalEntityId={7} detailOnly onDetailClose={onDetailClose} />); const original = await opened();
      await act(async () => { await original.onDelete!(); });
      expect(apiMocks.deleteLegalEntity).toHaveBeenCalledExactlyOnceWith(7); expect(toastMocks.success).toHaveBeenCalledOnce();
      expect(onDetailClose).not.toHaveBeenCalled(); expect(apiMocks.getLegalEntitiesPaged).not.toHaveBeenCalled();
      act(() => original.onClose()); expect(onDetailClose).toHaveBeenCalledOnce(); expect(apiMocks.getLegalEntitiesPaged).not.toHaveBeenCalled();
    });

    it('reloads the registry once after the owning detail form closes', async () => {
      const onDetailClose = vi.fn(); render(<LegalEntities onDetailClose={onDetailClose} />);
      fireEvent.click(await screen.findByRole('button', { name: legalEntity.shortName }));
      const original = await opened(); const reads = apiMocks.getLegalEntitiesPaged.mock.calls.length;
      await act(async () => { await original.onDelete!(); });
      expect(onDetailClose).not.toHaveBeenCalled(); expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(reads);
      act(() => original.onClose());
      await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(reads + 1)); expect(onDetailClose).toHaveBeenCalledOnce();
    });

    it.each(['permission', 'scope', 'search', 'view', 'unmount', 'session'])('drops direct confirmation after %s', async change => {
      let approve!: (value: boolean) => void;
      dialogMocks.confirm.mockReturnValue(new Promise<boolean>(resolve => { approve = resolve; }));
      const view = render(<LegalEntities />);
      fireEvent.click(await screen.findByRole('button', { name: `Удалить ${legalEntity.shortName}` }));
      if (change === 'permission') { changePermission({ canDelete: false }); view.rerender(<LegalEntities />); }
      if (change === 'scope') { changePermission({ viewScope: 'own' }); view.rerender(<LegalEntities />); }
      if (change === 'search') fireEvent.change(screen.getByPlaceholderText('Поиск по названию, ИНН, директору'), { target: { value: 'Different company' } });
      if (change === 'view') fireEvent.click(screen.getByRole('button', { name: 'Таблица' }));
      if (change === 'unmount') view.unmount();
      if (change === 'session') session.replace('logout');
      await act(async () => { approve(true); });
      expect(apiMocks.deleteLegalEntity).not.toHaveBeenCalled(); expect(toastMocks.success).not.toHaveBeenCalled(); expect(toastMocks.error).not.toHaveBeenCalled();
    });

    it.each(['cards', 'table'])('blocks duplicate direct requests and refreshes once in %s', async mode => {
      let finish!: () => void; apiMocks.deleteLegalEntity.mockReturnValue(new Promise<void>(resolve => { finish = resolve; }));
      dialogMocks.confirm.mockResolvedValue(true); const view = render(<LegalEntities />);
      await screen.findByRole('button', { name: `Удалить ${legalEntity.shortName}` });
      if (mode === 'table') fireEvent.click(screen.getByRole('button', { name: 'Таблица' }));
      const button = screen.getByRole('button', { name: `Удалить ${legalEntity.shortName}` }); fireEvent.click(button); fireEvent.click(button);
      await waitFor(() => expect(apiMocks.deleteLegalEntity).toHaveBeenCalledExactlyOnceWith(7));
      expect(dialogMocks.confirm).toHaveBeenCalledOnce(); expect(button).toHaveProperty('disabled', true);
      session.refresh(); view.rerender(<LegalEntities />);
      const reads = apiMocks.getLegalEntitiesPaged.mock.calls.length;
      await act(async () => { finish(); });
      await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(reads + 1)); expect(toastMocks.success).toHaveBeenCalledOnce();
    });

    it('invalidates a direct confirmation when pagination changes', async () => {
      let approve!: (value: boolean) => void;
      dialogMocks.confirm.mockReturnValue(new Promise<boolean>(resolve => { approve = resolve; }));
      apiMocks.getLegalEntitiesPaged.mockResolvedValue({ items: [legalEntity], total: 24, page: 1, pageSize: 12 });
      render(<LegalEntities />); fireEvent.click(await screen.findByRole('button', { name: `Удалить ${legalEntity.shortName}` }));
      fireEvent.click(screen.getByRole('button', { name: 'Вперед' }));
      await act(async () => { approve(true); });
      expect(apiMocks.deleteLegalEntity).not.toHaveBeenCalled(); expect(toastMocks.success).not.toHaveBeenCalled();
    });

    it.each(['cancel', 'confirmation-error', 'server-error'])('retains the direct list after %s', async outcome => {
      dialogMocks.confirm.mockResolvedValue(outcome !== 'cancel');
      if (outcome === 'confirmation-error') dialogMocks.confirm.mockRejectedValue(new Error('Cannot confirm'));
      if (outcome === 'server-error') apiMocks.deleteLegalEntity.mockRejectedValue(new Error('Company busy'));
      render(<LegalEntities />); const button = await screen.findByRole('button', { name: `Удалить ${legalEntity.shortName}` });
      const reads = apiMocks.getLegalEntitiesPaged.mock.calls.length; fireEvent.click(button);
      if (outcome !== 'cancel') await waitFor(() => expect(toastMocks.error).toHaveBeenCalledOnce());
      await waitFor(() => expect(button).toHaveProperty('disabled', false));
      expect(apiMocks.deleteLegalEntity).toHaveBeenCalledTimes(outcome === 'server-error' ? 1 : 0); expect(toastMocks.success).not.toHaveBeenCalled();
      expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(reads); expect(screen.getByRole('button', { name: legalEntity.shortName })).toBeTruthy();
    });

    it.each(['success', 'failure'])('ignores a late direct %s after another company opens', async outcome => {
      let finish!: () => void; let fail!: (error: Error) => void;
      apiMocks.deleteLegalEntity.mockReturnValue(new Promise<void>((resolve, reject) => { finish = resolve; fail = reject; }));
      dialogMocks.confirm.mockResolvedValue(true); const view = render(<LegalEntities />);
      fireEvent.click(await screen.findByRole('button', { name: `Удалить ${legalEntity.shortName}` }));
      await waitFor(() => expect(apiMocks.deleteLegalEntity).toHaveBeenCalledOnce());
      view.rerender(<LegalEntities initialLegalEntityId={8} detailOnly />);
      await opened(); const reads = apiMocks.getLegalEntitiesPaged.mock.calls.length;
      await act(async () => { if (outcome === 'success') finish(); else fail(new Error('Old direct deletion failed')); });
      expect((modalSpy.mock.lastCall![0] as OwnerModalProps).initial?.id).toBe(8);
      expect(toastMocks.success).not.toHaveBeenCalled(); expect(toastMocks.error).not.toHaveBeenCalled(); expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(reads);
    });
  });

  beforeEach(() => {
    modalSpy.mockClear(); toastMocks.success.mockClear(); toastMocks.error.mockClear();
    authUser.id = 1; authUser.role.id = 10; authUser.role.name = 'limited-lawyer';
    apiMocks.createLegalEntity.mockReset(); apiMocks.updateLegalEntity.mockReset();
    apiMocks.deleteLegalEntity.mockReset().mockResolvedValue(undefined);
    apiMocks.getLegalEntitiesPaged.mockReset();
    apiMocks.getLegalEntity.mockReset();
    dialogMocks.confirm.mockReset();
    dialogMocks.confirm.mockResolvedValue(false);
    apiMocks.getLegalEntity.mockResolvedValue(legalEntity);
    apiMocks.getLegalEntitiesPaged.mockResolvedValue({
      items: [legalEntity],
      total: 1,
      page: 1,
      pageSize: 12,
    });
    permissionMocks.useRolePermissions.mockReturnValue({
      ...defaultRolePermissions,
      legalEntities: {
        ...defaultRolePermissions.legalEntities,
        canView: true,
        canCreate: true,
        canEdit: true,
        canDelete: true,
      },
      cases: { ...defaultRolePermissions.cases, canView: false },
      contracts: { ...defaultRolePermissions.contracts, canView: false },
      accounts: { ...defaultRolePermissions.accounts, canView: false },
      acts: { ...defaultRolePermissions.acts, canView: false },
      payments: { ...defaultRolePermissions.payments, canView: false },
    });
    window.localStorage.clear();
  });

  it('opens a legal entity directly without loading or showing the registry', async () => {
    const onDetailClose = vi.fn();

    render(<LegalEntities initialLegalEntityId={legalEntity.id} detailOnly onDetailClose={onDetailClose} />);

    await waitFor(() => expect(apiMocks.getLegalEntity).toHaveBeenCalledWith(legalEntity.id));
    expect(apiMocks.getLegalEntitiesPaged).not.toHaveBeenCalled();
    expect(screen.queryByRole('heading', { name: 'Юридические лица' })).toBeNull();

    await userEvent.click(await screen.findByRole('button', { name: 'Назад из карточки' }));
    expect(onDetailClose).toHaveBeenCalledTimes(1);
  });

  it('deletion ownership: a retained detail callback cannot send after a role change', async () => {
    const session = documentCopyTestSession();
    try {
      const props = { initialLegalEntityId: 7, detailOnly: true };
      const view = render(<LegalEntities {...props} />);
      await waitFor(() => expect((modalSpy.mock.lastCall?.[0] as OwnerModalProps)?.onDelete).toBeTypeOf('function'));
      const original = modalSpy.mock.lastCall![0] as OwnerModalProps;
      authUser.role.id++;
      view.rerender(<LegalEntities {...props} />);
      await act(async () => { await original.onDelete!().catch(() => undefined); });
      expect(apiMocks.deleteLegalEntity).not.toHaveBeenCalled();
      expect(toastMocks.success).not.toHaveBeenCalled();
    } finally { session.restore(); }
  });

  it('deletion ownership: direct confirmation belongs to the original user', async () => {
    const session = documentCopyTestSession();
    try {
      let approve!: (value: boolean) => void;
      dialogMocks.confirm.mockReturnValue(new Promise<boolean>(resolve => { approve = resolve; }));
      const view = render(<LegalEntities />);
      fireEvent.click(await screen.findByRole('button', { name: `Удалить ${legalEntity.shortName}` }));
      expect(dialogMocks.confirm).toHaveBeenCalledOnce();
      authUser.id++;
      view.rerender(<LegalEntities />);
      await act(async () => { approve(true); });
      expect(apiMocks.deleteLegalEntity).not.toHaveBeenCalled();
      expect(toastMocks.success).not.toHaveBeenCalled();
    } finally { session.restore(); }
  });

  it.each(['close', 'company', 'permission', 'visibility', 'user', 'role', 'unmount'] as const)('parent ownership: ignores a saved entity after %s', async change => {
    apiMocks.getLegalEntity.mockImplementation(async (id: number) => ({ ...legalEntity, id, shortName: `Company ${id}` }));
    let finish!: (value: LegalEntityDetail) => void;
    apiMocks.updateLegalEntity.mockReturnValue(new Promise<LegalEntityDetail>(resolve => { finish = resolve; }));
    const props = { initialLegalEntityId: 7, detailOnly: true };
    const view = render(<LegalEntities {...props} />);
    await waitFor(() => expect(modalSpy).toHaveBeenCalled());
    const original = modalSpy.mock.calls.at(-1)![0] as OwnerModalProps;
    let pending!: Promise<LegalEntityDetail | Error>;
    act(() => { pending = original.onSubmit({ shortName: 'Late old result', clientIds: [] }).catch(error => error); });
    await waitFor(() => expect(apiMocks.updateLegalEntity).toHaveBeenCalledTimes(1));
    if (change === 'close') act(() => original.onBack());
    if (change === 'company') { view.rerender(<LegalEntities {...props} initialLegalEntityId={8} />); await waitFor(() => expect((modalSpy.mock.calls.at(-1)![0] as OwnerModalProps).initial?.id).toBe(8)); }
    if (change === 'permission') { permissionMocks.useRolePermissions.mock.results.at(-1)!.value.legalEntities.canEdit = false; view.rerender(<LegalEntities {...props} />); }
    if (change === 'visibility') { permissionMocks.useRolePermissions.mock.results.at(-1)!.value.legalEntities.viewScope = 'own'; view.rerender(<LegalEntities {...props} />); }
    if (change === 'user') { authUser.id = 2; view.rerender(<LegalEntities {...props} />); }
    if (change === 'role') { authUser.role.id = 11; view.rerender(<LegalEntities {...props} />); }
    if (change === 'unmount') view.unmount();
    await act(async () => { finish({ ...legalEntity, shortName: 'Late old result' } as LegalEntityDetail); expect(await pending).toBeInstanceOf(StaleUiOperationError); });
    expect(toastMocks.success).not.toHaveBeenCalled();
    expect(apiMocks.getLegalEntitiesPaged).not.toHaveBeenCalled();
    expect((modalSpy.mock.calls.at(-1)![0] as OwnerModalProps).initial?.shortName).not.toBe('Late old result');
    if (change === 'company') expect((modalSpy.mock.calls.at(-1)![0] as OwnerModalProps).initial?.id).toBe(8);
    if (change === 'close') expect(screen.queryByRole('button', { name: 'Назад из карточки' })).toBeNull();
  });

  it.each(['success', 'error'] as const)('parent ownership: discards obsolete %s opening response', async outcome => {
    let finish!: (value: LegalEntityDetail) => void; let fail!: (error: Error) => void;
    const first = new Promise<LegalEntityDetail>((resolve, reject) => { finish = resolve; fail = reject; });
    apiMocks.getLegalEntity.mockImplementation((id: number) => id === 7 ? first : Promise.resolve({ ...legalEntity, id: 8 }));
    const view = render(<LegalEntities initialLegalEntityId={7} detailOnly />);
    await waitFor(() => expect(apiMocks.getLegalEntity).toHaveBeenCalledWith(7));
    view.rerender(<LegalEntities initialLegalEntityId={8} detailOnly />);
    await waitFor(() => expect((modalSpy.mock.calls.at(-1)?.[0] as OwnerModalProps | undefined)?.initial?.id).toBe(8));
    await act(async () => { if (outcome === 'success') finish(legalEntity as LegalEntityDetail); else fail(new Error('Obsolete load error')); await first.catch(() => undefined); });
    expect((modalSpy.mock.calls.at(-1)![0] as OwnerModalProps).initial?.id).toBe(8);
    expect(toastMocks.error).not.toHaveBeenCalled();
  });

  it('parent ownership: accepts a normal update but rejects a held callback after close', async () => {
    apiMocks.updateLegalEntity.mockResolvedValue({ ...legalEntity, shortName: 'Current saved name' });
    render(<LegalEntities initialLegalEntityId={7} detailOnly />);
    await waitFor(() => expect(modalSpy).toHaveBeenCalled());
    const props = modalSpy.mock.calls.at(-1)![0] as OwnerModalProps;
    await act(async () => { await props.onSubmit({ shortName: 'Current saved name', clientIds: [] }); });
    expect((modalSpy.mock.calls.at(-1)![0] as OwnerModalProps).initial?.shortName).toBe('Current saved name');
    expect(toastMocks.success).toHaveBeenCalledTimes(1);
    const current = modalSpy.mock.calls.at(-1)![0] as OwnerModalProps;
    act(() => current.onBack());
    await expect(current.onSubmit({ shortName: 'Must not write', clientIds: [] })).rejects.toBeInstanceOf(StaleUiOperationError);
    expect(apiMocks.updateLegalEntity).toHaveBeenCalledTimes(1);
  });

  it('parent ownership: accepts a created registry entry with its new ID', async () => {
    apiMocks.createLegalEntity.mockResolvedValue({ ...legalEntity, id: 8, shortName: 'New registry company' });
    render(<LegalEntities />);
    await userEvent.click(await screen.findByRole('button', { name: 'Добавить юр. лицо' }));
    const props = modalSpy.mock.calls.at(-1)![0] as OwnerModalProps;
    expect(props.initial).toBeNull();
    await act(async () => { await props.onSubmit({ shortName: 'New registry company', clientIds: [] }); });
    expect(apiMocks.createLegalEntity).toHaveBeenCalledTimes(1);
    expect((modalSpy.mock.calls.at(-1)![0] as OwnerModalProps).initial?.id).toBe(8);
    expect(toastMocks.success).toHaveBeenCalledWith('Юр. лицо создано');
    await expect(props.onSubmit({ shortName: 'Old create callback', clientIds: [] })).rejects.toBeInstanceOf(StaleUiOperationError);
    expect(apiMocks.createLegalEntity).toHaveBeenCalledTimes(1);
  });

  it('parent ownership: cannot return an old success after a role change during registry refresh', async () => {
    apiMocks.getLegalEntity.mockImplementation(async () => ({ ...legalEntity, shortName: `Fresh role ${authUser.role.id}` }));
    apiMocks.updateLegalEntity.mockResolvedValue({ ...legalEntity, shortName: 'Acknowledged previous role write' });
    let finish!: (value: unknown) => void;
    apiMocks.getLegalEntitiesPaged.mockReturnValue(new Promise(resolve => { finish = resolve; }));
    const view = render(<LegalEntities initialLegalEntityId={7} detailOnly />);
    await waitFor(() => expect(modalSpy).toHaveBeenCalled());
    const original = modalSpy.mock.calls.at(-1)![0] as OwnerModalProps;
    let pending!: Promise<unknown>;
    act(() => { pending = original.onSubmit({ shortName: 'Acknowledged previous role write', clientIds: [] }).catch(error => error); });
    await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(1));
    authUser.role.id = 11; view.rerender(<LegalEntities initialLegalEntityId={7} detailOnly />);
    await waitFor(() => expect((modalSpy.mock.calls.at(-1)![0] as OwnerModalProps).initial?.shortName).toBe('Fresh role 11'));
    await act(async () => { finish({ items: [{ ...legalEntity, shortName: 'Previous role registry row' }], total: 1, page: 1, pageSize: 12 }); expect(await pending).toBeInstanceOf(StaleUiOperationError); });
    expect((modalSpy.mock.calls.at(-1)![0] as OwnerModalProps).initial?.shortName).toBe('Fresh role 11');
  });

  it.each(['read', 'save'] as const)('parent ownership: refuses a wrong entity ID returned during %s', async operation => {
    if (operation === 'read') apiMocks.getLegalEntity.mockResolvedValue({ ...legalEntity, id: 8 });
    else apiMocks.updateLegalEntity.mockResolvedValue({ ...legalEntity, id: 8 });
    render(<LegalEntities initialLegalEntityId={7} detailOnly />);
    if (operation === 'read') {
      await waitFor(() => expect(toastMocks.error).toHaveBeenCalled());
      expect(modalSpy).not.toHaveBeenCalled();
    } else {
      await waitFor(() => expect(modalSpy).toHaveBeenCalled());
      const original = modalSpy.mock.calls.at(-1)![0] as OwnerModalProps;
      await expect(original.onSubmit({ shortName: 'Wrong response', clientIds: [] })).rejects.toThrow('другую карточку');
      expect((modalSpy.mock.calls.at(-1)![0] as OwnerModalProps).initial?.id).toBe(7);
      expect(toastMocks.success).not.toHaveBeenCalled();
    }
  });

  it('hides related finance and case columns when role cannot view those sections', async () => {
    const openPayment = vi.fn();

    render(<LegalEntities onOpenPayment={openPayment} />);

    await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalled());
    await userEvent.click(screen.getByRole('button', { name: /Таблица/ }));

    expect(screen.queryByRole('columnheader', { name: /Дела/ })).toBeNull();
    expect(screen.queryByRole('columnheader', { name: /Договоры/ })).toBeNull();
    expect(screen.queryByRole('columnheader', { name: /Счета/ })).toBeNull();
    expect(screen.queryByRole('columnheader', { name: /Акты/ })).toBeNull();
    expect(screen.queryByRole('columnheader', { name: /Платежи/ })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Платеж №51' })).toBeNull();
    expect(openPayment).not.toHaveBeenCalled();

    await userEvent.click(screen.getByRole('button', { name: /Колонки/ }));
    const columnSettings = screen.getByLabelText('Настройки колонок');
    expect(columnSettings.textContent).not.toContain('Дела');
    expect(columnSettings.textContent).not.toContain('Договоры');
    expect(columnSettings.textContent).not.toContain('Счета');
    expect(columnSettings.textContent).not.toContain('Акты');
    expect(columnSettings.textContent).not.toContain('Платежи');
  });

  it('combines rapid search input into one request with an abort signal', async () => {
    render(<LegalEntities />);
    await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(1));
    apiMocks.getLegalEntitiesPaged.mockClear();

    const searchInput = screen.getByPlaceholderText('Поиск по названию, ИНН, директору');
    fireEvent.change(searchInput, { target: { value: 'ро' } });
    fireEvent.change(searchInput, { target: { value: 'рома' } });
    fireEvent.change(searchInput, { target: { value: 'ромашка' } });

    expect(apiMocks.getLegalEntitiesPaged).not.toHaveBeenCalled();
    await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(1));
    expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledWith(
      expect.objectContaining({ search: 'ромашка', page: 1, pageSize: 12 }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
  });

  it('aborts an active request when the search changes', async () => {
    render(<LegalEntities />);
    await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(1));
    apiMocks.getLegalEntitiesPaged.mockClear();
    const signals: AbortSignal[] = [];
    const resolvePending: Array<(response: {
      items: Array<typeof legalEntity>;
      total: number;
      page: number;
      pageSize: number;
    }) => void> = [];
    apiMocks.getLegalEntitiesPaged.mockImplementation((_params, options) => {
      if (options?.signal) signals.push(options.signal);
      return new Promise((resolve) => resolvePending.push(resolve));
    });

    const searchInput = screen.getByPlaceholderText('Поиск по названию, ИНН, директору');
    fireEvent.change(searchInput, { target: { value: 'ромашка' } });
    await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(1));
    expect(signals[0]?.aborted).toBe(false);

    fireEvent.change(searchInput, { target: { value: 'ромашка сибирь' } });

    await waitFor(() => expect(signals[0]?.aborted).toBe(true));
    await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(2));
    resolvePending.forEach((resolve) => resolve({ items: [], total: 0, page: 1, pageSize: 12 }));
  });

  it('loads a changed search directly on the first page', async () => {
    apiMocks.getLegalEntitiesPaged.mockResolvedValue({
      items: [legalEntity],
      total: 24,
      page: 1,
      pageSize: 12,
    });
    render(<LegalEntities />);
    await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(1));

    fireEvent.click(screen.getByRole('button', { name: 'Вперед' }));
    await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(2));
    expect(apiMocks.getLegalEntitiesPaged).toHaveBeenLastCalledWith(
      expect.objectContaining({ page: 2 }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
    apiMocks.getLegalEntitiesPaged.mockClear();

    fireEvent.change(screen.getByPlaceholderText('Поиск по названию, ИНН, директору'), {
      target: { value: 'новое юрлицо' },
    });

    await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledTimes(1));
    expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalledWith(
      expect.objectContaining({ search: 'новое юрлицо', page: 1 }),
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
  });

  it('warns that linked client cards will be preserved when deleting an organization', async () => {
    const session = documentCopyTestSession();
    try {
    render(<LegalEntities />);
    await waitFor(() => expect(apiMocks.getLegalEntitiesPaged).toHaveBeenCalled());

    await userEvent.click(screen.getByRole('button', { name: `Удалить ${legalEntity.shortName}` }));

    expect(dialogMocks.confirm).toHaveBeenCalledWith(expect.objectContaining({
      description: expect.stringContaining('Карточки связанных клиентов (1) сохранятся'),
    }));
    } finally { session.restore(); }
  });
});
