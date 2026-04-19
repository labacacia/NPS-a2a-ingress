// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace LabAcacia.A2aBridge;

/// <summary>
/// Upstream NWP node that the A2A bridge proxies to. An A2A AgentCard describes a
/// single logical agent — unlike MCP (which multiplexes several upstreams under one
/// server), the A2A bridge binds exactly one upstream per AgentCard.
/// </summary>
public sealed record A2aUpstream
{
    /// <summary>Base URL of the NWP node (no trailing slash).</summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>Optional Agent NID forwarded as <c>X-NWP-Agent</c>.</summary>
    public string? AgentNid { get; init; }

    /// <summary>Optional <c>Authorization</c> header forwarded verbatim.</summary>
    public string? AuthHeader { get; init; }
}

/// <summary>Configuration for <see cref="A2aBridge"/>.</summary>
public sealed class A2aBridgeOptions
{
    /// <summary>AgentCard <c>name</c> — human-readable.</summary>
    public string AgentName { get; set; } = "NPS A2A Bridge";

    /// <summary>AgentCard <c>description</c>.</summary>
    public string? AgentDescription { get; set; }

    /// <summary>AgentCard <c>version</c> (what the upstream advertises, not the bridge itself).</summary>
    public string AgentVersion { get; set; } = "0.1.0";

    /// <summary>
    /// Public URL the AgentCard advertises for this bridge's JSON-RPC endpoint.
    /// A2A clients use this to dispatch <c>tasks/send</c> / <c>tasks/get</c>.
    /// If null, the bridge uses the inbound request's own scheme+host+path at resolve time.
    /// </summary>
    public Uri? PublicUrl { get; set; }

    /// <summary>AgentCard <c>provider.organization</c>.</summary>
    public string ProviderOrganization { get; set; } = "LabAcacia / INNO LOTUS PTY LTD";

    /// <summary>AgentCard <c>provider.url</c>.</summary>
    public string? ProviderUrl { get; set; } = "https://github.com/labacacia/nps";

    /// <summary>AgentCard <c>documentationUrl</c>.</summary>
    public string? DocumentationUrl { get; set; }

    /// <summary>
    /// Authentication schemes advertised in the AgentCard. Empty list means no auth
    /// (bridge-level) — but the upstream may still require its own NIP auth.
    /// </summary>
    public IReadOnlyList<string> AuthSchemes { get; set; } = Array.Empty<string>();

    /// <summary>The single upstream NWP node this bridge fronts.</summary>
    public required A2aUpstream Upstream { get; set; }
}
