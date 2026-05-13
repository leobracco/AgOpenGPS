// AogStateSnapshot — DTO read-only del estado de AOG que los servicios
// AgroParallel consumen. Producido por IAogStateProvider.

namespace AgroParallel.Models
{
    /// <summary>
    /// Snapshot inmutable del estado relevante de AOG en un instante dado.
    /// Lo que necesitan los bridges (QuantiX/SectionX/OrbitX) para operar
    /// sin tener una referencia directa a FormGPS.
    /// </summary>
    public sealed class AogStateSnapshot
    {
        public bool IsJobStarted { get; set; }
        public string CurrentFieldDirectory { get; set; }

        public double AvgSpeed { get; set; }      // km/h
        public double Heading { get; set; }       // rad

        public double PivotEasting { get; set; }
        public double PivotNorthing { get; set; }

        public double Latitude { get; set; }
        public double Longitude { get; set; }

        public int NumSections { get; set; }
        // sectionOnRequest por índice [0..NumSections-1].
        public bool[] SectionOnRequest { get; set; }

        /// <summary>Ancho de la herramienta en metros (tool.width).</summary>
        public double ToolWidth { get; set; }

        /// <summary>Dosis actual leída del shapefile (kg/ha) en la posición
        /// del tractor. 0 si no hay shapefile o si IsInsideShape == false.</summary>
        public double ShapeCurrentDose { get; set; }

        /// <summary>Tractor está dentro de un polígono del shapefile activo.</summary>
        public bool ShapeIsInside { get; set; }

        /// <summary>"Tractor" | "Harvester" | "Articulated". Lo que viene de Settings.</summary>
        public string VehicleType { get; set; }

        /// <summary>Marca del vehículo: "AGOpenGPS","JohnDeere","Fendt", etc.
        /// Se mapea a un sprite en /img/{tractors|harvesters}/.</summary>
        public string VehicleBrand { get; set; }
    }
}
