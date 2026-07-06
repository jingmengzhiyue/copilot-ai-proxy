# Copilot AI Proxy 中文说明

Copilot AI Proxy 是一个轻量的 ASP.NET Core Minimal API 代理，用来把 GitHub Copilot、Cursor、Continue.dev、Visual Studio BYOM、Ollama 客户端以及 OpenAI SDK 接到多个模型供应商。

项目同时提供两套本地接口：

| 本地接口 | 路径前缀 | 常见客户端 |
|---|---|---|
| OpenAI 兼容接口 | `/v1/*` | GitHub Copilot、Cursor、Continue.dev、OpenAI SDK |
| Ollama 兼容接口 | `/api/*` | Visual Studio BYOM、Ollama 客户端 |

默认地址：

```text
http://localhost:11434
```

## 支持的 Provider

只有配置了 API Key 的 Provider 会在运行时启用。

| Provider key | 供应商 | 默认地址 | API Key 环境变量 |
|---|---|---|---|
| `deepseek` | DeepSeek | `https://api.deepseek.com` | `PROVIDER_DEEPSEEK_API_KEY` |
| `openai` | OpenAI | `https://api.openai.com` | `PROVIDER_OPENAI_API_KEY` |
| `zhipu` | 智谱 / BigModel | `https://open.bigmodel.cn/api/paas` | `PROVIDER_ZHIPU_API_KEY` |
| `qwen` | 通义千问 / DashScope 兼容模式 | `https://dashscope.aliyuncs.com/compatible-mode` | `PROVIDER_QWEN_API_KEY` |
| `google` | Google Gemini OpenAI 兼容接口 | `https://generativelanguage.googleapis.com` | `PROVIDER_GOOGLE_API_KEY` |
| `nvidia` | NVIDIA NIM | `https://integrate.api.nvidia.com` | `PROVIDER_NVIDIA_API_KEY` |
| `openrouter` | OpenRouter | `https://openrouter.ai/api` | `PROVIDER_OPENROUTER_API_KEY` |
| `groq` | Groq | `https://api.groq.com/openai` | `PROVIDER_GROQ_API_KEY` |
| `moonshot` | Moonshot / Kimi | `https://api.moonshot.ai` | `PROVIDER_MOONSHOT_API_KEY` |
| `cerebras` | Cerebras | `https://api.cerebras.ai` | `PROVIDER_CEREBRAS_API_KEY` |
| `zenmux` | ZenMux | `https://zenmux.ai/api` | `PROVIDER_ZENMUX_API_KEY` |
| `ollama` | Ollama Cloud 或本地 Ollama | `https://ollama.com` | `PROVIDER_OLLAMACLOUD_API_KEY` |
| `customopenai` | 任意 OpenAI 兼容服务 | 无默认值 | `PROVIDER_CUSTOMOPENAI_API_KEY` |

`customopenai` 必须额外配置 `PROVIDER_CUSTOMOPENAI_BASE_URL`。

## 快速开始

复制环境变量模板：

```bash
cp .env.example .env
```

Windows PowerShell：

```powershell
Copy-Item .env.example .env
```

编辑 `.env`，至少启用一个 Provider：

```bash
PROVIDER_DEEPSEEK_API_KEY=sk-your-deepseek-key
PROVIDER_DEEPSEEK_BASE_URL=https://api.deepseek.com

PROXY_PORT=11434
DEEPSEEK_MODEL=deepseek-v4-pro
```

启动代理：

```bash
dotnet run
```

查看模型：

```bash
curl http://localhost:11434/v1/models
curl http://localhost:11434/api/tags
```

## 两个 token limit 是什么

截图里有两个 token limit 输入框，通常分别对应：

| 截图含义 | 本项目字段 | 说明 |
|---|---|---|
| 输入上下文窗口 | `execution.context_length` | 模型最多能读取多少上下文，常对应 input token limit、num_ctx。 |
| 最大输出能力 | `execution.max_output_tokens` | 模型理论最多能输出多少 token，常对应 output token limit。 |

项目里还有一个容易混淆的字段：

| 字段 | 说明 |
|---|---|
| `execution.max_tokens` | 当客户端没有传 `max_tokens` 时，代理默认发给上游的请求输出上限。 |

简单理解：

- `context_length` 和 `max_output_tokens` 是“告诉客户端这个模型能力有多大”。
- `max_tokens` 是“默认这次请求让模型最多输出多少”。

