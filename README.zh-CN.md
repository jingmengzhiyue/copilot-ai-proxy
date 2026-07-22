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
| `minimax` | MiniMax | `https://api.minimax.io` | `PROVIDER_MINIMAX_API_KEY` |
| `hunyuan` | 腾讯混元 / TokenHub | `https://tokenhub.tencentmaas.com` | `PROVIDER_HUNYUAN_API_KEY` |
| `google` | Google Gemini OpenAI 兼容接口 | `https://generativelanguage.googleapis.com` | `PROVIDER_GOOGLE_API_KEY` |
| `nvidia` | NVIDIA NIM | `https://integrate.api.nvidia.com` | `PROVIDER_NVIDIA_API_KEY` |
| `openrouter` | OpenRouter | `https://openrouter.ai/api` | `PROVIDER_OPENROUTER_API_KEY` |
| `groq` | Groq | `https://api.groq.com/openai` | `PROVIDER_GROQ_API_KEY` |
| `moonshot` | Moonshot / Kimi | `https://api.moonshot.ai` | `PROVIDER_MOONSHOT_API_KEY` |
| `kimi` | Kimi 国内开放平台 | `https://api.moonshot.cn` | `PROVIDER_KIMI_API_KEY` |
| `cerebras` | Cerebras | `https://api.cerebras.ai` | `PROVIDER_CEREBRAS_API_KEY` |
| `zenmux` | ZenMux | `https://zenmux.ai/api` | `PROVIDER_ZENMUX_API_KEY` |
| `ollama` | Ollama Cloud 或本地 Ollama | `https://ollama.com` | `PROVIDER_OLLAMACLOUD_API_KEY` |
| `customopenai` | 任意 OpenAI 兼容服务，包括 Vercel AI Gateway | 无默认值 | `PROVIDER_CUSTOMOPENAI_API_KEY` |

`customopenai` 必须额外配置 `PROVIDER_CUSTOMOPENAI_BASE_URL`。Vercel 示例使用
`https://ai-gateway.vercel.sh`。

## Release 版本

普通用户推荐优先下载 GitHub Release 里的 zip 包。Release 包是自包含的，不需要用户安装 .NET SDK。

每个 Release 包包含：

- `ai-proxy-hub` 或 `ai-proxy-hub.exe`
- `.env.example`
- `config/model-selection/*.json`
- `README.md`
- `README.zh-CN.md`
- 对应平台的启动脚本

根据系统下载对应文件：

| 包名 | 适用系统 |
|---|---|
| `copilot-ai-proxy-vX.Y.Z-win-x64.zip` | Windows x64 |
| `copilot-ai-proxy-vX.Y.Z-linux-x64.zip` | Linux x64 |
| `copilot-ai-proxy-vX.Y.Z-osx-x64.zip` | Intel macOS |
| `copilot-ai-proxy-vX.Y.Z-osx-arm64.zip` | Apple Silicon macOS |

Windows：

```powershell
Expand-Archive .\copilot-ai-proxy-vX.Y.Z-win-x64.zip
cd .\copilot-ai-proxy-vX.Y.Z-win-x64
.\start-windows.cmd
```

Linux/macOS：

```bash
unzip copilot-ai-proxy-vX.Y.Z-linux-x64.zip
cd copilot-ai-proxy-vX.Y.Z-linux-x64
chmod +x ./start-unix.sh ./ai-proxy-hub
./start-unix.sh
```

首次运行时，启动脚本会从 `.env.example` 复制出 `.env`，然后提示你编辑配置。填好至少一个 Provider API Key 后，再次启动代理即可。

如果要修改模型上下文、显示名、是否支持工具调用或图片输入，直接编辑同目录下的 `config/model-selection/*.json`。修改 `.env` 或模型 JSON 后，需要重启代理。

## 快速开始

下面是从源码运行的方式。

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

## 上下文和输出 token 配置

本项目把模型的输入上下文窗口和最大输出能力分成两个字段配置：

| 能力 | 本项目字段 | 说明 |
|---|---|---|
| 输入上下文窗口 | `execution.context_length` | 模型最多能读取多少上下文，常对应 input token limit、num_ctx。 |
| 最大输出能力 | `execution.max_output_tokens` | 模型理论最多能输出多少 token，常对应 output token limit。 |

项目里还有一个容易混淆的字段：

