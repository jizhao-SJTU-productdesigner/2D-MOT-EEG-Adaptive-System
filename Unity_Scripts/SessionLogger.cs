using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class SessionLogger : MonoBehaviour
{
    // 定义每一关的数据结构
    [Serializable]
    public class TrialRecord
    {
        public int level;
        public int totalBalls;
        public int targetCount;
        public float speed;
        public int correct;
        public int wrong;
        public float accuracy;
        public float tarMedian;
        public float selectionTimeSec;
    }

    // 【新增】SessionSummary 类定义，ResultPanel 会用到
    [Serializable]
    public class SessionSummary
    {
        public string participantId;
        public string modeName;
        public float accuracy;
        public int totalHits;
        public int totalTargets;
        public int falsePositives;
        public float peakSpeed;
        public int peakTargets;
        public int peakSetSize;
        public float taskTimeSec;
        public float avgResponseTimeSec;
        public float trackTarMedian;
        public float baselineEOMedian;
        public float baselineECMedian;
     
    }
        // 【新增 1】存储静息态数据的变量
    private float _baselineEO = float.NaN;
    private float _baselineEC = float.NaN;

    private readonly List<TrialRecord> _trials = new List<TrialRecord>();
    private string _startTime;
    private string _participantId;
    private string _modeName;

    public void BeginSession(string participantId, string modeName)
    {
        _participantId = participantId;
        _modeName = modeName;
        _trials.Clear();
        _startTime = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        Debug.Log($"[SessionLogger] Session Started for ID: {_participantId}, Mode: {_modeName}");
    }

    // 【新增 2】接收静息态数据的方法
    public void SetBaselines(float eo, float ec)
    {
        _baselineEO = eo;
        _baselineEC = ec;
    }

    // 【修复】参数数量改为 9 个，与 NeuroMotGame 调用一致
    public void LogTrial(int level, int totalBalls, int targetCount, float speed, int correct, int wrong, float acc, float tar, float time)
    {
        _trials.Add(new TrialRecord
        {
            level = level,
            totalBalls = totalBalls,
            targetCount = targetCount,
            speed = speed,
            correct = correct,
            wrong = wrong,
            accuracy = acc,
            tarMedian = tar,
            selectionTimeSec = time
        });
    }

    /// <summary>
    /// 保存数据到 D 盘指定目录，按被试 ID 分文件夹
    /// </summary>
    public string SaveToExcel()
    {
        string rootPath = @"D:\AAA毕设文件\2D-MOT游戏被试数据汇总";

        if (!Directory.Exists(rootPath)) Directory.CreateDirectory(rootPath);

        string participantFolder = Path.Combine(rootPath, _participantId);
        if (!Directory.Exists(participantFolder)) Directory.CreateDirectory(participantFolder);

        // ================= 保存 Details (详细数据) =================
        string detailsFileName = $"Details_{_participantId}_{_modeName}_{_startTime}.csv";
        string detailsPath = Path.Combine(participantFolder, detailsFileName);

        using (StreamWriter sw = new StreamWriter(detailsPath))
        {
            sw.WriteLine("Level,TotalBalls,Targets,Speed,Correct,Wrong,Accuracy,TAR_Median,ResponseTime(s)");
            foreach (var t in _trials)
            {
                string line = $"{t.level},{t.totalBalls},{t.targetCount},{t.speed:F2},{t.correct},{t.wrong},{t.accuracy:F2},{t.tarMedian:F2},{t.selectionTimeSec:F2}";
                sw.WriteLine(line);
            }
        }

        // ================= 保存 Summary (总汇总表) =================
        string summaryPath = Path.Combine(rootPath, "All_Sessions_Summary.csv");
        bool isNewFile = !File.Exists(summaryPath);

        // 计算统计值
        float sumAcc = 0;
        float maxSpd = 0;
        float sumTime = 0;
        float sumTar = 0;
        int validTarCount = 0;

        foreach (var t in _trials)
        {
            sumAcc += t.accuracy;
            if (t.speed > maxSpd) maxSpd = t.speed;
            sumTime += t.selectionTimeSec;
            if (!float.IsNaN(t.tarMedian) && t.tarMedian > 0)
            {
                sumTar += t.tarMedian;
                validTarCount++;
            }
        }

        int n = _trials.Count > 0 ? _trials.Count : 1;
        float avgAcc = sumAcc / n;
        float avgTime = sumTime / n;
        float avgTarFinal = validTarCount > 0 ? sumTar / validTarCount : 0;

        using (StreamWriter sw = new StreamWriter(summaryPath, true))
        {
            if (isNewFile)
            {
                // 【修改 3A】表头增加两列：Baseline_EO, Baseline_EC
                sw.WriteLine("Date,ParticipantID,Mode,LevelsCompleted,Avg_Accuracy,Peak_Speed,Avg_ResponseTime,Avg_TAR,Baseline_EO,Baseline_EC");
            }
            // 【修改 3B】数据行把变量写进去
            string summaryLine = $"{DateTime.Now:yyyy-MM-dd HH:mm},{_participantId},{_modeName},{n},{avgAcc:F2},{maxSpd:F2},{avgTime:F2},{avgTarFinal:F2},{_baselineEO:F2},{_baselineEC:F2}";
            sw.WriteLine(summaryLine);
        }

        Debug.Log($"[SessionLogger] Data saved to: {participantFolder}");
        return participantFolder;
    }
}