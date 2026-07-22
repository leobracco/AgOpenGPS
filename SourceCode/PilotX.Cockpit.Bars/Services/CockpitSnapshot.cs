using System.Text.Json.Serialization;

namespace PilotX.Cockpit.Bars.Services;

/// <summary>Subset del AogStateSnapshot que consumen las barras del cockpit.
/// El WebHost emite snake_case (AgpJson); los JsonPropertyName lo matchean.</summary>
public sealed class CockpitSnapshot
{
    // Barra superior
    [JsonPropertyName("is_job_started")]      public bool IsJobStarted { get; set; }
    [JsonPropertyName("avg_speed")]           public double AvgSpeed { get; set; }   // km/h
    [JsonPropertyName("heading")]             public double Heading { get; set; }    // rad
    [JsonPropertyName("fix_quality")]         public int FixQuality { get; set; }    // 4=RTK FIJO,5=FLOAT,2=DGPS,1=GPS,8=SIM
    [JsonPropertyName("worked_area_total_m2")] public double WorkedAreaTotalM2 { get; set; }
    [JsonPropertyName("tracks_total")]        public int TracksTotal { get; set; }
    [JsonPropertyName("tracks_visible")]      public int TracksVisible { get; set; }
    [JsonPropertyName("track_idx")]           public int TrackIdx { get; set; }      // -1 = sin guía
    // Barra derecha
    [JsonPropertyName("is_auto_steer_on")]    public bool IsAutoSteerOn { get; set; }
    [JsonPropertyName("is_auto_snap_to_pivot")] public bool IsAutoSnapToPivot { get; set; }
    [JsonPropertyName("is_you_turn_on")]      public bool IsYouTurnOn { get; set; }
    [JsonPropertyName("is_section_auto_on")]  public bool IsSectionAutoOn { get; set; }
    [JsonPropertyName("is_section_manual_on")] public bool IsSectionManualOn { get; set; }
    [JsonPropertyName("isobus_alive")]        public bool IsobusAlive { get; set; }
    [JsonPropertyName("isobus_on")]           public bool IsobusOn { get; set; }
    [JsonPropertyName("is_auto_track_on")]    public bool IsAutoTrackOn { get; set; }
    [JsonPropertyName("is_contour_on")]       public bool IsContourOn { get; set; }
    [JsonPropertyName("is_contour_locked")]   public bool IsContourLocked { get; set; }
    [JsonPropertyName("has_boundary")]        public bool HasBoundary { get; set; }
    // Barra abajo
    [JsonPropertyName("flag_color")]          public int FlagColor { get; set; }     // 0 roja,1 verde,2 amarilla
    [JsonPropertyName("is_nudge_on")]         public bool IsNudgeOn { get; set; }
    [JsonPropertyName("has_headland")]        public bool HasHeadland { get; set; }
    [JsonPropertyName("is_headland_on")]      public bool IsHeadlandOn { get; set; }
    [JsonPropertyName("is_section_controlled_by_headland")] public bool IsSectionControlledByHeadland { get; set; }
    [JsonPropertyName("has_hyd_lift")]        public bool HasHydLift { get; set; }
    [JsonPropertyName("is_hyd_lift_on")]      public bool IsHydLiftOn { get; set; }
    [JsonPropertyName("has_tram")]            public bool HasTram { get; set; }
    [JsonPropertyName("tram_display_mode")]   public int TramDisplayMode { get; set; } // 0..3
    [JsonPropertyName("you_skip_mode")]       public int YouSkipMode { get; set; }     // 0..2
    [JsonPropertyName("row_skips_width")]     public int RowSkipsWidth { get; set; }   // 1..10
}
