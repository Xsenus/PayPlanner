import { act,fireEvent,render,screen } from '@testing-library/react';
import { beforeEach,afterEach,describe,it,expect,vi } from 'vitest';
import { documentCopyTestSession } from '../../test/documentCopySession';
import { CourtRecoveryPanel } from './CourtRecoveryPanel';
import { CaseAppealDecisionAction } from './CaseAppealDecisionAction';
import type { ClientCase } from '../../types';
vi.mock('../../contexts/AuthContext',()=>({useAuth:()=>({user:{id:7,role:{id:4}}})}));
let session:ReturnType<typeof documentCopyTestSession>;
beforeEach(()=>{session=documentCopyTestSession();});afterEach(()=>{session.restore();vi.unstubAllGlobals();});
const wire=(value:unknown)=>JSON.parse(JSON.stringify(value,(_key,item)=>item===null?undefined:item));
const item={paymentId:7,date:'2026-09-24T00:00:00',description:'Подтверждённый платёж',paidAmount:25000,allocatedAmount:null,effectiveAmount:0,warning:null};
const snapshot={awardedAmount:100000,receivedAmount:0,remainingAmount:100000,linkedPayments:[],availablePayments:[item],hasMoreAvailablePayments:false,hasCompletePaymentVisibility:true};
describe('case finance MVC wire contract',()=>{
 it('reads omitted nullable payment fields and saves exact cents through the real API client',async()=>{
  let allocated:number|null=null;const calls:{path:string;method:string;body:unknown}[]=[];
  vi.stubGlobal('fetch',vi.fn<typeof globalThis.fetch>().mockImplementation(async(input,init)=>{const path=new URL(String(input),'http://localhost').pathname;const method=init?.method??'GET';const body=init?.body?JSON.parse(String(init.body)):null;calls.push({path,method,body});if(!path.endsWith('/court-recoveries')&&!path.endsWith('/court-recoveries/7'))throw Error('Unexpected request');if(method==='PUT'){allocated=body.amount;return new Response(null,{status:204});}return Response.json(wire({...snapshot,receivedAmount:allocated??0,remainingAmount:100000-(allocated??0),linkedPayments:allocated===null?[]:[{...item,allocatedAmount:allocated,effectiveAmount:allocated}],availablePayments:allocated===null?[item]:[]}));}));
  render(<CourtRecoveryPanel caseId={3} canEdit/>);fireEvent.change(await screen.findByRole('combobox',{name:'Платёж для взыскания'}),{target:{value:'7'}});fireEvent.change(screen.getByRole('textbox',{name:'Сумма платежа в счёт взыскания'}),{target:{value:'0,29'}});fireEvent.click(screen.getByRole('button',{name:'Сохранить связь'}));await screen.findByRole('button',{name:'Убрать связь'});expect(calls.filter(c=>c.method==='PUT')).toEqual([{path:expect.stringMatching(/\/v2\/cases\/3\/court-recoveries\/7$/),method:'PUT',body:{amount:0.29}}]);expect(calls.filter(c=>c.method==='GET')).toHaveLength(2);expect(screen.queryByRole('alert')).toBeNull();
 });
 it.each(['no-award','limited'] as const)('reads nullable overview amounts omitted in %s response',async mode=>{
  vi.stubGlobal('fetch',vi.fn<typeof globalThis.fetch>().mockImplementation(async()=>Response.json(wire({...snapshot,awardedAmount:mode==='no-award'?null:100000,receivedAmount:mode==='limited'?null:0,remainingAmount:null,hasCompletePaymentVisibility:mode!=='limited'}))));
  render(<CourtRecoveryPanel caseId={3} canEdit/>);if(mode==='no-award')await screen.findByText(/Сначала сохраните присуждённую сумму/);else expect((await screen.findByText(/Подтверждено платежами:/)).textContent).toContain('—');expect(screen.queryByRole('alert')).toBeNull();
 });
 it.each([true,false])('keeps historical appeal choice when nullable task ID is omitted, edit=%s',async canEdit=>{
  const fetch=vi.fn<typeof globalThis.fetch>().mockImplementation(async input=>{const path=new URL(String(input),'http://localhost').pathname;if(path.endsWith('/user-settings/profile.timeZoneId'))return Response.json({key:'profile.timeZoneId',value:'UTC'});if(path.endsWith('/appeal-decision'))return Response.json(wire({id:8,caseId:3,choice:'appeal',taskId:null,decidedAt:'2026-09-24T00:00:00Z',decidedByUserName:'Исторический юрист'}));throw Error('Unexpected request');});vi.stubGlobal('fetch',fetch);
  const open=vi.fn();render(<CaseAppealDecisionAction clientCase={{id:3,status:'Open',courtDecisionAt:'2026-09-20',appealDeadlineAt:'2099-10-20'} as ClientCase} canEdit={canEdit} onOpenTask={open}/>);await screen.findByText(/Решено обжаловать.*Исторический юрист/);await act(async()=>{});expect(screen.queryByRole('alert')).toBeNull();expect(screen.queryByRole('button',{name:'Открыть задачу'})).toBeNull();expect(screen.queryByRole('button',{name:'Обжаловать — создать задачу'})).toBeNull();expect(open).not.toHaveBeenCalled();expect(fetch.mock.calls.filter(([url])=>String(url).includes('/appeal-decision'))).toHaveLength(1);
 });
});
