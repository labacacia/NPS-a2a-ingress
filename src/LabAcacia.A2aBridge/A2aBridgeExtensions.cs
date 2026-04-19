// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabAcacia.A2aBridge;

/// <summary>DI + pipeline extensions for the A2A bridge.</summary>
public static class A2aBridgeExtensions
{
    /// <summary>
    /// Register an <see cref="A2aBridge"/> with the given upstream configuration.
    /// The upstream is backed by a typed <c>HttpClient</c> from <c>IHttpClientFactory</c>.
    /// </summary>
    public static IServiceCollection AddA2aBridge(
        this IServiceCollection services,
        Action<A2aBridgeOptions> configure)
    {
        var opts = new A2aBridgeOptions
        {
            Upstream = new A2aUpstream { BaseUrl = new Uri("http://placeholder.invalid") },
        };
        configure(opts);

        if (opts.Upstream is null)
            throw new InvalidOperationException("A2aBridgeOptions.Upstream MUST be configured.");
        if (opts.Upstream.BaseUrl.Host == "placeholder.invalid")
            throw new InvalidOperationException("A2aBridgeOptions.Upstream.BaseUrl MUST be set to the real NWP node URL.");

        services.AddSingleton(opts);
        services.AddHttpClient();
        services.AddSingleton<A2aBridge>(sp =>
        {
            var http   = sp.GetRequiredService<IHttpClientFactory>();
            var client = new NwpUpstreamClient(http.CreateClient("a2a-bridge"), opts.Upstream);
            return new A2aBridge(opts, client, sp.GetService<ILogger<A2aBridge>>());
        });

        return services;
    }

    /// <summary>
    /// Maps both the AgentCard endpoint (<c>/.well-known/agent.json</c>) and the
    /// JSON-RPC RPC endpoint. Default RPC path is <c>/a2a</c>.
    /// </summary>
    public static IEndpointConventionBuilder MapA2aBridge(
        this IEndpointRouteBuilder endpoints,
        string rpcPath = "/a2a",
        string agentCardPath = "/.well-known/agent.json")
    {
        // AgentCard (GET)
        endpoints.MapGet(agentCardPath, async (HttpContext ctx, A2aBridge bridge) =>
        {
            var resolved = bridge.Options.PublicUrl?.ToString().TrimEnd('/')
                           ?? BuildLocalUrl(ctx, rpcPath);

            var card = await bridge.BuildAgentCardAsync(resolved, ctx.RequestAborted);
            ctx.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(ctx.Response.Body, card, A2aBridge.JsonCamel, ctx.RequestAborted);
        });

        // JSON-RPC endpoint (POST)
        return endpoints.MapPost(rpcPath, async (HttpContext ctx, A2aBridge bridge) =>
        {
            if (!ctx.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ?? true)
            {
                ctx.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                return;
            }

            JsonRpcRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<JsonRpcRequest>(
                    ctx.Request.Body, A2aBridge.Json, ctx.RequestAborted);
            }
            catch (JsonException jex)
            {
                await WriteResponse(ctx, new JsonRpcResponse
                {
                    Error = new JsonRpcError { Code = JsonRpcErrorCodes.ParseError, Message = jex.Message },
                });
                return;
            }

            if (req is null || string.IsNullOrEmpty(req.Method))
            {
                await WriteResponse(ctx, new JsonRpcResponse
                {
                    Error = new JsonRpcError { Code = JsonRpcErrorCodes.InvalidRequest, Message = "invalid JSON-RPC request" },
                });
                return;
            }

            var resp = await bridge.DispatchAsync(req, ctx.RequestAborted);

            // Notifications (id == null) MUST NOT produce a response per JSON-RPC 2.0.
            if (req.Id is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            await WriteResponse(ctx, resp);
        });
    }

    private static string BuildLocalUrl(HttpContext ctx, string rpcPath)
    {
        var scheme = ctx.Request.Scheme;
        var host   = ctx.Request.Host.Value;
        return $"{scheme}://{host}{rpcPath}";
    }

    private static async Task WriteResponse(HttpContext ctx, JsonRpcResponse resp)
    {
        ctx.Response.StatusCode  = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(resp, A2aBridge.Json));
        await ctx.Response.Body.WriteAsync(bytes);
    }
}
