using NUnit.Framework;
using PilotX.Cockpit.Bars.Services;

namespace PilotX.Cockpit.Bars.Tests;

public class CockpitStateClientTests
{
    [Test]
    public void Parse_MapsSnakeCaseJson()
    {
        // AgpJson emite snake_case; los JsonPropertyName deben matchear EXACTO
        // (PropertyNameCaseInsensitive no cubre underscores).
        const string json = """
        {"is_job_started":true,"avg_speed":8.5,"heading":1.57,"fix_quality":4,
         "worked_area_total_m2":12345.0,"tracks_total":23,"track_idx":10,
         "is_auto_steer_on":true,"has_headland":true,"tram_display_mode":2,
         "row_skips_width":3,"flag_color":1}
        """;
        var snap = CockpitStateClient.Parse(json);
        Assert.That(snap, Is.Not.Null);
        Assert.That(snap!.IsJobStarted, Is.True);
        Assert.That(snap.AvgSpeed, Is.EqualTo(8.5).Within(0.001));
        Assert.That(snap.FixQuality, Is.EqualTo(4));
        Assert.That(snap.TracksTotal, Is.EqualTo(23));
        Assert.That(snap.TrackIdx, Is.EqualTo(10));
        Assert.That(snap.IsAutoSteerOn, Is.True);
        Assert.That(snap.HasHeadland, Is.True);
        Assert.That(snap.TramDisplayMode, Is.EqualTo(2));
        Assert.That(snap.RowSkipsWidth, Is.EqualTo(3));
        Assert.That(snap.FlagColor, Is.EqualTo(1));
    }
}
