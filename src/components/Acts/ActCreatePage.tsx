import { useCallback, useEffect, useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { apiService } from '../../services/api';
import type { Act, ActInput, ActResponsible, Client, Invoice, PaymentKind } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { normalizeAct, normalizeActResponsibleArray, normalizeClientArray } from '../../utils/acts';
import { ActModal } from './ActModal';
import { useDocumentDeliveryOwner } from '../../hooks/useDocumentDeliveryOwner';
import { StaleUiOperationError } from '../../utils/StaleUiOperationError';
import { acknowledgeCreatedAct, actCreateSuccessMessage, type ActCreateOptions } from '../../services/actCreateAttempts';
import { useToast } from '../../hooks/useToast';

interface ActCreatePageProps {
  onBack: () => void;
  onCreated: (act: Act) => void;
  initialValues?: Partial<ActInput> & { amount?: number | null };
  invoiceSeed?: Invoice | null;
  defaultClientId?: number | null;
  defaultResponsibleId?: number | null;
  onOpenInvoice?: (invoiceId: number, type?: PaymentKind) => void;
}

export function ActCreatePage({
  onBack,
  onCreated,
  initialValues,
  invoiceSeed,
  defaultClientId,
  defaultResponsibleId,
  onOpenInvoice,
}: ActCreatePageProps) {
  const { user } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const canViewLinkedAccounts = permissions.accounts.canView;
  const canViewLinkedClients = permissions.clients.canView;
  const [clients, setClients] = useState<Client[]>([]);
  const [responsibles, setResponsibles] = useState<ActResponsible[]>([]);
  const [lookupsLoading, setLookupsLoading] = useState(true);
  const [lookupsError, setLookupsError] = useState<string | null>(null);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const toast = useToast();
  const writeOwner = useDocumentDeliveryOwner(JSON.stringify([user?.id, user?.role, user?.departmentId, permissions.acts,
    defaultClientId, initialValues?.clientId, initialValues?.legalEntityId, initialValues?.invoiceId, invoiceSeed?.id]));

  useEffect(() => {
    let cancelled = false;

    const loadLookups = async () => {
      setLookupsLoading(true);
      setLookupsError(null);
      try {
        const [clientsResponse, responsiblesResponse] = await Promise.all([
          canViewLinkedClients ? apiService.getClients() : Promise.resolve([]),
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
  }, [canViewLinkedClients]);

  const handleSubmit = useCallback(async (payload: ActInput, options?: ActCreateOptions) => {
    const operation = permissions.acts.canCreate ? writeOwner.begin('create') : null;
    if (!operation) throw new StaleUiOperationError();
    setSubmitError(null);
    try {
      const response = await apiService.createAct(payload, options);
      if (!operation.isCurrent()) throw new StaleUiOperationError();
      const created = normalizeAct(response);
      if (!created) {
        throw new Error('Не удалось прочитать созданный акт.');
      }

      toast.success(actCreateSuccessMessage(response));
      onCreated(created);
      acknowledgeCreatedAct(response);
      return response;
    } catch (error) {
      if (!operation.isCurrent()) throw new StaleUiOperationError();
      const message = extractDisplayErrorMessage(error, 'Не удалось создать акт.');
      setSubmitError(message);
      throw error;
    } finally {
      operation.finish();
    }
  }, [onCreated, permissions.acts.canCreate, toast, writeOwner]);

  return (
    <ActModal
      open
      mode="create"
      act={null}
      onClose={onBack}
      onBack={onBack}
      onSubmit={handleSubmit}
      accessScopeKey={writeOwner.scope}
      submitting={writeOwner.busy}
      errorMessage={submitError}
      clients={clients}
      responsibles={responsibles}
      lookupsLoading={lookupsLoading}
      lookupsError={lookupsError}
      defaultClientId={defaultClientId}
      defaultResponsibleId={defaultResponsibleId}
      initialValues={initialValues}
      invoiceSeed={invoiceSeed}
      presentation="page"
      onOpenInvoice={canViewLinkedAccounts ? onOpenInvoice : undefined}
      canViewInvoices={canViewLinkedAccounts}
    />
  );
}
