// ============================================================================
// FlagDtos.cs — DTOs del widget "Banderas" (banderas.html).
// Reemplaza a FormFlags + FormEnterFlag. Wire snake_case vía AgpJson.
// ============================================================================

using System.Collections.Generic;

namespace AgroParallel.Models
{
    /// <summary>Una bandera del lote.</summary>
    public sealed class FlagItemDto
    {
        /// <summary>Posición 1-based en la lista (lo que se usa para seleccionar).</summary>
        public int Number { get; set; }

        /// <summary>ID interno de la bandera (solo display, puede tener huecos).</summary>
        public int Id { get; set; }

        /// <summary>0 = rojo, 1 = verde, 2 = amarillo.</summary>
        public int Color { get; set; }

        public string Notes { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }

        /// <summary>Distancia actual del tractor a la bandera, en metros.</summary>
        public double DistanceM { get; set; }

        /// <summary>Coordenadas en el plano local del lote (mismo sistema que
        /// guías/lindero/cobertura). El mapa GL no puede dibujar con Lat/Lon
        /// directo: necesita esto para ubicar la bandera junto a todo lo demás.</summary>
        public double Easting { get; set; }
        public double Northing { get; set; }
    }

    /// <summary>Estado completo del widget de banderas.</summary>
    public sealed class FlagsStateDto
    {
        public bool Ok { get; set; }
        public bool HasField { get; set; }

        /// <summary>Bandera seleccionada (Number 1-based; 0 = ninguna).</summary>
        public int Picked { get; set; }

        /// <summary>Posición actual del tractor (prefill de "nueva bandera").</summary>
        public double CurLat { get; set; }
        public double CurLon { get; set; }

        public List<FlagItemDto> Flags { get; set; }

        public string Error { get; set; }
    }
}
