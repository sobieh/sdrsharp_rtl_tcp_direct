using System;
using System.Globalization;
using System.Windows.Forms;

namespace SDRSharp.RTLTCP
{
    public partial class RTLTcpSettings : Form
    {
        private readonly RtlTcpIO _owner;

        public string Hostname
        {
            get { return hostBox.Text; }
            set { hostBox.Text = value; }
        }

        public int Port
        {
            get { return (int)portNumberUpDown.Value; }
            set { portNumberUpDown.Value = value; }
        }

        public RTLTcpSettings(RtlTcpIO owner)
        {
            _owner = owner;
            InitializeComponent();

            samplerateComboBox.SelectedIndex = 8;
            samplingModeComboBox.SelectedIndex = 0;
            e4kgain.SelectedIndex = 0;
            rtlAgcCheckBox.Checked = false;
            tunerAgcCheckBox.Checked = false;
            
            UpdateGuiState();
        }
                
        private void refreshTimer_Tick(object sender, EventArgs e)
        {
            UpdateGuiState();
        }

        private void UpdateGuiState()
        {
            tunerGainTrackBar.Enabled = _owner.IsStreaming && !tunerAgcCheckBox.Checked;            
            tunerAgcCheckBox.Enabled = _owner.IsStreaming;
           
            samplerateComboBox.Enabled = !_owner.IsStreaming;
            hostBox.Enabled = !_owner.IsStreaming;
            portNumberUpDown.Enabled = !_owner.IsStreaming;
            if (_owner.TunerType.ToString() == "E4000")
            {
                e4kgain.Enabled = true;
            }
            else
            {
                e4kgain.SelectedIndex = 0;
                e4kgain.Enabled = false;
            }

            tunerLabel.Text = _owner.IsStreaming ? _owner.TunerType.ToString() : string.Empty;

            if (tunerGainTrackBar.Value > _owner.TunerGainCount)
            {
                tunerGainTrackBar.Value = 0;
            }
            if (_owner.UseTunerAGCE4K > 1)
            {
                tunerGainTrackBar.Maximum = 11;
                if (_owner.TunerGainIndex > 11)
                    _owner.TunerGainIndex = (uint)tunerGainTrackBar.Value;
            }
            else
            {
                if (tunerGainTrackBar.Maximum != _owner.TunerGainCount)
                {
                    tunerGainTrackBar.Maximum = (int)_owner.TunerGainCount;
                }
            }
        }

        private void RTLTcpSettings_VisibleChanged(object sender, EventArgs e)
        {
            refreshTimer.Enabled = Visible;
            UpdateGuiState();
        }

        private void RTLTcpSettings_FormClosing(object sender, FormClosingEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }

        private void samplerateComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            var samplerateString = samplerateComboBox.Items[samplerateComboBox.SelectedIndex].ToString().Split(' ')[0];
            var sampleRate = double.Parse(samplerateString, CultureInfo.InvariantCulture);
            _owner.Samplerate = (uint)(sampleRate * 1000000.0);
        }

        private void rtlAgcCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            _owner.UseRtlAGC = rtlAgcCheckBox.Checked;
        }

        private void tunerAgcCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            tunerGainTrackBar.Enabled = tunerAgcCheckBox.Enabled && !tunerAgcCheckBox.Checked;
            _owner.UseTunerAGC = tunerAgcCheckBox.Checked;
        }

        private void tunerGainTrackBar_Scroll(object sender, EventArgs e)
        {
            _owner.TunerGainIndex = (uint) tunerGainTrackBar.Value;
        }

        private void frequencyCorrectionNumericUpDown_ValueChanged(object sender, EventArgs e)
        {
            _owner.FrequencyCorrection = (int)frequencyCorrectionNumericUpDown.Value;
        }

        private void e4kgain_SelectedIndexChanged(object sender, EventArgs e)
        {
            _owner.UseTunerAGCE4K = (e4kgain.SelectedIndex + 1);
        }

        private void samplingModeComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            _owner.RtlSamplingMode = samplingModeComboBox.SelectedIndex;
        }

        private void offsetTuningCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            _owner.UseOffsetTuning = offsetTuningCheckBox.Checked;
        }
    }
}
