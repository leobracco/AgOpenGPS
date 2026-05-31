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
using System.Linq;
using System.Windows.Forms;
using AgroParallel.Common;

namespace AgroParallel.VistaX
{
    public class VistaXNativePanel : UserControl
    {
        // Paleta tomada de theme.css (--vx-*). Alineada con HUB web (vistax.js
        // / piloto.js) para coherencia cross-platform.
        //   ok       = #4BA63F (verde Agro Parallel)
        //   tapado   = #050505 (negro pleno — sensor con flujo bloqueado)
        //   exceso   = #3D8BFD (azul — por encima del objetivo + tolerancia)
        //   no-data  = #3A3F44 (gris idle — sin lecturas)
        //   muted    = #4A5055 (gris desaturado — silenciado por operario)
        //   bajo     → degradé negro→verde interpolado por ratio real/objetivo
        private static readonly Color CBgDark = Color.FromArgb(22, 24, 26);       // var(--agp-bg)
        private static readonly Color CBgHeader = Color.FromArgb(28, 31, 34);     // var(--agp-bg-soft)
        private static readonly Color CBgCard = Color.FromArgb(32, 35, 38);       // var(--agp-surface)
        private static readonly Color CText = Color.FromArgb(221, 224, 227);      // var(--agp-text)
        private static readonly Color CTextDim = Color.FromArgb(138, 143, 149);   // var(--agp-text-muted)
        private static readonly Color CAccent = Color.FromArgb(75, 166, 63);      // var(--vx-ok)
        private static readonly Color CAccentDark = Color.FromArgb(31, 77, 26);   // var(--vx-ok-dark)
        private static readonly Color CRed = Color.FromArgb(201, 119, 119);       // var(--agp-state-bad)
        private static readonly Color CRedDark = Color.FromArgb(140, 60, 60);
        private static readonly Color CYellow = Color.FromArgb(217, 184, 92);     // var(--agp-state-warn)
        private static readonly Color CBlocked = Color.FromArgb(5, 5, 5);         // var(--vx-tapado)
        private static readonly Color CBlockedBorder = Color.FromArgb(26, 26, 26); // var(--vx-tapado-border)
        private static readonly Color CBorder = Color.FromArgb(47, 51, 56);       // var(--agp-border)
        // Nuevos para estados VistaX:
        private static readonly Color CExceso = Color.FromArgb(61, 139, 253);     // var(--vx-exceso)
        private static readonly Color CExcesoDark = Color.FromArgb(27, 79, 156);  // var(--vx-exceso-dark)
        private static readonly Color CNoData = Color.FromArgb(58, 63, 68);       // var(--vx-no-data)
        private static readonly Color CMuted = Color.FromArgb(74, 80, 85);        // var(--vx-muted)
        private static readonly Color CMutedBorder = Color.FromArgb(47, 51, 56);  // var(--vx-muted-border)
        // Sensores auxiliares (VistaX-Core --ferti-l / --ferti-c + naranja
        // para bajada de herramienta).
        private static readonly Color CFertiLinea = Color.FromArgb(0, 229, 255);  // #00e5ff
        private static readonly Color CFertiCostado = Color.FromArgb(255, 234, 0); // #ffea00
        private static readonly Color CHerramienta = Color.FromArgb(255, 160, 0);

        private const int HeaderHeight = 28;
        private const int FooterHeight = 20;
        private const int AlarmBannerHeight = 16;
        private const int TrainLabelWidth = 110;
        // Resize dinámico: marca cuándo el tamaño fue calculado a partir del
        // snapshot. Reposition() debe respetarlo en lugar de pisarlo con la
        // geometría estática del config (_panelHeight / _panelWidthPercent).
        private bool _dynamicSizeApplied;

        private readonly VistaXConfig _config;
        private readonly int _panelHeight;
        private readonly int _panelWidthPercent;
        private readonly int _panelBottomMargin;

        private volatile SeedMonitorSnapshot _snap;
        private int _lastInvalidateTick;

        // Header del widget overlay — vista limpia "Agro Parallel":
        // sólo el botón de silenciar alarma queda visible. El operario hace
        // toda la config desde el Hub (página vistax.html) y el objetivo
        // vive en el catálogo de Insumos. Eliminamos ⚙ y OBJ del overlay.
        private Button _btnMute;

        // Toggle Surcos/Torres — sólo se muestra si el implemento define
        // torres > 1. Click alterna y dispara Invalidate().
        private Button _btnVista;
        private string _modoVista = "surcos"; // "surcos" | "torres"
        private bool _modoVistaForzadoUsuario;

