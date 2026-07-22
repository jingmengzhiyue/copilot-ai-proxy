# Model Configuration Audit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add official MiniMax and Tencent Hunyuan providers, support the correct output-limit request field, and correct only model parameters that can be verified from current official documentation.

**Architecture:** Keep `max_output_tokens` as internal model metadata exposed by the Ollama-compatible catalog. Add a separate `MaxCompletionTokensPreferred` request default so each provider configuration can choose exactly one upstream request field: `max_tokens` or `max_completion_tokens`. Provider discovery remains registry-driven; MiniMax and Hunyuan therefore require one registry entry and one curated JSON file each.

**Tech Stack:** .NET 10, C#, `System.Text.Json`, xUnit, JSON model-selection files.

---

### Task 1: Request output-limit schema

**Files:**
- Modify: `Models/ModelExecutionConfig.cs`
- Modify: `Services/ModelSelectionStore.cs`
- Modify: `Services/RequestTransformer.cs`
- Test: `tests/ProxyTests/ModelSelectionStoreTests.cs`
- Test: `tests/ProxyTests/RequestTransformerTests.cs`

- [ ] Add a failing store test asserting that a MiniMax entry loads `max_completion_tokens` into `MaxCompletionTokensPreferred`.
- [ ] Add failing transformer tests asserting that:
  - a configured `max_completion_tokens` is injected;
  - client `max_tokens` suppresses configured `max_completion_tokens`;
  - client `max_completion_tokens` suppresses configured `max_tokens`;
  - the transformed request never contains both fields.
- [ ] Run the focused tests and confirm that the new property/behavior is absent.
- [ ] Add `int? MaxCompletionTokensPreferred` to `ModelExecutionConfig`.
- [ ] Parse JSON `max_completion_tokens` in `ModelSelectionStore`.
- [ ] Update `ApplyExecutionDefaults` to treat both output-limit request fields as one mutually exclusive group while preserving client values.
- [ ] Re-run the focused tests and confirm they pass.

### Task 2: MiniMax and Tencent Hunyuan providers

**Files:**
- Modify: `Services/ProviderCapabilitiesRegistry.cs`
- Create: `config/model-selection/minimax.json`
- Create: `config/model-selection/hunyuan.json`
- Modify: `.env.example`
- Modify: `README.md`
- Modify: `README.zh-CN.md`
- Test: `tests/ProxyTests/ProviderRegistryTests.cs`
- Test: `tests/ProxyTests/ModelSelectionStoreTests.cs`

- [ ] Add failing registry tests for:
  - MiniMax: `MINIMAX`, `https://api.minimax.io`, `v1/chat/completions`, `v1/models`;
  - Hunyuan: `HUNYUAN`, `https://tokenhub.tencentmaas.com`, `v1/chat/completions`, `v1/models`, reasoning effort enabled.
- [ ] Add failing selection tests for MiniMax M3/M2.7 Highspeed and Hunyuan Hy3.
- [ ] Register both direct OpenAI-compatible providers.
- [ ] Add only `MiniMax-M3`, `MiniMax-M2.7-highspeed`, and `hy3`, using official context/output/tool/vision/reasoning metadata.
- [ ] Add commented environment examples and provider-table documentation.
- [ ] Replace the Kimi key accidentally present in `.env.example` with a non-secret placeholder.
- [ ] Re-run the focused registry and selection tests.

### Task 3: Verified direct-provider corrections

**Files:**
- Modify: `config/model-selection/deepseek.json`
- Modify: `config/model-selection/kimi.json`
- Modify: `config/model-selection/moonshot.json`
- Modify: `config/model-selection/google.json`
- Modify: `config/model-selection/groq.json`
- Modify: `config/model-selection/cerebras.json`
- Modify: `config/model-selection/openai.json`
- Modify: `Services/ProviderCapabilitiesRegistry.cs`
- Test: `tests/ProxyTests/ParameterValidationTests.cs`
- Test: `tests/ProxyTests/ModelSelectionStoreTests.cs`

- [ ] Update stale assertions first, using official values:
  - DeepSeek V4: 1,000,000 context and 384,000 output; reasoning levels `high`/`max`;
  - Kimi fixed sampling where officially required, without forcing an arbitrary request token limit;
  - Moonshot vision flags and sampling constraints;
  - Gemini Pro context to 1,048,576 and omit explicit sampling for Gemini 3.x;
  - Groq Llama 4 Scout to 131,072 context, 8,192 output, vision enabled;
  - Cerebras GLM 4.7 and GPT-OSS output limits and recommended sampling;
  - OpenAI/Groq/Cerebras request limits to `max_completion_tokens`.
- [ ] Run focused tests and record the expected failures against current JSON.
- [ ] Apply only the verified JSON and capability-registry corrections.
- [ ] Re-run the focused tests.

### Task 4: Verified aggregator and Ollama corrections

**Files:**
- Modify: `config/model-selection/nvidia.json`
- Modify: `config/model-selection/openrouter.json`
- Modify: `config/model-selection/ollamacloud.json`
- Modify: `Services/ProviderCapabilitiesRegistry.cs`
- Modify: `Endpoints/OllamaEndpoints.cs`
- Test: `tests/ProxyTests/ParameterValidationTests.cs`
- Test: `tests/ProxyTests/EndpointTests.cs`

- [ ] Add/update assertions for NVIDIA Nemotron sampling, OpenRouter Qwen Coder output, Ollama Cloud Qwen context, and provider capability flags.
- [ ] Add a failing Ollama request-conversion test proving `top_k` maps to native `options.top_k`.
- [ ] Apply the verified JSON corrections and enable `top_k` for Ollama.
- [ ] Map OpenAI-compatible `top_k` into Ollama native options without changing unrelated request fields.
- [ ] Re-run focused tests.

### Task 5: Full verification

**Files:**
- Review all changed files.

- [ ] Parse every `config/model-selection/*.json` file.
- [ ] Search enabled entries for simultaneous `max_tokens` and `max_completion_tokens`.
- [ ] Run `dotnet test tests/ProxyTests/ProxyTests.csproj --no-restore`.
- [ ] Review `git diff --check`, `git diff --stat`, and the full diff.
- [ ] Confirm that pre-existing unrelated worktree changes were preserved and report any test limitation honestly.
