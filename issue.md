# 项目问题清单

本文件汇总各轮独立代码审计的发现。新增审计章节追加在文件末尾，不得覆盖或删除已有内容。

---

## Kimi Code 独立审计

### 审计基线和范围

- **审计人**：Kimi Code（第一轮独立审计 agent）。
- **审计日期**：2026-09-17（UTC）。
- **分支 / HEAD**：`main` @ `f3741c0335e68353b32e61d62fae79b071b33c06`（2026-09-16 15:36:16 +0800，"添加了kimi code 套餐，但是存在bug"）。
- **工作区状态**：审计开始时存在未提交修改，全部保留未动：
  - 修改（13 个）：`CLAUDE.md`、`README.md`、`Endpoints/OllamaEndpoints.cs`、`Endpoints/OpenAiEndpoints.cs`、`Services/ModelCatalogService.cs`、`Services/ModelSelectionStore.cs`、`Services/ProviderRegistry.cs`、`docs/API.md`、`docs/ARCHITECTURE.md`、`docs/CONFIGURATION.md`、`tests/ProxyTests/{ModelCatalogServiceTests,ModelSelectionStoreTests,ParameterValidationTests}.cs`
  - 新增未跟踪（5 个）：`Models/ResolvedRoute.cs`、`docs/superpowers/plans/2026-09-16-routing-audit.md`、`docs/testing/2026-09-17-routing-audit.md`、`tests/ProxyTests/{RoutingAuditEndpointTests,StrictRoutingEndpointTests,StrictRoutingTests}.cs`
  - 注意：工作区是符号链接（`D:\Dev\copilot-ai-proxy` → `E:\Datum\Code\AICode\claude\copilot-ai-proxy`），异常堆栈中的路径以后者显示，二者是同一文件。
- **审计对象**：项目自有全部源码（`Program.cs`、`Endpoints/`、`Services/`、`Models/`、`Infrastructure/`）、配置（`config/model-selection/*.json`、`.env.example`、`.csproj`、`.gitignore`、`.dockerignore`）、测试（`tests/ProxyTests/`，20 个测试文件）、CI（`.github/workflows/*.yml`）、部署（`Dockerfile`、`docker-compose.yml`、`scripts/package-release.ps1`）、文档（`README.md`、`CLAUDE.md`、`docs/*.md`）。
- **基线验证**：`dotnet build` 0 警告 0 错误；`dotnet test` **469/469 通过**（.NET SDK 10.0.301）。
- **排除项及理由**：`bin/`、`obj/`（构建产物）；`artifacts/`（打包输出）；`.history/`（编辑器本地历史，已 gitignore）；`images/`（二进制截图）；`scripts/` 下的压测/基准脚本为人工开发工具，仅做表面审查（不属运行时链路）；`docs/superpowers/`、`docs/testing/2026-09-17-routing-audit.md` 是前一轮路由审计的过程产物，仅作上下文阅读，其中已修复的路由问题不重复列入。
- **本次未修改任何业务代码、配置、依赖或测试；未提交、未推送。** 复现脚本全部在临时目录 `C:\Users\jingm\AppData\Local\Temp\kimi-audit`（bash `/tmp/kimi-audit`），使用伪密钥与本地 stub，未触碰真实 provider 与真实密钥。

### 覆盖表

| 模块/目录 | 检查深度 | 执行的检查 | 遗漏及原因 |
|---|---|---|---|
| `Program.cs` | 全量逐行 | .env 加载语义、监听地址、启动时序 | — |
| `Endpoints/OpenAiEndpoints.cs` | 全量逐行 + 端到端复现 | 路由头、failover、流式生命周期、错误处理、Ollama 转换 | — |
| `Endpoints/OllamaEndpoints.cs` | 全量逐行 + 端到端复现 | tags/show/chat、协议转换、上游路径、异常处理 | — |
| `Endpoints/HealthEndpoints.cs` | 全量 | 输出字段、认证交互 | — |
| `Services/ModelCatalogService.cs` | 全量逐行 + 端到端复现 | ResolveRoute 各 tier、发现合并、刷新节流、超时 CTS | — |
| `Services/ProviderRegistry.cs` | 全量逐行 + 端到端复现 | DiscoverProviders、NormalizeBaseUrl、候选解析 | — |
| `Services/ModelSelectionStore.cs` | 全量逐行 | JSON 加载、双目录合并、匹配器分工 | — |
| `Services/RequestTransformer.cs` | 全量逐行 + 端到端复现 | 默认值注入、force 模式、ModifyRequest 缓存键 | — |
| `Services/ChatStreamingService.cs` | 全量逐行 | SSE/NDJSON 转换、缓存写入键 | — |
| `Services/ReasoningCacheService.cs` | 全量逐行 + 端到端复现 | 键生成、容量管理 | — |
| `Services/OllamaResponseBuilder.cs`、`Infrastructure/*`、`Models/*` | 全量 | 序列化契约、HttpClient 工厂、认证中间件 | — |
| `Services/ProviderBenchmarkService.cs` | 全量 | 后台循环、探测成本、报告文件增长 | 真实探测未运行（默认关闭，避免真实 API 调用） |
| `config/model-selection/*.json` | 全部 18 个文件解析校验 | provider 归一、match/enabled 交互、重复 match | 上游目录真实返回 id 无法逐一核对（需真实密钥） |
| `tests/ProxyTests/` | 测试清单 + 重点文件阅读；全量运行 | 469 通过、0 跳过；核对可疑路径是否有测试覆盖 | 未逐行通读全部 20 个测试文件 |
| CI / Docker / 打包脚本 | 全量 | 触发分支、镜像标签、密钥隔离、产物内容 | 未实际构建 Docker 镜像（本机未验证 Docker 可用性） |
| 文档（README/CLAUDE/docs） | 与代码逐项对照 | 错误码、诊断头、参数过滤规则、配置优先级、测试数量 | `README.zh-CN.md` 译文准确性未逐句核对 |

### 已确认问题

#### KIMI-001 `/api/chat` 流式转发硬编码 `v1/chat/completions`，zhipu/google 流式必然失败

