# 夏日 / 春日播放器动效（2026-09-27）

## 实现

- 每个二次元 PlayerPage 以相同概率选择 summer / spring，只选一次；普通播放器继续使用原来的视觉。
- 春日背景由用户指定的 onefor-imagegen 生成：gpt-image-2.5-flare、high、2048×1152。原始输出和提示词位于 artifacts/anime-scenes；打包资源位于 Assets/Player/Scenes。
- Tools/prepare_anime_scenes.py 使用本地 Pillow / NumPy 制作羽化网格形变素材及极限姿态对照图。它是小幅局部插画形变，非人物骨骼或完整 Live2D 动画。
- 根据用户后续要求加强：夏日增加 5 组横向风痕、随风叶片和浮尘，发梢 / 衣角 / 树叶摆幅提高，风铃摆动至 ±5°；春日改为 30 片不同大小、速度和相位的花瓣，跨约 1050 个设计像素下落，并横移、翻转。
- 2026-09-28 再次增强：夏日升级为 9 组多速度风痕、20 个三层景深叶片 / 光尘，并加强水面、云层、发梢、衣角和树冠的独立响应；春日升级为 48 片四层景深花瓣，加入双向旋转、翻面、弧形横漂和树冠呼吸。所有新增循环仍受原有暂停、隐藏和减少动效策略控制。
- 粒子使用 HUD 设计坐标的安全区域；人物局部层共享同一 UniformToFill 变换。舞台频谱不参与二次元页面。
- 增加播放按钮呼吸光、短暂收藏星光、猫咪线稿与周期性队列反光。光晕位置跟随真实按钮布局。
- 播放暂停后 600ms 淡停；最小化停止；系统 / 全局 / 二次元减少动效任一开启则静止。离开时取消订阅、停止动画并释放资源。
- 动画由 Composition 驱动，无逐帧 DispatcherTimer；只使用一次 600ms 的停止计时器。

## 验证

- 2026-09-28 最终增强版：Debug 全量 467 项测试通过；独立 Release 输出构建 0 警告、0 错误。快速集成验收覆盖 summer / spring 的播放、暂停、减少动效、最小化、全屏、窄窗口及 20 次创建 / 离开，最终 `activeControllers=0`；报告位于 `artifacts/anime-scenes/motion-enhancement-2026-09-28/lifecycle-final2/validation.json`。
- 加强版 Release 构建：0 警告、0 错误；465 项测试通过。
- 新增测试覆盖每次进入只随机一次、缺失任一场景资源时回退、暂停 / 恢复竞争、隐藏 / 减少动效 / 已释放状态及打包资源完整性。
- 完整集成测试记录：artifacts/anime-scenes/validation-final/validation.json。两套场景分别实际播放至少 65 秒，暂停为 Paused，减少动效为 Static，最小化为 Hidden；全屏、窄窗口及切歌不改变所选场景。
- 连续创建 / 离开 20 个播放器，所有动画控制器均已释放，activeControllers=0。WinUI 页面弱引用在强制 GC 后仍有存活，不能据此宣称页面内存已全部回收；后续应单独做托管/原生内存分析。
- 实际窗口检查过夏日、春日、长歌词、四首及三首队列、普通和窄窗口。发现并修复歌词 Translation 属性初始化顺序，以及按钮光晕位置偏移。
- 加强后的快速集成回归位于 artifacts/anime-scenes/strong-validation-v2/validation.json：两套分支及 20 次页面切换通过，activeControllers=0。实机截图确认了加大、持续下落的粉色花瓣；新粒子相位分散，进入后不会全体同时消失等待一轮。
- 验收宿主现已使用稳定的 Grid / XamlRoot，按正式 AppShell 的方式替换页面；同时避免立即关闭渲染线程还在使用的 Composition 动画和缓动对象。
- 录屏工具启动超时，未产出两段 60 秒实机视频；未测得 GPU 帧率，不能将接近 60fps 记为已验收。

## 重跑集成验收

Debug 专用 Tools/AnimeSceneValidation.cs 在设置 BEANS_ANIME_VALIDATE_DIR 时启用，使用独立 PlaybackService、本地静音 WAV、明确标注的测试歌词与队列，不写用户歌单、收藏或播放历史。BEANS_ANIME_VALIDATE_QUICK=1 将每套持续播放缩短为 2 秒，仅用于快速回归，不替代持续验收。相关测试入口不编入 Release。

BEANS_ANIME_SCENE_PREVIEW=summer 或 spring 可在单次进程中固定预览场景；正常运行不设置此变量，继续随机。
