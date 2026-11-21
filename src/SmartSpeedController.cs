using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Engine;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Inputs;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Mods;
using CesiumLoader.SDK.Speed;
using CesiumLoader.SDK.UserInterface;
using Core.Scene;
using GameLogic;
using Tools;
using UI;
using UnityEngine;

namespace SmartSpeedMod
{
    /// <summary>
    /// 局内平时按设定倍率加速，只有本人操作倒计时在运行时才恢复 1x。
    /// RawAction 只登记本人操作序号，不代表倒计时已开始或操作已完成。
    /// </summary>
    public sealed class SmartSpeedController : ModBase
    {
        private const double DefaultSpeed = 2.0;
        private const double MinSpeed = 1.0;
        private const double MaxSpeed = 10.0;   // 放宽到 10x
        private const double SpeedStep = 0.5;

        private ModConfig _config;
        private SpeedHud _hud;

        private bool _showHud = true;
        private float _hudX = 0f;                      // HUD 距右边缘距离（右下角）
        private float _hudY = 0f;                      // HUD 距底部距离（右下角）
        private int _hudFontSize = 30;

        private bool _inGame;                          // 当前是否在游戏局内
        private bool _settlementPanelActive;           // 结算点赞面板不受局内或倒计时设置影响
        private bool _accelerateInGameOnly = true;     // 仅局内加速（大厅等不加速）

        private double _targetSpeed = DefaultSpeed;
        private bool _timerDetectEnabled = true;
        private bool _notifyEnabled = true;
        private bool _smartEnabled = true;
        private KeyCode _speedUpKey = KeyCode.Equals;
        private KeyCode _speedDownKey = KeyCode.Minus;

        private RoomInfo _actionRoom;
        private readonly List<long> _ownActionSns = new List<long>();
        private bool _ownCountdownActive;

        private double _appliedSpeed = 1.0;
        private double _lastRequestedSpeed;
        private bool _awaitingSpeedAck;
        private bool _speedSetFailed;
        private DateTime _lastSpeedRequestAt;
        private bool _ready;
        private bool _eventsHooked;

        // 动画同步：恢复 1x 时强制还原 SpeedHackMod 的独立动画加速
        private object _speedHackController;       // SpeedHackMod.SpeedHackController 实例
        private MethodInfo _updateUnityAnimations; // 私有 UpdateUnityAnimations()
        private FieldInfo _fieldActiveSpeed;       // 私有 _activeSpeed
        private FieldInfo _fieldUnityTarget;       // 私有 _unityTargetSpeed
        private FieldInfo _fieldUseUnitySpeed;     // 私有 _useUnitySpeed
        private bool _animSyncResolved;
        private bool _animSyncEnabled = true;      // 是否启用动画同步修复
        private bool _animSyncWarned;

        private float _nextPollAt;
        private float _nextKeyRepeatAt;
        private const float PollInterval = 0.15f;
        private const double SpeedAckTimeout = 2.5;
        private const double SpeedDriftRetryDelay = 0.5;
        private const float SaveDelaySec = 1.0f;
        private float _saveAt;

        public override string Version => "1.0.0";
        public override string Name => "SmartSpeedMod";

        public override void OnInitialize()
        {
            _config = Config;
            LoadConfig();

            if (_showHud)
            {
                try { _hud = new SpeedHud("SmartSpeedHud", 900); }
                catch (Exception ex) { Log.Warn("HUD 初始化失败: " + ex.Message); }
            }

            // IsAvailable 只是"读回执"状态，启动初期常显示过期；SetSpeed 直接发 UDP 指令不需要等回执。
            // 这里不作为失败判定，只在日志里提示，真正的成功/失败以 ApplySpeed 结果为准。
            if (!SpeedHack.IsAvailable)
                Log.Info("倍率后端回执尚未就绪（不影响下发，将自动重试）: " + SpeedHack.UnavailableReason);

            HookEvents();
            _ready = true;
            ApplySpeed(true);

            Log.Info("智能倍速 v1.0 已启动: 目标 " + F(_targetSpeed) + "x, 范围 1~10x");
            Log.Info("仅在本人操作倒计时运行时恢复 1x");
            Log.Info("热键: [=]加速 [-]减速（加速开关请在 config.json 里设置 smartEnabled）");
            if (_notifyEnabled)
                UiService.Notify("智能倍速 v1.0: " + F(_targetSpeed) + "x（仅本人操作倒计时恢复 1x）");
        }

