# Kimi 国内 Provider 与 Custom 模型接入文档设计

## 目标

为代理新增独立的 Kimi 国内 Provider，使用 Kimi 开放平台国内接口和独立环境变量，同时完整保留现有 Moonshot 国际站 Provider。扩充中英文 README 中的自定义 OpenAI 兼容模型接入步骤，使用户可以从兼容性检查一路完成到 Visual Studio/Copilot 验证。

## 官方接口依据

- Kimi 国内服务地址为 `https://api.moonshot.cn`。
- 模型目录接口为 `GET /v1/models`。
- 对话补全接口为 `POST /v1/chat/completions`。
- 认证方式为 `Authorization: Bearer <MOONSHOT_API_KEY>`。
- 请求和响应兼容 OpenAI Chat Completions；`thinking`、消息内的 `partial` 等 Kimi 扩展字段可随请求透传。
- `platform.kimi.com` 与 `platform.kimi.ai` 的 API Key 不可混用，因此国内 Kimi 和现有国际 Moonshot 必须保持独立配置。

参考资料：

- <https://platform.kimi.com/docs/api/overview>
- <https://platform.kimi.com/docs/api/chat>
- <https://platform.kimi.com/docs/api/list-models>
- <https://platform.kimi.com/docs/models>

## 方案选择

采用独立 `kimi` Provider，而不是替换 `moonshot`、复用同一个环境变量或要求用户占用 `customopenai`。

这样做的原因：

- 国内站和国际站使用不同的 API Key，独立 Provider 与官方平台边界一致。
- 现有 Moonshot 用户的地址、Key 和路由行为不变。
- 用户仍可同时配置其他 `customopenai` 服务。
- 当前架构已经以 Provider 能力注册表驱动 OpenAI 兼容接口，不需要新增专用 HTTP 转发实现。

## Provider 设计

在 `ProviderCapabilitiesRegistry` 中新增 `kimi`：

| 属性 | 值 |
|---|---|
| Category | `Direct` |
| ApiFormat | `OpenAi` |
| SupportsReasoningEffort | `false` |
| SupportsTopK | `false` |
| ChatPath | `v1/chat/completions` |
| ModelsPath | `v1/models` |
| DefaultBaseUrl | `https://api.moonshot.cn` |
| EnvPrefix | `KIMI` |

用户配置：

```bash
PROVIDER_KIMI_API_KEY=sk-your-kimi-cn-key
# 可选；默认值如下：
PROVIDER_KIMI_BASE_URL=https://api.moonshot.cn
```

Provider 使用现有的 Bearer 认证、模型目录拉取、SSE 转发、非流式转发、工具调用和多模态消息处理逻辑。

## 模型配置

新增 `config/model-selection/kimi.json`，首批收录面向 Copilot 和代码代理的模型：

- `kimi-k2.7-code`
- `kimi-k2.7-code-highspeed`
- `kimi-k2.6`
- `kimi-k2.5`

模型配置使用 256K 上下文元数据，并按官方说明标记工具、视觉和思考能力。请求默认值沿用项目对 Kimi K2.x 的保守配置：`temperature=1.0`、`top_p=0.95`、`max_tokens=4096` 和 `override_client_params=true`。代理保留客户端提供的 `thinking` 扩展字段；不引入 Kimi 专属请求 DTO。

模型最终是否暴露仍由实时 `GET /v1/models` 返回值和本地启用的模型白名单共同决定。账号无权访问的模型不会出现在代理模型列表中。

## 路由兼容性

`kimi` 在能力注册表中的发现顺序放在现有 `moonshot` 之后。若两个 Provider 同时启用并返回相同上游模型 ID：

- 现有裸模型名继续优先路由到 `moonshot`，避免改变既有用户行为。
- `kimi-k2.7-code@kimi` 等限定别名只路由到 Kimi 国内站，且不触发跨 Provider failover。
- 只有 `kimi` 启用时，`kimi-k2.7-code` 等裸模型名正常路由到 Kimi 国内站。

README 将明确说明以上规则，并推荐在两边同时启用时使用 `@kimi`。

## README 设计

同时修改 `README.md` 与 `README.zh-CN.md`：

1. 在 Provider 表格和环境变量示例中加入国内 Kimi。
2. 增加国内 Kimi 的完整配置与请求示例。
3. 将现有简略的 `customopenai` 章节扩展为可执行教程：
   - 确认上游支持模型目录、聊天补全和 Bearer 认证。
   - 明确 `PROVIDER_CUSTOMOPENAI_BASE_URL` 应填写 API 根地址，不能重复包含代理会追加的 `v1` 路径。
   - 直接调用上游 `/v1/models` 获取真实模型 ID。
   - 给出完整的 `customopenai.json` 示例并解释关键字段。
   - 重启代理并验证 `/v1/models`、`/api/tags` 和聊天请求。
   - 给出 Visual Studio/Copilot 的本地地址、模型名和路由诊断方法。
   - 列出模型不出现、404 路径重复、401 Key 错误和模型 ID 不匹配等常见问题。

示例 Provider 和模型使用占位域名及虚构 ID，避免误导用户把示例当作真实服务。

## 测试策略

按 TDD 顺序增加测试：

1. Provider 能力测试验证 `kimi` 的环境变量前缀、默认地址和两个 API 路径。
2. Provider 发现测试验证只设置 `PROVIDER_KIMI_API_KEY` 时会注册国内 Kimi，且不注册 Moonshot。
3. 模型选择测试验证 `kimi.json` 被加载，并能找到首选代码模型及其能力元数据。
4. 路由测试验证 Kimi 与 Moonshot 同时存在时，`model@kimi` 精确指向国内 Provider。
5. 运行相关测试后，再运行全量 `dotnet test` 和 `dotnet build`。

测试只使用现有内存 Fake Provider 或本地环境变量，不调用真实 Kimi API，也不需要真实密钥。

## 非目标

- 不删除或重命名现有 `moonshot` Provider。
- 不迁移用户已有的 Moonshot 环境变量。
- 不新增文件上传、Token 估算、余额查询等代理端点。
- 不实现 Kimi 官方工具或联网搜索等平台专有业务能力。
- 不把 `max_tokens` 全局迁移为 `max_completion_tokens`，避免扩大本次改动范围。
