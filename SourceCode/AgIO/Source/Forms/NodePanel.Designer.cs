// ============================================================================
// NodePanel.Designer.cs — Mini HTTP+SSE server en AgIO con PWA embebida.
// Sirve un panel web LAN para ver el estado live de todos los nodos MQTT.
// Patrón partial de FormLoop, igual que MQTT.Designer.cs / UDP.designer.cs.
// ============================================================================

using AgLibrary.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AgIO
{
    public partial class FormLoop
    {
        // ── Estado ─────────────────────────────────────────────────────
        private HttpListener _npListener;
        private Thread _npThread;
        private CancellationTokenSource _npCts;
        private bool _npRunning;
        private int _npPort = 8080;

        // Cache de nodos por UID.
        private readonly ConcurrentDictionary<string, NodeSnapshot> _npNodes
            = new ConcurrentDictionary<string, NodeSnapshot>(StringComparer.OrdinalIgnoreCase);

        // Clientes SSE conectados.
        private readonly List<StreamWriter> _npSseClients = new List<StreamWriter>();
        private readonly object _npSseLock = new object();

        // ── DTO ────────────────────────────────────────────────────────
        public class NodeSnapshot
        {
            public string Uid;
            public string Type;
            public string Ip;
            public string Version;
            public string Hw;
            public DateTime FirstSeen;
            public DateTime LastSeen;
            public long MessageCount;
            // subtopic (ej "status_live", "sections", "target") -> último payload string
            public ConcurrentDictionary<string, string> Topics
                = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // ── Arranque ───────────────────────────────────────────────────
        private void StartNodePanel()
        {
            if (_npRunning) return;

            try
            {
                _npListener = new HttpListener();
                // 0.0.0.0:port — accesible desde toda la LAN.
                _npListener.Prefixes.Add("http://+:" + _npPort + "/");
                _npListener.Start();

                _npCts = new CancellationTokenSource();
                _npThread = new Thread(NodePanelLoop) { IsBackground = true, Name = "AgIO NodePanel" };
                _npThread.Start();

                _npRunning = true;
                Log.EventWriter("NodePanel HTTP started on port " + _npPort);
            }
            catch (HttpListenerException hex)
            {
                // 5 = Acceso denegado (falta netsh urlacl o no es admin).
                // Fallback: bindear sólo a localhost.
                Log.EventWriter("NodePanel +:" + _npPort + " failed (" + hex.ErrorCode +
                    "), falling back to localhost only");
                try
                {
                    _npListener = new HttpListener();
                    _npListener.Prefixes.Add("http://127.0.0.1:" + _npPort + "/");
                    _npListener.Start();

                    _npCts = new CancellationTokenSource();
                    _npThread = new Thread(NodePanelLoop) { IsBackground = true, Name = "AgIO NodePanel" };
                    _npThread.Start();

                    _npRunning = true;
                    Log.EventWriter("NodePanel HTTP started on 127.0.0.1:" + _npPort + " (LAN access disabled)");
                }
                catch (Exception ex2)
                {
                    Log.EventWriter("NodePanel start failed: " + ex2.Message);
                    _npRunning = false;
                }
            }
            catch (Exception ex)
            {
                Log.EventWriter("NodePanel start failed: " + ex.Message);
                _npRunning = false;
            }
        }

        private void StopNodePanel()
        {
            if (!_npRunning) return;
            _npRunning = false;

            try { _npCts?.Cancel(); } catch { }

            // Cerrar SSE.
            lock (_npSseLock)
            {
                foreach (var w in _npSseClients)
                {
                    try { w.Close(); } catch { }
                }
                _npSseClients.Clear();
            }

            try { _npListener?.Stop(); } catch { }
            try { _npListener?.Close(); } catch { }
            _npListener = null;

            Log.EventWriter("NodePanel HTTP stopped");
        }

        // ── Loop principal ────────────────────────────────────────────
        private void NodePanelLoop()
        {
            while (_npRunning && _npListener != null && _npListener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = _npListener.GetContext(); }
                catch { break; }

                // Cada request en su propio task.
                Task.Run(() => HandleRequest(ctx));
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                string path = ctx.Request.Url.AbsolutePath ?? "/";

                // CORS abierto (red local).
                ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
                ctx.Response.Headers["Cache-Control"] = "no-store";

                if (path == "/" || path == "/index.html")
                    ServeText(ctx, 200, "text/html; charset=utf-8", PWA_HTML);
                else if (path == "/manifest.json")
                    ServeText(ctx, 200, "application/manifest+json", PWA_MANIFEST);
                else if (path == "/sw.js")
                    ServeText(ctx, 200, "application/javascript; charset=utf-8", PWA_SW);
                else if (path == "/api/nodes")
                    ServeJson(ctx, 200, BuildNodesListJson());
                else if (path.StartsWith("/api/nodes/"))
                {
                    string uid = path.Substring("/api/nodes/".Length).Trim('/');
                    NodeSnapshot snap;
                    if (_npNodes.TryGetValue(uid, out snap))
                        ServeJson(ctx, 200, BuildNodeDetailJson(snap));
                    else
                        ServeJson(ctx, 404, "{\"error\":\"not found\"}");
                }
                else if (path == "/api/events")
                    ServeSse(ctx);
                else if (path == "/api/health")
                    ServeJson(ctx, 200,
                        "{\"ok\":true,\"nodes\":" + _npNodes.Count +
                        ",\"clients\":" + _mqttClientsConnected +
                        ",\"messages\":" + _mqttMessagesTotal + "}");
                else
                    ServeText(ctx, 404, "text/plain", "Not found: " + path);
            }
            catch (Exception ex)
            {
                Log.EventWriter("NodePanel request error: " + ex.Message);
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            }
        }

        // ── HTTP helpers ──────────────────────────────────────────────
        private static void ServeText(HttpListenerContext ctx, int status, string contentType, string body)
        {
            byte[] buf = Encoding.UTF8.GetBytes(body ?? "");
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = buf.Length;
            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
            ctx.Response.OutputStream.Close();
        }

        private static void ServeJson(HttpListenerContext ctx, int status, string body)
            => ServeText(ctx, status, "application/json; charset=utf-8", body);

        private void ServeSse(HttpListenerContext ctx)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["Connection"] = "keep-alive";
            ctx.Response.SendChunked = true;

            var sw = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false))
            { AutoFlush = true, NewLine = "\n" };

            // Snapshot inicial.
            try
            {
                sw.Write("event: snapshot\ndata: " + BuildNodesListJson() + "\n\n");
                sw.Flush();
            }
            catch { try { sw.Close(); } catch { } return; }

            lock (_npSseLock) _npSseClients.Add(sw);

            // Mantener vivo. El cliente cierra cuando navega; lo descubrimos en
            // el próximo broadcast cuando falle el Write.
            try
            {
                while (_npRunning && !_npCts.Token.IsCancellationRequested)
                {
                    Thread.Sleep(15000);
                    try { sw.Write(": ping\n\n"); sw.Flush(); }
                    catch { break; }
                }
            }
            finally
            {
                lock (_npSseLock) _npSseClients.Remove(sw);
                try { sw.Close(); } catch { }
            }
        }

        private void BroadcastSse(string evt, string jsonData)
        {
            string msg = "event: " + evt + "\ndata: " + jsonData + "\n\n";
            List<StreamWriter> dead = null;
            lock (_npSseLock)
            {
                foreach (var w in _npSseClients)
                {
                    try { w.Write(msg); w.Flush(); }
                    catch
                    {
                        if (dead == null) dead = new List<StreamWriter>();
                        dead.Add(w);
                    }
                }
                if (dead != null)
                    foreach (var w in dead) { _npSseClients.Remove(w); try { w.Close(); } catch { } }
            }
        }

        // ── Indexador (llamado desde InterceptingPublishAsync del broker) ─
        // Topic patterns:
        //   agp/{type}/announcement                 → payload trae uid/ip/version
        //   agp/{type}/{uid}/{subtopic...}          → telemetría/comando por nodo
        private void IndexNodeMessage(string topic, byte[] payload)
        {
            if (string.IsNullOrEmpty(topic) || !topic.StartsWith("agp/")) return;

            string[] parts = topic.Split('/');
            if (parts.Length < 3) return;

            string type = parts[1];
            string payloadStr = "";
            try { if (payload != null && payload.Length > 0) payloadStr = Encoding.UTF8.GetString(payload); }
            catch { }

            string uid = null;
            string subtopic = null;

            if (parts.Length == 3 && parts[2] == "announcement")
            {
                // Sacar uid del payload JSON.
                uid = ExtractJsonString(payloadStr, "uid");
                subtopic = "announcement";
            }
            else if (parts.Length >= 4)
            {
                uid = parts[2];
                subtopic = string.Join("/", parts, 3, parts.Length - 3);
            }
            else return;

            if (string.IsNullOrEmpty(uid)) return;

            var node = _npNodes.GetOrAdd(uid, u => new NodeSnapshot
            {
                Uid = u,
                Type = type,
                FirstSeen = DateTime.UtcNow
            });

            node.LastSeen = DateTime.UtcNow;
            node.MessageCount++;
            if (string.IsNullOrEmpty(node.Type)) node.Type = type;

            // Si es announcement, intentar extraer ip/version/hw.
            if (subtopic == "announcement")
            {
                string ip = ExtractJsonString(payloadStr, "ip");
                string ver = ExtractJsonString(payloadStr, "fw");
                if (string.IsNullOrEmpty(ver)) ver = ExtractJsonString(payloadStr, "version");
                string hw = ExtractJsonString(payloadStr, "hw");
                if (!string.IsNullOrEmpty(ip)) node.Ip = ip;
                if (!string.IsNullOrEmpty(ver)) node.Version = ver;
                if (!string.IsNullOrEmpty(hw)) node.Hw = hw;
            }

            // Truncar payloads grandes para no inflar memoria.
            if (payloadStr.Length > 4096) payloadStr = payloadStr.Substring(0, 4096) + "…";
            node.Topics[subtopic] = payloadStr;

            // Push a clientes SSE — un evento por mensaje, payload = mini-update.
            // Dejamos que la PWA decida si refresca toda la lista o solo el detalle.
            if (_npRunning)
            {
                string evtJson = "{\"uid\":" + JsonStr(uid) +
                    ",\"type\":" + JsonStr(node.Type) +
                    ",\"sub\":" + JsonStr(subtopic) +
                    ",\"ts\":" + node.LastSeen.Ticks +
                    ",\"payload\":" + JsonStr(payloadStr) + "}";
                BroadcastSse("update", evtJson);
            }
        }

        // ── JSON builders (simples, sin libs externas) ────────────────
        private string BuildNodesListJson()
        {
            var sb = new StringBuilder("{\"nodes\":[");
            bool first = true;
            foreach (var kv in _npNodes)
            {
                if (!first) sb.Append(',');
                first = false;
                var n = kv.Value;
                sb.Append("{");
                sb.Append("\"uid\":").Append(JsonStr(n.Uid)).Append(',');
                sb.Append("\"type\":").Append(JsonStr(n.Type)).Append(',');
                sb.Append("\"ip\":").Append(JsonStr(n.Ip ?? "")).Append(',');
                sb.Append("\"version\":").Append(JsonStr(n.Version ?? "")).Append(',');
                sb.Append("\"hw\":").Append(JsonStr(n.Hw ?? "")).Append(',');
                sb.Append("\"firstSeen\":").Append(n.FirstSeen.Ticks).Append(',');
                sb.Append("\"lastSeen\":").Append(n.LastSeen.Ticks).Append(',');
                sb.Append("\"ageMs\":").Append((long)(DateTime.UtcNow - n.LastSeen).TotalMilliseconds).Append(',');
                sb.Append("\"messageCount\":").Append(n.MessageCount).Append(',');
                sb.Append("\"topicCount\":").Append(n.Topics.Count);
                sb.Append("}");
            }
            sb.Append("],\"serverTime\":").Append(DateTime.UtcNow.Ticks);
            sb.Append(",\"brokerClients\":").Append(_mqttClientsConnected);
            sb.Append(",\"brokerMessages\":").Append(_mqttMessagesTotal);
            sb.Append("}");
            return sb.ToString();
        }

        private static string BuildNodeDetailJson(NodeSnapshot n)
        {
            var sb = new StringBuilder("{");
            sb.Append("\"uid\":").Append(JsonStr(n.Uid)).Append(',');
            sb.Append("\"type\":").Append(JsonStr(n.Type)).Append(',');
            sb.Append("\"ip\":").Append(JsonStr(n.Ip ?? "")).Append(',');
            sb.Append("\"version\":").Append(JsonStr(n.Version ?? "")).Append(',');
            sb.Append("\"hw\":").Append(JsonStr(n.Hw ?? "")).Append(',');
            sb.Append("\"firstSeen\":").Append(n.FirstSeen.Ticks).Append(',');
            sb.Append("\"lastSeen\":").Append(n.LastSeen.Ticks).Append(',');
            sb.Append("\"ageMs\":").Append((long)(DateTime.UtcNow - n.LastSeen).TotalMilliseconds).Append(',');
            sb.Append("\"messageCount\":").Append(n.MessageCount).Append(',');
            sb.Append("\"topics\":{");
            bool first = true;
            foreach (var kv in n.Topics)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(JsonStr(kv.Key)).Append(':').Append(JsonStr(kv.Value));
            }
            sb.Append("}}");
            return sb.ToString();
        }

        private static string JsonStr(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // Mini extractor: busca "key":"value" en un JSON simple sin parsear.
        // Suficiente para announcements típicos {"uid":"X","ip":"Y","fw":"Z"}.
        private static string ExtractJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            string needle = "\"" + key + "\"";
            int idx = json.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx + needle.Length);
            if (colon < 0) return null;
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            int q2 = json.IndexOf('"', q1 + 1);
            // Evitar escapadas — buscar comilla no escapada.
            while (q2 > 0 && json[q2 - 1] == '\\') q2 = json.IndexOf('"', q2 + 1);
            if (q2 < 0) return null;
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        // ── Helper: IP LAN para mostrarle al usuario dónde abrir ─────
        private static string GetLocalLanIp()
        {
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    // Truco clásico: "conectar" UDP a una IP externa fuerza al
                    // socket a elegir la interfaz LAN saliente sin enviar nada.
                    s.Connect("8.8.8.8", 65530);
                    var ep = s.LocalEndPoint as IPEndPoint;
                    return ep?.Address.ToString() ?? "127.0.0.1";
                }
            }
            catch { return "127.0.0.1"; }
        }

        // ── PWA (HTML+JS+CSS embebido) ────────────────────────────────
        // Tema PilotX: #4ABA3E accent, #E2E7E2 bg, #F5F7F4 cards, etc.
        private const string PWA_HTML = @"<!DOCTYPE html>
