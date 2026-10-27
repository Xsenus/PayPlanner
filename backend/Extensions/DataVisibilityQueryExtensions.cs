using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Extensions;

public static class DataVisibilityQueryExtensions
{
    // Keep assignment and every notification channel within the same live request scope as task reads.
    public static IQueryable<User> WithSalesRequestAccess(this IQueryable<User> users, int requestId, PaymentContext db)
        => users.Where(user => user.IsActive && user.IsApproved && user.Role != null
            && db.SalesRequests.Any(request => request.Id == requestId
                && (user.Role.Name.ToLower() == "admin" || user.Role.Permissions.Any(permission => permission.Section == RolePermissionCatalog.Requests
                    && permission.CanView && (permission.ViewScope == DataVisibilityScope.All
                        || request.ResponsibleUserId == user.Id || request.CreatedByUserId == user.Id
                        || (permission.ViewScope == DataVisibilityScope.Department && db.Users.Any(member => member.IsActive
                            && ((user.DepartmentId != null && member.DepartmentId == user.DepartmentId)
                                || (user.Department != null && user.Department.Trim() != "" && member.Department == user.Department.Trim()))
                            && (member.Id == request.ResponsibleUserId || member.Id == request.CreatedByUserId))))))));

    public static IQueryable<Client> ApplyClientVisibility(
        this IQueryable<Client> query,
        DataVisibilityAccess? access)
    {
        if (access is null) return query.Where(_ => false);
        if (access.SeesAll) return query;

        var visibleUserIds = access.VisibleUserIds.ToArray();
        return query.Where(client =>
            client.ResponsibleUserId.HasValue
            && visibleUserIds.Contains(client.ResponsibleUserId.Value));
    }

    public static IQueryable<SalesRequest> ApplySalesRequestVisibility(
        this IQueryable<SalesRequest> query,
        DataVisibilityAccess? access)
    {
        if (access is null) return query.Where(_ => false);
        if (access.SeesAll) return query;

        var visibleUserIds = access.VisibleUserIds.Append(access.UserId).Distinct().ToArray();
        return query.Where(request =>
            visibleUserIds.Contains(request.ResponsibleUserId)
            || visibleUserIds.Contains(request.CreatedByUserId));
    }

    public static IQueryable<LegalEntity> ApplyLegalEntityVisibility(
        this IQueryable<LegalEntity> query,
        DataVisibilityAccess? access)
    {
        if (access is null) return query.Where(_ => false);
        if (access.SeesAll) return query;

        var visibleUserIds = access.VisibleUserIds.ToArray();
        return query.Where(entity =>
            entity.ResponsibleUserId.HasValue
            && visibleUserIds.Contains(entity.ResponsibleUserId.Value));
    }

    public static IQueryable<ClientCase> ApplyClientCaseVisibility(
        this IQueryable<ClientCase> query,
        DataVisibilityAccess? access)
    {
        if (access is null) return query.Where(_ => false);
        if (access.SeesAll) return query;

        var visibleUserIds = access.VisibleUserIds.ToArray();
        return query.Where(clientCase =>
            !clientCase.IsHiddenFromExecutor
            && ((clientCase.ResponsibleUserId.HasValue
                && visibleUserIds.Contains(clientCase.ResponsibleUserId.Value))
            || (clientCase.Client != null
                && clientCase.Client.ResponsibleUserId.HasValue
                && visibleUserIds.Contains(clientCase.Client.ResponsibleUserId.Value))
            || (clientCase.LegalEntity != null
                && clientCase.LegalEntity.ResponsibleUserId.HasValue
                && visibleUserIds.Contains(clientCase.LegalEntity.ResponsibleUserId.Value))));
    }

