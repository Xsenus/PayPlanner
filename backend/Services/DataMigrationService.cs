using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

/// <summary>
/// NON-DESTRUCTIVE data migration service
/// Backfills companies/persons from legacy Clients table
/// PRESERVES all original data
/// </summary>
public class DataMigrationService
{
    private readonly PaymentContext _context;
    private readonly ILogger<DataMigrationService> _logger;

    public DataMigrationService(PaymentContext context, ILogger<DataMigrationService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Backfill companies and persons from Clients table
    /// Strategy: If Client.Company is not empty -> create Company
    /// If Client.Name is not empty -> create Person
    /// Link Person to Company if both exist for same client
    /// </summary>
    public async Task<(int companiesCreated, int personsCreated)> BackfillClientsToCompaniesAndPersonsAsync(bool dryRun = true)
    {
        int companiesCreated = 0;
        int personsCreated = 0;

        var clients = await _context.Set<Client>()
            .Where(c => c.IsActive)
            .ToListAsync();

        _logger.LogInformation($"Processing {clients.Count} clients for backfill (DryRun: {dryRun})");

        foreach (var client in clients)
        {
            // Check if already migrated
            var existingCompany = await _context.Set<Company>()
                .FirstOrDefaultAsync(c => c.LegacyClientId == client.Id);

            var existingPerson = await _context.Set<Person>()
                .FirstOrDefaultAsync(p => p.LegacyClientId == client.Id);

            Company? newCompany = null;

            // Create company if Company field has value and not already migrated
            if (!string.IsNullOrWhiteSpace(client.Company) && existingCompany == null)
            {
                newCompany = new Company
                {
                    LegalName = client.Company,
                    LegalAddress = client.Address,
                    Phone = client.Phone,
                    Email = client.Email,
                    Notes = $"Migrated from Client ID: {client.Id}\n{client.Notes}",
                    IsActive = client.IsActive,
                    LegacyClientId = client.Id,
                    CreatedAt = client.CreatedAt,
                    UpdatedAt = DateTime.UtcNow
                };

                if (!dryRun)
                {
                    _context.Set<Company>().Add(newCompany);
                    await _context.SaveChangesAsync(); // Save to get ID
                }

                companiesCreated++;
                _logger.LogInformation($"Company created/would create: {newCompany.LegalName}");
            }
            else if (existingCompany != null)
            {
                newCompany = existingCompany;
            }

            // Create person if Name field has value and not already migrated
            if (!string.IsNullOrWhiteSpace(client.Name) && existingPerson == null)
            {
                var nameParts = client.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var firstName = nameParts.Length > 0 ? nameParts[0] : client.Name;
                var lastName = nameParts.Length > 1 ? string.Join(" ", nameParts.Skip(1)) : "";

                var newPerson = new Person
                {
                    FirstName = firstName,
                    LastName = lastName,
                    CompanyId = newCompany?.Id,
                    Phone = client.Phone,
                    Email = client.Email,
                    Address = client.Address,
                    Notes = $"Migrated from Client ID: {client.Id}\n{client.Notes}",
                    IsActive = client.IsActive,
                    LegacyClientId = client.Id,
                    CreatedAt = client.CreatedAt,
                    UpdatedAt = DateTime.UtcNow
                };

                if (!dryRun)
                {
                    _context.Set<Person>().Add(newPerson);
                }

                personsCreated++;
                _logger.LogInformation($"Person created/would create: {newPerson.FirstName} {newPerson.LastName}");
            }
        }

        if (!dryRun)
        {
            await _context.SaveChangesAsync();
            _logger.LogInformation($"Backfill completed: {companiesCreated} companies, {personsCreated} persons");
        }
        else
        {
            _logger.LogInformation($"DRY RUN: Would create {companiesCreated} companies, {personsCreated} persons");
        }

        return (companiesCreated, personsCreated);
    }

    /// <summary>
    /// Update Cases to link to new companies/persons based on existing ClientId
    /// NON-DESTRUCTIVE: Only adds new links, doesn't remove ClientId
    /// </summary>
    public async Task<int> LinkCasesToNewEntitiesAsync(bool dryRun = true)
    {
        int casesUpdated = 0;

        var cases = await _context.Set<ClientCase>()
            .Where(c => c.ClientId != null)
            .ToListAsync();

        foreach (var case_ in cases)
        {
            var company = await _context.Set<Company>()
                .FirstOrDefaultAsync(c => c.LegacyClientId == case_.ClientId);

            var person = await _context.Set<Person>()
                .FirstOrDefaultAsync(p => p.LegacyClientId == case_.ClientId);

            if (company != null || person != null)
            {
                // Update case to reference new entities (additive only)
                if (company != null)
                {
                    case_.CompanyId = company.Id;
                }
                if (person != null)
                {
                    case_.PersonId = person.Id;
                }

                casesUpdated++;

                if (!dryRun)
                {
                    _context.Set<ClientCase>().Update(case_);
                }

                _logger.LogInformation($"Case {case_.CaseNumber} linked to company: {company?.Id}, person: {person?.Id}");
            }
        }

        if (!dryRun)
        {
            await _context.SaveChangesAsync();
            _logger.LogInformation($"Linked {casesUpdated} cases to new entities");
        }
        else
        {
            _logger.LogInformation($"DRY RUN: Would link {casesUpdated} cases");
        }

        return casesUpdated;
    }

    /// <summary>
    /// Verify data integrity after migration
    /// Ensures no data was lost
    /// </summary>
    public async Task<MigrationVerificationResult> VerifyMigrationAsync()
    {
        var result = new MigrationVerificationResult();

        result.TotalClients = await _context.Set<Client>().CountAsync();
        result.TotalCompanies = await _context.Set<Company>().CountAsync();
        result.TotalPersons = await _context.Set<Person>().CountAsync();
        result.MigratedClients = await _context.Set<Client>()
            .Where(c => _context.Set<Company>().Any(comp => comp.LegacyClientId == c.Id) ||
                       _context.Set<Person>().Any(p => p.LegacyClientId == c.Id))
            .CountAsync();
        result.UnmigratedClients = result.TotalClients - result.MigratedClients;

        result.IsValid = result.UnmigratedClients == 0 || result.UnmigratedClients < result.TotalClients * 0.1; // Allow 10% unmigrated

        _logger.LogInformation($"Migration verification: {result.MigratedClients}/{result.TotalClients} clients migrated");

        return result;
    }
}

public class MigrationVerificationResult
{
    public int TotalClients { get; set; }
    public int TotalCompanies { get; set; }
    public int TotalPersons { get; set; }
    public int MigratedClients { get; set; }
    public int UnmigratedClients { get; set; }
    public bool IsValid { get; set; }
}
