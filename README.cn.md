[English Version](./README.md) | 中文版

# LabAcacia.A2aBridge

[![NuGet](https://img.shields.io/nuget/v/LabAcacia.A2aBridge.svg)](https://www.nuget.org/packages/LabAcacia.A2aBridge)

一个 **ASP.NET Core 库**，把单个 **NPS NWP Action / Complex / Gateway Node** 暴露为
[Google Agent-to-Agent (A2A)](https://github.com/google/A2A) 服务端。A2A
对等 Agent 通过 `/.well-known/agent.json` 发现该节点，并用标准 JSON-RPC 2.0
调用 NWP 动作 —— 对 NPS 完全无感。

- **协议**：A2A v0.2 —— JSON-RPC 2.0 over HTTP POST。
- **目标平台**：.NET 10，ASP.NET Core。
- **NWP 规范**：`spec/NPS-2-NWP.md` v0.4（Action Node §7，异步生命周期 §7.2，`system.task.status` / `system.task.cancel` §7.3）。

---

## 功能映射

| A2A 方法                          | NWP 调用                                               | 说明                                                                         |
| --------------------------------- | ------------------------------------------------------ | ---------------------------------------------------------------------------- |
| `GET /.well-known/agent.json`     | `GET /.nwm` + `GET /actions`                           | AgentCard 为每个 NWP 动作生成一个 `skills[]` 条目。                          |
| `tasks/send`                      | `POST /invoke { action_id, params, request_id }`        | 同步 200 → Task(`completed`)，附带 `data` artifact；202 → Task(`submitted`）。 |
| `tasks/get`                       | `POST /invoke { action_id: "system.task.status" }`      | 上游状态 `pending/running/completed/failed/cancelled` 映射到 A2A `submitted/working/completed/failed/canceled`。 |
| `tasks/cancel`                    | `POST /invoke { action_id: "system.task.cancel" }`      | 成功时返回 Task(`canceled`)。                                               |
| `tasks/sendSubscribe`             | —                                                      | 拒绝：`-32004 UnsupportedOperation`（不启用流式）。                          |
| `tasks/pushNotification/*`        | —                                                      | 拒绝：`-32004 UnsupportedOperation`（不启用推送）。                          |

`tasks/send` 的 skill 选择顺序：

1. `params.metadata.skillId`（可选 `params.metadata.params`）。
2. `message.metadata.skillId`（可选 `message.metadata.params`）。
3. `message.parts` 中第一个 `data` part，字段键为 `skillId` / `skill_id` / `action_id`。

---

## 安装

```bash
dotnet add package LabAcacia.A2aBridge
```

---

## 快速开始

```csharp
using LabAcacia.A2aBridge;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRouting();
builder.Services.AddA2aBridge(o =>
{
    o.AgentName        = "OrdersAgent";
    o.AgentDescription = "创建与取消客户订单。";
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

A2A 客户端指向 `https://bridge.example.com/` 即可。

---

## 配置

`A2aBridgeOptions`：

| 属性                     | 默认值                                       | 用途                                                      |
| ------------------------ | -------------------------------------------- | --------------------------------------------------------- |
| `AgentName`              | `NPS A2A Bridge`                             | AgentCard `name`。                                        |
| `AgentDescription`       | `null`（回退到上游 `display_name`）          | AgentCard `description`。                                 |
| `AgentVersion`           | `0.1.0`                                      | AgentCard `version`。                                     |
| `PublicUrl`              | `null`（根据请求自动推导）                   | AgentCard `url` —— 客户端应发起调用的 RPC 端点。          |
| `ProviderOrganization`   | `LabAcacia / INNO LOTUS PTY LTD`             | AgentCard `provider.organization`。                       |
| `ProviderUrl`            | `https://github.com/labacacia/nps`           | AgentCard `provider.url`。                                |
| `DocumentationUrl`       | `null`                                       | AgentCard `documentationUrl`。                            |
| `AuthSchemes`            | `[]`                                         | AgentCard `authentication.schemes`（如 `"bearer"`）。     |
| `Upstream`               | *(必填)*                                     | 本桥接对应的唯一 NWP 节点。                               |

`A2aUpstream`：

| 属性         | 用途                                                                    |
| ------------ | ----------------------------------------------------------------------- |
| `BaseUrl`    | NWP 节点根 URL，挂载 `/.nwm`、`/actions`、`/invoke`。                   |
| `AgentNid`   | 每次调用添加 `X-NWP-Agent` 头（可选）。                                 |
| `AuthHeader` | 每次调用原样作为 `Authorization` 头（可选）。                           |

---

## 错误码映射

标准 JSON-RPC（`-32700` … `-32603`）加上 A2A 应用错误：

| 错误码   | 含义                                                |
| -------- | --------------------------------------------------- |
| `-32001` | task id 未知 —— 本桥接没有该 task 记录。            |
| `-32002` | 上游拒绝取消。                                      |
| `-32004` | 方法未实现（如 streaming / push）。                 |
| `-32010` | 轮询时上游返回非 2xx 状态。                         |

桥接内部的失败被统一包装成返回体 `Task.status.state = "failed"`，message 中承载上游 HTTP
内容，而非 JSON-RPC error，这样 A2A 客户端可以统一按照 task failure 处理。

JSON-RPC *通知*（无 `id`）返回 HTTP `204 No Content`。

---

## task 追踪说明

桥接在进程内维护 `{a2a_task_id → upstream_task_id}` 映射，`tasks/get` 和 `tasks/cancel`
据此将请求重写到正确的上游 task。桥接重启后异步 task 将丢失。生产多副本部署建议
替换为共享存储（v0.1 不覆盖）。

---

## 测试

```bash
dotnet test compat/a2a-bridge/tests/LabAcacia.A2aBridge.Tests/LabAcacia.A2aBridge.Tests.csproj
```

测试基于桩 `HttpMessageHandler`，无需网络。

---

## 许可

Apache 2.0，见仓库根目录 `LICENSE` 与 `NOTICE`。