        // Estado de la alarma sonora.
        private bool _alarmMuted;
        private int _lastBeepTick;
        private const int BeepIntervalMs = 1000;

        // Eventos que FormGPS / FormVistaXPopup manejan para propagar al monitor.
        // ObjetivoChanged y ConfigRequested ya no se disparan desde el widget
        // (se sacaron los controles) — se mantienen los miembros vacíos para
        // que los suscriptores existentes no rompan. Marcar [Obsolete] cuando
        // se limpie el binding desde FormGPS.
#pragma warning disable CS0067 // eventos conservados por compatibilidad con FormGPS/FormVistaXPopup
        public event Action<double> ObjetivoChanged;
        public event Action ConfigRequested;
#pragma warning restore CS0067

        // Toggle de silenciado por sensor — emitido al hacer doble-click sobre
        // el tubo de un surco. (uid, cable, nuevoMuted). El owner debe llamar
        // SeedMonitor.SetSensorMuted + persistir en implemento.json.
        public event Action<string, int, bool> SensorMuteToggleRequested;

        // Hit-test: rectángulo de cada tubo dibujado en el último OnPaint.
        // Permite traducir un click (x, y) al SurcoState clickeado.
        private sealed class TubeHit
        {
            public Rectangle Rect;
            public string Uid;
            public int Cable;
            public bool Muted;
        }
        private readonly List<TubeHit> _tubeHits = new List<TubeHit>();

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
            // Sólo dos controles visibles en el header: silenciar alarma y
            // toggle Surcos/Torres. Vista limpia "Agro Parallel" — todo lo
            // demás (objetivo, IP del nodo, fuente WAS, etc.) vive en el Hub.
            _btnMute = new Button();
            _btnMute.FlatStyle = FlatStyle.Flat;
            _btnMute.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            _btnMute.BackColor = Color.FromArgb(20, 20, 20);
            _btnMute.ForeColor = CAccent;
            _btnMute.Size = new Size(28, 22);
            _btnMute.Cursor = Cursors.Hand;
            _btnMute.FlatAppearance.BorderColor = CBorder;
            _btnMute.Click += (s, e) => AlarmMuted = !_alarmMuted;
            Controls.Add(_btnMute);
            UpdateMuteButtonIcon();

            _btnVista = new Button();
            _btnVista.Text = "S/T";
            _btnVista.FlatStyle = FlatStyle.Flat;
            _btnVista.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            _btnVista.BackColor = Color.FromArgb(20, 20, 20);
            _btnVista.ForeColor = CTextDim;
            _btnVista.Size = new Size(40, 22);
            _btnVista.Cursor = Cursors.Hand;
            _btnVista.FlatAppearance.BorderColor = CBorder;
            _btnVista.Visible = false; // se prende cuando llega snap con torres>1.
            _btnVista.Click += (s, e) =>
            {
                _modoVista = _modoVista == "surcos" ? "torres" : "surcos";
                _modoVistaForzadoUsuario = true;
                UpdateVistaButton();
                Invalidate();
            };
            Controls.Add(_btnVista);
            UpdateVistaButton();
        }

        private void UpdateVistaButton()
        {
            if (_btnVista == null) return;
            bool torres = _modoVista == "torres";
            _btnVista.Text = torres ? "Torres" : "Surcos";
            _btnVista.ForeColor = torres ? CAccent : CTextDim;
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

            // Toggle Surcos/Torres: visible solo si el implemento define más
            // de una torre. Si el operario nunca tocó el botón, seguimos el
            // VistaModoDefault del setup.
            bool puedeAgrupar = snap != null && snap.Torres > 1;
            if (_btnVista != null) _btnVista.Visible = puedeAgrupar;
            if (!_modoVistaForzadoUsuario && snap != null)
            {
                _modoVista = (snap.VistaModoDefault == "torres") ? "torres" : "surcos";
                UpdateVistaButton();
            }
            if (!puedeAgrupar && _modoVista == "torres")
            {
                _modoVista = "surcos";
                UpdateVistaButton();
            }

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

            // Resize dinámico del widget en función de la geometría del
            // implemento: cantidad de trenes y máximo de sensores por tren.
            // Se ejecuta al recibir el snapshot porque ese es el primer
            // momento donde sabemos la forma final del implemento (puede
            // cambiar en caliente vía ReloadImplemento).
            try { RecomputeDynamicSize(snap); } catch { }

            // Throttle a ~60fps. El monitor reporta a 500ms asi que en la
            // practica no se activa, pero protege si alguien sube la frecuencia.
            if (now - _lastInvalidateTick < 16) return;
            _lastInvalidateTick = now;
            Invalidate();
        }

