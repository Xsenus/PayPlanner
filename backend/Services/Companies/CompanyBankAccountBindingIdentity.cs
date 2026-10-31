using System.Security.Cryptography;
using System.Text;

namespace PayPlanner.Api.Services.Companies;

public static class CompanyBankAccountBindingIdentity
{
    public static string ProfileKey(string profileAccountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileAccountId);
        if (profileAccountId.Length > CompanyBankAccountList.MaxJsonLength)
            throw new ArgumentException("Идентификатор счёта превышает допустимый размер.", nameof(profileAccountId));
        // Ordinal stable identity: do not change case, shorten or renumber old IDs.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profileAccountId)));
    }
}
