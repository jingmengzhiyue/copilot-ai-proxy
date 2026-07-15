# Kimi CN Provider Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a first-class Kimi China provider backed by `https://api.moonshot.cn`, preserve the existing Moonshot international provider, and document a complete custom OpenAI-compatible model workflow.

**Architecture:** Register `kimi` in the existing capability-driven provider registry so it reuses the current OpenAI-compatible model discovery and chat forwarding path. Keep Kimi China model metadata in a separate allowlist and preserve Moonshot precedence for shared bare model IDs while supporting exact `model@kimi` routing. Update both README languages and the example environment file without introducing a new transport abstraction.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, xUnit, JSON model-selection files, Markdown documentation.

---

## File map

- Modify `Services/ProviderCapabilitiesRegistry.cs`: register the Kimi China endpoint and environment prefix.
- Create `config/model-selection/kimi.json`: curate Kimi China coding models and execution metadata.
- Modify `tests/ProxyTests/ProviderRegistryTests.cs`: cover Kimi capabilities and environment-driven discovery.
- Modify `tests/ProxyTests/ModelSelectionStoreTests.cs`: cover Kimi allowlist loading and model capabilities.
- Modify `tests/ProxyTests/RoutingDiagnosticTests.cs`: cover Moonshot/Kimi collision behavior and exact `@kimi` routing.
- Modify `.env.example`: expose optional Kimi China configuration.
- Modify `README.md`: document Kimi China and expand the custom provider tutorial.
- Modify `README.zh-CN.md`: provide the same workflow in Simplified Chinese.

### Task 1: Register the Kimi China provider

**Files:**
- Modify: `tests/ProxyTests/ProviderRegistryTests.cs`
- Modify: `Services/ProviderCapabilitiesRegistry.cs`

- [ ] **Step 1: Add failing capability and discovery tests**

Add the Kimi row to `ProviderCapabilitiesRegistry_OpenAiCompatibleProviders_AreRegistered`:

```csharp
[InlineData("kimi", "KIMI", "https://api.moonshot.cn", "v1/chat/completions", "v1/models")]
```

Add this isolated discovery test to `ProviderRegistryTests`:

```csharp
[Fact]
public void DiscoverProviders_KimiApiKey_RegistersChinaEndpointWithoutMoonshot()
{
    string? oldKimiKey = Environment.GetEnvironmentVariable("PROVIDER_KIMI_API_KEY");
    string? oldKimiBase = Environment.GetEnvironmentVariable("PROVIDER_KIMI_BASE_URL");
    string? oldMoonshotKey = Environment.GetEnvironmentVariable("PROVIDER_MOONSHOT_API_KEY");

    try
    {
        Environment.SetEnvironmentVariable("PROVIDER_KIMI_API_KEY", "test-kimi-key");
        Environment.SetEnvironmentVariable("PROVIDER_KIMI_BASE_URL", null);
        Environment.SetEnvironmentVariable("PROVIDER_MOONSHOT_API_KEY", null);

        ProviderRegistry registry = new(new ProviderHttpClientFactory());

        ProviderInfo kimi = Assert.Single(registry.Providers.Where(p => p.Name == "kimi"));
        Assert.Equal("https://api.moonshot.cn", kimi.BaseUrl);
        Assert.DoesNotContain(registry.Providers, p => p.Name == "moonshot");
    }
    finally
    {
        Environment.SetEnvironmentVariable("PROVIDER_KIMI_API_KEY", oldKimiKey);
        Environment.SetEnvironmentVariable("PROVIDER_KIMI_BASE_URL", oldKimiBase);
        Environment.SetEnvironmentVariable("PROVIDER_MOONSHOT_API_KEY", oldMoonshotKey);
    }
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test --filter "FullyQualifiedName~ProviderRegistryTests"
```

Expected: the new theory row fails with `Unknown provider: 'kimi'`; the discovery test cannot find a provider named `kimi`.

- [ ] **Step 3: Add the minimal registry entry after `moonshot`**

Insert into `ProviderCapabilitiesRegistry._capabilities` immediately after the existing `moonshot` entry:

```csharp
["kimi"] = new(
    Category: ProviderCategory.Direct,
    ApiFormat: ApiFormat.OpenAi,
    SupportsReasoningEffort: false,
    SupportsTopK: false,
    ChatPath: "v1/chat/completions",
    ModelsPath: "v1/models",
    DefaultBaseUrl: "https://api.moonshot.cn",
    EnvPrefix: "KIMI"),
```

