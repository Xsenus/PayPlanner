using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Extensions
{
    public static class QueryExtensions
    {
        private const string InstallmentPaymentMarker = "[pp:installment-payment]";

        // ---------- Acts ----------
        public static IQueryable<Act> ApplyActFilters(
            this IQueryable<Act> q,
            DateTime? from,
            DateTime? to,
            ActStatus? status,
            PaymentType? type,
            int? clientId,
            int? responsibleId,
            string? search)
        {
            if (from.HasValue) q = q.Where(a => a.Date >= from.Value);
            if (to.HasValue) q = q.Where(a => a.Date <= to.Value);
            if (status.HasValue) q = q.Where(a => a.Status == status.Value);
            if (type.HasValue) q = q.Where(a => a.Invoice != null && a.Invoice.Type == type.Value);
            if (clientId.HasValue) q = q.Where(a => a.ClientId == clientId.Value);
            if (responsibleId.HasValue) q = q.Where(a => a.ResponsibleId == responsibleId.Value);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLower();
                q = q.Where(a =>
                    (a.Number != null && a.Number.ToLower().Contains(term)) ||
                    (a.Title != null && a.Title.ToLower().Contains(term)) ||
                    (a.InvoiceNumber != null && a.InvoiceNumber.ToLower().Contains(term)) ||
                    (a.CounterpartyInn != null && a.CounterpartyInn.ToLower().Contains(term)) ||
                    (a.Comment != null && a.Comment.ToLower().Contains(term)) ||
                    (a.Client != null &&
                        ((a.Client.Name != null && a.Client.Name.ToLower().Contains(term)) ||
                         (a.Client.Company != null && a.Client.Company.ToLower().Contains(term)))) ||
                    (a.LegalEntity != null &&
                        ((a.LegalEntity.ShortName != null && a.LegalEntity.ShortName.ToLower().Contains(term)) ||
                         (a.LegalEntity.FullName != null && a.LegalEntity.FullName.ToLower().Contains(term)) ||
                         (a.LegalEntity.Inn != null && a.LegalEntity.Inn.ToLower().Contains(term)))) ||
                    (a.Responsible != null &&
                        (a.Responsible.FullName != null && a.Responsible.FullName.ToLower().Contains(term)))
                );
            }

            return q;
        }

        public static IQueryable<Act> ApplyActSort(this IQueryable<Act> q, string? sortBy, string? sortDir)
        {
            bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
            return (sortBy?.ToLowerInvariant()) switch
            {
                "number" => desc
                    ? q.OrderByDescending(a => a.Number).ThenByDescending(a => a.Id)
                    : q.OrderBy(a => a.Number).ThenBy(a => a.Id),
                "amount" => desc
                    ? q.OrderByDescending(a => a.Amount).ThenByDescending(a => a.Id)
                    : q.OrderBy(a => a.Amount).ThenBy(a => a.Id),
                "invoicenumber" => desc
                    ? q.OrderByDescending(a => a.InvoiceNumber ?? string.Empty).ThenByDescending(a => a.Id)
                    : q.OrderBy(a => a.InvoiceNumber ?? string.Empty).ThenBy(a => a.Id),
                "status" => desc
                    ? q.OrderByDescending(a => a.Status).ThenByDescending(a => a.Id)
                    : q.OrderBy(a => a.Status).ThenBy(a => a.Id),
                "client" => desc
                    ? q.OrderByDescending(a => a.Client != null ? a.Client.Name : a.LegalEntity != null ? a.LegalEntity.ShortName : string.Empty)
                        .ThenByDescending(a => a.Id)
                    : q.OrderBy(a => a.Client != null ? a.Client.Name : a.LegalEntity != null ? a.LegalEntity.ShortName : string.Empty)
                        .ThenBy(a => a.Id),
                "inn" or "counterpartyinn" => desc
                    ? q.OrderByDescending(a => a.CounterpartyInn ?? string.Empty).ThenByDescending(a => a.Id)
                    : q.OrderBy(a => a.CounterpartyInn ?? string.Empty).ThenBy(a => a.Id),
                "responsible" => desc
                    ? q.OrderByDescending(a => a.Responsible != null ? a.Responsible.FullName : string.Empty).ThenByDescending(a => a.Id)
                    : q.OrderBy(a => a.Responsible != null ? a.Responsible.FullName : string.Empty).ThenBy(a => a.Id),
                "createdat" => desc
                    ? q.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
                    : q.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id),
                _ => desc
                    ? q.OrderByDescending(a => a.Date).ThenByDescending(a => a.Id)
                    : q.OrderBy(a => a.Date).ThenBy(a => a.Id),
            };
        }

        // ---------- Payments ----------
        public static IQueryable<Payment> WithPaymentIncludes(this IQueryable<Payment> q) =>
            q.Include(p => p.Client)
                 .ThenInclude(c => c!.ClientStatus)
             .Include(p => p.LegalEntity)
             .Include(p => p.ExpenseClient)
                 .ThenInclude(c => c!.ClientStatus)
             .Include(p => p.ExpenseLegalEntity)
              .Include(p => p.ClientCase)
              .Include(p => p.DealType)
              .Include(p => p.IncomeType)
             .Include(p => p.PaymentSource)
             .Include(p => p.Service)
             .Include(p => p.PaymentStatusEntity);

        public static IQueryable<Payment> ApplyPaymentFilters(
            this IQueryable<Payment> q,
            DateTime? from, DateTime? to, int? clientId, int? caseId, string? search, string? clientRole = null, int? legalEntityId = null)
        {
            if (from.HasValue)
            {
                var fromDate = from.Value.Date;
                q = q.Where(p => p.Date >= fromDate);
            }

            if (to.HasValue)
            {
                var toDate = to.Value.Date;
                q = q.Where(p => p.Date <= toDate);
            }

            if (clientId.HasValue)
            {
                var normalizedClientRole = NormalizeClientRole(clientRole);
                q = normalizedClientRole switch
                {
                    "counterparty" => q.Where(p =>
                        p.ClientId == clientId.Value
                        || (p.Invoice != null && p.Invoice.Payments.Any(invoicePayment =>
                            invoicePayment.DocumentKind == DocumentKind.Invoice
                            && invoicePayment.ClientId == clientId.Value))),
                    "expenseClient" => q.Where(p =>
                        p.ExpenseClientId == clientId.Value
                        || (p.Invoice != null && p.Invoice.Payments.Any(invoicePayment =>
                            invoicePayment.DocumentKind == DocumentKind.Invoice
                            && invoicePayment.ExpenseClientId == clientId.Value))),
                    _ => q.Where(p =>
                        p.ClientId == clientId.Value ||
                        p.ExpenseClientId == clientId.Value ||
                        (p.Invoice != null && p.Invoice.Payments.Any(invoicePayment =>
                            invoicePayment.DocumentKind == DocumentKind.Invoice
                            && (invoicePayment.ClientId == clientId.Value
                                || invoicePayment.ExpenseClientId == clientId.Value)))),
                };
            }

            if (legalEntityId.HasValue)
            {
                var normalizedClientRole = NormalizeClientRole(clientRole);
                q = normalizedClientRole switch
                {
                    "counterparty" => q.Where(p =>
                        p.LegalEntityId == legalEntityId.Value
                        || (p.Invoice != null && p.Invoice.Payments.Any(invoicePayment =>
                            invoicePayment.DocumentKind == DocumentKind.Invoice
                            && invoicePayment.LegalEntityId == legalEntityId.Value))),
                    "expenseClient" => q.Where(p =>
                        p.ExpenseLegalEntityId == legalEntityId.Value
                        || (p.Invoice != null && p.Invoice.Payments.Any(invoicePayment =>
                            invoicePayment.DocumentKind == DocumentKind.Invoice
                            && invoicePayment.ExpenseLegalEntityId == legalEntityId.Value))),
                    _ => q.Where(p =>
                        p.LegalEntityId == legalEntityId.Value ||
                        p.ExpenseLegalEntityId == legalEntityId.Value ||
                        (p.Invoice != null && p.Invoice.Payments.Any(invoicePayment =>
                            invoicePayment.DocumentKind == DocumentKind.Invoice
                            && (invoicePayment.LegalEntityId == legalEntityId.Value
                                || invoicePayment.ExpenseLegalEntityId == legalEntityId.Value)))),
                };
            }

            if (caseId.HasValue)
            {
                q = q.Where(p =>
                    p.ClientCaseId == caseId.Value ||
                    p.InvoiceClientCases.Any(link => link.ClientCaseId == caseId.Value) ||
                    (p.Invoice != null && p.Invoice.Payments.Any(invoicePayment =>
                        invoicePayment.DocumentKind == DocumentKind.Invoice
                        && (invoicePayment.ClientCaseId == caseId.Value
                            || invoicePayment.InvoiceClientCases.Any(link => link.ClientCaseId == caseId.Value)))));
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                search = search.Trim().ToLower();
                q = q.Where(p =>
                    (p.Description != null && p.Description.ToLower().Contains(search)) ||
                    (p.Notes != null && p.Notes.ToLower().Contains(search)) ||
                    (p.Account != null && p.Account.ToLower().Contains(search)) ||
                    (p.PaymentOrderNumber != null && p.PaymentOrderNumber.ToLower().Contains(search)) ||
                    (p.Client != null &&
                        ((p.Client.Name != null && p.Client.Name.ToLower().Contains(search)) ||
                         (p.Client.Company != null && p.Client.Company.ToLower().Contains(search)))) ||
                    (p.ExpenseClient != null &&
                        ((p.ExpenseClient.Name != null && p.ExpenseClient.Name.ToLower().Contains(search)) ||
                         (p.ExpenseClient.Company != null && p.ExpenseClient.Company.ToLower().Contains(search)))) ||
                    (p.ClientCase != null && p.ClientCase.Title != null && p.ClientCase.Title.ToLower().Contains(search)) ||
                    (p.DealType != null && p.DealType.Name != null && p.DealType.Name.ToLower().Contains(search)) ||
                    (p.IncomeType != null && p.IncomeType.Name != null && p.IncomeType.Name.ToLower().Contains(search)) ||
                    (p.PaymentSource != null && p.PaymentSource.Name != null && p.PaymentSource.Name.ToLower().Contains(search)));
            }
            return q;
        }

        public static string? NormalizeClientRole(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = value.Trim().ToLowerInvariant();
            return normalized switch
            {
                "counterparty" or "client" or "supplier" => "counterparty",
                "expenseclient" or "expense-client" or "expense_client" => "expenseClient",
                _ => null,
            };
        }

        public static IQueryable<Payment> ExcludeInstallmentParentInvoices(
            this IQueryable<Payment> q,
            PaymentContext db,
            bool excludeInstallmentParents = true)
        {
            if (!excludeInstallmentParents)
            {
                return q;
            }

            return q.Where(payment =>
                payment.DocumentKind != DocumentKind.Invoice
                || payment.InvoiceId == null
                || !db.Payments.Any(child =>
                    child.InvoiceId == payment.InvoiceId
                    && child.Id != payment.Id
                    && child.DocumentKind != DocumentKind.Invoice
                    && child.SystemNotes != null
                    && (child.SystemNotes.Contains(InstallmentPaymentMarker)
                        || child.SystemNotes.Contains(PaymentRemainderTransferRules.PartialRemainderMarker))));
        }

        public static IQueryable<Payment> ApplyPaymentSort(this IQueryable<Payment> q, string? sortBy, string? sortDir)
        {
            bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
            return (sortBy?.ToLowerInvariant()) switch
            {
                "planneddate" => desc
                    ? q.OrderByDescending(p => p.PlannedDate).ThenBy(p => p.Id)
                    : q.OrderBy(p => p.PlannedDate).ThenBy(p => p.Id),
                "client" or "counterparty" => desc
                    ? q.OrderByDescending(p =>
                            p.Client != null ? p.Client.Name :
                            (p.CounterpartyName != null && p.CounterpartyName != string.Empty ? p.CounterpartyName : null) ??
                            (p.LegalEntity != null ? p.LegalEntity.ShortName ?? p.LegalEntity.FullName : null) ??
                            (p.ExpenseClient != null ? p.ExpenseClient.Name : null) ??
                            (p.ExpenseLegalEntity != null ? p.ExpenseLegalEntity.ShortName ?? p.ExpenseLegalEntity.FullName : null) ??
                            string.Empty)
                        .ThenBy(p => p.Id)
                    : q.OrderBy(p =>
                            p.Client != null ? p.Client.Name :
                            (p.CounterpartyName != null && p.CounterpartyName != string.Empty ? p.CounterpartyName : null) ??
                            (p.LegalEntity != null ? p.LegalEntity.ShortName ?? p.LegalEntity.FullName : null) ??
                            (p.ExpenseClient != null ? p.ExpenseClient.Name : null) ??
                            (p.ExpenseLegalEntity != null ? p.ExpenseLegalEntity.ShortName ?? p.ExpenseLegalEntity.FullName : null) ??
                            string.Empty)
                        .ThenBy(p => p.Id),
                "description" => desc
                    ? q.OrderByDescending(p => p.Description ?? string.Empty).ThenBy(p => p.Id)
                    : q.OrderBy(p => p.Description ?? string.Empty).ThenBy(p => p.Id),
                "type" => desc ? q.OrderByDescending(p => p.Type).ThenBy(p => p.Id) : q.OrderBy(p => p.Type).ThenBy(p => p.Id),
                "status" => desc ? q.OrderByDescending(p => p.Status).ThenBy(p => p.Id) : q.OrderBy(p => p.Status).ThenBy(p => p.Id),
                "category" => desc
                    ? q.OrderByDescending(p =>
                            (p.DealType != null ? p.DealType.Name : null) ??
                            (p.IncomeType != null ? p.IncomeType.Name : null) ??
                            (p.PaymentSource != null ? p.PaymentSource.Name : null) ??
                            string.Empty)
                        .ThenBy(p => p.Id)
                    : q.OrderBy(p =>
                            (p.DealType != null ? p.DealType.Name : null) ??
                            (p.IncomeType != null ? p.IncomeType.Name : null) ??
                            (p.PaymentSource != null ? p.PaymentSource.Name : null) ??
                            string.Empty)
                        .ThenBy(p => p.Id),
                "amount" => desc ? q.OrderByDescending(p => p.Amount).ThenBy(p => p.Id) : q.OrderBy(p => p.Amount).ThenBy(p => p.Id),
                "paiddate" => desc
                    ? q.OrderByDescending(p => p.PaidDate ?? p.LastPaymentDate).ThenBy(p => p.Id)
                    : q.OrderBy(p => p.PaidDate ?? p.LastPaymentDate).ThenBy(p => p.Id),
                "createdat" => desc ? q.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id) : q.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id),
                _ => desc ? q.OrderByDescending(p => p.Date).ThenBy(p => p.Id) : q.OrderBy(p => p.Date).ThenBy(p => p.Id), // date по умолчанию
            };
        }

        // ---------- Clients ----------
        public static IQueryable<Client> ApplyClientFilters(this IQueryable<Client> q, string? search, bool? isActive)
        {
            if (!string.IsNullOrWhiteSpace(search))
            {
                search = search.Trim().ToLower();
                q = q.Where(c =>
                    (c.Name != null && c.Name.ToLower().Contains(search)) ||
                    (c.Email != null && c.Email.ToLower().Contains(search)) ||
                    (c.Phone != null && c.Phone.ToLower().Contains(search)) ||
                    (c.Company != null && c.Company.ToLower().Contains(search)) ||
                    (c.Address != null && c.Address.ToLower().Contains(search)));
            }
            if (isActive.HasValue) q = q.Where(c => c.IsActive == isActive.Value);
            return q;
        }

        public static IQueryable<Client> ApplyClientVisibility(this IQueryable<Client> q, bool isAdmin, int? responsibleUserId)
        {
            if (isAdmin) return q;
            if (!responsibleUserId.HasValue) return q.Where(_ => false);
            return q.Where(c => c.ResponsibleUserId == responsibleUserId.Value);
        }

        public static IQueryable<LegalEntity> ApplyLegalEntityVisibility(this IQueryable<LegalEntity> q, bool isAdmin, int? responsibleUserId)
        {
            if (isAdmin) return q;
            if (!responsibleUserId.HasValue) return q.Where(_ => false);
            return q.Where(e => e.ResponsibleUserId == responsibleUserId.Value);
        }

        public static IQueryable<Client> ApplyCounterpartyRoleFilter(this IQueryable<Client> q, string? counterpartyRole)
        {
            var normalized = NormalizeCounterpartyRole(counterpartyRole);
            return normalized switch
            {
                "supplier" => q.Where(c =>
                    c.IsClientAndSupplier ||
                    (c.ClientStatus != null &&
                     c.ClientStatus.SystemKey != null &&
                     (c.ClientStatus.SystemKey.ToLower() == ClientStatusSystemKeys.Supplier ||
                      c.ClientStatus.SystemKey.ToLower() == ClientStatusSystemKeys.Creditor))),
                "client" => q.Where(c =>
                    c.IsClientAndSupplier ||
                    c.ClientStatus == null ||
                    c.ClientStatus.SystemKey == null ||
                    (c.ClientStatus.SystemKey.ToLower() != ClientStatusSystemKeys.Supplier &&
                     c.ClientStatus.SystemKey.ToLower() != ClientStatusSystemKeys.Creditor)),
                _ => q,
            };
        }

        public static IQueryable<LegalEntity> ApplyCounterpartyRoleFilter(this IQueryable<LegalEntity> q, string? counterpartyRole)
        {
            var normalized = NormalizeCounterpartyRole(counterpartyRole);
            return normalized switch
            {
                "supplier" => q.Where(e =>
                    e.IsClientAndSupplier ||
                    (e.ClientStatus != null &&
                     e.ClientStatus.SystemKey != null &&
                     (e.ClientStatus.SystemKey.ToLower() == ClientStatusSystemKeys.Supplier ||
                      e.ClientStatus.SystemKey.ToLower() == ClientStatusSystemKeys.Creditor))),
                "client" => q.Where(e =>
                    e.IsClientAndSupplier ||
                    e.ClientStatus == null ||
                    e.ClientStatus.SystemKey == null ||
                    (e.ClientStatus.SystemKey.ToLower() != ClientStatusSystemKeys.Supplier &&
                     e.ClientStatus.SystemKey.ToLower() != ClientStatusSystemKeys.Creditor)),
                _ => q,
            };
        }

        public static string? NormalizeCounterpartyRole(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.Trim().ToLowerInvariant() switch
            {
                "supplier" or "vendor" => "supplier",
                "client" or "customer" => "client",
                _ => null,
            };
        }

        public static IQueryable<Client> ApplyClientSort(this IQueryable<Client> q, string? sortBy, string? sortDir)
        {
            bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
            return (sortBy?.ToLowerInvariant()) switch
            {
                "createdat" => desc ? q.OrderByDescending(c => c.CreatedAt) : q.OrderBy(c => c.CreatedAt),
                _ => desc ? q.OrderByDescending(c => c.Name) : q.OrderBy(c => c.Name), // name по умолчанию
            };
        }

        // ---------- Cases ----------
        public static IQueryable<ClientCase> WithCaseIncludes(this IQueryable<ClientCase> q, bool includePayments = false)
        {
            q = q.Include(c => c.Client);
            q = q.Include(c => c.LegalEntity);
            q = q.Include(c => c.ResponsibleUser);
            if (includePayments) q = q.Include(c => c.Payments);
            return q;
        }

        public static IQueryable<ClientCase> ApplyCaseRegistryScope(this IQueryable<ClientCase> q, string? scope, int currentUserId)
        {
            return (scope?.Trim().ToLowerInvariant()) switch
            {
                "active" => q.Where(c => c.Status != ClientCaseStatus.Closed && c.Status != ClientCaseStatus.Lost),
                "archive" => q.Where(c => c.Status == ClientCaseStatus.Closed || c.Status == ClientCaseStatus.Lost),
                "mine" => q.Where(c => c.ResponsibleUserId == currentUserId),
                "control" => q.Where(c => c.IsUnderControl),
                _ => q,
            };
        }

        public static IQueryable<ClientCase> ApplyCaseFilters(
            this IQueryable<ClientCase> q,
            int? clientId,
            int? legalEntityId,
            ClientCaseStatus? status,
            string? search,
            int? responsibleUserId = null,
            string? caseType = null,
            string? priority = null,
            DateTime? from = null,
            DateTime? to = null,
            decimal? minFee = null,
            decimal? maxFee = null)
        {
            if (clientId.HasValue) q = q.Where(c => c.ClientId == clientId.Value);
            if (legalEntityId.HasValue) q = q.Where(c => c.LegalEntityId == legalEntityId.Value);
            if (status.HasValue) q = q.Where(c => c.Status == status.Value);
            if (responsibleUserId.HasValue) q = q.Where(c => c.ResponsibleUserId == responsibleUserId.Value);
            if (!string.IsNullOrWhiteSpace(caseType))
            {
                var type = caseType.Trim().ToLower();
                q = q.Where(c => c.CaseType != null && c.CaseType.ToLower().Contains(type));
            }

            if (!string.IsNullOrWhiteSpace(priority))
            {
                var normalizedPriority = priority.Trim().ToLower();
                q = q.Where(c => c.Priority != null && c.Priority.ToLower() == normalizedPriority);
            }

            if (from.HasValue) q = q.Where(c => c.CreatedAt >= from.Value || (c.StartDate.HasValue && c.StartDate.Value >= from.Value));
            if (to.HasValue) q = q.Where(c => c.CreatedAt <= to.Value || (c.DueDate.HasValue && c.DueDate.Value <= to.Value));
            if (minFee.HasValue) q = q.Where(c => c.FeeAmount >= minFee.Value);
            if (maxFee.HasValue) q = q.Where(c => c.FeeAmount <= maxFee.Value);

            if (!string.IsNullOrWhiteSpace(search))
            {
                search = search.Trim().ToLower();
                q = q.Where(c =>
                    (c.CaseNumber != null && c.CaseNumber.ToLower().Contains(search)) ||
                    (c.Title != null && c.Title.ToLower().Contains(search)) ||
                    (c.Description != null && c.Description.ToLower().Contains(search)) ||
                    (c.CaseType != null && c.CaseType.ToLower().Contains(search)) ||
                    (c.Stage != null && c.Stage.ToLower().Contains(search)) ||
                    (c.CourtName != null && c.CourtName.ToLower().Contains(search)) ||
                    (c.CourtCaseNumber != null && c.CourtCaseNumber.ToLower().Contains(search)) ||
                    (c.JudicialUid != null && c.JudicialUid.ToLower().Contains(search)) ||
                    (c.Claimant != null && c.Claimant.ToLower().Contains(search)) ||
                    (c.Defendant != null && c.Defendant.ToLower().Contains(search)) ||
                    (c.ThirdParties != null && c.ThirdParties.ToLower().Contains(search)) ||
                    (c.Representatives != null && c.Representatives.ToLower().Contains(search)) ||
                    (c.JudgeName != null && c.JudgeName.ToLower().Contains(search)) ||
                    (c.CourtSecretaryContacts != null && c.CourtSecretaryContacts.ToLower().Contains(search)) ||
                    (c.DocumentsSummary != null && c.DocumentsSummary.ToLower().Contains(search)) ||
                    (c.EventHistory != null && c.EventHistory.ToLower().Contains(search)) ||
                    (c.Strategy != null && c.Strategy.ToLower().Contains(search)) ||
                    (c.Client != null && c.Client.Name != null && c.Client.Name.ToLower().Contains(search)) ||
                    (c.LegalEntity != null && c.LegalEntity.ShortName != null && c.LegalEntity.ShortName.ToLower().Contains(search)) ||
                    (c.LegalEntity != null && c.LegalEntity.FullName != null && c.LegalEntity.FullName.ToLower().Contains(search)));
            }
            return q;
        }

        public static IQueryable<ClientCase> ApplyCaseActivityFilter(
            this IQueryable<ClientCase> q,
            string? activity,
            int? inactiveDays = null,
            IQueryable<TaskJournalItem>? taskJournalItems = null,
            IQueryable<TaskJournalComment>? taskJournalComments = null,
            IQueryable<ClientCaseDocument>? caseDocuments = null,
            IQueryable<ClientCaseCourtEvent>? courtEvents = null,
            string? excludedTaskTag = null)
        {
            var normalized = activity?.Trim().ToLowerInvariant();
            if (normalized is not ("inactive" or "active"))
            {
                return q;
            }

            var days = Math.Clamp(inactiveDays ?? 14, 1, 365);
            var cutoff = DateTime.UtcNow.AddDays(-days);
            IQueryable<ClientCase> inactiveQuery;

            if (taskJournalItems is null)
            {
                inactiveQuery = q.Where(c =>
                    c.InactivityRemindersEnabled
                    && c.Status != ClientCaseStatus.Closed
                    && c.Status != ClientCaseStatus.Lost
                    && c.CreatedAt <= cutoff
                    && (c.LastViewedAt ?? c.CreatedAt) <= cutoff
                    && (c.ActivityConfirmedAt ?? c.CreatedAt) <= cutoff);

                return normalized == "inactive"
                    ? inactiveQuery
                    : q.Where(c =>
                        !c.InactivityRemindersEnabled
                        || c.Status == ClientCaseStatus.Closed
                        || c.Status == ClientCaseStatus.Lost
                        || c.CreatedAt > cutoff
                        || (c.LastViewedAt ?? c.CreatedAt) > cutoff
                        || (c.ActivityConfirmedAt ?? c.CreatedAt) > cutoff);
            }

            var meaningfulTaskActivities = taskJournalItems
                .Where(task =>
                    task.ClientCaseId.HasValue
                    && (string.IsNullOrEmpty(excludedTaskTag) || !task.TagsJson.Contains(excludedTaskTag)))
                .Select(task => new
                {
                    CaseId = task.ClientCaseId!.Value,
                    At = (DateTime?)(task.UpdatedAt ?? task.CreatedAt),
                });
            var meaningfulActivities = meaningfulTaskActivities;
            if (taskJournalComments is not null)
            {
                meaningfulActivities = meaningfulActivities.Concat(taskJournalComments
                    .Where(comment =>
                        comment.TaskJournalItem.ClientCaseId.HasValue
                        && (string.IsNullOrEmpty(excludedTaskTag) || !comment.TaskJournalItem.TagsJson.Contains(excludedTaskTag)))
                    .Select(comment => new
                    {
                        CaseId = comment.TaskJournalItem.ClientCaseId!.Value,
                        At = (DateTime?)(comment.UpdatedAt ?? comment.CreatedAt),
                    }));
            }
            if (caseDocuments is not null)
            {
                meaningfulActivities = meaningfulActivities.Concat(caseDocuments.Select(document => new
                {
                    CaseId = document.ClientCaseId,
                    At = (DateTime?)document.UploadedAt,
                }));
            }
            if (courtEvents is not null)
            {
                meaningfulActivities = meaningfulActivities.Concat(courtEvents.Select(courtEvent => new
                {
                    CaseId = courtEvent.ClientCaseId,
                    At = (DateTime?)(courtEvent.UpdatedAt ?? courtEvent.CreatedAt),
                }));
            }

            var latestMeaningfulActivityByCase = meaningfulActivities
                .GroupBy(item => item.CaseId)
                .Select(group => new
                {
                    CaseId = group.Key,
                    At = group.Max(item => item.At),
                });

            inactiveQuery = q.Where(c =>
                c.InactivityRemindersEnabled
                && c.Status != ClientCaseStatus.Closed
                && c.Status != ClientCaseStatus.Lost
                && (latestMeaningfulActivityByCase
                    .Where(activityItem => activityItem.CaseId == c.Id)
                    .Select(activityItem => activityItem.At)
                    .FirstOrDefault() ?? c.CreatedAt) <= cutoff
                && (c.LastViewedAt ?? c.CreatedAt) <= cutoff
                && (c.ActivityConfirmedAt ?? c.CreatedAt) <= cutoff);

            return normalized == "inactive"
                ? inactiveQuery
                : q.Where(c => !inactiveQuery.Select(inactiveCase => inactiveCase.Id).Contains(c.Id));
        }

        public static IQueryable<ClientCase> ApplyCaseSort(this IQueryable<ClientCase> q, string? sortBy, string? sortDir)
        {
            bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
            return (sortBy?.ToLowerInvariant()) switch
            {
                "casenumber" => desc ? q.OrderByDescending(c => c.CaseNumber) : q.OrderBy(c => c.CaseNumber),
                "title" => desc ? q.OrderByDescending(c => c.Title) : q.OrderBy(c => c.Title),
                "client" => desc
                    ? q.OrderByDescending(c => c.Client != null ? c.Client.Name : c.LegalEntity != null ? c.LegalEntity.ShortName : string.Empty)
                    : q.OrderBy(c => c.Client != null ? c.Client.Name : c.LegalEntity != null ? c.LegalEntity.ShortName : string.Empty),
                "casetype" => desc ? q.OrderByDescending(c => c.CaseType) : q.OrderBy(c => c.CaseType),
                "stage" => desc ? q.OrderByDescending(c => c.Stage) : q.OrderBy(c => c.Stage),
                "responsible" => desc
                    ? q.OrderByDescending(c => c.ResponsibleUser != null ? c.ResponsibleUser.FullName : string.Empty)
                    : q.OrderBy(c => c.ResponsibleUser != null ? c.ResponsibleUser.FullName : string.Empty),
                "startdate" => desc ? q.OrderByDescending(c => c.StartDate) : q.OrderBy(c => c.StartDate),
                "duedate" => desc ? q.OrderByDescending(c => c.DueDate) : q.OrderBy(c => c.DueDate),
                "feeamount" => desc ? q.OrderByDescending(c => (double)c.FeeAmount) : q.OrderBy(c => (double)c.FeeAmount),
                "priority" => desc ? q.OrderByDescending(c => c.Priority) : q.OrderBy(c => c.Priority),
                "court" => desc ? q.OrderByDescending(c => c.CourtName) : q.OrderBy(c => c.CourtName),
                "nexthearingat" => desc ? q.OrderByDescending(c => c.NextHearingAt) : q.OrderBy(c => c.NextHearingAt),
                "lastviewedat" => desc ? q.OrderByDescending(c => c.LastViewedAt) : q.OrderBy(c => c.LastViewedAt),
                "status" => desc ? q.OrderByDescending(c => c.Status) : q.OrderBy(c => c.Status),
                _ => desc ? q.OrderByDescending(c => c.CreatedAt) : q.OrderBy(c => c.CreatedAt),
            };
        }
    }
}