- **类型**：功能
- **严重程度**：**P1**。Ollama 兼容面（VS BYOM、`/api/chat`）对流式请求使用硬编码路径，凡 `ChatPath` 不是 `v1/chat/completions` 的 provider（zhipu 为 `v4/chat/completions`、google 为 `v1beta/openai/chat/completions`）流式聊天全部打到错误 URL，上游返回 404，功能整体不可用。
- **状态**：已确认（端到端复现）
- **定位**：`Endpoints/OllamaEndpoints.cs:371`（`new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")`）；对照同文件 `:332`（非流式正确使用 `ollamaProvider.Capabilities.ChatPath`）与 `Endpoints/OpenAiEndpoints.cs:239,316`。
- **预期行为与实际行为**：预期按 `ProviderCapabilitiesRegistry` 中该 provider 的 `ChatPath` 转发（zhipu → `v4/chat/completions`）；实际永远发往 `v1/chat/completions`。
- **触发条件及影响范围**：客户端 POST `/api/chat` 且 `stream:true`，路由目标为 zhipu 或 google（含 display name、`@provider` 形式）。影响这两个 provider 的全部流式 `/api/chat` 流量。
- **根因和证据链**：`Services/ProviderCapabilitiesRegistry.cs:78`（zhipu `v4`）、`:68`（google `v1beta/openai`）定义了正确路径，但流式分支未读取该配置。端到端证据：以本地 stub 作为 zhipu 上游，代理日志中 stub 实际收到 `POST /api/paas/v1/chat/completions`（应为 `/api/paas/v4/chat/completions`）；同模型非流式请求走的是正确 v4 路径（对照成立）。
- **复现步骤**：配置任意 `ChatPath≠v1/chat/completions` 的 provider，POST `/api/chat` `{"model":"<model>@<provider>","stream":true,...}`，观察上游收到的路径。
- **实际验证结果**：已复现（stub 访问日志）。
- **建议修复方向**：改用 `ollamaProvider.Capabilities.ChatPath`；顺手把 `:289` 的硬编码 `"api/chat"` 一并替换（当前恰好等于 ollama 的 ChatPath，属潜在一致性隐患）。
- **回归验证**：新增集成测试：stub 仅监听 `v4/chat/completions`，断言 `/api/chat` 流式到达该路径；现有测试 stub 的 ChatPath 与硬编码值相同，所以从未暴露此问题。

#### KIMI-002 推理缓存读写键不匹配，跨会话串话泄露 reasoning_content

- **类型**：功能 / 安全（隐私）
- **严重程度**：**P1**。任意新会话的第一个无工具调用 assistant 消息会被注入进程早期某个无关会话缓存的推理内容，错误上下文被发往上游 provider：既污染生成质量，也把 A 会话的推理文本泄露进 B 会话的上游请求（共享代理时跨用户）。
- **状态**：已确认（端到端复现）
- **定位**：写入侧 `Services/ReasoningCacheService.cs:11`（`NextAssistantKey()` 全局单调计数）、`Services/ChatStreamingService.cs:89`、`Services/ReasoningCacheService.cs:56`；读取侧 `Services/RequestTransformer.cs:247`（`$"assistant:{idx++}"`，`idx` 每个请求从 0 开始）。
- **预期行为与实际行为**：预期"同一会话"的 assistant 消息取回"该消息对应"的缓存推理；实际读键（每请求 `assistant:0,1,…`）与写键（全局 `assistant:N`，N 单调递增）只有在进程第一次响应时才碰巧相等，此后新会话固定命中最早会话留下的陈旧条目。
- **触发条件及影响范围**：任意一次带 `reasoning_content` 的响应（DeepSeek/Kimi 等推理模型）写缓存后，后续任何会话只要包含无 tool_calls 的 assistant 历史消息即触发注入。`toolcall:` 键路径不受影响（id 由上游生成、天然唯一）。
- **根因和证据链**：两个键空间各自独立设计，从未对齐；单元测试（`tests/ProxyTests/RequestTransformerTests.cs:35`）手工 `Set("assistant:0", …)` 种子，掩盖了集成后的错位。端到端证据：会话 1 流式响应注入 `reasoning_content:"SECRET-REASONING-CONV1"`；会话 2（消息完全不同）转发给上游的请求体中出现 `"reasoning_content":"SECRET-REASONING-CONV1"`（stub 请求日志）。
- **复现步骤**：见"验证命令"一节 harness；先一次流式请求含 reasoning_content，再发一个含 assistant 历史的新会话，检查上游收到的 body。
- **实际验证结果**：已复现。
- **建议修复方向**：缓存键应绑定会话/消息内容（如消息指纹或客户端会话 id），而非全局序号；同时加 TTL/容量上限（见 KIMI-010）。短期止血：禁用无 tool_calls 分支的注入，或写入与读取统一使用同一种派生键。
- **回归验证**：集成测试：会话 A 写缓存后，会话 B（不同消息）断言上游请求体不含 A 的 reasoning_content。

#### KIMI-003 `/api/chat` 全部上游调用无传输错误处理，超时/断连一律 500

- **类型**：可靠性
- **严重程度**：**P1**。`timeout_seconds` 几乎每个模型都有配置（含默认 120–300s），上游稍慢或不可达时 `/api/chat` 抛未处理 `TaskCanceledException`/`HttpRequestException`，客户端收到空 body 的 HTTP 500，Kestrel 刷错误日志；而同一故障在 `/v1/chat/completions` 下会优雅 failover/502。
- **状态**：已确认（端到端复现）
- **定位**：`Endpoints/OllamaEndpoints.cs:273`、`:290`、`:331`、`:377` 四处 `SendAsync` 均无 try/catch；对照 `Endpoints/OpenAiEndpoints.cs:242`、`:331` 的捕获与降级。
- **预期行为与实际行为**：预期上游传输故障/模型超时返回 502/504 JSON 错误或触发 failover；实际未处理异常 → 500 空响应。
- **触发条件及影响范围**：`/api/chat`（流式与非流式、Ollama 直通与 OpenAI 转换两个分支）在上游 DNS 失败、连接拒绝、慢响应触发 `timeout_seconds` 时全部命中。影响 Ollama 兼容面的所有故障场景。
- **根因和证据链**：端点没有任何传输层兜底。端到端证据：上游 stub 挂起 60s、模型配置 `timeout_seconds:2` → `/api/chat` 2.2s 后返回 HTTP 500，代理日志 `System.Threading.Tasks.TaskCanceledException ... at OllamaEndpoints ... line 331`；同一请求打 `/v1/chat/completions` → 2.1s 后 502 `{"error":{...,"code":"upstream_unavailable"}}`。
- **复现步骤**：慢 stub + 短 `timeout_seconds`，POST `/api/chat` `stream:false`。
- **实际验证结果**：已复现（日志含完整堆栈）。
- **建议修复方向**：四处 `SendAsync` 统一包 `(HttpRequestException, OperationCanceledException) when !ct.IsCancellationRequested` → 502 JSON；客户端主动取消仍向上抛。
- **回归验证**：集成测试：挂起 stub + 1s 超时，断言 `/api/chat` 返回 502 而非 500。

