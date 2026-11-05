// Prove sulle regole, non sulle prestazioni.
//
// Il banco di carico misura quanto costa il server. Questo verifica che dica di
// no quando deve e che porti la gente dove deve. Sono due domande diverse e la
// seconda si dimentica piu' facilmente, perche' una regola sbagliata non
// rallenta niente: si nota mesi dopo, quando qualcuno attraversa un muro.
using LiteNetLib;
using LiteNetLib.Utils;
using Precursori.Shared.Net;

/// <summary>
/// Un client di prova che sa dov'e'. Serve la posizione, perche' le regole da
/// verificare qui — un muro che ferma, una rinascita che porta a casa — si
/// vedono solo guardando dove il giocatore finisce davvero.
/// </summary>
sealed class Sonda : IDisposable
{
    readonly NetManager _net;
    NetPeer _peer;
    ulong _playerId;

    public bool Pronta;
    public float X, Y, Z;

    /// L'id con cui il server ci mostra a noi stessi.
    public ulong Id => _playerId;
    public bool Vista;                 // vero appena il server ci ha mostrato a noi stessi
    public CraftResultMsg UltimoCraft;
    public RepairResultMsg UltimaRiparazione;

    /// Il catalogo ricevuto all'ingresso. La posizione nell'elenco e' anche
    /// l'identificatore con cui si chiede di creare una ricetta precisa.
    public RecipeInfo[] Ricette = Array.Empty<RecipeInfo>();

