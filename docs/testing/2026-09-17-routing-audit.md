# Kimi Code / Visual Studio 路由独立二次审计

审计日期：2026-09-16 至 2026-09-17。基线提交：`f3741c0`，审计对象包含任务开始时已存在的未提交修复。工作目录在任务期间从 `D:\Dev\copilot-ai-proxy` 切换至 `E:\Datum\Code\AICode\claude\copilot-ai-proxy`，修改及证据均已保留。未提交 Git commit，未修改密钥、Provider 配置或默认模型。

## 1. Executive Summary

**最终结论：PASS WITH ISSUES。** Kimi Code 主问题的修复方向正确：统一解析入口、保留显式 Provider 身份、单候选严格路由、替换上游 model。它不是把默认模型改成 Kimi 的 workaround，默认值仍为 `deepseek-v4-pro`。

但收到的实现并未通过独立反例验证：有真实的跨 Provider tags 往返错误、任意冒号后缀误剥离，以及发现的日期版本抢占显示名。上述问题已在本次审计中复现、补测试并修复。不能把修改前状态评为 PASS。

最终构建成功；**469/469 自动测试通过**；真实 tags 的 **28 个条目、56 个 name/model 字段均通过 show 往返**。真实 Kimi Code 普通、SSE、工具调用两轮、大请求及 Hunyuan 均成功。剩余问题主要在非核心错误处理、可观测性和未完成的 UI/压力验证。

## 2. 原始 Root Cause

基线 `ProviderRegistry.ResolveModel` 对找不到的 alias 返回 DefaultModel，`ResolveProvider` 还可选择首个 Provider。旧实现没有贯穿端点的严格 Provider 约束，也不理解 `KIMICODE - k3:latest`。

因此无法识别的 Kimi 名称可以经过默认回退变成 DeepSeek 请求。用户提供的历史诊断头与该源码路径一致：Kimi 名称 → default `deepseek-v4-pro` → DeepSeek → 402。此次未重放已修复的历史付费失败；“历史 402 由 DeepSeek 返回”依据历史诊断证据与旧源码，不能冒充本次新捕获的响应。

## 3. 当前实现与数据流

`config/model-selection/*.json` 定义 match、display_name、priority、enabled、execution；环境变量提供各 Provider 独立的 key/base URL。ProviderCapabilitiesRegistry 定义精确 Provider ID、环境变量前缀和 HTTP 路径；ProviderRegistry 注册客户端；ModelCatalogService 合并发现结果并建立 upstream、Provider、alias 映射。

对外 tags 的 name 现在是 `PROVIDER - 显示名或完整 upstream:latest`，model 是 `upstream@provider:latest`。两者都保存 Provider，vendor 前缀不再丢失。可选 aliases 中的裸 ID 仍表示自动路由，不承诺锁定 Provider。

客户端 request.model → `ModelCatalogService.ResolveRoute` → canonical model 与 ProviderHint → Candidates → RequestTransformer 替换 model → Provider Client + ChatPath。OpenAI 的普通/流式请求以及 Ollama chat/show 使用同一解析入口。

- 已注册的真实模型 ID 优先于兼容语法；真实 `@`、`/`、空格、点号、连字符和冒号可保留。
- 每个 literal tier 先尝试完整匹配，再考虑一个终止的 `:latest`。`:batch`、`:free` 不被当成兼容 tag。
- 显式 Provider 路由最多一个候选；未知 Provider/model 返回 404，不能走默认回退。
- 裸模型允许自动选择；完全未知的裸 ID 保留原有 default fallback，并标记 FallbackUsed。
- canonical identity 实际由 upstream ID 与 Provider 共同表达，裸模型本身并不唯一标识 Provider。代码没有引入全局唯一身份对象。
- `kimicode` 与 `kimi` 按精确 ID 分离，分别读取 `PROVIDER_KIMICODE_*`、`PROVIDER_KIMI_*`。默认有效请求路径分别为 `https://api.kimi.com/coding/v1/chat/completions` 与 `https://api.moonshot.cn/v1/chat/completions`。

