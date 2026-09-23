# 审计修复验证说明

所有运行验证使用合成凭证和本地模拟上游，不读取真实 `.env`，不执行真实模型计费调用。

## 可重放检查

```powershell
dotnet test ai-proxy-hub.sln -c Release --no-restore
dotnet build ai-proxy-hub.sln -c Release --no-restore -t:Rebuild -p:EnableNETAnalyzers=true -p:AnalysisLevel=latest
python tests/runtime-log-probe.py
python tests/benchmark_script_tests.py
powershell -NoProfile -File tests/benchmark-ranking.tests.ps1
git diff HEAD --check
```

`runtime-log-probe.py` 从 Release 输出复制最少程序集到临时目录，写入合成模型配置并清理子进程继承的提供商环境。它实际启动 Kestrel 和两个 loopback 上游，验证目录尚未完成时两个入口的请求、正常请求、流式转换、503、未知提供商、默认 fallback、候选切换、30 秒慢请求的日志；校验每个响应的 `X-Request-ID` 恰好对应一条摘要，并拒绝凭证/正文标记和重复生命周期日志。正常结束会关闭自己创建的进程与临时目录。

## 日志约定

`Proxy.Requests` 使用现有 ILogger 结构化日志。正常请求只输出一条 Information 摘要：Id、Requested、Normalized、Final、Provider、Fallback、Status、ElapsedMs、Stream、Outcome。Final/Provider 是实际最后尝试的上游，尚未发送请求时为 `-`。

失败、路由异常、默认 fallback、非流式候选切换或耗时至少 30 秒时使用 Warning，同一摘要额外带 Attempts、Candidates、Error、Failures、Slow、ResponseStarted。Fallback 包括默认模型回退和实际候选切换；失败历史保留上游状态和分类，不包含上游响应正文。已开始的流失败可能 HTTP 状态仍为 200，此时 Outcome=failed，不能仅按状态判定成功。总耗时包含流读取和下游写入，不是首 token 延迟。

`Microsoft.AspNetCore.Hosting.Diagnostics`、`Microsoft.AspNetCore.Routing.EndpointMiddleware` 默认最低 Warning，Warning/Error 未屏蔽。未引入额外的 token/usage/TTFT 解析，不改变流式不重试的契约。

## 限制

测试不覆盖所有真实厂商模型、真实 Copilot/VS 客户端、Docker 镜像、在线漏洞库及长时间压力。既有认证测试使用 WebHostBuilder，完整重建仍报告 8 处 ASPDEPR004；未禁用告警，也未为消除此提示重写无关测试宿主。

## 最终结果（2026-09-23）

全套 573 项通过，0 失败、0 跳过；Release 强制分析重建 0 错误，只有测试工程原有的 8 处 ASPDEPR004，生产 CS8600 已消除。Python 基准脚本测试 1 项、PowerShell 排名 3 组通过。实际 Kestrel 探针：startup_ms=631、model_requests=10、summaries=10；30 秒慢请求得到 Warning，候选切换记录最终 openai 与 Attempts=2，正常请求只有单条摘要。完整逐项结论和实际日志见项目根 issue.md 末尾修复追加。
