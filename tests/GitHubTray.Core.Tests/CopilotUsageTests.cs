using System.Text.Json;
using System.Text.Json.Nodes;
using GitHubTray.Core;

namespace GitHubTray.Core.Tests;

public sealed class CopilotUsageTests
{
    [Fact]
    public void CurrentAiCreditResponsePreservesReportedPercentageAndUtcReset()
    {
        var usage = Parse(CopilotTestData.Response());

        Assert.Equal("enterprise", usage.Plan);
        Assert.Equal(3, usage.Quotas.Length);
        var premium = usage.Quotas[0];
        Assert.Equal(CopilotQuotaKind.PremiumInteractions, premium.Kind);
        Assert.Equal(CopilotQuotaAvailability.Limited, premium.Availability);
        Assert.Equal(75.7, premium.PercentRemaining);
        Assert.True(premium.UsesAiCredits);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), premium.ResetsAt);
        Assert.All(usage.Quotas.Skip(1), quota =>
        {
            Assert.Equal(CopilotQuotaAvailability.Unlimited, quota.Availability);
            Assert.Null(quota.PercentRemaining);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(0.05)]
    public void LegacyQuotasKeepTheirUnitsAndBoundaryPercentages(double remaining)
    {
        var response = CopilotTestData.Response();
        response.Remove("token_based_billing");
        response.Remove("quota_reset_date_utc");
        var premium = Premium(response);
        premium.Remove("token_based_billing");
        premium.Remove("has_quota");
        premium["percent_remaining"] = remaining;

        var quota = Parse(response).Quotas[0];

        Assert.False(quota.UsesAiCredits);
        Assert.Equal(remaining, quota.PercentRemaining);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), quota.ResetsAt);
    }

    [Fact]
    public void QuotaBillingFlagAndUnixResetOverrideAccountDefaults()
    {
        var response = CopilotTestData.Response();
        Premium(response)["token_based_billing"] = false;
        var reset = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        Premium(response)["quota_reset_at"] = reset.ToUnixTimeSeconds();

        var quota = Parse(response).Quotas[0];

        Assert.False(quota.UsesAiCredits);
        Assert.Equal(reset, quota.ResetsAt);
    }

    [Fact]
    public void UnlimitedAndNoQuotaDoNotFabricateZeroUsage()
    {
        var response = CopilotTestData.Response();
        var premium = Premium(response);
        premium.Remove("percent_remaining");
        premium["has_quota"] = false;
        var notIncluded = Parse(response).Quotas[0];
        Assert.Equal(CopilotQuotaAvailability.NotIncluded, notIncluded.Availability);
        Assert.Null(notIncluded.PercentRemaining);
        premium["unlimited"] = true;
        var unlimited = Parse(response).Quotas[0];
        Assert.Equal(CopilotQuotaAvailability.Unlimited, unlimited.Availability);
        Assert.Null(unlimited.PercentRemaining);
    }

    [Fact]
    public void MissingOptionalQuotasAndResetStayAbsent()
    {
        var response = CopilotTestData.Response();
        response.Remove("quota_reset_date");
        response.Remove("quota_reset_date_utc");
        response["quota_snapshots"]!.AsObject().Remove("chat");
        response["quota_snapshots"]!.AsObject().Remove("completions");

        var quota = Assert.Single(Parse(response).Quotas);

        Assert.Equal(CopilotQuotaKind.PremiumInteractions, quota.Kind);
        Assert.Null(quota.ResetsAt);
    }

    [Fact]
    public void FreePlanCanExposeLimitedChatAndCompletionsWithoutPremium()
    {
        var response = CopilotTestData.Response();
        response["copilot_plan"] = "free";
        response["token_based_billing"] = false;
        var quotas = response["quota_snapshots"]!.AsObject();
        quotas.Remove("premium_interactions");
        quotas["chat"] = new JsonObject
        {
            ["unlimited"] = false, ["percent_remaining"] = 50
        };
        quotas["completions"] = new JsonObject
        {
            ["unlimited"] = false, ["percent_remaining"] = 25
        };
        var usage = Parse(response);
        Assert.Equal("free", usage.Plan);
        Assert.Equal(
            [CopilotQuotaKind.Chat, CopilotQuotaKind.Completions],
            usage.Quotas.Select(quota => quota.Kind));
        Assert.Equal(
            new double?[] { 50, 25 },
            usage.Quotas.Select(quota => quota.PercentRemaining));
        Assert.All(usage.Quotas, quota =>
        {
            Assert.Equal(CopilotQuotaAvailability.Limited, quota.Availability);
            Assert.False(quota.UsesAiCredits);
        });
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void InvalidPercentageIsRejected(double remaining)
    {
        var response = CopilotTestData.Response();
        Premium(response)["percent_remaining"] = remaining;
        Assert.Throws<JsonException>(() => Parse(response));
    }

    [Theory]
    [InlineData("date")]
    [InlineData("missing-quota")]
    [InlineData("missing-percentage")]
    [InlineData("plan")]
    [InlineData("reset")]
    public void MalformedResponsesAreNotSuccessfulEmptyUsage(string defect)
    {
        var response = CopilotTestData.Response();
        switch (defect)
        {
            case "date": response["quota_reset_date_utc"] = "not a date"; break;
            case "missing-quota": response["quota_snapshots"] = new JsonObject(); break;
            case "missing-percentage": Premium(response)["percent_remaining"] = "not a number"; break;
            case "plan": response["copilot_plan"] = ""; break;
            case "reset": Premium(response)["quota_reset_at"] = -1; break;
        }
        var exception = Record.Exception(() => Parse(response));
        Assert.True(exception is JsonException or InvalidOperationException);
    }

    [Fact]
    public void IdentityIsCheckedBeforePublishingQuotaData()
    {
        Assert.Throws<GitHubAccountChangedException>(() => Parse(CopilotTestData.Response("someone-else")));
        Assert.Equal("enterprise", Parse(CopilotTestData.Response("OCTOCAT")).Plan);
    }

    private static JsonObject Premium(JsonObject response) =>
        response["quota_snapshots"]!["premium_interactions"]!.AsObject();

    private static CopilotUsage Parse(JsonObject response)
    {
        using var document = JsonDocument.Parse(response.ToJsonString());
        return CopilotUsageParser.Parse(document.RootElement, "octocat");
    }
}
