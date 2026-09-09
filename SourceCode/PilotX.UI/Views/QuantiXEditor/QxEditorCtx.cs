// ============================================================================
// QxEditorCtx.cs — estado compartido + helpers del editor nativo de QuantiX.
//
// QUÉ QUEDÓ NATIVO: todo el "state" que quantix.js tenía en el objeto `state`
// (config de motores, live por uid, estado de PilotX, implemento central,
// columnas del shape, pincel, modo Configurar/En marcha) y las funciones que
// lo recorrían (allMotors, totalSurcos, surcoOwner, paintSurco, derivarTren…).
// QUÉ SIGUE EN HTML: quantix.js entero, que usa la PWA del celular.
//
// derivarTrenMotor es un ESPEJO de TrenResolver.cs (backend) — mismo criterio
// de "sin trenes reales" y mismo desempate. Si cambia el criterio allá, hay
// que cambiarlo acá.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views.QuantiXEditor;

/// <summary>Una entrada de la lista PLANA de motores (todos los nodos
/// habilitados juntos). El índice en esa lista es el "índice plano" que usa
/// el pincel del planter y el color.</summary>
public sealed class QxMotorEntry
{
    public QxNodoConfig Nodo = null!;
    public int NodeIdx;
    public int MotorIdx;
    public QxMotorConfig Motor = null!;
    public string Uid = "";
}

/// <summary>Tren derivado del implemento para un motor.</summary>
public sealed class QxTrenDerivado
{
    public int Id;
    public string Nombre = "";
    public bool Conflicto;
}

public sealed class QxEditorCtx
{
    public QuantiXEditorClient Client = null!;

    // ---- estado -----------------------------------------------------------
    public QxMotoresConfig Cfg = new();
    public readonly Dictionary<string, QxNodoLive> LiveByUid = new(StringComparer.Ordinal);
    public bool LiveOk;                 // último /live respondió
    public int  LiveNodos;

    public bool   AogJobStarted;
    public bool[]? SectionOn;
    public int    AogNumSections;
    public double AogSpeed;
    public double AogAreaHa;

    public QxImplemento? Impl;
    public double AnchoPilotX;

    public string ShapeSource = "";
    public List<QxShapeField> ShapeFields = new();

    public int  BrushMotor;
    public int? UltimoSurcoTocado;
    public bool ConfigForzado;
    public bool EnMarchaDetectada;
    public bool SiembraEnMarcha => EnMarchaDetectada && !ConfigForzado;

    /// <summary>Motor elegido por tab (son independientes a propósito: se
    /// puede afinar el PID del M1 y calibrar el M0).</summary>
    public readonly Dictionary<string, int> MotorVista = new(StringComparer.Ordinal)
    {
        ["motores"] = 0, ["pid"] = 0, ["calibrar"] = 0, ["prueba"] = 0,
    };

    // ---- servicios del host ----------------------------------------------
    public Action<string>? Aviso;
    /// <summary>Confirmación con overlay interno del panel (NUNCA ShowDialog
    /// ni Flyout: sobre el mapa GL los popups no se dibujan).</summary>
    public Func<string, string, Task<bool>>? Confirmar;
    public Action<string>? IrATab;

    // ---- paleta de motores (misma que el JS) ------------------------------
    private static readonly string[] MOTOR_COLORS =
        { "#4ABA3E", "#7F6BE0", "#E0A33E", "#3E9BE0", "#E06B8B", "#46C5B0" };

    public static IBrush MotorBrush(int idx) => new SolidColorBrush(MotorColor(idx));
    public static Color MotorColor(int idx)
        => Color.Parse(MOTOR_COLORS[((idx % MOTOR_COLORS.Length) + MOTOR_COLORS.Length) % MOTOR_COLORS.Length]);

    // ---- recorridas de la config -----------------------------------------

    public List<QxMotorEntry> AllMotors()
    {
        var outp = new List<QxMotorEntry>();
        var ns = Cfg?.Nodos;
        if (ns == null) return outp;
        for (int n = 0; n < ns.Count; n++)
        {
            var nodo = ns[n];
            if (nodo == null || !nodo.Habilitado) continue;
            var ms = nodo.Motores;
            if (ms == null) continue;
            for (int i = 0; i < ms.Count; i++)
                outp.Add(new QxMotorEntry { Nodo = nodo, NodeIdx = n, MotorIdx = i, Motor = ms[i], Uid = nodo.Uid });
        }
        return outp;
    }

