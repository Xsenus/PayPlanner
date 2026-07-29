using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SmsPhoneNumberSelectorTests
{
    [Fact]
    public void Select_PrefersMobileThenWorkThenBackup()
    {
        var result = SmsPhoneNumberSelector.Select(
        [
            new("+7 999 300-00-00", SmsPhoneNumberKinds.Backup),
            new("+7 999 200-00-00", SmsPhoneNumberKinds.Work),
            new("+7 999 100-00-00", SmsPhoneNumberKinds.Mobile)
        ]);

        Assert.True(result.HasValue);
        Assert.Equal("+79991000000", result.E164);
        Assert.Equal(SmsPhoneNumberKinds.Mobile, result.Kind);
        Assert.Equal(3, result.ValidCandidates);
        Assert.Equal(0, result.RejectedCandidates);
    }

    [Fact]
    public void Select_PrefersPrimaryWithinSameKindAndKeepsSourceOrderAsTieBreaker()
    {
        var result = SmsPhoneNumberSelector.Select(
        [
            new("+7 999 100-00-00", SmsPhoneNumberKinds.Mobile),
            new("+7 999 200-00-00", SmsPhoneNumberKinds.Mobile, true),
            new("+7 999 300-00-00", SmsPhoneNumberKinds.Mobile, true)
        ]);

        Assert.Equal("+79992000000", result.E164);
        Assert.Equal(SmsPhoneNumberKinds.Mobile, result.Kind);
    }

    [Fact]
    public void Select_FallsBackToNextValidCandidate()
    {
        var result = SmsPhoneNumberSelector.Select(
        [
            new("invalid mobile", SmsPhoneNumberKinds.Mobile, true),
            new("+375291234567", SmsPhoneNumberKinds.Work),
            new("8 999 300-00-00", SmsPhoneNumberKinds.Backup)
        ]);

        Assert.Equal("+79993000000", result.E164);
        Assert.Equal(SmsPhoneNumberKinds.Backup, result.Kind);
        Assert.Equal(1, result.ValidCandidates);
        Assert.Equal(2, result.RejectedCandidates);
    }

    [Fact]
    public void Select_UsesApprovedForeignPolicy()
    {
        var policy = new SmsPhoneNumberPolicy(["7", "375"]);
        var result = SmsPhoneNumberSelector.Select(
        [
            new("+375291234567", SmsPhoneNumberKinds.Mobile),
            new("+79992000000", SmsPhoneNumberKinds.Work)
        ],
        policy);

        Assert.Equal("+375291234567", result.E164);
        Assert.Equal(SmsPhoneNumberKinds.Mobile, result.Kind);
    }

    [Fact]
    public void Select_NormalizesUnknownKindAsLowestPriority()
    {
        var result = SmsPhoneNumberSelector.Select(
        [
            new("+79991000000", "personal"),
            new("+79992000000", SmsPhoneNumberKinds.Backup)
        ]);

        Assert.Equal("+79992000000", result.E164);
        Assert.Equal(SmsPhoneNumberKinds.Backup, result.Kind);
    }

    [Fact]
    public void Select_CountsUniqueValidNumbers()
    {
        var result = SmsPhoneNumberSelector.Select(
        [
            new("8 999 100-00-00", SmsPhoneNumberKinds.Work),
            new("+7 999 100-00-00", SmsPhoneNumberKinds.Mobile),
            new("+7 999 200-00-00", SmsPhoneNumberKinds.Backup)
        ]);

        Assert.Equal("+79991000000", result.E164);
        Assert.Equal(2, result.ValidCandidates);
    }

    [Fact]
    public void Select_ReturnsEmptyWhenNoCandidateCanReceiveSms()
    {
        var result = SmsPhoneNumberSelector.Select(
        [
            new(null, SmsPhoneNumberKinds.Mobile),
            new("123", SmsPhoneNumberKinds.Work),
            new("+375291234567", SmsPhoneNumberKinds.Backup)
        ]);

        Assert.False(result.HasValue);
        Assert.Null(result.E164);
        Assert.Null(result.Kind);
        Assert.Equal(0, result.ValidCandidates);
        Assert.Equal(3, result.RejectedCandidates);
        Assert.Equal(1, result.Rejections[SmsPhoneNumberErrorCodes.Empty]);
        Assert.Equal(1, result.Rejections[SmsPhoneNumberErrorCodes.InvalidFormat]);
        Assert.Equal(1, result.Rejections[SmsPhoneNumberErrorCodes.CountryNotAllowed]);
    }

    [Fact]
    public void Select_SkipsUnverifiedServiceAndOptedOutCandidates()
    {
        var result = SmsPhoneNumberSelector.Select(
        [
            new(
                "+79991000000",
                SmsPhoneNumberKinds.Mobile,
                IsVerified: true,
                IsOptedOut: true),
            new(
                "+79992000000",
                SmsPhoneNumberKinds.Work,
                IsVerified: false),
            new(
                "+79993000000",
                SmsPhoneNumberKinds.Backup,
                IsVerified: true,
                IsServiceNumber: true),
            new(
                "+79994000000",
                SmsPhoneNumberKinds.Unspecified,
                IsVerified: true)
        ]);

        Assert.Equal("+79994000000", result.E164);
        Assert.Equal(SmsPhoneNumberKinds.Unspecified, result.Kind);
        Assert.Equal(1, result.ValidCandidates);
        Assert.Equal(3, result.RejectedCandidates);
        Assert.Equal(1, result.Rejections[SmsPhoneCandidateRejectionCodes.OptedOut]);
        Assert.Equal(1, result.Rejections[SmsPhoneCandidateRejectionCodes.Unverified]);
        Assert.Equal(1, result.Rejections[SmsPhoneCandidateRejectionCodes.ServiceNumber]);
    }

    [Fact]
    public void Select_DoesNotNormalizeOrExposeOptedOutNumber()
    {
        var result = SmsPhoneNumberSelector.Select(
        [
            new(
                "sensitive opted-out value",
                SmsPhoneNumberKinds.Mobile,
                IsVerified: true,
                IsOptedOut: true)
        ]);

        Assert.False(result.HasValue);
        Assert.Equal(1, result.Rejections[SmsPhoneCandidateRejectionCodes.OptedOut]);
        Assert.DoesNotContain(
            result.Rejections.Keys,
            code => code == SmsPhoneNumberErrorCodes.InvalidFormat);
    }

    [Fact]
    public void Select_RejectsNullCandidateCollection()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SmsPhoneNumberSelector.Select(null!));
    }
}
