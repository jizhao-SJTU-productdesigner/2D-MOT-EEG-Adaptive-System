using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using System;

public class TarReceiverUdp : MonoBehaviour
{
    [Header("Configuration")]
    public int listenPort = 5005; // 对应 Python 的 UNITY_PORT

    [Header("Debug View")]
    public float LatestTar = 0f;
    public bool IsArtifact = false;
    public int ReceivedSeq = 0;
    public int ValidPacketsCount = 0;

    private UdpClient _udpClient;
    private Thread _recvThread;
    private volatile bool _isRunning; // volatile 保证线程安全

    // 用于 Track 阶段的临时缓存
    private List<float> _trackBuffer = new List<float>();
    private bool _isTracking = false;

    [Serializable]
    private class TarPacket
    {
        public int seq;
        public float tar;
        public bool artifact;
        public double ts;
    }

    void Start()
    {
        _isRunning = true;
        _recvThread = new Thread(ReceiveLoop);
        _recvThread.IsBackground = true;
        _recvThread.Start();
    }

    private void ReceiveLoop()
    {
        try
        {
            _udpClient = new UdpClient(listenPort);
            // 设置超时，避免一直卡在 Receive
            _udpClient.Client.ReceiveTimeout = 1000;
            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

            while (_isRunning)
            {
                try
                {
                    // Receive 是阻塞的，直到收到数据或 Socket 关闭
                    byte[] data = _udpClient.Receive(ref remoteEP);

                    if (data.Length > 0)
                    {
                        string json = Encoding.UTF8.GetString(data);
                        ParseAndStore(json);
                    }
                }
                catch (SocketException)
                {
                    // Socket 超时或被关闭，忽略，继续检查 _isRunning
                }
            }
        }
        catch (ThreadAbortException)
        {
            // 线程被强制终止，属于正常退出流程，不打印错误
        }
        catch (Exception e)
        {
            // 【修复关键点】只有在应当运行时才报错
            // 如果 _isRunning 已经是 false，说明是我们主动关的，不需要报错
            if (_isRunning)
            {
                Debug.LogWarning($"[UDP] Error: {e.Message}");
            }
        }
        finally
        {
            if (_udpClient != null) _udpClient.Close();
        }
    }

    private void ParseAndStore(string json)
    {
        try
        {
            TarPacket packet = JsonUtility.FromJson<TarPacket>(json);

            if (!packet.artifact && packet.tar > 0)
            {
                LatestTar = packet.tar;
                IsArtifact = false;

                if (_isTracking)
                {
                    lock (_trackBuffer)
                    {
                        _trackBuffer.Add(packet.tar);
                    }
                }
            }
            else
            {
                IsArtifact = true;
            }

            ReceivedSeq = packet.seq;
            ValidPacketsCount++;
        }
        catch
        {
            // 解析失败忽略
        }
    }

    // ================== 公开接口 ==================

    public void StartTracking()
    {
        lock (_trackBuffer)
        {
            _trackBuffer.Clear();
        }
        _isTracking = true;
        // Debug.Log("[TarReceiver] Start Tracking...");
    }

    public float StopAndGetMedian()
    {
        _isTracking = false;

        float median = float.NaN;
        lock (_trackBuffer)
        {
            int count = _trackBuffer.Count;
            if (count > 0)
            {
                _trackBuffer.Sort();
                median = _trackBuffer[count / 2];
            }
            // Debug.Log($"[TarReceiver] Stop Tracking. Count={count}, Median={median:F3}");
            _trackBuffer.Clear();
        }
        return median;
    }

    void OnDestroy()
    {
        // 1. 先设置标志位
        _isRunning = false;

        // 2. 关闭 UDP Client，这会强制中断 ReceiveLoop 中的 Receive 阻塞
        if (_udpClient != null)
        {
            try { _udpClient.Close(); } catch { }
            _udpClient = null;
        }

        // 3. 最后再尝试 Abort (可选，作为双重保险)
        if (_recvThread != null && _recvThread.IsAlive)
        {
            // 等待一小会儿让它自己退出
            if (!_recvThread.Join(200))
            {
                _recvThread.Abort();
            }
        }
    }
}