    public List<QxNodoConfig> NodosConUid()
    {
        var outp = new List<QxNodoConfig>();
        var ns = Cfg?.Nodos;
        if (ns == null) return outp;
        foreach (var n in ns) if (n != null && !string.IsNullOrEmpty(n.Uid)) outp.Add(n);
        return outp;
    }

    public QxMotorConfig? FindMotor(string uid, int mi)
    {
        var ns = Cfg?.Nodos;
        if (ns == null) return null;
        foreach (var n in ns)
            if (n != null && string.Equals(n.Uid, uid, StringComparison.Ordinal)
                && n.Motores != null && mi >= 0 && mi < n.Motores.Count)
                return n.Motores[mi];
        return null;
    }

    public QxNodoConfig? FindNodo(string uid)
    {
        var ns = Cfg?.Nodos;
        if (ns == null) return null;
        foreach (var n in ns)
            if (n != null && string.Equals(n.Uid, uid, StringComparison.Ordinal)) return n;
        return null;
    }

    /// <summary>Los motores que hay que OFRECER de un nodo: EXACTAMENTE los que
    /// tiene configurados. La cantidad real la fija el nodo — el backend arma
    /// la lista con los motores que vinieron en el announcement MQTT (el
    /// Quantix2Motors reporta 2; el de 7 canales, 7) y después manda el archivo.
    ///
    /// TRAMPA, fácil de reintroducir (arreglada 2026-08-16): esto rellenaba
    /// hasta 2 motores con Habilitado = true. Con SOLO ENTRAR a Motores / PID /
    /// Calibración / Prueba, un nodo de 0 o 1 motor quedaba con 2 en memoria, y
    /// el primer guardado —que puede salir solo, por ejemplo al tildar el nodo—
    /// los persistía, los publicaba al nodo por MQTT y los subía a OrbitX.
    /// Aparecían motores que no existen en la máquina y un canal fantasma
    /// HABILITADO recibe consigna. Regla: el relleno NUNCA inventa motores
    /// habilitados.
    ///
    /// Si el nodo no trae ninguno se ofrece UN placeholder DESHABILITADO para
    /// que la tab tenga algo que dibujar; GuardarAsync no lo persiste mientras
    /// el operario no lo toque (ver EsPlaceholderSinTocar).</summary>
    public List<QxMotorConfig> MotoresDelNodo(QxNodoConfig n)
    {
        n.Motores ??= new List<QxMotorConfig>();
        if (n.Motores.Count == 0)
            n.Motores.Add(new QxMotorConfig
            {
                Nombre = "Motor 1",
                Habilitado = false,
                EsPlaceholder = true,
            });
        return n.Motores;
    }

    /// <summary>Placeholder que el operario nunca tocó: sigue deshabilitado y
    /// sin surcos asignados. Habilitarlo, pintarle surcos o guardarlo desde la
    /// tab Motores lo convierte en un motor de verdad y deja de purgarse.</summary>
    public static bool EsPlaceholderSinTocar(QxMotorConfig m)
        => m != null && m.EsPlaceholder && !m.Habilitado
           && (m.Cortes == null || m.Cortes.Count == 0);

    /// <summary>El operario escribió algo REAL en este motor: midió el tope o
    /// el PWM mínimo, aplicó PID, guardó la calibración. Si era el placeholder
    /// de la UI deja de serlo y a partir de acá se persiste como cualquier
    /// motor.
    ///
    /// Va en todo camino que ESCRIBE y después guarda. Sin esto la medición se
    /// descartaba EN SILENCIO al persistir: la pantalla decía "✓ aplicado" y el
    /// archivo (y el nodo, y OrbitX) no tenían nada — el mismo pecado que este
    /// arreglo vino a cerrar, al revés.</summary>
    public static void MarcarTocado(QxMotorConfig? m)
    {
        if (m != null) m.EsPlaceholder = false;
    }

    public int MotorActivo(string tab, int cantidad)
    {
        int i = MotorVista.TryGetValue(tab, out var v) ? v : 0;
        return (i >= 0 && i < cantidad) ? i : 0;
    }

    public int NodoCount()
    {
        int c = 0;
        var ns = Cfg?.Nodos;
        if (ns == null) return 0;
        foreach (var n in ns) if (n != null && n.Habilitado) c++;
        return c;
    }

    /// <summary>"· N2" solo si hay más de un nodo habilitado. Un nodo maneja
    /// varios motores; la tolva es otra cosa — por eso etiquetamos N (nodo).</summary>
    public string NodoTag(QxMotorEntry e) => NodoCount() <= 1 ? "" : " · N" + (e.NodeIdx + 1);

