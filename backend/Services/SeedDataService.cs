using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.ReferenceData;
using System.Data;
using System.Data.Common;

namespace PayPlanner.Api.Services
{
    /// <summary>
    /// РџРµСЂРІРёС‡РЅРѕРµ РЅР°РїРѕР»РЅРµРЅРёРµ Р‘Р” С‚РµСЃС‚РѕРІС‹РјРё РґР°РЅРЅС‹РјРё: СЂРѕР»Рё, Р°РґРјРёРЅ, РєР»РёРµРЅС‚С‹, РґРµР»Р°, СЃР»РѕРІР°СЂРё Рё РїР»Р°С‚РµР¶Рё.
    /// </summary>
    public static class SeedDataService
    {
        private sealed class PaymentSeedDefinition
        {
            public string Email { get; init; } = string.Empty;
            public string CaseTitle { get; init; } = string.Empty;
            public DateTime Date { get; init; }
            public DateTime? AccountDate { get; init; }
            public string? Account { get; init; }
            public decimal Amount { get; init; }
            public PaymentType Type { get; init; }
            public PaymentStatus Status { get; init; }
            public string Description { get; init; } = string.Empty;
            public bool IsPaid { get; init; }
            public DateTime? PaidDate { get; init; }
            public string DealType { get; init; } = string.Empty;
            public string IncomeType { get; init; } = string.Empty;
            public string Source { get; init; } = string.Empty;
            public string StatusName { get; init; } = string.Empty;
            public string? ActReference { get; init; }
        }

        /// <summary>
        /// Р’С‹РїРѕР»РЅРёС‚СЊ СЃРёРґС‹.
        /// </summary>
        /// <param name="context">DbContext</param>
        /// <param name="seedClientsAndPayments">
        /// Р•СЃР»Рё true вЂ” РґРѕР±Р°РІРёС‚СЊ РєР»РёРµРЅС‚РѕРІ, РґРµР»Р° Рё РїР»Р°С‚РµР¶Рё (РґР°Р¶Рµ РµСЃР»Рё С‚Р°Р±Р»РёС†Р° РЅРµ РїСѓСЃС‚Р°СЏ).
        /// Р•СЃР»Рё false вЂ” РєР»РёРµРЅС‚С‹/РґРµР»Р°/РїР»Р°С‚РµР¶Рё СЃРёРґСЏС‚СЃСЏ С‚РѕР»СЊРєРѕ РєРѕРіРґР° Clients РїСѓСЃС‚.
        /// </param>
        public static async Task SeedAsync(PaymentContext context, bool seedClientsAndPayments = false)
        {
            await SeedDictionariesAsync(context);          // СЃР»РѕРІР°СЂРё
            await SeedRolesAsync(context);                 // СЂРѕР»Рё (admin, user)
            await SeedRolePermissionsAsync(context);       // РїСЂР°РІР° РїРѕ СЂРѕР»СЏРј
            await SeedAdminUserAsync(context);             // Р°РґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂ

            // Р’РѕР·РјРѕР¶РЅРѕСЃС‚СЊ РѕС‚РєР»СЋС‡РёС‚СЊ РєР»РёРµРЅС‚СЃРєРёРµ СЃРёРґС‹ РЅР° РїСЂРѕРґРµ:
            var skipClientsByEnv =
                string.Equals(Environment.GetEnvironmentVariable("PAYPLANNER_SKIP_CLIENT_SEED"), "1",
                    StringComparison.OrdinalIgnoreCase);

            var shouldSeedClients =
                !skipClientsByEnv && (seedClientsAndPayments || !await context.Clients.AnyAsync());

            if (shouldSeedClients)
            {
                try
                {
                    await SeedClientsCasesPaymentsAsync(context);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SEED][Clients] skipped due to error: {ex.GetType().Name}: {ex.Message}");
                    // РќРµ РІР°Р»РёРј РїСЂРѕС†РµСЃСЃ вЂ” РїСЂРѕСЃС‚Рѕ РїСЂРѕРїСѓСЃС‚РёРј РєР»РёРµРЅС‚СЃРєСѓСЋ С‡Р°СЃС‚СЊ
                }
            }
        }

