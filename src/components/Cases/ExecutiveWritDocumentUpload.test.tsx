import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import type { ClientCaseDocument } from '../../types';
import { ExecutiveWritDocumentUpload } from './ExecutiveWritDocumentUpload';

vi.mock('../../services/api', () => ({
  apiService: {
    downloadCaseDocument: vi.fn(),
    uploadCaseDocument: vi.fn(),
  },
}));

const scan: ClientCaseDocument = {
  id: 7,
  clientCaseId: 15,
  documentKey: 'writ',
  version: 2,
  documentType: 'Исполнительный лист',
  status: 'Получен',
  originalFileName: 'Исполнительный лист.pdf',
  contentType: 'application/pdf',
  sizeBytes: 2048,
  uploadedByName: 'Юрист',
  uploadedAt: '2026-09-27T10:00:00Z',
  isLatestVersion: true,
};

const originalCreateObjectUrl = Object.getOwnPropertyDescriptor(URL, 'createObjectURL');
const originalRevokeObjectUrl = Object.getOwnPropertyDescriptor(URL, 'revokeObjectURL');

describe('ExecutiveWritDocumentUpload', () => {
  beforeEach(() => {
    vi.mocked(apiService.downloadCaseDocument).mockReset();
    vi.mocked(apiService.uploadCaseDocument).mockReset();
  });

  afterEach(() => {
    vi.restoreAllMocks();
    if (originalCreateObjectUrl) Object.defineProperty(URL, 'createObjectURL', originalCreateObjectUrl);
    else Reflect.deleteProperty(URL, 'createObjectURL');
    if (originalRevokeObjectUrl) Object.defineProperty(URL, 'revokeObjectURL', originalRevokeObjectUrl);
    else Reflect.deleteProperty(URL, 'revokeObjectURL');
  });

  it('downloads the latest writ scan without requiring edit access', async () => {
    const createObjectUrl = vi.fn().mockReturnValue('blob:writ');
    const revokeObjectUrl = vi.fn();
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectUrl });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: revokeObjectUrl });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    vi.mocked(apiService.downloadCaseDocument).mockResolvedValue({
      blob: new Blob(['writ'], { type: 'application/pdf' }),
      fileName: 'Скан листа.pdf',
    });

    render(<ExecutiveWritDocumentUpload caseId={15} canUpload={false} documents={[
      { ...scan, id: 6, version: 1, originalFileName: 'Старый скан.pdf', isLatestVersion: false },
      scan,
    ]} onUploaded={vi.fn()} />);

    expect(screen.queryByLabelText('Загрузить скан исполнительного листа')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Скачать скан Старый скан.pdf' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Скачать скан Исполнительный лист.pdf' }));

    await waitFor(() => expect(apiService.downloadCaseDocument).toHaveBeenCalledWith(15, 7));
    expect(createObjectUrl).toHaveBeenCalledOnce();
    expect(click).toHaveBeenCalledOnce();
    expect(revokeObjectUrl).toHaveBeenCalledWith('blob:writ');
  });

  it('shows a download error and leaves the scan available for retry', async () => {
    vi.mocked(apiService.downloadCaseDocument).mockRejectedValue(new Error('Файл недоступен'));
    render(<ExecutiveWritDocumentUpload caseId={15} canUpload documents={[scan]} onUploaded={vi.fn()} />);

    fireEvent.click(screen.getByRole('button', { name: 'Скачать скан Исполнительный лист.pdf' }));

    expect((await screen.findByRole('alert')).textContent).toContain('Файл недоступен');
    expect((screen.getByRole('button', { name: 'Скачать скан Исполнительный лист.pdf' }) as HTMLButtonElement).disabled).toBe(false);
  });
});
