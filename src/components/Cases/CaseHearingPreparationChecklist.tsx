import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import { useDictionarySnapshot } from '../../hooks/useDictionarySnapshot';
import { useNestedCaseOperationOwner, type CaseOperationParent, type NestedCaseOperationOwner } from '../../hooks/useNestedCaseOperationOwner';
import { awaitOwnedUiResult, StaleUiOperationError } from '../../utils/StaleUiOperationError';
import { apiService } from '../../services/api';
import type { ClientCase, ClientCaseDocument, ClientCaseHearingPreparation } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatDateTimeInTimeZone } from '../../utils/timeZoneDate';

type CheckKind = 'contract' | 'authority' | 'materials';

interface Props {
  actionOwner?: CaseOperationParent;
  caseItem: ClientCase;
  timeZoneId: string;
  canEdit: boolean;
  datesSaved?: boolean;
  initialDocuments?: ClientCaseDocument[];
  initialDocumentsLoading?: boolean;
}

const checks: Array<{ kind: CheckKind; title: string; hint: string; at: keyof ClientCaseHearingPreparation; by: keyof ClientCaseHearingPreparation }> = [
  { kind: 'contract', title: 'Договор и реквизиты', hint: 'Проверьте действующий договор и реквизиты сторон.', at: 'contractCheckedAt', by: 'contractCheckedByName' },
  { kind: 'authority', title: 'Полномочия представителя', hint: 'Убедитесь, что полномочия действуют на дату заседания.', at: 'authorityCheckedAt', by: 'authorityCheckedByName' },
  { kind: 'materials', title: 'Материалы к заседанию', hint: 'Привяжите подготовленные файлы из документов дела.', at: 'materialsCheckedAt', by: 'materialsCheckedByName' },
];

