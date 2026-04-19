// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabAcacia.A2aBridge;

/// <summary>A2A protocol version implemented by this bridge.</summary>
public static class A2aProtocol
{
    /// <summary>
    /// A2A v0.2 (2024-11) — see
    /// <see href="https://github.com/google/A2A/blob/main/specification/json/a2a.json"/>.
    /// </summary>
    public const string Version = "0.2";
}

// ── AgentCard (/.well-known/agent.json) ──────────────────────────────────────

/// <summary>Top-level AgentCard (A2A §AgentCard).</summary>
public sealed record A2aAgentCard
{
    [JsonPropertyName("name")]         public required string Name { get; init; }
    [JsonPropertyName("description")]  public string? Description { get; init; }
    [JsonPropertyName("url")]          public required string Url { get; init; }
    [JsonPropertyName("provider")]     public A2aAgentProvider? Provider { get; init; }
    [JsonPropertyName("version")]      public required string Version { get; init; }
    [JsonPropertyName("documentationUrl")] public string? DocumentationUrl { get; init; }
    [JsonPropertyName("capabilities")] public required A2aAgentCapabilities Capabilities { get; init; }
    [JsonPropertyName("authentication")] public A2aAgentAuthentication? Authentication { get; init; }

    /// <summary>MIME-ish tags for what input parts the agent accepts (e.g. "text", "data").</summary>
    [JsonPropertyName("defaultInputModes")]
    public IReadOnlyList<string> DefaultInputModes { get; init; } = new[] { "text", "data" };

    /// <summary>MIME-ish tags for what output parts the agent emits.</summary>
    [JsonPropertyName("defaultOutputModes")]
    public IReadOnlyList<string> DefaultOutputModes { get; init; } = new[] { "text", "data" };

    [JsonPropertyName("skills")]
    public required IReadOnlyList<A2aAgentSkill> Skills { get; init; }
}

public sealed record A2aAgentProvider
{
    [JsonPropertyName("organization")] public required string Organization { get; init; }
    [JsonPropertyName("url")]          public string? Url { get; init; }
}

public sealed record A2aAgentCapabilities
{
    [JsonPropertyName("streaming")]           public bool Streaming { get; init; }
    [JsonPropertyName("pushNotifications")]   public bool PushNotifications { get; init; }
    [JsonPropertyName("stateTransitionHistory")] public bool StateTransitionHistory { get; init; }
}

public sealed record A2aAgentAuthentication
{
    /// <summary>Auth schemes (e.g. <c>"bearer"</c>, <c>"apikey"</c>, <c>"oauth2"</c>).</summary>
    [JsonPropertyName("schemes")]
    public required IReadOnlyList<string> Schemes { get; init; }

    [JsonPropertyName("credentials")] public string? Credentials { get; init; }
}

public sealed record A2aAgentSkill
{
    [JsonPropertyName("id")]          public required string Id { get; init; }
    [JsonPropertyName("name")]        public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("tags")]        public IReadOnlyList<string>? Tags { get; init; }
    [JsonPropertyName("examples")]    public IReadOnlyList<string>? Examples { get; init; }
    [JsonPropertyName("inputModes")]  public IReadOnlyList<string>? InputModes { get; init; }
    [JsonPropertyName("outputModes")] public IReadOnlyList<string>? OutputModes { get; init; }
}

// ── Task + Message + Part ────────────────────────────────────────────────────

/// <summary>A2A task state (A2A §Task Lifecycle).</summary>
public static class A2aTaskState
{
    public const string Submitted     = "submitted";
    public const string Working       = "working";
    public const string InputRequired = "input-required";
    public const string Completed     = "completed";
    public const string Canceled      = "canceled";
    public const string Failed        = "failed";
    public const string Unknown       = "unknown";
}

public sealed record A2aTask
{
    [JsonPropertyName("id")]         public required string Id { get; init; }
    [JsonPropertyName("sessionId")]  public string? SessionId { get; init; }
    [JsonPropertyName("status")]     public required A2aTaskStatus Status { get; init; }
    [JsonPropertyName("artifacts")]  public IReadOnlyList<A2aArtifact>? Artifacts { get; init; }
    [JsonPropertyName("history")]    public IReadOnlyList<A2aMessage>? History { get; init; }
    [JsonPropertyName("metadata")]   public JsonElement? Metadata { get; init; }
}

public sealed record A2aTaskStatus
{
    [JsonPropertyName("state")]     public required string State { get; init; }
    [JsonPropertyName("message")]   public A2aMessage? Message { get; init; }

    /// <summary>ISO-8601 timestamp of the most recent state transition.</summary>
    [JsonPropertyName("timestamp")] public string? Timestamp { get; init; }
}

public sealed record A2aMessage
{
    /// <summary>One of <c>"user"</c> or <c>"agent"</c>.</summary>
    [JsonPropertyName("role")]  public required string Role { get; init; }

    [JsonPropertyName("parts")] public required IReadOnlyList<A2aPart> Parts { get; init; }

    [JsonPropertyName("metadata")] public JsonElement? Metadata { get; init; }
}

/// <summary>
/// Discriminated union: <c>type</c> is one of <c>"text"</c>, <c>"data"</c>, <c>"file"</c>.
/// We use a generic envelope rather than a polymorphic hierarchy so each part keeps its
/// full JSON shape round-trippable.
/// </summary>
public sealed record A2aPart
{
    [JsonPropertyName("type")]     public required string Type { get; init; }
    [JsonPropertyName("text")]     public string? Text { get; init; }
    [JsonPropertyName("data")]     public JsonElement? Data { get; init; }
    [JsonPropertyName("file")]     public A2aFile? File { get; init; }
    [JsonPropertyName("metadata")] public JsonElement? Metadata { get; init; }
}

public sealed record A2aFile
{
    [JsonPropertyName("name")]     public string? Name { get; init; }
    [JsonPropertyName("mimeType")] public string? MimeType { get; init; }

    /// <summary>Base64-encoded bytes — mutually exclusive with <see cref="Uri"/>.</summary>
    [JsonPropertyName("bytes")] public string? Bytes { get; init; }

    /// <summary>Absolute URI the client can fetch — mutually exclusive with <see cref="Bytes"/>.</summary>
    [JsonPropertyName("uri")]   public string? Uri { get; init; }
}

public sealed record A2aArtifact
{
    [JsonPropertyName("name")]        public string? Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("parts")]       public required IReadOnlyList<A2aPart> Parts { get; init; }
    [JsonPropertyName("index")]       public int Index { get; init; }
    [JsonPropertyName("append")]      public bool? Append { get; init; }
    [JsonPropertyName("lastChunk")]   public bool? LastChunk { get; init; }
    [JsonPropertyName("metadata")]    public JsonElement? Metadata { get; init; }
}

// ── RPC parameter / result envelopes ─────────────────────────────────────────

public sealed record A2aSendTaskParams
{
    [JsonPropertyName("id")]        public required string Id { get; init; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
    [JsonPropertyName("message")]   public required A2aMessage Message { get; init; }
    [JsonPropertyName("metadata")]  public JsonElement? Metadata { get; init; }
    [JsonPropertyName("acceptedOutputModes")]
    public IReadOnlyList<string>? AcceptedOutputModes { get; init; }

    [JsonPropertyName("historyLength")] public int? HistoryLength { get; init; }
}

public sealed record A2aGetTaskParams
{
    [JsonPropertyName("id")]            public required string Id { get; init; }
    [JsonPropertyName("historyLength")] public int? HistoryLength { get; init; }
}

public sealed record A2aCancelTaskParams
{
    [JsonPropertyName("id")] public required string Id { get; init; }
}
