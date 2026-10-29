import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ActDetailPage } from './ActDetailPage';
import { apiService } from '../../services/api';

const {
  actModalSpy,
  getActMock,
  getClientsMock,
  getActResponsiblesMock,
  getDocumentStatusesMock,
  updateActMock,
  updateActWorkflowStatusMock,
  createActCorrectionMock,
  downloadActDocumentMock,
  sendActByEmailMock,
  dialogConfirmMock,
  dialogPromptMock,
  rolePermissionsMock,
  authState,
} = vi.hoisted(() => ({
  actModalSpy: vi.fn(),
  getActMock: vi.fn(),
  getClientsMock: vi.fn(),
  getActResponsiblesMock: vi.fn(),
  getDocumentStatusesMock: vi.fn(),
  updateActMock: vi.fn(),
  updateActWorkflowStatusMock: vi.fn(),
  createActCorrectionMock: vi.fn(),
  downloadActDocumentMock: vi.fn(),
  sendActByEmailMock: vi.fn(),
  dialogConfirmMock: vi.fn(),
  dialogPromptMock: vi.fn(),
  authState: { user: { id: 1, role: { id: 1 } } },
  rolePermissionsMock: {
    acts: {
      canView: true,
      canEdit: true,
      canDelete: true,
      canExport: true,
      viewScope: 'all',
    },
    accounts: {
      canView: true,
    },
    clients: {
      canView: true,
      canEdit: false,
      viewScope: 'all',
    },
  },
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => authState,
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: () => rolePermissionsMock,
}));

