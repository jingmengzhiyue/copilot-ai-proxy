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

---

## Grok 独立审计

### 审计基线和范围

- **审计人**：Grok（第二轮独立审计及交叉复核 agent）。
- **审计日期**：2026-09-17。
- **开始时 HEAD**：`main` @ `065ca11ec8ce8172b9856ae98718513d32721c55`（`kimi 审阅完成，生成issu.md`），工作区干净，相对 `origin/main` 超前 3 个提交。
- **结束时 HEAD**：`65a6b72d9b32566fefdf0e1544f4583fee76c04d`（`删除不合时宜的讨厌的CLAUDE.md`）。审计过程中仓库又多了一次只删 `CLAUDE.md` 的提交；**未改任何业务代码、配置、依赖或测试，未提交、未推送**。Kimi 原文所引 `CLAUDE.md` 行号在当前树中已不存在，交叉复核中单独标注。
- **Kimi 审计时 HEAD**：`f3741c0` + 当时未提交的路由修复（随后作为 `132c89f` 提交）。当前业务代码与 Kimi 所审的脏工作区一致，`65a6b72` 不改变下列缺陷的代码路径。
- **独立排查约束**：先读源码/配置/测试/文档并建立模块清单，再打开 `issue.md`。中途一次全仓 `grep` 误命中过 `issue.md`，但独立结论以源码通读和本轮 stub 复现为准，不以 Kimi 编号为前提。
- **基线验证**：`dotnet test tests/ProxyTests/ProxyTests.csproj -c Debug` → **469/469 通过，0 失败，0 跳过**。测试通过不构成缺陷不存在的反证。
- **复现环境**：临时目录 `C:\Users\jingm\AppData\Local\Temp\grok-audit-repro`，Python stub + `bin\Debug\net10.0\ai-proxy-hub.exe`，伪密钥，CWD 不含仓库 `.env`。未连接生产、未读取 `.env` 值、未打印密钥。
- **排除项**：`bin/`、`obj/`、`artifacts/`、`images/`、第三方 `Microsoft.*` / xUnit 程序集内部、真实上游 HTTP（无真实密钥）。`scripts/` 压测脚本仅表面审查。`README.zh-CN.md` 未逐句核对。未实际 `docker build`。

### 覆盖表

| 模块 | 检查深度 | 执行的检查 | 未覆盖 |
|---|---|---|---|
| `Program.cs` | 全量 | .env 加载、监听、启动目录刷新 | — |
| `Endpoints/OpenAiEndpoints.cs` | 全量 + stub 复现 | 路由头、failover、流式、Ollama 转换、异常 | 双候选流式 failover 未单独起第二 stub |
| `Endpoints/OllamaEndpoints.cs` | 全量 + stub 复现 | tags/show/chat、硬编码路径、传输异常、tool_calls 解析 | 真实 Ollama Cloud NDJSON |
| `Endpoints/HealthEndpoints.cs` | 全量 | 字段名、认证耦合 | — |
| `Services/ModelCatalogService.cs` | 全量 + stub | ResolveRoute、发现匹配、刷新时间戳 | 真实 Google `/models` 响应 |
| `Services/ProviderRegistry.cs` | 全量 + stub | DiscoverProviders、NormalizeBaseUrl、双 ollama | — |
| `Services/ModelSelectionStore.cs` | 全量 | 双文件合并、子串/精确匹配、kimicode 缺采样默认 | — |
| `Services/RequestTransformer.cs` | 全量 + stub | 早退跳过过滤、ModifyRequest 键、非数组 messages | — |
| `Services/ChatStreamingService.cs` | 全量 | SSE 转换、缓存写入键、超时 token | 未做超长流压测 |
| `Services/ReasoningCacheService.cs` | 全量 + 单测对照 | 全局计数 vs 请求内 idx、无淘汰 | 未重复跑 Kimi 的双会话注入 harness |
| `Services/OllamaResponseBuilder.cs` / `ProviderBenchmarkService.cs` / `Infrastructure/*` / `Models/*` | 全量 | 假 digest、认证比较、HttpClient 超时、能力表 | Benchmark 默认关闭，未对真实 API 探测 |
| `config/model-selection/*.json`（18 个） | 全量阅读 | kimicode/google/ollama 合并、override 缺口 | 真实目录 id 全集 |
| `tests/ProxyTests/` | 清单 + 关键文件 + 全量运行 | 469 通过；夹具未清全部 provider 键 | 未逐行通读全部测试 |
| CI / Docker / 打包 | 全量阅读 | 触发分支、nightly 镜像、`.env.example` 拷贝 | 未 docker build |
| 文档 | 与代码对照 | 错误码、诊断头、env 优先级 | `README.zh-CN.md` |

### 数据流（检查用）

客户端 → `ProxyAuthenticationMiddleware`（可选）→ `/v1/chat/completions` 或 `/api/chat` → `ModelCatalogService.ResolveRoute` → `RequestTransformer`（部分路径）→ `ProviderInfo.Client` + `Capabilities.ChatPath`（部分路径被硬编码覆盖）→ `ChatStreamingService` / JSON 转换 → 客户端。失败路径在 `/v1` 与 `/api/chat` 不对称。

### 已确认问题

#### GROK-001 `/api/chat` 流式硬编码 `v1/chat/completions`，zhipu/google 流式打到错误路径

- **类型**：功能
- **严重程度**：**P1**。VS BYOM 主路径 `/api/chat` + `stream:true` 对 `ChatPath ≠ v1/chat/completions` 的 provider 全部不可用。
- **状态**：已确认（独立端到端复现）
- **定位**：`Endpoints/OllamaEndpoints.cs:371`；对照 `:332` 非流式使用 `Capabilities.ChatPath`。
- **函数/符号**：`MapOllamaEndpoints` 流式 OpenAI 转换分支
- **预期与实际**：预期 zhipu 走 `v4/chat/completions`；实际流式走 `v1/chat/completions`。
- **触发条件**：`POST /api/chat` `stream:true`，路由到 zhipu 或 google。
- **影响**：该两家的 Ollama 兼容流式聊天失败（上游 404）。
- **根因**：流式分支未读取 `ProviderCapabilities.ChatPath`。
- **证据 / 复现**：stub 日志：非流式 `POST /v4/chat/completions`，流式 `POST /v1/chat/completions`，同模型 `/v1/chat/completions` 流式仍正确打到 `/v4/chat/completions`。结果文件 `C:\Users\jingm\AppData\Local\Temp\grok-audit-repro\results.txt` 节 `STUB_AFTER_ZHIPU`。
- **对应**：与 **KIMI-001** 同根因，独立复现。Kimi 的 stub 因 Base URL 含 `/api/paas` 而记为 `/api/paas/v1/...`，本轮 stub 根路径故记为 `/v1` vs `/v4`，缺陷相同。
- **修复方向**：流式改为 `ollamaProvider.Capabilities.ChatPath`；`:289` 的 `"api/chat"` 一并改为能力表。
- **回归验证**：stub 只监听 `v4/chat/completions`，断言 `/api/chat` 流式命中该路径。

#### GROK-002 kimicode 未强制 temperature/top_p，Copilot 采样参数会触发 Kimi Coding API 400

- **类型**：功能 / 配置
- **严重程度**：**P1**。Kimi 官方文档写明 k3 / kimi-for-coding 的 `temperature` 固定 1.0、`top_p` 固定 0.95，传入其他值报错。GitHub Copilot / VS 常带 `temperature≈0–0.2`。这与仓库提交说明「kimi 编程套餐存在 bug」直接对应，且 **Kimi 清单未单列此配置缺口**。
- **状态**：已确认（配置 + 转换器早退 + stub 抓包 + 官方文档）
- **定位**：
  - `config/model-selection/kimicode.json:5-55`（四条 enabled 模型均无 `temperature`/`top_p`/`override_client_params`，只有 `timeout_seconds` 等）
  - `Services/RequestTransformer.cs:24-31`（没有任何采样默认时直接 `return rawBody`，连 `top_k` 剥离也不做）
  - 对照 `config/model-selection/kimi.json:5-20`（`kimi-k3` 有 `temperature:1.0` + `override_client_params:true`）
- **函数/符号**：`RequestTransformer.ApplyExecutionDefaults`
- **预期与实际**：预期发往 `https://api.kimi.com/coding` 的请求要么省略 temperature/top_p，要么强制 1.0/0.95；实际把客户端 `temperature:0.2, top_p:0.1` 原样转发。
- **触发条件**：配置 `PROVIDER_KIMICODE_API_KEY` 后，客户端（尤其 Copilot）对 `k3`/`k3-256k`/`kimi-for-coding`/`kimi-for-coding-highspeed` 带非 1.0 温度发起聊天。
- **影响**：Kimi Coding 套餐请求被上游 `invalid_request_error` 拒绝，表现为「有 key 却不能用」。
- **根因**：kimicode 配置未按官方固定参数建模；转换器在「无任何 sampling 默认」时整段跳过，不会剥离或改写客户端字段。
- **证据**：官方参数表 https://platform.kimi.com/docs/api/models-overview （kimi-k3 / kimi-k2.7-code temperature 固定 1.0，其它值报错）。本轮 stub：`POST /v1/chat/completions` body `{"model":"k3",...,"temperature":0.2,"top_p":0.1,"stream":false}`。
- **对应**：无 KIMI 单号。与 KIMI-013（Ollama 直通跳过默认值）机制不同：此处是 OpenAI 面已调用 `ApplyExecutionDefaults` 但仍早退。
- **修复方向**：为 kimicode 四条模型设置 `override_client_params:true` 且 `temperature:1.0`/`top_p:0.95`，或在 force 模式下删除这两个字段；同时取消「无默认则整段 return」的早退，过滤逻辑应始终执行。
- **回归验证**：`k3@kimicode` 请求带 `temperature:0.2`，断言上游 body 为 1.0 或不含该字段。

#### GROK-003 `/api/chat` 上游传输失败未捕获，返回空 body 的 HTTP 500

- **类型**：可靠性
- **严重程度**：**P1**。Ollama 兼容面（VS BYOM）在超时/断连时得到不可解析的 500，而 `/v1` 已有 502 JSON。
- **状态**：已确认（独立端到端复现）
- **定位**：`Endpoints/OllamaEndpoints.cs:331`（本轮堆栈）；同文件 `:273`、`:290`、`:377` 同样无 try/catch。
- **函数/符号**：`MapOllamaEndpoints` OpenAI-format 非流式 `SendAsync`
- **预期与实际**：预期 502/`upstream_unavailable`；实际未处理 `HttpRequestException` → Kestrel 500、body 长度 0。
- **触发条件**：`/api/chat` 目标 provider 拒绝连接、DNS 失败或 `timeout_seconds` 触发。
- **影响**：客户端无结构化错误；代理错误日志刷屏。
- **根因**：`/api/chat` 未复用 `/v1` 的传输容错。
- **证据**：moonshot Base URL `http://127.0.0.1:1`：`/api/chat` → 500 空 body，日志 `HttpRequestException` at `OllamaEndpoints.cs:331`；同请求 `/v1/chat/completions` → 502 `code:upstream_unavailable`。
- **对应**：**KIMI-003**，独立复现（本轮用连接拒绝而非挂起 stub；结论相同）。
- **修复方向**：四处 `SendAsync` 捕获传输异常，客户端未取消时返回 502 JSON。
- **回归验证**：死端口 + `/api/chat` 断言 502 且 JSON 含 `upstream_unavailable`。

#### GROK-004 推理缓存写键用全局计数、读键用请求内下标，跨会话串话

- **类型**：功能 / 隐私
- **严重程度**：**P1**。共享代理时，后续会话的第一条无 tool_calls assistant 消息会注入进程内早期会话的 `reasoning_content`。
- **状态**：已确认（静态证据链完整；单测固定了错误键空间。未重复跑 Kimi 的双会话注入 harness）
- **定位**：写：`ReasoningCacheService.NextAssistantKey`（`Services/ReasoningCacheService.cs:11`）、`CacheReasoningFromResponse` `:56`、`ChatStreamingService.cs:89`。读：`RequestTransformer.ModifyRequest` `:247` `assistant:{idx++}`，`idx` 每请求从 0 起。
- **函数/符号**：`NextAssistantKey` vs `ModifyRequest`
- **预期与实际**：预期按会话/消息身份取回推理；实际只有进程第一次响应时读写键碰巧都是 `assistant:0`，之后新会话固定命中旧条目。
- **触发条件**：任意推理模型写缓存后，另一会话带 assistant 历史且无 tool_calls。
- **影响**：错误推理上下文进入上游；共享部署下跨用户泄露。
- **根因**：两种键派生从未对齐。`ReasoningCacheServiceTests` 只断言全局递增；`RequestTransformerTests` 手工 `Set("assistant:0")`，掩盖集成错位。
- **证据**：代码对照 + `ReasoningCacheServiceTests.NextAssistantKey_ReturnsSequentialKeys` / `CacheReasoningFromResponse_SequentialCalls_IncrementAssistantKeys`。
- **对应**：**KIMI-002**（同根因）。无界增长见交叉复核 KIMI-010，不另编号。
- **修复方向**：键绑定会话或消息指纹；读写同一派生；加 TTL/容量。
- **回归验证**：会话 A 写入后，会话 B 上游 body 不得含 A 的 reasoning_content。

#### GROK-005 `/v1/chat/completions` 对非数组 `messages` 返回 500

- **类型**：接口协议
- **严重程度**：**P2**。违背同文件「畸形 body 是 400」注释（`OpenAiEndpoints.cs:111`）。
- **状态**：已确认（独立复现）
- **定位**：`Services/RequestTransformer.cs:227` `EnumerateArray()`；调用点 `Endpoints/OpenAiEndpoints.cs:196` 无 try/catch。
- **函数/符号**：`ModifyRequest`
- **预期与实际**：预期 400 `invalid_request_error`；实际 `InvalidOperationException` → 500 空 body。
- **触发条件**：`messages` 为 string/object/number。
- **影响**：客户端看到无 JSON 的 500。
- **根因**：解析 JSON 对象后未校验 `messages` 类型。
- **证据**：`{"model":"deepseek-v4-pro@deepseek","messages":"x"}` → 500；日志 `JsonElement.EnumerateArray` at `RequestTransformer.cs:227` at `OpenAiEndpoints.cs:196`。
- **对应**：**KIMI-005**。
- **修复方向**：路由前校验数组；`ModifyRequest` 对非数组返回 null。
- **回归验证**：messages 非数组 → 400。

#### GROK-006 `/api/chat` 非流式把缺 `content` 的 tool_calls 响应打成 500

- **类型**：可靠性
- **严重程度**：**P2**。编码代理核心路径（工具调用）在多家 OpenAI 兼容上游的真实形态下崩溃。
- **状态**：已确认（独立复现 HTTP 500；本轮日志在进程结束前未刷出 `KeyNotFoundException` 行，但状态码与代码路径闭合）
- **定位**：`Endpoints/OllamaEndpoints.cs:346-354` `GetProperty("choices")[0].GetProperty("message")` / `GetProperty("content")`
- **函数/符号**：`MapOllamaEndpoints` 非流式 OpenAI 响应转换
- **预期与实际**：缺 `content` 应按空串；实际 `GetProperty` 抛异常。
- **触发条件**：`/api/chat` `stream:false`，上游 message 仅有 `tool_calls`。
- **影响**：工具调用轮次失败。
- **根因**：按「必有 content 字符串」解析。
- **证据**：stub `STUB_MODE=tool_no_content` → `/api/chat` HTTP 500。
- **对应**：**KIMI-004**。
- **修复方向**：`TryGetProperty`；空 `choices` 单独 502/400。
- **回归验证**：tool_calls-only、`content:null`、空 choices。

#### GROK-007 google.json 的 `models/` 前缀与 OpenAI 兼容目录裸 id 对不上，发现失败且上游 id 错误

- **类型**：功能 / 配置
- **严重程度**：**P1**（配置了 Google 后，`/v1/models` 看不到 Gemini；按 `/api/tags` 点选则会把 `models/gemini-…` 发给 OpenAI 兼容接口）。Kimi 标 P2 待验证；本轮用兼容面 stub + 官方文档把它升为已确认。
- **状态**：已确认（本地 OpenAI 兼容 stub + 官方文档；**未请求真实 Google API**）
- **定位**：`config/model-selection/google.json` 全部 `match` 为 `models/gemini-…`；发现过滤 `ModelCatalogService.cs:49-53` + `FindModelSelectionEntry` 子串方向为 discovered.Contains(match)。
- **函数/符号**：`FindModelSelectionEntry` / `ResolveRoute` / `ReplaceModelInRequestBody`
- **预期与实际**：官方 OpenAI 兼容示例模型 id 为 `gemini-3.8-flash`（无 `models/` 前缀），`base_url=https://generativelanguage.googleapis.com/v1beta/openai/`。stub 的 `GET` 返回 `gemini-2.5-flash` 时，catalog 不含任何 google 模型；显式 `models/gemini-2.5-flash@google` 仍把带前缀的 id 发给 `POST /v1beta/openai/chat/completions`。
- **触发条件**：启用 google provider。
- **影响**：列表空白；聊天用 tags 名则上游 404（按社区/官方裸 id 惯例）。
- **根因**：把原生 Gemini `models/` 资源名写进了 OpenAI 兼容 match。
- **证据**：health `providers` 含 `google`，`available_models` 无 gemini；`/v1/models` 无 google 条目；stub body `"model":"models/gemini-2.5-flash"`。文档：https://ai.google.dev/gemini-api/docs/openai
- **对应**：**KIMI-015**（本轮建议从「待验证」改为「已确认」，仍缺真实 Google 目录字节级对照）。
- **修复方向**：match 改为裸 id，或发现/转发时剥离 `models/`。
- **回归验证**：stub `/v1beta/openai/models` 返回裸 id，断言 catalog 收录且上游 model 为裸 id。

#### GROK-008 `NormalizeBaseUrl` 只折叠 ChatPath 首段，粘贴 Google 官方 base_url 会路径翻倍

- **类型**：功能
- **严重程度**：**P2**。默认不设 `PROVIDER_GOOGLE_BASE_URL` 时正确；按官方 SDK 文档设置则会 404。
- **状态**：已确认（`System.Uri` 拼接复现，与 HttpClient 行为一致）
- **定位**：`Services/ProviderRegistry.cs:293-307`；`ProviderCapabilitiesRegistry.cs:68` `v1beta/openai/chat/completions`
- **函数/符号**：`NormalizeBaseUrl`
- **预期与实际**：官方 `https://generativelanguage.googleapis.com/v1beta/openai/` + ChatPath → 重复 `/v1beta/openai/v1beta/openai/chat/completions`。默认根 URL 拼接结果正确。
- **触发条件**：用户把 Google OpenAI 兼容 `base_url` 整段写入 `PROVIDER_GOOGLE_BASE_URL`。
- **影响**：google provider 目录与聊天均 404。
- **根因**：只判断 base 是否以 `/{firstSegment}` 结尾（`v1beta`），不能折叠双段前缀。
- **证据**：`official=https://generativelanguage.googleapis.com/v1beta/openai/v1beta/openai/chat/completions` vs `default=https://generativelanguage.googleapis.com/v1beta/openai/chat/completions`。
- **对应**：**KIMI-009**。
- **修复方向**：按 ChatPath 最长前缀做后缀折叠。
- **回归验证**：google 双段 + kimicode 单段 `/v1` 都不回归。

#### GROK-009 `.env` 无条件覆盖进程环境变量，与文档优先级相反

- **类型**：配置 / 运维安全
- **严重程度**：**P2**
- **状态**：已确认（独立复现）
- **定位**：`Program.cs:21-23` `Environment.SetEnvironmentVariable(key, value)` 无「已存在则跳过」。
- **函数/符号**：启动 .env 加载循环
- **预期与实际**：`docs/CONFIGURATION.md` 把系统环境写在 `.env` 之前（Kimi 引用的 `CLAUDE.md` 优先级在 `65a6b72` 后已随文件删除，但 CONFIGURATION/DEPLOYMENT 仍在）。实际 CWD `.env` 覆盖 `PROXY_PORT`。
- **触发条件**：同时存在系统环境与 `.env`。
- **影响**：容器/CI 注入的端口、密钥被仓库遗留 `.env` 静默压过。
- **根因**：加载器不做「empty-only」写入。
- **证据**：进程 `PROXY_PORT=18348` + `.env` `PROXY_PORT=18499` → 监听 18499，`listen18348=False`。
- **对应**：**KIMI-008**。
- **修复方向**：仅当 `GetEnvironmentVariable` 为空时写入，或改文档并醒目说明。
- **回归验证**：系统值优先的启动测试。

#### GROK-010 本地 Ollama 与 Ollama Cloud 同名注册，`@ollama` 只能打到先注册者

- **类型**：路由
- **严重程度**：**P2**（Kimi 放在 KIMI-023 的 P3 元数据里；双实例时路由语义错误，建议按 P2）
- **状态**：已确认（独立复现）
- **定位**：`Services/ProviderRegistry.cs:257-271` 追加第二个 `Name="ollama"`；`FindConfiguredProvider` `:169-176` 返回第一个。
- **函数/符号**：`DiscoverProviders` / `FindConfiguredProvider`
- **预期与实际**：云与本地应可区分；实际 `health.providers` 出现 `["google","ollama","ollama"]`，`model@ollama` 固定命中列表首个。
- **触发条件**：同时设置 `PROVIDER_OLLAMACLOUD_API_KEY` 与指向 localhost 的 `PROVIDER_OLLAMA_BASE_URL`，且两者 BaseUrl 字符串不完全相等（本轮 cloud=`127.0.0.1`、local=`localhost`）。
- **影响**：限定路由无法选择本地或云；failover 列表含两个同名 provider。
- **根因**：能力表只有一个 `ollama` 名，本地作为第二实例硬编码塞入。
- **证据**：health JSON `providers:["google","ollama","ollama"]`。
- **对应**：**KIMI-023** 第 4 点。
- **修复方向**：拆成 `ollama` / `ollama-local`（或类似）并修正 Find 与 tags 前缀。
- **回归验证**：双 URL 时 `@ollama` 与 `@ollama-local` 分别打到对应 BaseUrl。

