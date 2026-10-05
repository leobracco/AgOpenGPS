// ============================================================================
// PlanimetriaClient.cs — cliente de /api/planimetria (planimetría fase 3 en la
// cabina). Lo usan la pestaña Configuración › GPS / IMU › Planimetría y el
// poller de la capa del mapa.
//
// Los DTOs son copia de AgroParallel.Models/PlanimetriaDtos.cs (la UI no
// referencia Models; mismo criterio que ConfigVehiculoClient). snake_case.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

public sealed class PlaniAmbientes
{
    [JsonPropertyName("modo")] public string Modo { get; set; } = "percentil";
    [JsonPropertyName("p_bajo")] public double PBajo { get; set; } = 25;
    [JsonPropertyName("p_loma")] public double PLoma { get; set; } = 75;
    [JsonPropertyName("d_bajo_m")] public double DBajoM { get; set; } = 0.3;
    [JsonPropertyName("d_loma_m")] public double DLomaM { get; set; } = 0.3;
    [JsonPropertyName("cota_bajo_m")] public double? CotaBajoM { get; set; }
    [JsonPropertyName("cota_loma_m")] public double? CotaLomaM { get; set; }
    [JsonPropertyName("area_bajo_ha")] public double? AreaBajoHa { get; set; }
    [JsonPropertyName("area_media_ha")] public double? AreaMediaHa { get; set; }
    [JsonPropertyName("area_loma_ha")] public double? AreaLomaHa { get; set; }
}

public sealed class PlaniDosis
{
    [JsonPropertyName("bajo")] public double Bajo { get; set; }
    [JsonPropertyName("media")] public double Media { get; set; }
    [JsonPropertyName("loma")] public double Loma { get; set; }
}

public sealed class PlaniEstado
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("habilitada")] public bool Habilitada { get; set; }
    [JsonPropertyName("capa_visible")] public bool CapaVisible { get; set; }
    [JsonPropertyName("modo_capa")] public string ModoCapa { get; set; } = "alturas";
    [JsonPropertyName("estado")] public string Estado { get; set; } = "apagado";
    [JsonPropertyName("motivo")] public string? Motivo { get; set; }
    [JsonPropertyName("lote")] public string? Lote { get; set; }
    [JsonPropertyName("rev")] public int Rev { get; set; }
    [JsonPropertyName("puntos_rtk")] public int PuntosRtk { get; set; }
    [JsonPropertyName("pasadas")] public int Pasadas { get; set; }
    [JsonPropertyName("res_m")] public double? ResM { get; set; }
    [JsonPropertyName("intervalo_m")] public double? IntervaloM { get; set; }
    [JsonPropertyName("z_min_m")] public double? ZMinM { get; set; }
    [JsonPropertyName("z_max_m")] public double? ZMaxM { get; set; }
    [JsonPropertyName("desnivel_m")] public double? DesnivelM { get; set; }
    [JsonPropertyName("area_ha")] public double? AreaHa { get; set; }
    [JsonPropertyName("pendiente_media_pct")] public double? PendienteMediaPct { get; set; }
    [JsonPropertyName("pendiente_max_pct")] public double? PendienteMaxPct { get; set; }
    [JsonPropertyName("bajos_cantidad")] public int BajosCantidad { get; set; }
    [JsonPropertyName("bajos_area_ha")] public double? BajosAreaHa { get; set; }
    [JsonPropertyName("nivelacion_aplicada")] public bool NivelacionAplicada { get; set; }
    [JsonPropertyName("sesgo_antes_cm")] public double? SesgoAntesCm { get; set; }
    [JsonPropertyName("sesgo_despues_cm")] public double? SesgoDespuesCm { get; set; }
    [JsonPropertyName("calculo_ms")] public long CalculoMs { get; set; }
    [JsonPropertyName("ambientes")] public PlaniAmbientes Ambientes { get; set; } = new();
    [JsonPropertyName("dosis")] public PlaniDosis Dosis { get; set; } = new();
    [JsonPropertyName("cota_tractor_m")] public double? CotaTractorM { get; set; }
    [JsonPropertyName("cota_guia_m")] public double? CotaGuiaM { get; set; }
    [JsonPropertyName("ultima_guia")] public string? UltimaGuia { get; set; }
    [JsonPropertyName("ultima_prescripcion")] public string? UltimaPrescripcion { get; set; }
}