        public override void OnUpdate()
        {
            if (!_ready) return;
            try
            {
                if (_config.ReloadIfChanged())
                    LoadConfig();

                HandleKeys();

                float now = UnityTime.RealtimeSinceStartup;

                if (now >= _nextPollAt)
                {
                    _nextPollAt = now + PollInterval;
                    // 局内判定（大厅/房间/结算等不算局内）
                    bool wasInGame = _inGame;
                    _settlementPanelActive = SimpleSingletonProvider<UIManager>.inst?.currentPanel is BattleSettlementPanel;
                    _inGame = !_settlementPanelActive && IsInGame();
                    if (wasInGame && !_inGame)
                        ResetCountdownState();
                    PollCountdown();
                    if (_inGame != wasInGame)
                        Log.Info("局内状态变化: " + (wasInGame ? "退出" : "进入") + "局内 → 目标 " + F(WantedSpeed()) + "x");
                    ApplySpeed(false, verify: true);
                }

                RenderHud();

                if (_saveAt > 0f && now >= _saveAt)
                {
                    _saveAt = 0f;
                    SaveConfig();
                }
            }
            catch (Exception ex)
            {
                Log.ReportCrash("OnUpdate", ex);
            }
        }

        public override void OnUnload()
        {
            UnhookEvents();
            try { SpeedHack.SetSpeed(_smartEnabled ? _targetSpeed : 1.0); }
            catch { }
            if (_hud != null) { try { _hud.Clear(); } catch { } _hud = null; }
            SaveConfig();
            Log.Info("智能倍速已卸载，倍率已恢复");
        }

        public override void OnConfigChanged() { LoadConfig(); }

        // ---------- 配置 ----------

        private void LoadConfig()
        {
            _targetSpeed = Clamp(_config.GetDouble("speed", DefaultSpeed));
            _timerDetectEnabled = _config.GetBool("timerDetect", true);
            _notifyEnabled = _config.GetBool("notify", true);
            _smartEnabled = _config.GetBool("smartEnabled", true);
            _speedUpKey = (KeyCode)_config.GetInt("speedUpKey", (int)KeyCode.Equals);
            _speedDownKey = (KeyCode)_config.GetInt("speedDownKey", (int)KeyCode.Minus);

            _accelerateInGameOnly = _config.GetBool("accelerateInGameOnly", true);
            bool newShowHud = _config.GetBool("showHud", true);
            _hudX = _config.GetFloat("hudX", 0f);
            _hudY = _config.GetFloat("hudY", 0f);
            _hudFontSize = _config.GetInt("hudFontSize", 30);

            if (newShowHud && _hud == null && _ready)
            {
                try { _hud = new SpeedHud("SmartSpeedHud", 900); } catch { }
            }
            else if (!newShowHud && _hud != null)
            {
                try { _hud.Clear(); } catch { }
                _hud = null;
            }
            _showHud = newShowHud;
        }

        private void SaveConfig()
        {
            _config.Set("speed", _targetSpeed);
            _config.Set("timerDetect", _timerDetectEnabled);
            _config.Set("notify", _notifyEnabled);
            _config.Set("smartEnabled", _smartEnabled);
            _config.Set("accelerateInGameOnly", _accelerateInGameOnly);
            _config.Set("showHud", _showHud);
            _config.Set("hudX", _hudX);
            _config.Set("hudY", _hudY);
            _config.Set("hudFontSize", _hudFontSize);
            _config.Save();
        }

        private void ScheduleSave() { _saveAt = UnityTime.RealtimeSinceStartup + SaveDelaySec; }

        // ---------- 倍率控制 ----------

        private double WantedSpeed()
        {
            if (!_smartEnabled || _settlementPanelActive) return 1.0;
            // 仅局内加速：大厅/房间/结算等界面恒 1x
            if (_accelerateInGameOnly && !_inGame) return 1.0;
            return _ownCountdownActive ? 1.0 : _targetSpeed;
        }

        private void ApplySpeed(bool force, bool verify = false)
        {
            double wanted = WantedSpeed();
            bool changed = Math.Abs(wanted - _appliedSpeed) >= 0.01;
            DateTime now = DateTime.UtcNow;
            double sinceRequest = (now - _lastSpeedRequestAt).TotalSeconds;
            if (!force && !changed)
            {
                if (!verify) return;
                if (!_speedSetFailed && !_awaitingSpeedAck && !_ownCountdownActive && !_settlementPanelActive) return;
                if (SpeedHack.TryGetSpeed(out double confirmed))
                {
                    if (Math.Abs(confirmed - wanted) < 0.01)
                    {
                        _awaitingSpeedAck = false;
                        _speedSetFailed = false;
                        return;
                    }
                    // 已确认实际倍率偏离，不必等完整的无回执超时。
                    if (sinceRequest < SpeedDriftRetryDelay) return;
                }
                else if ((!_awaitingSpeedAck && !_speedSetFailed) || sinceRequest < SpeedAckTimeout)
                    return; // 已确认的倍率不因偶尔读不到状态而重发
            }
            else if (!force && _speedSetFailed && sinceRequest < SpeedAckTimeout && wanted == _lastRequestedSpeed)
                return;

            _lastRequestedSpeed = wanted;
            // 给 SDK 留出接收回执和内部重试的时间，不持续覆盖待确认请求。
            _lastSpeedRequestAt = now;
            if (SpeedHack.SetSpeed(wanted))
            {
                _speedSetFailed = false;
                _awaitingSpeedAck = true;
                _appliedSpeed = wanted;
                if (changed)
                {
                    SyncSpeedHackAnimations(wanted);
                }
            }
            else
            {
                _speedSetFailed = true;
            }
        }