### 待验证风险

#### GROK-011 `ollama.json` 宽泛 `mistral` 子串可能重新纳入 cloud 已禁用模型

- **类型**：配置
- **严重程度**：**P3**
- **状态**：待验证（匹配器逻辑已确认；取决于真实/stub 目录是否返回被禁 id）
- **定位**：`config/model-selection/ollama.json:22` `match:mistral` enabled；`ollamacloud.json` 中 `mistral-large-3:675b` enabled:false；`FindModelSelectionEntry` 禁用条目不参与但宽条目仍命中。
- **对应**：**KIMI-017**。本轮未向 stub 注入 `mistral-large-3:675b`。
- **修复方向**：精确禁用否决子串，或收窄 match。

#### GROK-012 `BuildOllamaChatRequest` 将 `role:tool` 改写为 `assistant`

- **类型**：功能
- **严重程度**：**P2**
- **状态**：待验证（改写事实已确认；真实 Ollama 上游是否拒绝原 tool 角色未测）
- **定位**：`Endpoints/OpenAiEndpoints.cs:603-610`
- **对应**：**KIMI-016**。注释理由与「该函数只用于 Ollama-format 上游」矛盾。
- **修复方向**：保留 tool 角色或按 Ollama 字段映射，不要无条件改写。

### 工程改进建议

下列与 Kimi P3 项同根因，不重复计为独立缺陷，仅记录独立核对结果：

- 启动前串行 `RefreshAvailableModels` + `HttpClient.Timeout=5min` + `CancellationToken.None`（KIMI-006）：代码确认，本轮未做 10s 慢目录计时。
- 空 catalog 不更新 `ModelsLastRefreshUtc`（KIMI-007）：代码确认，本轮未做三次 `/api/tags` 计数。
- `ReasoningCacheService` 无 TTL/容量（KIMI-010）：代码确认，未做长跑内存。
- `/v1` 流式在 `HasStarted==false` 时也不 failover（KIMI-011）：代码确认；同函数失败时能改写 502，说明「headers already sent」理由不成立。
- `/v1` 的 `TryHandleOllamaCloudChatCompletion` 无传输 catch（KIMI-012）：代码确认。
- `/api/chat` Ollama 直通跳过 `ApplyExecutionDefaults`（KIMI-013）：代码确认。
- 默认 `0.0.0.0` 且无 `PROXY_API_KEY`（KIMI-014）：代码确认。对本产品「局域网 VS 连代理」可能是有意设计，但与 README「local endpoint」及缺认证叠加后风险成立。
- `/api/chat` 省略 `stream` 默认为 false（KIMI-018）：代码确认；VS BYOM 是否总带 `stream` 未测。
- 认证非常量比较、`MaskApiKey` 日志片段、`/health` 无豁免（KIMI-019）：代码确认。
- 双目录配置遮蔽、JSON 静默失败、`/v1/models` fire-and-forget（KIMI-020）：代码确认。
- 文档错误码/诊断头与代码不符（KIMI-021）：`docs/API.md:302-313`、`README.md:698-705` 仍在。原 `CLAUDE.md` 条目因 `65a6b72` 删除文件而失效。
- Dockerfile nightly preview、CI 只监听 `main`、benchmark 文件只增不删（KIMI-022）：代码确认。当前分支策略写「不要碰 main」，CI 却只跑 main。
- tags 假 digest、capabilities 恒含 tools、Ollama 伪 SSE 两个 chunk 不同 id（KIMI-023 1–3）：代码确认。

### 验证记录

```
dotnet test tests/ProxyTests/ProxyTests.csproj -c Debug
# 已通过! 失败: 0，通过: 469，已跳过: 0
```

独立 stub 复现（`C:\Users\jingm\AppData\Local\Temp\grok-audit-repro\results.txt`、`proxy.log`、`dual-ollama.txt`）：

| 场景 | 结果 |
|---|---|
| zhipu `/api/chat` 非流式 | 上游 `POST /v4/chat/completions` |
| zhipu `/api/chat` 流式 | 上游 `POST /v1/chat/completions` |
| zhipu `/v1` 流式 | 上游 `POST /v4/chat/completions` |
| `k3@kimicode` temperature=0.2 | 上游原样 0.2/0.1 |
| `models/gemini-2.5-flash@google` | 上游 model 带 `models/` 前缀；catalog 无 google 模型 |
| `messages:"x"` | `/v1` HTTP 500，`EnumerateArray` |
| moonshot 死端口 | `/api/chat` 500 空 body；`/v1` 502 JSON |
| tool_calls 无 content | `/api/chat` HTTP 500 |
| Google 官方 base_url 拼接 | 路径双段重复 |
| `.env` vs 进程 `PROXY_PORT` | 监听 .env 的 18499 |
| 双 ollama URL | `providers:["google","ollama","ollama"]` |

未重复执行：Kimi 的推理缓存双会话注入、10s 慢启动计时、空目录三次刷新计数、真实 Google/Ollama Cloud。

### 未覆盖部分

- 真实 17 家上游的现网目录与参数拒绝矩阵。
- `scripts/` 压测在真实密钥下的行为。
- Docker 镜像构建与 nightly 标签是否已失效。
- `README.zh-CN.md` 全文。
- 流式中途断连后客户端重试语义。
- 多候选裸模型的流式 failover（静态确认，未起双 stub）。

---

## Grok 对 Kimi 的交叉复核

复核对象：上文「Kimi Code 独立审计」KIMI-001 … KIMI-023。代码基线相对 Kimi 的 `f3741c0`+脏树：路由修复已在 `132c89f`，与 Kimi 当时所审内容一致；`65a6b72` 仅删除 `CLAUDE.md`，不修复下列缺陷。

| 编号 | 复核结论 | 证据与建议修正 |
|---|---|---|
| KIMI-001 | **确认** | 独立 stub：`/api/chat` 流式打 `/v1/chat/completions`，非流式与 `/v1` 流式打 `/v4`。对应 GROK-001。 |
| KIMI-002 | **确认** | 读写键代码与单测键空间对不上。本轮未重跑双会话 harness，但不构成反证。对应 GROK-004。 |
| KIMI-003 | **确认** | 死端口：`/api/chat` 500 空 body（堆栈 `OllamaEndpoints.cs:331`），`/v1` 502 JSON。对应 GROK-003。Kimi 用挂起+短 timeout，本轮用连接拒绝，同根因。 |
| KIMI-004 | **确认** | tool_calls-only stub → `/api/chat` 500。对应 GROK-006。 |
| KIMI-005 | **确认** | `messages` 为字符串 → 500，堆栈与 Kimi 一致。对应 GROK-005。 |
| KIMI-006 | **确认**（本轮未复计时） | `Program.cs:50` 在 `Run()` 前 await 串行发现 + 5min HttpClient 超时 + `CancellationToken.None` 静态闭合。接受 Kimi 的 11s 计时。 |
| KIMI-007 | **确认**（本轮未数探测次数） | `:76-79` 空结果不写 `ModelsLastRefreshUtc`，节流失效。逻辑充分。 |
| KIMI-008 | **确认** | 独立复现监听 18499 而非进程 18348。对应 GROK-009。行尾注释问题本轮未测，属附属，不否决主结论。 |
| KIMI-009 | **确认** | `Uri` 拼接得到双段路径。对应 GROK-008。默认 Base URL 不受影响，严重程度 P2 合适。 |
| KIMI-010 | **确认** | 全类无 Remove/TTL。与 KIMI-002 叠加放大串话窗口。未做内存实测，同意静态即可成立。 |
| KIMI-011 | **确认** | 流式只取 `candidates[0]`；失败分支能改 502，证明此时可 failover。本轮未起双候选 stub。 |
| KIMI-012 | **确认** | `TryHandleOllamaCloudChatCompletion` `:431` 无 catch，会打断非流式 failover。与 KIMI-003 同类，但是 `/v1` 面，**不是重复编号**：调用点不同。 |
| KIMI-013 | **确认** | Ollama 直通只 `ReplaceModelInOllamaRequestBody`，不调 `ApplyExecutionDefaults`。timeout 仍生效。未抓 Ollama options 对照，静态足够。 |
| KIMI-014 | **部分成立** | 绑定 `0.0.0.0` 且默认无认证属实（本轮代理日志 `Now listening on: http://0.0.0.0:18772`）。严重程度取决于产品意图：局域网 VS/Copilot 需要非 loopback。建议修正为「默认全接口 + 无认证」的**文档/加固缺口**，不一定改为默认 127.0.0.1（那会打断常见用法）。P2 在「无 PROXY_API_KEY 的共享网络」下成立。 |
| KIMI-015 | **确认**（升级） | Kimi 标待验证。本轮 OpenAI 兼容 stub 返回裸 `gemini-2.5-flash` 时 catalog 无 google 模型；显式带前缀请求把 `models/gemini-2.5-flash` 发给 `/v1beta/openai/chat/completions`。官方文档用裸 id。仍缺真实 Google `GET /v1beta/openai/models` 字节，但「待验证」过保守。对应 GROK-007。建议状态改为已确认，保留「真实目录未拉取」备注。 |
| KIMI-016 | **待验证** | 同意：改写代码属实、注释方向错误。上游是否必须保留 tool 角色需 Ollama 实锤。维持待验证。对应 GROK-012。 |
| KIMI-017 | **待验证** | 匹配器+合并逻辑同意。本轮 stub 未返回被禁 mistral id。维持待验证。对应 GROK-011。 |
| KIMI-018 | **部分成立** | 省略 `stream` → false 代码属实。原生 Ollama 默认 true。对 VS BYOM（通常显式传 stream）影响可能为 0。建议保持 P3，并在文档写明代理默认。 |
| KIMI-019 | **确认** | MaskApiKey / `==` 比较 / health 需认证均在代码中。P3 合适。 |
| KIMI-020 | **确认** | 双目录、吞 JSON 错、enabled 仅认 JSON false、`/v1/models` fire-and-forget 均在。Kimi 排除「fire-and-forget 造成数据错误」同意。 |
| KIMI-021 | **部分成立** | 条目 1（API.md 虚构错误码）、2（README/ARCHITECTURE 诊断头）在当前树仍成立。条目 3、4 所引 `CLAUDE.md` 已在 `65a6b72` 删除，**不再作为现存文档缺陷**；参数过滤未实现仍是代码事实，应改记为「实现与历史文档/CLAUDE 描述不一致，文件已删」。 |
| KIMI-022 | **确认** | Dockerfile nightly、CI 只 `main`、benchmark 文件无界。未 docker build。 |
| KIMI-023 | **部分成立** | 1–3（capabilities/假 digest/伪 SSE id）确认、P3 合适。第 4 点双 ollama **确认且本轮复现**，建议从本条拆出或升为 P2（见 GROK-010），不宜只当元数据。 |

**全覆盖检查**：KIMI-001 至 KIMI-023 均有复核结论，无遗漏。

**复核分歧摘要**：

1. **KIMI-015**：建议「待验证」→「已确认」（本地兼容面 stub + 官方文档；真实 Google 目录仍未拉取）。
2. **KIMI-014**：问题存在，但是否应改默认 bind 需产品决策；不宜当成无条件 bug。
3. **KIMI-021**：`CLAUDE.md` 相关子项因基线变化失效。
4. **KIMI-023.4**：同根因应升为独立 P2 路由缺陷（GROK-010）。
5. **Kimi 未单列**：GROK-002 kimicode 固定采样参数缺失——这是当前树里与「kimi 编程套餐异常」最直接的 P1。

**不成立的 Kimi 条目**：无。没有发现需要整条推翻的结论。

**重复关系**（同根因不重复计缺陷）：GROK-001=KIMI-001，GROK-003=KIMI-003，GROK-004=KIMI-002，GROK-005=KIMI-005，GROK-006=KIMI-004，GROK-007=KIMI-015，GROK-008=KIMI-009，GROK-009=KIMI-008，GROK-010=KIMI-023.4。GROK-002 为独立新发现。

---

## Codex 独立审计

### 基线、独立性与审计边界

- 审计人：Codex；日期：2026-09-17（Asia/Shanghai）。
- 基线：分支 `main`，HEAD `11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。初始 `git status --short` 为空；所有原有文件和忽略文件保留。Git提示用户全局ignore文件不可读，不影响本轮按显式路径审计和跟踪文件差异核对。
- 读取规则：适用的是用户提供的AGENTS.md指令；检查D盘、D:/Dev和项目目录及项目内文件清单，未找到额外落盘AGENTS.md。另读取 `.github/copilot-instructions.md`，遵循本地代理key与Ollama Cloud key必须区分。当前没有CLAUDE.md。
- 独立阶段没有读取 `issue.md`，文件搜索显式排除它或限于源码目录；历史审计文档也延后。独立发现/覆盖记录在 **2026-09-17 09:42:21 +08:00** 固定，之后才第一次读取前两轮全文。
- 独立冻结文件：临时审计目录下 `independent-freeze.txt`；SHA-256：`E3EDB85AA806A1F45ADB737D79037EB43A3BB2A62DA421D98658470FE3358AC7`。后续补证明确标注为交叉阶段，不回写冻结文件或增加“事后独立发现”。
- 本轮入口是实际当前工作树，而非只审旧commit。与Grok结束时 `65a6b72d9b32566fefdf0e1544f4583fee76c04d` 比较，仅 `issue.md` 不同，业务代码/配置/测试相同。Kimi审的是 `f3741c0` 加未提交修复，后来在 `132c89f` 提交；无法仅凭Kimi报告恢复它当时每个未提交字节，故所有最终定位重新以当前HEAD核对。被删除CLAUDE的结论另行排除。
- 初始 `issue.md`：**73742字节**；SHA-256：`7531D53CCC3F70CE3AB9894DEF0FDC0509ECBD13415E9902D1ED0DFAA826AC53`。先原样备份，最终采用字节追加并核对原始前缀。
- 仅审计与整理：没有修复业务代码、配置、依赖或已有测试；未提交、推送、部署。没有读取真实 `.env` 内容，没有真实key/真实数据请求。所有探针使用合成内容、内存HTTP handler或临时loopback stub。

### 结构、入口、数据流与信任边界

项目是.NET 10 ASP.NET Core Minimal API；生产项目没有第三方NuGet PackageReference，主要依赖共享框架。测试使用xUnit 2.9.3、Test SDK 17.14.1、Mvc.Testing 10.0.0-preview.5.25277.114、runner 3.1.4、coverlet 6.0.4。

启动：Program读取.env/进程变量 → ProviderRegistry按能力表注册HttpClient → ModelSelectionStore加载18份JSON → ModelCatalogService发现目录/建立别名 → 可选认证中间件 → OpenAI/Ollama/health端点 → app.Run。后台ProviderBenchmarkService默认关闭。

请求：客户端JSON → 可选Bearer校验 → ResolveRoute得到provider与upstream → RequestTransformer修改模型/参数/推理历史 → provider固定base与能力表路径 → 上游HTTP → 直接JSON、SSE、NDJSON或协议转换 → 客户端。非流式OpenAI入口有候选切换；显式provider限定只能单候选。API原生透传、跨协议转换与流式分支分别检查，不能拿一条成功路径代表全部。

边界：客户端model/messages及上游JSON/SSE均不可信；服务端配置base URL属于管理员配置，当前没有证据表明客户端能直接指定任意请求URL，因此没有把它误报为客户端SSRF。用户数据主要流向所选上游与进程内推理缓存；没有数据库/事务/用户数据持久化，后台报告是独立文件输出。鉴权默认可选是既有产品行为，不能凭偏好改成匿名health或宣称已绕过鉴权。跨请求共享状态重点是缓存、目录多字段快照与单例provider实例。

### 模块覆盖与验证命令清单

“深入”表示完整阅读生产代码并沿调用链检查；“基础”表示清单、结构/语法、关键行为抽查，不表示逐行审完全部断言。运行通过不代替静态证据或边界复现。

| 范围 | 深度 | 实际检查 | 未覆盖/限制 |
|---|---|---|---|
| Program.cs | 深入 | 配置优先级、启动顺序、DI生命周期、实际程序合成启动 | 无真实配置启动 |
| Endpoints/OpenAiEndpoints.cs | 深入+探针 | models、路由、候选切换、SSE、Ollama转换、状态与部分成功 | 未逐个真实客户端验收 |
| Endpoints/OllamaEndpoints.cs | 深入+探针 | tags/show/chat、参数、两种上游格式、流式期限与工具响应 | 未运行原生模型推理 |
| Endpoints/HealthEndpoints.cs | 深入 | 字段、健康语义、全局鉴权 | 无部署healthcheck运行 |
| Services/ModelCatalogService.cs | 深入+探针 | 目录发现、别名/限定路由、profile、刷新与timeout | 跨线程快照交错尚未确定性复现 |
| Services/ModelSelectionStore.cs | 深入+探针 | 18配置解析、exact/suffix/display/substring、启禁、合并 | 未核对所有真实上游ID/参数上限 |
| Services/ProviderRegistry.cs | 深入+探针 | provider发现、base规范化、双Ollama、候选与快照发布 | 未把管理员URL配置误当远程SSRF |
| Services/RequestTransformer.cs | 深入+探针 | 缓存注入、空消息、默认值/强制值、过滤与异常 | 不等于所有组合穷举 |
| Services/ChatStreamingService.cs | 深入+探针 | SSE/NDJSON、终止、工具碎片、缓存、背压、token | 未做超长流/内存耗尽负载；Flush随读推进，没认定另有无限队列 |
| Services/ReasoningCacheService.cs | 深入+探针 | 跨会话身份、容量与生命周期 | 未做真实数据泄露或OOM测试 |
| Services/OllamaResponseBuilder.cs | 深入 | show元数据、参数串、占位值 | 非英文区域格式及具体客户端digest依赖未运行 |
| Services/ProviderCapabilitiesRegistry.cs | 深入 | 全部17个provider能力、路径、环境前缀 | 各厂商所有能力矩阵未逐项在线核对 |
| Services/ProviderBenchmarkService.cs | 深入 | 开关、取消、30s探针期限、报告保留、alias重复 | 默认禁用，未执行真实付费探测 |
| Infrastructure/JsonDefaults.cs、ProviderHttpClientFactory.cs、ProxyAuthenticationMiddleware.cs | 深入 | JSON命名/null、HTTP复用/期限、认证入口与日志 | 未做TLS/重定向渗透或远程计时攻击 |
| Models/ModelExecutionConfig.cs、ModelSelectionEntry.cs、ProviderCapabilities.cs、ProviderInfo.cs、ResolvedRoute.cs | 深入 | 默认值、记录结构、路由契约、provider身份 | 未发现独立DTO运行缺陷 |
| config/model-selection/*.json（18份） | 全部解析+基础内容；Ollama等重点深入 | provider/模型项/启用/执行字段，当前文件都可解析 | 外部模型版本/价格/上下文真实性未全部验证 |
| ai-proxy-hub.csproj、sln、tests项目、launchSettings、run.cmd | 深入 | .NET版本、编译排除、配置复制、启动/构建命令 | 未执行run.cmd（会加载真实.env） |
| .github/workflows/*.yml、Dockerfile、compose、ignore | 深入静态 | CI触发、测试卡口、镜像、端口、.env排除 | 无docker命令，未构建镜像/执行远程CI |
| scripts/package-release.ps1 | 深入静态+语法 | 发布复制/归档/清理/启动脚本 | 未执行发布、跨平台包解压与执行权限验证 |
| scripts/benchmark-duplicate-models.ps1、comprehensive-benchmark.py、final-benchmark.py、quick-benchmark.py、eval-nvidia-nim.ps1、test-coding-agent-models.ps1、generate-modules.ps1 | 完整阅读+基础语法 | 请求构造、匹配、排名、错误、输出；生成模块脚本当前仅stub | 全部未请求真实服务 |
| scripts/stress-test-all-models.ps1、test-all-provider-keys.ps1、test-nim-models.ps1、test-provider-connectivity.ps1 | 基础检查+全文件语法 | 输入/网络目标/凭证边界/错误与报告关键段 | 未逐行验证全部统计与脚本运行路径 |
| tests/ProxyTests全部20个C#文件（含FakeProviders） | 全清单+重点深入+全量执行 | 路由、端点fixture、转换、缓存、参数断言；识别复制实现问题 | 未逐行读完所有断言；无完整覆盖率报告 |
| README中英文、docs/API/ARCHITECTURE/CONFIGURATION/DEPLOYMENT/TESTING | 基础+契约重点对照 | 鉴权、配置、接口、路径、错误码与测试说明 | 未逐句校对全部译文/历史外部文本 |
| docs/superpowers、历史docs/testing、静态价格/模型文本、emptystub.log、CHANGELOG/LICENSE | 基础清单；历史审计延后抽查 | 不作为当前运行结果；历史说明不覆盖当前源码证据 | 不重新审全部历史数据，防止独立阶段被旧结论引导 |
| bin、obj、artifacts、.history、.git内部、images、真实.env | 排除 | 构建/历史/版本控制产物、二进制截图或秘密配置 | 非项目自有运行源码，不读取密钥、不分析截图内容 |

生产自有C#文件共21个，均已深入检查；没有把未逐行读完的测试/脚本文档标成“全面深入”。

### 实际执行与证据保存

临时目录：`C:/Users/jingm/AppData/Local/Temp/codex-audit-00dcad660e054dada10484c08db0d89b`。仅复制跟踪的C#、项目、solution、模型JSON及xunit配置，不复制.env；进程启动前清除PROVIDER_/DEEPSEEK_/PROXY_/BENCHMARK_变量。副本生产源码与原文件一致，探针置于单独probes目录，编译时引用真实生产类而非复制算法。探针修改仅为运行时合成fixture，不修改项目配置文件。

| 命令/验证 | 实际结果 |
|---|---|
| git branch --show-current；git rev-parse HEAD；git status --short；显式路径rg清单 | main、上述HEAD、初始跟踪工作区干净 |
| dotnet --info | SDK10.0.301；可用.NET/ASP.NET Core10.0.9及10.0.0 |
| dotnet restore ai-proxy-hub.sln --source C:/Users/jingm/.nuget/packages -p:NuGetAudit=false（隔离副本） | 本地包缓存还原成功；未更新版本；未做在线漏洞数据库审查 |
| dotnet test ai-proxy-hub.sln --no-restore --logger "trx;LogFileName=baseline.trx" --results-directory results --verbosity minimal | **469通过，0失败，0跳过**；测试执行约3s；TRX保存在results |
| dotnet build ai-proxy-hub.sln --no-restore -c Release -p:DefaultItemExcludes=probes/** -p:EnableNETAnalyzers=true -p:AnalysisLevel=latest | **0错误，9警告**：生产CS8600一处、测试WebHostBuilder过时ASPDEPR004八处 |
| dotnet build probes/probes.csproj --no-restore；dotnet probes/bin/Debug/net10.0/probes.dll | 真实生产方法/端点探针完成；输出分别保存在probe-output.txt与cross-probe-output.txt；不是另加到现有469测试的正式测试数 |
| python startup-probe.py | 合成.env覆盖进程端口，Testing仍加载；file_port_status=200，system_port_listening=false；目录延迟1200ms，约3781ms后就绪（含启动开销） |
| PowerShell Parser.ParseFile（9份ps1） | 全部0语法错误，仅解析未执行脚本 |
| Python ast.parse（3份py） | 全部解析成功，未执行真实基准 |

探针运行过程的纠正记录：第一次从probes子目录运行，未找到模型配置，禁用匹配返回null；改从副本项目根运行后才使用其结果。一次尝试以EnableDefaultCompileItems=false排除探针导致CS5001（入口被排除），属于审计命令错误，随后以DefaultItemExcludes仅排除probes重新成功构建，未归咎于项目。一次PowerShell汇总命令有管道语法错误，已用实际成功的配置枚举/解析输出替代。Release构建后的附加git status在无.git副本报错，不是构建失败；构建日志明确0错误。

官方依据（均2026-09-17查询；除.NET版本限定外，服务端未锁版本，不把当前网页当成所有历史版本的证明）：

- **N1**：[.NET 10 HttpCompletionOption](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0)：HeadersRead之后正文需要单独期限。
- **O1**：[Ollama Chat REST](https://docs.ollama.com/api/chat)：stream默认true、format与原生工具结构。
- **O2**：[Ollama工具调用](https://docs.ollama.com/capabilities/tool-calling)：工具结果role=tool、参数为对象。
- **O3**：[Ollama视觉REST](https://docs.ollama.com/capabilities/vision)：原生REST传Base64图像；SDK路径/URL便利功能不可混同。
- **O4**：[OpenAI Chat接口](https://developers.openai.com/api/reference/resources/chat)：工具调用参数形式、SSE各片同一ID。
- **G1**：[Google OpenAI兼容接口](https://ai.google.dev/gemini-api/docs/openai)：示例裸模型ID及多段base URL；不包含本轮实际账户目录响应。
- **K1**：[Kimi开放平台参数表](https://platform.kimi.com/docs/api/models-overview)、**K2**：[Kimi Coding模型映射](https://www.kimi.com/code/docs/en/kimi-code/models.html)：分别核查，不跨端点直接套用。
- 尝试读取Ollama v0.6.4的GitHub源码失败（web cache miss / shell HttpRequestException）；因此未宣称核对该tag源码。这里0.6.4只是代理/api/version硬编码，不是已安装原生服务版本。

### 固定的独立发现（CODEX-001—CODEX-020）

下面编号与独立冻结记录一致；“最终已确认”包括固定后明确标示的补证。独立项为当时记录单元，若包含不同根因，最终条目已拆开。修复实施以最后章节的自包含条目为准。

#### CODEX-001 推理缓存跨会话、跨提供商注入旧内容

- **类型 / 严重程度 / 状态**：隐私 / 数据隔离；P1；最终已确认。严重度依据：预期只回填同一消息的推理；实际将 A 的内容送入 B 的上游请求，污染输出并可能跨用户或跨提供商泄露。单个用户的多会话也受影响。
- **定位 / 符号**：Services/ReasoningCacheService.cs:6,11,45-60；Services/RequestTransformer.cs:205,247-265；Services/ChatStreamingService.cs:80-91,206-216；Program.cs:38。符号：ReasoningCacheService.NextAssistantKey / CacheReasoningFromResponse；RequestTransformer.ModifyRequest。基线：`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **预期与实际、触发和影响**：会话 A 已缓存推理；不同会话 B 发送没有 reasoning_content 的 assistant 历史。也可通过重复工具 ID 命中 toolcall 键。 预期只回填同一消息的推理；实际将 A 的内容送入 B 的上游请求，污染输出并可能跨用户或跨提供商泄露。单个用户的多会话也受影响。
- **根因与完整静态链**：写键是进程全局 assistant:N，读键却从每次请求的 assistant:0 开始；toolcall 键也没有会话或提供商范围。；上述调用由真实端点/启动路径到达，保护条件与边界详列于对应FINAL项。
- **独立证据、复现及实际结果**：独立运行cache_cross_conversation=true；合成A标记出现在无关B历史中。全局写计数与逐请求读计数、tool键无隔离已在首次阅读前固定。
- **修复方向**：优先停止无身份依据的回填；定义会话/提供商/消息身份后统一读写键。仅把全局计数改为请求计数仍不隔离。
- **回归验证**：A 写入后 B 的两种协议、普通及工具历史均不得包含 A 标记；同会话正确消息可恢复；不同提供商重复工具 ID 不串用。
- **边界与最终去向**：FINAL-001。补充实测/反证/不确定性见最终项；没有真实上游或客户端运行的地方不据此声称现网必现。

