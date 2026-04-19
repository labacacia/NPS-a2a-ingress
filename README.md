English | [中文版](./README.cn.md)

# LabAcacia.A2aBridge

[![NuGet](https://img.shields.io/nuget/v/LabAcacia.A2aBridge.svg)](https://www.nuget.org/packages/LabAcacia.A2aBridge)

An **ASP.NET Core library** that exposes a single **NPS NWP Action / Complex /
Gateway Node** as a [Google Agent-to-Agent (A2A)](https://github.com/google/A2A)
server. A2A-speaking peer agents can discover the node via
`/.well-known/agent.json` and invoke NWP actions through standard JSON-RPC 2.0
without knowing anything about NPS.

- **Protocol**: A2A v0.2 — JSON-RPC 2.0 over HTTP POST.
- **Target**: .NET 10, ASP.NET Core.
- **NWP spec**: `spec/NPS-2-NWP.md` v0.4 (Action Node §7, async lifecycle §7.2, `system.task.status` / `system.task.cancel` §7.3).

---

## What it does

| A2A method                       | NWP call                                              | Notes                                                                        |
| -------------------------------- | ----------------------------------------------------- | ---------------------------------------------------------------------------- |
| `GET /.well-known/agent.json`    | `GET /.nwm` + `GET /actions` on the upstream          | AgentCard advertises one `skills[]` entry per NWP action.                    |
| `tasks/send`                     | `POST /invoke { action_id, params, request_id }`       | Sync 200 → Task(`completed`) with a `data` artifact. 202 → Task(`submitted`). |
| `tasks/get`                      | `POST /invoke { action_id: "system.task.status" }`     | Upstream lifecycle `pending/running/completed/failed/cancelled` maps onto A2A `submitted/working/completed/failed/canceled`. |
| `tasks/cancel`                   | `POST /invoke { action_id: "system.task.cancel" }`     | Returns Task(`canceled`) on success.                                         |
| `tasks/sendSubscribe`            | —                                                     | Refused with `-32004 UnsupportedOperation` — streaming is off.               |
| `tasks/pushNotification/*`       | —                                                     | Refused with `-32004 UnsupportedOperation` — push is off.                    |

Skill selection: `tasks/send` picks the upstream `action_id` from (in order) —

1. `params.metadata.skillId` (+ optional `params.metadata.params`).
2. `message.metadata.skillId` (+ optional `message.metadata.params`).
3. The first `data` part in `message.parts`, keyed by `skillId` / `skill_id` / `action_id`.

---

## Install

```bash
dotnet add package LabAcacia.A2aBridge
```

---

## Quick start

```csharp
using LabAcacia.A2aBridge;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRouting();
builder.Services.AddA2aBridge(o =>
{
    o.AgentName        = "OrdersAgent";
    o.AgentDescription = "Create and cancel customer orders.";
    o.AgentVersion     = "1.0.0";
    o.PublicUrl        = new Uri("https://bridge.example.com/a2a");
    o.Upstream         = new A2aUpstream
    {
        BaseUrl    = new Uri("https://action.internal/orders"),
        AgentNid   = "urn:nps:nid:agent:a2a-bridge",
        AuthHeader = "Bearer <service-token>",
    };
});

var app = builder.Build();
app.UseRouting();
app.UseEndpoints(e => e.MapA2aBridge());   // GET /.well-known/agent.json, POST /a2a
app.Run();
```

Point any A2A-compatible client at `https://bridge.example.com/`.

---

## Configuration

`A2aBridgeOptions`:

| Property                 | Default                                   | Purpose                                                    |
| ------------------------ | ----------------------------------------- | ---------------------------------------------------------- |
| `AgentName`              | `NPS A2A Bridge`                          | AgentCard `name`.                                          |
| `AgentDescription`       | `null` (falls back to upstream `display_name`) | AgentCard `description`.                              |
| `AgentVersion`           | `0.1.0`                                   | AgentCard `version`.                                       |
| `PublicUrl`              | `null` (auto-derived from request)        | AgentCard `url` — the RPC endpoint clients should dispatch to. |
| `ProviderOrganization`   | `LabAcacia / INNO LOTUS PTY LTD`          | AgentCard `provider.organization`.                         |
| `ProviderUrl`            | `https://github.com/labacacia/nps`        | AgentCard `provider.url`.                                  |
| `DocumentationUrl`       | `null`                                    | AgentCard `documentationUrl`.                              |
| `AuthSchemes`            | `[]`                                      | AgentCard `authentication.schemes` (e.g. `"bearer"`).      |
| `Upstream`               | *(required)*                              | The single NWP node this bridge fronts.                    |

`A2aUpstream`:

| Property     | Purpose                                                              |
| ------------ | -------------------------------------------------------------------- |
| `BaseUrl`    | Root URL where `/.nwm`, `/actions`, `/invoke` are mounted.           |
| `AgentNid`   | Sent as `X-NWP-Agent` on every upstream call (optional).             |
| `AuthHeader` | Sent verbatim as the `Authorization` header on every call (optional). |

---

## Error mapping

Standard JSON-RPC (`-32700` … `-32603`) plus A2A application errors:

| Code      | Meaning                                                     |
| --------- | ----------------------------------------------------------- |
| `-32001`  | Task id unknown — the bridge has no record of that task.    |
| `-32002`  | Upstream refused to cancel the task.                        |
| `-32004`  | Method is not implemented (e.g. streaming / push).          |
| `-32010`  | Upstream returned a non-success status during polling.      |

Bridge failures appear as the returned `Task.status.state = "failed"` with an
agent message carrying the upstream HTTP body, rather than as JSON-RPC errors —
this lets A2A clients treat them uniformly with other task failures.

JSON-RPC *notifications* (requests without `id`) receive HTTP `204 No Content`.

---

## Task-tracking note

The bridge holds an in-process map of `{a2a_task_id → upstream_task_id}` so that
`tasks/get` and `tasks/cancel` can rewrite the request onto the correct upstream
task. Restarting the bridge forgets async tasks in flight. For production
multi-replica deployments, replace the in-memory dictionary with a shared store
(out of scope for v0.1).

---

## Testing

```bash
dotnet test compat/a2a-bridge/tests/LabAcacia.A2aBridge.Tests/LabAcacia.A2aBridge.Tests.csproj
```

Tests run against a stub `HttpMessageHandler` — no network required.

---

## License

Apache 2.0. See `LICENSE` and `NOTICE` at the repo root.
