using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Payments;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class PaymentCreateCommandTests
{
    private const string Command = """
        {"date":"2026-10-25T09:00:00Z","plannedDate":"2026-10-25T09:00:00Z","amount":10,"paidAmount":0,"type":"Income","status":"Pending","description":"Payment","isPaid":false,"notes":"","systemNotes":"","rescheduleCount":0}
        """;

    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static (Payment Model, string Identity) Read(string json, JsonSerializerOptions? options = null)
    {
        using var document = JsonDocument.Parse(json);
        Assert.True(PaymentCreateCommand.TryRead(document.RootElement, options ?? Options(), out var model, out var identity, out var error), error);
        Assert.NotNull(model);
        Assert.NotNull(identity);
        Assert.Null(error);
        return (model!, identity!);
    }

    private static void Reject(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(PaymentCreateCommand.TryRead(document.RootElement, Options(), out var model, out var identity, out var error));
        Assert.Null(model);
        Assert.Null(identity);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    private static string WithField(string name, string rawValue)
    {
        using var source = JsonDocument.Parse(Command);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            var replaced = false;
            foreach (var property in source.RootElement.EnumerateObject())
            {
                if (property.Name == name)
                {
                    writer.WritePropertyName(name);
                    writer.WriteRawValue(rawValue);
                    replaced = true;
                }
                else
                {
                    property.WriteTo(writer);
                }
            }
            if (!replaced)
            {
                writer.WritePropertyName(name);
                writer.WriteRawValue(rawValue);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    [Fact]
    public void PropertyOrderDoesNotChangeLogicalIdentity()
    {
        using var source = JsonDocument.Parse(Command);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in source.RootElement.EnumerateObject().Reverse()) property.WriteTo(writer);
            writer.WriteEndObject();
        }

        Assert.Equal(Read(Command).Identity, Read(Encoding.UTF8.GetString(buffer.ToArray())).Identity);
    }

    [Fact]
    public void IdentityUsesVersionNulAndSortedLogicalJsonBeforeModelDefaults()
    {
        const string canonical = """
            {"amount":10,"date":"2026-10-25T09:00:00Z","description":"Payment","isPaid":false,"notes":"","paidAmount":0,"plannedDate":"2026-10-25T09:00:00Z","rescheduleCount":0,"status":"Pending","systemNotes":"","type":"Income"}
            """;
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("payment-create-v1\0" + canonical))).ToLowerInvariant();
        var first = Read(Command);
        first.Model.CreatedAt = DateTime.MinValue;
        first.Model.Id = 123;
        first.Model.SystemNotes = "Server-side changes are outside the logical input";

        Assert.Equal("payment-create-v1", PaymentCreateCommand.Version);
        Assert.Equal(expected, first.Identity);
        Assert.Equal(expected, Read(Command).Identity);
    }

    [Theory]
    [InlineData("10", "10.0")]
    [InlineData("10", "1e1")]
    [InlineData("10", "1000.000e-2")]
    [InlineData("-10", "-0.10e2")]
    [InlineData("0", "-0.0000e999999999999999999999999999999")]
    [InlineData("0.0000000000000000000000000001", "1e-28")]
    public void EqualExactDecimalRationalsHaveTheSameIdentity(string left, string right)
    {
        var a = Read(WithField("amount", left));
        var b = Read(WithField("amount", right));
        Assert.Equal(a.Identity, b.Identity);
        Assert.Equal(a.Model.Amount, b.Model.Amount);
    }

    [Theory]
    [InlineData("1e-29")]
    [InlineData("-1e-29")]
    [InlineData("1.00000000000000000000000000001")]
    [InlineData("79228162514264337593543950336")]
    [InlineData("7922816251426433759354395033.6")]
    [InlineData("1e999999999999999999999999999999")]
    [InlineData("1e-999999999999999999999999999999")]
    public void RejectsPrecisionLossUnderflowAndOverflowInsteadOfRounding(string number)
        => Reject(WithField("amount", number));

    [Theory]
    [InlineData("79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335")]
    [InlineData("7922816251426433759354395033.5")]
    [InlineData("-1e-28")]
    public void AcceptsExactDecimalBoundaries(string number)
        => Assert.NotNull(Read(WithField("amount", number)).Model);

    [Theory]
    [InlineData("-2147483648", -2147483648)]
    [InlineData("2147483647", 2147483647)]
    [InlineData("10.0", 10)]
    [InlineData("1e1", 10)]
    public void BindsExactlyRepresentableInt32Spellings(string number, int expected)
        => Assert.Equal(expected, Read(WithField("rescheduleCount", number)).Model.RescheduleCount);

    [Theory]
    [InlineData("rescheduleCount", "2147483648")]
    [InlineData("rescheduleCount", "-2147483649")]
    [InlineData("rescheduleCount", "0.0000000000000000000000000001")]
    [InlineData("clientId", "0")]
    [InlineData("clientId", "-1")]
    [InlineData("sellerCompanyId", "1.01")]
    [InlineData("sellerBranchId", "2147483648")]
    public void RejectsNonrepresentableOrNonpositiveIds(string field, string number)
        => Reject(WithField(field, number));

    [Fact]
    public void PresenceOfNullIsPartOfIdentity()
    {
        var omitted = Read(Command);
        var present = Read(WithField("clientId", "null"));
        Assert.NotEqual(omitted.Identity, present.Identity);
        Assert.Null(present.Model.ClientId);
        Assert.Null(omitted.Model.ClientId);
    }

    [Theory]
    [InlineData("amount", "10.01")]
    [InlineData("description", "\"Payment \"")]
    [InlineData("date", "\"2026-10-25T09:00:00+00:00\"")]
    [InlineData("sellerCompanyId", "7")]
    [InlineData("paidDate", "null")]
    public void ChangedLogicalFieldsHaveDifferentIdentities(string field, string value)
        => Assert.NotEqual(Read(Command).Identity, Read(WithField(field, value)).Identity);

    [Fact]
    public void IdentityUsesDecodedStringsWithoutTrimmingTheirValues()
    {
        Assert.Equal(Read(WithField("description", "\"A\"")).Identity,
            Read(WithField("description", "\"\\u0041\"")).Identity);
        Assert.NotEqual(Read(WithField("description", "\"A\"")).Identity,
            Read(WithField("description", "\"A \\n\"")).Identity);
    }

    [Theory]
    [InlineData("id", "1")]
    [InlineData("auditCreatedByUserId", "7")]
    [InlineData("createdAt", "\"2026-10-25T09:00:00Z\"")]
    [InlineData("sellerSnapshotJson", "\"{}\"")]
    [InlineData("outstandingAmount", "1")]
    [InlineData("client", "{}")]
    [InlineData("responsibles", "[]")]
    [InlineData("unexpected", "true")]
    public void RejectsUnknownServerOwnedAndNavigationFields(string field, string value)
        => Reject(WithField(field, value));

    [Theory]
    [InlineData("notes", "{}")]
    [InlineData("notes", "[]")]
    [InlineData("notes", "null")]
    [InlineData("amount", "\"10\"")]
    [InlineData("amount", "null")]
    [InlineData("isPaid", "\"false\"")]
    [InlineData("date", "null")]
    [InlineData("advanceReviewStatus", "null")]
    public void RejectsIncorrectScalarShapes(string field, string value)
        => Reject(WithField(field, value));

    [Theory]
    [InlineData("amount")]
    [InlineData("Amount")]
    [InlineData("AMOUNT")]
    public void RejectsDuplicateOrCaseFoldDuplicateFields(string field)
        => Reject(Command[..^1] + ",\"" + field + "\":10}");

    [Fact]
    public void RejectsAliasEvenWithoutACanonicalDuplicate()
        => Reject(Command.Replace("\"amount\":10", "\"Amount\":10", StringComparison.Ordinal));

    [Theory]
    [InlineData("date")]
    [InlineData("plannedDate")]
    [InlineData("amount")]
    [InlineData("paidAmount")]
    [InlineData("type")]
    [InlineData("status")]
    [InlineData("description")]
    [InlineData("isPaid")]
    [InlineData("notes")]
    [InlineData("systemNotes")]
    [InlineData("rescheduleCount")]
    public void RejectsMissingRequiredField(string missing)
    {
        using var source = JsonDocument.Parse(Command);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in source.RootElement.EnumerateObject())
                if (property.Name != missing) property.WriteTo(writer);
            writer.WriteEndObject();
        }
        Reject(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    public void RejectsNonObjectCommand(string json) => Reject(json);

    [Theory]
    [InlineData("\"Invoice\"")]
    [InlineData("\"payment\"")]
    [InlineData("2")]
    [InlineData("null")]
    public void RejectsEveryOtherDocumentKindShape(string kind)
        => Reject(WithField("documentKind", kind));

    [Fact]
    public void BindsEnumsDatesAndNullableFieldsUsingCallerOptions()
    {
        var result = Read(WithField("documentKind", "\"Payment\""));
        Assert.Equal(PaymentType.Income, result.Model.Type);
        Assert.Equal(PaymentStatus.Pending, result.Model.Status);
        Assert.Equal(DocumentKind.Payment, result.Model.DocumentKind);
        Assert.Equal(new DateTime(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc), result.Model.Date);
        Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision,
            Read(WithField("advanceReviewStatus", "\"RequiresDecision\"")).Model.AdvanceReviewStatus);
        Assert.Equal(PaymentOverpaymentHandling.Advance,
            Read(WithField("overpaymentHandling", "\"Advance\"")).Model.OverpaymentHandling);
        Assert.Null(Read(WithField("overpaymentHandling", "null")).Model.OverpaymentHandling);

        using var document = JsonDocument.Parse(Command);
        Assert.False(PaymentCreateCommand.TryRead(document.RootElement, new JsonSerializerOptions(JsonSerializerDefaults.Web),
            out _, out _, out _));
    }

    [Theory]
    [InlineData("date", "\"not-a-date\"")]
    [InlineData("type", "\"UnknownType\"")]
    [InlineData("status", "\"UnknownStatus\"")]
    [InlineData("advanceReviewStatus", "\"999\"")]
    [InlineData("overpaymentHandling", "\"999\"")]
    public void RejectsInvalidBoundDatesAndEnums(string field, string value)
        => Reject(WithField(field, value));

    [Fact]
    public void DoesNotMutateInputOrApplyPaymentBusinessNormalization()
    {
        var json = WithField("amount", "5.005");
        using var document = JsonDocument.Parse(json);
        var original = document.RootElement.GetRawText();
        Assert.True(PaymentCreateCommand.TryRead(document.RootElement, Options(), out var model, out var identity, out var error), error);
        Assert.NotNull(model);
        Assert.NotNull(identity);
        Assert.Equal(5.005m, model!.Amount);
        Assert.Equal(original, document.RootElement.GetRawText());
        Assert.Equal(PaymentStatus.Pending, model.Status);
        Assert.Equal(-3, Read(WithField("rescheduleCount", "-3")).Model.RescheduleCount);
        Assert.Equal(-2m, Read(WithField("paidAmount", "-2")).Model.PaidAmount);
    }
}
