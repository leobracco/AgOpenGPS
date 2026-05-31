// IGuidanceCalculator — XTE, heading error, steer-angle command, modo activo.
// Hoy hay 7.000 LOC en GPS/Classes/ (CABLine + CABCurve + CYouTurn + CGuidance
// + CTrack + CContour + CHead) que calculan esto y están atados a FormGPS.
// La impl FormGpsGuidanceCalculator envuelve los objetos vivos y traduce a DTO.
// Cuando algún día reemplacemos PilotX por una pipeline propia, hacés una segunda
// impl PilotXGuidanceCalculator y el view ni se entera.

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IGuidanceCalculator
    {
        GuidanceSnapshot GetSnapshot();
    }
}
