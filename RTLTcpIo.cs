using System;
using System.Windows.Forms;
using System.Net.Sockets;
using System.Threading;
using SDRSharp.Radio;

namespace SDRSharp.RTLTCP
{
    public enum RtlSdrTunerType
    {
        Unknown = 0,
        E4000,
        FC0012,
        FC0013,
        FC2580,
        R820T
    }

    public unsafe class RtlTcpIO : IFrontendController, IIQStreamController, ITunableSource, IFloatingConfigDialogProvider, IDisposable
	{
        private const int DongleInfoLength = 12;
        private const string DefaultHost = "127.0.0.1";
        private const short DefaultPort = 1234;
        private const uint DefaultFrequency = 100000000;
        private readonly static int BufferSize = Utils.GetIntSetting("RTLTcpBufferLength", 32 * 1024);
        
        #region Native rtl_tcp Commands

        private const byte CMD_SET_FREQ = 0x1;
        private const byte CMD_SET_SAMPLE_RATE = 0x2;
        private const byte CMD_SET_TUNER_GAIN_MODE = 0x3;
        private const byte CMD_SET_GAIN = 0x4;
        private const byte CMD_SET_FREQ_COR = 0x5;
        private const byte CMD_SET_AGC_MODE = 0x8;
        private const byte CMD_SET_SAMPLING_MODE = 0x9;
        private const byte CMD_SET_OFFSET_TUNING = 0xa;
        private const byte CMD_SET_TUNER_GAIN_INDEX = 0xd;

        #endregion

        private static readonly float* m_lutPtr;
        private static readonly UnsafeBuffer m_lutBuffer = UnsafeBuffer.Create(256, sizeof(float));

        private long m_frequency = DefaultFrequency;
        private double m_sampleRate;
        private string m_host;
        private int m_port;
        private bool m_useRtlAGC;
        private bool m_useTunerAGC;
        private bool m_offsetTuning;
        private int m_useTunerAGCE4K = 1;
        private int m_rtlSamplingMode = 0;
        private uint m_tunerGainIndex;
        private uint m_tunerGainCount;
        private uint m_tunerType;
        private int m_frequencyCorrection;
        private SamplesAvailableDelegate m_callback;
        private Thread m_sampleThread;
        private UnsafeBuffer m_iqBuffer;        
        private Complex* m_iqBufferPtr;
        private Socket m_socket;
        private readonly byte [] m_cmdBuffer = new byte[5];
        private readonly RTLTcpSettings m_gui;
                            
        #region Public Properties

        public bool IsStreaming
        {
            get { return m_sampleThread != null; }
        }

        public bool IsSoundCardBased
        {
            get { return false; }
        }

        public string SoundCardHint
        {
            get { return string.Empty; }
        }

        public RtlSdrTunerType TunerType
        {
            get { return (RtlSdrTunerType) m_tunerType; }
        }

        public void ShowSettingGUI(IWin32Window parent)
        {
            m_gui.Show();
        }

        public void HideSettingGUI()
        {
            m_gui.Hide();
        }

        public double Samplerate
        {
            get { return m_sampleRate; }
            set
            {
                m_sampleRate = value;
                SendCommand(CMD_SET_SAMPLE_RATE, (uint) m_sampleRate);
            }
        }

		public bool CanTune
		{
			get
			{
				return true;
			}
		}

		public long MinimumTunableFrequency
		{
			get
			{
				return 0L;
			}
		}

		public long MaximumTunableFrequency
		{
			get
			{
				return 2500000000L;
			}
		}

		public long Frequency
        {
            get { return m_frequency; }
            set
            {
                m_frequency = value;
                SendCommand(CMD_SET_FREQ, (uint) m_frequency);
            }
        }

        public int FrequencyCorrection
        {
            get { return m_frequencyCorrection; }
            set
            {
                m_frequencyCorrection = value;
                SendCommand(CMD_SET_FREQ_COR, m_frequencyCorrection);
            }
        }