    public static IQueryable<TaskJournalItem> ApplySalesRequestTaskPrivacy(
        this IQueryable<TaskJournalItem> query,
        DataVisibilityAccess? access,
        PaymentContext db)
    {
        if (access is null) return query.Where(_ => false);
        if (!access.IsAdmin)
        {
            // A broad task/calendar permission must not disclose the separate sales section.
            query = query.Where(task => task.SalesRequestId == null || db.Users.Any(user => user.Id == access.UserId && user.IsActive && user.IsApproved
                && user.Role != null && user.Role.Permissions.Any(permission => permission.Section == RolePermissionCatalog.Requests && permission.CanView
                    && (permission.ViewScope == DataVisibilityScope.All || db.SalesRequests.Any(request => request.Id == task.SalesRequestId
                        && (request.ResponsibleUserId == user.Id || request.CreatedByUserId == user.Id
                            || (permission.ViewScope == DataVisibilityScope.Department && db.Users.Any(member => member.IsActive
                                && ((user.DepartmentId != null && member.DepartmentId == user.DepartmentId)
                                    || (user.Department != null && user.Department.Trim() != "" && member.Department == user.Department.Trim()))
                                && (member.Id == request.ResponsibleUserId || member.Id == request.CreatedByUserId)))))))));
        }
        return query;
    }

    public static IQueryable<TaskJournalItem> ApplyTaskVisibility(
        this IQueryable<TaskJournalItem> query,
        DataVisibilityAccess? access,
        PaymentContext db)
    {
        if (access is null) return query.Where(_ => false);
        query = query.ApplySalesRequestTaskPrivacy(access, db);
        if (access.SeesAll) return query;

        var visibleUserIds = access.VisibleUserIds.ToArray();
        if (string.Equals(access.Section, RolePermissionCatalog.Calendar, StringComparison.OrdinalIgnoreCase))
        {
            return query.Where(task =>
                (task.AssigneeUserId.HasValue && visibleUserIds.Contains(task.AssigneeUserId.Value))
                || task.Participants.Any(participant =>
                    participant.UserId.HasValue && visibleUserIds.Contains(participant.UserId.Value)));
        }

        return query.Where(task =>
            (task.AssigneeUserId.HasValue && visibleUserIds.Contains(task.AssigneeUserId.Value))
            || (task.AuthorUserId.HasValue && visibleUserIds.Contains(task.AuthorUserId.Value))
            || task.Participants.Any(participant =>
                participant.UserId.HasValue && visibleUserIds.Contains(participant.UserId.Value)));
    }