Keeping this entry after `moonshot` preserves the existing provider as the tie-break winner for shared bare model IDs.

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run:

```powershell
dotnet test --filter "FullyQualifiedName~ProviderRegistryTests"
```

Expected: all `ProviderRegistryTests` pass.

- [ ] **Step 5: Commit the provider registration**

```powershell
git add Services/ProviderCapabilitiesRegistry.cs tests/ProxyTests/ProviderRegistryTests.cs
git commit -m "feat: register Kimi China provider"
```

### Task 2: Add the Kimi China model allowlist

**Files:**
- Modify: `tests/ProxyTests/ModelSelectionStoreTests.cs`
- Modify: `tests/ProxyTests/RoutingDiagnosticTests.cs`
- Create: `config/model-selection/kimi.json`

- [ ] **Step 1: Add failing model-selection tests**

Add `kimi` to `ProviderModelSelections_HasAllProviders`:

```csharp
Assert.True(store.ProviderModelSelections.ContainsKey("kimi"));
```

Add a focused test for the primary coding model:

```csharp
[Fact]
public void FindModelSelectionEntry_Kimi_K27Code_HasOfficialCapabilities()
{
    ModelSelectionStore store = new();

    ModelSelectionEntry? entry = store.FindModelSelectionEntry("kimi-k2.7-code", "kimi");

    Assert.NotNull(entry);
    Assert.Equal(262_144, entry.Value.Execution.ContextLength!.Value);
    Assert.True(entry.Value.Execution.SupportsTools ?? false);
    Assert.True(entry.Value.Execution.SupportsVision ?? false);
    Assert.True(entry.Value.Execution.SupportsReasoning ?? false);
    Assert.True(entry.Value.Execution.OverrideClientParams);
    Assert.Equal(1.0, entry.Value.Execution.Temperature);
}
```

Add a test ensuring the high-speed model is curated independently:

```csharp
[Fact]
public void FindModelSelectionEntry_Kimi_K27HighSpeed_FindsEntry()
{
    ModelSelectionStore store = new();

    ModelSelectionEntry? entry = store.FindModelSelectionEntry("kimi-k2.7-code-highspeed", "kimi");

    Assert.NotNull(entry);
    Assert.Equal(2, entry.Value.Priority);
}
```

Add this test beside the other qualified-alias diagnostics in `RoutingDiagnosticTests`:

```csharp
[Fact]
public async Task KimiAndMoonshot_QualifiedKimiAlias_RoutesOnlyToChinaProvider()
{
    (ModelCatalogService catalog, ProviderRegistry registry, _) =
        BuildCatalog(new Dictionary<string, string[]>
        {
            ["moonshot"] = ["kimi-k2.7-code"],
            ["kimi"] = ["kimi-k2.7-code"],
        });

    await catalog.RefreshAsync();

    IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> bare =
        registry.ResolveCandidates("kimi-k2.7-code");
    IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> qualified =
        registry.ResolveCandidates("kimi-k2.7-code@kimi");

    Assert.Equal("moonshot", bare[0].Provider.Name);
    Assert.Single(qualified);
    Assert.Equal("kimi", qualified[0].Provider.Name);
    Assert.Equal("kimi-k2.7-code", qualified[0].UpstreamModel);
}
```

- [ ] **Step 2: Run the model-selection tests and verify RED**

Run both affected suites:

```powershell
dotnet test --filter "FullyQualifiedName~ModelSelectionStoreTests|FullyQualifiedName~KimiAndMoonshot_QualifiedKimiAlias_RoutesOnlyToChinaProvider"
```

Expected: model-selection failures report that the `kimi` selection dictionary and entries do not exist; the routing test cannot construct the qualified Kimi mapping because no Kimi model is allowlisted.

- [ ] **Step 3: Create the minimal Kimi model configuration**

Create `config/model-selection/kimi.json`:

```json
{
  "provider": "kimi",
  "models": [
    {
      "match": "kimi-k2.7-code",
      "priority": 1,
      "enabled": true,
      "execution": {
        "context_length": 262144,
        "max_output_tokens": 262144,
        "supports_tools": true,
        "supports_vision": true,
        "supports_reasoning": true,
        "family": "kimi",
        "temperature": 1.0,
        "top_p": 0.95,
        "max_tokens": 4096,
        "timeout_seconds": 60,
        "override_client_params": true
      }
    },
    {
      "match": "kimi-k2.7-code-highspeed",
      "priority": 2,
      "enabled": true,
      "execution": {
        "context_length": 262144,
        "max_output_tokens": 262144,
        "supports_tools": true,
        "supports_vision": true,
        "supports_reasoning": true,
        "family": "kimi",
        "temperature": 1.0,
        "top_p": 0.95,
        "max_tokens": 4096,
        "timeout_seconds": 60,
        "override_client_params": true
      }
    },
    {
      "match": "kimi-k2.6",
      "priority": 3,
      "enabled": true,
      "execution": {
        "context_length": 262144,
        "max_output_tokens": 262144,
        "supports_tools": true,
        "supports_vision": true,
        "supports_reasoning": true,
        "family": "kimi",
        "temperature": 1.0,
        "top_p": 0.95,
        "max_tokens": 4096,
        "timeout_seconds": 60,
        "override_client_params": true
      }
    },
    {
      "match": "kimi-k2.5",
      "priority": 4,
      "enabled": true,
      "execution": {
        "context_length": 262144,
        "max_output_tokens": 262144,
        "supports_tools": true,
        "supports_vision": true,
        "supports_reasoning": true,
        "family": "kimi",
        "temperature": 1.0,
        "top_p": 0.95,
        "max_tokens": 4096,
        "timeout_seconds": 60,
        "override_client_params": true
      }
    }
  ]
}
```

- [ ] **Step 4: Run the model-selection tests and verify GREEN**

Run:

```powershell
dotnet test --filter "FullyQualifiedName~ModelSelectionStoreTests|FullyQualifiedName~KimiAndMoonshot_QualifiedKimiAlias_RoutesOnlyToChinaProvider"
```

Expected: all selected model and routing tests pass. The bare model resolves to Moonshot, while `kimi-k2.7-code@kimi` resolves only to Kimi China.

- [ ] **Step 5: Commit the allowlist**

```powershell
git add config/model-selection/kimi.json tests/ProxyTests/ModelSelectionStoreTests.cs tests/ProxyTests/RoutingDiagnosticTests.cs
git commit -m "feat: add Kimi China coding models"
```

### Task 3: Document Kimi China configuration

**Files:**
- Modify: `.env.example`
- Modify: `README.md`
- Modify: `README.zh-CN.md`

- [ ] **Step 1: Update the environment example**

Add `kimi` to the supported-provider comment and place this block immediately after Moonshot:

```bash
# Kimi China (platform.kimi.com; keys are not interchangeable with Moonshot international):
#PROVIDER_KIMI_API_KEY=sk-your-kimi-cn-key
# PROVIDER_KIMI_BASE_URL defaults to https://api.moonshot.cn
```

- [ ] **Step 2: Add Kimi China to both provider tables and model-file lists**

English provider row:

```markdown
| `kimi` | Kimi China | OpenAI-compatible | `https://api.moonshot.cn` | `PROVIDER_KIMI_API_KEY` |
```

Chinese provider row:

```markdown
| `kimi` | Kimi 国内开放平台 | `https://api.moonshot.cn` | `PROVIDER_KIMI_API_KEY` |
```

Add `config/model-selection/kimi.json` to the relevant model-file examples.

- [ ] **Step 3: Add a complete Kimi China usage section in both languages**

The section must show:

```bash
PROVIDER_KIMI_API_KEY=sk-your-kimi-cn-key
# Optional; this is the default:
PROVIDER_KIMI_BASE_URL=https://api.moonshot.cn
```

and a proxy request that pins the domestic endpoint:

```bash
curl http://localhost:11434/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "kimi-k2.7-code@kimi",
    "messages": [
      { "role": "user", "content": "Write a C# binary search function." }
    ],
    "stream": true
  }'
```

State explicitly that `PROVIDER_KIMI_API_KEY` and `PROVIDER_MOONSHOT_API_KEY` belong to different platforms and that `@kimi` is recommended when both are enabled.

- [ ] **Step 4: Review the Kimi documentation changes**

Run:

```powershell
rg -n "PROVIDER_KIMI|api\.moonshot\.cn|@kimi|kimi\.json" .env.example README.md README.zh-CN.md
```

Expected: each concept appears in the environment example and both README languages, with no replacement of `api.moonshot.ai`.

- [ ] **Step 5: Commit the Kimi documentation**

```powershell
git add .env.example README.md README.zh-CN.md
git commit -m "docs: explain Kimi China configuration"
```

### Task 4: Expand the custom OpenAI-compatible provider tutorial

**Files:**
- Modify: `README.md`
- Modify: `README.zh-CN.md`

- [ ] **Step 1: Replace the short English `customopenai` section with an executable workflow**

Use the fictional provider `https://api.example-provider.com` and model `example-coder-32b`. Include these exact stages:

1. Verify the upstream contract (`GET /v1/models`, `POST /v1/chat/completions`, Bearer authentication).
2. Query the upstream model catalog:

```bash
curl https://api.example-provider.com/v1/models \
  -H "Authorization: Bearer your-provider-key"
```

3. Configure the API root without a trailing `/v1`:

```bash
PROVIDER_CUSTOMOPENAI_API_KEY=your-provider-key
PROVIDER_CUSTOMOPENAI_BASE_URL=https://api.example-provider.com
```

4. Replace `config/model-selection/customopenai.json` with a complete example:

```json
{
  "provider": "customopenai",
  "models": [
    {
      "match": "example-coder-32b",
      "display_name": "Example Coder",
      "priority": 1,
      "enabled": true,
      "execution": {
        "context_length": 131072,
        "max_output_tokens": 16384,
        "supports_tools": true,
        "supports_vision": false,
        "family": "example",
        "temperature": 0.2,
        "max_tokens": 8192,
        "timeout_seconds": 180
      }
    }
  ]
}
```

5. Restart, verify `GET /v1/models` and `GET /api/tags`, then send a chat request using `Example Coder@customopenai`.
6. Configure Visual Studio/Copilot against `http://localhost:11434` using the exact model name returned by the proxy.
7. Diagnose routing with `X-Proxy-Provider`, `X-Proxy-Upstream-Model`, and `X-Proxy-Resolved-Model`.
8. Troubleshoot 401, duplicate `/v1/v1`, missing model IDs, disabled allowlist entries, and forgotten restarts.

- [ ] **Step 2: Mirror the workflow in Simplified Chinese**

Use the same commands, JSON, field values, validation endpoints, and troubleshooting cases. Translate explanations only; do not let the two READMEs diverge technically.

- [ ] **Step 3: Validate Markdown structure and example consistency**

Run:

```powershell
rg -n "example-coder-32b|Example Coder@customopenai|/v1/v1|X-Proxy-Upstream-Model" README.md README.zh-CN.md
git diff --check
```

Expected: all four tutorial concepts appear in both README files and `git diff --check` reports no whitespace errors.

- [ ] **Step 4: Commit the expanded tutorial**

```powershell
git add README.md README.zh-CN.md
git commit -m "docs: detail custom model provider setup"
```

### Task 5: Final verification

**Files:**
- Verify all modified files.

- [ ] **Step 1: Run the focused provider and model tests**

```powershell
dotnet test --filter "FullyQualifiedName~ProviderRegistryTests|FullyQualifiedName~ModelSelectionStoreTests|FullyQualifiedName~RoutingDiagnosticTests"
```

Expected: all selected tests pass with zero failures.

- [ ] **Step 2: Run the complete test suite**

```powershell
dotnet test --no-restore --verbosity minimal
```

Expected: all tests pass with zero failures. Record any pre-existing compiler warning separately rather than claiming warning-free output.

- [ ] **Step 3: Build the application**

```powershell
dotnet build --no-restore
```

Expected: exit code 0 and zero build errors.

- [ ] **Step 4: Validate JSON and inspect the final diff**

```powershell
Get-Content config/model-selection/kimi.json -Raw | ConvertFrom-Json | Out-Null
git diff --check develop...HEAD
git status --short
git diff --stat develop...HEAD
```

Expected: Kimi JSON parses, the diff has no whitespace errors, and only the planned files are changed.

- [ ] **Step 5: Review requirements line by line**

Confirm from the final diff that:

- Kimi China uses its own `PROVIDER_KIMI_*` settings and `api.moonshot.cn`.
- Existing Moonshot settings and `api.moonshot.ai` remain intact.
- The four curated Kimi models are available through `kimi.json` when returned by the live catalog.
- Exact `@kimi` routing is covered.
- Both README languages contain the detailed custom model tutorial.
- No real API key or credential was added.

- [ ] **Step 6: Create a final implementation commit only if verification produced uncommitted fixes**

```powershell
git add Services/ProviderCapabilitiesRegistry.cs config/model-selection/kimi.json tests/ProxyTests/ProviderRegistryTests.cs tests/ProxyTests/ModelSelectionStoreTests.cs tests/ProxyTests/RoutingDiagnosticTests.cs .env.example README.md README.zh-CN.md
git commit -m "feat: add Kimi China model interface"
```

Skip this commit when the working tree is already clean; do not create an empty commit.