        public bool UseRtlAGC
        {
            get { return m_useRtlAGC; }
            set
            {
                m_useRtlAGC = value;
                SendCommand(CMD_SET_AGC_MODE, m_useRtlAGC ? 1 : 0);
            }
        }

        public int RtlSamplingMode
        {
            get { return m_rtlSamplingMode; }
            set
            {
                m_rtlSamplingMode = value;
                SendCommand(CMD_SET_SAMPLING_MODE, (m_rtlSamplingMode));
            }
        }

        public bool UseTunerAGC
        {
            get { return m_useTunerAGC; }
            set
            {
                m_useTunerAGC = value;
                SendCommand(CMD_SET_TUNER_GAIN_MODE, m_useTunerAGC ? 0 : m_useTunerAGCE4K);
            }
        }

        public bool UseOffsetTuning
        {
            get { return m_offsetTuning; }
            set
            {
                m_offsetTuning = value;
                SendCommand(CMD_SET_OFFSET_TUNING, m_offsetTuning ? 1 : 0);
            }
        }

        public int UseTunerAGCE4K
        {
            get { return m_useTunerAGCE4K; }
            set
            {
                m_useTunerAGCE4K = value;
                SendCommand(CMD_SET_TUNER_GAIN_MODE, m_useTunerAGC ? 0 : m_useTunerAGCE4K);
                SendCommand(CMD_SET_SAMPLING_MODE, 1);
                SendCommand(CMD_SET_SAMPLING_MODE, 0);
            }
        }

        public uint TunerGainIndex
        {
            get { return m_tunerGainIndex; }
            set
            {
                m_tunerGainIndex = value;
                SendCommand(CMD_SET_TUNER_GAIN_INDEX, (int)m_tunerGainIndex);
            }
        }

        public uint TunerGainCount
        {
            get { return m_tunerGainCount; }
        }

        #endregion

        static RtlTcpIO()
        {
            m_lutPtr = (float*) m_lutBuffer;

            const float scale = 1.0f / 127.5f;
            for (var i = 0; i < 256; i++)
            {
                m_lutPtr[i] = (i - 127.5f) * scale;
            }
        }

        public RtlTcpIO()
        {
            m_gui = new RTLTcpSettings(this);
            m_gui.Hostname = Utils.GetStringSetting("RTLTcpHost", DefaultHost);
            m_gui.Port = Utils.GetIntSetting("RTLTcpPort", DefaultPort);
            m_frequency = DefaultFrequency;          
        }

        ~RtlTcpIO()
        {
            Dispose();
        }
                
        public void Dispose()
        {
            if (m_iqBuffer != null)
            {
                m_iqBuffer.Dispose();
                m_iqBuffer = null;
                m_iqBufferPtr = null;
            }
            if (m_gui != null)
            {
                m_gui.Dispose();                
            }
            GC.SuppressFinalize(this);
        }

        public void Open()
        {
        }

        public void Close()
        {
            if (m_socket != null)
            {
                m_socket.Close();
                m_socket = null;
            }
        }

        public void Start(SamplesAvailableDelegate callback)
        {
            m_callback = callback;
            m_host = m_gui.Hostname;
            m_port = m_gui.Port;
            m_socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            m_socket.NoDelay = true;
            m_socket.Connect(m_host, m_port);

            var dongleInfo = new byte[DongleInfoLength];

            var length = m_socket.Receive(dongleInfo, 0, DongleInfoLength, SocketFlags.None);
            if (length > 0)
            {
                ParseDongleInfo(dongleInfo);
            }
            
            SendCommand(CMD_SET_SAMPLE_RATE, (uint)m_sampleRate);
            SendCommand(CMD_SET_FREQ_COR, m_frequencyCorrection);
            SendCommand(CMD_SET_FREQ, (uint)m_frequency);
            SendCommand(CMD_SET_AGC_MODE, (uint)(m_useRtlAGC ? 1 : 0));
            SendCommand(CMD_SET_SAMPLING_MODE, (m_rtlSamplingMode));
            SendCommand(CMD_SET_OFFSET_TUNING, m_offsetTuning ? 1 : 0);
            SendCommand(CMD_SET_TUNER_GAIN_MODE, (uint)(m_useTunerAGC ? 0 : m_useTunerAGCE4K));
            SendCommand(CMD_SET_TUNER_GAIN_INDEX, (m_tunerGainIndex));

            m_sampleThread = new Thread(RecieveSamples);            
            m_sampleThread.Start();
            
            Utils.SaveSetting("RTLTcpHost", m_host);
            Utils.SaveSetting("RTLTcpPort", m_port);
        }

