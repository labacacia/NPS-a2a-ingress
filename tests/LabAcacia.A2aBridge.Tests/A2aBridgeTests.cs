// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using LabAcacia.A2aBridge;
using Xunit;

namespace LabAcacia.A2aBridge.Tests;

/// <summary>
/// Unit tests for <see cref="global::LabAcacia.A2aBridge.A2aBridge"/>. The upstream NWP
/// Action Node is replaced with a <see cref="StubHandler"/> so we can run without any
/// real HTTP server or network I/O.
/// </summary>
public sealed class A2aBridgeTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── AgentCard ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentCard_MergesUpstreamActionsIntoSkills()
    {
        var (bridge, _) = Build();

        var card = await bridge.BuildAgentCardAsync("https://bridge.test/a2a");

        Assert.Equal("NPS A2A Bridge (Test)", card.Name);
        Assert.Equal("https://bridge.test/a2a", card.Url);
        Assert.Equal(2, card.Skills.Count);
        Assert.Contains(card.Skills, s => s.Id == "orders.create" && s.Tags != null && s.Tags.Contains("async"));
        Assert.Contains(card.Skills, s => s.Id == "orders.cancel");
        Assert.False(card.Capabilities.Streaming);
        Assert.False(card.Capabilities.PushNotifications);
    }

    // ── tasks/send ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TasksSend_WithDataPartSkillId_CallsUpstreamInvoke_AndReturnsCompleted()
    {
        var (bridge, handler) = Build();

        var resp = await bridge.DispatchAsync(new JsonRpcRequest
        {
            Method = "tasks/send",
            Id     = JsonDocument.Parse("1").RootElement,
            Params = JsonSerializer.SerializeToElement(new
            {
                id      = "task-1",
                message = new
                {
                    role  = "user",
                    parts = new object[]
                    {
                        new { type = "data", data = new { skillId = "orders.cancel", orderId = 42 } },
                    },
                },
            }, Json),
        });

        Assert.Null(resp.Error);
        var task = resp.Result!.Value;
        Assert.Equal("task-1", task.GetProperty("id").GetString());
        Assert.Equal("completed", task.GetProperty("status").GetProperty("state").GetString());
        Assert.Equal(1, task.GetProperty("artifacts").GetArrayLength());

        var (_, body) = handler.RequestBodies.Single(r => r.Path.EndsWith("/invoke"));
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("orders.cancel", doc.RootElement.GetProperty("action_id").GetString());
        Assert.Equal("task-1",        doc.RootElement.GetProperty("request_id").GetString());
        Assert.False(doc.RootElement.GetProperty("params").TryGetProperty("skillId", out _));
        Assert.Equal(42, doc.RootElement.GetProperty("params").GetProperty("orderId").GetInt32());
    }

    [Fact]
    public async Task TasksSend_WithMetadataSkillId_UsesExplicitParams()
    {
        var (bridge, handler) = Build();

        var resp = await bridge.DispatchAsync(new JsonRpcRequest
        {
            Method = "tasks/send",
            Id     = JsonDocument.Parse("1").RootElement,
            Params = JsonSerializer.SerializeToElement(new
            {
                id       = "task-2",
                metadata = new { skillId = "orders.cancel", @params = new { orderId = 99 } },
                message  = new
                {
                    role  = "user",
                    parts = new object[] { new { type = "text", text = "please cancel" } },
                },
            }, Json),
        });

        Assert.Null(resp.Error);
        var (_, body) = handler.RequestBodies.Single(r => r.Path.EndsWith("/invoke"));
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("orders.cancel", doc.RootElement.GetProperty("action_id").GetString());
        Assert.Equal(99, doc.RootElement.GetProperty("params").GetProperty("orderId").GetInt32());
    }

    [Fact]
    public async Task TasksSend_Async202_ReturnsSubmitted_AndTracksTask()
    {
        var (bridge, handler) = Build();
        handler.InvokeStatus = HttpStatusCode.Accepted;
        handler.InvokeBody   = """{"task_id":"nwp-7777","status":"pending","poll_url":"/invoke"}""";

        var resp = await bridge.DispatchAsync(new JsonRpcRequest
        {
            Method = "tasks/send",
            Id     = JsonDocument.Parse("1").RootElement,
            Params = JsonSerializer.SerializeToElement(new
            {
                id      = "task-async",
                message = new
                {
                    role  = "user",
                    parts = new object[] { new { type = "data", data = new { skillId = "orders.create" } } },
                },
            }, Json),
        });

        Assert.Null(resp.Error);
        Assert.Equal("submitted",
            resp.Result!.Value.GetProperty("status").GetProperty("state").GetString());
    }

    [Fact]
    public async Task TasksSend_UpstreamError_ReturnsTaskWithFailedState()
    {
        var (bridge, handler) = Build();
        handler.InvokeStatus = HttpStatusCode.BadRequest;
        handler.InvokeBody   = """{"error":"NWP-ACTION-PARAMS-INVALID"}""";

        var resp = await bridge.DispatchAsync(new JsonRpcRequest
        {
            Method = "tasks/send",
            Id     = JsonDocument.Parse("1").RootElement,
            Params = JsonSerializer.SerializeToElement(new
            {
                id      = "task-bad",
                message = new
                {
                    role  = "user",
                    parts = new object[] { new { type = "data", data = new { skillId = "orders.cancel" } } },
                },
            }, Json),
        });

        Assert.Null(resp.Error);
        Assert.Equal("failed",
            resp.Result!.Value.GetProperty("status").GetProperty("state").GetString());
        var msg = resp.Result!.Value.GetProperty("status").GetProperty("message");
        Assert.Equal("agent", msg.GetProperty("role").GetString());
        Assert.Contains("NWP-ACTION-PARAMS-INVALID",
            msg.GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task TasksSend_NoSkillId_ReturnsInvalidParams()
    {
        var (bridge, _) = Build();

        var resp = await bridge.DispatchAsync(new JsonRpcRequest
        {
            Method = "tasks/send",
            Id     = JsonDocument.Parse("1").RootElement,
            Params = JsonSerializer.SerializeToElement(new
            {
                id      = "task-x",
                message = new
                {
                    role  = "user",
                    parts = new object[] { new { type = "text", text = "just talk" } },
                },
            }, Json),
        });

        Assert.NotNull(resp.Error);
        Assert.Equal(JsonRpcErrorCodes.InvalidParams, resp.Error!.Code);
    }

    [Fact]
    public async Task TasksSend_MissingMessage_ReturnsInvalidParams()
    {
        var (bridge, _) = Build();

        var resp = await bridge.DispatchAsync(new JsonRpcRequest
        {
            Method = "tasks/send",
            Id     = JsonDocument.Parse("1").RootElement,
            Params = JsonSerializer.SerializeToElement(new { id = "task-x" }, Json),
        });

        Assert.NotNull(resp.Error);
        Assert.Equal(JsonRpcErrorCodes.InvalidParams, resp.Error!.Code);
    }

    // ── tasks/get ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TasksGet_AfterAsync_MapsUpstreamStatusToWorking_ThenCompleted()
    {
        var (bridge, handler) = Build();

        // 1) Submit an async task.
        handler.InvokeStatus = HttpStatusCode.Accepted;
        handler.InvokeBody   = """{"task_id":"nwp-9999","status":"pending"}""";
        await bridge.DispatchAsync(new JsonRpcRequest
        {
            Method = "tasks/send",
            Id     = JsonDocument.Parse("1").RootElement,
            Params = JsonSerializer.SerializeToElement(new
            {
                id      = "t-async",
                message = new
                {
                    role  = "user",
                    parts = new object[] { new { type = "data", data = new { skillId = "orders.create" } } },
                },
            }, Json),
        });

        // 2) First poll → running.
        handler.InvokeStatus = HttpStatusCode.OK;
        handler.InvokeBody   = """{"anchor_ref":"","count":1,"data":[{"task_id":"nwp-9999","status":"running"}],"token_est":0}""";
        var g1 = await bridge.DispatchAsync(new JsonRpcRequest
        {
            Method = "tasks/get",
            Id     = JsonDocument.Parse("1").RootElement,
            Params = JsonSerializer.SerializeToElement(new { id = "t-async" }, Json),
        });
        Assert.Null(g1.Error);
        Assert.Equal("working",
            g1.Result!.Value.GetProperty("status").GetProperty("state").GetString());

        // 3) Second poll → completed with a result payload.
        handler.InvokeBody = """{"anchor_ref":"","count":1,"data":[{"task_id":"nwp-9999","status":"completed","result":{"order_id":"O-42"}}],"token_est":0}""";
        var g2 = await bridge.DispatchAsync(new JsonRpcRequest
        {
            Method = "tasks/get",
            Id     = JsonDocument.Parse("1").RootElement,
            Params = JsonSerializer.SerializeToElement(new { id = "t-async" }, Json),
        });
        Assert.Null(g2.Error);
        Assert.Equal("completed",
            g2.Result!.Value.GetProperty("status").GetProperty("state").GetString());
        var artifact = g2.Result!.Value.GetProperty("artifacts")[0];
        Assert.Equal("O-42",
            artifact.GetProperty("parts")[0].GetProperty("data").GetProperty("order_id").GetString());

        // 4) Verify the upstream was invoked with system.task.status.
        var statusCalls = handler.RequestBodies
            .Where(r => r.Path.EndsWith("/invoke"))
            .Select(r =>
            {
                using var d = JsonDocument.Parse(r.Body);
                return d.RootElement.GetProperty("action_id").GetString();
            })
            .ToList();
        Assert.Contains("system.task.status", statusCalls);
    }

    [Fact]
    public async Task TasksGet_UnknownTask_ReturnsTaskNotFound()
    {
        var (bridge, _) = Build();

        var resp = await bridge.DispatchAsync(new JsonRpcRequest
        {
            Method = "tasks/get",
            Id     = JsonDocument.Parse("1").RootElement,
            Params = JsonSerializer.SerializeToElement(new { id = "nope" }, Json),
        });

        Assert.NotNull(resp.Error);
        Assert.Equal(JsonRpcErrorCodes.TaskNotFound, resp.Error!.Code);
    }

    // ── tasks/cancel ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TasksCancel_KnownTask_CallsUpstreamCancel_AndReturnsCanceled()
    {
        var (bridge, handler) = Build();

        // Seed: submit async task first.
        handler.InvokeStatus = HttpStatusCode.Accepted;
        handler.InvokeBody   = """{"task_id":"nwp-1","status":"pending"}""";
        await bridge.DispatchAsync(new JsonRpcRequest
        {
            Method = "tasks/send",
            Id     = JsonDocument.Parse("1").RootElement,
            Params = JsonSerializer.SerializeToElement(new
            {
                id      = "t-cancel",
                message = new
                {
                    role  = "user",
                    parts = new object[] { new { type = "data", data = new { skillId = "orders.create" } } },
                },
            }, Json),
        });

        // Cancel: upstream responds 200.
        handler.InvokeStatus = HttpStatusCode.OK;
        handler.InvokeBody   = """{"anchor_ref":"","count":1,"data":[{"task_id":"nwp-1","status":"cancelled"}],"token_est":0}""";
        var resp = await bridge.DispatchAsync(new JsonRpcRequest
        {
            Method = "tasks/cancel",
            Id     = JsonDocument.Parse("1").RootElement,
            Params = JsonSerializer.SerializeToElement(new { id = "t-cancel" }, Json),
        });

        Assert.Null(resp.Error);
        Assert.Equal("canceled",
            resp.Result!.Value.GetProperty("status").GetProperty("state").GetString());

        var cancelCall = handler.RequestBodies.Last(r => r.Path.EndsWith("/invoke"));
        using var doc = JsonDocument.Parse(cancelCall.Body);
        Assert.Equal("system.task.cancel", doc.RootElement.GetProperty("action_id").GetString());
        Assert.Equal("nwp-1", doc.RootElement.GetProperty("params").GetProperty("task_id").GetString());
    }

    [Fact]
    public async Task TasksCancel_UnknownTask_ReturnsTaskNotFound()
    {
        var (bridge, _) = Build();

        var resp = await bridge.DispatchAsync(new JsonRpcRequest
        {
            Method = "tasks/cancel",
            Id     = JsonDocument.Parse("1").RootElement,
            Params = JsonSerializer.SerializeToElement(new { id = "nope" }, Json),
        });

        Assert.NotNull(resp.Error);
        Assert.Equal(JsonRpcErrorCodes.TaskNotFound, resp.Error!.Code);
    }

    // ── Unsupported / unknown methods ────────────────────────────────────────

    [Fact]
    public async Task TasksSendSubscribe_ReturnsUnsupportedOperation()
    {
        var (bridge, _) = Build();

        var resp = await bridge.DispatchAsync(new JsonRpcRequest
        {
            Method = "tasks/sendSubscribe",
            Id     = JsonDocument.Parse("1").RootElement,
        });

        Assert.NotNull(resp.Error);
        Assert.Equal(JsonRpcErrorCodes.UnsupportedOperation, resp.Error!.Code);
    }

    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        var (bridge, _) = Build();

        var resp = await bridge.DispatchAsync(new JsonRpcRequest
        {
            Method = "completely/unknown",
            Id     = JsonDocument.Parse("1").RootElement,
        });

        Assert.NotNull(resp.Error);
        Assert.Equal(JsonRpcErrorCodes.MethodNotFound, resp.Error!.Code);
    }

    // ── Construction guards ──────────────────────────────────────────────────

    [Fact]
    public void AddA2aBridge_MissingUpstream_Throws()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            services.AddA2aBridge(_ => { /* do not override Upstream */ }));
    }

    // ── Test fixtures ────────────────────────────────────────────────────────

    private static (global::LabAcacia.A2aBridge.A2aBridge Bridge, StubHandler Handler) Build()
    {
        var handler = StubHandler.ForActionNode();
        var upstream = new A2aUpstream { BaseUrl = new Uri("https://action.test/orders") };
        var opts = new A2aBridgeOptions
        {
            AgentName    = "NPS A2A Bridge (Test)",
            AgentVersion = "0.1.0-test",
            Upstream     = upstream,
        };
        var client = new NwpUpstreamClient(new HttpClient(handler), upstream);
        return (new global::LabAcacia.A2aBridge.A2aBridge(opts, client), handler);
    }
}

