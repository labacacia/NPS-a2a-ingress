// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabAcacia.A2aBridge;

/// <summary>
/// Core A2A ↔ NWP dispatcher. Translates A2A JSON-RPC methods into calls on the
/// single upstream NWP node:
/// <list type="bullet">
///   <item><c>tasks/send</c> → Action Node <c>/invoke</c></item>
///   <item><c>tasks/get</c>  → Action Node <c>/invoke</c> with <c>system.task.status</c></item>
///   <item><c>tasks/cancel</c> → Action Node <c>/invoke</c> with <c>system.task.cancel</c></item>
/// </list>
/// <para>
/// Skill selection: <c>tasks/send</c> chooses the upstream action via (in order of
/// preference) <c>params.metadata.skillId</c>, <c>message.metadata.skillId</c>, or a
/// <c>skillId</c>/<c>skill_id</c>/<c>action_id</c> key inside the first <c>data</c>
/// part of the message. Arguments are taken from <c>metadata.params</c> if present,
/// otherwise from the <c>data</c> part minus the skill-id key.
/// </para>
/// </summary>
public sealed class A2aBridge
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>camelCase serializer for A2A responses (AgentCard, Task, etc.).</summary>
    internal static readonly JsonSerializerOptions JsonCamel = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly A2aBridgeOptions _options;
    private readonly NwpUpstreamClient _client;
    private readonly ILogger _log;

    /// <summary>Maps A2A-supplied task ids onto upstream NWP task ids (for async polling).</summary>
    private readonly ConcurrentDictionary<string, UpstreamTaskBinding> _tasks = new();

    public A2aBridge(
        A2aBridgeOptions options,
        NwpUpstreamClient client,
        ILogger<A2aBridge>? logger = null)
    {
        _options = options;
        _client  = client;
        _log     = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Options this bridge was built with — exposed for the endpoint to read
    /// the public URL and metadata when synthesising the AgentCard.</summary>
    public A2aBridgeOptions Options => _options;

    // ── AgentCard ────────────────────────────────────────────────────────────

    /// <summary>
    /// Build the <c>/.well-known/agent.json</c> AgentCard by merging bridge options
    /// with the upstream's <c>/.nwm</c> + <c>/actions</c>. Each NWP action becomes
    /// an A2A skill.
    /// </summary>
    public async Task<A2aAgentCard> BuildAgentCardAsync(string resolvedUrl, CancellationToken ct = default)
    {
        var nwm     = await ReadNwm(ct);
        var actions = await ReadActions(ct);

        var skills = actions.Select(a => new A2aAgentSkill
        {
            Id          = a.Id,
            Name        = a.Id,
            Description = a.Description,
            Tags        = a.Async == true ? new[] { "async" } : null,
        }).ToList();

        return new A2aAgentCard
        {
            Name        = _options.AgentName,
            Description = _options.AgentDescription ?? nwm?.DisplayName,
            Url         = resolvedUrl,
            Version     = _options.AgentVersion,
            Provider = new A2aAgentProvider
            {
                Organization = _options.ProviderOrganization,
                Url          = _options.ProviderUrl,
            },
            DocumentationUrl = _options.DocumentationUrl,
            Capabilities = new A2aAgentCapabilities
            {
                Streaming              = false,
                PushNotifications      = false,
                StateTransitionHistory = false,
            },
            Authentication = _options.AuthSchemes.Count > 0
                ? new A2aAgentAuthentication { Schemes = _options.AuthSchemes }
                : null,
            Skills = skills,
        };
    }

    // ── JSON-RPC dispatch ────────────────────────────────────────────────────

    /// <summary>Dispatches one JSON-RPC request. Returns a populated <see cref="JsonRpcResponse"/>.</summary>
    public async Task<JsonRpcResponse> DispatchAsync(JsonRpcRequest req, CancellationToken ct = default)
    {
        try
        {
            return req.Method switch
            {
                "tasks/send"   => Ok(req.Id, await HandleSend(req.Params, ct)),
                "tasks/get"    => Ok(req.Id, await HandleGet(req.Params, ct)),
                "tasks/cancel" => Ok(req.Id, await HandleCancel(req.Params, ct)),
                "tasks/sendSubscribe" or "tasks/pushNotification/set" or "tasks/pushNotification/get" =>
                    Err(req.Id, JsonRpcErrorCodes.UnsupportedOperation,
                        $"Method '{req.Method}' is not supported — this bridge advertises capabilities.streaming=false and pushNotifications=false."),
                _ => Err(req.Id, JsonRpcErrorCodes.MethodNotFound,
                         $"Method '{req.Method}' is not supported by this bridge."),
            };
        }
        catch (BridgeException bex)
        {
            return Err(req.Id, bex.Code, bex.Message);
        }
        catch (JsonException jex)
        {
            return Err(req.Id, JsonRpcErrorCodes.InvalidParams, jex.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "A2A bridge dispatch failed for method {Method}", req.Method);
            return Err(req.Id, JsonRpcErrorCodes.InternalError, ex.Message);
        }
    }

    // ── tasks/send ───────────────────────────────────────────────────────────

    private async Task<JsonElement> HandleSend(JsonElement? rawParams, CancellationToken ct)
    {
        var p = rawParams?.Deserialize<A2aSendTaskParams>(Json)
            ?? throw new BridgeException(JsonRpcErrorCodes.InvalidParams, "tasks/send requires params.");
        if (string.IsNullOrWhiteSpace(p.Id))
            throw new BridgeException(JsonRpcErrorCodes.InvalidParams, "tasks/send: `id` is required.");
        if (p.Message is null || p.Message.Parts is null || p.Message.Parts.Count == 0)
            throw new BridgeException(JsonRpcErrorCodes.InvalidParams, "tasks/send: `message.parts` must contain at least one part.");

        var (actionId, paramsElement) = ExtractSkill(p);

        var invokeBody = JsonSerializer.SerializeToElement(new
        {
            action_id  = actionId,
            @params    = paramsElement,
            request_id = p.Id,
        }, Json);

        using var resp = await _client.PostInvokeAsync(invokeBody, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var statusCode = (int)resp.StatusCode;
        var nowIso     = DateTimeOffset.UtcNow.ToString("o");

        // 4xx/5xx → task failed with the upstream body surfaced as an agent message.
        if (!resp.IsSuccessStatusCode)
        {
            return Serialize(new A2aTask
            {
                Id        = p.Id,
                SessionId = p.SessionId,
                Status    = new A2aTaskStatus
                {
                    State     = A2aTaskState.Failed,
                    Timestamp = nowIso,
                    Message   = new A2aMessage
                    {
                        Role  = "agent",
                        Parts = new[]
                        {
                            new A2aPart
                            {
                                Type = "text",
                                Text = $"Upstream NWP returned HTTP {statusCode}: {body}",
                            },
                        },
                    },
                },
            });
        }

        // 202 → async task accepted. Upstream body is { task_id, status: "pending", poll_url? }.
        if (statusCode == 202)
        {
            var upstreamTaskId = ExtractTaskId(body);
            _tasks[p.Id] = new UpstreamTaskBinding(actionId, upstreamTaskId);

            return Serialize(new A2aTask
            {
                Id        = p.Id,
                SessionId = p.SessionId,
                Status    = new A2aTaskStatus
                {
                    State     = A2aTaskState.Submitted,
                    Timestamp = nowIso,
                },
            });
        }

        // 200 → sync completion with a CapsFrame-shaped body.
        using var doc = JsonDocument.Parse(body);
        return Serialize(new A2aTask
        {
            Id        = p.Id,
            SessionId = p.SessionId,
            Status    = new A2aTaskStatus
            {
                State     = A2aTaskState.Completed,
                Timestamp = nowIso,
            },
            Artifacts = new[]
            {
                new A2aArtifact
                {
                    Name  = actionId,
                    Index = 0,
                    Parts = new[]
                    {
                        new A2aPart { Type = "data", Data = doc.RootElement.Clone() },
                    },
                },
            },
        });
    }

    // ── tasks/get ────────────────────────────────────────────────────────────

    private async Task<JsonElement> HandleGet(JsonElement? rawParams, CancellationToken ct)
    {
        var p = rawParams?.Deserialize<A2aGetTaskParams>(Json)
            ?? throw new BridgeException(JsonRpcErrorCodes.InvalidParams, "tasks/get requires params.");
        if (!_tasks.TryGetValue(p.Id, out var binding))
            throw new BridgeException(JsonRpcErrorCodes.TaskNotFound,
                $"Task '{p.Id}' is not tracked by this bridge — either it never existed or the bridge restarted.");

        var nowIso = DateTimeOffset.UtcNow.ToString("o");
        var invokeBody = JsonSerializer.SerializeToElement(new
        {
            action_id = "system.task.status",
            @params   = new { task_id = binding.UpstreamTaskId },
        }, Json);

        using var resp = await _client.PostInvokeAsync(invokeBody, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new BridgeException(JsonRpcErrorCodes.UpstreamError,
                $"Upstream task status returned HTTP {(int)resp.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var (a2aState, artifacts) = MapUpstreamStatus(doc.RootElement, binding.ActionId);

        return Serialize(new A2aTask
        {
            Id     = p.Id,
            Status = new A2aTaskStatus { State = a2aState, Timestamp = nowIso },
            Artifacts = artifacts,
        });
    }

    // ── tasks/cancel ─────────────────────────────────────────────────────────

    private async Task<JsonElement> HandleCancel(JsonElement? rawParams, CancellationToken ct)
    {
        var p = rawParams?.Deserialize<A2aCancelTaskParams>(Json)
            ?? throw new BridgeException(JsonRpcErrorCodes.InvalidParams, "tasks/cancel requires params.");
        if (!_tasks.TryGetValue(p.Id, out var binding))
            throw new BridgeException(JsonRpcErrorCodes.TaskNotFound,
                $"Task '{p.Id}' is not tracked by this bridge.");

        var invokeBody = JsonSerializer.SerializeToElement(new
        {
            action_id = "system.task.cancel",
            @params   = new { task_id = binding.UpstreamTaskId },
        }, Json);

        using var resp = await _client.PostInvokeAsync(invokeBody, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new BridgeException(JsonRpcErrorCodes.TaskNotCancelable,
                $"Upstream refused cancel: HTTP {(int)resp.StatusCode} {body}");

        var nowIso = DateTimeOffset.UtcNow.ToString("o");
        return Serialize(new A2aTask
        {
            Id     = p.Id,
            Status = new A2aTaskStatus { State = A2aTaskState.Canceled, Timestamp = nowIso },
        });
    }

    // ── Skill extraction ─────────────────────────────────────────────────────

    /// <summary>
    /// Pull the <c>action_id</c> + params element out of an A2A <c>tasks/send</c> payload.
    /// Lookup order: params.metadata.skillId → message.metadata.skillId → first data-part.
    /// </summary>
    private static (string ActionId, JsonElement Params) ExtractSkill(A2aSendTaskParams p)
    {
        // 1) params.metadata.skillId + params.metadata.params
        if (p.Metadata is { ValueKind: JsonValueKind.Object } pm && TryReadSkill(pm, out var fromRoot, out var fromRootParams))
            return (fromRoot, fromRootParams);

        // 2) message.metadata.skillId + message.metadata.params
        if (p.Message.Metadata is { ValueKind: JsonValueKind.Object } mm && TryReadSkill(mm, out var fromMsg, out var fromMsgParams))
            return (fromMsg, fromMsgParams);

        // 3) first data part: { skillId|skill_id|action_id, params? }
        foreach (var part in p.Message.Parts)
        {
            if (!string.Equals(part.Type, "data", StringComparison.Ordinal)) continue;
            if (part.Data is not { ValueKind: JsonValueKind.Object } data) continue;

            if (TryGetString(data, "skillId", out var s1) ||
                TryGetString(data, "skill_id", out s1) ||
                TryGetString(data, "action_id", out s1))
            {
                // Prefer explicit `params` sub-object, else strip the skill key and pass the rest.
                if (data.TryGetProperty("params", out var explicitParams))
                    return (s1, explicitParams.Clone());

                // Build a filtered object that drops skillId/skill_id/action_id.
                return (s1, StripKeys(data, "skillId", "skill_id", "action_id"));
            }
        }

        throw new BridgeException(JsonRpcErrorCodes.InvalidParams,
            "tasks/send: could not determine the skill id — set `params.metadata.skillId`, `message.metadata.skillId`, or include a `data` part containing `skillId`.");
    }

    private static bool TryReadSkill(JsonElement meta, out string skill, out JsonElement pars)
    {
        skill = string.Empty;
        pars  = default;

        if (TryGetString(meta, "skillId", out var s) ||
            TryGetString(meta, "skill_id", out s) ||
            TryGetString(meta, "action_id", out s))
        {
            skill = s;
            pars  = meta.TryGetProperty("params", out var pp)
                ? pp.Clone()
                : JsonSerializer.SerializeToElement(new { }, Json);
            return true;
        }
        return false;
    }

    private static bool TryGetString(JsonElement obj, string propName, out string value)
    {
        if (obj.TryGetProperty(propName, out var v) && v.ValueKind == JsonValueKind.String)
        {
            value = v.GetString()!;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static JsonElement StripKeys(JsonElement source, params string[] keys)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var prop in source.EnumerateObject())
            {
                if (Array.IndexOf(keys, prop.Name) >= 0) continue;
                prop.WriteTo(w);
            }
            w.WriteEndObject();
        }
        ms.Position = 0;
        return JsonDocument.Parse(ms).RootElement.Clone();
    }

    // ── NWM / actions probes ─────────────────────────────────────────────────

    private async Task<NwmSnapshot?> ReadNwm(CancellationToken ct)
    {
        try
        {
            using var resp = await _client.GetNwmAsync(ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<NwmSnapshot>(body, NwpUpstreamClient.Json);
        }
        catch { return null; }
    }

    private async Task<IReadOnlyList<UpstreamAction>> ReadActions(CancellationToken ct)
    {
        try
        {
            using var resp = await _client.GetActionsAsync(ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<UpstreamAction>();
            var body = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("actions", out var actions)
                || actions.ValueKind != JsonValueKind.Object)
                return Array.Empty<UpstreamAction>();

            var list = new List<UpstreamAction>();
            foreach (var prop in actions.EnumerateObject())
            {
                var desc  = prop.Value.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                var async = prop.Value.TryGetProperty("async", out var a) && a.ValueKind == JsonValueKind.True;
                list.Add(new UpstreamAction(prop.Name, desc, async));
            }
            return list;
        }
        catch { return Array.Empty<UpstreamAction>(); }
    }

    // ── status mapping ───────────────────────────────────────────────────────

    /// <summary>
    /// Map an upstream <c>system.task.status</c> CapsFrame payload onto an A2A task state
    /// (§7.2 in NPS-2-NWP.md). The payload is expected to carry a <c>status</c> string
    /// and, when completed, a <c>result</c> sub-object.
    /// </summary>
    private static (string State, IReadOnlyList<A2aArtifact>? Artifacts) MapUpstreamStatus(
        JsonElement capsule, string actionId)
    {
        // CapsFrame envelope: { anchor_ref, count, data:[{...}], token_est } OR raw result.
        var payload = capsule;
        if (capsule.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
            payload = data[0];

        var rawStatus = payload.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() : null;

        var state = rawStatus switch
        {
            "pending"   => A2aTaskState.Submitted,
            "running"   => A2aTaskState.Working,
            "completed" => A2aTaskState.Completed,
            "failed"    => A2aTaskState.Failed,
            "cancelled" or "canceled" => A2aTaskState.Canceled,
            _           => A2aTaskState.Unknown,
        };

        if (state != A2aTaskState.Completed)
            return (state, null);

        var artifactPart = payload.TryGetProperty("result", out var result)
            ? result.Clone()
            : capsule.Clone();

        var artifacts = new[]
        {
            new A2aArtifact
            {
                Name  = actionId,
                Index = 0,
                Parts = new[]
                {
                    new A2aPart { Type = "data", Data = artifactPart },
                },
            },
        };
        return (state, artifacts);
    }

    private static string ExtractTaskId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("task_id", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString()!;
        }
        catch (JsonException) { /* fall through */ }
        // No task_id present in 202 response — use a deterministic placeholder so `tasks/get`
        // still routes, though the upstream call will likely fail.
        return string.Empty;
    }

    // ── Serialization helpers ────────────────────────────────────────────────

    private static JsonElement Serialize<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonCamel);

    private static JsonRpcResponse Ok(JsonElement? id, JsonElement result) =>
        new() { Id = id, Result = result };

    private static JsonRpcResponse Err(JsonElement? id, int code, string message) =>
        new() { Id = id, Error = new JsonRpcError { Code = code, Message = message } };

    // ── Internal records ─────────────────────────────────────────────────────

    private sealed record NwmSnapshot
    {
        public string? NodeType    { get; init; }
        public string? DisplayName { get; init; }
    }

    private sealed record UpstreamAction(string Id, string? Description, bool Async);

    private sealed record UpstreamTaskBinding(string ActionId, string UpstreamTaskId);
}

/// <summary>Internal exception carrying a JSON-RPC error code.</summary>
internal sealed class BridgeException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
