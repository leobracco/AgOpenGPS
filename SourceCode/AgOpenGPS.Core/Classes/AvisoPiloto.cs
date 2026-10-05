// ============================================================================
// AvisoPiloto.cs — marca de los mensajes que avisan que el piloto se soltó.
//
// Core avisa con mf.TimedMessageBox(título, mensaje), que en el motor
// headless solo iba al log. El motor reconoce este título y además publica
// el mensaje en el state para que la cabina muestre el cartel. El resto de
// los TimedMessageBox (ej. "Forward is Set") siguen yendo solo al log.
// ============================================================================

namespace AgOpenGPS
{
    public static class AvisoPiloto
    {
        public const string TituloDesenganche = "Piloto desenganchado";
    }
}