        /// <summary>
        /// РЎР»РѕРІР°СЂРё (deal types, income types, sources, statuses).
        /// </summary>
        private static async Task SeedDictionariesAsync(PaymentContext context)
        {
            await EnsureIncomeTypeSchemaAsync(context);
            await EnsureServiceDictionarySchemaAsync(context);
            await EnsureLegalEntityProfileDictionarySchemaAsync(context);
            await EnsureInvoiceLineDictionarySchemaAsync(context);

            try
            {
                var payments = await context.Payments
                    .Where(w => w.IsPaid && w.PaidAmount == 0 && w.LastPaymentDate == null)
                    .ToListAsync();

                if (payments.Count > 0)
                {
                    foreach (var payment in payments)
                    {
                        payment.PlannedDate = payment.PaidDate?.Date ?? default;
                        payment.LastPaymentDate = payment.PaidDate;
                        payment.PaidAmount = payment.Amount;
                    }
                    await context.SaveChangesAsync();
                }
            }
            catch (Exception) { }

            if (!await context.DealTypes.AnyAsync())
            {
                var dealTypes = new[]
                {
                    new DealType { Name = "РљРѕРЅСЃР°Р»С‚РёРЅРі",      Description = "РџСЂРѕС„РµСЃСЃРёРѕРЅР°Р»СЊРЅС‹Рµ РєРѕРЅСЃСѓР»СЊС‚Р°С†РёРѕРЅРЅС‹Рµ СѓСЃР»СѓРіРё", ColorHex = "#3B82F6" },
                    new DealType { Name = "РџСЂРѕРґР°Р¶Р° С‚РѕРІР°СЂР°",   Description = "РџСЂСЏРјР°СЏ РїСЂРѕРґР°Р¶Р° РїСЂРѕРґСѓРєС†РёРё",                ColorHex = "#10B981" },
                    new DealType { Name = "РџРѕРґРїРёСЃРєР°",         Description = "Р РµРіСѓР»СЏСЂРЅС‹Рµ РїРѕРґРїРёСЃРЅС‹Рµ СѓСЃР»СѓРіРё",             ColorHex = "#8B5CF6" },
                    new DealType { Name = "РџСЂРѕРµРєС‚",           Description = "Р Р°Р±РѕС‚Р° РїРѕ РїСЂРѕРµРєС‚Сѓ СЃ С„РёРєСЃРёСЂРѕРІР°РЅРЅС‹Рј РѕР±СЉС‘РјРѕРј", ColorHex = "#F59E0B" },
                    new DealType { Name = "РћР±СЃР»СѓР¶РёРІР°РЅРёРµ",     Description = "Р”РѕР»РіРѕСЃСЂРѕС‡РЅС‹Рµ РєРѕРЅС‚СЂР°РєС‚С‹ РЅР° РѕР±СЃР»СѓР¶РёРІР°РЅРёРµ",  ColorHex = "#EF4444" }
                };
                context.DealTypes.AddRange(dealTypes);
                await context.SaveChangesAsync();
            }

            if (!await context.IncomeTypes.AnyAsync())
            {
                if (!await context.IncomeTypes.Where(w => w.PaymentType == PaymentType.Income).AnyAsync())
                {
                    var incomeTypes = new[]
                    {
                        new IncomeType { Name = "Р”РѕС…РѕРґ РѕС‚ СѓСЃР»СѓРі",  Description = "Р”РѕС…РѕРґ РѕС‚ РѕРєР°Р·Р°РЅРёСЏ СѓСЃР»СѓРі",       ColorHex = "#10B981", PaymentType = PaymentType.Income },
                        new IncomeType { Name = "РџСЂРѕС‡РёРµ РґРѕС…РѕРґС‹",    Description = "РџСЂРѕС‡РёРµ РґРѕС…РѕРґС‹",                ColorHex = "#064E3B", PaymentType = PaymentType.Income },
                    };
                    context.IncomeTypes.AddRange(incomeTypes);
                    await context.SaveChangesAsync();
                }

                if (!await context.IncomeTypes.Where(w => w.PaymentType == PaymentType.Expense).AnyAsync())
                {
                    var expenseTypes = new[]
                    {
                    new IncomeType { Name = "РџСЂРѕС‡РёРµ СЂР°СЃС…РѕРґС‹", Description = "РРЅС‹Рµ СЂР°СЃС…РѕРґС‹", ColorHex = "#991B1B", PaymentType = PaymentType.Expense },
                };
                    context.IncomeTypes.AddRange(expenseTypes);
                    await context.SaveChangesAsync();
                }
            }                

            if (!await context.PaymentSources.AnyAsync())
            {
                var paymentSources = new[]
                {
                    new PaymentSource { Name = "Р‘Р°РЅРєРѕРІСЃРєРёР№ РїРµСЂРµРІРѕРґ", Description = "РџСЂСЏРјРѕР№ Р±Р°РЅРєРѕРІСЃРєРёР№ РїРµСЂРµРІРѕРґ", ColorHex = "#6B7280" },
                    new PaymentSource { Name = "Р‘Р°РЅРєРѕРІСЃРєР°СЏ РєР°СЂС‚Р°",   Description = "РћРїР»Р°С‚Р° Р±Р°РЅРєРѕРІСЃРєРѕР№ РєР°СЂС‚РѕР№",  ColorHex = "#4B5563" },
                    new PaymentSource { Name = "PayPal",             Description = "РћРїР»Р°С‚Р° С‡РµСЂРµР· PayPal",       ColorHex = "#374151" },
                    new PaymentSource { Name = "Р§РµРє",                Description = "РћРїР»Р°С‚Р° Р±Р°РЅРєРѕРІСЃРєРёРј С‡РµРєРѕРј",   ColorHex = "#1F2937" },
                    new PaymentSource { Name = "РќР°Р»РёС‡РЅС‹Рµ",           Description = "РћРїР»Р°С‚Р° РЅР°Р»РёС‡РЅС‹РјРё",          ColorHex = "#111827" }
                };
                context.PaymentSources.AddRange(paymentSources);
                await context.SaveChangesAsync();
            }

            if (!await context.PaymentStatuses.AnyAsync())
            {
                var paymentStatuses = new[]
                {
                    new PaymentStatusEntity { Name = "РћР¶РёРґР°РµС‚СЃСЏ",  Description = "РћРїР»Р°С‚Р° РЅР°С…РѕРґРёС‚СЃСЏ РІ РѕР¶РёРґР°РЅРёРё",  ColorHex = "#F59E0B" },
                    new PaymentStatusEntity { Name = "Р’С‹РїРѕР»РЅРµРЅРѕ",  Description = "РћРїР»Р°С‚Р° СѓСЃРїРµС€РЅРѕ Р·Р°РІРµСЂС€РµРЅР°",    ColorHex = "#10B981" },
                    new PaymentStatusEntity { Name = "РџСЂРѕСЃСЂРѕС‡РµРЅРѕ", Description = "РЎСЂРѕРє РѕРїР»Р°С‚С‹ РёСЃС‚С‘Рє",           ColorHex = "#EF4444" }
                };
                context.PaymentStatuses.AddRange(paymentStatuses);
                await context.SaveChangesAsync();
            }

            if (!await context.ClientStatuses.AnyAsync())
            {
                var clientStatuses = new[]
                {
                    new ClientStatusEntity { Name = "РљР»РёРµРЅС‚", Description = "РћСЃРЅРѕРІРЅРѕР№ Р·Р°РєР°Р·С‡РёРє", ColorHex = "#2563EB" },
                    new ClientStatusEntity { Name = "РџРѕСЃС‚Р°РІС‰РёРє", Description = "РџРѕСЃС‚Р°РІС‰РёРє СѓСЃР»СѓРі РёР»Рё С‚РѕРІР°СЂРѕРІ", ColorHex = "#7C3AED" },
                    new ClientStatusEntity { Name = "РљСЂРµРґРёС‚РѕСЂ", Description = "РљРѕРЅС‚СЂР°РіРµРЅС‚ СЃ РєСЂРµРґРёС‚РЅС‹РјРё РѕР±СЏР·Р°С‚РµР»СЊСЃС‚РІР°РјРё", ColorHex = "#DC2626" }
                };
                context.ClientStatuses.AddRange(clientStatuses);
                await context.SaveChangesAsync();
            }


            await CountryDictionaryCatalog.EnsureSeededAsync(context);

            if (!await context.BaseCurrencies.AnyAsync())
            {
                context.BaseCurrencies.AddRange(
                    new BaseCurrencyDictionaryItem { Name = "RUB", Description = "Р РѕСЃСЃРёР№СЃРєРёР№ СЂСѓР±Р»СЊ", ColorHex = "#16A34A" },
                    new BaseCurrencyDictionaryItem { Name = "USD", Description = "Р”РѕР»Р»Р°СЂ РЎРЁРђ", ColorHex = "#059669" },
                    new BaseCurrencyDictionaryItem { Name = "EUR", Description = "Р•РІСЂРѕ", ColorHex = "#15803D" }
                );
                await context.SaveChangesAsync();
            }

            if (!await context.ServiceLanguages.AnyAsync())
            {
                context.ServiceLanguages.AddRange(
                    new ServiceLanguageDictionaryItem { Name = "Р СѓСЃСЃРєРёР№", Description = "Р СѓСЃСЃРєРёР№ СЏР·С‹Рє", ColorHex = "#9333EA" },
                    new ServiceLanguageDictionaryItem { Name = "English", Description = "English language", ColorHex = "#7E22CE" }
                );
                await context.SaveChangesAsync();
            }

            if (!await context.OrganizationForms.AnyAsync())
            {
                context.OrganizationForms.AddRange(
                    new OrganizationFormDictionaryItem { Name = "РћРћРћ", Description = "РћР±С‰РµСЃС‚РІРѕ СЃ РѕРіСЂР°РЅРёС‡РµРЅРЅРѕР№ РѕС‚РІРµС‚СЃС‚РІРµРЅРЅРѕСЃС‚СЊСЋ", ColorHex = "#F59E0B" },
                    new OrganizationFormDictionaryItem { Name = "РРџ", Description = "РРЅРґРёРІРёРґСѓР°Р»СЊРЅС‹Р№ РїСЂРµРґРїСЂРёРЅРёРјР°С‚РµР»СЊ", ColorHex = "#D97706" },
                    new OrganizationFormDictionaryItem { Name = "РђРћ", Description = "РђРєС†РёРѕРЅРµСЂРЅРѕРµ РѕР±С‰РµСЃС‚РІРѕ", ColorHex = "#B45309" }
                );
                await context.SaveChangesAsync();
            }
            await TimezoneDictionaryCatalog.EnsureSeededAsync(context);

            if (!await context.TaxSystemModes.AnyAsync())
            {
                context.TaxSystemModes.AddRange(
                    new TaxSystemModeDictionaryItem { Name = "Р”РѕС…РѕРґС‹", Description = "РЈРЎРќ: РґРѕС…РѕРґС‹", ColorHex = "#0891B2" },
                    new TaxSystemModeDictionaryItem { Name = "Р”РѕС…РѕРґС‹ РјРёРЅСѓСЃ СЂР°СЃС…РѕРґС‹", Description = "РЈРЎРќ: РґРѕС…РѕРґС‹-СЂР°СЃС…РѕРґС‹", ColorHex = "#0E7490" },
                    new TaxSystemModeDictionaryItem { Name = "Р‘РµР· РќР”РЎ", Description = "Р РµР¶РёРј Р±РµР· РќР”РЎ", ColorHex = "#155E75" }
                );
                await context.SaveChangesAsync();
            }

            if (!await context.Services.AnyAsync())
            {
                context.Services.AddRange(
                    new ServiceDictionaryItem { Name = "Р®СЂРёРґРёС‡РµСЃРєР°СЏ РєРѕРЅСЃСѓР»СЊС‚Р°С†РёСЏ", Description = "Р Р°Р·РѕРІР°СЏ РєРѕРЅСЃСѓР»СЊС‚Р°С†РёСЏ", ColorHex = "#0EA5E9" },
                    new ServiceDictionaryItem { Name = "РџРѕРґРіРѕС‚РѕРІРєР° РґРѕРєСѓРјРµРЅС‚РѕРІ", Description = "РЎРѕСЃС‚Р°РІР»РµРЅРёРµ РґРѕРіРѕРІРѕСЂРѕРІ Рё Р·Р°СЏРІР»РµРЅРёР№", ColorHex = "#0284C7" },
                    new ServiceDictionaryItem { Name = "РЎРѕРїСЂРѕРІРѕР¶РґРµРЅРёРµ СЃРґРµР»РєРё", Description = "РљРѕРјРїР»РµРєСЃРЅРѕРµ СЃРѕРїСЂРѕРІРѕР¶РґРµРЅРёРµ", ColorHex = "#0369A1" }
                );
                await context.SaveChangesAsync();
            }

            if (!await context.InvoiceUnits.AnyAsync())
            {
                context.InvoiceUnits.AddRange(
                    new InvoiceUnitDictionaryItem { Name = "СѓСЃР». РµРґ.", Description = "РЈСЃР»РѕРІРЅР°СЏ РµРґРёРЅРёС†Р°", ColorHex = "#475569" },
                    new InvoiceUnitDictionaryItem { Name = "С€С‚", Description = "РЁС‚СѓРєРё", ColorHex = "#334155" },
                    new InvoiceUnitDictionaryItem { Name = "С‡Р°СЃ", Description = "Р§Р°СЃС‹", ColorHex = "#1E293B" },
                    new InvoiceUnitDictionaryItem { Name = "РґРµРЅСЊ", Description = "Р”РЅРё", ColorHex = "#0F172A" },
                    new InvoiceUnitDictionaryItem { Name = "РјРµСЃ", Description = "РњРµСЃСЏС†С‹", ColorHex = "#64748B" },
                    new InvoiceUnitDictionaryItem { Name = "РєРі", Description = "РљРёР»РѕРіСЂР°РјРјС‹", ColorHex = "#475569" },
                    new InvoiceUnitDictionaryItem { Name = "Р»", Description = "Р›РёС‚СЂС‹", ColorHex = "#334155" },
                    new InvoiceUnitDictionaryItem { Name = "Рј", Description = "РњРµС‚СЂС‹", ColorHex = "#1E293B" },
                    new InvoiceUnitDictionaryItem { Name = "РјВІ", Description = "РљРІР°РґСЂР°С‚РЅС‹Рµ РјРµС‚СЂС‹", ColorHex = "#0F172A" },
                    new InvoiceUnitDictionaryItem { Name = "РјВі", Description = "РљСѓР±РёС‡РµСЃРєРёРµ РјРµС‚СЂС‹", ColorHex = "#64748B" }
                );
                await context.SaveChangesAsync();
            }

            await EnsureStarterInvoiceUnitsAsync(context);
            await EnsureStarterServiceCatalogAsync(context);

            if (!await context.InvoiceItemNames.AnyAsync())
            {
                context.InvoiceItemNames.AddRange(
                    new InvoiceItemNameDictionaryItem { Name = "Р®СЂРёРґРёС‡РµСЃРєР°СЏ РєРѕРЅСЃСѓР»СЊС‚Р°С†РёСЏ", Unit = "СѓСЃР». РµРґ.", DefaultPrice = 5000m, Description = "Р Р°Р·РѕРІР°СЏ РєРѕРЅСЃСѓР»СЊС‚Р°С†РёСЏ", ColorHex = "#334155" },
                    new InvoiceItemNameDictionaryItem { Name = "РЎРѕСЃС‚Р°РІР»РµРЅРёРµ РґРѕРєСѓРјРµРЅС‚РѕРІ", Unit = "СѓСЃР». РµРґ.", DefaultPrice = 12000m, Description = "РџРѕРґРіРѕС‚РѕРІРєР° РїСЂРѕС†РµСЃСЃСѓР°Р»СЊРЅС‹С… Рё РґРѕРіРѕРІРѕСЂРЅС‹С… РґРѕРєСѓРјРµРЅС‚РѕРІ", ColorHex = "#1E293B" },
                    new InvoiceItemNameDictionaryItem { Name = "РџСЂРµРґСЃС‚Р°РІРёС‚РµР»СЊСЃС‚РІРѕ РІ СЃСѓРґРµ", Unit = "РґРµРЅСЊ", DefaultPrice = 15000m, Description = "РЈС‡Р°СЃС‚РёРµ РІ СЃСѓРґРµР±РЅС‹С… Р·Р°СЃРµРґР°РЅРёСЏС…", ColorHex = "#0F172A" },
                    new InvoiceItemNameDictionaryItem { Name = "РђР±РѕРЅРµРЅС‚СЃРєРѕРµ РѕР±СЃР»СѓР¶РёРІР°РЅРёРµ", Unit = "РјРµСЃ", DefaultPrice = 30000m, Description = "Р РµРіСѓР»СЏСЂРЅРѕРµ СЋСЂРёРґРёС‡РµСЃРєРѕРµ СЃРѕРїСЂРѕРІРѕР¶РґРµРЅРёРµ", ColorHex = "#475569" },
                    new InvoiceItemNameDictionaryItem { Name = "РђСѓРґРёС‚ РґРѕРєСѓРјРµРЅС‚РѕРІ", Unit = "СѓСЃР». РµРґ.", DefaultPrice = 8000m, Description = "РџСЂРѕРІРµСЂРєР° Рё Р°РЅР°Р»РёР· РґРѕРєСѓРјРµРЅС‚Р°С†РёРё", ColorHex = "#64748B" },
                    new InvoiceItemNameDictionaryItem { Name = "РџРѕРґРіРѕС‚РѕРІРєР° РїСЂРµС‚РµРЅР·РёРё", Unit = "СѓСЃР». РµРґ.", DefaultPrice = 7000m, Description = "Р”РѕСЃСѓРґРµР±РЅР°СЏ РїСЂРµС‚РµРЅР·РёРѕРЅРЅР°СЏ СЂР°Р±РѕС‚Р°", ColorHex = "#334155" },
                    new InvoiceItemNameDictionaryItem { Name = "РЎРѕРіР»Р°СЃРѕРІР°РЅРёРµ РґРѕРіРѕРІРѕСЂР°", Unit = "СѓСЃР». РµРґ.", DefaultPrice = 10000m, Description = "РџСЂР°РІРєРё Рё СЃРѕРіР»Р°СЃРѕРІР°РЅРёРµ СѓСЃР»РѕРІРёР№ РґРѕРіРѕРІРѕСЂР°", ColorHex = "#1E293B" },
                    new InvoiceItemNameDictionaryItem { Name = "Р’С‹РµР·Рґ СЃРїРµС†РёР°Р»РёСЃС‚Р°", Unit = "С‡Р°СЃ", DefaultPrice = 4000m, Description = "Р Р°Р±РѕС‚Р° СЃРїРµС†РёР°Р»РёСЃС‚Р° РЅР° РІС‹РµР·РґРµ", ColorHex = "#0F172A" },
                    new InvoiceItemNameDictionaryItem { Name = "РђРЅР°Р»РёС‚РёС‡РµСЃРєР°СЏ СЃРїСЂР°РІРєР°", Unit = "СѓСЃР». РµРґ.", DefaultPrice = 9000m, Description = "РџРѕРґРіРѕС‚РѕРІРєР° РїРёСЃСЊРјРµРЅРЅРѕР№ Р°РЅР°Р»РёС‚РёРєРё", ColorHex = "#475569" },
                    new InvoiceItemNameDictionaryItem { Name = "РџРѕРґРіРѕС‚РѕРІРєР° РѕС‚С‡РµС‚Р°", Unit = "СѓСЃР». РµРґ.", DefaultPrice = 11000m, Description = "РџРѕРґРіРѕС‚РѕРІРєР° РёС‚РѕРіРѕРІРѕРіРѕ РѕС‚С‡РµС‚Р°", ColorHex = "#64748B" }
                );
                await context.SaveChangesAsync();
            }
        }