#### KIMI-004 `/api/chat` 非流式解析上游响应未判空，tool_calls-only 响应崩溃为 500

- **类型**：可靠性
- **严重程度**：**P2**。工具调用是编码代理的核心路径；上游返回仅含 `tool_calls`、不含 `content` 属性的 message 时（多家 OpenAI 兼容 provider 的实际报文形态），代理抛 `KeyNotFoundException` → 500。`choices` 缺失或为空（如内容审查拦截但返回 200）同样崩溃。
- **状态**：已确认（端到端复现）
- **定位**：`Endpoints/OllamaEndpoints.cs:346`（`GetProperty("choices")[0].GetProperty("message")`）、`:354`（`msg.GetProperty("content")`）。
- **预期行为与实际行为**：预期 `content` 缺省按空串处理、`choices` 为空时返回明确错误；实际直接抛异常。
- **触发条件及影响范围**：POST `/api/chat` `stream:false` 路由到 OpenAI-format provider，且上游 message 无 `content` 键。影响所有经此路径的工具调用响应。
- **根因和证据链**：端到端证据：stub 返回 `{"choices":[{"message":{"role":"assistant","tool_calls":[…]},"finish_reason":"tool_calls"}]}`（无 content 属性）→ HTTP 500，日志 `System.Collections.Generic.KeyNotFoundException at OllamaEndpoints ... line 347`。
- **复现步骤**：stub 上游返回无 content 的 completion，POST `/api/chat` `stream:false`。
- **实际验证结果**：已复现。
- **建议修复方向**：改用 `TryGetProperty` 并容忍缺失/空 `choices`；非 200 与异常形态分开处理。
- **回归验证**：集成测试覆盖：tool_calls-only、content:null、空 choices 三种报文。

#### KIMI-005 `/v1/chat/completions` 对非数组 `messages` 无校验，返回 500 而非 400

- **类型**：接口协议 / 可靠性
- **严重程度**：**P2**。畸形客户端请求（`messages` 为字符串/对象/数字）触发未处理 `InvalidOperationException` → 500，违背该端点自己"畸形 body 是 400 不是 500"的约定（`OpenAiEndpoints.cs:111` 注释）。
- **状态**：已确认（端到端复现）
- **定位**：`Services/RequestTransformer.cs:227`（`msgs.EnumerateArray()`，未验证 `ValueKind == Array`）；调用点 `Endpoints/OpenAiEndpoints.cs:196` 无 try/catch。另 `role` 为非字符串时 `:229` 的 `r.GetString()` 同类。
- **预期行为与实际行为**：预期 400 `invalid_request_error`；实际 500 空响应。
- **触发条件及影响范围**：任何 `messages` 非数组的 POST `/v1/chat/completions`。`/api/chat` 的 `ModifyRequest` 调用点有 try/catch（`OllamaEndpoints.cs:256,308`），但 `ConvertOllamaToOpenAi`（`:414`）对非数组 `messages` 静默丢弃 → 请求被静默改写后发往上游，属同类输入校验缺口。
- **根因和证据链**：端到端证据：`{"messages":"not-an-array"}` 与 `{"messages":{...}}` 均 → HTTP 500，日志 `InvalidOperationException ... at RequestTransformer.ModifyRequest ... line 227 ... at OpenAiEndpoints ... line 196`。
- **复现步骤**：`curl /v1/chat/completions -d '{"model":"m","messages":"x"}'`。
- **实际验证结果**：已复现（两种形态）。
- **建议修复方向**：端点在路由前校验 `messages` 为数组（400）；`ModifyRequest` 内对非数组直接返回 null。
- **回归验证**：参数化测试：messages 为 string/object/number/null → 400。

#### KIMI-006 启动时同步串行拉取全部 provider 模型目录，可用性被最慢上游绑架

- **类型**：可靠性
- **严重程度**：**P2**。`Program.cs:50` 在 `app.Run()` 前 await 全量目录刷新；`ModelCatalogService.RefreshAvailableModels` 逐个 provider 串行 await（`:39-41`），每个 `HttpClient.Timeout=5min`（`ProviderHttpClientFactory.cs:44`）且用 `CancellationToken.None`。一个"黑洞"provider（丢包不拒绝）让启动阻塞 5 分钟，N 个则线性累加，期间端口完全不监听。
- **状态**：已确认（端到端复现，慢目录场景）
- **定位**：`Program.cs:50`；`Services/ModelCatalogService.cs:31-74`（串行循环）；`Infrastructure/ProviderHttpClientFactory.cs:44`。
- **预期行为与实际行为**：预期代理先监听、目录发现后台完成（代码已有 `AvailableModels=[DefaultModel]` 兜底与 `EnsureDefaultModelPresent`）；实际启动被目录发现阻塞。
- **触发条件及影响范围**：任一已配置 provider 目录接口缓慢/挂起。容器编排（docker-compose `restart: unless-stopped`）下表现为长时间未就绪。
- **根因和证据链**：端到端证据：stub 对 `GET /v4/models` 延迟 10s → 代理从启动到开始监听耗时 11s（实测）。
- **复现步骤**：慢 stub + 单 provider，计时到 `/health` 可用。
- **实际验证结果**：已复现（10s 延迟 → 11s 启动）。5 分钟超时路径未完整跑满（耗时原因），超时值取自工厂代码。
- **建议修复方向**：目录发现改为启动后后台任务（首次请求前已有兜底模型）；或为发现单独设短超时（如 10s）并并行化。
- **回归验证**：集成测试：慢目录 stub 下断言 `/health` 在阈值内可用。

