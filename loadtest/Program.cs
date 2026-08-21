// Prova di carico: N client veri, con account veri, che si muovono e
// raccolgono come farebbe della gente.
//
//   LoadTest.exe <numeroClient> <secondi> [host] [porta] [urlApi]
//
// Non simula il protocollo a mano: usa lo stesso Precursori.Shared del gioco,
// quindi se il protocollo cambia questa prova smette di compilare invece di
// mentire.
using System.Diagnostics;
using System.Net.Http.Json;
using LiteNetLib;
using LiteNetLib.Utils;
using Precursori.Shared.Net;

bool modoRegole = args.Length > 0 && args[0] == "regole";
if (modoRegole) args = args.Skip(1).ToArray();

// Sotto-modalita delle regole: le due meta della prova di persistenza girano in
// processi diversi, con in mezzo il server spento e riacceso.
string metaPersistenza = null;
if (modoRegole && args.Length > 0 && (args[0] == "prepara" || args[0] == "verifica"))
{
    metaPersistenza = args[0];
    args = args.Skip(1).ToArray();
}

// Modalita amministratore: esegue le righe date e riporta cio che il server
// risponde nel proprio log. Serve a guardare e a correggere il mondo vero senza
// doverci entrare a giocare.
bool modoAdmin = args.Length > 0 && args[0] == "admin";
string[] righeAdmin = modoAdmin ? args.Skip(1).ToArray() : Array.Empty<string>();
if (modoAdmin) args = Array.Empty<string>();

int clients = args.Length > 0 ? int.Parse(args[0]) : 50;
int seconds = args.Length > 1 ? int.Parse(args[1]) : 60;
string host = args.Length > 2 ? args[2] : "127.0.0.1";
int port    = args.Length > 3 ? int.Parse(args[3]) : 27015;
string api  = args.Length > 4 ? args[4] : "http://127.0.0.1:5080";

// Le prove sulle regole usano nomi propri: il banco le dichiara amministratrici
// per poter preparare gli scenari, e non si vuole che quel potere finisca su un
// account che serve anche ad altro.
string prefisso = modoRegole || modoAdmin ? "sonda" : "carico";
if (modoAdmin) { clients = 1; Console.WriteLine($"Modalita amministratore su {host}:{port}"); }
else if (modoRegole) { clients = 4; if (metaPersistenza == null) Console.WriteLine($"Prova sulle regole su {host}:{port}"); }
else Console.WriteLine($"Prova di carico: {clients} client per {seconds}s su {host}:{port}");

// ---- account -------------------------------------------------------------
// Servono token veri: l'autenticazione e obbligatoria e provarla senza
// significherebbe misurare un percorso che in produzione non esiste.
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
var tokens = new List<string>(clients);

for (int i = 0; i < clients; i++)
{
    string name = $"{prefisso}{i:D4}";
    string token = await Auth("login", name) ?? await Auth("register", name);
    if (token == null) { Console.WriteLine($"  {name}: autenticazione fallita"); continue; }
    tokens.Add(token);
    if ((i + 1) % 25 == 0) Console.WriteLine($"  autenticati {i + 1}/{clients}");
}
Console.WriteLine($"token ottenuti: {tokens.Count}/{clients}");

async Task<string> Auth(string endpoint, string name)
{
    try
    {
        var r = await http.PostAsJsonAsync($"{api}/auth/{endpoint}",
                                           new { name, password = "caricocarico" });
        if (!r.IsSuccessStatusCode) return null;
        var body = await r.Content.ReadFromJsonAsync<AuthReply>();
        return body?.token;
    }
    catch { return null; }
}

// ---- modalita amministratore ---------------------------------------------
if (modoAdmin)
{
    if (tokens.Count == 0) { Console.WriteLine("nessun token: annullo"); return 1; }

    using var sonda = new Sonda(tokens[0], host, port);
    if (!sonda.Attendi(() => sonda.Pronta)) { Console.WriteLine("handshake non completato"); return 1; }

    foreach (var riga in righeAdmin)
    {
        Console.WriteLine($"  > {riga}");
        sonda.Admin(riga);
        // Le risposte dei comandi di amministrazione finiscono nel log del
        // server, non sulla rete: qui si aspetta solo che vengano eseguiti.
        sonda.Attendi(() => false, 0.8);
    }
    sonda.Attendi(() => false, 1.5);
    Console.WriteLine("eseguito; l'esito e nel log del server");
    return 0;
}