    public int IndiceRicetta(string nome)
    {
        for (int i = 0; i < Ricette.Length; i++)
            if (string.Equals(Ricette[i].ResultName, nome, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public ConsumeResultMsg UltimoConsumo;
    public ChestContentsMsg UltimoBaule;
    public WorldVitalityMapMsg UltimaVitalita;
    public MatchStateMsg UltimaPartita;
    public RitualResultMsg UltimoRituale;
    public SanctuaryStateMsg UltimoSantuario;

    /// Gli eventi del mondo ricevuti, in ordine di arrivo.
    public readonly List<WorldEventMsg> Eventi = new();

    public bool HaEvento(WorldEventKind tipo)
    {
        lock (Eventi) return Eventi.Exists(e => e.Kind == tipo);
    }

    /// I messaggi di chat ricevuti, solo il testo, in ordine di arrivo.
    public readonly List<string> Chat = new();

    /// <summary>
    /// Quanti cristalli ci sono a terra in tutto il mondo, secondo il server
    /// (comando admin "contacristalli"). -1 se la risposta non arriva.
    /// </summary>
    public int ContaCristalli()
    {
        const string Testa = "cristalli a terra: ";
        int gia;
        lock (Chat) gia = Chat.Count;
        Admin("contacristalli");
        string riga = null;
        Attendi(() => { lock (Chat) riga = Chat.Skip(gia).LastOrDefault(t => t.StartsWith(Testa)); return riga != null; }, 5);
        return riga != null && int.TryParse(riga.Substring(Testa.Length), out int n) ? n : -1;
    }

    /// <summary>
    /// Manda un comando admin e aspetta la riga di chat locale che comincia con
    /// <paramref name="testa"/>; ne restituisce il resto, o null se non arriva.
    /// </summary>
    public string ChiediAdmin(string comando, string testa)
    {
        int gia;
        lock (Chat) gia = Chat.Count;
        Admin(comando);
        string riga = null;
        Attendi(() => { lock (Chat) riga = Chat.Skip(gia).LastOrDefault(t => t.StartsWith(testa)); return riga != null; }, 5);
        return riga?.Substring(testa.Length);
    }

    /// Quanti rituali con esito ha alle spalle questa sonda, secondo il server
    /// (comando admin "rituali"); con <paramref name="fissa"/>, prima li fissa.
    /// -1 se la risposta non arriva.
    public int RitualiFatti(int? fissa = null)
    {
        var r = ChiediAdmin(fissa is int n ? $"rituali {n}" : "rituali", "rituali fatti: ");
        return r != null && int.TryParse(r, out int v) ? v : -1;
    }

    /// <summary>
    /// Lo stato del santuario e dell'orda secondo il server (comando admin
    /// "santuario", piu' eventuali argomenti): "stato", "orda", "capi",
    /// "sparsa", "in attesa", "tornati", "cristalli alti a terra". Vuoto se la
    /// risposta non arriva.
    /// </summary>
    public Dictionary<string, string> StatoSantuario(string argomenti = "") => StatoAdmin("santuario", argomenti);

    /// <summary>
    /// Gli Echi secondo il server (comando admin "echi", piu' eventuali
    /// argomenti): "eco", "vaganti", "divoratori", "araldi", "con cristallo",
    /// "a caccia", "da ricreare", "cristalli alti a terra".
    /// </summary>
    public Dictionary<string, string> StatoEchi(string argomenti = "") => StatoAdmin("echi", argomenti);

    /// Un numero dallo stato, -1 se manca.
    public static int Numero(Dictionary<string, string> stato, string chiave) =>
        stato.TryGetValue(chiave, out var v) && int.TryParse(v, out int n) ? n : -1;

    Dictionary<string, string> StatoAdmin(string comando, string argomenti)
    {
        string Testa = comando + ": ";
        int gia;
        lock (Chat) gia = Chat.Count;
        Admin((comando + " " + argomenti).Trim());
        string riga = null;
        Attendi(() => { lock (Chat) riga = Chat.Skip(gia).LastOrDefault(t => t.StartsWith(Testa)); return riga != null; }, 5);
        var stato = new Dictionary<string, string>();
        if (riga == null) return stato;
        foreach (var voce in riga.Substring(Testa.Length).Split(", "))
        {
            int sp = voce.LastIndexOf(' ');
            if (sp > 0) stato[voce[..sp]] = voce[(sp + 1)..];
        }
        return stato;
    }

    public static string Descrivi(Dictionary<string, string> stato) =>
        stato.Count == 0 ? "(nessuna risposta)" : string.Join(", ", stato.Select(kv => $"{kv.Key} {kv.Value}"));

    /// Il registro di gioco come lo vedrebbe il client: sostituito da ogni
    /// WorldJournal, allungato da ogni WorldEvent.
    public readonly List<WorldEventMsg> Registro = new();
    public int RegistriRicevuti;

    /// Il registro in una riga, "id:tipo" separati da virgole: si confronta e si
    /// annota su file.
    public string RegistroInRiga()
    {
        lock (Registro) return string.Join(",", Registro.Select(v => $"{v.EntryId}:{v.Kind}"));
    }

    public void CancellaVoce(uint id) =>
        Manda(NetMsgType.Command, new CommandMsg
        {
            CommandId = 10,
            Type      = CommandType.DeleteJournalEntry,
            Payload   = MessagePack.MessagePackSerializer.Serialize(
                            new DeleteJournalEntryCommand { EntryId = id }),
        });

    public void Rituale(ulong sito, CrystalType grado)
    {
        UltimoRituale = null;
        Manda(NetMsgType.Command, new CommandMsg
        {
            CommandId = 9,
            Type      = CommandType.Ritual,
            Payload   = MessagePack.MessagePackSerializer.Serialize(new RitualCommand
            { SiteId = sito, Tier = grado }),
        });
    }

    public int Cristalli(CrystalType grado)
    {
        lock (Zaino) return Zaino.TryGetValue((ItemType.Crystal, (byte)grado), out int n) ? n : 0;
    }

    /// Somma della vitalita di tutti i chunk: basta per dire se e scesa.
    public long VitalitaTotale()
    {
        var v = UltimaVitalita?.Vitality;
        if (v == null) return -1;
        long s = 0;
        foreach (var b in v) s += b;
        return s;
    }

    public void Combina(EntityType risultato, float px, float pz) =>
        Manda(NetMsgType.Command, new CommandMsg
        {
            CommandId = 8,
            Type      = CommandType.CombineStructures,
            Payload   = MessagePack.MessagePackSerializer.Serialize(new CombineCommand
            { TargetBuilding = risultato, PlacementX = px, PlacementZ = pz }),
        });

    public void Raccogli(ulong bersaglio) =>
        Manda(NetMsgType.Command, new CommandMsg
        {
            CommandId = 7,
            Type      = CommandType.Gather,
            Payload   = MessagePack.MessagePackSerializer.Serialize(
                            new GatherCommand { TargetEntityId = bersaglio }),
        });

    public void ApriBaule(ulong id)
    {
        UltimoBaule = null;
        MandaBaule(new ChestTransferCommand { ChestId = id, PeekOnly = true });
    }

    public void SpostaNelBaule(ulong id, ItemType tipo, int quanti, ulong istanza, bool deposita)
    {
        UltimoBaule = null;
        MandaBaule(new ChestTransferCommand
        {
            ChestId = id, Type = tipo, Count = quanti, InstanceId = istanza, Deposit = deposita,
        });
    }

    void MandaBaule(ChestTransferCommand cmd) =>
        Manda(NetMsgType.Command, new CommandMsg
        {
            CommandId = 6,
            Type      = CommandType.ChestTransfer,
            Payload   = MessagePack.MessagePackSerializer.Serialize(cmd),
        });

    public void Mangia(ItemType cosa)
    {
        UltimoConsumo = null;
        Manda(NetMsgType.Command, new CommandMsg
        {
            CommandId = 5,
            Type      = CommandType.Consume,
            Payload   = MessagePack.MessagePackSerializer.Serialize(new ConsumeCommand { Type = cosa }),
        });
    }

    public void ChiediRicetta(int indice, float px, float pz)
    {
        UltimoCraft = null;
        Manda(NetMsgType.Command, new CommandMsg
        {
            CommandId = 4,
            Type      = CommandType.Craft,
            Payload   = MessagePack.MessagePackSerializer.Serialize(new CraftCommand
            {
                RecipeIndex = indice,
                Ingredients = Array.Empty<IngredientSlot>(),
                PlacementX  = px,
                PlacementZ  = pz,
            }),
        });
    }

    /// Tutto cio' che il server ci ha mostrato, per id. Serve per ritrovare una
    /// struttura appena piantata: l'amministratore ne conosce le coordinate ma
    /// non l'identificativo, e i comandi che agiscono su una struttura vogliono
    /// quello.
    public readonly Dictionary<ulong, EntityState> Mondo = new();

    /// Quanto abbiamo di ciascun oggetto, secondo il server.
    public readonly Dictionary<(ItemType, byte), int> Zaino = new();

    /// Gli esemplari con identita' propria, per identificativo.
    public readonly Dictionary<ulong, (ItemType Tipo, int Usura)> Istanze = new();

    public Sonda(string token, string host, int port)
    {
        var listener = new EventBasedNetListener();

        listener.PeerConnectedEvent += p =>
        {
            _peer = p;
            Manda(NetMsgType.HandshakeRequest,
                  new HandshakeRequest { ClientBuild = "regole", AuthToken = token });
        };

        listener.NetworkReceiveEvent += (p, r, ch, metodo) =>
        {
            try
            {
                var env = MsgPack.UnpackEnvelope(r.GetRemainingBytes());
                switch (env.Type)
                {
                    case NetMsgType.HandshakeOk:
                        _playerId = MessagePack.MessagePackSerializer
                                        .Deserialize<HandshakeOk>(env.Payload).PlayerId;
                        Pronta = true;
                        break;

                    case NetMsgType.WorldSnapshot:
                        // Il fotogramma intero e' la verita': si ricostruisce da
                        // capo invece di aggiungere soltanto. Senza, le entita'
                        // sparite restavano nella vista della sonda per sempre —
                        // e una prova che chiede "e' stato consumato?" avrebbe
                        // risposto sempre di no.
                        Aggiorna(MessagePack.MessagePackSerializer
                                     .Deserialize<WorldSnapshotMsg>(env.Payload).Entities,
                                 sostituisci: true);
                        break;

                    case NetMsgType.DeltaSnapshot:
                        Aggiorna(MessagePack.MessagePackSerializer
                                     .Deserialize<DeltaSnapshotMsg>(env.Payload).Upserts);
                        break;

                    case NetMsgType.CraftResult:
                        UltimoCraft ??= MessagePack.MessagePackSerializer
                                            .Deserialize<CraftResultMsg>(env.Payload);
                        break;

                    case NetMsgType.WorldVitalityMap:
                        UltimaVitalita = MessagePack.MessagePackSerializer
                                             .Deserialize<WorldVitalityMapMsg>(env.Payload);
                        break;

                    case NetMsgType.ChestContents:
                        UltimoBaule = MessagePack.MessagePackSerializer
                                          .Deserialize<ChestContentsMsg>(env.Payload);
                        break;

                    case NetMsgType.MatchState:
                        UltimaPartita = MessagePack.MessagePackSerializer
                                            .Deserialize<MatchStateMsg>(env.Payload);
                        break;

                    case NetMsgType.RitualResult:
                        UltimoRituale = MessagePack.MessagePackSerializer
                                            .Deserialize<RitualResultMsg>(env.Payload);
                        break;

                    case NetMsgType.SanctuaryState:
                        UltimoSantuario = MessagePack.MessagePackSerializer
                                              .Deserialize<SanctuaryStateMsg>(env.Payload);
                        break;

                    case NetMsgType.Chat:
                    {
                        var c = MessagePack.MessagePackSerializer.Deserialize<ChatMsg>(env.Payload);
                        lock (Chat) Chat.Add(c.Text ?? "");
                        break;
                    }

                    case NetMsgType.WorldEvent:
                    {
                        var ev = MessagePack.MessagePackSerializer
                                     .Deserialize<WorldEventMsg>(env.Payload);
                        lock (Eventi) Eventi.Add(ev);
                        lock (Registro) Registro.Add(ev);
                        break;
                    }

                    case NetMsgType.WorldJournal:
                    {
                        var j = MessagePack.MessagePackSerializer
                                    .Deserialize<WorldJournalMsg>(env.Payload);
                        lock (Registro) { Registro.Clear(); Registro.AddRange(j.Entries); }
                        RegistriRicevuti++;
                        break;
                    }

                    case NetMsgType.ConsumeResult:
                        UltimoConsumo = MessagePack.MessagePackSerializer
                                            .Deserialize<ConsumeResultMsg>(env.Payload);
                        break;

                    case NetMsgType.RecipeList:
                        Ricette = MessagePack.MessagePackSerializer
                                      .Deserialize<RecipeListMsg>(env.Payload).Recipes;
                        break;

                    case NetMsgType.RepairResult:
                        UltimaRiparazione ??= MessagePack.MessagePackSerializer
                                                  .Deserialize<RepairResultMsg>(env.Payload);
                        break;

                    case NetMsgType.InventoryDelta:
                    {
                        var d = MessagePack.MessagePackSerializer
                                    .Deserialize<InventoryDeltaMsg>(env.Payload);
                        lock (Zaino)
                            foreach (var it in d.Items)
                            {
                                Zaino[(it.Type, it.Subtype)] = it.Count;
                                // Gli oggetti con identita' si tengono da parte:
                                // per loro il conteggio non dice niente, conta
                                // che torni indietro LO STESSO esemplare.
                                // Quantita' zero vuol dire che non c'e' piu'.
                                if (it.InstanceId == 0) continue;
                                if (it.Count == 0) Istanze.Remove(it.InstanceId);
                                else Istanze[it.InstanceId] = (it.Type, it.Durability);
                            }
                        break;
                    }
                }
            }
            catch { /* i messaggi che non ci interessano non sono un errore */ }
        };

        _net = new NetManager(listener) { AutoRecycle = true, DisconnectTimeout = 15_000 };
        _net.Start();
        _net.Connect(host, port, "precursori");
    }

    void Aggiorna(EntityState[] entita, bool sostituisci = false)
    {
        lock (Mondo)
        {
            if (sostituisci) Mondo.Clear();
            foreach (var e in entita)
            {
                Mondo[e.EntityId] = e;
                if (e.EntityId != _playerId) continue;
                X = e.X; Y = e.Y; Z = e.Z;
                Vista = true;
            }
        }
    }

    /// <summary>L'entita di quel tipo piu vicina al punto, fra quelle viste.</summary>
    public EntityState PiuVicina(EntityType tipo, float x, float z)
    {
        EntityState best = null;
        float bestD = float.MaxValue;
        lock (Mondo)
            foreach (var e in Mondo.Values)
            {
                if (e.Type != tipo) continue;
                float d = Regole.Dist(e.X, e.Z, x, z);
                if (d < bestD) { bestD = d; best = e; }
            }
        return best;
    }

    public int Quanti(ItemType t)
    {
        lock (Zaino) return Zaino.TryGetValue((t, (byte)0), out int n) ? n : 0;
    }

    public void Poll() => _net.PollEvents();

    /// <summary>Aspetta che si avveri qualcosa, girando la rete nel frattempo.</summary>
    public bool Attendi(Func<bool> cosa, double secondi = 15)
    {
        var fine = DateTime.UtcNow.AddSeconds(secondi);
        while (DateTime.UtcNow < fine)
        {
            Poll();
            if (cosa()) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    public bool AttendiPronta() => Attendi(() => Pronta && Vista);

    public void Admin(string riga) =>
        Manda(NetMsgType.Command, new CommandMsg
        {
            CommandId = 1,
            Type      = CommandType.Admin,
            Payload   = MessagePack.MessagePackSerializer.Serialize(new AdminCommand { Line = riga }),
        });

    public void ChiediCostruzione(float px, float pz, params (ItemType Tipo, int Quanti)[] ingredienti)
    {
        UltimoCraft = null;
        Manda(NetMsgType.Command, new CommandMsg
        {
            CommandId = 2,
            Type      = CommandType.Craft,
            Payload   = MessagePack.MessagePackSerializer.Serialize(new CraftCommand
            {
                Category    = CraftCategory.Building,
                PlacementX  = px,
                PlacementZ  = pz,
                Ingredients = ingredienti
                    .Select(i => new IngredientSlot { Type = i.Tipo, Subtype = 0, Count = i.Quanti })
                    .ToArray(),
            }),
        });
    }

    public void ChiediRiparazione(ulong bersaglio)
    {
        UltimaRiparazione = null;
        Manda(NetMsgType.Command, new CommandMsg
        {
            CommandId = 3,
            Type      = CommandType.Repair,
            Payload   = MessagePack.MessagePackSerializer.Serialize(new RepairCommand
            {
                TargetEntityId = bersaglio,
            }),
        });
    }

    /// <summary>
    /// Cammina nella direzione data per il tempo dato, mandando input a 30 Hz
    /// come il client vero. Il server e' autoritativo sul movimento: qui si
    /// chiede soltanto, e si guarda dove si finisce.
    /// </summary>
    public void Cammina(float dx, float dz, double secondi)
    {
        uint seq = 0;
        var fine = DateTime.UtcNow.AddSeconds(secondi);
        var prossimo = DateTime.UtcNow;
        while (DateTime.UtcNow < fine)
        {
            Poll();
            if (DateTime.UtcNow >= prossimo)
            {
                Manda(NetMsgType.Input, new InputMsg { Seq = ++seq, MoveX = dx, MoveY = dz },
                      DeliveryMethod.Unreliable);
                prossimo = DateTime.UtcNow.AddMilliseconds(33);
            }
            Thread.Sleep(4);
        }
        // Fermarsi davvero: un input di movimento resta valido finche' non ne
        // arriva un altro, e senza questo la sonda continuerebbe a camminare
        // mentre si misura dove si e' fermata.
        //
        // L'arresto si ripete per mezzo secondo: viaggia inaffidabile come il
        // resto, e un arresto solo a volte si perdeva o arrivava prima
        // dell'ultimo passo. Nella prova del santuario due sonde hanno
        // camminato cosi' per 200 metri mentre entravano le altre (backlog P0).
        var stop = DateTime.UtcNow.AddSeconds(0.5);
        while (DateTime.UtcNow < stop)
        {
            Manda(NetMsgType.Input, new InputMsg { Seq = ++seq, MoveX = 0, MoveY = 0 },
                  DeliveryMethod.Unreliable);
            Attendi(() => false, 0.05);
        }
    }

    void Manda<T>(NetMsgType tipo, T carico, DeliveryMethod come = DeliveryMethod.ReliableOrdered)
    {
        if (_peer == null) return;
        var env = new Envelope { Type = tipo, Payload = MessagePack.MessagePackSerializer.Serialize(carico) };
        var w = new NetDataWriter();
        w.Put(MessagePack.MessagePackSerializer.Serialize(env));
        _peer.Send(w, come);
    }

    /// <summary>
    /// Se ne va salutando.
    ///
    /// Spegnere e basta il gestore di rete non avvisa il server, che se ne
    /// accorge solo allo scadere del tempo di attesa — quindici secondi. Per la
    /// prova di persistenza e' decisivo: il giocatore viene messo da parte
    /// proprio alla disconnessione, e rientrando prima che il server l'abbia
    /// notata si ottiene un personaggio nuovo invece di quello di prima. E'
    /// esattamente il difetto che si stava cercando, simulato per sbaglio dal
    /// banco.
    /// </summary>
    public void Dispose()
    {
        _net.DisconnectAll();
        var fine = DateTime.UtcNow.AddMilliseconds(600);
        while (DateTime.UtcNow < fine) { _net.PollEvents(); Thread.Sleep(10); }
        _net.Stop();
    }
}

static class Regole
{
    public static float Dist(float ax, float az, float bx, float bz)
    {
        float dx = ax - bx, dz = az - bz;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    // ---- 1. tetto di strutture ------------------------------------------------

    /// Il tetto usato dal banco. Non zero: le altre prove devono poter
    /// costruire, e un tetto a zero le bloccherebbe tutte.
    public const int Tetto = 3;

    public static bool TettoStrutture(string token, string host, int port)
    {
        Console.WriteLine($"--- tetto di strutture per fazione (tetto = {Tetto})");
        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: handshake non completato"); return false; }

        // Fazione 4: e' la sola che riempiamo fino al tetto, cosi' le altre
        // prove restano libere di costruire.
        s.Admin("setfaction 4");
        s.Admin("tp 300 -420");
        s.Attendi(() => Dist(s.X, s.Z, 300f, -420f) < 2f, 6);
        for (int i = 0; i < Tetto; i++)
            s.Admin($"build Campfire {300 + i * 3} -420");
        s.Attendi(() => false, 1.2);

        // Un falo': tre legni grezzi. Che la sonda non li abbia non importa — il
        // tetto si controlla prima degli ingredienti, di proposito: se la
        // fazione e' al limite, dirlo subito risparmia al giocatore di andare a
        // raccogliere roba che non potra' comunque usare.
        s.ChiediCostruzione(300f, -420f, (ItemType.RawWood, 3));
        if (!s.Attendi(() => s.UltimoCraft != null))
        {
            Console.WriteLine("  FALLITA: nessuna risposta alla richiesta di costruzione");
            return false;
        }

        var esito = s.UltimoCraft;
        Console.WriteLine($"  risposta: Success={esito.Success}  \"{esito.ResultName}\"");

        if (esito.Success) { Console.WriteLine("  FALLITA: costruzione accettata nonostante il tetto"); return false; }
        if (!esito.ResultName.Contains("tetto", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  FALLITA: rifiutata ma senza dire perche'");
            return false;
        }
        Console.WriteLine("  OK: rifiutata e spiegata");
        return true;
    }

    // ---- 2. palizzate ---------------------------------------------------------

    public static bool Palizzate(string tokenA, string tokenB, string host, int port)
    {
        Console.WriteLine("--- palizzate: fermano gli altri, non i propri");

        using var a = new Sonda(tokenA, host, port);
        using var b = new Sonda(tokenB, host, port);
        if (!a.AttendiPronta() || !b.AttendiPronta())
        {
            Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco");
            return false;
        }

        // Un punto lontano dai luoghi di comparsa, per non trovarci in mezzo
        // costruzioni di sessioni precedenti.
        const float MX = 420f, MZ = 420f;

        a.Admin("setfaction 1");
        a.Admin($"tp {MX.ToString(Inv)} {MZ.ToString(Inv)}");
        a.Attendi(() => Dist(a.X, a.Z, MX, MZ) < 2f, 6);
        a.Admin($"build Palisade {MX.ToString(Inv)} {MZ.ToString(Inv)}");
        a.Attendi(() => false, 1.0);

        // --- l'estraneo non passa
        b.Admin("setfaction 2");
        b.Admin($"tp {(MX - 5f).ToString(Inv)} {MZ.ToString(Inv)}");
        if (!b.Attendi(() => Dist(b.X, b.Z, MX - 5f, MZ) < 2f, 6))
        {
            Console.WriteLine($"  FALLITA: la sonda estranea non e' arrivata al punto di partenza (e' in {b.X:F1},{b.Z:F1})");
            return false;
        }
        b.Cammina(1f, 0f, 3.0);
        float superatoB = b.X - MX;
        Console.WriteLine($"  estraneo (fazione 2): fermo a x={b.X:F2}, muro a x={MX:F2}  → scarto {superatoB:F2}");

        // --- il padrone passa
        a.Admin($"tp {(MX - 5f).ToString(Inv)} {MZ.ToString(Inv)}");
        if (!a.Attendi(() => Dist(a.X, a.Z, MX - 5f, MZ) < 2f, 6))
        {
            Console.WriteLine($"  FALLITA: la sonda padrona non e' arrivata al punto di partenza (e' in {a.X:F1},{a.Z:F1})");
            return false;
        }
        a.Cammina(1f, 0f, 3.0);
        float superatoA = a.X - MX;
        Console.WriteLine($"  padrone  (fazione 1): fermo a x={a.X:F2}, muro a x={MX:F2}  → scarto {superatoA:F2}");

        bool fermato = superatoB < 0f;   // non ha raggiunto il muro
        bool passato = superatoA > 1f;   // l'ha oltrepassato

        if (fermato && passato) { Console.WriteLine("  OK: ferma l'estraneo e lascia passare il padrone"); return true; }
        if (!fermato) Console.WriteLine("  FALLITA: l'estraneo ha attraversato la palizzata");
        if (!passato) Console.WriteLine("  FALLITA: il padrone e' rimasto bloccato dalla propria palizzata");
        return false;
    }

    // ---- 3. rinascita allo stendardo ------------------------------------------

    public static bool RinascitaAlloStendardo(string token, string host, int port)
    {
        Console.WriteLine("--- rinascita: si torna allo stendardo, non allo spawn");

        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }

        const float SX = -380f, SZ = 300f;   // dove pianteremo lo stendardo
        const float MX = -355f, MZ = 300f;   // dove andremo a morire, 25 unita' piu' in la'

        s.Admin("setfaction 3");
        s.Admin($"tp {SX.ToString(Inv)} {SZ.ToString(Inv)}");
        if (!s.Attendi(() => Dist(s.X, s.Z, SX, SZ) < 2f, 6))
        {
            Console.WriteLine($"  FALLITA: non arrivata dove piantare lo stendardo (e' in {s.X:F1},{s.Z:F1})");
            return false;
        }
        s.Admin($"build Banner {SX.ToString(Inv)} {SZ.ToString(Inv)}");
        s.Attendi(() => false, 0.6);

        s.Admin($"tp {MX.ToString(Inv)} {MZ.ToString(Inv)}");
        if (!s.Attendi(() => Dist(s.X, s.Z, MX, MZ) < 2f, 6))
        {
            Console.WriteLine($"  FALLITA: non arrivata dove morire (e' in {s.X:F1},{s.Z:F1})");
            return false;
        }

        s.Admin("kill");
        // La rinascita e' a tre secondi dalla morte; si aspetta di vedersi
        // spostati invece di contare il tempo, che dipende dal carico.
        bool tornata = s.Attendi(() => Dist(s.X, s.Z, MX, MZ) > 5f, 20);

        float dalloStendardo = Dist(s.X, s.Z, SX, SZ);
        Console.WriteLine($"  rinata in ({s.X:F1}, {s.Z:F1}); stendardo in ({SX:F0}, {SZ:F0}) → distanza {dalloStendardo:F1}");

        if (!tornata) { Console.WriteLine("  FALLITA: non e' mai rinata"); return false; }
        if (dalloStendardo > 6f)
        {
            Console.WriteLine("  FALLITA: rinata lontano dallo stendardo (forse allo spawn della fazione)");
            return false;
        }
        Console.WriteLine("  OK: rinata allo stendardo");
        return true;
    }

    // ---- 4. territorio --------------------------------------------------------

    public static bool Territorio(string tokenA, string tokenB, string host, int port)
    {
        Console.WriteLine("--- territorio: la produzione e' di chi possiede il settore");

        using var a = new Sonda(tokenA, host, port);
        using var b = new Sonda(tokenB, host, port);
        if (!a.AttendiPronta() || !b.AttendiPronta())
        {
            Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco");
            return false;
        }

        const float TX = 120f, TZ = -150f;

        a.Admin("setfaction 1");
        a.Admin($"tp {TX.ToString(Inv)} {TZ.ToString(Inv)}");
        if (!a.Attendi(() => Dist(a.X, a.Z, TX, TZ) < 2f, 6))
        {
            Console.WriteLine($"  FALLITA: la sonda padrona non e' arrivata (e' in {a.X:F1},{a.Z:F1})");
            return false;
        }
        a.Admin($"build Banner {TX.ToString(Inv)} {TZ.ToString(Inv)}");
        a.Attendi(() => false, 1.0);

        // --- l'estraneo non puo' impiantare produzione
        b.Admin("setfaction 2");
        b.Admin($"tp {(TX + 6f).ToString(Inv)} {TZ.ToString(Inv)}");
        b.Attendi(() => Dist(b.X, b.Z, TX + 6f, TZ) < 3f, 6);
        b.Admin("give Crop 4");
        b.Admin("give RawWood 4");
        b.Attendi(() => b.Quanti(ItemType.Crop) >= 2, 6);

        b.ChiediCostruzione(TX + 6f, TZ, (ItemType.Crop, 2), (ItemType.RawWood, 1));
        if (!b.Attendi(() => b.UltimoCraft != null, 10))
        {
            Console.WriteLine("  FALLITA: nessuna risposta all'estraneo");
            return false;
        }
        var rispB = b.UltimoCraft;
        Console.WriteLine($"  estraneo (fazione 2): Success={rispB.Success}  \"{rispB.ResultName}\"");

        // --- il padrone si'
        a.Admin("give Crop 4");
        a.Admin("give RawWood 4");
        a.Attendi(() => a.Quanti(ItemType.Crop) >= 2, 6);

        a.ChiediCostruzione(TX, TZ, (ItemType.Crop, 2), (ItemType.RawWood, 1));
        if (!a.Attendi(() => a.UltimoCraft != null, 10))
        {
            Console.WriteLine("  FALLITA: nessuna risposta al padrone");
            return false;
        }
        var rispA = a.UltimoCraft;
        Console.WriteLine($"  padrone  (fazione 1): Success={rispA.Success} Pending={rispA.Pending}  \"{rispA.ResultName}\"");

        bool estraneoFermato = !rispB.Success &&
                               rispB.ResultName.Contains("settore", StringComparison.OrdinalIgnoreCase);
        bool padroneAmmesso  = rispA.Pending || rispA.Success;

        if (estraneoFermato && padroneAmmesso)
        {
            Console.WriteLine("  OK: ferma l'estraneo e lascia costruire il padrone");
            return true;
        }
        if (!estraneoFermato) Console.WriteLine("  FALLITA: l'estraneo ha potuto impiantare produzione in casa d'altri");
        if (!padroneAmmesso)  Console.WriteLine("  FALLITA: il padrone non puo' costruire nel proprio settore");
        return false;
    }

    // ---- 5. riparazione dello stendardo ---------------------------------------

    public static bool RiparazioneStendardo(string token, string host, int port)
    {
        Console.WriteLine("--- assedio: lo stendardo si ripara, a rate, e solo dal padrone");

        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }

        const float RX = 200f, RZ = 200f;
        const int MaxHp = 2000, PerVolta = 150;

        s.Admin("setfaction 1");
        s.Admin($"tp {RX.ToString(Inv)} {RZ.ToString(Inv)}");
        if (!s.Attendi(() => Dist(s.X, s.Z, RX, RZ) < 2f, 6))
        {
            Console.WriteLine($"  FALLITA: non arrivata (e' in {s.X:F1},{s.Z:F1})");
            return false;
        }
        s.Admin($"build Banner {RX.ToString(Inv)} {RZ.ToString(Inv)}");

        EntityState stendardo = null;
        if (!s.Attendi(() => (stendardo = s.PiuVicina(EntityType.Banner, RX, RZ)) != null &&
                             Dist(stendardo.X, stendardo.Z, RX, RZ) < 3f, 10))
        {
            Console.WriteLine("  FALLITA: lo stendardo non si e' visto negli snapshot");
            return false;
        }

        s.Admin("give Wood 10");
        s.Admin("give Stone 10");
        s.Attendi(() => s.Quanti(ItemType.Wood) >= 2 && s.Quanti(ItemType.Stone) >= 2, 8);

        // Intatto: la riparazione non ha senso e deve dirlo.
        s.ChiediRiparazione(stendardo.EntityId);
        if (!s.Attendi(() => s.UltimaRiparazione != null, 8))
        {
            Console.WriteLine("  FALLITA: nessuna risposta alla riparazione di uno stendardo intatto");
            return false;
        }
        var intatto = s.UltimaRiparazione;
        Console.WriteLine($"  intatto:  Success={intatto.Success}  \"{intatto.Message}\"");

        // Ammaccato: si ripara di quanto previsto, non di piu'.
        s.Admin($"damage {stendardo.EntityId} 600");
        s.Attendi(() => false, 0.8);

        s.ChiediRiparazione(stendardo.EntityId);
        if (!s.Attendi(() => s.UltimaRiparazione != null, 8))
        {
            Console.WriteLine("  FALLITA: nessuna risposta alla riparazione");
            return false;
        }
        var riparato = s.UltimaRiparazione;
        int atteso = MaxHp - 600 + PerVolta;
        Console.WriteLine($"  riparato: Success={riparato.Success} punti vita={riparato.Durability} (atteso {atteso})");

        bool intattoRifiutato = !intatto.Success;
        bool riparatoBene     = riparato.Success && riparato.Durability == atteso;

        if (intattoRifiutato && riparatoBene)
        {
            Console.WriteLine("  OK: rifiuta se intatto, e ripara a rate");
            return true;
        }
        if (!intattoRifiutato) Console.WriteLine("  FALLITA: ha 'riparato' uno stendardo gia' intatto");
        if (!riparatoBene)     Console.WriteLine("  FALLITA: la riparazione non ha reso i punti vita previsti");
        return false;
    }

    // ---- 6. i comandi di amministrazione sono chiusi --------------------------

    public static bool AmministrazioneChiusa(string token, string host, int port)
    {
        Console.WriteLine("--- amministrazione: chiusa a chi non e' amministratore");

        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }

        float partenzaX = s.X, partenzaZ = s.Z;
        s.Admin("tp 0 0");
        s.Attendi(() => false, 2.0);

        float spostamento = Dist(s.X, s.Z, partenzaX, partenzaZ);
        Console.WriteLine($"  dopo un 'tp 0 0' non autorizzato si e' spostata di {spostamento:F2}");

        if (spostamento < 3f) { Console.WriteLine("  OK: il comando e' stato ignorato"); return true; }
        Console.WriteLine("  FALLITA: un client qualunque puo' teletrasportarsi");
        return false;
    }

    // ---- 7. catalogo: si ottiene la ricetta che si e' chiesta -----------------
    //
    // E' la ragione per cui la scelta viaggia come indice e non come lista di
    // ingredienti. Banco da lavoro, abbeveratoio e distributore d'acqua chiedono
    // tutti Legno x3 e Pietra x2: dagli ingredienti sono indistinguibili, e
    // scegliere dal catalogo resterebbe un terno al lotto.

    public static bool CatalogoRicette(string token, string host, int port)
    {
        Console.WriteLine("--- catalogo: due ricette con gli stessi ingredienti restano distinte");

        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }
        if (!s.Attendi(() => s.Ricette.Length > 0, 10))
        {
            Console.WriteLine("  FALLITA: il catalogo non e' arrivato");
            return false;
        }

        int iTrough = s.IndiceRicetta("Water Trough");
        int iBench  = s.IndiceRicetta("Workbench");
        if (iTrough < 0 || iBench < 0)
        {
            Console.WriteLine($"  FALLITA: ricette non trovate nel catalogo ({s.Ricette.Length} presenti)");
            return false;
        }

        // Stessi ingredienti: e' proprio il caso che rende necessario l'indice.
        string IngDi(int i) => string.Join("+", s.Ricette[i].Ingredients
            .Select(g => $"{(g.Count <= 0 ? 1 : g.Count)}x{g.Type}"));
        Console.WriteLine($"  Workbench     = {IngDi(iBench)}");
        Console.WriteLine($"  Water Trough  = {IngDi(iTrough)}");

        const float CX = -120f, CZ = 260f;
        s.Admin("setfaction 3");
        s.Admin($"tp {CX.ToString(Inv)} {CZ.ToString(Inv)}");
        s.Admin("clearinv");
        s.Admin("give Wood 20");
        s.Admin("give Stone 20");
        if (!s.Attendi(() => s.Quanti(ItemType.Wood) >= 3 && s.Quanti(ItemType.Stone) >= 2 &&
                             Dist(s.X, s.Z, CX, CZ) < 2f, 12))
        {
            Console.WriteLine("  FALLITA: preparazione non riuscita");
            return false;
        }

        // Si chiede l'abbeveratoio, non il banco.
        s.ChiediRicetta(iTrough, CX + 2f, CZ);

        EntityState nato = null;
        bool comparso = s.Attendi(() =>
            (nato = s.PiuVicina(EntityType.WaterTrough, CX, CZ)) != null &&
            Dist(nato.X, nato.Z, CX, CZ) < 8f, 20);

        var bancoIndesiderato = s.PiuVicina(EntityType.Workbench, CX, CZ);
        bool bancoSpurio = bancoIndesiderato != null &&
                           Dist(bancoIndesiderato.X, bancoIndesiderato.Z, CX, CZ) < 8f;

        Console.WriteLine($"  chiesto Water Trough → comparso: {(comparso ? "Water Trough" : "niente")}" +
                          (bancoSpurio ? ", ma c'e' anche un Workbench" : ""));

        if (comparso && !bancoSpurio) { Console.WriteLine("  OK: e' arrivata la ricetta chiesta"); return true; }
        if (!comparso)   Console.WriteLine("  FALLITA: l'abbeveratoio non e' comparso");
        if (bancoSpurio) Console.WriteLine("  FALLITA: e' stato costruito un banco al posto suo");
        return false;
    }

    // ---- 8. mangiare cura ------------------------------------------------------

    public static bool MangiareCura(string token, string host, int port)
    {
        Console.WriteLine("--- cibo: mangiare restituisce vita, e da sazi si rifiuta");

        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }

        s.Admin("clearinv");
        s.Admin("give Meat 5");
        if (!s.Attendi(() => s.Quanti(ItemType.Meat) >= 2, 10))
        {
            Console.WriteLine("  FALLITA: non sono riuscito a procurare il cibo");
            return false;
        }

        // Da integri il cibo non va sprecato: deve rifiutare.
        s.Mangia(ItemType.Meat);
        if (!s.Attendi(() => s.UltimoConsumo != null, 8))
        {
            Console.WriteLine("  FALLITA: nessuna risposta al primo tentativo");
            return false;
        }
        var daIntegro = s.UltimoConsumo;
        Console.WriteLine($"  da integro:  Success={daIntegro.Success}  \"{daIntegro.Message}\"");

        // Ora ferito: si scende a 40 con un danno, poi si mangia.
        s.Admin("sethp 40");
        if (!s.Attendi(() => false, 1.5)) { }

        int carnePrima = s.Quanti(ItemType.Meat);
        s.Mangia(ItemType.Meat);
        if (!s.Attendi(() => s.UltimoConsumo != null && s.UltimoConsumo != daIntegro, 8))
        {
            Console.WriteLine("  FALLITA: nessuna risposta al secondo tentativo");
            return false;
        }
        var daFerito = s.UltimoConsumo;
        s.Attendi(() => s.Quanti(ItemType.Meat) < carnePrima, 6);

        Console.WriteLine($"  da ferito:   Success={daFerito.Success}  \"{daFerito.Message}\"  vita={daFerito.Hp}" +
                          $"  carne {carnePrima} → {s.Quanti(ItemType.Meat)}");

        bool rifiutaDaIntegro = !daIntegro.Success;
        bool curaDaFerito     = daFerito.Success && daFerito.Hp == 65;   // 40 + 25 della carne
        bool consumata        = s.Quanti(ItemType.Meat) == carnePrima - 1;

        if (rifiutaDaIntegro && curaDaFerito && consumata)
        {
            Console.WriteLine("  OK: cura di quanto previsto e consuma una unita");
            return true;
        }
        if (!rifiutaDaIntegro) Console.WriteLine("  FALLITA: ha sprecato cibo su chi era gia in forze");
        if (!curaDaFerito)     Console.WriteLine("  FALLITA: la vita non e' salita di 25 come previsto");
        if (!consumata)        Console.WriteLine("  FALLITA: il cibo non e' stato consumato");
        return false;
    }

    // ---- 9. bauli: l'esemplare resta lo stesso ---------------------------------
    //
    // Il baule e' il primo posto dove un attrezzo passa di mano, ed e' li' che
    // rischia di tornare una riga qualunque d'inventario: dentro con la sua
    // usura, fuori nuovo di zecca. Con un pezzo unico sarebbe peggio — smetterebbe
    // di essere unico senza che nessuno se ne accorga.

    public static bool Bauli(string token, string host, int port)
    {
        Console.WriteLine("--- bauli: un'ascia usata esce com'e' entrata");

        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }

        const float BX = 60f, BZ = -300f;
        s.Admin("setfaction 2");
        s.Admin($"tp {BX.ToString(Inv)} {BZ.ToString(Inv)}");
        s.Admin("clearinv");
        s.Admin($"build Chest {BX.ToString(Inv)} {BZ.ToString(Inv)}");
        s.Admin("give Axe 1");
        s.Admin("give Stone 8");

        EntityState baule = null;
        if (!s.Attendi(() => (baule = s.PiuVicina(EntityType.Chest, BX, BZ)) != null &&
                             Dist(baule.X, baule.Z, BX, BZ) < 4f &&
                             s.Istanze.Values.Any(i => i.Tipo == ItemType.Axe) &&
                             s.Quanti(ItemType.Stone) == 8, 15))
        {
            Console.WriteLine("  FALLITA: preparazione non riuscita");
            return false;
        }

        var ascia = s.Istanze.First(i => i.Value.Tipo == ItemType.Axe);
        ulong idAscia = ascia.Key;
        int usuraPrima = ascia.Value.Usura;
        Console.WriteLine($"  ascia id={idAscia} usura {usuraPrima}");

        // --- deposito dell'esemplare
        s.SpostaNelBaule(baule.EntityId, ItemType.Axe, 1, idAscia, deposita: true);
        if (!s.Attendi(() => s.UltimoBaule != null, 10))
        { Console.WriteLine("  FALLITA: nessuna risposta al deposito"); return false; }

        var dentro = s.UltimoBaule.Items.FirstOrDefault(i => i.InstanceId == idAscia);
        bool uscitaDalloZaino = !s.Istanze.ContainsKey(idAscia);
        Console.WriteLine($"  dopo il deposito: nel baule {(dentro != null ? $"id={dentro.InstanceId} usura {dentro.Durability}" : "NON c'e'")}" +
                          $", nello zaino {(uscitaDalloZaino ? "non c'e' piu'" : "c'e' ancora")}");

        // --- deposito di una pila
        s.SpostaNelBaule(baule.EntityId, ItemType.Stone, 8, 0, deposita: true);
        s.Attendi(() => s.UltimoBaule != null && s.UltimoBaule.Items.Any(i => i.Type == ItemType.Stone), 10);
        var pietre = s.UltimoBaule?.Items.FirstOrDefault(i => i.Type == ItemType.Stone);

        // --- ripresa dell'esemplare
        s.SpostaNelBaule(baule.EntityId, ItemType.Axe, 1, idAscia, deposita: false);
        if (!s.Attendi(() => s.Istanze.ContainsKey(idAscia), 10))
        { Console.WriteLine("  FALLITA: l'ascia non e' tornata nello zaino"); return false; }

        int usuraDopo = s.Istanze[idAscia].Usura;
        Console.WriteLine($"  ripresa:  id={idAscia} usura {usuraDopo} (era {usuraPrima}); pietre nel baule: {pietre?.Count ?? 0}");

        bool depositata  = dentro != null && dentro.Durability == usuraPrima;
        bool stessaAscia = usuraDopo == usuraPrima;
        bool pilaOk      = pietre != null && pietre.Count == 8;

        if (depositata && uscitaDalloZaino && stessaAscia && pilaOk)
        {
            Console.WriteLine("  OK: stesso esemplare all'andata e al ritorno, e le pile si spostano intere");
            return true;
        }
        if (!depositata)       Console.WriteLine("  FALLITA: l'ascia non e' entrata nel baule con la sua usura");
        if (!uscitaDalloZaino) Console.WriteLine("  FALLITA: l'ascia risulta ancora nello zaino: duplicata");
        if (!stessaAscia)      Console.WriteLine("  FALLITA: e' tornata un'ascia diversa");
        if (!pilaOk)           Console.WriteLine("  FALLITA: la pila di pietre non si e' spostata intera");
        return false;
    }

    // ---- 10. l'orto si semina e si esaurisce ----------------------------------
    //
    // Il seme era un vicolo cieco: due punti del codice lo davano, zero lo
    // usavano. Un oggetto che si accumula e non serve a niente non e un premio.

    public static bool Agricoltura(string token, string host, int port)
    {
        Console.WriteLine("--- orto: si semina, rende, e si esaurisce");

        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }
        if (!s.Attendi(() => s.Ricette.Length > 0, 10))
        { Console.WriteLine("  FALLITA: catalogo non arrivato"); return false; }

        const float OX = -60f, OZ = -320f;
        s.Admin("setfaction 2");
        s.Admin($"tp {OX.ToString(Inv)} {OZ.ToString(Inv)}");
        s.Admin("clearinv");
        s.Admin($"build FarmPlot {OX.ToString(Inv)} {OZ.ToString(Inv)}");

        EntityState orto = null;
        if (!s.Attendi(() => (orto = s.PiuVicina(EntityType.FarmPlot, OX, OZ)) != null &&
                             Dist(orto.X, orto.Z, OX, OZ) < 4f, 12))
        { Console.WriteLine("  FALLITA: l'orto non e' comparso"); return false; }

        // Un orto piantato d'ufficio non passa dalla ricetta, quindi nasce senza
        // semine: e' esattamente il caso "a riposo" che vogliamo provare.
        int semineIniziali = (int)orto.Extra2;
        Console.WriteLine($"  orto creato: semine {semineIniziali}");

        s.Admin("give Crop 4");
        if (!s.Attendi(() => s.Quanti(ItemType.Crop) == 4, 10))
        { Console.WriteLine("  FALLITA: non ho ottenuto i raccolti da seminare"); return false; }

        // Semina: il gather su un orto a riposo semina invece di raccogliere.
        s.Raccogli(orto.EntityId);

        bool seminato = s.Attendi(() =>
        {
            var o = s.PiuVicina(EntityType.FarmPlot, OX, OZ);
            return o != null && (int)o.Extra2 >= 4;
        }, 15);

        var dopo = s.PiuVicina(EntityType.FarmPlot, OX, OZ);
        int semineDopo = dopo != null ? (int)dopo.Extra2 : -1;
        int raccoltiRimasti = s.Quanti(ItemType.Crop);

        Console.WriteLine($"  dopo la semina: semine {semineDopo}, raccolti in zaino {raccoltiRimasti}");

        bool semineSalite = semineDopo >= 4;
        bool raccoltiSpesi = raccoltiRimasti == 0;

        if (seminato && semineSalite && raccoltiSpesi)
        {
            Console.WriteLine("  OK: i raccolti sono finiti sottoterra e il campo e' seminato");
            return true;
        }
        if (!semineSalite)  Console.WriteLine("  FALLITA: le semine non sono aumentate");
        if (!raccoltiSpesi) Console.WriteLine("  FALLITA: i raccolti non sono stati consumati");
        return false;
    }

    // ---- 11. i depositi accettano solo cio che gli spetta ----------------------

    public static bool Depositi(string token, string host, int port)
    {
        Console.WriteLine("--- depositi: nel granaio il cibo, nella torre l'acqua");

        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }

        const float DX = 140f, DZ = 340f;
        s.Admin("setfaction 2");
        s.Admin($"tp {DX.ToString(Inv)} {DZ.ToString(Inv)}");
        s.Admin("clearinv");
        s.Admin($"build Granary {DX.ToString(Inv)} {DZ.ToString(Inv)}");
        s.Admin("give Meat 6");
        s.Admin("give Stone 6");

        EntityState granaio = null;
        if (!s.Attendi(() => (granaio = s.PiuVicina(EntityType.Granary, DX, DZ)) != null &&
                             Dist(granaio.X, granaio.Z, DX, DZ) < 4f &&
                             s.Quanti(ItemType.Meat) == 6 && s.Quanti(ItemType.Stone) == 6, 15))
        { Console.WriteLine("  FALLITA: preparazione non riuscita"); return false; }

        // La pietra non e cibo: deve essere rifiutata, e con una spiegazione.
        s.SpostaNelBaule(granaio.EntityId, ItemType.Stone, 6, 0, deposita: true);
        if (!s.Attendi(() => s.UltimoBaule != null, 10))
        { Console.WriteLine("  FALLITA: nessuna risposta al deposito sbagliato"); return false; }
        var rifiuto = s.UltimoBaule;
        Console.WriteLine($"  pietra nel granaio: \"{rifiuto.Message}\"  (pietre in zaino {s.Quanti(ItemType.Stone)})");

        // La carne si.
        s.SpostaNelBaule(granaio.EntityId, ItemType.Meat, 4, 0, deposita: true);
        if (!s.Attendi(() => s.UltimoBaule != null && s.UltimoBaule.Items.Any(i => i.Type == ItemType.Meat), 10))
        { Console.WriteLine("  FALLITA: la carne non e' entrata"); return false; }
        var carneDentro = s.UltimoBaule.Items.First(i => i.Type == ItemType.Meat);
        Console.WriteLine($"  carne nel granaio: {carneDentro.Count}  (in zaino {s.Quanti(ItemType.Meat)})");

        // E si riprende.
        s.SpostaNelBaule(granaio.EntityId, ItemType.Meat, 2, 0, deposita: false);
        s.Attendi(() => s.Quanti(ItemType.Meat) >= 4, 10);
        Console.WriteLine($"  dopo il prelievo di 2: in zaino {s.Quanti(ItemType.Meat)}");

        bool pietraRespinta = s.Quanti(ItemType.Stone) == 6 &&
                              !string.IsNullOrEmpty(rifiuto.Message);
        bool carneEntrata   = carneDentro.Count == 4 && s.Quanti(ItemType.Meat) >= 4;

        if (pietraRespinta && carneEntrata)
        {
            Console.WriteLine("  OK: accetta il cibo, respinge il resto, e restituisce");
            return true;
        }
        if (!pietraRespinta) Console.WriteLine("  FALLITA: ha accettato la pietra nel granaio");
        if (!carneEntrata)   Console.WriteLine("  FALLITA: la carne non ha fatto andata e ritorno");
        return false;
    }

