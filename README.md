# CrossRagfair

CrossRagfair 是面向 SPT 4.0.13 / .NET 9 的跨服跳蚤市场服务端插件，作者为 Mochix2Milk。

当前实现包含：

- A、B 玩家档案保持完全独立；本服报价由原生 SPT 显示，异服报价投影到本服原生报价池。
- 支持 RUB、USD、EUR，不共享以物易物或多支付条件报价。
- Hub 使用 HMAC-SHA256 鉴权、单写入命令队列、JSON 快照和带哈希链的 JSONL 日志；不依赖 SQLite 或数据库 DLL。
- 原服必须保持在线租约并确认本地库存锁，Hub 才允许预占；原服离线时不能购买其报价。
- 远程购买采用 `Reserved -> BuyerApplying -> BuyerSaved -> Committed` Saga。买家档案先持久化，再扣减 Hub 权威库存，卖家结算事件由原服幂等消费。
- 共享报价支持撤回、续期协调和 Hub 权威到期。有效购买事务持续占用库存，撤回/续期会拒绝与在途购买冲突的操作。
- Hub 可在 Linux x64/ARM64 上运行，只要求 ASP.NET Core Runtime 9。

当前仍应视为联调版本：自动化测试覆盖协议、并发库存、崩溃恢复和 JSON 日志，但必须先在 SPT 测试档案上完成发布、购买、卖家邮件、撤回、续期和到期的端到端验收，再用于长期存档。

## 构建和测试

```powershell
.\build-server-plugin.bat
.\build-windows-hub.bat
.\build-linux-hub.bat
```

三个脚本分别输出到 `Build/ServerPlugin`、`Build/WindowsHub` 和 `Build/LinuxHub`。Windows/Linux Hub 均为 self-contained 单文件发布包，会把 .NET 9 与 ASP.NET Core 运行库封装进可执行文件；`appsettings.json` 保持外置以便修改。默认目标分别为 `win-x64` 和 `linux-x64`。ARM64 可通过参数指定，例如 `build-windows-hub.bat win-arm64` 或 `build-linux-hub.bat linux-arm64`。

首次构建某个目标架构时，.NET SDK 可能需要联网下载对应的运行时包。

Hub 默认使用 `https://0.0.0.0:7443`，从运行目录加载无密码的 `certificate.pfx`，并仅启用 TLS 1.2/1.3。构建前将证书放在 `certs/certificate.pfx`，并把其不含私钥的公钥证书导出为 `certs/certificate.cer`。Hub 构建脚本会复制 PFX，服务端插件构建脚本会复制 CER。PFX 包含私钥且已被 Git 忽略，不应提交或分发给节点客户端；CER 可以安全分发给节点。

Hub 控制台日志默认等级为 `Information`，可通过 `appsettings.json` 中的 `CrossRagfairHub:LogLevel` 调整。支持 `Trace`、`Debug`、`Information`/`INFO`、`Warning`/`WARN`、`Error`、`Critical`/`FATAL` 和 `None`。每个请求会按 `时间 [线程] [等级] [客户端请求] 方法 路径 -> 状态码 (耗时)` 输出；只记录路径，不记录查询字符串、HMAC 签名或共享密钥。

SPT 项目严格绑定工作区 `ref/` 对应的 4.0.13 API。发布包不会包含 `SPTarkov.*`、`SPT.Server.*` 或 `0Harmony.dll`。

## SPT 节点配置

把以下文件放入 A、B 各自的 `user/mods/CrossRagfair/`：

```text
CrossRagfair.Spt.dll
CrossRagfair.Core.dll
CrossRagfair.Contracts.dll
config.json
certificate.cer
LICENSE
```

两端应设置不同的 `serverId`、相同的非空 `compatibilityHash`，并在各自的 `config.json` 中通过 `sharedSecret` 配置至少 32 字符的节点密钥。Hub 的 `PeerSecrets` 必须为每个 `serverId` 配置对应密钥。请限制 `config.json` 的文件访问权限，不要提交真实密钥。

插件默认从自身目录加载 `certificate.cer`（可通过 `hubCertificatePath` 指定其他路径），并固定其 SHA-256 指纹。精确匹配的 CER 作为该 Hub 的信任锚，因此无需操作系统信任其自签名证书；主机名不匹配、证书过期、CER 缺失或 Hub 更换证书都会使连接按 fail-closed 拒绝。

配置开关：

- `readOnly=true`：只接收投影，不向 Hub 发布本服报价。
- `readOnly=false`：发布本服玩家报价。
- `enablePurchases=true`：允许远程购买并启用原服库存确认与卖家结算。

正式联调时 A、B 都应使用 `readOnly=false` 和 `enablePurchases=true`。Hub 或原服不可达时交易按 fail-closed 拒绝。

## Linux Hub

```bash
chmod +x ./CrossRagfair.Hub
./CrossRagfair.Hub
```

推荐安装到 `/opt/crossragfair-hub`，数据目录设为 `/var/lib/crossragfair`。如使用 systemd，令 `ExecStart` 指向 `/opt/crossragfair-hub/CrossRagfair.Hub`。公网部署时证书必须包含实际访问域名，并受到各节点信任；不要使用仅适用于 `localhost` 的开发证书。

完整范围与内部方案见 [需求文档](docs/requirements.md) 和 [设计文档](docs/design.md)。