#### KIMI-007 目录为空时不记录刷新时间，每个请求都重新串行探测全部 provider

- **类型**：可靠性 / 性能
- **严重程度**：**P2**。`RefreshAvailableModels` 在 `claimsByUpstream.Count == 0` 时提前 return（`ModelCatalogService.cs:76-79`），不更新 `ModelsLastRefreshUtc` → `RefreshAvailableModelsIfNeeded` 的 5 分钟节流失效 → 每个 `/api/tags`、`/api/chat` 请求（`/v1/models` 也会触发）都重新对所有 provider 串行发目录请求。故障期间每个客户端请求被叠加 N×上游超时 的延迟，且无并发去重。
- **状态**：已确认（端到端复现）
- **定位**：`Services/ModelCatalogService.cs:21-29`（节流判据）、`:76-79`（空结果提前返回）、`:142`（仅成功路径更新时间）。
- **预期行为与实际行为**：预期空结果也按 5 分钟节流（负缓存）；实际 `/health` 显示 `models_last_refresh_utc: 0001-01-01`，每个请求都产生上游探测。
- **触发条件及影响范围**：所有 provider 目录失败、或目录有数据但无一命中 config allowlist 的部署。正是代理最需要保持响应的故障窗口。
- **根因和证据链**：端到端证据：stub 返回空目录 → 3 次 `/api/tags` 产生 3 次上游 `GET /v4/models`（1:1）；2 次 `/v1/models` 再增 2 次；`/health` 时间戳始终 MinValue。
- **复现步骤**：空目录 stub，连续请求 `/api/tags` 观察上游命中次数。
- **实际验证结果**：已复现。
- **建议修复方向**：空结果也更新 `ModelsLastRefreshUtc`（或单独负缓存 TTL）；`RefreshAvailableModelsIfNeeded` 加并发闸（SemaphoreSlim/惰性单任务）。
- **回归验证**：集成测试：空目录下连续 3 请求，上游探测次数 = 1。

#### KIMI-008 `.env` 无条件覆盖系统环境变量，与文档声明的优先级相反

- **类型**：工程规范 / 安全（运维）
- **严重程度**：**P2**。`Program.cs:12-24` 对 `.env` 每个键无条件 `SetEnvironmentVariable`，使 `.env` 优先于系统环境变量；`CLAUDE.md:167-172` 明确声明优先级为"1. 系统环境变量 2. .env 文件"。运维在系统层改 `PROXY_PORT`/`PROXY_API_KEY`/轮换密钥会被仓库遗留 `.env` 静默压过，含安全语义（旧 `PROXY_API_KEY` 复活）。
- **状态**：已确认（端到端复现）
- **定位**：`Program.cs:21-23`。
- **预期行为与实际行为**：预期系统环境变量优先（文档）；实际 `.env` 优先（代码）。
- **触发条件及影响范围**：同时存在 `.env` 与系统环境变量的所有部署。
- **根因和证据链**：端到端证据：进程环境 `PROXY_PORT=18348` + CWD `.env` 写 `PROXY_PORT=18499` → 代理实际监听 18499。附带小问题：`.env` 解析不剥离行尾注释（`KEY=value # note` 会把注释吃进 value），`Trim('"')` 只处理双引号。
- **复现步骤**：见上。
- **实际验证结果**：已复现。
- **建议修复方向**：仅在系统未设置时写入（`GetEnvironmentVariable` 判空），或修改文档匹配代码；支持行尾注释与单引号。
- **回归验证**：单元/集成测试固定优先级语义。

#### KIMI-009 `NormalizeBaseUrl` 只折叠单段版本前缀，Google 官方 Base URL 粘贴后路径翻倍 404

- **类型**：功能
- **严重程度**：**P2**。google 的 `ChatPath` 是双段 `v1beta/openai/chat/completions`；用户按 Google 官方文档把 `PROVIDER_GOOGLE_BASE_URL` 设为 `https://generativelanguage.googleapis.com/v1beta/openai` 时，折叠逻辑只识别末尾 `/v1beta`，不匹配 → 上游路径变成 `/v1beta/openai/v1beta/openai/chat/completions` → 404，google provider 实际不可用。这正是该函数（`ProviderRegistry.cs:286-292` 注释）声称要防的事故，但实现只覆盖单段情形。
- **状态**：已确认（端到端复现）
- **定位**：`Services/ProviderRegistry.cs:293-307`；`Services/ProviderCapabilitiesRegistry.cs:68`。
- **预期行为与实际行为**：预期折叠为 `.../v1beta/openai/chat/completions`；实际目录请求打到 `/v1beta/openai/v1beta/openai/models`。
- **触发条件及影响范围**：用户为 google 显式配置官方文档版 Base URL。默认值（不设置）不受影响。
- **根因和证据链**：端到端证据：stub 日志 `GET /v1beta/openai/v1beta/openai/models`。
- **复现步骤**：`PROVIDER_GOOGLE_BASE_URL=<stub>/v1beta/openai` 启动，观察发现请求路径。
- **实际验证结果**：已复现。
- **建议修复方向**：折叠逻辑按 `ChatPath` 的最长前缀后缀匹配（而非首段），或对 google 这类双段前缀专门处理。
- **回归验证**：单测：`…/v1beta/openai` + google caps → 不重复；现有 `…/v1` 情形不回归。

#### KIMI-010 `ReasoningCacheService` 无界增长