        private static async Task EnsureServiceDictionarySchemaAsync(PaymentContext context, CancellationToken cancellationToken = default)
        {
            if (!context.Database.IsSqlite())
            {
                return;
            }

            var connection = context.Database.GetDbConnection();
            var shouldClose = connection.State != ConnectionState.Open;

            if (shouldClose)
            {
                await connection.OpenAsync(cancellationToken);
            }

            try
            {
                await ExecuteNonQueryAsync(connection, "CREATE TABLE IF NOT EXISTS \"ServiceDictionaryItems\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_ServiceDictionaryItems\" PRIMARY KEY AUTOINCREMENT, \"Name\" TEXT NOT NULL, \"Description\" TEXT NOT NULL, \"ColorHex\" TEXT NOT NULL, \"IsActive\" INTEGER NOT NULL, \"CreatedAt\" TEXT NOT NULL);", cancellationToken);
                await ExecuteNonQueryAsync(connection, "CREATE INDEX IF NOT EXISTS \"IX_ServiceDictionaryItems_IsActive\" ON \"ServiceDictionaryItems\" (\"IsActive\");", cancellationToken);
                await ExecuteNonQueryAsync(connection, "CREATE INDEX IF NOT EXISTS \"IX_ServiceDictionaryItems_Name\" ON \"ServiceDictionaryItems\" (\"Name\");", cancellationToken);
                await ExecuteNonQueryAsync(connection, "CREATE INDEX IF NOT EXISTS \"IX_ServiceDictionaryItems_IsActive_Name\" ON \"ServiceDictionaryItems\" (\"IsActive\", \"Name\");", cancellationToken);

                var hasServiceId = false;
                await using (var checkCommand = connection.CreateCommand())
                {
                    checkCommand.CommandText = "PRAGMA table_info(\"Payments\");";
                    await using var reader = await checkCommand.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        if (string.Equals(reader.GetString(1), "ServiceId", StringComparison.OrdinalIgnoreCase))
                        {
                            hasServiceId = true;
                            break;
                        }
                    }
                }

                if (!hasServiceId)
                {
                    await ExecuteNonQueryAsync(connection, "ALTER TABLE \"Payments\" ADD COLUMN \"ServiceId\" INTEGER NULL;", cancellationToken);
                }

                await ExecuteNonQueryAsync(connection, "CREATE INDEX IF NOT EXISTS \"IX_Payments_ServiceId\" ON \"Payments\" (\"ServiceId\");", cancellationToken);
            }
            finally
            {
                if (shouldClose && connection.State == ConnectionState.Open)
                {
                    await connection.CloseAsync();
                }
            }
        }


        private static async Task EnsureLegalEntityProfileDictionarySchemaAsync(PaymentContext context, CancellationToken cancellationToken = default)
        {
            if (!context.Database.IsSqlite())
            {
                return;
            }

            var connection = context.Database.GetDbConnection();
            var shouldClose = connection.State != ConnectionState.Open;

            if (shouldClose)
            {
                await connection.OpenAsync(cancellationToken);
            }

            try
            {
                await ExecuteNonQueryAsync(connection, "CREATE TABLE IF NOT EXISTS \"CountryDictionaryItems\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_CountryDictionaryItems\" PRIMARY KEY AUTOINCREMENT, \"Name\" TEXT NOT NULL, \"Description\" TEXT NOT NULL, \"ColorHex\" TEXT NOT NULL, \"IsActive\" INTEGER NOT NULL, \"CreatedAt\" TEXT NOT NULL);", cancellationToken);
                await ExecuteNonQueryAsync(connection, "CREATE TABLE IF NOT EXISTS \"BaseCurrencyDictionaryItems\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_BaseCurrencyDictionaryItems\" PRIMARY KEY AUTOINCREMENT, \"Name\" TEXT NOT NULL, \"Description\" TEXT NOT NULL, \"ColorHex\" TEXT NOT NULL, \"IsActive\" INTEGER NOT NULL, \"CreatedAt\" TEXT NOT NULL);", cancellationToken);
                await ExecuteNonQueryAsync(connection, "CREATE TABLE IF NOT EXISTS \"ServiceLanguageDictionaryItems\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_ServiceLanguageDictionaryItems\" PRIMARY KEY AUTOINCREMENT, \"Name\" TEXT NOT NULL, \"Description\" TEXT NOT NULL, \"ColorHex\" TEXT NOT NULL, \"IsActive\" INTEGER NOT NULL, \"CreatedAt\" TEXT NOT NULL);", cancellationToken);
                await ExecuteNonQueryAsync(connection, "CREATE TABLE IF NOT EXISTS \"OrganizationFormDictionaryItems\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_OrganizationFormDictionaryItems\" PRIMARY KEY AUTOINCREMENT, \"Name\" TEXT NOT NULL, \"Description\" TEXT NOT NULL, \"ColorHex\" TEXT NOT NULL, \"IsActive\" INTEGER NOT NULL, \"CreatedAt\" TEXT NOT NULL);", cancellationToken);
                await ExecuteNonQueryAsync(connection, "CREATE TABLE IF NOT EXISTS \"TimezoneDictionaryItems\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_TimezoneDictionaryItems\" PRIMARY KEY AUTOINCREMENT, \"Name\" TEXT NOT NULL, \"Description\" TEXT NOT NULL, \"ColorHex\" TEXT NOT NULL, \"IsActive\" INTEGER NOT NULL, \"CreatedAt\" TEXT NOT NULL);", cancellationToken);
            }
            finally
            {
                if (shouldClose && connection.State == ConnectionState.Open)
                {
                    await connection.CloseAsync();
                }
            }
        }


        private static async Task EnsureInvoiceLineDictionarySchemaAsync(PaymentContext context, CancellationToken cancellationToken = default)
        {
            if (!context.Database.IsSqlite())
            {
                return;
            }

            var connection = context.Database.GetDbConnection();
            var shouldClose = connection.State != ConnectionState.Open;

            if (shouldClose)
            {
                await connection.OpenAsync(cancellationToken);
            }

            try
            {
                await ExecuteNonQueryAsync(connection, "CREATE TABLE IF NOT EXISTS \"InvoiceUnits\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_InvoiceUnits\" PRIMARY KEY AUTOINCREMENT, \"Name\" TEXT NOT NULL, \"Description\" TEXT NOT NULL, \"ColorHex\" TEXT NOT NULL, \"IsActive\" INTEGER NOT NULL, \"CreatedAt\" TEXT NOT NULL);", cancellationToken);
                await ExecuteNonQueryAsync(connection, "CREATE INDEX IF NOT EXISTS \"IX_InvoiceUnitDictionaryItems_IsActive\" ON \"InvoiceUnits\" (\"IsActive\");", cancellationToken);
                await ExecuteNonQueryAsync(connection, "CREATE INDEX IF NOT EXISTS \"IX_InvoiceUnitDictionaryItems_Name\" ON \"InvoiceUnits\" (\"Name\");", cancellationToken);
                await ExecuteNonQueryAsync(connection, "CREATE INDEX IF NOT EXISTS \"IX_InvoiceUnitDictionaryItems_IsActive_Name\" ON \"InvoiceUnits\" (\"IsActive\", \"Name\");", cancellationToken);

                await ExecuteNonQueryAsync(connection, "CREATE TABLE IF NOT EXISTS \"InvoiceItemNames\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_InvoiceItemNames\" PRIMARY KEY AUTOINCREMENT, \"Name\" TEXT NOT NULL, \"Unit\" TEXT NOT NULL DEFAULT 'С€С‚', \"DefaultPrice\" TEXT NOT NULL DEFAULT '0', \"Description\" TEXT NOT NULL, \"ColorHex\" TEXT NOT NULL, \"IsActive\" INTEGER NOT NULL, \"CreatedAt\" TEXT NOT NULL);", cancellationToken);
                await ExecuteNonQueryAsync(connection, "CREATE INDEX IF NOT EXISTS \"IX_InvoiceItemNameDictionaryItems_IsActive\" ON \"InvoiceItemNames\" (\"IsActive\");", cancellationToken);
                await ExecuteNonQueryAsync(connection, "CREATE INDEX IF NOT EXISTS \"IX_InvoiceItemNameDictionaryItems_Name\" ON \"InvoiceItemNames\" (\"Name\");", cancellationToken);
                await ExecuteNonQueryAsync(connection, "CREATE INDEX IF NOT EXISTS \"IX_InvoiceItemNameDictionaryItems_IsActive_Name\" ON \"InvoiceItemNames\" (\"IsActive\", \"Name\");", cancellationToken);

                if (await TableExistsAsync(connection, "InvoiceUnitDictionaryItems", cancellationToken)
                    && !await TableHasRowsAsync(connection, "InvoiceUnits", cancellationToken))
                {
                    await ExecuteNonQueryAsync(connection,
                        "INSERT INTO \"InvoiceUnits\" (\"Name\", \"Description\", \"ColorHex\", \"IsActive\", \"CreatedAt\") SELECT \"Name\", \"Description\", \"ColorHex\", \"IsActive\", COALESCE(\"CreatedAt\", CURRENT_TIMESTAMP) FROM \"InvoiceUnitDictionaryItems\";",
                        cancellationToken);
                }

                if (await TableExistsAsync(connection, "InvoiceItemNameDictionaryItems", cancellationToken)
                    && !await TableHasRowsAsync(connection, "InvoiceItemNames", cancellationToken))
                {
                    var legacyHasUnit = await ColumnExistsAsync(connection, "InvoiceItemNameDictionaryItems", "Unit", cancellationToken);
                    var legacyHasDefaultPrice = await ColumnExistsAsync(connection, "InvoiceItemNameDictionaryItems", "DefaultPrice", cancellationToken);
                    var legacyUnitSql = legacyHasUnit ? "\"Unit\"" : "'С€С‚'";
                    var legacyDefaultPriceSql = legacyHasDefaultPrice ? "\"DefaultPrice\"" : "'0'";

                    await ExecuteNonQueryAsync(connection,
                        $"INSERT INTO \"InvoiceItemNames\" (\"Name\", \"Unit\", \"DefaultPrice\", \"Description\", \"ColorHex\", \"IsActive\", \"CreatedAt\") SELECT \"Name\", {legacyUnitSql}, {legacyDefaultPriceSql}, \"Description\", \"ColorHex\", \"IsActive\", COALESCE(\"CreatedAt\", CURRENT_TIMESTAMP) FROM \"InvoiceItemNameDictionaryItems\";",
                        cancellationToken);
                }
            }
            finally
            {
                if (shouldClose && connection.State == ConnectionState.Open)
                {
                    await connection.CloseAsync();
                }
            }
        }

        private static async Task EnsureIncomeTypeSchemaAsync(PaymentContext context, CancellationToken cancellationToken = default)
        {
            if (!context.Database.IsSqlite())
            {
                return;
            }

            var connection = context.Database.GetDbConnection();
            var shouldClose = connection.State != ConnectionState.Open;

            if (shouldClose)
            {
                await connection.OpenAsync(cancellationToken);
            }

            try
            {
                var hasPaymentType = false;

                await using (var checkCommand = connection.CreateCommand())
                {
                    checkCommand.CommandText = "PRAGMA table_info(\"IncomeTypes\");";
                    await using var reader = await checkCommand.ExecuteReaderAsync(cancellationToken);

                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var columnName = reader.GetString(1);
                        if (string.Equals(columnName, "PaymentType", StringComparison.OrdinalIgnoreCase))
                        {
                            hasPaymentType = true;
                            break;
                        }
                    }
                }

                if (!hasPaymentType)
                {
                    await ExecuteNonQueryAsync(connection, "ALTER TABLE \"IncomeTypes\" ADD COLUMN \"PaymentType\" INTEGER NOT NULL DEFAULT 0;", cancellationToken);
                    await ExecuteNonQueryAsync(connection, "CREATE INDEX IF NOT EXISTS \"IX_IncomeTypes_IsActive_PaymentType\" ON \"IncomeTypes\" (\"IsActive\", \"PaymentType\");", cancellationToken);
                    await ExecuteNonQueryAsync(connection, "CREATE INDEX IF NOT EXISTS \"IX_IncomeTypes_IsActive_PaymentType_Name\" ON \"IncomeTypes\" (\"IsActive\", \"PaymentType\", \"Name\");", cancellationToken);
                }
            }
            finally
            {
                if (shouldClose && connection.State == ConnectionState.Open)
                {
                    await connection.CloseAsync();
                }
            }
        }

        private static async Task ExecuteNonQueryAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task<bool> TableExistsAsync(DbConnection connection, string tableName, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";

            var parameter = command.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = tableName;
            command.Parameters.Add(parameter);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is not null && result != DBNull.Value;
        }

        private static async Task<bool> TableHasRowsAsync(DbConnection connection, string tableName, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT EXISTS(SELECT 1 FROM \"{tableName}\" LIMIT 1);";

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is not null && result != DBNull.Value && Convert.ToInt64(result) != 0;
        }

        private static async Task<bool> ColumnExistsAsync(DbConnection connection, string tableName, string columnName, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{tableName}\");";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// РЎРѕР·РґР°С‘С‚ СЂРѕР»Рё admin Рё user (РµСЃР»Рё РѕС‚СЃСѓС‚СЃС‚РІСѓСЋС‚).
        /// </summary>
        private static async Task SeedRolesAsync(PaymentContext context)
        {
            if (!await context.Roles.AnyAsync())
            {
                context.Roles.AddRange(
                    new Role { Name = "admin", Description = "РђРґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂ СЃРёСЃС‚РµРјС‹" },
                    new Role { Name = "user", Description = "РћР±С‹С‡РЅС‹Р№ РїРѕР»СЊР·РѕРІР°С‚РµР»СЊ" }
                );
                await context.SaveChangesAsync();
                return;
            }

            // Р”РѕСЃРµСЏС‚СЊ РЅРµРґРѕСЃС‚Р°СЋС‰РёРµ СЂРѕР»Рё
            await EnsureRoleAsync(context, "admin", "РђРґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂ СЃРёСЃС‚РµРјС‹");
            await EnsureRoleAsync(context, "user", "РћР±С‹С‡РЅС‹Р№ РїРѕР»СЊР·РѕРІР°С‚РµР»СЊ");
        }

        private static async Task SeedRolePermissionsAsync(PaymentContext context)
        {
            var roles = await context.Roles
                .AsNoTracking()
                .Select(r => new
                {
                    r.Id,
                    r.Name
                })
                .ToListAsync();
            if (roles.Count == 0)
            {
                return;
            }

            var existing = await context.RolePermissions
                .AsNoTracking()
                .Select(rp => new { rp.RoleId, rp.Section })
                .ToListAsync();

            foreach (var role in roles)
            {
                var defaults = RolePermissionCatalog.BuildDefaults(
                    string.Equals(role.Name, "admin", StringComparison.Ordinal));

                foreach (var section in RolePermissionCatalog.AllSections)
                {
                    if (existing.Any(rp => rp.RoleId == role.Id && rp.Section == section))
                    {
                        continue;
                    }

                    var values = defaults[section];

                    context.RolePermissions.Add(new RolePermission
                    {
                        RoleId = role.Id,
                        Section = section,
                        CanView = values.CanView,
                        CanCreate = values.CanCreate,
                        CanEdit = values.CanEdit,
                        CanDelete = values.CanDelete,
                        CanExport = values.CanExport,
                        CanViewAnalytics = values.CanViewAnalytics,
                    });
                }
            }

            await context.SaveChangesAsync();
        }

        /// <summary>
        /// РЎРѕР·РґР°С‘С‚ РїРѕР»СЊР·РѕРІР°С‚РµР»СЏ-Р°РґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂР°, РµСЃР»Рё РµРіРѕ РµС‰С‘ РЅРµС‚.
        /// </summary>
        private static async Task SeedAdminUserAsync(PaymentContext context)
        {
            var adminEmail = Environment.GetEnvironmentVariable("PAYPLANNER_ADMIN_EMAIL") ?? "admin@payplanner.local";
            var adminPassword = Environment.GetEnvironmentVariable("PAYPLANNER_ADMIN_PASSWORD") ?? "ChangeMeAdmin#12345";
            var adminFullName = Environment.GetEnvironmentVariable("PAYPLANNER_ADMIN_FULLNAME") ?? "System Administrator";

            if (await context.Users.AsNoTracking().AnyAsync())
            {
                return;
            }

            var exists = await context.Users.AsNoTracking().AnyAsync(u => u.Email == adminEmail);
            if (exists) return;

            var adminRoleId = await context.Roles
                .Where(r => r.Name == "admin")
                .Select(r => r.Id)
                .FirstOrDefaultAsync();

            if (adminRoleId == 0)
            {
                context.Roles.Add(new Role { Name = "admin", Description = "РђРґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂ СЃРёСЃС‚РµРјС‹" });
                await context.SaveChangesAsync();
                adminRoleId = await context.Roles.Where(r => r.Name == "admin").Select(r => r.Id).FirstAsync();
            }

            var hasher = new PasswordHasher<User>(
                Options.Create(new PasswordHasherOptions
                {
                    CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
                    IterationCount = 210_000
                })
            );

            var now = DateTime.UtcNow;

            var admin = new User
            {
                Email = adminEmail,
                FullName = adminFullName,
                RoleId = adminRoleId,
                IsActive = true,
                IsApproved = true,
                ApprovedAt = now,
                ApprovedByUserId = null,
                CreatedAt = now,
                UpdatedAt = now
            };

            admin.PasswordHash = hasher.HashPassword(admin, adminPassword);

            context.Users.Add(admin);
            await context.SaveChangesAsync();
        }

        // ----------------------- РЅРёР¶Рµ вЂ” СЃРёРґ РєР»РёРµРЅС‚РѕРІ/РґРµР», РїР»Р°С‚РµР¶РµР№ -----------------------

        private static async Task SeedClientsCasesPaymentsAsync(PaymentContext context)
        {
            // Р§РёС‚Р°РµРј СЃРїСЂР°РІРѕС‡РЅРёРєРё Рё СЃС‚СЂРѕРёРј Р±РµР·РѕРїР°СЃРЅС‹Рµ РєР°СЂС‚С‹ (Р±РµР· РїР°РґРµРЅРёСЏ РЅР° РґСѓР±Р»СЏС…)
            var dealTypes = await context.DealTypes.AsNoTracking().ToListAsync();
            var dealTypeByName = BuildNameMap(dealTypes, d => d.Name, d => d.Id);

            var incomeTypes = await context.IncomeTypes.AsNoTracking().ToListAsync();
            var incomeTypeByName = BuildNameMap(incomeTypes, i => i.Name, i => i.Id);

            var sources = await context.PaymentSources.AsNoTracking().ToListAsync();
            var sourceByName = BuildNameMap(sources, s => s.Name, s => s.Id);

            var statuses = await context.PaymentStatuses.AsNoTracking().ToListAsync();
            var statusByName = BuildNameMap(statuses, s => s.Name, s => s.Id);

            // РРґРµРјРїРѕС‚РµРЅС‚РЅС‹Рµ РєР»РёРµРЅС‚С‹
            var clients = new[]
            {
                new Client { Name = "РРІР°РЅ РРІР°РЅРѕРІ",      Email = "ivan@example.com",  Phone = "+7-900-010-01-01", Company = "РўРµС…РљРѕСЂРї",                Address = "СѓР». Р›РµРЅРёРЅР°, Рґ. 10, РњРѕСЃРєРІР°" },
                new Client { Name = "РњР°СЂРёСЏ РџРµС‚СЂРѕРІР°",    Email = "maria@company.com", Phone = "+7-900-010-01-02", Company = "РЎС‚СѓРґРёСЏ Р”РёР·Р°Р№РЅР°",         Address = "РїСЂ. РњРёСЂР°, Рґ. 25, РЎР°РЅРєС‚-РџРµС‚РµСЂР±СѓСЂРі" },
                new Client { Name = "РђР»РµРєСЃРµР№ РЎРјРёСЂРЅРѕРІ",  Email = "alex@business.com", Phone = "+7-900-010-01-03", Company = "РЎРјРёСЂРЅРѕРІ РРЅРґР°СЃС‚СЂРёР·",      Address = "СѓР». РЎРѕРІРµС‚СЃРєР°СЏ, Рґ. 5, Р•РєР°С‚РµСЂРёРЅР±СѓСЂРі" },
                new Client { Name = "Р•РєР°С‚РµСЂРёРЅР° РЎРёРґРѕСЂРѕРІР°", Email = "katya@startup.com", Phone = "+7-900-010-01-04", Company = "РРЅРЅРѕРІР°С†РёРѕРЅРЅС‹Рµ Р›Р°Р±РѕСЂР°С‚РѕСЂРёРё", Address = "СѓР». Р“Р°РіР°СЂРёРЅР°, Рґ. 12, РќРѕРІРѕСЃРёР±РёСЂСЃРє" },
                new Client { Name = "Р”РјРёС‚СЂРёР№ РљСѓР·РЅРµС†РѕРІ", Email = "dmitry@consulting.com", Phone = "+7-900-010-01-05", Company = "РљСѓР·РЅРµС†РѕРІ РљРѕРЅСЃР°Р»С‚РёРЅРі", Address = "СѓР». РџСѓС€РєРёРЅР°, Рґ. 8, РљР°Р·Р°РЅСЊ" }
            };

            var existingClients = await context.Clients.AsNoTracking().ToListAsync();
            var addedClients = new List<Client>();

            foreach (var c in clients)
            {
                var found = existingClients.FirstOrDefault(ec =>
                    (!string.IsNullOrWhiteSpace(c.Email) &&
                        string.Equals(ec.Email, c.Email, StringComparison.OrdinalIgnoreCase)) ||
                    (string.Equals(ec.Name, c.Name, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(ec.Phone, c.Phone, StringComparison.OrdinalIgnoreCase))
                );

                if (found is null)
                {
                    context.Clients.Add(c);
                    addedClients.Add(c);
                }
            }
            if (addedClients.Count > 0) await context.SaveChangesAsync();

            existingClients = await context.Clients.AsNoTracking().ToListAsync();

            var casesToEnsure = new List<(string clientEmail, string title, string? desc, ClientCaseStatus status)>
            {
                ("ivan@example.com",   "РџРѕРґРіРѕС‚РѕРІРєР° РґРѕРіРѕРІРѕСЂР° РєРѕРЅСЃР°Р»С‚РёРЅРіР°", "РђРЅР°Р»РёС‚РёРєР° + РўР—", ClientCaseStatus.Open),
                ("ivan@example.com",   "РџРѕРґРґРµСЂР¶РєР° РїРѕРґРїРёСЃРєРё SaaS",         "РњРµСЃСЏС‡РЅР°СЏ РїРѕРґРїРёСЃРєР°", ClientCaseStatus.Open),
                ("maria@company.com",  "Р РµРґРёР·Р°Р№РЅ СЃР°Р№С‚Р°",                  "UI/UX + РІС‘СЂСЃС‚РєР°", ClientCaseStatus.OnHold),
                ("alex@business.com",  "РџРѕСЃС‚Р°РІРєР° РїР°СЂС‚РёРё в„–101",            "РћС‚РіСЂСѓР·РєР° 50 С€С‚.", ClientCaseStatus.Open),
                ("katya@startup.com",  "РќР°СѓС‡РЅС‹Р№ РїСЂРѕРµРєС‚ R&D",              "Р­С‚Р°Рї 1 вЂ” РќРР ", ClientCaseStatus.Open),
                ("dmitry@consulting.com","РЎРѕРїСЂРѕРІРѕР¶РґРµРЅРёРµ 2025",            "РџРѕРјРµСЃСЏС‡РЅРѕРµ РѕР±СЃР»СѓР¶РёРІР°РЅРёРµ", ClientCaseStatus.Closed)
            };

            var caseEntities = new List<ClientCase>();

            foreach (var (clientEmail, title, desc, status) in casesToEnsure)
            {
                var cli = existingClients.FirstOrDefault(c =>
                    string.Equals(c.Email, clientEmail, StringComparison.OrdinalIgnoreCase));
                if (cli is null)
                {
                    Console.Error.WriteLine($"[SEED][Cases] client '{clientEmail}' not found, skip case '{title}'");
                    continue;
                }

                var exists = await context.ClientCases.AsNoTracking()
                    .AnyAsync(cc => cc.ClientId == cli.Id && cc.Title == title);

                if (!exists)
                {
                    var cc = new ClientCase
                    {
                        ClientId = cli.Id,
                        Title = title,
                        Description = desc ?? string.Empty,
                        Status = status
                    };
                    context.ClientCases.Add(cc);
                    caseEntities.Add(cc);
                }
            }

            if (caseEntities.Count > 0) await context.SaveChangesAsync();

            var allCases = await context.ClientCases.AsNoTracking().ToListAsync();

            var today = DateTime.UtcNow.Date;
            var paymentsToEnsure = new PaymentSeedDefinition[]
            {
                new PaymentSeedDefinition
                {
                    Email = "ivan@example.com",
                    CaseTitle = "РџРѕРґРіРѕС‚РѕРІРєР° РґРѕРіРѕРІРѕСЂР° РєРѕРЅСЃР°Р»С‚РёРЅРіР°",
                    Date = today.AddDays(-10),
                    AccountDate = (DateTime?)today.AddDays(-12),
                    Account = "1306",
                    Amount = 150_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Completed,
                    Description = "РђРІР°РЅСЃ РїРѕ РґРѕРіРѕРІРѕСЂСѓ",
                    IsPaid = true,
                    PaidDate = (DateTime?)today.AddDays(-10),
                    DealType = "РљРѕРЅСЃР°Р»С‚РёРЅРі",
                    IncomeType = "Р”РѕС…РѕРґ РѕС‚ СѓСЃР»СѓРі",
                    Source = "Р‘Р°РЅРєРѕРІСЃРєРёР№ РїРµСЂРµРІРѕРґ",
                    StatusName = "Р’С‹РїРѕР»РЅРµРЅРѕ",
                    ActReference = (string?)null
                },
                new PaymentSeedDefinition
                {
                    Email = "ivan@example.com",
                    CaseTitle = "РџРѕРґРіРѕС‚РѕРІРєР° РґРѕРіРѕРІРѕСЂР° РєРѕРЅСЃР°Р»С‚РёРЅРіР°",
                    Date = today.AddDays(20),
                    AccountDate = (DateTime?)today.AddDays(15),
                    Account = "INV-2026",
                    Amount = 200_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "РћРєРѕРЅС‡Р°С‚РµР»СЊРЅС‹Р№ СЂР°СЃС‡С‘С‚",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "РљРѕРЅСЃР°Р»С‚РёРЅРі",
                    IncomeType = "Р”РѕС…РѕРґ РѕС‚ СѓСЃР»СѓРі",
                    Source = "Р‘Р°РЅРєРѕРІСЃРєР°СЏ РєР°СЂС‚Р°",
                    StatusName = "РћР¶РёРґР°РµС‚СЃСЏ",
                    ActReference = (string?)null
                },

                // РњР°СЂРёСЏ РџРµС‚СЂРѕРІР° вЂ” Р РµРґРёР·Р°Р№РЅ СЃР°Р№С‚Р°
                new PaymentSeedDefinition
                {
                    Email = "maria@company.com",
                    CaseTitle = "Р РµРґРёР·Р°Р№РЅ СЃР°Р№С‚Р°",
                    Date = today.AddDays(-5),
                    AccountDate = (DateTime?)today.AddDays(-7),
                    Account = "1305",
                    Amount = 90_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Completed,
                    Description = "Р­С‚Р°Рї 1 вЂ” РїСЂРѕС‚РѕС‚РёРїС‹",
                    IsPaid = true,
                    PaidDate = (DateTime?)today.AddDays(-5),
                    DealType = "РџСЂРѕРµРєС‚",
                    IncomeType = "Р”РѕС…РѕРґ РѕС‚ СѓСЃР»СѓРі",
                    Source = "PayPal",
                    StatusName = "Р’С‹РїРѕР»РЅРµРЅРѕ",
                    ActReference = (string?)null
                },
                new PaymentSeedDefinition
                {
                    Email = "maria@company.com",
                    CaseTitle = "Р РµРґРёР·Р°Р№РЅ СЃР°Р№С‚Р°",
                    Date = today.AddDays(-1),
                    AccountDate = (DateTime?)today.AddDays(-3),
                    Account = "INV-2042",
                    Amount = 110_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Overdue,
                    Description = "Р­С‚Р°Рї 2 вЂ” UI",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "РџСЂРѕРµРєС‚",
                    IncomeType = "Р”РѕС…РѕРґ РѕС‚ СѓСЃР»СѓРі",
                    Source = "Р‘Р°РЅРєРѕРІСЃРєРёР№ РїРµСЂРµРІРѕРґ",
                    StatusName = "РџСЂРѕСЃСЂРѕС‡РµРЅРѕ",
                    ActReference = "РђРєС‚ РѕР¶РёРґР°РµС‚СЃСЏ"
                },
                new PaymentSeedDefinition
                {
                    Email = "maria@company.com",
                    CaseTitle = "Р РµРґРёР·Р°Р№РЅ СЃР°Р№С‚Р°",
                    Date = today.AddDays(-2),
                    AccountDate = (DateTime?)today.AddDays(-4),
                    Account = "154",
                    Amount = 65_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "РЎС‡С‘С‚ Р·Р° С‚РµСЃС‚РёСЂРѕРІР°РЅРёРµ",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "РџСЂРѕРµРєС‚",
                    IncomeType = "Р”РѕС…РѕРґ РѕС‚ СѓСЃР»СѓРі",
                    Source = "Р‘Р°РЅРєРѕРІСЃРєРёР№ РїРµСЂРµРІРѕРґ",
                    StatusName = "РћР¶РёРґР°РµС‚СЃСЏ",
                    ActReference = "РђРєС‚ Р±СѓРґРµС‚ РїРѕРґРїРёСЃР°РЅ РІ РЅРѕСЏР±СЂРµ"
                },

                // РђР»РµРєСЃРµР№ РЎРјРёСЂРЅРѕРІ вЂ” РџРѕСЃС‚Р°РІРєР° РїР°СЂС‚РёРё в„–101
                new PaymentSeedDefinition
                {
                    Email = "alex@business.com",
                    CaseTitle = "РџРѕСЃС‚Р°РІРєР° РїР°СЂС‚РёРё в„–101",
                    Date = today.AddDays(7),
                    AccountDate = (DateTime?)today.AddDays(2),
                    Account = "1307",
                    Amount = 350_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "РћРїР»Р°С‚Р° Р·Р° РїРѕСЃС‚Р°РІРєСѓ",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "РџСЂРѕРґР°Р¶Р° С‚РѕРІР°СЂР°",
                    IncomeType = "РџСЂРѕС‡РёРµ РґРѕС…РѕРґС‹",
                    Source = "Р§РµРє",
                    StatusName = "РћР¶РёРґР°РµС‚СЃСЏ",
                    ActReference = (string?)null
                },

                // Р•РєР°С‚РµСЂРёРЅР° РЎРёРґРѕСЂРѕРІР° вЂ” РќР°СѓС‡РЅС‹Р№ РїСЂРѕРµРєС‚ R&D
                new PaymentSeedDefinition
                {
                    Email = "katya@startup.com",
                    CaseTitle = "РќР°СѓС‡РЅС‹Р№ РїСЂРѕРµРєС‚ R&D",
                    Date = today.AddDays(30),
                    AccountDate = (DateTime?)today.AddDays(28),
                    Account = "INV-RD-001",
                    Amount = 500_000m,
                    Type = PaymentType.Income,
                    Status = PaymentStatus.Pending,
                    Description = "Р­С‚Р°Рї 1 вЂ” РќРР ",
                    IsPaid = false,
                    PaidDate = (DateTime?)null,
                    DealType = "РџСЂРѕРµРєС‚",
                    IncomeType = "РџСЂРѕС‡РёРµ РґРѕС…РѕРґС‹",
                    Source = "Р‘Р°РЅРєРѕРІСЃРєРёР№ РїРµСЂРµРІРѕРґ",
                    StatusName = "РћР¶РёРґР°РµС‚СЃСЏ",
                    ActReference = (string?)null
                },

                // Р”РјРёС‚СЂРёР№ РљСѓР·РЅРµС†РѕРІ вЂ” РЎРѕРїСЂРѕРІРѕР¶РґРµРЅРёРµ 2025 (Р РђРЎРҐРћР”)
                new PaymentSeedDefinition
                {
                    Email = "dmitry@consulting.com",
                    CaseTitle = "РЎРѕРїСЂРѕРІРѕР¶РґРµРЅРёРµ 2025",
                    Date = today.AddDays(-3),
                    AccountDate = (DateTime?)null,
                    Account = (string?)null,
                    Amount = 25_000m,
                    Type = PaymentType.Expense,
                    Status = PaymentStatus.Completed,
                    Description = "Р—Р°РєСѓРїРєР° РџРћ РґР»СЏ СЃРѕРїСЂРѕРІРѕР¶РґРµРЅРёСЏ",
                    IsPaid = true,
                    PaidDate = (DateTime?)today.AddDays(-3),
                    DealType = "РћР±СЃР»СѓР¶РёРІР°РЅРёРµ",
                    IncomeType = "РџСЂРѕС‡РёРµ СЂР°СЃС…РѕРґС‹",
                    Source = "РќР°Р»РёС‡РЅС‹Рµ",
                    StatusName = "Р’С‹РїРѕР»РЅРµРЅРѕ",
                    ActReference = (string?)null
                },
            };
            var toAdd = new List<Payment>();

            foreach (var p in paymentsToEnsure)
            {
                var cli = existingClients.FirstOrDefault(c =>
                    string.Equals(c.Email, p.Email, StringComparison.OrdinalIgnoreCase));
                if (cli is null)
                {
                    Console.Error.WriteLine($"[SEED][Payments] client '{p.Email}' not found, skip payment '{p.Description}'");
                    continue;
                }

                var caseEnt = allCases.FirstOrDefault(c => c.ClientId == cli.Id && c.Title == p.CaseTitle);
                if (caseEnt is null)
                {
                    Console.Error.WriteLine($"[SEED][Payments] case '{p.CaseTitle}' for '{p.Email}' not found, skip");
                    continue;
                }

                var exists = await context.Payments.AsNoTracking().AnyAsync(x =>
                    x.ClientId == cli.Id &&
                    x.ClientCaseId == caseEnt.Id &&
                    x.Date == p.Date &&
                    x.Amount == p.Amount &&
                    x.Type == p.Type &&
                    x.Description == p.Description
                );

                if (exists) continue;

                var payment = new Payment
                {
                    ClientId = cli.Id,
                    ClientCaseId = caseEnt.Id,
                    Date = p.Date,
                    PlannedDate = p.Date,
                    AccountDate = p.AccountDate,
                    Account = string.IsNullOrWhiteSpace(p.Account) ? null : p.Account,
                    Amount = p.Amount,
                    Type = p.Type,
                    Status = p.Status,
                    Description = p.Description,
                    Notes = p.ActReference ?? string.Empty,
                    IsPaid = p.IsPaid,
                    PaidDate = p.PaidDate,
                    PaidAmount = p.IsPaid ? p.Amount : 0m,
                    LastPaymentDate = p.PaidDate,
                    DealTypeId = GetIdOrThrow(dealTypeByName, p.DealType, "DealType"),
                    IncomeTypeId = GetIdOrThrow(incomeTypeByName, p.IncomeType, "IncomeType"),
                    PaymentSourceId = GetIdOrThrow(sourceByName, p.Source, "PaymentSource"),
                    PaymentStatusId = GetIdOrThrow(statusByName, p.StatusName, "PaymentStatus"),
                    SystemNotes = string.Empty,
                    RescheduleCount = 0
                };

                PaymentDomainService.Normalize(payment);
                PaymentDomainService.ApplyStatusRules(payment, DateTime.UtcNow);

                toAdd.Add(payment);
            }

            if (toAdd.Count > 0)
            {
                context.Payments.AddRange(toAdd);
                await context.SaveChangesAsync();
            }

            var responsible = await context.Users.AsNoTracking()
                .Where(u => u.IsActive && u.IsApproved)
                .OrderBy(u => u.Id)
                .FirstOrDefaultAsync();

            var actsToEnsure = new[]
            {
                new
                {
                    Number = "ACT-001",
                    Title = "РђРєС‚ РїРµСЂРµРґР°С‡Рё РјР°РєРµС‚РѕРІ РїРѕ СЂРµРґРёР·Р°Р№РЅСѓ СЃР°Р№С‚Р°",
                    Date = today.AddDays(-15),
                    Amount = 51721.64m,
                    Invoice = "1305",
                    ClientEmail = "maria@company.com",
                    Status = ActStatus.Transferred,
                    Inn = "7721234567"
                },
                new
                {
                    Number = "ACT-002",
                    Title = "РђРєС‚ РѕРєР°Р·Р°РЅРЅС‹С… СѓСЃР»СѓРі РїРѕ РєРѕРЅСЃР°Р»С‚РёРЅРіСѓ",
                    Date = today.AddDays(-5),
                    Amount = 118000m,
                    Invoice = "1306",
                    ClientEmail = "ivan@example.com",
                    Status = ActStatus.Created,
                    Inn = "7705123456"
                },
                new
                {
                    Number = "ACT-003",
                    Title = "РђРєС‚ РїСЂРёС‘РјР°-РїРµСЂРµРґР°С‡Рё РїР°СЂС‚РёРё в„–101",
                    Date = today.AddDays(-2),
                    Amount = 90000m,
                    Invoice = "1307",
                    ClientEmail = "alex@business.com",
                    Status = ActStatus.Signed,
                    Inn = "7805456789"
                }
            };

            var existingActs = await context.Acts.AsNoTracking().ToListAsync();
            var actsToAdd = new List<Act>();

            foreach (var act in actsToEnsure)
            {
                var client = existingClients.FirstOrDefault(c =>
                    string.Equals(c.Email, act.ClientEmail, StringComparison.OrdinalIgnoreCase));
                if (client is null)
                {
                    Console.Error.WriteLine($"[SEED][Acts] client '{act.ClientEmail}' not found, skip act '{act.Number}'");
                    continue;
                }

                var existsAct = existingActs.Any(a => a.Number == act.Number && a.ClientId == client.Id);
                if (existsAct) continue;

                actsToAdd.Add(new Act
                {
                    Number = act.Number,
                    Title = act.Title,
                    Date = act.Date,
                    Amount = act.Amount,
                    InvoiceNumber = act.Invoice,
                    CounterpartyInn = act.Inn,
                    Status = act.Status,
                    ClientId = client.Id,
                    ResponsibleId = responsible?.Id,
                    Comment = "",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            if (actsToAdd.Count > 0)
            {
                context.Acts.AddRange(actsToAdd);
                await context.SaveChangesAsync();
            }
        }

        // ----------------------- helpers -----------------------

        private static async Task EnsureRoleAsync(PaymentContext ctx, string name, string? desc)
        {
            var exists = await ctx.Roles.AsNoTracking().AnyAsync(r => r.Name == name);
            if (!exists)
            {
                ctx.Roles.Add(new Role { Name = name, Description = desc ?? string.Empty });
                await ctx.SaveChangesAsync();
            }
        }

        private static async Task EnsureIncomeTypeAsync(PaymentContext ctx, string name, string color, PaymentType paymentType, string? desc)
        {
            var found = await ctx.IncomeTypes.FirstOrDefaultAsync(x => x.Name == name);
            if (found is null)
            {
                ctx.IncomeTypes.Add(new IncomeType
                {
                    Name = name,
                    ColorHex = color,
                    Description = desc ?? string.Empty,
                    IsActive = true,
                    PaymentType = paymentType
                });
                await ctx.SaveChangesAsync();
            }
            else
            {
                if (found.PaymentType != paymentType)
                {
                    found.PaymentType = paymentType;
                    await ctx.SaveChangesAsync();
                }
            }
        }

        private static async Task EnsurePaymentSourceAsync(PaymentContext ctx, string name, string color, string? desc)
        {
            var exists = await ctx.PaymentSources.AsNoTracking().AnyAsync(x => x.Name == name);
            if (!exists)
            {
                ctx.PaymentSources.Add(new PaymentSource { Name = name, ColorHex = color, Description = desc ?? string.Empty, IsActive = true });
                await ctx.SaveChangesAsync();
            }
        }

        private static async Task EnsurePaymentStatusAsync(PaymentContext ctx, string name, string color, string? desc)
        {
            var exists = await ctx.PaymentStatuses.AsNoTracking().AnyAsync(x => x.Name == name);
            if (!exists)
            {
                ctx.PaymentStatuses.Add(new PaymentStatusEntity { Name = name, ColorHex = color, Description = desc ?? string.Empty, IsActive = true });
                await ctx.SaveChangesAsync();
            }
        }

        private static async Task EnsureStarterInvoiceUnitsAsync(PaymentContext ctx)
        {
            var starterUnits = new[]
            {
                new InvoiceUnitDictionaryItem { Name = "усл.ед.", Description = "Условная единица", ColorHex = "#475569", IsActive = true },
                new InvoiceUnitDictionaryItem { Name = "услуга", Description = "Услуга", ColorHex = "#334155", IsActive = true },
                new InvoiceUnitDictionaryItem { Name = "ч", Description = "Часы", ColorHex = "#1E293B", IsActive = true },
                new InvoiceUnitDictionaryItem { Name = "%", Description = "Проценты", ColorHex = "#0F172A", IsActive = true },
                new InvoiceUnitDictionaryItem { Name = "стр.", Description = "Страницы", ColorHex = "#64748B", IsActive = true },
            };

            var existingUnitNames = await ctx.InvoiceUnits.AsNoTracking()
                .Select(x => x.Name)
                .ToListAsync();

            var existingUnitKeys = existingUnitNames
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(Norm)
                .ToHashSet();

            var unitsToAdd = starterUnits
                .Where(x => !existingUnitKeys.Contains(Norm(x.Name)))
                .ToList();

            if (unitsToAdd.Count == 0)
            {
                return;
            }

            ctx.InvoiceUnits.AddRange(unitsToAdd);
            await ctx.SaveChangesAsync();
        }

        private static async Task EnsureStarterServiceCatalogAsync(PaymentContext ctx)
        {
            var starterCategories = new[]
            {
                new { Name = "Административные дела", Description = "Преднаполнение по скриншотам КУБА", ColorHex = "#2563EB" },
                new { Name = "Арбитраж", Description = "Преднаполнение по скриншотам КУБА", ColorHex = "#1D4ED8" },
                new { Name = "Аудит", Description = "Преднаполнение по скриншотам КУБА", ColorHex = "#0891B2" },
                new { Name = "Аутсорсинг", Description = "Преднаполнение по скриншотам КУБА", ColorHex = "#7C3AED" },
                new { Name = "Гражданские дела", Description = "Преднаполнение по скриншотам КУБА", ColorHex = "#0F766E" },
                new { Name = "Иная юридическая помощь", Description = "Преднаполнение по скриншотам КУБА", ColorHex = "#9333EA" },
                new { Name = "Прочие услуги", Description = "Преднаполнение по скриншотам КУБА", ColorHex = "#F59E0B" },
                new { Name = "Разовая юридическая помощь", Description = "Преднаполнение по скриншотам КУБА", ColorHex = "#DC2626" },
                new { Name = "Без группы", Description = "Преднаполнение по скриншотам КУБА", ColorHex = "#64748B" },
            };

            var starterServices = new (string Name, string Category, string Unit, decimal Price, string ColorHex)[]
            {
                ("АДМ/ Ознакомление с материалами дела ( за 1 том )", "Административные дела", "усл.ед.", 7000m, "#2563EB"),
                ("АДМ/ Представительство в административных органах", "Административные дела", "усл.ед.", 8000m, "#2563EB"),
                ("АДМ/ Представительство в суде ( 1 судодень )", "Административные дела", "усл.ед.", 10000m, "#2563EB"),
                ("АДМ/Составление административного искового заявления ( КАС )", "Административные дела", "усл.ед.", 12000m, "#2563EB"),
                ("АДМ/Составление заявлений, жалоб, возражений", "Административные дела", "усл.ед.", 10000m, "#2563EB"),

                ("АРБ/ Изучение материалов дела (1 том)", "Арбитраж", "усл.ед.", 15000m, "#1D4ED8"),
                ("АРБ/ Консультация юридического лица и ИП", "Арбитраж", "усл.ед.", 5000m, "#1D4ED8"),
                ("АРБ/ Ознакомление с материалами дела (1 том) в суде ( АРБИТРАЖ )", "Арбитраж", "усл.ед.", 10000m, "#1D4ED8"),
                ("АРБ/ Правовая экспертиза документов", "Арбитраж", "усл.ед.", 15000m, "#1D4ED8"),
                ("АРБ/ Составление жалобы в Верховный суд РФ", "Арбитраж", "усл.ед.", 30000m, "#1D4ED8"),
                ("АРБ/ Составление заявления в арбитражный суд", "Арбитраж", "усл.ед.", 8000m, "#1D4ED8"),
                ("АРБ/ Составление претензии", "Арбитраж", "усл.ед.", 8000m, "#1D4ED8"),
                ("АРБ/ Составление ходатайства", "Арбитраж", "усл.ед.", 2000m, "#1D4ED8"),
                ("АРБ/ Составления искового заявления ( отзыва на исковое заявление)", "Арбитраж", "усл.ед.", 15000m, "#1D4ED8"),
                ("АРБ/ Участие в исполнительном производстве ( 1 день)", "Арбитраж", "усл.ед.", 10000m, "#1D4ED8"),
                ("АРБ/ Участие в суде I инстанции (1 судодень)", "Арбитраж", "усл.ед.", 15000m, "#1D4ED8"),
                ("АРБ/ Участие в суде апелляционной ( кассационной инстанции) (1 судодень)", "Арбитраж", "усл.ед.", 30000m, "#1D4ED8"),
                ("АРБ/Гонорар от суммы исковых требований по имущественным спорам 10%", "Арбитраж", "%", 10m, "#1D4ED8"),

                ("АУД/ Проведение комплексного юридического аудита юридического лица", "Аудит", "усл.ед.", 80000m, "#0891B2"),

                ("АУТ/ Тариф \"БИЗНЕС ЛАЙТ\" ( ЮР-ЛИЦА )", "Аутсорсинг", "усл.ед.", 25500m, "#7C3AED"),
                ("АУТ/ Тариф \"БИЗНЕС\" ( ЮР-ЛИЦА )", "Аутсорсинг", "усл.ед.", 45000m, "#7C3AED"),
                ("АУТ/ Тариф \"ОПТИМАЛЬНЫЙ\"", "Аутсорсинг", "усл.ед.", 30000m, "#7C3AED"),
                ("АУТ/ Тариф \"ПРЕМИУМ\" ( ЮР-ЛИЦА )", "Аутсорсинг", "усл.ед.", 75000m, "#7C3AED"),
                ("АУТ/ Тариф \"СТАНДАРТНЫЙ\" ( ЮР-ЛИЦА )", "Аутсорсинг", "усл.ед.", 15000m, "#7C3AED"),
                ("АУТ/ Юридическое обслуживание- Тариф \"КОМФОРТ\" ( 1 месяц )", "Аутсорсинг", "усл.ед.", 10000m, "#7C3AED"),
                ("АУТ/Оказание услуг в рамках юридического абонентского обслуживания", "Аутсорсинг", "усл.ед.", 0m, "#7C3AED"),
                ("АУТ/Тариф \"БАЗОВЫЙ\"", "Аутсорсинг", "усл.ед.", 7000m, "#7C3AED"),
                ("АУТ/Тариф \"БУХ. ЭКОНОМ +\" (ЮР-ЛИЦА)", "Аутсорсинг", "усл.ед.", 10000m, "#7C3AED"),
                ("АУТ/Тариф \"БУХ. ЭКОНОМ\" (ЮР-ЛИЦА)", "Аутсорсинг", "усл.ед.", 5000m, "#7C3AED"),
                ("АУТ/Тариф \"Бухгалтерский Эконом Старт\"", "Аутсорсинг", "усл.ед.", 15000m, "#7C3AED"),
                ("АУТ/Тариф \"Индивидуальный\"", "Аутсорсинг", "усл.ед.", 5000m, "#7C3AED"),
                ("АУТ/Тариф \"ПЕРСОНАЛЬНЫЙ\"", "Аутсорсинг", "усл.ед.", 10000m, "#7C3AED"),
                ("АУТ/Тариф \"СЕМЕЙНЫЙ\" ( ФИЗ-ЛИЦА)", "Аутсорсинг", "усл.ед.", 15000m, "#7C3AED"),
                ("АУТ/Услуги оказанные сверх нормы тарифного плана", "Аутсорсинг", "услуга", 500m, "#7C3AED"),

                ("ГР/ Услуга по направлению документов в суды через ГАС ПРАВОСУДИЕ ( 1 направление)", "Гражданские дела", "усл.ед.", 1000m, "#0F766E"),
                ("ГР/ Анализ документов", "Гражданские дела", "усл.ед.", 6000m, "#0F766E"),
                ("ГР/ Внесудебная работа ( 1 час )", "Гражданские дела", "ч", 2500m, "#0F766E"),
                ("ГР/ Внесудебная работа на выезде по поручению клиента ( 1 час )", "Гражданские дела", "ч", 5000m, "#0F766E"),
                ("ГР/ Ознакомление и изучение материалов дела (1 том)", "Гражданские дела", "усл.ед.", 5000m, "#0F766E"),
                ("ГР/ Ознакомление с материалами дела ( 1 том) в суде ( общая юрисдикция)", "Гражданские дела", "усл.ед.", 7000m, "#0F766E"),
                ("ГР/ Подготовка частной жалобы на определения суда", "Гражданские дела", "усл.ед.", 10000m, "#0F766E"),
                ("ГР/ Составление апелляционной жалобы", "Гражданские дела", "усл.ед.", 15000m, "#0F766E"),
                ("ГР/ Составление возражения (отзыва) на Апелляционную жалобу", "Гражданские дела", "усл.ед.", 15000m, "#0F766E"),
                ("ГР/ Составление возражения (отзыва) на исковое заявления.", "Гражданские дела", "усл.ед.", 10000m, "#0F766E"),
                ("ГР/ Составление возражения (отзыва) на кассационную или надзорную жалобу", "Гражданские дела", "усл.ед.", 20000m, "#0F766E"),
                ("ГР/ Составление заявления в суд", "Гражданские дела", "усл.ед.", 5000m, "#0F766E"),
                ("ГР/ Составление искового заявления.", "Гражданские дела", "усл.ед.", 10000m, "#0F766E"),
                ("ГР/ Составление кассационной и надзорной жалоб", "Гражданские дела", "усл.ед.", 20000m, "#0F766E"),
                ("ГР/ Составление мирового соглашения в рамках гражданского дела", "Гражданские дела", "усл.ед.", 10000m, "#0F766E"),
                ("ГР/ Составление уточненного искового заявления.", "Гражданские дела", "усл.ед.", 8000m, "#0F766E"),
                ("ГР/ Составление ходатайства", "Гражданские дела", "усл.ед.", 1000m, "#0F766E"),
                ("ГР/ Составление частной жалобы на определения суда", "Гражданские дела", "услуга", 8000m, "#0F766E"),
                ("ГР/ Участие в суде I инстанции ( 1 судодень)", "Гражданские дела", "услуга", 10000m, "#0F766E"),
                ("ГР/ Участие в суде апелляционной, кассационной инстанции ( 1 судодень)", "Гражданские дела", "услуга", 15000m, "#0F766E"),
                ("ГР/ Участие представителя в судебном заседании 1 инстанция ( 1 судодень)", "Гражданские дела", "усл.ед.", 10000m, "#0F766E"),
                ("ГР/Гонорар от суммы исковых требований по имущественным спорам 10%", "Гражданские дела", "%", 10m, "#0F766E"),

                ("Задолженность за юридические услуги ( по акту сверки )", "Без группы", "усл.ед.", 0m, "#64748B"),
                ("Задолженность за юридические услуги ( по судебному акту )", "Без группы", "усл.ед.", 0m, "#64748B"),
                ("ИЗГОТОВЛЕНИЕ РЕКЛАМНОЙ ПРОДУКЦИИ", "Без группы", "шт", 0m, "#64748B"),
                ("Подписка на сервис www.kub-24.ru \"Выставление счетов\" на 4 месяца по 100 счетов в месяц", "Без группы", "шт", 0m, "#64748B"),

                ("ИН/ Составление жалобы в конституционный суд РФ", "Иная юридическая помощь", "усл.ед.", 35000m, "#9333EA"),
                ("ИН/ Составление жалобы по правам человека ( Стасбург )", "Иная юридическая помощь", "усл.ед.", 35000m, "#9333EA"),
                ("ИН/ Составление заявления", "Иная юридическая помощь", "усл.ед.", 5000m, "#9333EA"),
                ("ИН/Услуги семейного юриста (1 месяц)", "Иная юридическая помощь", "усл.ед.", 30000m, "#9333EA"),

                ("ИН/Контроль исполнения поручений по заявлениям - учреждениями и гос. и мун органами.", "Прочие услуги", "усл.ед.", 500m, "#F59E0B"),
                ("ИН/Контроль ответов на запросы, жалобы, заявления", "Прочие услуги", "усл.ед.", 0m, "#F59E0B"),
                ("ПР/ Услуга по Направлению/Получению почтовой корреспонденции клиента через отделение Почты России", "Прочие услуги", "усл.ед.", 1000m, "#F59E0B"),
                ("ПР/ Услуга по организации проведения кадастровых, инженерных работ для клиента.", "Прочие услуги", "усл.ед.", 1500m, "#F59E0B"),
                ("ПР/ Формирование пакета документов для иска, заявлений, жалоб и т.д. ( за 1 пакет )", "Прочие услуги", "усл.ед.", 500m, "#F59E0B"),
                ("ПР/ Юридические услуги", "Прочие услуги", "услуга", 10000m, "#F59E0B"),
                ("ПР/Водитель с личным транспортным средством (такси)", "Прочие услуги", "услуга", 0m, "#F59E0B"),
                ("ПР/Контроль назначения даты ознакомления с материалами дела в суде", "Прочие услуги", "усл.ед.", 500m, "#F59E0B"),
                ("ПР/Контроль назначения судебного заседания", "Прочие услуги", "усл.ед.", 500m, "#F59E0B"),
                ("ПР/Контроль обжалования решения суда", "Прочие услуги", "усл.ед.", 500m, "#F59E0B"),
                ("ПР/Контроль оплаты государственных пошлин", "Прочие услуги", "усл.ед.", 100m, "#F59E0B"),
                ("ПР/Контроль получения решения в суде", "Прочие услуги", "усл.ед.", 500m, "#F59E0B"),
                ("ПР/Контроль удостоверения и получения клиентом доверенности у нотариуса", "Прочие услуги", "усл.ед.", 100m, "#F59E0B"),
                ("ПР/Накладные расходы связанные с оказанием юридических услуг клиенту", "Прочие услуги", "усл.ед.", 0m, "#F59E0B"),
                ("ПР/Печать и ксерокопия документов клиента (1 стр.)", "Прочие услуги", "стр.", 10m, "#F59E0B"),
                ("ПР/Распечатка документов с электронного носителя", "Прочие услуги", "усл.ед.", 10m, "#F59E0B"),

                ("РЮП/ Анализ судебной практики по вопросу клиента", "Разовая юридическая помощь", "усл.ед.", 2000m, "#DC2626"),
                ("РЮП/ Ознакомление и анализ с документами представленными клиентом в офисе", "Разовая юридическая помощь", "усл.ед.", 4000m, "#DC2626"),
                ("РЮП/ Ознакомление с документами в месте их нахождения ( в т.ч суде, административном органе)", "Разовая юридическая помощь", "усл.ед.", 6000m, "#DC2626"),
                ("РЮП/ Письменная консультация по вопросу клиента", "Разовая юридическая помощь", "усл.ед.", 5000m, "#DC2626"),
                ("РЮП/ Подготовка ответов на жалобы, претензии, заявления", "Разовая юридическая помощь", "усл.ед.", 5000m, "#DC2626"),
                ("РЮП/ Подготовка проекта доверенности", "Разовая юридическая помощь", "усл.ед.", 1000m, "#DC2626"),
                ("РЮП/ Правовая экспертиза документов ( договоров, соглашений, протоколов и т.д.)", "Разовая юридическая помощь", "усл.ед.", 15000m, "#DC2626"),
                ("РЮП/ Составление запросов", "Разовая юридическая помощь", "усл.ед.", 5000m, "#DC2626"),
                ("РЮП/ Составление заявлений, жалоб, претензий", "Разовая юридическая помощь", "усл.ед.", 8000m, "#DC2626"),
                ("РЮП/ Составление проектов договоров , контрактов, уставов, соглашений", "Разовая юридическая помощь", "усл.ед.", 10000m, "#DC2626"),
                ("РЮП/ Устная консультация", "Разовая юридическая помощь", "усл.ед.", 0m, "#DC2626"),
                ("РЮП/Составление проектов внутренних локально-нормативных документов организации, либо ИП", "Разовая юридическая помощь", "усл.ед.", 8000m, "#DC2626"),
            };

            var categories = await ctx.ServiceCategories.ToListAsync();
            var categoriesByName = categories
                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .GroupBy(x => Norm(x.Name))
                .ToDictionary(g => g.Key, g => g.First());

            var categoriesToAdd = starterCategories
                .Where(x => !categoriesByName.ContainsKey(Norm(x.Name)))
                .Select(x => new ServiceCategoryDictionaryItem
                {
                    Name = x.Name,
                    Description = x.Description,
                    ColorHex = x.ColorHex,
                    IsActive = true
                })
                .ToList();

            if (categoriesToAdd.Count > 0)
            {
                ctx.ServiceCategories.AddRange(categoriesToAdd);
                await ctx.SaveChangesAsync();

                foreach (var category in categoriesToAdd)
                {
                    categoriesByName[Norm(category.Name)] = category;
                }
            }

            var services = await ctx.Services.ToListAsync();
            var servicesByName = services
                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .GroupBy(x => Norm(x.Name))
                .ToDictionary(g => g.Key, g => g.First());

            var hasChanges = false;

            foreach (var starter in starterServices)
            {
                if (!categoriesByName.TryGetValue(Norm(starter.Category), out var category))
                {
                    continue;
                }

                if (servicesByName.TryGetValue(Norm(starter.Name), out var existingService))
                {
                    if (!existingService.CategoryId.HasValue)
                    {
                        existingService.CategoryId = category.Id;
                        hasChanges = true;
                    }

                    if (ShouldApplyStarterUnit(existingService.Unit, starter.Unit))
                    {
                        existingService.Unit = starter.Unit;
                        hasChanges = true;
                    }

                    if (existingService.DefaultPrice == 0m && starter.Price != 0m)
                    {
                        existingService.DefaultPrice = starter.Price;
                        hasChanges = true;
                    }

                    if (string.IsNullOrWhiteSpace(existingService.ColorHex))
                    {
                        existingService.ColorHex = starter.ColorHex;
                        hasChanges = true;
                    }

                    if (!existingService.IsActive)
                    {
                        existingService.IsActive = true;
                        hasChanges = true;
                    }

                    continue;
                }

                var newService = new ServiceDictionaryItem
                {
                    Name = starter.Name,
                    Description = "Преднаполнение по скриншотам КУБА",
                    Unit = starter.Unit,
                    DefaultPrice = starter.Price,
                    ColorHex = starter.ColorHex,
                    IsActive = true,
                    CategoryId = category.Id
                };

                ctx.Services.Add(newService);
                servicesByName[Norm(newService.Name)] = newService;
                hasChanges = true;
            }

            if (hasChanges)
            {
                await ctx.SaveChangesAsync();
            }
        }

        private static bool ShouldApplyStarterUnit(string? currentUnit, string starterUnit)
        {
            if (string.IsNullOrWhiteSpace(currentUnit))
            {
                return true;
            }

            var current = Norm(currentUnit);
            var expected = Norm(starterUnit);

            if (current == expected)
            {
                return false;
            }

            return current switch
            {
                "ШТ" => true,
                "УСЛ. ЕД." when expected == "УСЛ.ЕД." => true,
                "ЧАС" when expected == "Ч" => true,
                _ => false
            };
        }

        // --- РЅРѕСЂРјР°Р»РёР·Р°С†РёСЏ Рё Р±РµР·РѕРїР°СЃРЅС‹Рµ РєР°СЂС‚С‹ РёРјС‘РЅ ---

        private static string Norm(string? s) =>
            (s ?? string.Empty).Trim().ToUpperInvariant();

        private static Dictionary<string, int> BuildNameMap<T>(
            IEnumerable<T> src,
            Func<T, string?> nameSelector,
            Func<T, int> idSelector)
        {
            return src
                .Where(x => !string.IsNullOrWhiteSpace(nameSelector(x)))
                .GroupBy(x => Norm(nameSelector(x)))
                .ToDictionary(g => g.Key, g => idSelector(g.First()));
        }

        private static int GetIdOrThrow(Dictionary<string, int> map, string key, string entityName)
        {
            if (map.TryGetValue(Norm(key), out var id))
                return id;

            throw new InvalidOperationException($"[SEED] {entityName} '{key}' not found (check dictionaries seeding/data).");
        }
    }
}