#### CODEX-002 长期运行的推理缓存没有容量和过期限制

- **类型 / 严重程度 / 状态**：内存 / 可靠性；P2；最终已确认。严重度依据：缓存应受内存预算约束；实际永久保留推理全文，进程内存持续积累，长期可能造成资源不足。并发字典解决线程安全，不解决有界性。
- **定位 / 符号**：Services/ReasoningCacheService.cs:6,11-16,58-60；Program.cs:38。符号：ReasoningCacheService.Set / CacheReasoningFromResponse。基线：`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **预期与实际、触发和影响**：长期使用带 reasoning_content 的模型，产生不同助手消息或工具 ID。 缓存应受内存预算约束；实际永久保留推理全文，进程内存持续积累，长期可能造成资源不足。并发字典解决线程安全，不解决有界性。
- **根因与完整静态链**：Singleton ConcurrentDictionary 只有新增/覆盖，没有移除；普通消息使用不断递增键。；上述调用由真实端点/启动路径到达，保护条件与边界详列于对应FINAL项。
- **独立证据、复现及实际结果**：独立低负载插入后计数1001，全类无删除或过期；没有做OOM压测。
- **修复方向**：条目数和字节预算、TTL/淘汰一起设计；单条过大时跳过缓存，避免安全隔离修复后继续无界。
- **回归验证**：写入超过配置预算后数量/总字节封顶；时间推进淘汰过期项；并发读写和大单条均可控。
- **边界与最终去向**：FINAL-012。补充实测/反证/不确定性见最终项；没有真实上游或客户端运行的地方不据此声称现网必现。

#### CODEX-003 空目录或发现失败时没有重试间隔；同一刷新周期的并发请求重复执行模型发现；监听启动被所有上游目录的串行发现阻塞

- **类型 / 严重程度 / 状态**：性能 / 故障放大；并发 / 上游负载；启动可用性；P2；最终已确认。严重度依据：应在失败/空结果后限频；实际每个请求都重新探测所有 provider，增加延迟和上游负载。 预期同一周期合并为一次发现；实际每个请求创建一套串行 GET，可占满连接并放大限流。与 FINAL-013 的失败退避是不同根因。 代理即使可以按静态配置路由，也在模型发现完成前不监听；等待可累加到各 provider 超时之和。
- **定位 / 符号**：Services/ModelCatalogService.cs:21-28,76-79,142；Endpoints/OllamaEndpoints.cs:13,131,142,174；Endpoints/OpenAiEndpoints.cs:22；Services/ModelCatalogService.cs:21-41；Endpoints/OpenAiEndpoints.cs:22；Program.cs:50,67；Services/ModelCatalogService.cs:39-41,725；Infrastructure/ProviderHttpClientFactory.cs:44。符号：ModelCatalogService.RefreshAvailableModelsIfNeeded / RefreshAvailableModels；Program启动。基线：`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **预期与实际、触发和影响**：所有目录返回空、失败或模型不命中选择规则，连续请求 tags/chat/show/models。 应在失败/空结果后限频；实际每个请求都重新探测所有 provider，增加延迟和上游负载。 目录到期时多个请求同时进入 RefreshAvailableModelsIfNeeded；即使最终目录成功也可同时进入。 预期同一周期合并为一次发现；实际每个请求创建一套串行 GET，可占满连接并放大限流。与 FINAL-013 的失败退避是不同根因。 任何已配置 provider 的目录接口慢或黑洞连接。 代理即使可以按静态配置路由，也在模型发现完成前不监听；等待可累加到各 provider 超时之和。
- **根因与完整静态链**：只在成功构建目录后更新 ModelsLastRefreshUtc；空结果或错误保持 MinValue/旧时间。 时间戳检查和刷新不是一个受保护操作，也没有共享进行中的刷新任务。 在 app.Run 前 await 目录刷新，循环串行，使用 CancellationToken.None 和 5 分钟 HttpClient 超时。；上述调用由真实端点/启动路径到达，保护条件与边界详列于对应FINAL项。
- **独立证据、复现及实际结果**：独立10个并发调用×4 provider得到40个GET；空结果不更新ModelsLastRefreshUtc。启动等待所有provider为完整静态链。三个具体根因在最终清单拆开；实际进程启动计时是交叉阶段补证。
- **修复方向**：记录最近尝试时间与成功时间，失败后采用明确的短退避，保留可用的旧目录。 共享在途发现任务或加并发闸，进入闸后再次检查到期；明确请求取消不能随意取消其他等待者需要的共享刷新。 先启动服务并利用静态路由，后台刷新；或独立短发现超时及有界并发；区分健康/就绪语义。
- **回归验证**：空目录和异常目录下顺序请求三次，仅产生一次周期的探测；间隔到期再尝试；成功时间不伪造。 10 个并发到期请求只产生每 provider 一次 GET；完成后所有调用者看到同一版本；单个请求取消不引发刷新风暴。 目录永不完成时，健康端口在约定短阈值内可用、已配置模型仍可路由；启动/停机可取消发现。
- **边界与最终去向**：FINAL-013、FINAL-014、FINAL-015。补充实测/反证/不确定性见最终项；没有真实上游或客户端运行的地方不据此声称现网必现。

#### CODEX-004 Google、智谱的 Ollama 接口流式请求使用错误路径

- **类型 / 严重程度 / 状态**：功能 / 接口路由；P2；最终已确认。严重度依据：应走 v1beta/openai/chat/completions 或 v4/chat/completions；实际请求 v1/chat/completions。这两个提供商此接口的流式请求受损；其他接口存在正确路径，故本轮定 P2 而非全服务 P1。
- **定位 / 符号**：Endpoints/OllamaEndpoints.cs:371-378；Services/ProviderCapabilitiesRegistry.cs:68,78。符号：OllamaEndpoints.MapOllamaEndpoints 流式OpenAI上游分支。基线：`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **预期与实际、触发和影响**：POST /api/chat，stream=true，路由到 google 或 zhipu。 应走 v1beta/openai/chat/completions 或 v4/chat/completions；实际请求 v1/chat/completions。这两个提供商此接口的流式请求受损；其他接口存在正确路径，故本轮定 P2 而非全服务 P1。
- **根因与完整静态链**：流式 OpenAI 转换分支硬编码 v1/chat/completions，绕过 provider.Capabilities.ChatPath。；上述调用由真实端点/启动路径到达，保护条件与边界详列于对应FINAL项。
- **独立证据、复现及实际结果**：独立真实端点捕获google与zhipu均到/v1/chat/completions，能力表分别要求其他路径。
- **修复方向**：流式请求使用能力表 ChatPath；原生 api/chat 硬编码目前值相同，可随改动统一。
- **回归验证**：仅为正确路径提供响应的 google/zhipu stub：普通、流式两协议均命中正确路径；错误路径零请求。
- **边界与最终去向**：FINAL-003。补充实测/反证/不确定性见最终项；没有真实上游或客户端运行的地方不据此声称现网必现。

#### CODEX-005 模型超时在 Ollama 接口读取流体时失效

- **类型 / 严重程度 / 状态**：超时 / 资源占用；P2；最终已确认。严重度依据：应到模型期限取消正文读取；实际只在客户端取消时才停，连接和请求可超过配置期限。
- **定位 / 符号**：Endpoints/OllamaEndpoints.cs:246-250,290-302,377-389；Services/ChatStreamingService.cs:129-164。符号：MapOllamaEndpoints；StreamNdjsonPassthrough / StreamOllamaAndCache。基线：`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **预期与实际、触发和影响**：上游及时返回 headers，正文缓慢或持续保持连接；模型配置 timeout_seconds。 应到模型期限取消正文读取；实际只在客户端取消时才停，连接和请求可超过配置期限。
- **根因与完整静态链**：SendAsync 用模型 linked token，而后续 StreamNdjsonPassthrough/StreamOllamaAndCache 却只收到客户端 ct；ResponseHeadersRead 后 HttpClient 超时也不约束 body。；上述调用由真实端点/启动路径到达，保护条件与边界详列于对应FINAL项。
- **独立证据、复现及实际结果**：独立模型期限1秒、正文延迟1.3秒，约1.4秒仍200完成；.NET10 HeadersRead超时说明已核对。
- **修复方向**：将模型 token 贯穿正文读取与错误体读取；分别处理客户端取消与模型超时，已开始流避免二次写 JSON。
- **回归验证**：headers 立即返回、body 挂起的两种上游格式均在 1 秒测试期限内结束；客户端取消立即释放；正常流不受影响。
- **边界与最终去向**：FINAL-009。补充实测/反证/不确定性见最终项；没有真实上游或客户端运行的地方不据此声称现网必现。

#### CODEX-006 未完成的上游 SSE 被转为成功结束的 Ollama 响应

- **类型 / 严重程度 / 状态**：流式完整性；P2；最终已确认。严重度依据：预期明确中断/错误，避免将部分输出当成功；实际向客户端追加 done=true, done_reason=stop，截断回答或代码可能被采纳。
- **定位 / 符号**：Services/ChatStreamingService.cs:156,160-165,206-208,248-259。符号：ChatStreamingService.StreamOllamaAndCache。基线：`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **预期与实际、触发和影响**：上游先输出部分内容，再正常关闭 HTTP body，但未输出完成事件；或流内错误事件被解析分支跳过。 预期明确中断/错误，避免将部分输出当成功；实际向客户端追加 done=true, done_reason=stop，截断回答或代码可能被采纳。
- **根因与完整静态链**：只以 EOF 结束循环，没有检查 finish_reason/[DONE]；finishReason 初始 stop，循环结束无条件 done=true。；上述调用由真实端点/启动路径到达，保护条件与边界详列于对应FINAL项。
- **独立证据、复现及实际结果**：独立仅partial delta、无完成事件输入，仍输出done=true/stop。另记录普通SSE吞HttpIOException，但不误称该方法伪造DONE。
- **修复方向**：追踪终止事件和错误事件；未完成 EOF 不伪造成功。连接已发字节后终止/协议内错误，不能重放整个请求。
- **回归验证**：正常完成恰有一个终止对象；无终止 EOF、错误 SSE、坏 JSON、读异常均不得生成成功 stop。
- **边界与最终去向**：FINAL-008。补充实测/反证/不确定性见最终项；没有真实上游或客户端运行的地方不据此声称现网必现。

#### CODEX-007 转换到 Ollama 时把工具结果改为 assistant 消息；两协议的工具参数结构直接复制，且流式参数碎片未组装；Ollama 工具响应转换为 OpenAI 流时工具调用被丢弃；同一合成 OpenAI SSE completion 使用多个 ID

- **类型 / 严重程度 / 状态**：工具协议 / 语义损坏；工具协议 / 序列化；工具协议 / 流式；SSE响应契约；P2；最终已确认。严重度依据：工具执行结果应保持 tool 语义；实际变成 assistant 自述，工具关联丢失，后续生成上下文错误。 OpenAI 需要 JSON 编码字符串与调用 id/type，Ollama 使用 arguments 对象。实际形状不匹配，Ollama 客户端还会收到未完成的参数片段，工具执行失败或参数不完整。 应向客户端发出工具调用；实际是空 content + stop + DONE，编码代理无法继续执行工具。 同一completion所有chunk应同id；实际内容和结束chunk不同id，依赖id归并的客户端可能无法合并。
- **定位 / 符号**：Endpoints/OpenAiEndpoints.cs:590-610（BuildOllamaChatRequest）；Endpoints/OpenAiEndpoints.cs:590-615,709-711；Endpoints/OllamaEndpoints.cs:359-360,424-458；Services/ChatStreamingService.cs:190-203,232-233；Endpoints/OpenAiEndpoints.cs:474-519（HandleOllamaCloudChatCompletion）；Endpoints/OpenAiEndpoints.cs:491,508。符号：BuildOllamaChatRequest；ConvertOllamaChatToOpenAiCompletion；HandleOllamaCloudChatCompletion；StreamOllamaAndCache。基线：`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **预期与实际、触发和影响**：OpenAI 客户端的多轮工具调用，历史里存在 role=tool，目标为 Ollama。 工具执行结果应保持 tool 语义；实际变成 assistant 自述，工具关联丢失，后续生成上下文错误。 使用 tools，响应或历史含函数调用；OpenAI 流式 arguments 分多个 delta。 OpenAI 需要 JSON 编码字符串与调用 id/type，Ollama 使用 arguments 对象。实际形状不匹配，Ollama 客户端还会收到未完成的参数片段，工具执行失败或参数不完整。 POST /v1/chat/completions，stream=true，Ollama 返回带工具调用的 message。 应向客户端发出工具调用；实际是空 content + stop + DONE，编码代理无法继续执行工具。 OpenAI流式入口选择Ollama上游，进入合成SSE分支。 同一completion所有chunk应同id；实际内容和结束chunk不同id，依赖id归并的客户端可能无法合并。
- **根因与完整静态链**：用“OpenAI 上游拒绝 tool”的错误方向解释，在只服务 Ollama 上游的转换函数中无条件改写 role。 将 OpenAI tool_calls 原样复制到 Ollama，反向也原样复制；没有处理 arguments 的字符串/对象差异及流式按 index 累积。 先生成完整 completion，随后仅提取 contentText 构造 SSE delta，完全忽略 tool_calls，结束原因固定 stop。 firstChunk和finishChunk分别调用Guid.NewGuid，未复用completion身份。；上述调用由真实端点/启动路径到达，保护条件与边界详列于对应FINAL项。
- **独立证据、复现及实际结果**：独立生产反射证明tool→assistant、arguments字符串原样；真实端点证明Ollama工具响应经SSE后消失，两个chunk ID不同。官方协议补查在固定后进行；角色改写、字段复制、工具丢失、ID生成是不同根因，最终分四项。
- **修复方向**：保留工具角色，按调用上下文映射 tool_call_id/tool_name 等关联字段；不要只换角色绕过校验。 显式实现双向工具调用映射；流式按 index 累积 name/arguments/id，完整 JSON 后输出原生工具调用；正确关联后续工具结果。 将转换后的工具调用编码为合法 OpenAI SSE delta；结束原因反映 tool_calls；内容和工具可同时存在。 生成一次id/created并在整个完成流复用。
- **回归验证**：一次调用、两次并行调用和第二轮结果，转换后工具角色/函数名/顺序可追溯；模拟原生请求校验器接受报文。 带嵌套对象/转义字符/并行调用/跨三个 delta 的参数，最终两端 schema 都合法且值完全一致；缺 id 的原生调用补稳定 ID。 仅工具、工具与文本混合、多工具三种响应，客户端能重建完整调用并进入下一轮；不得误标普通 stop。 普通文本、空文本、工具流逐片断言id相同、created一致、只有一个逻辑完成。
- **边界与最终去向**：FINAL-004、FINAL-005、FINAL-006、FINAL-028。补充实测/反证/不确定性见最终项；没有真实上游或客户端运行的地方不据此声称现网必现。

#### CODEX-008 OpenAI 图像 data URL 被原样塞入 Ollama 的 Base64 数组

