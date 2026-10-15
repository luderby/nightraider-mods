# 构建说明（Build）

这个仓库里 **`releases/` 下的压缩包是开箱即用的**，不需要构建。
本文件只说明**如何从源码重新编译**这三个 MOD。

---

## 1. 为什么要看这个文件

源码编译需要四类文件，它们**不在本仓库里**，因为是游戏本体或第三方加载器的文件：

| 需要的东西 | 从哪来 |
|---|---|
| `Assembly-CSharp.dll` | 游戏本体 `NightRaider_Data\Managed\` |
| `UnityEngine*.dll`（若干） | 同上 |
| `BepInEx.dll` | [BepInEx 5.4.x](https://github.com/BepInEx/BepInEx/releases)（x64） |
| `0Harmony.dll` | 同上（BepInEx core 自带） |

仓库里不含这些，是为了不重新分发游戏的受版权保护的程序集。

---

## 2. 指定游戏目录

工程默认找 `..\NightRaider`（即仓库的上一级），所以下面两种方式都可以：

**方式 A：设环境变量**

```powershell
$env:NR_GAME_DIR = 'D:\Steam\steamapps\common\Night Raider\NightRaider'
dotnet build src\RareTint -c Release
```

**方式 B：改 `Directory.Build.props`**

```xml
<NrGameDir>D:\Steam\steamapps\common\Night Raider\NightRaider</NrGameDir>
```

---

## 3. BepInEx 引用程序集放在哪

构建时按以下顺序查找 `BepInEx.dll` 和 `0Harmony.dll`：

1. `<游戏目录>\BepInEx\core\` —— 游戏已经装过 BepInEx 时直接可用
2. `<仓库>\libs\BepInEx\core\` —— 你也可以手工建这个目录放进去
3. `<仓库>\releases\refresh-shop-tint\NightRaider_RefreshShop_Tint_v1.3\BepInEx\core\`
   —— 把发布包**就地解压**后可用

三种都找不到时构建会失败，报错里会提示 `BepInEx.dll not found`。

---

## 4. 编译

```powershell
dotnet build src\RareRefresh -c Release
dotnet build src\RareTint    -c Release
dotnet build src\RareShop    -c Release
```

产物在各自的 `bin\Release\` 下。如果 `<游戏目录>\BepInEx\plugins\` 存在，
构建后会**自动把插件复制过去**并打印一行 `Installed plugin -> ...`。

目标框架是 **`netstandard2.1`**，因为游戏是 Unity 2022.3 + Mono。

---

## 5. 档位表

`tier-tables\NightRaider_item_tiers.csv` 就是运行时用的稀有度表，是**纯文本可编辑**的，
不需要重新编译 MOD。改完放进 `<游戏目录>\BepInEx\config\` 即可
（游戏内按 **F6** 可让底色 MOD 重读该表）。

格式：

```csv
modelId,tier,nameCn,nameEn,category,price
1001,3,AK-47,AK-47,WEAPON,12000
```

| 列 | 含义 |
|---|---|
| `modelId` | 游戏内物品 ID |
| `tier` | 稀有度 1~6（1 普通 / 2 优良 / 3 稀有 / 4 史诗 / 5 传说 / **6 绝世**） |
| `nameCn` / `nameEn` | 名字，仅用于生成提示，MOD 运行时主要用 ID |
| `category` | 分类标签，用于文档与统计 |
| `price` | 原价；出售价 = `ceil(price × 0.5)`，MOD 按档位倍率定价 |

改档位只需改 `tier` 列。
