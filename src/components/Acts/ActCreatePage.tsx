import { useCallback, useEffect, useState } from 'react';
import { apiService } from '../../services/api';
import type { Act, ActInput, ActResponsible, Client, PaymentKind } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { normalizeAct, normalizeActResponsibleArray, normalizeClientArray } from '../../utils/acts';
import { ActModal } from './ActModal';

interface ActCreatePageProps {
  onBack: () => void;
  onCreated: (act: Act) => void;
  initialValues?: Partial<ActInput> & { amount?: number | null };
  defaultClientId?: number | null;
  defaultResponsibleId?: number | null;
  onOpenInvoice?: (invoiceId: number, type?: PaymentKind) => void;
}

export function ActCreatePage({
  onBack,
  onCreated,
  initialValues,
  defaultClientId,
  defaultResponsibleId,
  onOpenInvoice,
}: ActCreatePageProps) {
  const [clients, setClients] = useState<Client[]>([]);
  const [responsibles, setResponsibles] = useState<ActResponsible[]>([]);
  const [lookupsLoading, setLookupsLoading] = useState(true);
  const [lookupsError, setLookupsError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    const loadLookups = async () => {
      setLookupsLoading(true);
      setLookupsError(null);
      try {
        const [clientsResponse, responsiblesResponse] = await Promise.all([
          apiService.getClients(),
          apiService.getActResponsibles(),
        ]);

        if (cancelled) {
          return;
        }

        setClients(normalizeClientArray(clientsResponse));
        setResponsibles(normalizeActResponsibleArray(responsiblesResponse));
      } catch (error) {
        if (cancelled) {
          return;
        }

        setLookupsError(extractDisplayErrorMessage(error, 'Не удалось загрузить данные для акта.'));
      } finally {
        if (!cancelled) {
          setLookupsLoading(false);
        }
      }
    };

    void loadLookups();

    return () => {
      cancelled = true;
    };
  }, []);

  const handleSubmit = useCallback(async (payload: ActInput) => {
    setSubmitting(true);
    setSubmitError(null);
    try {
      const created = normalizeAct(await apiService.createAct(payload));
      if (!created) {
        throw new Error('Не удалось прочитать созданный акт.');
      }

      onCreated(created);
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось создать акт.');
      setSubmitError(message);
      throw error;
    } finally {
      setSubmitting(false);
    }
  }, [onCreated]);

  return (
    <ActModal
      open
      mode="create"
      act={null}
      onClose={onBack}
      onBack={onBack}
      onSubmit={handleSubmit}
      submitting={submitting}
      errorMessage={submitError}
      clients={clients}
      responsibles={responsibles}
      lookupsLoading={lookupsLoading}
      lookupsError={lookupsError}
      defaultClientId={defaultClientId}
      defaultResponsibleId={defaultResponsibleId}
      initialValues={initialValues}
      presentation="page"
      onOpenInvoice={onOpenInvoice}
    />
  );
}