    public int TotalSurcos()
    {
        int max = AogNumSections;
        foreach (var e in AllMotors())
            if (e.Motor.Cortes != null)
                foreach (var c in e.Motor.Cortes) if (c > max) max = c;
        return Math.Max(max, 1);
    }

    /// <summary>Surco (1-based) → índice plano del motor dueño, -1 si huérfano.</summary>
    public int SurcoOwner(int surco)
    {
        var all = AllMotors();
        for (int i = 0; i < all.Count; i++)
            if (all[i].Motor.Cortes != null && all[i].Motor.Cortes.Contains(surco)) return i;
        return -1;
    }

    /// <summary>Asigna el surco al motor del pincel y lo SACA de cualquier otro
    /// motor de cualquier nodo: 1 surco = 1 motor en todo el conjunto.</summary>
    public void PintarSurco(int surco)
    {
        var all = AllMotors();
        if (BrushMotor < 0 || BrushMotor >= all.Count) return;
        for (int i = 0; i < all.Count; i++)
        {
            var m = all[i].Motor;
            m.Cortes ??= new List<int>();
            m.Cortes.RemoveAll(c => c == surco);
            if (i == BrushMotor) { m.Cortes.Add(surco); m.Cortes.Sort(); }
        }
        UltimoSurcoTocado = surco;
    }

    public List<int> SurcosHuerfanos()
    {
        var outp = new List<int>();
        if (AllMotors().Count == 0) return outp;
        int total = TotalSurcos();
        for (int s = 1; s <= total; s++) if (SurcoOwner(s) < 0) outp.Add(s);
        return outp;
    }

    /// <summary>Todos los surcos del motor están cortados (sección OFF).</summary>
    public bool MotorAllCut(QxMotorConfig m)
    {
        var cortes = m?.Cortes;
        if (cortes == null || cortes.Count == 0 || SectionOn == null) return false;
        foreach (var c in cortes)
        {
            int idx = c - 1;
            if (idx < 0 || idx >= SectionOn.Length) return false;
            if (SectionOn[idx]) return false;
        }
        return true;
    }

    public QxMotorLive? LiveMotor(string uid, int mi)
    {
        if (string.IsNullOrEmpty(uid)) return null;
        if (!LiveByUid.TryGetValue(uid, out var n) || n.MotorsLive == null) return null;
        foreach (var m in n.MotorsLive) if (m.Id == mi) return m;
        return null;
    }

    public bool NodoOnline(string uid)
        => LiveByUid.TryGetValue(uid, out var n) && n.Online;

    public string NodoFirmware(string uid)
        => LiveByUid.TryGetValue(uid, out var n) ? (n.Firmware ?? "") : "";

    // ---- trenes (espejo de TrenResolver.cs) -------------------------------

    public List<QxTrenConfig> TrenesDisponibles() => Impl?.Trenes ?? new List<QxTrenConfig>();

    /// <summary>Deriva a qué tren pertenece un motor a partir de los surcos que
    /// alimenta (cortes → surcos físicos → tren). null = no hay dato derivable
    /// y el caller usa el fallback manual del nodo.</summary>
    public QxTrenDerivado? DerivarTrenMotor(List<int>? cortes)
    {
        var ts = TrenesDisponibles();
        if (ts.Count < 2) return null;
        bool hayDistanciaReal = false;
        foreach (var t in ts) if (t.DistanciaM > 0.05) { hayDistanciaReal = true; break; }
        if (!hayDistanciaReal) return null;

        var surcosImpl = Impl?.Surcos;
        if (surcosImpl == null || surcosImpl.Count == 0) return null;

        // sección PilotX → surcos físicos
        var porSeccion = new Dictionary<int, List<int>>();
        var porSurco = new Dictionary<int, int>();
        foreach (var s in surcosImpl)
        {
            porSurco[s.Numero] = s.TrenId;
            if (s.SeccionPilotx < 1) continue;
            if (!porSeccion.TryGetValue(s.SeccionPilotx, out var lista))
                porSeccion[s.SeccionPilotx] = lista = new List<int>();
            lista.Add(s.Numero);
        }

        var surcos = new List<int>();
        if (cortes != null)
            foreach (var sec in cortes)
                if (porSeccion.TryGetValue(sec, out var lista)) surcos.AddRange(lista);
        if (surcos.Count == 0) return null;

        int? trenId = null; bool conflicto = false;
        foreach (var n in surcos)
        {
            if (!porSurco.TryGetValue(n, out var tid)) continue;
            if (trenId == null) trenId = tid;
            else if (tid != trenId) conflicto = true;
        }
        if (trenId == null) return null;

        string nombre = "Tren " + trenId.Value;
        foreach (var t in ts) if (t.Id == trenId.Value) { if (!string.IsNullOrEmpty(t.Nombre)) nombre = t.Nombre; break; }
        return new QxTrenDerivado { Id = trenId.Value, Nombre = nombre, Conflicto = conflicto };
    }