vi.mock('../../hooks/useTranslation', () => ({
  useTranslation: () => ({ t: () => undefined }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({
    confirm: dialogConfirmMock,
    prompt: dialogPromptMock,
  }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({
    success: vi.fn(),
    error: vi.fn(),
  }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getOwnCompanyOptions: vi.fn().mockResolvedValue([]),
    getDocumentImagePrintDefaults: vi.fn().mockResolvedValue({ includeLogo: false, includeSeal: false, includeSignature: false }),
    getAct: getActMock,
    getClients: getClientsMock,
    getActResponsibles: getActResponsiblesMock,
    getDocumentStatuses: getDocumentStatusesMock,
    updateAct: updateActMock,
    updateActWorkflowStatus: updateActWorkflowStatusMock,
    createActCorrection: createActCorrectionMock,
    deleteAct: vi.fn(),
    downloadActDocument: downloadActDocumentMock,
    sendActByEmail: sendActByEmailMock,
  },
}));

vi.mock('./ActModal', () => ({
  ActModal: (props: Record<string, unknown>) => {
    actModalSpy(props);
    return props.open ? <div data-testid="act-modal-page">{String(props.presentation)}</div> : null;
  },
}));

vi.mock('../Clients/ClientStatusBadge', () => ({
  ClientStatusBadge: () => null,
}));

vi.mock('../ui/ObjectAuditModal', () => ({
  ObjectAuditLauncher: () => null,
}));

describe('ActDetailPage', () => {
  beforeEach(() => {
    vi.mocked(apiService.getDocumentImagePrintDefaults).mockReset().mockResolvedValue({ includeLogo: false, includeSeal: false, includeSignature: false });
    vi.clearAllMocks();
    dialogConfirmMock.mockResolvedValue(true);
    dialogPromptMock.mockResolvedValue('Уточнение суммы');
    authState.user = { id: 1, role: { id: 1 } };
    rolePermissionsMock.acts.canView = true;
    rolePermissionsMock.acts.canEdit = true;
    rolePermissionsMock.acts.canDelete = true;
    rolePermissionsMock.acts.canExport = true;
    rolePermissionsMock.acts.viewScope = 'all';
    rolePermissionsMock.accounts.canView = true;
    rolePermissionsMock.clients.canView = true;
    rolePermissionsMock.clients.viewScope = 'all';
    getDocumentStatusesMock.mockResolvedValue([]);
    createActCorrectionMock.mockResolvedValue({
      id: 40,
      number: '40-К1',
      date: '2026-04-12',
      amount: 10_000,
      status: 'Created',
      workflowStatus: 'Draft',
      lineItems: [],
    });
    downloadActDocumentMock.mockResolvedValue(new Blob(['<html><body><div>Preview</div></body></html>'], { type: 'text/html' }));
    sendActByEmailMock.mockResolvedValue({
      sent: true,
      recipient: 'client@example.com',
      fileName: 'act-31.doc',
      format: 'word',
    });
  });

  it('does not open linked client or invoice when related sections are hidden by role', async () => {
    rolePermissionsMock.accounts.canView = false;
    rolePermissionsMock.clients.canView = false;

    getActMock.mockResolvedValue({
      id: 35,
      number: '35',
      date: '2026-04-14',
      amount: 11_000,
      status: 'Created',
      type: 'Income',
      clientId: 7,
      clientName: 'Клиент без доступа',
      invoiceId: 618,
      invoiceNumber: 'INV-618',
      lineItems: [],
    });
    getClientsMock.mockResolvedValue({ items: [] });
    getActResponsiblesMock.mockResolvedValue({ items: [] });

    const onOpenClient = vi.fn();
    const onOpenInvoice = vi.fn();

    render(
      <ActDetailPage
        actId={35}
        onBack={vi.fn()}
        onOpenClient={onOpenClient}
        onOpenInvoice={onOpenInvoice}
      />,
    );

    await waitFor(() => {
      expect(screen.getByText('Клиент без доступа')).toBeTruthy();
    });

    expect(getClientsMock).not.toHaveBeenCalled();
    expect(screen.getByText('Счет №INV-618')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Счет №INV-618/ })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Клиент без доступа' })).toBeNull();
    expect(onOpenClient).not.toHaveBeenCalled();
    expect(onOpenInvoice).not.toHaveBeenCalled();
  });

  it('renders act preview in page layout and keeps old act data stable', async () => {
    getActMock.mockResolvedValue({
      id: 30,
      number: '30',
      date: '2026-04-08',
      amount: 18_000,
      status: 'Created',
      type: 'Income',
      clientId: 7,
      lineItems: [
        null,
        {
          id: 1,
          sourceKey: 'line-1',
          name: 'Юридическая услуга',
          quantity: '1',
          unit: 'усл.ед.',
          price: '18000',
        },
      ],
    });
    getClientsMock.mockResolvedValue({
      items: [
        {
          id: 7,
          name: 'Тестовый клиент',
          email: '',
          phone: '',
          company: '',
          address: '',
          notes: '',
          createdAt: '2026-04-01T00:00:00Z',
          isActive: true,
        },
      ],
    });
    getActResponsiblesMock.mockResolvedValue({ items: [] });

    render(<ActDetailPage actId={30} onBack={vi.fn()} />);

    await waitFor(() => {
      expect(screen.getByText('Печатный шаблон акта')).toBeTruthy();
    });

    expect(await screen.findByText('Тестовый клиент')).toBeTruthy();
    expect(screen.getByText((_, node) => node?.textContent === 'Позиции: 1')).toBeTruthy();
    expect(screen.queryByText(/ИНН/i)).toBeNull();
    expect(screen.queryByText('Позиции акта')).toBeNull();
    await waitFor(() => {
      expect(downloadActDocumentMock).toHaveBeenCalledWith(30, 'html', { includeSignatureAndSeal: false });
    });
  });

  it('opens the act editor in page mode instead of an overlay modal', async () => {
    getActMock.mockResolvedValue({
      id: 31,
      number: '31',
      date: '2026-04-09',
      amount: 21_000,
      status: 'Created',
      type: 'Expense',
      clientId: 7,
      invoiceId: 618,
      lineItems: [],
    });
    getClientsMock.mockResolvedValue({
      items: [
        {
          id: 7,
          name: 'Поставщик услуг',
          email: '',
          phone: '',
          company: '',
          address: '',
          notes: '',
          createdAt: '2026-04-01T00:00:00Z',
          isActive: true,
        },
      ],
    });
    getActResponsiblesMock.mockResolvedValue({ items: [] });

    render(<ActDetailPage actId={31} onBack={vi.fn()} onOpenInvoice={vi.fn()} />);

    await waitFor(() => {
      expect(screen.getByText('Поставщик услуг')).toBeTruthy();
    });

    screen.getByRole('button', { name: 'Редактировать акт' }).click();

    await waitFor(() => {
      expect(screen.getByTestId('act-modal-page')).toBeTruthy();
    });
    expect(screen.getByTestId('act-modal-page').textContent).toBe('page');
    expect(actModalSpy).toHaveBeenLastCalledWith(expect.objectContaining({
      mode: 'edit',
      presentation: 'page',
    }));
  });

  it('sends act document by email from the action bar', async () => {
    getActMock.mockResolvedValue({
      id: 32,
      number: '32',
      date: '2026-04-10',
      amount: 24_000,
      status: 'Created',
      type: 'Income',
      clientId: 7,
      lineItems: [],
    });
    getClientsMock.mockResolvedValue({
      items: [
        {
          id: 7,
          name: 'Клиент для отправки',
          email: 'client@example.com',
          phone: '',
          company: '',
          address: '',
          notes: '',
          createdAt: '2026-04-01T00:00:00Z',
          isActive: true,
        },
      ],
    });
    getActResponsiblesMock.mockResolvedValue({ items: [] });

    render(<ActDetailPage actId={32} onBack={vi.fn()} />);

    await waitFor(() => {
      expect(screen.getByText('Клиент для отправки')).toBeTruthy();
    });

    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));

    await waitFor(() => {
      expect(sendActByEmailMock).toHaveBeenCalledWith(32, {
        format: 'word',
        email: 'client@example.com',
      });
    });
  });

  it('creates a correction act and reloads the correction chain', async () => {
    getActMock
      .mockResolvedValueOnce({
        id: 34,
        number: '34',
        date: '2026-04-12',
        amount: 10_000,
        status: 'Signed',
        workflowStatus: 'Signed',
        type: 'Income',
        clientId: 7,
        lineItems: [],
        correctionActs: [],
      })
      .mockResolvedValueOnce({
        id: 34,
        number: '34',
        date: '2026-04-12',
        amount: 10_000,
        status: 'Signed',
        workflowStatus: 'Signed',
        type: 'Income',
        clientId: 7,
        lineItems: [],
        correctionActs: [
          {
            id: 40,
            number: '34-К1',
            date: '2026-04-13',
            amount: 10_000,
            workflowStatus: 'Draft',
          },
        ],
      });
    getClientsMock.mockResolvedValue({
      items: [
        {
          id: 7,
          name: 'Клиент с корректировкой',
          email: '',
          phone: '',
          company: '',
          address: '',
          notes: '',
          createdAt: '2026-04-01T00:00:00Z',
          isActive: true,
        },
      ],
    });
    getActResponsiblesMock.mockResolvedValue({ items: [] });
    dialogPromptMock.mockResolvedValueOnce('Уточнение суммы');

    render(<ActDetailPage actId={34} onBack={vi.fn()} />);

    await waitFor(() => {
      expect(screen.getByText('Клиент с корректировкой')).toBeTruthy();
    });

    fireEvent.click(screen.getByRole('button', { name: 'Корректировка' }));

    await waitFor(() => {
      expect(createActCorrectionMock).toHaveBeenCalledWith(34, 'Уточнение суммы');
    });
    await waitFor(() => {
      expect(screen.getByText('Корректировочные акты')).toBeTruthy();
      expect(screen.getByText(/№34-К1/)).toBeTruthy();
    });
  });

  it('opens status change modal from the status badge and updates act status', async () => {
    getActMock.mockResolvedValue({
      id: 33,
      number: '33',
      date: '2026-04-11',
      amount: 26_000,
      status: 'Created',
      workflowStatus: 'Draft',
      type: 'Income',
      clientId: 7,
      lineItems: [],
    });
    getClientsMock.mockResolvedValue({
      items: [
        {
          id: 7,
          name: 'Клиент для статуса',
          email: '',
          phone: '',
          company: '',
          address: '',
          notes: '',
          createdAt: '2026-04-01T00:00:00Z',
          isActive: true,
        },
      ],
    });
    getActResponsiblesMock.mockResolvedValue({ items: [] });
    updateActWorkflowStatusMock.mockResolvedValue({
      id: 33,
      number: '33',
      date: '2026-04-11',
      amount: 26_000,
      status: 'Transferred',
      workflowStatus: 'OnSigning',
      type: 'Income',
      clientId: 7,
      lineItems: [],
    });

    render(<ActDetailPage actId={33} onBack={vi.fn()} />);

    await waitFor(() => {
      expect(screen.getByText('Клиент для статуса')).toBeTruthy();
    });

    fireEvent.click(screen.getAllByRole('button', { name: 'Изменить статус документа акта №33' })[0]);

    expect(screen.getByRole('heading', { name: 'Смена статуса документа' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /На подписании/ }));
    fireEvent.click(screen.getByRole('button', { name: /Изменить статус акта на «На подписании»/ }));

    await waitFor(() => {
      expect(updateActWorkflowStatusMock).toHaveBeenCalledWith(33, 'OnSigning', null);
    });
    await waitFor(() => {
      expect(screen.queryByRole('heading', { name: 'Смена статуса документа' })).toBeNull();
    });
  });

  describe('image print dialog', () => {
    beforeEach(() => {
      getActMock.mockImplementation(async (id: number) => ({ id, number: String(id), date: '2026-04-14', amount: 100,
        status: 'Created', type: 'Income', clientId: 7, lineItems: [] }));
      getClientsMock.mockResolvedValue({ items: [] });
      getActResponsiblesMock.mockResolvedValue({ items: [] });
      downloadActDocumentMock.mockResolvedValue(Object.assign(new Blob(['<html>Preview</html>']), { text: async () => '<html>Preview</html>' }));
    });
    const loaded = async () => {
      await waitFor(() => expect(downloadActDocumentMock).toHaveBeenCalledWith(35, 'html', { includeSignatureAndSeal: false }));
      downloadActDocumentMock.mockClear();
    };
    const open = (format: 'print' | 'word' | 'pdf') => {
      fireEvent.click(screen.getByRole('button', { name: format === 'print' ? 'Печать' : 'Скачать' }));
      fireEvent.click(screen.getByRole('button', { name: format === 'print' ? 'С подписью и печатью' : `${format === 'word' ? 'Word' : 'PDF'} без подписи и печати` }));
      return within(screen.getByRole('dialog', { name: 'Изображения в документе' }));
    };

    it.each(['print', 'word', 'pdf'] as const)('loads act document defaults for %s before preparing its copy', async format => {
      vi.mocked(apiService.getDocumentImagePrintDefaults).mockResolvedValueOnce({ includeLogo: true, includeSeal: false, includeSignature: true });
      render(<ActDetailPage actId={35} onBack={vi.fn()} />); await loaded();
      fireEvent.click(screen.getByRole('button', { name: format === 'print' ? 'Печать' : 'Скачать' }));
      fireEvent.click(screen.getByRole('button', { name: format === 'print' ? 'По настройкам документа' : `${format === 'word' ? 'Word' : 'PDF'} по настройкам` }));
      const dialog = within(screen.getByRole('dialog', { name: 'Изображения в документе' }));
      const label = format === 'print' ? 'Печатать' : `Скачать ${format === 'word' ? 'Word' : 'PDF'}`;
      await waitFor(() => expect((dialog.getByRole('button', { name: label }) as HTMLButtonElement).disabled).toBe(false));
      expect(apiService.getDocumentImagePrintDefaults).toHaveBeenCalledExactlyOnceWith('acts', 35, expect.any(AbortSignal));
      expect(downloadActDocumentMock).not.toHaveBeenCalled();
      fireEvent.click(dialog.getByLabelText('Печать')); fireEvent.click(dialog.getByRole('button', { name: label }));
      await waitFor(() => expect(downloadActDocumentMock).toHaveBeenCalledExactlyOnceWith(35, format === 'print' ? 'pdf' : format,
        { includeLogo: true, includeSeal: true, includeSignature: true, signal: expect.any(AbortSignal) }));
      await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull());
      expect(updateActMock).not.toHaveBeenCalled();
    });

    it.each(['switch', 'revoke', 'hide'] as const)('aborts an act document defaults read on %s', async change => {
      let finish!: (value: { includeLogo: boolean; includeSeal: boolean; includeSignature: boolean }) => void;
      vi.mocked(apiService.getDocumentImagePrintDefaults).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
      const view = render(<ActDetailPage actId={35} onBack={vi.fn()} />); await loaded();
      fireEvent.click(screen.getByRole('button', { name: 'Скачать' })); fireEvent.click(screen.getByRole('button', { name: 'PDF по настройкам' }));
      const signal = vi.mocked(apiService.getDocumentImagePrintDefaults).mock.calls[0][2]!;
      if (change === 'revoke') rolePermissionsMock.acts.canExport = false;
      if (change === 'hide') rolePermissionsMock.acts.canView = false;
      view.rerender(<ActDetailPage actId={change === 'switch' ? 36 : 35} onBack={vi.fn()} />);
      expect(signal.aborted).toBe(true);
      await act(async () => finish({ includeLogo: true, includeSeal: true, includeSignature: true }));
      expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull();
      expect(downloadActDocumentMock.mock.calls.filter(call => call[2]?.signal)).toHaveLength(0);
      expect(updateActMock).not.toHaveBeenCalled();
    });

    it('does not read act document defaults for an explicit old menu choice', async () => {
      render(<ActDetailPage actId={35} onBack={vi.fn()} />); await loaded(); open('word');
      expect(apiService.getDocumentImagePrintDefaults).not.toHaveBeenCalled();
    });

    it.each(['print', 'word', 'pdf'] as const)('confirms independent images for %s without changing the act', async format => {
      render(<ActDetailPage actId={35} onBack={vi.fn()} />); await loaded();
      const dialog = open(format);
      expect(downloadActDocumentMock).not.toHaveBeenCalled();
      fireEvent.click(dialog.getByLabelText('Логотип'));
      if (format === 'print') fireEvent.click(dialog.getByLabelText('Подпись'));
      else fireEvent.click(dialog.getByLabelText('Печать'));
      fireEvent.click(dialog.getByRole('button', { name: format === 'print' ? 'Печатать' : `Скачать ${format === 'word' ? 'Word' : 'PDF'}` }));
      await waitFor(() => expect(downloadActDocumentMock).toHaveBeenCalledExactlyOnceWith(35, format === 'print' ? 'pdf' : format,
        { includeLogo: true, includeSeal: true, includeSignature: false, signal: expect.any(AbortSignal) }));
      await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull());
      expect(updateActMock).not.toHaveBeenCalled();
    });

    it('cancels before confirmation without fetching a document', async () => {
      render(<ActDetailPage actId={35} onBack={vi.fn()} />); await loaded();
      fireEvent.click(open('word').getByRole('button', { name: 'Отмена' }));
      expect(downloadActDocumentMock).not.toHaveBeenCalled();
      expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull();
    });

    it('retains image choices after an error and retries with a new request', async () => {
      render(<ActDetailPage actId={35} onBack={vi.fn()} />); await loaded();
      downloadActDocumentMock.mockRejectedValueOnce(new Error('Синтетический отказ')).mockResolvedValueOnce(new Blob(['act']));
      const dialog = open('word'); fireEvent.click(dialog.getByLabelText('Подпись'));
      fireEvent.click(dialog.getByRole('button', { name: 'Скачать Word' }));
      await dialog.findByRole('alert');
      expect((dialog.getByLabelText('Подпись') as HTMLInputElement).checked).toBe(true);
      fireEvent.click(dialog.getByRole('button', { name: 'Скачать Word' }));
      await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull());
      expect(downloadActDocumentMock).toHaveBeenCalledTimes(2);
      expect(downloadActDocumentMock.mock.calls[1][2].signal).not.toBe(downloadActDocumentMock.mock.calls[0][2].signal);
    });

    it('aborts print and closes only its pending window immediately on cancellation', async () => {
      render(<ActDetailPage actId={35} onBack={vi.fn()} />); await loaded();
      let resolve!: (value: Blob) => void; const pending = new Promise<Blob>(done => { resolve = done; });
      downloadActDocumentMock.mockReturnValueOnce(pending);
      const dialog = open('print'); fireEvent.click(dialog.getByRole('button', { name: 'Печатать' }));
      const signal = downloadActDocumentMock.mock.calls[0][2].signal as AbortSignal;
      const popup = vi.mocked(window.open).mock.results.at(-1)!.value as Window;
      fireEvent.click(dialog.getByRole('button', { name: 'Отмена' }));
      expect(signal.aborted).toBe(true); expect(popup.close).toHaveBeenCalled();
      await act(async () => { resolve(new Blob(['late'])); await pending; });
      expect(popup.location.href).toBe(''); expect(popup.print).not.toHaveBeenCalled();
    });

    it.each(['switch', 'unmount', 'revoke'] as const)('fences a late download on %s', async change => {
      const view = render(<ActDetailPage actId={35} onBack={vi.fn()} />); await loaded();
      let resolve!: (value: Blob) => void; const pending = new Promise<Blob>(done => { resolve = done; });
      downloadActDocumentMock.mockReturnValueOnce(pending);
      fireEvent.click(open('word').getByRole('button', { name: 'Скачать Word' }));
      const signal = downloadActDocumentMock.mock.calls[0][2].signal as AbortSignal;
      const click = vi.spyOn(HTMLAnchorElement.prototype, 'click');
      try {
        if (change === 'unmount') view.unmount();
        else {
          if (change === 'revoke') rolePermissionsMock.acts.canExport = false;
          view.rerender(<ActDetailPage actId={change === 'switch' ? 36 : 35} onBack={vi.fn()} />);
        }
        expect(signal.aborted).toBe(true);
        await act(async () => { resolve(new Blob(['late'])); await pending; });
        expect(click).not.toHaveBeenCalled();
        expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull();
      } finally { click.mockRestore(); }
    });

    it('does not fetch preview or allow export without export permission', async () => {
      rolePermissionsMock.acts.canExport = false;
      render(<ActDetailPage actId={35} onBack={vi.fn()} />);
      expect((await screen.findByRole('button', { name: 'Печать' }) as HTMLButtonElement).disabled).toBe(true);
      expect((screen.getByRole('button', { name: 'Скачать' }) as HTMLButtonElement).disabled).toBe(true);
      expect(downloadActDocumentMock).not.toHaveBeenCalled();
      expect(document.querySelector('iframe')).toBeNull();
    });

    it('explains that expense acts do not use our company images', async () => {
      getActMock.mockResolvedValue({ id: 35, number: '35', date: '2026-04-14', amount: 100, status: 'Created', type: 'Expense', lineItems: [] });
      render(<ActDetailPage actId={35} onBack={vi.fn()} />); await loaded();
      expect(open('print').getByText('В расходном акте изображения нашей компании не используются.')).toBeTruthy();
    });

    it('does not fetch when the print window is blocked', async () => {
      render(<ActDetailPage actId={35} onBack={vi.fn()} />); await loaded();
      vi.mocked(window.open).mockReturnValueOnce(null);
      const dialog = open('print'); fireEvent.click(dialog.getByRole('button', { name: 'Печатать' }));
      await dialog.findByRole('alert'); expect(downloadActDocumentMock).not.toHaveBeenCalled();
    });
  });

  describe('independent optional catalogs and access boundaries', () => {
    const sourceAct = (id = 35, clientName = 'Разрешённый клиент') => ({
      id, number: String(id), date: '2026-04-14', amount: 11_000,
      status: 'Created', workflowStatus: 'Draft', type: 'Income',
      clientId: 7, clientName, invoiceId: 618, invoiceNumber: 'INV-618', lineItems: [],
    });
    const sourceClient = (id = 7, name = 'Клиент из справочника') => ({
      id, name, email: '', phone: '', company: '', address: '', notes: '',
      createdAt: '2026-04-01T00:00:00Z', isActive: true,
    });
    const deferred = <T,>() => {
      let resolve!: (value: T) => void;
      let reject!: (reason: unknown) => void;
      const promise = new Promise<T>((accept, decline) => { resolve = accept; reject = decline; });
      return { promise, resolve, reject };
    };
    const editorProps = () => actModalSpy.mock.calls.at(-1)?.[0] as Record<string, unknown>;
    const openEditor = async () => {
      fireEvent.click(await screen.findByRole('button', { name: 'Редактировать акт' }));
      await screen.findByTestId('act-modal-page');
      return editorProps();
    };

    beforeEach(() => {
      getActMock.mockReset().mockResolvedValue(sourceAct());
      getClientsMock.mockReset().mockResolvedValue({ items: [] });
      getActResponsiblesMock.mockReset().mockResolvedValue({ items: [] });
      updateActMock.mockReset();
    });

    it('keeps invoice-linked editing and source IDs without client view or client edit rights', async () => {
      rolePermissionsMock.clients.canView = false;
      const onOpenInvoice = vi.fn();
      render(<ActDetailPage actId={35} onBack={vi.fn()} onOpenInvoice={onOpenInvoice} />);

      const props = await openEditor();

      expect(getClientsMock).not.toHaveBeenCalled();
      expect(props.act).toMatchObject({ id: 35, clientId: 7, invoiceId: 618 });
      expect(props.clients).toEqual([]);
      expect(props.canViewInvoices).toBe(true);
      expect(props.onOpenInvoice).toBe(onOpenInvoice);
      expect(rolePermissionsMock.clients.canEdit).toBe(false);
    });

    it('preserves the allowed act and independent responsibles when the client catalog returns 403', async () => {
      const responsible = { id: 4, fullName: 'Ответственный по акту', name: 'Ответственный по акту' };
      getClientsMock.mockRejectedValue(new Error('HTTP 403 Forbidden at /api/clients - Справочник клиентов недоступен.'));
      getActResponsiblesMock.mockResolvedValue({ items: [responsible] });
      render(<ActDetailPage actId={35} onBack={vi.fn()} />);

      await screen.findByText('Разрешённый клиент');
      expect((await screen.findByRole('status')).textContent).toContain('Недостаточно прав для выполнения действия.');
      const props = await openEditor();

      expect(props.act).toMatchObject({ id: 35, clientId: 7, invoiceId: 618 });
      expect(props.clients).toEqual([]);
      expect(props.responsibles).toEqual([expect.objectContaining({ id: 4 })]);
      expect(props.lookupsLoading).toBe(false);
      expect(props.lookupsError).toContain('Недостаточно прав для выполнения действия.');
      expect(props.lookupsError).not.toContain('/api/clients');
      expect(downloadActDocumentMock).toHaveBeenCalledWith(35, 'html', { includeSignatureAndSeal: false });
    });

    it('opens the allowed act before a slow client catalog and retains the editor act when it arrives', async () => {
      const clients = deferred<{ items: ReturnType<typeof sourceClient>[] }>();
      getClientsMock.mockReturnValue(clients.promise);
      render(<ActDetailPage actId={35} onBack={vi.fn()} />);

      const initialProps = await openEditor();
      expect(initialProps.lookupsLoading).toBe(true);
      expect(initialProps.clients).toEqual([]);
      await act(async () => { clients.resolve({ items: [sourceClient()] }); });

      await waitFor(() => expect(editorProps().lookupsLoading).toBe(false));
      expect(screen.getByTestId('act-modal-page')).toBeTruthy();
      expect(editorProps().act).toBe(initialProps.act);
      expect(editorProps().act).toMatchObject({ clientId: 7, invoiceId: 618 });
      expect(editorProps().clients).toEqual([expect.objectContaining({ id: 7 })]);
      expect(getActMock).toHaveBeenCalledTimes(1);
    });

    it('keeps the client catalog and editable act when the responsible catalog fails', async () => {
      getClientsMock.mockResolvedValue({ items: [sourceClient()] });
      getActResponsiblesMock.mockRejectedValue(new Error('Ответственные недоступны.'));
      render(<ActDetailPage actId={35} onBack={vi.fn()} />);

      await screen.findByRole('status');
      const props = await openEditor();

      expect(props.act).toMatchObject({ id: 35, clientId: 7, invoiceId: 618 });
      expect(props.clients).toEqual([expect.objectContaining({ id: 7 })]);
      expect(props.responsibles).toEqual([]);
      expect(props.lookupsError).toContain('Ответственные недоступны.');
    });

    it.each([403, 404])('does not retain the previous act or fall back to catalogs when the new act returns %s', async (status) => {
      const { rerender } = render(<ActDetailPage actId={35} onBack={vi.fn()} />);
      await screen.findByText('Разрешённый клиент');
      await waitFor(() => expect(getClientsMock).toHaveBeenCalledTimes(1));
      getActMock.mockRejectedValueOnce(new Error(`HTTP ${status} - Новый акт недоступен.`));
      getClientsMock.mockClear();
      getActResponsiblesMock.mockClear();
      downloadActDocumentMock.mockClear();

      rerender(<ActDetailPage actId={36} onBack={vi.fn()} />);

      expect(screen.queryByText('Разрешённый клиент')).toBeNull();
      expect(await screen.findByText(status === 403 ? 'Недостаточно прав для выполнения действия.' : 'Новый акт недоступен.')).toBeTruthy();
      expect(screen.queryByRole('button', { name: 'Редактировать акт' })).toBeNull();
      expect(screen.queryByTestId('act-modal-page')).toBeNull();
      expect(screen.queryByText('Печатный шаблон акта')).toBeNull();
      expect(getClientsMock).not.toHaveBeenCalled();
      expect(getActResponsiblesMock).not.toHaveBeenCalled();
      expect(downloadActDocumentMock).not.toHaveBeenCalled();
    });

    it.each(['success', 'failure'])('ignores a late old route %s after the next act has loaded', async (outcome) => {
      const oldAct = deferred<ReturnType<typeof sourceAct>>();
      getActMock.mockReturnValueOnce(oldAct.promise).mockResolvedValueOnce(sourceAct(36, 'Текущий клиент'));
      const { rerender } = render(<ActDetailPage actId={35} onBack={vi.fn()} />);
      rerender(<ActDetailPage actId={36} onBack={vi.fn()} />);
      await screen.findByText('Текущий клиент');

      await act(async () => {
        if (outcome === 'success') oldAct.resolve(sourceAct(35, 'Старый клиент'));
        else oldAct.reject(new Error('Ошибка старого маршрута'));
      });

      expect(screen.getByText('Текущий клиент')).toBeTruthy();
      expect(screen.queryByText('Старый клиент')).toBeNull();
      expect(screen.queryByText('Ошибка старого маршрута')).toBeNull();
      expect((await openEditor()).act).toMatchObject({ id: 36, clientId: 7, invoiceId: 618 });
    });

    it('ignores a previous actor response for the same act ID', async () => {
      const oldAct = deferred<ReturnType<typeof sourceAct>>();
      getActMock.mockReturnValueOnce(oldAct.promise).mockResolvedValueOnce(sourceAct(35, 'Клиент текущего сотрудника'));
      const { rerender } = render(<ActDetailPage actId={35} onBack={vi.fn()} />);
      authState.user = { id: 2, role: { id: 2 } };
      rerender(<ActDetailPage actId={35} onBack={vi.fn()} />);
      await screen.findByText('Клиент текущего сотрудника');

      await act(async () => { oldAct.resolve(sourceAct(35, 'Клиент прежнего сотрудника')); });

      expect(screen.queryByText('Клиент прежнего сотрудника')).toBeNull();
      expect((await openEditor()).act).toMatchObject({ clientName: 'Клиент текущего сотрудника' });
      expect(getClientsMock).toHaveBeenCalledTimes(1);
    });

    it('ignores previous actor catalog responses for the same act ID', async () => {
      const oldClients = deferred<{ items: ReturnType<typeof sourceClient>[] }>();
      getClientsMock.mockReturnValueOnce(oldClients.promise).mockResolvedValueOnce({ items: [sourceClient(8, 'Новый справочник')] });
      const { rerender } = render(<ActDetailPage actId={35} onBack={vi.fn()} />);
      await waitFor(() => expect(getClientsMock).toHaveBeenCalledTimes(1));
      authState.user = { id: 2, role: { id: 2 } };
      rerender(<ActDetailPage actId={35} onBack={vi.fn()} />);
      await waitFor(() => expect(getClientsMock).toHaveBeenCalledTimes(2));
      await openEditor();

      await act(async () => { oldClients.resolve({ items: [sourceClient(9, 'Старый справочник')] }); });

      expect(editorProps().clients).toEqual([expect.objectContaining({ id: 8, name: 'Новый справочник' })]);
      expect(editorProps().act).toMatchObject({ id: 35, clientId: 7, invoiceId: 618 });
    });

    it('clears revoked client catalogs without losing the open act editor or accepting the old response', async () => {
      const clients = deferred<{ items: ReturnType<typeof sourceClient>[] }>();
      getClientsMock.mockReturnValue(clients.promise);
      const { rerender } = render(<ActDetailPage actId={35} onBack={vi.fn()} />);
      const initialProps = await openEditor();
      rolePermissionsMock.clients.canView = false;
      rerender(<ActDetailPage actId={35} onBack={vi.fn()} />);

      expect(screen.getByTestId('act-modal-page')).toBeTruthy();
      expect(editorProps().clients).toEqual([]);
      expect(editorProps().act).toBe(initialProps.act);
      await act(async () => { clients.resolve({ items: [sourceClient()] }); });

      expect(editorProps().clients).toEqual([]);
      expect(editorProps().act).toMatchObject({ clientId: 7, invoiceId: 618 });
      expect(getActMock).toHaveBeenCalledTimes(1);
      expect(getClientsMock).toHaveBeenCalledTimes(1);
    });

    it('does not reveal a pending act after its view permission is revoked', async () => {
      const pendingAct = deferred<ReturnType<typeof sourceAct>>();
      getActMock.mockReturnValue(pendingAct.promise);
      const { rerender } = render(<ActDetailPage actId={35} onBack={vi.fn()} />);
      rolePermissionsMock.acts.canView = false;
      rerender(<ActDetailPage actId={35} onBack={vi.fn()} />);
      await screen.findByText('Нет доступа к акту.');

      await act(async () => { pendingAct.resolve(sourceAct()); });

      expect(screen.getByText('Нет доступа к акту.')).toBeTruthy();
      expect(screen.queryByRole('button', { name: 'Редактировать акт' })).toBeNull();
      expect(getActMock).toHaveBeenCalledTimes(1);
      expect(getClientsMock).not.toHaveBeenCalled();
      expect(downloadActDocumentMock).not.toHaveBeenCalled();
    });

    it('closes the editor when act editing rights are revoked and retains the current read-only source IDs', async () => {
      const { rerender } = render(<ActDetailPage actId={35} onBack={vi.fn()} />);
      const initialProps = await openEditor();
      rolePermissionsMock.acts.canEdit = false;
      rerender(<ActDetailPage actId={35} onBack={vi.fn()} />);

      expect(screen.queryByTestId('act-modal-page')).toBeNull();
      await screen.findByText('Разрешённый клиент');
      expect(screen.queryByRole('button', { name: 'Редактировать акт' })).toBeNull();
      expect(initialProps.act).toMatchObject({ clientId: 7, invoiceId: 618 });
      expect(getActMock).toHaveBeenCalledTimes(2);
    });

    it.each([['route', 'success'], ['route', 'failure'], ['actor', 'success'], ['actor', 'failure']])('ignores a previous save after a %s change (%s) without changing the next editor', async (boundary, outcome) => {
      const savedAct = deferred<ReturnType<typeof sourceAct>>();
      updateActMock.mockReturnValue(savedAct.promise);
      const { rerender } = render(<ActDetailPage actId={35} onBack={vi.fn()} />);
      const firstProps = await openEditor();
      let submit!: Promise<void>;
      act(() => { submit = (firstProps.onSubmit as (payload: unknown) => Promise<void>)({ clientId: 7, invoiceId: 618 }); });
      expect(editorProps().submitting).toBe(true);

      const nextId = boundary === 'route' ? 36 : 35;
      if (boundary === 'actor') authState.user = { id: 2, role: { id: 2 } };
      getActMock.mockResolvedValue(sourceAct(nextId, 'Следующий акт'));
      rerender(<ActDetailPage actId={nextId} onBack={vi.fn()} />);
      const nextProps = await openEditor();
      expect(nextProps.submitting).toBe(false);
      await act(async () => {
        if (outcome === 'success') savedAct.resolve(sourceAct(35, 'Устаревшее сохранение'));
        else savedAct.reject(new Error('Ошибка старого сохранения'));
        await submit;
      });

      expect(screen.getByTestId('act-modal-page')).toBeTruthy();
      expect(editorProps().act).toBe(nextProps.act);
      expect(editorProps().act).toMatchObject({ id: nextId, clientId: 7, invoiceId: 618 });
      expect(editorProps().submitting).toBe(false);
      expect(editorProps().errorMessage).toBeNull();
      expect(updateActMock).toHaveBeenCalledTimes(1);
    });

    it.each([401, 403, 404] as const)('invalidates the parent act and preview after the editor reports mandatory GET %s', async (status) => {
      const onBack = vi.fn();
      render(<ActDetailPage actId={35} onBack={onBack} />);
      const props = await openEditor();
      const unavailable = props.onActUnavailable as (id: number, status: 401 | 403 | 404) => void;

      await act(async () => {
        unavailable(35, status);
        await (props.onSubmit as (payload: unknown) => Promise<void>)({ clientId: 7, invoiceId: 618 });
        (props.onBack as () => void)();
      });

      expect(screen.queryByText('Разрешённый клиент')).toBeNull();
      expect(screen.queryByTestId('act-modal-page')).toBeNull();
      expect(screen.queryByRole('button', { name: 'Редактировать акт' })).toBeNull();
      expect(screen.queryByText('Печатный шаблон акта')).toBeNull();
      expect(document.querySelector('iframe')).toBeNull();
      expect(updateActMock).not.toHaveBeenCalled();
      fireEvent.click(screen.getByRole('button', { name: 'К списку актов' }));
      expect(onBack).toHaveBeenCalledTimes(1);
    });

    it.each(['route', 'actor'] as const)('ignores an old editor denial and submit after a %s change', async (boundary) => {
      const { rerender } = render(<ActDetailPage actId={35} onBack={vi.fn()} />);
      const props = await openEditor();
      const nextId = boundary === 'route' ? 36 : 35;
      if (boundary === 'actor') authState.user = { id: 2, role: { id: 2 } };
      getActMock.mockResolvedValue(sourceAct(nextId, 'Текущий разрешённый акт'));
      rerender(<ActDetailPage actId={nextId} onBack={vi.fn()} />);
      await screen.findByText('Текущий разрешённый акт');

      await act(async () => {
        (props.onActUnavailable as (id: number, status: number) => void)(35, 403);
        await (props.onSubmit as (payload: unknown) => Promise<void>)({ clientId: 7, invoiceId: 618 });
      });

      expect(screen.getByText('Текущий разрешённый акт')).toBeTruthy();
      expect(screen.queryByText('Нет доступа к акту.')).toBeNull();
      expect((await openEditor()).act).toMatchObject({ id: nextId, clientId: 7, invoiceId: 618 });
      expect(updateActMock).not.toHaveBeenCalled();
    });

    it.each(['route-aba', 'reopen'] as const)('rejects an earlier editor callback and submit after a %s cycle', async (cycle) => {
      const { rerender } = render(<ActDetailPage actId={35} onBack={vi.fn()} />);
      const previous = await openEditor();
      if (cycle === 'route-aba') {
        getActMock.mockResolvedValue(sourceAct(36, 'Промежуточный акт'));
        rerender(<ActDetailPage actId={36} onBack={vi.fn()} />);
        await screen.findByText('Промежуточный акт');
        getActMock.mockResolvedValue(sourceAct());
        rerender(<ActDetailPage actId={35} onBack={vi.fn()} />);
        await screen.findByText('Разрешённый клиент');
      } else {
        act(() => { (previous.onBack as () => void)(); });
        await screen.findByText('Разрешённый клиент');
      }
      const current = await openEditor();

      await act(async () => {
        (previous.onActUnavailable as (id: number, status: number) => void)(35, 403);
        await (previous.onSubmit as (payload: unknown) => Promise<void>)({ clientId: 7, invoiceId: 618 });
      });

      expect(screen.getByTestId('act-modal-page')).toBeTruthy();
      expect(editorProps().act).toBe(current.act);
      expect(editorProps().errorMessage).toBeNull();
      expect(updateActMock).not.toHaveBeenCalled();
    });

    it('does not restore a cached preview when its late response arrives after editor access denial', async () => {
      const preview = deferred<Blob>();
      downloadActDocumentMock.mockReturnValue(preview.promise);
      render(<ActDetailPage actId={35} onBack={vi.fn()} />);
      const props = await openEditor();
      await act(async () => { (props.onActUnavailable as (id: number, status: number) => void)(35, 404); });
      await act(async () => { preview.resolve(new Blob(['<html>OLD PRIVATE PREVIEW</html>'], { type: 'text/html' })); });

      expect(screen.queryByText('Разрешённый клиент')).toBeNull();
      expect(screen.queryByText('Печатный шаблон акта')).toBeNull();
      expect(document.querySelector('iframe')).toBeNull();
      expect(screen.getByRole('button', { name: 'К списку актов' })).toBeTruthy();
    });

    it('does not show or fetch optional catalogs for a response belonging to another act ID', async () => {
      getActMock.mockResolvedValue(sourceAct(36));
      render(<ActDetailPage actId={35} onBack={vi.fn()} />);

      await screen.findByText('Акт не найден.');

      expect(screen.queryByRole('button', { name: 'Редактировать акт' })).toBeNull();
      expect(getClientsMock).not.toHaveBeenCalled();
      expect(getActResponsiblesMock).not.toHaveBeenCalled();
      expect(downloadActDocumentMock).not.toHaveBeenCalled();
    });
  });
});
