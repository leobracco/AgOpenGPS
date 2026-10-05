// ISectionXConfigService — CRUD de sectionX.json (mapeo cables → secciones PilotX).

using System;
using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface ISectionXConfigService
    {
        SectionXConfigDto Load();
        void Save(SectionXConfigDto dto);

        // Disparado cuando el JSON se guarda exitosamente. PilotX/FormGPS se
        // suscribe a esto para llamar ReloadSectionXBridge() — sin este evento
        // el bridge se queda con la config vieja (típicamente null si arrancó
        // con nodos:[]) y `/sections` nunca se publica al broker MQTT, así que
        // los relays de los nodos QuantiX/SectionX nunca se activan aunque la
        // UI haya guardado correctamente. Bug observado 2026-05-19.
        event Action ConfigSaved;
    }
}
