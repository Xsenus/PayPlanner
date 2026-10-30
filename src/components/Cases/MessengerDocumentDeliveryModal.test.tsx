import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import type { ClientCaseDocument } from '../../types';
import { MessengerDocumentDeliveryModal } from './MessengerDocumentDeliveryModal';

vi.mock('../../services/api', () => ({
  apiService: {
    getMessengerDocumentDeliveryConfiguration: vi.fn(),
    getMessengerRecipients: vi.fn(),
    getMessengerDocumentDeliveryHistory: vi.fn(),
    prepareCaseDocumentMessengerDelivery: vi.fn(),
    prepareFinancialDocumentMessengerDelivery: vi.fn(),
    queueMessengerDocumentDelivery: vi.fn(),
    createMessengerRecipient: vi.fn(),
  },
}));

const document: ClientCaseDocument = {
  id: 17,
  clientCaseId: 5,
  documentKey: 'claim',
  version: 2,
  documentType: 'Исковое заявление',
  status: 'Готов',
  originalFileName: 'Исковое заявление.pdf',
  contentType: 'application/pdf',
  sizeBytes: 2048,
  uploadedByName: 'Администратор',
  uploadedAt: '2026-07-31T10:00:00Z',
  isLatestVersion: true,
};

describe('MessengerDocumentDeliveryModal', () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  describe('delivery ownership', () => {
    const pdf = new Blob(['%PDF-1.7'], { type: 'application/pdf' });
    const financialDocument = { id: 44, originalFileName: 'invoice-44.pdf', documentType: 'Счёт', version: 1, sizeBytes: 0, sourceType: 'invoice' };
    const deferred = <T,>() => {
      let resolve!: (value: T) => void;
      let reject!: (error: Error) => void;
      const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
      return { promise, resolve, reject };
    };
    beforeEach(() => {
      vi.mocked(apiService.getMessengerDocumentDeliveryConfiguration).mockResolvedValue({ enabled: true, linkLifetimeHours: 24, providers: [{ channel: 'telegram', enabled: true }] });
      vi.mocked(apiService.getMessengerRecipients).mockResolvedValue([{ id: 8, clientId: 11, channel: 'telegram', provider: 'telegram', displayName: 'Иван Иванов', maskedAddress: '***4567', verificationStatus: 'verified', verificationMethod: 'admin-confirmed' }]);
      vi.mocked(apiService.getMessengerDocumentDeliveryHistory).mockResolvedValue([]);
      vi.mocked(apiService.prepareFinancialDocumentMessengerDelivery).mockResolvedValue({ id: 102 } as Awaited<ReturnType<typeof apiService.prepareFinancialDocumentMessengerDelivery>>);
      vi.mocked(apiService.queueMessengerDocumentDelivery).mockResolvedValue({ id: 102, status: 'queued' });
    });
    const start = async () => {
      await screen.findAllByText(/Иван Иванов/);
      fireEvent.click(screen.getByText(/Подтверждаю выбранный документ/));
      fireEvent.click(screen.getByRole('button', { name: /Подготовить и отправить/ }));
    };

    it.each(['close', 'document', 'client', 'rights', 'session', 'unmount'] as const)('stops after PDF generation on %s', async change => {
      const pending = deferred<Blob>(); const preparePdf = vi.fn(() => pending.promise);
      if (change === 'session') localStorage.setItem('pp_token', `synthetic.${btoa(JSON.stringify({ sub: '7' }))}.signature`);
      const props = { document: financialDocument, clientId: 11, ownerScope: 'actor7:export', preparePdf, onClose: vi.fn() };
      const view = render(<MessengerDocumentDeliveryModal {...props} />);
      await start(); expect(preparePdf).toHaveBeenCalledOnce();
      if (change === 'close') fireEvent.click(screen.getByRole('button', { name: 'Закрыть мастер отправки' }));
      if (change === 'unmount') view.unmount();
      if (change === 'document') view.rerender(<MessengerDocumentDeliveryModal {...props} document={{ ...financialDocument, id: 45 }} />);
      if (change === 'client') view.rerender(<MessengerDocumentDeliveryModal {...props} clientId={12} />);
      if (change === 'rights') view.rerender(<MessengerDocumentDeliveryModal {...props} ownerScope="actor7:no-export" />);
      if (change === 'session') localStorage.setItem('pp.auth-session-epoch.v1', 'replaced');
      try {
        await act(async () => pending.resolve(pdf));
        expect(apiService.prepareFinancialDocumentMessengerDelivery).not.toHaveBeenCalled();
        expect(apiService.queueMessengerDocumentDelivery).not.toHaveBeenCalled();
      } finally { localStorage.removeItem('pp.auth-session-epoch.v1'); localStorage.removeItem('pp_token'); }
    });

    it.each(['prepared', 'queued', 'error'] as const)('ignores late %s and never advances a closed wizard', async stage => {
      const pending = deferred<never>();
      const props = { document: financialDocument, clientId: 11, preparePdf: vi.fn().mockResolvedValue(pdf), onClose: vi.fn() };
      if (stage === 'queued') vi.mocked(apiService.queueMessengerDocumentDelivery).mockReturnValueOnce(pending.promise);
      else vi.mocked(apiService.prepareFinancialDocumentMessengerDelivery).mockReturnValueOnce(pending.promise);
      render(<MessengerDocumentDeliveryModal {...props} />); await start();
      await waitFor(() => expect(stage === 'queued' ? apiService.queueMessengerDocumentDelivery : apiService.prepareFinancialDocumentMessengerDelivery).toHaveBeenCalledOnce());
      fireEvent.click(screen.getByRole('button', { name: 'Закрыть мастер отправки' }));
      await act(async () => { if (stage === 'error') pending.reject(new Error('stale failure')); else pending.resolve({ id: 102 } as never); });
      if (stage !== 'queued') expect(apiService.queueMessengerDocumentDelivery).not.toHaveBeenCalled();
      expect(screen.queryByText(/поставлен в безопасную очередь|stale failure/)).toBeNull();
      expect(apiService.getMessengerDocumentDeliveryHistory).toHaveBeenCalledTimes(1);
    });

    it('does not continue a late configuration read after close', async () => {
      const pending = deferred<Awaited<ReturnType<typeof apiService.getMessengerDocumentDeliveryConfiguration>>>();
      vi.mocked(apiService.getMessengerDocumentDeliveryConfiguration).mockReturnValueOnce(pending.promise);
      render(<MessengerDocumentDeliveryModal document={document} clientId={11} onClose={vi.fn()} />);
      fireEvent.click(screen.getByRole('button', { name: 'Закрыть мастер отправки' }));
      await act(async () => pending.resolve({ enabled: true, linkLifetimeHours: 24, providers: [] }));
      expect(apiService.getMessengerRecipients).not.toHaveBeenCalled();
      expect(apiService.getMessengerDocumentDeliveryHistory).not.toHaveBeenCalled();
    });

    it('does not accept a late created contact after changing the selected contract client', async () => {
      const pending = deferred<Awaited<ReturnType<typeof apiService.createMessengerRecipient>>>();
      vi.mocked(apiService.createMessengerRecipient).mockReturnValueOnce(pending.promise);
      render(<MessengerDocumentDeliveryModal document={financialDocument} clientId={11} clientOptions={[{ id: 11, name: 'Первый клиент' }, { id: 12, name: 'Второй клиент' }]} preparePdf={vi.fn().mockResolvedValue(pdf)} onClose={vi.fn()} />);
      await screen.findAllByText(/Иван Иванов/);
      fireEvent.click(screen.getByRole('button', { name: 'Добавить подтверждённый контакт' }));
      fireEvent.change(screen.getByLabelText('Имя получателя'), { target: { value: 'Старый контакт' } });
      fireEvent.change(screen.getByLabelText('Контакт получателя'), { target: { value: '12345' } });
      fireEvent.click(screen.getByLabelText(/Контакт проверен и принадлежит/));
      fireEvent.click(screen.getByRole('button', { name: 'Сохранить подтверждённый контакт' }));
      expect(apiService.createMessengerRecipient).toHaveBeenCalledWith(expect.objectContaining({ clientId: 11 }));
      fireEvent.change(screen.getByText('Получатель по договору').querySelector('select')!, { target: { value: '12' } });
      await waitFor(() => expect(apiService.getMessengerRecipients).toHaveBeenLastCalledWith(12));
      await act(async () => pending.resolve({ id: 99, clientId: 11, channel: 'telegram', provider: 'telegram', displayName: 'Старый контакт', maskedAddress: '***12345', verificationStatus: 'verified', verificationMethod: 'admin-confirmed' }));
      expect(screen.queryByText(/Старый контакт/)).toBeNull();
      expect((screen.getByLabelText(/Подтверждаю выбранный документ/) as HTMLInputElement).checked).toBe(false);
      expect(apiService.queueMessengerDocumentDelivery).not.toHaveBeenCalled();
    });

    it('keeps a new wizard locked while its own PDF is pending despite the old finally', async () => {
      const first = deferred<Blob>(); const second = deferred<Blob>();
      const preparePdf = vi.fn().mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
      const props = { document: financialDocument, clientId: 11, preparePdf, onClose: vi.fn() };
      const view = render(<MessengerDocumentDeliveryModal {...props} />); await start();
      view.rerender(<MessengerDocumentDeliveryModal {...props} document={{ ...financialDocument, id: 45 }} />); await start();
      await act(async () => first.resolve(pdf));
      expect((screen.getByRole('button', { name: /Подготов/ }) as HTMLButtonElement).disabled).toBe(true);
      expect(apiService.prepareFinancialDocumentMessengerDelivery).not.toHaveBeenCalled();
      await act(async () => second.resolve(pdf));
      await waitFor(() => expect(apiService.queueMessengerDocumentDelivery).toHaveBeenCalledExactlyOnceWith(102));
      expect(apiService.prepareFinancialDocumentMessengerDelivery).toHaveBeenCalledExactlyOnceWith('invoice', 45, pdf, expect.any(Object));
    });
  });

  it('безопасно блокирует отправку, когда интеграция выключена', async () => {
    vi.mocked(apiService.getMessengerDocumentDeliveryConfiguration).mockResolvedValue({
      enabled: false,
      linkLifetimeHours: 24,
      providers: [
        { channel: 'telegram', enabled: false },
        { channel: 'max', enabled: false },
      ],
    });

    render(<MessengerDocumentDeliveryModal document={document} clientId={11} onClose={vi.fn()} />);

    expect(await screen.findByText(/доставка документов выключена/i)).toBeTruthy();
    expect((screen.getByRole('button', { name: /подготовить и отправить/i }) as HTMLButtonElement).disabled).toBe(true);
    expect(apiService.getMessengerRecipients).not.toHaveBeenCalled();
  });

  it('требует подтверждение и ставит выбранную версию в очередь', async () => {
    vi.mocked(apiService.getMessengerDocumentDeliveryConfiguration).mockResolvedValue({
      enabled: true,
      linkLifetimeHours: 24,
      providers: [{ channel: 'telegram', enabled: true }],
    });
    vi.mocked(apiService.getMessengerRecipients).mockResolvedValue([{
      id: 8,
      clientId: 11,
      channel: 'telegram',
      provider: 'telegram',
      displayName: 'Иван Иванов',
      maskedAddress: '***4567',
      verificationStatus: 'verified',
      verificationMethod: 'admin-confirmed',
      verifiedAt: '2026-07-31T10:00:00Z',
    }]);
    vi.mocked(apiService.getMessengerDocumentDeliveryHistory).mockResolvedValue([]);
    vi.mocked(apiService.prepareCaseDocumentMessengerDelivery).mockResolvedValue({
      id: 101,
      publicId: '00000000-0000-0000-0000-000000000101',
      documentType: 'case-document',
      documentId: 17,
      documentVersion: 2,
      originalFileName: document.originalFileName,
      checksumSha256: 'ABC',
      sizeBytes: 2048,
      channel: 'telegram',
      provider: 'telegram',
      status: 'prepared',
      linkExpiresAt: '2026-08-01T10:00:00Z',
    });
    vi.mocked(apiService.queueMessengerDocumentDelivery).mockResolvedValue({ id: 101, status: 'queued' });

    render(<MessengerDocumentDeliveryModal document={document} clientId={11} onClose={vi.fn()} />);
    expect((await screen.findAllByText(/иван иванов/i)).length).toBeGreaterThan(0);

    const submit = screen.getByRole('button', { name: /подготовить и отправить/i }) as HTMLButtonElement;
    expect(submit.disabled).toBe(true);
    await userEvent.click(screen.getByText(/подтверждаю выбранный документ/i));
    expect(submit.disabled).toBe(false);
    await userEvent.click(submit);

    await waitFor(() => expect(apiService.prepareCaseDocumentMessengerDelivery).toHaveBeenCalledWith(17, expect.objectContaining({
      recipientBindingId: 8,
      confirmRecipientAndDocument: true,
    })));
    expect(apiService.queueMessengerDocumentDelivery).toHaveBeenCalledWith(101);
    expect(await screen.findByText(/поставлен в безопасную очередь/i)).toBeTruthy();
  });

  it('формирует канонический PDF финансового документа перед подготовкой', async () => {
    vi.mocked(apiService.getMessengerDocumentDeliveryConfiguration).mockResolvedValue({
      enabled: true,
      linkLifetimeHours: 24,
      providers: [{ channel: 'telegram', enabled: true }],
    });
    vi.mocked(apiService.getMessengerRecipients).mockResolvedValue([{
      id: 8,
      clientId: 11,
      channel: 'telegram',
      provider: 'telegram',
      displayName: 'Иван Иванов',
      maskedAddress: '***4567',
      verificationStatus: 'verified',
      verificationMethod: 'admin-confirmed',
      verifiedAt: '2026-07-31T10:00:00Z',
    }]);
    vi.mocked(apiService.getMessengerDocumentDeliveryHistory).mockResolvedValue([]);
    vi.mocked(apiService.prepareFinancialDocumentMessengerDelivery).mockResolvedValue({
      id: 102,
      publicId: '00000000-0000-0000-0000-000000000102',
      documentType: 'invoice',
      documentId: 44,
      documentVersion: 1,
      originalFileName: 'invoice-44.pdf',
      checksumSha256: 'DEF',
      sizeBytes: 12,
      channel: 'telegram',
      provider: 'telegram',
      status: 'prepared',
      linkExpiresAt: '2026-08-01T10:00:00Z',
    });
    vi.mocked(apiService.queueMessengerDocumentDelivery).mockResolvedValue({ id: 102, status: 'queued' });
    const pdf = new Blob(['%PDF'], { type: 'application/pdf' });
    const preparePdf = vi.fn().mockResolvedValue(pdf);

    render(<MessengerDocumentDeliveryModal
      document={{ id: 44, originalFileName: 'invoice-44.pdf', documentType: 'Счёт', version: 1, sizeBytes: 0, sourceType: 'invoice' }}
      clientId={11}
      preparePdf={preparePdf}
      onClose={vi.fn()}
    />);
    await screen.findAllByText(/иван иванов/i);
    await userEvent.click(screen.getByText(/подтверждаю выбранный документ/i));
    await userEvent.click(screen.getByRole('button', { name: /подготовить и отправить/i }));

    await waitFor(() => expect(preparePdf).toHaveBeenCalledOnce());
    expect(apiService.prepareFinancialDocumentMessengerDelivery).toHaveBeenCalledWith(
      'invoice',
      44,
      pdf,
      expect.objectContaining({ recipientBindingId: 8, confirmRecipientAndDocument: true }),
    );
    expect(apiService.queueMessengerDocumentDelivery).toHaveBeenCalledWith(102);
  });
});