<html lang='es'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1,viewport-fit=cover'>
<meta name='theme-color' content='#4ABA3E'>
<title>PilotX · Nodos</title>
<link rel='manifest' href='/manifest.json'>
<style>
*{box-sizing:border-box;margin:0;padding:0;-webkit-tap-highlight-color:transparent}
html,body{height:100%;font-family:'Segoe UI',-apple-system,system-ui,sans-serif;background:#E2E7E2;color:#101612}
header{background:#F5F7F4;border-bottom:1px solid #C5CFC5;padding:12px 16px;display:flex;align-items:center;gap:12px;position:sticky;top:0;z-index:10}
header .dot{width:10px;height:10px;border-radius:50%;background:#4ABA3E;box-shadow:0 0 8px #4ABA3E}
header .dot.off{background:#C5CFC5;box-shadow:none}
header h1{font-size:16px;font-weight:600;color:#101612;letter-spacing:.2px}
header .meta{margin-left:auto;font-size:12px;color:#535E54}
main{padding:12px;max-width:900px;margin:auto}
.empty{text-align:center;color:#535E54;padding:64px 16px;font-size:14px}
.card{background:#F5F7F4;border:1px solid #C5CFC5;border-radius:14px;padding:16px;margin-bottom:10px;cursor:pointer;transition:transform .08s,border-color .15s}
.card:hover{border-color:#4ABA3E}
.card:active{transform:scale(.99)}
.card .row1{display:flex;align-items:center;gap:10px;margin-bottom:6px}
.card .row1 .badge{background:#4ABA3E;color:#fff;font-size:10px;padding:3px 8px;border-radius:8px;font-weight:600;letter-spacing:.5px;text-transform:uppercase}
.card .row1 .uid{font-family:Consolas,monospace;font-size:13px;color:#535E54}
.card .row1 .age{margin-left:auto;font-size:11px;color:#535E54}
.card .row1 .age.stale{color:#a07000}
.card .row1 .age.dead{color:#a02020}
.card h2{font-size:14px;font-weight:500;color:#101612;margin-bottom:4px}
.card .stats{display:flex;gap:14px;font-size:11px;color:#535E54;flex-wrap:wrap}
.card .stats span b{color:#101612;font-weight:600}
.modal{position:fixed;inset:0;background:rgba(16,22,18,.6);display:none;align-items:flex-end;justify-content:center;z-index:20;animation:fade .15s}
.modal.show{display:flex}
.modal .sheet{background:#F5F7F4;border-radius:18px 18px 0 0;width:100%;max-width:900px;max-height:88vh;overflow:auto;padding:18px;animation:slide .2s}
@media(min-width:600px){.modal{align-items:center}.modal .sheet{border-radius:18px;max-height:80vh}}
@keyframes fade{from{opacity:0}to{opacity:1}}
@keyframes slide{from{transform:translateY(40px)}to{transform:translateY(0)}}
.sheet h2{font-size:18px;margin-bottom:4px}
.sheet .muted{color:#535E54;font-size:12px;margin-bottom:14px;font-family:Consolas,monospace}
.sheet .closer{position:sticky;top:-18px;float:right;background:#E2E7E2;border:1px solid #C5CFC5;border-radius:50%;width:32px;height:32px;cursor:pointer;font-size:16px;color:#101612;line-height:1}
.sheet .topic{background:#fff;border:1px solid #C5CFC5;border-radius:10px;padding:10px 12px;margin-bottom:8px}
.sheet .topic .k{font-family:Consolas,monospace;font-size:12px;color:#4ABA3E;font-weight:600;margin-bottom:4px}
.sheet .topic .v{font-family:Consolas,monospace;font-size:12px;color:#101612;white-space:pre-wrap;word-break:break-all;line-height:1.4}
.btn{background:#4ABA3E;color:#fff;border:none;border-radius:10px;padding:9px 16px;font-weight:600;font-size:13px;cursor:pointer}
.btn:hover{background:#5BD04E}
footer{text-align:center;color:#535E54;font-size:11px;padding:24px 12px}
</style>
</head>
<body>
<header>
  <span class='dot' id='dot'></span>
  <h1>PilotX · Nodos</h1>
  <span class='meta' id='meta'>—</span>
</header>
<main>
  <div id='list'></div>
  <div id='empty' class='empty' style='display:none'>Sin nodos detectados todavía…<br><small>Esperando announce MQTT</small></div>
</main>
<div class='modal' id='modal'><div class='sheet' id='sheet'></div></div>
<footer>Agro Parallel · PilotX · LAN local</footer>
<script>
const fmtAge=(ms)=>{ if(ms<2000)return 'live'; if(ms<60000)return Math.round(ms/1000)+'s'; if(ms<3600000)return Math.round(ms/60000)+'m'; return Math.round(ms/3600000)+'h'; };
const ageClass=(ms)=>ms<10000?'':(ms<60000?'stale':'dead');
let nodes=[];
let openUid=null;
async function loadList(){
  try{
    const r=await fetch('/api/nodes');
    const d=await r.json();
    nodes=d.nodes||[];
    document.getElementById('meta').textContent=
      (d.brokerClients||0)+' MQTT · '+nodes.length+' nodo'+(nodes.length===1?'':'s');
    render();
  }catch(e){ console.error(e); }
}
function render(){
  const list=document.getElementById('list');
  const empty=document.getElementById('empty');
  if(nodes.length===0){ list.innerHTML=''; empty.style.display='block'; return; }
  empty.style.display='none';
  nodes.sort((a,b)=>(b.lastSeen||0)-(a.lastSeen||0));
  list.innerHTML=nodes.map(n=>{
    const age=n.ageMs||0;
    return `<div class='card' onclick=""openDetail('${n.uid}')"">
      <div class='row1'>
        <span class='badge'>${(n.type||'?').toUpperCase()}</span>
        <span class='uid'>${n.uid}</span>
        <span class='age ${ageClass(age)}'>${fmtAge(age)}</span>
      </div>
      <h2>${n.ip||'—'} ${n.version?(' · v'+n.version):''}</h2>
      <div class='stats'>
        <span>msgs <b>${n.messageCount}</b></span>
        <span>topics <b>${n.topicCount}</b></span>
      </div>
    </div>`;
  }).join('');
}
async function openDetail(uid){
  openUid=uid;
  const r=await fetch('/api/nodes/'+encodeURIComponent(uid));
  const n=await r.json();
  if(n.error){ alert('Nodo no encontrado'); return; }
  const sheet=document.getElementById('sheet');
  const topics=Object.entries(n.topics||{}).sort();
  sheet.innerHTML=`<button class='closer' onclick='closeDetail()'>×</button>
    <h2>${(n.type||'?').toUpperCase()} · ${n.uid}</h2>
    <div class='muted'>${n.ip||'sin IP'} ${n.version?(' · fw '+n.version):''} · last ${fmtAge(n.ageMs)}</div>
    ${topics.length===0?'<p class=\'muted\'>Sin tópicos publicados aún.</p>':
      topics.map(([k,v])=>`<div class='topic'><div class='k'>${k}</div><div class='v'>${escapeHtml(v||'')}</div></div>`).join('')}`;
  document.getElementById('modal').classList.add('show');
}
function closeDetail(){ openUid=null; document.getElementById('modal').classList.remove('show'); }
document.getElementById('modal').addEventListener('click',e=>{ if(e.target.id==='modal') closeDetail(); });
function escapeHtml(s){ return String(s).replace(/[&<>""']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;','\'':'&#39;'})[c]); }

// SSE — reconnect con backoff básico.
let es=null, retry=1000;
function connectSSE(){
  if(es){ try{es.close()}catch(e){} }
  es=new EventSource('/api/events');
  es.addEventListener('snapshot',ev=>{
    const d=JSON.parse(ev.data); nodes=d.nodes||[]; render();
    document.getElementById('dot').classList.remove('off'); retry=1000;
  });
  es.addEventListener('update',ev=>{
    const u=JSON.parse(ev.data);
    let n=nodes.find(x=>x.uid===u.uid);
    if(!n){ loadList(); return; }
    n.lastSeen=u.ts; n.ageMs=0; n.messageCount=(n.messageCount||0)+1;
    if(u.sub && (n.topicCount||0)===0) n.topicCount=1;
    render();
    if(openUid===u.uid) openDetail(u.uid); // refresca modal si está abierto
  });
  es.onerror=()=>{
    document.getElementById('dot').classList.add('off');
    try{es.close()}catch(e){}
    setTimeout(connectSSE, retry);
    retry=Math.min(retry*2, 10000);
  };
}
loadList(); connectSSE();
// Tick de envejecimiento — cada 1s recalculamos ageMs visualmente.
setInterval(()=>{ const now=Date.now(); nodes.forEach(n=>{ /* ageMs ya viene del server, pero entre eventos lo aumentamos para feedback */ if(n.lastSeenLocal===undefined) n.lastSeenLocal=now-n.ageMs; n.ageMs=now-n.lastSeenLocal; }); render(); }, 2000);

// Service worker (instalable como PWA).
if('serviceWorker' in navigator){ navigator.serviceWorker.register('/sw.js').catch(()=>{}); }
</script>
</body>
</html>";

        private const string PWA_MANIFEST = @"{
  ""name"": ""PilotX Nodos"",
  ""short_name"": ""PilotX"",
  ""start_url"": ""/"",
  ""display"": ""standalone"",
  ""background_color"": ""#E2E7E2"",
  ""theme_color"": ""#4ABA3E"",
  ""description"": ""Panel local de nodos Agro Parallel"",
  ""icons"": []
}";

        private const string PWA_SW = @"// Service worker mínimo: cache del shell para que la PWA abra offline
// (los datos siempre vienen live del server, no se cachean).
const CACHE='pilotx-v1';
const SHELL=['/','/manifest.json'];
self.addEventListener('install',e=>{e.waitUntil(caches.open(CACHE).then(c=>c.addAll(SHELL)));self.skipWaiting();});
self.addEventListener('activate',e=>{e.waitUntil(self.clients.claim());});
self.addEventListener('fetch',e=>{
  const u=new URL(e.request.url);
  if(u.pathname.startsWith('/api/')){ return; } // datos: siempre red
  e.respondWith(fetch(e.request).catch(()=>caches.match(e.request)));
});";
    }
}