- **类型**：可靠性 / 性能
- **严重程度**：**P2**。`_reasoningCache`（`ReasoningCacheService.cs:6`）无容量上限、无 TTL、无淘汰；每次带推理内容的响应（流式或非流式）永久新增一条，长驻进程内存单调上涨。与 KIMI-002 叠加后，陈旧条目还长期可被错误命中。
- **状态**：已确认（静态证据链完整；未做长跑内存实测——需长时间运行，标注未运行验证）
- **定位**：`Services/ReasoningCacheService.cs:6,13-16`。
- **预期行为与实际行为**：预期有界缓存；实际只增不减。
- **触发条件及影响范围**：任何长期运行且使用推理模型的部署。
- **根因和证据链**：代码通读：`Set` 仅 `_reasoningCache[key] = value`，全类无 Remove/Clear/过期。
- **复现步骤**：静态即可判定；如需实测，循环发起推理请求观察进程内存。
- **实际验证结果**：未运行验证（静态证据充分）。
- **建议修复方向**：LRU + 容量上限（如 1000 条）或 TTL（如 1h）。
- **回归验证**：单测：写入超过容量后条目数封顶。

#### KIMI-011 `/v1/chat/completions` 流式在任何字节发出前失败也不 failover

- **类型**：可靠性
- **严重程度**：**P2**。流式分支只取 `candidates[0]`（`OpenAiEndpoints.cs:285-286`），`SendAsync` 失败直接 502（`:331-339`）。注释与文档（`docs/ARCHITECTURE.md:257`、`CLAUDE.md:92`）给出的理由是"headers already sent"，但此刻响应尚未开始（`HttpResponse.HasStarted=false`——同一段代码自己就在失败后把状态改成 502 并写 JSON，证明可以安全改答），完全有条件先试下一个候选。流式可用性因此显著低于非流式。
- **状态**：已确认（静态证据链；行为差异由 KIMI-003 的对照实验间接证实）
- **定位**：`Endpoints/OpenAiEndpoints.cs:285-353`。
- **预期行为与实际行为**：预期首字节前失败可 failover 到下一候选；实际单候选失败即终态。
- **触发条件及影响范围**：多候选裸模型的流式请求，首选 provider 传输故障时。上游已吐字节后的中途失败确实无法 failover（那是真约束）。
- **根因和证据链**：代码路径通读 + 非流式分支 failover 对照。
- **复现步骤**：双候选模型，首选 provider 指向死端口，`stream:true` → 502 而不试次选。
- **实际验证结果**：未单独运行该双候选场景（静态证据充分，且失败响应改写已被同函数自身证明可行）。
- **建议修复方向**：在 `HasStarted==false` 窗口内按候选列表重试；首个候选开始写字节后再禁止 failover。
- **回归验证**：集成测试：首选死、次选活，断言流式成功且响应来自次选。

#### KIMI-012 `/v1/chat/completions` 的 Ollama-format 候选无传输容错，failover 链在 ollama 节点断裂

- **类型**：可靠性
- **严重程度**：**P2**。非流式 failover 循环中，OpenAI-format 候选的 `SendAsync` 有 catch 继续下一候选（`OpenAiEndpoints.cs:242-248`），但 Ollama-format 候选走 `TryHandleOllamaCloudChatCompletion`，其 `SendAsync` 无包装（`:431-433`）：ollama provider 不可达时异常直接冒泡为 500，剩余候选不再尝试。流式路径 `HandleOllamaCloudChatCompletion`（`:459-461`）同样无包装。
- **状态**：已确认（静态；与 KIMI-003 同类根因，位于 /v1 面）
- **定位**：`Endpoints/OpenAiEndpoints.cs:224-231`、`:431-433`、`:459-461`。
- **预期行为与实际行为**：预期 ollama 候选传输失败时继续 failover 或返回 502；实际 500 并中断候选链。
- **触发条件及影响范围**：`/v1/chat/completions` 路由到 Ollama-format provider（ollama cloud/local）且其不可达。
- **根因和证据链**：代码对照明显；KIMI-003 已用同类场景证明未包装 `SendAsync` 的最终表现（500）。
- **复现步骤**：ollama provider 指向死端口，POST `/v1/chat/completions`。
- **实际验证结果**：未运行验证（需要 ollama-format provider 配置；静态证据链完整）。
- **建议修复方向**：与 KIMI-003 一致的传输异常兜底。
- **回归验证**：集成测试断言 502 且 failover 继续。

#### KIMI-013 `/api/chat` → Ollama-format provider 直通路径跳过执行默认值与 override_client_params

- **类型**：功能
- **严重程度**：**P2**。`/api/chat` 命中 Ollama-format provider 时仅做 model 替换（`OllamaEndpoints.cs:268`），不调用 `ApplyExecutionDefaults`；而 OpenAI-format 路径（`:321`）与 `/v1` 面（`OpenAiEndpoints.cs:218,298`）都会注入。`config/model-selection/ollamacloud.json` 的条目配置了 `temperature/top_p/max_tokens/timeout_seconds`，其中 `kimi2.7-code` 还带 `override_client_params:true`+`temperature:1.0`（CLAUDE.md:161-165 记载该机制专为"模型强制 temperature=1.0"设计）——这些在 `/api/chat` 直通路径全部静默失效。
- **状态**：已确认（静态证据链；上游是否真拒绝非 1.0 温度未对真实 Ollama Cloud 验证）
- **定位**：`Endpoints/OllamaEndpoints.cs:252-303`；`config/model-selection/ollamacloud.json`（kimi2.7-code 条目）。
- **预期行为与实际行为**：预期配置的默认值/强制值对两条 API 面一致生效；实际 Ollama 直通路径被跳过（timeout 除外——`timeout_seconds` 经 `CreateModelTimeoutCts` 生效）。
- **触发条件及影响范围**：`/api/chat` 且路由到 ollama provider 的全部请求。
- **根因和证据链**：代码对照 + 配置文件实读（ollamacloud.json 48 处 temperature/top_p/max_tokens 配置）。
- **复现步骤**：stub 冒充 ollama 上游，比较 `/api/chat` 与 `/v1/chat/completions` 转发 body 中的 options。
- **实际验证结果**：未运行验证（静态证据充分）。
- **建议修复方向**：直通前把 execution 默认值映射进 Ollama `options` 块（`num_predict`/`temperature`/`top_p`），并遵守 `override_client_params`。
- **回归验证**：集成测试：配置 temperature 的 ollama 模型，断言直通 body `options.temperature` 被注入/强制。

