// ============================================================================
// ExploradorArchivos.cs — puerta de entrada al explorador de archivos PROPIO
// de PilotX (ExploradorPanel) para los paneles que antes abrían el
// StorageProvider de Windows.
//
// Contrato con los llamadores:
//   · CardDisponible dice si la card nativa está lista (Windows + MainWindow
//     ya la publicó). Si es false, el llamador usa SU camino StorageProvider
//     de siempre — así Linux/Android no pierden el picker del sistema y el
//     comportamiento aguas abajo no cambia.
//   · ElegirAsync/GuardarAsync devuelven RUTAS locales (no IStorageFile):
//     el llamador lee con File/FileInfo, que es lo que la cabina necesita
//     (p. ej. FirmwaresPanel valida 8 MB/1 KB con FileInfo.Length).
//
// Por qué estático y no inyectado: la card es única por ventana y la publica
// MainWindow en ExploradorPanel.Instancia (mismo espíritu que el toast por el
// evento Aviso). Inyectarla habría tocado 4 firmas de Attach + los caminos de
// embebido de ConfigPanel para el mismo resultado.
// ============================================================================

using System;
using System.Threading.Tasks;
using PilotX.Desktop.Views;

namespace PilotX.Desktop.Services;

public static class ExploradorArchivos
{
    /// <summary>La card nativa está disponible (Windows, y MainWindow ya la
    /// montó). En false, el llamador cae a su StorageProvider de siempre.</summary>
    public static bool CardDisponible
        => OperatingSystem.IsWindows() && ExploradorPanel.Instancia != null;

    /// <summary>Elegir archivo(s). Devuelve rutas locales o null si canceló
    /// (o si la card no está — el llamador ya chequeó CardDisponible).</summary>
    public static Task<string[]?> ElegirAsync(string titulo, string[] extensiones, bool multiple = false)
    {
        var card = ExploradorPanel.Instancia;
        if (card == null) return Task.FromResult<string[]?>(null);
        return card.ElegirAsync(titulo, extensiones, multiple);
    }

    /// <summary>Guardar: devuelve la ruta destino (extensión incluida) o null.
    /// La confirmación de pisar un archivo existente ya pasó adentro.</summary>
    public static Task<string?> GuardarAsync(string titulo, string nombreSugerido, string extension)
    {
        var card = ExploradorPanel.Instancia;
        if (card == null) return Task.FromResult<string?>(null);
        return card.GuardarAsync(titulo, nombreSugerido, extension);
    }
}