const EMPTY_DOCUMENTS: ClientCaseDocument[] = [];
export function CaseHearingPreparationChecklist(props: Props) {
  const owner = useNestedCaseOperationOwner(JSON.stringify(['preparation', props.caseItem.id, props.caseItem.nextHearingAt, props.caseItem.responsibleUserId, props.caseItem.status, props.canEdit, props.datesSaved ?? true]), props.actionOwner);
  return owner.active ? <OwnedHearingPreparation key={owner.scope} {...props} owner={owner} /> : null;
}
function OwnedHearingPreparation({ caseItem, timeZoneId, canEdit, datesSaved = true, initialDocuments, initialDocumentsLoading = false, owner }: Props & { owner: NestedCaseOperationOwner }) {
  const [selectedIds, setSelectedIds] = useState<number[]>([]);
  const selectionEdited = useRef(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const hearingAt = Date.parse(caseItem.nextHearingAt ?? '');
  const available = caseItem.id > 0 && Number.isFinite(hearingAt) && hearingAt > Date.now() && !['Closed', 'Lost'].includes(caseItem.status);
  const { capture } = owner;
  const validatePreparation = useCallback((value: ClientCaseHearingPreparation) => {
    if (!value || Date.parse(value.hearingAt) !== hearingAt || !Array.isArray(value.documentIds)
      || !value.documentIds.every(id => Number.isSafeInteger(id) && id > 0) || new Set(value.documentIds).size !== value.documentIds.length) throw new Error('Не удалось подтвердить подготовку к текущему заседанию. Обновите проверки.');
    return value;
  }, [hearingAt]);
  const loadPreparation = useCallback(async (signal: AbortSignal) => {
    const read = capture(); if (!read) throw new StaleUiOperationError();
    return validatePreparation(await awaitOwnedUiResult(() => apiService.getCaseHearingPreparation(caseItem.id, { signal }), () => read.isCurrent() && !signal.aborted));
  }, [capture, caseItem.id, validatePreparation]);
  const preparationRead = useDictionarySnapshot<ClientCaseHearingPreparation | null>(loadPreparation, null, { enabled: available && datesSaved, ownerScope: owner.scope }, false);
  const preparation = preparationRead.value;
  const documentSequence = useRef(0);
  const manualDocumentRead = useRef<AbortController | null>(null);
  useEffect(() => () => manualDocumentRead.current?.abort(), []);
  const validateDocuments = useCallback((rows: ClientCaseDocument[]) => {
    if (!Array.isArray(rows) || !rows.every(row => row && row.clientCaseId === caseItem.id && Number.isSafeInteger(row.id) && row.id > 0) || new Set(rows.map(row => row.id)).size !== rows.length) throw new Error('Не удалось подтвердить список документов текущего дела.');
    return rows;
  }, [caseItem.id]);
  const loadDocuments = useCallback(async (signal: AbortSignal) => {
    const read = capture(); if (!read) throw new StaleUiOperationError();
    const sequence = ++documentSequence.current;
    const rows = await awaitOwnedUiResult(() => initialDocuments ? Promise.resolve(initialDocuments) : apiService.getCaseDocuments(caseItem.id, signal), () => read.isCurrent() && !signal.aborted && sequence === documentSequence.current);
    return validateDocuments(rows);
  }, [capture, caseItem.id, initialDocuments, validateDocuments]);
  const documentRead = useDictionarySnapshot(loadDocuments, EMPTY_DOCUMENTS, { enabled: available && datesSaved && !initialDocumentsLoading, ownerScope: owner.scope }, false);
  const documents = documentRead.value;
  const documentsLoading = initialDocumentsLoading || documentRead.loading;
  const currentDocuments = useRef(documentRead); currentDocuments.current = documentRead;
  useEffect(() => { if (preparation && !selectionEdited.current) setSelectedIds(preparation.documentIds); }, [preparation]);
  const loading = preparationRead.loading;
  const saving = owner.kind?.startsWith('preparation:save:') ? owner.kind.split(':')[2] : null;
  const failure = actionError ?? preparationRead.error ?? documentRead.error;
  const error = failure ? extractDisplayErrorMessage(failure, 'Не удалось загрузить подготовку к заседанию.') : null;
  const selectedIdSet = useMemo(() => new Set(selectedIds), [selectedIds]);
  const savedIdSet = useMemo(() => new Set(preparation?.documentIds ?? []), [preparation?.documentIds]);
  const availableIdSet = useMemo(() => new Set(documents.map(row => row.id)), [documents]);
  const unavailableSelectedIds = selectedIds.filter(id => !availableIdSet.has(id));
  if (!available) return null;
  const save = async (kind: CheckKind, checked: boolean) => {
    if (!canEdit || !datesSaved || !preparation || preparationRead.loading || preparationRead.error) return;
    const operation = owner.begin(`preparation:save:${kind}`); if (!operation) return;
    setActionError(null);
    try {
      const availableIds = new Set(currentDocuments.current.value.map(row => row.id));
      if (kind === 'materials' && (initialDocumentsLoading || currentDocuments.current.loading || currentDocuments.current.error || selectedIds.some(id => !availableIds.has(id)))) throw new Error('Выберите материалы из доступных документов текущего дела. Сначала обновите список.');
      const updated = validatePreparation(await awaitOwnedUiResult(() => apiService.updateCaseHearingPreparation(caseItem.id, { hearingAt: preparation.hearingAt, checkKind: kind, checked,
        ...(kind === 'materials' ? { documentIds: selectedIds } : {}) }), operation.isCurrent));
      if (kind === 'materials') {
        const confirmedIds = new Set(updated.documentIds);
        if (updated.documentIds.length !== selectedIds.length || selectedIds.some(id => !confirmedIds.has(id))) throw new Error('Сохранённый набор материалов не совпадает с выбранным. Обновите проверки.');
        selectionEdited.current = false; setSelectedIds(updated.documentIds);
      }
      preparationRead.updateValue(() => updated);
    } catch (cause) {
      if (operation.isCurrent() && !(cause instanceof StaleUiOperationError)) setActionError(extractDisplayErrorMessage(cause, 'Не удалось сохранить проверку.'));
    } finally { operation.finish(); }
  };
  const refreshDocuments = async () => {
    if (initialDocumentsLoading) return;
    const operation = owner.begin('preparation:documents'); if (!operation) return;
    const sequence = ++documentSequence.current;
    const controller = new AbortController(); manualDocumentRead.current = controller;
    documentRead.updateValue(value => value);
    setActionError(null);
    try {
      const rows = await awaitOwnedUiResult(() => apiService.getCaseDocuments(caseItem.id, controller.signal), () => operation.isCurrent() && !controller.signal.aborted && sequence === documentSequence.current);
      documentRead.updateValue(() => validateDocuments(rows));
    } catch (cause) { if (operation.isCurrent() && !(cause instanceof StaleUiOperationError)) setActionError(extractDisplayErrorMessage(cause, 'Не удалось обновить список документов.')); }
    finally { if (manualDocumentRead.current === controller) manualDocumentRead.current = null; operation.finish(); }
  };
  const retryPreparation = async () => {
    const operation = owner.begin('preparation:retry'); if (!operation) return;
    setActionError(null);
    try { await awaitOwnedUiResult(preparationRead.refresh, operation.isCurrent); }
    catch (cause) { if (operation.isCurrent() && !(cause instanceof StaleUiOperationError)) setActionError(extractDisplayErrorMessage(cause, 'Не удалось загрузить проверки.')); }
    finally { operation.finish(); }
  };

  const selectedChanged = preparation && (selectedIds.length !== preparation.documentIds.length
    || selectedIds.some((id) => !savedIdSet.has(id)));

  return (
    <section className="rounded-xl border border-sky-200 bg-sky-50/50 p-3 text-sm text-slate-800">
      <h3 className="font-semibold">Проверка перед заседанием</h3>
      <p className="mt-1 text-xs text-slate-600">Отметки относятся только к заседанию {formatDateTimeInTimeZone(caseItem.nextHearingAt!, timeZoneId)}. После переноса даты старые отметки не считаются готовностью к новому заседанию.</p>
      {!datesSaved ? <p className="mt-2 text-xs text-amber-800">Сначала сохраните новую дату заседания и ответственного по делу.</p> : null}
      {loading ? <p className="mt-2 text-xs text-slate-500">Загружаем проверки…</p> : null}
      {preparation ? (
        <div className="mt-2 space-y-2">
          {checks.map(({ kind, title, hint, at, by }) => {
            const reviewedAt = preparation[at] as string | null;
            const reviewer = preparation[by] as string;
            return (
              <div key={kind} className="rounded-lg border border-sky-100 bg-white p-2">
                <div className="flex items-start justify-between gap-2">
                  <div>
                    <div className="text-xs font-semibold">{title}</div>
                    <p className="text-xs text-slate-500">{hint}</p>
                    <p className={`mt-1 text-xs ${reviewedAt ? 'text-emerald-700' : 'text-amber-800'}`}>
                      {reviewedAt ? `Проверено ${formatDateTimeInTimeZone(reviewedAt, timeZoneId)}${reviewer ? ` · ${reviewer}` : ''}` : 'Не проверено'}
                    </p>
                  </div>
                  {canEdit ? <button type="button" onClick={() => void save(kind, !reviewedAt)} disabled={owner.busy || loading || !!preparationRead.error || !datesSaved || (kind === 'materials' && !reviewedAt && selectedIds.length === 0)} className="shrink-0 rounded-lg border border-sky-200 px-2.5 py-1 text-xs font-semibold text-sky-800 hover:bg-sky-50 disabled:cursor-not-allowed disabled:opacity-50">{saving === kind ? 'Сохраняем…' : reviewedAt ? 'Снять отметку' : 'Подтвердить'}</button> : null}
                </div>
                {kind === 'materials' ? (
                  <div className="mt-2 border-t border-sky-100 pt-2">
                    <div className="flex items-center justify-between gap-2"><span className="text-xs font-medium">Документы этого заседания</span><button type="button" onClick={() => void refreshDocuments()} disabled={owner.busy || documentsLoading} className="text-xs text-blue-700 hover:underline">Обновить список</button></div>
                    {!documentsLoading && documents.length === 0 ? <p className="mt-1 text-xs text-slate-500">В деле пока нет загруженных документов. Добавьте файл в разделе «Документы».</p> : null}
                    <div className="mt-1 max-h-40 space-y-1 overflow-y-auto">
                      {documents.map((document) => (
                        <label key={document.id} className="flex items-center gap-2 text-xs">
                          <input type="checkbox" checked={selectedIdSet.has(document.id)} disabled={!canEdit || owner.busy || documentsLoading || !!documentRead.error || !datesSaved} onChange={() => { if (!canEdit || !datesSaved || !owner.capture() || owner.busy || currentDocuments.current.loading || currentDocuments.current.error || !currentDocuments.current.value.some(row => row.id === document.id)) return; selectionEdited.current = true; setSelectedIds(current => current.includes(document.id) ? current.filter(id => id !== document.id) : [...current, document.id]); }} />
                          <span className="truncate" title={document.originalFileName}>{document.originalFileName}</span>
                        </label>
                      ))}
                    </div>
                    {!documentsLoading && !documentRead.error && unavailableSelectedIds.length > 0 ? <div className="mt-2 rounded-lg border border-amber-200 bg-amber-50 p-2 text-xs text-amber-800">
                      <p>Недоступны ранее выбранные документы: {unavailableSelectedIds.length}. Связи сохранятся, пока вы не исключите их из выбора и не сохраните изменения.</p>
                      {canEdit ? <button type="button" disabled={owner.busy || !datesSaved} className="mt-1 text-left font-medium underline disabled:opacity-50" onClick={() => {
                        const current = currentDocuments.current;
                        if (!canEdit || !datesSaved || !owner.capture() || owner.busy || initialDocumentsLoading || current.loading || current.error) return;
                        const availableIds = new Set(current.value.map(row => row.id));
                        selectionEdited.current = true; setSelectedIds(previous => previous.filter(id => availableIds.has(id)));
                      }}>Исключить недоступные материалы из выбора</button> : null}
                    </div> : null}
                    {selectedChanged && preparation.materialsCheckedAt ? <p className="mt-2 text-xs text-amber-800">Набор документов изменён. После сохранения отметку проверки материалов нужно будет поставить заново.</p> : null}
                    {canEdit && selectedChanged ? <button type="button" onClick={() => void save('materials', false)} disabled={owner.busy || loading || !!preparationRead.error || !datesSaved} className="mt-2 rounded-lg border border-sky-200 px-2.5 py-1 text-xs font-semibold text-sky-800 disabled:opacity-50">Сохранить связи с документами</button> : null}
                  </div>
                ) : null}
              </div>
            );
          })}
        </div>
      ) : null}
      {error ? <p role="alert" className="mt-2 text-xs text-rose-700">{error}</p> : null}
      {preparationRead.error ? <button type="button" disabled={owner.busy || loading} onClick={() => void retryPreparation()} className="mt-2 text-xs font-medium text-blue-700 hover:underline">Повторить загрузку проверок</button> : null}
    </section>
  );
}