        /// <summary>
        /// 修复"抉择时进度恢复 1x 但 UI/独立动画仍在加速"的问题。
        /// 根因：SpeedHackMod 用 FairyUiTimeScaleBridge / IndependentAnimationBridge
        /// 独立加速了 UI 过渡与动画组件，这些状态只在它自己的倍率降为 1 时才 Restore。
        /// 我们反射把它的倍率字段临时压到 1（抉择期间），触发它 Restore 动画；
        /// 抉择结束后我们再把这些字段还原为我们的目标倍率，动画随之恢复加速。
        /// 全程 try/catch，失败安全降级，不影响本 mod 核心功能。
        /// </summary>
        private void SyncSpeedHackAnimations(double speed)
        {
            if (!_animSyncEnabled) return;
            try
            {
                if (!_animSyncResolved)
                {
                    _animSyncResolved = true;
                    ModContext shCtx = ModRegistry.Get("SpeedHackMod");
                    if (shCtx != null && shCtx.Mod != null)
                    {
                        _speedHackController = shCtx.Mod;
                        Type t = _speedHackController.GetType();
                        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                        _updateUnityAnimations = t.GetMethod("UpdateUnityAnimations", flags);
                        _fieldActiveSpeed = t.GetField("_activeSpeed", flags);
                        _fieldUnityTarget = t.GetField("_unityTargetSpeed", flags);
                        _fieldUseUnitySpeed = t.GetField("_useUnitySpeed", flags);
                    }
                }
                if (_speedHackController == null) return;

                // 把 SpeedHackMod 的内部倍率对齐到我们当前生效的倍率。
                // 这样它每帧的 UpdateUnityAnimations 会据此正确 Restore/加速动画。
                if (_fieldActiveSpeed != null)
                    _fieldActiveSpeed.SetValue(_speedHackController, speed);
                if (_fieldUnityTarget != null && _fieldUseUnitySpeed != null)
                {
                    bool useUnity = (_fieldUseUnitySpeed.GetValue(_speedHackController) is bool) && (bool)_fieldUseUnitySpeed.GetValue(_speedHackController);
                    if (useUnity)
                        _fieldUnityTarget.SetValue(_speedHackController, speed);
                }
                if (_updateUnityAnimations != null)
                    _updateUnityAnimations.Invoke(_speedHackController, null);
            }
            catch (Exception ex)
            {
                if (!_animSyncWarned)
                {
                    _animSyncWarned = true;
                    Log.Warn("动画同步失败（已降级，动画可能与进度不同步）: " + ex.Message);
                }
            }
        }

        // ---------- 热键 ----------

        private void HandleKeys()
        {
            float now = UnityTime.RealtimeSinceStartup;

            bool up = IsKeyPressedRepeat(_speedUpKey, now);
            bool down = IsKeyPressedRepeat(_speedDownKey, now);
            if (up == down) return;

            double target = Clamp(_targetSpeed + (up ? SpeedStep : -SpeedStep));
            if (target == _targetSpeed) return;
            _targetSpeed = target;
            ScheduleSave();
            ApplySpeed(false);
            // 不弹通知：HUD 已显示倍率，避免调速时刷屏
        }

        private bool IsKeyPressedRepeat(KeyCode key, float now)
        {
            if (!InputService.IsKeyHeld(key)) return false;
            if (InputService.IsKeyPressed(key))
            {
                _nextKeyRepeatAt = now + 0.4f;
                return true;
            }
            if (now >= _nextKeyRepeatAt)
            {
                _nextKeyRepeatAt = now + 0.12f;
                return true;
            }
            return false;
        }

        // ---------- 本人操作倒计时 ----------

        private void ResetCountdownState()
        {
            _actionRoom = null;
            _ownActionSns.Clear();
            _ownCountdownActive = false;
        }

        private void TrackRoom(RoomInfo room)
        {
            if (object.ReferenceEquals(_actionRoom, room)) return;
            _actionRoom = room;
            _ownActionSns.Clear();
        }

