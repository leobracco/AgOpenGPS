//Please, if you use this, share the improvements

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Forms;
using AgLibrary.Logging;
using AgOpenGPS.Classes;
using AgOpenGPS.Controls;
using AgOpenGPS.Core;
using AgOpenGPS.Core.Models;
using AgOpenGPS.Core.Translations;
using AgOpenGPS.Core.ViewModels;
using AgOpenGPS.Forms.Profiles;
using AgOpenGPS.Properties;
using OpenTK;
using OpenTK.Graphics.OpenGL;
// AGROPARALLEL_MOD_START
using AgroParallel.Common;
// AGROPARALLEL_MOD_END
// VISTAX_MOD_START
using AgroParallel.VistaX;
// VISTAX_MOD_END
// ORBITX_MOD_START
using AgroParallel.OrbitX;
// ORBITX_MOD_END
// SECTIONX_MOD_START
using AgroParallel.SectionX;
// SECTIONX_MOD_END
// QUANTIX_MOD_START
using AgroParallel.QuantiX;
// QUANTIX_MOD_END
// CAMARAS_MOD_START
using AgroParallel.Camaras;
// CAMARAS_MOD_END
// FLOWX_MOD_START
using AgroParallel.FlowX;
// FLOWX_MOD_END
namespace AgOpenGPS
{
    //the main form object
    public partial class FormGPS : Form
    {
        public ApplicationCore AppCore { get; }

        public ApplicationModel AppModel => AppCore.AppModel;
        public ApplicationViewModel AppViewModel => AppCore.AppViewModel;

        // Deprecated. Only here to avoid numerous changes to existing code that not has been refactored.
        // Please use AppViewModel.IsMetric directly
        public bool isMetric
        {
            get { return AppViewModel.IsMetric; }
            set
            {
                AppViewModel.IsMetric = value;
            }
        }

        // Deprecated. Only here to avoid numerous changes to existing code that not has been refactored.
        // Please use AppViewModel.IsDay directly
        public bool isDay
        {
            get { return AppViewModel.IsDay; }
            set
            {
                AppViewModel.IsDay = value;
            }
        }

        // Deprecated. Only here to avoid numerous changes to existing code that not has been refactored.
        // Please use AppViewModel.Fields directly
        public string currentFieldDirectory
        {
            get { return AppModel.Fields.CurrentFieldName; }
            set { AppModel.Fields.SetCurrentFieldByName(value); }
        }

        // Deprecated. Only here to avoid numerous changes to existing code that not has been refactored.
        // Please use AppModel.FixHeading directly
        public double fixHeading
        {
            get { return AppModel.FixHeading.AngleInRadians; }
            set { AppModel.FixHeading = new GeoDir(value); }
        }

        public bool isJobStarted => AppModel.Fields.ActiveField != null;

        public string displayFieldName => AppModel.Fields.ActiveField != null ? AppModel.Fields.ActiveField.Name : gStr.gsNone;


        //To bring forward AgIO if running
        [System.Runtime.InteropServices.DllImport("User32.dll")]
        private static extern bool SetForegroundWindow(IntPtr handle);

        [System.Runtime.InteropServices.DllImport("User32.dll")]
        private static extern bool ShowWindow(IntPtr hWind, int nCmdShow);

        // VISTAX_MOD_START
        private SeedMonitor vistaXMonitor;
        // Watcher sobre implemento.json — recarga el monitor cuando el operario
        // guarda cambios desde la web (PUT /api/vistax/implemento). Sin esto,
        // el widget del overlay PilotX seguía con el mapeo viejo hasta reiniciar.
        private System.IO.FileSystemWatcher vistaXImplWatcher;
        private System.Windows.Forms.Timer vistaXImplDebounce;
        private VistaXNativePanel vistaXPanel;
        // Overlays HTML de VistaX (WebView2) — reemplazan al panel nativo cuando
        // _useHtmlVxOverlay=true. Son dos ventanas independientes:
        //   · vistaXStripHtml  → franja inferior ancha (vistax-live.html).
        //   · vistaXStatsHtml  → ventana superior con KPIs y aux (vistax-stats.html).
        // Si _useHtmlVxOverlay=false se crean el legacy native panel y estos quedan null.
        private AgroParallel.Common.VistaXWebOverlayPanel vistaXStripHtml;
        private AgroParallel.Common.VistaXWebOverlayPanel vistaXStatsHtml;
        // Default: nuevo overlay HTML. Para volver al legacy WinForms cambiar a false.
        private bool _useHtmlVxOverlay = true;
        public VistaXConfig vistaXConfig;
        private FormVistaXPopup vistaXPopupForm;
        private VistaXFieldLogger vistaXLogger;
        // VISTAX_MOD_END

        // SHAPEFILE_MOD_START
        private ShapefileLayer shapefileLayer;

        // Acceso interno para FormGpsStateProvider (Fase A — desacople via
        // IAogStateProvider). Mantiene el campo privado para el resto del
        // codebase; solo el adaptador del mismo assembly puede usarlo.
        internal ShapefileLayer ShapefileLayerForAdapters => shapefileLayer;
#pragma warning disable CS0169, CS0649 // wire-up de menus shapefile pendiente
        private ToolStripMenuItem shapefileToggleItem;
        private ToolStripMenuItem shapefileStyleItem;
        private ToolStripMenuItem shapefileInspectItem;
#pragma warning restore CS0169, CS0649
        private ShapefileLegendControl shapefileLegend;
        // Widget QuantiX HTML (WebView2) que reemplaza al ShapefileLegendControl
        // en pantalla principal. Cuando _useHtmlQxWidget=true se crea éste y el
        // legacy queda en null (todos los Refresh/Update son no-op porque
        // chequean shapefileLegend != null).
        // El widget HTML se autoalimenta vía /api/widget-quantix/state — no hay
        // SetMotorDosis ni MotorManualChanged en este camino: la POST a
        // /api/widget-quantix/manual escribe manual_mode/manual_dosis directo
        // en quantiX_motores.json, sin pisar dosis_fija (fuera de mapa default).
        // static readonly (no const) para que el branch del legacy
        // ShapefileLegendControl no se elimine como dead-code: lo dejamos
        // disponible si en algún momento conviene volver atrás.
        private static readonly bool _useHtmlQxWidget = true;
        private AgroParallel.Common.QuantiXWidgetWebView2Control quantixWidgetHtml;
        // Banner full-width arriba de la cabina: aparece SOLO cuando hay nodos
        // del implemento activo offline. UI HTML (cabina-alarmas.html) hosteada
        // en WebView2 para facilitar la futura migración a Avalonia.
        private AgroParallel.Common.AlertaNodosWebOverlayControl alertaNodosOverlay;
        // FlowX overlay: telemetría read-only (caudal real/target/PWM/PID) por
        // nodo configurado. Se posiciona apilado sobre el shapefileLegend.
        private AgroParallel.FlowX.FlowXLegendControl flowXLegend;

        // Modo inspeccion (paso 12): al hacer click en el mapa se abre un
        // popup con los atributos DBF del poligono bajo el cursor.
#pragma warning disable CS0649 // se asignara cuando se conecte el handler
        private bool shapefileInspectMode;
#pragma warning restore CS0649

        // Snapshots de matrices GL tomadas en cada frame — usadas por la
        // unprojection para convertir mouse (screen) -> mundo (easting/northing).
        private readonly double[] _glModelView = new double[16];
        private readonly double[] _glProjection = new double[16];
        private readonly int[] _glViewport = new int[4];
        private bool _glMatricesValid;
        // SHAPEFILE_MOD_END

        // ORBITX_MOD_START
        public OrbitXSync orbitXSync;
        // ORBITX_MOD_END
        // SECTIONX_MOD_START
        public SectionXBridge sectionXBridge;
        public SectionsSpeedPublisher sectionsSpeedPublisher;
        // SECTIONX_MOD_END
        // CAMARAS_MOD_START
        public CamarasRemoteRelay camarasRelay;
        // CAMARAS_MOD_END
        // FLOWX_MOD_START
        public FlowXBridge flowXBridge;
        // FLOWX_MOD_END
        // QUANTIX_MOD_START
        private QuantiXConfig quantiXConfig;
        private QuantiXSender quantiXSender;
        private QuantiXMotorBridge quantiXBridge;
        // QUANTIX_MOD_END

        // COREX_FIELD_MOD_START
        // Último campo notificado al sistema AgroParallel (CoreX / VistaX)
        // Permite detectar si es "nuevo", "abierto" o "continuar"
        private string _apLastFieldNotified = "";
        // COREX_FIELD_MOD_END


        #region // Class Props and instances

        //maximum sections available
        public const int MAXSECTIONS = 64;

        private bool leftMouseDownOnOpenGL; //mousedown event in opengl window
        public int flagNumberPicked = 0;

        public bool isBtnAutoSteerOn;

        //if we are saving a file
        public bool isSavingFile = false;

        //texture holders
        public ScreenTextures ScreenTextures = new ScreenTextures();
        public VehicleTextures VehicleTextures = new VehicleTextures();

        //create instance of a stopwatch for timing of frames and NMEA hz determination
        private readonly Stopwatch swFrame = new Stopwatch();

        public double secondsSinceStart;
        public double gridToolSpacing;

        //private readonly Stopwatch swDraw = new Stopwatch();
        //swDraw.Reset();
        //swDraw.Start();
        //swDraw.Stop();
        //label3.Text = ((double) swDraw.ElapsedTicks / (double) System.Diagnostics.Stopwatch.Frequency * 1000).ToString();

        //Time to do fix position update and draw routine
        public double frameTime = 0;

        //create instance of a stopwatch for timing of frames and NMEA hz determination
        //private readonly Stopwatch swHz = new Stopwatch();

        //Time to do fix position update and draw routine
        public double gpsHz = 10;

        //whether or not to use Stanley control
        public bool isStanleyUsed = true;

        public double m2InchOrCm, inchOrCm2m, m2FtOrM, ftOrMtoM, cm2CmOrIn, inOrCm2Cm;
        public string unitsFtM, unitsInCm, unitsInCmNS;

        public char[] hotkeys;

        //used by filePicker Form to return picked file and directory
        public string filePickerFileAndDirectory;

        //the position of the GPS Data window within the FormGPS window
        public int GPSDataWindowLeft = 80, GPSDataWindowTopOffset = 220;

        //isGPSData form up
        public bool isGPSSentencesOn = false, isKeepOffsetsOn = false;

        public Camera camera;

        /// <summary>
        /// create world grid
        /// </summary>
        public WorldGrid worldGrid;

        /// <summary>
        /// The NMEA class that decodes it
        /// </summary>
        public CNMEA pn;

        /// <summary>
        /// an array of sections
        /// </summary>
        public CSection[] section;

        /// <summary>
        /// an array of patches to draw
        /// </summary>
        //public CPatches[] triStrip;
        public List<CPatches> triStrip;

        /// <summary>
        /// AB Line object
        /// </summary>
        public CABLine ABLine;

        /// <summary>
        /// TramLine class for boundary and settings
        /// </summary>
        public CTram tram;

        /// <summary>
        /// Contour Mode Instance
        /// </summary>
        public CContour ct;

        /// <summary>
        /// Contour Mode Instance
        /// </summary>
        public CTrack trk;

        /// <summary>
        /// ABCurve instance
        /// </summary>
        public CABCurve curve;

        /// <summary>
        /// Auto Headland YouTurn
        /// </summary>
        public CYouTurn yt;

        /// <summary>
        /// Our vehicle only
        /// </summary>
        public CVehicle vehicle;

        /// <summary>
        /// Just the tool attachment that includes the sections
        /// </summary>
        public CTool tool;

        /// <summary>
        /// All the structs for recv and send of information out ports
        /// </summary>
        public CModuleComm mc;

        /// <summary>
        /// The boundary object
        /// </summary>
        public CBoundary bnd;

        /// <summary>
        /// Building a headland instance
        /// </summary>
        public CHeadLine hdl;

        /// <summary>
        /// The internal simulator
        /// </summary>
        public CSim sim;

        /// <summary>
        /// Heading, Roll, Pitch, GPS, Properties
        /// </summary>
        public CAHRS ahrs;

        /// <summary>
        /// Recorded Path
        /// </summary>
        public CRecordedPath recPath;

        /// <summary>
        /// Most of the displayed field data for GUI
        /// </summary>
        public CFieldData fd;

        ///// <summary>
        ///// Sound
        ///// </summary>
        public CSound sounds;

        public AgOpenGPS.Core.DrawLib.Font font;

        /// <summary>
        /// The new steer algorithms
        /// </summary>
        public CGuidance gyd;

        /// <summary>
        /// The new brightness code
        /// </summary>
        public CWindowsSettingsBrightnessController displayBrightness;

        /// <summary>
        /// The ISOBUS communication class
        /// </summary>
        public CISOBUS isobus;

        #endregion // Class Props and instances

        //The method assigned to the PowerModeChanged event call
        private void SystemEvents_PowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            //We are interested only in StatusChange cases
            if (e.Mode.HasFlag(Microsoft.Win32.PowerModes.StatusChange))
            {
                PowerLineStatus powerLineStatus = SystemInformation.PowerStatus.PowerLineStatus;

                Log.EventWriter($"Power Line Status Change to: {powerLineStatus}");

                if (powerLineStatus == PowerLineStatus.Online)
                {
                    btnChargeStatus.BackColor = Color.YellowGreen;

                    Form f = Application.OpenForms["FormSaveOrNot"];

                    if (f != null)
                    {
                        f.Focus();
                        f.Close();
                    }
                }
                else
                {
                    btnChargeStatus.BackColor = Color.LightCoral;
                }

                if (Settings.Default.setDisplay_isShutdownWhenNoPower && powerLineStatus == PowerLineStatus.Offline)
                {
                    Log.EventWriter("Shutdown Computer By Power Lost Setting");
                    Close();
                }
            }
        }

        public FormGPS()
        {
            //winform initialization
            InitializeComponent();

            InitializeLanguages();

            AppCore = new ApplicationCore(
                new DirectoryInfo(RegistrySettings.baseDirectory),
                null,
                null);
            // Uncomment next line for Performance analysis
            // InitializePerformanceTool(AppCore);

            //time keeper
            secondsSinceStart = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds;

            camera = new Camera(
                Properties.Settings.Default.setDisplay_camPitch,
                Properties.Settings.Default.setDisplay_camZoom);

            worldGrid = new WorldGrid(Resources.z_Floor);

            //our vehicle made with gl object and pointer of mainform
            vehicle = new CVehicle(this);

            tool = new CTool(this);

            //create a new section and set left and right positions
            //created whether used or not, saves restarting program

            section = new CSection[MAXSECTIONS];
            for (int j = 0; j < MAXSECTIONS; j++) section[j] = new CSection();

            triStrip = new List<CPatches>
            {
                new CPatches(this)
            };

            //our NMEA parser
            pn = new CNMEA(this);

            //create the ABLine instance
            ABLine = new CABLine(this);

            //new instance of contour mode
            ct = new CContour(this);

            curve = new CABCurve(this);

            //new track instance
            trk = new CTrack(this);

            hdl = new CHeadLine();

            ////new instance of auto headland turn
            yt = new CYouTurn(this);

            //module communication
            mc = new CModuleComm(this);

            //boundary object
            bnd = new CBoundary(this);

            //nmea simulator built in.
            sim = new CSim(this);

            ////all the attitude, heading, roll, pitch reference system
            ahrs = new CAHRS();

            //A recorded path
            recPath = new CRecordedPath(this);

            //fieldData all in one place
            fd = new CFieldData(this);

            //start the stopwatch
            //swFrame.Start();

            //instance of tram
            tram = new CTram(this);

            font = new AgOpenGPS.Core.DrawLib.Font(camera, ScreenTextures.Font);

            //the new steer algorithms
            gyd = new CGuidance(this);

            //sounds class
            sounds = new CSound();

            //brightness object class
            displayBrightness = new CWindowsSettingsBrightnessController(Properties.Settings.Default.setDisplay_isBrightnessOn);

            isobus = new CISOBUS(this);
        }

