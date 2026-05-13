// AogStateSnapshot — DTO read-only del estado de AOG que los servicios
// AgroParallel consumen. Producido por IAogStateProvider.

using System.Collections.Generic;

namespace AgroParallel.Models
{
    /// <summary>Punto 2D en metros locales (Easting/Northing).</summary>
    public sealed class FieldPoint
    {
        public double E { get; set; }
        public double N { get; set; }
        public FieldPoint() { }
        public FieldPoint(double e, double n) { E = e; N = n; }
    }

    /// <summary>Track activo (AB line / curve / pivote). null si no hay.</summary>
    public sealed class TrackInfo
    {
        /// <summary>Nombre puesto en AOG.</summary>
        public string Name { get; set; }

        /// <summary>"AB" | "Curve" | "Pivot" | "None" | otro string.</summary>
        public string Mode { get; set; }

        /// <summary>Heading en rad (solo válido para modo AB).</summary>
        public double Heading { get; set; }

        /// <summary>Punto A (para AB lines). null si no aplica.</summary>
        public FieldPoint A { get; set; }
        /// <summary>Punto B (para AB lines). null si no aplica.</summary>
        public FieldPoint B { get; set; }

        /// <summary>Curva discretizada (para Curve / Pivot). null o vacío si no aplica.</summary>
        public List<FieldPoint> CurvePts { get; set; }
    }

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

        // ---- Geometría del lote (todo en metros locales, mismo frame que PivotEasting/Northing) ----

        /// <summary>Boundaries del lote. El primero es el contorno externo,
        /// los siguientes (si hay) son drive-thru islands.</summary>
        public List<List<FieldPoint>> Boundaries { get; set; }

        /// <summary>Cabeceras (headland offset). Una por boundary; puede estar vacía.</summary>
        public List<List<FieldPoint>> Headlands { get; set; }

        /// <summary>Track de guía actualmente activo (AB line / curve / pivot). null si no hay.</summary>
        public TrackInfo ActiveTrack { get; set; }
    }
}