    /// <summary>Copia los trenes del implemento a cfg.trenes antes de
    /// persistir (los trenes ya no se editan en QuantiX).</summary>
    public void SyncTrenes()
    {
        var ts = Impl?.Trenes;
        if (ts == null) return;
        var nueva = new List<QxTrenConfig>();
        foreach (var t in ts)
            nueva.Add(new QxTrenConfig
            {
                Id = t.Id,
                Nombre = string.IsNullOrEmpty(t.Nombre) ? ("Tren " + t.Id) : t.Nombre,
                DistanciaM = t.DistanciaM,
            });
        Cfg.Trenes = nueva;
    }

    // ---- modo Configurar / En marcha --------------------------------------

    /// <summary>"En marcha" = sembrando de verdad: lote abierto + nodos vivos +
    /// la máquina AVANZANDO (>0,5 km/h). Sin la condición de velocidad,
    /// tener el lote abierto con un nodo prendido bastaba para ocultar la
    /// toolbar de Configurar (reporte 2026-08-10). El forzado se suelta solo
    /// cuando cae la detección, para no quedar pegado en config.</summary>
    public void ComputeEnMarcha()
    {
        EnMarchaDetectada = AogJobStarted && LiveByUid.Count > 0 && AogSpeed > 0.5;
        if (!EnMarchaDetectada) ConfigForzado = false;
    }

    /// <summary>Columnas numéricas del shapefile activo, candidatas a mapa.</summary>
    public List<string> ShapeDoseFields()
    {
        var outp = new List<string>();
        foreach (var f in ShapeFields) if (f.Numeric && !string.IsNullOrEmpty(f.Name)) outp.Add(f.Name);
        return outp;
    }

    public QxAgroCtx AgroCtx() => QxAgro.CtxFrom(Impl, AogSpeed);

    // ---- persistencia -----------------------------------------------------

    /// <summary>PUT de la config entera. Los controles de nodo la usan para
    /// aplicar al instante en vez de esperar al botón de guardar general.</summary>
    public async Task<QxOpResult> GuardarAsync(CancellationToken ct = default)
    {
        SyncTrenes();
        return await Client.PutMotoresAsync(ConfigParaGuardar(), ct).ConfigureAwait(false);
    }

    /// <summary>La config como se persiste: igual a la de memoria pero SIN los
    /// placeholders que la UI creó y el operario nunca tocó. Ese PUT es el que
    /// escribe el archivo, se publica al nodo por MQTT y termina en OrbitX: lo
    /// que salga de acá es lo que el resto del sistema cree que tiene la
    /// máquina.
    ///
    /// Filtra COPIANDO, no borrando: las tabs tienen referencias vivas a los
    /// objetos de motor (closures de los steppers y checkboxes) y sacarlos de
    /// la lista en pleno guardado dejaría al operario editando un objeto
    /// suelto, sin darse cuenta.</summary>
    private QxMotoresConfig ConfigParaGuardar()
    {
        var copia = new QxMotoresConfig
        {
            Trenes = Cfg.Trenes,
            Ignorados = Cfg.Ignorados,
            CompensarCurva = Cfg.CompensarCurva,
            Extra = Cfg.Extra,
            Nodos = new List<QxNodoConfig>(),
        };
        var ns = Cfg?.Nodos;
        if (ns == null) return copia;

        foreach (var n in ns)
        {
            if (n == null) continue;
            var reales = new List<QxMotorConfig>();
            if (n.Motores != null)
                foreach (var m in n.Motores)
                    if (m != null && !EsPlaceholderSinTocar(m)) reales.Add(m);

            if (n.Motores == null || reales.Count == n.Motores.Count) { copia.Nodos.Add(n); continue; }

            copia.Nodos.Add(new QxNodoConfig
            {
                Uid = n.Uid,
                Nombre = n.Nombre,
                Habilitado = n.Habilitado,
                DistanciaEntreTrenes = n.DistanciaEntreTrenes,
                Extra = n.Extra,
                Motores = reales,
            });
        }
        return copia;
    }
}
