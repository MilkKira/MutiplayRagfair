# 跨服跳蚤市场互通插件——技术设计文档

文档状态：草案 v0.2  
作者：Mochix2Milk  
日期：2026-08-18

## 1. 设计结论

采用“**每服一个 SPT 服务端模组 + 单写 Hub + 本地报价投影 + Saga 事务 + JSON 事件日志**”架构，不使用 SQLite 或其他数据库引擎。

不采用简单的 A/B 报价列表互相复制。SPT 的成交链路会在买家服查找卖家档案并完成本地结算；远端卖家档案不存在于买家服，直接复制会导致错误结算。共享市场必须将“买家扣款/收货”和“原服卖家结算”拆成两个受事务 ID 约束的步骤。

基线已锁定为 SPT `4.0.13-RELEASE+2891fd4.20260302.2891fd41fd07b6150a2192ac0d24adb93eb72862`、程序集版本 `4.0.13.0`、目标框架 `.NETCoreApp v9.0`。这些信息来自工作区 `ref/` 中的实际 DLL，而不是由其他版本文档推断。

## 2. 部署拓扑

```text
公网 HTTPS

SPT A + CrossRagfair(Node + Hub + JSON Journal)
                         ▲
                         │ 双向长连接
                         ▼
SPT B + CrossRagfair(Node)
```

v1 在 A 的 SPT 插件进程内启用 Hub 模式，B 以 Node 模式经公网连接 A。A/Hub 是已接受的单点：Hub 不可用时 B 可继续展示短期缓存，但不得购买、撤单、续期或发布共享报价。

B 主动通过 HTTPS 注册短租约，并使用带超时的 HTTP 长轮询接收 Hub 的报价锁定请求。这样 B 无需向公网开放回调端口。原服轮询中断、确认超时或租约过期后，Hub 拒绝购买该原服报价。

后续可将相同 Hub 核心托管为独立进程，不改变节点协议。

## 3. 项目与包结构

```text
src/
  CrossRagfair.Contracts/       # DTO、协议版本、签名规范；不引用 SPT
  CrossRagfair.Core/            # 状态机、幂等、映射、对账；不引用 SPT
  CrossRagfair.Spt/             # SPT 4.0.13 精确版本适配、DI、Harmony
  CrossRagfair.Hub/             # HTTP API、JSON 事件存储、Outbox
tests/
  CrossRagfair.Core.Tests/
  CrossRagfair.IntegrationTests/
```

插件安装目录：

```text
user/mods/CrossRagfair/
  CrossRagfair.Spt.dll
  CrossRagfair.Core.dll
  CrossRagfair.Contracts.dll
  CrossRagfair.Hub.dll
  config.json
  LICENSE
  data/
    hub/                        # Hub 快照和追加日志，仅 A 使用
    node/                       # 本服 Outbox、Saga、Inbox 和游标
```

所有 `SPTarkov.*`、`SPT.Server.*`、Harmony 和框架 DLL 均 `Private=false`，不随包分发。持久化仅使用 .NET 9 自带的 `System.Text.Json`、`System.IO`、`System.Threading.Channels` 和 `System.Security.Cryptography`；不发布 `Microsoft.Data.Sqlite`、`SQLitePCLRaw` 或任何原生数据库库。元数据、项目和程序集作者统一为 `Mochix2Milk`。

## 4. 核心组件

### 4.1 SPT 节点适配层

- `ListingCaptureAdapter`：在原生报价成功创建并保存后写入本地 Outbox。
- `RemoteOfferProjectionService`：拉取 Hub 增量，将远端报价放入本服内存报价池。
- `RemotePurchaseCoordinator`：识别远端报价并执行预占、买家本地变更、保存、提交或补偿。
- `OriginSettlementWorker`：轮询属于本服的成交/撤单/到期事件，调用原生报价完成逻辑。
- `SharedOfferLifecycleGuard`：阻止 SPT 的模拟买家自动成交共享玩家报价，并协调撤单、续期和到期。
- `SptVersionAdapter`：封装所有随 SPT 版本变化的类型和方法；业务核心不得直接反射 SPT 私有状态。

### 4.2 Hub

