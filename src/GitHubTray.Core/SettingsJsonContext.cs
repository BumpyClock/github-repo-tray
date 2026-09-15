using System.Text.Json.Serialization;

namespace GitHubTray.Core;

[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext;