## 4. Source Review

| 文件 | 检查职责与结论 |
|---|---|
| `Services/ProviderCapabilitiesRegistry.cs`、`Infrastructure/ProviderHttpClientFactory.cs` | 独立 key/base URL、相对 HTTP 路径、HttpClient、Bearer；未发现 kimi/kimicode 前缀串用 |
| `Services/ProviderRegistry.cs` | 精确 Provider 查找、catalog/candidates、默认策略、仅终止 latest 剥离 |
| `Services/ModelSelectionStore.cs` | routing exact 与 discovery substring 分工；exact 优先，k3 与长名称不会互相替换 |
| `Services/ModelCatalogService.cs` | 主解析器、发现 alias、canonical、配置后备、超时；日期变体不能继承基础模型显示名 |
| `Models/ResolvedRoute.cs` | Requested/Normalized/Model/ProviderHint/Candidates/FallbackUsed 明确分开 |
| `Endpoints/OpenAiEndpoints.cs` | JSON 验证、共享路由、上下游 model 替换、单候选严格性、错误状态与 SSE |
| `Endpoints/OllamaEndpoints.cs` | tags 生成、show/chat 解析、JSON 与 strict 错误、协议转换 |
| `Services/RequestTransformer.cs` | OpenAI tools、tool_choice、assistant tool_calls、tool messages 保留；约 120KB 捕获比对通过 |
| `Services/ChatStreamingService.cs` | SSE 逐行转发含 usage/tool calls/DONE；断流处理仍有下述限制 |

没有发现对 `KIMICODE - k3:latest` 写死的特殊 if；Provider 特定能力配置与通用 routing 区分明确。

## 5. Issues Found

| 严重度 | 可复现问题 | 处置 |
|---|---|---|
| High | tags 的 OpenRouter `model` 字段回传后选择 DeepSeek；原测试显式允许这种 Provider 偏移 | 已修复：name/model 均携带 Provider，实际 endpoint 测试精确比较 provider/upstream，移除放行错误的镜像测试 |
| High | `k3:batch@kimicode`、`:free` 被当成 k3；`k3@kimicode:batch` 被错误接受 | 已修复：只剥离终止 `:latest` |
| High | 完整 vendor/model 可以经最后一段匹配变成另一个模型；真实 `:latest` 配置可输给已发现的基础模型 | 已修复：完整路径不能丢弃 vendor；literal config 与 catalog 在剥离前比较 |
| Medium | 真实 Qwen 显示 alias 解析成日期版本，例如 `Qwen3.7 Max` → `qwen3.7-max-2026-06-08` | 已修复：substring 发现的变体不继承基础模型 display_name；真实 4 项失败 → 0 |
| Medium | `/api/show` 把非法 JSON、未知显式 Provider 返回 200；chat 中字符串 stream 导致异常 | 已修复：show 400/404、chat 400；测试覆盖 |
| Medium | upstream error body 截断后仍可能泄露被上游回显的 prompt/密钥；Resolved 头实际表示 Normalized | 已修复：仅记录 body 长度；新增 Normalized 头，Resolved 表示实际解析结果 |
| Low | 6 项基线测试依赖过期/禁用模型配置 | 更新 GLM 当前配置断言、Nemotron `:free` 变体，移除已被现有用例覆盖的旧 Qwen 条目；未放宽断言或启用已禁用模型 |

**仍保留的问题（不阻断当前 Kimi Code 路由）：**

