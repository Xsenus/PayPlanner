import { useCallback, useEffect, useMemo, useState } from 'react';
import { CheckCircle2, Loader2, Send, ShieldCheck, UserPlus, X } from 'lucide-react';

import {
  apiService,
  type MessengerDocumentDeliveryConfiguration,
  type MessengerDocumentDeliveryDto,
  type MessengerRecipientBindingDto,
} from '../../services/api';
import { SystemCheckbox } from '../ui/SystemCheckbox';
import { SystemSelect } from '../ui/SystemSelect';
import { useDocumentDeliveryOwner } from '../../hooks/useDocumentDeliveryOwner';
import { captureAuthSessionIdentity } from '../../services/authService';

const CHANNEL_LABELS: Record<string, string> = {
  telegram: 'Telegram',
  max: 'MAX',
  'whatsapp-business': 'WhatsApp Business',
};

const STATUS_LABELS: Record<string, string> = {
  prepared: 'Подготовлено',
  queued: 'В очереди',
  processing: 'Отправляется',
  sent: 'Отправлено',
  delivered: 'Доставлено',
  read: 'Прочитано',
  failed: 'Ошибка',
  cancelled: 'Отменено',
};

export type MessengerDeliveryDocumentDescriptor = {
  id: number;
  originalFileName: string;
  documentType: string;
  version: number;
  sizeBytes: number;
  sourceType?: string | null;
};

type MessengerDeliverySourceType = 'invoice' | 'act' | 'contract';

function resolveMessengerDeliverySourceType(sourceType?: string | null): MessengerDeliverySourceType | undefined {
  return sourceType === 'invoice' || sourceType === 'act' || sourceType === 'contract'
    ? sourceType
    : undefined;
}

type Props = {
  ownerScope?: string;
  document: MessengerDeliveryDocumentDescriptor;
  clientId: number;
  clientOptions?: Array<{ id: number; name: string }>;
  preparePdf?: () => Promise<Blob>;
  onClose: () => void;
};

export function MessengerDocumentDeliveryModal(props: Props) {
  const key = JSON.stringify([props.ownerScope, props.document.id, props.document.sourceType, props.document.version,
    props.document.originalFileName, props.document.sizeBytes, props.clientId, captureAuthSessionIdentity()]);
  return <MessengerDeliveryForm key={key} {...props} />;
}

