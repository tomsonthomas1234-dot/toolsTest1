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

                    case NetMsgType.ChestContents:
                        UltimoBaule = MessagePack.MessagePackSerializer
                                          .Deserialize<ChestContentsMsg>(env.Payload);
                        break;

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
        Manda(NetMsgType.Input, new InputMsg { Seq = ++seq, MoveX = 0, MoveY = 0 },
              DeliveryMethod.Unreliable);
        Attendi(() => false, 0.4);
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
        if (nato && banchiRimasti == 0)
        { Console.WriteLine("  OK: il tavolo c'e' e i banchi sono stati consumati"); return true; }
        if (!nato)              Console.WriteLine("  FALLITA: la combinazione non ha prodotto niente");
        if (banchiRimasti > 0)  Console.WriteLine("  FALLITA: i banchi non sono stati consumati");
        return false;
    }

    // ---- 13. persistenza del giocatore ----------------------------------------
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
        File.WriteAllLines(Lasciato, righe);

        Console.WriteLine($"  lasciato: legno {righe[0]}, pietra {righe[1]}, " +
                          $"asce [{righe[2]}], in ({s.X:F0}, {s.Z:F0})");
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