#### KIMI-014 默认绑定 `0.0.0.0` 且默认无认证，局域网可直接消耗上游付费密钥

- **类型**：安全
- **严重程度**：**P2**。`Program.cs:32` 固定 `http://0.0.0.0:{port}`；`PROXY_API_KEY` 默认空 = 无认证（`Infrastructure/ProxyAuthenticationMiddleware.cs:5-8`）。`docker-compose.yml` 默认把端口映射到宿主全部接口。同一局域网（或误暴露的公网接口）任何人都能调用代理、消耗全部已配置 provider 的付费额度。README 把它定位为 "local endpoint"，与实际监听范围不符。
- **状态**：已确认（静态；监听行为已由本轮复现 harness 的 `netstat` 输出佐证 `0.0.0.0`）
- **定位**：`Program.cs:30-32`；`docker-compose.yml:6-7`；`Infrastructure/ProxyAuthenticationMiddleware.cs`。
- **预期行为与实际行为**：预期默认仅监听 `127.0.0.1`（README 的本地定位）；实际全接口裸露。
- **触发条件及影响范围**：一切未设 `PROXY_API_KEY` 的部署；共享网络环境。
- **根因和证据链**：代码与 compose 文件直读。
- **复现步骤**：`netstat` 观察 `0.0.0.0:11434` LISTENING。
- **实际验证结果**：已确认监听全网卡。
- **建议修复方向**：默认绑 `127.0.0.1`，显式 `PROXY_BIND=0.0.0.0` 才放开；或 Docker 镜像默认要求 `PROXY_API_KEY`；至少在 README/compose 注释中醒目警示。
- **回归验证**：启动测试断言默认监听地址。

### 待验证风险

#### KIMI-015 `config/model-selection/google.json` 全部 match 带 `models/` 前缀，疑似永远匹配不到目录