    public static IQueryable<Payment> ApplyPaymentVisibility(
        this IQueryable<Payment> query,
        DataVisibilityAccess? access)
    {
        if (access is null) return query.Where(_ => false);
        if (access.SeesAll) return query;

        var visibleUserIds = access.VisibleUserIds.ToArray();
        return query.Where(payment =>
            payment.Responsibles.Any(link => visibleUserIds.Contains(link.UserId))
            || (payment.Client != null
                && payment.Client.ResponsibleUserId.HasValue
                && visibleUserIds.Contains(payment.Client.ResponsibleUserId.Value))
            || (payment.ExpenseClient != null
                && payment.ExpenseClient.ResponsibleUserId.HasValue
                && visibleUserIds.Contains(payment.ExpenseClient.ResponsibleUserId.Value))
            || (payment.LegalEntity != null
                && payment.LegalEntity.ResponsibleUserId.HasValue
                && visibleUserIds.Contains(payment.LegalEntity.ResponsibleUserId.Value))
            || (payment.ExpenseLegalEntity != null
                && payment.ExpenseLegalEntity.ResponsibleUserId.HasValue
                && visibleUserIds.Contains(payment.ExpenseLegalEntity.ResponsibleUserId.Value))
            || (payment.ClientCase != null
                && payment.ClientCase.Client != null
                && payment.ClientCase.Client.ResponsibleUserId.HasValue
                && visibleUserIds.Contains(payment.ClientCase.Client.ResponsibleUserId.Value))
            || (payment.ClientCase != null
                && payment.ClientCase.LegalEntity != null
                && payment.ClientCase.LegalEntity.ResponsibleUserId.HasValue
                && visibleUserIds.Contains(payment.ClientCase.LegalEntity.ResponsibleUserId.Value))
            || payment.InvoiceClientCases.Any(link =>
                (link.ClientCase.Client != null
                    && link.ClientCase.Client.ResponsibleUserId.HasValue
                    && visibleUserIds.Contains(link.ClientCase.Client.ResponsibleUserId.Value))
                || (link.ClientCase.LegalEntity != null
                    && link.ClientCase.LegalEntity.ResponsibleUserId.HasValue
                    && visibleUserIds.Contains(link.ClientCase.LegalEntity.ResponsibleUserId.Value)))
            || (payment.Invoice != null
                && payment.Invoice.Payments.Any(invoicePayment =>
                    invoicePayment.DocumentKind == DocumentKind.Invoice
                    && (!payment.ClientId.HasValue || payment.ClientId == invoicePayment.ClientId)
                    && (!payment.LegalEntityId.HasValue || payment.LegalEntityId == invoicePayment.LegalEntityId)
                    && (!payment.SellerCompanyId.HasValue || payment.SellerCompanyId == invoicePayment.SellerCompanyId)
                    && (!payment.SellerBranchId.HasValue || payment.SellerBranchId == invoicePayment.SellerBranchId)
                    && (payment.DocumentKind != DocumentKind.Invoice
                        || (payment.ClientId == invoicePayment.ClientId && payment.LegalEntityId == invoicePayment.LegalEntityId))
                    && (payment.Type == invoicePayment.Type
                        || (payment.DocumentKind != DocumentKind.Invoice
                            && ((payment.ClientId.HasValue && payment.ClientId == invoicePayment.ClientId)
                                || (payment.LegalEntityId.HasValue && payment.LegalEntityId == invoicePayment.LegalEntityId))))
                    && (invoicePayment.Responsibles.Any(link => visibleUserIds.Contains(link.UserId))
                        || (invoicePayment.Client != null
                            && invoicePayment.Client.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(invoicePayment.Client.ResponsibleUserId.Value))
                        || (invoicePayment.ExpenseClient != null
                            && invoicePayment.ExpenseClient.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(invoicePayment.ExpenseClient.ResponsibleUserId.Value))
                        || (invoicePayment.LegalEntity != null
                            && invoicePayment.LegalEntity.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(invoicePayment.LegalEntity.ResponsibleUserId.Value))
                        || (invoicePayment.ExpenseLegalEntity != null
                            && invoicePayment.ExpenseLegalEntity.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(invoicePayment.ExpenseLegalEntity.ResponsibleUserId.Value))
                        || (invoicePayment.ClientCase != null
                            && invoicePayment.ClientCase.Client != null
                            && invoicePayment.ClientCase.Client.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(invoicePayment.ClientCase.Client.ResponsibleUserId.Value))
                        || (invoicePayment.ClientCase != null
                            && invoicePayment.ClientCase.LegalEntity != null
                            && invoicePayment.ClientCase.LegalEntity.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(invoicePayment.ClientCase.LegalEntity.ResponsibleUserId.Value))
                        || invoicePayment.InvoiceClientCases.Any(link =>
                            (link.ClientCase.Client != null
                                && link.ClientCase.Client.ResponsibleUserId.HasValue
                                && visibleUserIds.Contains(link.ClientCase.Client.ResponsibleUserId.Value))
                            || (link.ClientCase.LegalEntity != null
                                && link.ClientCase.LegalEntity.ResponsibleUserId.HasValue
                                && visibleUserIds.Contains(link.ClientCase.LegalEntity.ResponsibleUserId.Value)))
                        || (invoicePayment.Contract != null
                            && (invoicePayment.Contract.ClientContracts.Any(link =>
                                    link.Client.ResponsibleUserId.HasValue
                                    && visibleUserIds.Contains(link.Client.ResponsibleUserId.Value))
                                || invoicePayment.Contract.LegalEntityContracts.Any(link =>
                                    link.LegalEntity.ResponsibleUserId.HasValue
                                    && visibleUserIds.Contains(link.LegalEntity.ResponsibleUserId.Value)))))))
            || (payment.Contract != null
                && (payment.Contract.ClientContracts.Any(link =>
                        link.Client.ResponsibleUserId.HasValue
                        && visibleUserIds.Contains(link.Client.ResponsibleUserId.Value))
                    || payment.Contract.LegalEntityContracts.Any(link =>
                        link.LegalEntity.ResponsibleUserId.HasValue
                        && visibleUserIds.Contains(link.LegalEntity.ResponsibleUserId.Value)))));
    }

