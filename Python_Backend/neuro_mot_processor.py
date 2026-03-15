# neuro_mot_processor.py
# 基于心算预实验 bci_tar_recorder_math.py 改造
# 功能：LSL接收 EEG -> 信号处理 -> TAR计算 -> UDP发给Unity -> CSV日志闭环

import os, sys, time, json, socket, threading, csv
import numpy as np
from collections import deque
from pylsl import resolve_byprop, StreamInlet, local_clock
from scipy.signal import butter, lfilter, lfilter_zi, welch

# ===================== 1. 参数锁定 (Protocol Lock-in) =====================
# 硬件与LSL
FS = 250
CHANNELS = ['O1', 'O2']  # 仅处理这两个通道
LSL_STREAM_NAME = None   # None表示自动搜索type='EEG'
LSL_TIMEOUT = 5

# 滤波
NOTCH_HZ = 50.0
BP_LOW = 1.0
BP_HIGH = 40.0
BP_ORDER = 4

# 特征计算
WIN_LEN_SEC = 2.0
STEP_SEC = 0.25          # 保持 4Hz 输出，Unity端做聚合更稳健
THETA_BAND = (4.0, 7.0)
ALPHA_BAND = (8.0, 13.0) # 保持与预实验一致 (8-13Hz)
COMBINE_MODE = 'sum'     # O1+O2 功率聚合

# 伪迹检测 (严格复现预实验 RUN_META.json)
ARTIFACT_AMP_UV = 220.0 #220.0
ARTIFACT_STD_UV = 70.0 #70.0
MAD_Z_THRESH = 5.0
USE_MAD = True

# 通信
UNITY_IP = "127.0.0.1"
UNITY_PORT = 5005        # 发送给 Unity
LISTEN_PORT = 5006       # 监听 Unity 发回的事件

# 日志
# 注意：前面加 r 是为了防止反斜杠转义，路径要和 Unity 保持完全一致
DATA_DIR = r"D:\AAA毕设文件\2D-MOT游戏被试数据汇总"

# ===================== 2. 信号处理算法类 =====================
class SignalProcessor:
    def __init__(self):
        # 滤波器初始化
        nyq = 0.5 * FS
        self.b_bp, self.a_bp = butter(BP_ORDER, [BP_LOW/nyq, BP_HIGH/nyq], btype='band')
        self.b_notch, self.a_notch = butter(2, [ (NOTCH_HZ-2)/nyq, (NOTCH_HZ+2)/nyq ], btype='bandstop')
        
        # 实时滤波状态 (zi)
        self.zi_bp = np.zeros((2, 2 * BP_ORDER)) # 2 channels
        self.zi_notch = np.zeros((2, 2 * 2))

    def online_filter(self, new_chunk, zi_bp, zi_notch):
        # new_chunk: (n_samples, n_ch)
        filtered = np.zeros_like(new_chunk)
        for i in range(new_chunk.shape[1]):
            # 1. Notch
            tmp, zi_notch[i] = lfilter(self.b_notch, self.a_notch, new_chunk[:, i], zi=zi_notch[i])
            # 2. Bandpass
            filtered[:, i], zi_bp[i] = lfilter(self.b_bp, self.a_bp, tmp, zi=zi_bp[i])
        return filtered

    def detect_artifact(self, epoch):
        # epoch: (n_samples, n_ch)
        # 1. Amplitude check
        max_amp = np.max(np.abs(epoch))
        if max_amp > ARTIFACT_AMP_UV:
            return True, "AMP"
        
        # 2. Std check
        std_val = np.max(np.std(epoch, axis=0))
        if std_val > ARTIFACT_STD_UV:
            return True, "STD"

        # 3. MAD check (简易版，参考预实验逻辑)
        if USE_MAD:
            med = np.median(epoch, axis=0)
            mad = np.median(np.abs(epoch - med), axis=0)
            # 防止 mad 为 0
            mad[mad == 0] = 1e-9
            z_score = 0.6745 * (epoch - med) / mad
            if np.max(np.abs(z_score)) > MAD_Z_THRESH:
                return True, "MAD"
        
        return False, "OK"

    def calc_tar(self, epoch):
        # epoch: (n_samples, n_ch)
        nperseg = len(epoch)
        freqs, psd = welch(epoch, fs=FS, window='hamming', nperseg=nperseg, axis=0)
        
        # 频带积分
        idx_theta = np.logical_and(freqs >= THETA_BAND[0], freqs <= THETA_BAND[1])
        idx_alpha = np.logical_and(freqs >= ALPHA_BAND[0], freqs <= ALPHA_BAND[1])
        
        # 积分 (简单求和近似)
        pow_theta = np.sum(psd[idx_theta, :], axis=0)
        pow_alpha = np.sum(psd[idx_alpha, :], axis=0)
        
        # 聚合
        if COMBINE_MODE == 'sum':
            p_theta_ag = np.sum(pow_theta)
            p_alpha_ag = np.sum(pow_alpha)
        else: # mean
            p_theta_ag = np.mean(pow_theta)
            p_alpha_ag = np.mean(pow_alpha)
            
        tar = p_theta_ag / (p_alpha_ag + 1e-9)
        return tar, p_theta_ag, p_alpha_ag

