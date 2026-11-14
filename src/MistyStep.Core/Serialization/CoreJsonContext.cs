using System.Text.Json;
using System.Text.Json.Serialization;
using MistyStep.Core.Config;
using MistyStep.Core.Pairing;

namespace MistyStep.Core.Serialization;

/// <summary>Source-generated JSON metadata for the settings file (indented, camelCase, string enums).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppConfig))]
public sealed partial class CoreJsonContext : JsonSerializerContext;

/// <summary>Compact JSON for the setup code; property names come from the record's attributes.</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SetupInfo))]
public sealed partial class SetupJsonContext : JsonSerializerContext;
