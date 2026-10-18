<p align="center">
  <img src="assets/stages/halyard-day.webp" alt="黄金时刻的 Halyard Marina" width="100%">
</p>

<h1 align="center">INKWAVE</h1>

<p align="center">
  <a href="README.md">English</a> · <b>简体中文</b>
</p>

<p align="center">
  一款原创的 Splatoon 风格 4v4 占地对战射击游戏（Unity 版本）。<br>
  涂满地面，在自己的墨水里潜行，把对手的涂地面积比下去。
</p>

<p align="center">
  <a href="#本地运行">本地运行</a> ·
  <a href="#操作">操作</a> ·
  <a href="#实现原理">实现原理</a> ·
  <a href="CONTRIBUTING.md">参与贡献</a>
</p>

<p align="center">
  <a href="https://github.com/Blagii/inkwave/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/Blagii/inkwave/actions/workflows/ci.yml/badge.svg"></a>
  <img alt="Unity 6" src="https://img.shields.io/badge/Unity-6000.0-000000?logo=unity&logoColor=white">
  <a href="LICENSE"><img alt="MIT" src="https://img.shields.io/badge/license-MIT-blue"></a>
</p>

> **游戏已迁移到 Unity**（`unity/INKWAVE`）：玩法、地图、数值完全不变，three.js 与 Electron 已从产品中移除。
> 详情见 [`docs/UNITY-PORT.md`](docs/UNITY-PORT.md)。

---

## 特色

- **多种 4 v 4 模式。** 占地对战（涂地面积多者获胜）与区域控制（守住活动区域，从 100 倒数——侧区会轮换，还有罚时和加时）。可与三个难度档位的机器人对战。
- **与好友联机。** 创建私人房间，分享五位字符的房间码，最多八名玩家带着各自的装备和外观进入大厅。空位由机器人补齐；中途有人掉线，机器人会接管他的乌贼小子。
- **乌贼形态。** 按住即可潜入自己的墨水：快速游动、补充墨罐、攀爬已涂的墙面、在墨水上跃过水面缺口。
- **十二种武器**，手感各不相同：Spritzer、Twinfire Pistols、Canopy Brolly（霰弹枪 + 可发射护盾）、Popper Blaster、Squall Spinner、Glint Charger、Tideline Bow（三箭齐发、两段蓄力）、Swell Roller、Swish Brush、Brine Cutlass（蓄力一击斩）、Sponge Mitts（墨拳、蓄力跳跃、攀墙）以及 Bilge Bucket。任意主武器都可搭配 15 种副武器与 19 种大招。
- **七张地图，白天或黄昏。** Tidewater Plaza、Kelpline Terminal、Halyard Marina、Saltpan Basin、Crossroads Market、Lockgate Canals 与 Terrace Heights，每张都是真实地点，布局各异。部分地图在区域控制模式下会更换少量构件。
- **像液体一样的墨水。** 墨点会扩散并沉降，新墨有光泽而逐渐干燥，墨迹会沿墙流淌，游动时水面本身也会留下尾迹。
- **真正能看懂的地图。** 按住 <kbd>Tab</kbd>，镜头会升起变成实时战场的一幅微缩模型视角，标有队友位置，并可一键超级跳跃。
- **衣柜系统。** 自定义你的乌贼小子：触手发型、头部装备、面部、服装。
- **全部程序化生成。** 角色、动画、武器、贴图、道具、音效和音乐全部由代码生成。除两款字体外没有任何下载资源。

<p align="center">
  <img src="assets/stages/tidewater-day.webp" width="49%" alt="Tidewater Plaza">
  <img src="assets/stages/kelpline-dusk.webp" width="49%" alt="黄昏时的 Kelpline Terminal">
</p>

## 操作

| 动作 | 键盘 / 鼠标 | 手柄 |
|---|---|---|
| 移动 | <kbd>W</kbd> <kbd>A</kbd> <kbd>S</kbd> <kbd>D</kbd> | 左摇杆 |
| 瞄准 | 鼠标 | 右摇杆 |
| 开火 | 鼠标左键 | RT |
| 乌贼形态 | <kbd>Shift</kbd> | LT |
| 跳跃 / 翻滚闪避 | <kbd>Space</kbd> | A |
| 副武器（炸弹） | 鼠标右键 / <kbd>E</kbd> | RB |
| 大招 | <kbd>F</kbd> | Y |
| 地图 + 超级跳跃 | 按住 <kbd>Tab</kbd> 或 <kbd>M</kbd>，然后按 <kbd>1</kbd>–<kbd>4</kbd> 或点击标记 | View |
| 暂停 | <kbd>Esc</kbd> | Start |