- **类型**：功能 / 配置
- **严重程度**：**P2**（若属实，google provider 的模型全部不曝光；显式请求还会把带前缀的 id 原样发给上游）
- **状态**：待验证
- **定位**：`config/model-selection/google.json`（`models/gemini-2.5-pro` 等 6 条）；匹配逻辑 `Services/ModelSelectionStore.cs:98`（子串方向为 `discoveredId.Contains(configuredMatch)`）。
- **预期行为与实际行为**：预期 match 与 Google OpenAI 兼容目录返回的 id 对齐；实际疑似不一致：Google OpenAI 兼容面的公开文档与社区示例一律使用裸 id（如 `gemini-2.5-flash`），第三方 issue 亦显示带 `models/` 前缀请求会报错（如 [open-codesign#335](https://github.com/OpenCoworkAI/open-codesign/issues/335)、[openclaw#69484](https://github.com/openclaw/openclaw/issues/69484) 的证据中模型字段均为裸 id）。若目录返回裸 id：`"gemini-2.5-pro".Contains("models/gemini-2.5-pro")` 为 false → 发现永不命中；此时只能靠客户端字面请求 `models/gemini-2.5-pro` 命中 config-exact，再把该字面量发给上游 → 疑似 404。
- **触发条件及影响范围**：配置 google provider 的部署。
- **根因和证据链**：配置文件与匹配器通读 + 官方/社区用法佐证。
- **复现步骤**：`curl https://generativelanguage.googleapis.com/v1beta/openai/models -H "Authorization: Bearer $KEY"` 核对返回 id 形态。
- **实际验证结果**：**未运行验证**（需要真实 Google API key；公开文档未给出该端点的响应样例）。
- **建议修复方向**：按真实目录 id 去掉 `models/` 前缀，或为 google 在发现/转发时统一剥离该前缀。
- **回归验证**：拿到真实目录响应后加一条解析测试固定 id 形态。

#### KIMI-016 `BuildOllamaChatRequest` 把 `role:"tool"` 改写为 `"assistant"`，破坏发往 Ollama 上游的工具调用关联

- **类型**：功能
- **严重程度**：**P2**（OpenAI 面 → ollama provider 的多轮工具调用语义受损）
- **状态**：待验证（代码改写事实已确认；上游影响待真实 Ollama Cloud 核实）
- **定位**：`Endpoints/OpenAiEndpoints.cs:603-610`。
- **预期行为与实际行为**：预期 `role:"tool"`（含 `tool_call_id`）原样转给 Ollama（Ollama `/api/chat` 原生支持 tool 角色消息）；实际被改写成 `"assistant"`，`tool_call_id` 变成孤儿字段，工具结果与调用的关联丢失。注释理由是"OpenAI 风格上游会拒绝"，但该函数恰恰用于发往 Ollama-format 上游，理由与方向不匹配。
- **触发条件及影响范围**：`/v1/chat/completions` 路由到 ollama provider 且消息含工具结果。
- **根因和证据链**：代码通读；Ollama 官方 API 文档支持 tool 角色。
- **复现步骤**：对 Ollama Cloud 发含 `role:"tool"` 的原生请求对照行为。
- **实际验证结果**：**未运行验证**（需真实 Ollama Cloud key）。
- **建议修复方向**：直通 tool 角色（必要时做 `tool_call_id`→`name` 的字段映射），删除角色改写。
- **回归验证**：集成测试断言直通 body 中 tool 角色保留。

#### KIMI-017 `ollama.json` 的宽泛子串 `mistral` 会重新纳入 `ollamacloud.json` 显式禁用的模型

- **类型**：配置 / 功能
- **严重程度**：**P3**
- **状态**：待验证（逻辑链已确认；是否触发取决于 ollama 上游目录实际返回的 id 列表）
- **定位**：`config/model-selection/ollama.json`（`"match":"mistral"`,`enabled:true`,`priority:4`）vs `config/model-selection/ollamacloud.json`（`mistral-large-3:675b` 等 `enabled:false`）；匹配器 `Services/ModelSelectionStore.cs:85-101`（精确未命中时按优先级取第一个子串命中，禁用条目不参与但宽条目仍在）。
- **预期行为与实际行为**：预期 `enabled:false` 的模型不出现在目录；实际 `mistral-large-3:675b` 等会被 `mistral` 子串条目以优先级 4 重新纳入（两文件 provider 同为 `ollama`，加载时合并）。
- **触发条件及影响范围**：ollama cloud/local 目录返回含 "mistral" 的被禁 id 时。
- **根因和证据链**：两份配置实读 + 匹配器语义。
- **复现步骤**：stub 目录返回 `mistral-large-3:675b`，观察 `/v1/models` 是否曝光。
- **实际验证结果**：**未运行验证**（不掌握当前 ollama 目录真实内容；逻辑已由匹配器代码固定）。
- **建议修复方向**：明确禁用应优先于宽泛子串（精确禁用条目否决子串命中），或收窄 `mistral` 条目。
- **回归验证**：配置级单测覆盖"显式禁用 vs 宽泛启用"。

#### KIMI-018 `/api/chat` 省略 `stream` 时默认非流式，与原生 Ollama API 默认相反

- **类型**：接口协议 / 兼容性
- **严重程度**：**P3**
- **状态**：待验证（代码行为已确认；影响取决于具体 Ollama 客户端是否省略 `stream`）
- **定位**：`Endpoints/OllamaEndpoints.cs:211`（省略即 false）。Ollama 官方 API 默认 `stream:true`。
- **预期行为与实际行为**：原生 Ollama 客户端省略 `stream` 期待 NDJSON 流；代理返回单个 JSON 对象，严格按流解析的客户端会解析失败。
- **建议修复方向**：`/api/chat` 缺省按 Ollama 语义置 true；确认 VS BYOM 等主要客户端总是显式传值后可降级为文档说明。

### 工程改进建议（P3 级确认小项）

#### KIMI-019 凭证处理小项（日志片段、非常量时间比较、/health 无豁免）

- **类型**：安全
- **严重程度**：**P3**
- **状态**：已确认（静态）
- **定位与事实**：
  1. `Endpoints/OpenAiEndpoints.cs:405-413` `MaskApiKey` 把上游 key 的前 4+后 4 字符写入警告日志（8 个字符的密钥片段进入日志文件）；
  2. `Infrastructure/ProxyAuthenticationMiddleware.cs:14` 用 `==` 比较 `PROXY_API_KEY`（非常量时间，理论计时侧信道；本地工具风险低）；
  3. 设置 `PROXY_API_KEY` 后 `/health` 也需认证，无豁免路径（`HealthEndpoints.cs:5` + 中间件全局拦截）——容器/负载均衡健康检查会 401（当前 compose 未配 healthcheck，暂未引爆）。
- **建议修复方向**：日志只记录 key 哈希或长度；改 `CryptographicOperations.FixedTimeEquals`；为 `/health` 加匿名豁免或在文档写明需带 key。

#### KIMI-020 配置加载健壮性小项（双目录遮蔽、静默吞错、宽松布尔解析、fire-and-forget 刷新）

- **类型**：工程规范 / 可靠性
- **严重程度**：**P3**
- **状态**：已确认（静态；双目录合并顺序在本轮复现 harness 中实测一致）
- **定位与事实**：
  1. `Services/ModelSelectionStore.cs:233-237,340-351`：同时读取 `AppContext.BaseDirectory/config/model-selection` 与 `CWD/config/model-selection`，同 `match` 先到先得；csproj 以 `PreserveNewest` 把配置复制到 bin（`ai-proxy-hub.csproj:22-24`），直接运行已编译二进制时 bin 里的旧副本静默压过项目根目录的人工编辑（`dotnet run` 因每次重建不受影响）；
  2. `ModelSelectionStore.cs:366-369`：单个 JSON 解析失败静默吞掉、无任何日志——用户改错一个逗号，该文件全部模型静默消失；
  3. `ModelSelectionStore.cs:307`：`enabled` 仅识别 JSON `false`，字符串 `"false"`、数字 `0` 都会被当作启用；
  4. `Endpoints/OpenAiEndpoints.cs:22`：`_ = RefreshAvailableModelsIfNeeded(...)` fire-and-forget——`/v1/models` 响应总是刷新前的旧数据，且并发请求可叠加重复刷新（本轮已实测刷新本身能完成，不构成数据错误，仅时效/浪费问题）。
- **建议修复方向**：配置只认一个权威目录或显式优先级并打日志；解析失败记 warning；`enabled` 严格布尔；`/v1/models` 改为 await 或带并发闸。

#### KIMI-021 文档与代码多处不符

- **类型**：文档
- **严重程度**：**P3**
- **状态**：已确认（逐项与代码/实测对照）
- **定位与事实**：
  1. `docs/API.md:302-313`：错误示例 `reasoning_effort not supported by NVIDIA provider`/`UNSUPPORTED_PARAMETER`、`ALL_PROVIDERS_FAILED`、字符串形 `error` 字段在代码中均不存在；实际是 404 `provider_not_found`/`model_not_found`、502 `upstream_unavailable` 与对象形 `error`；
  2. `README.md:694-705` 与 `docs/ARCHITECTURE.md:211-212`：声称 `X-Proxy-Upstream-Model`/`X-Proxy-Provider` 两个端点都返回；实际 `/v1/chat/completions` 只发 `X-Proxy-Primary-Provider`/`X-Proxy-Primary-Upstream`（`OpenAiEndpoints.cs:171-172`），这两个头仅 `/api/chat` 发送（`OllamaEndpoints.cs:238-239`）；
  3. `CLAUDE.md:48`："Supported providers (9)" vs `ProviderCapabilitiesRegistry` 实际注册 17 个；`CLAUDE.md:17` "329 tests"、`:180` "436 tests across 16 test files" vs 实测 469 tests / 20 文件；
  4. `CLAUDE.md:150-159` 声称的参数过滤规则部分未实现：Groq 移除 `tools`/`tool_choice`、所有 provider 移除 `function_call`、NVIDIA/Groq/Moonshot 移除客户端提交的 `reasoning_effort`——`Services/RequestTransformer.cs` 只实现了 `top_k` 剥离与默认值注入/强制，客户端提交的 `reasoning_effort`、`function_call`、`tools` 对所有 provider 原样透传（`:116-127,133-135`）。文档承诺的保护不存在，客户端直发这些参数时行为取决于上游容忍度。
- **建议修复方向**：按代码实况修订上述文档；或补实现以对齐文档（涉及行为变更需单独决策）。

#### KIMI-022 构建/部署/后台任务小项

- **类型**：工程规范 / 可靠性
- **严重程度**：**P3**
- **状态**：已确认（静态）
- **定位与事实**：
  1. `Dockerfile:1,8` 使用 `mcr.microsoft.com/dotnet/nightly/*:10.0-preview` 镜像——.NET 10 已正式发布，nightly preview 标签不再更新且可能被清理，镜像构建随时间漂移/失效；
  2. `.github/workflows/dotnet.yml:8-10` 仅监听 `main`，而 `CLAUDE.md:5-9` 规定一切工作在 `develop` 分支——develop 上的 push/PR 无 CI 卡口；
  3. `Services/ProviderBenchmarkService.cs:170-175`：启用后每周期新写一个带时间戳的报告文件、从不清理（无界增长），且探测对每个曝光模型发起真实付费 API 调用（默认关闭，属已知取舍，文档未提示费用）。
- **建议修复方向**：Dockerfile 换稳定标签（`mcr.microsoft.com/dotnet/sdk:10.0`）；CI 增加 develop 触发；基准报告加滚动清理并在文档注明费用。

#### KIMI-023 元数据/协议细节小项

- **类型**：工程规范
- **严重程度**：**P3**
- **状态**：已确认（静态）
- **定位与事实**：
  1. `Services/ModelCatalogService.cs:267`：`capabilities` 数组恒含 `"tools"`，与同一响应里的 `supports_tools:false` 可能自相矛盾；
  2. `Endpoints/OllamaEndpoints.cs:90-91`、`Services/OllamaResponseBuilder.cs:19-20`：`size`/`digest` 为固定假值（全 0 digest），依赖 digest 校验的客户端会误判；
  3. `Endpoints/OpenAiEndpoints.cs:491,508`：Ollama 云非流转 SSE 的两个 chunk 使用不同 `chatcmpl-id`（同一 completion 的 chunk 应同 id）；`ConvertOllamaChatToOpenAiCompletion`（`:694-718`）不产出 `usage` 字段；
  4. `Services/ProviderRegistry.cs:260-271`：本地 Ollama 与 Ollama Cloud 以同名 `"ollama"` 重复注册，`@ollama` 限定路由只能命中先注册者（`FindConfiguredProvider` 返回首个），云/本地并存时路由语义含糊。
- **建议修复方向**：逐条对齐协议契约；同名 provider 考虑拆分为独立 provider 名（如 `ollama-local`）。

### 验证命令、结果摘要及无法执行的检查

**基线**

```bash
dotnet build ai-proxy-hub.sln      # 0 警告 0 错误
dotnet test ai-proxy-hub.sln --no-build   # 469/469 通过，0 跳过（.NET SDK 10.0.301）
```

**端到端复现 harness**（全部在临时目录，伪密钥 + 本地 stub，未触碰真实 provider）

-  stub：Python `http.server` 模拟 zhipu/google/ollama 上游（记录请求路径与 body、可注入延迟/挂起/特定响应形态）；代理以 `bin/Debug/net10.0/ai-proxy-hub.exe` 从临时 CWD 启动（避开仓库 `.env`），仅注入单个测试 provider 的环境变量。
-  KIMI-001：`POST /api/chat stream:true` → stub 日志收到 `/api/paas/v1/chat/completions`（应为 v4）。
-  KIMI-002：会话 1 流式响应注入 `SECRET-REASONING-CONV1` → 会话 2 上游 body 出现该字符串。
-  KIMI-003：挂起 stub + `timeout_seconds:2` → `/api/chat` HTTP 500（日志 `TaskCanceledException`，`OllamaEndpoints.cs:331`）；对照 `/v1/chat/completions` → 502 JSON。
-  KIMI-004：stub 返回无 `content` 的 tool_calls completion → `/api/chat` HTTP 500（`KeyNotFoundException`，`OllamaEndpoints.cs:347`）。
-  KIMI-005：`messages` 为字符串/对象 → `/v1/chat/completions` HTTP 500（`InvalidOperationException`，`RequestTransformer.cs:227`）。
-  KIMI-006：`GET /v4/models` 延迟 10s → 启动到监听 11s。
-  KIMI-007：空目录 stub → 3 次 `/api/tags` 产生 3 次上游探测，`models_last_refresh_utc` 保持 `0001-01-01`。
-  KIMI-008：系统 `PROXY_PORT=18348` + `.env` `PROXY_PORT=18499` → 实际监听 18499。
-  KIMI-009：`PROVIDER_GOOGLE_BASE_URL=…/v1beta/openai` → stub 收到 `/v1beta/openai/v1beta/openai/models`。
-  另验证并**排除**的假设：`/v1/models` 的 fire-and-forget 刷新实际可以完成（`ModelsLastRefreshUtc` 正常更新），不构成数据错误，仅作 KIMI-020 第 4 条记录。

**无法执行 / 未执行的检查**

-  KIMI-015、KIMI-016、KIMI-017：需要真实 Google / Ollama Cloud API key 与真实上游目录内容，本轮不使用真实密钥。
-  KIMI-010 的内存增长未做长时间实测（静态证据充分）。
-  KIMI-012 未单独起 ollama-format 故障场景（与已实测的 KIMI-003 同根因）。
-  Docker 镜像实际构建未执行（本机 Docker 可用性未确认；Dockerfile 问题为静态结论）。
-  真实模型目录逐项核对（哪些 id 实际存在）超出本轮无密钥范围。

**审计局限**：本清单不声称找出所有问题；`tests/ProxyTests/` 未逐行通读，基准/压测脚本仅表面审查，第三方依赖的内部行为未超出已用版本（net10.0 / Mvc.Testing 10.0.0-preview.5）的表面核对。