        // Calcula el tamaño ideal del widget a partir del snapshot:
        //   ancho  = label de tren (110) + N_max_sensores * (sensorW + spacing) + márgenes
        //   alto   = header compacto + fila KPI + ticker + N_trenes * altura_min_tren + footer
        // Clampea contra el parent y respeta un piso/techo razonable. Si el
        // operario arrastró el widget, se preserva la posición custom y solo
        // se clampea contra el área visible.
        private void RecomputeDynamicSize(SeedMonitorSnapshot snap)
        {
            if (Parent == null) return;
            if (snap == null || snap.Trenes == null || snap.Trenes.Length == 0) return;

            int nTrains = snap.Trenes.Length;
            int maxSlots = 0;
            bool modoTorres = _modoVista == "torres" && snap.Torres > 1;

            if (modoTorres)
            {
                // En vista torres, todos los trenes comparten el mismo número
                // de "slots" — la torre. Usamos snap.Torres como tope visible
                // por tren (peor caso: un tren cubre todas las torres).
                maxSlots = snap.Torres;
            }
            else
            {
                foreach (var t in snap.Trenes)
                {
                    if (t == null || t.Surcos == null) continue;
                    // Slots = bajadas únicas (cada bajada puede tener varios sensores
                    // auxiliares — ferti/herramienta — que comparten tubo).
                    var bajadasVistas = new HashSet<int>();
                    foreach (var s in t.Surcos) if (s != null) bajadasVistas.Add(s.Bajada);
                    if (bajadasVistas.Count > maxSlots) maxSlots = bajadasVistas.Count;
                }
            }
            if (maxSlots == 0) maxSlots = 1;

            // En modo torres usamos tubos más anchos — cada torre representa
            // un módulo físico, no una bajada — para que el contraste visual
            // entre torres OK y con falla sea claro a distancia.
            int sensorW = modoTorres ? 36 : 22;
            int spacing = modoTorres ? 6 : 4;
            const int RightMargin = 12;
            int targetW = TrainLabelWidth + maxSlots * sensorW
                          + Math.Max(0, maxSlots - 1) * spacing + RightMargin;

            const int PerTrainH = 60;
            const int KpiBlockH = 38 + 4;     // alto fila KPI + padding
            const int TickerBlockH = AlarmBannerHeight + 4;
            int targetH = HeaderHeight + KpiBlockH + TickerBlockH
                          + nTrains * PerTrainH + FooterHeight + 6;

            int parentW = Parent.Width;
            int parentH = Parent.Height;
            // Topes: nunca exceder el parent menos un margen mínimo de 8 px.
            if (targetW > parentW - 8) targetW = parentW - 8;
            if (targetH > parentH - 8) targetH = parentH - 8;
            // Pisos: el widget tiene que ser usable aunque haya un solo tren
            // con un solo sensor (texto del header, botones, KPIs).
            if (targetW < 280) targetW = 280;
            if (targetH < 140) targetH = 140;

            if (Math.Abs(Size.Width - targetW) <= 2 && Math.Abs(Size.Height - targetH) <= 2)
                return; // mismo tamaño efectivo, sin cambios.

            Size = new Size(targetW, targetH);
            _dynamicSizeApplied = true;

            // Reubicar:
            //   - Sin posición custom → re-centrar horizontalmente y anclar al
            //     pie del parent respetando _panelBottomMargin.
            //   - Con posición custom (operario arrastró) → solo clampear.
            if (!HasCustomPosition)
            {
                int x = (parentW - targetW) / 2;
                int y = parentH - targetH - _panelBottomMargin;
                if (x < 4) x = 4;
                if (y < 4) y = 4;
                Location = new Point(x, y);
            }
            else
            {
                int x = Location.X, y = Location.Y;
                if (x + targetW > parentW - 4) x = parentW - targetW - 4;
                if (y + targetH > parentH - 4) y = parentH - targetH - 4;
                if (x < 4) x = 4;
                if (y < 4) y = 4;
                Location = new Point(x, y);
            }
        }

