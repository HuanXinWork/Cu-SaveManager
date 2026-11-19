# CuSaveManager 更新日志

> English changelog: see [changes.en.md](changes.en.md)

## 1.3.4

修复与 Casualties: Together 5.5.x 不兼容、客户端进房后卡在物种选择界面的问题。1.3.3 摘掉了 Together 挂在 `PreRunScript.StartRun` 上的 Prefix，并对联机客户端提示 “You can't start the game, server can.” 后拦截；而 Together 5.5 起客户端的 Join / Ready up 正是由这个 Prefix（`CharacterSelectWait.OnStartOrLoadClicked`）处理，客户端因此既不能准备，也不能中途加入。现在不再摘除：联机房间内只要 Together 带有 `CharacterSelectWait`，`StartRun` 就整个放行给 Together，主机开新局也随之改走 Together 的准备大厅。

未开房的单机态（装了 Together 但没有联机会话）改为在 Together 的 `StartRun` / `LoadRun` Prefix 上挂旁路守卫，开局与读档直接走原版流程，不再经过 Together 的单机拦截。

多人死亡自动回档的阈值 `MultiplayerDeathThreshold` 默认值由 1 改为 0（全员阵亡才触发），配置里已有的 1 会在升级后首次启动时一次性改为 0；需要「死 N 人就回档」的可以手动设回 1–16。死亡触发的倒计时期间若不再满足死亡条件（例如队友被救活），倒计时自动取消。死亡判定或回档进行期间先于 Together 拦截 `PlayerCamera.ToMainMenu`，避免 Together 抢先注销网络实体并让全员退房。

修复多人回档的几个问题：回档窗口期拦截 `ServerMain.LateSpawnLocation` 时补调 `PlayerCheckpoint.ReclaimGear`（1.3.3 整段跳过，远程玩家回档后拿不回检查点装备）；回档第一阶段退回主菜单时不再写入位置（会把回档前的现场写进刚还原的存档）；回到层底过层存档（`mp_rules.json` 的 `LEVEL=false`）时不再套用上一层出口的 `PLRPOS` 坐标；回档倒计时提示不再重复发给 P2P 主机自己。

联机客户端进图时不再套用本地的新局世界引擎设置，并关闭 QoL 的种子干预，世界生成跟随主机；退回主菜单时清除从主机收到的种子状态，也不再把房间里的位置写进本地存档。

适配 Together 5.5 存档新增的 `PLRCHARS`（玩家角色表）：回档重映射 persistentId 时与 `PLRPOS` 一并改写。特殊模式（`GamemodeManager.HasGamemode()`，如 BattleRoyale）进行中不做定时备份和死亡自动回档。

阵亡或回档进行中不再允许手动保存、定时备份与保存并退出，避免把死档写进备份；联机时全队到达层底（按 Together 的 `CheckIfEnoughPeopleAreAtLayerFinish` 判定）跳过定时备份。记录存档位置时不再采用已死亡角色的坐标，并跳过 QoL 后台自动存档触发的写入。

单机切换槽位读档时，只有槽位带位置信息且未安装 QoL 才把 `biome` 写回 `save.sv`，不再无条件改写。

固定种子的世界生成改为在协程每一步保存、恢复各生成阶段自己的随机数状态，不受生成期间其它随机数消耗的影响；联机时不包装 Together 的外层握手协程，以免覆盖刚与主机同步的随机数状态。

存档面板打开时拦截鼠标悬停与拖拽，不再穿透到面板下方。更新检查的版本号比较支持带 `-rc.1`、`+build` 后缀的标签，并统一按前三段比较。英文界面按钮文字 `Save && Exit` 改为 `Save & Exit`。

新增 `Together.ModHooks` / `TogetherPanelApi` 的可选对接：Together 提供这些接口时改走官方钩子，没有时（包括当前 Steam 版 5.5.3）沿用补丁路径。

本版还包含一轮全量代码审查的其它修复。

## 1.3.3

联机读档不再摘除 Together 的 `PreRunScript.LoadRun` Prefix，改为按「会话是否运行中」条件放行：联机时由 Together 走完整的角色选择与逐玩家读档流程，本模组只在单机（联机 mod 在场但未开房）时接管。`StartRun` 仍保留原接管（联机模组那侧的 Prefix 只会在单机时硬拦、在联机时额外打开 Ready 大厅），新开局体验不变。

修正对 HarmonyX 的误判：其 Prefix 返回值只汇总成「原方法是否执行」，不会阻止后续 Prefix 执行。因此 StartRun 不能靠 `return true` 放行，必须保持摘除；单机 LoadRun 会多一条联机模组的 “You can't play singleplayer” 提示，但世界已由本模组加载，不影响功能。

新增进世界前的角色兜底（`Together.WorldMP.LoadVanillaGeneratedWorld`）：主机侧把存档角色同时写回 `PreRunScript.characterSelected` 与 `ScavPlayer.LocalPlayer.selectedCharacter`，避免被 Together 的 `PlayerCamera.Awake` 覆盖成 Experiment。角色只从本地玩家自己的 `mp_save/<persistentId>` 读取，不再使用「遍历任意子目录取第一个」的回退，避免把别人的物种读成自己的。

