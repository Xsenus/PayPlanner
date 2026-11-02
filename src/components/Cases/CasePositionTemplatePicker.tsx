import { useCallback, useRef, useState } from 'react';
import { useDictionarySnapshot } from '../../hooks/useDictionarySnapshot';
import { useNestedCaseOperationOwner, type CaseOperationParent, type NestedCaseOperationOwner } from '../../hooks/useNestedCaseOperationOwner';
import { awaitOwnedUiResult, StaleUiOperationError } from '../../utils/StaleUiOperationError';
import { apiService, type CasePositionTemplateDto } from '../../services/api';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

interface Props {
  caseId?: number;
  actionOwner?: CaseOperationParent;
  text: string;
  onChange: (text: string) => void;
  disabled?: boolean;
}

const EMPTY_TEMPLATES = { items: [] as CasePositionTemplateDto[], canManageShared: false };
export function CasePositionTemplatePicker(props: Props) {
  const owner = useNestedCaseOperationOwner(JSON.stringify(['position-templates', props.caseId, props.disabled ?? false]), props.actionOwner);
  return owner.active && !props.disabled ? <OwnedPositionTemplates key={owner.scope} {...props} owner={owner} /> : null;
}
function OwnedPositionTemplates({ text, onChange, owner }: Props & { owner: NestedCaseOperationOwner }) {
  const [selectedId, setSelectedId] = useState('');
  const [editingId, setEditingId] = useState<number | null>(null);
  const [editingUpdatedAt, setEditingUpdatedAt] = useState('');
  const [title, setTitle] = useState('');
  const [body, setBody] = useState('');
  const [scope, setScope] = useState<'personal' | 'shared'>('personal');
  const [showEditor, setShowEditor] = useState(false);
  const [archiveConfirmId, setArchiveConfirmId] = useState<number | null>(null);
  const [actionError, setActionError] = useState('');
  const { capture } = owner;
  const validTemplate = (item: CasePositionTemplateDto) => item && Number.isSafeInteger(item.id) && item.id > 0 && typeof item.title === 'string' && typeof item.text === 'string'
    && ['personal', 'shared'].includes(item.scope) && typeof item.canManage === 'boolean' && typeof item.updatedAt === 'string';
  const loadTemplates = useCallback(async (signal: AbortSignal) => {
    const read = capture(); if (!read) throw new StaleUiOperationError();
    const result = await awaitOwnedUiResult(() => apiService.getCasePositionTemplates({ signal }), () => read.isCurrent() && !signal.aborted);
    if (!result || !Array.isArray(result.items) || !result.items.every(item => item && Number.isSafeInteger(item.id) && item.id > 0 && typeof item.title === 'string' && typeof item.text === 'string'
      && ['personal', 'shared'].includes(item.scope) && typeof item.canManage === 'boolean' && typeof item.updatedAt === 'string') || typeof result.canManageShared !== 'boolean'
      || new Set(result.items.map(item => item.id)).size !== result.items.length) throw new Error('Не удалось подтвердить список шаблонов позиции.');
    return result;
  }, [capture]);
  const templateRead = useDictionarySnapshot(loadTemplates, EMPTY_TEMPLATES, { ownerScope: owner.scope }, false);
  const templates = templateRead.value.items; const canManageShared = templateRead.value.canManageShared;
  const loading = templateRead.loading; const saving = owner.busy; const error = actionError || templateRead.error || '';
  const currentRead = useRef(templateRead); currentRead.current = templateRead;
  const currentDraft = useRef({ text, onChange }); currentDraft.current = { text, onChange };
  const selected = templates.find(item => String(item.id) === selectedId);
  const ready = () => !!owner.capture() && !currentRead.current.loading && !currentRead.current.error;
  const insertSelected = () => {
    if (!ready() || saving || !selected || !currentRead.current.value.items.some(item => item.id === selected.id && item.updatedAt === selected.updatedAt)) return;
    const currentText = currentDraft.current.text;
    const next = currentText.trim() ? `${currentText.trimEnd()}\n\n${selected.text}` : selected.text;
    if (next.length > 4000) { setActionError('После вставки текст позиции превысит 4000 символов. Сократите его перед применением шаблона.'); return; }
    setActionError(''); currentDraft.current.onChange(next);
  };
  const resetEditor = () => { setShowEditor(false); setEditingId(null); setEditingUpdatedAt(''); setTitle(''); setBody(''); setScope('personal'); };
  const startEdit = () => {
    if (!ready() || saving || !selected?.canManage) return;
    setEditingId(selected.id); setEditingUpdatedAt(selected.updatedAt); setTitle(selected.title); setBody(selected.text); setScope(selected.scope); setShowEditor(true); setActionError('');
  };
  const saveTemplate = async () => {
    if (!ready() || !showEditor) return;
    if (!title.trim() || !body.trim()) { setActionError('Укажите название и текст шаблона.'); return; }
    if (title.trim().length > 160 || body.trim().length > 4000) { setActionError('Название шаблона — до 160 символов, текст — до 4000.'); return; }
    const existing = currentRead.current.value.items.find(item => item.id === editingId);
    if (editingId !== null && (!existing?.canManage || existing.scope !== scope || existing.updatedAt !== editingUpdatedAt)) { setActionError('Шаблон изменился или больше недоступен для редактирования. Откройте его заново.'); return; }
    if (!['personal', 'shared'].includes(scope) || (editingId === null && scope === 'shared' && !currentRead.current.value.canManageShared)) { setActionError('Недостаточно прав для общего шаблона.'); return; }
    const operation = owner.begin('position-template:save'); if (!operation) return;
    setActionError('');
    try {
      const payload = { title: title.trim(), text: body.trim(), scope };
      const saved = await awaitOwnedUiResult(() => editingId === null ? apiService.createCasePositionTemplate(payload) : apiService.updateCasePositionTemplate(editingId, payload), operation.isCurrent);
      if (!validTemplate(saved) || saved.title !== payload.title || saved.text !== payload.text || saved.scope !== payload.scope || (editingId !== null && saved.id !== editingId)) throw new Error('Не удалось подтвердить сохранённый шаблон. Обновите список перед повтором.');
      templateRead.updateValue(current => ({ ...current, items: [...current.items.filter(item => item.id !== saved.id), saved]
        .sort((a, b) => a.scope === b.scope ? a.title.localeCompare(b.title, 'ru') : a.scope === 'shared' ? -1 : 1) }));
      setSelectedId(String(saved.id)); resetEditor();
    } catch (cause) { if (operation.isCurrent() && !(cause instanceof StaleUiOperationError)) setActionError(extractDisplayErrorMessage(cause, 'Не удалось сохранить шаблон.')); }
    finally { operation.finish(); }
  };
  const archiveTemplate = async () => {
    if (!ready() || !selected?.canManage || archiveConfirmId !== selected.id || !currentRead.current.value.items.some(item => item.id === selected.id && item.canManage && item.updatedAt === selected.updatedAt)) return;
    const operation = owner.begin('position-template:archive'); if (!operation) return;
    setActionError('');
    try {
      await awaitOwnedUiResult(() => apiService.archiveCasePositionTemplate(selected.id), operation.isCurrent);
      templateRead.updateValue(current => ({ ...current, items: current.items.filter(item => item.id !== selected.id) }));
      setSelectedId(''); setArchiveConfirmId(null); resetEditor();
    } catch (cause) { if (operation.isCurrent() && !(cause instanceof StaleUiOperationError)) setActionError(extractDisplayErrorMessage(cause, 'Не удалось архивировать шаблон.')); }
    finally { operation.finish(); }
  };
  const retry = async () => {
    const operation = owner.begin('position-template:retry'); if (!operation) return;
    setActionError('');
    try { await awaitOwnedUiResult(templateRead.refresh, operation.isCurrent); }
    catch (cause) { if (operation.isCurrent() && !(cause instanceof StaleUiOperationError)) setActionError(extractDisplayErrorMessage(cause, 'Не удалось загрузить шаблоны позиции.')); }
    finally { operation.finish(); }
  };

  return (
    <div className="mt-2 rounded-lg border border-blue-100 bg-blue-50/40 p-3 text-xs text-slate-700">
      <p className="font-semibold">Заготовки позиции по делу</p>
      <p className="mt-1 text-slate-500">Общие шаблоны видны юристам; личные — только вам. Вставленный текст можно изменить перед сохранением дела.</p>
      <div className="mt-2 flex flex-wrap items-end gap-2">
        <label className="min-w-52 flex-1">
          <span className="sr-only">Шаблон позиции</span>
          <select aria-label="Шаблон позиции" value={selectedId} onChange={(event) => { const value = event.target.value; if (!ready() || saving || (value && !templates.some(item => String(item.id) === value))) return; setSelectedId(value); setArchiveConfirmId(null); }} disabled={loading || saving} className="h-9 w-full rounded-lg border border-slate-200 bg-white px-2 text-xs">
            <option value="">{loading ? 'Загрузка…' : 'Выберите шаблон'}</option>
            {templates.map((item) => <option key={item.id} value={item.id}>{item.scope === 'shared' ? 'Общий' : 'Личный'} · {item.title}</option>)}
          </select>
        </label>
        <button type="button" disabled={!selected || saving || loading || !!templateRead.error} onClick={insertSelected} className="h-9 rounded-lg border border-blue-200 bg-white px-3 font-medium text-blue-700 disabled:opacity-50">Вставить в позицию</button>
        <button type="button" disabled={saving || loading || !!templateRead.error} onClick={() => { if (!ready()) return; setEditingId(null); setTitle(''); setBody(text.slice(0, 4000)); setScope('personal'); setShowEditor(true); setActionError(''); }} className="h-9 rounded-lg border border-slate-200 bg-white px-3 font-medium disabled:opacity-50">Новый шаблон</button>
        {selected?.canManage ? <button type="button" disabled={saving} onClick={startEdit} className="h-9 rounded-lg border border-slate-200 bg-white px-3 font-medium disabled:opacity-50">Изменить шаблон</button> : null}
        {selected?.canManage ? <button type="button" disabled={saving} onClick={() => { if (ready() && !saving) setArchiveConfirmId(selected.id); }} className="h-9 rounded-lg border border-rose-200 bg-white px-3 font-medium text-rose-700 disabled:opacity-50">В архив</button> : null}
      </div>
      {archiveConfirmId === selected?.id ? <div className="mt-2 flex items-center gap-2"><span>Архивировать «{selected.title}»? Текст уже сохранённых дел останется.</span><button type="button" disabled={saving} onClick={() => void archiveTemplate()} className="font-semibold text-rose-700">Подтвердить</button><button type="button" disabled={saving} onClick={() => setArchiveConfirmId(null)}>Отмена</button></div> : null}
      {showEditor ? (
        <div className="mt-3 grid gap-2 rounded-lg border border-slate-200 bg-white p-3">
          <label>Название шаблона<input disabled={saving} aria-label="Название шаблона позиции" maxLength={160} value={title} onChange={(event) => setTitle(event.target.value)} className="mt-1 w-full rounded-lg border border-slate-200 px-2 py-1.5" /></label>
          <label>Текст шаблона<textarea disabled={saving} aria-label="Текст шаблона позиции" maxLength={4000} rows={5} value={body} onChange={(event) => setBody(event.target.value)} className="mt-1 w-full rounded-lg border border-slate-200 px-2 py-1.5" /></label>
          {editingId === null ? <label>Доступность<select disabled={saving} aria-label="Доступность шаблона позиции" value={scope} onChange={(event) => setScope(event.target.value as 'personal' | 'shared')} className="ml-2 rounded-lg border border-slate-200 px-2 py-1.5"><option value="personal">Личный</option>{canManageShared ? <option value="shared">Общий</option> : null}</select></label> : <p>Тип шаблона: {scope === 'shared' ? 'общий' : 'личный'}. При изменении шаблона уже сохранённые дела не меняются.</p>}
          <div className="flex gap-2"><button type="button" disabled={saving} onClick={() => void saveTemplate()} className="rounded-lg bg-blue-600 px-3 py-1.5 font-semibold text-white disabled:opacity-50">{saving ? 'Сохраняем…' : 'Сохранить шаблон'}</button><button type="button" disabled={saving} onClick={resetEditor} className="rounded-lg border border-slate-200 px-3 py-1.5">Отмена</button></div>
        </div>
      ) : null}
      {templateRead.error ? <button type="button" disabled={saving || loading} onClick={() => void retry()} className="mt-2 font-medium text-blue-700 hover:underline">Повторить загрузку шаблонов позиции</button> : null}
      {error ? <p role="alert" className="mt-2 text-rose-700">{error}</p> : null}
    </div>
  );
}
