// ============================================================================
// ITramSimpleService.cs — contrato del editor "Tramlines simples" (FormTram).
// Todas las operaciones corren la geometría en el hilo UI de FormGPS y devuelven
// el estado completo para refrescar el panel. La preview se ve en el mapa.
// ============================================================================

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface ITramSimpleService
    {
        // Replica FormTram_Load: fija generateMode según trams/contorno existentes,
        // construye si no hay, deja el displayMode visible. Devuelve estado inicial.
        TramSimpleStateDto Open();

        // Estado actual sin tocar nada (por si el panel quiere refrescar).
        TramSimpleStateDto GetState();

        // Cambia pasadas (>=1), persiste el setting y reconstruye el tram.
        TramSimpleStateDto SetPasses(int passes);

        // Cambia la transparencia del dibujo (0..100 %). No reconstruye.
        TramSimpleStateDto SetAlpha(int percent);

        // Cambia el modo de generación ("All"/"FillTracks"/"BoundaryTracks") y reconstruye.
        TramSimpleStateDto SetMode(string mode);

        // Invierte la dirección de la guía activa (swap A↔B) y reconstruye el tram.
        TramSimpleStateDto SwapAB();

        // Cierra el editor. save=true conserva y persiste; save=false descarta la
        // preview. Ambos guardan el tram a archivo y refrescan paneles/botón.
        bool Commit(bool save);
    }
}
