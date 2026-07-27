// ============================================================================
// GuidanceEngineHost.cs — host headless para el loop de guiado (bloque 14,
// "proceso único"). Reemplaza a FormGPS: implementa las mismas interfaces
// I*Host que hoy implementa FormGPS vía partials (GPS/AgroParallel/Common/
// FormGps.*Host.cs), pero sin WinForms ni OpenGL. Orquesta los mismos
// CPositionUpdater/CHeadingUpdater/CAutoSteerUpdater/CYouTurnUpdater/
// CSectionCalculator/CSettingsSender ya extraídos a Core (bloque 9).
//
// Protocolo de red: idéntico al de FormGPS (UDPComm.Designer.cs) — escucha
// PGN en 127.0.0.1:15555 y contesta a CoreX en 127.255.255.255:17777, mismo
// CRC. Así corre contra un CoreX/ModSim real sin tocarlos.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using AgLibrary.Logging;
using AgOpenGPS.Core;

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost
    {
        // ---- modelo de guiado (mismos objetos que hoy vive en FormGPS) ----
        public readonly ApplicationModel AppModelField;
        public readonly CNMEA Pn;
        public readonly CAHRS Ahrs = new CAHRS();
        public readonly CSim Sim;
        public readonly WorldGrid WorldGridField = new WorldGrid();
        // No readonly: GuidanceEngineVehicleToolService (PilotX.Android) las
        // reconstruye al guardar config, mismo patron que FormGPS.vehicle/tool.
        public CVehicle Vehicle;
        public readonly CBoundary Bnd;
        public readonly CTrack Trk;
        public readonly CABLine ABLineField;
        public readonly CABCurve CurveField;
        public CTool Tool;
        public readonly CModuleComm Mc;
        public readonly CYouTurn Yt;
        public readonly CContour Ct;
        public readonly CRecordedPath RecPath;
        public readonly CFieldData Fd;
        public readonly CISOBUS Isobus;
        public readonly CTram Tram;
        public readonly CGuidance Gyd;
        public readonly CSection[] Sections = new CSection[MaxSectionsConst];
        public readonly List<CPatches> TriStripField = new List<CPatches>();

        public const int MaxSectionsConst = 64;

        // PGN de salida (mismos byte[] fijos que FormGPS).
        public readonly CPGN_FE P254Field = new CPGN_FE();
        public readonly CPGN_EF P239Field = new CPGN_EF();
        public readonly CPGN_E5 P229Field = new CPGN_E5();
        public readonly CPGN_FC P252Field = new CPGN_FC();
        public readonly CPGN_FB P251Field = new CPGN_FB();
        public readonly CPGN_EE P238Field = new CPGN_EE();
        public readonly CPGN_EC P236Field = new CPGN_EC();
        public readonly CPGN_EB P235Field = new CPGN_EB();

        // ---- orquestadores (bloque 9, ya en Core) ----
        public readonly CPositionUpdater PositionUpdater;
        public readonly CHeadingUpdater HeadingUpdater;
        public readonly CAutoSteerUpdater AutoSteerUpdater;
        public readonly CYouTurnUpdater YouTurnUpdater;
        public readonly CSectionCalculator SectionCalculator;
        public readonly CSettingsSender SettingsSender;
        public readonly PgnReceiver PgnReceiverField;

        // ---- red (mismo protocolo UDP loopback que FormGPS) ----
        private Socket _loopBackSocket;
        private EndPoint _epAgIO = new IPEndPoint(IPAddress.Parse("127.255.255.255"), 17777);
        private EndPoint _endPointLoopBack = new IPEndPoint(IPAddress.Loopback, 0);
        private byte[] _loopBuffer = new byte[1024];
        private bool _running;

        public GuidanceEngineHost(DirectoryInfo baseDirectory)
        {
            AppModelField = new ApplicationModel(baseDirectory);

            Pn = new CNMEA(this);
            Sim = new CSim(this);
            Vehicle = new CVehicle(this);
            Bnd = new CBoundary(this);
            Trk = new CTrack(this);
            ABLineField = new CABLine(this);
            CurveField = new CABCurve(this);
            Tool = new CTool(this);
            Mc = new CModuleComm(this);
            Yt = new CYouTurn(this);
            Ct = new CContour(this);
            RecPath = new CRecordedPath(this);
            Fd = new CFieldData(this);
            Isobus = new CISOBUS(this);
            Tram = new CTram(this);
            Gyd = new CGuidance(this);

            for (int i = 0; i < Sections.Length; i++) Sections[i] = new CSection();

            PositionUpdater = new CPositionUpdater(this);
            HeadingUpdater = new CHeadingUpdater(this);
            AutoSteerUpdater = new CAutoSteerUpdater(this);
            YouTurnUpdater = new CYouTurnUpdater(this);
            SectionCalculator = new CSectionCalculator(this);
            SettingsSender = new CSettingsSender(this);
            PgnReceiverField = new PgnReceiver(this);

            headingFromSource = "Fix";

            AplicarGeometriaDeSecciones();
        }

        /// <summary>
        /// Reparte el ancho del implemento entre las secciones (posición lateral
        /// izquierda/derecha de cada una). Es lo que hacía
        /// <c>FormGPS.LoadSettings</c> (GUI.Designer.cs) al arrancar y el motor
        /// headless NO estaba haciendo.
        ///
        /// Sin esto, todas las secciones se quedan con el default de
        /// <c>CSection</c> (positionLeft=-4 / positionRight=+4): quedan TODAS
        /// encimadas en el mismo lugar, los bordes de la tira de cobertura
        /// colapsan en un punto y la huella sale de ancho CERO. Síntoma en
        /// cabina: "prendo una sección y pinta cualquier cosa".
        ///
        /// Hay que volver a llamarlo cada vez que cambie la config del implemento
        /// (ancho, cantidad de secciones, modo secciones/zonas u offset).
        /// </summary>
        public void AplicarGeometriaDeSecciones()
        {
            try
            {
                if (Tool.isSectionsNotZones)
                {
                    SectionCalculator.SectionSetPosition();
                    SectionCalculator.SectionCalcWidths();
                }
                else
                {
                    SectionCalculator.SectionCalcMulti();
                }
            }
            catch { /* config incompleta: mejor seguir que no arrancar */ }
        }

        // ---- arranque/parada del socket loopback (equivalente a
        // StartLoopbackServer/ReceiveAppData de UDPComm.Designer.cs, sin el
        // BeginInvoke a hilo de UI — acá no hay hilo de UI). ----
        public void Start()
        {
            if (_running) return;
            _loopBackSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _loopBackSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            _loopBackSocket.Bind(new IPEndPoint(IPAddress.Loopback, 15555));
            _running = true;
            // Igual que FormGPS (FormGPS.cs:849): arrancar el watchdog del
            // PgnReceiver. Sin esto, udpWatch nunca corre → ElapsedMilliseconds
            // queda en 0 → el gate del PGN 0xD6 (< UdpWatchLimit) descarta TODOS
            // los fixes de GPS antes de arrancar el watch → deadlock, nunca se
            // procesa posición real (el modo --sim no pasa por acá y por eso sí
            // andaba).
            PgnReceiverField.StartWatch();
            _loopBackSocket.BeginReceiveFrom(_loopBuffer, 0, _loopBuffer.Length, SocketFlags.None,
                ref _endPointLoopBack, ReceiveAppData, null);
            Log.EventWriter("GuidanceEngine: UDP loopback escuchando en 127.0.0.1:15555");
        }

        public void Stop()
        {
            _running = false;
            try { _loopBackSocket?.Close(); } catch { }
            _loopBackSocket = null;
        }

        private void ReceiveAppData(IAsyncResult ar)
        {
            if (!_running) return;
            try
            {
                int len = _loopBackSocket.EndReceiveFrom(ar, ref _endPointLoopBack);
                byte[] data = new byte[len];
                Array.Copy(_loopBuffer, data, len);

                _loopBackSocket.BeginReceiveFrom(_loopBuffer, 0, _loopBuffer.Length, SocketFlags.None,
                    ref _endPointLoopBack, ReceiveAppData, null);

                PgnReceiverField.ReceiveFromAgIO(data);
            }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: error de recepción UDP: " + ex.Message);
            }
        }

        // equivalente exacto a FormGPS.SendPgnToLoop (UDPComm.Designer.cs:108).
        public void SendPgnToLoop(byte[] byteData)
        {
            if (_loopBackSocket != null && byteData.Length > 2)
            {
                try
                {
                    int crc = 0;
                    for (int i = 2; i + 1 < byteData.Length; i++) crc += byteData[i];
                    byteData[byteData.Length - 1] = (byte)crc;

                    _loopBackSocket.BeginSendTo(byteData, 0, byteData.Length, SocketFlags.None,
                        _epAgIO, EndSend, null);
                }
                catch { }
            }
        }

        private void EndSend(IAsyncResult ar)
        {
            try { _loopBackSocket?.EndSend(ar); } catch { }
        }

        // ---- pipeline por fix — equivalente a FormGPS.UpdateFixPosition()
        // (Position.designer.cs), pero sin las 2 líneas de refresh de GL
        // (oglBack/oglMain) ni el timer de frameTime, que son puro render. ----
        public void UpdateFixPosition()
        {
            startCounter++;

            if (!isGPSPositionInitialized)
            {
                PositionUpdater.InitializeFirstFewGPSPositions();
                return;
            }

            if (!isFirstHeadingSet && hasBeenFirstHeadingSet)
            {
                for (int i = 0; i < stepFixPts.Length; i++)
                {
                    stepFixPts[i].isSet = 0;
                    stepFixPts[i].easting = 0;
                    stepFixPts[i].northing = 0;
                    stepFixPts[i].distance = 0;
                }
                prevFix = Pn.fix;
                prevDistFix = Pn.fix;
                gpsHeading = 0;
                fixHeading = 0;
                imuGPS_Offset = 0;
                hasBeenFirstHeadingSet = false;
            }

            Pn.speed = Pn.vtgSpeed;
            Pn.AverageTheSpeed();

            HeadingUpdater.UpdateHeading();
            AutoSteerUpdater.SendCorrectedPositionPgn();
            AutoSteerUpdater.BuildAndSendAutoSteerPgn();
            YouTurnUpdater.UpdateYouTurnState();

            if (IsJobStarted)
            {
                // Control de secciones + cobertura (crea/gestiona las tiras CPatches
                // que AddSectionOrPathPoints va llenando de triángulos). Corre después
                // del pipeline de posición y antes de enviar los PGN, porque
                // BuildMachineByte (dentro) puebla los bytes de sección de P239/P229.
                SectionControlToUpdate();

                P239Field.pgn[P239Field.geoStop] = Mc.isOutOfBounds ? (byte)1 : (byte)0;
                SendPgnToLoop(P239Field.pgn);
                SendPgnToLoop(P229Field.pgn);
            }
        }
    }
}
