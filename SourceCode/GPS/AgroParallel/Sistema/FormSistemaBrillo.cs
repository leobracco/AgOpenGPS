// ============================================================================
// FormSistemaBrillo.cs - Tab Brillo del módulo Sistema
// Brillo via DDC/CI (Monitor Configuration API en dxva2.dll) para monitores
// externos. Fallback a WMI WmiSetBrightness para laptops/integrados.
// ============================================================================

using System;
using System.Drawing;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AgroParallel.Common;

namespace AgroParallel.Sistema
{
    public class FormSistemaBrillo : Form
    {
        private TrackBar _slider;
        private Label _lblValue;
        private Label _lblStatus;
        private Button _btn0, _btn25, _btn50, _btn75, _btn100;

        // ===== Monitor Configuration API (DDC/CI) =====
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [DllImport("dxva2.dll", EntryPoint = "GetNumberOfPhysicalMonitorsFromHMONITOR")]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint pdwNumberOfPhysicalMonitors);
        [DllImport("dxva2.dll", EntryPoint = "GetPhysicalMonitorsFromHMONITOR")]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);
        [DllImport("dxva2.dll", EntryPoint = "DestroyPhysicalMonitors")]
        private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);
        [DllImport("dxva2.dll", EntryPoint = "GetMonitorBrightness")]
        private static extern bool GetMonitorBrightness(IntPtr hMonitor, ref uint pdwMinimumBrightness, ref uint pdwCurrentBrightness, ref uint pdwMaximumBrightness);
        [DllImport("dxva2.dll", EntryPoint = "SetMonitorBrightness")]
        private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(System.Drawing.Point pt, uint dwFlags);
        private const uint MONITOR_DEFAULTTOPRIMARY = 1;

        public FormSistemaBrillo()
        {
            Text = "Brillo";
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Theme.BgBlack;
            ForeColor = Theme.TextPrimary;
            Theme.ApplyToForm(this);
            BuildUI();
            Load += (s, e) => InitFromCurrent();
        }

        private void BuildUI()
        {
            var pnl = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BgBlack, Padding = new Padding(40) };

            var lblTitle = new Label
            {
                Text = "Brillo de pantalla",
                Font = new Font(Theme.FontFamily, 14f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary,
                Location = new Point(40, 30),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            pnl.Controls.Add(lblTitle);

            _lblValue = new Label
            {
                Text = "50%",
                Font = new Font("Consolas", 36f, FontStyle.Bold),
                ForeColor = Theme.Accent,
                Location = new Point(40, 70),
                Size = new Size(200, 60),
                BackColor = Color.Transparent
            };
            pnl.Controls.Add(_lblValue);

            _slider = new TrackBar
            {
                Location = new Point(40, 150),
                Size = new Size(700, 56),
                Minimum = 0,
                Maximum = 100,
                TickFrequency = 10,
                LargeChange = 10,
                SmallChange = 1,
                Value = 50
            };
            _slider.ValueChanged += (s, e) => Apply(_slider.Value);
            pnl.Controls.Add(_slider);

            int y = 230;
            _btn0 = MkBtn("0%", 80); _btn0.Location = new Point(40, y); _btn0.Click += (s, e) => SetSlider(0);
            _btn25 = MkBtn("25%", 80); _btn25.Location = new Point(130, y); _btn25.Click += (s, e) => SetSlider(25);
            _btn50 = MkBtn("50%", 80); _btn50.Location = new Point(220, y); _btn50.Click += (s, e) => SetSlider(50);
            _btn75 = MkBtn("75%", 80); _btn75.Location = new Point(310, y); _btn75.Click += (s, e) => SetSlider(75);
            _btn100 = MkBtn("100%", 80); _btn100.Location = new Point(400, y); _btn100.Click += (s, e) => SetSlider(100);
            pnl.Controls.Add(_btn0); pnl.Controls.Add(_btn25); pnl.Controls.Add(_btn50);
            pnl.Controls.Add(_btn75); pnl.Controls.Add(_btn100);

            _lblStatus = new Label
            {
                Location = new Point(40, 290),
                Size = new Size(700, 26),
                Font = new Font(Theme.FontFamily, 9f),
                ForeColor = Theme.TextDisabled,
                BackColor = Color.Transparent,
                Text = ""
            };
            pnl.Controls.Add(_lblStatus);

            Controls.Add(pnl);
        }

        private void SetSlider(int v)
        {
            _slider.Value = Math.Max(0, Math.Min(100, v));
        }

        private Button MkBtn(string text, int w)
        {
            var b = new Button
            {
                Text = text,
                Width = w,
                Height = 38,
                Font = new Font(Theme.FontFamily, 10f, FontStyle.Bold),
                BackColor = Theme.BgCard,
                ForeColor = Theme.TextPrimary,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderColor = Theme.Border;
            return b;
        }

        // ==================================================================
        // Brillo: DDC/CI primero, fallback WMI laptop
        // ==================================================================

        private bool _hasDdc;

        private void InitFromCurrent()
        {
            try
            {
                int v = TryGetDdcBrightness();
                if (v >= 0)
                {
                    _hasDdc = true;
                    _slider.Value = v;
                    _lblValue.Text = v + "%";
                    _lblStatus.Text = "DDC/CI activo (monitor externo).";
                    return;
                }
            }
            catch { }

            try
            {
                int v = TryGetWmiBrightness();
                if (v >= 0)
                {
                    _slider.Value = v;
                    _lblValue.Text = v + "%";
                    _lblStatus.Text = "WMI activo (panel integrado).";
                    return;
                }
            }
            catch { }

            _lblStatus.Text = "No se detectó control de brillo (ni DDC/CI ni WMI). Probar igual con el slider.";
        }

        private void Apply(int value)
        {
            _lblValue.Text = value + "%";
            try
            {
                if (_hasDdc || TrySetDdcBrightness(value))
                {
                    _hasDdc = true;
                    _lblStatus.Text = "DDC/CI: " + value + "%";
                    return;
                }
            }
            catch { }

            try
            {
                if (TrySetWmiBrightness(value))
                {
                    _lblStatus.Text = "WMI: " + value + "%";
                    return;
                }
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Error: " + ex.Message;
            }
        }

        private static int TryGetDdcBrightness()
        {
            IntPtr h = MonitorFromPoint(new System.Drawing.Point(0, 0), MONITOR_DEFAULTTOPRIMARY);
            uint num = 0;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(h, ref num) || num == 0) return -1;
            var arr = new PHYSICAL_MONITOR[num];
            if (!GetPhysicalMonitorsFromHMONITOR(h, num, arr)) return -1;
            try
            {
                uint min = 0, cur = 0, max = 0;
                if (!GetMonitorBrightness(arr[0].hPhysicalMonitor, ref min, ref cur, ref max)) return -1;
                if (max == 0) return -1;
                return (int)Math.Round(100.0 * (cur - min) / (max - min));
            }
            finally { DestroyPhysicalMonitors(num, arr); }
        }

        private static bool TrySetDdcBrightness(int percent)
        {
            IntPtr h = MonitorFromPoint(new System.Drawing.Point(0, 0), MONITOR_DEFAULTTOPRIMARY);
            uint num = 0;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(h, ref num) || num == 0) return false;
            var arr = new PHYSICAL_MONITOR[num];
            if (!GetPhysicalMonitorsFromHMONITOR(h, num, arr)) return false;
            try
            {
                uint min = 0, cur = 0, max = 0;
                if (!GetMonitorBrightness(arr[0].hPhysicalMonitor, ref min, ref cur, ref max)) return false;
                uint target = (uint)(min + (max - min) * percent / 100);
                bool ok = true;
                for (int i = 0; i < num; i++)
                    ok &= SetMonitorBrightness(arr[i].hPhysicalMonitor, target);
                return ok;
            }
            finally { DestroyPhysicalMonitors(num, arr); }
        }

        private static int TryGetWmiBrightness()
        {
            using (var mos = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightness"))
            foreach (ManagementObject mo in mos.Get())
                return Convert.ToInt32(mo["CurrentBrightness"]);
            return -1;
        }

        private static bool TrySetWmiBrightness(int percent)
        {
            using (var mos = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods"))
            {
                foreach (ManagementObject mo in mos.Get())
                {
                    mo.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)percent });
                    return true;
                }
            }
            return false;
        }
    }
}