    public static IQueryable<CashOrder> ApplyCashOrderVisibility(
        this IQueryable<CashOrder> query,
        DataVisibilityAccess? access)
    {
        if (access is null) return query.Where(_ => false);
        if (access.SeesAll) return query;

        var visibleUserIds = access.VisibleUserIds.ToArray();
        return query.Where(order =>
            (order.CreatedByUserId.HasValue && visibleUserIds.Contains(order.CreatedByUserId.Value))
            || (order.Client != null
                && order.Client.ResponsibleUserId.HasValue
                && visibleUserIds.Contains(order.Client.ResponsibleUserId.Value))
            || (order.Payment != null
                && (order.Payment.Responsibles.Any(link => visibleUserIds.Contains(link.UserId))
                    || (order.Payment.Client != null
                        && order.Payment.Client.ResponsibleUserId.HasValue
                        && visibleUserIds.Contains(order.Payment.Client.ResponsibleUserId.Value))
                    || (order.Payment.ExpenseClient != null
                        && order.Payment.ExpenseClient.ResponsibleUserId.HasValue
                        && visibleUserIds.Contains(order.Payment.ExpenseClient.ResponsibleUserId.Value))
                    || (order.Payment.LegalEntity != null
                        && order.Payment.LegalEntity.ResponsibleUserId.HasValue
                        && visibleUserIds.Contains(order.Payment.LegalEntity.ResponsibleUserId.Value))
                    || (order.Payment.ExpenseLegalEntity != null
                        && order.Payment.ExpenseLegalEntity.ResponsibleUserId.HasValue
                        && visibleUserIds.Contains(order.Payment.ExpenseLegalEntity.ResponsibleUserId.Value))
                    || (order.Payment.ClientCase != null
                        && order.Payment.ClientCase.Client != null
                        && order.Payment.ClientCase.Client.ResponsibleUserId.HasValue
                        && visibleUserIds.Contains(order.Payment.ClientCase.Client.ResponsibleUserId.Value))
                    || (order.Payment.ClientCase != null
                        && order.Payment.ClientCase.LegalEntity != null
                        && order.Payment.ClientCase.LegalEntity.ResponsibleUserId.HasValue
                        && visibleUserIds.Contains(order.Payment.ClientCase.LegalEntity.ResponsibleUserId.Value))
                    || order.Payment.InvoiceClientCases.Any(link =>
                        (link.ClientCase.Client != null
                            && link.ClientCase.Client.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(link.ClientCase.Client.ResponsibleUserId.Value))
                        || (link.ClientCase.LegalEntity != null
                            && link.ClientCase.LegalEntity.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(link.ClientCase.LegalEntity.ResponsibleUserId.Value)))
                    || (order.Payment.Contract != null
                        && (order.Payment.Contract.ClientContracts.Any(link =>
                                link.Client.ResponsibleUserId.HasValue
                                && visibleUserIds.Contains(link.Client.ResponsibleUserId.Value))
                            || order.Payment.Contract.LegalEntityContracts.Any(link =>
                                link.LegalEntity.ResponsibleUserId.HasValue
                                && visibleUserIds.Contains(link.LegalEntity.ResponsibleUserId.Value))))))
            || (order.Invoice != null
                && order.Invoice.Payments.Any(invoicePayment =>
                    invoicePayment.DocumentKind == DocumentKind.Invoice
                    && (invoicePayment.Responsibles.Any(link => visibleUserIds.Contains(link.UserId))
                        || (invoicePayment.Client != null
                            && invoicePayment.Client.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(invoicePayment.Client.ResponsibleUserId.Value))
                        || (invoicePayment.ExpenseClient != null
                            && invoicePayment.ExpenseClient.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(invoicePayment.ExpenseClient.ResponsibleUserId.Value))
                        || (invoicePayment.LegalEntity != null
                            && invoicePayment.LegalEntity.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(invoicePayment.LegalEntity.ResponsibleUserId.Value))
                        || (invoicePayment.ExpenseLegalEntity != null
                            && invoicePayment.ExpenseLegalEntity.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(invoicePayment.ExpenseLegalEntity.ResponsibleUserId.Value))
                        || (invoicePayment.ClientCase != null
                            && invoicePayment.ClientCase.Client != null
                            && invoicePayment.ClientCase.Client.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(invoicePayment.ClientCase.Client.ResponsibleUserId.Value))
                        || (invoicePayment.ClientCase != null
                            && invoicePayment.ClientCase.LegalEntity != null
                            && invoicePayment.ClientCase.LegalEntity.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(invoicePayment.ClientCase.LegalEntity.ResponsibleUserId.Value))
                        || invoicePayment.InvoiceClientCases.Any(link =>
                            (link.ClientCase.Client != null
                                && link.ClientCase.Client.ResponsibleUserId.HasValue
                                && visibleUserIds.Contains(link.ClientCase.Client.ResponsibleUserId.Value))
                            || (link.ClientCase.LegalEntity != null
                                && link.ClientCase.LegalEntity.ResponsibleUserId.HasValue
                                && visibleUserIds.Contains(link.ClientCase.LegalEntity.ResponsibleUserId.Value)))
                        || (invoicePayment.Contract != null
                            && (invoicePayment.Contract.ClientContracts.Any(link =>
                                    link.Client.ResponsibleUserId.HasValue
                                    && visibleUserIds.Contains(link.Client.ResponsibleUserId.Value))
                                || invoicePayment.Contract.LegalEntityContracts.Any(link =>
                                    link.LegalEntity.ResponsibleUserId.HasValue
                                    && visibleUserIds.Contains(link.LegalEntity.ResponsibleUserId.Value)))))))
            || (order.MoneyOperation != null
                && order.MoneyOperation.ResponsibleUserId.HasValue
                && visibleUserIds.Contains(order.MoneyOperation.ResponsibleUserId.Value))
            || (order.MoneyOperation != null
                && order.MoneyOperation.LinkedInvoice != null
                && order.MoneyOperation.LinkedInvoice.Payments.Any(invoicePayment =>
                    invoicePayment.DocumentKind == DocumentKind.Invoice
                    && (invoicePayment.Responsibles.Any(link => visibleUserIds.Contains(link.UserId))
                        || (invoicePayment.Client != null
                            && invoicePayment.Client.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(invoicePayment.Client.ResponsibleUserId.Value))
                        || (invoicePayment.ExpenseClient != null
                            && invoicePayment.ExpenseClient.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(invoicePayment.ExpenseClient.ResponsibleUserId.Value))
                        || (invoicePayment.LegalEntity != null
                            && invoicePayment.LegalEntity.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(invoicePayment.LegalEntity.ResponsibleUserId.Value))
                        || (invoicePayment.ExpenseLegalEntity != null
                            && invoicePayment.ExpenseLegalEntity.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(invoicePayment.ExpenseLegalEntity.ResponsibleUserId.Value))
                        || (invoicePayment.ClientCase != null
                            && invoicePayment.ClientCase.Client != null
                            && invoicePayment.ClientCase.Client.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(invoicePayment.ClientCase.Client.ResponsibleUserId.Value))
                        || (invoicePayment.ClientCase != null
                            && invoicePayment.ClientCase.LegalEntity != null
                            && invoicePayment.ClientCase.LegalEntity.ResponsibleUserId.HasValue
                            && visibleUserIds.Contains(invoicePayment.ClientCase.LegalEntity.ResponsibleUserId.Value))
                        || invoicePayment.InvoiceClientCases.Any(link =>
                            (link.ClientCase.Client != null
                                && link.ClientCase.Client.ResponsibleUserId.HasValue
                                && visibleUserIds.Contains(link.ClientCase.Client.ResponsibleUserId.Value))
                            || (link.ClientCase.LegalEntity != null
                                && link.ClientCase.LegalEntity.ResponsibleUserId.HasValue
                                && visibleUserIds.Contains(link.ClientCase.LegalEntity.ResponsibleUserId.Value)))
                        || (invoicePayment.Contract != null
                            && (invoicePayment.Contract.ClientContracts.Any(link =>
                                    link.Client.ResponsibleUserId.HasValue
                                    && visibleUserIds.Contains(link.Client.ResponsibleUserId.Value))
                                || invoicePayment.Contract.LegalEntityContracts.Any(link =>
                                    link.LegalEntity.ResponsibleUserId.HasValue
                                    && visibleUserIds.Contains(link.LegalEntity.ResponsibleUserId.Value))))))));
    }

