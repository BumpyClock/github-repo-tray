using System.Collections.Immutable;
using System.Collections.ObjectModel;
using GitHubTray.Core;

namespace GitHubTray_App.ViewModels;

public sealed record CopilotQuotaViewModel(CopilotQuota Quota)
{
    public string Plan { get; init; } = "";
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Label => Quota.Kind switch
    {
        CopilotQuotaKind.PremiumInteractions => Quota.UsesAiCredits ? "AI credits" : "Premium requests",
        CopilotQuotaKind.Chat => "Chat",
        CopilotQuotaKind.Completions => "Completions",
        _ => throw new ArgumentOutOfRangeException(nameof(Quota))
    };
    public bool HasMeter => Quota.Availability == CopilotQuotaAvailability.Limited;
    public string MeterAutomationId => $"Copilot{Quota.Kind}Meter";
    public string ValueAutomationId => $"Copilot{Quota.Kind}Value";
    public double UsedPercent => HasMeter ? 100 - Quota.PercentRemaining!.Value : 0;
    public string ValueLabel => Quota.Availability switch
    {
        CopilotQuotaAvailability.Limited => $"{UsedPercent:0.#}% used",
        CopilotQuotaAvailability.Unlimited => "Unlimited",
        CopilotQuotaAvailability.NotIncluded => "No quota",
        _ => throw new ArgumentOutOfRangeException(nameof(Quota))
    };
    public string ResetLabel
    {
        get
        {
            if (Quota.ResetsAt is not { } reset)
            {
                return "Reset time unavailable";
            }
            var remaining = reset - ObservedAt;
            return remaining <= TimeSpan.Zero ? "Reset pending"
                : remaining.TotalDays >= 1 ? $"Resets in {remaining.Days}d {remaining.Hours}h"
                : remaining.TotalHours >= 1 ? $"Resets in {remaining.Hours}h {remaining.Minutes}m"
                : remaining.TotalMinutes >= 1 ? $"Resets in {remaining.Minutes}m"
                : "Resets in <1m";
        }
    }
    public string AccessibleName => $"{Label}: {ValueLabel}. {ResetLabel}. Account-wide usage, not this CLI session.";
}

public sealed record CopilotUsageViewModel(
    string Plan,
    ImmutableArray<CopilotQuotaViewModel> Quotas,
    string Status,
    bool HasError)
{
    public bool HasStatus => Status.Length > 0;

    // Adapt once per binding update; boxed ImmutableArray is not a NativeAOT WinRT ItemsSource.
    public ReadOnlyCollection<CopilotQuotaViewModel> GetQuotaItems() => Array.AsReadOnly(Quotas.ToArray());

    public static CopilotUsageViewModel Create(
        CopilotUsageSection? section, bool verified, bool refreshing, DateTimeOffset? now = null)
    {
        if (!verified)
        {
            return new("", [], refreshing ? "Loading Copilot usage..."
                : "Copilot usage hidden until the GitHub CLI account is verified.", false);
        }

        var usage = section?.Usage;
        var plan = usage?.Plan switch
        {
            null => "",
            "individual" => "Individual",
            "individual_pro" => "Pro",
            "individual_pro_plus" => "Pro+",
            "business" => "Business",
            "enterprise" => "Enterprise",
            "free" => "Free",
            var otherPlan => otherPlan
        };
        var observedAt = now ?? DateTimeOffset.UtcNow;
        var rows = usage?.Quotas.Where(quota => quota.Kind == CopilotQuotaKind.PremiumInteractions ||
                quota.Availability != CopilotQuotaAvailability.Unlimited)
            .Select((quota, index) => new CopilotQuotaViewModel(quota)
            {
                Plan = index == 0 ? plan : "",
                ObservedAt = observedAt
            }).ToImmutableArray() ?? [];
        var status = section?.Error is { } error
            ? $"{(section.IsStale ? "Stale usage" : "Usage unavailable")}: {error}" +
                (section.UpdatedAt is { } updated ? $" Last success {updated.ToLocalTime():g}." : "")
            : section?.Source == DashboardSectionSource.Cached
                ? section.UpdatedAt is { } cachedAt
                    ? refreshing
                        ? $"Cached from {cachedAt.ToLocalTime():g}. Refreshing Copilot usage..."
                        : $"Cached from {cachedAt.ToLocalTime():g}."
                    : refreshing
                        ? "Cached Copilot usage. Refreshing..."
                        : "Cached Copilot usage."
            : refreshing ? "Refreshing Copilot usage..."
            : usage is null ? "Copilot usage has not been loaded."
            : rows.IsEmpty ? "No metered Copilot quota." : "";
        return new(plan, rows, status, section?.Error is not null);
    }
}
