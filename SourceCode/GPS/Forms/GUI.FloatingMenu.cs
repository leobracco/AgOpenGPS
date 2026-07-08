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

        //paleta PilotX
        private static readonly Color pxBgSoft = Color.FromArgb(0xF5, 0xF7, 0xF4);
        private static readonly Color pxBg = Color.FromArgb(0xE2, 0xE7, 0xE2);
        private static readonly Color pxBorder = Color.FromArgb(0xC5, 0xCF, 0xC5);
        private static readonly Color pxText = Color.FromArgb(0x10, 0x16, 0x12);
        private static readonly Color pxGreen = Color.FromArgb(0x4A, 0xBA, 0x3E);

        public void CreateFloatingMenu()
        {
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

            FloatMenuAddCategory("Lote", FloatMenuGlyph(0xE55F, 38, pxGreen), FloatMenuFillLote);          //pin de mapa
            FloatMenuAddCategory("Líneas", FloatMenuGlyph(0xE922, 38, pxGreen), FloatMenuFillLineas);       //flechas paralelas
            FloatMenuAddCategory("Guiado", FloatMenuGlyph(0xE55D, 38, pxGreen), FloatMenuFillGuiado);       //vehículo
            FloatMenuAddCategory("Ruta grabada", FloatMenuGlyph(0xEACD, 38, pxGreen), FloatMenuFillRutaGrabada); //punto grabación
            FloatMenuAddCategory("Secciones", FloatMenuGlyph(0xE9B0, 38, pxGreen), FloatMenuFillSecciones); //grilla
            FloatMenuAddCategory("Vista", FloatMenuGlyph(0xE8F4, 38, pxGreen), FloatMenuFillVista);         //ojo
            FloatMenuAddCategory("Configuración", FloatMenuGlyph(0xE8B8, 38, pxGreen), FloatMenuFillConfig); //engranaje
            FloatMenuAddCategory("Herramientas", FloatMenuGlyph(0xF10B, 38, pxGreen), FloatMenuFillHerramientas); //herramientas
            FloatMenuAddCategory("Agro Parallel", FloatMenuGlyph(0xE80B, 38, pxGreen), FloatMenuFillAgroParallel); //mundo conectado
            FloatMenuAddCategory("Simulador", FloatMenuGlyph(0xF06C, 38, pxGreen), FloatMenuFillSimulador); //robot
            FloatMenuAddCategory("Sistema", FloatMenuGlyph(0xE30C, 38, pxGreen), FloatMenuFillSistema);     //ventanas

            //PilotX: muestra/oculta los paneles auto-ocultados de forma deliberada.
            //Reemplaza al toggle invisible de la franja del mapa, que en táctil se
            //disparaba sin querer y hacía reaparecer todos los paneles.
            FloatMenuAddAction("Paneles", FloatMenuGlyph(0xE5D2, 30, pxGreen), () =>
            {
                if (isPanelBottomHidden)
                {
                    ShowAutoHiddenPanels();
                }
                else
                {
                    isPanelBottomHidden = true;
                    PanelsAndOGLSize();
                }
                panelFloatMenu.Visible = false;
            });

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

        private void FloatMenuFillLote()
        {
            FloatMenuAddButton("Lote", btnJobMenu);
            FloatMenuAddButton("Datos lote", btnFieldStats);
            FloatMenuAddButton("Bandera", btnFlag);
            FloatMenuAddButton("Tram lines", btnTramDisplayMode);
        }

        private void FloatMenuFillLineas()
        {
            FloatMenuAddButton("Líneas", btnTrack);
            FloatMenuAddButton("Dibujar AB", btnABDraw);
            FloatMenuAddButton("A+", btnPlusAB);
            FloatMenuAddButton("Crear líneas", btnBuildTracks);
            FloatMenuAddButton("Ocultar líneas", btnTracksOff);
            FloatMenuAddButton("Línea sig.", btnCycleLines);
            FloatMenuAddButton("Línea ant.", btnCycleLinesBk);
            FloatMenuAddButton("Snap pivot", btnSnapToPivot);
            FloatMenuAddButton("Nudge", btnNudge);
            FloatMenuAddButton("Nudge ref.", btnRefNudge);
            FloatMenuAddButton("Ajustar ‹", btnAdjLeft);
            FloatMenuAddButton("Ajustar ›", btnAdjRight);
        }

        private void FloatMenuFillGuiado()
        {
            FloatMenuAddButton("Contorno", btnContour);
            FloatMenuAddButton("Bloq. contorno", btnContourLock);
            FloatMenuAddButton("U-Turn", btnAutoYouTurn);
            FloatMenuAddButton("Saltos U-Turn", btnYouSkipEnable);
            FloatMenuAddButton("Cabecera", btnHeadlandOnOff);
            FloatMenuAddButton("Hidráulico", btnHydLift);
            FloatMenuAddButton("AutoTrack", btnAutoTrack);
        }

        private void FloatMenuFillRutaGrabada()
        {
            FloatMenuAddButton("Ir / Parar", btnPathGoStop);
            FloatMenuAddButton("Grabar", btnPathRecordStop);
            FloatMenuAddButton("Elegir ruta", btnPickPath);
            FloatMenuAddButton("Reanudar", btnResumePath);
            FloatMenuAddButton("Invertir AB", btnSwapABRecordedPath);
        }

        private void FloatMenuFillSecciones()
        {
            FloatMenuAddButton("Auto", btnSectionMasterAuto);
            FloatMenuAddButton("Manual", btnSectionMasterManual);
            FloatMenuAddButton("ISOBUS", btnIsobusSectionControl);
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
            FloatMenuAddButton("Color mapeo", btnChangeMappingColor);
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
            FloatMenuAddAction("Todos los ajustes", FloatMenuGlyph(0xE429, 30, pxText),
                () => allSettingsMenuItem_Click(this, EventArgs.Empty));
            FloatMenuAddButton("Navegación", btnNavigationSettings);
            FloatMenuAddButton("Dirección", btnAutoSteerConfig);
            FloatMenuAddButton("Datos GPS", btnGPSData);
            FloatMenuAddAction("Colores", Properties.Resources.ColourPick,
                () => colorsToolStripMenuItem_Click(this, EventArgs.Empty));
            FloatMenuAddAction("Colores secciones", Properties.Resources.SectionMapping,
                () => colorsSectionToolStripMenuItem_Click(this, EventArgs.Empty));
        }

        //vuelve al nivel Configuración desde un subgrupo
        private void FloatMenuBackToConfig()
        {
            FloatMenuShowCategory("Configuración", FloatMenuFillConfig);
        }

        //abre FormConfig directo en la solapa pedida, sin pasar por su menú lateral
        private void FloatMenuOpenConfig(string tabName)
        {
            panelFloatMenu.Visible = false; //cerrar el menú flotante antes del diálogo modal
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

        private void FloatMenuFillHerramientas()
        {
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
            FloatMenuAddAction("Herram. límites", Properties.Resources.Boundary,
                () => boundaryToolToolStripMenu_Click(this, EventArgs.Empty));
            FloatMenuAddAction("Suavizar AB", Properties.Resources.ABSmooth,
                () => SmoothABtoolStripMenu_Click(this, EventArgs.Empty));
            FloatMenuAddAction("Borrar contornos", Properties.Resources.Trash,
                () => deleteContourPathsToolStripMenuItem_Click(this, EventArgs.Empty));
            FloatMenuAddAction("Corregir posición", FloatMenuGlyph(0xE55C, 30, pxText),
                () => offsetFixToolStrip_Click(this, EventArgs.Empty));
            FloatMenuAddAction("Visor eventos", FloatMenuGlyph(0xEF6E, 30, pxText),
                () => eventViewerToolStripMenuItem_Click(this, EventArgs.Empty));
            FloatMenuAddAction("Webcam", FloatMenuGlyph(0xE04B, 30, pxText),
                () => webcamToolStrip_Click(this, EventArgs.Empty));
        }

        private void FloatMenuFillAgroParallel()
        {
            //ventana nueva de CoreX: la trae al frente o lanza CoreX.exe
            FloatMenuAddAction("CoreX", Properties.Resources.AgIO,
                () => InvokeOnClick(btnStartAgIO, EventArgs.Empty));
            FloatMenuAddAction("Hub", FloatMenuGlyph(0xE9F4, 30, pxText),
                () => toolStripAgroParallel_Click(this, EventArgs.Empty));
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
            FloatMenuAddButton("Reset sim", btnResetSim);
            FloatMenuAddButton("Reversa", btnSimReverseDirection);
            FloatMenuAddButton("Velocidad 0", btnSimSetSpeedToZero);
            FloatMenuAddButton("Ángulo 0", btnResetSteerAngle);
            FloatMenuAddButton("Rumbo herr.", btnResetToolHeading);
        }

        private void FloatMenuFillSistema()
        {
            FloatMenuAddButton("Minimizar", btnMinimizeMainForm);
            FloatMenuAddButton("Maximizar", btnMaximizeMainForm);
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
            b.Click += (s, e) =>
            {
                panelsAutoHideCounter = panelsAutoHideDelay;
                FloatMenuShowCategory(title, fill, back);
            };
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
            b.Click += (s, e) =>
            {
                panelsAutoHideCounter = panelsAutoHideDelay;
                action();
            };
            flowFloatMenu.Controls.Add(b);
        }
    }
}
