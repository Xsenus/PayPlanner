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
import { useDocumentDeliveryOwner, type DocumentReadOwner } from '../../hooks/useDocumentDeliveryOwner';
import { captureAuthSessionIdentity } from '../../services/authService';

type Props = { onClose: () => void; onMerged: () => void; ownerScope?: string; readOwner?: DocumentReadOwner; enabled?: boolean };

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

export function ContactMergeModal(props: Props) {
  const key = JSON.stringify([props.ownerScope, props.readOwner?.scope, props.enabled, captureAuthSessionIdentity()]);
  return <ContactMergeScreen key={key} {...props} />;
}

function ContactMergeScreen({ onClose, onMerged, ownerScope, readOwner, enabled = true }: Props) {
  const dialogOwner = useDocumentDeliveryOwner(JSON.stringify([ownerScope, readOwner?.scope, enabled]));
  const [closed, setClosed] = useState(false);
  const captureParent = readOwner?.capture;
  const captureDialog = dialogOwner.capture;
  const captureCurrent = () => {
    const local = enabled && !closed ? captureDialog() : null;
    const parent = captureParent?.();
    return local && (!captureParent || parent) ? { isCurrent: () => local.isCurrent() && (!parent || parent.isCurrent()) } : null;
  };
  const { confirm } = useDialog();
  const toast = useToast();
  const [contacts, setContacts] = useState<ContactDirectoryItemDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [primaryKey, setPrimaryKey] = useState('');
  const [duplicateKey, setDuplicateKey] = useState('');
  const [primarySearch, setPrimarySearch] = useState('');
  const [duplicateSearch, setDuplicateSearch] = useState('');
  const [previewState, setPreview] = useState<{ scope: string; value: ContactMergePreviewDto } | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const pairOwner = useDocumentDeliveryOwner(JSON.stringify([dialogOwner.scope, primaryKey, duplicateKey]));
  const { capture: capturePair, scope: pairScope } = pairOwner;
  const merging = pairOwner.busy;
  const preview = previewState?.scope === pairScope && capturePair() && captureCurrent() ? previewState.value : null;
  const [readRetry, setReadRetry] = useState(0);
  const [previewRetry, setPreviewRetry] = useState(0);
  const [readError, setReadError] = useState(false);
  const previewRequestId = useRef(0);
  const pendingConfirmation = useRef<AbortController | null>(null);
  useEffect(() => () => pendingConfirmation.current?.abort(), [pairScope, closed]);

  useEffect(() => {
    const local = enabled && !closed ? captureDialog() : null; const parent = captureParent?.();
    if (!local || (captureParent && !parent)) return;
    const controller = new AbortController();
    const current = () => local.isCurrent() && (!parent || parent.isCurrent()) && !controller.signal.aborted;
    setLoading(true); setError(null); setReadError(false);
    void apiService.getContacts({ scope: 'all', includeLinkedProfiles: true }, { signal: controller.signal }).then((items) => {
      if (current()) setContacts(items.filter((item) => item.isActive && !item.auditIsDeleted));
    }).catch((cause) => {
      if (current()) { setContacts([]); setReadError(true); setError(extractDisplayErrorMessage(cause, 'Не удалось загрузить контакты')); }
    }).finally(() => { if (current()) setLoading(false); });
    return () => controller.abort();
  }, [enabled, closed, captureDialog, captureParent, readRetry]);

  const groups = useMemo(() => findContactDuplicateGroups(contacts), [contacts]);
  const byKey = useMemo(() => new Map(contacts.map((item) => [keyOf(item), item])), [contacts]);
  const primary = byKey.get(primaryKey);
  const duplicate = byKey.get(duplicateKey);
  const differentKinds = Boolean(primary && duplicate && primary.contactType !== duplicate.contactType);
  const differentSources = Boolean(primary && duplicate && primary.sourceType !== duplicate.sourceType);
  const reverseMixedPair = primary?.sourceType === 'external'
    && (duplicate?.sourceType === 'client' || duplicate?.sourceType === 'legal');
  const unsupportedMixedPair = differentSources && !(reverseMixedPair || (duplicate?.sourceType === 'external'
    && (primary?.sourceType === 'client' || primary?.sourceType === 'legal')));
  const sameRecord = Boolean(primaryKey && primaryKey === duplicateKey);

  useEffect(() => {
    const requestId = ++previewRequestId.current;
    const local = capturePair(); const parent = captureParent?.(); const dialog = captureDialog();
    setPreview(null); setPreviewing(false);
    if (!enabled || closed || !local || !dialog || (captureParent && !parent) || !primary || !duplicate || sameRecord || differentKinds || unsupportedMixedPair) return;
    setError(null); setPreviewing(true);
    const controller = new AbortController();
    const current = () => requestId === previewRequestId.current && local.isCurrent() && dialog.isCurrent()
      && (!parent || parent.isCurrent()) && !controller.signal.aborted;
    const request: ContactMergeRequestDto = {
      primarySourceType: primary.sourceType, primarySourceId: primary.sourceId,
      duplicateSourceType: duplicate.sourceType, duplicateSourceId: duplicate.sourceId,
    };
    void apiService.previewContactMerge(request, { signal: controller.signal }).then((result) => {
      if (current()) setPreview({ scope: pairScope, value: result });
    }).catch((cause) => {
      if (current()) setError(extractDisplayErrorMessage(cause, 'Не удалось проверить карточки'));
    }).finally(() => { if (current()) setPreviewing(false); });
    return () => controller.abort();
  }, [differentKinds, duplicate, primary, sameRecord, unsupportedMixedPair, enabled, closed, capturePair, pairScope, captureDialog, captureParent, previewRetry]);

  const chooseGroup = (group: (typeof groups)[number]) => {
    if (!captureCurrent() || merging || !groups.includes(group)) return;
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

  const handleClose = () => {
    if (!captureCurrent() || merging) return;
    setClosed(true); dialogOwner.invalidate(); onClose();
  };
  const merge = async () => {
    const owner = captureCurrent();
    if (!owner || !primary || !duplicate || !preview?.canMerge || previewing) return;
    const operation = pairOwner.begin('merge'); if (!operation) return;
    const confirmation = new AbortController(); pendingConfirmation.current = confirmation;
    const current = () => owner.isCurrent() && operation.isCurrent();
    try {
      const approved = await confirm({
        signal: confirmation.signal,
        title: 'Объединить контакты?',
        description: `Основной останется «${primary.fullName}» (#${primary.sourceId}). «${duplicate.fullName}» (#${duplicate.sourceId}) будет архивирован; ${preview.affectedLinks} связей будут перенесены.${reverseMixedPair ? ' Для выбранного быстрого контакта будет создана новая полная карточка, к ней перейдут дела и документы.' : ''} Отменить перенос связей одной кнопкой нельзя.`,
        confirmLabel: 'Объединить и архивировать', cancelLabel: 'Отмена', variant: 'danger',
      });
      if (!approved || !current()) return;
      setError(null);
      await apiService.mergeContacts({
        primarySourceType: primary.sourceType, primarySourceId: primary.sourceId,
        duplicateSourceType: duplicate.sourceType, duplicateSourceId: duplicate.sourceId,
      });
      if (!current()) return;
      toast.success('Контакты объединены. Дубль сохранён в архиве.', 'Контакты');
      if (!current()) return; onMerged();
      if (!current()) return; dialogOwner.invalidate(); onClose();
    } catch (cause) {
      if (current()) setError(extractDisplayErrorMessage(cause, 'Не удалось объединить контакты'));
    } finally {
      if (pendingConfirmation.current === confirmation) pendingConfirmation.current = null;
      confirmation.abort(); operation.finish();
    }
  };

  if (!captureCurrent()) return null;

  return (
    <div className="fixed inset-0 z-[1200] flex items-center justify-center bg-slate-950/60 p-3">
      <div role="dialog" aria-modal="true" aria-label="Объединение дублей контактов" className="flex max-h-[92vh] w-full max-w-4xl flex-col overflow-hidden rounded-2xl bg-white shadow-2xl">
        <div className="flex items-start justify-between border-b border-slate-200 p-3">
          <div>
            <h2 className="flex items-center gap-2 text-lg font-bold text-slate-900"><GitMerge className="h-5 w-5" /> Объединение дублей</h2>
            <p className="text-sm text-slate-600">Выберите основную карточку вручную. Дубль останется в архиве, связи перейдут к основной.</p>
          </div>
          <button type="button" onClick={handleClose} disabled={merging} aria-label="Закрыть объединение" className="rounded-lg p-2 hover:bg-slate-100"><X className="h-4 w-4" /></button>
        </div>
        <div className="space-y-3 overflow-y-auto p-3">
          {loading ? <p role="status" className="flex items-center gap-2 text-sm text-slate-600"><Loader2 className="h-4 w-4 animate-spin" /> Ищем возможные дубли…</p> : null}
          {!loading ? (
            <section className="rounded-xl border border-slate-200 p-3">
              <h3 className="text-sm font-semibold">Возможные совпадения: {groups.length}</h3>
              <p className="text-xs text-slate-500">Совпадения по телефону или email — подсказка, а не автоматическое объединение.</p>
              <div className="mt-2 flex max-h-36 flex-wrap gap-2 overflow-y-auto">
                {groups.length === 0 ? <span className="text-sm text-slate-500">Совпадений не найдено. Можно выбрать две карточки вручную.</span> : groups.map((group) => (
                  <button key={group.contacts.map(keyOf).join('|')} type="button" disabled={merging} onClick={() => chooseGroup(group)} className="rounded-lg border border-blue-200 bg-blue-50 px-2.5 py-1.5 text-left text-xs text-blue-800 hover:bg-blue-100">
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
                <span className="mt-1 flex items-center gap-2 rounded-lg border border-slate-300 px-2"><Search className="h-4 w-4 text-slate-400" /><input type="search" value={field.search} disabled={merging} onChange={(event) => { if (captureCurrent() && !merging) field.setSearch(event.target.value); }} placeholder="Поиск по имени, телефону или email" className="w-full py-2 outline-none" /></span>
                <select aria-label={field.label} value={field.value} disabled={merging} onChange={(event) => { const value = event.target.value; if (captureCurrent() && !merging && (!value || byKey.has(value))) field.setValue(value); }} className="mt-1 w-full rounded-lg border border-slate-300 bg-white p-2 text-sm">
                  <option value="">Выберите контакт</option>
                  {filteredOptions(field.search, field.value).map((item) => <option key={keyOf(item)} value={keyOf(item)}>{contactOption(item)}</option>)}
                </select>
              </label>
            ))}
          </div>
          {sameRecord ? <p className="rounded-lg bg-amber-50 p-2 text-sm text-amber-800">Выберите две разные карточки.</p> : null}
          {differentKinds ? <p className="rounded-lg bg-amber-50 p-2 text-sm text-amber-800">Человека нельзя объединить с организацией.</p> : null}
          {unsupportedMixedPair ? <p className="rounded-lg bg-amber-50 p-2 text-sm text-amber-800">Объединять можно карточки одного вида: человека с человеком или организацию с организацией.</p> : null}
          {reverseMixedPair ? <p className="rounded-lg bg-blue-50 p-2 text-sm text-blue-800">Быстрый контакт останется основным. Система создаст для него новую полную карточку и перенесёт на неё связи архивируемой карточки.</p> : null}
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
          {error ? <div role="alert" className="rounded-lg bg-rose-50 p-2 text-sm text-rose-700">{error}
            <button type="button" disabled={merging} onClick={() => { if (!captureCurrent() || merging) return; if (readError) setReadRetry(value => value + 1); else setPreviewRetry(value => value + 1); }} className="ml-2 underline">{readError ? 'Загрузить контакты повторно' : 'Повторить проверку'}</button>
          </div> : null}
        </div>
        <div className="flex justify-end gap-2 border-t border-slate-200 p-3">
          <button type="button" onClick={handleClose} disabled={merging} className="rounded-lg border border-slate-300 px-3 py-2 text-sm">Отмена</button>
          <button type="button" onClick={() => void merge()} disabled={!preview?.canMerge || previewing || merging} className="rounded-lg bg-blue-600 px-3 py-2 text-sm font-semibold text-white disabled:opacity-50">
            {merging ? 'Объединяем…' : 'Объединить и архивировать'}
          </button>
        </div>
      </div>
    </div>
  );
}
