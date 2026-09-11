using AgOpenGPS.Core.Models;
using System.Collections.Generic;

namespace AgOpenGPS
{
    public partial class CBoundaryList
    {
        //list of coordinates of boundary line
        public List<vec3> fenceLine = new List<vec3>(128);

        public List<vec2> fenceLineEar = new List<vec2>(128);
        public List<vec3> hdLine = new List<vec3>(128);
        public List<vec3> turnLine = new List<vec3>(128);

        // Lindero VIRTUAL de "Marcar giro" (rectángulo sintético o clon recortado):
        // existe solo en memoria para que el U-turn tenga línea de giro. NUNCA se
        // guarda a Boundary.txt, no aparece en el snapshot del mapa como lindero y
        // no cuenta para el área del lote.
        public bool isVirtualTurnBoundary = false;

        //constructor
        public CBoundaryList()
        {
            area = 0;
            isDriveThru = false;
        }

        public GeoLineSegment GetHeadLineSegment(int index)
        {
            int nextIndex = (index + 1) % hdLine.Count;
            return new GeoLineSegment(hdLine[index].ToGeoCoord(), hdLine[nextIndex].ToGeoCoord());
        }

    }
}