新增读档补丁优先级自检：启动时打印 `LoadRun` / `StartRun` 上所有 Prefix 的执行顺序，排在联机模组之后会打警告。

sidecar 新增 `character` 字段作为角色第二来源：主存档 `character` 为 0 而 sidecar 非 0 时采用 sidecar，采信前校验 RunId 一致，避免旧局 sidecar 盖掉新局角色。

## 1.3.2

修复回档后角色被重置为 Experiment（Milky / Dune 同样生效）。

修复「当前存档」卡片在游戏内与读档后误显示「无存档」。

新增 Debug 构建的流程日志（读档 / 回档 / 存档 / 换档 / 种子，Release 不含）。

## 1.3.1

修复读档后角色不跟随存档（Milky / Dune 被重置为 Experiment）。

## 1.3.0

适配游戏正式版 1.0 与 Together 5.4.0。

联机桥从 KrokMP 迁移到 Together Interop 契约层：会话、通道、名册、类型注册全部走反射契约，不再直接引用联机模组程序集。

多人存档判定改按「联机会话是否运行中」：Together 只在 `Net.IsRunning` 时把游戏存档目录切到 `mp_save`。原判定按「联机模组已加载」，装了 Together 但单机游玩时会误判为多人会话，打包 `mp_save` 时找不到目录。

固定世界种子注入改挂 `Together.LastBeforeGenerationState` 构造，种子广播接管 `ServerMain.Server_AnnounceSeed`，回档位置写回改为等世界生成完成。

声明对 QoL Unknown 的软依赖，避免加载顺序导致首次启动时 QoL 成员全部解析失败。

新增 Debug 构建专用日志，Release 产物不含调试字符串。

## 1.2.1

兼容 Dedicated Server：回档时跳过 sidecar 主机坐标写回（专服没有本地玩家）。

修复多人回档位置包时序：改为等待世界生成完成（`Util.IsWorldGenerated`）后一次写回，替代固定帧数重试，避免位置包早于 LateSpawn 生命周期而被覆盖。

移除冗余的 PlaceBody 广播静音（KrokMP 新版已自带 `has_spawn_location` 守卫）。

工程：目标框架 net472 → net48；新增 `KrokoshaCasualtiesMP.dll` 直接引用（位置写回协程由反射改为强引用）；LangVersion 统一 latest 以兼容 CI 的 .NET 8 SDK。

## 1.2.0

修复抗辐射药等陷阱同名物品每次进下一层必消失的问题。

修复超级银河皮带/材料包等自定义大容量容器读档后内容物丢失或散落一地的问题。

## 1.1.8 & 1.1.9

修复「存档并退出」不在存档/回档列表里生成槽位的问题。

## 1.1.7

修复在层底（出现「进入下一层」按钮时）存档后读档跳过整层的问题。

## 1.1.6

**修复进层时层级编号不递增（单机 / 装了 KrokMP 的单人）。**

- 根因：游戏用「存档→读档」推进层级。`SaveSystem.SaveGame` 写入 `biome = biomeDepth + 1`（下一层），`TryLoadGame` 直接 `biomeDepth = biome`，因此在层底保存再继续即载入下一层。CuSaveManager 的「续档持久化」把 `biome` 字段改写成内存里 0 基的 `biomeDepth`，抹掉了这个 `+1`，于是读档又回到原层（层号始终不变）。该问题出现在单机（以及装了 KrokMP 但未开房）场景——此时进层走的是 `SaveGame`→`LoadGame`，正对应「走到底点下一层却还是当前层」。自 1.1.3 起就存在，移除本 mod 即恢复正常。
- 修复：在层底保存（玩家位于底端＝进下一层）时保留游戏写入的 `biome = biomeDepth + 1`，不再规范回当前层；仅层中保存（原位续玩）才做 biome 规范化。主菜单 Continue 路径拿不到场内玩家位置，那里多余的 biome 改写已移除，避免再次撤销进层。多人 `mp_save` 规范化同样加了层底保护。

## 1.1.5

本 mod 自身固定世界引擎（self）确定化补强：补全协程内全部同步执行点的重置。

- 背景：协程世界生成阶段（`IEnumerator`，如 `WorldGenerateTerrain`/`WorldPlaceEntities`/`WorldGenerateStructures`）的 Harmony Prefix 只在协程对象创建时（首个 `yield` 之前）执行一次，真正抽随机发生在 `yield` 之后的帧里，期间 `UnityEngine.Random` 可能被其它系统打乱 → 仅阶段级重置不可靠，地形/实体可能无法按种子复现。
- 新增对协程内**同步执行**子方法的逐一即时重置：`FastNoiseLite` 构造函数（地形/洞穴噪声，按 `_noiseGenStep` 递增）、`DistributeEntities`（敌人/陷阱/箱子/尸体等分布，按名字 hash+参数+调用序号）、`PlaceLiquids`、`GenerateLifePods`/`GenerateDropCapsules`/`GenerateCollapsedPods`、`ApplyLayerModifiers`（与读档还原补丁共存）。
- 进层/重生成/清理时复位计数器与种子：`ContinueRun`、`RegenerateWorld`、`Clear`，并在 `GenerateWorld`/`WorldGenerateTerrain` 阶段开头复位噪声计数。
- 战利品确定化：`TraderScript.GenerateInventory`、`Openable.OnUse`（前后保存/还原 `Random.state` 避免污染全局流）。
- **修复选 self 引擎进下一层“卡层”（多人主机）**：进层时游戏先在内存递增 `totalTraveled`，但磁盘 save.sv 仍为上一层旧值；`MpWorldSeedInjector` 原先只读磁盘旧值，导致每进一层都用上一层种子重算 → 重生成与所在层相同的地图。改为取内存/磁盘两者最大值（读档时用磁盘值，进层时用内存递增值），与 `SeededWorldPatcher.LayerSeed` 保持一致。
- 存档/读档一致路径（原生存档 + 各还原补丁）不变。

