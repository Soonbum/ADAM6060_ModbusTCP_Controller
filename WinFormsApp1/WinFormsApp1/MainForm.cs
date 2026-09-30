using System;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace WinFormsApp1
{
    public partial class MainForm : Form
    {
        // ADAM-6060 통신 규격 상수
        private const int MODBUS_PORT = 502;
        private const byte UNIT_ID = 1;
        private const ushort DI_START_OFFSET = 0;   // Discrete Input CH0~5 (10001~10006)
        private const ushort DO_START_OFFSET = 16;  // Coil Relay CH0~5 (00017~00022)

        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private ushort _transactionId = 0;
        private Timer _pollTimer;

        // UI 컨트롤
        private TextBox txtIp;
        private Button btnConnect;
        private Label[] lblDiIndicators = new Label[6];
        private Button[] btnDoControls = new Button[6];
        private bool[] _doStatus = new bool[6];

        public MainForm()
        {
            InitializeCustomUI();
        }

        private void InitializeCustomUI()
        {
            this.Text = "ADAM-6060 Modbus/TCP Controller";
            this.Size = new Size(520, 380);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;

            // 1. 접속 영역
            var grpConn = new GroupBox { Text = "연결 설정", Location = new Point(15, 10), Size = new Size(475, 60) };
            var lblIp = new Label { Text = "장비 IP:", Location = new Point(15, 25), AutoSize = true };
            txtIp = new TextBox { Text = "192.168.250.193", Location = new Point(70, 22), Width = 140 };
            btnConnect = new Button { Text = "접속", Location = new Point(220, 20), Width = 80, Height = 26 };
            btnConnect.Click += BtnConnect_Click;

            grpConn.Controls.AddRange(new Control[] { lblIp, txtIp, btnConnect });
            this.Controls.Add(grpConn);

            // 2. DI 감시 영역 (FC 02)
            var grpDi = new GroupBox { Text = "DI 모니터링 (Discrete Inputs: 10001 ~ 10006)", Location = new Point(15, 80), Size = new Size(475, 100) };
            for (int i = 0; i < 6; i++)
            {
                var lblTitle = new Label { Text = $"DI-{i}", Location = new Point(20 + (i * 75), 25), Width = 60, TextAlign = ContentAlignment.MiddleCenter };
                lblDiIndicators[i] = new Label
                {
                    Text = "OFF",
                    Location = new Point(20 + (i * 75), 50),
                    Size = new Size(60, 30),
                    BackColor = Color.LightGray,
                    ForeColor = Color.Black,
                    TextAlign = ContentAlignment.MiddleCenter,
                    BorderStyle = BorderStyle.FixedSingle,
                    Font = new Font(this.Font, FontStyle.Bold)
                };
                grpDi.Controls.AddRange(new Control[] { lblTitle, lblDiIndicators[i] });
            }
            this.Controls.Add(grpDi);

            // 3. DO 제어 영역 (FC 01 / FC 05, Coil: 00017 ~ 00022)
            var grpDo = new GroupBox { Text = "DO 릴레이 제어 (Coils: 00017 ~ 00022, Offset 16 ~ 21)", Location = new Point(15, 190), Size = new Size(475, 110) };
            for (int i = 0; i < 6; i++)
            {
                int ch = i;
                btnDoControls[i] = new Button
                {
                    Text = $"DO-{ch}\nOFF",
                    Location = new Point(20 + (i * 75), 30),
                    Size = new Size(65, 55),
                    BackColor = Color.LightGray,
                    Font = new Font(this.Font, FontStyle.Bold),
                    Enabled = false
                };
                btnDoControls[i].Click += async (s, e) => await ToggleDoRelayAsync(ch);
                grpDo.Controls.Add(btnDoControls[i]);
            }
            this.Controls.Add(grpDo);

            // 주기적 폴링 타이머 (300ms 주기)
            _pollTimer = new Timer { Interval = 300 };
            _pollTimer.Tick += async (s, e) => await PollDeviceDataAsync();

            this.FormClosing += (s, e) => Disconnect();
        }

        // --- 접속 및 해제 처리 ---
        private void BtnConnect_Click(object sender, EventArgs e)
        {
            if (_tcpClient != null && _tcpClient.Connected)
            {
                Disconnect();
            }
            else
            {
                Connect();
            }
        }

        private void Connect()
        {
            try
            {
                _tcpClient = new TcpClient();
                _tcpClient.Connect(txtIp.Text.Trim(), MODBUS_PORT);
                _stream = _tcpClient.GetStream();

                btnConnect.Text = "접속 해제";
                btnConnect.BackColor = Color.MistyRose;
                txtIp.Enabled = false;

                foreach (var btn in btnDoControls) btn.Enabled = true;

                _pollTimer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"연결 실패: {ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Disconnect();
            }
        }

        private void Disconnect()
        {
            _pollTimer.Stop();

            _stream?.Close();
            _tcpClient?.Close();
            _stream = null;
            _tcpClient = null;

            btnConnect.Text = "접속";
            btnConnect.BackColor = SystemColors.Control;
            txtIp.Enabled = true;

            for (int i = 0; i < 6; i++)
            {
                lblDiIndicators[i].BackColor = Color.LightGray;
                lblDiIndicators[i].Text = "OFF";
                btnDoControls[i].BackColor = Color.LightGray;
                btnDoControls[i].Text = $"DO-{i}\nOFF";
                btnDoControls[i].Enabled = false;
            }
        }

        private readonly System.Threading.SemaphoreSlim _lock = new System.Threading.SemaphoreSlim(1, 1);

        // --- 데이터 폴링 (DI 6채널, DO 6채널 상태 읽기) ---
        private async Task PollDeviceDataAsync()
        {
            if (_stream == null || !_tcpClient.Connected) return;

            try
            {
                // 1. DI 상태 읽기 (Function Code 02, Discrete Inputs 오프셋 0부터 6개)
                bool[] diStates = await ReadInputsAsync(DI_START_OFFSET, 6);
                for (int i = 0; i < 6; i++)
                {
                    lblDiIndicators[i].BackColor = diStates[i] ? Color.FromArgb(46, 204, 113) : Color.LightGray;
                    lblDiIndicators[i].Text = diStates[i] ? "ON" : "OFF";
                    lblDiIndicators[i].ForeColor = diStates[i] ? Color.White : Color.Black;
                }

                // 2. DO 현재 릴레이 상태 읽기 (Function Code 01, Coils 오프셋 16부터 6개)
                bool[] doStates = await ReadCoilsAsync(DO_START_OFFSET, 6);
                for (int i = 0; i < 6; i++)
                {
                    _doStatus[i] = doStates[i];
                    btnDoControls[i].BackColor = doStates[i] ? Color.FromArgb(231, 76, 60) : Color.LightGray;
                    btnDoControls[i].Text = $"DO-{i}\n{(doStates[i] ? "ON" : "OFF")}";
                    btnDoControls[i].ForeColor = doStates[i] ? Color.White : Color.Black;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"폴링 통신 오류: {ex.Message}");
                Disconnect();
            }
        }

        // --- DO 릴레이 ON/OFF 토글 (Function Code 05) ---
        private async Task ToggleDoRelayAsync(int channel)
        {
            if (_stream == null || !_tcpClient.Connected) return;

            ushort coilOffset = (ushort)(DO_START_OFFSET + channel);
            bool targetState = !_doStatus[channel];

            try
            {
                await WriteSingleCoilAsync(coilOffset, targetState);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"DO 제어 오류: {ex.Message}");
            }
        }

        // ==============================================================================
        // Modbus/TCP 프로토콜 저수준 구현 (인덱스 보정 및 세마포어 적용)
        // ==============================================================================

        // Function Code 02: Read Discrete Inputs (DI)
        private async Task<bool[]> ReadInputsAsync(ushort startAddress, ushort count)
        {
            byte[] pdu = new byte[]
            {
                0x02,
                (byte)(startAddress >> 8), (byte)(startAddress & 0xFF),
                (byte)(count >> 8), (byte)(count & 0xFF)
            };

            byte[] fullResponse = await SendModbusRequestAsync(pdu);

            // fullResponse 구조:
            // [0~6]: MBAP Header
            // [7]: Function Code (0x02)
            // [8]: Byte Count
            // [9]: Data Byte
            byte dataByte = fullResponse[9];

            bool[] result = new bool[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = ((dataByte >> i) & 0x01) == 1;
            }
            return result;
        }

        // Function Code 01: Read Coils (DO 상태 읽기)
        private async Task<bool[]> ReadCoilsAsync(ushort startAddress, ushort count)
        {
            byte[] pdu = new byte[]
            {
                0x01,
                (byte)(startAddress >> 8), (byte)(startAddress & 0xFF),
                (byte)(count >> 8), (byte)(count & 0xFF)
            };

            byte[] fullResponse = await SendModbusRequestAsync(pdu);
            byte dataByte = fullResponse[9];

            bool[] result = new bool[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = ((dataByte >> i) & 0x01) == 1;
            }
            return result;
        }

        // Function Code 05: Write Single Coil (DO 릴레이 제어)
        private async Task WriteSingleCoilAsync(ushort coilAddress, bool state)
        {
            ushort coilValue = state ? (ushort)0xFF00 : (ushort)0x0000;
            byte[] pdu = new byte[]
            {
                0x05,
                (byte)(coilAddress >> 8), (byte)(coilAddress & 0xFF),
                (byte)(coilValue >> 8), (byte)(coilValue & 0xFF)
            };

            await SendModbusRequestAsync(pdu);
        }

        // 패킷 전송 및 동기화 응답 수신
        private async Task<byte[]> SendModbusRequestAsync(byte[] pdu)
        {
            await _lock.WaitAsync();
            try
            {
                if (_stream == null || !_tcpClient.Connected)
                    throw new InvalidOperationException("소켓이 닫혀 있습니다.");

                ushort tId = ++_transactionId;
                ushort length = (ushort)(1 + pdu.Length); // Unit ID(1) + PDU

                byte[] packet = new byte[7 + pdu.Length];
                packet[0] = (byte)(tId >> 8);
                packet[1] = (byte)(tId & 0xFF);
                packet[2] = 0x00; // Modbus Protocol
                packet[3] = 0x00;
                packet[4] = (byte)(length >> 8);
                packet[5] = (byte)(length & 0xFF);
                packet[6] = UNIT_ID;

                Array.Copy(pdu, 0, packet, 7, pdu.Length);

                await _stream.WriteAsync(packet, 0, packet.Length);

                // MBAP 헤더 7바이트 먼저 수신
                byte[] headerBuffer = new byte[7];
                await ReadExactAsync(_stream, headerBuffer, 7);

                // 응답 PDU 길이 계산 (Length 바이트 - UnitID 1바이트)
                ushort remainingLen = (ushort)(((headerBuffer[4] << 8) | headerBuffer[5]) - 1);
                byte[] pduBuffer = new byte[remainingLen];
                await ReadExactAsync(_stream, pduBuffer, remainingLen);

                // 최종 조합: 헤더(7B) + PDU Buffer
                byte[] fullResponse = new byte[7 + remainingLen];
                Array.Copy(headerBuffer, 0, fullResponse, 0, 7);
                Array.Copy(pduBuffer, 0, fullResponse, 7, remainingLen);

                // Modbus Exception 응답 확인 (FC + 0x80)
                if (fullResponse[7] >= 0x80)
                {
                    byte errCode = fullResponse[8];
                    throw new Exception($"Modbus 에러 응답 수신 (Exception Code: {errCode:X2})");
                }

                return fullResponse;
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task ReadExactAsync(Stream stream, byte[] buffer, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer, totalRead, count - totalRead);
                if (read == 0) throw new SocketException((int)SocketError.ConnectionReset);
                totalRead += read;
            }
        }
    }
}