| 字段 | 说明 |
|---|---|
| `execution.max_tokens` | 当客户端没有传 `max_tokens` 时，代理默认发给上游的请求输出上限。 |
| `execution.max_completion_tokens` | 使用新版 OpenAI 字段的供应商所需的默认请求输出上限。 |

简单理解：

- `context_length` 和 `max_output_tokens` 是“告诉客户端这个模型能力有多大”。
- `max_tokens` 和 `max_completion_tokens` 是同一请求上限的两种供应商字段，只配置官方文档要求的一种，代理不会同时发送。

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
| `kimi.json` | Kimi 国内开放平台模型 |
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
| `execution.max_completion_tokens` | 否 | 使用新版 OpenAI 字段时的默认请求输出上限。 |
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

## 配置 Kimi 国内接口

Kimi 国内站和 Moonshot 国际站的 API Key 不通用。国内站单独配置：

```bash
PROVIDER_KIMI_API_KEY=sk-your-kimi-cn-key
# 可选，默认就是这个：
PROVIDER_KIMI_BASE_URL=https://api.moonshot.cn
```

模型白名单位于 `config/model-selection/kimi.json`。如果国内站和国际站
同时启用，建议使用 `@kimi` 明确指定国内接口：

```bash
curl http://localhost:11434/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "kimi-k2.7-code@kimi",
    "messages": [{ "role": "user", "content": "写一个 C# 二分查找函数。" }],
    "stream": true
  }'
```

## 通过 `customopenai` 使用 Vercel AI Gateway

Vercel AI Gateway 提供 OpenAI 兼容的模型列表和 Chat Completions 接口。本项目
已有的 `customopenai` Provider 可以直接连接，无需再增加 Vercel 专用 Provider。

### 1. 创建连接

在 Vercel 中创建 AI Gateway API Key，并且只把它保存在本地 `.env` 文件中：

```bash
PROVIDER_CUSTOMOPENAI_API_KEY=your-vercel-ai-gateway-key
PROVIDER_CUSTOMOPENAI_BASE_URL=https://ai-gateway.vercel.sh
```

Base URL 不要添加 `/v1`。本项目向上游发送请求时，会自行追加 `v1/models` 和
`v1/chat/completions`。

需要查看当前可用的准确模型 ID 时，可以查询上游模型列表：

```bash
curl https://ai-gateway.vercel.sh/v1/models
```

复制完整的上游模型 ID，包括 `meta/`、`kwaipilot/` 这样的创建者前缀。

### 2. 配置一个模型

仓库中的 `config/model-selection/customopenai.json` 已配置经过验证的 Vercel
模型：

```json
{
  "provider": "customopenai",
  "models": [
    {
      "match": "meta/muse-spark-1.1",
      "display_name": "Muse Spark 1.1",
      "priority": 1,
      "enabled": true,
      "execution": {
        "context_length": 1048576,
        "max_output_tokens": 1048576,
        "supports_tools": true,
        "supports_vision": true,
        "supports_reasoning": true,
        "family": "muse-spark",
        "temperature": 0.2,
        "max_tokens": 65536,
        "timeout_seconds": 300
      }
    }
  ]
}
```

主要字段含义如下：

| 字段 | 含义 |
|---|---|
| `match` | Vercel `/v1/models` 返回的准确模型 ID。 |
| `display_name` | 客户端模型列表中显示的简短且唯一的名称。 |
| `priority` | 显示和路由顺序；数字越小越靠前。 |
| `enabled` | 是否通过本代理公开该模型。 |
| `context_length` | 模型的输入上下文能力。 |
| `max_output_tokens` | 向客户端声明的模型最大输出能力。 |
| `supports_tools` | 是否声明工具或函数调用能力。 |
| `supports_vision` | 是否声明图片输入能力。 |
| `supports_reasoning` | 是否声明推理能力。 |
| `max_tokens` | 客户端未指定时，本次请求使用的默认输出上限。 |
| `timeout_seconds` | 该模型的上游请求超时时间。 |

能力字段应以 Vercel 模型列表公布的数据为准。`max_tokens` 是单次请求默认值，
可以小于模型能力字段 `max_output_tokens`。

### 3. 在同一连接中添加多个模型

在同一个 `models` 数组中继续添加对象即可。所有模型共用一组
`PROVIDER_CUSTOMOPENAI_API_KEY` 和 `PROVIDER_CUSTOMOPENAI_BASE_URL`：