// ---- prova sulle regole --------------------------------------------------
if (modoRegole)
{
    if (tokens.Count < 4) { Console.WriteLine("servono quattro token: prova annullata"); return 1; }

    if (metaPersistenza == "prepara")
        return Regole.PersistenzaPrepara(tokens[3], host, port) ? 0 : 1;
    if (metaPersistenza == "verifica")
        return Regole.PersistenzaVerifica(tokens[3], host, port, "dopo il riavvio") ? 0 : 1;

    // Il terzo account non e' fra gli amministratori: serve proprio a
    // verificare che senza quel titolo i comandi non funzionino.
    var prove = new (string Nome, Func<bool> Esegui)[]
    {
        ("tetto di strutture",      () => Regole.TettoStrutture(tokens[0], host, port)),
        ("palizzate",               () => Regole.Palizzate(tokens[0], tokens[1], host, port)),
        ("rinascita allo stendardo",() => Regole.RinascitaAlloStendardo(tokens[0], host, port)),
        ("territorio",              () => Regole.Territorio(tokens[0], tokens[1], host, port)),
        ("riparazione stendardo",   () => Regole.RiparazioneStendardo(tokens[0], host, port)),
        ("amministrazione chiusa",  () => Regole.AmministrazioneChiusa(tokens[2], host, port)),
        ("catalogo ricette",        () => Regole.CatalogoRicette(tokens[1], host, port)),
        ("mangiare cura",           () => Regole.MangiareCura(tokens[1], host, port)),
        ("bauli",                   () => Regole.Bauli(tokens[1], host, port)),
        ("agricoltura",             () => Regole.Agricoltura(tokens[1], host, port)),
        ("depositi",                () => Regole.Depositi(tokens[1], host, port)),
        ("combinazioni",            () => Regole.Combinazioni(tokens[1], host, port)),
        ("terra che si consuma",    () => Regole.TerraSiConsuma(tokens[1], host, port)),
        ("persistenza al rientro",  () => Regole.PersistenzaAlRientro(tokens[3], host, port)),
    };

    int falliti = 0;
    foreach (var (nome, esegui) in prove)
    {
        Console.WriteLine();
        bool ok;
        try { ok = esegui(); }
        catch (Exception ex) { Console.WriteLine($"  FALLITA con eccezione: {ex.Message}"); ok = false; }
        if (!ok) falliti++;
    }

    Console.WriteLine();
    Console.WriteLine($"prove: {prove.Length - falliti}/{prove.Length} superate");
    return falliti == 0 ? 0 : 1;
}

// ---- client --------------------------------------------------------------
var bots = new List<Bot>();
for (int i = 0; i < tokens.Count; i++) bots.Add(new Bot(tokens[i], host, port, i, tokens.Count));

Console.WriteLine($"i primi {Bot.DisperseSeconds:F0}s servono a sparpagliare i bot; " +
                  "poi restano perlopiu fermi, come gioca la gente.");

var sw = Stopwatch.StartNew();
uint seq = 0;
var rng = new Random(1234);

while (sw.Elapsed.TotalSeconds < seconds)
{
    foreach (var b in bots) b.Poll();

    // Input a 30 Hz come il client vero: e il messaggio piu frequente e quello
    // che decide il costo reale.
    if (sw.ElapsedMilliseconds % 33 < 5)
    {
        seq++;
        foreach (var b in bots) b.SendInput(seq, rng, sw.Elapsed.TotalSeconds);
    }

    Thread.Sleep(2);
}

// ---- risultati -----------------------------------------------------------
int connected = bots.Count(b => b.HandshakeOk);
long bytes    = bots.Sum(b => b.BytesIn);
long snaps    = bots.Sum(b => b.Snapshots);
double secs   = sw.Elapsed.TotalSeconds;

Console.WriteLine();
Console.WriteLine("===== ESITO =====");
Console.WriteLine($"client collegati      : {connected}/{tokens.Count}");
Console.WriteLine($"rifiutati o caduti    : {tokens.Count - connected}");
Console.WriteLine($"snapshot interi       : {snaps}  ({snaps / Math.Max(1, connected) / secs:F1}/s per client)");

long deltas = bots.Sum(b => b.Deltas);
if (deltas > 0)
{
    long conf = bots.Sum(b => b.Confronti);
    long div  = bots.Sum(b => b.Divergenze);
    long peggio = bots.Max(b => b.PeggiorDivergenza);
    Console.WriteLine($"differenziali         : {deltas}  ({deltas / Math.Max(1, connected) / secs:F1}/s per client)");
    Console.WriteLine($"verifica ricostruzione: {conf} confronti col fotogramma intero, " +
                      $"{div} entita fuori posto ({(double)div / Math.Max(1, conf):F2} per confronto, " +
                      $"peggior caso {peggio})");
}
Console.WriteLine($"traffico in ingresso  : {bytes / 1024.0 / 1024.0:F1} MB  " +
                  $"({bytes / secs / 1024.0:F0} KB/s totali, " +
                  $"{bytes / secs / 1024.0 / Math.Max(1, connected):F1} KB/s per client)");
Console.WriteLine($"durata                : {secs:F0}s");
Console.WriteLine("=================");

foreach (var b in bots) b.Stop();
return 0;

sealed class AuthReply { public string token { get; set; } public string name { get; set; } }

sealed class Bot
{
    readonly NetManager _net;
    NetPeer _peer;
    readonly string _token;