手柄在托管（https）版本上可用。在普通的 `http://` 局域网地址下，浏览器会屏蔽 Gamepad API。

## 联机对战

在主菜单选择 **Online**，然后选择 **Create a room** 并把房间码发给好友（或选择 **Join a room** 输入对方的房间码）。
房主决定地图、昼夜时段、对局时长以及是否用机器人补位；其他人在各自选好队伍、武器和外观后准备。
阵容、表情动作和准备状态对房间内所有人实时同步。

房间运行在一个轻量中继上（Cloudflare Worker，每个房间一个 Durable Object，位于 [`server/`](server)）。它只转发
消息：每名玩家模拟自己的乌贼小子并广播状态，其他人则通过同一套动画系统在约 0.1 秒延迟的平滑时间线上绘制。
具体原理以及用于测量它的工具见 [`docs/NET.md`](docs/NET.md)。

要在自己的网络上联机，把中继和游戏一起运行：

```bash
npm install      # 只需一次：中继基于 wrangler 运行
npm run relay    # ws://<本机>:8787
```

从 `localhost` 或局域网地址打开的页面会自动使用该中继；`?relay=wss://…` 可将其指向其他任意地址。

## 本地运行

游戏是 **Unity** 工程：用 Unity 6000.0.40f1（URP 17）打开 `unity/INKWAVE` 直接 Play 即可。
地图、数值与文本已经导出在工程里，无需任何准备。

```bash
git clone https://github.com/jaydendavisnc/inkwave.git
cd inkwave
npm install           # 仅数据工具需要（three.js，用来烘焙道具几何体）
npm run unity:export  # 从 src/ 重新导出地图与数值
npm run unity:check   # 校验 C# 读取的每个数值都还在
npm run unity:bake    # 烘焙道具几何体（.glb，构建产物）
```

在 Unity 里：**Tools → INKWAVE → Build Play Scene**，然后 Play；
**Tools → INKWAVE → Build Linux / Windows / macOS Player** 可以出独立包。

桌面版打包与 Steam

桌面版本直接由 Unity 打包（CI 每次 push 都会出 Linux 包）。Steamworks 改用 `UnitySteamworks`：
13 个成就、Steam 云存档（`inkwave-save.json`）、富状态与商店统计——全部可选，
没有 Steam 时游戏行为完全不变。

见 [`docs/STEAM-PATH.md`](docs/STEAM-PATH.md)（发布计划）与 [`docs/UNITY-PORT.md`](docs/UNITY-PORT.md)（还差什么）。

## 实现原理

- **墨水绘制在纹理空间。** 每个可涂面都占据一张图集上的一块区域；墨点由 GPU 绘制到图集中，同时一张粗粒度的 CPU 网格负责同步涂地比分和游戏逻辑查询。关卡着色器把墨水叠加到表面上，并带有自身的高度、光泽和湿润度。见 `Scripts/Gameplay/PaintSystem.cs` 与 `Shaders/InkSurface.shader`。
- **地图即数据。** 一张布局就是竞技场一半区域的盒体与斜面列表；另一半是它的 180° 旋转，因此两队场地永远完全一致。见 `Scripts/World/Level.cs`。
- **角色完全程序化。** 乌贼小子的几何体由代码拼出（身体、头、会跟随准星的眼睛、手脚、墨罐、触手），潜行时切换成乌贼形态。见 `Scripts/Render/ActorRenderer.cs`。
- **音效也是程序化。** 一个小合成器在运行时渲染音效（扫频音、带滤波的噪声、气泡声），没有任何音频文件。见 `Scripts/Audio/AudioSystem.cs`。
- **各系统通过事件通信。** 武器、角色和对局发出带类型的事件；HUD、音频与特效负责订阅。

## 界面语言

游戏内置英文与简体中文界面：**设置 → 通用 → 语言**。选择会保存在本地，切换后立即生效。

## 参与贡献

欢迎提交 issue 和 pull request。关于项目结构和需要先运行的检查项，请阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 许可证

[MIT](LICENSE) © 2026 Jayden Davis。INKWAVE 是一个独立项目，与任天堂无关；Splatoon 是任天堂的商标。