    public static IQueryable<Act> ApplyActVisibility(
        this IQueryable<Act> query,
        DataVisibilityAccess? access)
    {
        if (access is null) return query.Where(_ => false);
        if (access.SeesAll) return query;

        var visibleUserIds = access.VisibleUserIds.ToArray();
        return query.Where(act =>
            (act.ResponsibleId.HasValue && visibleUserIds.Contains(act.ResponsibleId.Value))
            || (act.Client != null
                && act.Client.ResponsibleUserId.HasValue
                && visibleUserIds.Contains(act.Client.ResponsibleUserId.Value))
            || (act.LegalEntity != null
                && act.LegalEntity.ResponsibleUserId.HasValue
                && visibleUserIds.Contains(act.LegalEntity.ResponsibleUserId.Value))
            || (act.Invoice != null
                && (act.Invoice.Responsibles.Any(link => visibleUserIds.Contains(link.UserId))
                    || (act.Invoice.Client != null
                        && act.Invoice.Client.ResponsibleUserId.HasValue
                        && visibleUserIds.Contains(act.Invoice.Client.ResponsibleUserId.Value))
                    || (act.Invoice.LegalEntity != null
                        && act.Invoice.LegalEntity.ResponsibleUserId.HasValue
                        && visibleUserIds.Contains(act.Invoice.LegalEntity.ResponsibleUserId.Value)))));
    }