- **类型 / 严重程度 / 状态**：视觉协议 / 编码；P2；最终已确认。严重度依据：原生 REST 的 images 需要 Base64 编码的图像数据；实际带 data: 前缀或http链接，不符合原生编码，视觉请求无法按契约解析。
- **定位 / 符号**：Endpoints/OpenAiEndpoints.cs:543-584,618-626（BuildOllamaChatRequest）。符号：OpenAiEndpoints.BuildOllamaChatRequest。基线：`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **预期与实际、触发和影响**：OpenAI 入口向原生 Ollama 发送含 data:image/...;base64,... 的图像。 原生 REST 的 images 需要 Base64 编码的图像数据；实际带 data: 前缀或http链接，不符合原生编码，视觉请求无法按契约解析。
- **根因与完整静态链**：图像转换只复制 image_url.url，没有从 data URL 提取 Base64 载荷，也没有区分远程URL与原生图像编码。；上述调用由真实端点/启动路径到达，保护条件与边界详列于对应FINAL项。
- **独立证据、复现及实际结果**：独立生产反射输出images仍含data URL。冻结时为待核验协议的候选；固定后用官方REST视觉文档确认编码要求，最终已确认。
- **修复方向**：解析合法 data URL 并验证/提取 payload；远程 URL 明确拒绝或独立设计受限制的获取流程，不能直接透传当Base64；保留图像类型。
- **回归验证**：真实最小PNG/JPEG的data URL转为可解码字节；多图及纯文本不丢失；坏URL/非Base64给400；不引入任意URL抓取。
- **边界与最终去向**：FINAL-019。补充实测/反证/不确定性见最终项；没有真实上游或客户端运行的地方不据此声称现网必现。

#### CODEX-009 结构合法但字段类型错误的请求触发 500

- **类型 / 严重程度 / 状态**：输入校验 / API 错误契约；P2；最终已确认。严重度依据：预期结构化400且不调用上游；实际请求进入未捕获 InvalidOperationException/格式异常，500。JSON语法错误已有400保护，不能说所有畸形JSON都500。
- **定位 / 符号**：Endpoints/OpenAiEndpoints.cs:139-149,196；Services/RequestTransformer.cs:227-239；Endpoints/OllamaEndpoints.cs:422,428,484；Endpoints/OpenAiEndpoints.cs:557-577。符号：RequestTransformer.ModifyRequest；两端点请求转换。基线：`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **预期与实际、触发和影响**：例如 /v1/chat/completions 发送 model=probe@google,messages=42；或消息嵌套字段类型错误。 预期结构化400且不调用上游；实际请求进入未捕获 InvalidOperationException/格式异常，500。JSON语法错误已有400保护，不能说所有畸形JSON都500。
- **根因与完整静态链**：只校验根对象和 stream 布尔，没有验证 messages、role、tool_calls、images、选项数值等；转换器直接调用类型专用 API。；上述调用由真实端点/启动路径到达，保护条件与边界详列于对应FINAL项。
- **独立证据、复现及实际结果**：独立messages=42得到500，upstream为空；其他嵌套字段缺类型保护按调用链记录。
- **修复方向**：在入口集中校验必要结构与字段类型/整数范围，返回具体 param 的400；不能只吞转换异常后把坏请求送上游。
- **回归验证**：messages=null/string/object/number，数组元素非对象，role非字符串，tool_calls/images非数组，num_predict非整数/越界均400、无上游调用；合法null content不被误拒。
- **边界与最终去向**：FINAL-018。补充实测/反证/不确定性见最终项；没有真实上游或客户端运行的地方不据此声称现网必现。

#### CODEX-010 Ollama 分支上游传输错误未规范处理，且会打断非流式候选切换

- **类型 / 严重程度 / 状态**：错误传播 / 故障切换；P2；最终已确认。严重度依据：应结构化 502/504，非流式允许的候选继续切换；实际可能 500 并终止列表。Ollama HTTP 4xx/5xx 只返回 false，会丢失最后错误并可能返回通用 502或更早候选错误。
- **定位 / 符号**：Endpoints/OllamaEndpoints.cs:273-274,290-291,331-332,377-378；Endpoints/OpenAiEndpoints.cs:224-231,431-438,459-469。符号：TryHandleOllamaCloudChatCompletion / HandleOllamaCloudChatCompletion；MapOllamaEndpoints。基线：`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **预期与实际、触发和影响**：上游连接拒绝、传输异常或模型超时；/v1 非流式列表遇到 Ollama 候选；或它是唯一失败候选。 应结构化 502/504，非流式允许的候选继续切换；实际可能 500 并终止列表。Ollama HTTP 4xx/5xx 只返回 false，会丢失最后错误并可能返回通用 502或更早候选错误。
- **根因与完整静态链**：这些调用不在 OpenAI 候选 SendAsync 的 catch 中；Ollama helper 失败也不返回 HTTP 状态/正文给 lastResponse。；上述调用由真实端点/启动路径到达，保护条件与边界详列于对应FINAL项。
- **独立证据、复现及实际结果**：独立Ollama合成传输异常经/v1返回500；静态发现helper既绕过catch，也不保留HTTP错误。api/chat四处未捕获调用一并固定。
- **修复方向**：在各发送边界统一区分传输故障、客户端取消、模型超时；helper 返回可保留的状态/正文；非流式继续后续候选，严格路由不跨 provider。
- **回归验证**：各格式连接拒绝/超时/401/429/503；可切换列表第一项失败第二项成功；唯一 Ollama 错误按明确策略保留；客户端取消不重试。
- **边界与最终去向**：FINAL-010。补充实测/反证/不确定性见最终项；没有真实上游或客户端运行的地方不据此声称现网必现。

#### CODEX-011 原生 Ollama 直通忽略配置的执行默认值与强制覆盖

- **类型 / 严重程度 / 状态**：配置执行；P2；最终已确认。严重度依据：预期与 /v1 的执行策略一致；实际 client temperature 原样保留且缺省限制不注入。可改变采样、输出长度与成本；不需证明真实服务一定拒绝才构成配置失效。
- **定位 / 符号**：Endpoints/OllamaEndpoints.cs:230-232,252-280；config/model-selection/ollamacloud.json:5-19,103-117。符号：OllamaEndpoints.MapOllamaEndpoints 原生直通分支。基线：`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **预期与实际、触发和影响**：/api/chat 路由到原生 Ollama，模型设置 temperature/max_tokens/override_client_params。 预期与 /v1 的执行策略一致；实际 client temperature 原样保留且缺省限制不注入。可改变采样、输出长度与成本；不需证明真实服务一定拒绝才构成配置失效。
- **根因与完整静态链**：取得 execution 后直通只修改 model，不映射 options；不经过 ApplyExecutionDefaults。；上述调用由真实端点/启动路径到达，保护条件与边界详列于对应FINAL项。
- **独立证据、复现及实际结果**：独立内存force配置temp1/max4，捕获仍temp0.3且缺num_predict；配置未动项目文件。
- **修复方向**：原生路径按同一策略合并 options，将 max_tokens/max_completion_tokens 映射 num_predict，保留未要求覆盖的客户端参数。
- **回归验证**：真实配置和合成配置覆盖省略、已有、force true/false，两个入口最终上游选项一致。
- **边界与最终去向**：FINAL-011。补充实测/反证/不确定性见最终项；没有真实上游或客户端运行的地方不据此声称现网必现。

#### CODEX-012 精确禁用条目会被宽泛启用规则重新纳入

- **类型 / 严重程度 / 状态**：模型选择 / 配置契约；P2；最终已确认。严重度依据：预期明确禁用模型不被目录接纳；实际可在 /v1/models 出现并被 @ollama 严格路由，违背配置选择意图。
- **定位 / 符号**：Services/ModelSelectionStore.cs:85-103；Services/ModelCatalogService.cs:49-72,99-114,545-549；config/model-selection/ollama.json:22；config/model-selection/ollamacloud.json:170-183。符号：ModelSelectionStore.FindModelSelectionEntry；ModelCatalogService.RefreshAvailableModels。基线：`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **预期与实际、触发和影响**：上游目录包含 mistral-large-3:675b；原有两个 Ollama 配置合并，一个宽泛启用、一个精确禁用。 预期明确禁用模型不被目录接纳；实际可在 /v1/models 出现并被 @ollama 严格路由，违背配置选择意图。
- **根因与完整静态链**：禁用条目被跳过，不作为精确否决；另一个启用的 mistral 子串继续匹配。；上述调用由真实端点/启动路径到达，保护条件与边界详列于对应FINAL项。
- **独立证据、复现及实际结果**：首次探针从probes子目录运行没加载配置，返回null，不能作为结论；纠正到隔离项目根后使用原始配置复现disabled ID命中mistral。固定后进一步补了发现→路由集成验证。
- **修复方向**：先检查精确禁用；明确配置中的负向规则优先级，或将默认泛匹配改成显式列表。
- **回归验证**：被禁精确 ID 即使符合启用子串也不列出、不接受为严格候选；显式启用变体继续工作。
- **边界与最终去向**：FINAL-016。补充实测/反证/不确定性见最终项；没有真实上游或客户端运行的地方不据此声称现网必现。

#### CODEX-013 tags/show 查询丢失提供商身份，返回其他提供商的模型能力；capabilities 恒包含 tools，与 supports_tools=false 矛盾

- **类型 / 严重程度 / 状态**：元数据 / 跨模块契约；元数据契约；P2；最终已确认。严重度依据：路由可正确，描述却来自另一家，客户端可能错误启用视觉/工具，或按过大上下文构造请求。 预期tools能力与显式布尔一致；实际同时返回false和tools能力，客户端可能展示不可用工具能力。
- **定位 / 符号**：Endpoints/OllamaEndpoints.cs:79,137,162；Services/OllamaResponseBuilder.cs:10-13；Services/ModelCatalogService.cs:187-190；Services/ModelSelectionStore.cs:13-53；Services/ModelCatalogService.cs:192,267-283。符号：ModelCatalogService.GetModelProfile；OllamaResponseBuilder.BuildOllamaShowResponse。基线：`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **预期与实际、触发和影响**：两家提供商提供同名模型，但上下文、视觉、工具或推荐参数不同；请求限定其中一家。 路由可正确，描述却来自另一家，客户端可能错误启用视觉/工具，或按过大上下文构造请求。 当前或用户配置supports_tools=false，通过tags/show读取能力。 预期tools能力与显式布尔一致；实际同时返回false和tools能力，客户端可能展示不可用工具能力。
- **根因与完整静态链**：路由结果被简化成 upstream 字符串，profile 方法再跨所有 provider 选择配置，未使用请求所限定 provider。 capabilities仅按vision生成，忽略已计算的tools布尔值。；上述调用由真实端点/启动路径到达，保护条件与边界详列于对应FINAL项。
- **独立证据、复现及实际结果**：独立源码链发现tags/show丢provider；独立合成supports_tools=false仍返回tools。冻结后追加show反例context2000却返回1000。两种根因最终分别列出。
- **修复方向**：模型元数据方法贯穿 provider 身份；tags 用遍历中的 provider，show 用 route.Candidates 的实例；避免全局猜测覆盖已选 provider。 按tools、vision独立构造能力列表。
- **回归验证**：两 provider 同名模型配置相反的视觉/工具/上下文值；tags 和 show 分别匹配该 provider，刷新前后均一致。 tools/vision四种组合，布尔字段与capabilities完全一致；被排除类别不能宣称工具能力。
- **边界与最终去向**：FINAL-017、FINAL-027。补充实测/反证/不确定性见最终项；没有真实上游或客户端运行的地方不据此声称现网必现。

#### CODEX-014 没有执行默认值时，unsupported top_k 过滤直接跳过

- **类型 / 严重程度 / 状态**：参数过滤；P2；最终已确认。严重度依据：应按Capabilities剥离top_k；实际保留，支持矩阵行为不一致，可能被严格上游拒绝。
- **定位 / 符号**：Services/RequestTransformer.cs:24-32,46,129-132；config/model-selection/kimicode.json:8-14。符号：RequestTransformer.ApplyExecutionDefaults。基线：`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **预期与实际、触发和影响**：模型只有能力/超时配置（当前kimicode如此）或没有默认值，客户端发送top_k，provider声明不支持。 应按Capabilities剥离top_k；实际保留，支持矩阵行为不一致，可能被严格上游拒绝。
- **根因与完整静态链**：hasAnyDefault=false 导致整段返回，过滤逻辑错误依赖是否配置默认采样值。；上述调用由真实端点/启动路径到达，保护条件与边界详列于对应FINAL项。
- **独立证据、复现及实际结果**：独立空执行配置、caps不支持top_k，输出仍有top_k:20。没有把客户端reasoning_effort原样透传一概列为缺陷。
- **修复方向**：把必须过滤的逻辑移出默认值早退；无默认也进行必要字段处理。
- **回归验证**：同一caps分别搭配空配置、仅timeout、含采样默认；top_k始终按支持标志处理，允许的客户端字段不变。
- **边界与最终去向**：FINAL-022。补充实测/反证/不确定性见最终项；没有真实上游或客户端运行的地方不据此声称现网必现。

#### CODEX-015 端点测试启动会重新读取真实 .env，测试夹具缺乏隔离

- **类型 / 严重程度 / 状态**：测试安全 / 有效性；P2；最终已确认。严重度依据：测试应只到stub；实际可能启动真实提供商目录查询、带真实key并使结果依赖外界。该执行边界不能仅靠约定“无外网调用”保证。
- **定位 / 符号**：tests/ProxyTests/EndpointTests.cs:74-96；Program.cs:4-24；tests/ProxyTests/EndpointTests.cs:15-18。符号：ProxyFixture构造函数；Program.env加载循环。基线：`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **预期与实际、触发和影响**：开发者在含有效.env的项目根目录运行现有端点测试，或继承未清理的provider key。 测试应只到stub；实际可能启动真实提供商目录查询、带真实key并使结果依赖外界。该执行边界不能仅靠约定“无外网调用”保证。
- **根因与完整静态链**：ProxyFixture设置合成环境后用WebApplicationFactory启动真实Program；Program又读AppContext/CWD的.env，Testing环境没有隔离；夹具也未清所有已支持provider。；上述调用由真实端点/启动路径到达，保护条件与边界详列于对应FINAL项。
- **独立证据、复现及实际结果**：独立读取测试启动链后识别.env覆盖fake环境的可能，故469测试只在无.env副本运行；Testing下.env仍被加载的实际进程补证在交叉阶段完成。
- **修复方向**：夹具显式替换ProviderHttpClientFactory/配置加载入口，禁用.env自动加载并隔离所有provider；避免只维护容易遗漏的key清单。
- **回归验证**：在测试专用目录放合成.env且预设所有provider环境，断言全部请求到fake handler，真实socket目的地零个；退出恢复环境。
- **边界与最终去向**：FINAL-026。补充实测/反证/不确定性见最终项；没有真实上游或客户端运行的地方不据此声称现网必现。

#### CODEX-016 基准脚本将全部失败的提供商评为最快；综合基准用显示名称匹配限定模型 ID，导致全部跳过

- **类型 / 严重程度 / 状态**：分析脚本正确性；分析脚本 / 模型身份；P3；最终已确认。严重度依据：应从成功结果里选最快；实际失败项-1排第一，产生错误FASTEST推荐，误导配置决策。 应测量存在的模型；实际所有预设限定ID与显示名称不等，测试被SKIP，报告没有有效比较。
- **定位 / 符号**：scripts/benchmark-duplicate-models.ps1:106-111,132-139；scripts/comprehensive-benchmark.py:54-66；Endpoints/OllamaEndpoints.cs:70,76,82-86。符号：benchmark-duplicate-models.ps1排名段；comprehensive-benchmark.py可用模型筛选。基线：`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **预期与实际、触发和影响**：同组至少一项全失败、另一项成功。 应从成功结果里选最快；实际失败项-1排第一，产生错误FASTEST推荐，误导配置决策。 脚本连接当前版本代理的正常tags响应。 应测量存在的模型；实际所有预设限定ID与显示名称不等，测试被SKIP，报告没有有效比较。
- **根因与完整静态链**：失败平均延迟设-1，最终不筛选成功便按延迟升序取第一项。 available取tags.models[].name（PROVIDER - model:latest），却与预设model@provider比较。；上述调用由真实端点/启动路径到达，保护条件与边界详列于对应FINAL项。
- **独立证据、复现及实际结果**：独立完整静态证据：失败=-1参与升序排名；另一脚本以显示name匹配model@provider。是两个根因，最终拆分；真实基准没有运行。
- **修复方向**：先筛SuccessCount>0且有效延迟，全部失败时明确无可用结果；报告保留失败记录但不参与速度排名。 使用tags.model或解析aliases，按稳定模型身份匹配并保留上游/提供商诊断；不要比较显示名。
- **回归验证**：合成结果[-1,100,200]选100；全失败无winner；部分成功按既定统计指标选择。 合成当前tags至少两组模型，存在项被实际调用、缺失项才skip；模型显示名变化不影响测试选择。
- **边界与最终去向**：FINAL-029、FINAL-030。补充实测/反证/不确定性见最终项；没有真实上游或客户端运行的地方不据此声称现网必现。

#### CODEX-017 Ollama 请求中的 stop、seed 和结构化输出 format 被静默丢弃

- **类型 / 严重程度 / 状态**：请求参数 / 协议转换；P2；最终已确认。严重度依据：应转换支持的语义或明确告知不支持；实际悄悄删除，停止条件、可重复性和JSON输出约束失效。
- **定位 / 符号**：Endpoints/OllamaEndpoints.cs:474-517。符号：OllamaEndpoints.ConvertOllamaToOpenAi。基线：`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **预期与实际、触发和影响**：/api/chat 转到支持相应选项的OpenAI兼容上游，客户端指定停止序列、随机种子或JSON格式。 应转换支持的语义或明确告知不支持；实际悄悄删除，停止条件、可重复性和JSON输出约束失效。
- **根因与完整静态链**：options.stop/seed 被当作没有OpenAI对应字段直接跳过；format 被排除但没有映射 response_format。；上述调用由真实端点/启动路径到达，保护条件与边界详列于对应FINAL项。
- **独立证据、复现及实际结果**：独立生产反射输入format/json、stop、seed，输出均不含它们；固定后补查官方协议。
- **修复方向**：按提供商/模型支持度映射stop、seed、response_format；不支持时有明确契约；避免透传所有原生options。
- **回归验证**：支持这些字段的stub捕获等价参数；不支持模型给明确拒绝/文档化行为；options其他字段不被回归删除。
- **边界与最终去向**：FINAL-021。补充实测/反证/不确定性见最终项；没有真实上游或客户端运行的地方不据此声称现网必现。

#### CODEX-018 本地与云 Ollama 同名，明确的本地模型仍被送往云实例

- **类型 / 严重程度 / 状态**：路由 / 信任边界；P2；最终已确认。严重度依据：预期按目录所有者到本地；实际严格路由抓取了先注册的 cloud Client。可能导致 404，也可能将预期留在本地的提示词发给云。
- **定位 / 符号**：Services/ProviderRegistry.cs:164-176,257-272；Services/ModelCatalogService.cs:178-184,413-431,545-549。符号：ProviderRegistry.DiscoverProviders / FindConfiguredProvider；ModelCatalogService.ResolveWithinProvider。基线：`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **预期与实际、触发和影响**：同时配置 OLLAMACLOUD 的 key/base 与本地 PROVIDER_OLLAMA_BASE_URL，两个地址不同；本地模型登记为 local-only@ollama。 预期按目录所有者到本地；实际严格路由抓取了先注册的 cloud Client。可能导致 404，也可能将预期留在本地的提示词发给云。
- **根因与完整静态链**：注册两个 Name=ollama 的 ProviderInfo；FindConfiguredProvider 选首项，目录映射及去重又只按名称认实例。；上述调用由真实端点/启动路径到达，保护条件与边界详列于对应FINAL项。
- **独立证据、复现及实际结果**：独立静态链发现重复ollama名字、按名字去重与首项查找不一致。固定后交叉探针证明目录local-only所有者是localhost却解析到cloud。
- **修复方向**：赋予本地/云稳定且唯一的实例身份；配置、目录键、显示别名、查找、去重同时使用该身份；保留旧别名的明确迁移策略。
- **回归验证**：双实例不同模型及同名模型分别测试 tags/show/chat；明确本地路由必须只到本地 handler；候选去重不得误合并。
- **边界与最终去向**：FINAL-002。补充实测/反证/不确定性见最终项；没有真实上游或客户端运行的地方不据此声称现网必现。

#### CODEX-019 省略 stream 时代理分支与原生 Ollama 默认行为不一致

- **类型 / 严重程度 / 状态**：流式默认值 / 协议；P2；最终已确认。严重度依据：代理走缓冲单JSON路径，上游返回NDJSON；响应标 application/json 却含多条JSON。转OpenAI上游时则显式false，失去原生默认流式。
- **定位 / 符号**：Endpoints/OllamaEndpoints.cs:211,268-280；Services/ChatStreamingService.cs:129-145。符号：OllamaEndpoints.MapOllamaEndpoints 原生非流式分支。基线：`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **预期与实际、触发和影响**：原生 Ollama 客户端省略 stream，请求经 /api/chat 路由到原生上游。 代理走缓冲单JSON路径，上游返回NDJSON；响应标 application/json 却含多条JSON。转OpenAI上游时则显式false，失去原生默认流式。
- **根因与完整静态链**：缺省 stream 被代理判为false，而发到原生上游的 body 没有写入false；原生默认true。；上述调用由真实端点/启动路径到达，保护条件与边界详列于对应FINAL项。
- **独立证据、复现及实际结果**：独立固定省略stream被判false且原body不补false；冻结时待核验原生缺省值。固定后官方默认true及端点两行响应补证，现已确认。
- **修复方向**：统一在解析时采用原生默认true，并让实际上游body和下游Content-Type与分支一致；明确保留显式false。
- **回归验证**：省略stream、true、false分别对两种上游测试；省略/true逐行输出且Content-Type正确，false只输出一个完成JSON。
- **边界与最终去向**：FINAL-020。补充实测/反证/不确定性见最终项；没有真实上游或客户端运行的地方不据此声称现网必现。

#### CODEX-020 模型发现失败时 /v1/models 缺少仍可路由的静态模型