        // Compatibilidad con VistaXPanel de CefSharp — el caller sigue
        // llamando estos metodos. SetSnapshot es el camino real.
        public void UpdateDisplay(SeedMonitorSnapshot snap) { SetSnapshot(snap); }
        public void FlushPending() { }

        // Si HasCustomPosition=true (el usuario arrastró el panel), Reposition
        // mantiene la posición custom y solo clampea contra el área visible —
        // no recentra. El owner (FormGPS) decide cuándo prender este flag al
        // aplicar la posición persistida.
        public bool HasCustomPosition { get; set; }

        public void Reposition()
        {
            if (Parent == null) return;

            int parentW = Parent.Width;
            int parentH = Parent.Height;

            // Si ya tenemos un tamaño dinámico calculado desde el snapshot
            // (RecomputeDynamicSize), respetarlo y solo re-clampear. La
            // geometría estática del config solo se usa al primer Reposition
            // antes de que llegue el primer snapshot.
            int panelH = _dynamicSizeApplied ? Size.Height : _panelHeight;
            int panelW = _dynamicSizeApplied ? Size.Width
                : (int)(parentW * _panelWidthPercent / 100.0);

            Size = new Size(panelW, panelH);

            if (HasCustomPosition)
            {
                // Clamp solo: si el parent achicó, traemos el panel adentro.
                int x = Location.X;
                int y = Location.Y;
                if (x + panelW > parentW - 4) x = parentW - panelW - 4;
                if (y + panelH > parentH - 4) y = parentH - panelH - 4;
                if (x < 4) x = 4;
                if (y < 4) y = 4;
                Location = new Point(x, y);
            }
            else
            {
                Location = new Point(
                    (parentW - panelW) / 2,
                    parentH - panelH - _panelBottomMargin);
            }
            BringToFront();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutHeaderControls();
        }