- Medium：`/api/chat` 的网络/超时异常尚没有与 OpenAI 端点相同的 502 转换，源码仍允许异常上抛；本次错误状态矩阵验证的是 OpenAI-compatible 上游路径，不能推广到全部 Ollama 分支。
- Medium：SSE 收到部分数据后遇到 HttpIOException 会直接结束，没有明确的流内错误事件。不会伪造 DONE，但不能保证所有客户端正确识别断流；“客户端自动重试”没有验证。
- Low：自动模式多候选 failover 时，Routing 日志与 Primary 头记录初始候选，不能独自证明最终成功候选；FallbackUsed 表示默认模型回退，不表示候选重试。
- Low：构建仍有 1 项 CS8600 与 8 项 ASPDEPR004 警告。未作无关框架升级。

## 6. Changes Made

生产修改限于 ProviderRegistry、ModelSelectionStore、ModelCatalogService、两个 endpoint 文件。修复 tag/alias/发现显示名身份问题、show/stream 输入错误、诊断字段和 error-body 日志。OpenAI SSE 读取使用模型超时与客户端取消关联的 token。补充 `RoutingAuditEndpointTests`，扩展 StrictRoutingTests，修正配置过期断言，并同步 API/配置/架构说明。

调用方迁移：刷新模型列表。自定义显示名现在有 Provider 前缀，model 字段返回 qualified ID。旧裸名仍支持自动路由；旧的缩短 slash 显示 alias 仍兼容。默认模型和未知裸模型 fallback 产品语义未改变。任务开始已有的未提交改动（包括 CLAUDE.md、ResolvedRoute 等）被保留，不全是本轮新增。

## 7. Automated Tests

| 测试 | 场景 | 结果 |
|---|---|---|
| `Audit_UnknownVariantMustNotBecomeAnotherModel` | batch/free、错误 provider tag、错误 vendor | 修复前 4 项失败，最终通过 |
| `Audit_ConfigLiteralTagBeatsDiscoveredBaseModel` | 真实 latest ID 优先于发现的基础模型 | 修复前失败，最终通过 |
| `Audit_ActualTagsRoundTripPreservesEveryProviderAndUpstream` | 八 Provider 真实 tags endpoint；无发现/含 snapshot 发现；特殊字符模型 | 两种状态均通过；不复制 endpoint 命名逻辑 |
| `StrictRoute_*`、`V1Chat_*`、`ApiChat_*` | 显式 Provider、Kimi 隔离、prefix、负例、invalid JSON、旧 alias | 通过 |
| `ToolsAndLargeBody_ReachPinnedProviderUnchanged` | 普通/SSE，120,000 字符 system prompt、tools/结果消息结构比对 | 通过 |
| `ExplicitProvider_UpstreamErrorsNeverCrossProviders` | 普通/SSE × 401/402/403/404/429/500/503 | 14 项通过；均仅调用 kimicode |
| `TransportFailure_Returns502WithoutProviderFallback` | 网络错误/模拟 timeout × 普通/SSE | 4 项通过 |
| `ClientCancellation_StopsUpstreamWithoutFailover` | 请求取消传至等待中的上游，不跨 Provider | 2 项通过（响应头之前） |
| `InvalidRequests_DoNotSucceedOrBecome500` | show JSON/strict 错误、stream 非布尔类型 | 5 项修复前失败，最终通过 |
| `StrictRoute_SlashStrippedDisplayName_FindsConfigSuffixMatch` | 旧缩短 slash alias 的冷/热 catalog canonical 一致 | 2 项通过 |

基线：436 项，430 通过、6 失败。最终：**469 项全部通过，0 skipped**。`dotnet build --no-restore` 成功，`git diff --check` 通过。首次 restore 被网络沙箱阻止，获工具授权联网还原后继续完成。迁移目录后测试日志曾受 Windows Event Log 权限影响，新增测试主机已清除宿主日志 provider，未改变生产日志设置。

证据：`artifacts/audit/audit-baseline.trx`、`audit-counterexamples.trx`、`audit-tags-before.trx`、`audit-http-before.trx`、`audit-discovery-collision.trx`、`audit-final.trx`、`build-final.log`。

## 8. Integration Tests

通过 `dotnet run --no-build --no-launch-profile` 启动真实 Proxy，使用本地现有凭据，发送合成内容。没有打印密钥或 prompt。