- `ListingRepository`：权威报价状态和剩余数量。
- `ReservationService`：原子预占、TTL、提交和释放。
- `PeerOutbox`：为各原服保存至少一次投递的结算事件。
- `IdentityMap`：`(serverId, localOfferId/profileId)` 到 24 位全局 ID 的持久映射。
- `CompatibilityRegistry`：节点版本与市场兼容哈希。
- `ReconciliationService`：摘要对账、孤儿检测和告警。
- `OriginCommandBroker`：维护按原服隔离的 HTTP 长轮询命令队列、在线租约和锁定请求/响应关联。

### 4.3 本地持久层

Node 与 Hub 均使用“JSON 快照 + 追加式 JSONL 事务日志”。Hub 只有一个写入者：所有修改命令进入 `Channel<HubCommand>`，由单一后台循环串行完成校验、日志追加、刷盘和内存状态更新。因此库存的检查与预占在逻辑上是原子操作，不依赖文件级并发写入。

持久化文件：

```text
data/
  hub/
    hub.snapshot.json
    hub.events.jsonl
    hub.lock
  node/
    node.snapshot.json
    node.events.jsonl
    node.lock
```

每条 JSONL 记录至少包含：

- `sequence`：严格递增序号。
- `eventId` 和 `transactionId`：投递与幂等键。
- `eventType`：状态转换类型。
- `timestamp`：UTC 时间。
- `payload`：报价、预占、事务或游标数据。
- `previousHash`：上一条完整记录的 SHA-256。
- `hash`：规范化记录内容的 SHA-256。

写入顺序固定为：在单写循环中验证当前状态，构造事件，向 JSONL 追加完整一行和换行符，调用 `FileStream.Flush(flushToDisk: true)`，再应用到内存状态并返回成功。若刷盘失败，不改变内存状态，也不向调用方确认。

快照使用临时文件写入并刷盘，然后通过同目录原子替换切换为正式文件。快照保存 `lastSequence` 和 `lastHash`；启动时加载快照，只重放序号更大的日志。只有新快照完成替换后才允许轮转旧日志。`hub.lock`/`node.lock` 使用独占文件句柄，阻止同一数据目录被两个进程同时打开。

恢复规则：末尾缺少换行、JSON 不完整或哈希不完整的最后一条记录视为断电残留，可忽略并隔离保存；任何中间记录损坏、序号跳跃或哈希链断裂都必须失败关闭共享写操作，禁止从远端投影反推权威库存。

## 5. SPT 集成方案

### 5.1 已从目标 DLL 确认的接口

- 元数据：一个具体 `AbstractModMetadata`；目标加载器为 `SPTarkov.Server.Modding.ModDllLoader`。
- 生命周期：`IOnLoad.OnLoad() -> Task`、`IOnUpdate.OnUpdate(long) -> Task<bool>`，本版本没有路由级 `CancellationToken`。
- 搜索：`RagfairController.GetOffers(MongoId, SearchRequestData) -> GetOffersResult`。
- 发布：`RagfairController.AddPlayerOffer(PmcData, AddOfferRequestData, MongoId) -> ItemEventRouterResponse`。
- 撤销/续期：`FlagOfferForRemoval(MongoId, MongoId)`、`ExtendOffer(ExtendOfferRequestData, MongoId)`。
- 购买：`TradeController.ConfirmRagfairTrading(PmcData, ProcessRagfairTradeRequestData, MongoId) -> ItemEventRouterResponse`。
- 原服结算：`RagfairOfferHelper.CompleteOffer(MongoId, RagfairOffer, int) -> ItemEventRouterResponse`。
- 报价池：`RagfairOfferService.AddOffer/GetOffers/GetOfferByOfferId/ReduceOfferQuantity/RemoveOfferById`。
- 档案持久化：`SaveServer.SaveProfileAsync(MongoId) -> Task<long>`。
- 已确认以上目标均为实例方法；Harmony 解析必须使用列出的精确参数类型。

### 5.2 首选扩展策略

