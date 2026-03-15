using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class NeuroMotGame : MonoBehaviour
{
    public enum Mode { Adaptive, Linear }

    [Header("=== 核心组件 ==")]
    public MotTrialManager trialManager;
    public ResultPanelController resultPanel;
    public SessionLogger logger;
    public TarReceiverUdp tarReceiver;
    public AudioSource audioSource;

    [Header("=== 音频资源 ==")]
    public AudioClip clipVoiceRelaxEO;
    public AudioClip clipVoiceRelaxEC;
    public AudioClip clipVoiceFinish;
    public AudioClip clipBeep;

    [Header("=== UI Panels ==")]
    public GameObject panelStart;
    public GameObject panelBaseline;
    public GameObject panelHUD;
    public GameObject panelResult;

    [Header("=== Pause UI (已升级) ==")]
    public GameObject panelPauseToast;   // 红色暂停浮窗
    public Button btnHudPause;           // 左下角的暂停小按钮
    public Button btnPauseHome;          // 浮窗上的“返回主页”按钮

    [Header("=== Pause Icons (新增) ==")]
    public Sprite iconPause;             // 拖入 ui_icon_pause_64
    public Sprite iconPlay;              // 拖入 ui_icon_start_64

    [Header("=== Baseline UI ==")]
    public Button btnStartGame;
    public TMP_Text txtBaselineInfo;
    public TMP_Text txtBaselineTimer;

    [Header("=== HUD Icons ==")]
    public Image imgModeBadge;
    public Sprite spriteModeAdaptive;
    public Sprite spriteModeLinear;
    public GameObject iconCountdownBadge;
    public GameObject panelGameReady;

    [Header("=== StartPanel UI ==")]
    public TMP_InputField inputParticipantId;
    public Button btnLinear;
    public Button btnAdaptive;

    // ===================== 参数配置区 (保持不变) =====================
    [Header("=== 阶段时间配置 ==")]
    public float timeBaselineEO = 60f;
    public float timeBaselineEC = 60f;
    public float timeGameReadyFade = 2.0f;
    public float timeHighlight = 2.5f;
    public float timeTrack = 10f;
    public float timeSelection = -1f;
    public float timeReveal = 2f;
    public float timeRest = 3f;

    [Header("=== 游戏关卡配置 ==")]
    public int totalLevels = 8;
    public int startTotalBalls = 6;
    public int startTargetCount = 2;
    public float startSpeed = 2.5f; //初始速度2.5
    public float linearBaseSpeed = 2.0f;
    public float linearDeltaSpeed = 0.3f;
    public float adaptiveThresholdFactor = 2f; //将阈值系数改为 2 (即 200% increase)
    public float speedMin = 0.5f;
    public float speedMax = 6.0f;
    public int ballsMin = 4;
    public int ballsMax = 16;
    public int targetsMin = 1;

    // 内部状态
    private Mode _mode;
    private string _participantId = "00";
    private float _baselineEOMedian = float.NaN;
    private float _baselineECMedian = float.NaN;
    private float _tarThreshold = float.NaN;
    private bool _startGameClicked = false;
    private bool _isPaused = false;

    private void Start()
    {
        // -----------------------------------------------------------
        // 1. 自动查找补丁 (防止 Inspector 丢失引用)
        // -----------------------------------------------------------
        if (btnHudPause == null && panelHUD != null)
        {
            var found = panelHUD.transform.Find("Btn_PauseIcon");
            if (found != null) btnHudPause = found.GetComponent<Button>();
        }

        // 2. 初始化 UI 状态
        if (imgModeBadge) imgModeBadge.gameObject.SetActive(false);
        if (iconCountdownBadge) iconCountdownBadge.SetActive(false);
        if (panelGameReady) panelGameReady.SetActive(false);
        if (panelPauseToast) panelPauseToast.SetActive(false);

        // 3. 确保开始界面开启，HUD 关闭
        if (panelStart) panelStart.SetActive(true);
        if (panelHUD) panelHUD.SetActive(false); // 开局隐藏 HUD，这是对的

        // 4. 绑定按钮事件
        if (btnStartGame)
        {
            btnStartGame.gameObject.SetActive(false);
            btnStartGame.onClick.AddListener(() => _startGameClicked = true);
        }

        if (btnHudPause)
        {
            btnHudPause.onClick.RemoveAllListeners();
            btnHudPause.onClick.AddListener(TogglePause);
        }

        if (btnPauseHome)
        {
            btnPauseHome.onClick.RemoveAllListeners();
            btnPauseHome.onClick.AddListener(QuitSessionToHome);
        }
    }

    private void Awake()
    {
        if (btnLinear) btnLinear.onClick.AddListener(() => OnChooseMode(Mode.Linear));
        if (btnAdaptive) btnAdaptive.onClick.AddListener(() => OnChooseMode(Mode.Adaptive));

        if (resultPanel && resultPanel.btnHome)
        {
            resultPanel.btnHome.onClick.RemoveAllListeners();
            resultPanel.btnHome.onClick.AddListener(OnBackToStart);
        }
    }

    // ===================== 暂停与退出逻辑 (已修复图标切换) =====================

    public void TogglePause()
    {
        _isPaused = !_isPaused;

        if (_isPaused)
        {
            // 暂停状态
            Time.timeScale = 0f;
            if (panelPauseToast) panelPauseToast.SetActive(true);
            if (audioSource) audioSource.Pause();

            // 切换为播放图标 (表示点击后继续)
            if (btnHudPause && iconPlay) btnHudPause.image.sprite = iconPlay;
        }
        else
        {
            // 继续状态
            Time.timeScale = 1f;
            if (panelPauseToast) panelPauseToast.SetActive(false);
            if (audioSource) audioSource.UnPause();

            // 切换回暂停图标
            if (btnHudPause && iconPause) btnHudPause.image.sprite = iconPause;
        }
    }

    public void QuitSessionToHome()
    {
        // 1. 恢复时间与状态
        Time.timeScale = 1f;
        _isPaused = false;

        // 2. 隐藏暂停界面
        if (panelPauseToast) panelPauseToast.SetActive(false);

        // 3. 停止所有协程 (杀死游戏流程)
        StopAllCoroutines();

        // 4. 清理场上的球
        if (trialManager != null) trialManager.Cleanup();

        // 5. 关闭游戏面板 (这是导致你看到它消失的原因，但在回主页时这是对的)
        if (panelBaseline) panelBaseline.SetActive(false);
        if (panelHUD) panelHUD.SetActive(false);
        if (panelResult) panelResult.SetActive(false);

        // 6. 恢复按钮图标为默认 (防止下次进来还是播放图标)
        if (btnHudPause && iconPause) btnHudPause.image.sprite = iconPause;

        // 7. 返回开始界面
        OnBackToStart();
    }

    // ===================== 流程逻辑 (已修复按钮消失问题) =====================

    private void OnBackToStart()
    {
        if (panelResult) panelResult.SetActive(false);
        if (panelStart) panelStart.SetActive(true);
        if (imgModeBadge) imgModeBadge.gameObject.SetActive(false);
        if (iconCountdownBadge) iconCountdownBadge.SetActive(false);
        if (tarReceiver) tarReceiver.StopAndGetMedian();
    }

    private void OnChooseMode(Mode m)
    {
        _mode = m;
        string modeName = (_mode == Mode.Adaptive) ? "Adaptive" : "Linear";

        _participantId = "TestUser";
        if (inputParticipantId != null && !string.IsNullOrWhiteSpace(inputParticipantId.text))
        {
            _participantId = inputParticipantId.text.Trim();
        }

        // 显示模式标签
        if (imgModeBadge != null)
        {
            imgModeBadge.gameObject.SetActive(true);
            if (_mode == Mode.Adaptive && spriteModeAdaptive) imgModeBadge.sprite = spriteModeAdaptive;
            else if (_mode == Mode.Linear && spriteModeLinear) imgModeBadge.sprite = spriteModeLinear;
            imgModeBadge.SetNativeSize();
        }

        if (logger != null) logger.BeginSession(_participantId, modeName);
        if (panelStart) panelStart.SetActive(false);

        StartCoroutine(RunSessionFlow());
    }

    private IEnumerator RunSessionFlow()
    {
        // =========================================================
        // 【修正点】: 必须在这里就立刻开启 HUD，否则静息检测时左边是空的
        // =========================================================
        if (panelHUD)
        {
            panelHUD.SetActive(true); // 立刻唤醒 HUD 面板
        }

        if (btnHudPause)
        {
            btnHudPause.gameObject.SetActive(true); // 确保暂停按钮显示
            // 确保图标是“暂停”状态 (因为刚开始运行)
            if (iconPause) btnHudPause.image.sprite = iconPause;
        }
        // =========================================================

        // 1. Baseline (静息检测)
        if (panelBaseline)
        {
            panelBaseline.SetActive(true);
            yield return RunBaselineWithAudio();

            // ... (中间的 Baseline 计算代码保持不变) ...
            if (_baselineECMedian > 0) _tarThreshold = _baselineECMedian * adaptiveThresholdFactor;
            else _tarThreshold = 1.0f;

            if (btnStartGame)
            {
                btnStartGame.gameObject.SetActive(true);
                if (txtBaselineInfo) txtBaselineInfo.text = "点击“开始游戏”进入第一轮";
                _startGameClicked = false;
                yield return new WaitUntil(() => _startGameClicked);
                btnStartGame.gameObject.SetActive(false);
            }
            panelBaseline.SetActive(false);
        }

        // 2. Ready Fade
        if (panelGameReady)
        {
            panelGameReady.SetActive(true);
            yield return new WaitForSeconds(timeGameReadyFade);
            panelGameReady.SetActive(false);
        }

        // (原先在这里的 HUD 开启代码已经被移到最上面了，这里就不用写了)

        // 3. Levels
        yield return RunLevels();
    }

    private IEnumerator RunBaselineWithAudio()
    {
        float sampleDt = 0.1f;

        // ======================= 1. 睁眼阶段 (EO) =======================
        if (txtBaselineInfo) txtBaselineInfo.text = "请放松，看向十字靶心...";
        PlayAudio(clipVoiceRelaxEO);

        // 【新增】发送指令阶段事件（可选）
        // if (trialManager.eventSender) trialManager.eventSender.SendEvent("Baseline_EO_Instruction");

        yield return new WaitForSeconds(GetClipLength(clipVoiceRelaxEO) + 0.5f);

        PlayAudio(clipBeep);
        if (txtBaselineInfo) txtBaselineInfo.text = "【睁眼阶段】记录数据中...";

        // 【关键新增】发送 EO 开始事件！告诉 Python "现在开始算 EO 数据了"
        if (trialManager != null && trialManager.eventSender != null)
        {
            trialManager.eventSender.SendEvent("Baseline_EO_Start");
            Debug.Log("[Event] Sent: Baseline_EO_Start");
        }

        yield return new WaitForSeconds(0.5f);

        List<float> eoVals = new List<float>();
        float timer = timeBaselineEO;
        while (timer > 0)
        {
            timer -= sampleDt;
            if (txtBaselineTimer) txtBaselineTimer.text = Mathf.CeilToInt(timer).ToString();

            if (tarReceiver)
            {
                float v = tarReceiver.LatestTar;
                if (!float.IsNaN(v) && v > 0) eoVals.Add(v);
            }
            yield return new WaitForSeconds(sampleDt);
        }
        _baselineEOMedian = Median(eoVals);

        // 【关键新增】发送 EO 结束事件
        if (trialManager != null && trialManager.eventSender != null)
        {
            trialManager.eventSender.SendEvent("Baseline_EO_End");
        }

        // ======================= 2. 闭眼阶段 (EC) =======================
        if (txtBaselineInfo) txtBaselineInfo.text = "收集完毕，请闭眼放松...";
        PlayAudio(clipVoiceRelaxEC);
        yield return new WaitForSeconds(GetClipLength(clipVoiceRelaxEC) + 0.5f);

        PlayAudio(clipBeep);
        if (txtBaselineInfo) txtBaselineInfo.text = "【闭眼阶段】记录数据中...";

        // 【关键新增】发送 EC 开始事件！
        if (trialManager != null && trialManager.eventSender != null)
        {
            trialManager.eventSender.SendEvent("Baseline_EC_Start");
            Debug.Log("[Event] Sent: Baseline_EC_Start");
        }

        yield return new WaitForSeconds(0.5f);

        List<float> ecVals = new List<float>();
        timer = timeBaselineEC;
        while (timer > 0)
        {
            timer -= sampleDt;
            if (txtBaselineTimer) txtBaselineTimer.text = Mathf.CeilToInt(timer).ToString();

            if (tarReceiver)
            {
                float v = tarReceiver.LatestTar;
                if (!float.IsNaN(v) && v > 0) ecVals.Add(v);
            }
            yield return new WaitForSeconds(sampleDt);
        }
        _baselineECMedian = Median(ecVals);

        // 【关键新增】发送 EC 结束事件
        if (trialManager != null && trialManager.eventSender != null)
        {
            trialManager.eventSender.SendEvent("Baseline_EC_End");
        }

        // ======================= 3. 结束 =======================
        if (txtBaselineInfo) txtBaselineInfo.text = "静息检测完成，准备开始游戏";
        PlayAudio(clipVoiceFinish);
        yield return new WaitForSeconds(GetClipLength(clipVoiceFinish));
    }

    private IEnumerator RunLevels()
    {
        // 初始化变量
        int totalBalls = startTotalBalls;
        int targets = startTargetCount;
        float speed = startSpeed;

        // 统计用变量
        float peakSpeed = speed; int peakTotalBalls = totalBalls; int peakTargets = targets;
        int totalHits = 0; int totalTargetsAll = 0; int totalFalsePositives = 0;
        float sumTaskTime = 0f; float sumResponseTime = 0f; int responseCount = 0;
        List<float> allTarValues = new List<float>();

        // 开始关卡循环
        for (int level = 1; level <= totalLevels; level++)
        {
            // =================================================================
            // 【修改点 1】Linear 模式：更平滑的难度曲线
            // =================================================================
            if (_mode == Mode.Linear)
            {
                switch (level)
                {
                    // --- 热身阶段 (让被试适应 UI 和 操作) ---
                    case 1: totalBalls = 4; targets = 1; speed = 1.0f; break; // 极简：4球1目标
                    case 2: totalBalls = 6; targets = 2; speed = 1.5f; break; // 简单：6球2目标

                    // --- 进阶阶段 (稍微提速) ---
                    case 3: totalBalls = 6; targets = 2; speed = 2.0f; break; // 速度+
                    case 4: totalBalls = 8; targets = 3; speed = 2.0f; break; // 经典MOT负荷(3目标)

                    // --- 挑战阶段 (增加干扰球) ---
                    case 5: totalBalls = 8; targets = 3; speed = 2.5f; break; // 速度++
                    case 6: totalBalls = 10; targets = 3; speed = 2.5f; break; // 干扰球+2

                    // --- 高难阶段 (4目标是很多人的认知极限) ---
                    case 7: totalBalls = 10; targets = 4; speed = 3.0f; break;
                    case 8: totalBalls = 12; targets = 5; speed = 3.5f; break;

                    // --- 极限阶段 (后续关卡) ---
                    default:
                        totalBalls = 12;
                        targets = 4;
                        speed = 3.0f + (level - 8) * 0.2f; // 只加度，不加球了，防止密集恐惧症
                        break;
                }

                // =================================================================
                // 【修改点 2】打印每关难度日志
                // =================================================================
                Debug.Log($"<color=cyan>[Linear Difficulty]</color> Level {level}: 球数={totalBalls}, 目标={targets}, 速度={speed:F1}");
            }

            // 组装配置
            var cfg = new MotTrialManager.TrialConfig
            {
                totalBalls = totalBalls,
                targetCount = targets,
                speed = speed,
                highlightSeconds = timeHighlight,
                trackingSeconds = timeTrack,
                selectionSeconds = timeSelection,
                revealSeconds = timeReveal,
                restSeconds = timeRest
            };

            // 运行关卡 (等待回调)
            MotTrialManager.TrialResult result = default;
            bool done = false;

            // 调用 MTM
            yield return trialManager.RunTrial(_mode.ToString(), level, cfg, r => { result = r; done = true; });

            // 等待 MTM 跑完这一关
            while (!done) yield return null;

            // --- 数据统计 (保持不变) ---
            totalHits += result.correct;
            totalTargetsAll += targets;
            totalFalsePositives += result.wrong;
            sumTaskTime += result.trialDurationSec;

            if (result.selectionTimeSec >= 0) { sumResponseTime += result.selectionTimeSec; responseCount++; }
            if (speed > peakSpeed) { peakSpeed = speed; peakTargets = targets; peakTotalBalls = totalBalls; }
            if (!float.IsNaN(result.tarMedian)) allTarValues.Add(result.tarMedian);

            // 记录 CSV
            if (logger) logger.LogTrial(level, totalBalls, targets, speed, result.correct, result.wrong, result.acc, result.tarMedian, result.selectionTimeSec);

            // 如果是自适应模式，计算下一关参数
            if (_mode == Mode.Adaptive) ApplyAdaptive(ref totalBalls, ref targets, ref speed, result.tarMedian, result.acc);
        }

        // 所有关卡结束，显示结算面板
        if (panelResult) panelResult.SetActive(true);
        if (resultPanel)
        {
            float finalAcc = totalTargetsAll > 0 ? (float)totalHits / totalTargetsAll : 0;
            float finalAvgResponse = responseCount > 0 ? sumResponseTime / responseCount : 0;
            float finalAvgTar = Median(allTarValues);

            // 这里加上了你最新的 SetupResult 参数
            resultPanel.SetupResult(_participantId, _mode.ToString(), finalAcc, totalHits, totalTargetsAll, totalFalsePositives, peakSpeed, peakTargets, peakTotalBalls, sumTaskTime, finalAvgResponse, _baselineEOMedian, _baselineECMedian, finalAvgTar, logger);
        }
    }

    private void PlayAudio(AudioClip clip) { if (audioSource && clip) audioSource.PlayOneShot(clip); }
    private float GetClipLength(AudioClip clip) => clip ? clip.length : 0f;

    // =========================================================
    // 【核心修改】基于六状态模型 (6-State Model) 的自适应算法
    // 状态定义：Boredom, Mastery, Flow, Unstable, Struggling, Withdrawal
    // =========================================================
    private void ApplyAdaptive(ref int totalBalls, ref int targets, ref float speed, float currentTar, float currentAcc)
    {
        // 1. 判定条件 (Trigger Conditions)
        // ---------------------------------------------------------
        // TAR 判定：如果没采到数据(NaN)，默认为低唤醒 (False)
        bool isTarHigh = !float.IsNaN(currentTar) && (currentTar > _tarThreshold);

        // ACC 判定
        bool isAccPerfect = currentAcc >= 0.99f;    // ACC >= 99% (完美)
        bool isAccPass = currentAcc >= 0.70f;       // 70% <= ACC < 99% (及格)
        // isAccFail = currentAcc < 70% (不及格)

        string stateName = "Unknown"; // 用于日志调试

        // 2. 状态机逻辑 (State Machine)
        // ---------------------------------------------------------
        if (isAccPerfect) // [Mastery: ACC >= 99%]
        {
            if (!isTarHigh)
            {
                // [State 1: Boredom 无聊] 
                // 任务太简单，资源未唤起 -> 大幅提难
                stateName = "Boredom";
                speed += 0.3f;
                totalBalls += 2;
                targets += 1;
            }
            else
            {
                // [State 2: Mastery 精通] 
                // 表现完美，资源充分调动 -> 精细化提难策略
                stateName = "Mastery";

                // --- 场景：从 4 球冲刺 5 球的“软着陆”策略 ---
                if (targets == 4)
                {
                    // 策略：先在 4 球下练速度，练扎实了再冲 5 球
                    // 假设 3.0 是一个强者的速度分界线
                    if (speed < 3.0f)
                    {
                        speed += 0.3f; // 优先提速，夯实 4 球基础
                        // targets, totalBalls 不变
                    }
                    else
                    {
                        // 速度够快了，说明 4 球已经彻底毕业，冲击 5 球！
                        targets = 5;
                        totalBalls += 1;
                        speed -= 0.5f; // 【关键】冲击 5 球时，主动大幅降速，防止难度崩盘
                    }
                }
                else if (targets < 4)
                {
                    // 1~3 球阶段，正常升级 (加球 + 加球)
                    targets += 1;
                    totalBalls += 1;
                    // 低难度阶段可以不加速，或者微调 speed += 0; 
                }
                else // targets == 5
                {
                    // 5 球极限，保持现状或微调
                    // 可以在这里允许微幅提速，挑战人类极限
                    speed += 0.1f;
                }
            }
        }
        else if (isAccPass) // 70% ~ 99% [Flow / Unstable]
        {
            if (isTarHigh)
            {
                // [State 3: Flow 心流]
                // 状态极佳 -> 微幅提速保持张力
                stateName = "Flow";
                speed += 0.1f;
            }
            else
            {
                // [State 4: Unstable 不稳定]
                // 运气好或走神 -> 保持不变，观察下一轮
                stateName = "Unstable";
                /* Hold */
            }
        }
        else // ACC < 70% [Fail]
        {
            if (isTarHigh)
            {
                // [State 5: Struggling 挣扎] 
                // 很努力(TAR高)但做不对。说明到了"能力上限"。
                stateName = "Struggling";

                // --- 场景：5 球太难，如何回退？---
                if (targets == 5)
                {
                    // 策略：承认 5 球超纲，退回 4 球，稍微减速
                    targets = 4;
                    totalBalls -= 1;
                    speed -= 0.2f;
                }
                else if (targets == 4)
                {
                    // 策略：死保 4 球基准线，优先大幅降速
                    if (speed > 1.0f) // 只要速度还没到底
                    {
                        speed -= 0.4f; // 大幅降速 (慢动作模式)
                        // targets 不变
                    }
                    else
                    {
                        // 速度已经慢成蜗牛了还是不行，那只能认命降级了
                        targets = 3;
                        totalBalls = Mathf.Max(ballsMin, totalBalls - 1);
                    }
                }
                else
                {
                    // 其他情况 (1~3球)，常规减速
                    speed -= 0.3f;
                }
            }
            else
            {
                // [State 6: Withdrawal 撤离] 
                // 彻底崩溃，放弃思考(TAR低) -> 大幅降难挽回信心
                stateName = "Withdrawal";
                speed -= 0.5f;
                totalBalls = Mathf.Max(ballsMin, totalBalls - 2);
                targets = Mathf.Max(targetsMin, targets - 1);
            }
        }

        // 3. 边界安全限制 (Safety Clamping)
        // ---------------------------------------------------------
        // 锁死目标数上限为 5
        targets = Mathf.Clamp(targets, 1, 5);

        // 确保至少有1个干扰球 (targets < totalBalls)
        if (targets >= totalBalls) totalBalls = targets + 1;

        // 限制速度和总球数范围
        speed = Mathf.Clamp(speed, speedMin, speedMax);
        totalBalls = Mathf.Clamp(totalBalls, ballsMin, ballsMax);

        // 4. 打印调试日志
        Debug.Log($"[Adaptive] State: {stateName} | Acc: {currentAcc * 100:F0}% | TAR: {currentTar:F2} (Thres:{_tarThreshold:F2}) " +
                  $"-> Next: Spd={speed:F1}, Balls={totalBalls}, Tar={targets}");
    }
    private static float Median(List<float> xs)
    {
        if (xs == null || xs.Count == 0) return float.NaN;
        xs.Sort(); int n = xs.Count; if (n % 2 == 1) return xs[n / 2];
        return 0.5f * (xs[n / 2 - 1] + xs[n / 2]);
    }
}