        public void Stop()
        {
            Close();
            if (m_sampleThread != null)
            {
                m_sampleThread.Join();
                m_sampleThread = null;
            }
            m_callback = null;           
        }

        #region Private Methods

        private void ParseDongleInfo(byte[] buffer)
        {
            if (buffer.Length < DongleInfoLength)
            {
                return;
            }

            if (buffer[0] != 'R' || buffer[1] != 'T' || buffer[2] != 'L' || buffer[3] != '0')
            {
                m_tunerType = 0;
                m_tunerGainCount = 0;
                return;
            }
            m_tunerType = (uint)(buffer[4] << 24 | buffer[5] << 16 | buffer[6] << 8 | buffer[7]);
            m_tunerGainCount = (uint)(buffer[8] << 24 | buffer[9] << 16 | buffer[10] << 8 | buffer[11]);                        
        }

        public bool SendCommand(byte cmd, byte[] val)
        {
            if (m_socket == null || val.Length < 4)
            {
                return false;
            }
            
            m_cmdBuffer[0] = cmd;
            m_cmdBuffer[1] = val[3]; //Network byte order
            m_cmdBuffer[2] = val[2];
            m_cmdBuffer[3] = val[1];
            m_cmdBuffer[4] = val[0];
            try
            {
                m_socket.Send(m_cmdBuffer);
            }
            catch
            {
                return false;
            }
            return true;
        }

        private void SendCommand(byte cmd, UInt32 val)
        {
            var valBytes = BitConverter.GetBytes(val);
            SendCommand(cmd, valBytes);
        }

        private void SendCommand(byte cmd, Int32 val)
        {
            var valBytes = BitConverter.GetBytes(val);
            SendCommand(cmd, valBytes);
        }

        #endregion

        #region Worker Thread

        private void RecieveSamples()
        {
            var recBuffer = new byte[BufferSize];
            var recUnsafeBuffer = UnsafeBuffer.Create(recBuffer);
            var recPtr = (byte*) recUnsafeBuffer;
            m_iqBuffer = UnsafeBuffer.Create(BufferSize / 2, sizeof(Complex));
            m_iqBufferPtr = (Complex*) m_iqBuffer;
            var offs = 0;                        
            while (m_socket != null && m_socket.Connected)
            {
                try
                {
                    var bytesRec = m_socket.Receive(recBuffer, offs, BufferSize - offs, SocketFlags.None);
                    var totalBytes = offs + bytesRec;
                    offs = totalBytes % 2; //Need to correctly handle the hypothetical case where we somehow get an odd number of bytes
                    ProcessSamples(recPtr, totalBytes - offs); //This might work.
                    if (offs == 1)
                    {
                        recPtr[0] = recPtr[totalBytes - 1];
                    }
                }
                catch
                {
                    Close();
                    break;
                }
            }
			this.m_iqBuffer.Dispose();
			this.m_iqBuffer = null;
			this.m_iqBufferPtr = null;
			GC.KeepAlive(recUnsafeBuffer);
		}

        private void ProcessSamples(byte* rawPtr, int len)
        {
            var sampleCount = len / 2;

            var ptr = m_iqBufferPtr;
            for (var i = 0; i < sampleCount; i++)
            {
                ptr->Imag = m_lutPtr[*rawPtr++];
                ptr->Real = m_lutPtr[*rawPtr++];
                ptr++;
            }
			m_callback?.Invoke(this, m_iqBufferPtr, sampleCount);
		}

        #endregion
    }
}