如果未来供应商修改模型上下文窗口，比如智谱把 `glm-5.2` 从 100 万上下文升级到 200 万，只需要改对应 JSON：

```json
{
  "match": "glm-5.2",
  "display_name": "GLM 5.2",
  "execution": {
    "context_length": 2000000,
    "max_output_tokens": 131072,
    "max_tokens": 32768
  }
}
```

修改后重启代理，再检查：

```bash
curl "http://localhost:11434/api/show?model=GLM%205.2"
curl http://localhost:11434/api/tags
```

## 是否支持图片上下文

支持。

项目里的配置字段是：

```json
{
  "execution": {
    "supports_vision": true
  }
}
```

当 `supports_vision` 为 `true` 时，代理会在 Ollama 兼容元数据里暴露：

- `supports_vision: true`
- `supports_images: true`
- `capabilities` 中包含 `vision`

项目已经支持两种方向的图片格式转换：

| 方向 | 转换 |
|---|---|
| OpenAI -> Ollama | OpenAI multi-part `image_url` 转为 Ollama `images` 数组 |
| Ollama -> OpenAI | Ollama `images` 数组转为 OpenAI multi-part `image_url` |

注意：`supports_vision` 只应该给真正支持图片输入的模型开启。错误开启会让客户端发送图片，但上游模型可能拒绝请求。

## 模型配置文件在哪里

所有模型配置都在：

```text
config/model-selection/
```

常见文件：

| 文件 | 用途 |
|---|---|
| `deepseek.json` | DeepSeek 模型 |
| `zhipu.json` | 智谱 / BigModel 模型 |
| `qwen.json` | 通义千问 / DashScope 模型 |
| `customopenai.json` | 自定义 OpenAI 兼容服务 |
| `openai.json` | OpenAI 模型 |
| `moonshot.json` | Moonshot / Kimi 模型 |
| `ollamacloud.json` | Ollama Cloud 模型 |

## 模型配置格式

示例：

```json
{
  "provider": "zhipu",
  "models": [
    {
      "match": "glm-5.2",
      "display_name": "GLM 5.2",
      "priority": 1,
      "enabled": true,
      "execution": {
        "context_length": 1000000,
        "max_output_tokens": 65536,
        "supports_tools": true,
        "supports_vision": false,
        "family": "z-ai",
        "temperature": 1.0,
        "max_tokens": 16384,
        "timeout_seconds": 240
      }
    }
  ]
}
```

字段说明：

| 字段 | 是否必填 | 说明 |
|---|---:|---|
| `match` | 是 | 用来匹配上游模型 id，可以是完整 id 或稳定子串。 |
| `display_name` | 否 | 显示给客户端的短名称，也可以直接用它发起请求。 |
| `priority` | 是 | 多个 Provider 提供同名模型时，数字越小优先级越高。 |
| `enabled` | 是 | 是否暴露给客户端。 |
| `execution.context_length` | 否 | 输入上下文窗口。 |
| `execution.max_output_tokens` | 否 | 最大输出能力。 |
| `execution.max_tokens` | 否 | 默认请求输出上限。 |
| `execution.supports_tools` | 否 | 是否支持工具调用。 |
| `execution.supports_vision` | 否 | 是否支持图片上下文。 |
| `execution.family` | 否 | 模型家族标签。 |
| `execution.timeout_seconds` | 否 | 上游请求超时。 |
| `execution.override_client_params` | 否 | 是否强制覆盖客户端传来的参数。 |

## 新增 DeepSeek 模型

1. 确认 `.env` 中启用了 DeepSeek：

```bash
PROVIDER_DEEPSEEK_API_KEY=sk-your-deepseek-key
```

2. 查看 DeepSeek 上游实际返回的模型 id，或者通过代理查看：

```bash
curl http://localhost:11434/v1/models
```

3. 编辑：

```text
config/model-selection/deepseek.json
```

4. 添加模型：

```json
{
  "match": "deepseek-new-model",
  "display_name": "DeepSeek New",
  "priority": 10,
  "enabled": true,
  "execution": {
    "context_length": 128000,
    "max_output_tokens": 8192,
    "supports_tools": true,
    "supports_vision": false,
    "family": "deepseek",
    "temperature": 0.2,
    "max_tokens": 8192,
    "reasoning_effort": "high",
    "timeout_seconds": 180
  }
}
```

