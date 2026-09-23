# 审计修复执行计划

**目标：** 保留 issue.md 原始审计，处理 FINAL-001–030、B-001–005、C-001–007，消除 CS8600 并验证请求摘要日志。

**方案：** 在现有 Minimal API、服务和 ILogger 边界做根因修复；三个并行子任务分别负责协议、目录配置、工程建议，主任务负责缓存边界、启动隔离、日志和最终集成。遵循用户授权在当前工作区执行，不提交、推送或部署。

**技术：** .NET 10、xUnit、ASP.NET Core、PowerShell/Python 离线模拟上游。

- [x] 基线：读取完整审计、适用指令和当前 diff；保存 issue.md 原始字节；Release 编译记录既有警告；隔离环境执行原有测试。
- [x] 隐私：停止无会话身份的生产缓存读写；内部缓存限制条目/字节/寿命；验证普通消息与重复工具 ID 不串用推理。
- [x] 协议：真实转换函数和端点复现路径、工具、图像、字段类型、超时、流终止与候选异常；最小修复后运行专用回归。
- [x] 目录：验证双 Ollama、禁用覆盖、静态列表、profile 和并发刷新；修复 nullable 数据流并验证空目录退避。
- [x] 启动：进程环境优先、Testing 不读 .env；测试夹具屏蔽所有继承 provider 并恢复；后台发现不阻塞监听。
- [x] 日志：请求上下文只保存模型/提供商/状态等元数据；一次完成摘要，异常追加安全分类和尝试路径；降低两个框架生命周期类别到 Warning。
- [x] 工程：修复基准排名和模型身份；核查官方契约与 Docker 可用性；按收益处理 C 类，明确未采纳和验证限制。
- [x] 集成：全量测试、Release 分析构建、实际 Kestrel + loopback 上游验证成功/失败/fallback/流式日志；复核 diff 和敏感数据边界。
- [x] 收尾：只追加 issue.md 逐项结论和证据，核对原始字节前缀不变；简短报告实际结果及限制。

验证命令：`dotnet test ai-proxy-hub.sln -c Release`、`dotnet build ai-proxy-hub.sln -c Release --no-restore -p:EnableNETAnalyzers=true -p:AnalysisLevel=latest`。运行测试不使用真实上游密钥或项目 .env；原审计记录的 469 项测试是历史基线，最终本轮 573 项通过，新增测试必须调用生产实现。
