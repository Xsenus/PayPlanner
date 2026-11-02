import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import type { ClientCaseDocument } from '../../types';
import { ExecutiveWritDocumentUpload } from './ExecutiveWritDocumentUpload';


import { documentCopyTestSession } from '../../test/documentCopySession';
const nestedAuth = vi.hoisted(() => ({ id: 7, role: 4 }));
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: nestedAuth.id, role: { id: nestedAuth.role } } }) }));
let nestedSession: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { nestedSession = documentCopyTestSession(); nestedAuth.id = 7; nestedAuth.role = 4; });
afterEach(() => nestedSession?.restore());
const deferred = <T,>() => { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((yes,no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
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
      { ...scan, id: 8, documentKey: 'cancelled-writ', status: 'Аннулирован', originalFileName: 'Аннулированный лист.pdf' },
      { ...scan, id: 9, documentKey: 'draft-writ', status: 'Подготовлен', originalFileName: 'Черновик листа.pdf' },
      scan,
    ]} onUploaded={vi.fn()} />);

    expect(screen.queryByLabelText('Загрузить скан исполнительного листа')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Скачать скан Старый скан.pdf' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Скачать скан Аннулированный лист.pdf' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Скачать скан Черновик листа.pdf' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Скачать скан Исполнительный лист.pdf' }));

    await waitFor(() => expect(apiService.downloadCaseDocument).toHaveBeenCalledWith(15, 7, { signal: expect.any(AbortSignal) }));
    expect(createObjectUrl).toHaveBeenCalledOnce();
    expect(click).toHaveBeenCalledOnce();
    await waitFor(() => expect(revokeObjectUrl).toHaveBeenCalledWith('blob:writ'), { timeout: 1600 });
  });

  it('shows a download error and leaves the scan available for retry', async () => {
    vi.mocked(apiService.downloadCaseDocument).mockRejectedValue(new Error('Файл недоступен'));
    render(<ExecutiveWritDocumentUpload caseId={15} canUpload documents={[scan]} onUploaded={vi.fn()} />);

    fireEvent.click(screen.getByRole('button', { name: 'Скачать скан Исполнительный лист.pdf' }));

    expect((await screen.findByRole('alert')).textContent).toContain('Файл недоступен');
    expect((screen.getByRole('button', { name: 'Скачать скан Исполнительный лист.pdf' }) as HTMLButtonElement).disabled).toBe(false);
  });
  it('nested writ baseline: late upload cannot refresh a different case', async () => {
    const pending=deferred<ClientCaseDocument>();vi.mocked(apiService.uploadCaseDocument).mockReturnValueOnce(pending.promise);const props={caseId:15,canUpload:true,documents:[scan],onUploaded:vi.fn(),onAttemptSettled:vi.fn()};const view=render(<ExecutiveWritDocumentUpload {...props}/>);fireEvent.change(screen.getByLabelText('Загрузить скан исполнительного листа'),{target:{files:[new File(['x'],'writ.pdf')]}});view.rerender(<ExecutiveWritDocumentUpload {...props} caseId={16} documents={[]}/>);await act(async()=>pending.resolve(scan));expect(props.onUploaded).not.toHaveBeenCalled();expect(props.onAttemptSettled).not.toHaveBeenCalled();
  });

  it.each((['upload', 'download'] as const).flatMap(kind => (['case', 'rights', 'role', 'login', 'unmount'] as const).flatMap(change => ['success', 'error'].map(outcome => ({ kind, change, outcome })))))('nested writ boundary: $kind ignores $outcome after $change', async ({ kind, change, outcome }) => {
    const createUrl = vi.fn().mockReturnValue('blob:late-writ'); const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    if (kind === 'download') Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createUrl });
    const pending=deferred<unknown>(); const api=kind==='upload'?vi.mocked(apiService.uploadCaseDocument):vi.mocked(apiService.downloadCaseDocument); api.mockReturnValueOnce(pending.promise as never);
    const props={caseId:15,canUpload:true,documents:[scan],onUploaded:vi.fn(),onAttemptSettled:vi.fn()};const view=render(<ExecutiveWritDocumentUpload {...props}/>);
    if(kind==='upload')fireEvent.change(screen.getByLabelText('Загрузить скан исполнительного листа'),{target:{files:[new File(['x'],'writ.pdf')]}});else fireEvent.click(screen.getByRole('button',{name:'Скачать скан Исполнительный лист.pdf'}));expect(api).toHaveBeenCalledOnce();
    const signal=kind==='download'?vi.mocked(apiService.downloadCaseDocument).mock.calls[0][2]?.signal:undefined;
    if(change==='case')view.rerender(<ExecutiveWritDocumentUpload {...props} caseId={16} documents={[]}/>);else if(change==='rights')view.rerender(<ExecutiveWritDocumentUpload {...props} canUpload={false}/>);else if(change==='role'){nestedAuth.role++;view.rerender(<ExecutiveWritDocumentUpload {...props}/>);}else if(change==='login')nestedSession.replace();else view.unmount();
    if(signal&&change!=='login')expect(signal.aborted).toBe(true);
    await act(async()=>{if(outcome==='error')pending.reject(new Error('ПРЕЖНИЙ СКАН'));else pending.resolve(kind==='upload'?scan:{blob:new Blob(['x']),fileName:'old.pdf'});});expect(props.onUploaded).not.toHaveBeenCalled();expect(props.onAttemptSettled).not.toHaveBeenCalled();expect(document.body.textContent).not.toContain('ПРЕЖНИЙ СКАН'); if (kind === 'download') { expect(createUrl).not.toHaveBeenCalled(); expect(click).not.toHaveBeenCalled(); }
  });
  it('nested writ serialization: duplicate upload dispatches once and current refresh failure is explicit',async()=>{
    const pending=deferred<ClientCaseDocument>();vi.mocked(apiService.uploadCaseDocument).mockReturnValueOnce(pending.promise);const props={caseId:15,canUpload:true,documents:[scan],onUploaded:vi.fn().mockRejectedValue(new Error('Refresh failed')),onAttemptSettled:vi.fn()};render(<ExecutiveWritDocumentUpload {...props}/>);const input=screen.getByLabelText('Загрузить скан исполнительного листа');act(()=>{fireEvent.change(input,{target:{files:[new File(['x'],'writ.pdf')]}});fireEvent.change(input,{target:{files:[new File(['x'],'writ.pdf')]}});});expect(apiService.uploadCaseDocument).toHaveBeenCalledOnce();await act(async()=>pending.resolve(scan));await screen.findByText('Скан загружен, но список документов не обновился. Проверьте его перед повторной загрузкой.');expect(props.onUploaded).toHaveBeenCalledOnce();expect(props.onAttemptSettled).toHaveBeenCalledOnce();expect(apiService.uploadCaseDocument).toHaveBeenCalledOnce();
  });
  it('nested writ validation: a scan from another case is not offered',()=>{render(<ExecutiveWritDocumentUpload caseId={16} canUpload={false} documents={[scan]} onUploaded={vi.fn()}/>);expect(screen.queryByRole('button',{name:'Скачать скан Исполнительный лист.pdf'})).toBeNull();expect(apiService.downloadCaseDocument).not.toHaveBeenCalled();});

});