# ===================== 3. 主程序 =====================
def main():
    # --- 初始化连接 ---
    print(">>> 正在搜索 LSL 流 (type='EEG')...")
    streams = resolve_byprop('type', 'EEG', timeout=LSL_TIMEOUT)
    if not streams:
        print("[ERROR] 未找到 EEG 流，请检查 OpenBCI GUI Networking 是否开启！")
        return
    
    inlet = StreamInlet(streams[0])
    print(f">>> 已连接流: {inlet.info().name()}")
    
    # --- 初始化 UDP ---
    sock_send = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock_recv = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock_recv.bind(("0.0.0.0", LISTEN_PORT))
    sock_recv.setblocking(False)
    
    # --- 日志文件 ---
    subject = input("请输入被试编号 (如 S01): ").strip() or "TEST"
    session_time = time.strftime("%Y%m%d_%H%M%S")
    os.makedirs(f"{DATA_DIR}/{subject}", exist_ok=True)
    
    # 两个日志：Features (特征) 和 Events (事件)
    f_feat_path = f"{DATA_DIR}/{subject}/{subject}_{session_time}_FEATURES.csv"
    f_evt_path = f"{DATA_DIR}/{subject}/{subject}_{session_time}_EVENTS.csv"
    
    f_feat = open(f_feat_path, 'w', newline='')
    w_feat = csv.writer(f_feat)
    w_feat.writerow(["lsl_time", "tar", "theta", "alpha", "artifact", "unity_event_tag"])
    
    f_evt = open(f_evt_path, 'w', newline='')
    w_evt = csv.writer(f_evt)
    w_evt.writerow(["sys_time", "unity_event_json"])

    print(f">>> 日志记录中: {f_feat_path}")

    # --- 缓冲区与状态 ---
    processor = SignalProcessor()
    buffer = deque(maxlen=int(WIN_LEN_SEC * FS)) # 2秒缓冲
    
    samples_since_process = 0
    process_interval = int(STEP_SEC * FS) # 0.25s * 250 = 62 samples
    
    last_unity_event = ""
    packet_seq = 0
    
    zi_bp = processor.zi_bp
    zi_notch = processor.zi_notch
    
    print(">>> 开始处理... (按 Ctrl+C 停止)")
    
    try:
        while True:
            # 1. 接收 Unity 事件 (非阻塞)
            try:
                data, _ = sock_recv.recvfrom(1024)
                msg = data.decode('utf-8')
                last_unity_event = msg # 缓存最新的事件标签用于特征打标
                w_evt.writerow([time.time(), msg]) # 立即记录事件
                f_evt.flush()
                print(f"[EVENT] 收到 Unity 事件: {msg}")
            except BlockingIOError:
                pass

            # 2. 拉取 LSL 数据
            chunk, timestamps = inlet.pull_chunk(timeout=0.1)
            if timestamps:
                # 提取 O1, O2 (假设是前两个通道，如果不是请修改索引)
                # OpenBCI Default: 1-8 are EEG. We assume GUI sends all. 
                # 这里假设你只开了2个通道或者我们要取 index 0 和 1 (对应 Cyton ch1, ch2)
                # 如果你的 O1/O2 插在其他孔，请修改这里！比如 ch5, ch6 -> [:, 4:6]
                eeg_data = np.array(chunk)[:, 0:2] 
                
                # 实时滤波
                filtered_chunk = processor.online_filter(eeg_data, zi_bp, zi_notch)
                
                # 存入缓冲
                for sample in filtered_chunk:
                    buffer.append(sample)
                    samples_since_process += 1
                
                # 3. 达到步长 (0.25s)，进行计算
                if samples_since_process >= process_interval:
                    samples_since_process = 0
                    
                    if len(buffer) == buffer.maxlen:
                        epoch = np.array(buffer)
                        
                        # 伪迹检测
                        is_artifact, reason = processor.detect_artifact(epoch)
                        
                        tar_val = -1.0
                        theta_val = 0.0
                        alpha_val = 0.0
                        
                        if not is_artifact:
                            tar_val, theta_val, alpha_val = processor.calc_tar(epoch)
                        
                        # 4. 发送 UDP 给 Unity
                        packet = {
                            "seq": packet_seq,
                            "tar": float(tar_val), # -1 表示无效/伪迹
                            "artifact": is_artifact,
                            "ts": timestamps[-1]
                        }
                        sock_send.sendto(json.dumps(packet).encode('utf-8'), (UNITY_IP, UNITY_PORT))
                        packet_seq += 1
                        
                        # 5. 记录特征日志
                        # 这里的 last_unity_event 能帮你把 TAR 和 关卡 对应起来
                        w_feat.writerow([timestamps[-1], f"{tar_val:.4f}", f"{theta_val:.2f}", f"{alpha_val:.2f}", int(is_artifact), last_unity_event])
                        
                        # 终端显示
                        status = f"ART({reason})" if is_artifact else f"TAR: {tar_val:.3f}"
                        sys.stdout.write(f"\rSeq: {packet_seq} | {status} | Event: {last_unity_event[:10]}   ")
                        sys.stdout.flush()
                        
    except KeyboardInterrupt:
        print("\n>>> 停止录制")
    finally:
        f_feat.close()
        f_evt.close()
        sock_send.close()
        sock_recv.close()

if __name__ == "__main__":
    main()