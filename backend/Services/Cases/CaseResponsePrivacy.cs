using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Cases;

internal static class CaseResponsePrivacy
{
    public static void ApplyToRead(IEnumerable<ClientCase> cases, bool canRevealSensitiveData, bool canViewFinance)
    {
        foreach (var item in cases)
        {
            KeepResponsibleCaption(item);
            item.CanRevealSensitiveData = canRevealSensitiveData;
            if (!canRevealSensitiveData) HideSensitiveData(item);
            if (!canViewFinance) HideFinanceData(item);
        }
    }

    public static void KeepResponsibleCaption(ClientCase item)
    {
        item.ResponsibleUser = Caption(item.ResponsibleUser);
        item.ControlMarkedByUser = Caption(item.ControlMarkedByUser);
        if (item.LegalEntity is { } legal) legal.ResponsibleUser = Caption(legal.ResponsibleUser);
        if (item.Client?.LegalEntity is { } clientLegal) clientLegal.ResponsibleUser = Caption(clientLegal.ResponsibleUser);
    }

    private static User? Caption(User? user) => user is null ? null : new User { Id = user.Id, FullName = user.FullName, Email = user.Email };

    public static void HideSensitiveData(ClientCase item)
    {
        if (item.Client is { } client)
        {
            client.Phone = string.Empty;
            client.Email = string.Empty;
            client.Address = string.Empty;
            client.Notes = string.Empty;
            if (client.LegalEntity is { } clientLegal) HideLegalEntitySensitiveData(clientLegal);
        }
        if (item.LegalEntity is { } legal) HideLegalEntitySensitiveData(legal);
    }

    private static void HideLegalEntitySensitiveData(LegalEntity legal)
    {
        legal.Inn = null;
        legal.Kpp = null;
        legal.Ogrn = null;
        legal.Address = null;
        legal.Phone = null;
        legal.Email = null;
        legal.Director = null;
        legal.Notes = null;
    }

    public static void HideFinanceData(IEnumerable<ClientCase> cases)
    {
        foreach (var item in cases) HideFinanceData(item);
    }

    private static void HideFinanceData(ClientCase item)
    {
        item.FeeAmount = 0m;
        item.IncomeTotal = 0m;
        item.IncomePaid = 0m;
        item.IncomeOutstanding = 0m;
        item.IncomeCount = 0;
        item.ExpenseTotal = 0m;
        item.ExpensePaid = 0m;
        item.ExpenseOutstanding = 0m;
        item.ExpenseCount = 0;
        item.Payments = new List<Payment>();
        item.InvoiceClientCases = new List<InvoiceClientCase>();
    }
}
