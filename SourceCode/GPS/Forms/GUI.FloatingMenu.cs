//PilotX: submenú flotante — un panel móvil (arrastrable) y cerrable que agrupa
//los botones de todos los menús. Se lanza desde un botón siempre visible.
//Navegación jerárquica: la vista inicial muestra las categorías; al tocar una
//se listan sus botones en la misma ventana, con "‹" para volver.
//Los ítems disparan el Click del botón original vía InvokeOnClick (funciona
//aunque el botón original esté en un panel oculto).

using System;
using System.Drawing;
using System.Windows.Forms;

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        private Panel panelFloatMenu;
        private FlowLayoutPanel flowFloatMenu;
        private Button btnFloatMenuLauncher;
        private Label lblFloatMenuTitle;
        private Button btnFloatMenuBack;
        private Point floatMenuDragStart;
        private bool floatMenuDragging;
        private Action floatMenuBackAction; //a dónde vuelve la flecha atrás (null = home)

        //Auto-ocultado de botoneras: 15 s después de mostrarse (o de la última
        //interacción — hover/touch/click sobre las barras manda
        //"paneles_keepalive" y reinicia el contador) se esconden solas; la
        //flecha del mapa (MenuShowHide) las vuelve a traer.
        private Timer timerOcultarPaneles;

        public void ReiniciarTimerOcultarPaneles()
        {
            if (timerOcultarPaneles == null)
            {
                timerOcultarPaneles = new Timer { Interval = 15000 };
                timerOcultarPaneles.Tick += (s, e) =>
                {
                    timerOcultarPaneles.Stop();
                    if (isJobStarted && !isPanelBottomHidden)
                    {
                        isPanelBottomHidden = true;
                        PanelsAndOGLSize();
                    }
                };
            }
            timerOcultarPaneles.Stop();
            if (isJobStarted && !isPanelBottomHidden) timerOcultarPaneles.Start();
        }

        //paleta PilotX
        private static readonly Color pxBgSoft = Color.FromArgb(0xF5, 0xF7, 0xF4);
        private static readonly Color pxBg = Color.FromArgb(0xE2, 0xE7, 0xE2);
        private static readonly Color pxBorder = Color.FromArgb(0xC5, 0xCF, 0xC5);
        private static readonly Color pxText = Color.FromArgb(0x10, 0x16, 0x12);
        private static readonly Color pxGreen = Color.FromArgb(0x4A, 0xBA, 0x3E);

        // ==================================================================
        // Modo "barras HTML": las 3 barras espejo (superior/derecha/abajo)
        // REEMPLAZAN a las nativas (panelControlBox/panelRight/panelBottom),
        // que quedan intactas pero ocultas. Se dockean a los bordes del mapa
        // vía FloatingDock y las muestra/oculta la MISMA flecha de siempre
        // (btnTogglePaneles) + el auto-ocultado de 15 s. Persistente entre
        // arranques (archivo flag en AgroParallel\).
        // ==================================================================
        public bool isHtmlBarsMode = false;

        private const string BarraTopPage = "pages/barra-superior.html";
        private const string BarraRightPage = "pages/barra-derecha.html";
        private const string BarraBottomPage = "pages/barra-abajo.html";
        private const string BarraLeftPage = "pages/menu-izquierda.html";

        private static string BarrasHtmlFlagPath => System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "AgroParallel", "barras-html.on");

        public void ToggleBarrasHtml()
        {
            isHtmlBarsMode = !isHtmlBarsMode;
            try
            {
                string flag = BarrasHtmlFlagPath;
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(flag));
                if (isHtmlBarsMode) System.IO.File.WriteAllText(flag, "1");
                else if (System.IO.File.Exists(flag)) System.IO.File.Delete(flag);
            }
            catch { /* persistencia best-effort */ }
            isPanelBottomHidden = false; //arrancar el modo nuevo con todo a la vista
            PanelsAndOGLSize();
        }

        //Sincroniza las 3 barras dockeadas con el estado actual. La llama
        //PanelsAndOGLSize (misma frecuencia con la que el nativo repinta
        //sus paneles). Con el modo apagado cierra las barras y restaura
        //panelControlBox.
        public void ActualizarBarrasHtml()
        {
            if (!isHtmlBarsMode)
            {
                CerrarBarraHtmlDock(BarraTopPage);
                CerrarBarraHtmlDock(BarraRightPage);
                CerrarBarraHtmlDock(BarraBottomPage);
                CerrarBarraHtmlDock(BarraLeftPage);
                //menuStrip1 NO se restaura: la hamburguesa nativa quedó
                //retirada definitivamente (se oculta en FormGPS_Load).
                try { panelControlBox.Visible = true; } catch { }
                return;
            }

            try { panelControlBox.Visible = false; menuStrip1.Visible = false; } catch { }

            //superior: SIEMPRE visible (pedido 2026-07-13): reemplaza por
            //completo al panelControlBox nativo y no participa del
            //auto-ocultado de la flecha. Derecha/abajo: solo con lote
            //abierto y botoneras visibles.
            bool operables = isJobStarted && !isPanelBottomHidden;
            //ancha: incluye hamburguesa + chips de info
            //(fecha, lat/lon, señal, ha/h, ha hechas)
            MostrarBarraHtmlDock(BarraTopPage, "Barra superior",
                new Size(1020, 64), "top", new Padding(0), true);
            //izquierda: reemplaza al panelLeft nativo — visible salvo flecha,
            //con margen abajo para no tapar btnTogglePaneles ni el zoom.
            MostrarBarraHtmlDock(BarraLeftPage, "Menú izquierda",
                new Size(116, 0), "left", new Padding(0, 76, 0, 200),
                !(isJobStarted && isPanelBottomHidden));
            MostrarBarraHtmlDock(BarraRightPage, "Barra derecha",
                new Size(74, 0), "right", new Padding(0, 76, 0, 84), operables);
            MostrarBarraHtmlDock(BarraBottomPage, "Barra abajo",
                new Size(0, 74), "bottom", new Padding(0, 0, 84, 0), operables);
        }

        private void MostrarBarraHtmlDock(string page, string title, Size sz,
            string dock, Padding margin, bool visible)
        {
            try
            {
                if (_floatWidgets == null)
                    _floatWidgets = new System.Collections.Generic.Dictionary<string, Form>(StringComparer.OrdinalIgnoreCase);

                if (_floatWidgets.TryGetValue(page, out var existing)
                    && existing != null && !existing.IsDisposed)
                {
                    var hub = existing as global::AgroParallel.Shell.FormAgroParallelHubWebView2;
                    if (hub != null && hub.FloatingDock == dock)
                    {
                        existing.Visible = visible;
                        return;
                    }
                    //estaba abierta como widget flotante común: recrear dockeada
                    try { existing.Close(); } catch { }
                }
                if (!visible) return;

                string initialPage = page + (page.IndexOf('?') < 0 ? "?widget=1" : "&widget=1");
                var widget = BuildHubForm(initialPage);
                widget.FloatingWidget = true;
                widget.FloatingDock = dock;
                widget.DockMargin = margin;
                widget.FloatingSize = sz;
                if (!string.IsNullOrEmpty(title)) widget.Text = title;
                widget.AnchorControl = this.oglMainControl;
                _floatWidgets[page] = widget;
                widget.FormClosed += (s, e) => { _floatWidgets.Remove(page); };
                widget.Show(this);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BarrasHtml] " + ex.Message);
            }
        }

        //Cierra la barra SOLO si está dockeada (no toca un widget flotante
        //común que el operario haya abierto desde el menú).
        private void CerrarBarraHtmlDock(string page)
        {
            try
            {
                if (_floatWidgets != null
                    && _floatWidgets.TryGetValue(page, out var w)
                    && w != null && !w.IsDisposed
                    && w is global::AgroParallel.Shell.FormAgroParallelHubWebView2 hub
                    && !string.IsNullOrEmpty(hub.FloatingDock))
                {
                    w.Close();
                }
            }
            catch { }
        }

        //Botón flecha para ocultar/mostrar las botoneras (menú viejo AOG).
        //Es un Button real (no el dibujito GL MenuShowHide, que quedaba tapado
        //por overlays) — siempre visible con lote abierto, esquina inf. izq.
        private Button btnTogglePaneles;

        private void CreateTogglePanelesButton()
        {
            btnTogglePaneles = new Button
            {
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                Image = FloatMenuGlyph(0xE5CB, 26, pxText), //chevron izquierda (= ocultar)
                FlatStyle = FlatStyle.Flat,
                BackColor = pxBg,
                ForeColor = pxText,
                TabStop = false,
                Visible = false
            };
            btnTogglePaneles.FlatAppearance.BorderColor = pxBorder;
            btnTogglePaneles.Click += (s, e) =>
            {
                isPanelBottomHidden = !isPanelBottomHidden;
                PanelsAndOGLSize();
            };
            Controls.Add(btnTogglePaneles);
        }

        //visibilidad + glifo según el estado; lo llama PanelsAndOGLSize
        public void ActualizarTogglePaneles()
        {
            if (btnTogglePaneles == null) return;
            btnTogglePaneles.Visible = isJobStarted;
            btnTogglePaneles.Image = FloatMenuGlyph(
                isPanelBottomHidden ? 0xE5CC : 0xE5CB, 26, pxText); //» mostrar / « ocultar
            btnTogglePaneles.SetBounds(6, ClientSize.Height - 190, 56, 56);
            btnTogglePaneles.BringToFront();
        }

        public void CreateFloatingMenu()
        {
            CreateTogglePanelesButton();

            //restaurar el modo barras HTML del arranque anterior
            try { isHtmlBarsMode = System.IO.File.Exists(BarrasHtmlFlagPath); } catch { }

            btnFloatMenuLauncher = new Button
            {
                Text = "Menú",
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                Image = FloatMenuGlyph(0xE5D2, 22, pxText), //hamburguesa
                TextImageRelation = TextImageRelation.ImageBeforeText,
                TextAlign = ContentAlignment.MiddleRight,
                ImageAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 10, 0),
                FlatStyle = FlatStyle.Flat,
                BackColor = pxBg,
                ForeColor = pxText,
                TabStop = false,
                Visible = true
            };
            btnFloatMenuLauncher.FlatAppearance.BorderColor = pxBorder;
            btnFloatMenuLauncher.Click += (s, e) => ToggleFloatMenu();
            Controls.Add(btnFloatMenuLauncher);
            PositionFloatMenuLauncher();
        }

        public void PositionFloatMenuLauncher()
        {
            if (btnFloatMenuLauncher == null) return;
            //en modo barras HTML el lanzador hamburguesa sobra: todo vive en
            //la barra superior y el menú izquierdo HTML (pedido 2026-07-16)
            btnFloatMenuLauncher.Visible = !isHtmlBarsMode;
            btnFloatMenuLauncher.SetBounds((ClientSize.Width - 110) / 2, 4, 110, 44);
            btnFloatMenuLauncher.BringToFront();

            //si el panel quedó fuera de la ventana tras un resize, lo reencuadra
            if (panelFloatMenu != null && panelFloatMenu.Visible)
            {
                panelFloatMenu.Left = Math.Max(0, Math.Min(panelFloatMenu.Left, ClientSize.Width - panelFloatMenu.Width));
                panelFloatMenu.Top = Math.Max(0, Math.Min(panelFloatMenu.Top, ClientSize.Height - 60));
                panelFloatMenu.BringToFront();
            }
        }

        private void ToggleFloatMenu()
        {
            if (panelFloatMenu == null) BuildFloatMenu();

            if (panelFloatMenu.Visible)
            {
                panelFloatMenu.Visible = false;
            }
            else
            {
                FloatMenuShowHome();
                panelFloatMenu.Location = new Point(
                    (ClientSize.Width - panelFloatMenu.Width) / 2,
                    Math.Max(52, (ClientSize.Height - panelFloatMenu.Height) / 2));
                panelFloatMenu.Visible = true;
                panelFloatMenu.BringToFront();
            }
        }

        private void BuildFloatMenu()
        {
            panelFloatMenu = new Panel
            {
                Size = new Size(460, 540),
                BackColor = pxBgSoft,
                BorderStyle = BorderStyle.FixedSingle,
                Visible = false
            };

            //header arrastrable: [‹] título ......... [✕]
            var header = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = pxBg };

            btnFloatMenuBack = new Button
            {
                Image = FloatMenuGlyph(0xE5C4, 20, pxText), //flecha atrás
                FlatStyle = FlatStyle.Flat,
                BackColor = pxBg,
                ForeColor = pxText,
                Dock = DockStyle.Left,
                Width = 52,
                TabStop = false,
                Visible = false
            };
            btnFloatMenuBack.FlatAppearance.BorderSize = 0;
            btnFloatMenuBack.Click += (s, e) =>
            {
                if (floatMenuBackAction != null) floatMenuBackAction();
                else FloatMenuShowHome();
            };

            lblFloatMenuTitle = new Label
            {
                Text = "Menú PilotX",
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                ForeColor = pxText,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
                Padding = new Padding(12, 0, 0, 0)
            };

            var btnClose = new Button
            {
                Image = FloatMenuGlyph(0xE5CD, 18, pxText), //cerrar
                FlatStyle = FlatStyle.Flat,
                BackColor = pxBg,
                ForeColor = pxText,
                Dock = DockStyle.Right,
                Width = 52,
                TabStop = false
            };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.Click += (s, e) => panelFloatMenu.Visible = false;

            //arrastre del panel por el header (label o fondo)
            void HookDrag(Control c)
            {
                c.MouseDown += (s, e) =>
                {
                    if (e.Button == MouseButtons.Left)
                    { floatMenuDragging = true; floatMenuDragStart = e.Location; }
                };
                c.MouseMove += (s, e) =>
                {
                    if (floatMenuDragging)
                    {
                        panelFloatMenu.Left += e.X - floatMenuDragStart.X;
                        panelFloatMenu.Top += e.Y - floatMenuDragStart.Y;
                    }
                };
                c.MouseUp += (s, e) => floatMenuDragging = false;
            }
            HookDrag(header);
            HookDrag(lblFloatMenuTitle);

            header.Controls.Add(lblFloatMenuTitle);
            header.Controls.Add(btnClose);
            header.Controls.Add(btnFloatMenuBack);

            flowFloatMenu = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoScroll = true,
                Padding = new Padding(8, 8, 8, 8),
                BackColor = pxBgSoft
            };

            panelFloatMenu.Controls.Add(flowFloatMenu);
            panelFloatMenu.Controls.Add(header);
            Controls.Add(panelFloatMenu);
        }

        //--- navegación ---

        private void FloatMenuShowHome()
        {
            flowFloatMenu.SuspendLayout();
            flowFloatMenu.Controls.Clear();
            lblFloatMenuTitle.Text = "Menú PilotX";
            btnFloatMenuBack.Visible = false;
            floatMenuBackAction = null;

            //Arquitectura del menú (2026-07-09, revisión completa contra el menú
            //viejo de AOG): categorías por TAREA del operario, sin duplicados.
            //  Operación   → lo que se toca mientras se trabaja (steer/secciones)
            //  Guías       → crear/elegir/mover guías (ex Líneas + sueltos)
            //  Lote        → abrir lote y cosas del lote (banderas, mapeo, tram)
            //  Lindero…    → límites y cabeceras
            //  Ruta grabada→ grabación/reproducción de recorridos
            //  Vista       → cámara/pantalla
            //  Configuración → FormConfig + perfiles + colores + directorios
            //  Diagnóstico → gráficos/asistentes/visores (ex Herramientas)
            //  Agro Parallel → Hub/CoreX/Cámaras/VistaX
            //  Simulador   → ON/OFF + controles del sim
            //  Sistema     → ventana/kiosko/reset/ayuda/apagar
            FloatMenuAddCategory("Operación", FloatMenuGlyph(0xE55D, 38, pxGreen), FloatMenuFillOperacion);
            FloatMenuAddCategory("Guías", FloatMenuGlyph(0xE922, 38, pxGreen), FloatMenuFillGuias);
            FloatMenuAddCategory("Lote", FloatMenuGlyph(0xE55F, 38, pxGreen), FloatMenuFillLote);
            FloatMenuAddCategory("Lindero y cabecera", FloatMenuScaleIcon(Properties.Resources.Boundary, 38), FloatMenuFillLindero);
            FloatMenuAddCategory("Ruta grabada", FloatMenuGlyph(0xEACD, 38, pxGreen), FloatMenuFillRutaGrabada);
            FloatMenuAddCategory("Vista", FloatMenuGlyph(0xE8F4, 38, pxGreen), FloatMenuFillVista);
            FloatMenuAddCategory("Configuración", FloatMenuGlyph(0xE8B8, 38, pxGreen), FloatMenuFillConfig);
            FloatMenuAddCategory("Diagnóstico", FloatMenuGlyph(0xE6E1, 38, pxGreen), FloatMenuFillDiagnostico);
            FloatMenuAddCategory("Agro Parallel", FloatMenuGlyph(0xE80B, 38, pxGreen), FloatMenuFillAgroParallel);
            FloatMenuAddCategory("Simulador", FloatMenuGlyph(0xF06C, 38, pxGreen), FloatMenuFillSimulador);
            FloatMenuAddCategory("Sistema", FloatMenuGlyph(0xE30C, 38, pxGreen), FloatMenuFillSistema);

            flowFloatMenu.ResumeLayout();
            FloatMenuFitToContent(4);
        }

        private void FloatMenuShowCategory(string title, Action fill, Action back = null)
        {
            flowFloatMenu.SuspendLayout();
            flowFloatMenu.Controls.Clear();
            lblFloatMenuTitle.Text = title;
            btnFloatMenuBack.Visible = true;
            floatMenuBackAction = back;
            fill();
            flowFloatMenu.ResumeLayout();
            FloatMenuFitToContent(4);
        }

        //ajusta el tamaño de la ventana al contenido (grilla de íconos)
        private void FloatMenuFitToContent(int maxCols)
        {
            if (flowFloatMenu.Controls.Count == 0) return;

            var c0 = flowFloatMenu.Controls[0];
            int itemW = c0.Width + c0.Margin.Horizontal;
            int itemH = c0.Height + c0.Margin.Vertical;
            int n = flowFloatMenu.Controls.Count;

            int cols = Math.Min(n, maxCols);
            int rows = (n + cols - 1) / cols;

            int w = cols * itemW + flowFloatMenu.Padding.Horizontal + 6;
            int h = rows * itemH + flowFloatMenu.Padding.Vertical + 44 + 6; //44 = header

            //no desbordar la ventana principal: si no entra, scrollea
            int maxH = ClientSize.Height - 60;
            if (h > maxH)
            {
                h = maxH;
                w += SystemInformation.VerticalScrollBarWidth;
            }

            panelFloatMenu.Size = new Size(w, h);

            //reencuadrar dentro de la ventana
            panelFloatMenu.Left = Math.Max(0, Math.Min(panelFloatMenu.Left, ClientSize.Width - w));
            panelFloatMenu.Top = Math.Max(0, Math.Min(panelFloatMenu.Top, ClientSize.Height - h));
        }

        //--- contenido de cada categoría (se van sumando iterativamente) ---

        //lo que el operario toca MIENTRAS trabaja: steer, secciones, U-Turn
        private void FloatMenuFillOperacion()
        {
            FloatMenuAddButton("AutoSteer", btnAutoSteer);
            FloatMenuAddButton("AutoTrack", btnAutoTrack);
            FloatMenuAddButton("U-Turn", btnAutoYouTurn);
            FloatMenuAddButton("Saltos U-Turn", btnYouSkipEnable);
            //ancho de salto (combobox original): cicla las opciones
            FloatMenuAddAction("Filas salto: " + (cboxpRowWidth.SelectedItem ?? "—"),
                FloatMenuGlyph(0xE3EC, 30, pxText), () =>
                {
                    if (cboxpRowWidth.Items.Count > 0)
                        cboxpRowWidth.SelectedIndex =
                            (cboxpRowWidth.SelectedIndex + 1) % cboxpRowWidth.Items.Count;
                    FloatMenuShowCategory("Operación", FloatMenuFillOperacion, floatMenuBackAction);
                });
            FloatMenuAddButton("Secc. Auto", btnSectionMasterAuto);
            FloatMenuAddButton("Secc. Manual", btnSectionMasterManual);
            //checkbox: habilita/deshabilita el control de secciones
            FloatMenuAddAction("Control: " + (cboxIsSectionControlled.Checked ? "SÍ" : "NO"),
                FloatMenuGlyph(0xE9F6, 30, pxText), () =>
                {
                    cboxIsSectionControlled.Checked = !cboxIsSectionControlled.Checked;
                    FloatMenuShowCategory("Operación", FloatMenuFillOperacion, floatMenuBackAction);
                });
            FloatMenuAddButton("ISOBUS", btnIsobusSectionControl);
            FloatMenuAddButton("Hidráulico", btnHydLift);
        }

        //lote: abrir/crear lote y todo lo que se hace DENTRO del lote
        private void FloatMenuFillLote()
        {
            //Accesos directos de lote (pedido usuario 2026-07-20): continuar el
            //último, abrir uno existente y cerrar el activo — sin pasar por el
            //submenú intermedio. "Continuar" solo aparece si hay un último lote;
            //"Cerrar" solo si hay uno abierto.
            if (!string.IsNullOrEmpty(currentFieldDirectory))
                FloatMenuAddAction("Continuar lote", FloatMenuGlyph(0xE037, 30, pxGreen), () =>
                {
                    panelFloatMenu.Visible = false;
                    FileOpenField("Resume");
                });

            FloatMenuAddAction("Abrir lote", FloatMenuGlyph(0xE2C8, 30, pxText), () =>
            {
                panelFloatMenu.Visible = false;
                FloatMenuAbrirLote();
            });

            if (isJobStarted)
                FloatMenuAddAction("Cerrar lote", FloatMenuGlyph(0xE5CD, 30, pxText), () =>
                {
                    panelFloatMenu.Visible = false;
                    JobClose();
                });

            //abre el menú de lote (lote nuevo, abrir existente, cerrar, desde KML…)
            FloatMenuAddButton("Lote nuevo / abrir", btnJobMenu);
            FloatMenuAddButton("Datos lote", btnFieldStats);
            FloatMenuAddButton("Bandera", btnFlag);
            FloatMenuAddAction("Bandera lat/lon", FloatMenuGlyph(0xE55F, 30, pxText),
                () => { panelFloatMenu.Visible = false; flagByLatLonToolStripMenuItem_Click(this, EventArgs.Empty); });
            FloatMenuAddAction("Borrar aplicado", FloatMenuScaleIcon(Properties.Resources.Trash, 30),
                () => { panelFloatMenu.Visible = false; toolStripAreYouSure_Click(this, EventArgs.Empty); });
            FloatMenuAddButton("Color mapeo", btnChangeMappingColor);
            FloatMenuAddButton("Rumbo herr.", btnResetToolHeading);
            FloatMenuAddAction("Tram crear", FloatMenuScaleIcon(Properties.Resources.TramAll, 30),
                () => { panelFloatMenu.Visible = false; tramLinesMenuField_Click(this, EventArgs.Empty); });
            FloatMenuAddButton("Tram vista", btnTramDisplayMode);
        }

        //Réplica del btnJobOpen_Click de FormJob: guarda el lote activo si lo
        //hay y abre el selector de lotes (FormFilePicker, con lista y distancia)
        //para cargar el elegido. Directo desde el menú flotante de Lote.
        private async void FloatMenuAbrirLote()
        {
            if (isJobStarted)
                await FileSaveEverythingBeforeClosingField();

            filePickerFileAndDirectory = "";
            using (var form = new FormFilePicker(this))
            {
                if (form.ShowDialog(this) == DialogResult.Yes)
                    FileOpenField(filePickerFileAndDirectory);
            }
        }

        //guías: crear (tirar A, A/B, A/B curvo viven en "Crear guías"),
        //elegir, centrar, mover y modo curva/contorno
        private void FloatMenuFillGuias()
        {
            //reabre el widget "Elegir guía" si el operario lo cerró
            FloatMenuAddAction("Elegir guía", FloatMenuGlyph(0xE8F0, 30, pxText), () =>
            {
                panelFloatMenu.Visible = false;
                OpenGuiaRapidaWidget();
            });
            //botonera HTML con carpetas personalizables (drag & drop)
            FloatMenuAddAction("Botonera", FloatMenuGlyph(0xE5C3, 30, pxText), () =>
            {
                panelFloatMenu.Visible = false;
                OpenAgroParallelWidget("pages/botonera.html", "Botonera", 940, 640);
            });
            FloatMenuAddButton("Crear guías", btnBuildTracks);   //tirar A · A/B · A/B curvo
            FloatMenuAddButton("Dibujar AB", btnABDraw);
            FloatMenuAddButton("A+", btnPlusAB);
            FloatMenuAddButton("Elegir guía", btnTrack);
            FloatMenuAddButton("Centrar guía", btnSnapToPivot);
            FloatMenuAddButton("Mover ‹", btnAdjLeft);
            FloatMenuAddButton("Mover ›", btnAdjRight);
            FloatMenuAddButton("Curva/Contorno", btnContour);
            FloatMenuAddButton("Bloq. contorno", btnContourLock);
            FloatMenuAddButton("Guía ant.", btnCycleLinesBk);
            FloatMenuAddButton("Guía sig.", btnCycleLines);
            FloatMenuAddButton("Ocultar guías", btnTracksOff);
            FloatMenuAddButton("Nudge", btnNudge);
            FloatMenuAddButton("Nudge ref.", btnRefNudge);
            FloatMenuAddAction("Suavizar AB", FloatMenuScaleIcon(Properties.Resources.ABSmooth, 30),
                () => { panelFloatMenu.Visible = false; SmoothABtoolStripMenu_Click(this, EventArgs.Empty); });
            FloatMenuAddAction("Importar guías", FloatMenuGlyph(0xE255, 30, pxText),
                () => { panelFloatMenu.Visible = false; copyTracksToolStripMenuItem_Click(this, EventArgs.Empty); });
            FloatMenuAddAction("Borrar contornos", FloatMenuScaleIcon(Properties.Resources.Trash, 30),
                () => { panelFloatMenu.Visible = false; deleteContourPathsToolStripMenuItem_Click(this, EventArgs.Empty); });
        }

        //lindero y cabecera: crear/editar límites del lote y cabeceras
        private void FloatMenuFillLindero()
        {
            FloatMenuAddAction("Lindero", FloatMenuScaleIcon(Properties.Resources.Boundary, 30),
                () => { panelFloatMenu.Visible = false; boundariesToolStripMenuItem_Click(this, EventArgs.Empty); });
            FloatMenuAddAction("Herram. límites", FloatMenuScaleIcon(Properties.Resources.BoundaryRecordTool, 30),
                () => { panelFloatMenu.Visible = false; boundaryToolToolStripMenu_Click(this, EventArgs.Empty); });
            FloatMenuAddAction("Cabecera", FloatMenuScaleIcon(Properties.Resources.HeadlandOn, 30),
                () => { panelFloatMenu.Visible = false; headlandToolStripMenuItem_Click(this, EventArgs.Empty); });
            FloatMenuAddAction("Cabecera avanzada", FloatMenuScaleIcon(Properties.Resources.Headache, 30),
                () => { panelFloatMenu.Visible = false; headlandBuildToolStripMenuItem_Click(this, EventArgs.Empty); });
            FloatMenuAddButton("Cabecera SÍ/NO", btnHeadlandOnOff);
        }

        private void FloatMenuFillRutaGrabada()
        {
            FloatMenuAddButton("Ir / Parar", btnPathGoStop);
            FloatMenuAddButton("Grabar", btnPathRecordStop);
            FloatMenuAddButton("Elegir ruta", btnPickPath);
            FloatMenuAddButton("Reanudar", btnResumePath);
            FloatMenuAddButton("Invertir AB", btnSwapABRecordedPath);
        }

        private void FloatMenuFillVista()
        {
            FloatMenuAddButton("2D", btn2D);
            FloatMenuAddButton("3D", btn3D);
            FloatMenuAddButton("Norte 2D", btnN2D);
            FloatMenuAddButton("Grilla", btnGrid);
            FloatMenuAddButton("Día/Noche", btnDayNightMode);
            FloatMenuAddButton("Brillo +", btnBrightnessUp);
            FloatMenuAddButton("Brillo −", btnBrightnessDn);
            FloatMenuAddButton("Inclinar +", btnTiltUp);
            FloatMenuAddButton("Inclinar −", btnTiltDn);
            //panel de ajustes de cámara/navegación (venía de "General")
            FloatMenuAddButton("Navegación", btnNavigationSettings);
        }

        private void FloatMenuFillConfig()
        {
            //subgrupos que saltean el menú lateral interno de FormConfig:
            //cada ítem abre FormConfig directo en su TabPage
            FloatMenuAddCategory("Vehículo", FloatMenuScaleIcon(Properties.Resources.vehiclePageTractor, 38),
                FloatMenuFillConfigVehiculo, FloatMenuBackToConfig);
            FloatMenuAddCategory("Implemento", FloatMenuGlyph(0xE869, 38, pxGreen),
                FloatMenuFillConfigImplemento, FloatMenuBackToConfig);
            FloatMenuAddCategory("Fuentes datos", FloatMenuGlyph(0xE322, 38, pxGreen),
                FloatMenuFillConfigFuentes, FloatMenuBackToConfig);

            //pantallas sueltas de FormConfig
            FloatMenuAddAction("Resumen", FloatMenuGlyph(0xF071, 30, pxText),
                () => FloatMenuOpenConfig("tabSummary"));
            FloatMenuAddAction("U-Turn", FloatMenuGlyph(0xEBA1, 30, pxText),
                () => FloatMenuOpenConfig("tabUTurn"));
            FloatMenuAddAction("Display", FloatMenuGlyph(0xEF5B, 30, pxText),
                () => FloatMenuOpenConfig("tabDisplay"));
            FloatMenuAddAction("Botones", FloatMenuGlyph(0xF1C1, 30, pxText),
                () => FloatMenuOpenConfig("tabBtns"));
            FloatMenuAddAction("Tram", FloatMenuGlyph(0xE3EC, 30, pxText),
                () => FloatMenuOpenConfig("tabTram"));

            //otras pantallas de configuración fuera de FormConfig
            FloatMenuAddButton("Dirección", btnAutoSteerConfig);
            FloatMenuAddAction("Todos los ajustes", FloatMenuGlyph(0xE429, 30, pxText),
                () => allSettingsMenuItem_Click(this, EventArgs.Empty));
            FloatMenuAddAction("Colores", Properties.Resources.ColourPick,
                () => colorsToolStripMenuItem_Click(this, EventArgs.Empty));
            FloatMenuAddAction("Colores secciones", Properties.Resources.SectionMapping,
                () => colorsSectionToolStripMenuItem_Click(this, EventArgs.Empty));
            //perfiles de máquina (del menú "All" viejo: Profile → New/Load)
            FloatMenuAddAction("Perfil nuevo", FloatMenuGlyph(0xE7FE, 30, pxText),
                () => { panelFloatMenu.Visible = false; newProfileToolStripMenuItem_Click(this, EventArgs.Empty); });
            FloatMenuAddAction("Cargar perfil", FloatMenuGlyph(0xE2C8, 30, pxText),
                () => { panelFloatMenu.Visible = false; loadProfileToolStripMenuItem_Click(this, EventArgs.Empty); });
            FloatMenuAddAction("Directorios", FloatMenuGlyph(0xE2C7, 30, pxText),
                () => { panelFloatMenu.Visible = false; setWorkingDirectoryToolStripMenuItem_Click(this, EventArgs.Empty); });
        }

        //vuelve al nivel Configuración desde un subgrupo
        private void FloatMenuBackToConfig()
        {
            FloatMenuShowCategory("Configuración", FloatMenuFillConfig);
        }

        //abre la Configuración HTML (pages/config.html) directo en la solapa
        //pedida. Reemplaza a FormConfig; la WinForm nativa queda como fallback
        //(FloatMenuOpenConfigNativa) por si el WebView2 falla.
        private void FloatMenuOpenConfig(string tabName)
        {
            //null si llega desde la barra HTML sin haber abierto nunca el menú flotante
            if (panelFloatMenu != null) panelFloatMenu.Visible = false;

            string tab = ConfigTabToHtml(tabName);

            //si ya hay un widget de config abierto (quizás en otra solapa),
            //lo cerramos: el dict _floatWidgets se indexa por page+query y si
            //no, se acumularían ventanas de config una por pestaña.
            if (_floatWidgets != null)
            {
                var abiertos = new System.Collections.Generic.List<string>();
                foreach (var k in _floatWidgets.Keys)
                    if (k.StartsWith("pages/config.html", StringComparison.OrdinalIgnoreCase))
                        abiertos.Add(k);
                foreach (var k in abiertos)
                {
                    Form f;
                    if (_floatWidgets.TryGetValue(k, out f) && f != null && !f.IsDisposed)
                    {
                        try { f.Close(); } catch { }
                    }
                }
            }

            OpenAgroParallelWidget("pages/config.html?tab=" + tab, "Configuración", 1180, 700);
        }

        //mapa TabPage WinForm (FormConfig) → id de solapa en config.html
        private static string ConfigTabToHtml(string tabName)
        {
            switch (tabName)
            {
                case "tabVConfig": return "vconfig";
                case "tabVDimensions": return "vdimensions";
                case "tabVAntenna": return "vantenna";
                case "tabVGuidance": return "vconfig";     //vacía en el original
                case "tabTConfig": return "tconfig";
                case "tabTHitch": return "thitch";
                case "tabToolOffset": return "tooloffset";
                case "tabToolPivot": return "toolpivot";
                case "tabTSettings": return "tsettings";
                case "tabTSections": return "tsections";
                case "tabTSwitches": return "tswitches";
                case "tabRelay": return "relay";
                case "tabAMachine": return "amachine";
                case "tabDHeading": return "heading";
                case "tabDRoll": return "roll";
                case "tabUTurn": return "uturn";
                case "tabTram": return "tram";
                case "tabDisplay": return "display";
                case "tabBtns": return "botones";
                default: return "summary";
            }
        }

        //fallback: FormConfig WinForm nativa (no referenciada desde el menú,
        //se conserva para diagnóstico/emergencia)
        private void FloatMenuOpenConfigNativa(string tabName)
        {
            if (panelFloatMenu != null) panelFloatMenu.Visible = false; //cerrar antes del diálogo modal
            using (var form = new FormConfig(this) { InitialTabName = tabName })
            {
                form.ShowDialog(this);
            }
        }

        private void FloatMenuFillConfigVehiculo()
        {
            FloatMenuAddAction("Tipo", FloatMenuScaleIcon(Properties.Resources.vehiclePageTractor, 30),
                () => FloatMenuOpenConfig("tabVConfig"));
            FloatMenuAddAction("Dimensiones", FloatMenuGlyph(0xE41C, 30, pxText),
                () => FloatMenuOpenConfig("tabVDimensions"));
            FloatMenuAddAction("Antena", FloatMenuGlyph(0xE8BF, 30, pxText),
                () => FloatMenuOpenConfig("tabVAntenna"));
            FloatMenuAddAction("Guiado", FloatMenuGlyph(0xE55D, 30, pxText),
                () => FloatMenuOpenConfig("tabVGuidance"));
        }

        private void FloatMenuFillConfigImplemento()
        {
            FloatMenuAddAction("Tipo", FloatMenuGlyph(0xE869, 30, pxText),
                () => FloatMenuOpenConfig("tabTConfig"));
            FloatMenuAddAction("Enganche", FloatMenuGlyph(0xE157, 30, pxText),
                () => FloatMenuOpenConfig("tabTHitch"));
            FloatMenuAddAction("Offset", FloatMenuGlyph(0xE8D4, 30, pxText),
                () => FloatMenuOpenConfig("tabToolOffset"));
            FloatMenuAddAction("Pivot", FloatMenuGlyph(0xE55C, 30, pxText),
                () => FloatMenuOpenConfig("tabToolPivot"));
            FloatMenuAddAction("Secciones", FloatMenuGlyph(0xE3EC, 30, pxText),
                () => FloatMenuOpenConfig("tabTSections"));
            FloatMenuAddAction("Switches", FloatMenuGlyph(0xE9F6, 30, pxText),
                () => FloatMenuOpenConfig("tabTSwitches"));
            FloatMenuAddAction("Ajustes", FloatMenuGlyph(0xE429, 30, pxText),
                () => FloatMenuOpenConfig("tabTSettings"));
        }

        private void FloatMenuFillConfigFuentes()
        {
            FloatMenuAddAction("Rumbo", FloatMenuGlyph(0xE87A, 30, pxText),
                () => FloatMenuOpenConfig("tabDHeading"));
            FloatMenuAddAction("Roll", FloatMenuGlyph(0xE1C1, 30, pxText),
                () => FloatMenuOpenConfig("tabDRoll"));
            FloatMenuAddAction("Módulo máquina", FloatMenuGlyph(0xE322, 30, pxText),
                () => FloatMenuOpenConfig("tabAMachine"));
            FloatMenuAddAction("Relés", FloatMenuGlyph(0xF102, 30, pxText),
                () => FloatMenuOpenConfig("tabRelay"));
        }

        //diagnóstico: datos crudos, gráficos y asistentes (ex "Herramientas";
        //lo de guías/límites se mudó a sus categorías)
        private void FloatMenuFillDiagnostico()
        {
            FloatMenuAddButton("Datos GPS", btnGPSData);
            FloatMenuAddAction("Asistente dirección", Properties.Resources.WizardWand,
                () => steerWizardMenuItem_Click(this, EventArgs.Empty));
            FloatMenuAddAction("Gráfico dirección", FloatMenuGlyph(0xE6E1, 30, pxText),
                () => toolStripAutoSteerChart_Click(this, EventArgs.Empty));
            FloatMenuAddAction("Gráfico rumbo", FloatMenuGlyph(0xE6E1, 30, pxText),
                () => headingChartToolStripMenuItem_Click(this, EventArgs.Empty));
            FloatMenuAddAction("Gráfico XTE", FloatMenuGlyph(0xEB66, 30, pxText),
                () => xTEChartToolStripMenuItem_Click(this, EventArgs.Empty));
            FloatMenuAddAction("Chequeo roll", FloatMenuGlyph(0xEB66, 30, pxText),
                () => correctionToolStrip_Click(this, EventArgs.Empty));
            FloatMenuAddAction("Corregir posición", FloatMenuGlyph(0xE55C, 30, pxText),
                () => offsetFixToolStrip_Click(this, EventArgs.Empty));
            FloatMenuAddAction("Visor eventos", FloatMenuGlyph(0xEF6E, 30, pxText),
                () => eventViewerToolStripMenuItem_Click(this, EventArgs.Empty));
            FloatMenuAddAction("Webcam", FloatMenuGlyph(0xE04B, 30, pxText),
                () => webcamToolStrip_Click(this, EventArgs.Empty));
        }

        private void FloatMenuFillAgroParallel()
        {
            FloatMenuAddAction("Hub", FloatMenuGlyph(0xE9F4, 30, pxText),
                () => toolStripAgroParallel_Click(this, EventArgs.Empty));
            //CoreX: abre su dashboard web (lanza CoreX.exe si hace falta)
            FloatMenuAddButton("CoreX", btnStartAgIO);
            //copia HTML flotante del menú izquierdo (el nativo sigue igual)
            FloatMenuAddAction("Menú izq. HTML", FloatMenuGlyph(0xE5D2, 30, pxText), () =>
            {
                panelFloatMenu.Visible = false;
                OpenAgroParallelWidget("pages/menu-izquierda.html", "Menú izquierdo", 110, 560);
            });
            //copia HTML flotante de la barra superior (panelControlBox nativo sigue igual)
            FloatMenuAddAction("Barra superior HTML", FloatMenuGlyph(0xE5D2, 30, pxText), () =>
            {
                panelFloatMenu.Visible = false;
                OpenAgroParallelWidget("pages/barra-superior.html", "Barra superior", 560, 64);
            });
            //copia HTML flotante de la barra derecha (panelRight nativo sigue igual)
            FloatMenuAddAction("Barra derecha HTML", FloatMenuGlyph(0xE5D2, 30, pxText), () =>
            {
                panelFloatMenu.Visible = false;
                OpenAgroParallelWidget("pages/barra-derecha.html", "Barra derecha", 96, 620);
            });
            //copia HTML flotante de la barra de abajo (panelBottom nativo sigue igual)
            FloatMenuAddAction("Barra abajo HTML", FloatMenuGlyph(0xE5D2, 30, pxText), () =>
            {
                panelFloatMenu.Visible = false;
                OpenAgroParallelWidget("pages/barra-abajo.html", "Barra abajo", 920, 96);
            });
            //modo barras HTML: las 3 barras espejo reemplazan a las nativas
            //(dockeadas a los bordes del mapa; la flecha las oculta/muestra)
            FloatMenuAddAction(isHtmlBarsMode ? "Barras nativas" : "Barras HTML",
                FloatMenuGlyph(0xE8D3, 30, pxText), () =>
            {
                panelFloatMenu.Visible = false;
                ToggleBarrasHtml();
            });
            FloatMenuAddAction("Cámaras", FloatMenuGlyph(0xE412, 30, pxText),
                () => toolStripCamaras_Click(this, EventArgs.Empty));
            FloatMenuAddAction("VistaX · Semilla", FloatMenuGlyph(0xE8F4, 30, pxText),
                () => toolStripVistaXSemilla_Click(this, EventArgs.Empty));
            FloatMenuAddAction("VistaX · Máquina", FloatMenuGlyph(0xF049, 30, pxText),
                () => toolStripVistaXMaquina_Click(this, EventArgs.Empty));
            FloatMenuAddAction("VistaX · Densidad", FloatMenuGlyph(0xE26B, 30, pxText),
                () => toolStripVistaXDensidad_Click(this, EventArgs.Empty));
        }

        private void FloatMenuFillSimulador()
        {
            //toggle maestro del simulador (del menú "All" viejo). El item tiene
            //CheckOnClick=true y su handler LEE Checked, así que hay que
            //dispararlo con PerformClick (setear Checked a mano no ejecuta nada).
            FloatMenuAddAction("Simulador: " + (simulatorOnToolStripMenuItem.Checked ? "SÍ" : "NO"),
                FloatMenuGlyph(0xF06C, 30, pxText), () =>
                {
                    simulatorOnToolStripMenuItem.PerformClick();
                    FloatMenuShowCategory("Simulador", FloatMenuFillSimulador, floatMenuBackAction);
                });
            FloatMenuAddAction("Coordenadas sim", FloatMenuGlyph(0xE55C, 30, pxText),
                () => { panelFloatMenu.Visible = false; enterSimCoordsToolStripMenuItem_Click(this, EventArgs.Empty); });
            FloatMenuAddButton("Reset sim", btnResetSim);
            FloatMenuAddButton("Reversa", btnSimReverseDirection);
            FloatMenuAddButton("Velocidad 0", btnSimSetSpeedToZero);
            FloatMenuAddButton("Ángulo 0", btnResetSteerAngle);
        }

        private void FloatMenuFillSistema()
        {
            FloatMenuAddButton("Minimizar", btnMinimizeMainForm);
            FloatMenuAddButton("Maximizar", btnMaximizeMainForm);
            FloatMenuAddAction("Modo kiosko", FloatMenuGlyph(0xE1C4, 30, pxText),
                () => { panelFloatMenu.Visible = false; kioskModeToolStrip_Click(this, EventArgs.Empty); });
            FloatMenuAddAction("Ayuda", FloatMenuGlyph(0xE887, 30, pxText),
                () => { panelFloatMenu.Visible = false; helpMenuItem_Click(this, EventArgs.Empty); });
            //restablece TODA la config a fábrica — el handler original ya pide
            //confirmación y reinicia, no hace falta guard extra acá
            FloatMenuAddAction("Reset de fábrica", FloatMenuGlyph(0xE8BA, 30, pxText),
                () => { panelFloatMenu.Visible = false; resetALLToolStripMenuItem_Click(this, EventArgs.Empty); });
            FloatMenuAddButton("Apagar", btnShutdown);
        }

        //--- constructores de ítems ---

        //fuente Google Material Icons cargada desde el TTF que acompaña al exe
        private static System.Drawing.Text.PrivateFontCollection floatMenuFonts;
        private static FontFamily floatMenuIconFamily;

        private static FontFamily FloatMenuIconFamily()
        {
            if (floatMenuIconFamily != null) return floatMenuIconFamily;
            try
            {
                string ttf = System.IO.Path.Combine(Application.StartupPath, "Fonts", "MaterialIcons-Regular.ttf");
                if (!System.IO.File.Exists(ttf))
                    ttf = System.IO.Path.Combine(Application.StartupPath, "MaterialIcons-Regular.ttf");
                if (System.IO.File.Exists(ttf))
                {
                    floatMenuFonts = new System.Drawing.Text.PrivateFontCollection();
                    floatMenuFonts.AddFontFile(ttf);
                    floatMenuIconFamily = floatMenuFonts.Families[0];
                }
            }
            catch { /* sin la fuente se cae al fallback MDL2 */ }
            return floatMenuIconFamily;
        }

        //renderiza un glifo de Google Material Icons (TTF junto al exe) como
        //bitmap nítido del tamaño pedido; fallback: Segoe MDL2 Assets
        private static Image FloatMenuGlyph(int code, int size, Color color)
        {
            var fam = FloatMenuIconFamily();
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            using (var f = fam != null
                ? new Font(fam, size * 0.92f, FontStyle.Regular, GraphicsUnit.Pixel)
                : new Font("Segoe MDL2 Assets", size * 0.68f, GraphicsUnit.Pixel))
            using (var br = new SolidBrush(color))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                string s = ((char)code).ToString();
                var sz = g.MeasureString(s, f);
                g.DrawString(s, f, br, (size - sz.Width) / 2f, (size - sz.Height) / 2f);
            }
            return bmp;
        }

        //escala el ícono a un tamaño uniforme (los recursos vienen de tamaños varios)
        private static Image FloatMenuScaleIcon(Image icon, int size)
        {
            if (icon == null) return null;
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(icon, 0, 0, size, size);
            }
            return bmp;
        }

        //tarjeta de categoría en la vista inicial
        private void FloatMenuAddCategory(string title, Image icon, Action fill, Action back = null)
        {
            var b = new Button
            {
                Text = title,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Size = new Size(112, 86),
                FlatStyle = FlatStyle.Flat,
                BackColor = pxBg,
                ForeColor = pxText,
                Image = FloatMenuScaleIcon(icon, 38),
                TextImageRelation = TextImageRelation.ImageAboveText,
                TextAlign = ContentAlignment.BottomCenter,
                ImageAlign = ContentAlignment.TopCenter,
                TabStop = false,
                Margin = new Padding(4),
                Padding = new Padding(0, 6, 0, 6)
            };
            b.FlatAppearance.BorderColor = pxGreen;
            b.Click += (s, e) => FloatMenuShowCategory(title, fill, back);
            flowFloatMenu.Controls.Add(b);
        }

        //ítem que reutiliza el ícono del botón original y dispara su Click
        private void FloatMenuAddButton(string text, Button source)
        {
            FloatMenuAddAction(text, source?.Image, () =>
            {
                if (source != null) InvokeOnClick(source, EventArgs.Empty);
            });
        }

        //Comandos del piloto desde la UI web (pages/guia-rapida.html y
        //pages/botonera.html vía POST /api/aog/guidance/command). Dispara el
        //Click del botón/handler nativo en el hilo de UI. Los strings son
        //contrato con wwwroot/js/pilot-commands.js (catálogo de labels/grupos).
        public bool ExecuteGuidanceCommand(string cmd)
        {
            Button b = null;
            Action act = null;
            string cmdLower = (cmd ?? "").Trim().ToLowerInvariant();
            //paneles_keepalive: las barras HTML lo mandan al detectar
            //interacción (hover/touch/click) para que el auto-ocultado no
            //las esconda mientras se están usando.
            if (cmdLower == "paneles_keepalive")
            {
                try
                {
                    if (InvokeRequired) BeginInvoke((MethodInvoker)ReiniciarTimerOcultarPaneles);
                    else ReiniciarTimerOcultarPaneles();
                    return true;
                }
                catch { return false; }
            }
            //skips_{n}: ancho de salteo de U-turn (cboxpRowWidth 1..10). Setear
            //SelectedIndex dispara SelectedIndexChanged solo (ComboBox nativo).
            if (cmdLower.StartsWith("skips_"))
            {
                int nSkips;
                if (int.TryParse(cmdLower.Substring(6), out nSkips) && nSkips >= 1 && nSkips <= 10)
                {
                    act = () => cboxpRowWidth.SelectedIndex = nSkips - 1;
                    try
                    {
                        if (InvokeRequired) BeginInvoke((MethodInvoker)(() => act()));
                        else act();
                        return true;
                    }
                    catch { return false; }
                }
                return false;
            }
            //idioma_{code}: cambio de idioma desde el submenú HTML de la
            //hamburguesa. Usa el MISMO SetLanguage que el menú nativo. El code
            //se toma del cmd original (no lowercased) porque los tags nativos
            //distinguen mayúsculas (ej. "zh-CHS").
            if (cmdLower.StartsWith("idioma_"))
            {
                string lang = (cmd ?? "").Trim().Substring(7);
                //validar ANTES de SetLanguage: ese método guarda el código en
                //el registro ANTES de crear la CultureInfo, así que un código
                //inválido dejaba a PilotX crasheando en el arranque.
                try { new System.Globalization.CultureInfo(lang); }
                catch { return false; }
                Action setLang = () => { try { SetLanguage(lang); } catch { } };
                try
                {
                    if (InvokeRequired) BeginInvoke((MethodInvoker)(() => setLang()));
                    else setLang();
                    return true;
                }
                catch { return false; }
            }
            //shift_north_{cm} / shift_east_{cm}: corrimiento de deriva GPS (drift
            //compensation) en cm, absoluto, clamp ±9999. Reemplaza los
            //NumericUpDown de FormShiftPos (aplica en vivo, igual que el modal).
            if (cmdLower.StartsWith("shift_north_") || cmdLower.StartsWith("shift_east_"))
            {
                bool isNorth = cmdLower.StartsWith("shift_north_");
                string num = cmdLower.Substring(isNorth ? 12 : 11);
                int cm;
                if (!int.TryParse(num, System.Globalization.NumberStyles.AllowLeadingSign,
                                  System.Globalization.CultureInfo.InvariantCulture, out cm))
                    return false;
                if (cm > 9999) cm = 9999;
                if (cm < -9999) cm = -9999;
                int cmFinal = cm;
                Action setShift = () =>
                {
                    // GeoDelta es struct (value type): nunca null.
                    var cur = AppModel.SharedFieldProperties.DriftCompensation;
                    double north = cur.NorthingDelta;
                    double east = cur.EastingDelta;
                    if (isNorth) north = cmFinal / 100.0; else east = cmFinal / 100.0;
                    AppModel.SharedFieldProperties.DriftCompensation =
                        new AgOpenGPS.Core.Models.GeoDelta(north, east);
                };
                try
                {
                    if (InvokeRequired) BeginInvoke((MethodInvoker)(() => setShift()));
                    else setShift();
                    return true;
                }
                catch { return false; }
            }
            //sim_coords_{lat}_{lon}: reubica el simulador a esa coordenada (teletransporte).
            //Reemplaza el OK de FormSimCoords. Sólo con simulador encendido y sin lote
            //abierto (mismos guards). lat/lon en grados decimales, cultura invariante.
            if (cmdLower.StartsWith("sim_coords_"))
            {
                string rest = cmdLower.Substring("sim_coords_".Length);
                string[] parts = rest.Split('_');
                if (parts.Length != 2) return false;
                double lat, lon;
                var styles = System.Globalization.NumberStyles.Float |
                             System.Globalization.NumberStyles.AllowLeadingSign;
                if (!double.TryParse(parts[0], styles,
                                     System.Globalization.CultureInfo.InvariantCulture, out lat) ||
                    !double.TryParse(parts[1], styles,
                                     System.Globalization.CultureInfo.InvariantCulture, out lon))
                    return false;
                if (lat < -90.0 || lat > 90.0 || lon < -180.0 || lon > 180.0) return false;
                // Guards: no reubicar con lote abierto ni con el simulador apagado.
                if (isJobStarted) return false;
                if (timerSim == null || !timerSim.Enabled) return false;
                double latF = lat, lonF = lon;
                Action apply = () =>
                    pn.DefineLocalPlane(new AgOpenGPS.Core.Models.Wgs84(latF, lonF), true);
                try
                {
                    if (InvokeRequired) BeginInvoke((MethodInvoker)(() => apply()));
                    else apply();
                    return true;
                }
                catch { return false; }
            }
            //sec_colors_{hex1}_..._{hex16}_{0|1}: guarda los 16 colores de sección
            //(hex RRGGBB, sin '#') + el flag multicolor (último token). Reemplaza el
            //OK de FormColorSection: escribe Settings.setColor_secNN + tool.secColors +
            //setColor_isMultiColorSections y persiste con Save().
            if (cmdLower.StartsWith("sec_colors_"))
            {
                string rest = cmdLower.Substring("sec_colors_".Length);
                string[] parts = rest.Split('_');
                if (parts.Length != 17) return false;
                var cols = new System.Drawing.Color[16];
                for (int i = 0; i < 16; i++)
                {
                    if (parts[i].Length != 6) return false;
                    int rgb;
                    if (!int.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber,
                                      System.Globalization.CultureInfo.InvariantCulture, out rgb))
                        return false;
                    cols[i] = System.Drawing.Color.FromArgb(255,
                        (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
                }
                bool multi = parts[16] == "1";
                Action apply = () =>
                {
                    var st = Properties.Settings.Default;
                    tool.secColors[0] = st.setColor_sec01 = cols[0];
                    tool.secColors[1] = st.setColor_sec02 = cols[1];
                    tool.secColors[2] = st.setColor_sec03 = cols[2];
                    tool.secColors[3] = st.setColor_sec04 = cols[3];
                    tool.secColors[4] = st.setColor_sec05 = cols[4];
                    tool.secColors[5] = st.setColor_sec06 = cols[5];
                    tool.secColors[6] = st.setColor_sec07 = cols[6];
                    tool.secColors[7] = st.setColor_sec08 = cols[7];
                    tool.secColors[8] = st.setColor_sec09 = cols[8];
                    tool.secColors[9] = st.setColor_sec10 = cols[9];
                    tool.secColors[10] = st.setColor_sec11 = cols[10];
                    tool.secColors[11] = st.setColor_sec12 = cols[11];
                    tool.secColors[12] = st.setColor_sec13 = cols[12];
                    tool.secColors[13] = st.setColor_sec14 = cols[13];
                    tool.secColors[14] = st.setColor_sec15 = cols[14];
                    tool.secColors[15] = st.setColor_sec16 = cols[15];
                    st.setColor_isMultiColorSections = tool.isMultiColoredSections = multi;
                    st.Save();
                };
                try
                {
                    if (InvokeRequired) BeginInvoke((MethodInvoker)(() => apply()));
                    else apply();
                    return true;
                }
                catch { return false; }
            }
            //display_colors_{frameDay}_{frameNight}_{fieldDay}_{fieldNight}_{textDay}
            //_{textNight}_{camSmooth}_{0|1}: colores de display (marco/campo/texto para
            //día y noche, hex RRGGBB sin '#') + suavizado de cámara (0..100) + modo día
            //(último token). Reemplaza el OK de FormColor: escribe los mf.*Color +
            //Settings.setDisplay_color* + setDisplay_camSmooth + camSmoothFactor, aplica
            //el modo día/noche con SwapDayNightMode() y persiste con Save().
            if (cmdLower.StartsWith("display_colors_"))
            {
                string rest = cmdLower.Substring("display_colors_".Length);
                string[] parts = rest.Split('_');
                if (parts.Length != 8) return false;
                var cols = new System.Drawing.Color[6];
                for (int i = 0; i < 6; i++)
                {
                    if (parts[i].Length != 6) return false;
                    int rgb;
                    if (!int.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber,
                                      System.Globalization.CultureInfo.InvariantCulture, out rgb))
                        return false;
                    cols[i] = System.Drawing.Color.FromArgb(255,
                        (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
                }
                int camSmooth;
                if (!int.TryParse(parts[6], System.Globalization.NumberStyles.Integer,
                                  System.Globalization.CultureInfo.InvariantCulture, out camSmooth))
                    return false;
                if (camSmooth < 0) camSmooth = 0;
                if (camSmooth > 100) camSmooth = 100;
                bool wantDay = parts[7] == "1";
                Action apply = () =>
                {
                    var st = Properties.Settings.Default;
                    frameDayColor = st.setDisplay_colorDayFrame = cols[0];
                    frameNightColor = st.setDisplay_colorNightFrame = cols[1];
                    fieldColorDay = st.setDisplay_colorFieldDay = cols[2];
                    fieldColorNight = st.setDisplay_colorFieldNight = cols[3];
                    textColorDay = st.setDisplay_colorTextDay = cols[4];
                    textColorNight = st.setDisplay_colorTextNight = cols[5];
                    st.setDisplay_camSmooth = camSmooth;
                    camSmoothFactor = ((double)camSmooth * 0.004) + 0.15;
                    // Aplicar el modo pedido y reflejar los colores en los controles
                    // vivos (igual que FormColor: swap para pasar de modo, o doble
                    // swap para re-aplicar el modo actual).
                    if (isDay != wantDay) SwapDayNightMode();
                    else { SwapDayNightMode(); SwapDayNightMode(); }
                    st.Save();
                };
                try
                {
                    if (InvokeRequired) BeginInvoke((MethodInvoker)(() => apply()));
                    else apply();
                    return true;
                }
                catch { return false; }
            }
            //smooth_ab_{open|set_<n>|apply|save|cancel}: suavizado de la curva AB
            //(reemplazo de FormSmoothAB). El widget dibuja sobre el mapa principal:
            //isSmoothWindowOpen=true muestra la vista previa (curve.smooList), y cada
            //set_<n> recalcula con curve.SmoothAB(n*2). Requiere lote abierto y una
            //curva/track seleccionado (mismos guards que el menú nativo). apply guarda
            //en memoria; save además persiste a archivo; cancel descarta.
            if (cmdLower.StartsWith("smooth_ab_"))
            {
                if (!isJobStarted || trk.idx < 0) return false;
                string sub = cmdLower.Substring("smooth_ab_".Length);
                Action apply = null;
                if (sub == "open")
                {
                    apply = () => curve.isSmoothWindowOpen = true;
                }
                else if (sub.StartsWith("set_"))
                {
                    int count;
                    if (!int.TryParse(sub.Substring(4), System.Globalization.NumberStyles.Integer,
                                      System.Globalization.CultureInfo.InvariantCulture, out count))
                        return false;
                    if (count < 2) count = 2;
                    if (count > 100) count = 100;
                    int c = count;
                    apply = () => { curve.isSmoothWindowOpen = true; curve.SmoothAB(c * 2); };
                }
                else if (sub == "apply")
                {
                    apply = () => { curve.isSmoothWindowOpen = false; curve.SaveSmoothList(); curve.smooList?.Clear(); };
                }
                else if (sub == "save")
                {
                    apply = () => { curve.isSmoothWindowOpen = false; curve.SaveSmoothList(); curve.smooList?.Clear(); FileSaveTracks(); };
                }
                else if (sub == "cancel")
                {
                    apply = () => { curve.isSmoothWindowOpen = false; curve.smooList?.Clear(); };
                }
                else return false;
                try
                {
                    if (InvokeRequired) BeginInvoke((MethodInvoker)(() => apply()));
                    else apply();
                    return true;
                }
                catch { return false; }
            }
            switch (cmdLower)
            {
                //--- guías ---
                case "center": b = btnSnapToPivot; break;      //centrar guía
                case "nudge_left": b = btnAdjLeft; break;      //mover guía ‹
                case "nudge_right": b = btnAdjRight; break;    //mover guía ›
                case "contour": b = btnContour; break;         //activar curva/contorno
                case "contour_lock": b = btnContourLock; break;
                case "build": b = btnBuildTracks; break;       //crear guías (A, A/B, curvo)
                case "pick": b = btnTrack; break;              //elegir guía
                case "ab_draw": b = btnABDraw; break;
                case "a_plus": b = btnPlusAB; break;
                case "track_prev": b = btnCycleLinesBk; break;
                case "track_next": b = btnCycleLines; break;
                case "tracks_off": b = btnTracksOff; break;
                case "nudge": b = btnNudge; break;
                case "nudge_ref": b = btnRefNudge; break;
                //crear guía directo en el tipo elegido (widget "Elegir guía"):
                //abre FormBuildTracks y navega hasta el flujo con los handlers reales
                case "track_new_a": act = () => OpenBuildTracksPanel("btnzAPlus"); break;
                case "track_new_ab": act = () => OpenBuildTracksPanel("btnzABLine"); break;
                case "track_new_curve": act = () => OpenBuildTracksPanel("btnzABCurve"); break;
                // cboxAutoSnapToPivot es un CheckBox (no Button, no tiene
                // PerformClick): togglear Checked no dispara Click solo, así
                // que replicamos el click real a mano — igual que haría el
                // operario tocando el checkbox nativo.
                case "auto_snap_to_pivot":
                    act = () =>
                    {
                        cboxAutoSnapToPivot.Checked = !cboxAutoSnapToPivot.Checked;
                        cboxAutoSnapToPivot_Click(this, EventArgs.Empty);
                    };
                    break;
                //--- operación ---
                case "autosteer": b = btnAutoSteer; break;
                case "autotrack": b = btnAutoTrack; break;
                case "uturn": b = btnAutoYouTurn; break;
                case "uturn_skips": b = btnYouSkipEnable; break;
                case "sec_auto": b = btnSectionMasterAuto; break;
                case "sec_manual": b = btnSectionMasterManual; break;
                case "isobus": b = btnIsobusSectionControl; break;
                case "hidraulico": b = btnHydLift; break;
                //--- lote ---
                case "lote_menu": b = btnJobMenu; break;
                case "lote_datos": b = btnFieldStats; break;
                case "bandera": b = btnFlag; break;
                case "mapeo_color": b = btnChangeMappingColor; break;
                //--- menú izquierdo HTML (espejo del panelLeft nativo) ---
                case "navegacion": b = btnNavigationSettings; break;
                case "hub": act = () => toolStripAgroParallel_Click(this, EventArgs.Empty); break;
                case "direccion": b = btnAutoSteerConfig; break;
                case "corex": b = btnStartAgIO; break;
                case "datos_gps": b = btnGPSData; break;
                //--- barra superior HTML (espejo del panelControlBox nativo) ---
                case "minimizar": b = btnMinimizeMainForm; break;
                case "maximizar": b = btnMaximizeMainForm; break;
                case "apagar": b = btnShutdown; break;
                //hamburguesa nativa (perfil/idioma/simulador/kiosco/reset/ayuda):
                //DropDown.Show con coordenadas del form para que funcione aunque
                //menuStrip1 esté oculto en modo barras HTML.
                case "menu_principal":
                    act = () => fileToolStripMenuItem.DropDown.Show(this, new Point(24, 76));
                    break;
                //ítems de la hamburguesa espejados en HTML (barra-superior.html).
                //simulatorOnToolStripMenuItem es CheckOnClick: el handler lee
                //Checked YA toggleado, así que replicamos el click completo.
                case "simulador":
                    act = () =>
                    {
                        simulatorOnToolStripMenuItem.Checked = !simulatorOnToolStripMenuItem.Checked;
                        simulatorOnToolStripMenuItem_Click(simulatorOnToolStripMenuItem, EventArgs.Empty);
                    };
                    break;
                case "sim_coords": act = () => enterSimCoordsToolStripMenuItem_Click(this, EventArgs.Empty); break;
                case "kiosco": act = () => kioskModeToolStrip_Click(this, EventArgs.Empty); break;
                case "reset_all": act = () => resetALLToolStripMenuItem_Click(this, EventArgs.Empty); break;
                //los 3 desplegables nativos del panel izquierdo
                case "menu_all": act = () => toolStripDropDownButton1.ShowDropDown(); break;
                case "menu_herr_lote": act = () => toolStripBtnFieldTools.ShowDropDown(); break;
                case "menu_herramientas": act = () => toolStripDropDownButton4.ShowDropDown(); break;
                //--- submenú Config (espejo del desplegable "All") ---
                case "config_form": act = () => FloatMenuOpenConfig("tabSummary"); break;
                case "todos_ajustes": act = () => allSettingsMenuItem_Click(this, EventArgs.Empty); break;
                case "colores": act = () => colorsToolStripMenuItem_Click(this, EventArgs.Empty); break;
                case "colores_sec": act = () => colorsSectionToolStripMenuItem_Click(this, EventArgs.Empty); break;
                case "perfil_nuevo": act = () => newProfileToolStripMenuItem_Click(this, EventArgs.Empty); break;
                case "perfil_cargar": act = () => loadProfileToolStripMenuItem_Click(this, EventArgs.Empty); break;
                //gestión completa de perfiles en HTML (reemplaza las 2 forms nativas):
                //cargar/sumar/copiar/proteger con clave/borrar/backup
                case "perfil_gestion": act = () => OpenAgroParallelWidget("pages/perfiles.html", "Perfiles", 560, 620); break;
                case "directorios": act = () => setWorkingDirectoryToolStripMenuItem_Click(this, EventArgs.Empty); break;
                case "ayuda": act = () => helpMenuItem_Click(this, EventArgs.Empty); break;
                //--- submenú Herramientas (espejo de SpecialFunctions) ---
                case "asistente_direccion": act = () => steerWizardMenuItem_Click(this, EventArgs.Empty); break;
                case "grafico_direccion": act = () => toolStripAutoSteerChart_Click(this, EventArgs.Empty); break;
                case "grafico_rumbo": act = () => headingChartToolStripMenuItem_Click(this, EventArgs.Empty); break;
                case "grafico_xte": act = () => xTEChartToolStripMenuItem_Click(this, EventArgs.Empty); break;
                case "chequeo_roll": act = () => correctionToolStrip_Click(this, EventArgs.Empty); break;
                case "herr_limites": act = () => boundaryToolToolStripMenu_Click(this, EventArgs.Empty); break;
                case "suavizar_ab": act = () => SmoothABtoolStripMenu_Click(this, EventArgs.Empty); break;
                case "borrar_contornos": act = () => deleteContourPathsToolStripMenuItem_Click(this, EventArgs.Empty); break;
                case "corregir_pos": act = () => offsetFixToolStrip_Click(this, EventArgs.Empty); break;
                //corregir posición en HTML (reemplaza FormShiftPos): poner el
                //corrimiento de deriva en cero y togglear si se mantiene aplicado.
                case "shift_zero":
                    act = () => AppModel.SharedFieldProperties.DriftCompensation =
                        new AgOpenGPS.Core.Models.GeoDelta(0.0, 0.0);
                    break;
                case "offsets_on": act = () => isKeepOffsetsOn = true; break;
                case "offsets_off": act = () => isKeepOffsetsOn = false; break;
                case "visor_eventos": act = () => eventViewerToolStripMenuItem_Click(this, EventArgs.Empty); break;
                case "webcam": act = () => webcamToolStrip_Click(this, EventArgs.Empty); break;
                //--- submenú Herr. lote (espejo de FieldTools) ---
                case "cabecera_avanzada": act = () => headlandBuildToolStripMenuItem_Click(this, EventArgs.Empty); break;
                case "importar_guias": act = () => copyTracksToolStripMenuItem_Click(this, EventArgs.Empty); break;
                case "bandera_latlon": act = () => flagByLatLonToolStripMenuItem_Click(this, EventArgs.Empty); break;
                case "borrar_aplicado": act = () => toolStripAreYouSure_Click(this, EventArgs.Empty); break;
                case "tram_crear": act = () => tramLinesMenuField_Click(this, EventArgs.Empty); break;
                case "tram_vista": b = btnTramDisplayMode; break;
                //--- lindero y cabecera ---
                case "lindero": act = () => boundariesToolStripMenuItem_Click(this, EventArgs.Empty); break;
                case "cabecera": act = () => headlandToolStripMenuItem_Click(this, EventArgs.Empty); break;
                case "cabecera_onoff": b = btnHeadlandOnOff; break;
                // cboxIsSectionControlled es un CheckBox: mismo patrón que
                // auto_snap_to_pivot (togglear Checked + handler a mano).
                case "cabecera_secciones":
                    act = () =>
                    {
                        cboxIsSectionControlled.Checked = !cboxIsSectionControlled.Checked;
                        cboxIsSectionControlled_Click(this, EventArgs.Empty);
                    };
                    break;
                case "reset_herramienta": b = btnResetToolHeading; break;
                //--- vista ---
                case "v2d": b = btn2D; break;
                case "v3d": b = btn3D; break;
                case "norte2d": b = btnN2D; break;
                case "grilla": b = btnGrid; break;
                case "dia_noche": b = btnDayNightMode; break;
                case "brillo_up": b = btnBrightnessUp; break;
                case "brillo_dn": b = btnBrightnessDn; break;
                default: return false;
            }
            //InvokeOnClick y NO PerformClick: dispara el handler aunque el botón
            //esté en un panel oculto (2D/3D/brillo viven en panelNavigation, que
            //casi siempre está escondido; PerformClick ahí no hace nada).
            if (b != null) act = () => InvokeOnClick(b, EventArgs.Empty);
            if (act == null) return false;
            try
            {
                if (InvokeRequired) BeginInvoke((MethodInvoker)(() => act()));
                else act();
                return true;
            }
            catch { return false; }
        }

        //Aplica el sprite de vehículo custom (Mis vehículos, Agro Parallel).
        //archivo = nombre dentro de AgroParallel\wwwroot\img\vehiculos\ con la
        //convención tipo_marca_modelo.png:
        //  · tipo "articulado" → se parte al medio por el pivote: mitad de
        //    arriba = textura frontal, mitad de abajo = trasera (así el 4WD
        //    dobla por el pivote como el nativo)
        //  · resto de los tipos → textura del tractor rígido
        //Vacío/no existe = vuelve a las marcas embebidas. Llamar en hilo UI.
        public void AplicarVehiculoCustom()
        {
            try
            {
                //primero SIEMPRE restaurar las embebidas (así cambiar de un
                //custom articulado a uno rígido no deja mezclas)
                VehicleTextures.Tractor.SetBitmap(
                    TractorBitmaps.GetBitmap(Properties.Settings.Default.setBrand_TBrand));
                VehicleTextures.ArticulatedFront.SetBitmap(
                    ArticulatedBitmaps.GetFrontBitmap(Properties.Settings.Default.setBrand_WDBrand));
                VehicleTextures.ArticulatedRear.SetBitmap(
                    ArticulatedBitmaps.GetRearBitmap(Properties.Settings.Default.setBrand_WDBrand));

                string archivo = Properties.Settings.Default.setBrand_VehiculoCustom;
                if (string.IsNullOrEmpty(archivo)) return;

                string ruta = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "AgroParallel", "wwwroot", "img", "vehiculos", archivo);
                if (!System.IO.File.Exists(ruta)) return;

                bool esArticulado = archivo.StartsWith("articulado",
                    StringComparison.OrdinalIgnoreCase);
                if (esArticulado)
                {
                    // Piezas hechas a mano (mandan sobre el corte automático):
                    //   <nombre>.frente.png  → textura frontal (pivote = borde INFERIOR)
                    //   <nombre>.cola.png    → textura trasera (pivote = borde SUPERIOR)
                    string sinExt = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(ruta),
                        System.IO.Path.GetFileNameWithoutExtension(ruta));
                    string rutaFrente = sinExt + ".frente.png";
                    string rutaCola = sinExt + ".cola.png";
                    if (System.IO.File.Exists(rutaFrente) && System.IO.File.Exists(rutaCola))
                    {
                        using (var f = new Bitmap(rutaFrente))
                            VehicleTextures.ArticulatedFront.SetBitmap(new Bitmap(f));
                        using (var c = new Bitmap(rutaCola))
                            VehicleTextures.ArticulatedRear.SetBitmap(new Bitmap(c));
                        return;
                    }

                    // Corte automático: buscar la "cintura" (fila con menos píxeles
                    // opacos) en la banda central = la articulación, y recortar
                    // cada pieza a su contenido para que el arte toque el borde
                    // del pivote (el mapa estira el canvas completo).
                    using (var raw = new Bitmap(ruta))
                    using (var bmp = new Bitmap(raw))
                    {
                        int splitY = FilaCintura(bmp);
                        var frente = RecortarAContenido(bmp, 0, splitY);
                        var cola = RecortarAContenido(bmp, splitY, bmp.Height);
                        if (frente != null) VehicleTextures.ArticulatedFront.SetBitmap(frente);
                        if (cola != null) VehicleTextures.ArticulatedRear.SetBitmap(cola);
                    }
                }
                else
                {
                    // Variante "para el mapa" (<nombre>.mapa.png, ej: la versión
                    // SIN ruedas delanteras pintadas): si existe, es la textura
                    // del mapa y el PNG principal queda solo para el selector.
                    string sinExt2 = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(ruta),
                        System.IO.Path.GetFileNameWithoutExtension(ruta));
                    string rutaMapa = sinExt2 + ".mapa.png";
                    string rutaUsar = System.IO.File.Exists(rutaMapa) ? rutaMapa : ruta;
                    using (var raw = new Bitmap(rutaUsar))
                    {
                        VehicleTextures.Tractor.SetBitmap(new Bitmap(raw));
                    }
                }
            }
            catch { /* sprite corrupto: quedan las texturas embebidas */ }
        }

        //Copia el canal alfa del bitmap a un byte[ancho*alto] (sin unsafe).
        private static byte[] CanalAlfa(Bitmap bmp, out int w, out int h)
        {
            w = bmp.Width; h = bmp.Height;
            var d = bmp.LockBits(new Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var buf = new byte[d.Stride * h];
                System.Runtime.InteropServices.Marshal.Copy(d.Scan0, buf, 0, buf.Length);
                var alfa = new byte[w * h];
                for (int y = 0; y < h; y++)
                {
                    int row = y * d.Stride;
                    for (int x = 0; x < w; x++)
                        alfa[y * w + x] = buf[row + x * 4 + 3];
                }
                return alfa;
            }
            finally { bmp.UnlockBits(d); }
        }

        //Fila con MENOS píxeles opacos en la banda central (35%..65% del alto):
        //en un articulado visto desde arriba es la articulación.
        private static int FilaCintura(Bitmap bmp)
        {
            int w, h;
            byte[] alfa = CanalAlfa(bmp, out w, out h);
            int y0 = (int)(h * 0.35), y1 = (int)(h * 0.65);
            int mejorY = h / 2, mejorAncho = int.MaxValue;
            for (int y = y0; y < y1; y++)
            {
                int opacos = 0;
                for (int x = 0; x < w; x++)
                    if (alfa[y * w + x] > 40) opacos++;
                if (opacos > 0 && opacos < mejorAncho)
                {
                    mejorAncho = opacos;
                    mejorY = y;
                }
            }
            return mejorY;
        }

        //Recorta la franja [yDesde, yHasta) al bounding box de sus píxeles
        //opacos. Devuelve null si la franja está vacía.
        private static Bitmap RecortarAContenido(Bitmap bmp, int yDesde, int yHasta)
        {
            int w, h;
            byte[] alfa = CanalAlfa(bmp, out w, out h);
            int minX = w, minY = yHasta, maxX = -1, maxY = -1;
            for (int y = yDesde; y < yHasta && y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (alfa[y * w + x] > 40)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < 0) return null;
            return bmp.Clone(new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1),
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        }

        //Abre FormBuildTracks directo en el flujo de creación pedido (A+, A/B
        //o A/B curvo): abre/enfoca el form nativo, pasa a "nueva guía"
        //(btnNewTrack → panelChoose) y elige el tipo — todo disparando los
        //handlers reales por nombre de control, sin duplicar lógica.
        private void OpenBuildTracksPanel(string chooseButtonName)
        {
            try
            {
                //InvokeOnClick: funciona aunque panelBottom esté oculto
                InvokeOnClick(btnBuildTracks, EventArgs.Empty);
                var f = Application.OpenForms["FormBuildTracks"];
                if (f == null) return;

                var newBtn = f.Controls.Find("btnNewTrack", true);
                if (newBtn.Length > 0 && newBtn[0] is Button nb && nb.Visible && nb.Enabled)
                    nb.PerformClick();

                var pick = f.Controls.Find(chooseButtonName, true);
                if (pick.Length > 0 && pick[0] is Button pb && pb.Visible && pb.Enabled)
                    pb.PerformClick();
            }
            catch { /* si el flujo nativo cambió, al menos queda el form abierto */ }
        }

        //ítem con acción arbitraria (para handlers sin botón en paneles)
        private void FloatMenuAddAction(string text, Image icon, Action action)
        {
            var b = new Button
            {
                Text = text,
                Font = new Font("Segoe UI", 8F),
                Size = new Size(88, 76),
                FlatStyle = FlatStyle.Flat,
                BackColor = pxBg,
                ForeColor = pxText,
                Image = FloatMenuScaleIcon(icon, 30),
                TextImageRelation = TextImageRelation.ImageAboveText,
                TextAlign = ContentAlignment.BottomCenter,
                ImageAlign = ContentAlignment.TopCenter,
                TabStop = false,
                Margin = new Padding(4)
            };
            b.FlatAppearance.BorderColor = pxBorder;
            b.Click += (s, e) => action();
            flowFloatMenu.Controls.Add(b);
        }
    }
}