    public bool HandshakeOk;
    public long BytesIn;
    public long Snapshots;
    public long Deltas;

    // Mondo ricostruito applicando i differenziali, e quanto si scosta dai
    // fotogrammi interi.
    readonly HashSet<ulong> _mondo = new();
    public long Confronti, Divergenze, PeggiorDivergenza;

    // Ogni bot ha una direzione sua e una cadenza sua: e cosi che si sparpagliano
    // invece di restare tutti addosso ai quattro punti di comparsa.
    readonly float _headingX, _headingY;
    readonly double _phase;

    public Bot(string token, string host, int port, int index, int total)
    {
        _token = token;
        double a = Math.PI * 2 * index / Math.Max(1, total);
        _headingX = (float)Math.Cos(a);
        _headingY = (float)Math.Sin(a);
        _phase = (double)index / Math.Max(1, total);
        var listener = new EventBasedNetListener();

        listener.PeerConnectedEvent += p =>
        {
            _peer = p;
            Send(NetMsgType.HandshakeRequest, new HandshakeRequest
            {
                ClientBuild = "loadtest", AuthToken = _token,
            });
        };

        listener.NetworkReceiveEvent += (p, r, ch, d) =>
        {
            var raw = r.GetRemainingBytes();
            BytesIn += raw.Length;
            try
            {
                var env = MsgPack.UnpackEnvelope(raw);
                if (env.Type == NetMsgType.HandshakeOk) HandshakeOk = true;
                else if (env.Type == NetMsgType.WorldSnapshot)
                {
                    Snapshots++;
                    var full = MessagePack.MessagePackSerializer
                                   .Deserialize<WorldSnapshotMsg>(env.Payload);

                    // Il fotogramma intero e la verita. Se il mondo ricostruito
                    // dai delta non gli somiglia, i differenziali sono economici
                    // e sbagliati — che e peggio che costosi e giusti.
                    if (Deltas > 0)
                    {
                        var vero = new HashSet<ulong>(full.Entities.Select(e => e.EntityId));
                        int diff = vero.Except(_mondo).Count() + _mondo.Except(vero).Count();
                        Confronti++;
                        Divergenze += diff;
                        if (diff > PeggiorDivergenza) PeggiorDivergenza = diff;
                    }

                    _mondo.Clear();
                    foreach (var e in full.Entities) _mondo.Add(e.EntityId);
                }
                else if (env.Type == NetMsgType.DeltaSnapshot)
                {
                    Deltas++;
                    var delta = MessagePack.MessagePackSerializer
                                    .Deserialize<DeltaSnapshotMsg>(env.Payload);
                    foreach (var e in delta.Upserts)  _mondo.Add(e.EntityId);
                    foreach (var id in delta.Despawns) _mondo.Remove(id);
                }
            }
            catch { /* il conteggio dei byte vale comunque */ }
        };

        _net = new NetManager(listener) { AutoRecycle = true, DisconnectTimeout = 15_000 };
        _net.Start();
        _net.Connect(host, port, "precursori");
    }

    public void Poll() => _net.PollEvents();

    // Quanto dura la fase di sparpagliamento, in secondi.
    public const double DisperseSeconds = 30;

    public void SendInput(uint seq, Random rng, double elapsed)
    {
        if (_peer == null || !HandshakeOk) return;

        float mx, my;
        if (elapsed < DisperseSeconds)
        {
            // Fase 1: ognuno per la sua strada, per allontanarsi dai punti di
            // comparsa e occupare celle diverse della griglia.
            mx = _headingX; my = _headingY;
        }
        else
        {
            // Fase 2: come gioca la gente davvero. Si sta fermi a raccogliere o
            // costruire e ogni tanto ci si sposta di poco. Un giocatore che
            // scatta a caso trenta volte al secondo non esiste, e misurare quello
            // vuol dire misurare un carico che in partita non capita mai.
            double t = elapsed * 0.12 + _phase * 10;
            bool moving = (t % 1.0) < 0.25;          // in moto un quarto del tempo
            if (moving)
            {
                double dir = t * 2.7;
                mx = (float)Math.Cos(dir) * 0.6f;
                my = (float)Math.Sin(dir) * 0.6f;
            }
            else { mx = 0; my = 0; }
        }

        Send(NetMsgType.Input, new InputMsg { Seq = seq, MoveX = mx, MoveY = my },
             DeliveryMethod.Unreliable);
    }

    void Send<T>(NetMsgType type, T payload, DeliveryMethod how = DeliveryMethod.ReliableOrdered)
    {
        if (_peer == null) return;
        var env = new Envelope { Type = type, Payload = MessagePack.MessagePackSerializer.Serialize(payload) };
        var w = new NetDataWriter();
        w.Put(MessagePack.MessagePackSerializer.Serialize(env));
        _peer.Send(w, how);
    }

    public void Stop() => _net.Stop();
}
