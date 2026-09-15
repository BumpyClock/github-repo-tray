using System.Text.Json.Nodes;

namespace GitHubTray.Core.Tests;

internal static class PullRequestTestData
{
    internal static JsonObject Response(string login = "octocat", string revision = "tray")
    {
        var response = JsonNode.Parse("""
            {
              "data": {
                "viewer": { "login": "octocat" },
                "search": {
                  "nodes": [{
                    "number": 42, "title": "Improve tray",
                    "url": "https://github.com/octocat/tray/pull/42",
                    "updatedAt": "2026-09-01T10:30:00Z", "state": "OPEN", "isDraft": false,
                    "headRefName": "fix/tray", "baseRefName": "main", "reviewDecision": "REVIEW_REQUIRED",
                    "repository": { "nameWithOwner": "octocat/tray" },
                    "author": { "login": "octocat", "avatarUrl": "https://avatars.githubusercontent.com/u/1?v=4" },
                    "comments": { "totalCount": 2 },
                    "labels": { "totalCount": 1, "nodes": [{ "name": "enhancement", "color": "a2eeef" }] },
                    "commits": { "nodes": [{ "commit": {
                      "oid": "0123456789012345678901234567890123456789",
                      "statusCheckRollup": {
                        "state": "PENDING",
                        "contexts": { "totalCount": 3, "nodes": [
                          { "__typename": "CheckRun", "name": "Build", "status": "IN_PROGRESS", "conclusion": null },
                          { "__typename": "CheckRun", "name": "Tests", "status": "QUEUED", "conclusion": null },
                          { "__typename": "StatusContext", "context": "Lint", "state": "SUCCESS" }
                        ] }
                      }
                    } }] }
                  }]
                }
              }
            }
            """)!.AsObject();
        response["data"]!["viewer"]!["login"] = login;
        var pull = Pull(response);
        pull["title"] = $"Improve {revision}";
        pull["url"] = $"https://github.com/{login}/{revision}/pull/42";
        pull["repository"]!["nameWithOwner"] = $"{login}/{revision}";
        pull["author"]!["login"] = login;
        return response;
    }

    internal static JsonObject Empty(string login = "octocat")
    {
        var response = Response(login);
        response["data"]!["search"]!["nodes"] = new JsonArray();
        return response;
    }

    internal static JsonObject Pull(JsonObject response) => response["data"]!["search"]!["nodes"]![0]!.AsObject();
    internal static JsonObject Commit(JsonObject response) => Pull(response)["commits"]!["nodes"]![0]!["commit"]!.AsObject();
    internal static JsonObject Contexts(JsonObject response) => Commit(response)["statusCheckRollup"]!["contexts"]!.AsObject();
}
