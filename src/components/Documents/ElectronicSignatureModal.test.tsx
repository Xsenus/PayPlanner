import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiServiceMock, resetApiServiceMock } from '../../test/apiMock';
import { ElectronicSignatureModal } from './ElectronicSignatureModal';

vi.mock('../../services/api', async () => ({
  apiService: (await import('../../test/apiMock')).apiServiceMock,
}));

describe('ElectronicSignatureModal', () => {
  beforeEach(() => {
    resetApiServiceMock(
      'getElectronicSignatureConfiguration',
      'getElectronicSignatureRecipients',
      'getElectronicSignatureHistory',
      'prepareElectronicSignatureEnvelope',
      'cancelElectronicSignatureEnvelope',
      'downloadElectronicSignatureEvidencePackage',
    );
    apiServiceMock.getElectronicSignatureConfiguration.mockResolvedValue({
      enabled: false,
      preparationEnabled: true,
      defaultSignatureType: 'qualified',
      defaultSigningOrder: 'sequential',
      defaultArchiveRetentionYears: 10,
      providers: [{ name: 'KonturDiadoc', enabled: false }],
    });
    apiServiceMock.getElectronicSignatureRecipients.mockResolvedValue([
      { id: 7, displayName: 'Иван Иванов', email: 'ivan@example.test', accountType: 'client', isRepresentative: false },
      { id: 8, displayName: 'Анна Петрова', email: 'anna@example.test', accountType: 'representative', isRepresentative: true },
    ]);
    apiServiceMock.getElectronicSignatureHistory.mockResolvedValue([]);
    apiServiceMock.prepareElectronicSignatureEnvelope.mockResolvedValue({ id: 1 });
  });

  describe('delivery ownership', () => {
    const deferred = <T,>() => {
      let resolve!: (value: T) => void;
      let reject!: (error: Error) => void;
      const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
      return { promise, resolve, reject };
    };
    const pdf = new Blob(['%PDF-1.7'], { type: 'application/pdf' });
    const start = async () => {
      fireEvent.click(await screen.findByText('Иван Иванов'));
      fireEvent.click(screen.getByText(/Подтверждаю выбранную неизменяемую PDF-версию/));
      fireEvent.click(screen.getByRole('button', { name: 'Подготовить неизменяемую версию' }));
    };
    const props = () => ({ documentType: 'invoice' as const, documentId: 15, documentName: 'Счёт 15', clientId: 3, ownerScope: 'actor7:export', preparePdf: vi.fn().mockResolvedValue(pdf), onClose: vi.fn() });

    it.each(['close', 'document', 'client', 'rights', 'session', 'unmount'] as const)('stops before envelope creation on %s', async change => {
      const pending = deferred<Blob>(); const current = { ...props(), preparePdf: vi.fn(() => pending.promise) };
      if (change === 'session') localStorage.setItem('pp_token', `synthetic.${btoa(JSON.stringify({ sub: '7' }))}.signature`);
      const view = render(<ElectronicSignatureModal {...current} />); await start();
      expect(current.preparePdf).toHaveBeenCalledOnce();
      if (change === 'close') fireEvent.click(screen.getByRole('button', { name: 'Закрыть' }));
      if (change === 'unmount') view.unmount();
      if (change === 'document') view.rerender(<ElectronicSignatureModal {...current} documentId={16} />);
      if (change === 'client') view.rerender(<ElectronicSignatureModal {...current} clientId={4} />);
      if (change === 'rights') view.rerender(<ElectronicSignatureModal {...current} ownerScope="actor7:no-export" />);
      if (change === 'session') localStorage.setItem('pp.auth-session-epoch.v1', 'replaced');
      try {
        await act(async () => pending.resolve(pdf));
        expect(apiServiceMock.prepareElectronicSignatureEnvelope).not.toHaveBeenCalled();
      } finally { localStorage.removeItem('pp.auth-session-epoch.v1'); localStorage.removeItem('pp_token'); }
    });

    it.each(['success', 'error'] as const)('ignores envelope %s after the document changes', async result => {
      const pending = deferred<never>(); apiServiceMock.prepareElectronicSignatureEnvelope.mockReturnValueOnce(pending.promise);
      const current = props(); const view = render(<ElectronicSignatureModal {...current} />); await start();
      await waitFor(() => expect(apiServiceMock.prepareElectronicSignatureEnvelope).toHaveBeenCalledOnce());
      view.rerender(<ElectronicSignatureModal {...current} documentId={16} />);
      await screen.findByText('Иван Иванов');
      const reads = apiServiceMock.getElectronicSignatureHistory.mock.calls.length;
      await act(async () => { if (result === 'error') pending.reject(new Error('stale signature failure')); else pending.resolve({ id: 1 } as never); });
      expect(apiServiceMock.getElectronicSignatureHistory).toHaveBeenCalledTimes(reads);
      expect(screen.queryByText(/Отправка останется выключенной|stale signature failure/)).toBeNull();
      expect((screen.getByLabelText(/Подтверждаю выбранную неизменяемую PDF-версию/) as HTMLInputElement).checked).toBe(false);
    });

    it('never replaces the recipients of a new document with a late old load', async () => {
      const pending = deferred<Array<{ id: number; displayName: string }>>();
      apiServiceMock.getElectronicSignatureRecipients.mockReturnValueOnce(pending.promise);
      const current = props(); const view = render(<ElectronicSignatureModal {...current} />);
      view.rerender(<ElectronicSignatureModal {...current} documentId={16} clientId={4} />);
      await screen.findByText('Иван Иванов');
      await act(async () => pending.resolve([{ id: 99, displayName: 'Старый подписант' }]));
      expect(screen.queryByText('Старый подписант')).toBeNull();
      expect(screen.getByText('Иван Иванов')).toBeTruthy();
    });

    it.each(['archive', 'cancel'] as const)('ignores a late %s response after close', async action => {
      const prepared = { id: 4, publicId: '44444444-4444-4444-8444-444444444444', documentType: 'invoice', documentId: 15, documentVersion: 1, signatureType: 'qualified', signingOrder: 'sequential', provider: 'KonturDiadoc', status: 'prepared', originalFileName: 'invoice.pdf', sizeBytes: 100, checksumSha256: 'abc', archiveRetentionYears: 10, requiresMachineReadablePowerOfAttorney: false, createdAt: '2026-08-01T00:00:00Z', signers: [], events: [] };
      apiServiceMock.getElectronicSignatureHistory.mockResolvedValue([prepared]);
      const pending = deferred<never>();
      const createUrl = vi.fn(); Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createUrl });
      if (action === 'archive') apiServiceMock.downloadElectronicSignatureEvidencePackage.mockReturnValueOnce(pending.promise);
      else apiServiceMock.cancelElectronicSignatureEnvelope.mockReturnValueOnce(pending.promise);
      render(<ElectronicSignatureModal {...props()} />);
      fireEvent.click(await screen.findByRole('button', { name: action === 'archive' ? /Архив/ : 'Отменить' }));
      if (action === 'cancel') fireEvent.click(screen.getByRole('button', { name: 'Отменить версию' }));
      await waitFor(() => expect(action === 'archive' ? apiServiceMock.downloadElectronicSignatureEvidencePackage : apiServiceMock.cancelElectronicSignatureEnvelope).toHaveBeenCalledOnce());
      fireEvent.click(screen.getByRole('button', { name: 'Закрыть' }));
      await act(async () => pending.resolve({ ...prepared, blob: new Blob(['zip']), fileName: 'evidence.zip' } as never));
      expect(createUrl).not.toHaveBeenCalled();
      expect(screen.queryByText(/PDF и история сохранены/)).toBeNull();
      expect(apiServiceMock.getElectronicSignatureHistory).toHaveBeenCalledTimes(1);
    });
  });

  it('prepares an immutable snapshot while the external provider is disabled', async () => {
    const preparePdf = vi.fn(async () => new Blob(['%PDF-1.7'], { type: 'application/pdf' }));
    render(<ElectronicSignatureModal documentType="invoice" documentId={15} documentName="Счёт 15" clientId={3} preparePdf={preparePdf} onClose={() => undefined} />);

    expect(await screen.findByText('Иван Иванов')).toBeTruthy();
    expect(screen.getByText(/Внешняя отправка безопасно выключена/)).toBeTruthy();
    fireEvent.click(screen.getByText('Иван Иванов'));
    fireEvent.click(screen.getByText(/Подтверждаю выбранную неизменяемую PDF-версию/));
    fireEvent.click(screen.getByRole('button', { name: 'Подготовить неизменяемую версию' }));

    await waitFor(() => expect(apiServiceMock.prepareElectronicSignatureEnvelope).toHaveBeenCalledWith(
      'invoice',
      15,
      expect.any(Blob),
      expect.objectContaining({
        clientId: 3,
        signerAccountIds: [7],
        signatureType: 'qualified',
        signingOrder: 'sequential',
        provider: 'KonturDiadoc',
        confirmSnapshotAndSigners: true,
      }),
    ));
    expect(await screen.findByText(/Отправка останется выключенной/)).toBeTruthy();
  });

  it('requires an explicitly granted portal account', async () => {
    apiServiceMock.getElectronicSignatureRecipients.mockResolvedValue([]);
    render(<ElectronicSignatureModal documentType="act" documentId={2} documentName="Акт 2" clientId={3} preparePdf={vi.fn()} onClose={() => undefined} />);

    expect(await screen.findByText(/Сначала выдайте клиенту или представителю явный доступ/)).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Подготовить неизменяемую версию' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('cancels only an unsent prepared version and keeps its history', async () => {
    const prepared = {
      id: 3, publicId: '33333333-3333-4333-8333-333333333333', documentType: 'invoice', documentId: 15,
      documentVersion: 1, signatureType: 'qualified', signingOrder: 'sequential', provider: 'KonturDiadoc',
      status: 'prepared', originalFileName: 'invoice.pdf', sizeBytes: 100, checksumSha256: 'abc',
      archiveRetentionYears: 10, requiresMachineReadablePowerOfAttorney: false, createdAt: '2026-08-01T00:00:00Z',
      signers: [{ id: 7, order: 1, displayName: 'Иван Иванов', maskedContact: 'i***n@example.test', partyType: 'client', status: 'pending', requiresMachineReadablePowerOfAttorney: false }],
      events: [],
    };
    apiServiceMock.getElectronicSignatureHistory.mockResolvedValueOnce([prepared]).mockResolvedValueOnce([{ ...prepared, status: 'cancelled' }]);
    apiServiceMock.cancelElectronicSignatureEnvelope.mockResolvedValue({ ...prepared, status: 'cancelled' });

    render(<ElectronicSignatureModal documentType="invoice" documentId={15} documentName="Счёт 15" clientId={3} preparePdf={vi.fn()} onClose={() => undefined} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Отменить' }));
    expect(screen.getByRole('dialog', { name: 'Отменить подготовленную версию?' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Отменить версию' }));

    await waitFor(() => expect(apiServiceMock.cancelElectronicSignatureEnvelope).toHaveBeenCalledWith(prepared.publicId));
    expect(await screen.findByText(/PDF и история сохранены/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Отменить' })).toBeNull();
  });

  it('downloads an internal evidence package for a prepared version', async () => {
    const prepared = {
      id: 4, publicId: '44444444-4444-4444-8444-444444444444', documentType: 'invoice', documentId: 15,
      documentVersion: 1, signatureType: 'qualified', signingOrder: 'sequential', provider: 'KonturDiadoc',
      status: 'prepared', originalFileName: 'invoice.pdf', sizeBytes: 100, checksumSha256: 'abc',
      archiveRetentionYears: 10, requiresMachineReadablePowerOfAttorney: false, createdAt: '2026-08-01T00:00:00Z',
      signers: [], events: [],
    };
    apiServiceMock.getElectronicSignatureHistory.mockResolvedValue([prepared]);
    apiServiceMock.downloadElectronicSignatureEvidencePackage.mockResolvedValue({ blob: new Blob(['zip']), fileName: 'evidence.zip' });
    const createObjectUrl = vi.fn(() => 'blob:evidence');
    const revokeObjectUrl = vi.fn();
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectUrl });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: revokeObjectUrl });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);

    render(<ElectronicSignatureModal documentType="invoice" documentId={15} documentName="Счёт 15" clientId={3} preparePdf={vi.fn()} onClose={() => undefined} />);
    fireEvent.click(await screen.findByRole('button', { name: /Архив/ }));

    await waitFor(() => expect(apiServiceMock.downloadElectronicSignatureEvidencePackage).toHaveBeenCalledWith(prepared.publicId));
    expect(createObjectUrl).toHaveBeenCalledTimes(1);
    expect(revokeObjectUrl).toHaveBeenCalledWith('blob:evidence');
    click.mockRestore();
  });
});
