import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from '../../services/api';
import { CasePositionTemplatePicker } from './CasePositionTemplatePicker';


import { documentCopyTestSession } from '../../test/documentCopySession';
const nestedAuth = vi.hoisted(() => ({ id: 7, role: 4 }));
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: nestedAuth.id, role: { id: nestedAuth.role } } }) }));
let nestedSession: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { nestedSession = documentCopyTestSession(); nestedAuth.id = 7; nestedAuth.role = 4; });
afterEach(() => nestedSession?.restore());
const deferred = <T,>() => { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((yes,no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
vi.mock('../../services/api', () => ({
  apiService: {
    getCasePositionTemplates: vi.fn(),
    createCasePositionTemplate: vi.fn(),
    updateCasePositionTemplate: vi.fn(),
    archiveCasePositionTemplate: vi.fn(),
  },
}));

const shared = {
  id: 1, title: 'Возражения', text: 'Проверить доводы и доказательства.',
  scope: 'shared' as const, updatedAt: '2026-09-25T00:00:00Z', canManage: false,
};

describe('CasePositionTemplatePicker', () => {
  beforeEach(() => {
    vi.mocked(apiService.getCasePositionTemplates).mockReset();
    vi.mocked(apiService.getCasePositionTemplates).mockResolvedValue({ items: [shared], canManageShared: false });
    vi.mocked(apiService.createCasePositionTemplate).mockReset();
    vi.mocked(apiService.updateCasePositionTemplate).mockReset();
    vi.mocked(apiService.archiveCasePositionTemplate).mockReset();
  });

  it('inserts a copy after existing case text without changing the saved case automatically', async () => {
    const onChange = vi.fn();
    render(<CasePositionTemplatePicker text="Моя позиция" onChange={onChange} />);

    await screen.findByRole('option', { name: 'Общий · Возражения' });
    fireEvent.change(screen.getByRole('combobox', { name: 'Шаблон позиции' }), { target: { value: '1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Вставить в позицию' }));

    expect(onChange).toHaveBeenCalledWith('Моя позиция\n\nПроверить доводы и доказательства.');
    expect(apiService.updateCasePositionTemplate).not.toHaveBeenCalled();
  });

  it('creates a personal template while hiding shared management from a non-admin', async () => {
    vi.mocked(apiService.createCasePositionTemplate).mockResolvedValue({
      id: 2, title: 'Моя заготовка', text: 'Текст дела', scope: 'personal',
      updatedAt: '2026-09-25T00:00:00Z', canManage: true,
    });
    render(<CasePositionTemplatePicker text="Текст дела" onChange={vi.fn()} />);
    await screen.findByRole('option', { name: 'Общий · Возражения' });

    fireEvent.click(screen.getByRole('button', { name: 'Новый шаблон' }));
    expect(screen.queryByRole('option', { name: 'Общий' })).toBeNull();
    fireEvent.change(screen.getByRole('textbox', { name: 'Название шаблона позиции' }), { target: { value: 'Моя заготовка' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить шаблон' }));

    await waitFor(() => expect(apiService.createCasePositionTemplate).toHaveBeenCalledWith({
      title: 'Моя заготовка', text: 'Текст дела', scope: 'personal',
    }));
    expect(await screen.findByRole('option', { name: 'Личный · Моя заготовка' })).toBeTruthy();
  });

  it('keeps the draft unchanged when insertion would exceed the case field limit', async () => {
    const onChange = vi.fn();
    render(<CasePositionTemplatePicker text={'x'.repeat(3999)} onChange={onChange} />);
    await screen.findByRole('option', { name: 'Общий · Возражения' });
    fireEvent.change(screen.getByRole('combobox', { name: 'Шаблон позиции' }), { target: { value: '1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Вставить в позицию' }));
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.getByRole('alert').textContent).toContain('4000 символов');
  });

  it('lets an administrator create a shared template', async () => {
    vi.mocked(apiService.getCasePositionTemplates).mockResolvedValue({ items: [], canManageShared: true });
    vi.mocked(apiService.createCasePositionTemplate).mockResolvedValue({
      id: 3, title: 'Общая позиция', text: 'Текст', scope: 'shared',
      updatedAt: '2026-09-25T00:00:00Z', canManage: true,
    });
    render(<CasePositionTemplatePicker text="Текст" onChange={vi.fn()} />);
    await waitFor(() => expect(apiService.getCasePositionTemplates).toHaveBeenCalled());
    fireEvent.click(screen.getByRole('button', { name: 'Новый шаблон' }));
    fireEvent.change(screen.getByRole('combobox', { name: 'Доступность шаблона позиции' }), { target: { value: 'shared' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Название шаблона позиции' }), { target: { value: 'Общая позиция' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить шаблон' }));
    await waitFor(() => expect(apiService.createCasePositionTemplate).toHaveBeenCalledWith({
      title: 'Общая позиция', text: 'Текст', scope: 'shared',
    }));
  });

  it('updates and archives only a manageable template after confirmation', async () => {
    vi.mocked(apiService.getCasePositionTemplates).mockResolvedValue({
      items: [{ ...shared, canManage: true }], canManageShared: true,
    });
    vi.mocked(apiService.updateCasePositionTemplate).mockResolvedValue({
      ...shared, text: 'Исправленная позиция', canManage: true,
    });
    vi.mocked(apiService.archiveCasePositionTemplate).mockResolvedValue(undefined);
    const onChange = vi.fn();
    render(<CasePositionTemplatePicker text="Позиция конкретного дела" onChange={onChange} />);
    await screen.findByRole('option', { name: 'Общий · Возражения' });
    fireEvent.change(screen.getByRole('combobox', { name: 'Шаблон позиции' }), { target: { value: '1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Изменить шаблон' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Текст шаблона позиции' }), { target: { value: 'Исправленная позиция' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить шаблон' }));
    await waitFor(() => expect(apiService.updateCasePositionTemplate).toHaveBeenCalledWith(1, {
      title: 'Возражения', text: 'Исправленная позиция', scope: 'shared',
    }));
    expect(onChange).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'В архив' }));
    expect(apiService.archiveCasePositionTemplate).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Подтвердить' }));
    await waitFor(() => expect(apiService.archiveCasePositionTemplate).toHaveBeenCalledWith(1));
    expect(screen.queryByRole('option', { name: 'Общий · Возражения' })).toBeNull();
    expect(onChange).not.toHaveBeenCalled();
  });
  it('nested picker baseline: late template creation cannot enter a reopened editor', async () => {
    const pending=deferred<Awaited<ReturnType<typeof apiService.createCasePositionTemplate>>>();vi.mocked(apiService.createCasePositionTemplate).mockReturnValueOnce(pending.promise);const props={text:'Позиция',onChange:vi.fn()};const view=render(<CasePositionTemplatePicker {...props}/>);await screen.findByRole('option',{name:'Общий · Возражения'});fireEvent.click(screen.getByRole('button',{name:'Новый шаблон'}));fireEvent.change(screen.getByLabelText('Название шаблона позиции'),{target:{value:'ПРЕЖНИЙ ШАБЛОН'}});fireEvent.click(screen.getByRole('button',{name:'Сохранить шаблон'}));view.rerender(<CasePositionTemplatePicker {...props} disabled/>);view.rerender(<CasePositionTemplatePicker {...props}/>);await act(async()=>pending.resolve({id:91,title:'ПРЕЖНИЙ ШАБЛОН',text:'Позиция',scope:'personal',updatedAt:'2026-11-02T00:00:00Z',canManage:true}));expect(document.body.textContent).not.toContain('ПРЕЖНИЙ ШАБЛОН');expect(props.onChange).not.toHaveBeenCalled();
  });

  it.each((['create','update','archive'] as const).flatMap(kind=>(['case','rights','role','login'] as const).flatMap(change=>['success','error'].map(outcome=>({kind,change,outcome})))))('nested picker boundary: $kind ignores $outcome after $change',async({kind,change,outcome})=>{
    vi.mocked(apiService.getCasePositionTemplates).mockResolvedValue({items:[{...shared,canManage:true}],canManageShared:true});const pending=deferred<unknown>();const api=kind==='create'?vi.mocked(apiService.createCasePositionTemplate):kind==='update'?vi.mocked(apiService.updateCasePositionTemplate):vi.mocked(apiService.archiveCasePositionTemplate);api.mockReturnValueOnce(pending.promise as never);const props={caseId:15,text:'Позиция',onChange:vi.fn()};const view=render(<CasePositionTemplatePicker {...props}/>);await screen.findByRole('option',{name:'Общий · Возражения'});
    if(kind==='create'){fireEvent.click(screen.getByRole('button',{name:'Новый шаблон'}));fireEvent.change(screen.getByLabelText('Название шаблона позиции'),{target:{value:'ПРЕЖНИЙ ШАБЛОН'}});fireEvent.click(screen.getByRole('button',{name:'Сохранить шаблон'}));}else{fireEvent.change(screen.getByLabelText('Шаблон позиции'),{target:{value:'1'}});fireEvent.click(screen.getByRole('button',{name:kind==='update'?'Изменить шаблон':'В архив'}));if(kind==='update'){fireEvent.change(screen.getByLabelText('Название шаблона позиции'),{target:{value:'ПРЕЖНИЙ ШАБЛОН'}});fireEvent.click(screen.getByRole('button',{name:'Сохранить шаблон'}));}else fireEvent.click(screen.getByRole('button',{name:'Подтвердить'}));}expect(api).toHaveBeenCalledOnce();
    if(change==='case')view.rerender(<CasePositionTemplatePicker {...props} caseId={16}/>);else if(change==='rights'){view.rerender(<CasePositionTemplatePicker {...props} disabled/>);view.rerender(<CasePositionTemplatePicker {...props}/>);}else if(change==='role'){nestedAuth.role++;view.rerender(<CasePositionTemplatePicker {...props}/>);}else nestedSession.replace();await act(async()=>{if(outcome==='error')pending.reject(new Error('ОШИБКА ПРЕЖНЕГО ШАБЛОНА'));else pending.resolve(kind==='archive'?undefined:{...shared,id:kind==='create'?91:1,title:'ПРЕЖНИЙ ШАБЛОН',text:kind==='create'?'Позиция':shared.text,scope:kind==='create'?'personal':'shared',canManage:true});});expect(document.body.textContent).not.toContain('ОШИБКА ПРЕЖНЕГО ШАБЛОНА');expect(screen.queryByRole('option',{name:/ПРЕЖНИЙ ШАБЛОН/})).toBeNull();if(change==='login'){if(kind==='archive')expect(screen.getByRole('button',{name:'Подтвердить'})).toBeTruthy();else expect(screen.getByLabelText('Название шаблона позиции')).toHaveProperty('value','ПРЕЖНИЙ ШАБЛОН');}expect(props.onChange).not.toHaveBeenCalled();
  });
  it('nested picker reads: role replacement aborts old catalogue without reading again on typing',async()=>{
    const pending=deferred<Awaited<ReturnType<typeof apiService.getCasePositionTemplates>>>();vi.mocked(apiService.getCasePositionTemplates).mockReturnValueOnce(pending.promise);const props={caseId:15,text:'Исходная',onChange:vi.fn()};const view=render(<CasePositionTemplatePicker {...props}/>);await act(async()=>{});const signal=vi.mocked(apiService.getCasePositionTemplates).mock.calls[0][0]?.signal;view.rerender(<CasePositionTemplatePicker {...props} text="Новая ручная позиция"/>);expect(apiService.getCasePositionTemplates).toHaveBeenCalledOnce();nestedAuth.role++;view.rerender(<CasePositionTemplatePicker {...props}/>);expect(signal?.aborted).toBe(true);await act(async()=>pending.resolve({items:[{...shared,title:'СТАРЫЙ КАТАЛОГ'}],canManageShared:true}));expect(screen.queryByRole('option',{name:/СТАРЫЙ КАТАЛОГ/})).toBeNull();
  });
  it('nested picker permission: injected shared scope cannot create a shared template for a non-admin',async()=>{
    render(<CasePositionTemplatePicker text="Позиция" onChange={vi.fn()}/>);await screen.findByRole('option',{name:'Общий · Возражения'});fireEvent.click(screen.getByRole('button',{name:'Новый шаблон'}));fireEvent.change(screen.getByLabelText('Название шаблона позиции'),{target:{value:'Личный'}});const select=screen.getByLabelText('Доступность шаблона позиции');const option=document.createElement('option');option.value='shared';option.textContent='Скрытый общий';select.append(option);fireEvent.change(select,{target:{value:'shared'}});fireEvent.click(screen.getByRole('button',{name:'Сохранить шаблон'}));await screen.findByText('Недостаточно прав для общего шаблона.');expect(apiService.createCasePositionTemplate).not.toHaveBeenCalled();
  });
  it('nested picker serialization: create reserves synchronously and current failure permits retry with draft',async()=>{
    const pending=deferred<Awaited<ReturnType<typeof apiService.createCasePositionTemplate>>>();vi.mocked(apiService.createCasePositionTemplate).mockReturnValueOnce(pending.promise);render(<CasePositionTemplatePicker text="Позиция" onChange={vi.fn()}/>);await screen.findByRole('option',{name:'Общий · Возражения'});fireEvent.click(screen.getByRole('button',{name:'Новый шаблон'}));fireEvent.change(screen.getByLabelText('Название шаблона позиции'),{target:{value:'Сохранённый черновик'}});const button=screen.getByRole('button',{name:'Сохранить шаблон'});act(()=>{fireEvent.click(button);fireEvent.click(button);});expect(apiService.createCasePositionTemplate).toHaveBeenCalledOnce();await act(async()=>pending.reject(new Error('Повторите запись шаблона')));await screen.findByText('Повторите запись шаблона');expect(screen.getByLabelText('Название шаблона позиции')).toHaveProperty('value','Сохранённый черновик');vi.mocked(apiService.createCasePositionTemplate).mockResolvedValueOnce({...shared,id:2,title:'Сохранённый черновик',text:'Позиция',scope:'personal',canManage:true});fireEvent.click(screen.getByRole('button',{name:'Сохранить шаблон'}));await screen.findByRole('option',{name:'Личный · Сохранённый черновик'});expect(apiService.createCasePositionTemplate).toHaveBeenCalledTimes(2);
  });

  it('nested picker retry: current catalogue failure blocks actions and retry keeps the latest case text',async()=>{
    vi.mocked(apiService.getCasePositionTemplates).mockRejectedValueOnce(new Error('Шаблоны временно недоступны'));const props={text:'Исходная позиция',onChange:vi.fn()};const view=render(<CasePositionTemplatePicker {...props}/>);await screen.findByText('Шаблоны временно недоступны');expect(screen.getByRole('button',{name:'Новый шаблон'})).toHaveProperty('disabled',true);view.rerender(<CasePositionTemplatePicker {...props} text="Ручная текущая позиция"/>);fireEvent.click(screen.getByRole('button',{name:'Повторить загрузку шаблонов позиции'}));await screen.findByRole('option',{name:'Общий · Возражения'});fireEvent.change(screen.getByLabelText('Шаблон позиции'),{target:{value:'1'}});fireEvent.click(screen.getByRole('button',{name:'Вставить в позицию'}));expect(props.onChange).toHaveBeenCalledExactlyOnceWith('Ручная текущая позиция\n\nПроверить доводы и доказательства.');expect(apiService.getCasePositionTemplates).toHaveBeenCalledTimes(2);
  });

});
