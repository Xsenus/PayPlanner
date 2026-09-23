import { useEffect, useMemo, useRef, useState } from 'react';
import { GitMerge, Loader2, Search, X } from 'lucide-react';
import {
  apiService,
  type ContactDirectoryItemDto,
  type ContactMergePreviewDto,
  type ContactMergeRequestDto,
} from '../../services/api';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { findContactDuplicateGroups } from '../../utils/contactDuplicates';

type Props = { onClose: () => void; onMerged: () => void };

function keyOf(contact: ContactDirectoryItemDto) {
  return `${contact.sourceType}:${contact.sourceId}`;
}

function kindLabel(contact: ContactDirectoryItemDto) {
  if (contact.sourceType === 'client') return 'Клиент';
  if (contact.sourceType === 'legal') return 'Организация';
  return contact.hasFullProfile ? 'Быстрый контакт с полной карточкой' : 'Быстрый контакт';
}

function contactOption(contact: ContactDirectoryItemDto) {
  return `${contact.fullName} · ${kindLabel(contact)} · ${contact.phone ?? contact.phones[0] ?? contact.email ?? contact.emails[0] ?? `#${contact.sourceId}`}`;
}

export function ContactMergeModal({ onClose, onMerged }: Props) {
  const { confirm } = useDialog();
  const toast = useToast();
  const [contacts, setContacts] = useState<ContactDirectoryItemDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [primaryKey, setPrimaryKey] = useState('');
  const [duplicateKey, setDuplicateKey] = useState('');
  const [primarySearch, setPrimarySearch] = useState('');
  const [duplicateSearch, setDuplicateSearch] = useState('');
  const [preview, setPreview] = useState<ContactMergePreviewDto | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const [merging, setMerging] = useState(false);
  const previewRequestId = useRef(0);

  useEffect(() => {
    let mounted = true;
    void apiService.getContacts({ scope: 'all', includeLinkedProfiles: true }).then((items) => {
      if (mounted) setContacts(items.filter((item) => item.isActive && !item.auditIsDeleted));
    }).catch((cause) => {
      if (mounted) setError(extractDisplayErrorMessage(cause, 'Не удалось загрузить контакты'));
    }).finally(() => { if (mounted) setLoading(false); });
    return () => { mounted = false; };
  }, []);

  const groups = useMemo(() => findContactDuplicateGroups(contacts), [contacts]);
  const byKey = useMemo(() => new Map(contacts.map((item) => [keyOf(item), item])), [contacts]);
  const primary = byKey.get(primaryKey);
  const duplicate = byKey.get(duplicateKey);
  const differentKinds = Boolean(primary && duplicate && primary.contactType !== duplicate.contactType);
  const differentSources = Boolean(primary && duplicate && primary.sourceType !== duplicate.sourceType);
  const unsupportedMixedPair = differentSources && !(duplicate?.sourceType === 'external'
    && (primary?.sourceType === 'client' || primary?.sourceType === 'legal'));
  const sameRecord = Boolean(primaryKey && primaryKey === duplicateKey);

  useEffect(() => {
    const requestId = ++previewRequestId.current;
    setPreview(null);
    setError(null);
    if (!primary || !duplicate || sameRecord || differentKinds || unsupportedMixedPair) return;
    setPreviewing(true);
    const request: ContactMergeRequestDto = {
      primarySourceType: primary.sourceType,
      primarySourceId: primary.sourceId,
      duplicateSourceType: duplicate.sourceType,
      duplicateSourceId: duplicate.sourceId,
    };
    void apiService.previewContactMerge(request).then((result) => {
      if (requestId === previewRequestId.current) setPreview(result);
    }).catch((cause) => {
      if (requestId === previewRequestId.current) setError(extractDisplayErrorMessage(cause, 'Не удалось проверить карточки'));
    }).finally(() => {
      if (requestId === previewRequestId.current) setPreviewing(false);
    });
  }, [differentKinds, duplicate, primary, sameRecord, unsupportedMixedPair]);

  const chooseGroup = (group: (typeof groups)[number]) => {
    const suggestedPrimary = group.contacts.find((contact) => contact.sourceType !== 'external') ?? group.contacts[0];
    const suggestedDuplicate = group.contacts.find((contact) => keyOf(contact) !== keyOf(suggestedPrimary))!;
    setPrimaryKey(keyOf(suggestedPrimary));
    setDuplicateKey(keyOf(suggestedDuplicate));
    setPrimarySearch(suggestedPrimary.fullName);
    setDuplicateSearch(suggestedDuplicate.fullName);
  };

  const filteredOptions = (search: string, selectedKey: string) => {
    const query = search.trim().toLocaleLowerCase('ru');
    const result = contacts.filter((item) => !query || contactOption(item).toLocaleLowerCase('ru').includes(query)).slice(0, 50);
    const selected = byKey.get(selectedKey);
    if (selected && !result.some((item) => keyOf(item) === selectedKey)) result.unshift(selected);
    return result;
  };

  const merge = async () => {
    if (!primary || !duplicate || !preview?.canMerge || merging) return;
    const approved = await confirm({
      title: 'Объединить контакты?',
      description: `Основной останется «${primary.fullName}» (#${primary.sourceId}). «${duplicate.fullName}» (#${duplicate.sourceId}) будет архивирован; ${preview.affectedLinks} связей будут перенесены. Отменить перенос связей одной кнопкой нельзя.`,
      confirmLabel: 'Объединить и архивировать',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;
    setMerging(true);
    setError(null);
    try {
      await apiService.mergeContacts({
        primarySourceType: primary.sourceType,
        primarySourceId: primary.sourceId,
        duplicateSourceType: duplicate.sourceType,
        duplicateSourceId: duplicate.sourceId,
      });
      toast.success('Контакты объединены. Дубль сохранён в архиве.', 'Контакты');
      onMerged();
      onClose();
    } catch (cause) {
      setError(extractDisplayErrorMessage(cause, 'Не удалось объединить контакты'));
    } finally {
      setMerging(false);
    }
  };

  return (
    <div className="fixed inset-0 z-[1200] flex items-center justify-center bg-slate-950/60 p-3">
      <div role="dialog" aria-modal="true" aria-label="Объединение дублей контактов" className="flex max-h-[92vh] w-full max-w-4xl flex-col overflow-hidden rounded-2xl bg-white shadow-2xl">
        <div className="flex items-start justify-between border-b border-slate-200 p-3">
          <div>
            <h2 className="flex items-center gap-2 text-lg font-bold text-slate-900"><GitMerge className="h-5 w-5" /> Объединение дублей</h2>
            <p className="text-sm text-slate-600">Выберите основную карточку вручную. Дубль останется в архиве, связи перейдут к основной.</p>
          </div>
          <button type="button" onClick={onClose} disabled={merging} aria-label="Закрыть объединение" className="rounded-lg p-2 hover:bg-slate-100"><X className="h-4 w-4" /></button>
        </div>
        <div className="space-y-3 overflow-y-auto p-3">
          {loading ? <p role="status" className="flex items-center gap-2 text-sm text-slate-600"><Loader2 className="h-4 w-4 animate-spin" /> Ищем возможные дубли…</p> : null}
          {!loading ? (
            <section className="rounded-xl border border-slate-200 p-3">
              <h3 className="text-sm font-semibold">Возможные совпадения: {groups.length}</h3>
              <p className="text-xs text-slate-500">Совпадения по телефону или email — подсказка, а не автоматическое объединение.</p>
              <div className="mt-2 flex max-h-36 flex-wrap gap-2 overflow-y-auto">
                {groups.length === 0 ? <span className="text-sm text-slate-500">Совпадений не найдено. Можно выбрать две карточки вручную.</span> : groups.map((group) => (
                  <button key={group.contacts.map(keyOf).join('|')} type="button" onClick={() => chooseGroup(group)} className="rounded-lg border border-blue-200 bg-blue-50 px-2.5 py-1.5 text-left text-xs text-blue-800 hover:bg-blue-100">
                    {group.contacts[0].fullName} · {group.contacts.length} записи · {group.matches.slice(0, 2).join(', ')}
                  </button>
                ))}
              </div>
            </section>
          ) : null}
          <div className="grid gap-3 sm:grid-cols-2">
            {([
              { label: 'Основная карточка', value: primaryKey, setValue: setPrimaryKey, search: primarySearch, setSearch: setPrimarySearch },
              { label: 'Дубль в архив', value: duplicateKey, setValue: setDuplicateKey, search: duplicateSearch, setSearch: setDuplicateSearch },
            ] as const).map((field) => (
              <label key={field.label} className="block text-sm font-semibold text-slate-700">
                {field.label}
                <span className="mt-1 flex items-center gap-2 rounded-lg border border-slate-300 px-2"><Search className="h-4 w-4 text-slate-400" /><input type="search" value={field.search} onChange={(event) => field.setSearch(event.target.value)} placeholder="Поиск по имени, телефону или email" className="w-full py-2 outline-none" /></span>
                <select aria-label={field.label} value={field.value} onChange={(event) => field.setValue(event.target.value)} className="mt-1 w-full rounded-lg border border-slate-300 bg-white p-2 text-sm">
                  <option value="">Выберите контакт</option>
                  {filteredOptions(field.search, field.value).map((item) => <option key={keyOf(item)} value={keyOf(item)}>{contactOption(item)}</option>)}
                </select>
              </label>
            ))}
          </div>
          {sameRecord ? <p className="rounded-lg bg-amber-50 p-2 text-sm text-amber-800">Выберите две разные карточки.</p> : null}
          {differentKinds ? <p className="rounded-lg bg-amber-50 p-2 text-sm text-amber-800">Человека нельзя объединить с организацией.</p> : null}
          {unsupportedMixedPair ? <p className="rounded-lg bg-amber-50 p-2 text-sm text-amber-800">Для смешанной пары выберите полную карточку основной, а быстрый контакт — дублем. Так связи дел и документов останутся на полной карточке.</p> : null}
          {previewing ? <p role="status" className="text-sm text-slate-600">Проверяем связи и права…</p> : null}
          {preview ? (
            <section className="rounded-xl border border-slate-200 bg-slate-50 p-3 text-sm">
              <p className="font-semibold">Будет перенесено связей: {preview.affectedLinks}</p>
              <p className="mt-1">Телефоны: {preview.phones.join(', ') || '—'}</p>
              <p>Email: {preview.emails.join(', ') || '—'}</p>
              {preview.conflicts.map((message) => <p key={message} className="mt-1 text-rose-700">{message}</p>)}
              <p className="mt-2 text-xs text-slate-500">Различающиеся сведения, не вошедшие в основную карточку, сохраняются в архивной записи. Её можно найти в журнале удалённых объектов.</p>
            </section>
          ) : null}
          {error ? <p role="alert" className="rounded-lg bg-rose-50 p-2 text-sm text-rose-700">{error}</p> : null}
        </div>
        <div className="flex justify-end gap-2 border-t border-slate-200 p-3">
          <button type="button" onClick={onClose} disabled={merging} className="rounded-lg border border-slate-300 px-3 py-2 text-sm">Отмена</button>
          <button type="button" onClick={() => void merge()} disabled={!preview?.canMerge || previewing || merging} className="rounded-lg bg-blue-600 px-3 py-2 text-sm font-semibold text-white disabled:opacity-50">
            {merging ? 'Объединяем…' : 'Объединить и архивировать'}
          </button>
        </div>
      </div>
    </div>
  );
}
