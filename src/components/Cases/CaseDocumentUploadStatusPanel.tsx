import { useEffect, useRef, useState } from 'react';
import { apiService, getAuthToken, type CaseDocumentUploadState } from '../../services/api';
import { useDialog } from '../../hooks/useDialog';
import { useNestedCaseOperationOwner, type CaseOperationParent, type NestedCaseOperationOwner } from '../../hooks/useNestedCaseOperationOwner';

interface Props {
  caseId: number;
  actionOwner?: CaseOperationParent;
  revision: number;
  onPublished: (signal: AbortSignal) => Promise<void>;
}

const labels: Record<CaseDocumentUploadState['status'], string> = {
  waiting: 'Ожидание', retrying: 'Повторная попытка', paused: 'Приостановлено',
  failed: 'Не завершено', manualRetry: 'Нужен повтор', uploaded: 'Добавлен', unknown: 'Не подтверждено', retired: 'Попытка прекращена',
};

export function CaseDocumentUploadStatusPanel(props: Props) {
  const owner = useNestedCaseOperationOwner(JSON.stringify(['case-upload-status', props.caseId]), props.actionOwner);
  return owner.active ? <OwnedUploadStatusPanel key={owner.scope} {...props} owner={owner} /> : null;
}

function OwnedUploadStatusPanel({ caseId, revision, onPublished, owner }: Props & { owner: NestedCaseOperationOwner }) {
  const captureOwner = owner.capture;
  const { confirm } = useDialog();
  const action = useRef<AbortController | null>(null);
  const readSequence = useRef(0);
  const released = useRef(new Set<string>());
  const [retiring, setRetiring] = useState<string | null>(null);
  const [notice, setNotice] = useState<{ caseId: number; revision: number; text: string } | null>(null);
  const [actionError, setActionError] = useState<{ caseId: number; revision: number; kind: 'documents' | 'proof'; text: string } | null>(null);
  const [states, setStates] = useState<CaseDocumentUploadState[]>([]);
  const currentStates = useRef(states); currentStates.current = states;
  const [error, setError] = useState<string | null>(null);
  const [navigation, setNavigation] = useState({ caseId, revision, index: 0, cursors: [undefined] as (string | undefined)[] });
  const position = navigation.caseId === caseId && navigation.revision === revision
    ? navigation : { caseId, revision, index: 0, cursors: [undefined] as (string | undefined)[] };
  const page = position.index + 1;
  const after = position.cursors[position.index];
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const hasMore = nextCursor !== null;
  const [refresh, setRefresh] = useState(0);
  const published = useRef(new Set<string>());
  const needsDocumentRefresh = useRef(false);
  const publicationCaseId = useRef(caseId);
  const callback = useRef(onPublished);
  callback.current = onPublished;

  useEffect(() => {
    const readOwner = captureOwner(); if (!readOwner) return;
    const controller = new AbortController();
    const isCurrent = () => !controller.signal.aborted && readOwner.isCurrent();
    let timer: ReturnType<typeof setTimeout> | undefined;
    let known: CaseDocumentUploadState[] = [];
    let firstRead = true;
    setStates([]);
    setError(null);
    setNextCursor(null);
    setRetiring(null);
    if (publicationCaseId.current !== caseId) {
      released.current.clear();
      published.current.clear();
      needsDocumentRefresh.current = false;
      publicationCaseId.current = caseId;
    }
    const read = async () => {
      if (!isCurrent()) return;
      if (action.current) { timer = setTimeout(() => { void read(); }, 5000); return; }
      const sequence = readSequence.current;
      try {
        const active = known.filter((item) => item.status === 'waiting' || item.status === 'retrying' || item.status === 'paused');
        const discovery = firstRead ? await apiService.getPendingCaseDocumentUploads(caseId, after, controller.signal) : null;
        const received = discovery?.items ?? (active.length > 0
          ? await apiService.getCaseDocumentUploadStates(caseId, active.map((item) => item.uploadKey), controller.signal,
            { acknowledgePublishedAttempts: true }) : []);
        if (!isCurrent() || sequence !== readSequence.current) return;
        const current = received.map((item): CaseDocumentUploadState => item.status === 'uploaded'
          && (item.documentId == null || item.documentId <= 0 || item.version == null || item.version <= 0)
          ? { ...item, status: 'unknown', documentId: null, version: null,
            message: 'Не удалось подтвердить добавление документа. Обновите список документов.' } : item);
        // Subsequent reads use one indexed batch, never history or a request per file.
        // Disappearance is not proof of publication.
        const missing = active.filter((item) => !current.some((state) => state.uploadKey === item.uploadKey));
        const newlyPublished = current.filter((item) => item.status === 'uploaded' && item.documentId != null && item.version != null && item.version > 0 && !published.current.has(item.uploadKey));
        if (newlyPublished.length > 0 || discovery?.localPublicationConfirmed === true) needsDocumentRefresh.current = true;
        const unavailable = missing.map((item): CaseDocumentUploadState => ({ ...item, status: 'failed', documentId: null, version: null,
            message: 'Не удалось подтвердить состояние загрузки. Обновите список документов перед повторной отправкой.' }));
        const updates = new Map([...current, ...unavailable].map((item) => [item.uploadKey, item]));
        const previousKeys = new Set(known.map((item) => item.uploadKey));
        known = [...known.map((item) => updates.get(item.uploadKey) ?? item),
          ...current.filter((item) => !previousKeys.has(item.uploadKey))];
        setStates(known);
        setError(null);
        if (discovery) setNextCursor(discovery.nextCursor);
        firstRead = false;
        if (needsDocumentRefresh.current) {
          await callback.current(controller.signal);
          if (!isCurrent() || sequence !== readSequence.current) return;
          needsDocumentRefresh.current = false;
          setActionError((previous) => previous?.caseId === caseId && previous.revision === revision && previous.kind === 'documents' ? null : previous);
          known.filter((item) => item.status === 'uploaded' && item.documentId != null && item.version != null && item.version > 0)
            .forEach((item) => published.current.add(item.uploadKey));
        }
        const pending = known.filter((item) => item.status === 'waiting' || item.status === 'retrying' || item.status === 'paused');
        if (pending.length > 0) {
          timer = setTimeout(() => { void read(); }, pending.every((item) => item.status === 'paused') ? 30000 : 5000);
        }
      } catch (cause) {
        if (!isCurrent() || sequence !== readSequence.current) return;
        // Do not retain sensitive names when access can no longer be confirmed.
        setStates([]);
        setNextCursor(null);
        setError(cause instanceof Error && /HTTP (401|403|404)\b/.test(cause.message)
          ? 'Нет доступа к состоянию загрузок этого дела.'
          : 'Не удалось обновить состояние загрузок. Прикреплённые документы можно посмотреть в списке документов.');
        if (cause instanceof Error && /HTTP (401|403|404)\b/.test(cause.message)) {
          needsDocumentRefresh.current = false;
        } else if (needsDocumentRefresh.current) {
          // Retry the document list, using the saved receipt; never upload again.
          timer = setTimeout(() => { void read(); }, 5000);
        }
      }
    };
    void read();
    return () => {
      controller.abort(); if (timer) clearTimeout(timer);
      action.current?.abort(); action.current = null;
    };
  }, [caseId, revision, after, refresh, captureOwner]);

  const retire = async (item: CaseDocumentUploadState) => {
    if (action.current || item.status === 'uploaded' || released.current.has(item.uploadKey) || !currentStates.current.includes(item)) return;
    const operation = owner.begin('document:retire-upload'); if (!operation) return;
    const generated = item.generation != null;
    const controller = new AbortController();
    const requestToken = getAuthToken();
    const isCurrent = () => !controller.signal.aborted && operation.isCurrent();
    action.current = controller; readSequence.current++;
    setRetiring(item.uploadKey); setError(null); setActionError(null);
    try {
      const approved = await confirm({ signal: controller.signal, title: generated ? 'Прекратить формирование' : 'Подготовить новую загрузку',
        description: generated
          ? `Прекратить прежнюю попытку формирования «${item.fileName ?? 'документа'}»? После подтверждения она больше не добавит документ. Если документ уже добавлен, он сохранится. Новое формирование запустите отдельным действием.`
          : item.status === 'retired'
          ? `Прежняя попытка для «${item.fileName ?? 'документа'}» уже прекращена. Подтвердить подготовку новой загрузки? Новый файл потребуется выбрать и отправить отдельно.`
          : `Прекратить прежнюю попытку для «${item.fileName ?? 'документа'}»? После подтверждения она больше не добавит документ. Если документ уже добавлен, он сохранится. Новый файл потребуется выбрать и отправить отдельно.`,
        confirmLabel: 'Подтвердить', cancelLabel: 'Отмена' });
      if (!approved || !isCurrent()) return;
      const result = await apiService.retireCaseDocumentUpload(caseId, item.uploadKey, controller.signal, requestToken);
      if (!isCurrent()) return;
      released.current.add(item.uploadKey);
      setNotice({ caseId, revision, text: generated
        ? result.status === 'uploaded'
          ? 'Прежняя попытка уже добавила документ. Он сохранён. Новое формирование запустите отдельно.'
          : 'Прежняя попытка формирования прекращена. Новый документ сформируйте отдельным действием.'
        : result.status === 'uploaded'
        ? 'Прежняя попытка уже добавила документ. Он сохранён. Новый файл выберите и отправьте отдельно.'
        : 'Прежняя попытка прекращена. Новый файл выберите и отправьте отдельно.' });
      const displayedResult = generated && result.status === 'retired' && result.generation == null
        ? { ...result, generation: item.generation, message: 'Попытка формирования прекращена.' }
        : result;
      setStates((previous) => previous.map((state) => state.uploadKey === item.uploadKey ? displayedResult : state));
      if (result.status === 'uploaded' && !published.current.has(item.uploadKey)) {
        needsDocumentRefresh.current = true;
        await callback.current(controller.signal);
        if (!isCurrent()) return;
        needsDocumentRefresh.current = false;
        published.current.add(item.uploadKey);
      }
    } catch (cause) {
      if (!isCurrent()) return;
      if (cause instanceof Error && /HTTP (401|403|404)\b/.test(cause.message)) {
        setStates([]); setNextCursor(null); setNotice(null);
        setActionError({ caseId, revision, kind: 'proof', text: 'Нет доступа к этой попытке. Обновите состояние загрузок.' });
      } else setActionError({ caseId, revision, kind: released.current.has(item.uploadKey) ? 'documents' : 'proof', text: released.current.has(item.uploadKey)
        ? 'Прежний документ добавлен, но список не обновился. Обновите список документов.'
        : cause instanceof Error && cause.message.includes('данные вкладки недоступны')
        ? 'Результат подтверждён, но данные вкладки недоступны. Восстановите доступ и повторите проверку перед новой загрузкой.'
        : 'Не удалось подтвердить результат. Обновите загрузки перед следующим действием.' });
    } finally {
      operation.finish();
      if (action.current === controller) {
        action.current = null;
        if (!controller.signal.aborted && owner.capture()) { setRetiring(null); setRefresh((value) => value + 1); }
      }
    }
  };

  const controlsDisabled = retiring !== null || owner.busy;
  const visibleNotice = notice?.caseId === caseId && notice.revision === revision ? notice.text : null;
  const visibleActionError = actionError?.caseId === caseId && actionError.revision === revision ? actionError.text : null;
  if (states.length === 0 && !error && !visibleNotice && !visibleActionError && page === 1 && !hasMore) return null;
  return (
    <section aria-label="Состояние ваших загрузок" className="rounded-lg border border-blue-200 bg-blue-50/40 p-3 text-sm">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="font-semibold">Состояние ваших загрузок</h3>
        <button type="button" disabled={controlsDisabled} className="text-xs font-semibold text-blue-700 disabled:opacity-50" onClick={() => {
          if (!captureOwner()) return;
          setActionError(null);
          setNavigation({ caseId, revision, index: 0, cursors: [undefined] });
          setRefresh((value) => value + 1);
        }}>Обновить загрузки</button>
      </div>
      {error ? <p role="alert" className="mt-2 text-rose-700">{error}</p> : null}
      {visibleActionError ? <p role="alert" className="mt-2 text-rose-700">{visibleActionError}</p> : null}
      {visibleNotice ? <p role="status" className="mt-2 text-blue-800">{visibleNotice}</p> : null}
      <ul className="mt-2 space-y-2" aria-live="polite">
        {states.map((item) => (
          <li key={item.uploadKey} className="rounded-md border border-blue-100 bg-white p-2">
            <div className="flex flex-wrap justify-between gap-2"><span className="break-all font-medium">{item.fileName ?? (item.generation ? 'Формирование документа' : 'Загрузка документа')}</span><span>{labels[item.status]}</span></div>
            <p className="mt-1 text-xs text-slate-600">{item.message}</p>
            {item.status !== 'uploaded' && !released.current.has(item.uploadKey) ? <button type="button"
              disabled={controlsDisabled} onClick={() => { void retire(item); }}
              className="mt-1 text-xs font-semibold text-blue-700 disabled:opacity-50">
              {retiring === item.uploadKey ? 'Проверяем результат…' : item.generation ? 'Прекратить формирование' : 'Подготовить новую загрузку'}
            </button> : null}
          </li>
        ))}
      </ul>
      {page > 1 || hasMore ? <div className="mt-2 flex flex-wrap items-center gap-3 text-xs">
        <button type="button" disabled={page === 1 || controlsDisabled} onClick={() => { if (captureOwner()) setNavigation({ ...position, index: position.index - 1 }); }}>Предыдущая страница</button>
        <span className="whitespace-nowrap">Страница {page}</span>
        <button type="button" disabled={!hasMore || page >= 10000 || controlsDisabled} onClick={() => { if (captureOwner()) setNavigation({ ...position,
          index: position.index + 1, cursors: [...position.cursors.slice(0, position.index + 1), nextCursor!] }); }}>Следующая страница</button>
      </div> : null}
    </section>
  );
}
