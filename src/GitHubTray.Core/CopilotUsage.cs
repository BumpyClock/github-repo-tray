using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace GitHubTray.Core;

public enum CopilotQuotaKind { PremiumInteractions, Chat, Completions }
public enum CopilotQuotaAvailability { Limited, Unlimited, NotIncluded }

public sealed record CopilotQuota(
    CopilotQuotaKind Kind,
    CopilotQuotaAvailability Availability,
    double? PercentRemaining,
    bool UsesAiCredits,
    DateTimeOffset? ResetsAt);

public sealed record CopilotUsage(string Plan, ImmutableArray<CopilotQuota> Quotas);

public sealed record CopilotUsageSection(CopilotUsage? Usage, DateTimeOffset? UpdatedAt, string? Error)
{
    public DashboardSectionSource Source { get; init; } = DashboardSectionSource.Live;
    public bool IsStale => Error is not null && Usage is not null && UpdatedAt.HasValue;
}

public static class CopilotUsageParser
{
    public const string Endpoint = "copilot_internal/user";

    public static CopilotUsage Parse(JsonElement root, string expectedLogin)
    {
        var login = root.GetProperty("login").GetString();
        if (string.IsNullOrWhiteSpace(login))
        {
            throw new JsonException("Missing Copilot account.");
        }
        if (!string.Equals(login, expectedLogin, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitHubAccountChangedException(
                "The GitHub account changed while loading Copilot usage. Refresh again to load the current account.");
        }

        var plan = root.GetProperty("copilot_plan").GetString();
        if (string.IsNullOrWhiteSpace(plan) || plan.Length > 80 || plan.Any(char.IsControl))
        {
            throw new JsonException("Invalid Copilot plan.");
        }

        var usesAiCredits = OptionalBoolean(root, "token_based_billing") ?? false;
        var resetsAt = ReadDate(root, "quota_reset_date_utc") ?? ReadDate(root, "quota_reset_date");
        var snapshots = root.GetProperty("quota_snapshots");
        var quotas = ImmutableArray.CreateBuilder<CopilotQuota>();
        foreach (var (name, kind) in new[]
        {
            ("premium_interactions", CopilotQuotaKind.PremiumInteractions),
            ("chat", CopilotQuotaKind.Chat),
            ("completions", CopilotQuotaKind.Completions)
        })
        {
            if (!snapshots.TryGetProperty(name, out var quota) || quota.ValueKind == JsonValueKind.Null)
            {
                continue;
            }
            var unlimited = quota.GetProperty("unlimited").GetBoolean();
            var included = OptionalBoolean(quota, "has_quota") ?? true;
            var availability = unlimited ? CopilotQuotaAvailability.Unlimited
                : included ? CopilotQuotaAvailability.Limited : CopilotQuotaAvailability.NotIncluded;
            double? remaining = null;
            if (availability == CopilotQuotaAvailability.Limited)
            {
                remaining = quota.GetProperty("percent_remaining").GetDouble();
                if (!double.IsFinite(remaining.Value) || remaining is < 0 or > 100)
                {
                    throw new JsonException("Invalid Copilot remaining percentage.");
                }
            }

            var quotaReset = resetsAt;
            if (quota.TryGetProperty("quota_reset_at", out var reset) && reset.ValueKind != JsonValueKind.Null)
            {
                var seconds = reset.GetInt64();
                if (seconds < 0)
                {
                    throw new JsonException("Invalid Copilot reset time.");
                }
                if (seconds > 0)
                {
                    quotaReset = DateTimeOffset.FromUnixTimeSeconds(seconds);
                }
            }
            quotas.Add(new(kind, availability, remaining,
                OptionalBoolean(quota, "token_based_billing") ?? usesAiCredits, quotaReset));
        }
        if (quotas.Count == 0)
        {
            throw new JsonException("No supported Copilot quotas were returned.");
        }
        return new(plan, quotas.ToImmutable());
    }

    private static bool? OptionalBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetBoolean() : null;

    private static DateTimeOffset? ReadDate(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
        {
            throw new JsonException("Invalid Copilot reset date.");
        }
        return date;
    }
}