1. 通过 `[Injectable]` 注册配置、后台同步器、Hub 客户端和生命周期服务；使用 SPT 4.0.13 的 `OnLoadOrder` 常量安排加载顺序。
2. 通过公开的 `RagfairOfferService` API维护远端投影，让原生过滤、排序、分类和分页继续工作。
3. 只有原生扩展点无法区分远端报价时才使用 Harmony：
   - 对远端购买在 `ConfirmRagfairTrading` 前缀中完成 Hub 预占和 `BuyerApplying` 转换，然后复用原方法完成买家扣款与收货；后缀保存买家档案并提交 Hub。本地报价完全走原流程。
   - 对发布使用后缀捕获新建报价，但实际发布必须等待档案保存成功。
   - 对共享报价的模拟成交、撤单、续期和到期应用最小范围补丁。
4. 所有补丁使用精确参数类型解析，逐个注册和记录；必需目标缺失时关闭互通功能，但不破坏 SPT 服务端启动。

`ConfirmRagfairTrading` 在目标 DLL 中是同步实例方法，因此 v1 的 Harmony 接管会执行有严格超时的同步 Hub 协调。不得无限阻塞 SPT 请求线程。目标版本的 `TradeCallbacks.ProcessRagfairTrade` 同样同步，因此不存在可以直接复用的异步购买扩展点。

### 5.3 投影规则

- Node 只导入 `originServerId != localServerId` 的报价，避免自己的报价重复。
- 投影使用 Hub 分配的全局报价 ID和全局卖家 ID；物品树 ID同样重映射并保持父子引用。
- 原始 `originOfferId`、`originProfileId` 不下发给 EFT 客户端，只保存在节点侧映射和 Hub。
- 投影进入原生报价池前校验模板、槽位、父子关系、数量和支付条件；缺失模板的报价隔离并告警。
- 节点维护 `globalOfferId -> RemoteOfferMetadata` 旁路字典，不能仅依赖 `RagfairOffer.User` 猜测远端身份。

## 6. 状态模型

### 6.1 报价状态

```text
LOCAL_PENDING -> ACTIVE -> RESERVED -> ACTIVE
                         \-> SOLD_OUT
ACTIVE -> CANCELLED
ACTIVE -> EXPIRED
```

- `LOCAL_PENDING` 只存在于原服 Outbox，Hub 尚不可见。
- `RESERVED` 可以有多个不超过总库存的预占记录；展示数量为总库存减有效预占。
- `CANCELLED`、`EXPIRED`、`SOLD_OUT` 为终态。

### 6.2 购买 Saga

```text
CREATED -> RESERVED -> BUYER_APPLIED -> BUYER_SAVED -> COMMITTED
                  \-> ABORTED
COMMITTED -> ORIGIN_EVENTED -> ORIGIN_APPLIED
```

每个转换均以事务 ID幂等。Hub 只有在 `BUYER_SAVED` 后接受 `COMMIT`。原服结算是最终一致步骤，不阻塞买家已成功的购买响应。

## 7. 关键流程

### 7.1 发布

1. 客户端调用原生 Add Offer。
2. SPT 完成合法性校验、扣税、物品托管并生成本地报价。
3. 补丁记录候选报价；档案保存成功钩子将 `PUBLISH` 写入本地 Outbox。
4. Worker 将完整快照发送 Hub；Hub 生成全局 ID并返回。
5. 另一节点按游标拉取增量并加入内存报价池。
6. Hub 确认前报价不得在远端出售。

### 7.2 远端购买

1. Harmony 前缀检查请求中的报价 ID；全部为本地则放行原方法。
2. 发现远端报价后生成事务 ID，校验不允许本地/远端混合批次。
3. 调用 Hub `reserve`；Hub 先检查原服在线租约，再通过原服的 HTTPS 长轮询通道要求其验证本地报价并建立销售锁。
4. 原服确认后，Hub 的单写命令循环检查状态和数量，将预占事件追加并刷盘后更新内存状态；原服离线、超时或拒绝时立即失败。
5. 节点依据预占返回的不可变快照，调用目标版本验证过的原生付款和库存服务构造 `ItemEventRouterResponse`。
6. 调用 `SaveServer.SaveProfileAsync(sessionID)` 并确认成功；本地 Saga 写为 `BUYER_SAVED`。
7. 调用 Hub `commit`。Hub 扣减权威库存并写入原服 Outbox。
8. 返回成功。若 5/6 失败，调用 `abort` 并释放原服锁；若 commit 响应丢失，按事务 ID查询状态，不得重新发货。

