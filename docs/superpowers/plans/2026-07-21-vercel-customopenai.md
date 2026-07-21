# Vercel customopenai Configuration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Configure the verified Vercel free-tier `meta/muse-spark-1.1` model through `customopenai` and document single-model and multi-model setup equally in English and Chinese.

**Architecture:** Keep the existing generic `customopenai` provider and its single API key/base URL connection. Replace the user-edited paid model selection with Muse Spark, rely on the existing model array and same-provider file merge behavior for multiple models, and limit code changes to tests/configuration/documentation.

**Tech Stack:** .NET 10, C#, xUnit, JSON model-selection files, dotenv, Markdown

---

### Task 1: Lock the Vercel model profile with a failing test

**Files:**
- Modify: `tests/ProxyTests/ModelSelectionStoreTests.cs`
- Modify: `config/model-selection/customopenai.json`

- [ ] **Step 1: Replace the stale custom model assertion with the desired Muse Spark profile**

```csharp
[Fact]
public void FindModelSelectionEntry_CustomOpenAi_MuseSpark_HasVercelProfile()
{
    ModelSelectionStore store = new();

    ModelSelectionEntry? entry = store.FindModelSelectionEntry("meta/muse-spark-1.1", "customopenai");

    Assert.NotNull(entry);
    Assert.Equal("Muse Spark 1.1", entry.Value.DisplayName);
    Assert.Equal(1_048_576, entry.Value.Execution.ContextLength);
    Assert.Equal(1_048_576, entry.Value.Execution.MaxOutputTokens);
    Assert.Equal(65_536, entry.Value.Execution.MaxTokensPreferred);
    Assert.True(entry.Value.Execution.SupportsTools);
    Assert.True(entry.Value.Execution.SupportsVision);
    Assert.True(entry.Value.Execution.SupportsReasoning);
    Assert.Equal("muse-spark", entry.Value.Execution.Family);
}
```

- [ ] **Step 2: Run the new test and verify RED**

Run:

```powershell
dotnet test tests/ProxyTests/ProxyTests.csproj --no-restore --filter "FullyQualifiedName~FindModelSelectionEntry_CustomOpenAi_MuseSpark_HasVercelProfile"
```

Expected: FAIL because the current file still declares `deepseek/deepseek-v4-pro`.

- [ ] **Step 3: Replace the custom model configuration with Muse Spark**

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

- [ ] **Step 4: Run the targeted tests and verify GREEN**

Run:

```powershell
dotnet test tests/ProxyTests/ProxyTests.csproj --no-restore --filter "FullyQualifiedName~CustomOpenAi"
```

Expected: all matching tests pass.

### Task 2: Normalize local and example environment configuration

**Files:**
- Modify: `.env`
- Modify: `.env.example`
- Modify: `.gitignore`

- [ ] **Step 1: Preserve the local key and normalize only its Base URL**

Ensure `.env` contains exactly one active Base URL assignment:

```dotenv
PROVIDER_CUSTOMOPENAI_BASE_URL=https://ai-gateway.vercel.sh
```

Do not print, replace, or copy `PROVIDER_CUSTOMOPENAI_API_KEY`.

- [ ] **Step 2: Replace the generic example comments with a Vercel-ready example**

```dotenv
# Generic OpenAI-compatible provider (Vercel AI Gateway example).
# This project appends /v1/models and /v1/chat/completions, so omit /v1 here.
# Create an AI Gateway API key in Vercel, then replace the placeholder below.
#PROVIDER_CUSTOMOPENAI_API_KEY=your-vercel-ai-gateway-key
#PROVIDER_CUSTOMOPENAI_BASE_URL=https://ai-gateway.vercel.sh
```

- [ ] **Step 3: Ignore editor history containing dotenv snapshots**

Append to `.gitignore`:

```gitignore
.history/
```

- [ ] **Step 4: Validate without exposing the key**

Run:

```powershell
$envLines = Get-Content -LiteralPath .env
$keyLines = @($envLines | Where-Object { $_ -match '^PROVIDER_CUSTOMOPENAI_API_KEY=' })
$baseLines = @($envLines | Where-Object { $_ -match '^PROVIDER_CUSTOMOPENAI_BASE_URL=' })
$keyValue = if ($keyLines.Count -eq 1) { ($keyLines[0] -split '=', 2)[1].Trim() } else { '' }
"KEY=$(if ($keyValue) { '<set>' } else { '<missing>' })"
"KEY_ASSIGNMENTS=$($keyLines.Count)"
"BASE_ASSIGNMENTS=$($baseLines.Count)"
"BASE_URL=$(if ($baseLines.Count -eq 1) { ($baseLines[0] -split '=', 2)[1].Trim() } else { '<invalid>' })"
git check-ignore .history
```