public sealed class PlaniAccion
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("mensaje")] public string? Mensaje { get; set; }
    [JsonPropertyName("nombre")] public string? Nombre { get; set; }
    [JsonPropertyName("cota_m")] public double? CotaM { get; set; }
    [JsonPropertyName("puntos")] public int Puntos { get; set; }
    [JsonPropertyName("poligonos")] public int Poligonos { get; set; }
}

public sealed class PlaniCurva
{
    [JsonPropertyName("elev")] public double Elev { get; set; }
    [JsonPropertyName("maestra")] public bool Maestra { get; set; }
    [JsonPropertyName("lineas")] public List<float[]>? Lineas { get; set; }
}

public sealed class PlaniCapa
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("rev")] public int Rev { get; set; }
    [JsonPropertyName("nx")] public int Nx { get; set; }
    [JsonPropertyName("ny")] public int Ny { get; set; }
    [JsonPropertyName("e_oeste")] public double EOeste { get; set; }
    [JsonPropertyName("e_este")] public double EEste { get; set; }
    [JsonPropertyName("n_sur")] public double NSur { get; set; }
    [JsonPropertyName("n_norte")] public double NNorte { get; set; }
    [JsonPropertyName("z_base")] public double ZBase { get; set; }
    [JsonPropertyName("z_min")] public double ZMin { get; set; }
    [JsonPropertyName("z_max")] public double ZMax { get; set; }
    [JsonPropertyName("z_cm")] public string? ZCm { get; set; }
    [JsonPropertyName("zona")] public string? Zona { get; set; }
    [JsonPropertyName("intervalo_m")] public double IntervaloM { get; set; }
    [JsonPropertyName("modo_capa")] public string? ModoCapa { get; set; }
    [JsonPropertyName("cota_guia_m")] public double? CotaGuiaM { get; set; }
    [JsonPropertyName("curvas")] public List<PlaniCurva>? Curvas { get; set; }
    [JsonPropertyName("curva_guia")] public PlaniCurva? CurvaGuia { get; set; }
}

public sealed class PlanimetriaClient
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };
    private static readonly char[] Basura = { (char)0xFEFF, (char)0x200B };

    private readonly HttpClient _http;
    private readonly string _base;

    public PlanimetriaClient(string baseUrl, TimeSpan? timeout = null)
    {
        _base = (string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/" : baseUrl).TrimEnd('/') + "/";
        _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(4) };
    }

    /// <summary>Estado. null = sin conexión; ok=false error="service-unavailable" =
    /// el motor responde pero no trae planimetría (404: motor viejo o Android).</summary>
    public async Task<PlaniEstado?> EstadoAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_base + "api/planimetria", ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new PlaniEstado { Ok = false, Error = "service-unavailable" };
            if (!resp.IsSuccessStatusCode) return null;
            string json = (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).TrimStart(Basura);
            return JsonSerializer.Deserialize<PlaniEstado>(json, Opts);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    public Task<PlaniCapa?> CapaAsync(CancellationToken ct = default) => GetAsync<PlaniCapa>("api/planimetria/capa", ct);

    /// <summary>Cambios parciales: pasar un objeto anónimo con las claves snake del wire.</summary>
    public Task<PlaniEstado?> ConfigurarAsync(object cambios, CancellationToken ct = default)
        => PostAsync<PlaniEstado>("api/planimetria/config", cambios, ct);

    public Task<PlaniEstado?> CalcularAsync(CancellationToken ct = default)
        => PostAsync<PlaniEstado>("api/planimetria/calcular", new { }, ct);

    public Task<PlaniAccion?> CrearGuiaAsync(double? cota, CancellationToken ct = default)
        => PostAsync<PlaniAccion>("api/planimetria/guia", new { cota_m = cota }, ct);

    public Task<PlaniAccion?> CrearPrescripcionAsync(CancellationToken ct = default)
        => PostAsync<PlaniAccion>("api/planimetria/prescripcion", new { }, ct);

    private async Task<T?> GetAsync<T>(string ruta, CancellationToken ct) where T : class
    {
        try
        {
            using var resp = await _http.GetAsync(_base + ruta, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            string json = (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).TrimStart(Basura);
            return JsonSerializer.Deserialize<T>(json, Opts);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private async Task<T?> PostAsync<T>(string ruta, object cuerpo, CancellationToken ct) where T : class
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(cuerpo), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_base + ruta, content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            string json = (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).TrimStart(Basura);
            return JsonSerializer.Deserialize<T>(json, Opts);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }
}
