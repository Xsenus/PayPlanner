import { useEffect, useState } from 'react';
import { apiService, type CasePositionTemplateDto } from '../../services/api';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

interface Props {
  text: string;
  onChange: (text: string) => void;
  disabled?: boolean;
}

export function CasePositionTemplatePicker({ text, onChange, disabled = false }: Props) {
  const [templates, setTemplates] = useState<CasePositionTemplateDto[]>([]);
  const [canManageShared, setCanManageShared] = useState(false);
  const [selectedId, setSelectedId] = useState('');
  const [editingId, setEditingId] = useState<number | null>(null);
  const [title, setTitle] = useState('');
  const [body, setBody] = useState('');
  const [scope, setScope] = useState<'personal' | 'shared'>('personal');
  const [showEditor, setShowEditor] = useState(false);
  const [archiveConfirmId, setArchiveConfirmId] = useState<number | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    if (disabled) return;
    let active = true;
    void apiService.getCasePositionTemplates()
      .then((result) => {
        if (!active) return;
        setTemplates(result.items);
        setCanManageShared(result.canManageShared);
      })
      .catch((reason) => { if (active) setError(extractDisplayErrorMessage(reason, 'Не удалось загрузить шаблоны позиции.')); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [disabled]);

  if (disabled) return null;

  const selected = templates.find((item) => String(item.id) === selectedId);

  const insertSelected = () => {
    if (!selected) return;
    const next = text.trim() ? `${text.trimEnd()}\n\n${selected.text}` : selected.text;
    if (next.length > 4000) {
      setError('После вставки текст позиции превысит 4000 символов. Сократите его перед применением шаблона.');
      return;
    }
    setError('');
    onChange(next);
  };

  const resetEditor = () => {
    setShowEditor(false);
    setEditingId(null);
    setTitle('');
    setBody('');
    setScope('personal');
  };

  const startEdit = () => {
    if (!selected?.canManage) return;
    setEditingId(selected.id);
    setTitle(selected.title);
    setBody(selected.text);
    setScope(selected.scope);
    setShowEditor(true);
    setError('');
  };

  const saveTemplate = async () => {
    if (!title.trim() || !body.trim()) {
      setError('Укажите название и текст шаблона.');
      return;
    }
    setSaving(true);
    setError('');
    try {
      const payload = { title: title.trim(), text: body.trim(), scope };
      const saved = editingId === null
        ? await apiService.createCasePositionTemplate(payload)
        : await apiService.updateCasePositionTemplate(editingId, payload);
      setTemplates((current) => [...current.filter((item) => item.id !== saved.id), saved]
        .sort((a, b) => (a.scope === b.scope ? a.title.localeCompare(b.title, 'ru') : a.scope === 'shared' ? -1 : 1)));
      setSelectedId(String(saved.id));
      resetEditor();
    } catch (reason) {
      setError(extractDisplayErrorMessage(reason, 'Не удалось сохранить шаблон.'));
    } finally {
      setSaving(false);
    }
  };

  const archiveTemplate = async () => {
    if (!selected?.canManage || archiveConfirmId !== selected.id) return;
    setSaving(true);
    setError('');
    try {
      await apiService.archiveCasePositionTemplate(selected.id);
      setTemplates((current) => current.filter((item) => item.id !== selected.id));
      setSelectedId('');
      setArchiveConfirmId(null);
      resetEditor();
    } catch (reason) {
      setError(extractDisplayErrorMessage(reason, 'Не удалось архивировать шаблон.'));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="mt-2 rounded-lg border border-blue-100 bg-blue-50/40 p-3 text-xs text-slate-700">
      <p className="font-semibold">Заготовки позиции по делу</p>
      <p className="mt-1 text-slate-500">Общие шаблоны видны юристам; личные — только вам. Вставленный текст можно изменить перед сохранением дела.</p>
      <div className="mt-2 flex flex-wrap items-end gap-2">
        <label className="min-w-52 flex-1">
          <span className="sr-only">Шаблон позиции</span>
          <select aria-label="Шаблон позиции" value={selectedId} onChange={(event) => { setSelectedId(event.target.value); setArchiveConfirmId(null); }} disabled={loading || saving} className="h-9 w-full rounded-lg border border-slate-200 bg-white px-2 text-xs">
            <option value="">{loading ? 'Загрузка…' : 'Выберите шаблон'}</option>
            {templates.map((item) => <option key={item.id} value={item.id}>{item.scope === 'shared' ? 'Общий' : 'Личный'} · {item.title}</option>)}
          </select>
        </label>
        <button type="button" disabled={!selected || saving} onClick={insertSelected} className="h-9 rounded-lg border border-blue-200 bg-white px-3 font-medium text-blue-700 disabled:opacity-50">Вставить в позицию</button>
        <button type="button" disabled={saving} onClick={() => { setEditingId(null); setTitle(''); setBody(text.slice(0, 4000)); setScope('personal'); setShowEditor(true); setError(''); }} className="h-9 rounded-lg border border-slate-200 bg-white px-3 font-medium disabled:opacity-50">Новый шаблон</button>
        {selected?.canManage ? <button type="button" disabled={saving} onClick={startEdit} className="h-9 rounded-lg border border-slate-200 bg-white px-3 font-medium disabled:opacity-50">Изменить шаблон</button> : null}
        {selected?.canManage ? <button type="button" disabled={saving} onClick={() => setArchiveConfirmId(selected.id)} className="h-9 rounded-lg border border-rose-200 bg-white px-3 font-medium text-rose-700 disabled:opacity-50">В архив</button> : null}
      </div>
      {archiveConfirmId === selected?.id ? <div className="mt-2 flex items-center gap-2"><span>Архивировать «{selected.title}»? Текст уже сохранённых дел останется.</span><button type="button" disabled={saving} onClick={() => void archiveTemplate()} className="font-semibold text-rose-700">Подтвердить</button><button type="button" onClick={() => setArchiveConfirmId(null)}>Отмена</button></div> : null}
      {showEditor ? (
        <div className="mt-3 grid gap-2 rounded-lg border border-slate-200 bg-white p-3">
          <label>Название шаблона<input aria-label="Название шаблона позиции" maxLength={160} value={title} onChange={(event) => setTitle(event.target.value)} className="mt-1 w-full rounded-lg border border-slate-200 px-2 py-1.5" /></label>
          <label>Текст шаблона<textarea aria-label="Текст шаблона позиции" maxLength={4000} rows={5} value={body} onChange={(event) => setBody(event.target.value)} className="mt-1 w-full rounded-lg border border-slate-200 px-2 py-1.5" /></label>
          {editingId === null ? <label>Доступность<select aria-label="Доступность шаблона позиции" value={scope} onChange={(event) => setScope(event.target.value as 'personal' | 'shared')} className="ml-2 rounded-lg border border-slate-200 px-2 py-1.5"><option value="personal">Личный</option>{canManageShared ? <option value="shared">Общий</option> : null}</select></label> : <p>Тип шаблона: {scope === 'shared' ? 'общий' : 'личный'}. При изменении шаблона уже сохранённые дела не меняются.</p>}
          <div className="flex gap-2"><button type="button" disabled={saving} onClick={() => void saveTemplate()} className="rounded-lg bg-blue-600 px-3 py-1.5 font-semibold text-white disabled:opacity-50">{saving ? 'Сохраняем…' : 'Сохранить шаблон'}</button><button type="button" disabled={saving} onClick={resetEditor} className="rounded-lg border border-slate-200 px-3 py-1.5">Отмена</button></div>
        </div>
      ) : null}
      {error ? <p role="alert" className="mt-2 text-rose-700">{error}</p> : null}
    </div>
  );
}
