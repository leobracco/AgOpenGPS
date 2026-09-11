// ============================================================================
// INodosCuratedService.cs
// Capa de identidad curada por encima de NodoRegistryService.
// Persiste qué nodos aceptó/ignoró el operario y con qué alias humano.
// ============================================================================

using System.Collections.Generic;
using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface INodosCuratedService
    {
        NodosCuratedDto Load();
        void Save(NodosCuratedDto dto);

        /// <summary>Vista combinada con telemetría runtime. Si el registry es null devuelve solo curados.</summary>
        IReadOnlyList<NodoUnifiedDto> GetUnified(INodoRegistryService registry);

        void Aceptar(string uid, string tipo, string alias);
        void Ignorar(string uid);
        void Restaurar(string uid);
        void Eliminar(string uid);
        void Renombrar(string uid, string nuevoAlias);
    }
}