- **类型 / 严重程度 / 状态**：模型枚举 / 可用性；P2；最终已确认。严重度依据：ResolveRoute能以配置精确项服务模型，列表却不显示，客户端无法选到本可用模型。需要区分“可路由”与“已探测可用”，不必声称上游实际健康。
- **定位 / 符号**：Endpoints/OpenAiEndpoints.cs:14-90；Services/ModelCatalogService.cs:436-449,491-512。符号：OpenAiEndpoints.MapOpenAiEndpoints 模型列表；ModelCatalogService.ConfigExactClaims。基线：`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **预期与实际、触发和影响**：配置启用并提供API key，但/models目录失败/空；客户端依赖/v1/models选模型。 ResolveRoute能以配置精确项服务模型，列表却不显示，客户端无法选到本可用模型。需要区分“可路由”与“已探测可用”，不必声称上游实际健康。
- **根因与完整静态链**：列表实现只遍历AvailableModels和ModelToProvider；注释承诺合入static config，但注入的ModelSelectionStore从未用于枚举。；上述调用由真实端点/启动路径到达，保护条件与边界详列于对应FINAL项。
- **独立证据、复现及实际结果**：独立完整静态链确认列表未使用静态配置，路由却可用ConfigExactClaims；固定后端点补证候选1但列表无same。
- **修复方向**：列表按明确契约合入已激活provider的enabled精确模型，或者显式提供可配置列表来源；不得凭无效/禁用项制造模型。
- **回归验证**：空目录时仍列出可按配置路由的精确ID及限定别名；停用provider/禁用模型不出现；恢复发现后不重复。
- **边界与最终去向**：FINAL-023。补充实测/反证/不确定性见最终项；没有真实上游或客户端运行的地方不据此声称现网必现。

独立阶段另外固定了尚未证实的目录快照发布风险和Docker标签风险，见B-003/B-004；测试复制实现、文档、资源所有权等归C类。没有把Google模型前缀或Kimi Coding固定采样规则事后添加为Codex独立发现。

## Codex 交叉复核

### 全部原始编号与Grok复核结论核验

前轮编号共 **35个（KIMI 23 + GROK 12）**；其中一个编号可能打包多子项，数量不代表独立根因数量。下表逐项给出结论、对Grok复核的同意/修正及最终去向。“重复”表示根因重复，不表示没有缺陷。对子项分别处理，不能用整条“确认”掩盖某个未证实推断。

| Kimi编号 | Codex结论 | 对前轮结论的核验、证据与修正 | 最终去向 |
|---|---|---|---|
| KIMI-001 | 确认 | 同意Grok对根因的确认；独立抓到google/zhipu错误路径。仅影响两提供商的/api流式分支，最终P2。 | FINAL-003 |
| KIMI-002 | 确认 | 同意主结论；独立合成缓存串会话。纠正“tool ID天然唯一不受影响”：无会话/provider隔离，也可由客户端构造重复ID。 | FINAL-001 |
| KIMI-003 | 部分成立 | 未捕获传输失败确认；不是整个进程崩溃，已开始的流也不能保证还返回500。与KIMI-012同属错误处理边界遗漏，最终P2。 | FINAL-010 |
| KIMI-004 | 确认 | cross_tool_no_content实测500；content:null不触发缺键异常，空choices应独立按坏上游报文处理。与Grok确认一致。 | FINAL-007 |
| KIMI-005 | 确认 | messages=42实测500且零上游调用。补充嵌套类型校验；已存在根JSON与stream保护，不夸大为所有坏请求。 | FINAL-018 |
| KIMI-006 | 确认 | 完整启动调用链；实际程序慢目录探针补证。先绑定/短发现期限才是修复方向，最终与刷新退避分开计数。 | FINAL-015 |
| KIMI-007 | 确认 | 空结果不更新时间且反复请求；单独区分负向缓存与并发去重两个根因。与Grok静态确认一致。 | FINAL-013、FINAL-014 |
| KIMI-008 | 部分成立 | 覆盖行为实测确认；原CLAUDE已删除，但DEPLOYMENT:318-323仍承诺系统优先。CONFIGURATION恰写.env在前，纠正Grok此处引用。主契约冲突P2；行尾注释等归建议。 | FINAL-024；C-002；D-001 |
| KIMI-009 | 确认 | 调用生产NormalizeBaseUrl后实际URI含双v1beta/openai；默认base不受影响。 | FINAL-025 |
| KIMI-010 | 确认 | 1001条低负载保留+全类无淘汰。没有声称已测OOM；与Grok一致，独立于隐私根因。 | FINAL-012 |
| KIMI-011 | 部分成立 | 首字节前可切换，代码不切换属实；但当前docs/API.md明确streaming does not failover，不能只以“技术上能做”认定违反现有契约。推翻其确定功能缺陷分类，保留可靠性建议；严格路由永不跨provider。 | C-007；D-003 |
| KIMI-012 | 确认 | 本轮真实端点+合成HttpRequestException复现500；补充HTTP错误被helper丢弃。不同调用点不自动成为不同根因，与KIMI-003合并且列全位置。 | FINAL-010 |
| KIMI-013 | 确认 | 本轮抓包配置force1.0而实际0.3，无num_predict；不依赖真实上游是否拒绝。Grok“timeout仍生效”只对headers前成立，正文遗漏另列。 | FINAL-011、FINAL-009（补充边界） |
| KIMI-014 | 部分成立 | 默认全接口+可选认证属实；README明确认证可选，未有只许本机的硬契约或暴露实例证据。不能直接认定所有部署均受攻击，同意Grok应按部署选择处理。 | C-001；D-004 |
| KIMI-015 | 待验证 | 裸目录ID不匹配已用本地生产matcher确认；官方示例使用裸ID，但未给目录响应实际字节，也未证明带models/会被拒绝。不同意Grok据stub升级为必现P1。 | B-001 |
| KIMI-016 | 确认 | 独立生产转换器改role；Ollama官方多轮工具示例明确role=tool，已经有契约反证，不需要真实云key才确认语义损坏。结束前轮待验证。 | FINAL-004；相关但不同根因FINAL-005 |
| KIMI-017 | 确认 | 原配置+合成目录实测被禁ID出现在目录，限定路由成功。目录输入可由本地Ollama产生，无需证明某一天云目录已有它。结束Grok待验证；P2。 | FINAL-016 |
| KIMI-018 | 部分成立 | 官方默认true与省略false确认；本轮进一步证实原生分支body仍缺stream，NDJSON被作单JSON返回。单JSON未必使所有流客户端解析失败，纠正原影响表述，按分支不一致P2。 | FINAL-020 |
| KIMI-019 | 部分成立 | 1日志露前后片段属实但非完整密钥泄漏；2非常量比较未证实可利用计时攻击；3全局认证包含health符合现契约，不自动要求匿名。与Grok全项缺陷式确认不同。 | C-001；D-005 |
| KIMI-020 | 部分成立 | 1双目录先到先得、2吞配置错、3非法布尔当true属实但契约/校验改进；4fire-and-forget能完成且可能在返回前完成，并非永远旧数据。无闸和失败重试已单列；不能简单改await却忽略延迟。 | C-002；FINAL-013、FINAL-014；D-006 |
| KIMI-021 | 部分成立 | 1错误示例、2诊断头文档冲突仍在；3已删CLAUDE内容排除；4不根据失效文档要求删除tools/function_call/reasoning_effort，现有测试明确保留部分客户端字段。 | C-003；D-001、D-007 |
| KIMI-022 | 部分成立 | 1preview标签真实但是否失效未构建，列风险；2当前无develop强制规则，不成立；3benchmark报告累积/费用是默认关闭功能的运维建议，未证明磁盘耗尽。 | B-004；C-006；D-002 |
| KIMI-023 | 部分成立 | 1tools布尔矛盾确认；2假digest影响特定客户端待验证；3SSE两个ID确认，usage缺省归协议完善；4双实例错路由确认。逐子项拆分，避免P3打包掩盖路由问题。 | FINAL-027、FINAL-028、FINAL-002；B-005；C-007 |

| Grok编号 | Codex结论 | 核验、证据与修正 | 最终去向 |
|---|---|---|---|
| GROK-001 | 重复 | 独立复现且确认；与KIMI-001/CODEX-004同根因，严重度收敛P2。 | FINAL-003 |
| GROK-002 | 待验证 | 确证kimicode没有采样默认且透传0.2/0.1；引用参数页描述kimi-k3等开放平台模型，不足覆盖Coding四别名。Coding官方页面还显示kimi-for-coding已指向K2.8 Preview。未拿到该端点参数规则或拒绝响应，不能确认为套餐异常的P1根因。早退top_k另有独立缺陷。 | B-002；FINAL-022（早退子事实） |
| GROK-003 | 重复 | 确认未处理传输错误；与KIMI-003/012及CODEX-010合并，不把500升为全局可用性故障，P2。 | FINAL-010 |
| GROK-004 | 重复 | 独立合成注入证实，P1；前轮静态证据无需多数投票即成立。 | FINAL-001 |
| GROK-005 | 重复 | 独立messages=42复现500，定位一致，P2。 | FINAL-018 |
| GROK-006 | 重复 | cross_tool_no_content实测500；缺content/null/空choices区别已修正。 | FINAL-007 |
| GROK-007 | 部分成立 | 本地裸ID目录会被过滤、显式前缀会被转发成立；“Google实际目录全裸且前缀必404”证据不足，撤销已确认P1，保留待验证。 | B-001 |
| GROK-008 | 重复 | 生产NormalizeBaseUrl+Uri复现重复目录，最终P2。 | FINAL-025 |
| GROK-009 | 重复 | 主优先级冲突确认；纠正CONFIGURATION引用，以DEPLOYMENT当前文本建立期望。 | FINAL-024；C-003 |
| GROK-010 | 重复 | 比前轮仅重复名字证据更强：实际解析local-only@ollama得到cloud BaseUrl。按名称去重会丢实例，不能笼统说总有两个failover候选。 | FINAL-002 |
| GROK-011 | 重复 | 从待验证升级：当前配置+被禁模型目录输入，列出且严格路由成功。 | FINAL-016 |
| GROK-012 | 重复 | 从待验证升级：生产方法改写+官方role=tool契约，功能缺陷确认。 | FINAL-004 |

### 关键分歧及补证

1. Google前缀：保留Kimi“待验证”的谨慎分类。真实matcher拒绝裸ID是已知条件结果，stub不是真实目录证据；Grok的“全部必然失败、P1”不能从官方裸ID示例直接推出。B-001列出最小缺失证据。
2. Kimi Coding固定采样：透传低温已证实，但开放平台与Coding别名对应、尤其kimi-for-coding版本已变化；固定四个模型参数的修复建议尚无足够依据。GROK-002转B-002，独立的top_k早退保留FINAL-022。
3. 工具角色和禁用匹配：补充官方契约、原始配置和可控输入，均可确认，不需要冒险使用真实key。分别FINAL-004、FINAL-016。
4. 首字节前重试：技术上可以，不等于现契约要求；当前明确不提供流式failover，归C-007。严格provider限定仍不可跨实例/厂商重试。
5. .env：主行为确认，但纠正Grok引错文档优先级；DEPLOYMENT才是现存“系统优先”的明确依据。双Ollama：不是仅重复显示名字，已证明实例错选。
6. P1按实际影响收敛：缓存隔离是已证实的数据跨会话问题；仅故障时500、特定接口路径错误不等同灾难性故障，最终其他功能问题按P2。无P0证据。

## 最终问题清单

### 统计、基线和使用方式

- 前两轮原始编号：KIMI **23**、GROK **12**，共 **35**。
- Codex独立冻结编号：**20**；三轮编号记录合计 **55**。这不是去重后的缺陷数。
- 根因拆分、去重后A类：**30项**，其中 **P0 0、P1 1、P2 25、P3 4**。
- B类待验证：**5项**；C类工程建议：**7组**。C组可含同一改进主题的多个非缺陷建议，不混入A类计数。
- D类保留被排除、改分类和重复映射，不能作为新增缺陷再次统计。
- 每项默认审计人/最终复核者为Codex；来源含CODEX即其独立发现，含KIMI/GROK即对应报告的发现/记录来源。含“交叉复核”的项不冒充Codex独立发现。Grok自报独立期间一次全仓搜索误命中issue.md，因此不能把三份报告简单计为三份盲审证据。
- 适用基线为 `11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`，本轮未修复以下问题。优先修复P1隔离，再按实际使用协议安排P2；回归测试必须运行真实生产路径，不能复制转换算法自证。

### A. 已确认问题：可进入修复计划

#### FINAL-001 推理缓存跨会话、跨提供商注入旧内容

- **严重程度 / 类型 / 状态**：P1；隐私 / 数据隔离；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-001；KIMI-002；GROK-004；Codex（独立）、Kimi Code、Grok；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Services/ReasoningCacheService.cs:6,11,45-60；Services/RequestTransformer.cs:205,247-265；Services/ChatStreamingService.cs:80-91,206-216；Program.cs:38；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：写键是进程全局 assistant:N，读键却从每次请求的 assistant:0 开始；toolcall 键也没有会话或提供商范围。
- **触发条件**：会话 A 已缓存推理；不同会话 B 发送没有 reasoning_content 的 assistant 历史。也可通过重复工具 ID 命中 toolcall 键。
- **预期行为、实际影响**：预期只回填同一消息的推理；实际将 A 的内容送入 B 的上游请求，污染输出并可能跨用户或跨提供商泄露。单个用户的多会话也受影响。
- **关键证据与实际验证**：独立探针 cache_cross_conversation=true：CacheReasoningFromResponse 写入合成标记后，ModifyRequest 对完全不同的 assistant 消息输出该标记。写入、读取和端点转发调用链完整；已有 reasoning_content 非空时确有保护，但不覆盖缺省路径。Kimi 曾端到端复现，本轮未依赖其结论。
- **建议修复方向**：优先停止无身份依据的回填；定义会话/提供商/消息身份后统一读写键。仅把全局计数改为请求计数仍不隔离。
- **回归验证 / 验收**：A 写入后 B 的两种协议、普通及工具历史均不得包含 A 标记；同会话正确消息可恢复；不同提供商重复工具 ID 不串用。
- **尚存不确定性**：已证实合成内容进入另一请求，不声称已发生真实用户泄露；前轮“tool ID 天然唯一所以不受影响”缺少保证。

#### FINAL-002 本地与云 Ollama 同名，明确的本地模型仍被送往云实例

- **严重程度 / 类型 / 状态**：P2；路由 / 信任边界；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-018；GROK-010；KIMI-023.4；Codex（独立）、Kimi Code、Grok；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Services/ProviderRegistry.cs:164-176,257-272；Services/ModelCatalogService.cs:178-184,413-431,545-549；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：注册两个 Name=ollama 的 ProviderInfo；FindConfiguredProvider 选首项，目录映射及去重又只按名称认实例。
- **触发条件**：同时配置 OLLAMACLOUD 的 key/base 与本地 PROVIDER_OLLAMA_BASE_URL，两个地址不同；本地模型登记为 local-only@ollama。
- **预期行为、实际影响**：预期按目录所有者到本地；实际严格路由抓取了先注册的 cloud Client。可能导致 404，也可能将预期留在本地的提示词发给云。
- **关键证据与实际验证**：cross_dual_ollama：registered=2，catalogOwner=http://localhost:9999，routed=http://ollama.test。纯内存注册真实生产类、无外网请求；确认静态 qualifiedUpstream 查找不会校验实例地址。候选链并非始终保留两者，按名称 Distinct 还会丢失实例。
- **建议修复方向**：赋予本地/云稳定且唯一的实例身份；配置、目录键、显示别名、查找、去重同时使用该身份；保留旧别名的明确迁移策略。
- **回归验证 / 验收**：双实例不同模型及同名模型分别测试 tags/show/chat；明确本地路由必须只到本地 handler；候选去重不得误合并。
- **尚存不确定性**：隐私影响取决于本地使用预期，未验证实际外发；常规单实例不受此冲突影响。

#### FINAL-003 Google、智谱的 Ollama 接口流式请求使用错误路径

- **严重程度 / 类型 / 状态**：P2；功能 / 接口路由；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-004；KIMI-001；GROK-001；Codex（独立）、Kimi Code、Grok；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Endpoints/OllamaEndpoints.cs:371-378；Services/ProviderCapabilitiesRegistry.cs:68,78；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：流式 OpenAI 转换分支硬编码 v1/chat/completions，绕过 provider.Capabilities.ChatPath。
- **触发条件**：POST /api/chat，stream=true，路由到 google 或 zhipu。
- **预期行为、实际影响**：应走 v1beta/openai/chat/completions 或 v4/chat/completions；实际请求 v1/chat/completions。这两个提供商此接口的流式请求受损；其他接口存在正确路径，故本轮定 P2 而非全服务 P1。
- **关键证据与实际验证**：google_stream_path、zhipu_stream_path 均捕获 /v1/chat/completions；模拟 handler 为观察路径返回 200，不把该 200 描述为真实上游 404。代码能力表与捕获的冲突已足够；前两轮路径严格 stub 另报告 404。
- **建议修复方向**：流式请求使用能力表 ChatPath；原生 api/chat 硬编码目前值相同，可随改动统一。
- **回归验证 / 验收**：仅为正确路径提供响应的 google/zhipu stub：普通、流式两协议均命中正确路径；错误路径零请求。
- **尚存不确定性**：未调用真实上游；结论限于所列可达路径，不推断所有客户端表现。

#### FINAL-004 转换到 Ollama 时把工具结果改为 assistant 消息

- **严重程度 / 类型 / 状态**：P2；工具协议 / 语义损坏；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-007；KIMI-016；GROK-012；Codex（独立）、Kimi Code、Grok；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Endpoints/OpenAiEndpoints.cs:590-610（BuildOllamaChatRequest）；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：用“OpenAI 上游拒绝 tool”的错误方向解释，在只服务 Ollama 上游的转换函数中无条件改写 role。
- **触发条件**：OpenAI 客户端的多轮工具调用，历史里存在 role=tool，目标为 Ollama。
- **预期行为、实际影响**：工具执行结果应保持 tool 语义；实际变成 assistant 自述，工具关联丢失，后续生成上下文错误。
- **关键证据与实际验证**：独立反射调用生产方法：role=tool 输出 role=assistant。Ollama 官方工具调用文档明确以 role=tool 回传结果（参考源 O2）；无需真实模型收费调用即可确定协议语义被破坏。
- **建议修复方向**：保留工具角色，按调用上下文映射 tool_call_id/tool_name 等关联字段；不要只换角色绕过校验。
- **回归验证 / 验收**：一次调用、两次并行调用和第二轮结果，转换后工具角色/函数名/顺序可追溯；模拟原生请求校验器接受报文。
- **尚存不确定性**：不保证每个模型都会拒绝该错误角色，但语义损坏已确定。

#### FINAL-005 两协议的工具参数结构直接复制，且流式参数碎片未组装

- **严重程度 / 类型 / 状态**：P2；工具协议 / 序列化；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-007；Codex（独立）；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Endpoints/OpenAiEndpoints.cs:590-615,709-711；Endpoints/OllamaEndpoints.cs:359-360,424-458；Services/ChatStreamingService.cs:190-203,232-233；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：将 OpenAI tool_calls 原样复制到 Ollama，反向也原样复制；没有处理 arguments 的字符串/对象差异及流式按 index 累积。
- **触发条件**：使用 tools，响应或历史含函数调用；OpenAI 流式 arguments 分多个 delta。
- **预期行为、实际影响**：OpenAI 需要 JSON 编码字符串与调用 id/type，Ollama 使用 arguments 对象。实际形状不匹配，Ollama 客户端还会收到未完成的参数片段，工具执行失败或参数不完整。
- **关键证据与实际验证**：openai_to_ollama_tool 显示 arguments 仍为字符串；生产转换源码反向保留 JsonElement，StreamOllamaAndCache 对每片 Clone 后即写出。O1/O2/O4 官方协议对照。实际端点调用形状与独立函数一致；未执行真实工具。
- **建议修复方向**：显式实现双向工具调用映射；流式按 index 累积 name/arguments/id，完整 JSON 后输出原生工具调用；正确关联后续工具结果。
- **回归验证 / 验收**：带嵌套对象/转义字符/并行调用/跨三个 delta 的参数，最终两端 schema 都合法且值完全一致；缺 id 的原生调用补稳定 ID。
- **尚存不确定性**：兼容提供商可能容忍部分冗余字段，但字符串与对象差异及碎片不可作为完整调用是确定的。

#### FINAL-006 Ollama 工具响应转换为 OpenAI 流时工具调用被丢弃

- **严重程度 / 类型 / 状态**：P2；工具协议 / 流式；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-007；Codex（独立）；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Endpoints/OpenAiEndpoints.cs:474-519（HandleOllamaCloudChatCompletion）；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：先生成完整 completion，随后仅提取 contentText 构造 SSE delta，完全忽略 tool_calls，结束原因固定 stop。
- **触发条件**：POST /v1/chat/completions，stream=true，Ollama 返回带工具调用的 message。
- **预期行为、实际影响**：应向客户端发出工具调用；实际是空 content + stop + DONE，编码代理无法继续执行工具。
- **关键证据与实际验证**：ollama_stream_tool_lost：上游模拟函数 f(x=1)，最终 SSE 无 tool_calls 且 finish_reason=stop。该路径确实被正式端点调用，非测试复制函数。
- **建议修复方向**：将转换后的工具调用编码为合法 OpenAI SSE delta；结束原因反映 tool_calls；内容和工具可同时存在。
- **回归验证 / 验收**：仅工具、工具与文本混合、多工具三种响应，客户端能重建完整调用并进入下一轮；不得误标普通 stop。
- **尚存不确定性**：未调用真实上游；结论限于所列可达路径，不推断所有客户端表现。

#### FINAL-007 合法的仅工具调用响应缺少 content 时，Ollama 接口返回 500

- **严重程度 / 类型 / 状态**：P2；响应契约 / 可靠性；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：KIMI-004；GROK-006（Codex 交叉复核）；Kimi Code、Grok；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Endpoints/OllamaEndpoints.cs:345-360；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：非流式转换使用 GetProperty(content)，把可缺省内容当必填；对 choices/message 也没有结构化错误分支。
- **触发条件**：OpenAI 兼容上游返回 message.tool_calls，但不包含 content 属性。
- **预期行为、实际影响**：预期保留工具调用、content 置空；实际未处理 KeyNotFoundException，500 空响应。content:null 本身 GetString 返回 null，不是同一失败。
- **关键证据与实际验证**：cross_tool_no_content 经真实 /api/chat 得到 500，捕获上游调用正确。源码精确解释缺键；前两轮相同复现。空 choices 属上游异常报文，应报 502，不应当合法成功。
- **建议修复方向**：使用 TryGetProperty 处理缺省/null content；校验 choices/message，对无效上游响应给出 502；与工具形状映射分开修复。
- **回归验证 / 验收**：分别覆盖缺 content、content:null、正常文本、choices=[]；前两者合法完成，最后者结构化 502。
- **尚存不确定性**：本轮确认缺 content 形态；不声称所有工具响应都失败。

#### FINAL-008 未完成的上游 SSE 被转为成功结束的 Ollama 响应

- **严重程度 / 类型 / 状态**：P2；流式完整性；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-006；Codex（独立）；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Services/ChatStreamingService.cs:156,160-165,206-208,248-259；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：只以 EOF 结束循环，没有检查 finish_reason/[DONE]；finishReason 初始 stop，循环结束无条件 done=true。
- **触发条件**：上游先输出部分内容，再正常关闭 HTTP body，但未输出完成事件；或流内错误事件被解析分支跳过。
- **预期行为、实际影响**：预期明确中断/错误，避免将部分输出当成功；实际向客户端追加 done=true, done_reason=stop，截断回答或代码可能被采纳。
- **关键证据与实际验证**：独立 StreamOllamaAndCache 输入仅一条 partial delta、无 finish/DONE，输出末条 done=true/stop。实际 /api/chat 调用该方法；没有上层完整性保护。
- **建议修复方向**：追踪终止事件和错误事件；未完成 EOF 不伪造成功。连接已发字节后终止/协议内错误，不能重放整个请求。
- **回归验证 / 验收**：正常完成恰有一个终止对象；无终止 EOF、错误 SSE、坏 JSON、读异常均不得生成成功 stop。
- **尚存不确定性**：普通 StreamAndCache 也吞 HttpIOException，但不伪造 DONE；不能把两者描述成同一种已确认成功伪造。

#### FINAL-009 模型超时在 Ollama 接口读取流体时失效

- **严重程度 / 类型 / 状态**：P2；超时 / 资源占用；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-005；Codex（独立）；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Endpoints/OllamaEndpoints.cs:246-250,290-302,377-389；Services/ChatStreamingService.cs:129-164；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：SendAsync 用模型 linked token，而后续 StreamNdjsonPassthrough/StreamOllamaAndCache 却只收到客户端 ct；ResponseHeadersRead 后 HttpClient 超时也不约束 body。
- **触发条件**：上游及时返回 headers，正文缓慢或持续保持连接；模型配置 timeout_seconds。
- **预期行为、实际影响**：应到模型期限取消正文读取；实际只在客户端取消时才停，连接和请求可超过配置期限。
- **关键证据与实际验证**：model_timeout_ignored_after_headers：配置 1 秒，延迟正文 1.3 秒，约 1.4 秒仍返回 200 和完整 late。N1 (.NET 10) 官方文档明确超时仅覆盖 HeadersRead 完成之前。客户取消仍有效，故不说所有取消失效。
- **建议修复方向**：将模型 token 贯穿正文读取与错误体读取；分别处理客户端取消与模型超时，已开始流避免二次写 JSON。
- **回归验证 / 验收**：headers 立即返回、body 挂起的两种上游格式均在 1 秒测试期限内结束；客户端取消立即释放；正常流不受影响。
- **尚存不确定性**：OpenAI 接口 StreamAndCache 已传 requestCt，不属于这个遗漏。

#### FINAL-010 Ollama 分支上游传输错误未规范处理，且会打断非流式候选切换

- **严重程度 / 类型 / 状态**：P2；错误传播 / 故障切换；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-010；KIMI-003；KIMI-012；GROK-003；Codex（独立）、Kimi Code、Grok；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Endpoints/OllamaEndpoints.cs:273-274,290-291,331-332,377-378；Endpoints/OpenAiEndpoints.cs:224-231,431-438,459-469；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：这些调用不在 OpenAI 候选 SendAsync 的 catch 中；Ollama helper 失败也不返回 HTTP 状态/正文给 lastResponse。
- **触发条件**：上游连接拒绝、传输异常或模型超时；/v1 非流式列表遇到 Ollama 候选；或它是唯一失败候选。
- **预期行为、实际影响**：应结构化 502/504，非流式允许的候选继续切换；实际可能 500 并终止列表。Ollama HTTP 4xx/5xx 只返回 false，会丢失最后错误并可能返回通用 502或更早候选错误。
- **关键证据与实际验证**：独立 ollama_transport_error：合成 HttpRequestException → /v1 500 空体。/api 四个调用完整静态链及 Kimi/Grok 对该面历史复现一致；发送之前未写流字节可改状态。已发送流后不能声称仍能返回 502。
- **建议修复方向**：在各发送边界统一区分传输故障、客户端取消、模型超时；helper 返回可保留的状态/正文；非流式继续后续候选，严格路由不跨 provider。
- **回归验证 / 验收**：各格式连接拒绝/超时/401/429/503；可切换列表第一项失败第二项成功；唯一 Ollama 错误按明确策略保留；客户端取消不重试。
- **尚存不确定性**：本轮将 KIMI-003/012 合并为共同缺失错误边界的多处修复点，仍逐个保留位置和验收；不因调用点不同就重复计数。

#### FINAL-011 原生 Ollama 直通忽略配置的执行默认值与强制覆盖

- **严重程度 / 类型 / 状态**：P2；配置执行；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-011；KIMI-013；Codex（独立）、Kimi Code；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Endpoints/OllamaEndpoints.cs:230-232,252-280；config/model-selection/ollamacloud.json:5-19,103-117；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：取得 execution 后直通只修改 model，不映射 options；不经过 ApplyExecutionDefaults。
- **触发条件**：/api/chat 路由到原生 Ollama，模型设置 temperature/max_tokens/override_client_params。
- **预期行为、实际影响**：预期与 /v1 的执行策略一致；实际 client temperature 原样保留且缺省限制不注入。可改变采样、输出长度与成本；不需证明真实服务一定拒绝才构成配置失效。
- **关键证据与实际验证**：native_defaults_ignored：内存配置 temp=1,max_tokens=4,force=true；捕获仍 temp=0.3 且无 num_predict。实际配置也有 force 条目；/v1 同配置输出 temp=1,num_predict=4。timeout 创建仍存在，但正文阶段另有 FINAL-009。
- **建议修复方向**：原生路径按同一策略合并 options，将 max_tokens/max_completion_tokens 映射 num_predict，保留未要求覆盖的客户端参数。
- **回归验证 / 验收**：真实配置和合成配置覆盖省略、已有、force true/false，两个入口最终上游选项一致。
- **尚存不确定性**：未运行真实模型请求；以生产源码及所列隔离验证为证。

#### FINAL-012 长期运行的推理缓存没有容量和过期限制

- **严重程度 / 类型 / 状态**：P2；内存 / 可靠性；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-002；KIMI-010；Codex（独立）、Kimi Code；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Services/ReasoningCacheService.cs:6,11-16,58-60；Program.cs:38；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：Singleton ConcurrentDictionary 只有新增/覆盖，没有移除；普通消息使用不断递增键。
- **触发条件**：长期使用带 reasoning_content 的模型，产生不同助手消息或工具 ID。
- **预期行为、实际影响**：缓存应受内存预算约束；实际永久保留推理全文，进程内存持续积累，长期可能造成资源不足。并发字典解决线程安全，不解决有界性。
- **关键证据与实际验证**：独立探针低负载写入后保留 1001 条；全类无 TTL/Remove/Clear。未做 OOM 测试，不声称已崩溃。
- **建议修复方向**：条目数和字节预算、TTL/淘汰一起设计；单条过大时跳过缓存，避免安全隔离修复后继续无界。
- **回归验证 / 验收**：写入超过配置预算后数量/总字节封顶；时间推进淘汰过期项；并发读写和大单条均可控。
- **尚存不确定性**：未运行真实模型请求；以生产源码及所列隔离验证为证。

#### FINAL-013 空目录或发现失败时没有重试间隔

- **严重程度 / 类型 / 状态**：P2；性能 / 故障放大；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-003；KIMI-007；KIMI-020.4；Codex（独立）、Kimi Code；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Services/ModelCatalogService.cs:21-28,76-79,142；Endpoints/OllamaEndpoints.cs:13,131,142,174；Endpoints/OpenAiEndpoints.cs:22；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：只在成功构建目录后更新 ModelsLastRefreshUtc；空结果或错误保持 MinValue/旧时间。
- **触发条件**：所有目录返回空、失败或模型不命中选择规则，连续请求 tags/chat/show/models。
- **预期行为、实际影响**：应在失败/空结果后限频；实际每个请求都重新探测所有 provider，增加延迟和上游负载。
- **关键证据与实际验证**：独立 10 次刷新得到 40 个 GET（4 个 provider）；cross 探针后续 /api/chat 再次等待四次 GET。代码确认空结果不推进时间；Kimi 另记录连续三请求三次探测。
- **建议修复方向**：记录最近尝试时间与成功时间，失败后采用明确的短退避，保留可用的旧目录。
- **回归验证 / 验收**：空目录和异常目录下顺序请求三次，仅产生一次周期的探测；间隔到期再尝试；成功时间不伪造。
- **尚存不确定性**：未运行真实模型请求；以生产源码及所列隔离验证为证。

#### FINAL-014 同一刷新周期的并发请求重复执行模型发现

- **严重程度 / 类型 / 状态**：P2；并发 / 上游负载；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-003；KIMI-007；KIMI-020.4；Codex（独立）、Kimi Code；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Services/ModelCatalogService.cs:21-41；Endpoints/OpenAiEndpoints.cs:22；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：时间戳检查和刷新不是一个受保护操作，也没有共享进行中的刷新任务。
- **触发条件**：目录到期时多个请求同时进入 RefreshAvailableModelsIfNeeded；即使最终目录成功也可同时进入。
- **预期行为、实际影响**：预期同一周期合并为一次发现；实际每个请求创建一套串行 GET，可占满连接并放大限流。与 FINAL-013 的失败退避是不同根因。
- **关键证据与实际验证**：独立 Task.WhenAll 启动 10 次，4 provider handler 收到 40 次 GET；每个 GET 延迟 20ms 确保重叠。全方法无锁/共享任务保护；不是 dictionary 原地并发写入。
- **建议修复方向**：共享在途发现任务或加并发闸，进入闸后再次检查到期；明确请求取消不能随意取消其他等待者需要的共享刷新。
- **回归验证 / 验收**：10 个并发到期请求只产生每 provider 一次 GET；完成后所有调用者看到同一版本；单个请求取消不引发刷新风暴。
- **尚存不确定性**：未运行真实模型请求；以生产源码及所列隔离验证为证。

#### FINAL-015 监听启动被所有上游目录的串行发现阻塞

- **严重程度 / 类型 / 状态**：P2；启动可用性；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-003；KIMI-006；Codex（独立）、Kimi Code；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Program.cs:50,67；Services/ModelCatalogService.cs:39-41,725；Infrastructure/ProviderHttpClientFactory.cs:44；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：在 app.Run 前 await 目录刷新，循环串行，使用 CancellationToken.None 和 5 分钟 HttpClient 超时。
- **触发条件**：任何已配置 provider 的目录接口慢或黑洞连接。
- **预期行为、实际影响**：代理即使可以按静态配置路由，也在模型发现完成前不监听；等待可累加到各 provider 超时之和。
- **关键证据与实际验证**：独立静态链已固定；交叉阶段运行实际 Release 程序，单个模拟目录延迟1200ms，约3781ms 后健康端口可用（包含进程启动开销，不能解释为纯发现耗时）。Kimi 原报告10s延迟→11s启动未在本轮重跑。
- **建议修复方向**：先启动服务并利用静态路由，后台刷新；或独立短发现超时及有界并发；区分健康/就绪语义。
- **回归验证 / 验收**：目录永不完成时，健康端口在约定短阈值内可用、已配置模型仍可路由；启动/停机可取消发现。
- **尚存不确定性**：5分钟×N是配置与调用顺序推导的上界风险，未跑满。

#### FINAL-016 精确禁用条目会被宽泛启用规则重新纳入

- **严重程度 / 类型 / 状态**：P2；模型选择 / 配置契约；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-012；KIMI-017；GROK-011；Codex（独立）、Kimi Code、Grok；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Services/ModelSelectionStore.cs:85-103；Services/ModelCatalogService.cs:49-72,99-114,545-549；config/model-selection/ollama.json:22；config/model-selection/ollamacloud.json:170-183；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：禁用条目被跳过，不作为精确否决；另一个启用的 mistral 子串继续匹配。
- **触发条件**：上游目录包含 mistral-large-3:675b；原有两个 Ollama 配置合并，一个宽泛启用、一个精确禁用。
- **预期行为、实际影响**：预期明确禁用模型不被目录接纳；实际可在 /v1/models 出现并被 @ollama 严格路由，违背配置选择意图。
- **关键证据与实际验证**：独立 matcher 返回 mistral；交叉阶段使用原始配置+模拟目录，cross_disabled_discovered listed=true，route=None（成功）。无需真实云目录即可证明本地可达条件。
- **建议修复方向**：先检查精确禁用；明确配置中的负向规则优先级，或将默认泛匹配改成显式列表。
- **回归验证 / 验收**：被禁精确 ID 即使符合启用子串也不列出、不接受为严格候选；显式启用变体继续工作。
- **尚存不确定性**：不声称生产当前目录一定包含该 ID；确认的是可配置/可达目录输入下禁用被覆盖。

#### FINAL-017 tags/show 查询丢失提供商身份，返回其他提供商的模型能力

- **严重程度 / 类型 / 状态**：P2；元数据 / 跨模块契约；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-013；Codex（独立）；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Endpoints/OllamaEndpoints.cs:79,137,162；Services/OllamaResponseBuilder.cs:10-13；Services/ModelCatalogService.cs:187-190；Services/ModelSelectionStore.cs:13-53；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：路由结果被简化成 upstream 字符串，profile 方法再跨所有 provider 选择配置，未使用请求所限定 provider。
- **触发条件**：两家提供商提供同名模型，但上下文、视觉、工具或推荐参数不同；请求限定其中一家。
- **预期行为、实际影响**：路由可正确，描述却来自另一家，客户端可能错误启用视觉/工具，或按过大上下文构造请求。
- **关键证据与实际验证**：cross_profile_provider_lost：同名 google 配置 context=2000，deepseek=1000，GET /api/show?model=same@google 返回1000；provider-aware GetExecutionConfigForModel 对照=2000。实际代码明确有按 provider 查询能力但 show 没传。
- **建议修复方向**：模型元数据方法贯穿 provider 身份；tags 用遍历中的 provider，show 用 route.Candidates 的实例；避免全局猜测覆盖已选 provider。
- **回归验证 / 验收**：两 provider 同名模型配置相反的视觉/工具/上下文值；tags 和 show 分别匹配该 provider，刷新前后均一致。
- **尚存不确定性**：未运行真实模型请求；以生产源码及所列隔离验证为证。

#### FINAL-018 结构合法但字段类型错误的请求触发 500

- **严重程度 / 类型 / 状态**：P2；输入校验 / API 错误契约；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-009；KIMI-005；GROK-005；Codex（独立）、Kimi Code、Grok；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Endpoints/OpenAiEndpoints.cs:139-149,196；Services/RequestTransformer.cs:227-239；Endpoints/OllamaEndpoints.cs:422,428,484；Endpoints/OpenAiEndpoints.cs:557-577；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：只校验根对象和 stream 布尔，没有验证 messages、role、tool_calls、images、选项数值等；转换器直接调用类型专用 API。
- **触发条件**：例如 /v1/chat/completions 发送 model=probe@google,messages=42；或消息嵌套字段类型错误。
- **预期行为、实际影响**：预期结构化400且不调用上游；实际请求进入未捕获 InvalidOperationException/格式异常，500。JSON语法错误已有400保护，不能说所有畸形JSON都500。
- **关键证据与实际验证**：invalid_messages：HTTP500、upstream=[]。根对象成功解析后 ModifyRequest.EnumerateArray 抛错；其他给定位置为同类完整静态链，未枚举所有组合动态测试。
- **建议修复方向**：在入口集中校验必要结构与字段类型/整数范围，返回具体 param 的400；不能只吞转换异常后把坏请求送上游。
- **回归验证 / 验收**：messages=null/string/object/number，数组元素非对象，role非字符串，tool_calls/images非数组，num_predict非整数/越界均400、无上游调用；合法null content不被误拒。
- **尚存不确定性**：未运行真实模型请求；以生产源码及所列隔离验证为证。

#### FINAL-019 OpenAI 图像 data URL 被原样塞入 Ollama 的 Base64 数组

- **严重程度 / 类型 / 状态**：P2；视觉协议 / 编码；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-008；Codex（独立）；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Endpoints/OpenAiEndpoints.cs:543-584,618-626（BuildOllamaChatRequest）；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：图像转换只复制 image_url.url，没有从 data URL 提取 Base64 载荷，也没有区分远程URL与原生图像编码。
- **触发条件**：OpenAI 入口向原生 Ollama 发送含 data:image/...;base64,... 的图像。
- **预期行为、实际影响**：原生 REST 的 images 需要 Base64 编码的图像数据；实际带 data: 前缀或http链接，不符合原生编码，视觉请求无法按契约解析。
- **关键证据与实际验证**：独立反射生产方法输出 images[0]=data:image/png;base64,YQ==。Ollama 官方视觉REST文档 O3 明确 Base64 要求；SDK可接受路径/URL不代表原生REST可以。没有下载用户图像或实际调用模型。
- **建议修复方向**：解析合法 data URL 并验证/提取 payload；远程 URL 明确拒绝或独立设计受限制的获取流程，不能直接透传当Base64；保留图像类型。
- **回归验证 / 验收**：真实最小PNG/JPEG的data URL转为可解码字节；多图及纯文本不丢失；坏URL/非Base64给400；不引入任意URL抓取。
- **尚存不确定性**：项目未固定原生 Ollama 服务器版本；结论依据当前官方REST协议。v0.6.4源码访问失败已记录，不假称完成该版本源码核验。

#### FINAL-020 省略 stream 时代理分支与原生 Ollama 默认行为不一致

- **严重程度 / 类型 / 状态**：P2；流式默认值 / 协议；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-019；KIMI-018；Codex（独立）、Kimi Code；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Endpoints/OllamaEndpoints.cs:211,268-280；Services/ChatStreamingService.cs:129-145；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：缺省 stream 被代理判为false，而发到原生上游的 body 没有写入false；原生默认true。
- **触发条件**：原生 Ollama 客户端省略 stream，请求经 /api/chat 路由到原生上游。
- **预期行为、实际影响**：代理走缓冲单JSON路径，上游返回NDJSON；响应标 application/json 却含多条JSON。转OpenAI上游时则显式false，失去原生默认流式。
- **关键证据与实际验证**：cross_native_stream_omitted 捕获上游 body 确实无stream；模拟原生默认返回两行，端点原样写回；代码ContentType=application/json。O1 官方默认true。不能声称单个JSON必然无法被所有NDJSON客户端读取，实际问题是分支/格式不一致。
- **建议修复方向**：统一在解析时采用原生默认true，并让实际上游body和下游Content-Type与分支一致；明确保留显式false。
- **回归验证 / 验收**：省略stream、true、false分别对两种上游测试；省略/true逐行输出且Content-Type正确，false只输出一个完成JSON。
- **尚存不确定性**：显式带stream的现有客户端不受缺省分支影响；未用真实VS客户端验证。

#### FINAL-021 Ollama 请求中的 stop、seed 和结构化输出 format 被静默丢弃

- **严重程度 / 类型 / 状态**：P2；请求参数 / 协议转换；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-017；Codex（独立）；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Endpoints/OllamaEndpoints.cs:474-517；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：options.stop/seed 被当作没有OpenAI对应字段直接跳过；format 被排除但没有映射 response_format。
- **触发条件**：/api/chat 转到支持相应选项的OpenAI兼容上游，客户端指定停止序列、随机种子或JSON格式。
- **预期行为、实际影响**：应转换支持的语义或明确告知不支持；实际悄悄删除，停止条件、可重复性和JSON输出约束失效。
- **关键证据与实际验证**：ollama_options_lost 输入 format=json,options.stop=[END],seed=42，生产转换输出仅model/stream/messages。O1/O4官方字段可对照；不同模型是否支持各字段需能力判断，不能一律假设支持。
- **建议修复方向**：按提供商/模型支持度映射stop、seed、response_format；不支持时有明确契约；避免透传所有原生options。
- **回归验证 / 验收**：支持这些字段的stub捕获等价参数；不支持模型给明确拒绝/文档化行为；options其他字段不被回归删除。
- **尚存不确定性**：未运行真实上游；结论由所列生产源码和隔离证据支持。

#### FINAL-022 没有执行默认值时，unsupported top_k 过滤直接跳过

- **严重程度 / 类型 / 状态**：P2；参数过滤；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-014；GROK-002（仅其早退子事实）；Codex（独立）、Grok；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Services/RequestTransformer.cs:24-32,46,129-132；config/model-selection/kimicode.json:8-14；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：hasAnyDefault=false 导致整段返回，过滤逻辑错误依赖是否配置默认采样值。
- **触发条件**：模型只有能力/超时配置（当前kimicode如此）或没有默认值，客户端发送top_k，provider声明不支持。
- **预期行为、实际影响**：应按Capabilities剥离top_k；实际保留，支持矩阵行为不一致，可能被严格上游拒绝。
- **关键证据与实际验证**：topk_no_defaults 输出仍含top_k:20；cross_kimicode_params也验证未默认时原样返回。已有含默认值测试通过不覆盖本路径。客户端reasoning_effort保持不变另有显式测试，未将其自动归入缺陷。
- **建议修复方向**：把必须过滤的逻辑移出默认值早退；无默认也进行必要字段处理。
- **回归验证 / 验收**：同一caps分别搭配空配置、仅timeout、含采样默认；top_k始终按支持标志处理，允许的客户端字段不变。
- **尚存不确定性**：证明了违背自身过滤规则；未验证真实kimicode是否拒绝top_k，不能据此单独确认GROK-002固定采样结论。

#### FINAL-023 模型发现失败时 /v1/models 缺少仍可路由的静态模型

- **严重程度 / 类型 / 状态**：P2；模型枚举 / 可用性；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-020；Codex（独立）；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Endpoints/OpenAiEndpoints.cs:14-90；Services/ModelCatalogService.cs:436-449,491-512；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：列表实现只遍历AvailableModels和ModelToProvider；注释承诺合入static config，但注入的ModelSelectionStore从未用于枚举。
- **触发条件**：配置启用并提供API key，但/models目录失败/空；客户端依赖/v1/models选模型。
- **预期行为、实际影响**：ResolveRoute能以配置精确项服务模型，列表却不显示，客户端无法选到本可用模型。需要区分“可路由”与“已探测可用”，不必声称上游实际健康。
- **关键证据与实际验证**：cross_empty_discovery_list：配置same@google的候选数=1，/v1/models不含same；静态配置fallback和列表缺失同时成立。
- **建议修复方向**：列表按明确契约合入已激活provider的enabled精确模型，或者显式提供可配置列表来源；不得凭无效/禁用项制造模型。
- **回归验证 / 验收**：空目录时仍列出可按配置路由的精确ID及限定别名；停用provider/禁用模型不出现；恢复发现后不重复。
- **尚存不确定性**：当前docs/CONFIGURATION部分文字只描述发现列表，文档本身存在分歧；修复应同时明确枚举契约。

#### FINAL-024 .env 覆盖进程环境，违背部署文档的配置优先级

- **严重程度 / 类型 / 状态**：P2；运维配置；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：KIMI-008；GROK-009（Codex交叉复核）；Kimi Code、Grok；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Program.cs:4-24；docs/DEPLOYMENT.md:318-323；docs/CONFIGURATION.md:22-26；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：加载.env无条件SetEnvironmentVariable，不保留外部已设置值；文档同时有两套优先级说法。
- **触发条件**：.env与进程同时配置端口、代理密钥或上游密钥。
- **预期行为、实际影响**：部署文档声明系统环境优先，实际.env优先；运维覆盖或密钥轮换可能静默失效。
- **关键证据与实际验证**：startup-probe启动真实Release程序集，进程端口与临时.env端口不同；结果file_port_status=200,system_port_listening=false。Testing也不豁免。Grok声称CONFIGURATION把系统环境放前不准确：该页实际.env在前，但DEPLOYMENT仍明确系统优先，因此主缺陷保留。
- **建议修复方向**：选定并统一优先级；若遵循部署契约，只对未设置的环境值加载.env；分别定义空字符串含义。
- **回归验证 / 验收**：进程与.env相同/冲突/进程空值/无.env四组，端口、代理key和provider配置遵循同一优先级；测试只用合成值。
- **尚存不确定性**：不把单引号/行尾注释支持当既定完整dotenv规范；这些归配置健壮性建议。

#### FINAL-025 Google 多段 API base URL 不能被规范化

- **严重程度 / 类型 / 状态**：P2；URL配置 / 接口路径；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：KIMI-009；GROK-008（Codex交叉复核）；Kimi Code、Grok；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Services/ProviderRegistry.cs:293-307；Services/ProviderCapabilitiesRegistry.cs:68-70；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：NormalizeBaseUrl只检查ChatPath首段v1beta，识别不了完整v1beta/openai后缀。
- **触发条件**：用户把官方OpenAI兼容base_url .../v1beta/openai/写进PROVIDER_GOOGLE_BASE_URL。
- **预期行为、实际影响**：应形成一次/v1beta/openai路径；实际两次重复，发现和聊天都落到错误URL。默认不设置base URL不受影响。
- **关键证据与实际验证**：cross_google_base实际调用生产NormalizeBaseUrl后System.Uri解析得到http://google.test/v1beta/openai/v1beta/openai/chat/completions；G1官方示例使用多段base。
- **建议修复方向**：按完整API目录前缀规范化或明确支持的base形状；不破坏自定义反向代理的合法路径。
- **回归验证 / 验收**：google根URL与完整API目录、kimicode含v1/不含v1、有无尾斜杠得到相同有效端点；自定义路径前缀保留。
- **尚存不确定性**：未运行真实上游；结论由所列生产源码和隔离证据支持。

#### FINAL-026 端点测试启动会重新读取真实 .env，测试夹具缺乏隔离

- **严重程度 / 类型 / 状态**：P2；测试安全 / 有效性；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-015；Grok覆盖记录（未单列编号）；Codex（独立）；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：tests/ProxyTests/EndpointTests.cs:74-96；Program.cs:4-24；tests/ProxyTests/EndpointTests.cs:15-18；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：ProxyFixture设置合成环境后用WebApplicationFactory启动真实Program；Program又读AppContext/CWD的.env，Testing环境没有隔离；夹具也未清所有已支持provider。
- **触发条件**：开发者在含有效.env的项目根目录运行现有端点测试，或继承未清理的provider key。
- **预期行为、实际影响**：测试应只到stub；实际可能启动真实提供商目录查询、带真实key并使结果依赖外界。该执行边界不能仅靠约定“无外网调用”保证。
- **关键证据与实际验证**：本轮运行采用无.env且清空所有相关环境的临时副本，469通过。另合成startup-probe证明Testing仍读.env覆盖进程配置；ProxyFixture具体调用链闭合，未用真实密钥验证危险行为。
- **建议修复方向**：夹具显式替换ProviderHttpClientFactory/配置加载入口，禁用.env自动加载并隔离所有provider；避免只维护容易遗漏的key清单。
- **回归验证 / 验收**：在测试专用目录放合成.env且预设所有provider环境，断言全部请求到fake handler，真实socket目的地零个；退出恢复环境。
- **尚存不确定性**：本轮没有从原仓库根直接启动测试，也没有操作真实.env；不声称已经产生真实计费。

#### FINAL-027 capabilities 恒包含 tools，与 supports_tools=false 矛盾

- **严重程度 / 类型 / 状态**：P3；元数据契约；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-013；KIMI-023.1；Codex（独立）、Kimi Code；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Services/ModelCatalogService.cs:192,267-283；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：capabilities仅按vision生成，忽略已计算的tools布尔值。
- **触发条件**：当前或用户配置supports_tools=false，通过tags/show读取能力。
- **预期行为、实际影响**：预期tools能力与显式布尔一致；实际同时返回false和tools能力，客户端可能展示不可用工具能力。
- **关键证据与实际验证**：tools_profile：运行时配置SupportsTools=false，生产GetModelProfile返回[completion,tools]。当前大多启用模型配置true，因此限制严重程度P3。
- **建议修复方向**：按tools、vision独立构造能力列表。
- **回归验证 / 验收**：tools/vision四种组合，布尔字段与capabilities完全一致；被排除类别不能宣称工具能力。
- **尚存不确定性**：未运行真实上游；结论由所列生产源码和隔离证据支持。

#### FINAL-028 同一合成 OpenAI SSE completion 使用多个 ID

- **严重程度 / 类型 / 状态**：P3；SSE响应契约；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-007；KIMI-023.3；Codex（独立）、Kimi Code；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：Endpoints/OpenAiEndpoints.cs:491,508；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：firstChunk和finishChunk分别调用Guid.NewGuid，未复用completion身份。
- **触发条件**：OpenAI流式入口选择Ollama上游，进入合成SSE分支。
- **预期行为、实际影响**：同一completion所有chunk应同id；实际内容和结束chunk不同id，依赖id归并的客户端可能无法合并。
- **关键证据与实际验证**：ollama_stream_tool_lost探针响应中两个不同chatcmpl ID；O4官方文档明确每片同一ID。即使没有工具调用也成立，与FINAL-006不是同根因。
- **建议修复方向**：生成一次id/created并在整个完成流复用。
- **回归验证 / 验收**：普通文本、空文本、工具流逐片断言id相同、created一致、只有一个逻辑完成。
- **尚存不确定性**：未运行具体客户端归并器，协议违背本身确定，故P3。

#### FINAL-029 基准脚本将全部失败的提供商评为最快

- **严重程度 / 类型 / 状态**：P3；分析脚本正确性；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-016；Codex（独立）；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：scripts/benchmark-duplicate-models.ps1:106-111,132-139；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：失败平均延迟设-1，最终不筛选成功便按延迟升序取第一项。
- **触发条件**：同组至少一项全失败、另一项成功。
- **预期行为、实际影响**：应从成功结果里选最快；实际失败项-1排第一，产生错误FASTEST推荐，误导配置决策。
- **关键证据与实际验证**：完整静态链：失败赋值→AvgLatencyMs→Sort-Object→Select-Object -First 1，无中间成功过滤；脚本语法解析通过。没有调用真实基准服务。
- **建议修复方向**：先筛SuccessCount>0且有效延迟，全部失败时明确无可用结果；报告保留失败记录但不参与速度排名。
- **回归验证 / 验收**：合成结果[-1,100,200]选100；全失败无winner；部分成功按既定统计指标选择。
- **尚存不确定性**：未运行真实上游；结论由所列生产源码和隔离证据支持。

#### FINAL-030 综合基准用显示名称匹配限定模型 ID，导致全部跳过

- **严重程度 / 类型 / 状态**：P3；分析脚本 / 模型身份；已确认。严重度理由见实际影响。
- **来源 / 独立发现者或原始记录者 / 复核者**：CODEX-016；Codex（独立）；Codex最终复核（前轮互审详见表）。
- **最终定位及适用基线**：scripts/comprehensive-benchmark.py:54-66；Endpoints/OllamaEndpoints.cs:70,76,82-86；`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **根因**：available取tags.models[].name（PROVIDER - model:latest），却与预设model@provider比较。
- **触发条件**：脚本连接当前版本代理的正常tags响应。
- **预期行为、实际影响**：应测量存在的模型；实际所有预设限定ID与显示名称不等，测试被SKIP，报告没有有效比较。
- **关键证据与实际验证**：生产tags构造和Python比较操作闭合；脚本预设ID包含@，name格式固定大写提供商加' - '，不能等值。Python AST三文件解析成功，不构成行为正确的证据。
- **建议修复方向**：使用tags.model或解析aliases，按稳定模型身份匹配并保留上游/提供商诊断；不要比较显示名。
- **回归验证 / 验收**：合成当前tags至少两组模型，存在项被实际调用、缺失项才skip；模型显示名变化不影响测试选择。
- **尚存不确定性**：未运行真实上游；结论由所列生产源码和隔离证据支持。