function MessengerDeliveryForm({ document, clientId, clientOptions, preparePdf, onClose, ownerScope }: Props) {
  const sourceType = resolveMessengerDeliverySourceType(document.sourceType);
  const [selectedClientId, setSelectedClientId] = useState(clientId);
  const scope = JSON.stringify([ownerScope, document.id, sourceType, document.version, selectedClientId]);
  const delivery = useDocumentDeliveryOwner(scope);
  const { capture: captureDelivery } = delivery;
  const [loadedScope, setLoadedScope] = useState<string | null>(null);
  const [storedConfiguration, setConfiguration] = useState<MessengerDocumentDeliveryConfiguration | null>(null);
  const [storedRecipients, setRecipients] = useState<MessengerRecipientBindingDto[]>([]);
  const [storedHistory, setHistory] = useState<MessengerDocumentDeliveryDto[]>([]);
  const configuration = loadedScope === scope ? storedConfiguration : null;
  const recipients = loadedScope === scope ? storedRecipients : [];
  const history = loadedScope === scope ? storedHistory : [];
  const [recipientId, setRecipientId] = useState('');
  const [message, setMessage] = useState(`Направляем документ «${document.originalFileName}».`);
  const [confirmed, setConfirmed] = useState(false);
  const [loading, setLoading] = useState(true);
  const submitting = delivery.busy;
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');
  const [showRecipientForm, setShowRecipientForm] = useState(false);
  const [newRecipient, setNewRecipient] = useState({
    displayName: '',
    channel: 'telegram',
    address: '',
    ownershipConfirmed: false,
  });

  const load = useCallback(async () => {
    const owner = captureDelivery();
    if (!owner) return;
    setLoadedScope(null);
    setLoading(true);
    setError('');
    try {
      const config = await apiService.getMessengerDocumentDeliveryConfiguration();
      if (!owner.isCurrent()) return;
      setConfiguration(config);
      if (!config.enabled) {
        setRecipients([]);
        setHistory([]);
        setLoadedScope(scope);
        return;
      }
      const [nextRecipients, nextHistory] = await Promise.all([
        apiService.getMessengerRecipients(selectedClientId),
        apiService.getMessengerDocumentDeliveryHistory(selectedClientId),
      ]);
      if (!owner.isCurrent()) return;
      setRecipients(nextRecipients);
      const expectedType = sourceType ?? 'case-document';
      setHistory(nextHistory.filter((item) => item.documentId === document.id && item.documentType === expectedType));
      setRecipientId((current) => current || String(nextRecipients[0]?.id ?? ''));
      setLoadedScope(scope);
    } catch (cause) {
      if (!owner.isCurrent()) return;
      setConfiguration(null); setRecipients([]); setHistory([]);
      setError(cause instanceof Error ? cause.message : 'Не удалось загрузить настройки доставки.');
    } finally {
      if (owner.isCurrent()) setLoading(false);
    }
  }, [captureDelivery, document.id, scope, selectedClientId, sourceType]);

  useEffect(() => {
    void load();
  }, [load]);

  const enabledProviders = useMemo(
    () => configuration?.providers.filter((provider) => provider.enabled) ?? [],
    [configuration],
  );
  const selectedRecipient = recipients.find((item) => String(item.id) === recipientId);
  const selectedProviderEnabled = enabledProviders.some((item) => item.channel === selectedRecipient?.channel);

  const createRecipient = async () => {
    if (loadedScope !== scope || !configuration?.enabled) return;
    if (!newRecipient.displayName.trim() || !newRecipient.address.trim() || !newRecipient.ownershipConfirmed) {
      setError('Укажите получателя, контакт и подтвердите принадлежность контакта клиенту.');
      return;
    }
    const operation = delivery.begin('recipient');
    if (!operation) return;
    setError('');
    try {
      const created = await apiService.createMessengerRecipient({
        clientId: selectedClientId,
        channel: newRecipient.channel,
        provider: newRecipient.channel,
        displayName: newRecipient.displayName.trim(),
        address: newRecipient.address.trim(),
        verificationMethod: 'admin-confirmed',
        confirmContactOwnership: true,
      });
      if (!operation.isCurrent()) return;
      setRecipients((items) => [created, ...items.filter((item) => item.id !== created.id)]);
      setRecipientId(String(created.id));
      setShowRecipientForm(false);
      setNewRecipient({ displayName: '', channel: 'telegram', address: '', ownershipConfirmed: false });
    } catch (cause) {
      if (!operation.isCurrent()) return;
      setError(cause instanceof Error ? cause.message : 'Не удалось сохранить подтверждённый контакт.');
    } finally {
      operation.finish();
    }
  };

  const prepareAndQueue = async () => {
    if (loadedScope !== scope || loading) return;
    if (!recipientId || !confirmed) {
      setError('Выберите получателя и подтвердите документ, версию и контакт.');
      return;
    }
    if (!selectedProviderEnabled) {
      setError('Выбранный официальный канал пока выключен администратором.');
      return;
    }
    const operation = delivery.begin('send');
    if (!operation) return;
    setError('');
    setSuccess('');
    try {
      const deliveryPayload = {
        recipientBindingId: Number(recipientId),
        message: message.trim() || undefined,
        confirmRecipientAndDocument: true,
      };
      const pdf = sourceType ? await requirePdf() : null;
      if (!operation.isCurrent()) return;
      const prepared = sourceType
        ? await apiService.prepareFinancialDocumentMessengerDelivery(
            sourceType,
            document.id,
            pdf!,
            deliveryPayload,
          )
        : await apiService.prepareCaseDocumentMessengerDelivery(document.id, deliveryPayload);
      if (!operation.isCurrent()) return;
      await apiService.queueMessengerDocumentDelivery(prepared.id);
      if (!operation.isCurrent()) return;
      setSuccess('Документ поставлен в безопасную очередь отправки.');
      setConfirmed(false);
      await load();
    } catch (cause) {
      if (!operation.isCurrent()) return;
      setError(cause instanceof Error ? cause.message : 'Не удалось подготовить отправку.');
    } finally {
      operation.finish();
    }
  };

  const requirePdf = async () => {
    if (!preparePdf) {
      throw new Error('Не настроено формирование PDF выбранного документа.');
    }
    const pdf = await preparePdf();
    if (pdf.size <= 0 || (pdf.type && pdf.type !== 'application/pdf')) {
      throw new Error('CRM не смогла сформировать корректный PDF для отправки.');
    }
    return pdf.type === 'application/pdf' ? pdf : pdf.slice(0, pdf.size, 'application/pdf');
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-950/60 p-3">
      <section className="max-h-[94vh] w-full max-w-3xl overflow-y-auto rounded-xl bg-white shadow-2xl" aria-label="Отправка документа в мессенджер">
        <header className="sticky top-0 z-10 flex items-start justify-between gap-3 border-b border-slate-200 bg-white px-4 py-3">
          <div>
            <div className="text-xs font-semibold uppercase tracking-[0.14em] text-blue-600">Защищённая доставка</div>
            <h2 className="mt-1 text-lg font-semibold text-slate-900">Отправить документ</h2>
          </div>
          <button type="button" onClick={() => { delivery.invalidate(); onClose(); }} aria-label="Закрыть мастер отправки" className="rounded-lg border border-slate-200 p-2 text-slate-500 hover:bg-slate-50">
            <X className="h-4 w-4" />
          </button>
        </header>

        <div className="space-y-3 p-4">
          {clientOptions && clientOptions.length > 1 ? (
            <label className="block text-sm font-semibold text-slate-900">
              Получатель по договору
              <SystemSelect
                value={String(selectedClientId)}
                onChange={(event) => {
                  setSelectedClientId(Number(event.target.value));
                  setRecipientId('');
                  setConfirmed(false);
                  setSuccess(''); setShowRecipientForm(false);
                  setNewRecipient({ displayName: '', channel: 'telegram', address: '', ownershipConfirmed: false });
                }}
                className="mt-2 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm font-normal"
              >
                {clientOptions.map((client) => <option key={client.id} value={client.id}>{client.name}</option>)}
              </SystemSelect>
            </label>
          ) : null}
          <section className="rounded-xl border border-slate-200 bg-slate-50 p-3">
            <div className="font-semibold text-slate-900">{document.originalFileName}</div>
            <div className="mt-1 text-sm text-slate-500">
              {document.documentType} · версия {document.version}
              {document.sizeBytes > 0 ? ` · ${(document.sizeBytes / 1024).toFixed(1)} КБ` : ' · PDF будет сформирован перед отправкой'}
            </div>
            <div className="mt-2 flex items-center gap-2 text-xs text-slate-500">
              <ShieldCheck className="h-4 w-4 text-emerald-600" /> Перед отправкой CRM создаст неизменяемый снимок и SHA-256.
            </div>
          </section>

          {loading ? <div className="flex items-center gap-2 py-6 text-sm text-slate-500"><Loader2 className="h-4 w-4 animate-spin" /> Загрузка…</div> : null}
          {!loading && configuration && !configuration.enabled ? (
            <div className="rounded-xl border border-amber-200 bg-amber-50 p-3 text-sm text-amber-800">
              Доставка документов выключена. Мастер подготовлен, но отправка и сохранение контактов станут доступны только после настройки официального API и HTTPS-адреса CRM.
            </div>
          ) : null}

          {!loading && configuration?.enabled ? (
            <>
              <section className="rounded-xl border border-slate-200 p-3">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <h3 className="text-sm font-semibold text-slate-900">Получатель</h3>
                  <button type="button" onClick={() => setShowRecipientForm((value) => !value)} className="inline-flex items-center gap-1.5 text-sm font-medium text-blue-700">
                    <UserPlus className="h-4 w-4" /> Добавить подтверждённый контакт
                  </button>
                </div>
                <SystemSelect value={recipientId} onChange={(event) => setRecipientId(event.target.value)} className="mt-3 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm">
                  <option value="">Выберите получателя</option>
                  {recipients.map((recipient) => (
                    <option key={recipient.id} value={recipient.id}>
                      {recipient.displayName} · {CHANNEL_LABELS[recipient.channel] ?? recipient.channel} · {recipient.maskedAddress}
                    </option>
                  ))}
                </SystemSelect>
                {selectedRecipient && !selectedProviderEnabled ? (
                  <div className="mt-2 text-xs text-amber-700">Канал {CHANNEL_LABELS[selectedRecipient.channel] ?? selectedRecipient.channel} настроен как выключенный.</div>
                ) : null}

                {showRecipientForm ? (
                  <div className="mt-3 grid gap-2 rounded-lg border border-blue-100 bg-blue-50 p-3 sm:grid-cols-2">
                    <input aria-label="Имя получателя" value={newRecipient.displayName} onChange={(event) => setNewRecipient((old) => ({ ...old, displayName: event.target.value }))} placeholder="ФИО клиента или представителя" className="rounded-lg border border-slate-200 px-3 py-2 text-sm" />
                    <SystemSelect value={newRecipient.channel} onChange={(event) => setNewRecipient((old) => ({ ...old, channel: event.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm">
                      {configuration.providers.map((provider) => <option key={provider.channel} value={provider.channel}>{CHANNEL_LABELS[provider.channel] ?? provider.channel}{provider.enabled ? '' : ' — выключен'}</option>)}
                    </SystemSelect>
                    <input aria-label="Контакт получателя" value={newRecipient.address} onChange={(event) => setNewRecipient((old) => ({ ...old, address: event.target.value }))} placeholder="ID/номер официального канала" className="rounded-lg border border-slate-200 px-3 py-2 text-sm sm:col-span-2" />
                    <label className="flex items-start gap-2 text-sm text-slate-700 sm:col-span-2">
                      <SystemCheckbox checked={newRecipient.ownershipConfirmed} onChange={(event) => setNewRecipient((old) => ({ ...old, ownershipConfirmed: event.target.checked }))} className="mt-0.5 h-4 w-4" />
                      Контакт проверен и принадлежит выбранному клиенту или явно назначенному представителю.
                    </label>
                    <button type="button" onClick={() => void createRecipient()} disabled={submitting} className="rounded-lg bg-blue-600 px-3 py-2 text-sm font-semibold text-white disabled:opacity-50 sm:col-span-2">Сохранить подтверждённый контакт</button>
                  </div>
                ) : null}
              </section>

              <section className="rounded-xl border border-slate-200 p-3">
                <label className="text-sm font-semibold text-slate-900">Текст сообщения
                  <textarea value={message} onChange={(event) => setMessage(event.target.value)} maxLength={1000} rows={3} className="mt-2 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm font-normal" />
                </label>
                <label className="mt-3 flex items-start gap-2 rounded-lg bg-amber-50 p-3 text-sm text-amber-900">
                  <SystemCheckbox checked={confirmed} onChange={(event) => setConfirmed(event.target.checked)} className="mt-0.5 h-4 w-4" />
                  Подтверждаю выбранный документ, его версию и получателя. Статусы доставки не являются согласием или электронной подписью.
                </label>
              </section>

              {history.length > 0 ? (
                <section className="rounded-xl border border-slate-200 p-3">
                  <h3 className="text-sm font-semibold text-slate-900">История этого документа</h3>
                  <div className="mt-2 space-y-2">
                    {history.slice(0, 5).map((item) => (
                      <div key={item.id} className="flex flex-wrap items-center justify-between gap-2 rounded-lg bg-slate-50 px-3 py-2 text-sm">
                        <span>{item.recipient} · {CHANNEL_LABELS[item.channel] ?? item.channel}</span>
                        <span className="font-medium text-slate-700">{STATUS_LABELS[item.status] ?? item.status}</span>
                      </div>
                    ))}
                  </div>
                </section>
              ) : null}
            </>
          ) : null}

          {error ? <div role="alert" className="rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">{error}</div> : null}
          {success ? <div className="flex items-center gap-2 rounded-lg border border-emerald-200 bg-emerald-50 p-3 text-sm text-emerald-700"><CheckCircle2 className="h-4 w-4" /> {success}</div> : null}
        </div>

        <footer className="sticky bottom-0 flex justify-end gap-2 border-t border-slate-200 bg-white px-4 py-3">
          <button type="button" onClick={onClose} className="rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-700">Закрыть</button>
          <button type="button" onClick={() => void prepareAndQueue()} disabled={submitting || !configuration?.enabled || !recipientId || !confirmed || !selectedProviderEnabled} className="inline-flex items-center gap-2 rounded-lg bg-slate-900 px-4 py-2 text-sm font-semibold text-white disabled:opacity-40">
            {submitting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />} Подготовить и отправить
          </button>
        </footer>
      </section>
    </div>
  );
}