恢复规则：启动时逐笔检查未终结 Saga。`RESERVED` 可安全释放；`BUYER_SAVED` 必须查询 Hub 并提交同一事务；`COMMITTED` 只确认结果，不再应用买家变更。

### 7.3 原服结算

1. 原服按游标拉取成交事件。
2. 以事务 ID检查本地 Inbox，已应用则直接 ACK。
3. 通过映射找到本地报价和卖家档案。
4. 调用经目标版本验证的原生 `CompleteOffer` 路径，处理数量、收款邮件和声望。
5. 保存档案，将 Inbox 标记为已应用，然后 ACK Hub。

### 7.4 撤单/续期/到期

- 撤单：先由 Hub CAS 从 `ACTIVE` 改为 `CANCELLED`；若存在有效预占则返回冲突。成功后原服才执行原生退物。
- 续期：原服先执行原生扣费与续期；成功后将新结束时间以版本 CAS 写入 Hub。Hub 暂时忙或不可达时，节点周期性比较原生结束时间并重试，因此远端最多暂时按较早时间下架，不会把已到期库存继续出售。
- 到期：Hub 在节点心跳/写命令时统一判时；有在途购买则延后终结。原服的 `ProcessStaleOffer` 对仍被 Hub 标记为活动的共享报价延后处理，Hub 投影终结并同步回原服后再走 SPT 原生退物逻辑。

## 8. JSON 状态模型

Hub 快照至少包含：

- `peers`：协议、SPT 版本、兼容哈希和最近在线状态。
- `offers`：全局/原服 ID、卖家、物品快照、总量、可用量、预占量、状态、版本和期限。
- `reservations`：事务、报价、买家服、数量、快照哈希、状态和过期时间。
- `peerOutbox`：待原服消费的成交、撤单和到期事件。
- `idMap`：本地 ID到全局 24 位 ID的持久映射。
- `nonces`：防重放窗口内的节点随机数。

Node 快照至少包含：

- `localOutbox`：待发布或待重试的本地事件。
- `purchaseSagas`：买家事务当前状态和请求哈希。
- `originInbox`：已经应用或正在应用的原服事件。
- `projectionCursors`：各远端投影和事件游标。

JSON 快照只是加速恢复的派生状态；追加日志才是状态转换证据。库存预占在 Hub 单写循环内执行 `state == ACTIVE && available - reserved >= requested` 条件判断，成功事件刷盘后才对外确认。

## 9. 节点协议

建议 API：

- `POST /api/v1/peers/register`
- `GET /api/v1/origin/commands/next`（原服 HTTPS 长轮询锁定命令）
- `POST /api/v1/offers/publish`
- `POST /api/v1/offers/{id}/reserve`
- `POST /api/v1/transactions/{txId}/commit`
- `POST /api/v1/transactions/{txId}/abort`
- `GET /api/v1/transactions/{txId}`
- `POST /api/v1/offers/{id}/cancel`
- `POST /api/v1/offers/{id}/extend`
- `GET /api/v1/projections?cursor=...`
- `GET /api/v1/events?cursor=...`
- `POST /api/v1/events/{eventId}/ack`

所有写请求在请求体中携带幂等键。签名串包含 HTTP 方法、路径、时间戳、随机数和请求体 SHA-256；使用每节点独立的 HMAC-SHA256 密钥。Hub 拒绝超出时间窗或重复随机数的请求，并校验鉴权节点与请求中的买家服/原服身份一致。A、B 不在同一局域网，因此生产部署强制 HTTPS；A 仅开放 Hub 端口，建议配置域名、受信任证书、防火墙限流和固定 IP 白名单（如果 B 的出口地址稳定）。

## 10. 配置草案

```json
{
  "enabled": true,
  "mode": "hub-and-node",
  "serverId": "server-a",
  "hubUrl": "https://10.0.0.10:7443",
  "sharedSecret": "use-an-external-secret-or-protected-file",
  "syncIntervalSeconds": 2,
  "requestTimeoutMilliseconds": 2000,
  "reservationTtlSeconds": 15,
  "failMode": "closed",
  "allowCurrencies": ["RUB", "USD", "EUR"],
  "requireOriginOnline": true,
  "originLeaseSeconds": 10,
  "persistence": {
    "snapshotIntervalEvents": 1000,
    "maxJournalBytes": 67108864,
    "flushToDisk": true,
    "retainRotatedJournals": 3
  },
  "readOnly": false,
  "dryRun": false
}
```

