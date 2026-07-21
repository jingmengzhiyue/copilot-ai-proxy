# Vercel AI Gateway customopenai 配置设计

## 目标

让项目通过现有 `customopenai` Provider 调用 Vercel AI Gateway 中已验证可用的免费层代码模型，并在中英文 README 中完整说明单模型和多模型配置方法。

## 已确认事实

- 项目会在 Provider Base URL 后追加 `v1/models` 和 `v1/chat/completions`，因此 Vercel Base URL 必须配置为 `https://ai-gateway.vercel.sh`，不能包含 `/v1`。
- `meta/muse-spark-1.1` 当前位于 Vercel 免费层模型列表，并已使用本地 `.env` 中的现有 Vercel Key 完成最小 Chat Completions 调用验证。
- 模型公开元数据为 1,048,576 token 上下文、1,048,576 token 最大输出，支持推理、工具调用、图片和 PDF 输入。
- 当前代码已支持在一个 `customopenai` Provider 下配置多个模型；无需为多个共享同一 Base URL 和 API Key 的模型新增 Provider 实现。

## 配置设计

### 本地连接配置

`.env` 使用现有密钥，并确保连接地址为：

```dotenv
PROVIDER_CUSTOMOPENAI_API_KEY=<your-vercel-ai-gateway-key>
PROVIDER_CUSTOMOPENAI_BASE_URL=https://ai-gateway.vercel.sh
```

`.env.example` 只提供注释掉的占位示例，不包含真实密钥。

### 默认 Vercel 模型

`config/model-selection/customopenai.json` 的默认示例改为 `meta/muse-spark-1.1`：

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

### 多模型

推荐在同一个 `models` 数组中增加模型对象。所有对象共享 `PROVIDER_CUSTOMOPENAI_API_KEY` 和 `PROVIDER_CUSTOMOPENAI_BASE_URL`。每个对象必须使用上游 `/v1/models` 返回的精确模型 ID，并设置唯一 `display_name` 以便客户端选择。

项目加载器也允许多个 JSON 文件声明同一个 `"provider": "customopenai"`，并将不重复的模型合并。README 将把这种方式列为适合大型配置的可选组织方式，同时说明不要在不同文件中重复同一个 `match`。

多个不同 Base URL 或 API Key 的独立连接不属于本次范围；本次教程明确区分“一个连接下的多个模型”和“多个独立连接”。

## 文档设计

同步更新 `README.md` 与 `README.zh-CN.md` 的相关章节，包含：

1. Vercel AI Gateway 单模型完整配置。
2. 在一个 `models` 数组中添加多个模型。
3. 使用多个 JSON 文件组织同一个 `customopenai` Provider 的模型。
4. 字段含义、精确模型 ID、显示名与优先级说明。
5. 重启、模型发现、非流式和流式调用验证。
6. Visual Studio/Copilot 的本地 Base URL、API Key 和模型名称填写方法。
7. 中文相关内容与英文保持一致，并补齐中文 README 中缺失的说明。

按用户要求，README 不写入 403、充值要求或相关排查内容。

## 安全设计

- 不读取或写入真实密钥到受版本控制文件、测试输出或文档。
- 将 `.history/` 加入 `.gitignore`，避免历史 `.env` 文件被意外提交。
- 保留现有 `.history/` 内容，不执行删除。

## 测试与验证

1. 先更新或新增测试，使其针对新的 Vercel 模型和多模型行为失败。
2. 修改模型配置，使针对性测试通过。
3. 运行完整测试套件，确认没有破坏其他 Provider。
4. 启动本地代理并检查 `/v1/models` 与 `/api/tags` 中的模型别名。
5. 通过本地代理发出最小 Chat Completions 请求，验证请求实际路由到 Vercel 免费层模型。
6. 检查中英文 README 的关键标题、配置变量、模型 ID 和示例命令一致。

## 不在范围内

- 不新增 Vercel 专用 Provider。
- 不支持多个不同 Base URL/API Key 的动态 `customopenai` 实例。
- 不重构与本需求无关的 Provider、模型配置或 README 内容。
- 不删除用户已有历史文件或覆盖其他未提交修改。
