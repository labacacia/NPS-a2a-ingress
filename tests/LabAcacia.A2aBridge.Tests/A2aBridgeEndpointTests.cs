// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LabAcacia.A2aBridge;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Xunit;

namespace LabAcacia.A2aBridge.Tests;

/// <summary>
/// End-to-end tests for the middleware-mapped endpoints (<c>/.well-known/agent.json</c>
/// and the JSON-RPC <c>/a2a</c> endpoint), using <see cref="TestServer"/> with a stub
/// upstream transport.
/// </summary>
public sealed class A2aBridgeEndpointTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task AgentCardEndpoint_ReturnsJson_WithResolvedUrl()
    {
        using var server = BuildServer();
        using var client = server.CreateClient();

        var resp = await client.GetAsync("/.well-known/agent.json");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType!.MediaType);

        var card = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("NPS A2A Bridge (Endpoint Test)", card.GetProperty("name").GetString());
        Assert.Contains("orders.cancel",
            card.GetProperty("skills").EnumerateArray()
                .Select(s => s.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task RpcEndpoint_WrongContentType_Returns415()
    {
        using var server = BuildServer();
        using var client = server.CreateClient();

        using var req = new HttpRequestMessage(HttpMethod.Post, "/a2a")
        {
            Content = new StringContent("not json", System.Text.Encoding.UTF8, "text/plain"),
        };
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode);
    }

    [Fact]
    public async Task RpcEndpoint_DispatchesTasksSend_OverJsonRpc()
    {
        using var server = BuildServer();
        using var client = server.CreateClient();

        var body = new
        {
            jsonrpc = "2.0",
            id      = 1,
            method  = "tasks/send",
            @params = new
            {
                id      = "ep-task-1",
                message = new
                {
                    role  = "user",
                    parts = new object[]
                    {
                        new { type = "data", data = new { skillId = "orders.cancel" } },
                    },
                },
            },
        };

        var resp = await client.PostAsJsonAsync("/a2a", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var envelope = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.False(envelope.TryGetProperty("error", out _) &&
                     envelope.GetProperty("error").ValueKind != JsonValueKind.Null);
        Assert.Equal("completed",
            envelope.GetProperty("result").GetProperty("status").GetProperty("state").GetString());
    }

    [Fact]
    public async Task RpcEndpoint_Notification_Returns204NoContent()
    {
        using var server = BuildServer();
        using var client = server.CreateClient();

        // No `id` → JSON-RPC notification; spec forbids a response body.
        var body = new
        {
            jsonrpc = "2.0",
            method  = "tasks/sendSubscribe",
        };
        var resp = await client.PostAsJsonAsync("/a2a", body);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    // ── Host builder with stub upstream wired through IHttpClientFactory ─────

    private static TestServer BuildServer()
    {
        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();

                    // AddA2aBridge registers the real DI graph; we then override just the
                    // HttpMessageHandler for the "a2a-bridge" named client so upstream
                    // traffic hits StubHandler instead of real sockets.
                    services.AddA2aBridge(o =>
                    {
                        o.AgentName = "NPS A2A Bridge (Endpoint Test)";
                        o.Upstream  = new A2aUpstream
                        {
                            BaseUrl = new Uri("https://action.test/orders"),
                        };
                    });

                    services.Configure<HttpClientFactoryOptions>("a2a-bridge", options =>
                    {
                        options.HttpMessageHandlerBuilderActions.Add(b =>
                        {
                            b.PrimaryHandler = StubHandler.ForActionNode();
                        });
                    });
                });

                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapA2aBridge());
                });
            });

        return hostBuilder.Start().GetTestServer();
    }
}
