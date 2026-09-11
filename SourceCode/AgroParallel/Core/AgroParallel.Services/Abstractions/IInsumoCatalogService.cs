// IInsumoCatalogService — CRUD del catálogo compartido de insumos
// (semillas / fertilizantes / fitosanitarios). Lo consumen VistaX, QuantiX,
// FlowX y la página datos-lote.html para evitar tipear la dosis en cada lado.

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IInsumoCatalogService
    {
        InsumoCatalogDto Load();
        void Save(InsumoCatalogDto dto);

        /// <summary>Devuelve el InsumoDto activo (cuyo Id == catalog.ActivoId).
        /// null si no hay activo o el id no matchea ningún item.</summary>
        InsumoDto GetActivo();

        /// <summary>Setea el insumo activo por Id y persiste. Devuelve false si
        /// el Id no existe en el catálogo (no cambia nada en ese caso).</summary>
        bool SetActivo(string id);
    }
}