    // ---- 12. costruzioni per combinazione --------------------------------------

    public static bool Combinazioni(string token, string host, int port)
    {
        Console.WriteLine("--- combinazioni: due banchi diventano un tavolo da costruzione");

        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }

        const float CX = 320f, CZ = 120f;
        s.Admin("setfaction 2");
        s.Admin($"tp {CX.ToString(Inv)} {CZ.ToString(Inv)}");
        s.Admin($"build Workbench {(CX + 2f).ToString(Inv)} {CZ.ToString(Inv)}");
        s.Admin($"build Workbench {(CX - 2f).ToString(Inv)} {CZ.ToString(Inv)}");
        s.Attendi(() => false, 0.8);

        if (!s.Attendi(() => s.Mondo.Values.Count(e => e.Type == EntityType.Workbench &&
                                                       Dist(e.X, e.Z, CX, CZ) < 6f) >= 2, 15))
        { Console.WriteLine("  FALLITA: i due banchi non sono comparsi"); return false; }

        Console.WriteLine("  due banchi in posizione");
        s.Combina(EntityType.ConstructorTable, CX, CZ);

        EntityState tavolo = null;
        bool nato = s.Attendi(() => (tavolo = s.PiuVicina(EntityType.ConstructorTable, CX, CZ)) != null &&
                                    Dist(tavolo.X, tavolo.Z, CX, CZ) < 16f, 20);

        int banchiRimasti = s.Mondo.Values.Count(e => e.Type == EntityType.Workbench &&
                                                      Dist(e.X, e.Z, CX, CZ) < 6f);
        Console.WriteLine($"  esito: tavolo {(nato ? "creato" : "NON creato")}, banchi rimasti li' {banchiRimasti}");

        // I banchi devono sparire: una combinazione che non consuma gli
        // ingredienti sarebbe un modo di fabbricare tavoli dal nulla.
        if (!(nato && banchiRimasti == 0))
        {
            if (!nato)             Console.WriteLine("  FALLITA: la combinazione non ha prodotto niente");
            if (banchiRimasti > 0) Console.WriteLine("  FALLITA: i banchi non sono stati consumati");
            return false;
        }
        Console.WriteLine("  il tavolo c'e' e i banchi sono stati consumati");

        // --- quattro orti fanno una fattoria, e la fattoria e' un'altra cosa
        const float FX = 260f, FZ = 200f;
        s.Admin($"tp {FX.ToString(Inv)} {FZ.ToString(Inv)}");
        for (int i = 0; i < 4; i++)
            s.Admin($"build FarmPlot {(FX + i * 2f).ToString(Inv)} {FZ.ToString(Inv)}");
        s.Attendi(() => false, 1.2);

