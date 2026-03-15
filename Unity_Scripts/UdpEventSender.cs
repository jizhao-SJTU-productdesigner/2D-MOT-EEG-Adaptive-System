using System.Net.Sockets;
using System.Text;
using UnityEngine;
using System.Net;

public class UdpEventSender : MonoBehaviour
{
    [Header("Configuration")]
    public string targetIP = "127.0.0.1"; // 本地 IP
    public int targetPort = 5006;         // 对应 Python 的 UNITY_PORT

    private UdpClient _client;

    void Awake()
    {
        _client = new UdpClient();
    }

    /// <summary>
    /// 发送字符串事件给 Python
    /// </summary>
    public void SendEvent(string eventMsg)
    {
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(eventMsg);
            // 异步发送，防止卡顿主线程
            _client.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Parse(targetIP), targetPort));
            // Debug.Log($"[UDP Send] {eventMsg}"); 
        }
        catch (System.Exception e)
        {
            Debug.LogError($"UDP Send Error: {e.Message}");
        }
    }

    void OnDestroy()
    {
        if (_client != null) _client.Close();
    }
}