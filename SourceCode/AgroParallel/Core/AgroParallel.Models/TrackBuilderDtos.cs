// ============================================================================
// TrackBuilderDtos.cs — POCOs para el gestor de tracks HTML (tracks.html).
// Wire snake_case por AgpJson; nombres C# PascalCase.
// ============================================================================

using System.Collections.Generic;

namespace AgroParallel.Models
{
    public sealed class TrackBuilderStateDto
    {
        public bool Ok { get; set; } = true;
        public List<TrackItemDto> Tracks { get; set; } = new List<TrackItemDto>();
        public int SelectedIdx { get; set; } = -1;
        public int ActiveIdx { get; set; } = -1;  // trk.idx (guía activa para el guiado)
        public string Error { get; set; }
    }
}