### B. 待验证风险：不作为确定缺陷

#### B-001 Google models/ 前缀与实际兼容目录/聊天接口的关系

- **来源 / 定位**：KIMI-015、GROK-007；config/model-selection/google.json:5,25,45,63,81；Services/ModelSelectionStore.cs:98；Services/ModelCatalogService.cs:49-53；基线`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **已有证据**：cross_google_bare_match=NONE；带前缀配置可被严格路由并原样转发。G1官方示例使用裸模型ID，但没有给出本次账户目录原始响应，也没有声明models/别名一定非法。
- **缺失证据 / 分歧**：缺指定Google兼容端点的目录ID样本、以及同模型带前缀/不带前缀请求的接受规则。stub只证明设定输入下行为，不能证明真实服务输入。
- **下一步最小验证**：由维护者提供脱敏GET /v1beta/openai/models样本及官方别名规则，先离线重放；只有必要且另获真实调用授权后做最小两请求对照。
- **条件影响及分类**：若成立，目录漏模型/聊天错误，预计P2；不按必然全量不可用P1进入修复计划。

#### B-002 Kimi Coding 四个别名是否都必须固定 temperature/top_p

- **来源 / 定位**：GROK-002；config/model-selection/kimicode.json:5-57；Services/RequestTransformer.cs:24-32；基线`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **已有证据**：cross_kimicode_params确认0.2/0.1透传。K1的固定采样表针对开放平台kimi-k3/kimi-k2.7-code等；K2说明Coding别名映射且kimi-for-coding已更新为K2.8 Preview。没有四别名在api.kimi.com/coding上的明确同一参数约束。
- **缺失证据 / 分歧**：Coding端点适用的正式参数表/版本对应关系，或脱敏400原文和请求；“Copilot一般带某参数”也未由本轮真实客户端抓包证实。
- **下一步最小验证**：先核对Coding官方参数契约及维护者已有脱敏失败样本；针对实际别名和思考模式验证，不应直接给四模型硬编码同一采样值。
- **条件影响及分类**：若成立是参数兼容缺陷，可能严重影响特定客户端；仍不把它宣称为已定位的套餐异常根因。