Expected: key `<set>`, Base URL `https://ai-gateway.vercel.sh`, one active assignment each, and `.history` is printed by `git check-ignore`.

### Task 3: Replace the English customopenai guide

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Rewrite the generic provider section around a tested Vercel example**

The section must contain these subsections and facts:

```markdown
## Vercel AI Gateway through `customopenai`

### 1. Create the connection

Use `https://ai-gateway.vercel.sh` without `/v1` and keep the key only in `.env`.

### 2. Configure one model

Use this complete model-selection example and explain every field that users may customize:

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

### 3. Add multiple models to one connection

Show `meta/muse-spark-1.1` and `kwaipilot/kat-coder-air-v2.5` as two complete objects inside one `models` array. Explain that all entries share the same key and Base URL, `match` must be the exact upstream ID, `display_name` should be unique, and lower `priority` values appear first.

### 4. Split a large model list across files

Show `customopenai-coding.json` and `customopenai-general.json`, both with `"provider": "customopenai"`. Explain that files are merged, duplicate `match` values must be avoided, and this does not create separate credentials.

### 5. Restart and verify

Show `/v1/models`, `/api/tags`, non-streaming Chat Completions, streaming Chat Completions, and Visual Studio/Copilot settings.
```

- [ ] **Step 2: Keep unrelated English README content unchanged**

Run `git diff -- README.md` and confirm every changed line belongs to the custom provider guide or its provider-table wording.

### Task 4: Bring the Chinese customopenai guide to parity

**Files:**
- Modify: `README.zh-CN.md`

- [ ] **Step 1: Write the equivalent Chinese tutorial**

Use the same structure and examples as Task 3:

```markdown
## 通过 `customopenai` 使用 Vercel AI Gateway

### 1. 创建连接
### 2. 配置一个模型
### 3. 在同一连接中添加多个模型
### 4. 将较长的模型列表拆分为多个文件
### 5. 重启并验证
```

Explain in clear Chinese that one `customopenai` connection can contain many models, while different API keys/Base URLs require separate provider support and are not created by splitting JSON files.

- [ ] **Step 2: Enforce the requested exclusions**

Run case-insensitive searches over both READMEs. Expected: no new discussion of HTTP 403, account top-ups, paid credits, or troubleshooting for that restriction.

- [ ] **Step 3: Verify English/Chinese example parity**

Check that both READMEs contain the same Base URL, environment variables, model ID, single-file multi-model example, split-file example, and four verification commands/settings.

### Task 5: Full regression and live proxy verification

**Files:**
- Verify: all files modified above

- [ ] **Step 1: Run formatting and JSON checks**

Run:

```powershell
git diff --check
Get-Content -Raw config/model-selection/customopenai.json | ConvertFrom-Json | Out-Null
```

Expected: exit code 0.

- [ ] **Step 2: Run the full test suite**

Run:

```powershell
dotnet test tests/ProxyTests/ProxyTests.csproj --no-restore
```

Expected: all tests pass. Pre-existing compiler deprecation warnings may remain.

- [ ] **Step 3: Start the proxy and verify discovery**

Run `dotnet run --no-build`, wait for port 11434, then request:

```powershell
Invoke-RestMethod http://localhost:11434/v1/models
Invoke-RestMethod http://localhost:11434/api/tags
```

Expected: both outputs contain `meta/muse-spark-1.1`, `meta/muse-spark-1.1@customopenai`, `Muse Spark 1.1`, and `Muse Spark 1.1@customopenai` where the endpoint exposes aliases.

- [ ] **Step 4: Verify a minimal request through the local proxy**

Send a non-streaming request with model `Muse Spark 1.1@customopenai`, prompt `Reply OK.`, and `max_tokens: 32`. Expected: HTTP 200 and response headers identifying `customopenai` and upstream model `meta/muse-spark-1.1`.

- [ ] **Step 5: Stop only the diagnostic process started in Step 3**

Resolve the listener PID on port 11434, verify its process name is `ai-proxy-hub`, and stop that exact process.

- [ ] **Step 6: Review the final diff against the approved design**

Confirm that no unrelated user changes were reverted, no secret appears in tracked files, and all approved README requirements are covered.
