using CommunityToolkit.Mvvm.ComponentModel;

namespace PilotX.Cockpit.Bars.ViewModels;

/// <summary>
/// Un botón de sección de la barra inferior. Espejo de los `btnSectionXMan` del
/// panelBottom nativo: muestra el número y se pinta según el estado del botón
/// (Off/Auto/On), NO según si la sección está aplicando en este instante.
///
/// Los colores son los mismos del nativo (`SetColors`, Sections.Designer.cs):
/// rojo = apagada, verde = automático, ámbar = forzada a mano.
/// </summary>
public sealed partial class SeccionBotonViewModel : ObservableObject
{
    /// <summary>Número que ve el operario (1..16). El comando que se manda al
    /// motor es "seccion_&lt;Numero&gt;".</summary>
    public int Numero { get; }

    /// <summary>Comando que se manda al motor: <c>seccion_&lt;n&gt;</c> en modo
    /// secciones individuales, <c>zona_&lt;n&gt;</c> en modo zonas.</summary>
    public string Comando { get; }

    public SeccionBotonViewModel(int numero, string? comando = null)
    {
        Numero = numero;
        Comando = comando ?? ("seccion_" + numero);
        Texto = numero.ToString();
    }

    [ObservableProperty] private string _texto = "";

    /// <summary>0=Off, 1=Auto, 2=On (mismo orden que btnStates).</summary>
    [ObservableProperty] private int _estado;

    /// <summary>Color de fondo, ya resuelto para el binding.</summary>
    [ObservableProperty] private string _color = ColorOff;

    // Paleta del nativo (rama isDay de SetColors), alineada con el design system.
    public const string ColorOff = "#B23E3E";   // apagada
    public const string ColorAuto = "#4ABA3E";  // automático (acento PilotX)
    public const string ColorOn = "#C49A2E";    // forzada a mano

    public void SetEstado(int estado)
    {
        Estado = estado;
        Color = estado switch
        {
            1 => ColorAuto,
            2 => ColorOn,
            _ => ColorOff,
        };
    }
}