#### B-003 目录映射分字段发布及并发刷新存在版本不一致窗口

- **来源 / 定位**：Codex独立冻结记录（未编号风险）；Services/ProviderRegistry.cs:41-47；Services/ModelCatalogService.cs:124-142,681-701；基线`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **已有证据**：AvailableModels、provider map、upstream map、candidate map依次赋值；读端跨多次字段读取，EnsureDefaultModelPresent也发布快照。字典本身发布后通常不原地改写，不能宣称必然集合枚举异常。
- **缺失证据 / 分歧**：缺可重复的跨版本交错实例导致错误路由/别名失败的实际结果；旧刷新覆盖新刷新也未施加可控屏障验证。
- **下一步最小验证**：在临时测试版本用屏障控制两次不同目录快照的发布/读取交错，断言同一解析来自同一版本；若复现，再改为单一不可变snapshot整体发布。
- **条件影响及分类**：潜在错误路由/短暂目录不一致，待定P2；FINAL-014只确认并发重复探测，不代替本条正确性证明。

#### B-004 Docker nightly preview 标签的可构建性与运行兼容性

- **来源 / 定位**：KIMI-022.1；Grok复核；Dockerfile:1,8；基线`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **已有证据**：两个镜像仍用nightly/*:10.0-preview；本机未发现docker命令。SDK10.0.301下本机Release构建成功不能证明镜像可用，也不能反向证明preview已失效。
- **缺失证据 / 分歧**：实际镜像manifest/tag可用性、镜像内SDK/运行时版本及构建启动结果。
- **下一步最小验证**：在隔离CI执行docker build和无密钥健康启动；记录镜像digest，再决定固定稳定标签/摘要。
- **条件影响及分类**：可能阻断容器构建，暂不列已确认缺陷；主机运行验证不覆盖容器。

#### B-005 占位 digest/size 对具体客户端缓存判断的影响

- **来源 / 定位**：KIMI-023.2；Grok复核；Endpoints/OllamaEndpoints.cs:90-91；Services/OllamaResponseBuilder.cs:18-20；基线`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **已有证据**：所有模型固定相同size与全零digest是事实；模型是API虚拟模型，没有本地权重文件可直接计算真实digest。
- **缺失证据 / 分歧**：缺依赖这些字段进行归并、校验或缓存失效的客户端及故障样本；不能仅凭假值推导已发生客户端错误。
- **下一步最小验证**：用目标VS/Ollama客户端观察两模型切换和metadata更新时的缓存键；若依赖digest，定义API模型稳定身份摘要而不是权重摘要。
- **条件影响及分类**：潜在客户端缓存/展示问题，预计P3；不要求无依据地模拟真实文件大小。

### C. 工程改进建议：与功能缺陷分开

#### C-001 认证与部署边界加固

- **来源 / 定位**：KIMI-014、KIMI-019；Codex独立基础设施检查；Program.cs:30-32；Infrastructure/ProxyAuthenticationMiddleware.cs:5-14；Endpoints/OpenAiEndpoints.cs:390-413；docker-compose.yml:6-7；基线`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **建议、理由与验收**：默认无认证且全接口监听已明确；按实际共享网络用途选择绑定范围或强制设置key。日志改用不可逆短标识代替key片段，比较可采用FixedTimeEquals。health应文档化带认证，不能为便利自动开放。验收：有key时所有要求保护的路径拒绝无效凭证，日志无可识别原key片段；缺key模式与部署说明一致。没有声称已证明远程计时攻击或完整密钥泄漏。

#### C-002 配置来源、校验与错误可观察性

- **来源 / 定位**：KIMI-020.1-.3、KIMI-008附项；Codex独立配置检查；Services/ModelSelectionStore.cs:236-252,305-309,340-368；Program.cs:12-24；基线`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **建议、理由与验收**：双目录先到先得可能遮蔽CWD编辑；非法JSON被整文件忽略，非法enabled类型按true处理。定义一个权威配置来源/明确优先级，严格校验并报文件名与字段，勿输出密钥。若支持dotenv注释/引号，写清语法。验收：冲突来源、坏JSON、非法布尔/数值都有可定位诊断。并发刷新和失败退避已在FINAL-013/014，不再当建议重复计缺陷。

#### C-003 修订现存文档中的接口与运行说明

- **来源 / 定位**：KIMI-021、GROK-009引用纠正；Codex独立文档检查；docs/API.md:51-76,302-313,334-355；README.md:694-704；docs/ARCHITECTURE.md:211-212；docs/CONFIGURATION.md:22-26；docs/TESTING.md的CI/测试说明；基线`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **建议、理由与验收**：核对health实际snake_case字段、错误体/错误码、仅/api存在的诊断头、当前共享ResolveRoute、appsettings实际不参与环境变量直读，以及测试数量/主分支CI说明。删除历史CLAUDE不是新缺陷；不能为迎合失效文档删除当前有效tools参数。验收：所有示例离线端点测试可重放，配置优先级只保留一套约定（FINAL-024）。

#### C-004 关键测试应调用生产实现，避免复制算法自证

- **来源 / 定位**：CODEX独立测试覆盖记录；tests/ProxyTests/ImageSupportTests.cs:27-195；tests/ProxyTests/ModelSelectionTests.cs:27-109；tests/ProxyTests/FakeProviders/FakeProviderHandler.cs:51-81；基线`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **建议、理由与验收**：图像转换和部分配置加载测试复制实现，可能同步复制错误；fake目录handler也不严格约束全部路径。将重要用例改为真实内部方法/真实端点，stub只接受约定路径。验收：故意在临时生产副本改错转换规则或路径，相应测试必须失败。缺测试本身不计运行时缺陷。

#### C-005 明确 HttpClient/handler 所有权，补充低噪声诊断

- **来源 / 定位**：CODEX独立资源检查；构建告警；Infrastructure/ProviderHttpClientFactory.cs:6-15,42；Services/ProviderRegistry.cs:246-247；Services/ModelSelectionStore.cs:340；Services/ChatStreamingService.cs:95-98,119-125；基线`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **建议、理由与验收**：工厂共享handler和Provider客户端生命周期为进程级，目前没有显式Dispose所有者；补充容器停止时释放及必要异常分类日志。不要误报每请求创建SocketsHttpHandler导致socket泄漏：当前确实共享handler。处理CS8600与测试过时API告警可单独安排。验收：重复创建/关闭测试宿主能释放资源，日志不打印请求正文与凭证。

#### C-006 后台基准的保留期、成本和探测有效性

- **来源 / 定位**：KIMI-022.3；Grok复核；Codex独立后台服务检查；Services/ProviderBenchmarkService.cs:27-41,82-103,107-150,171-175；基线`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **建议、理由与验收**：功能默认禁用；启用后按目录别名逐一调用且每周期写报告，缺保留策略；只按HTTP成功不检查内容正确性。文档说明调用成本、alias重复测量和报告保留，按目标定义采样/清理。验收：受控模拟周期生成有限保留报告，取消及时，不重复把同一provider/model的alias当不同模型。未做长跑磁盘耗尽测试，不列必现事故。

#### C-007 流式能力边界与可选增强明确化

- **来源 / 定位**：KIMI-011、KIMI-023.3的usage子项；Codex独立流式检查；Endpoints/OpenAiEndpoints.cs:285-306,454-530,694-719；docs/API.md:329；Services/ChatStreamingService.cs:34-39,119-125；基线`11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`。
- **建议、理由与验收**：当前文档明确流式不failover，而Ollama转OpenAI采用先缓冲非流式再合成SSE。可设计首字节前候选切换、原生逐片转换、usage映射和中断诊断，但需说明重试/重复计费与已发字节禁止重放。固定错误的SSE ID和工具丢失已属FINAL-028/006，不用“增强”掩盖。验收：若引入切换，仅无provider限定且未开始响应时允许；开始后不重复输出，usage支持范围文档一致。

### D. 已排除或被替代的结论

| 编号 | 原结论/来源 | 排除理由或合并去向 |
|---|---|---|
| D-001 | KIMI-021.3/.4、KIMI-008历史CLAUDE引用、Grok对应复核 | CLAUDE.md在65a6b72删除，不作为当前文件缺陷；现存DEPLOYMENT优先级冲突保留FINAL-024，其他真实文档冲突C-003。 |
| D-002 | KIMI-022.2、Grok“CI只main与当前必须develop冲突” | 当前无适用的必须develop/不得main规则；.NET与Release workflow有测试，不能要求遵循已删除文档。CI可拓展属于选择，不是现存确定缺陷。 |
| D-003 | KIMI-011及Grok确认中“能够首字节前failover所以现行为必是bug” | 现API文档明确流式不切换；可行性不能建立必须提供的契约。作为C-007，不计已确认功能缺陷。 |
| D-004 | KIMI-014的无条件安全缺陷定性 | 全接口/无key配置事实保留C-001；没有证明越过既有认证或生产网络已暴露。不开匿名health、不自动修改监听行为。 |
| D-005 | KIMI-019.3、Grok确认“health需要认证是缺陷” | 全局认证覆盖health与现配置说明一致；部署healthcheck示例需同步认证属于C-003/C-001。 |
| D-006 | KIMI-020.4附加断言“fire-and-forget永远旧数据/不能完成” | 生产方法可以完成，若同步/快速完成也可能返回新数据；本轮保留具体已证实的重复探测与退避缺失FINAL-013/014，不采用笼统推断。 |
| D-007 | KIMI-021.4及前轮要求移除所有tools/function_call或客户端reasoning_effort的暗示 | 失效文档不是当前协议依据；ParameterValidationTests明确保留客户端reasoning_effort。top_k早退是真正可定位缺陷，单列FINAL-022。 |
| D-008 | GROK-002、GROK-007确定P1及必现上游拒绝的表述 | 分别转B-002/B-001，保留本地确证事实与缺失证据，不删除编号、不直接改四个别名或Google所有ID。 |
| D-009 | KIMI-023.2“假digest一定导致客户端失败”、KIMI-022.1“preview标签已失效” | 分别转B-005/B-004；未验证具体客户端或镜像，不把可能性当结果。 |
| D-010 | 所有重复编号 | GROK-001=KIMI-001→FINAL-003；GROK-003=KIMI-003及KIMI-012→FINAL-010；GROK-004=KIMI-002→FINAL-001；GROK-005=KIMI-005→FINAL-018；GROK-006=KIMI-004→FINAL-007；GROK-008=KIMI-009→FINAL-025；GROK-009=KIMI-008→FINAL-024；GROK-010=KIMI-023.4→FINAL-002；GROK-011=KIMI-017→FINAL-016；GROK-012=KIMI-016→FINAL-004。GROK-007=KIMI-015→B-001；CODEX映射见独立发现各条，不丢弃独立来源。 |

### 完成核对与剩余范围

全部23个KIMI编号、12个GROK编号及Grok对Kimi的23行复核均已给出去向；KIMI-019至023的打包子项也逐项保留。20个CODEX编号均映射最终条目，复合根因被显式拆分。A类每项有生产源码完整链或本轮运行证据，B类没有借前轮多数意见升级。

现有469项测试与Release分析构建完成；额外探针验证了缓存污染、模型路径、字段校验、流体期限、终止完整性、工具转换、配置默认、profile、目录过滤/刷新/列表、base规范化、双实例路由以及实际.env启动优先级。源码和测试仅在临时副本编译，没有修改原项目业务文件。追加前后校验73742字节原文前缀完全一致；最终原工作区只新增issue.md修改。

未覆盖：真实17家上游目录/参数矩阵、真实VS/Copilot/Ollama客户端、原生Ollama指定历史版本源码（获取失败）、Docker与跨平台发布产物、在线依赖漏洞库、长时间内存/磁盘耗尽与高并发压力、可控目录快照交错、全部历史材料及中英文文档逐句一致性。没有以安全复现范围代替这些验证，也不宣称项目已无其他问题。

---

## 修复结论追加（2026-09-23，保留上述原始审计）

本节是实际修改后的结论；上文中的“未修改业务代码”“469 项测试”等是审计阶段的历史记录，不代表本节修复状态。修复基于 main 的 `11f013d4525e9fc5ed7b959d3d17ab58a7e3668e`，恢复中断时的 stash 后继续；未 commit、push、deploy，两个 stash 均保留。追加前原始审计全文 SHA256 为 `3E288B768D8228A9F28D9FBB102CD715E8F87F0DF1926DF065C22ED1E0582BEE`，追加后逐字节核对其前缀不变。

### 处理原则与证据

按上述最终归并编号处理，同根因不重复改造。隐私与协议正确性优先，其次目录/启动/配置，再处理工程建议。回归调用生产转换函数或真实端点；目录并发测试实际发现跨版本结果（预期 v49、实际 v50），修复后 20,000 次解析校验通过。协议首批四项回归在修复前失败；后续补充工具关联、空值、异常流等边界验证。未对每个条目声称独立运行过旧版本失败测试：部分由原始审计的可执行探针复现，本轮增加修复后回归。最终协议专项 71/71、整个测试工程 573/573 通过。

证据文件：`tests/ProxyTests/ProtocolRepairTests.cs`、`ProtocolEndpointRegressionTests.cs`（协议和真实端点），`CatalogAuditRegressionTests.cs`（目录、配置、并发），`RuntimeRepairTests.cs`（缓存、环境、日志），`ProviderBenchmarkServiceTests.cs`、`ProviderHttpClientFactoryTests.cs`、`RequestTransformerTests.cs`（工程边界），以及 `tests/runtime-log-probe.py`（真实 Kestrel）。可重放命令和日志语义见 `docs/testing/2026-09-17-audit-repair.md`。

### A. 已确认缺陷：30 项全部已修复

| 编号 | 根因修复及兼容性说明 | 验证结果 |
|---|---|---|
| FINAL-001 | `RequestTransformer`、端点、流服务停止无会话身份的全局推理缓存读写。客户端显式 reasoning_content 保留；不再自动补回历史推理，这是防止串会话的必要行为变化。 | 两个入口、普通历史/重复工具 ID、流式/非流式响应均验证不串用、不创建共享缓存。 |
| FINAL-002 | `ProviderRegistry` 区分云端 ollama 与双实例时本地 ollama-local；仅本地时仍叫 ollama，配置规则兼容复用。 | 双实例同名模型各自限定路由正确，裸模型保留两候选。 |
| FINAL-003 | 所有聊天分支使用 Provider 的 ChatPath，删除硬编码兼容路径。 | Google/Zhipu 等端点路径回归及流式/非流式验证通过。 |
| FINAL-004 | Ollama→OpenAI 转换保留 tool 角色及必要关联，不能降格为 user。 | 生产转换与端点捕获上游请求验证工具历史。 |
| FINAL-005 | `ChatProtocol` 双向转换工具 arguments、ID；流碎片按 index 组装。显式/隐式工具结果均消费待匹配调用，null ID 按缺失推断；孤立/未知 ID 提前 400。 | 多工具交错碎片、混合 ID、未知 ID 零上游调用；溢出/小数/负数 index 规范为 502，非 500。 |
| FINAL-006 | Ollama 工具结果转换为 OpenAI tool_calls 和对应 finish_reason，合成 SSE 不丢工具。 | 无文本/有文本、单/多工具的合成 SSE 回归通过。 |
| FINAL-007 | 合法缺省或 null content 保持可用；坏 choices/响应结构按上游协议错误处理。 | 合法可选 content 返回成功，坏响应 502。 |
| FINAL-008 | 流转换要求有效完成标记，截断、错误帧、坏 JSON 不再伪造成功 done；开始响应后中止。 | 截断及错误 SSE、交错工具流生产服务回归通过。 |
| FINAL-009 | 链接模型超时与客户端取消令牌，覆盖 SendAsync 后的正文读取。 | 上游先给 headers 后挂起 body 的模拟响应按模型期限取消。 |
| FINAL-010 | 传输异常分类为 502/超时，非流式候选继续尝试；原生 HTTP 错误保留状态/正文，已开始流不重放。 | 传输失败、原生 4xx/5xx、候选切换回归；实际 503 和第二候选成功日志验证。 |
| FINAL-011 | 原生 Ollama options 应用执行默认值和强制覆盖规则，保留无关参数。 | force 开/关及客户端值保留的实际端点测试通过。 |
| FINAL-012 | 内部缓存增加 256 条、4 MiB 字符存储估算、单项 1 MiB、15 分钟 TTL 与同步边界；生产已经不写该缓存。 | 数量、大小、过期、并发回归通过；不是对进程总内存的 4 MiB 承诺。 |
| FINAL-013 | 目录刷新分离最近尝试与成功时间，失败/空结果同样受 5 分钟节流，取消不记成功。 | 空目录、坏 JSON、错误类型、取消后重试测试通过。 |
| FINAL-014 | 刷新使用 SemaphoreSlim 串行入口并合并重叠请求，发布采用短锁。 | 13 个并发刷新合并一次探测；取消路径释放锁，B-003 一并验证。 |
| FINAL-015 | 目录刷新改为 BackgroundService，启动与请求不等待发现；每分钟检查刷新期限，每 Provider 发现期限 10 秒。 | 真实监听 631 ms；目录各延迟 3 秒时两个入口均能在 2 秒内完成静态模型请求。 |
| FINAL-016 | 精确 disabled 项优先否决宽泛 enabled 匹配。 | model-private 不再由 model 通配规则重新启用，普通匹配仍有效。 |
| FINAL-017 | profile/show 查询携带明确 Provider，避免同名/子串规则串用。 | 不同 Provider 配置相同/相似模型，返回所属配置上下文和能力。 |
| FINAL-018 | `ChatProtocol.ValidateRequest` 在业务读取前校验实际使用的嵌套字段类型和数字范围，允许合法 nullable 参数。 | 参数类型错误提前 400、零上游调用；合法可选 null 保留。 |
| FINAL-019 | OpenAI→Ollama 图片只提取合法 data:image/...;base64 载荷；不支持的远程图片明确 400，不隐式下载。 | 载荷提取、坏 Base64、远程 URL 拒绝且零访问测试通过。 |
| FINAL-020 | /api/chat 省略 stream 按 Ollama 默认 true，分支、上游 body 和响应类型一致。 | 两类上游分别验证省略/true/false。 |
| FINAL-021 | options.stop/seed 与 format 映射到对应 OpenAI 字段，JSON/schema 输出不再静默丢失。 | 捕获等价 stop/seed/response_format；具体厂商支持度仍由上游契约决定，未声称全模型支持。 |
| FINAL-022 | top_k 能力过滤移出默认值早退；保留客户端 reasoning_effort 契约。 | 无默认、仅 timeout、有默认配置均按能力过滤。 |
| FINAL-023 | /v1/models、tags 合入已激活 Provider 的 enabled 静态精确项及限定别名，空发现也可列举。 | 空目录、停用 Provider、禁用模型、静态集合回归；列表表示可路由配置，不保证上游健康。 |
| FINAL-024 | `DotEnvLoader` 仅补进程中不存在的变量，显式空值仍优先；Testing 不读 .env。 | 缺失/已有/空值/无文件/文件来源测试；真实启动冲突端口采用进程值。 |
| FINAL-025 | 按最长完整 API 目录前缀规范化 Google base URL，保留自定义代理前缀。 | 根目录、v1beta、v1beta/openai、custom 地址组合回归通过。 |
| FINAL-026 | 测试夹具清理并恢复 Provider/代理/基准环境，Testing 禁止 dotenv；替换 HTTP 工厂，限定精确 stub 主机/端口并禁止重定向逃逸。 | 全套测试在隔离夹具通过；实际运行探针独立临时目录、合成配置和 loopback 上游。 |
| FINAL-027 | tools 与 vision 独立生成 capabilities，并与布尔值一致。 | 四种 tools/vision 组合通过。 |
| FINAL-028 | 合成 SSE 的 id、created 每次完成只生成一次。 | 文本、空文本、工具流逐片身份及结束原因一致。 |
| FINAL-029 | 速度排名仅选择有成功记录且有效延迟的结果；全失败无 winner。 | PowerShell 调用生产排名代码，混合失败/全失败/部分成功三组通过。 |
| FINAL-030 | 综合基准用 tags.model 稳定身份匹配，不再拿显示名比较限定 ID。 | Python mock 执行实际脚本，两组模型各被调用三次，显示名不影响选择。 |

### B. 风险核验结果

| 编号 | 最终状态、依据与剩余事项 |
|---|---|
| B-001 | **待外部证据，未认定为缺陷。** 核对 Google 官方 OpenAI 兼容示例只证明裸 ID 示例存在，不能证明 models/ 别名非法；未获得本账户脱敏目录和接受规则。保留现有配置，下一步以真实目录样本离线重放；未擅自计费调用。 |
| B-002 | **待外部证据，未硬编码采样值。** Coding 模型文档和开放平台参数说明不足以证明当前四别名同一约束；没有可靠的对应失败样本。FINAL-022 的自身过滤违约已独立修复。下一步需实际别名/版本的正式参数表或脱敏 400。 |
| B-003 | **已复现并修复。** 并发发布与 ResolveRoute 得到不同版本 upstream 的测试实际失败；ProviderRegistry.CatalogSync 保护整个路由读取和发布，锁内不做网络 I/O。修复后并发 20,000 次解析一致；不宣称完成长期压力测试。 |
| B-004 | **客观受阻，未修改 Dockerfile。** 本机没有 Docker，镜像 manifest 网络查询未获得可用结果；主机 .NET 10 编译不能证明 nightly preview 镜像可拉取/运行。需有 Docker 的 CI 构建、无密钥启动并记录 digest，再决定稳定标签。 |
| B-005 | **未证实客户端影响，保留占位元数据。** 没有目标客户端依赖 digest/size 的抓包或故障证据；API 虚拟模型没有可据以计算的权重文件。需实际 VS/Ollama 客户端切换/缓存验证后再定义摘要语义。 |

参考契约仍以上述原审计引用为准；本轮也核对了 https://ai.google.dev/gemini-api/docs/openai 与 https://www.kimi.com/code/docs/en/kimi-code/models.html ，不把文档示例推断成未声明的服务限制。

### C. 工程建议取舍

| 编号 | 最终处理与验证 |
|---|---|
| C-001 | **采纳低风险加固。** 认证比较采用 SHA256 后 FixedTimeEquals；删除请求日志中的 key 片段，保留现有全局认证/health 保护范围和无 key 模式，文档同步。认证原有测试通过；未无依据改变默认监听地址。 |
| C-002 | **采纳。** 模型 JSON 配置工作目录优先、文件排序确定性合并；坏 JSON/非法 bool/数值明确拒绝并记录文件名与安全原因，不回显配置值。dotenv 明确只支持简单键值、外层单双引号与整行注释，不实现插值/行尾注释。冲突来源与非法配置回归通过。 |
| C-003 | **采纳。** README、API、ARCHITECTURE、CONFIGURATION、DEPLOYMENT、TESTING 更新实际 health/头/认证、共享路由、配置优先级、缓存停止、后台发现、CI 和流式限制。按现有端点和测试核对，未重写无关文档。 |
| C-004 | **重点采纳。** 新增关键回归直接调用生产实现/端点，使用严格本地上游；首次四项协议及目录版本测试先失败后修复。未为形式一致性重写全部旧测试，降低无关改动风险。 |
| C-005 | **采纳所有权、CS8600 与诊断修复。** 工厂由 DI 释放所有客户端和共享 handler，自定义每次调用 handler 仍属调用者；端点请求显式 using。重复 Dispose/已释放后创建及 handler 所有权测试通过。既有测试工程 8 处 ASPDEPR004 不在本次迁移，不禁用警告。 |
| C-006 | **采纳。** 基准按 Provider/upstream 去重别名、传播取消、释放请求、报告最多保留 100 份；文档声明可能计费且 HTTP 成功不等于内容质量。模拟重复模型/取消/报告保留测试通过；未做真实付费质量评测和长期磁盘压力。 |
| C-007 | **保留现有边界。** 流式仍只尝试首候选，响应开始后失败中止；Ollama→OpenAI 合成 SSE 仍先缓冲完整响应。不引入逐 token 重写、流重试或高成本 usage/TTFT 解析。工具/身份的确定错误已由 FINAL-006/028 修复，错误流不再假成功；文档明确限制。 |

### D. 原排除项逐项复核

| 编号 | 本轮最终结论 |
|---|---|
| D-001 | 维持排除历史 CLAUDE 引用；有效文档问题已由 FINAL-024/C-003 处理。 |
| D-002 | 维持排除必须 develop 的过时假设；未修改 CI 分支政策。 |
| D-003 | 维持流式不切换契约；实现与测试确认首候选策略，见 C-007。 |
| D-004 | 不把既有开放部署模式认定为认证绕过；采用 C-001 有收益的比较和日志修复。 |
| D-005 | health 继续受认证保护；文档示例同步，未增加匿名例外。 |
| D-006 | 不采纳“永远旧数据”的绝对断言；具体并发/退避/阻塞由 FINAL-013/014/015 修复。 |
| D-007 | 不删除有效 tools 或调用者 reasoning_effort；只修复 FINAL-022 能力过滤。 |
| D-008 | 维持 B-001/B-002 待证状态，不提升为确定 P1，不批量猜改配置。 |
| D-009 | 维持 B-004/B-005 验证限制；未假定镜像失效或客户端必然错误。 |
| D-010 | 维持原表所有 KIMI/GROK/CODEX 来源映射；重复项随对应 FINAL/B 条目闭环，无删除原始来源。 |

### 补充事项 1：CS8600

**已消除，类型约束修复，无警告抑制。** 字典 TryGetValue 在返回 false 时允许 out 值为 null，原来的非 nullable 局部类型不能表达该语义。将合并位置改为 `out ModelSelectionEntry[]? existing`，仅在 TryGetValue 为 true 的分支枚举/合并；字典仅写入 ToArray 产生的非 null 数组，其 nullable 流分析可证明分支安全。不使用新增 `!`、关闭 nullable 或禁用诊断解决此警告，也没有把缺失数组伪装成空数组改变合并行为。调用链、同 Provider 多文件合并和配置加载相关回归通过。最终强制 Rebuild + 最新分析器无 CS8600，生产工程无编译警告；解决方案仍有测试工程既存的 8 处 ASPDEPR004。

### 补充事项 2：终端日志

**已实现并实际运行验证。** `ModelRequestLogging` 在两个聊天入口及认证之前建立请求上下文，响应提供服务端 `X-Request-ID`。常规成功只打一条结构化 Information 摘要，记录 Requested/Normalized/实际 Final/Provider/Fallback/HTTP Status/ElapsedMs/Stream/Outcome。两个 ASP.NET 生命周期类别最低 Warning，Warning/Error 仍可见。删除旧长 Routing 日志。

失败、路由异常、fallback、候选切换、总耗时至少 30 秒使用同一条 Warning 摘要，追加 Attempts、Candidates、Error、Failures、Slow、ResponseStarted。失败历史只含 Provider/HTTP 状态/分类；不记录 key、Authorization、完整正文、用户内容、原异常消息或上游错误正文。模型标识限制长度并替换控制字符。Fallback 在摘要中也包含候选切换；现有路由响应头维持默认模型回退语义。流已开始后失败可能仍是 Status=200，因此须结合 Outcome 判断。

最终真实 Kestrel + loopback 模型上游运行：`PASS startup_ms=631 model_requests=10 summaries=10`。以下为输出原样摘录（合成模型/凭证，无真实计费）：

```text
13:38:57 info: Proxy.Requests[0] Request Id=0HNOP6JPGCM6N:00000001 Requested=probe@deepseek Normalized=probe Final=probe Provider=deepseek Fallback=False Status=200 ElapsedMs=4 Stream=False Outcome=completed
13:38:58 warn: Proxy.Requests[0] Request Id=0HNOP6JPGCM6P:00000001 Requested=failure@deepseek Normalized=failure Final=failure Provider=deepseek Fallback=False Status=503 ElapsedMs=3 Stream=False Outcome=failed Attempts=1 Candidates=deepseek/failure Error=upstream_http Failures=deepseek:503:upstream_http Slow=False ResponseStarted=True
13:38:58 warn: Proxy.Requests[0] Request Id=0HNOP6JPGCM6T:00000001 Requested=probe Normalized=probe Final=probe Provider=openai Fallback=True Status=200 ElapsedMs=5 Stream=False Outcome=completed Attempts=2 Candidates=deepseek/probe,openai/probe Error=none Failures=deepseek:503:upstream_http Slow=False ResponseStarted=True
13:39:28 warn: Proxy.Requests[0] Request Id=0HNOP6JPGCM6U:00000001 Requested=slow@deepseek Normalized=slow Final=slow Provider=deepseek Fallback=False Status=200 ElapsedMs=30105 Stream=False Outcome=completed Attempts=1 Candidates=deepseek/slow Error=none Failures= Slow=True ResponseStarted=True
```

### 最终验证和范围限制

- `dotnet build ai-proxy-hub.sln --no-restore -c Release -t:Rebuild -p:EnableNETAnalyzers=true -p:AnalysisLevel=latest`：成功，0 错误、8 个既有 ASPDEPR004，CS8600 消失；未关闭任何警告。
- `dotnet test ai-proxy-hub.sln --no-build --no-restore -c Release --verbosity minimal`：573 通过，0 失败，0 跳过。首次集成发现的目录版本回归已修复，最终无观察到的新失败。
- `python tests/benchmark_script_tests.py`：1 项通过；`powershell -NoProfile -File tests/benchmark-ranking.tests.ps1`：3 组通过。
- `python tests/runtime-log-probe.py`：实际服务 10 请求/10 摘要；正常、流式、503、404、默认回退、候选切换、30 秒慢请求及发现尚未完成时请求均通过；日志中无合成敏感标记、无四条默认生命周期信息、无旧 Routing。测试使用临时目录和本地上游，结束关闭所创建的进程。
- 最终 diff 按 HEAD 同时复核暂存/未暂存修改、异常/取消/空值/并发/兼容路径和日志泄密边界，`git diff HEAD --check` 通过。修改只涉及本报告修复、必要回归和对应文档；未覆盖用户原有审计内容，未新增依赖或更改真实密钥配置。
- 未验证真实厂商全矩阵、真实 VS/Copilot 客户端、Docker/跨平台发布、在线漏洞库及长时间压力。B-001/002/004/005 保留明确待办；不以 mock 成功宣称真实上游全部兼容。工程建议取舍不等于尚有已确认且可完成缺陷被仅写状态搁置。

### 2026-09-23 追加回归：开发启动端口与客户端不一致

用户实际以 `./run`（内部为 `dotnet run`）启动后，Visual Studio 报 `ConnectionRefused localhost:11434`；启动横幅与 Kestrel 实际监听却均为 `11343`。根因是原有 `Properties/launchSettings.json` 同时固定 `PROXY_PORT=11343` 和 `applicationUrl=http://localhost:11343`，而项目 `.env`、README 和客户端均使用 11434。此前 FINAL-024 为实现文档约定将进程环境优先于 `.env`；launch profile 注入的值属于进程环境，因此原本被旧 loader 意外覆盖的冲突显现。审计阶段的配置优先级修复本身正确，但当时没有按仓库真实 launch profile 重放 `dotnet run`，该兼容性遗漏现已补齐。

修复：仅移除 launch profile 的固定端口及 applicationUrl，保留 Development 环境；启动监听由进程 `PROXY_PORT`、`.env`、默认 11434 决定。新增真实运行探针对 profile 无端口覆盖的断言，README/CONFIGURATION 同步说明。用户的 `.env` 和客户端配置未改动。

实际验证：在项目目录执行 `dotnet run --no-build --no-restore -c Release`，输出 `URL: http://localhost:11434/v1` 和 `Now listening on: http://0.0.0.0:11434`；对 11434 的 `/health`、`/v1/models` 均得到 HTTP 200，11343 没有监听。此运行验证连接的是服务本身，没有向真实模型发送生成请求。`minimax Status=401` 表示该提供商目录鉴权失败，是独立于本地 ConnectionRefused 的问题；需检查该上游 key/权限，不应通过改端口或重试掩盖。当前执行环境的其他上游发现还出现 HttpRequestException，不能据此断言用户网络中的真实模型生成已恢复；原有本地模拟上游的请求/日志验证仍成立。
