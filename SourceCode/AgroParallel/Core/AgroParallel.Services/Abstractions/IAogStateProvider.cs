// IAogStateProvider — linchpin del desacople AgroParallel ↔ PilotX.
// Los servicios (QuantiX/SectionX/OrbitX) consumen estado de PilotX SOLO
// a través de esta interfaz. La implementación FormGpsStateProvider vive
// en el proyecto GPS (único lugar autorizado a usar `using AgOpenGPS;`).

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IAogStateProvider
    {
        /// <summary>Snapshot tipado del estado de PilotX en este instante.</summary>
        AogStateSnapshot GetSnapshot();

        /// <summary>
        /// Volcado completo de ajustes estáticos + telemetría en vivo, ya
        /// formateado como pares etiqueta/valor. Reemplaza la vieja ventana
        /// WinForms "View All Settings" (FormAllSettings) por la página HTML
        /// /pages/ajustes-todos.html.
        /// </summary>
        AllSettingsSnapshot GetAllSettings();

        /// <summary>
        /// Registro de eventos: cola del log persistido en disco + buffer de la
        /// sesión actual. Reemplaza la vieja ventana WinForms FormEventViewer
        /// por la página HTML /pages/eventos.html.
        /// </summary>
        EventLogSnapshot GetEventLog();

        /// <summary>
        /// Muestra en vivo (error de rumbo + XTE) para el gráfico de guiado.
        /// Reemplaza la ventana WinForms FormGraphXTE por la página HTML
        /// /pages/grafico-xte.html, que arma su propio buffer rodante.
        /// </summary>
        XteGraphSample GetXteGraphSample();

        /// <summary>
        /// Muestra en vivo (rumbo GPS + rumbo IMU corregido) para el gráfico de
        /// rumbo. Reemplaza la ventana WinForms FormGraphHeading por la página
        /// HTML /pages/grafico-rumbo.html, que arma su propio buffer rodante.
        /// </summary>
        HeadingGraphSample GetHeadingGraphSample();

        /// <summary>
        /// Muestra en vivo (ángulo de dirección real + seteado) para el gráfico
        /// de dirección. Reemplaza la ventana WinForms FormGraphSteer por la
        /// página HTML /pages/grafico-direccion.html, con buffer rodante propio.
        /// </summary>
        SteerGraphSample GetSteerGraphSample();

        /// <summary>
        /// Muestra en vivo (distancia de corrección por roll, easting crudo y sin
        /// corregir, roll del IMU) para el gráfico de chequeo de roll. Reemplaza la
        /// ventana WinForms FormCorrection por la página HTML
        /// /pages/grafico-correccion.html, con buffer rodante propio.
        /// </summary>
        CorrectionGraphSample GetCorrectionGraphSample();

        /// <summary>
        /// Estado del corrimiento de deriva GPS (norte/este en cm + si se
        /// mantiene aplicado). Reemplaza la ventana WinForms FormShiftPos por la
        /// página HTML /pages/corregir-posicion.html. Las escrituras van por
        /// POST /api/aog/guidance/command (shift_north_/shift_east_/shift_zero/
        /// offsets_on/offsets_off).
        /// </summary>
        ShiftPosSnapshot GetShiftPos();

        /// <summary>
        /// Coordenadas guardadas del simulador (lat/lon) + si se puede aplicar
        /// (simulador encendido y sin lote abierto). Reemplaza la ventana WinForms
        /// FormSimCoords por la página HTML /pages/sim-coords.html. Aplicar va por
        /// POST /api/aog/guidance/command (sim_coords_&lt;lat&gt;_&lt;lon&gt;).
        /// </summary>
        SimCoordsSnapshot GetSimCoords();

        /// <summary>
        /// Devuelve los 16 colores de sección guardados (hex "#RRGGBB") y el flag
        /// multicolor. Reemplaza la ventana WinForms FormColorSection por la página
        /// HTML /pages/colores-secciones.html. Aplicar va por POST
        /// /api/aog/guidance/command (sec_colors_&lt;hex1&gt;_..._&lt;hex16&gt;_&lt;0|1&gt;).
        /// </summary>
        SectionColorsSnapshot GetSectionColors();

        /// <summary>
        /// Devuelve los colores de display (marco/campo/texto para día y noche),
        /// el suavizado de cámara y si está en modo día. Reemplaza la ventana
        /// WinForms FormColor por la página HTML /pages/colores.html. Aplicar va por
        /// POST /api/aog/guidance/command (display_colors_&lt;frameDay&gt;_&lt;frameNight&gt;
        /// _&lt;fieldDay&gt;_&lt;fieldNight&gt;_&lt;textDay&gt;_&lt;textNight&gt;_&lt;camSmooth&gt;_&lt;0|1&gt;).
        /// </summary>
        DisplayColorsSnapshot GetDisplayColors();

        /// <summary>
        /// Lee el valor numérico de un campo DBF arbitrario del shapefile
        /// activo, en el polígono actualmente bajo el tractor. Retorna 0 si
        /// no hay shapefile, no hay polígono, o el campo no es numérico.
        /// Habilita motores QuantiX con `CampoDosis` apuntando a un atributo
        /// específico del shape (multi-producto en una sola capa).
        /// </summary>
        double GetShapeFieldDose(string fieldName);

        /// <summary>
        /// Polígonos de la capa shapefile activa (prescripción/dosis). El
        /// cliente la pinta como overlay del mapa. Devuelve null si no hay
        /// shapefile cargado.
        /// </summary>
        ShapeSnapshot GetShape();

        /// <summary>
        /// Lista los campos DBF del shapefile activo con su stats numérico.
        /// La UI de QuantiX la usa para poblar el dropdown CampoDosis.
        /// Si no hay shapefile, devuelve un ShapeFieldsSnapshot vacío.
        /// </summary>
        ShapeFieldsSnapshot GetShapeFields();
    }
}