        // Doble-click sobre un tubo → toggle de silenciado del sensor que lo
        // alimenta. El owner es responsable de aplicar y persistir el cambio
        // (SeedMonitor.SetSensorMuted + VistaXConfig.SaveImplemento).
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;
            // Buscar en orden inverso (lo último dibujado queda encima).
            for (int i = _tubeHits.Count - 1; i >= 0; i--)
            {
                var hit = _tubeHits[i];
                if (!hit.Rect.Contains(e.Location)) continue;
                if (string.IsNullOrEmpty(hit.Uid)) return; // sin uid no se puede mutear
                var h = SensorMuteToggleRequested;
                if (h != null) h(hit.Uid, hit.Cable, !hit.Muted);
                return;
            }
        }

        private void LayoutHeaderControls()
        {
            // Header compacto Agro Parallel: solo mute + toggle vista a la
            // derecha. Sin ⚙ y sin OBJ — la config vive en el Hub.
            int y = Math.Max(2, (HeaderHeight - 22) / 2);
            if (_btnMute != null)
                _btnMute.Location = new Point(Width - 6 - _btnMute.Width, y);
            if (_btnVista != null && _btnMute != null)
                _btnVista.Location = new Point(_btnMute.Left - _btnVista.Width - 4, y);
        }

        // SyncObjetivoFromSnapshot: el NumericUpDown se eliminó del header,
        // pero el método se mantiene como no-op para preservar la firma por
        // si algún caller externo lo invoca. El objetivo ahora se setea desde
        // el catálogo de Insumos (Hub web).
        private void SyncObjetivoFromSnapshot(double objetivo) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            LayoutHeaderControls();
            // Reset hit-test map en cada repaint — se rellena en DrawTrain.
            _tubeHits.Clear();
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

            // Header compacto del widget: solo banda fina con el título de
            // sección. Sin logo y sin "Agro Parallel" — el branding va en la
            // app principal, el widget overlay debe ocupar el mínimo posible.
            DrawCompactHeader(g, "MONITOR DE SIEMBRA");

            // KPIs en fila debajo del header.
            int kpiY = HeaderHeight + 4;
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
            // Fondo card para la fila de KPIs — 3 KPIs en lugar de 4 (la
            // velocidad ya la muestra PilotX en su propio HUD, no la
            // duplicamos acá). Vista limpia "Agro Parallel".
            var kpiRect = new Rectangle(4, y, Width - 8, 34);
            Theme.FillRoundedRect(g, kpiRect, Theme.BgCard, 6);

            int count = 3;
            int kpiW = (Width - 16) / count;
            int x = 8;

            // DENSIDAD (sem/m).
            DrawMiniKpi(g, x, y + 2, kpiW,
                snap.SpmPromedio.ToString("F1", CultureInfo.InvariantCulture), "s/m", "DENSIDAD");
            x += kpiW;

            // FALLAS — rojo si hay fallas activas.
            Color fallasColor = snap.FallasActivas > 0 ? Theme.Error : Theme.TextPrimary;
            DrawMiniKpi(g, x, y + 2, kpiW,
                snap.FallasActivas.ToString(CultureInfo.InvariantCulture), "", "FALLAS", fallasColor);
            x += kpiW;

            // ONLINE — adapta el label al modo de vista. En "torres" muestra
            // T_activas/T_totales con la misma semántica de "online" del modo surcos.
            bool modoTorres = _modoVista == "torres" && snap.Torres > 1;
            string label;
            string value;
            if (modoTorres)
            {
                int spt = snap.SurcosPorTorre > 0
                    ? snap.SurcosPorTorre
                    : (int)Math.Ceiling((snap.Surcos != null ? snap.Surcos.Length : 0) / (double)Math.Max(1, snap.Torres));
                int torresActivas = ContarTorresActivas(snap, spt);
                value = torresActivas + "/" + snap.Torres;
                label = "TORRES";
            }
            else
            {
                int totalConfig = snap.Surcos != null ? snap.Surcos.Length : 0;
                value = snap.SurcosActivos + "/" + totalConfig;
                label = "SURCOS";
            }
            DrawMiniKpi(g, x, y + 2, kpiW, value, "", label);
        }

        // Una torre está "activa" si al menos uno de sus surcos reporta datos.
        // Usamos la misma definición que SurcosActivos del snapshot.
        private int ContarTorresActivas(SeedMonitorSnapshot snap, int spt)
        {
            if (snap.Surcos == null || snap.Surcos.Length == 0 || snap.Torres <= 0 || spt <= 0) return 0;
            var torresConDatos = new HashSet<int>();
            foreach (var s in snap.Surcos)
            {
                if (s == null || s.Spm <= 0) continue;
                int torreIdx = (s.Bajada - 1) / spt + 1;
                if (torreIdx >= 1 && torreIdx <= snap.Torres) torresConDatos.Add(torreIdx);
            }
            return torresConDatos.Count;
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
            DrawCompactHeader(g, "MONITOR DE SIEMBRA");

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
                    g.DrawString(vel, fVal, br, (Width - sz.Width) / 2f, HeaderHeight + 16);
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
                    HeaderHeight + 42);
            }
        }

        // Header compacto del widget overlay: banda fina con el título de
        // sección en verde acento, alineada a la izquierda. NO incluye el
        // logo "Agro Parallel" ni el texto de marca — el branding va en la
        // app principal, el widget sobre el mapa pide minimalismo.
        private void DrawCompactHeader(Graphics g, string sectionName)
        {
            using (var b = new SolidBrush(Theme.BgHeader))
                g.FillRectangle(b, 0, 0, Width, HeaderHeight);
            using (var pen = new Pen(Theme.Border))
                g.DrawLine(pen, 0, HeaderHeight - 1, Width, HeaderHeight - 1);

            if (!string.IsNullOrEmpty(sectionName))
            {
                using (var f = new Font(Theme.FontFamily, 8.5f, FontStyle.Bold))
                using (var br = new SolidBrush(Theme.Accent))
                    g.DrawString(sectionName, f, br, 8, 7);
            }
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

            // En modo torres, ignoramos la división por tren — las torres
            // cruzan los trenes (ej. 8 torres × 12 surcos = 96 surcos, sin
            // importar cuántos trenes haya). Pintamos una sola fila ancha.
            bool modoTorres = _modoVista == "torres" && snap.Torres > 1;
            if (modoTorres)
            {
                DrawTorres(g, snap, bodyTop, bodyBottom);
                return;
            }

            for (int i = 0; i < n; i++)
            {
                var tren = snap.Trenes[i];
                if (tren == null) continue;
                int y0 = bodyTop + i * perTrainH;
                int y1 = y0 + perTrainH;
                DrawTrain(g, tren, y0, y1, snap.ToleranciaDesvio);
            }
        }

        // Vista TORRES: agrupa todas las bajadas por torre = ceil(bajada/SurcosPorTorre).
        // Una sola fila de tubos anchos — uno por torre. Estado de la torre =
        // peor estado de sus surcos (precedencia tapado > bajo > exceso > nodata > muted > ok),
        // SPM promediado sobre surcos con datos. Diseñado para sembradoras
        // grandes (ej. 96 surcos / 8 torres) donde ver 96 tubitos no es útil.
        private void DrawTorres(Graphics g, SeedMonitorSnapshot snap, int bodyTop, int bodyBottom)
        {
            int spt = snap.SurcosPorTorre > 0
                ? snap.SurcosPorTorre
                : (int)Math.Ceiling((snap.Surcos != null ? snap.Surcos.Length : 0) / (double)Math.Max(1, snap.Torres));
            if (spt <= 0) spt = 1;

            // Agrupar surcos en torres por POSICIÓN GLOBAL a lo largo de la barra.
            // Las torres NO distinguen entre tren 1 / tren 2 — el mapeo es continuo
            // sobre toda la sembradora. Por eso ordenamos por (tren, bajada) y
            // usamos el índice del flat array; el campo Bajada por sí solo no sirve
            // porque se reinicia en cada tren (1..N por tren).
            var torres = new SortedDictionary<int, List<SurcoState>>();
            for (int k = 1; k <= snap.Torres; k++) torres[k] = new List<SurcoState>();
            if (snap.Surcos != null)
            {
                var flat = snap.Surcos
                    .Where(s => s != null)
                    .OrderBy(s => s.Tren)
                    .ThenBy(s => s.Bajada)
                    .ToList();
                for (int i = 0; i < flat.Count; i++)
                {
                    int k = Math.Min(snap.Torres, i / spt + 1);
                    if (k < 1) k = 1;
                    torres[k].Add(flat[i]);
                }
            }

            int drawAreaLeft = TrainLabelWidth;
            int drawAreaRight = Width - 8;
            int drawAreaW = drawAreaRight - drawAreaLeft;
            if (drawAreaW < 32) return;

            int slotCount = snap.Torres;
            int sensorW = 36;
            int spacing = 6;
            int total = slotCount * sensorW + Math.Max(0, slotCount - 1) * spacing;
            if (total > drawAreaW)
            {
                int available = drawAreaW - Math.Max(0, slotCount - 1) * 2;
                sensorW = Math.Max(10, available / Math.Max(1, slotCount));
                spacing = 2;
                total = slotCount * sensorW + (slotCount - 1) * spacing;
            }
            int offsetX = drawAreaLeft + (drawAreaW - total) / 2;

            // Label "TORRES" a la izquierda — reemplaza el nombre del tren.
            using (var fLbl = new Font(Theme.FontFamily, 7.5f, FontStyle.Bold))
            using (var lblBrush = new SolidBrush(Theme.TextFaint))
                g.DrawString("TORRES", fLbl, lblBrush, 6, bodyTop + 2);

            int tubeTop = bodyTop + 14;
            int tubeBottom = bodyBottom - 2;
            int tubeAreaH = tubeBottom - tubeTop;
            if (tubeAreaH < 8) return;

            // Buscar el objetivo "del implemento" — usamos el primer tren con
            // objetivo > 0 como referencia. Para sembradoras uniformes los
            // trenes comparten objetivo; si difieren, esto es una aproximación.
            double objetivo = 0;
            if (snap.Trenes != null)
            {
                foreach (var t in snap.Trenes)
                    if (t != null && t.Objetivo > 0) { objetivo = t.Objetivo; break; }
            }

            int i2 = 0;
            foreach (var kv in torres)
            {
                int torreIdx = kv.Key;
                var surcos = kv.Value;
                int x = offsetX + i2 * (sensorW + spacing);
                i2++;

                // Estado de la torre = peor entre sus surcos. Promedio SPM
                // sólo sobre surcos con datos > 0.
                PillStatus worst = PillStatus.NoData;
                int worstRank = -1;
                double sumSpm = 0; int nSpm = 0; int fallas = 0;
                foreach (var s in surcos)
                {
                    PillStatus st = ClassifySurco(s, snap.ToleranciaDesvio, objetivo);
                    int rank = PillStatusRank(st);
                    if (rank > worstRank) { worstRank = rank; worst = st; }
                    if (s.Spm > 0) { sumSpm += s.Spm; nSpm++; }
                    if (st == PillStatus.Tapado || st == PillStatus.Bajo || st == PillStatus.Exceso) fallas++;
                }
                double avgSpm = nSpm > 0 ? sumSpm / nSpm : 0;
                double ratio = objetivo > 0 ? avgSpm / objetivo : 0;

                // Altura del "líquido" del tubo: proporcional al ratio,
                // clampeado entre 10% y 95% para que siempre se vea algo.
                double pctH;
                if (worst == PillStatus.NoData) pctH = 10;
                else if (worst == PillStatus.Muted) pctH = 30;
                else if (worst == PillStatus.Tapado) pctH = 12;
                else pctH = Math.Max(15, Math.Min(95, ratio * 75.0));
                int actualH = Math.Max(2, (int)Math.Round(tubeAreaH * pctH / 100.0));
                int tubeY = tubeTop + (tubeAreaH - actualH);

                DrawTube(g, x, tubeY, sensorW, actualH, tubeAreaH, tubeTop, worst, ratio);

                // Etiqueta T<k> sobre el tubo + badge rojo de #fallas (si > 0).
                using (var fTag = new Font(Theme.FontFamily, 7f, FontStyle.Bold))
                using (var brTag = new SolidBrush(Theme.TextSecondary))
                {
                    string tag = "T" + torreIdx;
                    var sz = g.MeasureString(tag, fTag);
                    g.DrawString(tag, fTag, brTag, x + (sensorW - sz.Width) / 2f, tubeTop - 11);
                }
                if (fallas > 0)
                {
                    using (var brBg = new SolidBrush(Theme.Error))
                        g.FillEllipse(brBg, x + sensorW - 12, tubeTop + 2, 10, 10);
                    using (var fNum = new Font(Theme.FontFamily, 6.5f, FontStyle.Bold))
                    using (var brNum = new SolidBrush(Color.White))
                    {
                        string n2 = fallas.ToString(CultureInfo.InvariantCulture);
                        var sz = g.MeasureString(n2, fNum);
                        g.DrawString(n2, fNum, brNum,
                            x + sensorW - 12 + (10 - sz.Width) / 2f,
                            tubeTop + 1 + (10 - sz.Height) / 2f);
                    }
                }

                // Hit-test: click sobre la torre → no hace nada (todavía).
                // Si en el futuro queremos zoom a la torre, registrar acá.
                _tubeHits.Add(new TubeHit
                {
                    Rect = new Rectangle(x, tubeTop, sensorW, tubeAreaH),
                    Uid = "",
                    Cable = torreIdx,
                    Muted = false
                });
            }
        }

        // Clasificación de un surco — misma lógica que DrawTrain pero extraída
        // para reusarla en la vista torres.
        private PillStatus ClassifySurco(SurcoState s, double tolerancia, double objetivo)
        {
            if (s == null) return PillStatus.NoData;
            if (s.Muted) return PillStatus.Muted;
            if (s.SeccionCortada) return PillStatus.NoData;
            if (s.Alerta && s.Spm <= 0.5) return PillStatus.Tapado;
            if (s.Spm <= 0 && s.LastUpdate == DateTime.MinValue) return PillStatus.NoData;
            if (s.Spm <= 0) return PillStatus.Tapado;
            if (objetivo <= 0) return PillStatus.Ok;
            double tol = Math.Max(0, tolerancia) / 100.0;
            double ratio = s.Spm / objetivo;
            if (ratio > 1.0 + tol) return PillStatus.Exceso;
            if (ratio < 1.0 - tol) return PillStatus.Bajo;
            return PillStatus.Ok;
        }

        // Precedencia: peor estado domina al pintar una torre completa.
        // Tapado/Bajo/Exceso son fallas; NoData/Muted indican ausencia de
        // datos pero no son fallas; Ok es el estado sano.
        private static int PillStatusRank(PillStatus st)
        {
            switch (st)
            {
                case PillStatus.Tapado: return 5;
                case PillStatus.Bajo:   return 4;
                case PillStatus.Exceso: return 3;
                case PillStatus.NoData: return 2;
                case PillStatus.Muted:  return 1;
                case PillStatus.Ok:     return 0;
                default: return -1;
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
                // Clasificar — orden de precedencia:
                //   Muted > SeccionCortada(→NoData) > Tapado > NoData > Exceso > Bajo > Ok
                PillStatus status;
                double pctHeight; // % del tubeAreaH que ocupa el tubo (bottom-aligned).
                double ratio = objetivo > 0 ? s.Spm / objetivo : 0.0;

                if (s.Muted)
                {
                    status = PillStatus.Muted;
                    pctHeight = 30;
                }
                else if (s.SeccionCortada)
                {
                    // Sección cortada — no es falla, solo "no aplica".
                    status = PillStatus.NoData;
                    pctHeight = 6;
                }
                else if (s.Alerta && s.Spm <= 0.5)
                {
                    // Flujo bloqueado confirmado por alarma de timeout o por valor 0.
                    status = PillStatus.Tapado;
                    pctHeight = 15;
                }
                else if (s.Spm <= 0 && s.LastUpdate == DateTime.MinValue)
                {
                    // Nunca recibió datos — gris idle, distinguible de tapado.
                    status = PillStatus.NoData;
                    pctHeight = 10;
                }
                else if (s.Spm <= 0)
                {
                    // Recibió datos antes pero ahora está en 0 → asumir tapado.
                    status = PillStatus.Tapado;
                    pctHeight = 8;
                }
                else if (objetivo > 0)
                {
                    double tol = Math.Max(0, tolerancia) / 100.0; // % → fracción
                    pctHeight = Math.Max(10, Math.Min(95, ratio * 75.0));
                    if (ratio > 1.0 + tol) status = PillStatus.Exceso;
                    else if (ratio < 1.0 - tol) status = PillStatus.Bajo;
                    else                       status = PillStatus.Ok;
                }
                else
                {
                    // Sin objetivo definido: altura fija media, color Ok.
                    status = PillStatus.Ok;
                    pctHeight = 50;
                }

                int actualH = Math.Max(2, (int)Math.Round(tubeAreaH * pctHeight / 100.0));
                int tubeY = tubeTop + (tubeAreaH - actualH);

                DrawTube(g, x, tubeY, sensorW, actualH, tubeAreaH, tubeTop, status, ratio);
                DrawLed(g, x + sensorW / 2, tubeTop - ledR - 2, ledR,
                    status == PillStatus.Tapado ? RowState.Failure :
                    status == PillStatus.Bajo   ? RowState.LowRate :
                    status == PillStatus.Exceso ? RowState.HighRate :
                    status == PillStatus.Ok     ? RowState.Ok : RowState.NoData);

                // Registrar hit-test para doble-click → toggle mute.
                _tubeHits.Add(new TubeHit
                {
                    Rect = new Rectangle(x, tubeTop, sensorW, tubeAreaH),
                    Uid = s.Uid ?? "",
                    Cable = s.Cable,
                    Muted = s.Muted
                });

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

        // Estados de píldora — alineados con la paleta web (vistax.js):
        //   Ok       → verde sólido
        //   Bajo     → degradé negro→verde según ratio real/objetivo (0..1)
        //   Tapado   → negro pleno (alerta: flujo bloqueado)
        //   Exceso   → azul (por encima del objetivo + tolerancia)
        //   NoData   → gris idle (sin lecturas / timeout)
        //   Muted    → gris desaturado (silenciado por operario)
        private enum PillStatus { Ok, Bajo, Tapado, Exceso, NoData, Muted }

        private void DrawTube(Graphics g, int x, int y, int w, int h,
            int fullH, int fullTop, PillStatus status, double ratio)
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
                case PillStatus.Tapado:
                    using (var b = new SolidBrush(CBlocked))
                        g.FillRectangle(b, rect);
                    return;
                case PillStatus.NoData:
                    using (var b = new SolidBrush(CNoData))
                        g.FillRectangle(b, rect);
                    return;
                case PillStatus.Muted:
                    using (var b = new SolidBrush(CMuted))
                        g.FillRectangle(b, rect);
                    // Marca de mute: línea horizontal central (igual que la UI web).
                    using (var pen = new Pen(CTextDim, 2f))
                    {
                        int midY = fullTop + fullH / 2;
                        g.DrawLine(pen, x + 2, midY, x + w - 2, midY);
                    }
                    return;
                case PillStatus.Exceso:
                    topColor = CExceso;
                    botColor = CExcesoDark;
                    break;
                case PillStatus.Bajo:
                    // Degradé negro→verde según ratio (clamp 0..1). Cuanto más
                    // cerca del objetivo, más verde; tapado completo ≈ negro.
                    double r = Math.Max(0, Math.Min(1, ratio));
                    int rr = (int)Math.Round(5 + (CAccent.R - 5) * r);
                    int gg = (int)Math.Round(5 + (CAccent.G - 5) * r);
                    int bb = (int)Math.Round(5 + (CAccent.B - 5) * r);
                    topColor = Color.FromArgb(rr, gg, bb);
                    botColor = CBlocked;
                    break;
                default: // Ok
                    topColor = CAccent;
                    botColor = CAccentDark;
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