        private void FormGPS_Load(object sender, EventArgs e)
        {
            Log.EventWriter("Program Started: "
                + DateTime.Now.ToString("f", CultureInfo.InvariantCulture));
            Log.EventWriter("AOG Version: " + Application.ProductVersion.ToString(CultureInfo.InvariantCulture));

            if (!Properties.Settings.Default.setDisplay_isTermsAccepted)
            {
                using (var form = new FormTermsAndConditions())
                {
                    if (form.ShowDialog(this) != DialogResult.OK)
                    {
                        Log.EventWriter("Terms Not Accepted");
                        Log.FileSaveSystemEvents();
                        Environment.Exit(0);
                    }
                    else
                    {
                        Log.EventWriter("Terms Accepted");
                    }
                }
            }
            else Log.EventWriter("Terms Already Accepted");

            // VISTAX_MOD_START
            vistaXConfig = VistaXConfig.Load();
            // VISTAX_MOD_END

            this.MouseWheel += ZoomByMouseWheel;

            //The way we subscribe to the System Event to check when Power Mode has changed.
            Microsoft.Win32.SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;

            //start udp server is required
            StartLoopbackServer();

            //boundaryToolStripBtn.Enabled = false;
            FieldMenuButtonEnableDisable(false);

            panelRight.Enabled = false;

            oglMain.Left = 75;
            oglMain.Width = this.Width - statusStripLeft.Width - 84;

            panelSim.Left = Width / 2 - 330;
            panelSim.Width = 700;
            panelSim.Top = Height - 60;

            //set the language to last used
            SetLanguage(RegistrySettings.culture);

            //make sure current field directory exists, null if not
            currentFieldDirectory = Settings.Default.setF_CurrentDir;

            Log.EventWriter("Program Directory: " + Application.StartupPath);
            Log.EventWriter("Fields Directory: " + (RegistrySettings.fieldsDirectory));

            if (isBrightnessOn)
            {
                if (displayBrightness.isWmiMonitor)
                {
                    Settings.Default.setDisplay_brightnessSystem = displayBrightness.GetBrightness();
                    Settings.Default.Save();
                }
                else
                {
                    btnBrightnessDn.Enabled = false;
                    btnBrightnessUp.Enabled = false;
                }

                //display brightness
                if (displayBrightness.isWmiMonitor)
                {
                    if (Settings.Default.setDisplay_brightness < Settings.Default.setDisplay_brightnessSystem)
                    {
                        Settings.Default.setDisplay_brightness = Settings.Default.setDisplay_brightnessSystem;
                        Settings.Default.Save();
                    }

                    displayBrightness.SetBrightness(Settings.Default.setDisplay_brightness);
                }
                else
                {
                    btnBrightnessDn.Enabled = false;
                    btnBrightnessUp.Enabled = false;
                }
            }

            // load all the gui elements in gui.designer.cs
            LoadSettings();

            //for field data and overlap
            oglZoom.Width = 400;
            oglZoom.Height = 400;
            oglZoom.Left = 100;
            oglZoom.Top = 100;

            if (RegistrySettings.vehicleFileName != "" && Properties.Settings.Default.setDisplay_isAutoStartAgIO)
            {
                //Start AgIO process
                Process[] processName = Process.GetProcessesByName("AgIO");
                if (processName.Length == 0)
                {
                    //Start application here
                    string strPath = Path.Combine(Application.StartupPath, "AgIO.exe");
                    try
                    {
                        ProcessStartInfo processInfo = new ProcessStartInfo
                        {
                            FileName = strPath,
                            WorkingDirectory = Path.GetDirectoryName(strPath)
                        };
                        Process proc = Process.Start(processInfo);
                        Log.EventWriter("AgIO Started");
                    }
                    catch
                    {
                        TimedMessageBox(2000, "No File Found", "Can't Find AgIO");
                        Log.EventWriter("Can't Find AgIO, File not Found");
                    }
                }
            }

            //nmea limiter
            udpWatch.Start();

            panelDrag.Draggable(true);

            hotkeys = new char[19];

            hotkeys = Properties.Settings.Default.setKey_hotkeys.ToCharArray();

            if (RegistrySettings.vehicleFileName == "")
            {
                Log.EventWriter("No profile selected, prompt to create a new one");

                YesMessageBox("No profile selected\n\nCreate a new profile to save your configuration\n\nIf no profile is created, NO changes will be saved!");

                using (FormNewProfile form = new FormNewProfile(this))
                {
                    form.ShowDialog(this);
                }
            }
            // VISTAX_MOD_START
            InitVistaX();
            // Botón VX en panelControlBox removido 2026-05-26 — pisaba la card
            // de GPS y ya no se usa: el overlay VistaX se prende/apaga desde el
            // Hub (/widgets.html) y se auto-abre al activar un perfil de
            // implemento con nodos VistaX (ver OverlayAutoOpener).
            // VISTAX_MOD_END

            // Watcher de overlayPrefs.json (controlado desde el Hub):
            // levanta un timer 250ms que detecta cambios y dispara redraw
            // de cada overlay (QX/VX/FX) sin reiniciar.
            StartOverlayPrefsWatcher();

            // ORBITX_MOD_START — Sync cloud al inicio.
            try
            {
                var oxCfg = OrbitXConfig.Load();
                if (oxCfg.Enabled && !string.IsNullOrEmpty(oxCfg.DeviceToken))
                {
                    orbitXSync = new OrbitXSync(new AgroParallel.Adapters.FormGpsStateProvider(this), oxCfg);
                    orbitXSync.Start();
                }

                // CAMARAS_MOD_START — Relay de cámaras: siempre registra al cloud
                // para que el panel sepa que el tractor tiene cámaras; los workers
                // de RTSP solo arrancan si CamarasStreamingEnabled = true.
                if (oxCfg.Enabled && !string.IsNullOrEmpty(oxCfg.DeviceToken))
                {
                    camarasRelay = new CamarasRemoteRelay(oxCfg);
                    camarasRelay.Start();
                }
                // CAMARAS_MOD_END
            }
            catch { }
            // ORBITX_MOD_END

            // SECTIONX_MOD_START — Bridge de secciones al inicio.
            try
            {
                var sxCfg = SectionXConfig.Load();
                if (sxCfg.Enabled && sxCfg.Nodos.Count > 0)
                {
                    sectionXBridge = new SectionXBridge(new AgroParallel.Adapters.FormGpsStateProvider(this), sxCfg);
                    _ = sectionXBridge.StartAsync();
                }

                // Publisher de velocidad por sección — independiente de SectionXConfig.Nodos
                // porque la velocidad la consumen también QuantiX/FlowX/VistaX (no solo relays).
                // Se publica en agp/aog/sections_speed a 5 Hz. Solo emite con lote abierto.
                sectionsSpeedPublisher = new SectionsSpeedPublisher(new AgroParallel.Adapters.FormGpsStateProvider(this));
                _ = sectionsSpeedPublisher.StartAsync();
            }
            catch { }
            // SECTIONX_MOD_END

            // QUANTIX_MOD_START — Bridge de motores al inicio (no espera campo abierto).
            try
            {
                if (quantiXBridge == null)
                {
                    var motCfg = MotoresConfig.Load();
                    if (motCfg.Nodos.Count > 0)
                    {
                        Console.WriteLine("[QuantiX] " + motCfg.Nodos.Count + " nodo(s) configurados, iniciando bridge...");
                        quantiXBridge = new QuantiXMotorBridge(new AgroParallel.Adapters.FormGpsStateProvider(this), new AgroParallel.Services.PrescripcionService());
                        _ = quantiXBridge.StartAsync();
                    }
                }
                // Visibilidad de la leyenda QX se fuerza al final de
                // InitShapefileLegend (que se llama después de este bloque).
            }
            catch (Exception ex)
            {
                Console.WriteLine("[QuantiX] Error iniciando bridge: " + ex.Message);
            }
            // QUANTIX_MOD_END

            // FLOWX_MOD_START — Bridge de bomba pulverizadora al inicio.
            // Solo arranca si flowX.json tiene Enabled=true y al menos un nodo.
            // El bridge tickea a 200ms, publica target L/min escalado por secciones
            // abiertas al topic agp/flow/{uid}/target. Si el firmware todavía está
            // buggy (ver project_flowx_stormx_scaffold.md), el publish no daña nada.
            try
            {
                var fxCfg = FlowXConfig.Load();
                if (fxCfg.Enabled && fxCfg.Nodos != null && fxCfg.Nodos.Count > 0)
                {
                    flowXBridge = new FlowXBridge(
                        new AgroParallel.Adapters.FormGpsStateProvider(this), fxCfg);
                    _ = flowXBridge.StartAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[FlowX] Bridge start error: " + ex.Message);
            }
            // FLOWX_MOD_END

            // AGROPARALLEL_MOD_START
            InitAgroParallelModulesMenu();
            // AGROPARALLEL_MOD_END

            // SHAPEFILE_MOD_START
            InitShapefileMenu();
            // SHAPEFILE_MOD_END

            // AGROPARALLEL_WEB_UI_START
            // Mientras AOG siga siendo la pantalla principal de cabina, el Hub
            // HTML NO se abre automáticamente — solo manualmente por el botón
            // AgroParallel (OpenAgroParallelHub en btnAgroParallel_Click).
            //
            // Pre-warm de WebView2: CoreWebView2Environment.CreateAsync demora
            // ~500ms-1s la primera vez. Lo disparamos acá en background así,
            // cuando el operario toca "⬢ AP" o "📷 Cámaras" más tarde, la
            // apertura del Hub es ~instantánea. Idempotente; si falla,
            // OnLoad del Hub vuelve a intentar sin diferencia visible.
            try
            {
                _ = global::AgroParallel.Shell.FormAgroParallelHubWebView2.Prewarm();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AgroParallel] Prewarm: " + ex.Message);
            }

            // Íconos del menú superior: cargar PNG desde btnImages/ en runtime
            // (más simple que pasarlos por Resources.resx, que es autogenerado).
            // Si los archivos no existen, dejamos el texto fallback que ya pintó el Designer.
            try
            {
                string imgDir = System.IO.Path.Combine(Application.StartupPath, "btnImages");
                string hubPng = System.IO.Path.Combine(imgDir, "AgroParallelHub.png");
                if (System.IO.File.Exists(hubPng))
                {
                    // HUB es un item dentro del dropdown del engranaje — sí o sí
                    // tiene que respetar el layout de los HERMANOS (Configuration,
                    // Auto Steer, etc. = 419x44 cada uno). Antes lo forzábamos a
                    // Size(100, 44) "estilo botón de toolbar", lo que hacía que
                    // al abrir el menú se viera todo descalzado: la fila era
                    // gigante (419 px) pero el item HUB ocupaba 100, dejando un
                    // hueco enorme a la derecha. Ahora dejamos AutoSize=true y
                    // que el ToolStrip dimensione cada item parejo.
                    toolStripAgroParallel.Image = System.Drawing.Image.FromFile(hubPng);
                    toolStripAgroParallel.Text = "HUB";
                    toolStripAgroParallel.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
                    toolStripAgroParallel.ImageScaling = ToolStripItemImageScaling.SizeToFit;
                    toolStripAgroParallel.TextImageRelation = TextImageRelation.ImageBeforeText;
                    toolStripAgroParallel.ToolTipText = "Abrir Hub Agro Parallel";
                }
                string camPng = System.IO.Path.Combine(imgDir, "Camera2D64.png");
                if (System.IO.File.Exists(camPng))
                {
                    // Idem HUB: que el item se dimensione solo dentro del dropdown.
                    // Texto "Cámaras" en vez de string vacío — sin texto el item
                    // queda como un icono colgado en medio de una lista de labels,
                    // que es justo el efecto "abre todo mal" que reportó el operario.
                    toolStripCamaras.Image = System.Drawing.Image.FromFile(camPng);
                    toolStripCamaras.Text = "Cámaras";
                    toolStripCamaras.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
                    toolStripCamaras.ImageScaling = ToolStripItemImageScaling.SizeToFit;
                    toolStripCamaras.TextImageRelation = TextImageRelation.ImageBeforeText;
                    toolStripCamaras.ToolTipText = "Cámaras";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AgroParallel] Iconos: " + ex.Message);
            }

            // Levantar AgpWebHost:5180 ya mismo (no esperar al click del Hub).
            // Esto habilita que widgets Avalonia standalone (AgroParallel.Shell.Avalonia.exe
            // --page=pages/camaras.html) puedan conectar sin abrir el Hub completo.
            // Idempotente: si el Hub se abre despues, reusa este mismo host.
            try
            {
                string wwwroot = System.IO.Path.Combine(
                    System.AppDomain.CurrentDomain.BaseDirectory, "AgroParallel", "wwwroot");
                var state = new global::AgroParallel.Adapters.FormGpsStateProvider(this);
                var lotes = new global::AgroParallel.Adapters.FormGpsLotesService(this);
                var vehicleTool = new global::AgroParallel.Adapters.FormGpsVehicleToolService(this);
                var shapefile = new global::AgroParallel.Adapters.FormGpsShapefileService(this);
                var coverage = new global::AgroParallel.Adapters.FormGpsCoverageService(this);
                var sectionsCore = new global::AgroParallel.Adapters.FormGpsSectionControlService(this);
                var quantixRuntime = new global::AgroParallel.Adapters.FormGpsQuantiXRuntimeService(this, state);
                var guidance = new global::AgroParallel.Adapters.FormGpsGuidanceCalculator(this);
                var toolGeometry = new global::AgroParallel.Adapters.FormGpsToolGeometryCalculator(this);
                var tram = new global::AgroParallel.Adapters.FormGpsTramCalculator(this);
                var pilotxUpdate = new global::AgroParallel.Adapters.FormGpsPilotXUpdateService();
                global::AgroParallel.Shell.AgpWebHostBootstrap.EnsureStarted(
                    state, lotes, vehicleTool, shapefile,
                    coverage, sectionsCore, quantixRuntime, guidance, pilotxUpdate,
                    wwwroot,
                    toolGeometry: toolGeometry,
                    tram: tram);

                // El widget QX HTML necesita la Url del host para navegar; cuando
                // InitShapefileMenu() corrió antes (línea 739), Url todavía no
                // estaba. Reintentamos la creación / visibilidad ahora que sí está.
                try { UpdateShapefileLegendVisibility(); } catch { }

                // Banner top de alarmas (HTML overlay). Se queda Visible=false
                // hasta que la página emita postMessage('show') al detectar nodos
                // del implemento activo offline.
                try { InitAlertaNodosOverlay(); } catch (Exception exA) { System.Diagnostics.Debug.WriteLine("[AlertaNodos] init: " + exA.Message); }

                // Cuando el operario guarda sectionX.json desde la UI Hub,
                // relanzar el SectionXBridge — si no lo hacemos, el bridge se
                // queda con la config vieja (típicamente null si arrancó con
                // nodos:[]) y /sections nunca se publica al broker → los relays
                // de los nodos QuantiX nunca se activan. Bug observado 2026-05-19.
                try
                {
                    var sxSvc = global::AgroParallel.Shell.AgpWebHostBootstrap.SectionXConfigSvc;
                    if (sxSvc != null)
                    {
                        sxSvc.ConfigSaved += () =>
                        {
                            try
                            {
                                if (IsDisposed || !IsHandleCreated) return;
                                BeginInvoke(new Action(() => ReloadSectionXBridge()));
                            }
                            catch { /* swallow — no romper el save por un handler */ }
                        };
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[AgroParallel] SectionX subscribe: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AgroParallel] WebHost autostart: " + ex.Message);
            }
            // AGROPARALLEL_WEB_UI_END
        }

        #region Shutdown Handling

        // Centralized shutdown coordinator
        private bool isShuttingDown = false;

        private async void FormGPS_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (isShuttingDown) return;

            // Set shutdown flag to prevent re-entrance
            isShuttingDown = true;
            e.Cancel = true; // Prevent immediate close

            // Close subforms
            string[] formNames = { "FormGPSData", "FormFieldData", "FormPan", "FormTimedMessage" };
            foreach (string name in formNames)
            {
                Form f = Application.OpenForms[name];
                if (f != null && !f.IsDisposed)
                {
                    try { f.Close(); } catch { }
                }
            }

            // AGROPARALLEL_MOD_START
            // Cerrar explicitamente las ventanas popup de AgroParallel antes del
            // check de OwnedForms para que el cierre no quede cancelado.
            if (vistaXPopupForm != null && !vistaXPopupForm.IsDisposed)
            {
                try { vistaXPopupForm.Close(); } catch { }
                vistaXPopupForm = null;
            }
            // Como fallback: cerrar cualquier otro OwnedForm nuestro (forms de
            // config / estilo / export shapefile etc.). Evita que un modal
            // abierto por error bloquee el shutdown.
            var ownedCopy = this.OwnedForms.ToArray();
            foreach (var of in ownedCopy)
            {
                if (of != null && !of.IsDisposed)
                {
                    try { of.Close(); } catch { }
                }
            }
            // AGROPARALLEL_MOD_END

            // Cancel shutdown if owned forms are still open
            if (this.OwnedForms.Any())
            {
                TimedMessageBox(2000, gStr.gsWindowsStillOpen, gStr.gsCloseAllWindowsFirst);
                isShuttingDown = false;
                return;
            }

            // Get user choice for shutdown behavior
            int choice = SaveOrNot();
            if (choice == 1)
            {
                // User cancelled shutdown
                isShuttingDown = false;
                return;
            }

            // AGROPARALLEL_MOD_START — apagar servicios background una vez
            // confirmado el shutdown, antes del save. Crítico: camarasRelay
            // spawnea ffmpeg.exe (procesos externos al runtime .NET) — si no
            // los matamos explícitamente, quedan vivos en background al
            // cerrar AOG (bug observado: 3 ffmpeg zombies por cierre).
            // orbitXSync se para acá para no escribir orbitX.json mientras
            // FileSaveEverythingBeforeClosingField está corriendo.
            try { camarasRelay?.Dispose(); camarasRelay = null; } catch { }
            try { orbitXSync?.Stop(); orbitXSync = null; } catch { }
            try { sectionsSpeedPublisher?.Dispose(); sectionsSpeedPublisher = null; } catch { }
            try { sectionXBridge?.Dispose(); sectionXBridge = null; } catch { }
            try { flowXBridge?.Dispose(); flowXBridge = null; } catch { }
            // AGROPARALLEL_MOD_END

            // Turn off auto sections if active
            if (isJobStarted && autoBtnState == btnStates.Auto)
            {
                btnSectionMasterAuto.PerformClick();
            }

            // Execute shutdown with proper exception handling
            try
            {
                // VISTAX_MOD_START
                CleanupVistaX();
                // VISTAX_MOD_END

                Log.EventWriter("Closing Application " + DateTime.Now);
                await ShowSavingFormAndShutdown(choice);
            }
            catch (Exception ex)
            {
                Log.EventWriter($"CRITICAL: Shutdown error: {ex}");
                MessageBox.Show($"Error during shutdown: {ex.Message}\n\nAttempting force exit...",
                    "Shutdown Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                // Ensure application exits even if shutdown fails
                Application.Exit();
            }
        }


        private async Task ShowSavingFormAndShutdown(int choice)
        {
            FormSaving savingForm = null;

            try
            {
                savingForm = new FormSaving();

                if (isJobStarted)
                {
                    // Setup progress steps
                    savingForm.AddStep("Field", gStr.gsSaveField);
                    savingForm.AddStep("Settings", gStr.gsSaveSettings);
                    savingForm.AddStep("Finalize", gStr.gsSaveFinalizeShutdown);

                    savingForm.Show();
                    await Task.Delay(300); // Let UI settle

                    // STEP 1: Save Field (Boundary, Tracks, Sections, Contour, etc.)
                    try
                    {
                        await FileSaveEverythingBeforeClosingField();
                        savingForm.UpdateStep("Field", gStr.gsSaveFieldSavedLocal, SavingStepState.Done);
                    }
                    catch (Exception ex)
                    {
                        Log.EventWriter($"CRITICAL: Field save error during shutdown: {ex}");
                        savingForm.UpdateStep("Field", "Field save FAILED: " + ex.Message, SavingStepState.Failed);

                        // Ask user if they want to continue despite error
                        DialogResult result = MessageBox.Show(
                            $"Field data save failed:\n{ex.Message}\n\nContinue shutdown anyway? (data may be lost)",
                            "Critical Save Error",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning);

                        if (result == DialogResult.No)
                        {
                            isShuttingDown = false;
                            if (savingForm != null && !savingForm.IsDisposed) savingForm.Close();
                            return; // Exit without calling FinishShutdown - user cancelled
                        }
                    }

                    // STEP 3: Settings + System Log
                    try
                    {
                        Settings.Default.Save();
                        Log.FileSaveSystemEvents();
                        await Task.Delay(300);
                        savingForm.UpdateStep("Settings", gStr.gsSaveSettingsSaved, SavingStepState.Done);
                    }
                    catch (Exception ex)
                    {
                        Log.EventWriter($"Settings save error: {ex}");
                        savingForm.UpdateStep("Settings", "Settings save failed", SavingStepState.Failed);
                    }

                    // STEP 4: Finalizing
                    await Task.Delay(500);
                    savingForm.UpdateStep("Finalize", gStr.gsSaveAllDone, SavingStepState.Done);
                    await Task.Delay(750);
                    savingForm.Finish();
                }
                else
                {
                    // Job not started - just save settings with visual feedback
                    savingForm.AddStep("Settings", gStr.gsSaveSettings);
                    savingForm.AddStep("Finalize", gStr.gsSaveFinalizeShutdown);

                    savingForm.Show();
                    await Task.Delay(300); // Let UI settle

                    try
                    {
                        Settings.Default.Save();
                        Log.FileSaveSystemEvents();
                        await Task.Delay(300);
                        savingForm.UpdateStep("Settings", gStr.gsSaveSettingsSaved, SavingStepState.Done);
                    }
                    catch (Exception ex)
                    {
                        Log.EventWriter($"Settings save error: {ex}");
                        savingForm.UpdateStep("Settings", "Settings save failed", SavingStepState.Failed);
                    }

                    // Finalizing
                    await Task.Delay(500);
                    savingForm.UpdateStep("Finalize", gStr.gsSaveAllDone, SavingStepState.Done);
                    await Task.Delay(750);
                    savingForm.Finish();
                }
            }
            finally
            {
                // Ensure form is disposed
                savingForm?.Dispose();
            }

            // Only finish shutdown if we didn't return early due to user cancellation
            FinishShutdown(choice);
        }


        private void FinishShutdown(int choice)
        {
            SaveFormGPSWindowSettings();

            double minutesSinceStart = ((DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds) / 60;
            if (minutesSinceStart < 1) minutesSinceStart = 1;

            Log.EventWriter("Missed Sentence Counter Total: " + missedSentenceCount.ToString()
                + "   Missed Per Minute: " + ((double)missedSentenceCount / minutesSinceStart).ToString("N4"));

            Log.EventWriter("Program Exit: " + DateTime.Now.ToString("f", CultureInfo.CreateSpecificCulture(RegistrySettings.culture)) + "\r");

            // Restore display brightness
            if (displayBrightness.isWmiMonitor)
            {
                try { displayBrightness.SetBrightness(Settings.Default.setDisplay_brightnessSystem); }
                catch { }
            }

            // Perform Windows shutdown if user selected it
            if (choice == 2)
            {
                try
                {
                    Process[] agio = Process.GetProcessesByName("AgIO");
                    if (agio.Length > 0) agio[0].CloseMainWindow();
                }
                catch { }

                try
                {
                    Process.Start("shutdown", "/s /t 0");
                }
                catch { }
            }

            // Close loopback socket if active
            if (loopBackSocket != null)
            {
                try
                {
                    loopBackSocket.Shutdown(SocketShutdown.Both);
                    loopBackSocket.Close();
                }
                catch { }
            }

            // Auto close AgIO process if enabled
            if (Settings.Default.setDisplay_isAutoOffAgIO)
            {
                try
                {
                    Process[] agio = Process.GetProcessesByName("AgIO");
                    if (agio.Length > 0)
                    {
                        agio[0].CloseMainWindow();
                        // Si no responde en 2s, matar — evita que el DLL quede
                        // bloqueado y el proximo build falle con MSB3027.
                        if (!agio[0].WaitForExit(2000))
                        {
                            try { agio[0].Kill(); } catch { }
                        }
                    }
                }
                catch { }
            }

            // VISTAX_MOD_END

            // Close the main application form
            try { Close(); }
            catch (ObjectDisposedException) { }
        }
        #endregion

        public int SaveOrNot()
        {
            CloseTopMosts();

            using (FormSaveOrNot form = new FormSaveOrNot(this))
            {
                DialogResult result = form.ShowDialog(this);

                if (result == DialogResult.OK) return 0;      //Exit to windows
                if (result == DialogResult.Ignore) return 1;   //Ignore & return
                if (result == DialogResult.Yes) return 2;   //Shutdown computer

                return 1;  // oops something is really busted
            }
        }

        private void FormGPS_ResizeEnd(object sender, EventArgs e)
        {
            PanelsAndOGLSize();
            if (isGPSPositionInitialized) SetZoom();

            Form f = Application.OpenForms["FormGPSData"];
            if (f != null)
            {
                f.Top = this.Top + this.Height / 2 - GPSDataWindowTopOffset;
                f.Left = this.Left + GPSDataWindowLeft;
            }

            f = Application.OpenForms["FormFieldData"];
            if (f != null)
            {
                f.Top = this.Top + this.Height / 2 - GPSDataWindowTopOffset;
                f.Left = this.Left + GPSDataWindowLeft;
            }

            f = Application.OpenForms["FormPan"];
            if (f != null)
            {
                f.Top = this.Height / 3 + this.Top;
                f.Left = this.Width - 400 + this.Left;
            }
        }

        private void btnIsobusSC_Click(object sender, EventArgs e)
        {
            isobus.RequestSectionControlEnabled(!isobus.SectionControlEnabled);
        }

        private void FormGPS_Move(object sender, EventArgs e)
        {
            Form f = Application.OpenForms["FormGPSData"];
            if (f != null)
            {
                f.Top = this.Top + this.Height / 2 - GPSDataWindowTopOffset;
                f.Left = this.Left + GPSDataWindowLeft;
            }

            f = Application.OpenForms["FormFieldData"];
            if (f != null)
            {
                f.Top = this.Top + this.Height / 2 - GPSDataWindowTopOffset;
                f.Left = this.Left + GPSDataWindowLeft;
            }

            f = Application.OpenForms["FormPan"];
            if (f != null)
            {
                f.Top = this.Top + 75;
                f.Left = this.Left + this.Width - 380;
            }
        }

        //request a new job
        public void JobNew()
        {
            //SendSteerSettingsOutAutoSteerPort();

            AppModel.Fields.OpenField();
            startCounter = 0;

            btnFieldStats.Visible = true;

            btnSectionMasterManual.Enabled = true;
            manualBtnState = btnStates.Off;
            btnSectionMasterManual.Image = Properties.Resources.ManualOff;

            btnSectionMasterAuto.Enabled = true;
            autoBtnState = btnStates.Off;
            btnSectionMasterAuto.Image = Properties.Resources.SectionMasterOff;

            btnSection1Man.BackColor = Color.Red;
            btnSection2Man.BackColor = Color.Red;
            btnSection3Man.BackColor = Color.Red;
            btnSection4Man.BackColor = Color.Red;
            btnSection5Man.BackColor = Color.Red;
            btnSection6Man.BackColor = Color.Red;
            btnSection7Man.BackColor = Color.Red;
            btnSection8Man.BackColor = Color.Red;
            btnSection9Man.BackColor = Color.Red;
            btnSection10Man.BackColor = Color.Red;
            btnSection11Man.BackColor = Color.Red;
            btnSection12Man.BackColor = Color.Red;
            btnSection13Man.BackColor = Color.Red;
            btnSection14Man.BackColor = Color.Red;
            btnSection15Man.BackColor = Color.Red;
            btnSection16Man.BackColor = Color.Red;

            btnSection1Man.Enabled = true;
            btnSection2Man.Enabled = true;
            btnSection3Man.Enabled = true;
            btnSection4Man.Enabled = true;
            btnSection5Man.Enabled = true;
            btnSection6Man.Enabled = true;
            btnSection7Man.Enabled = true;
            btnSection8Man.Enabled = true;
            btnSection9Man.Enabled = true;
            btnSection10Man.Enabled = true;
            btnSection11Man.Enabled = true;
            btnSection12Man.Enabled = true;
            btnSection13Man.Enabled = true;
            btnSection14Man.Enabled = true;
            btnSection15Man.Enabled = true;
            btnSection16Man.Enabled = true;

            btnZone1.BackColor = Color.Red;
            btnZone2.BackColor = Color.Red;
            btnZone3.BackColor = Color.Red;
            btnZone4.BackColor = Color.Red;
            btnZone5.BackColor = Color.Red;
            btnZone6.BackColor = Color.Red;
            btnZone7.BackColor = Color.Red;
            btnZone8.BackColor = Color.Red;

            btnZone1.Enabled = true;
            btnZone2.Enabled = true;
            btnZone3.Enabled = true;
            btnZone4.Enabled = true;
            btnZone5.Enabled = true;
            btnZone6.Enabled = true;
            btnZone7.Enabled = true;
            btnZone8.Enabled = true;

            btnContour.Enabled = true;
            btnTrack.Enabled = true;
            btnABDraw.Enabled = true;
            btnCycleLines.Image = Properties.Resources.ABLineCycle;
            btnCycleLinesBk.Image = Properties.Resources.ABLineCycleBk;

            ABLine.abHeading = 0.00;
            btnAutoSteer.Enabled = true;

            DisableYouTurnButtons();
            btnFlag.Enabled = true;

            if (tool.isSectionsNotZones)
            {
                LineUpIndividualSectionBtns();
            }
            else
            {
                LineUpAllZoneButtons();
            }

            //update the menu
            this.menustripLanguage.Enabled = false;
            panelRight.Enabled = true;
            //boundaryToolStripBtn.Enabled = true;
            isPanelBottomHidden = false;

            FieldMenuButtonEnableDisable(true);
            PanelUpdateRightAndBottom();
            PanelsAndOGLSize();
            SetZoom();

            fileSaveCounter = 25;
            lblGuidanceLine.Visible = false;
            lblHardwareMessage.Visible = false;
            btnAutoTrack.Image = Resources.AutoTrackOff;
            trk.isAutoTrack = false;

            // COREX_FIELD_MOD_START
            NotificarCampoExterno_JobNew();
            // COREX_FIELD_MOD_END

            // SHAPEFILE_MOD_START
            TryAutoLoadShapefileForCurrentField();
            // SHAPEFILE_MOD_END

            // QUANTIX_MOD_START
            StartQuantiXSender();
            // QUANTIX_MOD_END
        }

        //close the current job
        public void JobClose()
        {
            recPath.resumeState = 0;
            btnResumePath.Image = Properties.Resources.pathResumeStart;
            recPath.currentPositonIndex = 0;

            sbGrid.Clear();

            //reset field offsets
            if (!isKeepOffsetsOn)
            {
                AppModel.SharedFieldProperties.DriftCompensation = new GeoDelta(0.0, 0.0);
            }

            //turn off headland
            bnd.isHeadlandOn = false;

            btnFieldStats.Visible = false;

            recPath.recList.Clear();
            recPath.StopDrivingRecordedPath();
            panelDrag.Visible = false;

            //make sure hydraulic lift is off
            p_239.pgn[p_239.hydLift] = 0;
            vehicle.isHydLiftOn = false;
            btnHydLift.Image = Properties.Resources.HydraulicLiftOff;
            btnHydLift.Visible = false;
            lblHardwareMessage.Visible = false;

            lblGuidanceLine.Visible = false;
            lblHardwareMessage.Visible = false;

            //zoom gone
            oglZoom.SendToBack();

            //clean all the lines
            bnd.bndList.Clear();

            panelRight.Enabled = false;
            FieldMenuButtonEnableDisable(false);

            menustripLanguage.Enabled = true;

            // COREX_FIELD_MOD_START
            // Notificar ANTES de limpiar currentFieldDirectory
            NotificarCampoExterno_JobClose();
            // COREX_FIELD_MOD_END

            // SHAPEFILE_MOD_START
            ClearShapefileLayer();
            // SHAPEFILE_MOD_END

            // QUANTIX_MOD_START
            StopQuantiXSender();
            // QUANTIX_MOD_END

            AppModel.Fields.CloseField();


            //fix ManualOffOnAuto buttons
            manualBtnState = btnStates.Off;
            btnSectionMasterManual.Image = Properties.Resources.ManualOff;

            //fix auto button
            autoBtnState = btnStates.Off;
            btnSectionMasterAuto.Image = Properties.Resources.SectionMasterOff;

            if (tool.isSectionsNotZones)
            {
                //Update the button colors and text
                AllSectionsAndButtonsToState(btnStates.Off);

                //enable disable manual buttons
                LineUpIndividualSectionBtns();
            }
            else
            {
                AllZonesAndButtonsToState(autoBtnState);
                LineUpAllZoneButtons();
            }

            btnZone1.BackColor = Color.Silver;
            btnZone2.BackColor = Color.Silver;
            btnZone3.BackColor = Color.Silver;
            btnZone4.BackColor = Color.Silver;
            btnZone5.BackColor = Color.Silver;
            btnZone6.BackColor = Color.Silver;
            btnZone7.BackColor = Color.Silver;
            btnZone8.BackColor = Color.Silver;

            btnZone1.Enabled = false;
            btnZone2.Enabled = false;
            btnZone3.Enabled = false;
            btnZone4.Enabled = false;
            btnZone5.Enabled = false;
            btnZone6.Enabled = false;
            btnZone7.Enabled = false;
            btnZone8.Enabled = false;

            btnSection1Man.Enabled = false;
            btnSection2Man.Enabled = false;
            btnSection3Man.Enabled = false;
            btnSection4Man.Enabled = false;
            btnSection5Man.Enabled = false;
            btnSection6Man.Enabled = false;
            btnSection7Man.Enabled = false;
            btnSection8Man.Enabled = false;
            btnSection9Man.Enabled = false;
            btnSection10Man.Enabled = false;
            btnSection11Man.Enabled = false;
            btnSection12Man.Enabled = false;
            btnSection13Man.Enabled = false;
            btnSection14Man.Enabled = false;
            btnSection15Man.Enabled = false;
            btnSection16Man.Enabled = false;

            btnSection1Man.BackColor = Color.Silver;
            btnSection2Man.BackColor = Color.Silver;
            btnSection3Man.BackColor = Color.Silver;
            btnSection4Man.BackColor = Color.Silver;
            btnSection5Man.BackColor = Color.Silver;
            btnSection6Man.BackColor = Color.Silver;
            btnSection7Man.BackColor = Color.Silver;
            btnSection8Man.BackColor = Color.Silver;
            btnSection9Man.BackColor = Color.Silver;
            btnSection10Man.BackColor = Color.Silver;
            btnSection11Man.BackColor = Color.Silver;
            btnSection12Man.BackColor = Color.Silver;
            btnSection13Man.BackColor = Color.Silver;
            btnSection14Man.BackColor = Color.Silver;
            btnSection15Man.BackColor = Color.Silver;
            btnSection16Man.BackColor = Color.Silver;

            //clear the section lists
            for (int j = 0; j < triStrip.Count; j++)
            {
                //clean out the lists
                triStrip[j].patchList?.Clear();
                triStrip[j].triangleList?.Clear();
            }

            triStrip?.Clear();
            triStrip.Add(new CPatches(this));

            //clear the flags
            flagPts.Clear();

            //ABLine
            tram.tramList?.Clear();

            //curve line
            curve.ResetCurveLine();

            //tracks
            trk.gArr?.Clear();
            trk.idx = -1;

            //clean up tram
            tram.displayMode = 0;
            tram.generateMode = 0;
            tram.tramBndInnerArr?.Clear();
            tram.tramBndOuterArr?.Clear();

            //clear out contour and Lists
            btnContour.Enabled = false;
            //btnContourPriority.Enabled = false;
            //btnSnapToPivot.Image = Properties.Resources.SnapToPivot;
            ct.ResetContour();
            ct.isContourBtnOn = false;
            btnContour.Image = Properties.Resources.ContourOff;
            ct.isContourOn = false;

            btnABDraw.Enabled = false;
            btnCycleLines.Image = Properties.Resources.ABLineCycle;
            //btnCycleLines.Enabled = false;
            btnCycleLinesBk.Image = Properties.Resources.ABLineCycleBk;
            //btnCycleLinesBk.Enabled = false;

            //AutoSteer
            btnAutoSteer.Enabled = false;
            isBtnAutoSteerOn = false;
            btnAutoSteer.Image = trk.isAutoSnapToPivot ? Properties.Resources.AutoSteerOffSnapToPivot : Properties.Resources.AutoSteerOff;

            //auto YouTurn shutdown
            yt.isYouTurnBtnOn = false;
            btnAutoYouTurn.Image = Properties.Resources.YouTurnNo;

            btnABDraw.Visible = false;

            yt.ResetYouTurn();
            DisableYouTurnButtons();

            //reset acre and distance counters
            fd.workedAreaTotal = 0;

            //reset GUI areas
            fd.UpdateFieldBoundaryGUIAreas();

            recPath.recList?.Clear();
            recPath.shortestDubinsList?.Clear();
            recPath.shuttleDubinsList?.Clear();

            isPanelBottomHidden = false;

            PanelsAndOGLSize();
            SetZoom();
            worldGrid.BingMap = null;

            panelSim.Top = Height - 60;

            PanelUpdateRightAndBottom();

            btnSection1Man.Text = "1";
        }

        public void FieldMenuButtonEnableDisable(bool isOn)
        {
            SmoothABtoolStripMenu.Enabled = isOn;
            deleteContourPathsToolStripMenuItem.Enabled = isOn;
            boundaryToolToolStripMenu.Enabled = isOn;
            offsetFixToolStrip.Enabled = isOn;
            toolStripBtnFieldTools.Enabled = isOn;

            boundariesToolStripMenuItem.Enabled = isOn;
            headlandToolStripMenuItem.Enabled = isOn;
            headlandBuildToolStripMenuItem.Enabled = isOn;
            flagByLatLonToolStripMenuItem.Enabled = isOn;
            tramLinesMenuField.Enabled = isOn;
            tramsMultiMenuField.Enabled = isOn;
            recordedPathStripMenu.Enabled = isOn;
        }

        //take the distance from object and convert to camera data
        public void SetZoom()
        {
            //match grid to cam distance and redo perspective
            double gridStep = camera.camSetDistance / -15;

            gridToolSpacing = (int)(gridStep / tool.width + 0.5);
            if (gridToolSpacing < 1) gridToolSpacing = 1;
            worldGrid.FieldGrid.GridStep = gridToolSpacing * tool.width;

            oglMain.MakeCurrent();
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();
            Matrix4 mat = Matrix4.CreatePerspectiveFieldOfView((float)fovy, oglMain.AspectRatio, 1f, (float)(camDistanceFactor * camera.camSetDistance));
            GL.LoadMatrix(ref mat);
            GL.MatrixMode(MatrixMode.Modelview);
        }

        public void SendSettings()
        {
            //Form Steer Settings
            p_252.pgn[p_252.countsPerDegree] = unchecked((byte)Properties.Settings.Default.setAS_countsPerDegree);
            p_252.pgn[p_252.ackerman] = unchecked((byte)Properties.Settings.Default.setAS_ackerman);

            p_252.pgn[p_252.wasOffsetHi] = unchecked((byte)(Properties.Settings.Default.setAS_wasOffset >> 8));
            p_252.pgn[p_252.wasOffsetLo] = unchecked((byte)(Properties.Settings.Default.setAS_wasOffset));

            p_252.pgn[p_252.highPWM] = unchecked((byte)Properties.Settings.Default.setAS_highSteerPWM);
            p_252.pgn[p_252.lowPWM] = unchecked((byte)Properties.Settings.Default.setAS_lowSteerPWM);
            p_252.pgn[p_252.gainProportional] = unchecked((byte)Properties.Settings.Default.setAS_Kp);
            p_252.pgn[p_252.minPWM] = unchecked((byte)Properties.Settings.Default.setAS_minSteerPWM);

            SendPgnToLoop(p_252.pgn);

            //steer config
            p_251.pgn[p_251.set0] = Properties.Settings.Default.setArdSteer_setting0;
            p_251.pgn[p_251.set1] = Properties.Settings.Default.setArdSteer_setting1;
            p_251.pgn[p_251.maxPulse] = Properties.Settings.Default.setArdSteer_maxPulseCounts;
            p_251.pgn[p_251.minSpeed] = unchecked((byte)(Properties.Settings.Default.setAS_minSteerSpeed * 10));

            if (Properties.Settings.Default.setAS_isConstantContourOn)
                p_251.pgn[p_251.angVel] = 1;
            else p_251.pgn[p_251.angVel] = 0;

            SendPgnToLoop(p_251.pgn);

            //machine settings    
            p_238.pgn[p_238.set0] = Properties.Settings.Default.setArdMac_setting0;
            p_238.pgn[p_238.raiseTime] = Properties.Settings.Default.setArdMac_hydRaiseTime;
            p_238.pgn[p_238.lowerTime] = Properties.Settings.Default.setArdMac_hydLowerTime;

            p_238.pgn[p_238.user1] = Properties.Settings.Default.setArdMac_user1;
            p_238.pgn[p_238.user2] = Properties.Settings.Default.setArdMac_user2;
            p_238.pgn[p_238.user3] = Properties.Settings.Default.setArdMac_user3;
            p_238.pgn[p_238.user4] = Properties.Settings.Default.setArdMac_user4;

            SendPgnToLoop(p_238.pgn);
        }

        public void SendRelaySettingsToMachineModule()
        {
            string[] words = Properties.Settings.Default.setRelay_pinConfig.Split(',');

            //load the pgn
            p_236.pgn[p_236.pin0] = (byte)int.Parse(words[0]);
            p_236.pgn[p_236.pin1] = (byte)int.Parse(words[1]);
            p_236.pgn[p_236.pin2] = (byte)int.Parse(words[2]);
            p_236.pgn[p_236.pin3] = (byte)int.Parse(words[3]);
            p_236.pgn[p_236.pin4] = (byte)int.Parse(words[4]);
            p_236.pgn[p_236.pin5] = (byte)int.Parse(words[5]);
            p_236.pgn[p_236.pin6] = (byte)int.Parse(words[6]);
            p_236.pgn[p_236.pin7] = (byte)int.Parse(words[7]);
            p_236.pgn[p_236.pin8] = (byte)int.Parse(words[8]);
            p_236.pgn[p_236.pin9] = (byte)int.Parse(words[9]);

            p_236.pgn[p_236.pin10] = (byte)int.Parse(words[10]);
            p_236.pgn[p_236.pin11] = (byte)int.Parse(words[11]);
            p_236.pgn[p_236.pin12] = (byte)int.Parse(words[12]);
            p_236.pgn[p_236.pin13] = (byte)int.Parse(words[13]);
            p_236.pgn[p_236.pin14] = (byte)int.Parse(words[14]);
            p_236.pgn[p_236.pin15] = (byte)int.Parse(words[15]);
            p_236.pgn[p_236.pin16] = (byte)int.Parse(words[16]);
            p_236.pgn[p_236.pin17] = (byte)int.Parse(words[17]);
            p_236.pgn[p_236.pin18] = (byte)int.Parse(words[18]);
            p_236.pgn[p_236.pin19] = (byte)int.Parse(words[19]);

            p_236.pgn[p_236.pin20] = (byte)int.Parse(words[20]);
            p_236.pgn[p_236.pin21] = (byte)int.Parse(words[21]);
            p_236.pgn[p_236.pin22] = (byte)int.Parse(words[22]);
            p_236.pgn[p_236.pin23] = (byte)int.Parse(words[23]);
            SendPgnToLoop(p_236.pgn);


            p_235.pgn[p_235.sec0Lo] = unchecked((byte)(section[0].sectionWidth * 100));
            p_235.pgn[p_235.sec0Hi] = unchecked((byte)((int)((section[0].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec1Lo] = unchecked((byte)(section[1].sectionWidth * 100));
            p_235.pgn[p_235.sec1Hi] = unchecked((byte)((int)((section[1].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec2Lo] = unchecked((byte)(section[2].sectionWidth * 100));
            p_235.pgn[p_235.sec2Hi] = unchecked((byte)((int)((section[2].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec3Lo] = unchecked((byte)(section[3].sectionWidth * 100));
            p_235.pgn[p_235.sec3Hi] = unchecked((byte)((int)((section[3].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec4Lo] = unchecked((byte)(section[4].sectionWidth * 100));
            p_235.pgn[p_235.sec4Hi] = unchecked((byte)((int)((section[4].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec5Lo] = unchecked((byte)(section[5].sectionWidth * 100));
            p_235.pgn[p_235.sec5Hi] = unchecked((byte)((int)((section[5].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec6Lo] = unchecked((byte)(section[6].sectionWidth * 100));
            p_235.pgn[p_235.sec6Hi] = unchecked((byte)((int)((section[6].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec7Lo] = unchecked((byte)(section[7].sectionWidth * 100));
            p_235.pgn[p_235.sec7Hi] = unchecked((byte)((int)((section[7].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec8Lo] = unchecked((byte)(section[8].sectionWidth * 100));
            p_235.pgn[p_235.sec8Hi] = unchecked((byte)((int)((section[8].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec9Lo] = unchecked((byte)(section[9].sectionWidth * 100));
            p_235.pgn[p_235.sec9Hi] = unchecked((byte)((int)((section[9].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec10Lo] = unchecked((byte)(section[10].sectionWidth * 100));
            p_235.pgn[p_235.sec10Hi] = unchecked((byte)((int)((section[10].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec11Lo] = unchecked((byte)(section[11].sectionWidth * 100));
            p_235.pgn[p_235.sec11Hi] = unchecked((byte)((int)((section[11].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec12Lo] = unchecked((byte)(section[12].sectionWidth * 100));
            p_235.pgn[p_235.sec12Hi] = unchecked((byte)((int)((section[12].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec13Lo] = unchecked((byte)(section[13].sectionWidth * 100));
            p_235.pgn[p_235.sec13Hi] = unchecked((byte)((int)((section[13].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec14Lo] = unchecked((byte)(section[14].sectionWidth * 100));
            p_235.pgn[p_235.sec14Hi] = unchecked((byte)((int)((section[14].sectionWidth * 100)) >> 8));
            p_235.pgn[p_235.sec15Lo] = unchecked((byte)(section[15].sectionWidth * 100));
            p_235.pgn[p_235.sec15Hi] = unchecked((byte)((int)((section[15].sectionWidth * 100)) >> 8));

            p_235.pgn[p_235.numSections] = (byte)tool.numOfSections;

            SendPgnToLoop(p_235.pgn);
        }

        //message box pops up with info then goes away
        public void TimedMessageBox(int timeout, string s1, string s2)
        {
            FormTimedMessage form = new FormTimedMessage(timeout, s1, s2);
            form.Show(this);
            this.Activate();
        }

        public void YesMessageBox(string s1)
        {
            var form = new FormYes(s1);
            form.ShowDialog(this);
        }

        // AGROPARALLEL_MOD_START
        private void toolStripAgroParallel_Click(object sender, EventArgs e)
        {
            // Hub como widget Avalonia en modo float: ventana mediana
            // (1100x720) sobre AOG, con marco y X nativa. Permite seguir
            // viendo el piloto al lado. Si el exe Avalonia no esta presente,
            // cae al Hub WinForms clasico.
            if (!LaunchAvaloniaWidget(null, "float", "AgroParallel · Hub", 1100, 720))
                OpenAgroParallelHub();
        }

        // Atajo desde el menú "All" para abrir Cámaras como widget independiente.
        // Apunta a pages/camaras-widget.html (vista limpia: solo cámaras activas,
        // doble click = fullscreen una sola; sin sidebar/tabs/config).
        // Modo float = ventana chica encima del piloto, redimensionable, con X.
        // La config de cámaras se sigue editando dentro del Hub (camaras.html).
        private void toolStripCamaras_Click(object sender, EventArgs e)
        {
            if (!LaunchAvaloniaWidget("pages/camaras-widget.html", "float", "Cámaras", 640, 400))
                OpenAgroParallelHub("pages/camaras.html");
        }

        // Lanza AgroParallel.Shell.Avalonia.exe como proceso aparte. AOG sigue
        // siendo el motor (FormGPS) y el host AgpWebHost ya esta corriendo en
        // :5180 desde FormGPS_Load. El widget solo es un WebView2 + ventana.
        private bool LaunchAvaloniaWidget(string page, string mode = "full", string title = null,
                                          int width = 0, int height = 0)
        {
            try
            {
                string exe = ResolveAvaloniaWidgetExe();
                if (string.IsNullOrEmpty(exe) || !System.IO.File.Exists(exe)) return false;
                var sb = new System.Text.StringBuilder();
                if (!string.IsNullOrEmpty(page)) sb.Append("--page=").Append(page).Append(' ');
                if (!string.IsNullOrEmpty(mode)) sb.Append("--mode=").Append(mode).Append(' ');
                if (!string.IsNullOrEmpty(title)) sb.Append("--title=\"").Append(title).Append("\" ");
                if (width > 0) sb.Append("--width=").Append(width).Append(' ');
                if (height > 0) sb.Append("--height=").Append(height).Append(' ');
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false,
                    Arguments = sb.ToString().TrimEnd(),
                };
                System.Diagnostics.Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AvaloniaWidget] Launch: " + ex.Message);
                return false;
            }
        }

        private static string ResolveAvaloniaWidgetExe()
        {
            string baseDir = System.AppDomain.CurrentDomain.BaseDirectory;
            // 1) Path de produccion: junto al exe de AOG en /AvaloniaWidget/
            string prod = System.IO.Path.Combine(baseDir, "AvaloniaWidget", "AgroParallel.Shell.Avalonia.exe");
            if (System.IO.File.Exists(prod)) return prod;
            // 2) Path de dev: buscar bin del spike subiendo desde baseDir
            //    (AOG corre desde SourceCode/GPS/bin/Release/win-x64/ -> spike en
            //     SourceCode/AgroParallel/Spikes/AgroParallel.Shell.Avalonia/bin/<Cfg>/net9.0-windows/)
            try
            {
                var dir = new System.IO.DirectoryInfo(baseDir);
                for (int i = 0; i < 8 && dir != null; i++)
                {
                    string candidate = System.IO.Path.Combine(
                        dir.FullName,
                        "SourceCode", "AgroParallel", "Spikes", "AgroParallel.Shell.Avalonia",
                        "bin", "Debug", "net9.0-windows", "AgroParallel.Shell.Avalonia.exe");
                    if (System.IO.File.Exists(candidate)) return candidate;
                    candidate = candidate.Replace(@"\Debug\", @"\Release\");
                    if (System.IO.File.Exists(candidate)) return candidate;
                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }

        // Botón VX removido 2026-05-26 — vivía en panelControlBox y se solapaba
        // con la card de GPS. El toggle del overlay VistaX se hace desde el Hub
        // (/widgets.html) y además se auto-abre al activar un perfil de implemento
        // con nodos VistaX asignados (ver OverlayAutoOpener). ToggleVistaX() se
        // mantiene porque lo siguen llamando los CloseRequested de los overlays
        // HTML y el watcher de overlayPrefs.json.
        private void ToggleVistaX()
        {
            var cfg = VistaXConfig.Load();
            bool isOn = vistaXPanel != null || vistaXStripHtml != null || vistaXStatsHtml != null;

            if (isOn)
            {
                // VistaX está activo → desactivar (cualquiera de los dos modos).
                CleanupVistaX();

                cfg.Enabled = false;
                cfg.Save();

                System.Diagnostics.Debug.WriteLine("[VistaX] Desactivado por el usuario");
            }
            else
            {
                // VistaX está inactivo → activar
                cfg.Enabled = true;
                cfg.Save();

                InitVistaX();

                bool reactivado = vistaXPanel != null || vistaXStripHtml != null;
                if (!reactivado)
                {
                    // Falló la inicialización.
                    cfg.Enabled = false;
                    cfg.Save();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[VistaX] Activado por el usuario");
                }
            }
        }
        // AGROPARALLEL_MOD_END

        // ---- Overlay Prefs (controladas desde el Hub) -------------------------
        // El operario activa/desactiva los widgets de overlay (QuantiX shapefile-
        // Legend, VistaX, FlowX legend) desde el Hub (página hub.html). El
        // estado se persiste en overlayPrefs.json. Acá levantamos un timer chico
        // que relee el archivo cada 250 ms y, cuando detecta un cambio respecto
        // del último snapshot, dispara el redraw del widget correspondiente.
        // VistaX se reconcilia llamando ToggleVistaX() cuando el flag no
        // coincide con el estado real del panel.
        private bool _userWantsQXOverlay = true;
        private bool _userWantsFXOverlay = true;
        private bool _userWantsVXOverlay = true;
        private System.Windows.Forms.Timer _overlayPrefsTimer;

        public bool UserWantsQXOverlay => _userWantsQXOverlay;
        public bool UserWantsFXOverlay => _userWantsFXOverlay;
        public bool UserWantsVXOverlay => _userWantsVXOverlay;

        private void StartOverlayPrefsWatcher()
        {
            // Idempotente: si ya está, no duplicamos.
            if (_overlayPrefsTimer != null) return;
            // Snapshot inicial sin disparar redraws (los controles ya están en
            // su estado default; cuando el operario cambie en el Hub, recién
            // ahí reaccionamos).
            try
            {
                var p0 = AgroParallel.Services.OverlayPrefsService.Instance.Load();
                _userWantsQXOverlay = p0.QXOverlay;
                _userWantsFXOverlay = p0.FXOverlay;
                _userWantsVXOverlay = p0.VxOverlay;
            }
            catch { }

            _overlayPrefsTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _overlayPrefsTimer.Tick += (s, e) =>
            {
                try
                {
                    var p = AgroParallel.Services.OverlayPrefsService.Instance.Load();
                    if (p.QXOverlay != _userWantsQXOverlay)
                    {
                        _userWantsQXOverlay = p.QXOverlay;
                        try { UpdateShapefileLegendVisibility(); } catch { }
                    }
                    if (p.FXOverlay != _userWantsFXOverlay)
                    {
                        _userWantsFXOverlay = p.FXOverlay;
                        try { UpdateFlowXLegend(); } catch { }
                    }
                    if (p.VxOverlay != _userWantsVXOverlay)
                    {
                        _userWantsVXOverlay = p.VxOverlay;
                    }
                    // Reconciliación VistaX: el flag manda. Si el Hub quiere ON
                    // y el panel no está, lo prendemos; si quiere OFF y está,
                    // lo apagamos. Idempotente — solo togglea ante mismatch.
                    bool vxOn = vistaXPanel != null || vistaXStripHtml != null;
                    if (_userWantsVXOverlay && !vxOn)
                    {
                        try { ToggleVistaX(); } catch { }
                    }
                    else if (!_userWantsVXOverlay && vxOn)
                    {
                        try { ToggleVistaX(); } catch { }
                    }
                }
                catch { /* defaults: todo ON */ }
            };
            _overlayPrefsTimer.Start();
        }

        // VISTAX_MOD_START
        private void InitVistaX()
        {
            try
            {
                // Guard: no crear doble instancia (cualquiera de los dos caminos)
                if (vistaXPanel != null || vistaXStripHtml != null) return;

                var cfg = VistaXConfig.Load();
                System.Diagnostics.Debug.WriteLine("[VistaX] Config: Enabled=" + cfg.Enabled);

                if (!cfg.Enabled)
                {
                    System.Diagnostics.Debug.WriteLine("[VistaX] Deshabilitado");
                    return;
                }

                // Monitor MQTT nativo — siempre se crea: alimenta panel/strip y al logger.
                vistaXMonitor = new SeedMonitor(new AgroParallel.Adapters.FormGpsStateProvider(this), cfg);
                vistaXMonitor.AlarmTriggered += msg =>
                    System.Diagnostics.Debug.WriteLine("[VistaX] Alarma: " + msg);

                if (_useHtmlVxOverlay)
                {
                    // ---- Camino HTML (dos overlays WebView2) -----------------
                    InitVistaXHtmlOverlays(cfg);
                }
                else
                {
                    // ---- Legacy: panel nativo GDI+ ---------------------------
                    vistaXPanel = new VistaXNativePanel(cfg);
                    vistaXPanel.Visible = true;
                    vistaXPanel.AlarmMuted = cfg.AlarmMuted;
                    this.Controls.Add(vistaXPanel);
                    vistaXPanel.Reposition();

                    try
                    {
                        var prefs = AgroParallel.Services.OverlayPrefsService.Instance.Load();
                        if (prefs.VxX >= 0 && prefs.VxY >= 0)
                        {
                            vistaXPanel.HasCustomPosition = true;
                            vistaXPanel.Location = new System.Drawing.Point(prefs.VxX, prefs.VxY);
                        }
                    }
                    catch { }

                    AgroParallel.Common.OverlayDragger.Attach(vistaXPanel, pt =>
                    {
                        try
                        {
                            vistaXPanel.HasCustomPosition = true;
                            var p = AgroParallel.Services.OverlayPrefsService.Instance.Load();
                            p.VxX = pt.X; p.VxY = pt.Y;
                            AgroParallel.Services.OverlayPrefsService.Instance.Save(p);
                        }
                        catch { }
                    });

                    // Hook del snapshot al panel nativo.
                    vistaXMonitor.SnapshotUpdated += vistaXPanel.SetSnapshot;

                    // Edición de objetivo + config + mute desde el header nativo.
                    var monitorCapture = vistaXMonitor;
                    vistaXPanel.ObjetivoChanged += v => monitorCapture.SetObjetivo(v, 0);
                    vistaXPanel.ConfigRequested += OpenVistaXConfigDialog;
                    var cfgCapture = cfg;
                    vistaXPanel.SensorMuteToggleRequested += (uid, cable, muted) =>
                    {
                        int hits = monitorCapture.SetSensorMuted(uid, cable, muted);
                        if (hits > 0 && monitorCapture.Implemento != null)
                            cfgCapture.SaveImplemento(monitorCapture.Implemento);
                    };
                }

                // Logger NDJSON — graba snapshots a disco y exporta SHP al detener.
                if (cfg.LogToFieldRecord)
                {
                    vistaXLogger = new VistaXFieldLogger(new AgroParallel.Adapters.FormGpsStateProvider(this), cfg);
                    vistaXLogger.Start(); // Arrancar inmediatamente.
                    System.Diagnostics.Debug.WriteLine("[VistaX-Log] Logger creado, IsLogging=" + vistaXLogger.IsLogging
                        + ", Path=" + (vistaXLogger.CurrentLogPath ?? "null"));

                    vistaXMonitor.SnapshotUpdated += snap =>
                    {
                        if (snap == null) return;
                        var logger = vistaXLogger;
                        if (logger == null) return;

                        // Si no está logueando (falló el Start), reintentar.
                        if (!logger.IsLogging) logger.Start();

                        logger.WriteSnapshot(snap);
                    };
                }

                // Fire-and-forget: StartAsync maneja sus propios errores de MQTT.
                _ = vistaXMonitor.StartAsync();

                // FileSystemWatcher sobre el JSON del implemento — si la UI
                // web (PUT /api/vistax/implemento) reescribe el archivo,
                // recargamos el mapeo de sensores en caliente. El evento llega
                // en un thread de IO, lo debouncamos y marshall-eamos al UI
                // thread para evitar reentrancia sobre _surcos.
                try
                {
                    string implPath = cfg.ImplementoJsonPath;
                    if (!string.IsNullOrEmpty(implPath) && System.IO.File.Exists(implPath))
                    {
                        string dir = System.IO.Path.GetDirectoryName(implPath);
                        string file = System.IO.Path.GetFileName(implPath);
                        if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(file))
                        {
                            vistaXImplDebounce = new System.Windows.Forms.Timer { Interval = 250 };
                            vistaXImplDebounce.Tick += (s, e) =>
                            {
                                vistaXImplDebounce.Stop();
                                try { vistaXMonitor?.ReloadImplemento(); } catch { }
                            };

                            vistaXImplWatcher = new System.IO.FileSystemWatcher(dir, file)
                            {
                                NotifyFilter = System.IO.NotifyFilters.LastWrite
                                             | System.IO.NotifyFilters.Size
                                             | System.IO.NotifyFilters.CreationTime,
                                EnableRaisingEvents = true
                            };
                            System.IO.FileSystemEventHandler bounce = (s, ev) =>
                            {
                                try
                                {
                                    if (this.IsHandleCreated && !this.IsDisposed)
                                    {
                                        this.BeginInvoke((Action)(() =>
                                        {
                                            vistaXImplDebounce.Stop();
                                            vistaXImplDebounce.Start();
                                        }));
                                    }
                                }
                                catch { }
                            };
                            vistaXImplWatcher.Changed += bounce;
                            vistaXImplWatcher.Created += bounce;
                        }
                    }
                }
                catch (Exception wex)
                {
                    System.Diagnostics.Debug.WriteLine("[VistaX] Watcher implemento.json no instalado: " + wex.Message);
                }

                // Reposicionar al redimensionar — native panel y/o HTML overlays.
                this.Resize += (s, e) =>
                {
                    if (vistaXPanel != null) vistaXPanel.Reposition();
                    RepositionVistaXHtmlOverlays();
                };

                System.Diagnostics.Debug.WriteLine("[VistaX] Iniciado OK (modo "
                    + (_useHtmlVxOverlay ? "HTML" : "nativo") + ")");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX] ERROR: " + ex.ToString());
            }
        }

        private void CleanupVistaX()
        {
            if (vistaXPanel != null)
            {
                // Persistir el estado del mute antes de disponer.
                try
                {
                    if (vistaXConfig != null && vistaXConfig.AlarmMuted != vistaXPanel.AlarmMuted)
                    {
                        vistaXConfig.AlarmMuted = vistaXPanel.AlarmMuted;
                        vistaXConfig.Save();
                    }
                }
                catch { }
                try { vistaXPanel.Dispose(); } catch { }
                vistaXPanel = null;
            }
            if (_vxHtmlWaitTimer != null)
            {
                try { _vxHtmlWaitTimer.Stop(); } catch { }
                try { _vxHtmlWaitTimer.Dispose(); } catch { }
                _vxHtmlWaitTimer = null;
                _vxHtmlPendingCfg = null;
            }
            if (vistaXStripHtml != null)
            {
                try { this.Controls.Remove(vistaXStripHtml); } catch { }
                try { vistaXStripHtml.Dispose(); } catch { }
                vistaXStripHtml = null;
            }
            if (vistaXStatsHtml != null)
            {
                try { this.Controls.Remove(vistaXStatsHtml); } catch { }
                try { vistaXStatsHtml.Dispose(); } catch { }
                vistaXStatsHtml = null;
            }
            if (vistaXLogger != null)
            {
                try { vistaXLogger.Dispose(); } catch { }
                vistaXLogger = null;
            }
            if (vistaXImplWatcher != null)
            {
                try { vistaXImplWatcher.EnableRaisingEvents = false; } catch { }
                try { vistaXImplWatcher.Dispose(); } catch { }
                vistaXImplWatcher = null;
            }
            if (vistaXImplDebounce != null)
            {
                try { vistaXImplDebounce.Stop(); } catch { }
                try { vistaXImplDebounce.Dispose(); } catch { }
                vistaXImplDebounce = null;
            }
            if (vistaXMonitor != null)
            {
                // Stop+Dispose en background con timeout: si MQTT esta en un
                // backoff de reconexion, StopAsync puede bloquear el hilo UI
                // y congelar el Close/Toggle/Config.
                var m = vistaXMonitor;
                vistaXMonitor = null;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { m.StopAsync().Wait(2000); } catch { }
                    try { m.Dispose(); } catch { }
                });
            }
        }

        // -------------------------------------------------------------------------
        // Overlays HTML de VistaX — strip inferior + ventana stats superior.
        // Reposiciona y persiste posiciones por separado en OverlayPrefsService.
        // Datos vienen del WebHost (/api/vistax/live) — los overlays son
        // 100% read-only; no necesitan callbacks al monitor para funcionar.
        // -------------------------------------------------------------------------
        private System.Windows.Forms.Timer _vxHtmlWaitTimer;
        private VistaXConfig _vxHtmlPendingCfg;

        private void InitVistaXHtmlOverlays(VistaXConfig cfg)
        {
            string baseUrl = AgroParallel.Shell.AgpWebHostBootstrap.Url;
            if (string.IsNullOrEmpty(baseUrl))
            {
                // AgpWebHost aún no levantado (InitVistaX corre antes que
                // EnsureStarted en FormGPS_Load). Reintentamos a 500 ms hasta
                // que la URL esté lista — máximo 30 s. Idempotente.
                _vxHtmlPendingCfg = cfg;
                if (_vxHtmlWaitTimer == null)
                {
                    int ticks = 0;
                    _vxHtmlWaitTimer = new System.Windows.Forms.Timer { Interval = 500 };
                    _vxHtmlWaitTimer.Tick += (s, e) =>
                    {
                        ticks++;
                        var url = AgroParallel.Shell.AgpWebHostBootstrap.Url;
                        if (!string.IsNullOrEmpty(url))
                        {
                            _vxHtmlWaitTimer.Stop();
                            _vxHtmlWaitTimer.Dispose();
                            _vxHtmlWaitTimer = null;
                            // Reintentar solo si todavía no hay overlays (no se
                            // hizo Toggle off en el medio) y monitor sigue vivo.
                            var pendingCfg = _vxHtmlPendingCfg;
                            _vxHtmlPendingCfg = null;
                            if (pendingCfg != null && vistaXMonitor != null
                                && vistaXStripHtml == null && vistaXStatsHtml == null)
                            {
                                InitVistaXHtmlOverlays(pendingCfg);
                            }
                        }
                        else if (ticks > 60)
                        {
                            _vxHtmlWaitTimer.Stop();
                            _vxHtmlWaitTimer.Dispose();
                            _vxHtmlWaitTimer = null;
                            System.Diagnostics.Debug.WriteLine("[VistaX-HTML] timeout esperando WebHost");
                        }
                    };
                    _vxHtmlWaitTimer.Start();
                }
                System.Diagnostics.Debug.WriteLine("[VistaX-HTML] WebHost no levantado, reintentando…");
                return;
            }

            var prefs = AgroParallel.Services.OverlayPrefsService.Instance.Load();

            // Cantidad de sensores primarios (semilla / fertilizante) en el
            // implemento — define el ancho natural del strip.
            int primaryCount = CountPrimaryVxSensors(cfg);

            // ---- Tamaños default dinámicos en base a ClientSize + sensores ----
            // Strip: ancho = barra status (80) + chips (count * 44) + KPIs (210),
            //        clamp [380..ClientSize.Width-32]. Altura ~ 64 px (incluye drag bar).
            // Stats: ancho ~18% del mapa (clamp 240..320), alto ~38% (clamp 260..440).
            int stripContentW = 80 + Math.Max(1, primaryCount) * 44 + 210;
            int defaultStripW = Clamp(stripContentW, 380, Math.Max(380, this.ClientSize.Width - 32));
            int defaultStripH = Clamp((int)(this.ClientSize.Height * 0.07), 60, 96);
            int defaultStatsW = Clamp((int)(this.ClientSize.Width * 0.18), 240, 320);
            int defaultStatsH = Clamp((int)(this.ClientSize.Height * 0.38), 260, 440);

            int stripW = prefs.VxStripW > 0 ? prefs.VxStripW : defaultStripW;
            int stripH = prefs.VxStripH > 0 ? prefs.VxStripH : defaultStripH;

            // ---- Strip (vistax-live.html) ----
            vistaXStripHtml = new AgroParallel.Common.VistaXWebOverlayPanel(
                baseUrl, "pages/vistax-live.html",
                new System.Drawing.Size(stripW, stripH),
                new System.Drawing.Size(360, 50),
                "VistaX · monitor");
            bool stripCustom = prefs.VxStripX >= 0 && prefs.VxStripY >= 0;
            // Sin stretch: anclamos a Bottom|Left para que NO ocupe todo el ancho.
            vistaXStripHtml.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            this.Controls.Add(vistaXStripHtml);
            vistaXStripHtml.BringToFront();

            // ---- Stats (vistax-stats.html) ----
            int statsW = prefs.VxStatsW > 0 ? prefs.VxStatsW : defaultStatsW;
            int statsH = prefs.VxStatsH > 0 ? prefs.VxStatsH : defaultStatsH;
            vistaXStatsHtml = new AgroParallel.Common.VistaXWebOverlayPanel(
                baseUrl, "pages/vistax-stats.html",
                new System.Drawing.Size(statsW, statsH),
                new System.Drawing.Size(220, 200),
                "VistaX · stats");
            bool statsCustom = prefs.VxStatsX >= 0 && prefs.VxStatsY >= 0;
            vistaXStatsHtml.Anchor = statsCustom
                ? (AnchorStyles.Top | AnchorStyles.Left)
                : (AnchorStyles.Top | AnchorStyles.Right);
            this.Controls.Add(vistaXStatsHtml);
            vistaXStatsHtml.BringToFront();

            // Posicionamiento inicial (default o restaurado).
            RepositionVistaXHtmlOverlays();
            if (stripCustom)
                vistaXStripHtml.Location = new System.Drawing.Point(prefs.VxStripX, prefs.VxStripY);
            if (statsCustom)
                vistaXStatsHtml.Location = new System.Drawing.Point(prefs.VxStatsX, prefs.VxStatsY);

            // Drag bar — el WebView2 se come los mouse events, por eso atachamos
            // OverlayDragger SOLO al DragHandle (barra superior 22 px).
            AgroParallel.Common.OverlayDragger.Attach(vistaXStripHtml.DragHandle, pt =>
            {
                try
                {
                    if (vistaXStripHtml == null) return;
                    // DragHandle se mueve dentro del UserControl; mapeamos al padre.
                    var p = AgroParallel.Services.OverlayPrefsService.Instance.Load();
                    p.VxStripX = vistaXStripHtml.Left;
                    p.VxStripY = vistaXStripHtml.Top;
                    p.VxStripW = vistaXStripHtml.Width;
                    p.VxStripH = vistaXStripHtml.Height;
                    AgroParallel.Services.OverlayPrefsService.Instance.Save(p);
                }
                catch { }
            }, vistaXStripHtml);
            AgroParallel.Common.OverlayDragger.Attach(vistaXStatsHtml.DragHandle, pt =>
            {
                try
                {
                    if (vistaXStatsHtml == null) return;
                    var p = AgroParallel.Services.OverlayPrefsService.Instance.Load();
                    p.VxStatsX = vistaXStatsHtml.Left;
                    p.VxStatsY = vistaXStatsHtml.Top;
                    p.VxStatsW = vistaXStatsHtml.Width;
                    p.VxStatsH = vistaXStatsHtml.Height;
                    AgroParallel.Services.OverlayPrefsService.Instance.Save(p);
                }
                catch { }
            }, vistaXStatsHtml);

            // Close: oculta y persiste el toggle.
            vistaXStripHtml.CloseRequested += () => ToggleVistaX();
            vistaXStatsHtml.CloseRequested  += () => ToggleVistaX();

            // Resize grip → persist W/H (X/Y se mantienen).
            vistaXStripHtml.ResizedByUser += sz =>
            {
                try
                {
                    var p = AgroParallel.Services.OverlayPrefsService.Instance.Load();
                    p.VxStripW = sz.Width;
                    p.VxStripH = sz.Height;
                    // Si no estaba marcado como custom (posición default),
                    // ahora congelamos la posición actual también — al
                    // resizear el bottom-stretch dejaría de tener sentido.
                    if (p.VxStripX < 0 || p.VxStripY < 0)
                    {
                        p.VxStripX = vistaXStripHtml.Left;
                        p.VxStripY = vistaXStripHtml.Top;
                    }
                    AgroParallel.Services.OverlayPrefsService.Instance.Save(p);
                }
                catch { }
            };
            vistaXStatsHtml.ResizedByUser += sz =>
            {
                try
                {
                    var p = AgroParallel.Services.OverlayPrefsService.Instance.Load();
                    p.VxStatsW = sz.Width;
                    p.VxStatsH = sz.Height;
                    if (p.VxStatsX < 0 || p.VxStatsY < 0)
                    {
                        p.VxStatsX = vistaXStatsHtml.Left;
                        p.VxStatsY = vistaXStatsHtml.Top;
                    }
                    AgroParallel.Services.OverlayPrefsService.Instance.Save(p);
                }
                catch { }
            };
        }

        private static int Clamp(int v, int min, int max)
            => v < min ? min : (v > max ? max : v);

        // Cuenta sensores de tipo "semilla" + "fertilizante" en el implemento
        // configurado — son los que el strip de monitoreo dibuja como chips.
        // Si no hay implemento todavía, devuelve 8 (fallback razonable).
        private static int CountPrimaryVxSensors(AgroParallel.VistaX.VistaXConfig cfg)
        {
            try
            {
                var imp = cfg?.LoadImplemento();
                if (imp?.MapeoSensores == null) return 8;
                int n = 0;
                foreach (var s in imp.MapeoSensores)
                {
                    var t = (s?.Tipo ?? string.Empty).ToLowerInvariant();
                    if (t == "semilla" || t == "fertilizante") n++;
                }
                return n > 0 ? n : 8;
            }
            catch { return 8; }
        }

        private void RepositionVistaXHtmlOverlays()
        {
            var prefs = AgroParallel.Services.OverlayPrefsService.Instance.Load();

            // Strip: si el usuario no fijó posición custom, lo ubicamos centrado
            // en el borde inferior del mapa con su ancho natural (no stretch).
            if (vistaXStripHtml != null && (prefs.VxStripX < 0 || prefs.VxStripY < 0))
            {
                int w = vistaXStripHtml.Width;
                int h = vistaXStripHtml.Height;
                int x = Math.Max(8, (this.ClientSize.Width - w) / 2);
                int y = Math.Max(8, this.ClientSize.Height - h - 36);
                vistaXStripHtml.Location = new System.Drawing.Point(x, y);
            }

            // Stats: si no hay posición custom, top-right con anchor.
            if (vistaXStatsHtml != null
                && (vistaXStatsHtml.Anchor & AnchorStyles.Right) == AnchorStyles.Right
                && (vistaXStatsHtml.Anchor & AnchorStyles.Left) != AnchorStyles.Left)
            {
                int w = prefs.VxStatsW > 0
                    ? prefs.VxStatsW
                    : Clamp((int)(this.ClientSize.Width * 0.18), 240, 320);
                int hh = prefs.VxStatsH > 0
                    ? prefs.VxStatsH
                    : Clamp((int)(this.ClientSize.Height * 0.38), 260, 440);
                vistaXStatsHtml.Width = w;
                vistaXStatsHtml.Height = hh;
                int x = this.ClientSize.Width - w - 8;
                int y = 80;
                vistaXStatsHtml.Location = new System.Drawing.Point(Math.Max(0, x), y);
            }
        }
        // VISTAX_MOD_END

        // AGROPARALLEL_MOD_START
        // Lee agroParallelModules.json del directorio base y agrega un item en el
        // dropdown de configuracion por cada modulo habilitado. Cada item abre una
        // ventana popup (ModulePopupForm) que embebe CefSharp apuntando a su Url.
        private void InitAgroParallelModulesMenu()
        {
            // Asignar logo al botón del menú.
            var logo = AgroParallel.Common.Theme.Logo;
            if (logo != null)
            {
                toolStripAgroParallel.Image = new System.Drawing.Bitmap(logo, 28, 28);
                toolStripAgroParallel.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
                toolStripAgroParallel.Text = "PilotX";
                toolStripAgroParallel.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.ImageAndText;
                toolStripAgroParallel.ToolTipText = "PilotX · Agro Parallel";
            }

        }

        public void OpenVistaXPerfilesDialog()
        {
            try
            {
                if (vistaXConfig == null) vistaXConfig = VistaXConfig.Load();

                string newPath = null;
                using (var dlg = new FormVistaXPerfiles(vistaXConfig))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK
                        && !string.IsNullOrEmpty(dlg.ActivatedPath))
                    {
                        newPath = dlg.ActivatedPath;
                    }
                }

                if (!string.IsNullOrEmpty(newPath))
                {
                    vistaXConfig.ImplementoJsonPath = newPath;
                    vistaXConfig.Save();

                    if (vistaXPopupForm != null && !vistaXPopupForm.IsDisposed)
                    {
                        try { vistaXPopupForm.Close(); } catch { }
                        vistaXPopupForm = null;
                    }
                    CleanupVistaX();
                    vistaXConfig = VistaXConfig.Load();
                    InitVistaX();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX] OpenPerfilesDialog: " + ex.Message);
            }
        }

        public void OpenVistaXNodosDialog()
        {
            try
            {
                if (vistaXConfig == null) vistaXConfig = VistaXConfig.Load();

                using (var dlg = new FormVistaXNodos(vistaXConfig, vistaXMonitor))
                {
                    dlg.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX] OpenNodosDialog: " + ex.Message);
            }
        }

        public void OpenVistaXTrenesDialog()
        {
            try
            {
                if (vistaXConfig == null) vistaXConfig = VistaXConfig.Load();

                bool changed = false;
                using (var dlg = new FormVistaXTrenes(vistaXConfig))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Changed)
                        changed = true;
                }

                if (changed)
                {
                    if (vistaXPopupForm != null && !vistaXPopupForm.IsDisposed)
                    {
                        try { vistaXPopupForm.Close(); } catch { }
                        vistaXPopupForm = null;
                    }
                    CleanupVistaX();
                    vistaXConfig = VistaXConfig.Load();
                    InitVistaX();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX] OpenTrenesDialog: " + ex.Message);
            }
        }

        public void OpenVistaXSensoresDialog()
        {
            try
            {
                if (vistaXConfig == null) vistaXConfig = VistaXConfig.Load();

                bool changed = false;
                using (var dlg = new FormVistaXSensores(vistaXConfig))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Changed)
                        changed = true;
                }

                if (changed)
                {
                    if (vistaXPopupForm != null && !vistaXPopupForm.IsDisposed)
                    {
                        try { vistaXPopupForm.Close(); } catch { }
                        vistaXPopupForm = null;
                    }
                    CleanupVistaX();
                    vistaXConfig = VistaXConfig.Load();
                    InitVistaX();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX] OpenSensoresDialog: " + ex.Message);
            }
        }

        public void OpenVistaXMapeoDialog()
        {
            try
            {
                if (vistaXConfig == null) vistaXConfig = VistaXConfig.Load();
                using (var dlg = new FormVistaXMapeo(vistaXConfig))
                {
                    dlg.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX] OpenMapeoDialog: " + ex.Message);
            }
        }

        public void OpenVistaXSonidosDialog()
        {
            try
            {
                if (vistaXConfig == null) vistaXConfig = VistaXConfig.Load();
                using (var dlg = new FormVistaXSonidos(vistaXConfig))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        vistaXConfig = VistaXConfig.Load();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX] OpenSonidosDialog: " + ex.Message);
            }
        }

        public void OpenVistaXPruebaDialog()
        {
            try
            {
                if (vistaXConfig == null) vistaXConfig = VistaXConfig.Load();
                var dlg = new FormVistaXPrueba(vistaXConfig);
                dlg.Show();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX] OpenPruebaDialog: " + ex.Message);
            }
        }

        public void OpenVistaXSimulatorDialog()
        {
            try
            {
                if (vistaXConfig == null) vistaXConfig = VistaXConfig.Load();
                var dlg = new FormVistaXSimulator(vistaXConfig);
                dlg.Show();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX] OpenSimulatorDialog: " + ex.Message);
            }
        }

        private System.Windows.Forms.Form _hubFormWeb;

        public void OpenAgroParallelHub() { OpenAgroParallelHub(null); }

        public void OpenAgroParallelHub(string initialPage)
        {
            try
            {
                if (_hubFormWeb != null && !_hubFormWeb.IsDisposed)
                {
                    // Si la ventana ya está abierta y nos piden una página
                    // específica, navegamos vía el WebView2 (no relanzamos el host).
                    if (!string.IsNullOrEmpty(initialPage)
                        && _hubFormWeb is global::AgroParallel.Shell.FormAgroParallelHubWebView2 existing)
                    {
                        try
                        {
                            existing.InitialPage = initialPage; // por las dudas si recarga
                            // Re-navegar el WebView2 al target.
                            var fld = existing.GetType().GetField("_webView",
                                System.Reflection.BindingFlags.Instance |
                                System.Reflection.BindingFlags.NonPublic);
                            var hostFld = existing.GetType().GetField("_webHost",
                                System.Reflection.BindingFlags.Instance |
                                System.Reflection.BindingFlags.NonPublic);
                            if (fld != null && hostFld != null)
                            {
                                var wv = fld.GetValue(existing) as Microsoft.Web.WebView2.WinForms.WebView2;
                                var host = hostFld.GetValue(existing) as global::AgroParallel.WebHost.AgpWebHost;
                                if (wv?.CoreWebView2 != null && host != null)
                                {
                                    string url = host.Url.TrimEnd('/') + "/" + initialPage.TrimStart('/');
                                    wv.CoreWebView2.Navigate(url);
                                }
                            }
                        }
                        catch { /* fallback: solo traer al frente */ }
                    }
                    _hubFormWeb.BringToFront();
                    return;
                }

                string wwwroot = System.IO.Path.Combine(
                    System.AppDomain.CurrentDomain.BaseDirectory,
                    "AgroParallel", "wwwroot");

                var state = new global::AgroParallel.Adapters.FormGpsStateProvider(this);
                var lotes = new global::AgroParallel.Adapters.FormGpsLotesService(this);
                var vehicleTool = new global::AgroParallel.Adapters.FormGpsVehicleToolService(this);
                var shapefile = new global::AgroParallel.Adapters.FormGpsShapefileService(this);
                // Core/view split (adapter scaffolding): AOG sigue siendo el motor;
                // los HTML leen estos servicios via REST en lugar de FormGPS directo.
                var coverage = new global::AgroParallel.Adapters.FormGpsCoverageService(this);
                var sectionsCore = new global::AgroParallel.Adapters.FormGpsSectionControlService(this);
                var quantixRuntime = new global::AgroParallel.Adapters.FormGpsQuantiXRuntimeService(this, state);
                var guidance = new global::AgroParallel.Adapters.FormGpsGuidanceCalculator(this);
                var pilotxUpdate = new global::AgroParallel.Adapters.FormGpsPilotXUpdateService();
                var hub = new global::AgroParallel.Shell.FormAgroParallelHubWebView2(
                    state, lotes, vehicleTool, shapefile,
                    coverage, sectionsCore, quantixRuntime, guidance,
                    pilotxUpdate,
                    wwwroot);
                hub.InitialPage = initialPage; // null = welcome
                // El Hub se overlay sobre el GLControl del mapa (oglMain) en lugar
                // de ir fullscreen. Sigue al control si AOG se redimensiona o mueve.
                hub.AnchorControl = this.oglMain;
                _hubFormWeb = hub;
                _hubFormWeb.FormClosed += (s, e) => { _hubFormWeb = null; };
                _hubFormWeb.Show(this);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AgroParallel] OpenHub: " + ex.Message);
            }
        }

        public void OpenVistaXConfigDialog()
        {
            try
            {
                if (vistaXConfig == null) vistaXConfig = VistaXConfig.Load();

                using (var dlg = new FormVistaXConfig(vistaXConfig))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                }

                // Reinicia con la config nueva: cierra popup si esta abierto,
                // para y dispone el monitor/panel, y vuelve a inicializar.
                if (vistaXPopupForm != null && !vistaXPopupForm.IsDisposed)
                {
                    try { vistaXPopupForm.Close(); } catch { }
                    vistaXPopupForm = null;
                }
                CleanupVistaX();
                vistaXConfig = VistaXConfig.Load();
                InitVistaX();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX] OpenConfigDialog: " + ex.Message);
            }
        }

        private void OpenVistaXNativePopup()
        {
            if (vistaXPopupForm != null && !vistaXPopupForm.IsDisposed)
            {
                if (vistaXPopupForm.WindowState == FormWindowState.Minimized)
                    vistaXPopupForm.WindowState = FormWindowState.Normal;
                vistaXPopupForm.BringToFront();
                vistaXPopupForm.Activate();
                return;
            }

            var cfg = vistaXConfig ?? VistaXConfig.Load();
            vistaXPopupForm = new FormVistaXPopup(cfg, vistaXMonitor);
            vistaXPopupForm.FormClosed += (s, e) => vistaXPopupForm = null;
            vistaXPopupForm.Show(this);
        }
        public void ReloadSectionXBridge()
        {
            try
            {
                if (sectionXBridge != null) { sectionXBridge.Dispose(); sectionXBridge = null; }
                var cfg = SectionXConfig.Load();
                if (cfg.Enabled && cfg.Nodos.Count > 0)
                {
                    sectionXBridge = new SectionXBridge(new AgroParallel.Adapters.FormGpsStateProvider(this), cfg);
                    _ = sectionXBridge.StartAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SectionX] ReloadBridge: " + ex.Message);
            }
        }

        public void ReloadOrbitXSync()
        {
            try
            {
                if (orbitXSync != null) { orbitXSync.Stop(); orbitXSync = null; }
                var cfg = OrbitXConfig.Load();
                if (cfg.Enabled && !string.IsNullOrEmpty(cfg.DeviceToken))
                {
                    orbitXSync = new OrbitXSync(new AgroParallel.Adapters.FormGpsStateProvider(this), cfg);
                    orbitXSync.Start();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[OrbitX] ReloadSync: " + ex.Message);
            }
        }

        // CAMARAS_MOD_START
        public void ReloadCamarasRelay()
        {
            try
            {
                if (camarasRelay != null) { camarasRelay.Dispose(); camarasRelay = null; }
                var cfg = OrbitXConfig.Load();
                if (cfg.Enabled && !string.IsNullOrEmpty(cfg.DeviceToken))
                {
                    camarasRelay = new CamarasRemoteRelay(cfg);
                    camarasRelay.Start();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Camaras] ReloadRelay: " + ex.Message);
            }
        }
        // CAMARAS_MOD_END

        // AGROPARALLEL_MOD_END

        // SHAPEFILE_MOD_START
        // Agrega "Cargar Shapefile" al dropdown de configuracion. El handler abre
        // un OpenFileDialog, parsea con ShapefileReader y muestra un resumen.
        // Sin render todavia — eso es el paso siguiente.
        private void InitShapefileMenu()
        {
            try
            {
                // Shapefile + QuantiX — movidos al Hub.
                // Solo inicializar la leyenda embebida en el mapa.
                InitShapefileLegend();
                InitFlowXLegend();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shapefile] InitShapefileMenu: "
                    + ex.Message);
            }
        }

        public void OpenShapefileLoadDialog()
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "Cargar Shapefile / GeoJSON";
                ofd.Filter = "Shapefile (*.shp)|*.shp|GeoJSON (*.geojson;*.json)|*.geojson;*.json|Todos (*.*)|*.*";

                string initial = RegistrySettings.fieldsDirectory;
                if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial))
                    ofd.InitialDirectory = initial;

                if (ofd.ShowDialog(this) != DialogResult.OK) return;

                LoadShapefileFromPath(ofd.FileName, showSummary: true, promptStyle: true);
            }
        }

        // Carga un shapefile desde un path dado. Si promptStyle=true abre el
        // dialogo de estilo tras la carga; si ya hay StyleField guardado se aplica
        // en silencio (auto-load). Persiste el estado al final.
        private bool LoadShapefileFromPath(string shpPath, bool showSummary, bool promptStyle,
            string autoStyleField = null, bool autoVisible = true, bool autoShowFill = true,
            bool autoShowOutline = true)
        {
            ShapefileReadResult result;
            Cursor oldCursor = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                string ext = Path.GetExtension(shpPath).ToLowerInvariant();
                if (ext == ".geojson" || ext == ".json")
                    result = ShapefileReader.ReadGeoJson(shpPath);
                else
                    result = ShapefileReader.ReadPolygons(shpPath);
            }
            catch (Exception ex)
            {
                Cursor.Current = oldCursor;
                System.Diagnostics.Debug.WriteLine("[Shapefile] Error leyendo "
                    + shpPath + ": " + ex);
                if (showSummary)
                {
                    MessageBox.Show(this,
                        "Error leyendo shapefile:\n\n" + ex.Message,
                        "Shapefile",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return false;
            }
            Cursor.Current = oldCursor;

            if (showSummary)
                ShowShapefileSummary(Path.GetFileName(shpPath), result);

            if (!isJobStarted)
            {
                if (showSummary)
                {
                    MessageBox.Show(this,
                        "No hay un campo abierto. Abri o crea un campo antes de cargar un "
                        + "shapefile para poder dibujarlo sobre el mapa.",
                        "Shapefile", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return false;
            }

            shapefileLayer = new ShapefileLayer(result, Path.GetFileName(shpPath));
            shapefileLayer.SourceFullPath = shpPath;
            shapefileLayer.ShowFill = autoShowFill;
            shapefileLayer.ShowOutline = autoShowOutline;
            shapefileLayer.IsVisible = autoVisible;

            if (shapefileToggleItem != null)
            {
                shapefileToggleItem.Enabled = !shapefileLayer.IsEmpty;
                shapefileToggleItem.Checked = autoVisible;
            }
            if (shapefileStyleItem != null)
            {
                // El estilo por DBF solo aplica al fill de poligonos.
                shapefileStyleItem.Enabled = shapefileLayer.PolygonCount > 0
                    && shapefileLayer.FieldNames.Count > 0;
            }

            // Auto-aplicar estilo si viene de persistencia.
            if (!string.IsNullOrEmpty(autoStyleField))
                shapefileLayer.ApplyColorByField(autoStyleField);

            // Dialogo de estilo solo en carga manual.
            if (promptStyle
                && shapefileLayer.PolygonCount > 0
                && shapefileLayer.FieldNames.Count > 0)
            {
                OpenShapefileStyleDialog();
            }

            RefreshShapefileLegend();
            SaveShapefileState();
            return true;
        }

        public void OpenShapefileStyleDialog()
        {
            // Estilo de shapefile ahora se configura desde la UI HTML (Piloto).
            // El diálogo WinForms legacy fue eliminado en la migración a WebView2.
            if (shapefileLayer == null || shapefileLayer.PolygonCount == 0) return;
            RefreshShapefileLegend();
            SaveShapefileState();
        }

        private string GetCurrentFieldFullPath()
        {
            if (string.IsNullOrEmpty(currentFieldDirectory)) return null;
            if (string.IsNullOrEmpty(RegistrySettings.fieldsDirectory)) return null;
            return Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory);
        }

        private void SaveShapefileState()
        {
            try
            {
                string fieldPath = GetCurrentFieldFullPath();
                if (string.IsNullOrEmpty(fieldPath)) return;
                if (!Directory.Exists(fieldPath)) return;

                if (shapefileLayer == null || string.IsNullOrEmpty(shapefileLayer.SourceFullPath))
                {
                    // Nada que persistir — si habia un archivo previo, borrarlo.
                    ShapefilePersistence.Delete(fieldPath);
                    return;
                }

                var cfg = new ShapefileFieldConfig
                {
                    ShpPath = shapefileLayer.SourceFullPath,
                    StyleField = shapefileLayer.StyleField,
                    Visible = shapefileLayer.IsVisible,
                    ShowFill = shapefileLayer.ShowFill,
                    ShowOutline = shapefileLayer.ShowOutline
                };
                ShapefilePersistence.Save(fieldPath, cfg);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shapefile] SaveShapefileState: "
                    + ex.Message);
            }
        }

        public void TryAutoLoadShapefileForCurrentField()
        {
            try
            {
                string fieldPath = GetCurrentFieldFullPath();
                if (string.IsNullOrEmpty(fieldPath)) return;

                var cfg = ShapefilePersistence.Load(fieldPath);
                if (cfg == null || string.IsNullOrWhiteSpace(cfg.ShpPath)) return;
                if (!File.Exists(cfg.ShpPath))
                {
                    System.Diagnostics.Debug.WriteLine("[Shapefile] Auto-load: shape no existe "
                        + cfg.ShpPath);
                    return;
                }

                LoadShapefileFromPath(cfg.ShpPath,
                    showSummary: false, promptStyle: false,
                    autoStyleField: cfg.StyleField,
                    autoVisible: cfg.Visible,
                    autoShowFill: cfg.ShowFill,
                    autoShowOutline: cfg.ShowOutline);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shapefile] TryAutoLoad: " + ex.Message);
            }
        }

        // Punto de entrada para servicios externos (HUB web HTML) que ya
        // colocaron .shp+.shx+.dbf+.prj en disco y necesitan disparar la carga
        // sin abrir el OpenFileDialog. No muestra MessageBox; el resultado se
        // devuelve por bool y los detalles quedan en ShapefileLayerForAdapters.
        public bool LoadShapefileFromExternal(string shpPath)
        {
            string _;
            return LoadShapefileFromExternal(shpPath, out _);
        }

        // Overload con diagnóstico: cuando el upload viene del Hub no hay
        // MessageBox para el operario, así que devolvemos por out la razón
        // concreta del fallo. Si no, el JS sólo veía "(revisar consola)" y
        // el operario tenía que ir al PC con DevTools — inviable en tractor.
        public bool LoadShapefileFromExternal(string shpPath, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(shpPath)) { error = "Path vacío."; return false; }
            if (!System.IO.File.Exists(shpPath)) { error = "No existe el .shp en disco: " + shpPath; return false; }
            // Pre-check: sin job abierto, LoadShapefileFromPath retorna false
            // silenciosamente. Acá lo decimos con palabras.
            if (!isJobStarted)
            {
                error = "Abrí un lote en PilotX antes de cargar el shapefile (no hay job activo).";
                return false;
            }
            try
            {
                bool ok = LoadShapefileFromPath(shpPath, showSummary: false, promptStyle: false);
                if (!ok)
                {
                    error = "El lector aceptó el archivo pero no quedó nada cargado (¿0 polígonos? ¿formato no WGS84?).";
                    return false;
                }
                if (shapefileLayer == null || shapefileLayer.IsEmpty)
                {
                    error = "Capa cargada vacía — el .shp no contiene polígonos legibles.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shapefile] External load failed: " + ex);
                error = ex.Message;
                return false;
            }
        }

        public void ClearShapefileLayer()
        {
            shapefileLayer = null;
            if (shapefileToggleItem != null)
            {
                shapefileToggleItem.Enabled = false;
                shapefileToggleItem.Checked = false;
            }
            if (shapefileStyleItem != null)
                shapefileStyleItem.Enabled = false;
            RefreshShapefileLegend();
        }

        // Intenta procesar un click en modo inspeccion. Retorna true si lo
        // manejo (en cuyo caso el handler del mapa no debe hacer nada mas).
        private void OpenShapefileExportDialog()
        {
            if (shapefileLayer == null || shapefileLayer.IsEmpty)
            {
                MessageBox.Show(this,
                    "No hay shapefile cargado para exportar.",
                    "Exportar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var sfd = new SaveFileDialog())
            {
                sfd.Title = "Exportar shapefile";
                sfd.Filter = "Shapefile (*.shp)|*.shp|KML (*.kml)|*.kml";
                sfd.DefaultExt = "shp";
                sfd.AddExtension = true;

                string fieldPath = GetCurrentFieldFullPath();
                if (!string.IsNullOrEmpty(fieldPath) && Directory.Exists(fieldPath))
                    sfd.InitialDirectory = fieldPath;

                string stem = string.IsNullOrEmpty(shapefileLayer.Source)
                    ? "export"
                    : Path.GetFileNameWithoutExtension(shapefileLayer.Source);
                sfd.FileName = stem + "_export";

                if (sfd.ShowDialog(this) != DialogResult.OK) return;

                Cursor oldCursor = Cursor.Current;
                Cursor.Current = Cursors.WaitCursor;
                try
                {
                    IShapefileExportSource src = shapefileLayer;
                    bool isKml = sfd.FilterIndex == 2
                        || string.Equals(Path.GetExtension(sfd.FileName),
                            ".kml", StringComparison.OrdinalIgnoreCase);

                    if (isKml)
                        ShapefileExporter.ExportKml(src, sfd.FileName, shapefileLayer.Source);
                    else
                        ShapefileExporter.ExportShapefile(src, sfd.FileName);
                }
                catch (Exception ex)
                {
                    Cursor.Current = oldCursor;
                    System.Diagnostics.Debug.WriteLine("[Shapefile] Export: " + ex);
                    MessageBox.Show(this,
                        "Error exportando:\n\n" + ex.Message,
                        "Exportar", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                Cursor.Current = oldCursor;

                MessageBox.Show(this,
                    "Export completo:\n" + sfd.FileName,
                    "Exportar", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        public bool TryHandleShapefileInspectClick(int screenX, int screenY)
        {
            if (!shapefileInspectMode) return false;
            if (shapefileLayer == null || shapefileLayer.PolygonCount == 0) return true;
            if (!_glMatricesValid) return true;

            double east, north;
            if (!UnprojectMouseToGround(screenX, screenY, out east, out north)) return true;

            int idx = shapefileLayer.FindPolygonAt(east, north);
            if (idx < 0)
            {
                TimedMessageBox(1500, "Shapefile", "Click fuera de poligonos.");
                return true;
            }

            var attrs = shapefileLayer.GetPolygonAttributes(idx);
            double? curVal = null;
            if (!string.IsNullOrEmpty(shapefileLayer.StyleField))
            {
                double v;
                if (shapefileLayer.TryGetPolygonNumeric(idx, shapefileLayer.StyleField, out v))
                    curVal = v;
            }

            // Inspección de atributos de shapefile: ahora se hace desde la UI
            // HTML (Piloto). El diálogo WinForms legacy fue eliminado.
            _ = attrs; _ = curVal;
            return true;
        }

        // Unproyecta (screenX, screenY) al plano z=0 del mundo usando las
        // matrices capturadas en el ultimo frame. (screenX, screenY) son
        // coords de oglMain (top-left origin).
        private bool UnprojectMouseToGround(int screenX, int screenY, out double east, out double north)
        {
            east = 0; north = 0;
            if (!_glMatricesValid) return false;

            int vpW = _glViewport[2];
            int vpH = _glViewport[3];
            if (vpW <= 0 || vpH <= 0) return false;

            // GL viewport tiene origen abajo-izq; oglMain abajo-arriba invertido.
            double ndcX = (2.0 * (screenX - _glViewport[0])) / vpW - 1.0;
            double ndcY = 1.0 - (2.0 * (screenY - _glViewport[1])) / vpH;

            // mvp = modelview * projection (convention columna-major GL; el
            // producto se hace en el mismo orden en el que GL aplica M y P:
            // p_clip = P * M * p_world, entonces invirtiendo:
            // p_world = (P * M)^(-1) * p_clip = invMvp * p_clip).
            double[] mvp = MulMat4(_glProjection, _glModelView);
            double[] inv;
            if (!InvertMat4(mvp, out inv)) return false;

            double nx, ny, nz;
            if (!TransformMat4Point(inv, ndcX, ndcY, -1.0, out nx, out ny, out nz)) return false;
            double fx, fy, fz;
            if (!TransformMat4Point(inv, ndcX, ndcY, 1.0, out fx, out fy, out fz)) return false;

            double dz = fz - nz;
            if (Math.Abs(dz) < 1e-9) return false;
            double t = -nz / dz;
            east = nx + t * (fx - nx);
            north = ny + t * (fy - ny);
            return true;
        }

        // Matrices column-major como GL las devuelve. Indice row r col c: a[c*4 + r].
        private static double[] MulMat4(double[] a, double[] b)
        {
            var r = new double[16];
            for (int c = 0; c < 4; c++)
                for (int row = 0; row < 4; row++)
                {
                    double s = 0;
                    for (int k = 0; k < 4; k++)
                        s += a[k * 4 + row] * b[c * 4 + k];
                    r[c * 4 + row] = s;
                }
            return r;
        }

        private static bool TransformMat4Point(double[] m, double x, double y, double z,
            out double ox, out double oy, out double oz)
        {
            double rx = m[0] * x + m[4] * y + m[8] * z + m[12];
            double ry = m[1] * x + m[5] * y + m[9] * z + m[13];
            double rz = m[2] * x + m[6] * y + m[10] * z + m[14];
            double rw = m[3] * x + m[7] * y + m[11] * z + m[15];
            if (Math.Abs(rw) < 1e-12) { ox = oy = oz = 0; return false; }
            ox = rx / rw; oy = ry / rw; oz = rz / rw;
            return true;
        }

        private static bool InvertMat4(double[] m, out double[] inv)
        {
            inv = new double[16];
            inv[0] = m[5] * m[10] * m[15] - m[5] * m[11] * m[14] - m[9] * m[6] * m[15] + m[9] * m[7] * m[14] + m[13] * m[6] * m[11] - m[13] * m[7] * m[10];
            inv[4] = -m[4] * m[10] * m[15] + m[4] * m[11] * m[14] + m[8] * m[6] * m[15] - m[8] * m[7] * m[14] - m[12] * m[6] * m[11] + m[12] * m[7] * m[10];
            inv[8] = m[4] * m[9] * m[15] - m[4] * m[11] * m[13] - m[8] * m[5] * m[15] + m[8] * m[7] * m[13] + m[12] * m[5] * m[11] - m[12] * m[7] * m[9];
            inv[12] = -m[4] * m[9] * m[14] + m[4] * m[10] * m[13] + m[8] * m[5] * m[14] - m[8] * m[6] * m[13] - m[12] * m[5] * m[10] + m[12] * m[6] * m[9];
            inv[1] = -m[1] * m[10] * m[15] + m[1] * m[11] * m[14] + m[9] * m[2] * m[15] - m[9] * m[3] * m[14] - m[13] * m[2] * m[11] + m[13] * m[3] * m[10];
            inv[5] = m[0] * m[10] * m[15] - m[0] * m[11] * m[14] - m[8] * m[2] * m[15] + m[8] * m[3] * m[14] + m[12] * m[2] * m[11] - m[12] * m[3] * m[10];
            inv[9] = -m[0] * m[9] * m[15] + m[0] * m[11] * m[13] + m[8] * m[1] * m[15] - m[8] * m[3] * m[13] - m[12] * m[1] * m[11] + m[12] * m[3] * m[9];
            inv[13] = m[0] * m[9] * m[14] - m[0] * m[10] * m[13] - m[8] * m[1] * m[14] + m[8] * m[2] * m[13] + m[12] * m[1] * m[10] - m[12] * m[2] * m[9];
            inv[2] = m[1] * m[6] * m[15] - m[1] * m[7] * m[14] - m[5] * m[2] * m[15] + m[5] * m[3] * m[14] + m[13] * m[2] * m[7] - m[13] * m[3] * m[6];
            inv[6] = -m[0] * m[6] * m[15] + m[0] * m[7] * m[14] + m[4] * m[2] * m[15] - m[4] * m[3] * m[14] - m[12] * m[2] * m[7] + m[12] * m[3] * m[6];
            inv[10] = m[0] * m[5] * m[15] - m[0] * m[7] * m[13] - m[4] * m[1] * m[15] + m[4] * m[3] * m[13] + m[12] * m[1] * m[7] - m[12] * m[3] * m[5];
            inv[14] = -m[0] * m[5] * m[14] + m[0] * m[6] * m[13] + m[4] * m[1] * m[14] - m[4] * m[2] * m[13] - m[12] * m[1] * m[6] + m[12] * m[2] * m[5];
            inv[3] = -m[1] * m[6] * m[11] + m[1] * m[7] * m[10] + m[5] * m[2] * m[11] - m[5] * m[3] * m[10] - m[9] * m[2] * m[7] + m[9] * m[3] * m[6];
            inv[7] = m[0] * m[6] * m[11] - m[0] * m[7] * m[10] - m[4] * m[2] * m[11] + m[4] * m[3] * m[10] + m[8] * m[2] * m[7] - m[8] * m[3] * m[6];
            inv[11] = -m[0] * m[5] * m[11] + m[0] * m[7] * m[9] + m[4] * m[1] * m[11] - m[4] * m[3] * m[9] - m[8] * m[1] * m[7] + m[8] * m[3] * m[5];
            inv[15] = m[0] * m[5] * m[10] - m[0] * m[6] * m[9] - m[4] * m[1] * m[10] + m[4] * m[2] * m[9] + m[8] * m[1] * m[6] - m[8] * m[2] * m[5];

            double det = m[0] * inv[0] + m[1] * inv[4] + m[2] * inv[8] + m[3] * inv[12];
            if (Math.Abs(det) < 1e-15) return false;
            double invDet = 1.0 / det;
            for (int i = 0; i < 16; i++) inv[i] *= invDet;
            return true;
        }

        // Camino HTML: hostea WebView2 con /pages/widget-quantix.html en lugar
        // del ShapefileLegendControl. Misma posición/tamaño/drag persistido que
        // el legacy — el widget HTML se autoalimenta por REST.
        private void InitQuantiXWidgetHtml()
        {
            if (quantixWidgetHtml != null) return;
            string baseUrl = AgroParallel.Shell.AgpWebHostBootstrap.Url;
            if (string.IsNullOrEmpty(baseUrl)) return; // host aún no levantado

            quantixWidgetHtml = new AgroParallel.Common.QuantiXWidgetWebView2Control(baseUrl);
            quantixWidgetHtml.Visible = false;
            quantixWidgetHtml.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            quantixWidgetHtml.Location = new Point(85, this.ClientSize.Height - quantixWidgetHtml.Height - 90);
            this.Controls.Add(quantixWidgetHtml);
            quantixWidgetHtml.BringToFront();

            // Posición custom persistida (drag previo) → restaurar.
            try
            {
                var prefs = AgroParallel.Services.OverlayPrefsService.Instance.Load();
                if (prefs.QxX >= 0 && prefs.QxY >= 0)
                {
                    quantixWidgetHtml.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                    quantixWidgetHtml.Location = new Point(prefs.QxX, prefs.QxY);
                }
            }
            catch { }

            // Drag & persist — reusa el mismo OverlayPrefsService que el legacy.
            AgroParallel.Common.OverlayDragger.Attach(quantixWidgetHtml, pt =>
            {
                try
                {
                    var p = AgroParallel.Services.OverlayPrefsService.Instance.Load();
                    p.QxX = pt.X; p.QxY = pt.Y;
                    AgroParallel.Services.OverlayPrefsService.Instance.Save(p);
                }
                catch { }
            });

            UpdateShapefileLegendVisibility();
        }

        // Banner top full-width — visible SOLO cuando el HTML (cabina-alarmas.html)
        // detecta nodos offline del implemento activo y emite postMessage('show').
        // Lazy: si el host aún no levantó, sale temprano y reintenta en próximas
        // pasadas (idéntico patrón que InitQuantiXWidgetHtml).
        private void InitAlertaNodosOverlay()
        {
            if (alertaNodosOverlay != null) return;
            string baseUrl = AgroParallel.Shell.AgpWebHostBootstrap.Url;
            if (string.IsNullOrEmpty(baseUrl)) return;

            alertaNodosOverlay = new AgroParallel.Common.AlertaNodosWebOverlayControl(baseUrl);
            alertaNodosOverlay.Visible = false; // el control HTML decide cuándo mostrar
            alertaNodosOverlay.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            alertaNodosOverlay.Location = new Point(0, 0);
            alertaNodosOverlay.Width = this.ClientSize.Width;
            this.Controls.Add(alertaNodosOverlay);
            alertaNodosOverlay.BringToFront();
        }

        private void InitShapefileLegend()
        {
            if (_useHtmlQxWidget)
            {
                InitQuantiXWidgetHtml();
                return;
            }
            if (shapefileLegend != null) return;

            shapefileLegend = new ShapefileLegendControl();
            shapefileLegend.Visible = false;
            shapefileLegend.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            shapefileLegend.Location = new Point(85, this.ClientSize.Height - shapefileLegend.Height - 90);
            this.Controls.Add(shapefileLegend);
            shapefileLegend.BringToFront();

            // Posición custom persistida (drag previo) → restaurar.
            try
            {
                var prefs = AgroParallel.Services.OverlayPrefsService.Instance.Load();
                if (prefs.QxX >= 0 && prefs.QxY >= 0)
                {
                    shapefileLegend.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                    shapefileLegend.Location = new Point(prefs.QxX, prefs.QxY);
                }
            }
            catch { }

            // Drag & persist.
            AgroParallel.Common.OverlayDragger.Attach(shapefileLegend, pt =>
            {
                try
                {
                    var p = AgroParallel.Services.OverlayPrefsService.Instance.Load();
                    p.QxX = pt.X; p.QxY = pt.Y;
                    AgroParallel.Services.OverlayPrefsService.Instance.Save(p);
                }
                catch { }
            });

            // Manual por motor: (motorIdx, manual, dosis).
            // Persistimos en motor.DosisFija — el bridge lo lee y publica el
            // target PPS al ESP32. Si manual=false, DosisFija=0 y el bridge
            // vuelve al cálculo desde shapefile/CampoDosis.
            shapefileLegend.MotorManualChanged += (motorIdx, manual, dosis) =>
            {
                try
                {
                    var motCfg = MotoresConfig.Load();
                    // Target: el nodo seleccionado en el widget. Cada nodo
                    // dosifica en manual de forma independiente.
                    string selUid = shapefileLegend.SelectedNodoUid;
                    QxNodoConfig target = null;
                    if (!string.IsNullOrEmpty(selUid))
                    {
                        foreach (var n in motCfg.Nodos)
                            if (n.Habilitado && n.Uid == selUid) { target = n; break; }
                    }
                    if (target == null)
                    {
                        foreach (var n in motCfg.Nodos)
                            if (n.Habilitado) { target = n; break; }
                    }
                    if (target != null && motorIdx < target.Motores.Length)
                    {
                        target.Motores[motorIdx].DosisFija = manual ? dosis : 0;
                        motCfg.Save();
                    }
                }
                catch { }
            };

            // Aparece en modo manual (sin shape) si hay nodos QX configurados.
            UpdateShapefileLegendVisibility();
        }

        private void RefreshShapefileLegend()
        {
            if (shapefileLegend == null) return;
            if (shapefileLayer != null && !string.IsNullOrEmpty(shapefileLayer.StyleField))
                shapefileLegend.SetLegend(shapefileLayer.StyleField, shapefileLayer.StyleMin, shapefileLayer.StyleMax);
            else
                shapefileLegend.Clear();
            UpdateShapefileLegendVisibility();
        }

        private void UpdateShapefileLegendVisibility()
        {
            // Camino HTML: visibilidad gobernada por _userWantsQXOverlay y
            // por la existencia de nodos QX. El widget HTML se autoadapta a
            // shape vs. sin-shape; no necesitamos HasData del control.
            if (_useHtmlQxWidget)
            {
                // Init perezoso: InitShapefileMenu() corre antes de
                // AgpWebHostBootstrap.EnsureStarted() en FormGPS_Load, por lo que
                // la primera pasada de InitQuantiXWidgetHtml() devolvió temprano
                // con Url vacío. Reintentamos acá hasta que el host esté arriba.
                if (quantixWidgetHtml == null)
                {
                    try { InitQuantiXWidgetHtml(); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[QxWidget] lazy init: " + ex.Message);
                    }
                    if (quantixWidgetHtml == null) return;
                }
                bool hasQxNodosHtml = false;
                try { hasQxNodosHtml = MotoresConfig.Load().Nodos.Count > 0; } catch { }
                bool showHtml = hasQxNodosHtml && _userWantsQXOverlay;
                quantixWidgetHtml.Visible = showHtml;
                if (showHtml) quantixWidgetHtml.BringToFront();
                return;
            }

            if (shapefileLegend == null) return;
            // Visible si:
            //  · Hay shapefile cargado y visible (caso original), O
            //  · Hay nodos QuantiX configurados (para dosificar manualmente sin mapa).
            // Y siempre respetando el toggle del usuario (pill QX).
            bool hasShape = shapefileLegend.HasData
                && shapefileLayer != null
                && shapefileLayer.IsVisible;
            bool hasQxNodos = false;
            try { hasQxNodos = MotoresConfig.Load().Nodos.Count > 0; } catch { }
            bool show = (hasShape || hasQxNodos) && _userWantsQXOverlay;
            shapefileLegend.Visible = show;
            if (show) shapefileLegend.BringToFront();
        }

        public void UpdateShapefileCurrentDose()
        {
            if (shapefileLegend == null) return;
            // shapefileLayer puede ser null cuando no hay mapa cargado pero
            // sí hay nodos QuantiX configurados (modo manual sin mapa).
            if (shapefileLayer != null)
            {
                shapefileLegend.SetCurrent(
                    shapefileLayer.CurrentDose,
                    shapefileLayer.HasCurrentDose);
            }
            else
            {
                shapefileLegend.SetCurrent(0, false);
            }

            // QUANTIX_MOD_START — alimentar vúmetros de motores.
            shapefileLegend.SetConnected(quantiXBridge != null && quantiXBridge.IsRunning);

            // Si el bridge todavía no levantó pero el operario ya configuró
            // nodos, mostramos placeholders en el widget (nombres + 0/0) en
            // vez de dejarlo negro/vacío. Apenas el bridge produzca PPS reales
            // el bloque de abajo los sobrescribe.
            if (quantiXBridge == null || !quantiXBridge.IsRunning)
            {
                try
                {
                    var motCfg = MotoresConfig.Load();
                    var nodoInfos = new System.Collections.Generic.List<ShapefileLegendControl.NodoInfo>();
                    foreach (var n in motCfg.Nodos)
                        if (n.Habilitado)
                            nodoInfos.Add(new ShapefileLegendControl.NodoInfo { Uid = n.Uid, Nombre = string.IsNullOrEmpty(n.Nombre) ? n.Uid : n.Nombre });
                    shapefileLegend.SetNodos(nodoInfos);

                    string selUid = shapefileLegend.SelectedNodoUid;
                    foreach (var nodo in motCfg.Nodos)
                    {
                        if (!nodo.Habilitado) continue;
                        if (!string.IsNullOrEmpty(selUid) && nodo.Uid != selUid) continue;
                        for (int mi = 0; mi < nodo.Motores.Length && mi < 2; mi++)
                        {
                            var motor = nodo.Motores[mi];
                            string nombre = string.IsNullOrEmpty(motor.Nombre) ? ("M" + mi) : motor.Nombre;
                            shapefileLegend.SetMotorDosis(mi, nombre, 0, 0, false);
                            shapefileLegend.SetMotorManualState(mi, motor.DosisFija > 0, motor.DosisFija);
                        }
                        break;
                    }
                }
                catch { }
            }

            if (quantiXBridge != null && quantiXBridge.IsRunning)
            {
                try
                {
                    var motCfg = MotoresConfig.Load();

                    // Alimentar el selector de nodos del widget.
                    var nodoInfos = new System.Collections.Generic.List<ShapefileLegendControl.NodoInfo>();
                    foreach (var n in motCfg.Nodos)
                        if (n.Habilitado)
                            nodoInfos.Add(new ShapefileLegendControl.NodoInfo { Uid = n.Uid, Nombre = string.IsNullOrEmpty(n.Nombre) ? n.Uid : n.Nombre });
                    shapefileLegend.SetNodos(nodoInfos);

                    // Renderizar SOLO el nodo seleccionado. Cada nodo tiene su
                    // propio M0/M1; el widget muestra/edita uno por vez.
                    string selUid = shapefileLegend.SelectedNodoUid;

                    foreach (var nodo in motCfg.Nodos)
                    {
                        if (!nodo.Habilitado) continue;
                        if (!string.IsNullOrEmpty(selUid) && nodo.Uid != selUid) continue;
                        for (int mi = 0; mi < nodo.Motores.Length && mi < 2; mi++)
                        {
                            var motor = nodo.Motores[mi];
                            double vel = avgSpeed;
                            double velMs = vel / 3.6;
                            double ancho = tool != null && tool.width > 0 ? tool.width : 1;

                            // Dosis objetivo.
                            double dosisObj = 0;
                            if (motor.DosisFija > 0)
                                dosisObj = motor.DosisFija;
                            else if (!string.IsNullOrEmpty(motor.CampoDosis) && shapefileLayer != null)
                            {
                                // Leer el campo específico del shapefile.
                                int polyIdx = shapefileLayer.CurrentPolygonIndex;
                                if (polyIdx >= 0)
                                {
                                    double fieldVal;
                                    if (shapefileLayer.TryGetPolygonNumeric(polyIdx, motor.CampoDosis, out fieldVal))
                                        dosisObj = fieldVal;
                                }
                                if (dosisObj <= 0)
                                    dosisObj = shapefileLayer.CurrentDose;
                            }
                            else if (shapefileLayer != null && shapefileLayer.HasCurrentDose)
                            {
                                // Fallback: motor sin CampoDosis propio → usa el
                                // StyleField global del shape (= lo mismo que el
                                // bridge en QuantiXMotorBridge.OnTick rama "campo
                                // global"). Esto mantiene el panel en sync con
                                // lo que efectivamente está dosificando.
                                dosisObj = shapefileLayer.CurrentDose;
                            }

                            // Dosis real (kg/ha) inversa desde PPS real del ESP32.
                            // Bridge: pps = (dosis_kg × 1000 × ancho × velMs) / 10000 / MeterCal_g_pulso
                            // Inversa: dosis_kg = pps × MeterCal × 10 / (ancho × velMs)
                            double ppsReal = quantiXBridge.GetPpsReal(nodo.Uid, mi);
                            double dosisReal = 0;
                            if (velMs > 0.1 && motor.MeterCal > 0)
                                dosisReal = ppsReal * motor.MeterCal * 10.0 / (ancho * velMs);

                            string nombre = motor.Nombre ?? ("M" + mi);
                            if (!string.IsNullOrEmpty(motor.CampoDosis))
                                nombre = motor.CampoDosis;
                            else if (shapefileLayer != null && !string.IsNullOrEmpty(shapefileLayer.StyleField))
                                nombre = shapefileLayer.StyleField;

                            bool activo = ppsReal > 0 || dosisObj > 0;
                            shapefileLegend.SetMotorDosis(mi, nombre, dosisObj, dosisReal, activo);
                            // Sync MAN/AUTO + manual dose desde la config del nodo seleccionado.
                            shapefileLegend.SetMotorManualState(mi, motor.DosisFija > 0, motor.DosisFija);
                        }
                        break;
                    }
                }
                catch { }
            }
            // QUANTIX_MOD_END

            // FLOWX_LEGEND_MOD_START — refresh del overlay FlowX en el mismo tick.
            UpdateFlowXLegend();
            // FLOWX_LEGEND_MOD_END
        }

        // FLOWX_LEGEND_MOD_START
        // Overlay de telemetría FlowX en el mapa. Apilado encima del
        // shapefileLegend (QuantiX) cuando ambos están activos.
        private void InitFlowXLegend()
        {
            if (flowXLegend != null) return;
            flowXLegend = new AgroParallel.FlowX.FlowXLegendControl();
            flowXLegend.Visible = false;
            flowXLegend.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            // Lo posicionamos arriba del widget QuantiX (legacy o HTML) si existe;
            // sino, en su lugar.
            Control qxAnchor = (Control)shapefileLegend ?? quantixWidgetHtml;
            int baseY = (qxAnchor != null)
                ? qxAnchor.Top - flowXLegend.Height - 8
                : this.ClientSize.Height - flowXLegend.Height - 90;
            flowXLegend.Location = new Point(85, baseY);
            this.Controls.Add(flowXLegend);
            flowXLegend.BringToFront();

            // Posición custom persistida → restaurar.
            try
            {
                var prefs = AgroParallel.Services.OverlayPrefsService.Instance.Load();
                if (prefs.FxX >= 0 && prefs.FxY >= 0)
                {
                    flowXLegend.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                    flowXLegend.Location = new Point(prefs.FxX, prefs.FxY);
                }
            }
            catch { }

            // Drag & persist.
            AgroParallel.Common.OverlayDragger.Attach(flowXLegend, pt =>
            {
                try
                {
                    var p = AgroParallel.Services.OverlayPrefsService.Instance.Load();
                    p.FxX = pt.X; p.FxY = pt.Y;
                    AgroParallel.Services.OverlayPrefsService.Instance.Save(p);
                }
                catch { }
            });
        }

        private void UpdateFlowXLegend()
        {
            if (flowXLegend == null) return;
            try
            {
                var live = AgroParallel.Shell.AgpWebHostBootstrap.FlowXLive;
                var cfg = FlowXConfig.Load();
                int cfgCount = (cfg != null && cfg.Nodos != null) ? cfg.Nodos.Count : 0;
                // Respetamos el toggle del usuario (pill FX): aunque la config
                // tenga nodos y esté enabled, si el operario apagó el overlay
                // no lo mostramos.
                bool show = (cfg != null && cfg.Enabled) && cfgCount > 0 && _userWantsFXOverlay;
                if (!show)
                {
                    if (flowXLegend.Visible) flowXLegend.Visible = false;
                    return;
                }

                var rows = new System.Collections.Generic.List<AgroParallel.FlowX.FlowXLegendControl.NodoLive>();
                if (live != null)
                {
                    var snap = live.GetSnapshot();
                    if (snap != null && snap.Nodos != null)
                    {
                        foreach (var n in snap.Nodos)
                        {
                            rows.Add(new AgroParallel.FlowX.FlowXLegendControl.NodoLive
                            {
                                Uid = n.Uid,
                                Nombre = string.IsNullOrEmpty(n.Nombre) ? n.Uid : n.Nombre,
                                Online = n.Online,
                                CaudalLmin = n.CaudalLmin,
                                TargetLmin = n.TargetLmin,
                                Pwm = n.Pwm,
                                PidEstado = n.PidEstado
                            });
                        }
                    }
                }
                else
                {
                    // Live service todavía no levantó — mostramos placeholders
                    // a partir de la config para no dejarlo vacío.
                    foreach (var n in cfg.Nodos)
                    {
                        if (string.IsNullOrEmpty(n.Uid)) continue;
                        rows.Add(new AgroParallel.FlowX.FlowXLegendControl.NodoLive
                        {
                            Uid = n.Uid,
                            Nombre = string.IsNullOrEmpty(n.Nombre) ? n.Uid : n.Nombre,
                            Online = false,
                            CaudalLmin = 0,
                            TargetLmin = 0,
                            Pwm = 0,
                            PidEstado = "off"
                        });
                    }
                }

                flowXLegend.SetNodos(rows);
                bool anyOnline = false;
                foreach (var r in rows) if (r.Online) { anyOnline = true; break; }
                flowXLegend.SetConnected(anyOnline);

                // Re-anclar arriba del widget QuantiX cada vez (legacy o HTML).
                Control qxAnchor2 = (Control)shapefileLegend ?? quantixWidgetHtml;
                if (qxAnchor2 != null && qxAnchor2.Visible)
                {
                    int newY = qxAnchor2.Top - flowXLegend.Height - 8;
                    if (flowXLegend.Top != newY)
                        flowXLegend.Location = new Point(flowXLegend.Left, newY);
                }

                if (!flowXLegend.Visible) flowXLegend.Visible = true;
                flowXLegend.BringToFront();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[FlowX] UpdateFlowXLegend: " + ex.Message);
            }
        }
        // FLOWX_LEGEND_MOD_END

        // QUANTIX_MOD_START
        private void StartQuantiXSender()
        {
            try
            {
                if (quantiXSender != null) return;

                if (quantiXConfig == null)
                    quantiXConfig = QuantiXConfig.Load();

                // Bridge de motores: SIEMPRE arranca si hay nodos configurados.
                // No depende de Enabled del sender UDP.
                if (quantiXBridge == null)
                {
                    quantiXBridge = new QuantiXMotorBridge(new AgroParallel.Adapters.FormGpsStateProvider(this), new AgroParallel.Services.PrescripcionService());
                    _ = quantiXBridge.StartAsync();
                }

                if (!quantiXConfig.Enabled)
                {
                    Console.WriteLine("[QuantiX] Sender UDP deshabilitado, bridge de motores activo");
                    return;
                }

                quantiXSender = new QuantiXSender(quantiXConfig, BuildQuantiXSample);
                quantiXSender.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[QuantiX] StartSender: " + ex.Message);
            }
        }

        private void StopQuantiXSender()
        {
            try
            {
                if (quantiXBridge != null)
                {
                    quantiXBridge.Dispose();
                    quantiXBridge = null;
                }
                if (quantiXSender == null) return;
                quantiXSender.Dispose();
                quantiXSender = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[QuantiX] StopSender: " + ex.Message);
            }
        }

        private QuantiXSample BuildQuantiXSample()
        {
            var sample = new QuantiXSample();

            if (shapefileLayer != null)
            {
                sample.Inside = shapefileLayer.CurrentInside;
                sample.Dose = shapefileLayer.CurrentDose;
                sample.FieldName = shapefileLayer.StyleField;
            }

            // Posicion actual del pivot en WGS84 via la reproyeccion inversa.
            try
            {
                var plane = AppModel != null ? AppModel.LocalPlane : null;
                if (plane != null)
                {
                    var geo = new AgOpenGPS.Core.Models.GeoCoord(
                        pivotAxlePos.northing, pivotAxlePos.easting);
                    var ll = plane.ConvertGeoCoordToWgs84(geo);
                    sample.Latitude = ll.Latitude;
                    sample.Longitude = ll.Longitude;
                }
                sample.HeadingRad = pivotAxlePos.heading;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[QuantiX] BuildSample pos: " + ex.Message);
            }

            return sample;
        }
        // QUANTIX_MOD_END

        private void ShowShapefileSummary(string fileName, ShapefileReadResult r)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Archivo: " + fileName);
            sb.AppendLine("Poligonos: " + r.Polygons.Count);
            sb.AppendLine("Lineas: " + r.Lines.Count);
            sb.AppendLine("Puntos: " + r.Points.Count);

            if (r.Polygons.Count + r.Lines.Count + r.Points.Count > 0)
            {
                sb.AppendLine(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Extent lat: {0:F6} .. {1:F6}", r.MinLat, r.MaxLat));
                sb.AppendLine(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Extent lon: {0:F6} .. {1:F6}", r.MinLon, r.MaxLon));
            }

            sb.AppendLine();
            sb.AppendLine("Campos DBF (" + r.DbfFieldNames.Count + "):");
            if (r.DbfFieldNames.Count == 0)
            {
                sb.AppendLine("  (ninguno)");
            }
            else
            {
                int shown = Math.Min(r.DbfFieldNames.Count, 20);
                for (int i = 0; i < shown; i++)
                    sb.AppendLine("  - " + r.DbfFieldNames[i]);
                if (r.DbfFieldNames.Count > shown)
                    sb.AppendLine("  ... y " + (r.DbfFieldNames.Count - shown) + " mas");
            }

            if (r.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Advertencias:");
                foreach (var w in r.Warnings)
                    sb.AppendLine("  ! " + w);
            }

            MessageBox.Show(this, sb.ToString(), "Shapefile cargado",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        // SHAPEFILE_MOD_END

        // COREX_FIELD_MOD_START
        /// <summary>
        /// Escribe current_field.json en la carpeta raíz de AgOpenGPS (Documents\AgOpenGPS\)
        /// para que CoreX y los módulos de AgroParallel detecten el campo activo sin AppData.
        ///
        /// Detecta automáticamente la acción:
        ///   "nuevo"    → directorio recién creado, sin datos previos
        ///   "abierto"  → campo diferente al último notificado, con datos existentes
        ///   "continuar"→ mismo campo que estaba activo la última vez
        ///   "cerrado"  → no hay campo activo (JobClose)
        /// </summary>
        private void NotificarCampoExterno_JobNew()
        {
            try
            {
                string nombre = currentFieldDirectory ?? "";
                if (string.IsNullOrEmpty(nombre)) return;

                string accion;
                if (nombre == _apLastFieldNotified)
                {
                    // Mismo campo que la última vez → el usuario eligió "Continuar"
                    accion = "continuar";
                }
                else
                {
                    // Campo distinto → determinar si ya tenía datos o es completamente nuevo
                    string fieldDir = Path.Combine(RegistrySettings.fieldsDirectory, nombre);
                    bool tieneDatos = false;
                    if (Directory.Exists(fieldDir))
                    {
                        // Archivos con contenido real indican campo preexistente
                        string[] archivosConDatos = { "Boundary.txt", "TrackLines.txt", "ABLines.txt" };
                        foreach (string arch in archivosConDatos)
                        {
                            string ruta = Path.Combine(fieldDir, arch);
                            if (File.Exists(ruta) && new FileInfo(ruta).Length > 10)
                            {
                                tieneDatos = true;
                                break;
                            }
                        }
                    }
                    accion = tieneDatos ? "abierto" : "nuevo";
                }

                _apLastFieldNotified = nombre;
                EscribirCurrentFieldJson(nombre, accion);
            }
            catch
            {
                // Nunca interrumpir AgOpenGPS por un error de notificación
            }
        }

        private void NotificarCampoExterno_JobClose()
        {
            try
            {
                _apLastFieldNotified = "";
                EscribirCurrentFieldJson("", "cerrado");
            }
            catch { }
        }

        /// <summary>
        /// Serializa el estado del campo a JSON sin dependencias externas.
        /// Ruta: Documents\AgOpenGPS\current_field.json
        /// </summary>
        private void EscribirCurrentFieldJson(string fieldName, string accion)
        {
            try
            {
                string nombreSeguro = (fieldName ?? "")
                    .Replace("\\", "\\\\")
                    .Replace("\"", "\\\"");

                long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                string json =
                    "{\r\n" +
                    "  \"fieldName\": \"" + nombreSeguro + "\",\r\n" +
                    "  \"accion\": \""    + accion       + "\",\r\n" +
                    "  \"ts\": "          + ts           + "\r\n" +
                    "}";

                // Intentar múltiples rutas en orden de prioridad
                var candidatos = new System.Collections.Generic.List<string>();

                // 1. RegistrySettings.baseDirectory (ruta oficial)
                string regBase = RegistrySettings.baseDirectory;
                if (!string.IsNullOrEmpty(regBase))
                    candidatos.Add(regBase);

                // 2. fieldsDirectory un nivel arriba (si baseDirectory falla)
                string regFields = RegistrySettings.fieldsDirectory;
                if (!string.IsNullOrEmpty(regFields))
                {
                    string parent = System.IO.Path.GetDirectoryName(regFields.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(parent))
                        candidatos.Add(parent);
                }

                // 3. Carpetas estándar de Documents como fallback
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                candidatos.Add(Path.Combine(docs, "AgOpenGPS"));
                candidatos.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "OneDrive", "Documents", "AgOpenGPS"));

                // 4. Último recurso: directorio del ejecutable
                candidatos.Add(Application.StartupPath);

                System.Diagnostics.Debug.WriteLine("[CoreX] baseDirectory = \"" + regBase + "\"");
                System.Diagnostics.Debug.WriteLine("[CoreX] fieldsDirectory = \"" + regFields + "\"");

                string filePath = null;
                foreach (string dir in candidatos)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(dir)) continue;
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        string ruta = Path.Combine(dir, "current_field.json");
                        File.WriteAllText(ruta, json, System.Text.Encoding.UTF8);
                        filePath = ruta;
                        System.Diagnostics.Debug.WriteLine(
                            "[CoreX] current_field.json escrito en: " + ruta);
                        break; // Éxito — no seguir intentando
                    }
                    catch (Exception exInner)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[CoreX] Falló " + dir + ": " + exInner.Message);
                    }
                }

                if (filePath == null)
                    System.Diagnostics.Debug.WriteLine("[CoreX] ERROR: No se pudo escribir en ninguna ruta");
                else
                    System.Diagnostics.Debug.WriteLine(
                        "[CoreX] OK → " + accion + ": \"" + nombreSeguro + "\"");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[CoreX] EXCEPCIÓN en EscribirCurrentFieldJson: " + ex.Message);
            }
        }

        /// <summary>
        /// El usuario confirmó el borrado del área trabajada.
        /// Escribe current_field.json con accion = "area_borrada" manteniendo el campo activo.
        /// CoreX / VistaX detectan esto para resetear el lote actual sin cerrarlo.
        /// </summary>
        public void NotificarBorradoArea()
        {
            try
            {
                string nombre = currentFieldDirectory ?? "";
                System.Diagnostics.Debug.WriteLine("[CoreX] NotificarBorradoArea → campo: \"" + nombre + "\"");
                EscribirCurrentFieldJson(nombre, "area_borrada");
            }
            catch { }
        }
        // COREX_FIELD_MOD_END

        // ORBITX_GPS_STATUS_START — Escribe gps_status.json para que OrbitX-Sync envíe tracking.
        private void WriteGpsStatusJson()
        {
            try
            {
                double lat = 0, lon = 0, heading = 0, speed = 0;

                if (AppModel?.CurrentLatLon != null)
                {
                    lat = AppModel.CurrentLatLon.Latitude;
                    lon = AppModel.CurrentLatLon.Longitude;
                }
                speed = avgSpeed;
                heading = pivotAxlePos.heading * (180.0 / Math.PI);
                string field = currentFieldDirectory ?? "";

                // No escribir si no hay GPS
                if (Math.Abs(lat) < 0.001 && Math.Abs(lon) < 0.001) return;

                var sb = new System.Text.StringBuilder();
                sb.Append("{");
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "\"lat\":{0:F7},\"lon\":{1:F7},\"heading\":{2:F1},\"speed\":{3:F2},",
                    lat, lon, heading, speed);
                sb.AppendFormat("\"field\":\"{0}\",", (field ?? "").Replace("\\", "\\\\").Replace("\"", "\\\""));
                sb.AppendFormat("\"ts\":{0}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                sb.Append("}");

                string dir = AppDomain.CurrentDomain.BaseDirectory;
                File.WriteAllText(Path.Combine(dir, "gps_status.json"), sb.ToString(),
                    System.Text.Encoding.UTF8);
            }
            catch { }
        }
        // ORBITX_GPS_STATUS_END
    }//class FormGPS
}//namespace AgOpenGPS


