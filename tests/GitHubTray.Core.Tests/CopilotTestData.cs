using System.Text.Json.Nodes;

namespace GitHubTray.Core.Tests;

internal static class CopilotTestData
{
    internal static JsonObject Response(string login = "octocat") => new()
    {
        ["login"] = login,
        ["copilot_plan"] = "enterprise",
        ["token_based_billing"] = true,
        ["quota_reset_date"] = "2026-10-01",
        ["quota_reset_date_utc"] = "2026-10-01T00:00:00Z",
        ["quota_snapshots"] = new JsonObject
        {
            ["premium_interactions"] = new JsonObject
            {
                ["unlimited"] = false,
                ["has_quota"] = true,
                ["percent_remaining"] = 75.7,
                ["quota_reset_at"] = 0,
                ["token_based_billing"] = true
            },
            ["chat"] = new JsonObject { ["unlimited"] = true },
            ["completions"] = new JsonObject { ["unlimited"] = true }
        }
    };
}
