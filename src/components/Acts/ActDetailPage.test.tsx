import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ActDetailPage } from './ActDetailPage';

const {
  actModalSpy,
  getActMock,
  getClientsMock,
  getActResponsiblesMock,
  getDocumentStatusesMock,
  updateActWorkflowStatusMock,
  createActCorrectionMock,
  downloadActDocumentMock,
  sendActByEmailMock,
} = vi.hoisted(() => ({
  actModalSpy: vi.fn(),
  getActMock: vi.fn(),
  getClientsMock: vi.fn(),
  getActResponsiblesMock: vi.fn(),
  getDocumentStatusesMock: vi.fn(),
  updateActWorkflowStatusMock: vi.fn(),
  createActCorrectionMock: vi.fn(),
  downloadActDocumentMock: vi.fn(),
  sendActByEmailMock: vi.fn(),
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { id: 1, role: { id: 1 } },
  }),
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: () => ({
    acts: {
      canEdit: true,
      canDelete: true,
    },
  }),
}));

vi.mock('../../hooks/useTranslation', () => ({
  useTranslation: () => ({ t: () => undefined }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({
    confirm: vi.fn().mockResolvedValue(true),
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
    getAct: getActMock,
    getClients: getClientsMock,
    getActResponsibles: getActResponsiblesMock,
    getDocumentStatuses: getDocumentStatusesMock,
    updateAct: vi.fn(),
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
    vi.clearAllMocks();
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

    expect(screen.getByText('Тестовый клиент')).toBeTruthy();
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
    vi.spyOn(window, 'prompt').mockReturnValueOnce('Уточнение суммы');

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
});
