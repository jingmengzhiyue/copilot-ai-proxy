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
