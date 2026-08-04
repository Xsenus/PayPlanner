import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiServiceMock, resetApiServiceMock } from '../../test/apiMock';
import { PublicMessengerDocumentPage } from './PublicMessengerDocumentPage';

vi.mock('../../services/api', async () => ({
  apiService: (await import('../../test/apiMock')).apiServiceMock,
}));

const PUBLIC_ID = '11111111-1111-4111-8111-111111111111';

describe('PublicMessengerDocumentPage', () => {
  beforeEach(() => {
    resetApiServiceMock('downloadPublicMessengerDocument');
    sessionStorage.clear();
    window.history.replaceState(null, '', `/public/document/${PUBLIC_ID}#token=one-time-secret`);
  });

  it('removes the token from URL and waits for an explicit user action', async () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    const createObjectUrl = vi.fn(() => 'blob:public-document');
    const revokeObjectUrl = vi.fn();
    vi.stubGlobal('URL', Object.assign(URL, {
      createObjectURL: createObjectUrl,
      revokeObjectURL: revokeObjectUrl,
    }));
    apiServiceMock.downloadPublicMessengerDocument.mockResolvedValue({
      blob: new Blob(['pdf']),
      fileName: 'Договор.pdf',
    });

    render(<PublicMessengerDocumentPage />);

    expect(window.location.hash).toBe('');
    expect(apiServiceMock.downloadPublicMessengerDocument).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Скачать документ' }));

    await waitFor(() => {
      expect(apiServiceMock.downloadPublicMessengerDocument).toHaveBeenCalledWith(PUBLIC_ID, 'one-time-secret');
      expect(click).toHaveBeenCalledTimes(1);
    });
    expect(createObjectUrl).toHaveBeenCalledTimes(1);
    expect(revokeObjectUrl).toHaveBeenCalledWith('blob:public-document');
    expect(await screen.findByText(/Повторно открыть эту ссылку нельзя/)).toBeTruthy();
    expect(sessionStorage.getItem(`pp:public-document:${PUBLIC_ID}`)).toBeNull();

    click.mockRestore();
    vi.unstubAllGlobals();
  });

  it('does not call API when the link has no token', () => {
    window.history.replaceState(null, '', `/public/document/${PUBLIC_ID}`);

    render(<PublicMessengerDocumentPage />);

    expect(screen.getByText(/Ссылка неполная/)).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Скачать документ' }) as HTMLButtonElement).disabled).toBe(true);
    expect(apiServiceMock.downloadPublicMessengerDocument).not.toHaveBeenCalled();
  });
});