5. 重启代理。

## 新增智谱模型

1. 配置 `.env`：

```bash
PROVIDER_ZHIPU_API_KEY=your-bigmodel-key
# 可选，默认就是这个：
PROVIDER_ZHIPU_BASE_URL=https://open.bigmodel.cn/api/paas
```

2. 编辑：

```text
config/model-selection/zhipu.json
```

3. 添加模型：

```json
{
  "match": "glm-new-model",
  "display_name": "GLM New",
  "priority": 10,
  "enabled": true,
  "execution": {
    "context_length": 128000,
    "max_output_tokens": 8192,
    "supports_tools": true,
    "supports_vision": false,
    "family": "z-ai",
    "temperature": 0.8,
    "max_tokens": 8192,
    "timeout_seconds": 180
  }
}
```

如果新智谱模型支持图片输入，把 `supports_vision` 改成 `true`。

## 自定义任意 OpenAI 兼容服务

如果服务支持：

- `GET /v1/models`
- `POST /v1/chat/completions`
- `Authorization: Bearer <api-key>`

可以用 `customopenai`：

```bash
PROVIDER_CUSTOMOPENAI_API_KEY=your-provider-key
PROVIDER_CUSTOMOPENAI_BASE_URL=https://your-provider.example.com
```

然后编辑：

```text
config/model-selection/customopenai.json
```

把示例模型 `custom-coding-model` 换成真实上游模型 id。

## display_name 有什么用

多个来源的模型名可能很长，例如：

```text
provider/vendor/model-family-long-name-version-preview
```

可以配置：

```json
{
  "match": "provider/vendor/model-family-long-name-version-preview",
  "display_name": "My Short Model",
  "enabled": true
}
```

这样客户端模型列表里显示 `My Short Model`，并且请求时也可以直接使用：

```json
{
  "model": "My Short Model",
  "messages": [
    { "role": "user", "content": "Hello" }
  ]
}
```

代理会自动路由到真实上游模型 id。

## 路由别名

本项目支持这些模型名形式：

| 形式 | 示例 | 行为 |
|---|---|---|
| 上游模型 id | `glm-5.2` | 路由到该模型的默认 Provider。 |
| 显示名 | `GLM 5.2` | 路由到 `display_name` 对应的上游模型。 |
| Provider 限定别名 | `glm-5.2@zhipu` | 强制路由到指定 Provider。 |
| 显示名限定别名 | `GLM 5.2@zhipu` | 用短名称并强制指定 Provider。 |
| Ollama tag 后缀 | `GLM 5.2:latest` | 自动去掉 `:latest` 后再路由。 |

## 配置修改后是否要重启

需要。

当前项目不会热加载 `config/model-selection/*.json`。修改 `.env` 或模型 JSON 后，请重启代理。

## 测试

```bash
dotnet test
```

如果本地正在运行代理导致 Windows 文件锁，可以让测试输出到临时目录：

```powershell
$temp = Join-Path $env:TEMP 'copilot-ai-proxy-tests'
$bin = ((Join-Path $temp 'bin') + '/')
$obj = ((Join-Path $temp 'obj') + '/')
dotnet test tests/ProxyTests/ProxyTests.csproj `
  -p:UseAppHost=false `
  -p:BaseOutputPath=$bin `
  -p:BaseIntermediateOutputPath=$obj
```

## 常见问题

### Provider 没出现

- 检查 API Key 是否配置。
- 检查 `.env` 修改后是否重启代理。
- `customopenai` 必须配置 `PROVIDER_CUSTOMOPENAI_BASE_URL`。

### 模型没出现

- 检查上游 `/models` 是否真的返回该模型 id。
- 检查对应 `config/model-selection/{provider}.json` 是否有启用的 `match`。
- 修改 JSON 后重启代理。

### 模型显示名太长

配置 `display_name`。

### 图片请求失败

- 确认模型真的支持图片输入。
- 确认该模型配置了 `execution.supports_vision: true`。
- 如果上游只支持文本模型，不要开启 `supports_vision`。

### 上下文窗口不对

修改对应模型配置中的：

- `execution.context_length`
- `execution.max_output_tokens`
- 必要时修改 `execution.max_tokens`

然后重启代理。

## 英文文档

英文 README 见：

```text
README.md
```
