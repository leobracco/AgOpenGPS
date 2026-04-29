// ============================================================================
// VistaXNativePanel.cs - Panel VistaX renderizado 100% en WinForms/GDI+
// Ubicación: SourceCode/GPS/AgroParallel/VistaX/VistaXNativePanel.cs
// Target: net48 (C# 7.3)
//
// Reemplaza el VistaXPanel basado en CefSharp. Consume SeedMonitorSnapshot
// del SeedMonitor (MQTT nativo) y dibuja header con KPIs + tubos por surco
// agrupados por tren + footer de estado.
//
// Sin CefSharp, sin Node.js, sin Socket.IO. Solo GDI+ sobre un UserControl.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace AgroParallel.VistaX
{
    public class VistaXNativePanel : UserControl
    {
        // Paleta tomada de VistaX-Core / public/css/vistax.css
        private static readonly Color CBgDark = Color.FromArgb(0, 0, 0);          // #000000
        private static readonly Color CBgHeader = Color.FromArgb(20, 20, 20);     // #141414
        private static readonly Color CBgCard = Color.FromArgb(30, 30, 30);       // #1e1e1e
        private static readonly Color CText = Color.FromArgb(255, 255, 255);      // #ffffff
        private static readonly Color CTextDim = Color.FromArgb(102, 102, 102);   // #666666
        private static readonly Color CAccent = Color.FromArgb(0, 230, 118);      // #00e676
        private static readonly Color CAccentDark = Color.FromArgb(0, 160, 64);   // #00a040
        private static readonly Color CRed = Color.FromArgb(255, 23, 68);         // #ff1744
        private static readonly Color CRedDark = Color.FromArgb(183, 28, 28);     // #b71c1c
        private static readonly Color CYellow = Color.FromArgb(255, 234, 0);      // #ffea00
        private static readonly Color CBlocked = Color.FromArgb(8, 8, 8);         // #080808
        private static readonly Color CBlockedBorder = Color.FromArgb(17, 17, 17); // #111111
        private static readonly Color CBorder = Color.FromArgb(40, 40, 40);
        // Sensores auxiliares (VistaX-Core --ferti-l / --ferti-c + naranja
        // para bajada de herramienta).
        private static readonly Color CFertiLinea = Color.FromArgb(0, 229, 255);  // #00e5ff
        private static readonly Color CFertiCostado = Color.FromArgb(255, 234, 0); // #ffea00
        private static readonly Color CHerramienta = Color.FromArgb(255, 160, 0);

        private const int HeaderHeight = 40;
        private const int FooterHeight = 20;
        private const int AlarmBannerHeight = 16;
        private const int TrainLabelWidth = 110;

        private readonly VistaXConfig _config;
        private readonly int _panelHeight;
        private readonly int _panelWidthPercent;
        private readonly int _panelBottomMargin;

        private volatile SeedMonitorSnapshot _snap;
        private int _lastInvalidateTick;

        // Controles de edicion sobre el header (sobrepuestos al OnPaint).
        private NumericUpDown _numObjetivo;
        private Button _btnConfig;
        private Button _btnMute;
        private bool _suppressObjetivoEvent;

        // Estado de la alarma sonora.
        private bool _alarmMuted;
        private int _lastBeepTick;
        private const int BeepIntervalMs = 1000;

        // Eventos que FormGPS / FormVistaXPopup manejan para propagar al monitor.
        public event Action<double> ObjetivoChanged;
        public event Action ConfigRequested;

        // Persistencia opcional: el owner puede leer/escribir esto via JSON.
        public bool AlarmMuted
        {
            get { return _alarmMuted; }
            set
            {
                if (_alarmMuted == value) return;
                _alarmMuted = value;
                UpdateMuteButtonIcon();
                Invalidate();
            }
        }

        public VistaXNativePanel(VistaXConfig config)
        {
            _config = config ?? throw new ArgumentNullException("config");
            _panelHeight = config.PanelHeight > 0 ? config.PanelHeight : 150;
            _panelWidthPercent = config.PanelWidthPercent > 0 ? config.PanelWidthPercent : 60;
            _panelBottomMargin = config.PanelBottomMargin >= 0 ? config.PanelBottomMargin : 200;

            DoubleBuffered = true;
            BackColor = CBgDark;
            SetStyle(ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw, true);

            BuildHeaderControls();
        }

        private void BuildHeaderControls()
        {
            _numObjetivo = new NumericUpDown();
            _numObjetivo.Minimum = 0;
            _numObjetivo.Maximum = 9999;
            _numObjetivo.DecimalPlaces = 1;
            _numObjetivo.Increment = 0.1m;
            _numObjetivo.Value = 0;
            _numObjetivo.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            _numObjetivo.BackColor = Color.FromArgb(15, 15, 15);
            _numObjetivo.ForeColor = CAccent;
            _numObjetivo.BorderStyle = BorderStyle.FixedSingle;
            _numObjetivo.Width = 70;
            _numObjetivo.ValueChanged += (s, e) =>
            {
                if (_suppressObjetivoEvent) return;
                var h = ObjetivoChanged;
                if (h != null) h((double)_numObjetivo.Value);
            };
            Controls.Add(_numObjetivo);

            _btnConfig = new Button();
            _btnConfig.Text = "⚙";
            _btnConfig.FlatStyle = FlatStyle.Flat;
            _btnConfig.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            _btnConfig.BackColor = Color.FromArgb(20, 20, 20);
            _btnConfig.ForeColor = CAccent;
            _btnConfig.Size = new Size(28, 24);
            _btnConfig.Cursor = Cursors.Hand;
            _btnConfig.FlatAppearance.BorderColor = CBorder;
            _btnConfig.Click += (s, e) =>
            {
                var h = ConfigRequested;
                if (h != null) h();
            };
            Controls.Add(_btnConfig);

            _btnMute = new Button();
            _btnMute.FlatStyle = FlatStyle.Flat;
            _btnMute.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            _btnMute.BackColor = Color.FromArgb(20, 20, 20);
            _btnMute.ForeColor = CAccent;
            _btnMute.Size = new Size(28, 24);
            _btnMute.Cursor = Cursors.Hand;
            _btnMute.FlatAppearance.BorderColor = CBorder;
            _btnMute.Click += (s, e) => AlarmMuted = !_alarmMuted;
            Controls.Add(_btnMute);
            UpdateMuteButtonIcon();
        }

        private void UpdateMuteButtonIcon()
        {
            if (_btnMute == null) return;
            _btnMute.Text = _alarmMuted ? "\U0001F507" : "\U0001F50A";
            _btnMute.ForeColor = _alarmMuted ? CTextDim : CAccent;
        }

        // Llamado por SeedMonitor.SnapshotUpdated. El evento viene desde un Timer
        // en hilo background, por eso hay que marshaller a UI antes de tocar state.
        public void SetSnapshot(SeedMonitorSnapshot snap)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<SeedMonitorSnapshot>(SetSnapshot), snap); }
                catch { /* handle dispuesto: ignorar */ }
                return;
            }

            _snap = snap;

            // Alarma sonora: beep repetitivo mientras HasAlarm y no mute.
            // Limitado a un beep cada BeepIntervalMs para no saturar.
            int now = Environment.TickCount;
            if (snap != null && snap.HasAlarm && !_alarmMuted
                && now - _lastBeepTick >= BeepIntervalMs)
            {
                _lastBeepTick = now;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { Console.Beep(1200, 180); } catch { }
                });
            }

            // Throttle a ~60fps. El monitor reporta a 500ms asi que en la
            // practica no se activa, pero protege si alguien sube la frecuencia.
            if (now - _lastInvalidateTick < 16) return;
            _lastInvalidateTick = now;
            Invalidate();
        }

        // Compatibilidad con VistaXPanel de CefSharp — el caller sigue
        // llamando estos metodos. SetSnapshot es el camino real.
        public void UpdateDisplay(SeedMonitorSnapshot snap) { SetSnapshot(snap); }
        public void FlushPending() { }

        public void Reposition()
        {
            if (Parent == null) return;

            int parentW = Parent.Width;
            int parentH = Parent.Height;

            int panelH = _panelHeight;
            int panelW = (int)(parentW * _panelWidthPercent / 100.0);

            Size = new Size(panelW, panelH);
            Location = new Point(
                (parentW - panelW) / 2,
                parentH - panelH - _panelBottomMargin);
            BringToFront();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutHeaderControls();
        }

        private void LayoutHeaderControls()
        {
            if (_btnConfig != null)
                _btnConfig.Location = new Point(Width - 30 - _btnConfig.Width, 8);
            if (_btnMute != null && _btnConfig != null)
                _btnMute.Location = new Point(_btnConfig.Left - _btnMute.Width - 4, 8);
            if (_numObjetivo != null)
                _numObjetivo.Location = new Point(110, 8);
        }

        // Actualiza el valor del NumericUpDown desde el snapshot sin disparar
        // el evento ValueChanged (asi no pensamos que el usuario edito).
        private void SyncObjetivoFromSnapshot(double objetivo)
        {
            if (_numObjetivo == null) return;
            if (_numObjetivo.Focused) return; // respetar edicion en curso.
            decimal v = (decimal)Math.Max(0, Math.Min((double)_numObjetivo.Maximum, objetivo));
            if (v == _numObjetivo.Value) return;
            _suppressObjetivoEvent = true;
            try { _numObjetivo.Value = v; }
            finally { _suppressObjetivoEvent = false; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            LayoutHeaderControls();
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Theme.BgBlack);

            // Borde exterior redondeado.
            Theme.DrawRoundedBorder(g, new Rectangle(0, 0, Width - 1, Height - 1),
                Theme.Border, Theme.BorderRadius);

            var snap = _snap;
            if (snap == null || snap.Trenes == null || snap.Trenes.Length == 0)
            {
                DrawWaitingState(g, snap);
                return;
            }

            // Header estilo mockup.
            Theme.PaintHeader(g, Width, "MONITOR DE SIEMBRA");

            // KPIs en fila debajo del header.
            int kpiY = Theme.HeaderHeight + 4;
            DrawKpiRow(g, snap, kpiY);

            // Status ticker.
            int tickerY = kpiY + 38;
            DrawStatusTicker(g, snap, tickerY);

            // Barras de surcos.
            int bodyTop = tickerY + AlarmBannerHeight + 2;
            int bodyBottom = Height - FooterHeight;
            DrawTrains(g, snap, bodyTop, bodyBottom);

            // Footer.
            DrawFooter(g, snap);
        }

        private void DrawKpiRow(Graphics g, SeedMonitorSnapshot snap, int y)
        {
            // Fondo card para la fila de KPIs.
            var kpiRect = new Rectangle(4, y, Width - 8, 34);
            Theme.FillRoundedRect(g, kpiRect, Theme.BgCard, 6);

            // KPIs distribuidos uniformemente.
            int count = 4;
            int kpiW = (Width - 16) / count;
            int x = 8;

            // VEL.
            DrawMiniKpi(g, x, y + 2, kpiW,
                snap.Velocidad.ToString("F1", CultureInfo.InvariantCulture), "km/h", "VEL");
            x += kpiW;

            // S/M.
            DrawMiniKpi(g, x, y + 2, kpiW,
                snap.SpmPromedio.ToString("F1", CultureInfo.InvariantCulture), "s/m", "DENSIDAD");
            x += kpiW;

            // FALLAS.
            Color fallasColor = snap.FallasActivas > 0 ? Theme.Error : Theme.TextPrimary;
            DrawMiniKpi(g, x, y + 2, kpiW,
                snap.FallasActivas.ToString(CultureInfo.InvariantCulture), "", "FALLAS", fallasColor);
            x += kpiW;

            // ONLINE.
            int totalConfig = snap.Surcos != null ? snap.Surcos.Length : 0;
            string online = snap.SurcosActivos + "/" + totalConfig;
            DrawMiniKpi(g, x, y + 2, kpiW, online, "", "SURCOS");
        }

        private void DrawMiniKpi(Graphics g, int x, int y, int w,
            string value, string unit, string label, Color? valueColor = null)
        {
            Color vc = valueColor ?? Theme.TextPrimary;

            using (var fVal = new Font(Theme.FontFamily, 13f, FontStyle.Bold))
            using (var br = new SolidBrush(vc))
                g.DrawString(value, fVal, br, x + 4, y);

            if (!string.IsNullOrEmpty(unit))
            {
                using (var fVal = new Font(Theme.FontFamily, 13f, FontStyle.Bold))
                {
                    var sz = g.MeasureString(value, fVal);
                    using (var fUnit = new Font(Theme.FontFamily, 7.5f))
                    using (var br = new SolidBrush(Theme.TextSecondary))
                        g.DrawString(unit, fUnit, br, x + 4 + sz.Width, y + 6);
                }
            }

            using (var fLbl = new Font(Theme.FontFamily, 7f, FontStyle.Bold))
            using (var br = new SolidBrush(Theme.TextFaint))
                g.DrawString(label, fLbl, br, x + 4, y + 18);
        }

        private void DrawStatusTicker(Graphics g, SeedMonitorSnapshot snap, int y)
        {
            bool hasAlarm = snap.HasAlarm;
            Color bg = hasAlarm ? Color.FromArgb(40, 8, 8) : Color.FromArgb(8, 20, 8);
            Color fg = hasAlarm ? Theme.Error : Theme.Accent;

            using (var b = new SolidBrush(bg))
                g.FillRectangle(b, 4, y, Width - 8, AlarmBannerHeight);

            string text = hasAlarm
                ? (snap.AlarmMessage ?? "FALLA EN EL SISTEMA")
                : (snap.MonitoreoActivo ? "SEMBRANDO" : "EN ESPERA");

            using (var f = new Font(Theme.FontFamily, 7.5f, FontStyle.Bold))
            using (var br = new SolidBrush(fg))
                g.DrawString(text, f, br, 10, y + 1);

            // Implemento name on the right.
            if (!string.IsNullOrEmpty(snap.NombreImplemento))
            {
                using (var f = new Font(Theme.FontFamily, 7.5f))
                using (var br = new SolidBrush(Theme.TextFaint))
                {
                    var sz = g.MeasureString(snap.NombreImplemento, f);
                    g.DrawString(snap.NombreImplemento, f, br, Width - sz.Width - 12, y + 1);
                }
            }
        }

        private void DrawWaitingState(Graphics g, SeedMonitorSnapshot snap)
        {
            Theme.PaintHeader(g, Width, "MONITOR DE SIEMBRA");

            bool connected = snap != null && snap.IsConnected;
            DrawConnectionLed(g, connected);

            // Velocidad.
            if (snap != null)
            {
                using (var fVal = new Font(Theme.FontFamily, 14f, FontStyle.Bold))
                using (var br = new SolidBrush(Theme.TextPrimary))
                {
                    string vel = snap.Velocidad.ToString("F1", CultureInfo.InvariantCulture) + " km/h";
                    var sz = g.MeasureString(vel, fVal);
                    g.DrawString(vel, fVal, br, (Width - sz.Width) / 2f, Theme.HeaderHeight + 20);
                }
            }

            using (var f = new Font(Theme.FontFamily, 10f))
            using (var br = new SolidBrush(Theme.TextSecondary))
            {
                string msg = connected
                    ? "Conectado \u2014 esperando telemetr\u00EDa..."
                    : "Esperando conexi\u00F3n MQTT...";

                var sz = g.MeasureString(msg, f);
                g.DrawString(msg, f, br,
                    (Width - sz.Width) / 2f,
                    Theme.HeaderHeight + 50);
            }
        }

        private void DrawHeader(Graphics g, SeedMonitorSnapshot snap)
        {
            using (var hbg = new SolidBrush(CBgHeader))
                g.FillRectangle(hbg, 0, 0, Width, HeaderHeight);

            using (var fTitle = new Font("Segoe UI", 12f, FontStyle.Bold))
            using (var titleBrush = new SolidBrush(CAccent))
                g.DrawString("VistaX", fTitle, titleBrush, 12, 10);

            using (var fSub = new Font("Segoe UI", 8.5f))
            using (var dimBrush = new SolidBrush(CTextDim))
            {
                string impl = string.IsNullOrEmpty(snap.NombreImplemento)
                    ? ""
                    : snap.NombreImplemento;
                g.DrawString(impl, fSub, dimBrush, 70, 14);
            }

            // KPIs alineados a la derecha, antes del LED de conexion.
            // Reservar espacio para el boton de config + LED de conexion a
            // la derecha del header antes de dibujar los KPIs.
            int kpiRight = Width - 30 - (_btnConfig != null ? _btnConfig.Width + 8 : 0);
            using (var fLbl = new Font("Segoe UI", 7.5f))
            using (var fVal = new Font("Segoe UI", 11f, FontStyle.Bold))
            using (var lblBrush = new SolidBrush(CTextDim))
            using (var valBrush = new SolidBrush(CText))
            using (var redBrush = new SolidBrush(CRed))
            {
                int totalOnline = snap.SurcosActivos;
                int totalConfig = (snap.Surcos != null) ? snap.Surcos.Length : 0;
                string online = totalConfig > 0
                    ? totalOnline + "/" + totalConfig
                    : totalOnline.ToString(CultureInfo.InvariantCulture);

                // Objetivo: tomar el del primer tren con objetivo > 0. El valor
                // se muestra/edita via el NumericUpDown del header, no como KPI.
                double objetivo = 0;
                if (snap.Trenes != null)
                {
                    foreach (var tl in snap.Trenes)
                    {
                        if (tl != null && tl.Objetivo > 0) { objetivo = tl.Objetivo; break; }
                    }
                }
                SyncObjetivoFromSnapshot(objetivo);

                // Label "OBJ" pequeño arriba del NumericUpDown para aclarar que
                // ese input es el objetivo (el control lo dibuja WinForms).
                using (var fLbl2 = new Font("Segoe UI", 7.5f))
                    g.DrawString("OBJ", fLbl2, lblBrush, 113, 0);

                var kpis = new[]
                {
                    Tuple.Create("VEL", snap.Velocidad.ToString("F1", CultureInfo.InvariantCulture), false),
                    Tuple.Create("S/M", snap.SpmPromedio.ToString("F1", CultureInfo.InvariantCulture), false),
                    Tuple.Create("FALLAS", snap.FallasActivas.ToString(CultureInfo.InvariantCulture),
                        snap.FallasActivas > 0),
                    Tuple.Create("ONLINE", online, false)
                };

                for (int i = kpis.Length - 1; i >= 0; i--)
                {
                    var k = kpis[i];
                    var valSz = g.MeasureString(k.Item2, fVal);
                    var lblSz = g.MeasureString(k.Item1, fLbl);
                    float boxW = Math.Max(valSz.Width, lblSz.Width) + 4;
                    float boxX = kpiRight - boxW;

                    g.DrawString(k.Item1, fLbl, lblBrush, boxX, 4);
                    g.DrawString(k.Item2, fVal, k.Item3 ? redBrush : valBrush, boxX, 17);

                    kpiRight = (int)boxX - 12;
                    if (kpiRight < 160) break;
                }
            }

            DrawConnectionLed(g, snap.IsConnected);
        }

        private void DrawConnectionLed(Graphics g, bool connected)
        {
            int r = 5;
            int cx = Width - 16;
            int cy = 22;
            using (var ledBrush = new SolidBrush(connected ? Theme.Accent : Theme.Error))
                g.FillEllipse(ledBrush, cx - r, cy - r, r * 2, r * 2);
        }

        private void DrawTrains(Graphics g, SeedMonitorSnapshot snap, int bodyTop, int bodyBottom)
        {
            int bodyH = bodyBottom - bodyTop;
            if (bodyH <= 0) return;

            int n = snap.Trenes.Length;
            int perTrainH = n > 0 ? bodyH / n : bodyH;

            for (int i = 0; i < n; i++)
            {
                var tren = snap.Trenes[i];
                if (tren == null) continue;
                int y0 = bodyTop + i * perTrainH;
                int y1 = y0 + perTrainH;
                DrawTrain(g, tren, y0, y1, snap.ToleranciaDesvio);
            }
        }

        private void DrawTrain(Graphics g, TrenLayout tren, int y0, int y1, double tolerancia)
        {
            using (var fLbl = new Font(Theme.FontFamily, 7.5f, FontStyle.Bold))
            using (var lblBrush = new SolidBrush(Theme.TextFaint))
            {
                string name = tren.Tren == 1 ? "DELANTERO" : tren.Tren == 2 ? "TRASERO"
                    : "TREN " + tren.Tren.ToString(CultureInfo.InvariantCulture);
                g.DrawString(name, fLbl, lblBrush, 6, y0 + 2);
            }

            if (tren.Surcos == null || tren.Surcos.Length == 0) return;

            // Agrupar por Bajada — cada grupo tiene 1 semilla (tubo principal)
            // y opcionalmente ferti_linea / ferti_costado / bajada_herramienta.
            var bajadas = new SortedDictionary<int, List<SurcoState>>();
            foreach (var s in tren.Surcos)
            {
                if (s == null) continue;
                List<SurcoState> list;
                if (!bajadas.TryGetValue(s.Bajada, out list))
                {
                    list = new List<SurcoState>();
                    bajadas[s.Bajada] = list;
                }
                list.Add(s);
            }
            int slotCount = bajadas.Count;
            if (slotCount == 0) return;

            int drawAreaLeft = TrainLabelWidth;
            int drawAreaRight = Width - 8;
            int drawAreaW = drawAreaRight - drawAreaLeft;
            if (drawAreaW < 32) return;

            int tubeH = (y1 - y0) - 10;
            if (tubeH < 8) return;
            int ledR = 3;

            int sensorW = tren.SensorWidthPx > 0 ? tren.SensorWidthPx : 20;
            int spacing = tren.SpacingPx;
            int total = slotCount * sensorW + Math.Max(0, slotCount - 1) * spacing;
            if (total > drawAreaW)
            {
                int available = drawAreaW - Math.Max(0, slotCount - 1) * 2;
                sensorW = Math.Max(6, available / Math.Max(1, slotCount));
                spacing = 2;
                total = slotCount * sensorW + (slotCount - 1) * spacing;
            }
            int offsetX = drawAreaLeft + (drawAreaW - total) / 2;

            int tubeTop = y0 + 8;
            int tubeAreaH = tubeH;
            double objetivo = tren.Objetivo > 0 ? tren.Objetivo : 0;

            int i = 0;
            foreach (var kv in bajadas)
            {
                var group = kv.Value;
                var s = FindByTipo(group, "semilla") ?? group[0];
                int x = offsetX + i * (sensorW + spacing);
                i++;
                // Clasificar: alerta > cortado > sinSenal > desvio > ok.
                PillStatus status;
                double pctHeight; // % del tubeAreaH que ocupa el tubo (bottom-aligned).
                if (s.SeccionCortada)
                {
                    status = PillStatus.Tapado;
                    pctHeight = 3;
                }
                else if (s.Alerta)
                {
                    // Falla confirmada (tubo tapado o flujo muy bajo).
                    status = PillStatus.Alerta;
                    pctHeight = 15;
                }
                else if (s.Spm <= 0 && s.LastUpdate == DateTime.MinValue)
                {
                    // Nunca recibió datos — gris medio, distinguible de tapado.
                    status = PillStatus.SinSenal;
                    pctHeight = 10;
                }
                else if (s.Spm <= 0)
                {
                    // Recibió datos antes pero ahora está en 0.
                    status = PillStatus.SinSenal;
                    pctHeight = 6;
                }
                else if (objetivo > 0)
                {
                    double pctDesvio = Math.Abs((s.Spm - objetivo) / objetivo) * 100.0;
                    // (spm / objetivo) * 75 con clamp 10..95.
                    pctHeight = Math.Max(10, Math.Min(95, (s.Spm / objetivo) * 75.0));
                    status = (tolerancia > 0 && pctDesvio > tolerancia)
                        ? PillStatus.Desvio : PillStatus.Ok;
                }
                else
                {
                    // Sin objetivo definido: altura fija media, color Ok.
                    status = PillStatus.Ok;
                    pctHeight = 50;
                }

                int actualH = Math.Max(2, (int)Math.Round(tubeAreaH * pctHeight / 100.0));
                int tubeY = tubeTop + (tubeAreaH - actualH);

                DrawTube(g, x, tubeY, sensorW, actualH, tubeAreaH, tubeTop, status);
                DrawLed(g, x + sensorW / 2, tubeTop - ledR - 2, ledR,
                    status == PillStatus.Alerta ? RowState.Failure :
                    status == PillStatus.Desvio ? RowState.LowRate :
                    status == PillStatus.Ok ? RowState.Ok : RowState.NoData);

                // LEDs de tipos auxiliares: ferti_linea (cyan), ferti_costado
                // (amarillo), bajada_herramienta (naranja). Se dibujan abajo
                // del canister del tubo como puntitos.
                DrawAuxLeds(g, group, x, tubeTop + tubeAreaH + 2, sensorW);
            }
        }

        private static SurcoState FindByTipo(List<SurcoState> list, string tipo)
        {
            if (list == null) return null;
            foreach (var s in list)
                if (s != null && string.Equals(s.Tipo, tipo, StringComparison.OrdinalIgnoreCase))
                    return s;
            return null;
        }

        private void DrawAuxLeds(Graphics g, List<SurcoState> group, int x, int y, int w)
        {
            // Hasta 3 LEDs alineados horizontalmente bajo el tubo.
            int dotR = 2;
            var types = new[] {
                Tuple.Create(TipoSensor.FertiLinea, CFertiLinea),
                Tuple.Create(TipoSensor.FertiCostado, CFertiCostado),
                Tuple.Create(TipoSensor.BajadaHerramienta, CHerramienta),
                Tuple.Create(TipoSensor.Turbina, Color.FromArgb(156, 39, 176)),
                Tuple.Create(TipoSensor.Tolva, Color.FromArgb(121, 85, 72))
            };

            int drawn = 0;
            foreach (var t in types)
            {
                var s = FindByTipo(group, t.Item1);
                if (s == null) continue;
                int cx = x + 3 + drawn * (dotR * 2 + 3);
                if (cx + dotR * 2 > x + w) break; // no cabe mas.
                bool active = s.Valor > 0 && !s.SeccionCortada;
                Color c = active ? t.Item2
                    : Color.FromArgb(60, t.Item2.R, t.Item2.G, t.Item2.B);
                using (var b = new SolidBrush(c))
                    g.FillEllipse(b, cx, y, dotR * 2, dotR * 2);
                drawn++;
            }
        }

        private enum PillStatus { Ok, Desvio, Alerta, Tapado, SinSenal }

        private void DrawTube(Graphics g, int x, int y, int w, int h,
            int fullH, int fullTop, PillStatus status)
        {
            // Canasta de referencia (borde redondeado sutil).
            var canister = new Rectangle(x, fullTop, w, fullH);
            Theme.FillRoundedRect(g, canister, Color.FromArgb(14, 14, 16), 4);
            Theme.DrawRoundedBorder(g, canister, Theme.Border, 4, 0.5f);

            // Nivel dinamico (el "liquido" del tubo).
            var rect = new Rectangle(x + 1, y, w - 2, h);
            Color topColor, botColor;

            switch (status)
            {
                case PillStatus.Alerta:
                    topColor = Theme.Error;
                    botColor = Color.FromArgb(140, 30, 30);
                    break;
                case PillStatus.Tapado:
                    using (var b = new SolidBrush(Color.FromArgb(8, 8, 10)))
                        g.FillRectangle(b, rect);
                    return;
                case PillStatus.SinSenal:
                    using (var b = new SolidBrush(Color.FromArgb(40, 42, 48)))
                        g.FillRectangle(b, rect);
                    return;
                case PillStatus.Desvio:
                    topColor = Theme.Warning;
                    botColor = Color.FromArgb(160, 110, 10);
                    break;
                default: // Ok
                    topColor = Theme.Accent;
                    botColor = Theme.AccentDim;
                    break;
            }

            if (h > 1)
            {
                using (var brush = new LinearGradientBrush(
                    new Point(x, y), new Point(x, y + Math.Max(1, h)), topColor, botColor))
                    g.FillRectangle(brush, rect);
            }
        }

        private void DrawLed(Graphics g, int cx, int cy, int r, RowState state)
        {
            Color c;
            switch (state)
            {
                case RowState.Ok: c = Theme.Accent; break;
                case RowState.Failure: c = Theme.Error; break;
                case RowState.LowRate: c = Theme.Warning; break;
                case RowState.HighRate: c = Theme.Warning; break;
                default: c = Color.FromArgb(50, 52, 58); break;
            }
            using (var b = new SolidBrush(c))
                g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
        }

        private void DrawFooter(Graphics g, SeedMonitorSnapshot snap)
        {
            int fy = Height - FooterHeight;

            using (var bg = new SolidBrush(Theme.BgToolbar))
                g.FillRectangle(bg, 0, fy, Width, FooterHeight);
            using (var pen = new Pen(Theme.Border))
                g.DrawLine(pen, 0, fy, Width, fy);

            string msg = snap.MonitoreoActivo
                ? "\u25CF  SEMBRANDO"
                : "\u25CB  EN ESPERA (" + snap.MetodoInicio + ")";

            using (var f = new Font(Theme.FontFamily, 8.5f, FontStyle.Bold))
            using (var brush = new SolidBrush(snap.MonitoreoActivo ? Theme.Accent : Theme.TextSecondary))
            {
                var sz = g.MeasureString(msg, f);
                g.DrawString(msg, f, brush,
                    (Width - sz.Width) / 2f,
                    fy + (FooterHeight - sz.Height) / 2f);
            }
        }
    }
}