## 1.1.4

仅装 KrokMP 联机 mod（未装 QoL）时，与 KrokMP 同开会出现三个问题，本版修复：

- **地图模式恒为默认**：本 mod 接管 `PreRunScript.StartRun` 后丢掉了 KrokMP 原补丁里的 `WorldGeneration.runSettings = 所选预设` 写入，导致主机开房时荒凉/无芯片/自定义等地图模式被 `WorldGeneration.Awake` 回落到 normal 预设。已在接管逻辑里补回该写入。
- **每次重开都是同一种子（地图与上一次相同）**：默认 `PreferredEngine=qol`，未装 QoL 时旧逻辑回落到本 mod 的「确定化世界」引擎并强制注入固定种子；且 `EnsureFreshSeed` 在已激活时直接返回，跨局复用上一局种子。现默认行为改为：KrokMP 在场即交还其原生世界生成（随机地图）；本 mod 固定世界引擎改为显式 opt-in（面板里手动选「本 mod」）。`EnsureFreshSeed` 每次新开局重新取种子。
- **无法进入下一层（卡在当前层）**：确定化引擎在进层时按存档里旧的 `totalTraveled` 复算种子，重新生成与所在层相同的世界，表现为“走到底端选下一层仍是同一层”。随上一条交还 KrokMP 原生进层后修复。

## 1.1.2

- 主面板侧栏新增「关于」分页：与 SkinSync 同款居中标题 + 链接按钮 + 名字按钮风格，列出版本号 / 仓库 / 最新发布 / 作者 / 依赖。
- 设置 →「其他」区段加「界面语言」三档切换（自动 / 中文 / English）；写入配置文件 `I18n.PreferredLanguage`，重启游戏后保留。语言判定升级为三层兜底（配置 → 游戏 Locale 关键词识别 → PlayerPrefs）。
- 与其他嵌入侧栏的 mod 统一半透明底色（HwAssistive 之前底色明显偏深，本轮对齐到 saveManager 标准），切换侧栏分页时主区底色不再跳变。

## 1.1.1

- 修复读档/回档时游戏在第 1 层重新派发起始物资（应急灯或灯笼+狗粮+水瓶+垃圾袋）的问题；游戏 `WorldGeneration.WorldPlacePlayer` 自身没有读档守卫，每次回档第 1 层都会按 `runSettings["startingsupplies"]` 重发。新增「读档时不重发起始物资」开关（设置 → 其他，默认开启），关闭后回到游戏原版行为。

## 1.1.0

- 主面板按屏幕分辨率整体等比缩放，超大设计尺寸不再超出屏幕；保留边距、可拖动，命中检测随缩放校正。
- 读档 / 回档时从存档恢复游戏难度（RunSettings），修复单机与多人重新读档后自定义难度变默认的问题。
- 修复面板缩放后右上角 X 关闭图标显示异常。

## 1.0.9

- 修复 `ImGuiImeRecovery` 在面板关闭后仍因外部 IMGUI（如 KrokMP 多人连接页）有焦点就清 `FocusControl`，导致 IP/用户名字段无法输入的问题；现仅在面板关闭时 `RequestClear()` 才回收焦点。

## 1.0.8

- 修复在面板文本框（昵称、种子等）用过中文输入法之后，本模组快捷键和快捷栏按键都不响应的问题。关面板后会清掉 IMGUI 残留的键盘焦点。

## 1.0.7

围绕"固定世界 + 回档"这条主线做了一轮系统性整理：

- **固定世界引擎二选一**：面板可选「QoL（优先）」或「本 mod」。选 QoL 时把世界确定化交给 QoL；选本 mod 时由自带引擎接管并暂时禁用 QoL 的世界介入。未安装 QoL 时「QoL」按钮禁用，只能用本 mod 引擎。
- **多人支持**：多人存档按 KrokMP 的 `mp_save` 目录整体打包为槽位；回档由主机两阶段重载（返回主菜单 → 自动重新加载），全员重连进入备份点。
- **统一日志**：所有日志收敛到统一出口，可在设置里开启「在游戏控制台显示模组日志」，按 ` 键打开的控制台即可查看。
- **更新检测**：启动时比对 GitHub 最新版本，有新版在屏幕左上角红字提示；可在设置里关闭。