    public static IQueryable<Contract> ApplyContractVisibility(
        this IQueryable<Contract> query,
        DataVisibilityAccess? access)
    {
        if (access is null) return query.Where(_ => false);
        if (access.SeesAll) return query;

        var visibleUserIds = access.VisibleUserIds.ToArray();
        return query.Where(contract =>
            contract.ClientContracts.Any(link =>
                link.Client.ResponsibleUserId.HasValue
                && visibleUserIds.Contains(link.Client.ResponsibleUserId.Value))
            || contract.LegalEntityContracts.Any(link =>
                link.LegalEntity.ResponsibleUserId.HasValue
                && visibleUserIds.Contains(link.LegalEntity.ResponsibleUserId.Value))
            || contract.ContractInvoices.Any(link =>
                link.Invoice.Responsibles.Any(responsible => visibleUserIds.Contains(responsible.UserId))
                || (link.Invoice.Client != null
                    && link.Invoice.Client.ResponsibleUserId.HasValue
                    && visibleUserIds.Contains(link.Invoice.Client.ResponsibleUserId.Value))
                || (link.Invoice.LegalEntity != null
                    && link.Invoice.LegalEntity.ResponsibleUserId.HasValue
                    && visibleUserIds.Contains(link.Invoice.LegalEntity.ResponsibleUserId.Value)))
            || contract.ContractPayments.Any(link =>
                link.Payment.Responsibles.Any(responsible => visibleUserIds.Contains(responsible.UserId))
                || (link.Payment.Client != null
                    && link.Payment.Client.ResponsibleUserId.HasValue
                    && visibleUserIds.Contains(link.Payment.Client.ResponsibleUserId.Value))
                || (link.Payment.LegalEntity != null
                    && link.Payment.LegalEntity.ResponsibleUserId.HasValue
                    && visibleUserIds.Contains(link.Payment.LegalEntity.ResponsibleUserId.Value)))
            || contract.ContractActs.Any(link =>
                (link.Act.ResponsibleId.HasValue && visibleUserIds.Contains(link.Act.ResponsibleId.Value))
                || (link.Act.Client != null
                    && link.Act.Client.ResponsibleUserId.HasValue
                    && visibleUserIds.Contains(link.Act.Client.ResponsibleUserId.Value))
                || (link.Act.LegalEntity != null
                    && link.Act.LegalEntity.ResponsibleUserId.HasValue
                    && visibleUserIds.Contains(link.Act.LegalEntity.ResponsibleUserId.Value)))
            || contract.Payments.Any(payment =>
                payment.Responsibles.Any(responsible => visibleUserIds.Contains(responsible.UserId))
                || (payment.Client != null
                    && payment.Client.ResponsibleUserId.HasValue
                    && visibleUserIds.Contains(payment.Client.ResponsibleUserId.Value))
                || (payment.LegalEntity != null
                    && payment.LegalEntity.ResponsibleUserId.HasValue
                    && visibleUserIds.Contains(payment.LegalEntity.ResponsibleUserId.Value))));
    }
}