// ── Stub upstream ────────────────────────────────────────────────────────────

/// <summary>
/// In-memory <see cref="HttpMessageHandler"/> emulating an NWP Action Node. Tests flip
/// <see cref="InvokeStatus"/> / <see cref="InvokeBody"/> to simulate sync vs async
/// responses and error conditions.
/// </summary>
internal sealed class StubHandler : HttpMessageHandler
{
    public List<(string Path, string Body)> RequestBodies { get; } = new();

    public string NwmBody     { get; set; } = string.Empty;
    public string ActionsBody { get; set; } = string.Empty;

    public HttpStatusCode InvokeStatus { get; set; } = HttpStatusCode.OK;
    public string         InvokeBody   { get; set; } = """{"anchor_ref":"","count":1,"data":[{"ok":true}],"token_est":0}""";

    public static StubHandler ForActionNode() => new()
    {
        NwmBody     = """{"nwp":"0.4","node_id":"urn:nps:node:test:orders","node_type":"action","display_name":"Orders Agent"}""",
        ActionsBody = """
        {
          "actions": {
            "orders.create": { "description": "Create an order", "async": true },
            "orders.cancel": { "description": "Cancel an order", "async": false }
          }
        }
        """,
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(ct);
            RequestBodies.Add((path, body));
        }

        return path switch
        {
            var p when p.EndsWith("/.nwm")    => Text(NwmBody),
            var p when p.EndsWith("/actions") => Text(ActionsBody),
            var p when p.EndsWith("/invoke")  => new HttpResponseMessage(InvokeStatus)
            {
                Content = new StringContent(InvokeBody, Encoding.UTF8, "application/nwp-capsule"),
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}