        private void PollCountdown()
        {
            var room = SimpleSingletonProvider<GameLogicManager>.inst?.room?.curRoomInfo;
            TrackRoom(room);

            bool active = false;
            if (_inGame && _timerDetectEnabled && room != null)
            {
                for (int i = _ownActionSns.Count - 1; i >= 0; i--)
                {
                    long sn = _ownActionSns[i];
                    // 已提交的操作立即解除保护，即使界面或计时器对象还没清掉。
                    if (room._finishSn.Contains(sn))
                        _ownActionSns.RemoveAt(i);
                    else if (!active && TimerDetector.HasActiveOperation(sn))
                        active = true;
                }
            }

            if (active == _ownCountdownActive) return;
            _ownCountdownActive = active;
            if (_smartEnabled)
                Log.Info(active ? "本人操作倒计时开始 → 恢复 1x" : "本人操作倒计时结束 → 目标 " + F(WantedSpeed()) + "x");
        }

        /// <summary>
        /// 判断当前是否在游戏局内。
        /// 场景判定（最可靠）：SceneType.Battle(3) 或 SceneType.SinglePlayer(6，吉星小镇)。
        /// 场景信息不可用时回退到战斗房间判定（借鉴 TurnFocusMod.IsLiveBattle）。
        /// 大厅(Home=2)/登录/结算等非局内返回 false。
        /// </summary>
        private static bool IsInGame()
        {
            try
            {
                // 主判定：当前场景类型。Battle=多人对局，SinglePlayer=吉星小镇单人模式
                SceneManager scenes = SimpleSingletonProvider<SceneManager>.inst;
                if (scenes != null && scenes.currentType != null)
                {
                    SceneType type = scenes.currentType.Value;
                    if (type == SceneType.Battle || type == SceneType.SinglePlayer)
                        return true;
                    // 明确处于大厅/登录/选曲等非局内场景 → 直接 false，不回退
                    if (type == SceneType.Home || type == SceneType.Login ||
                        type == SceneType.SelectMusic || type == SceneType.Music || type == SceneType.None)
                        return false;
                }

                // 回退判定：有战斗实例且有玩家数据，且房间状态为 RUNNING
                GameLogicManager gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (gm == null || gm.account == null || gm.battle == null)
                    return false;
                if (gm.battle.PlayerDatas == null || gm.battle.PlayerDatas.Count == 0)
                    return false;
                RoomLogic room = gm.room;
                if (room != null && room.roomController != null)
                    return room.roomController.IsBattle();
                return true;
            }
            catch { return false; }
        }

        private void HookEvents()
        {
            if (_eventsHooked) return;
            _eventsHooked = true;
            GameEvents.RawAction += OnRawAction;
            GameEvents.MatchSettled += OnMatchSettled;
            try { GameEvents.StartAutoHook(); }
            catch (Exception ex) { Log.Warn("启动事件钩子失败: " + ex.Message); }
        }

        private void UnhookEvents()
        {
            if (!_eventsHooked) return;
            _eventsHooked = false;
            GameEvents.RawAction -= OnRawAction;
            GameEvents.MatchSettled -= OnMatchSettled;
            ResetCountdownState();
        }

        private void OnRawAction(RawActionEvent action)
        {
            if (action == null || action.Sn <= 0 || !Players.IsSelf(action.PlayerId)) return;
            var room = SimpleSingletonProvider<GameLogicManager>.inst?.room?.curRoomInfo;
            if (room == null) return;
            TrackRoom(room);
            if (room._finishSn.Contains(action.Sn) || _ownActionSns.Contains(action.Sn)) return;

            if (_ownActionSns.Count == 64) _ownActionSns.RemoveAt(0);
            _ownActionSns.Add(action.Sn);
        }

        private void OnMatchSettled(IReadOnlyList<PlayerFinalStats> stats)
        {
            ResetCountdownState();
        }

        // ---------- 游戏内 HUD ----------

        private void RenderHud()
        {
            if (_hud == null) return;
            try
            {
                // 非局内（大厅/房间/结算）不显示 HUD
                if (_settlementPanelActive || (_accelerateInGameOnly && !_inGame))
                {
                    _hud.Hide();
                    return;
                }
                // 极简：只显示当前倍率。配色区分状态：灰=关闭，青=抉择恢复，金=加速中
                string text;
                int accent;
                if (!_smartEnabled)
                {
                    text = "1x"; accent = 0;
                }
                else if (_ownCountdownActive)
                {
                    text = "1x"; accent = 1;
                }
                else
                {
                    text = F(_appliedSpeed) + "x"; accent = 2;
                }
                _hud.Render(text, accent, _hudX, _hudY, _hudFontSize);
            }
            catch (Exception ex)
            {
                Log.ReportCrash("RenderHud", ex);
                if (_hud != null) { try { _hud.Hide(); } catch { } }
            }
        }

        // ---------- 工具 ----------

        private static double Clamp(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return DefaultSpeed;
            if (v < MinSpeed) return MinSpeed;
            if (v > MaxSpeed) return MaxSpeed;
            return Math.Round(v, 2);
        }

        private static string F(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }
    }
}
