# Night Raider 自制 MOD

《夜袭者 / Night Raider》的三个 BepInEx 插件，**分成三个互不依赖的安装包**，按需要挑着装。

| 包 | 内容 | 按键 | 适合谁 |
|---|---|---|---|
| **[仅刷新商店](releases/refresh-shop-only/)** | 只有刷新商店 | **F7** | 只想要商店、不要物品上色 |
| **[刷新商店 + 物品底色](releases/refresh-shop-tint/)** | 刷新商店 + 稀有度底色 | **F7** | 想要完整效果（推荐） |
| **[物品商店](releases/item-shop/)** | 稀有商品购物商店 | **F8** | 想要第二个商店 |

三个包**可以任意组合**：只装一个、装两个、或者三个都装都行，共用同一张稀有度表。

---

## 下载与安装

到 [`releases/`](releases/) 下选包，**解压整个压缩包**，然后**双击 `一键安装.bat`**：

```
releases/refresh-shop-only/NightRaider_RefreshShop_Only_v1.1.zip   →  只要 F7 刷新商店
releases/refresh-shop-tint/NightRaider_RefreshShop_Tint_v1.3.zip   →  F7 刷新商店 + 物品底色
releases/item-shop/NightRaider_ItemShop_v1.3.5.zip                 →  F8 物品商店
```

安装程序会自动找到游戏目录、备份、复制、校验，全程不用你判断。
**没装过 MOD 的人**：包里自带 BepInEx 5.4.23.5，装完直接能用，不需要事先准备任何东西。
卸载双击 `一键卸载.bat`。

### 和别的 MOD 不会冲突

三个包的安装程序都遵守同一条原则：**缺什么补什么，已有的东西一个字节都不动。**

| 已经存在的文件 | 安装程序的处理 |
|---|---|
| `winhttp.dll`、`BepInEx\core\` 运行库 | 已有 → **原样跳过**，绝不覆盖 |
| `BepInEx\plugins\` 里别的 MOD | 只往目录里**新增**本包的文件 |
| 稀有度表 `NightRaider_item_tiers.csv` | 已有 → 跳过，不会冲掉你在用的表 |
| 你自己改过的配置（按键、费用） | 不覆盖 |
| 同名文件被本程序覆盖的 | 一律**先备份**，可完整还原 |

卸载也是**外科手术式**的：只拿掉本 MOD 的文件，**绝不整目录删除 `BepInEx\`**
（这一点很关键 —— 整目录删除会连带干掉你之后装的其它 MOD）。
共用的稀有度表在还有别的 MOD 时会保留。

所以**先装哪个、后装哪个都没关系**，任意顺序、任意组合都不会互相破坏。

手动安装的话，把 `BepInEx/` 和 `winhttp.dll` / `doorstop_config.ini` / `.doorstop_version`
一起放进游戏根目录（`NightRaider.exe` 所在目录）。

---

## 功能说明

### 刷新商店（F7）

一个 10 张卡片的稀有商品柜台，价格按稀有度分级，花 **50000 GP** 可整页刷新。

| 档位 | 名字 | 颜色 | 倍率 | 权重 |
|---|---|---|---|---|
| 1 | 普通 | — | 不参与 | — |
| 2 | 优良 | 绿 | 15~25× | 39.6% |
| 3 | 稀有 | 蓝 | 20~30× | 29.7% |
| 4 | 史诗 | 紫 | 25~35× | 19.8% |
| 5 | 传说 | 橙 | 30~40× | 9.9% |
| 6 | **绝世** | 红 `FF3B30` | 35~45× | 1% |

价格 = 出售价 × 对应档位倍率（在倍率区间内随机）。弹药按**整叠**出售。买过的窗口显示「已售罄」。

### 物品稀有度底色

给物品格子加一层稀有度底色，**不改动图标本身**：

- 背包、A~L 仓库、各种箱子
- 人物身上的装备栏（9 格）
- 快捷栏（HUD 6 格 + 背包界面 6 格，用 3px 底边色条，不遮图标）
- **改装界面**：武器 7 个配件槽 + 装备/头盔 4 个配件槽 + 配件选择弹窗
- 悬停说明：物品名按稀有度上色，并显示 **【已锁定】/【已标记】/【已损坏】**
- 耐久归零的装备用**暗灰**（`#4A4A4A`），不再是游戏自带的大红