```json
{
  "provider": "customopenai",
  "models": [
    {
      "match": "meta/muse-spark-1.1",
      "display_name": "Muse Spark 1.1",
      "priority": 1,
      "enabled": true,
      "execution": {
        "context_length": 1048576,
        "max_output_tokens": 1048576,
        "supports_tools": true,
        "supports_vision": true,
        "supports_reasoning": true,
        "family": "muse-spark",
        "temperature": 0.2,
        "max_tokens": 65536,
        "timeout_seconds": 300
      }
    },
    {
      "match": "kwaipilot/kat-coder-air-v2.5",
      "display_name": "Kat Coder Air V2.5",
      "priority": 2,
      "enabled": true,
      "execution": {
        "context_length": 256000,
        "max_output_tokens": 80000,
        "supports_tools": true,
        "supports_vision": false,
        "supports_reasoning": true,
        "family": "kat-coder",
        "temperature": 0.2,
        "max_tokens": 32768,
        "timeout_seconds": 300
      }
    }
  ]
}
```

添加每个模型时需要注意：

- `match` 必须等于上游的准确模型 ID。
- `display_name` 应保持唯一，便于客户端区分模型。
- 如果希望列表顺序稳定，请使用不同的 `priority`。
- 只填写该模型实际支持的能力。

### 4. 将较长的模型列表拆分为多个文件

模型很多时，可以在 `config/model-selection/` 下创建多个 JSON 文件。文件名可以
自定义，但每个文件都必须声明同一个 Provider。

`config/model-selection/customopenai-muse.json`：

```json
{
  "provider": "customopenai",
  "models": [
    {
      "match": "meta/muse-spark-1.1",
      "display_name": "Muse Spark 1.1",
      "priority": 1,
      "enabled": true,
      "execution": {
        "context_length": 1048576,
        "max_output_tokens": 1048576,
        "supports_tools": true,
        "supports_vision": true,
        "supports_reasoning": true,
        "family": "muse-spark",
        "max_tokens": 65536,
        "timeout_seconds": 300
      }
    }
  ]
}
```

`config/model-selection/customopenai-coding.json`：

```json
{
  "provider": "customopenai",
  "models": [
    {
      "match": "kwaipilot/kat-coder-air-v2.5",
      "display_name": "Kat Coder Air V2.5",
      "priority": 2,
      "enabled": true,
      "execution": {
        "context_length": 256000,
        "max_output_tokens": 80000,
        "supports_tools": true,
        "supports_vision": false,
        "supports_reasoning": true,
        "family": "kat-coder",
        "max_tokens": 32768,
        "timeout_seconds": 300
      }
    }
  ]
}
```

加载器会合并所有 `provider` 为 `customopenai` 且 `match` 不重复的模型。不要在
多个文件中重复配置同一个 `match`。拆分文件只用于整理模型列表，不会创建不同的
API Key 或 Base URL；多个独立连接需要单独的 Provider 支持。

### 5. 重启并验证

模型选择文件只在启动时加载，因此每次修改 JSON 后都需要重启代理。先检查两种
模型列表格式：

```bash
curl http://localhost:11434/v1/models
curl http://localhost:11434/api/tags
```

测试非流式 OpenAI 兼容请求：

```bash
curl http://localhost:11434/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"Muse Spark 1.1@customopenai","messages":[{"role":"user","content":"你好"}],"stream":false}'
```

测试流式请求：

```bash
curl http://localhost:11434/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"Muse Spark 1.1@customopenai","messages":[{"role":"user","content":"你好"}],"stream":true}'
```

OpenAI 兼容客户端使用 Base URL `http://localhost:11434/v1`。Visual Studio
BYOM 或 Ollama 兼容客户端使用 `http://localhost:11434`。如果设置了
`PROXY_API_KEY`，客户端填写该本地代理 Key；否则填写任意非空值。模型名称选择
`/v1/models` 或 `/api/tags` 返回的值。

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

## 创建 Release

维护者可以在本地生成 Release 包：

```powershell
.\scripts\package-release.ps1 -Version vX.Y.Z
```

也可以推送版本 tag，让 GitHub Actions 自动发布：

```bash
git tag vX.Y.Z
git push origin vX.Y.Z
```

`Release` workflow 会先运行测试，然后为 Windows、Linux、macOS 生成自包含 zip 包，并上传到 GitHub Release。

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