        if (!s.Attendi(() => s.Mondo.Values.Count(e => e.Type == EntityType.FarmPlot &&
                                                       Dist(e.X, e.Z, FX, FZ) < 10f) >= 4, 15))
        { Console.WriteLine("  FALLITA: i quattro orti non sono comparsi"); return false; }

        s.Combina(EntityType.Farm, FX, FZ);

        EntityState fattoria = null;
        bool fatta = s.Attendi(() => (fattoria = s.PiuVicina(EntityType.Farm, FX, FZ)) != null &&
                                     Dist(fattoria.X, fattoria.Z, FX, FZ) < 16f, 20);
        int ortiRimasti = s.Mondo.Values.Count(e => e.Type == EntityType.FarmPlot &&
                                                    Dist(e.X, e.Z, FX, FZ) < 10f);

        Console.WriteLine($"  quattro orti → fattoria: {(fatta ? "creata" : "NON creata")}, orti rimasti {ortiRimasti}");

        if (fatta && ortiRimasti == 0)
        { Console.WriteLine("  OK: entrambe le combinazioni funzionano"); return true; }
        if (!fatta)            Console.WriteLine("  FALLITA: la fattoria non e' stata creata");
        if (ortiRimasti > 0)   Console.WriteLine("  FALLITA: gli orti non sono stati consumati");
        return false;
    }

    // ---- 13. la terra si consuma e se lo ricorda -------------------------------
    //
    // Portato dal ramo phase0-persistence. La cicatrice si vedeva ma non costava
    // niente: il terreno cambiava forma e il mondo restava ricco come prima.

    public static bool TerraSiConsuma(string token, string host, int port)
    {
        Console.WriteLine("--- terra: inaridirla la impoverisce, e il mondo se lo ricorda");

        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }
        if (!s.Attendi(() => s.UltimaVitalita != null, 12))
        { Console.WriteLine("  FALLITA: la mappa di vitalita' non e' arrivata all'ingresso"); return false; }

        long prima = s.VitalitaTotale();
        int chunk = s.UltimaVitalita.Vitality.Length;
        Console.WriteLine($"  all'ingresso: {chunk} chunk, vitalita' totale {prima}");

        // Lo spawn della fazione 1: terra per definizione, dato che i giocatori
        // ci compaiono. Un punto scelto a caso sarebbe quasi sempre oceano —
        // 840 chunk su 1024 sono acqua — e l'oceano non si inaridisce, quindi la
        // prova fallirebbe senza che ci sia niente di rotto.
        const float TX = -400f, TZ = -400f;
        s.Admin($"tp {TX.ToString(Inv)} {TZ.ToString(Inv)}");
        if (!s.Attendi(() => Dist(s.X, s.Z, TX, TZ) < 3f, 8))
        { Console.WriteLine("  FALLITA: non arrivata sul punto"); return false; }

        s.Admin("blight 80 0.5");
        bool scesa = s.Attendi(() => s.VitalitaTotale() < prima, 12);
        long dopo = s.VitalitaTotale();

        Console.WriteLine($"  dopo l'inaridimento: {dopo}  (differenza {dopo - prima})");

        if (!scesa)
        {
            Console.WriteLine("  FALLITA: la vitalita' non e' scesa");
            return false;
        }
        if (chunk != 1024)
        {
            Console.WriteLine($"  FALLITA: attesi 1024 chunk, ricevuti {chunk}");
            return false;
        }
        Console.WriteLine("  OK: la terra si consuma e il mondo lo comunica");
        return true;
    }

    // ---- 14. morire costa ------------------------------------------------------
    //
    // Fino a ieri si rinasceva con lo zaino intatto. In un gioco dove il PvP e'
    // conquista era la scelta piu' forte del progetto, presa senza volerlo.

    public static bool MorireCosta(string token, string host, int port)
    {
        Console.WriteLine("--- morte: si perde quello che si porta");

        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }

        const float MX = 300f, MZ = -300f;
        s.Admin("setfaction 4");
        s.Admin($"tp {MX.ToString(Inv)} {MZ.ToString(Inv)}");
        s.Admin("clearinv");
        s.Admin("give Wood 30");
        s.Admin("give Stone 30");
        s.Admin("give Axe 1");

        if (!s.Attendi(() => s.Quanti(ItemType.Wood) == 30 && s.Quanti(ItemType.Stone) == 30 &&
                             s.Istanze.Values.Any(i => i.Tipo == ItemType.Axe) &&
                             Dist(s.X, s.Z, MX, MZ) < 3f, 15))
        { Console.WriteLine("  FALLITA: preparazione non riuscita"); return false; }

        Console.WriteLine($"  prima: legno {s.Quanti(ItemType.Wood)}, pietra {s.Quanti(ItemType.Stone)}, " +
                          $"asce {s.Istanze.Values.Count(i => i.Tipo == ItemType.Axe)}");

        s.Admin("kill");

        bool svuotato = s.Attendi(() => s.Quanti(ItemType.Wood) == 0 &&
                                        s.Quanti(ItemType.Stone) == 0 &&
                                        !s.Istanze.Values.Any(i => i.Tipo == ItemType.Axe), 20);

        Console.WriteLine($"  dopo:  legno {s.Quanti(ItemType.Wood)}, pietra {s.Quanti(ItemType.Stone)}, " +
                          $"asce {s.Istanze.Values.Count(i => i.Tipo == ItemType.Axe)}");

        // Parte della roba deve essere finita a terra, non essere sparita tutta:
        // "non e' detto che caschi" non vuol dire "non casca mai".
        int aTerra = s.Mondo.Values.Count(e => e.Type == EntityType.DroppedItem &&
                                               Dist(e.X, e.Z, MX, MZ) < 12f);
        Console.WriteLine($"  oggetti a terra vicino al punto di morte: {aTerra}");

        if (!svuotato)
        {
            Console.WriteLine("  FALLITA: lo zaino non si e' svuotato morendo");
            return false;
        }
        Console.WriteLine("  OK: morire svuota lo zaino" +
                          (aTerra > 0 ? " e qualcosa resta a terra" : " (stavolta non e' caduto niente: e' possibile)"));
        return true;
    }

    // ---- 15. carestia ----------------------------------------------------------
    //
    // Decisa da Tommaso al posto della fine del mondo del ramo: sotto il 5% la
    // terra non da piu niente, e i rituali non danno ne doni ne le conseguenze
    // solite. Si prova anche l'uscita, perche una carestia da cui non si esce
    // nemmeno d'ufficio lascerebbe il mondo di prova inservibile.
    //
    // Inizio e fine arrivano come evento del mondo a tutti i collegati: per
    // questo c'e' un secondo giocatore che guarda soltanto. Il numero della
    // vitalita' invece non deve arrivare a nessuno.

    public static bool Carestia(string token, string tokenOsservatore, string host, int port)
    {
        Console.WriteLine("--- carestia: sotto il 5% i rituali non danno niente, e lo sanno tutti");

        using var s = new Sonda(token, host, port);
        using var o = new Sonda(tokenOsservatore, host, port);
        if (!s.AttendiPronta() || !o.AttendiPronta())
        { Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco"); return false; }

        // Il cerchio al centro dell'isola centrale: SpawnDevWorld lo mette li'
        // con questo identificativo.
        const ulong Sito = 20_000;
        const float RX = 2f, RZ = 0f;
        s.Admin($"tp {RX.ToString(Inv)} {RZ.ToString(Inv)}");
        s.Admin("give Crystal 1 Common");
        if (!s.Attendi(() => Dist(s.X, s.Z, RX, RZ) < 3f && s.Cristalli(CrystalType.Common) >= 1, 10))
        { Console.WriteLine("  FALLITA: preparazione non riuscita (posizione o cristallo)"); return false; }

        if (!s.Attendi(() => s.UltimaPartita != null && !s.UltimaPartita.InFamine, 6))
        { Console.WriteLine("  FALLITA: il mondo di prova e' gia' in carestia, o lo stato non arriva"); return false; }

        s.Admin("setvitality 0.02");
        bool entrata = s.Attendi(() => s.UltimaPartita.InFamine, 6);
        // L'osservatore non gira la rete da solo: lo si fa girare qui.
        bool avvisati = s.Attendi(() => { o.Poll(); return s.HaEvento(WorldEventKind.FamineBegins)
                                                    && o.HaEvento(WorldEventKind.FamineBegins); }, 6);
        Console.WriteLine($"  dopo setvitality 0.02: carestia={s.UltimaPartita.InFamine}, " +
                          $"evento a chi l'ha causata={s.HaEvento(WorldEventKind.FamineBegins)}, " +
                          $"all'altro={o.HaEvento(WorldEventKind.FamineBegins)}");

        bool ok = true;
        if (!entrata) { Console.WriteLine("  FALLITA: la carestia non e' cominciata"); ok = false; }
        if (!avvisati) { Console.WriteLine("  FALLITA: l'evento d'inizio carestia non e' arrivato a tutti"); ok = false; }
        if (s.UltimaPartita.WorldVitality != 0f || (o.UltimaPartita != null && o.UltimaPartita.WorldVitality != 0f))
        { Console.WriteLine("  FALLITA: il numero della vitalita' arriva ai giocatori"); ok = false; }
        else
        {
            int prima = s.Cristalli(CrystalType.Common);
            s.Rituale(Sito, CrystalType.Common);
            if (!s.Attendi(() => s.UltimoRituale != null, 6))
            { Console.WriteLine("  FALLITA: nessuna risposta al rituale"); ok = false; }
            else
            {
                var r = s.UltimoRituale;
                s.Attendi(() => s.Cristalli(CrystalType.Common) < prima, 3);
                Console.WriteLine($"  rituale: accettato={r.Accepted}, dono='{r.Boon}', unico='{r.UniqueName}'");
                Console.WriteLine($"           esito='{r.Bane}', cristalli {prima} -> {s.Cristalli(CrystalType.Common)}");
                if (!r.Accepted) { Console.WriteLine("  FALLITA: in carestia il rituale si deve poter fare"); ok = false; }
                if (!string.IsNullOrEmpty(r.Boon) || !string.IsNullOrEmpty(r.UniqueName))
                { Console.WriteLine("  FALLITA: in carestia il rituale ha dato un dono"); ok = false; }
                if (s.Cristalli(CrystalType.Common) >= prima)
                { Console.WriteLine("  FALLITA: il cristallo non e' stato consumato"); ok = false; }
            }
        }

        // Si rimette la terra com'era, anche se qualcosa sopra e' fallito: le
        // prove che vengono dopo non devono girare in carestia. Il rituale ha
        // chiamato tre Eco, che non se ne vanno da soli: si tolgono.
        s.StatoEchi("uccidi");
        s.Admin("setvitality 1");
        bool uscita = s.Attendi(() => !s.UltimaPartita.InFamine, 6);
        bool avvisatiFine = s.Attendi(() => { o.Poll(); return s.HaEvento(WorldEventKind.FamineEnds)
                                                        && o.HaEvento(WorldEventKind.FamineEnds); }, 6);
        Console.WriteLine($"  dopo setvitality 1: carestia={s.UltimaPartita.InFamine}, " +
                          $"evento a chi l'ha causata={s.HaEvento(WorldEventKind.FamineEnds)}, " +
                          $"all'altro={o.HaEvento(WorldEventKind.FamineEnds)}");
        if (!uscita) { Console.WriteLine("  FALLITA: la carestia non e' finita riportando la terra al 100%"); ok = false; }
        if (!avvisatiFine) { Console.WriteLine("  FALLITA: l'evento di fine carestia non e' arrivato a tutti"); ok = false; }

        if (ok) Console.WriteLine("  OK: in carestia il rituale costa il cristallo e non da niente; tutti lo sanno; se ne esce");
        return ok;
    }

    // ---- 15a. gli Echi ---------------------------------------------------------
    //
    // Regole di Tommaso (backlog A1a4): un rituale in carestia chiama gli Echi
    // del grado del cristallo; cacciano prima chi ha celebrato, poi chiunque;
    // chi colpisce un grande si ritrova addosso i piccoli; non rinascono; il
    // cristallo lo lascia solo il grande. La sonda non sa combattere: il colpo
    // e le uccisioni passano dal comando admin "echi", che usa le stesse
    // funzioni del combattimento vero.

    /// Vero se un Eco e' a meno di r metri dalla sonda, fra quelli che vede.
    static bool EcoVicino(Sonda s, float r)
    {
        lock (s.Mondo)
            return s.Mondo.Values.Any(e => e.Type == EntityType.Echo && Dist(e.X, e.Z, s.X, s.Z) < r);
    }

    /// La vita della sonda, da quello che il server le mostra di se'.
    static int Vita(Sonda s, ulong id)
    {
        lock (s.Mondo) return s.Mondo.TryGetValue(id, out var e) ? (int)e.Extra0 : -1;
    }

    public static bool Echi(string token, string tokenOsservatore, string host, int port)
    {
        Console.WriteLine("--- echi: un rituale in carestia chiama gli Echi, che cacciano chi ha celebrato");

        using var s = new Sonda(token, host, port);
        using var o = new Sonda(tokenOsservatore, host, port);
        if (!s.AttendiPronta() || !o.AttendiPronta())
        { Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco"); return false; }

        const ulong Sito = 20_000;   // il cerchio al centro dell'isola centrale
        const float RX = 2f, RZ = 0f;

        // Si parte senza Echi: un giro precedente andato male potrebbe averne
        // lasciati.
        var stato = s.StatoEchi("uccidi");
        int altiPrima = Sonda.Numero(stato, "cristalli alti a terra");

        s.Admin($"tp {RX.ToString(Inv)} {RZ.ToString(Inv)}");
        s.Admin("give Crystal 1 Epic");
        o.Admin("tp 2 -20");
        if (!s.Attendi(() => { o.Poll(); return Dist(s.X, s.Z, RX, RZ) < 3f && Dist(o.X, o.Z, 2f, -20f) < 3f
                                               && s.Cristalli(CrystalType.Epic) >= 1; }, 10))
        { Console.WriteLine("  FALLITA: preparazione non riuscita (posizioni o cristallo)"); return false; }

        bool ok = true;
        s.Admin("setvitality 0.02");
        if (!s.Attendi(() => s.UltimaPartita != null && s.UltimaPartita.InFamine, 6))
        { Console.WriteLine("  FALLITA: la carestia non e' cominciata"); s.Admin("setvitality 1"); return false; }

        try
        {
            // --- il rituale chiama gli Echi dell'Epico. Chi ha celebrato se ne va
            // subito a 70 metri (entro i 150 dal sito): gli Eco nascono a 10-15
            // metri e corrono a 6,5 al secondo, restando li' sarebbe morto prima
            // della prova. L'osservatore resta a 20 dal sito, piu' vicino agli
            // Echi: devono andare dal primo.
            int vitaO = Vita(o, o.Id), vitaOMin = vitaO;
            s.Rituale(Sito, CrystalType.Epic);
            s.Admin("tp 2 70");
            if (!s.Attendi(() => s.UltimoRituale != null, 6))
            { Console.WriteLine("  FALLITA: nessuna risposta al rituale"); return false; }
            var r = s.UltimoRituale;
            Console.WriteLine($"  rituale Epico: accettato={r.Accepted}, dono='{r.Boon}', esito='{r.Bane}'");
            if (!r.Accepted || !string.IsNullOrEmpty(r.Boon))
            { Console.WriteLine("  FALLITA: in carestia il rituale va accettato e non da' doni"); ok = false; }
            if (r.Bane == null || !r.Bane.Contains("arrivando"))
            { Console.WriteLine("  FALLITA: il rito non avvisa che qualcosa arriva"); ok = false; }

            stato = s.StatoEchi();
            Console.WriteLine($"  dopo il rituale: {Sonda.Descrivi(stato)}");
            if (Sonda.Numero(stato, "eco") != 6 || Sonda.Numero(stato, "vaganti") != 2
                || Sonda.Numero(stato, "divoratori") != 1 || Sonda.Numero(stato, "araldi") != 0
                || Sonda.Numero(stato, "con cristallo") != 1)
            { Console.WriteLine("  FALLITA: per un Epico servono 6 Eco, 2 Vaganti e 1 Divoratore col cristallo"); ok = false; }

            // --- prima chi ha celebrato. La vita dell'osservatore si segue per
            // tutta l'attesa: se morisse, rinascerebbe a 100 e non si vedrebbe.
            bool raggiunta = s.Attendi(() =>
            {
                o.Poll();
                int v = Vita(o, o.Id);
                if (v >= 0) vitaOMin = Math.Min(vitaOMin, v);
                return EcoVicino(s, 4f);
            }, 20);
            Console.WriteLine($"  chi ha celebrato, a 70 metri: raggiunto dagli Eco={raggiunta} (e' in {s.X:F0}, {s.Z:F0}); " +
                              $"vita dell'osservatore {vitaO}, al minimo {vitaOMin}");
            if (!raggiunta) { Console.WriteLine("  FALLITA: gli Eco non hanno inseguito chi ha celebrato"); ok = false; }
            if (vitaO > 0 && vitaOMin < vitaO)
            { Console.WriteLine("  FALLITA: gli Echi hanno attaccato l'osservatore prima di chi ha celebrato"); ok = false; }

            // L'osservatore si allontana prima che gli Echi tornino al sito, e
            // chi ha celebrato scappa oltre i 150 metri: lo dimenticano.
            o.Admin("tp 2 -60");
            s.Admin("tp 300 300");
            // Smettono subito di cacciare, ma i Vaganti tornano al sito a 3,5
            // metri al secondo: si aspetta che siano tutti a casa, cosi' tutti
            // i piccoli stanno entro 30 metri dal Divoratore.
            bool calmi = false, aCasa = false;
            for (int i = 0; i < 40 && !(calmi && aCasa); i++)
            {
                s.Attendi(() => { o.Poll(); return false; }, 1.0);
                stato = s.StatoEchi();
                calmi = Sonda.Numero(stato, "a caccia") == 0;
                aCasa = Sonda.Numero(stato, "fuori casa") == 0;
            }
            Console.WriteLine($"  scappati tutti e due: {Sonda.Descrivi(stato)}");
            if (!calmi) { Console.WriteLine("  FALLITA: gli Echi cacciano ancora senza nessuno vicino"); ok = false; }
            if (!aCasa) { Console.WriteLine("  FALLITA: gli Echi non sono tornati al sito"); ok = false; }

            // --- i piccoli difendono i grandi: l'osservatore, a 60 metri dal
            // sito e fuori dalla vista di tutti, colpisce il Divoratore.
            o.StatoEchi("colpisci");
            o.Attendi(() => false, 0.5);
            stato = o.StatoEchi();
            int aCaccia = Sonda.Numero(stato, "a caccia");
            bool difeso = o.Attendi(() => EcoVicino(o, 4f), 20);
            Console.WriteLine($"  colpito il Divoratore da lontano: a caccia {aCaccia} su 9, " +
                              $"Eco addosso a chi ha colpito={difeso}");
            if (aCaccia != 9) { Console.WriteLine("  FALLITA: non tutti i piccoli vicini difendono il grande"); ok = false; }
            if (!difeso) { Console.WriteLine("  FALLITA: gli Eco non sono arrivati addosso a chi ha colpito"); ok = false; }
            o.Admin("tp 2 -250");

            // --- muoiono, non rinascono, e il Divoratore lascia il cristallo
            stato = s.StatoEchi("uccidi");
            s.Attendi(() => false, 2.5);
            stato = s.StatoEchi();
            int alti = Sonda.Numero(stato, "cristalli alti a terra");
            Console.WriteLine($"  uccisi tutti: {Sonda.Descrivi(stato)} (cristalli alti a terra prima {altiPrima})");
            if (Sonda.Numero(stato, "eco") + Sonda.Numero(stato, "vaganti") + Sonda.Numero(stato, "divoratori") != 0)
            { Console.WriteLine("  FALLITA: qualche Echo e' rinato"); ok = false; }
            if (alti != altiPrima + 1)
            { Console.WriteLine("  FALLITA: il Divoratore non ha lasciato il suo cristallo, o l'hanno lasciato altri"); ok = false; }
        }
        finally
        {
            // Le prove dopo non devono girare in carestia ne' con Echi in giro.
            s.StatoEchi("uccidi");
            s.Admin("setvitality 1");
            s.Attendi(() => s.UltimaPartita != null && !s.UltimaPartita.InFamine, 6);
        }

        if (ok) Console.WriteLine("  OK: gli Echi arrivano, cacciano chi ha celebrato, difendono il grande e lasciano il cristallo");
        return ok;
    }

    // ---- 15a. piu rituali fai, piu rischi --------------------------------------
    //
    // Decisione di Tommaso (backlog A1c): il dono vale sempre lo stesso, ma chi
    // ha fatto tanti rituali ha piu' probabilita' di un conto, e di un conto
    // grave. Un tiro solo non dice niente di una probabilita': il comando admin
    // "rischiorituali" tira l'esito vero duemila volte, con gli stessi dadi, per
    // chi e' al primo rituale e per chi ne ha fatti quaranta. Poi si controlla
    // che il contatore salga con un rituale vero.

    /// Le quattro cifre di una risposta di "rischiorituali": solo dono, dono e
    /// conseguenza, solo conseguenza, gravi. Null se la risposta non arriva.
    static int[] Rischio(Sonda s, string grado, int fatti)
    {
        var r = s.ChiediAdmin($"rischiorituali {grado} {fatti} 2000", "rischio: ");
        if (r == null) return null;
        int Cifra(string nome)
        {
            int i = r.IndexOf(nome + " ", StringComparison.Ordinal);
            if (i < 0) return -1;
            var resto = r[(i + nome.Length + 1)..];
            int fine = resto.IndexOf(',');
            return int.TryParse(fine < 0 ? resto : resto[..fine], out int v) ? v : -1;
        }
        return new[] { Cifra("solo dono"), Cifra("dono e conseguenza"), Cifra("solo conseguenza"), Cifra("gravi") };
    }

    public static bool RitualiERischio(string token, string host, int port)
    {
        Console.WriteLine("--- rituali e rischio: chi ne ha fatti tanti paga piu' spesso, e piu' caro");

        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }

        bool ok = true;

        // Atteso (MetaStrada 20, tetto 95): Comune 20% di conti al primo
        // rituale, 70% al quarantesimo, gravi da 0 a 27% dei conti; Epico da 55%
        // a 82%, gravi da 25% a 58%. Si lascia un margine di 4 punti al caso.
        foreach (var (grado, contoPrima, contoDopo) in new[] { ("Common", 20, 70), ("Epic", 55, 82) })
        {
            var prima = Rischio(s, grado, 0);
            var dopo  = Rischio(s, grado, 40);
            if (prima == null || dopo == null || prima.Contains(-1) || dopo.Contains(-1))
            { Console.WriteLine($"  FALLITA: {grado}, nessuna risposta leggibile a rischiorituali"); ok = false; continue; }

            int ContoPct(int[] v) => (int)Math.Round(100.0 * (v[1] + v[2]) / 2000);
            int GraviPct(int[] v) => (int)Math.Round(100.0 * v[3] / 2000);
            Console.WriteLine($"  {grado}, al primo rituale: solo dono {prima[0]}, entrambi {prima[1]}, " +
                              $"solo conto {prima[2]}, gravi {prima[3]} (conto {ContoPct(prima)}%)");
            Console.WriteLine($"  {grado}, dopo 40 rituali: solo dono {dopo[0]}, entrambi {dopo[1]}, " +
                              $"solo conto {dopo[2]}, gravi {dopo[3]} (conto {ContoPct(dopo)}%)");

            if (Math.Abs(ContoPct(prima) - contoPrima) > 4 || Math.Abs(ContoPct(dopo) - contoDopo) > 4)
            { Console.WriteLine($"  FALLITA: {grado}, conto atteso {contoPrima}% e {contoDopo}%"); ok = false; }
            if (dopo[1] + dopo[2] <= prima[1] + prima[2])
            { Console.WriteLine($"  FALLITA: {grado}, dopo 40 rituali i conti non sono aumentati"); ok = false; }
            if (dopo[3] <= prima[3] || GraviPct(dopo) < GraviPct(prima) + 10)
            { Console.WriteLine($"  FALLITA: {grado}, dopo 40 rituali i conti gravi non sono aumentati abbastanza"); ok = false; }
            if (dopo[0] >= prima[0])
            { Console.WriteLine($"  FALLITA: {grado}, dopo 40 rituali si esce puliti quanto prima"); ok = false; }
            if (grado == "Common" && prima[3] != 0)
            { Console.WriteLine("  FALLITA: un Comune al primo rituale non deve mai avere un conto grave"); ok = false; }
        }

        // Il contatore vero: si parte da 3, un rituale Comune al cerchio del
        // centro lo porta a 4.
        const ulong Sito = 20_000;
        const float RX = 2f, RZ = 0f;
        int partenza = s.RitualiFatti(3);
        s.Admin($"tp {RX.ToString(Inv)} {RZ.ToString(Inv)}");
        s.Admin("give Crystal 1 Common");
        if (partenza != 3)
        { Console.WriteLine($"  FALLITA: il contatore non si fissa (risposta {partenza})"); return false; }
        if (!s.Attendi(() => Dist(s.X, s.Z, RX, RZ) < 3f && s.Cristalli(CrystalType.Common) >= 1, 10))
        { Console.WriteLine("  FALLITA: preparazione non riuscita (posizione o cristallo)"); return false; }
        if (!s.Attendi(() => s.UltimaPartita != null && !s.UltimaPartita.InFamine, 6))
        { Console.WriteLine("  FALLITA: il mondo di prova e' in carestia, il rituale non tirerebbe l'esito"); return false; }

        s.Rituale(Sito, CrystalType.Common);
        if (!s.Attendi(() => s.UltimoRituale != null, 6) || !s.UltimoRituale.Accepted)
        { Console.WriteLine($"  FALLITA: rituale non accettato ({s.UltimoRituale?.Refusal})"); return false; }
        int dopoRito = s.RitualiFatti();
        Console.WriteLine($"  rituale vero: dono='{s.UltimoRituale.Boon}', conto='{s.UltimoRituale.Bane}', " +
                          $"rituali fatti 3 -> {dopoRito}");
        if (dopoRito != 4) { Console.WriteLine("  FALLITA: il rituale non ha contato"); ok = false; }

        if (ok) Console.WriteLine("  OK: piu' rituali alle spalle, piu' conti e piu' gravi; il contatore sale");
        return ok;
    }

    // ---- 15b. riparazione ed era rigogliosa -----------------------------------
    //
    // La formula di Tommaso (backlog A1a): ogni fazione 12,5%, ogni Epico 12,5%,
    // ogni Precursore 25%; almeno 2 fazioni e 2 cristalli alti, di cui un
    // Precursore. Si ferma al 100%, salvo 4 fazioni con 4 Precursori, che
    // aprono l'era rigogliosa fino al 125%.
    //
    // Il rituale di gruppo non c'e' ancora: si prova la formula col comando
    // admin "riparazione", che ne applica il risultato. Il numero della
    // vitalita' non arriva ai giocatori, ma la mappa per chunk si': il chunk di
    // terraferma piu' arido dice a quanto e' stata portata la terra.

    static int ChunkPiuArido(Sonda s) => s.UltimaVitalita == null ? -1 : s.UltimaVitalita.Vitality.Min();

    public static bool Riparazione(string token, string tokenOsservatore, string host, int port)
    {
        Console.WriteLine("--- riparazione: la formula, i rifiuti, l'era rigogliosa");

        using var s = new Sonda(token, host, port);
        using var o = new Sonda(tokenOsservatore, host, port);
        if (!s.AttendiPronta() || !o.AttendiPronta())
        { Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco"); return false; }
        if (!s.Attendi(() => s.UltimaPartita != null && !s.UltimaPartita.InFamine && s.UltimaVitalita != null, 6))
        { Console.WriteLine("  FALLITA: il mondo di prova e' gia' in carestia, o lo stato non arriva"); return false; }

        bool ok = true;
        bool Tutti(WorldEventKind k) => s.Attendi(() => { o.Poll(); return s.HaEvento(k) && o.HaEvento(k); }, 6);

        s.Admin("setvitality 0.02");
        if (!s.Attendi(() => s.UltimaPartita.InFamine, 6))
        { Console.WriteLine("  FALLITA: la carestia d'ufficio non e' cominciata"); s.Admin("setvitality 1"); return false; }

        // Sotto il minimo: una fazione sola, poi un solo cristallo alto, poi
        // quattro Epici senza Precursore. Nessuno dei tre deve toccare la terra.
        s.Admin("riparazione 1 1 1");
        s.Admin("riparazione 2 0 1");
        s.Admin("riparazione 4 4 0");
        s.Attendi(() => false, 3);
        int dopoRifiuti = ChunkPiuArido(s);
        Console.WriteLine($"  dopo tre riparazioni sotto il minimo: carestia={s.UltimaPartita.InFamine}, chunk piu' arido {dopoRifiuti}/255");
        if (!s.UltimaPartita.InFamine || dopoRifiuti > 10)
        { Console.WriteLine("  FALLITA: una riparazione sotto il minimo ha toccato la terra"); ok = false; }

        // Il minimo: 2 fazioni, 1 Epico, 1 Precursore = 62,5%.
        s.Admin("riparazione 2 1 1");
        bool uscita = s.Attendi(() => !s.UltimaPartita.InFamine, 6);
        bool fine = Tutti(WorldEventKind.FamineEnds);
        s.Attendi(() => ChunkPiuArido(s) > 10, 6);
        int minimo = ChunkPiuArido(s);
        Console.WriteLine($"  riparazione 2 fazioni, 1 Epico, 1 Precursore: carestia={s.UltimaPartita.InFamine}, " +
                          $"fine carestia a tutti={fine}, chunk piu' arido {minimo}/255 (atteso ~159, il 62,5%)");
        if (!uscita || !fine) { Console.WriteLine("  FALLITA: la riparazione non ha chiuso la carestia per tutti"); ok = false; }
        if (Math.Abs(minimo - 159) > 2) { Console.WriteLine("  FALLITA: la terra non e' al 62,5%"); ok = false; }

        // 4 fazioni e 3 Precursori: la formula da' 125%, ma senza 4 Precursori
        // ci si ferma al 100% e l'era rigogliosa non comincia.
        s.Admin("riparazione 4 0 3");
        s.Attendi(() => ChunkPiuArido(s) == 255, 6);
        s.Attendi(() => { o.Poll(); return false; }, 3);
        int pieno = ChunkPiuArido(s);
        bool rigoglioPresto = s.HaEvento(WorldEventKind.BloomBegins) || o.HaEvento(WorldEventKind.BloomBegins);
        Console.WriteLine($"  riparazione 4 fazioni, 3 Precursori: chunk piu' arido {pieno}/255, era rigogliosa={rigoglioPresto}");
        if (pieno != 255) { Console.WriteLine("  FALLITA: la terra non e' tornata al 100%"); ok = false; }
        if (rigoglioPresto) { Console.WriteLine("  FALLITA: era rigogliosa senza 4 Precursori"); ok = false; }

        // 4 fazioni e 4 Precursori: l'era rigogliosa.
        s.Admin("riparazione 4 0 4");
        bool rigoglio = Tutti(WorldEventKind.BloomBegins);
        Console.WriteLine($"  riparazione 4 fazioni, 4 Precursori: inizio era rigogliosa a tutti={rigoglio}");
        if (!rigoglio) { Console.WriteLine("  FALLITA: l'era rigogliosa non e' cominciata per tutti"); ok = false; }

        // Si rimette la terra com'era, anche se qualcosa sopra e' fallito.
        s.Admin("setvitality 1");
        bool fineRigoglio = Tutti(WorldEventKind.BloomEnds);
        Console.WriteLine($"  dopo setvitality 1: fine era rigogliosa a tutti={fineRigoglio}");
        if (!fineRigoglio) { Console.WriteLine("  FALLITA: l'era rigogliosa non e' finita tornando al 100%"); ok = false; }

        if (ok) Console.WriteLine("  OK: la riparazione segue la formula, rifiuta sotto il minimo, e solo 4+4 apre l'era rigogliosa");
        return ok;
    }

    // ---- 15b2. il santuario -----------------------------------------------------
    //
    // Regole di Tommaso (backlog A1a3c): il rituale di riparazione vero si fa
    // solo nel santuario. E' chiuso; l'ingresso si apre con 5 giocatori per 2
    // fazioni davanti e 2 cristalli alti fra loro; dentro si offrono i
    // cristalli, e alla chiusura contano solo chi e' dentro e quello che ha
    // offerto. Sotto il 25% la terra si ripara, sopra i cristalli si perdono.
    // Dopo, un'ora di riposo.
    //
    // Undici sonde: cinque della fazione 1, sei della 2, perche' una esce prima
    // della chiusura e la fazione 2 deve restare a cinque. L'angolo si fissa a
    // 0 e il minuto d'ingresso si accorcia col comando "santuario chiudi".

    const ulong CentroDelMondo = 20_100;
    static readonly float Diag       = MathF.Sqrt(0.5f);
    static readonly float IngressoXZ = 59f * MathF.Sqrt(0.5f);   // 59 metri sulla diagonale
    static readonly float CentroXZ   = 70f * MathF.Sqrt(0.5f);

    static bool Dentro(Sonda p) => Dist(p.X, p.Z, CentroXZ, CentroXZ) <= 8f;

    /// Tutti davanti all'ingresso, fermi.
    static bool AllIngresso(Sonda[] ps)
    {
        foreach (var p in ps) p.Admin($"tp {IngressoXZ.ToString(Inv)} {IngressoXZ.ToString(Inv)}");
        bool arrivate = ps.All(p => p.Attendi(() => Dist(p.X, p.Z, IngressoXZ, IngressoXZ) < 1.5f, 6));
        foreach (var p in ps) p.Cammina(0f, 0f, 0.1);
        ScacciaBelve(ps[0]);
        return arrivate;
    }

    /// <summary>
    /// Via lupi e cinghiali da quello che la sonda vede. L'isola centrale e'
    /// selvaggia: dieci sonde ferme per un minuto davanti all'ingresso venivano
    /// sbranate una dopo l'altra e rinascevano allo stendardo, e la prova
    /// misurava i lupi invece del santuario.
    ///
    /// Si chiede al server, col comando "belve", di toglierle tutte entro 120
    /// metri. Prima si toglievano quelle a vista, ma la sonda vede solo le 32
    /// entita' piu' vicine: un lupo poco piu' in la' restava, cacciava, e una
    /// sonda rinasceva allo stendardo a 210 metri (backlog P1). 120 metri
    /// coprono l'isola centrale piu' il territorio di un lupo (32) e il suo
    /// raggio di caccia (15).
    /// </summary>
    static void ScacciaBelve(Sonda p)
    {
        p.Attendi(() => false, 0.5);
        p.Admin("belve 120");
        p.Attendi(() => false, 0.3);
    }

    /// Dieci metri verso il centro del mondo: dall'ingresso si finisce dentro,
    /// se si puo' passare.
    ///
    /// Poi ci si ferma piu' volte. L'ultimo input di Cammina viaggia
    /// inaffidabile e il server non guarda il numero di sequenza: se l'arresto
    /// arriva prima dell'ultimo passo, la sonda continua a camminare mentre le
    /// altre entrano, ed esce dall'altra parte del santuario senza saperlo.
    static void Entra(Sonda p)
    {
        p.Cammina(Diag, Diag, 2.0);
        p.Cammina(0f, 0f, 0.3);
    }

    static string DescriviSantuario(SanctuaryStateMsg s) => s == null ? "(niente)" : !s.Known ? "non si sa dove sta" :
        $"centro ({s.CenterX:F1}, {s.CenterZ:F1}) raggio {s.Radius}, ingresso ({s.EntranceX:F1}, {s.EntranceZ:F1}), " +
        $"{s.Gate}" + (s.Gate == SanctuaryGate.Open ? $" per {s.OpenSecondsLeft} s" : "") +
        $", mappa {(s.ShowOnMap ? "si" : "no")}, id {s.CenterId}";

    /// Le posizioni fresche di tutte: una sonda che non gira la rete crede di
    /// essere dove l'ha lasciata l'ultima volta che ha guardato.
    static void Aggiorna(Sonda[] ps) { foreach (var p in ps) p.Attendi(() => false, 0.2); }

    public static bool Santuario(string[] tokens, string host, int port)
    {
        Console.WriteLine("--- santuario: si apre alle condizioni, conta chi e' dentro, ripara solo sotto il 25%");

        var ps = tokens.Select(t => new Sonda(t, host, port)).ToArray();
        try
        {
            if (!ps.All(p => p.AttendiPronta()))
            { Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco"); return false; }

            var capo      = ps[0];     // fazione 1, offre il Precursore
            var epico     = ps[5];     // fazione 2, offre l'Epico
            var fuggiasco = ps[10];    // fazione 2, offre un Epico ed esce prima
            bool ok = true;

            for (int i = 0; i < ps.Length; i++)
            {
                ps[i].Admin("clearinv");
                ps[i].Admin($"setfaction {(i < 5 ? 1 : 2)}");
            }
            // --- dentro non nasce niente (Tommaso, A1a3c2a). Prima di spostarlo:
            // l'angolo scelto alla nascita del mondo e' vuoto, mentre negli
            // altri tre uno dei quaranta cristalli di partenza cade sempre
            // dentro il cerchio. Poi si sposta nell'angolo 0, e anche li' si fa
            // sgombero.
            int RisorseDentro(int angolo)
            {
                float cx = (angolo == 0 || angolo == 3 ? 1f : -1f) * CentroXZ;
                float cz = (angolo == 0 || angolo == 1 ? 1f : -1f) * CentroXZ;
                capo.Admin($"tp {cx.ToString(Inv)} {cz.ToString(Inv)}");
                capo.Attendi(() => Dist(capo.X, capo.Z, cx, cz) < 2f, 6);
                capo.Attendi(() => false, 1.5);
                lock (capo.Mondo) return capo.Mondo.Values.Count(e =>
                    (e.Type == EntityType.Crystal || e.Type.ToString().EndsWith("Node")) &&
                    Dist(e.X, e.Z, cx, cz) <= 8f);
            }
            var perAngolo = Enumerable.Range(0, 4).Select(RisorseDentro).ToArray();
            Console.WriteLine($"  nodi e cristalli dentro il cerchio, angolo per angolo: {string.Join(", ", perAngolo)}");
            if (!perAngolo.Contains(0))
            { Console.WriteLine("  FALLITA: in nessun angolo il santuario e' sgombro"); ok = false; }

            capo.Admin("santuario angolo 0");
            capo.Admin("santuario riposo");
            int dentroAngolo0 = RisorseDentro(0);
            Console.WriteLine($"  dopo lo spostamento nell'angolo 0: {dentroAngolo0} dentro");
            if (dentroAngolo0 != 0)
            { Console.WriteLine("  FALLITA: spostato il santuario, dentro resta qualcosa"); ok = false; }

            // --- il santuario nel protocollo (A1a3c2c): arriva a chi entra nel
            // mondo, segue lo spostamento, e sulla mappa si evidenzia solo in
            // carestia.
            if (ps.Any(p => p.UltimoSantuario == null))
            { Console.WriteLine("  FALLITA: all'ingresso nel mondo il santuario non e' arrivato a tutti"); ok = false; }
            bool spostato = capo.Attendi(() => capo.UltimoSantuario is { } s &&
                Dist(s.CenterX, s.CenterZ, CentroXZ, CentroXZ) < 0.1f &&
                Dist(s.EntranceX, s.EntranceZ, IngressoXZ, IngressoXZ) < 0.1f, 4);
            var vs = capo.UltimoSantuario;
            Console.WriteLine($"  il santuario visto dal client: {DescriviSantuario(vs)}");
            if (!spostato) { Console.WriteLine("  FALLITA: il client non sa che il santuario e' nell'angolo 0"); ok = false; }
            else if (vs.Radius != 8f || vs.CenterId != CentroDelMondo || vs.Gate != SanctuaryGate.Closed)
            { Console.WriteLine("  FALLITA: raggio, centro del mondo o ingresso sbagliati"); ok = false; }
            capo.Admin("setvitality 0.02");
            bool mappaCarestia = capo.Attendi(() => capo.UltimoSantuario?.ShowOnMap == true, 4);
            capo.Admin("setvitality 0.2");
            bool mappaFine = capo.Attendi(() => capo.UltimoSantuario?.ShowOnMap == false, 4);
            Console.WriteLine($"  sulla mappa: in carestia {mappaCarestia}, finita la carestia {(mappaFine ? "spento" : "ancora acceso")}");
            if (!mappaCarestia || !mappaFine)
            { Console.WriteLine("  FALLITA: il santuario non si evidenzia sulla mappa solo in carestia"); ok = false; }
            if (!capo.Attendi(() => capo.UltimaVitalita != null && ChunkPiuArido(capo) < 60, 6))
            { Console.WriteLine("  FALLITA: la terra non e' scesa al 20%"); capo.Admin("setvitality 1"); return false; }

            // --- chiuso: da solo non si entra
            if (!AllIngresso(new[] { capo }))
            { Console.WriteLine("  FALLITA: la sonda non e' arrivata all'ingresso"); return false; }
            Entra(capo);
            Console.WriteLine($"  da solo all'ingresso: dentro={Dentro(capo)}");
            if (Dentro(capo)) { Console.WriteLine("  FALLITA: il santuario chiuso lascia entrare"); ok = false; }

            // --- dieci davanti ma un solo cristallo alto: resta chiuso
            capo.Admin("give Crystal 1 Precursor");
            fuggiasco.Admin("give Crystal 1 Epic");     // non e' davanti: non conta
            if (!AllIngresso(ps.Take(10).ToArray()))
            { Console.WriteLine("  FALLITA: le sonde non sono arrivate all'ingresso"); return false; }
            capo.Attendi(() => false, 2.5);
            Entra(capo);
            Console.WriteLine($"  dieci davanti, un solo cristallo alto: dentro={Dentro(capo)}");
            if (Dentro(capo)) { Console.WriteLine("  FALLITA: si apre con un solo cristallo alto"); ok = false; }

            // --- arriva il secondo cristallo: si apre, e si entra
            epico.Admin("give Crystal 1 Epic");
            AllIngresso(ps);
            capo.Attendi(() => false, 2.5);
            foreach (var p in ps) Entra(p);
            Aggiorna(ps);
            int dentro = ps.Count(Dentro);
            Console.WriteLine($"  con il secondo cristallo: dentro {dentro}/11" + (dentro == 11 ? "" :
                " (fuori: " + string.Join(", ", ps.Select((p, i) => (p, i)).Where(t => !Dentro(t.p))
                    .Select(t => $"{t.i} a {Dist(t.p.X, t.p.Z, CentroXZ, CentroXZ):F1} m")) + ")"));
            if (dentro != 11) { Console.WriteLine("  FALLITA: l'ingresso non si e' aperto per tutti"); ok = false; }
            Console.WriteLine($"  aperto, visto dal client: {DescriviSantuario(capo.UltimoSantuario)}");
            if (capo.UltimoSantuario is not { Gate: SanctuaryGate.Open, OpenSecondsLeft: >= 50 and <= 60 })
            { Console.WriteLine("  FALLITA: il client non sa che l'ingresso e' aperto, o per quanto"); ok = false; }

            // --- offerte; il fuggiasco offre ed esce
            capo.Rituale(CentroDelMondo, CrystalType.Precursor);
            epico.Rituale(CentroDelMondo, CrystalType.Epic);
            fuggiasco.Rituale(CentroDelMondo, CrystalType.Epic);
            bool offerte = capo.Attendi(() => capo.UltimoRituale?.Accepted == true, 4)
                         & epico.Attendi(() => epico.UltimoRituale?.Accepted == true, 4)
                         & fuggiasco.Attendi(() => fuggiasco.UltimoRituale?.Accepted == true, 4);
            if (!offerte) { Console.WriteLine("  FALLITA: il centro del mondo non ha accettato le offerte"); ok = false; }
            fuggiasco.Admin("tp 30 30");
            fuggiasco.Attendi(() => Dist(fuggiasco.X, fuggiasco.Z, 30, 30) < 2f, 6);

            capo.Admin("santuario chiudi");
            capo.Attendi(() => ChunkPiuArido(capo) > 100, 6);
            foreach (var p in ps) p.Attendi(() => false, 0.3);
            int arido = ChunkPiuArido(capo);
            Console.WriteLine($"  sotto il 25%, 2 fazioni, 1 Precursore, 1 Epico: chunk piu' arido {arido}/255 (atteso ~159), " +
                              $"Precursore del capo {capo.Cristalli(CrystalType.Precursor)}, Epico offerto {epico.Cristalli(CrystalType.Epic)}, " +
                              $"Epico di chi e' uscito {fuggiasco.Cristalli(CrystalType.Epic)}");
            if (Math.Abs(arido - 159) > 2) { Console.WriteLine("  FALLITA: la terra non e' al 62,5%"); ok = false; }
            if (capo.Cristalli(CrystalType.Precursor) != 0 || epico.Cristalli(CrystalType.Epic) != 0)
            { Console.WriteLine("  FALLITA: i cristalli offerti non sono stati impiegati"); ok = false; }
            if (fuggiasco.Cristalli(CrystalType.Epic) != 1)
            { Console.WriteLine("  FALLITA: chi e' uscito prima ha perso il cristallo"); ok = false; }

            // --- a riposo, e lo sa solo chi e' sull'isola centrale. Lontano
            // dall'isola, senza carestia, non si sa nemmeno dove sta; con la
            // carestia si sa dove sta, ma non com'e' l'ingresso; finita la
            // carestia, il client lo dimentica (Tommaso, revisione di A1a3c2c).
            bool riposoVisto = capo.Attendi(() => capo.UltimoSantuario?.Gate == SanctuaryGate.Resting, 4);
            fuggiasco.Admin("tp 300 -420");
            bool ignoto = fuggiasco.Attendi(() => fuggiasco.UltimoSantuario is
                { Known: false, Gate: SanctuaryGate.Unknown, CenterX: 0f, CenterZ: 0f, EntranceX: 0f, EntranceZ: 0f, CenterId: 0 }, 6);
            Console.WriteLine($"  dopo il rituale, visto da dentro: {DescriviSantuario(capo.UltimoSantuario)}; " +
                              $"da fuori dall'isola: {DescriviSantuario(fuggiasco.UltimoSantuario)}");
            if (!riposoVisto) { Console.WriteLine("  FALLITA: il client non sa che il santuario riposa"); ok = false; }
            if (!ignoto) { Console.WriteLine("  FALLITA: lontano dall'isola, senza carestia, il client sa qualcosa del santuario"); ok = false; }

            capo.Admin("setvitality 0.02");
            bool inCarestia = fuggiasco.Attendi(() => fuggiasco.UltimoSantuario is
                { Known: true, Gate: SanctuaryGate.Unknown, ShowOnMap: true } s &&
                Dist(s.CenterX, s.CenterZ, CentroXZ, CentroXZ) < 0.1f && s.CenterId == CentroDelMondo, 4);
            Console.WriteLine($"  lontano dall'isola, in carestia: {DescriviSantuario(fuggiasco.UltimoSantuario)}");
            if (!inCarestia) { Console.WriteLine("  FALLITA: in carestia chi e' lontano non sa dove sta il santuario"); ok = false; }
            capo.Admin("setvitality 0.625");
            bool dimenticato = fuggiasco.Attendi(() => fuggiasco.UltimoSantuario?.Known == false, 4);
            Console.WriteLine($"  lontano dall'isola, finita la carestia: {DescriviSantuario(fuggiasco.UltimoSantuario)}");
            if (!dimenticato) { Console.WriteLine("  FALLITA: finita la carestia, chi e' lontano sa ancora dove sta il santuario"); ok = false; }

            // --- riposo: con tutte le condizioni, resta chiuso
            foreach (var p in ps) p.Admin("tp 30 30");
            capo.Admin("give Crystal 1 Precursor");
            epico.Admin("give Crystal 1 Epic");
            AllIngresso(ps.Take(10).ToArray());
            capo.Attendi(() => false, 2.5);
            Entra(capo);
            Console.WriteLine($"  durante il riposo, con tutte le condizioni: dentro={Dentro(capo)}");
            if (Dentro(capo)) { Console.WriteLine("  FALLITA: il santuario si apre durante il riposo"); ok = false; }

            // --- sopra il 25%: i cristalli si perdono, la terra resta com'e'
            capo.Admin("santuario riposo");
            // Chiuso, o gia' aperto: le dieci sonde sono ancora davanti
            // all'ingresso coi cristalli, e finito il riposo si riapre subito.
            if (!capo.Attendi(() => capo.UltimoSantuario?.Gate is SanctuaryGate.Closed or SanctuaryGate.Open, 4))
            { Console.WriteLine($"  FALLITA: finito il riposo il client vede {DescriviSantuario(capo.UltimoSantuario)}"); ok = false; }
            capo.Admin("setvitality 1");
            capo.Attendi(() => ChunkPiuArido(capo) == 255, 6);
            foreach (var p in ps) p.Admin("tp 30 30");
            AllIngresso(ps.Take(10).ToArray());
            capo.Attendi(() => false, 2.5);
            foreach (var p in ps.Take(10)) Entra(p);
            capo.Rituale(CentroDelMondo, CrystalType.Precursor);
            epico.Rituale(CentroDelMondo, CrystalType.Epic);
            capo.Attendi(() => capo.UltimoRituale?.Accepted == true, 4);
            epico.Attendi(() => epico.UltimoRituale?.Accepted == true, 4);
            capo.Admin("santuario chiudi");
            capo.Attendi(() => capo.Cristalli(CrystalType.Precursor) == 0, 6);
            epico.Attendi(() => epico.Cristalli(CrystalType.Epic) == 0, 3);
            Console.WriteLine($"  sopra il 25%: Precursore {capo.Cristalli(CrystalType.Precursor)}, Epico {epico.Cristalli(CrystalType.Epic)}, " +
                              $"chunk piu' arido {ChunkPiuArido(capo)}/255");
            if (capo.Cristalli(CrystalType.Precursor) != 0 || epico.Cristalli(CrystalType.Epic) != 0)
            { Console.WriteLine("  FALLITA: sopra il 25% i cristalli non si sono consumati"); ok = false; }
            if (ChunkPiuArido(capo) != 255)
            { Console.WriteLine("  FALLITA: sopra il 25% la terra e' cambiata"); ok = false; }

            // --- l'orda (Tommaso, A1a3c2b): dieci dentro fanno 50 lupi (il
            // minimo), piu' un mini boss per il Precursore e uno per l'Epico.
            // Prima uccide chi e' dentro, poi si sparge; finche' vive il
            // santuario riposa; i mini boss lasciano cadere i loro cristalli.
            var orda = capo.StatoSantuario();
            Console.WriteLine($"  l'orda appena sprigionata: {Sonda.Descrivi(orda)}");
            if (orda.GetValueOrDefault("orda") != "50" || orda.GetValueOrDefault("capi") != "2"
                || orda.GetValueOrDefault("in attesa") != "2")
            { Console.WriteLine("  FALLITA: l'orda non ha 50 lupi e 2 mini boss"); ok = false; }

            var finoA = DateTime.UtcNow.AddSeconds(30);
            while (orda.GetValueOrDefault("sparsa") != "1" && DateTime.UtcNow < finoA)
            {
                capo.Attendi(() => false, 1.0);
                orda = capo.StatoSantuario();
            }
            Console.WriteLine($"  dopo: {Sonda.Descrivi(orda)}");
            if (orda.GetValueOrDefault("sparsa") != "1")
            { Console.WriteLine("  FALLITA: in 30 secondi l'orda non ha svuotato il santuario"); ok = false; }

            capo.Admin("santuario riposo");
            capo.Attendi(() => false, 1.5);
            orda = capo.StatoSantuario();
            Console.WriteLine($"  riposo finito, orda viva: stato {orda.GetValueOrDefault("stato")}");
            if (orda.GetValueOrDefault("stato") != "Resting")
            { Console.WriteLine("  FALLITA: con l'orda viva il santuario non riposa"); ok = false; }

            int aTerraPrima = int.TryParse(orda.GetValueOrDefault("cristalli alti a terra"), out int atp) ? atp : -1;
            orda = capo.StatoSantuario("orda uccidi");
            Console.WriteLine($"  orda uccisa: {Sonda.Descrivi(orda)}");
            if (orda.GetValueOrDefault("orda") != "0" || orda.GetValueOrDefault("capi") != "0"
                || orda.GetValueOrDefault("in attesa") != "0")
            { Console.WriteLine("  FALLITA: uccisa l'orda, qualcosa resta vivo o in attesa"); ok = false; }
            if (orda.GetValueOrDefault("cristalli alti a terra") != (aTerraPrima + 2).ToString())
            { Console.WriteLine("  FALLITA: i mini boss non hanno lasciato cadere i due cristalli"); ok = false; }

            capo.Admin("santuario riposo");
            capo.Attendi(() => false, 1.5);
            orda = capo.StatoSantuario();
            if (orda.GetValueOrDefault("stato") != "Closed")
            { Console.WriteLine($"  FALLITA: morta l'orda e finito il riposo, il santuario e' {orda.GetValueOrDefault("stato")}"); ok = false; }
            // I cristalli dei mini boss restano a terra, e uno morto ancora dentro
            // il santuario lascia il suo davanti all'ingresso: le sonde lo
            // raccoglierebbero nel passo dopo, che conta i cristalli di ognuna.
            capo.Admin("contacristalli azzera");
            // Chi e' morto rinasce in tre secondi: le prove dopo lo vogliono vivo.
            capo.Attendi(() => false, 4.0);

            // --- rituale incompleto (Tommaso, A1a3c2a): si apre in dieci, ne
            // entrano nove, e una fazione non ha piu' i suoi cinque. Senza
            // offerte non succede niente e il santuario non riposa; con le
            // offerte i cristalli si perdono, la terra non cambia, e riposa.
            bool ApriInNove()
            {
                foreach (var p in ps) p.Admin("tp 30 30");
                AllIngresso(ps.Take(10).ToArray());
                capo.Attendi(() => false, 2.5);
                foreach (var p in ps.Take(9)) Entra(p);
                Aggiorna(ps);
                return ps.Take(9).All(Dentro) && !Dentro(ps[9]);
            }
            capo.Admin("santuario riposo");
            capo.Admin("give Crystal 1 Precursor");
            epico.Admin("give Crystal 1 Epic");
            bool nove = ApriInNove();
            capo.Admin("santuario chiudi");
            capo.Attendi(() => false, 1.0);
            Console.WriteLine($"  in nove, nessuna offerta: entrati={nove}, Precursore {capo.Cristalli(CrystalType.Precursor)}, " +
                              $"Epico {epico.Cristalli(CrystalType.Epic)}");
            if (!nove) { Console.WriteLine("  FALLITA: non si e' aperto per i nove"); ok = false; }
            if (capo.Cristalli(CrystalType.Precursor) != 1 || epico.Cristalli(CrystalType.Epic) != 1)
            { Console.WriteLine("  FALLITA: senza offerte qualcuno ha perso un cristallo"); ok = false; }

            // Niente riposo: si riapre subito, e stavolta si offre.
            bool noveAncora = ApriInNove();
            capo.Rituale(CentroDelMondo, CrystalType.Precursor);
            epico.Rituale(CentroDelMondo, CrystalType.Epic);
            capo.Attendi(() => capo.UltimoRituale?.Accepted == true, 4);
            epico.Attendi(() => epico.UltimoRituale?.Accepted == true, 4);
            capo.Admin("santuario chiudi");
            capo.Attendi(() => capo.Cristalli(CrystalType.Precursor) == 0, 6);
            epico.Attendi(() => epico.Cristalli(CrystalType.Epic) == 0, 3);
            capo.Attendi(() => capo.UltimoRituale?.Bane != null, 3);
            Console.WriteLine($"  in nove, con le offerte: riaperto subito={noveAncora}, Precursore {capo.Cristalli(CrystalType.Precursor)}, " +
                              $"Epico {epico.Cristalli(CrystalType.Epic)}, chunk piu' arido {ChunkPiuArido(capo)}/255, " +
                              $"risposta \"{capo.UltimoRituale?.Bane}\"");
            if (!noveAncora) { Console.WriteLine("  FALLITA: dopo un rituale mai partito il santuario riposa"); ok = false; }
            if (capo.Cristalli(CrystalType.Precursor) != 0 || epico.Cristalli(CrystalType.Epic) != 0)
            { Console.WriteLine("  FALLITA: nel rituale incompleto i cristalli offerti non si sono persi"); ok = false; }
            if (ChunkPiuArido(capo) != 255)
            { Console.WriteLine("  FALLITA: il rituale incompleto ha cambiato la terra"); ok = false; }
            if (capo.UltimoRituale?.Bane?.Contains("incompleto") != true)
            { Console.WriteLine("  FALLITA: il rituale incompleto non ha risposto"); ok = false; }

            // E ora riposa: con tutte le condizioni resta chiuso.
            capo.Admin("give Crystal 1 Precursor");
            epico.Admin("give Crystal 1 Epic");
            foreach (var p in ps) p.Admin("tp 30 30");
            AllIngresso(ps.Take(10).ToArray());
            capo.Attendi(() => false, 2.5);
            Entra(capo);
            Console.WriteLine($"  dopo il rituale incompleto, con tutte le condizioni: dentro={Dentro(capo)}");
            if (Dentro(capo)) { Console.WriteLine("  FALLITA: dopo il rituale incompleto il santuario non riposa"); ok = false; }

            // Si rimette tutto com'era, anche se qualcosa sopra e' fallito.
            capo.Admin("santuario riposo");
            capo.Admin("setvitality 1");
            foreach (var p in ps) { p.Admin("tp 30 30"); p.Admin("clearinv"); }
            capo.Attendi(() => false, 1.0);

            if (ok) Console.WriteLine("  OK: il santuario si apre solo alle condizioni, conta chi e' dentro, e ripara solo sotto il 25%");
            return ok;
        }
        finally
        {
            foreach (var p in ps) p.Dispose();
        }
    }

    // ---- 15c. l'era rigogliosa si consuma e raddoppia i drop -------------------
    //
    // Numeri di Tommaso (backlog A1a3b): l'eccedenza sopra il 100% se ne va da
    // sola in 14 giorni; finche dura, sull'isola centrale raddoppia tutto
    // (risorse, cristalli, animali e nascite) e fuori niente. Animali e nascite
    // qui non si provano: la sonda non sa cacciare ne' allevare. Le strutture
    // si' (A1a3b2): una trappola da pesce al centro e una fuori.
    //
    // Due settimane non si aspettano: il comando admin "consumaera" fa passare
    // i giorni come li farebbe passare il tick. Dal 125%, dopo 7 giorni l'era
    // c'e' ancora, dopo altri 7 e' finita. Il riavvio a meta' era lo prova la
    // persistenza, con sonda0003.

    static int CristalliVisti(Sonda s)
    {
        lock (s.Mondo) return s.Mondo.Values.Count(e => e.Type == EntityType.Crystal);
    }

    public static bool EraRigogliosa(string token, string host, int port)
    {
        Console.WriteLine("--- era rigogliosa: doppio sull'isola centrale, niente fuori, finisce da sola in 14 giorni");

        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }

        bool ok = true;
        s.Admin("setvitality 1");
        s.Admin("clearinv");

        // I cristalli nuovi nascono a ondate vicino al centro, su una griglia di
        // 2,5: ci si mette fra quattro punti della griglia, cosi' la sonda non ne
        // raccoglie nessuno passandoci sopra.
        s.Admin("tp 1.25 11.25");
        s.Attendi(() => Dist(s.X, s.Z, 1.25f, 11.25f) < 2f, 8);
        ScacciaBelve(s);

        // I cristalli si contano sul server, non a vista. Il server manda solo
        // le 32 entita' piu' vicine: con una ventina di cristalli gia' in vista
        // prima dell'era, "dieci in piu'" sfiorava quel tetto, e la prova
        // passava o falliva a seconda di cosa c'era attorno (backlog P0:
        // fallita anche su main). Il comando "contacristalli" risponde col
        // numero vero: 40 in equilibrio, 80 nell'era.
        //
        // Si parte da zero: la prova della riparazione ha gia' aperto un'era,
        // e i cristalli in piu' restano a terra anche dopo. In 20 secondi di
        // equilibrio le ondate da 5 tornano al tetto di 40 e si fermano.
        s.Admin("contacristalli azzera");
        s.Attendi(() => false, 20);
        int cristalliPrima = s.ContaCristalli();

        s.Admin("setvitality 1.25");
        if (!s.Attendi(() => s.HaEvento(WorldEventKind.BloomBegins), 6))
        { Console.WriteLine("  FALLITA: l'era rigogliosa non e' cominciata"); s.Admin("setvitality 1"); return false; }

        // Un'ondata doppia ogni 2 secondi finche' non ce ne sono 80: in 12
        // secondi ci si arriva.
        s.Attendi(() => false, 12);
        int cristalliDopo = s.ContaCristalli();
        Console.WriteLine($"  cristalli a terra: {cristalliPrima} in equilibrio, {cristalliDopo} dopo 12 secondi di era rigogliosa");
        if (cristalliPrima < 0 || cristalliDopo < 0)
        { Console.WriteLine("  FALLITA: il server non ha detto quanti cristalli ci sono"); ok = false; }
        else if (cristalliPrima < 40 || cristalliPrima > 44 || cristalliDopo < 80)
        { Console.WriteLine("  FALLITA: nell'era rigogliosa i cristalli a terra non sono raddoppiati"); ok = false; }

        // Un nodo di ferro e uno di pietra piantati d'ufficio, la' dove sta la
        // sonda: prima fuori dall'isola centrale, poi dentro.
        var piantati = new List<ulong>();
        (EntityState Ferro, EntityState Pietra) Pianta(float nx, float nz)
        {
            s.Admin($"tp {nx.ToString(Inv)} {nz.ToString(Inv)}");
            s.Attendi(() => Dist(s.X, s.Z, nx, nz) < 2f, 8);
            s.Admin($"build IronNode {nx.ToString(Inv)} {(nz + 1f).ToString(Inv)}");
            s.Admin($"build StoneNode {(nx + 1f).ToString(Inv)} {nz.ToString(Inv)}");
            EntityState f = null, p = null;
            s.Attendi(() => (f = s.PiuVicina(EntityType.IronNode, nx, nz + 1f)) != null &&
                            Dist(f.X, f.Z, nx, nz + 1f) < 1f &&
                            (p = s.PiuVicina(EntityType.StoneNode, nx + 1f, nz)) != null &&
                            Dist(p.X, p.Z, nx + 1f, nz) < 1f, 12);
            if (f != null && Dist(f.X, f.Z, nx, nz + 1f) < 1f) piantati.Add(f.EntityId);
            if (p != null && Dist(p.X, p.Z, nx + 1f, nz) < 1f) piantati.Add(p.EntityId);
            return (f, p);
        }

        void Ripulisci()
        {
            s.Admin("setvitality 1");
            foreach (var id in piantati) s.Admin($"remove {id}");
            s.Admin("clearinv");
            s.Attendi(() => false, 1);
        }

        // Un colpo: quanto e' entrato nello zaino. Mezzo secondo dopo, per il
        // tempo di ricarica della raccolta.
        int Colpo(EntityState nodo, ItemType cosa)
        {
            int prima = s.Quanti(cosa);
            s.Raccogli(nodo.EntityId);
            s.Attendi(() => s.Quanti(cosa) > prima, 4);
            s.Attendi(() => false, 0.7);
            return s.Quanti(cosa) - prima;
        }

        // Una trappola da pesce piantata due metri a ovest della sonda, fatta
        // rendere una volta col comando "produci": quanti pesci ci sono dentro.
        // Decisione di Tommaso (A1a3b2): al centro anche le strutture rendono
        // il doppio, fuori no.
        int Trappola(float nx, float nz)
        {
            float tx = nx - 2f;
            EntityState t = null;
            // Due tentativi: una volta su cinque, su main, la trappola fuori
            // dall'isola non e' mai comparsa alla sonda pur essendo stata
            // piantata (backlog P0). Causa non trovata: si riprova e, se manca
            // ancora, si scrive dove stava la sonda.
            for (int tentativo = 1; tentativo <= 2 && t == null; tentativo++)
            {
                s.Admin($"tp {nx.ToString(Inv)} {nz.ToString(Inv)}");
                if (!s.Attendi(() => Dist(s.X, s.Z, nx, nz) < 2f, 8))
                    Console.WriteLine($"  (trappola, tentativo {tentativo}: la sonda e' a ({s.X:F1}, {s.Z:F1}), non a ({nx}, {nz}))");
                s.Admin($"build FishTrap {tx.ToString(Inv)} {nz.ToString(Inv)}");
                if (!s.Attendi(() => (t = s.PiuVicina(EntityType.FishTrap, tx, nz)) != null &&
                                     Dist(t.X, t.Z, tx, nz) < 1f, 12))
                {
                    int viste; lock (s.Mondo) viste = s.Mondo.Count;
                    Console.WriteLine($"  (trappola, tentativo {tentativo}: non vista; sonda a ({s.X:F1}, {s.Z:F1}), {viste} entita' in vista)");
                    t = null;
                }
            }
            if (t == null) return -1;
            piantati.Add(t.EntityId);
            s.Admin("produci");
            s.Attendi(() => { var x = s.PiuVicina(EntityType.FishTrap, tx, nz); return x != null && x.Extra1 > 0; }, 6);
            s.Attendi(() => false, 1);
            var dopo = s.PiuVicina(EntityType.FishTrap, tx, nz);
            return dopo != null ? (int)dopo.Extra1 : -1;
        }

        // Fuori: in mare aperto a sud dell'isola centrale, lontano dagli altri
        // scenari. L'era rigogliosa qui non cambia niente.
        int pesciFuori = Trappola(-90f, -340f);
        int pesciCentro = Trappola(60f, -60f);
        Console.WriteLine($"  trappola da pesce: {pesciFuori} pesci fuori dall'isola centrale, {pesciCentro} sull'isola centrale");
        if (pesciFuori != 1)  { Console.WriteLine("  FALLITA: fuori dall'isola centrale la trappola non rende uno"); ok = false; }
        if (pesciCentro != 2) { Console.WriteLine("  FALLITA: sull'isola centrale la trappola non rende il doppio"); ok = false; }

        var (ferroFuori, pietraFuori) = Pianta(-90f, -340f);
        if (ferroFuori == null || pietraFuori == null)
        { Console.WriteLine("  FALLITA: i nodi fuori dall'isola centrale non sono comparsi"); Ripulisci(); return false; }
        int ferroF = Colpo(ferroFuori, ItemType.Iron);
        int pietraF = Colpo(pietraFuori, ItemType.Stone);
        Console.WriteLine($"  fuori dall'isola centrale: un colpo da' {ferroF} ferro e {pietraF} pietra");
        if (ferroF != 1 || pietraF != 1)
        { Console.WriteLine("  FALLITA: fuori dall'isola centrale l'era rigogliosa ha cambiato i drop"); ok = false; }

        // Dentro: sull'isola centrale (chunk 17, 14), lontano dalla griglia dei
        // cristalli. Qui raddoppia tutto, la pietra come il ferro.
        var (ferro, pietra) = Pianta(60f, -60f);
        if (ferro == null || pietra == null)
        { Console.WriteLine("  FALLITA: i nodi sull'isola centrale non sono comparsi"); Ripulisci(); return false; }
        int ferroEra = Colpo(ferro, ItemType.Iron);
        int pietraEra = Colpo(pietra, ItemType.Stone);
        Console.WriteLine($"  sull'isola centrale: un colpo da' {ferroEra} ferro e {pietraEra} pietra");
        if (ferroEra != 2)  { Console.WriteLine("  FALLITA: sull'isola centrale il ferro non raddoppia"); ok = false; }
        if (pietraEra != 2) { Console.WriteLine("  FALLITA: sull'isola centrale la pietra non raddoppia"); ok = false; }

        // Sette giorni: dal 125% al 112,5%, l'era c'e' ancora.
        s.Admin("consumaera 7");
        s.Attendi(() => false, 3);
        bool finitaPresto = s.HaEvento(WorldEventKind.BloomEnds);
        int ferroMeta = Colpo(ferro, ItemType.Iron);
        Console.WriteLine($"  dopo 7 giorni: era finita={finitaPresto}, un colpo da' {ferroMeta} ferro");
        if (finitaPresto)  { Console.WriteLine("  FALLITA: l'era e' finita a meta' delle due settimane"); ok = false; }
        if (ferroMeta != 2) { Console.WriteLine("  FALLITA: a meta' era il ferro non raddoppia piu'"); ok = false; }

        // Altri sette: l'eccedenza e' finita, e l'era con lei, senza nessun comando.
        s.Admin("consumaera 7");
        bool finita = s.Attendi(() => s.HaEvento(WorldEventKind.BloomEnds), 6);
        int ferroDopo = Colpo(ferro, ItemType.Iron);
        Console.WriteLine($"  dopo 14 giorni: era finita={finita}, un colpo da' {ferroDopo} ferro");
        if (!finita)        { Console.WriteLine("  FALLITA: dopo 14 giorni l'era rigogliosa non e' finita"); ok = false; }
        if (ferroDopo != 1) { Console.WriteLine("  FALLITA: finita l'era il ferro non torna a uno"); ok = false; }

        // Si rimette tutto com'era, anche se qualcosa sopra e' fallito.
        Ripulisci();

        if (ok) Console.WriteLine("  OK: l'era rigogliosa raddoppia cristalli, risorse e strutture sull'isola centrale, niente fuori, e finisce da sola dopo 14 giorni");
        return ok;
    }

    // ---- 16. registro di gioco -------------------------------------------------
    //
    // Deciso da Tommaso (backlog A1a2b): ogni giocatore ha lo storico degli stati
    // del mondo che ha vissuto, salvato sul server. Al primo ingresso lo stato
    // attuale; se il mondo cambia mentre e' via, al rientro UNA voce con lo stato
    // di adesso; le voci restano finche' non le cancella lui.
    //
    // Il giocatore del registro e' sonda0004, che nessun'altra prova usa: il
    // mondo di prova si azzera a ogni giro, quindi per il server e' sempre nuovo.
    // Il riavvio del server lo prova la persistenza, con sonda0003.

    public static bool RegistroDiGioco(string tokenAdmin, string tokenNuovo, string host, int port)
    {
        Console.WriteLine("--- registro di gioco: lo stato all'ingresso, i cambi, le cancellazioni");

        bool ok = true;
        string primo;
        using (var n = new Sonda(tokenNuovo, host, port))
        {
            if (!n.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda nuova non e' entrata"); return false; }
            n.Attendi(() => n.RegistriRicevuti > 0 && n.Eventi.Count > 0, 6);
            primo = n.RegistroInRiga();
            Console.WriteLine($"  primo ingresso: registro [{primo}], notifiche {n.Eventi.Count}");
            if (n.RegistriRicevuti == 0) { Console.WriteLine("  FALLITA: il registro non e' arrivato"); ok = false; }
            if (!(n.Eventi.Count == 1 && n.Eventi[0].Kind == WorldEventKind.NowCalm && n.Registro.Count == 1))
            { Console.WriteLine("  FALLITA: al primo ingresso doveva arrivare una notifica sola, con lo stato attuale"); ok = false; }
        }
        Thread.Sleep(2500);   // il registro si mette da parte all'uscita

        // Il mondo cambia mentre e' via: due volte. Al rientro deve sapere solo
        // com'e' adesso.
        using var s = new Sonda(tokenAdmin, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda amministratrice non e' entrata"); return false; }
        s.Admin("setvitality 0.02");
        bool carestia = s.Attendi(() => s.UltimaPartita != null && s.UltimaPartita.InFamine, 6);

        string secondo;
        uint daCancellare = 0;
        using (var n = new Sonda(tokenNuovo, host, port))
        {
            if (!n.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda nuova non e' rientrata"); s.Admin("setvitality 1"); return false; }
            n.Attendi(() => n.RegistriRicevuti > 0 && n.Eventi.Count > 0, 6);
            n.Attendi(() => false, 1.0);   // nient'altro deve arrivare
            Console.WriteLine($"  rientro in carestia (carestia={carestia}): registro [{n.RegistroInRiga()}], " +
                              $"notifiche [{string.Join(",", n.Eventi.Select(e => e.Kind))}]");
            if (!(n.Eventi.Count == 1 && n.Eventi[0].Kind == WorldEventKind.NowInFamine))
            { Console.WriteLine("  FALLITA: al rientro doveva arrivare una notifica sola, 'il mondo e' in carestia'"); ok = false; }
            if (!n.RegistroInRiga().StartsWith(primo))
            { Console.WriteLine("  FALLITA: il registro di prima non e' tornato"); ok = false; }

            // Un cambio mentre e' collegato: voce nuova, come notifica.
            s.Admin("setvitality 1");
            bool fine = n.Attendi(() => { s.Poll(); return n.HaEvento(WorldEventKind.FamineEnds); }, 6);
            Console.WriteLine($"  fine della carestia da collegato: notifica={fine}, registro [{n.RegistroInRiga()}]");
            if (!fine) { Console.WriteLine("  FALLITA: la fine della carestia non e' arrivata"); ok = false; }

            // Si cancella la voce della carestia.
            lock (n.Registro)
                daCancellare = n.Registro.FirstOrDefault(v => v.Kind == WorldEventKind.NowInFamine)?.EntryId ?? 0;
            int prima = n.RegistriRicevuti;
            n.CancellaVoce(daCancellare);
            n.Attendi(() => n.RegistriRicevuti > prima, 6);
            secondo = n.RegistroInRiga();
            Console.WriteLine($"  cancellata la voce {daCancellare}: registro [{secondo}]");
            if (daCancellare == 0 || secondo.Contains($"{daCancellare}:"))
            { Console.WriteLine("  FALLITA: la voce non e' stata cancellata"); ok = false; }
        }
        Thread.Sleep(2500);

        // Rientro senza cambi: lo stesso registro, senza la voce cancellata, e
        // nessuna notifica.
        using (var n = new Sonda(tokenNuovo, host, port))
        {
            if (!n.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda nuova non e' rientrata"); s.Admin("setvitality 1"); return false; }
            n.Attendi(() => n.RegistriRicevuti > 0, 6);
            n.Attendi(() => false, 1.0);
            string terzo = n.RegistroInRiga();
            Console.WriteLine($"  rientro senza cambi: registro [{terzo}], notifiche {n.Eventi.Count}");
            if (terzo != secondo) { Console.WriteLine("  FALLITA: il registro non e' tornato uguale (la voce cancellata e' tornata?)"); ok = false; }
            if (n.Eventi.Count != 0) { Console.WriteLine("  FALLITA: senza cambi non doveva arrivare nessuna notifica"); ok = false; }
        }

        if (ok) Console.WriteLine("  OK: stato al primo ingresso, una voce sola al rientro, cambi notificati, cancellazioni ricordate");
        return ok;
    }

    // ---- 17. persistenza del giocatore ----------------------------------------
    //
    // Qui i difetti si sono gia' visti in partita, piu' volte: si costruiva e la
    // costruzione restava, ma l'inventario spariva. E' anche il caso peggiore da
    // verificare a mano, perche' richiede di uscire, rientrare e ricordarsi cosa
    // si aveva — cioe' proprio quello che una macchina fa senza sbagliare.

    public static readonly string Lasciato =
        Path.Combine(Path.GetTempPath(), "precursori-lasciato.txt");

    /// <summary>
    /// Prepara: si da' della roba, si sposta, e annota cosa aveva addosso quando
    /// se n'e' andato.
    ///
    /// Annota invece di confrontare con numeri scritti qui: cosa ci sia nello
    /// zaino dipende anche da quello che il personaggio aveva gia', e una prova
    /// che si aspetta "sette legni" fallisce o passa a seconda di cosa e'
    /// successo prima. La domanda vera non e' "ha sette legni" ma "ha ancora
    /// quello che aveva", e quella si risponde solo ricordando.
    /// </summary>
    // ---- l'orda supera il riavvio ------------------------------------------------
    //
    // Tommaso (A1a3c2b): l'orda resta finche' non muore tutta, anche dopo un
    // riavvio, e un Precursore non deve sparire dal mondo con lei. Prima di
    // spegnere si sprigiona un'orda d'ufficio (50 lupi e un mini boss Epico);
    // dopo, deve esserci ancora, sparsa, col suo mini boss. La sonda sta lontana
    // dall'isola centrale: l'orda non deve poterla raggiungere.

    public static bool OrdaPrepara(string token, string host, int port)
    {
        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda dell'orda non e' entrata in gioco"); return false; }
        var stato = s.StatoSantuario("orda 1 1 0");
        Console.WriteLine($"  orda prima del riavvio: {Sonda.Descrivi(stato)}");
        if (stato.GetValueOrDefault("orda") != "50" || stato.GetValueOrDefault("capi") != "1")
        { Console.WriteLine("  FALLITA: l'orda d'ufficio non e' nata"); return false; }

        // Gli Echi di un Precursore, lontano da tutti: non si dissolvono mai,
        // quindi dopo il riavvio devono esserci ancora, Araldo e cristallo
        // compresi.
        s.StatoEchi("uccidi");
        var echi = s.StatoEchi("evoca Precursor -400 400");
        Console.WriteLine($"  echi prima del riavvio: {Sonda.Descrivi(echi)}");
        if (Sonda.Numero(echi, "eco") != 6 || Sonda.Numero(echi, "vaganti") != 2
            || Sonda.Numero(echi, "araldi") != 1 || Sonda.Numero(echi, "con cristallo") != 1)
        { Console.WriteLine("  FALLITA: gli Echi d'ufficio non sono nati"); return false; }
        return true;
    }

    public static bool OrdaVerifica(string token, string host, int port)
    {
        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda dell'orda non e' entrata in gioco"); return false; }
        s.Attendi(() => false, 1.5);   // il tick ricrea l'orda una volta al secondo
        var stato = s.StatoSantuario();
        Console.WriteLine($"  orda dopo il riavvio: {Sonda.Descrivi(stato)}");
        bool ok = stato.GetValueOrDefault("orda") == "50" && stato.GetValueOrDefault("capi") == "1"
                  && stato.GetValueOrDefault("sparsa") == "1" && stato.GetValueOrDefault("in attesa") == "1"
                  && stato.GetValueOrDefault("stato") == "Resting";
        if (!ok) Console.WriteLine("  FALLITA: l'orda non e' sopravvissuta al riavvio com'era");
        // Si toglie comunque: il mondo di prova riparte pulito al prossimo giro,
        // ma chi lo guarda dopo non deve trovarci 50 lupi.
        stato = s.StatoSantuario("orda uccidi");
        if (stato.GetValueOrDefault("in attesa") != "0")
        { Console.WriteLine($"  FALLITA: uccisa l'orda, resta {Sonda.Descrivi(stato)}"); ok = false; }

        var echi = s.StatoEchi();
        Console.WriteLine($"  echi dopo il riavvio: {Sonda.Descrivi(echi)}");
        if (Sonda.Numero(echi, "eco") != 6 || Sonda.Numero(echi, "vaganti") != 2
            || Sonda.Numero(echi, "araldi") != 1 || Sonda.Numero(echi, "con cristallo") != 1)
        { Console.WriteLine("  FALLITA: gli Echi non sono sopravvissuti al riavvio com'erano"); ok = false; }
        int altiPrima = Sonda.Numero(echi, "cristalli alti a terra");
        echi = s.StatoEchi("uccidi");
        if (Sonda.Numero(echi, "cristalli alti a terra") != altiPrima + 1)
        { Console.WriteLine($"  FALLITA: ucciso l'Araldo, il Precursore non e' caduto: {Sonda.Descrivi(echi)}"); ok = false; }
        return ok;
    }

    public static bool PersistenzaPrepara(string token, string host, int port)
    {
        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }

        const float PX = 250f, PZ = -250f;

        // Si parte da zero. Senza, ogni giro eredita lo zaino del precedente e
        // le quantita' si accumulano: la prova diventava "passa o fallisce a
        // seconda di quante volte l'hai lanciata", che e' il difetto che avevamo
        // gia' tolto dal mondo e ci era rientrato dall'inventario.
        s.Admin("clearinv");
        if (!s.Attendi(() => s.Quanti(ItemType.Wood) == 0 &&
                             s.Quanti(ItemType.Stone) == 0 &&
                             !s.Istanze.Values.Any(i => i.Tipo == ItemType.Axe), 10))
        {
            Console.WriteLine("  FALLITA: non sono riuscito a svuotare lo zaino");
            return false;
        }

        s.Admin("setfaction 2");
        s.Admin("give Wood 7");
        s.Admin("give Stone 5");
        s.Admin("give Axe 1");
        s.Admin($"tp {PX.ToString(Inv)} {PZ.ToString(Inv)}");

        // Quantita' esatte: partendo da zero non c'e' ambiguita', e attendere
        // l'uguaglianza invece di un minimo esclude la corsa fra il momento in
        // cui si guarda e quello in cui arriva l'ultimo pezzo.
        bool pronto = s.Attendi(() =>
            s.Quanti(ItemType.Wood) == 7 &&
            s.Quanti(ItemType.Stone) == 5 &&
            s.Istanze.Values.Count(i => i.Tipo == ItemType.Axe) == 1 &&
            Dist(s.X, s.Z, PX, PZ) < 2f, 15);

        if (!pronto)
        {
            Console.WriteLine($"  FALLITA in preparazione: legno={s.Quanti(ItemType.Wood)} " +
                              $"pietra={s.Quanti(ItemType.Stone)} asce={s.Istanze.Values.Count(i => i.Tipo == ItemType.Axe)} " +
                              $"posizione=({s.X:F1},{s.Z:F1})");
            return false;
        }

        var asce = s.Istanze.Where(i => i.Value.Tipo == ItemType.Axe)
                            .OrderBy(i => i.Key).ToArray();
        var righe = new[]
        {
            s.Quanti(ItemType.Wood).ToString(Inv),
            s.Quanti(ItemType.Stone).ToString(Inv),
            string.Join(",", asce.Select(a => $"{a.Key}:{a.Value.Usura}")),
            s.X.ToString(Inv), s.Z.ToString(Inv),
        };
        // Il registro di gioco: una carestia d'ufficio, avanti e indietro, per
        // avere voci da ricordare, e se ne cancella una. Al rientro deve tornare
        // tutto tranne quella, e nessuna notifica: il mondo e' com'era.
        s.Admin("setvitality 0.02");
        s.Attendi(() => s.HaEvento(WorldEventKind.FamineBegins), 6);
        s.Admin("setvitality 1");
        s.Attendi(() => s.HaEvento(WorldEventKind.FamineEnds), 6);
        uint via;
        lock (s.Registro)
            via = s.Registro.LastOrDefault(v => v.Kind == WorldEventKind.FamineBegins)?.EntryId ?? 0;
        int ricevuti = s.RegistriRicevuti;
        s.CancellaVoce(via);
        s.Attendi(() => s.RegistriRicevuti > ricevuti, 6);

        // L'era rigogliosa a meta': 125% e sette giorni consumati, cioe' 112,5%.
        // Dopo il riavvio deve esserci ancora, ne' ripartita da capo ne' persa:
        // la verifica lo controlla facendola finire.
        s.Admin("setvitality 1.25");
        if (!s.Attendi(() => s.HaEvento(WorldEventKind.BloomBegins), 6))
        {
            Console.WriteLine("  FALLITA in preparazione: l'era rigogliosa non e' cominciata");
            return false;
        }
        s.Admin("consumaera 7");
        s.Attendi(() => false, 1.5);
        string registro = s.RegistroInRiga();
        if (via == 0 || registro.Contains($"{via}:"))
        {
            Console.WriteLine($"  FALLITA in preparazione: registro [{registro}], voce da cancellare {via}");
            return false;
        }

        // Il rischio dei rituali accumulato non deve sparire con un riavvio.
        int rituali = s.RitualiFatti(7);
        if (rituali != 7)
        {
            Console.WriteLine($"  FALLITA in preparazione: il contatore dei rituali non si fissa (risposta {rituali})");
            return false;
        }

        File.WriteAllLines(Lasciato, righe.Append(registro).Append(rituali.ToString(Inv)));

        Console.WriteLine($"  lasciato: legno {righe[0]}, pietra {righe[1]}, " +
                          $"asce [{righe[2]}], in ({s.X:F0}, {s.Z:F0}), registro [{registro}]");
        return true;
    }

    /// <summary>Verifica: rientra e confronta con quello che aveva lasciato.</summary>
    public static bool PersistenzaVerifica(string token, string host, int port, string quando)
    {
        if (!File.Exists(Lasciato))
        {
            Console.WriteLine("  FALLITA: non c'e' traccia di cosa avesse lasciato");
            return false;
        }
        var atteso = File.ReadAllLines(Lasciato);
        int legnoAtt  = int.Parse(atteso[0], Inv);
        int pietraAtt = int.Parse(atteso[1], Inv);
        string asceAtt = atteso[2];
        float xAtt = float.Parse(atteso[3], Inv), zAtt = float.Parse(atteso[4], Inv);
        string registroAtt = atteso.Length > 5 ? atteso[5] : "";
        int ritualiAtt = atteso.Length > 6 ? int.Parse(atteso[6], Inv) : -1;

        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' rientrata in gioco"); return false; }

        // L'inventario completo arriva subito dopo l'handshake; si concede tempo
        // perche' arrivi tutto invece di guardare troppo presto.
        s.Attendi(() => s.Quanti(ItemType.Wood) >= legnoAtt && s.Istanze.Count > 0, 10);

        int legno = s.Quanti(ItemType.Wood), pietra = s.Quanti(ItemType.Stone);
        string asce = string.Join(",", s.Istanze.Where(i => i.Value.Tipo == ItemType.Axe)
                                                .OrderBy(i => i.Key)
                                                .Select(a => $"{a.Key}:{a.Value.Usura}"));
        float scarto = Dist(s.X, s.Z, xAtt, zAtt);

        Console.WriteLine($"  {quando}: legno {legno} (era {legnoAtt}), pietra {pietra} (era {pietraAtt}), " +
                          $"asce [{asce}] (erano [{asceAtt}]), a {scarto:F1} da dove aveva lasciato");

        bool ok = true;
        if (legno != legnoAtt)   { Console.WriteLine("  FALLITA: il legno non e' tornato uguale"); ok = false; }
        if (pietra != pietraAtt) { Console.WriteLine("  FALLITA: la pietra non e' tornata uguale"); ok = false; }
        // Sugli esemplari si confrontano gli identificativi e l'usura: non basta
        // che ci sia "un'ascia", deve essere LA STESSA ascia. E' la differenza
        // fra un inventario che si ricrea e uno che si ricorda.
        if (asce != asceAtt)     { Console.WriteLine("  FALLITA: non sono tornati gli stessi esemplari"); ok = false; }
        if (scarto > 5f)         { Console.WriteLine("  FALLITA: non e' rientrato dove aveva lasciato"); ok = false; }

        // Il registro arriva subito dopo l'handshake; un secondo in piu' per
        // essere sicuri che non arrivi anche una notifica che non deve esserci.
        s.Attendi(() => s.RegistriRicevuti > 0, 6);
        s.Attendi(() => false, 1.0);
        string registro = s.RegistroInRiga();
        Console.WriteLine($"  {quando}: registro [{registro}] (era [{registroAtt}]), notifiche {s.Eventi.Count}");
        if (registro != registroAtt)
        { Console.WriteLine("  FALLITA: il registro di gioco non e' tornato uguale"); ok = false; }
        if (s.Eventi.Count != 0)
        { Console.WriteLine("  FALLITA: il mondo non e' cambiato, eppure e' arrivata una notifica"); ok = false; }

        int rituali = s.RitualiFatti();
        Console.WriteLine($"  {quando}: rituali fatti {rituali} (erano {ritualiAtt})");
        if (rituali != ritualiAtt)
        { Console.WriteLine("  FALLITA: il contatore dei rituali non e' tornato uguale"); ok = false; }

        // L'era lasciata al 112,5%: 6,8 giorni la portano a un soffio sopra il
        // 100% e non basta a chiuderla; altri 0,3 si'. Se si fosse persa, o
        // fosse ripartita dal 125%, non finirebbe esattamente qui.
        s.Admin("consumaera 6.8");
        s.Attendi(() => false, 3);
        bool finitaPresto = s.HaEvento(WorldEventKind.BloomEnds);
        s.Admin("consumaera 0.3");
        bool finita = s.Attendi(() => s.HaEvento(WorldEventKind.BloomEnds), 6);
        Console.WriteLine($"  {quando}: era rigogliosa finita dopo 6,8 giorni={finitaPresto}, dopo 7,1={finita}");
        if (finitaPresto || !finita)
        { Console.WriteLine("  FALLITA: l'era rigogliosa non e' ripresa da dove era rimasta"); ok = false; }
        s.Admin("setvitality 1");
        s.Attendi(() => false, 1);
        return ok;
    }

    /// <summary>Esce e rientra sullo stesso server.</summary>
    public static bool PersistenzaAlRientro(string token, string host, int port)
    {
        Console.WriteLine("--- persistenza: uscire e rientrare non svuota lo zaino");
        if (!PersistenzaPrepara(token, host, port)) return false;

        // Il salvataggio avviene alla disconnessione: si lascia il tempo di
        // completarla prima di ripresentarsi.
        Thread.Sleep(2500);
        return PersistenzaVerifica(token, host, port, "al rientro");
    }

    static readonly System.Globalization.CultureInfo Inv =
        System.Globalization.CultureInfo.InvariantCulture;
}