| 请求 | 真实结果 |
|---|---|
| `kimi-for-coding@kimicode` | 200，kimicode / kimi-for-coding，无 fallback |
| tags 中的 `KIMICODE - kimi-for-coding:latest` | 200，同一身份 |
| tags 中的 `KIMICODE - k3:latest` | 200，kimicode / k3 |
| K3 stream=true | 200，text/event-stream，收到 `[DONE]` |
| `/api/chat` 的 `k3@kimicode:latest` | 200，kimicode / k3 |
| K3 大请求 | 127,482 字节，200；第二次约 50.7 秒，未截断请求 |
| K3 工具调用、工具结果回传 | 两轮均 200；1 个 tool_call；第二轮正常完成 |
| tags 中的 Hunyuan display name | 200，hunyuan / hy3；收尾复核 finish_reason=stop |
| 错误 Provider/错误模型 | 404，Candidates=0，FallbackUsed=false |
| 非法 JSON | 400 |
| tags → show 全量 | 28 条、56 个 name/model 字段；修复前 4 个 Qwen 日期版本偏移，修复后 0 失败 |

日志样例：`RequestedModel=KIMICODE - k3:latest NormalizedModel=k3 ResolvedModel=k3 ProviderHint=kimicode Candidates=1 ResolvedProvider=kimicode UpstreamModel=k3 BaseUrl=https://api.kimi.com/coding FallbackUsed=False`。BaseUrl 不带 `/v1` 是因为 ChatPath 为 `v1/chat/completions`，有效 URL 正确。

证据：`artifacts/audit/integration-results.json`、`integration-final-smoke.json`、`live-tags.json`、`live-roundtrip.json`、`proxy-verified.log`；可复用探针为同目录的 `integration.ps1`、`roundtrip.ps1`。最后一个仅影响旧 slash alias 的 NormalizedModel 字段修正后，再次完整 build/test 通过；未为这个不改变 upstream 的字段修正重复付费请求。审计启动的服务器已停止。

## 9. Regression Assessment

| Provider | 路由/alias 自动回归 | 官方聊天实测 |
|---|---|---|
| Kimi Code | 通过，包括与 Kimi 隔离、四个模型前缀与标准别名 | 通过，普通/SSE/tools/大请求 |
| Hunyuan | 通过，自定义显示名 canonical=hy3 | 通过 |
| Kimi、DeepSeek、Qwen、Zhipu、OpenRouter、CustomOpenAI | 配置及 tags 冷/热发现往返通过 | 未逐家执行付费聊天；不能声称账号余额、权限或全部模型可用 |

真实本地 tags/show 验证覆盖当时已配置的 28 个模型；不要求未启用 Provider 出现在生产列表。

## 10. Remaining Risks

没有从 Visual Studio UI 发起新 Copilot Chat；本次验证的是其已知 HTTP 协议链路，不能代替 UI 验收。真实多轮 streaming tool-call 重组、流中网络中断、客户端在流中取消，以及高并发 catalog 刷新未做压力/故障注入测试。

未知裸模型仍可变成默认模型。虽然诊断头和日志能说明该决定，但忽略诊断头的客户端仍可能误以为使用了指定模型。建议后续提供显式可配置的拒绝未知模型策略；本次按要求不擅自改变既有产品语义。

Provider 列表可路由不等于上游一定提供该模型；配置后备解决发现失败，真实不可用仍由上游错误表达。未证明任意未来配置、重复自定义显示名及所有旧缩短后缀均无歧义。

## 11. Final Verdict

**PASS WITH ISSUES**

核心修复与本次新增修复已获得源码、反例失败记录、469 项自动测试及真实 HTTP 证据支持。Kimi Code 不再依赖误回退到默认模型。由于上述非阻断性错误处理/日志限制，以及尚未执行 Visual Studio UI 和流中故障验证，不给出无保留 PASS。
