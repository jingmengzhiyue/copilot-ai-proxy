# Routing audit implementation plan

**Goal:** Independently verify provider identity round trips and repair reproduced errors only.

**Architecture:** Keep ModelCatalogService.ResolveRoute as the endpoint routing boundary. Preserve legacy bare-model default fallback; pin generated Ollama identities to their originating provider. Keep exact upstream IDs ahead of compatibility parsing.

**Tech Stack:** C# / .NET 10, ASP.NET Core, xUnit, HTTP integration probes.

- [x] Add counterexamples to StrictRoutingTests: `k3:batch@kimicode` must fail, configured `k3:latest` must beat discovered `k3`, and explicit slash IDs must not match another vendor's suffix.
- [x] Run `dotnet test --filter FullyQualifiedName~StrictRouting --no-restore` and record failures before repairs.
- [x] Limit tag removal in ProviderRegistry/ModelCatalogService to terminal `:latest`; match exact IDs before suffix aliases.
- [x] Bind both Ollama tags name/model to providers; exercise the actual endpoint across all eight requested providers, checking exact upstream IDs.
- [x] Verify malformed JSON and strict errors on show/chat; avoid logging upstream error bodies that may echo prompts or credentials.
- [x] Run full build/test; distinguish baseline stale configuration assertions from new failures.
- [x] Start an isolated real proxy, test current tags, Kimi Code streaming/tool calls/large request, Hunyuan and negative routing. Save sanitized evidence and an eleven-part audit report.

No commits or unrelated refactoring. Existing uncommitted changes remain in place.