真实实现不应鼓励明文长期密钥；优先允许从环境变量或单独受限文件读取。任何非 RUB/USD/EUR 的 Requirement 都拒绝进入共享市场。

## 11. 故障与补偿

- **Hub 超时**：查询同一事务 ID；未知结果时禁止重试本地发货。
- **预占后买家失败**：显式 abort；节点崩溃则由 TTL释放。
- **买家已保存、commit 响应丢失**：恢复任务查询并以同一事务 ID提交。
- **原服离线**：在线租约失效后禁止新预占；已经 `COMMITTED` 的事务由 Hub 保留结算事件，原服恢复后幂等处理。
- **投影落后**：购买仍以 Hub 预占为准，过期投影只会收到售罄错误。
- **日志尾部不完整**：忽略并隔离最后一条未完整记录，从最后一条通过哈希校验的记录恢复。
- **快照或日志中段损坏**：停止所有共享写操作，保留原文件用于诊断；禁止从投影反推权威库存。
- **版本不兼容**：隔离节点，不加载远端投影。

## 12. 测试策略

### 单元测试

- 报价和 Saga 状态机的合法/非法转换。
- ID 映射、物品树 ID重写、签名和防重放。
- 并发预占、幂等 publish/commit/abort/ack。
- JSONL 序号、哈希链、末尾截断、快照重放和日志轮转。

### 集成测试

- 两个临时 Node + Hub + 临时 JSON 数据目录。
- A/B 同时争抢最后库存。
- 在每个状态转换后模拟进程终止并验证恢复。
- 分别在追加前、追加后未刷盘、刷盘后、快照替换前后模拟进程终止。
- 延迟、丢包、重复请求、乱序事件和时钟偏差。
- 单件、多件、整包、武器子物品树和三种货币。

### SPT 运行测试

- 在一次性 SPT 副本和测试档案上验证元数据发现、DI、补丁目标、发布、搜索、购买、撤单、到期、邮件和声望。
- Release 构建零错误；检查发布目录不包含 SPT/EFT/Unity/BepInEx/Harmony DLL。
- 对每个必需 Harmony 目标在启动时输出已验证签名，缺失时安全关闭互通。

## 13. 分阶段实施

1. **版本取证**：取得 A/B DLL 和运行配置，记录精确版本、目标框架、路由与保存服务签名。
2. **只读原型**：发布快照、Hub 存储、B/A 远端投影和搜索；禁止购买。
3. **货币购买**：实现预占、买家保存、提交、原服结算和崩溃恢复。
4. **完整生命周期**：部分成交、撤单、续期、到期和声望。
5. **公网硬化与打包**：TLS/WSS、鉴权、限流、对账、排空模式、Release 包和安装文档。

## 14. 主要风险

- 本插件只支持工作区 DLL 对应的 SPT 4.0.13；升级 SPT 必须重新检查签名、构建和回归。
- 玩家档案保存的精确时序决定购买 Saga 的落点，是编码前最重要的源码/程序集验证项。
- SPT 本地模拟成交必须对共享报价关闭，否则会与真实跨服成交竞争。
- 两服模组/物品模板不一致会导致无法展示或生成物品，因此兼容哈希必须是硬门槛。
- 自定义 JSON 日志没有数据库引擎代为保证事务性，因此单写队列、真实落盘、哈希链、原子快照替换和故障注入测试均为不可删除的正确性要求。
- 内嵌 Hub 是单点，但比双主互相复制更容易保证不双卖；高可用应作为独立后续项目。

## 15. 官方依据

- 工作区目标程序集：`ref/SPT.Server.dll`、`ref/SPTarkov.DI.dll`、`ref/SPTarkov.Server.Core.dll`。
- 工作区 API 文档：`ref/SPTarkov.Server.Core.xml`、`ref/SPTarkov.DI.xml`。
- SPT C# 服务端仓库：<https://github.com/sp-tarkov/server-csharp>
