import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CompanyRequisitesCheck } from './CompanyRequisitesCheck';
import { apiService } from '../../services/api';
import { documentCopyTestSession } from '../../test/documentCopySession';
import type { RequisitesCheck } from '../../types';

vi.mock('../../services/api', () => ({ apiService: { getCompanyRequisitesCheck: vi.fn() } }));
const api = vi.mocked(apiService.getCompanyRequisitesCheck);
const issue = { field: 'inn', message: 'Сохранённый ИНН требует проверки' };
const deferred = <T,>() => { let resolve!: (value: T) => void; let reject!: (value: Error) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };

describe('saved company requisites check states', () => {
  beforeEach(() => { vi.clearAllMocks(); api.mockReset().mockResolvedValue({ legalEntityId: 5, issues: [] }); });

  it('shows a pending check and distinguishes saved data from unsaved input', async () => {
    const pending = deferred<RequisitesCheck>(); api.mockReturnValueOnce(pending.promise);
    render(<CompanyRequisitesCheck companyId={5} />);
    expect(screen.getByRole('status').textContent).toContain('Проверяем сохранённые реквизиты');
    expect(screen.getByText(/Изменения в форме проверяются после сохранения/)).toBeTruthy();
    await act(async () => pending.resolve({ legalEntityId: 5, issues: [] }));
    expect(screen.getByRole('status').textContent).toContain('замечаний нет');
    expect(api).toHaveBeenCalledOnce();
  });

  it('shows a failed check and retries only on an explicit action', async () => {
    api.mockRejectedValueOnce(new Error('Нет доступа к проверке'));
    render(<CompanyRequisitesCheck companyId={5} />);
    expect(await screen.findByRole('alert')).toHaveProperty('textContent', expect.stringContaining('Нет доступа к проверке'));
    expect(screen.queryByText(/замечаний нет/)).toBeNull(); expect(api).toHaveBeenCalledOnce();
    api.mockResolvedValueOnce({ legalEntityId: 5, issues: [issue] });
    fireEvent.click(screen.getByRole('button', { name: 'Повторить проверку' }));
    expect(await screen.findByText(issue.message)).toBeTruthy(); expect(api).toHaveBeenCalledTimes(2);
  });

  it.each(['company', 'saved-version'])('hides previous issues immediately when the %s changes', async change => {
    api.mockResolvedValueOnce({ legalEntityId: 5, issues: [issue] }); const pending = deferred<RequisitesCheck>();
    const view = render(<CompanyRequisitesCheck companyId={5} refreshKey={1} />); await screen.findByText(issue.message);
    api.mockReturnValueOnce(pending.promise); const nextId = change === 'company' ? 6 : 5;
    view.rerender(<CompanyRequisitesCheck companyId={nextId} refreshKey={2} />);
    expect(screen.queryByText(issue.message)).toBeNull(); expect(screen.getByRole('status').textContent).toContain('Проверяем');
    await act(async () => pending.resolve({ legalEntityId: nextId, issues: [] }));
    expect(screen.getByRole('status').textContent).toContain('замечаний нет');
  });

  it('rejects a response with another company ID', async () => {
    api.mockResolvedValueOnce({ legalEntityId: 6, issues: [{ field: 'inn', message: 'foreign details' }] });
    render(<CompanyRequisitesCheck companyId={5} />);
    await screen.findByRole('alert'); expect(screen.queryByText('foreign details')).toBeNull(); expect(screen.queryByText(/замечаний нет/)).toBeNull();
  });

  it.each([null, { legalEntityId: 5 }, { legalEntityId: 5, issues: 'invalid' }, { legalEntityId: 5, issues: [{ field: 'inn', message: '' }] }])('rejects an invalid result %#', async result => {
    api.mockResolvedValueOnce(result as RequisitesCheck); render(<CompanyRequisitesCheck companyId={5} />);
    await screen.findByRole('alert'); expect(screen.queryByText(/замечаний нет/)).toBeNull();
  });

  it.each(['success', 'error'])('ignores an earlier company %s after the next one succeeds', async result => {
    const pending = deferred<RequisitesCheck>(); api.mockReturnValueOnce(pending.promise).mockResolvedValueOnce({ legalEntityId: 6, issues: [] });
    const view = render(<CompanyRequisitesCheck companyId={5} />); view.rerender(<CompanyRequisitesCheck companyId={6} />);
    await screen.findByText(/замечаний нет/);
    await act(async () => { if (result === 'success') pending.resolve({ legalEntityId: 5, issues: [issue] }); else pending.reject(new Error('old error')); });
    expect(screen.queryByText(issue.message)).toBeNull(); expect(screen.queryByText('old error')).toBeNull(); expect(screen.getByRole('status').textContent).toContain('замечаний нет');
  });

  it.each(['success', 'error'])('ignores late %s after login replacement before rerender', async result => {
    const session = documentCopyTestSession(); const pending = deferred<RequisitesCheck>();
    try {
      api.mockReturnValueOnce(pending.promise); render(<CompanyRequisitesCheck companyId={5} />); session.replace();
      await act(async () => { if (result === 'success') pending.resolve({ legalEntityId: 5, issues: [issue] }); else pending.reject(new Error('old login error')); });
      expect(screen.queryByText(issue.message)).toBeNull(); expect(screen.queryByText('old login error')).toBeNull(); expect(screen.queryByText(/замечаний нет/)).toBeNull();
    } finally { session.restore(); }
  });

  it('hides a former login result and rereads after that login is replaced', async () => {
    const session = documentCopyTestSession();
    try {
      api.mockResolvedValueOnce({ legalEntityId: 5, issues: [issue] }); const view = render(<CompanyRequisitesCheck companyId={5} />); await screen.findByText(issue.message);
      const pending = deferred<RequisitesCheck>(); api.mockReturnValueOnce(pending.promise); session.replace(); view.rerender(<CompanyRequisitesCheck companyId={5} />);
      expect(screen.queryByText(issue.message)).toBeNull(); expect(api).toHaveBeenCalledTimes(2);
      await act(async () => pending.resolve({ legalEntityId: 5, issues: [] })); await screen.findByText(/замечаний нет/);
    } finally { session.restore(); }
  });

  it('does not retry from a former login callback', async () => {
    const session = documentCopyTestSession();
    try {
      api.mockRejectedValueOnce(new Error('Temporary error')); render(<CompanyRequisitesCheck companyId={5} />);
      const retry = await screen.findByRole('button', { name: 'Повторить проверку' }); session.replace(); fireEvent.click(retry);
      expect(api).toHaveBeenCalledOnce();
    } finally { session.restore(); }
  });

  it('retains one request during unrelated rerender and valid session refresh', async () => {
    const session = documentCopyTestSession();
    try {
      api.mockResolvedValueOnce({ legalEntityId: 5, issues: [issue] }); const view = render(<CompanyRequisitesCheck companyId={5} refreshKey="saved" />); await screen.findByText(issue.message);
      session.refresh(); view.rerender(<CompanyRequisitesCheck companyId={5} refreshKey="saved" />);
      await waitFor(() => expect(api).toHaveBeenCalledOnce()); expect(screen.getByText(issue.message)).toBeTruthy();
    } finally { session.restore(); }
  });
});