游戏内按 **F6** 可重新读取稀有度表。

### 物品商店（F8）

独立的稀有商品购物商店，商店行按稀有度上色。

---

## 稀有度表

[`tier-tables/NightRaider_item_tiers.csv`](tier-tables/) 是运行时用的档位表，**纯文本可编辑，改完不需要重新编译**：

```csv
modelId,tier,nameCn,nameEn,category,price
1001,3,AK-47,AK-47,WEAPON,12000
```

改 `tier` 列（1~6）即可。放到 `<游戏目录>\BepInEx\config\` 覆盖同名文件，游戏内按 **F6** 生效。

当前全表 **1373 个物品**，分布：普通 250 / 优良 378 / 稀有 314 / 史诗 267 / 传说 121 / **绝世 43**。

`tier-tables/` 下另有几份分类明细表（绝世档名单、工作台工具与材料、弹药投掷物等），
用于核对和微调档位。

---

## 从源码构建

三个插件都能从 [`src/`](src/) 重新编译：

```powershell
$env:NR_GAME_DIR = 'D:\Steam\steamapps\common\Night Raider\NightRaider'
dotnet build src\RareTint    -c Release
dotnet build src\RareRefresh -c Release
dotnet build src\RareShop    -c Release
```

需要游戏本体的 `Assembly-CSharp.dll` 和 `UnityEngine*.dll`，以及 BepInEx 的
`BepInEx.dll` / `0Harmony.dll` —— 这些**不在仓库里**（游戏与第三方版权文件）。
详细步骤见 **[BUILD.md](BUILD.md)**。

已验证：用上述方式构建三个插件，产物与 `releases/` 里发布的 DLL **字节完全一致**。

---

## 维护笔记

`tools/push-via-api.ps1` 是通过 **GitHub REST API** 提交的脚本
（blobs → tree → commit → ref），在 `github.com:443` 不可达、只能用
`api.github.com` 的环境下替代 `git push`：

```powershell
.\tools\push-via-api.ps1 -Repo owner/name -Branch main
```

正常情况下用普通 `git push` 即可，不需要这个脚本。

---

## 目录结构

```
├── releases/
│   ├── refresh-shop-only/   只有刷新商店（F7）
│   ├── refresh-shop-tint/   刷新商店 + 物品底色（F7）
│   └── item-shop/           物品商店（F8）
├── src/
│   ├── RareRefresh/         刷新商店（F7）
│   ├── RareTint/            物品底色
│   ├── RareShop/            物品商店（F8）
│   └── HireNpc/             雇请 NPC（制作中）
├── tier-tables/             稀有度表（运行时用 + 分类明细）
├── docs/                    详细说明与变更记录
├── tools/                   维护脚本
├── Directory.Build.props    共享构建配置（游戏路径在这里）
├── BUILD.md                 构建说明
└── LICENSE
```

每个包的详细说明见 `docs/` 与压缩包内的 `① 先看我 - 安装说明.txt`。

---

## 兼容性

- **游戏版本**：《夜袭者 / Night Raider》，Unity 2022.3.49f1c1，Mono，x64
- **BepInEx**：5.4.23.5（x64），包内自带
- 两个包之间无冲突；与其它 BepInEx 插件一般也不冲突

> 不要把 **F7** 和 **F8** 两个面板**同时开着** —— 它们打开时都会屏蔽游戏输入，会互相拦截点击。关一个再开另一个。

---

## 免责声明

本 MOD 用于个人娱乐使用，并进行免费的分享，但不对使用本作品可能导致的任何损害承担责任，
包括但不限于：游戏存档损坏或丢失、游戏崩溃、与其他模组的兼容性问题、因游戏版本更新导致的模组失效。

本仓库**不包含游戏本体的任何文件**，也不重新分发游戏程序集。使用时请自备正版游戏。

---

## 许可

本项目源码采用 [MIT 许可](LICENSE)。

发布包里附带 **BepInEx 5.4.23.5**，遵循其自身的 LGPL-2.1 许可，版权归 BepInEx 作者所有。
