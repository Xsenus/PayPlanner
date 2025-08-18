import React, { useEffect, useState } from 'react';
import { X, Trash2 } from 'lucide-react';
import { useClients } from '../../hooks/useClients';
import { useDictionaries } from '../../hooks/useDictionaries';
import { useTranslation } from '../../hooks/useTranslation';
import { apiService } from '../../services/api';
import type { Payment, PaymentStatus, ClientCase } from '../../types';
import { toDateInputValue, fromInputToApiDate, todayYMD } from '../../utils/dateUtils';

type PaymentKind = 'Income' | 'Expense';

interface PaymentModalProps {
  defaultClientId?: number;
  defaultClientCaseId?: number;
  type?: PaymentKind;
  isOpen: boolean;
  onClose: () => void;
  onSubmit: (
    payment:
      | Omit<Payment, 'id' | 'createdAt'>
      | ({ id: number } & Omit<Payment, 'id' | 'createdAt'>),
  ) => Promise<void>;
  payment?: Payment | null;
  onDelete?: (id: number) => Promise<void>;
}

export function PaymentModal({
  isOpen,
  onClose,
  onSubmit,
  payment,
  onDelete,
  defaultClientId,
  defaultClientCaseId,
  type,
}: PaymentModalProps) {
  const { clients } = useClients();
  const { dealTypes, incomeTypes, paymentSources } = useDictionaries();
  const { t } = useTranslation();

  const [loading, setLoading] = useState(false);
  const [cases, setCases] = useState<ClientCase[]>([]);

  const [formData, setFormData] = useState({
    date: todayYMD(),
    amount: '',
    status: 'Pending' as PaymentStatus,
    description: '',
    isPaid: false,
    paidDate: '',
    notes: '',
    clientId: '',
    clientCaseId: '',
    dealTypeId: '',
    incomeTypeId: '',
    paymentSourceId: '',
    paymentStatusId: '',
    type: (type ?? 'Income') as PaymentKind,
  });

  useEffect(() => {
    if (!isOpen) return;

    if (payment) {
      setFormData({
        date: toDateInputValue(payment.date) || todayYMD(),
        amount: payment.amount.toString(),
        status: payment.status,
        description: payment.description,
        isPaid: payment.isPaid,
        paidDate: toDateInputValue(payment.paidDate) || '',
        notes: payment.notes || '',
        clientId: payment.clientId?.toString() || '',
        clientCaseId: payment.clientCaseId?.toString() || '',
        dealTypeId: payment.dealTypeId?.toString() || '',
        incomeTypeId: payment.incomeTypeId?.toString() || '',
        paymentSourceId: payment.paymentSourceId?.toString() || '',
        paymentStatusId: payment.paymentStatusId?.toString() || '',
        type: (payment.type as PaymentKind) ?? type ?? 'Income',
      });
    } else {
      setFormData({
        date: todayYMD(),
        amount: '',
        status: 'Pending',
        description: '',
        isPaid: false,
        paidDate: '',
        notes: '',
        clientId: defaultClientId?.toString() || '',
        clientCaseId: defaultClientCaseId?.toString() || '',
        dealTypeId: '',
        incomeTypeId: '',
        paymentSourceId: '',
        paymentStatusId: '',
        type: (type ?? 'Income') as PaymentKind,
      });
    }

    setCases([]);
  }, [isOpen, payment, defaultClientId, defaultClientCaseId, type]);

  useEffect(() => {
    const load = async () => {
      const cid = Number(formData.clientId);
      if (!cid) {
        setCases([]);
        setFormData((s) => ({ ...s, clientCaseId: '' }));
        return;
      }
      const list = await apiService.getCases(cid);
      setCases(list || []);

      if (formData.clientCaseId) {
        const ok = (list || []).some((c) => String(c.id) === formData.clientCaseId);
        if (!ok) setFormData((s) => ({ ...s, clientCaseId: '' }));
      }
    };
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [formData.clientId]);

  const markPaid = () =>
    setFormData((s) => ({
      ...s,
      isPaid: true,
      status: 'Completed',
      paidDate: s.paidDate || todayYMD(),
    }));

  const markPending = () =>
    setFormData((s) => ({
      ...s,
      isPaid: false,
      status: 'Pending',
      paidDate: '',
    }));

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (loading) return;

    const amountNum = parseFloat(formData.amount);
    if (!Number.isFinite(amountNum) || amountNum < 0) {
      console.error('Invalid amount value:', formData.amount);
      return;
    }

    setLoading(true);

    const normalizedDate = fromInputToApiDate(formData.date)!;
    const normalizedPaidDate =
      formData.isPaid && formData.paidDate ? fromInputToApiDate(formData.paidDate) : undefined;

    try {
      const paymentData = {
        date: normalizedDate,
        amount: amountNum,
        type: formData.type,
        status: formData.status,
        description: formData.description,
        isPaid: formData.isPaid,
        paidDate: normalizedPaidDate,
        notes: formData.notes,
        clientId: formData.clientId ? parseInt(formData.clientId, 10) : undefined,
        clientCaseId: formData.clientCaseId ? parseInt(formData.clientCaseId, 10) : undefined,
        dealTypeId: formData.dealTypeId ? parseInt(formData.dealTypeId, 10) : undefined,
        incomeTypeId: formData.incomeTypeId ? parseInt(formData.incomeTypeId, 10) : undefined,
        paymentSourceId: formData.paymentSourceId
          ? parseInt(formData.paymentSourceId, 10)
          : undefined,
        paymentStatusId: formData.paymentStatusId
          ? parseInt(formData.paymentStatusId, 10)
          : undefined,
      } as Omit<Payment, 'id' | 'createdAt'>;

      if (payment) {
        await onSubmit({ id: payment.id, ...paymentData });
      } else {
        await onSubmit(paymentData);
      }

      onClose();
    } finally {
      setLoading(false);
    }
  };

  const handleChange = (
    e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>,
  ) => {
    const { name, value, type: inputType } = e.target;

    if (name === 'isPaid') {
      const checked = (e.target as HTMLInputElement).checked;
      if (checked) markPaid();
      else markPending();
      return;
    }

    if (name === 'status') {
      const newStatus = value as PaymentStatus;
      if (newStatus === 'Completed') markPaid();
      else if (newStatus === 'Pending') markPending();
      else if (newStatus === 'Overdue')
        setFormData((s) => ({ ...s, status: 'Overdue', isPaid: false, paidDate: '' }));
      return;
    }

    if (name === 'clientId') {
      setFormData((prev) => ({ ...prev, clientId: value, clientCaseId: '' }));
      return;
    }

    if (name === 'type') {
      setFormData((prev) => ({
        ...prev,
        type: value as PaymentKind,
        incomeTypeId: value === 'Income' ? prev.incomeTypeId : '',
      }));
      return;
    }

    setFormData((prev) => ({
      ...prev,
      [name]: inputType === 'checkbox' ? (e.target as HTMLInputElement).checked : value,
    }));
  };

  const canDelete = !!payment && !!onDelete;
  if (!isOpen) return null;

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center p-4 z-50">
      <div className="bg-white rounded-xl max-w-md w-full max-h-[90vh] overflow-y-auto">
        <div className="p-6">
          <div className="flex items-center justify-between mb-6">
            <h2 className="text-xl font-semibold text-gray-900">
              {payment ? t('editPayment') : t('addPayment')}
            </h2>
            <button
              onClick={onClose}
              type="button"
              className="p-2 hover:bg-gray-100 rounded-lg transition-colors"
              aria-label="Close">
              <X size={20} />
            </button>
          </div>

          <form onSubmit={handleSubmit} className="space-y-4">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">
                {t('type') ?? 'Тип'}
              </label>
              <select
                name="type"
                value={formData.type}
                onChange={handleChange}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                <option value="Income">{t('income') ?? 'Доход'}</option>
                <option value="Expense">{t('expense') ?? 'Расход'}</option>
              </select>
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('date')}</label>
              <input
                type="date"
                name="date"
                value={formData.date}
                onChange={handleChange}
                required
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('amount')}</label>
              <input
                type="number"
                name="amount"
                value={formData.amount}
                onChange={handleChange}
                required
                min="0"
                step="0.01"
                inputMode="decimal"
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('client')}</label>
              <select
                name="clientId"
                value={formData.clientId}
                onChange={handleChange}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                <option value="">{t('selectClient')}</option>
                {clients.map((client) => (
                  <option key={client.id} value={client.id}>
                    {client.name} {client.company ? `— ${client.company}` : ''}
                  </option>
                ))}
              </select>
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">
                {t('case') ?? 'Дело'}
              </label>
              <select
                name="clientCaseId"
                value={formData.clientCaseId}
                onChange={handleChange}
                disabled={!formData.clientId}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg disabled:bg-gray-100 focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                <option value="">{t('selectCase') ?? 'Без дела'}</option>
                {cases.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.title}
                  </option>
                ))}
              </select>
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">
                {t('dealType')}
              </label>
              <select
                name="dealTypeId"
                value={formData.dealTypeId}
                onChange={handleChange}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                <option value="">{t('selectDealType')}</option>
                {dealTypes
                  .filter((dealType) => dealType.isActive)
                  .map((dealType) => (
                    <option key={dealType.id} value={dealType.id}>
                      {dealType.name}
                    </option>
                  ))}
              </select>
            </div>

            {formData.type === 'Income' && (
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-2">
                  {t('incomeType')}
                </label>
                <select
                  name="incomeTypeId"
                  value={formData.incomeTypeId}
                  onChange={handleChange}
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                  <option value="">{t('selectIncomeType')}</option>
                  {incomeTypes
                    .filter((incomeType) => incomeType.isActive)
                    .map((incomeType) => (
                      <option key={incomeType.id} value={incomeType.id}>
                        {incomeType.name}
                      </option>
                    ))}
                </select>
              </div>
            )}

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">
                {t('paymentSource')}
              </label>
              <select
                name="paymentSourceId"
                value={formData.paymentSourceId}
                onChange={handleChange}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                <option value="">{t('selectPaymentSource')}</option>
                {paymentSources
                  .filter((source) => source.isActive)
                  .map((source) => (
                    <option key={source.id} value={source.id}>
                      {source.name}
                    </option>
                  ))}
              </select>
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('status')}</label>
              <select
                name="status"
                value={formData.status}
                onChange={handleChange}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500">
                <option value="Pending">{t('pending')}</option>
                <option value="Completed">{t('completedStatus')}</option>
                <option value="Overdue">{t('overdue')}</option>
              </select>
            </div>

            <div className="flex items-center">
              <input
                type="checkbox"
                name="isPaid"
                checked={formData.isPaid}
                onChange={handleChange}
                className="h-4 w-4 text-blue-600 focus:ring-blue-500 border-gray-300 rounded"
              />
              <label className="ml-2 block text-sm text-gray-700">{t('paymentReceived')}</label>
            </div>

            {formData.isPaid && (
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-2">
                  {t('paidDate')}
                </label>
                <input
                  type="date"
                  name="paidDate"
                  value={formData.paidDate}
                  onChange={handleChange}
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                />
              </div>
            )}

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">
                {t('description')}
              </label>
              <textarea
                name="description"
                value={formData.description}
                onChange={handleChange}
                rows={3}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                placeholder={t('paymentDescription')}
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-2">{t('notes')}</label>
              <textarea
                name="notes"
                value={formData.notes}
                onChange={handleChange}
                rows={2}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                placeholder={t('additionalNotes')}
              />
            </div>

            <div className="flex flex-col sm:flex-row gap-3 pt-4">
              {canDelete && (
                <button
                  type="button"
                  onClick={async () => {
                    if (!payment) return;
                    const ok = confirm(t('confirmDeletePayment') ?? 'Удалить платеж?');
                    if (!ok) return;
                    await onDelete!(payment.id);
                    onClose();
                  }}
                  className="sm:w-auto w-full inline-flex items-center justify-center gap-2 py-2 px-4 rounded-lg font-medium bg-red-50 text-red-700 hover:bg-red-100 transition-colors">
                  <Trash2 className="h-4 w-4" />
                  {t('delete')}
                </button>
              )}

              <div className="flex-1 flex gap-3">
                <button
                  type="submit"
                  disabled={loading}
                  className={`flex-1 py-2 px-4 rounded-lg font-medium transition-colors ${
                    formData.type === 'Income'
                      ? 'bg-emerald-600 text-white hover:bg-emerald-700'
                      : 'bg-red-600 text-white hover:bg-red-700'
                  } disabled:opacity-50`}>
                  {loading
                    ? payment
                      ? t('updating')
                      : t('adding')
                    : payment
                    ? t('save')
                    : t('addPayment')}
                </button>

                <button
                  type="button"
                  onClick={onClose}
                  className="flex-1 py-2 px-4 bg-gray-200 text-gray-800 rounded-lg font-medium hover:bg-gray-300 transition-colors">
                  {t('cancel')}
                </button>
              </div>
            </div>
          </form>
        </div>
      </div>
    </div>
  );
}
