using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class MotTrialManager : MonoBehaviour
{
    // ===================== 数据结构定义 =====================

    // [TrialConfig] 每一关的参数配置 (由 NeuroMotGame 传入)
    [Serializable]
    public struct TrialConfig
    {
        public int totalBalls;        // 总球数 (干扰+目标)
        public int targetCount;       // 目标球数
        public float speed;           // 移动速度
        public float highlightSeconds;// 记忆阶段时长 (目标高亮)
        public float trackingSeconds; // 追踪阶段时长 (所有球同色移动)
        public float selectionSeconds;// 选择阶段时长 (<0 表示不限时)
        public float revealSeconds;   // 结果揭晓时长
        public float restSeconds;     // 休息阶段时长
    }

    // [TrialResult] 每一关的运行结果 (传回给 NeuroMotGame 记录日志)
    [Serializable]
    public struct TrialResult
    {
        public int levelIndex;

        // 本关配置副本 (方便日志追溯)
        public int totalBalls;
        public int targetCount;
        public float speed;
        public float highlightSeconds, trackingSeconds, selectionSeconds, revealSeconds, restSeconds;

        // 行为表现
        public int hits;              // 选对的数量
        public int falsePositives;    // 选错的数量 (误报)
        public float acc;             // 正确率 (hits / targets)
        public bool confirmed;        // 是否点击了确认按钮

        // 时间指标
        public float selectionTimeSec;// 反应时 (Track结束 -> 点击确认)
        public float trialDurationSec;// 本关总耗时

        // TAR 生理指标 (Highlight + Track 阶段的数据统计)
        public float tarMedian, tarMean, tarStd, tarMin, tarMax;
        public int tarN;              // 有效 TAR 样本数

        // 便捷属性
        public int correct => hits;
        public int wrong => falsePositives;
        public int tarCount => tarN;
    }

    // 游戏阶段枚举
    public enum Phase { Idle, Highlight, Track, Select, Reveal, Rest }

    // ===================== Inspector 引用绑定 =====================
    [Header("场景对象引用")]
    public Transform arenaRoot;       // 放置小球的父物体
    public RectTransform arenaRect;   // 场地边界 (用于计算生成范围)

    [Header("预制体")]
    public MotBall ballPrefab;        // 小球预制体

    [Header("UI 文本")]
    public TMP_Text topHint;          // 顶部提示文字 (如 "请追踪")
    public TMP_Text countdownText;    // 倒计时文字

    [Header("选择阶段 UI (必须绑定)")]
    public GameObject selectionPanel; // 包含确定/取消按钮的面板
    public Button btnCancel;          // 取消/重置选择按钮
    public Button btnConfirm;         // 确认提交按钮

    [Header("休息阶段 UI (可选)")]
    public Button btnNext;            // "下一关"按钮

    [Header("数据通信模块")]
    public TarReceiverUdp tarReceiver;// 接收 Python 发来的 TAR 数据
    // 【新增】发送 Event 给 Python
    public UdpEventSender eventSender;

    // ===================== 运行时状态 =====================
    public Phase CurrentPhase { get; private set; } = Phase.Idle;

    private readonly List<MotBall> _balls = new();      // 当前场上的所有球
    private readonly HashSet<MotBall> _targets = new(); // 真正的目标球集合
    private readonly HashSet<MotBall> _selected = new();// 玩家当前选中的球集合

    private Rect _boundsWorld;          // 计算后的场地世界坐标范围
    private int _cfgTargetCount = 3;    // 本关目标数缓存

    private bool _confirmPressed = false; // 标记是否点击了确认
    private bool _nextPressed = false;    // 标记是否点击了下一关

    // ===================== 初始化与事件绑定 =====================
    private void Awake()
    {
        HookButtons();
    }

    // 绑定按钮点击事件
    private void HookButtons()
    {
        if (btnCancel != null)
        {
            btnCancel.onClick.RemoveAllListeners();
            btnCancel.onClick.AddListener(() =>
            {
                if (CurrentPhase != Phase.Select) return;
                ClearSelections();      // 清空当前选择
                RefreshConfirmButton(); // 刷新按钮状态
            });
        }

        if (btnConfirm != null)
        {
            btnConfirm.onClick.RemoveAllListeners();
            btnConfirm.onClick.AddListener(() =>
            {
                if (CurrentPhase != Phase.Select) return;
                // 必须选满目标数才能提交
                if (_selected.Count != GetTargetCountSafe()) return;
                _confirmPressed = true;
            });
        }

        if (btnNext != null)
        {
            btnNext.onClick.RemoveAllListeners();
            btnNext.onClick.AddListener(() =>
            {
                if (CurrentPhase != Phase.Rest) return;
                _nextPressed = true;
            });
        }
    }

    private int GetTargetCountSafe() => Mathf.Max(1, _cfgTargetCount);

    // ===================== 核心功能：生成与运动 =====================

    // 计算 UI RectTransform 在世界空间中的边界
    private Rect ComputeBoundsWorld()
    {
        if (arenaRect != null)
        {
            Vector3[] corners = new Vector3[4];
            arenaRect.GetWorldCorners(corners);
            float minX = corners.Min(c => c.x);
            float maxX = corners.Max(c => c.x);
            float minY = corners.Min(c => c.y);
            float maxY = corners.Max(c => c.y);
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }
        return Rect.MinMaxRect(-3f, -1.5f, 3f, 1.5f); // 默认备用范围
    }

    // 在边界内随机取点
    private Vector2 RandomPointInBounds(Rect b)
    {
        return new Vector2(
            UnityEngine.Random.Range(b.xMin, b.xMax),
            UnityEngine.Random.Range(b.yMin, b.yMax)
        );
    }

    // 生成小球
    private void SpawnBalls(int totalBalls)
    {
        _boundsWorld = ComputeBoundsWorld();

        for (int i = 0; i < totalBalls; i++)
        {
            var pos = RandomPointInBounds(_boundsWorld);
            MotBall b = Instantiate(ballPrefab, pos, Quaternion.identity, arenaRoot);
            b.manager = this;
            b.ResetVisual();
            b.SetWorldBounds(_boundsWorld);
            b.OnClicked += OnBallClicked; // 监听小球点击
            _balls.Add(b);
        }
    }

    // 随机指定目标球
    private void AssignTargets(int targetCount)
    {
        _targets.Clear();
        var shuffled = _balls.OrderBy(_ => UnityEngine.Random.value).ToList();
        for (int i = 0; i < Mathf.Min(targetCount, shuffled.Count); i++)
            _targets.Add(shuffled[i]);
    }

    // 开始运动 (所有球随机方向)
    private void StartMoving(float speed)
    {
        foreach (var b in _balls)
        {
            Vector2 dir = UnityEngine.Random.insideUnitCircle.normalized;
            b.SetVelocity(dir * speed);
        }
    }

    // 停止运动
    private void StopMoving()
    {
        foreach (var b in _balls) b.Stop();
    }

    // ===================== 核心功能：选择逻辑 =====================

    // 当小球被点击时回调
    private void OnBallClicked(MotBall b)
    {
        if (CurrentPhase != Phase.Select) return;

        // 如果已选 -> 取消选择
        if (_selected.Contains(b))
        {
            _selected.Remove(b);
            b.SetSelected(false);
            RefreshConfirmButton();
            return;
        }

        // 如果未选且没满 -> 选中
        if (_selected.Count >= GetTargetCountSafe()) return;

        _selected.Add(b);
        b.SetSelected(true);
        RefreshConfirmButton();
    }

    // 清空所有已选
    private void ClearSelections()
    {
        foreach (var b in _selected)
            if (b != null) b.SetSelected(false);
        _selected.Clear();
    }

    // 刷新确认按钮状态 (选满才能点)
    private void RefreshConfirmButton()
    {
        if (btnConfirm != null)
            btnConfirm.interactable = (_selected.Count == GetTargetCountSafe());
    }

    // 控制选择界面的显隐 (兼容面板模式和单纯按钮模式)
    private void SetSelectUIActive(bool active)
    {
        // 1. 优先控制面板
        if (selectionPanel != null)
        {
            selectionPanel.SetActive(active);
        }
        // 2. 备用逻辑
        else
        {
            GameObject panel = null;
            if (btnConfirm != null) panel = btnConfirm.transform.parent?.gameObject;
            if (panel != null) panel.SetActive(active);
        }

        // 3. 确保按钮激活
        if (btnCancel != null) btnCancel.gameObject.SetActive(active);
        if (btnConfirm != null) btnConfirm.gameObject.SetActive(active);

        if (active) RefreshConfirmButton();
    }

    // ===================== UI 辅助函数 =====================

    // 设置顶部提示文字 (并自动控制背景显隐)
    private void SetTopHint(string txt)
    {
        if (topHint == null) return;
        topHint.text = txt;

        Transform t = topHint.transform;
        GameObject badge = null;
        // 向上查找 StageBadge 父物体
        if (t.parent != null && t.parent.name == "StageBadge") badge = t.parent.gameObject;
        else if (t.parent != null && t.parent.parent != null && t.parent.parent.name == "StageBadge") badge = t.parent.parent.gameObject;

        if (badge != null) badge.SetActive(!string.IsNullOrEmpty(txt));
    }

    // 设置倒计时显隐
    private void SetCountdownVisible(bool v)
    {
        if (countdownText == null) return;
        var badge = countdownText.transform.parent != null ? countdownText.transform.parent.gameObject : null;
        if (badge != null) badge.SetActive(v);
        countdownText.gameObject.SetActive(v);
        if (!v) countdownText.text = "";
    }

    // 设置倒计时数值
    private void SetCountdownValue(float secondsLeft)
    {
        if (countdownText == null) return;
        countdownText.text = Mathf.CeilToInt(secondsLeft).ToString();
    }

    // 设置下一关按钮显隐
    private void SetNextUIActive(bool active)
    {
        if (btnNext == null) return;

        Transform restPanel = null;
        var t = btnNext.transform;
        while (t != null)
        {
            if (t.name == "RestPanel") { restPanel = t; break; }
            t = t.parent;
        }

        if (restPanel != null) restPanel.gameObject.SetActive(active);
        btnNext.gameObject.SetActive(active);

        if (active && restPanel != null) restPanel.SetAsLastSibling();
    }

    public void SetNextButtonLabel(string label)
    {
        if (btnNext == null) return;
        var tmp = btnNext.GetComponentInChildren<TMP_Text>(true);
        if (tmp != null) tmp.text = label;
    }

    // ===================== TAR 统计计算 =====================

    // 计算中位数
    private float Median(List<float> x)
    {
        if (x == null || x.Count == 0) return float.NaN;
        x.Sort();
        int n = x.Count;
        if (n % 2 == 1) return x[n / 2];
        return 0.5f * (x[n / 2 - 1] + x[n / 2]);
    }

    // 计算一组 TAR 数据的详细统计量 (均值, 中位数, 标准差等)
    private void ComputeTarStats(List<float> buf, out float med, out float mean, out float std, out float min, out float max, out int n)
    {
        n = buf == null ? 0 : buf.Count;
        med = Median(buf);
        mean = std = min = max = float.NaN;
        if (n <= 0) return;

        min = float.PositiveInfinity;
        max = float.NegativeInfinity;
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            float v = buf[i];
            sum += v;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        mean = (float)(sum / n);
        double var = 0;
        for (int i = 0; i < n; i++)
        {
            double d = buf[i] - mean;
            var += d * d;
        }
        std = (float)Math.Sqrt(var / n);
    }

    // ===================== 公开接口 =====================

    // 供外部 (NeuroMotGame) 调用以运行一关
    public IEnumerator RunTrial(string modeName, int level, TrialConfig cfg, Action<TrialResult> onDone)
    {
        TrialResult result = default;
        // 启动核心协程
        yield return RunTrialCore(modeName, level, cfg, r => result = r);
        // 回调返回结果
        onDone?.Invoke(result);
    }

    // ===================== 核心流程控制 (协程) =====================
    private IEnumerator RunTrialCore(string modeName, int level, TrialConfig cfg, Action<TrialResult> setResult)
    {
        // 0. 发送关卡开始事件 (Python 端记录)
        if (eventSender != null)
            eventSender.SendEvent($"Level_{level}_Start_Mode_{modeName}");

        Cleanup(); // 清理上一关残留
        _cfgTargetCount = cfg.targetCount;

        // 参数准备
        bool selfPaced = cfg.selectionSeconds < 0f;
        float selectionLimit = selfPaced ? 999999f : ((cfg.selectionSeconds <= 0f) ? 8f : cfg.selectionSeconds);
        float revealSeconds = (cfg.revealSeconds <= 0f) ? 2f : cfg.revealSeconds;
        float restSeconds = (cfg.restSeconds <= 1f) ? 1f : cfg.restSeconds; // 最小休息1秒

        // 生成球 & 分配目标
        SpawnBalls(cfg.totalBalls);
        AssignTargets(cfg.targetCount);

        double trialStart = Time.realtimeSinceStartupAsDouble;
        double selectionStart = -1;
        List<float> tarBuf = new(); // 用于存储本关所有有效 TAR 数据

        // 记录上一包序号，防止重复记录
        int lastSeq = -1;
        if (tarReceiver != null) lastSeq = tarReceiver.ReceivedSeq;

        // ----------------------------------------------------
        // 1. 记忆阶段 (Highlight Phase) - [开始收集 TAR]
        // ----------------------------------------------------
        CurrentPhase = Phase.Highlight;
        SetTopHint("记忆目标");

        // 【新增】发送 Event
        if (eventSender != null) eventSender.SendEvent("Phase_Highlight");

        foreach (var b in _balls) b.SetTarget(_targets.Contains(b)); // 高亮目标

        float h_timer = cfg.highlightSeconds;
        SetCountdownVisible(true);
        while (h_timer > 0f)
        {
            h_timer -= Time.deltaTime;
            SetCountdownValue(h_timer);

            // 采集 TAR (Highlight)
            if (tarReceiver != null && tarReceiver.ReceivedSeq != lastSeq)
            {
                lastSeq = tarReceiver.ReceivedSeq;
                float tar = tarReceiver.LatestTar;
                // 仅记录非伪迹 (>0) 的有效值
                if (!float.IsNaN(tar) && tar > 0f) tarBuf.Add(tar);
            }
            yield return null;
        }
        foreach (var b in _balls) b.SetTarget(false); // 关闭高亮

        // ----------------------------------------------------
        // 2. 追踪阶段 (Track Phase) - [继续收集 TAR]
        // ----------------------------------------------------
        CurrentPhase = Phase.Track;
        SetTopHint("请追踪");

        // 【新增】发送 Event
        if (eventSender != null) eventSender.SendEvent("Phase_Track");

        StartMoving(cfg.speed);

        float t = cfg.trackingSeconds;
        SetCountdownVisible(true);
        SetCountdownValue(t);
        while (t > 0f)
        {
            t -= Time.deltaTime;
            SetCountdownValue(t);

            // 采集 TAR (Tracking)
            if (tarReceiver != null && tarReceiver.ReceivedSeq != lastSeq)
            {
                lastSeq = tarReceiver.ReceivedSeq;
                float tar = tarReceiver.LatestTar;
                if (!float.IsNaN(tar) && tar > 0f) tarBuf.Add(tar);
            }
            yield return null;
        }
        SetCountdownVisible(false);
        StopMoving();

        // ----------------------------------------------------
        // 3. 选择阶段 (Selection Phase) - [停止采集]
        // ----------------------------------------------------
        CurrentPhase = Phase.Select;
        SetTopHint("请选择");

        // 【新增】发送 Event
        if (eventSender != null) eventSender.SendEvent("Phase_Select");

        selectionStart = Time.realtimeSinceStartupAsDouble;
        _confirmPressed = false;
        ClearSelections();
        SetSelectUIActive(true); // 显示选择面板

        if (selfPaced)
        {
            // 不限时，等待点击
            while (!_confirmPressed) yield return null;
        }
        else
        {
            // 限时选择
            float sel = selectionLimit;
            SetCountdownVisible(true);
            SetCountdownValue(sel);
            while (sel > 0f && !_confirmPressed)
            {
                sel -= Time.deltaTime;
                SetCountdownValue(sel);
                yield return null;
            }
            SetCountdownVisible(false);
        }
        SetSelectUIActive(false);
        SetCountdownVisible(false);

        // 计算命中数
        int hits = 0;
        int falsePos = 0;
        foreach (var b in _selected)
        {
            if (b == null) continue;
            if (_targets.Contains(b)) hits++; else falsePos++;
        }

        // ----------------------------------------------------
        // 4. 揭晓阶段 (Reveal Phase)
        // ----------------------------------------------------
        CurrentPhase = Phase.Reveal;
        SetTopHint("结果揭晓");
        foreach (var b in _balls) b.RevealResult(_targets.Contains(b), _selected.Contains(b));
        yield return CountdownOnly(revealSeconds);

        // ----------------------------------------------------
        // 5. 休息阶段 (Rest Phase)
        // ----------------------------------------------------
        CurrentPhase = Phase.Rest;
        SetTopHint("休息一下");
        _nextPressed = false;

        // 【新增】发送关卡结束 Event
        if (eventSender != null) eventSender.SendEvent($"Level_{level}_End");

        yield return CountdownOnly(restSeconds);

        // 显示"下一关"按钮并等待点击
        if (btnNext != null)
        {
            SetNextUIActive(true);
            while (!_nextPressed) yield return null;
            SetNextUIActive(false);
        }

        // ----------------------------------------------------
        // 结算与返回
        // ----------------------------------------------------
        // 计算本关 TAR 统计量
        ComputeTarStats(tarBuf, out float tarMed, out float tarMean, out float tarStd, out float tarMin, out float tarMax, out int tarN);

        float selectionTimeSec = (selectionStart < 0) ? 0f : (float)(Time.realtimeSinceStartupAsDouble - selectionStart);
        float trialDurationSec = (float)(Time.realtimeSinceStartupAsDouble - trialStart);

        TrialResult result = new TrialResult
        {
            levelIndex = level,
            totalBalls = cfg.totalBalls,
            targetCount = cfg.targetCount,
            speed = cfg.speed,
            highlightSeconds = cfg.highlightSeconds,
            trackingSeconds = cfg.trackingSeconds,
            selectionSeconds = cfg.selectionSeconds,
            revealSeconds = cfg.revealSeconds,
            restSeconds = cfg.restSeconds,
            hits = hits,
            falsePositives = falsePos,
            acc = hits / (float)Mathf.Max(1, cfg.targetCount),
            confirmed = _confirmPressed,
            selectionTimeSec = selectionTimeSec,
            trialDurationSec = trialDurationSec,
            tarMedian = tarMed, // 核心指标：中位数
            tarMean = tarMean,
            tarStd = tarStd,
            tarMin = tarMin,
            tarMax = tarMax,
            tarN = tarN
        };
        setResult?.Invoke(result);
        Cleanup();
    }

    // 仅倒计时 helper
    private IEnumerator CountdownOnly(float seconds)
    {
        float t = Mathf.Max(0f, seconds);
        SetCountdownVisible(true);
        SetCountdownValue(t);
        while (t > 0f)
        {
            t -= Time.deltaTime;
            SetCountdownValue(t);
            yield return null;
        }
        SetCountdownVisible(false);
    }

    // 清理场景
    public void Cleanup()
    {
        SetSelectUIActive(false);
        SetNextUIActive(false);
        SetTopHint("");
        SetCountdownVisible(false);
        // 销毁所有球
        foreach (var b in _balls) if (b != null) Destroy(b.gameObject);
        _balls.Clear();
        _targets.Clear();
        _selected.Clear();
        CurrentPhase = Phase.Idle;
    }
}