import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import type { ClientCase, ClientCaseDocument, ClientCaseHearingPreparation } from '../../types';
import { CaseHearingPreparationChecklist } from './CaseHearingPreparationChecklist';


import { documentCopyTestSession } from '../../test/documentCopySession';
const nestedAuth = vi.hoisted(() => ({ id: 7, role: 4 }));
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: nestedAuth.id, role: { id: nestedAuth.role } } }) }));
let nestedSession: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { nestedSession = documentCopyTestSession(); nestedAuth.id = 7; nestedAuth.role = 4; });
afterEach(() => nestedSession?.restore());
const deferred = <T,>() => { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((yes,no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
vi.mock('../../services/api', () => ({
  apiService: {
    getCaseHearingPreparation: vi.fn(),
    getCaseDocuments: vi.fn(),
    updateCaseHearingPreparation: vi.fn(),
  },
}));

const hearingAt = '2099-10-20T15:00:00Z';
const caseItem = { id: 42, title: 'Дело', status: 'Open', nextHearingAt: hearingAt } as ClientCase;
const preparation: ClientCaseHearingPreparation = {
  hearingAt,
  contractCheckedAt: null,
  contractCheckedByName: '',
  authorityCheckedAt: null,
  authorityCheckedByName: '',
  materialsCheckedAt: null,
  materialsCheckedByName: '',
  documentIds: [],
};

describe('CaseHearingPreparationChecklist', () => {
  beforeEach(() => {
    vi.mocked(apiService.getCaseHearingPreparation).mockReset().mockResolvedValue(preparation);
    vi.mocked(apiService.getCaseDocuments).mockReset().mockResolvedValue([{
      id: 7, clientCaseId: 42, originalFileName: 'Позиция.pdf',
    }] as never);
    vi.mocked(apiService.updateCaseHearingPreparation).mockReset().mockImplementation(async (_caseId, payload) => ({
      ...preparation,
      ...(payload.checkKind === 'contract' ? { contractCheckedAt: '2099-10-19T10:00:00Z', contractCheckedByName: 'Юрист' } : {}),
      ...(payload.checkKind === 'materials' ? { materialsCheckedAt: '2099-10-19T10:00:00Z', documentIds: payload.documentIds ?? [] } : {}),
    }));
  });

  it('records separate checks and links documents for the saved hearing', async () => {
    render(<CaseHearingPreparationChecklist caseItem={caseItem} timeZoneId="UTC" canEdit />);
    expect(await screen.findByText('Позиция.pdf')).toBeTruthy();
    fireEvent.click(screen.getAllByRole('button', { name: 'Подтвердить' })[0]);
    await waitFor(() => expect(apiService.updateCaseHearingPreparation).toHaveBeenCalledWith(42, {
      hearingAt, checkKind: 'contract', checked: true,
    }));
    fireEvent.click(screen.getByRole('checkbox'));
    fireEvent.click(screen.getAllByRole('button', { name: 'Подтвердить' })[1]);
    await waitFor(() => expect(apiService.updateCaseHearingPreparation).toHaveBeenCalledWith(42, {
      hearingAt, checkKind: 'materials', checked: true, documentIds: [7],
    }));
  });

  it('shows checks without editing to a viewer and disables saving an unsaved date', async () => {
    const view = render(<CaseHearingPreparationChecklist caseItem={caseItem} timeZoneId="UTC" canEdit={false} />);
    await screen.findByText('Позиция.pdf');
    expect(screen.queryByRole('button', { name: 'Подтвердить' })).toBeNull();
    view.rerender(<CaseHearingPreparationChecklist caseItem={caseItem} timeZoneId="UTC" canEdit datesSaved={false} />);
    expect(screen.getByText(/Сначала сохраните новую дату/)).toBeTruthy();
    expect(apiService.updateCaseHearingPreparation).not.toHaveBeenCalled();
  });

  it('loads separate preparation after hearing date moves', async () => {
    const view = render(<CaseHearingPreparationChecklist caseItem={caseItem} timeZoneId="UTC" canEdit />);
    await screen.findByText('Позиция.pdf');
    const movedAt = '2099-10-22T15:00:00Z';
    vi.mocked(apiService.getCaseHearingPreparation).mockResolvedValueOnce({ ...preparation, hearingAt: movedAt });
    view.rerender(<CaseHearingPreparationChecklist caseItem={{ ...caseItem, nextHearingAt: movedAt }} timeZoneId="UTC" canEdit />);
    await waitFor(() => expect(apiService.getCaseHearingPreparation).toHaveBeenCalledTimes(2));
    expect(screen.getAllByText('Не проверено').length).toBe(3);
  });

  it('removes materials approval when the linked document set changes', async () => {
    vi.mocked(apiService.getCaseHearingPreparation).mockResolvedValueOnce({
      ...preparation, materialsCheckedAt: '2099-10-19T10:00:00Z',
      materialsCheckedByName: 'Юрист', documentIds: [7],
    });
    render(<CaseHearingPreparationChecklist caseItem={caseItem} timeZoneId="UTC" canEdit />);
    await screen.findByText('Позиция.pdf');
    fireEvent.click(screen.getByRole('checkbox'));
    expect(screen.getByText(/После сохранения отметку проверки материалов нужно будет поставить заново/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить связи с документами' }));
    await waitFor(() => expect(apiService.updateCaseHearingPreparation).toHaveBeenCalledWith(42, {
      hearingAt, checkKind: 'materials', checked: false, documentIds: [],
    }));
  });
  it('nested preparation baseline: a late save cannot approve a different hearing', async () => {
    const pending=deferred<ClientCaseHearingPreparation>();vi.mocked(apiService.updateCaseHearingPreparation).mockReturnValueOnce(pending.promise);const props={caseItem,timeZoneId:'UTC',canEdit:true};const view=render(<CaseHearingPreparationChecklist {...props}/>);await screen.findByText('Позиция.pdf');fireEvent.click(screen.getAllByRole('button',{name:'Подтвердить'})[0]);const movedAt='2099-10-22T15:00:00Z';vi.mocked(apiService.getCaseHearingPreparation).mockResolvedValueOnce({...preparation,hearingAt:movedAt});view.rerender(<CaseHearingPreparationChecklist {...props} caseItem={{...caseItem,nextHearingAt:movedAt}}/>);await act(async()=>{});await act(async()=>pending.resolve({...preparation,contractCheckedAt:'2099-10-19T00:00:00Z',contractCheckedByName:'ПРЕЖНЯЯ ОТМЕТКА'}));expect(document.body.textContent).not.toContain('ПРЕЖНЯЯ ОТМЕТКА');
  });

  it.each((['hearing','rights','role','login','unmount'] as const).flatMap(change=>['success','error'].map(outcome=>({change,outcome}))))('nested preparation boundary: save $outcome after $change is not applied',async({change,outcome})=>{
    const pending=deferred<ClientCaseHearingPreparation>();vi.mocked(apiService.updateCaseHearingPreparation).mockReturnValueOnce(pending.promise);const props={caseItem,timeZoneId:'UTC',canEdit:true};const view=render(<CaseHearingPreparationChecklist {...props}/>);await screen.findByText('Позиция.pdf');fireEvent.click(screen.getAllByRole('button',{name:'Подтвердить'})[0]);expect(apiService.updateCaseHearingPreparation).toHaveBeenCalledOnce();
    if(change==='hearing'){const movedAt='2099-10-23T15:00:00Z';vi.mocked(apiService.getCaseHearingPreparation).mockResolvedValueOnce({...preparation,hearingAt:movedAt});view.rerender(<CaseHearingPreparationChecklist {...props} caseItem={{...caseItem,nextHearingAt:movedAt}}/>);}else if(change==='rights')view.rerender(<CaseHearingPreparationChecklist {...props} canEdit={false}/>);else if(change==='role'){nestedAuth.role++;view.rerender(<CaseHearingPreparationChecklist {...props}/>);}else if(change==='login')nestedSession.replace();else view.unmount();
    await act(async()=>{if(outcome==='error')pending.reject(new Error('ПРЕЖНЯЯ ПРОВЕРКА'));else pending.resolve({...preparation,contractCheckedAt:'2099-10-19T00:00:00Z',contractCheckedByName:'ПРЕЖНЯЯ ПРОВЕРКА'});});expect(document.body.textContent).not.toContain('ПРЕЖНЯЯ ПРОВЕРКА');
  });
  it('nested preparation reads: a role change aborts old preparation and document requests',async()=>{
    const pending=deferred<ClientCaseHearingPreparation>();vi.mocked(apiService.getCaseHearingPreparation).mockReturnValueOnce(pending.promise);const props={caseItem,timeZoneId:'UTC',canEdit:true};const view=render(<CaseHearingPreparationChecklist {...props}/>);await act(async()=>{});const signal=vi.mocked(apiService.getCaseHearingPreparation).mock.calls[0][1]?.signal;expect(signal).toBeInstanceOf(AbortSignal);nestedAuth.role++;view.rerender(<CaseHearingPreparationChecklist {...props}/>);expect(signal?.aborted).toBe(true);await act(async()=>pending.resolve({...preparation,contractCheckedAt:'2099-10-19T00:00:00Z',contractCheckedByName:'СТАРЫЙ ПРОСМОТР'}));expect(document.body.textContent).not.toContain('СТАРЫЙ ПРОСМОТР');
  });
  it('nested preparation draft: independent contract approval and changed parent files retain manual materials',async()=>{
    const files=[{id:7,clientCaseId:42,originalFileName:'Позиция.pdf'}] as ClientCaseDocument[];const props={caseItem,timeZoneId:'UTC',canEdit:true,initialDocuments:files};const view=render(<CaseHearingPreparationChecklist {...props}/>);await screen.findByText('Позиция.pdf');fireEvent.click(screen.getByRole('checkbox'));fireEvent.click(screen.getAllByRole('button',{name:'Подтвердить'})[0]);await waitFor(()=>expect(screen.getByText(/Проверено.*Юрист/)).toBeTruthy());expect(screen.getByRole('checkbox')).toHaveProperty('checked',true);const reads=vi.mocked(apiService.getCaseHearingPreparation).mock.calls.length;view.rerender(<CaseHearingPreparationChecklist {...props} initialDocuments={[...files]}/>);await act(async()=>{});expect(screen.getByRole('checkbox')).toHaveProperty('checked',true);expect(apiService.getCaseHearingPreparation).toHaveBeenCalledTimes(reads);
  });
  it('nested preparation refresh: explicit document refresh makes a real GET even with parent supplied files',async()=>{
    render(<CaseHearingPreparationChecklist caseItem={caseItem} timeZoneId="UTC" canEdit initialDocuments={[{id:7,clientCaseId:42,originalFileName:'Позиция.pdf'}] as never}/>);await screen.findByText('Позиция.pdf');expect(apiService.getCaseDocuments).not.toHaveBeenCalled();vi.mocked(apiService.getCaseDocuments).mockResolvedValueOnce([{id:8,clientCaseId:42,originalFileName:'Новый файл.pdf'}] as never);fireEvent.click(screen.getByRole('button',{name:'Обновить список'}));await screen.findByText('Новый файл.pdf');expect(apiService.getCaseDocuments).toHaveBeenCalledExactlyOnceWith(42,expect.any(AbortSignal));expect(apiService.getCaseHearingPreparation).toHaveBeenCalledOnce();
  });
  it('nested preparation validation: wrong hearing and foreign file cannot authorize material links',async()=>{
    vi.mocked(apiService.getCaseHearingPreparation).mockResolvedValueOnce({...preparation,hearingAt:'2099-10-25T15:00:00Z'});render(<CaseHearingPreparationChecklist caseItem={caseItem} timeZoneId="UTC" canEdit/>);await screen.findByText('Не удалось подтвердить подготовку к текущему заседанию. Обновите проверки.');expect(screen.queryByRole('button',{name:'Подтвердить'})).toBeNull();expect(apiService.updateCaseHearingPreparation).not.toHaveBeenCalled();
  });
  it('nested preparation serialization: pending save blocks a second check and document refresh',async()=>{
    const pending=deferred<ClientCaseHearingPreparation>();vi.mocked(apiService.updateCaseHearingPreparation).mockReturnValueOnce(pending.promise);render(<CaseHearingPreparationChecklist caseItem={caseItem} timeZoneId="UTC" canEdit/>);await screen.findByText('Позиция.pdf');const buttons=screen.getAllByRole('button',{name:'Подтвердить'});act(()=>{fireEvent.click(buttons[0]);fireEvent.click(buttons[1]);fireEvent.click(screen.getByRole('button',{name:'Обновить список'}));});expect(apiService.updateCaseHearingPreparation).toHaveBeenCalledOnce();expect(apiService.getCaseDocuments).toHaveBeenCalledOnce();await act(async()=>pending.resolve(preparation));
  });

  it('nested preparation retry: current read failure can be retried without submitting any check',async()=>{
    vi.mocked(apiService.getCaseHearingPreparation).mockRejectedValueOnce(new Error('Проверки временно недоступны'));render(<CaseHearingPreparationChecklist caseItem={caseItem} timeZoneId="UTC" canEdit/>);await screen.findByText('Проверки временно недоступны');expect(screen.queryByRole('button',{name:'Подтвердить'})).toBeNull();fireEvent.click(screen.getByRole('button',{name:'Повторить загрузку проверок'}));await screen.findByText('Договор и реквизиты');expect(apiService.getCaseHearingPreparation).toHaveBeenCalledTimes(2);expect(apiService.updateCaseHearingPreparation).not.toHaveBeenCalled();
  });

  it('nested preparation materials: inaccessible linked IDs are preserved and cannot be resubmitted by guessing',async()=>{
    vi.mocked(apiService.getCaseHearingPreparation).mockResolvedValueOnce({...preparation,documentIds:[99]});render(<CaseHearingPreparationChecklist caseItem={caseItem} timeZoneId="UTC" canEdit/>);await screen.findByText('Позиция.pdf');fireEvent.click(screen.getAllByRole('button',{name:'Подтвердить'})[2]);await screen.findByText('Выберите материалы из доступных документов текущего дела. Сначала обновите список.');expect(apiService.updateCaseHearingPreparation).not.toHaveBeenCalled();expect(screen.getByRole('checkbox')).toHaveProperty('checked',false);fireEvent.click(screen.getByRole('button',{name:'Исключить недоступные материалы из выбора'}));expect(apiService.updateCaseHearingPreparation).not.toHaveBeenCalled();vi.mocked(apiService.updateCaseHearingPreparation).mockResolvedValueOnce({...preparation,documentIds:[]});fireEvent.click(screen.getByRole('button',{name:'Сохранить связи с документами'}));await waitFor(()=>expect(apiService.updateCaseHearingPreparation).toHaveBeenCalledExactlyOnceWith(42,{hearingAt,checkKind:'materials',checked:false,documentIds:[]}));
  });

  it('nested preparation unavailable: parent file loading and view-only permission never expose removal action',async()=>{
    vi.mocked(apiService.getCaseHearingPreparation).mockResolvedValue({...preparation,documentIds:[99]});const props={caseItem,timeZoneId:'UTC',canEdit:true,initialDocuments:[]};const view=render(<CaseHearingPreparationChecklist {...props} initialDocumentsLoading/>);await screen.findByText('Договор и реквизиты');expect(screen.queryByRole('button',{name:'Исключить недоступные материалы из выбора'})).toBeNull();view.rerender(<CaseHearingPreparationChecklist {...props} canEdit={false}/>);await screen.findByText(/Недоступны ранее выбранные документы: 1/);expect(screen.queryByRole('button',{name:'Исключить недоступные материалы из выбора'})).toBeNull();expect(apiService.updateCaseHearingPreparation).not.toHaveBeenCalled();
  });

});
