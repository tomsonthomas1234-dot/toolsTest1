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

    /// Le ultime statistiche mandate dal server, e l'armatura indossata
    /// (A14e1), con quanti ne sono arrivati.
    public volatile PlayerStatsMsg Statistiche;
    public int StatisticheRicevute;
    public volatile EquipmentStateMsg Indossati;
    public int IndossatiRicevuti;

    /// Indossare un esemplare dello zaino (l'esito si legge con Esito(113)).
    public void Indossa(ulong istanza) =>
        Comando(113, CommandType.Equip,
            MessagePack.MessagePackSerializer.Serialize(new EquipCommand { InstanceId = istanza }));

    /// Togliere il pezzo di uno slot (l'esito si legge con Esito(113)).
    public void Togli(EquipSlot slot) =>
        Comando(113, CommandType.Equip,
            MessagePack.MessagePackSerializer.Serialize(new EquipCommand { Slot = slot, InstanceId = 0 }));

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

    /// L'ultimo stato delle abilita mandato dal server (A14b), e quanti ne sono
    /// arrivati.
    public volatile AbilityStateMsg Abilita;
    public int AbilitaRicevute;

    /// Lo stato delle abilita in una riga secondo il server (comando admin
    /// "abilita", piu' eventuali argomenti); null se la risposta non arriva.
    public string AbilitaInRiga(string argomenti = "") =>
        ChiediAdmin(("abilita " + argomenti).Trim(), "abilita: ");

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

    /// Le armi e lo scudo che la sonda dice di portare (backlog B1: andare a
    /// terra consuma cio' che si impugna).
    public void Dotazione(ItemType arma1, ItemType arma2, SideItemType fianco) =>
        Manda(NetMsgType.Command, new CommandMsg
        {
            CommandId = 8,
            Type      = CommandType.ConfigureLoadout,
            Payload   = MessagePack.MessagePackSerializer.Serialize(new ConfigureLoadoutCommand
            {
                Weapon1 = arma1, Weapon2 = arma2, Side = fianco,
            }),
        });

    /// La posizione di combattimento (0 arma1, 1 arma1 e mano libera, 2 arma2,
    /// 3 arma2 e mano libera, 4 solo mano libera), per la regola dello scudo
    /// (A14d).
    /// L'esito si legge con Esito(112).
    public void Posizione(byte posizione) =>
        Comando(112, CommandType.SwitchStance,
            MessagePack.MessagePackSerializer.Serialize(new SwitchStanceCommand { Stance = posizione }));

    /// Lo stato di un giocatore visto da questa sonda: vita, a terra, morto.
    /// Vita -1 se non lo vede.
    public (float Vita, bool ATerra, bool Morto, float Secondi) Stato(ulong id)
    {
        lock (Mondo)
            return Mondo.TryGetValue(id, out var e)
                ? (e.Extra0, (e.Flags & PlayerFlags.Downed) != 0, (e.Flags & PlayerFlags.Dead) != 0, e.Extra2)
                : (-1f, false, false, 0f);
    }

    void MandaBaule(ChestTransferCommand cmd) =>
        Manda(NetMsgType.Command, new CommandMsg
        {
            CommandId = 6,
            Type      = CommandType.ChestTransfer,
            Payload   = MessagePack.MessagePackSerializer.Serialize(cmd),
        });

    /// L'ultima risposta ricevuta per ogni CommandId. Gli altri comandi della
    /// sonda usano numeri sotto 100: chi vuole leggere la risposta di un comando
    /// preciso usa un numero suo da 100 in su (A14a).
    public readonly Dictionary<uint, CommandResultMsg> Esiti = new();

    public void Comando(uint id, CommandType tipo, byte[] contenuto)
    {
        lock (Esiti) Esiti.Remove(id);
        Manda(NetMsgType.Command, new CommandMsg { CommandId = id, Type = tipo, Payload = contenuto });
    }

    public CommandResultMsg Esito(uint id)
    {
        lock (Esiti) return Esiti.TryGetValue(id, out var r) ? r : null;
    }

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

    /// Tutti i bit visti nei Flags del Custode da quando si e azzerato
    /// (backlog B7: le mosse, vedi KeeperState). Si legge e si azzera sotto
    /// lock (Mondo).
    public uint BitDelCustodeVisti;

    /// Quanto abbiamo di ciascun oggetto, secondo il server.
    public readonly Dictionary<(ItemType, byte), int> Zaino = new();

    /// Gli esemplari con identita' propria, per identificativo.
    public readonly Dictionary<ulong, (ItemType Tipo, int Usura)> Istanze = new();

    /// Il grado di ogni esemplare, per identificativo (A14c). Sotto lo stesso
    /// lucchetto dello zaino.
    public readonly Dictionary<ulong, byte> GradiIstanze = new();

    /// Il motivo del rifiuto all'ingresso, se il server ne ha dato uno.
    public ErrorCode? ErroreIngresso;

    /// Vero quando il server ci ha chiuso la porta (espulsione, bando).
    public bool Disconnessa;

    /// Scrive in chat, nel canale locale.
    public void Scrivi(string testo) =>
        Manda(NetMsgType.Command, new CommandMsg
        {
            CommandId = 11,
            Type      = CommandType.Chat,
            Payload   = MessagePack.MessagePackSerializer.Serialize(
                            new ChatCommand { Channel = ChatChannel.Local, Text = testo }),
        });

    /// Vero se fra i messaggi di chat ce n'e' uno che contiene il testo.
    public bool HaLetto(string pezzo)
    {
        lock (Chat) return Chat.Exists(t => t.Contains(pezzo));
    }

    public Sonda(string token, string host, int port)
    {
        var listener = new EventBasedNetListener();

        listener.PeerConnectedEvent += p =>
        {
            _peer = p;
            Manda(NetMsgType.HandshakeRequest,
                  new HandshakeRequest { ClientBuild = "regole", AuthToken = token });
        };

        listener.PeerDisconnectedEvent += (p, info) => Disconnessa = true;

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

                    case NetMsgType.HandshakeError:
                        ErroreIngresso = MessagePack.MessagePackSerializer
                                             .Deserialize<HandshakeError>(env.Payload).Code;
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

                    case NetMsgType.AbilityState:
                        Abilita = MessagePack.MessagePackSerializer.Deserialize<AbilityStateMsg>(env.Payload);
                        Interlocked.Increment(ref AbilitaRicevute);
                        break;

                    case NetMsgType.PlayerStats:
                        Statistiche = MessagePack.MessagePackSerializer.Deserialize<PlayerStatsMsg>(env.Payload);
                        Interlocked.Increment(ref StatisticheRicevute);
                        break;

                    case NetMsgType.EquipmentState:
                        Indossati = MessagePack.MessagePackSerializer.Deserialize<EquipmentStateMsg>(env.Payload);
                        Interlocked.Increment(ref IndossatiRicevuti);
                        break;

                    case NetMsgType.ConsumeResult:
                        UltimoConsumo = MessagePack.MessagePackSerializer
                                            .Deserialize<ConsumeResultMsg>(env.Payload);
                        break;

                    case NetMsgType.CommandResult:
                    {
                        var cr = MessagePack.MessagePackSerializer
                                     .Deserialize<CommandResultMsg>(env.Payload);
                        lock (Esiti) Esiti[cr.CommandId] = cr;
                        break;
                    }

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
                                if (it.Count == 0) { Istanze.Remove(it.InstanceId); GradiIstanze.Remove(it.InstanceId); }
                                else { Istanze[it.InstanceId] = (it.Type, it.Durability); GradiIstanze[it.InstanceId] = it.Level; }
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
                // I bit delle mosse durano un secondo o meno: tra un controllo
                // e l'altro si perderebbero, quindi si tengono qui (B7).
                if (e.Type == EntityType.Keeper) BitDelCustodeVisti |= e.Flags;
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

    /// <summary>
    /// Tiene premuto l'attacco per il tempo dato, ferma, come il client vero a
    /// 30 Hz. Il colpo da vicino prende il piu' vicino a portata: a mani nude
    /// un metro e mezzo. Serve a battere il Custode (backlog B4).
    /// </summary>
    public void Colpisci(double secondi)
    {
        uint seq = 1000;
        var fine = DateTime.UtcNow.AddSeconds(secondi);
        var prossimo = DateTime.UtcNow;
        while (DateTime.UtcNow < fine)
        {
            Poll();
            if (DateTime.UtcNow >= prossimo)
            {
                Manda(NetMsgType.Input, new InputMsg { Seq = ++seq, Buttons = 1u }, DeliveryMethod.Unreliable);
                prossimo = DateTime.UtcNow.AddMilliseconds(33);
            }
            Thread.Sleep(4);
        }
        var stop = DateTime.UtcNow.AddSeconds(0.3);
        while (DateTime.UtcNow < stop)
        {
            Manda(NetMsgType.Input, new InputMsg { Seq = ++seq }, DeliveryMethod.Unreliable);
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

    // ---- 6b. moderazione: silenzio a tempo e bando ----------------------------
    //
    // Il bersaglio e' un account a parte, fuori dagli amministratori: le prove
    // non devono bandire una sonda che serve anche ad altro, e un giro andato
    // storto non deve lasciarne una fuori dalla porta (si toglie tutto alla
    // fine, comunque vada).

    public const string Bersaglio = "sonda0016";

    /// Manda un comando e aspetta la risposta dell'amministrazione in chat.
    static bool Risposta(Sonda a, string comando, string attesa)
    {
        a.Admin(comando);
        return a.Attendi(() => a.HaLetto(attesa), 5);
    }

    /// L'amministratore va dove sta il bersaglio, entro il raggio della chat.
    static bool Avvicina(Sonda a, Sonda b)
    {
        a.Admin($"tp {b.X.ToString(Inv)} {b.Z.ToString(Inv)}");
        return a.Attendi(() => Dist(a.X, a.Z, b.X, b.Z) < 3f, 6);
    }

    /// Scrive una riga unica e dice se l'altro l'ha letta entro qualche secondo.
    static bool Arriva(Sonda da, Sonda a, string etichetta, double secondi = 3)
    {
        string riga = $"{etichetta}-{Guid.NewGuid():N}"[..24];
        da.Scrivi(riga);
        return a.Attendi(() => a.HaLetto(riga), secondi);
    }

    public static bool Silenzio(string tokenAdmin, string tokenBersaglio, string host, int port)
    {
        Console.WriteLine("--- moderazione: chi e' silenziato non arriva a nessuno, e poi torna a parlare");
        using var a = new Sonda(tokenAdmin, host, port);
        using var b = new Sonda(tokenBersaglio, host, port);
        if (!a.AttendiPronta() || !b.AttendiPronta()) { Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco"); return false; }
        try
        {
            if (!Avvicina(a, b)) { Console.WriteLine("  FALLITA: l'amministratore non e' arrivato vicino"); return false; }

            // Senza questo, un silenzio "riuscito" potrebbe essere solo una
            // chat che non arriva per altri motivi.
            if (!Arriva(b, a, "prima")) { Console.WriteLine("  FALLITA: la chat non arriva nemmeno senza silenzio"); return false; }
            Console.WriteLine("  senza silenzio la chat arriva");

            if (!Risposta(a, $"mute {Bersaglio} 5", $"silenziato: {Bersaglio}"))
            { Console.WriteLine("  FALLITA: nessuna conferma del silenzio"); return false; }
            bool avvisato = b.Attendi(() => b.HaLetto("Sei stato silenziato"), 3);

            bool passa = Arriva(b, a, "muto");
            bool saDiEssere = b.Attendi(() => b.HaLetto("Sei silenziato: ancora"), 3);
            Console.WriteLine($"  silenziato: avvisato {avvisato}, la chat arriva {passa}, glielo si ricorda {saDiEssere}");
            if (passa || !avvisato || !saDiEssere) { Console.WriteLine("  FALLITA: il silenzio non regge"); return false; }

            if (!Risposta(a, $"unmute {Bersaglio}", $"silenzio tolto: {Bersaglio}"))
            { Console.WriteLine("  FALLITA: nessuna conferma della fine del silenzio"); return false; }
            if (!Arriva(b, a, "dopo")) { Console.WriteLine("  FALLITA: tolto il silenzio, la chat non arriva"); return false; }
            Console.WriteLine("  OK: silenziato non scrive, tolto il silenzio torna a scrivere");
            return true;
        }
        finally { a.Admin($"unmute {Bersaglio}"); a.Attendi(() => false, 0.5); }
    }

    public static bool Bando(string tokenAdmin, Func<string> tokenBersaglio, string host, int port)
    {
        Console.WriteLine("--- moderazione: il bandito esce, non rientra e sa perche'");
        using var a = new Sonda(tokenAdmin, host, port);
        if (!a.AttendiPronta()) { Console.WriteLine("  FALLITA: l'amministratore non e' entrato in gioco"); return false; }
        try
        {
            using (var b = new Sonda(tokenBersaglio(), host, port))
            {
                if (!b.AttendiPronta()) { Console.WriteLine("  FALLITA: il bersaglio non e' entrato prima del bando"); return false; }
                if (!Risposta(a, $"ban {Bersaglio}", $"bandito: {Bersaglio}"))
                { Console.WriteLine("  FALLITA: nessuna conferma del bando"); return false; }
                bool fuori = b.Attendi(() => b.Disconnessa, 6);
                Console.WriteLine($"  bandito in gioco: messo fuori {fuori}, avvisato {b.HaLetto("bandito")}");
                if (!fuori) { Console.WriteLine("  FALLITA: il bandito e' rimasto in gioco"); return false; }
            }

            if (!BanditoRespinto(tokenBersaglio(), host, port)) return false;

            if (!Risposta(a, $"unban {Bersaglio}", $"bando tolto: {Bersaglio}"))
            { Console.WriteLine("  FALLITA: nessuna conferma della revoca"); return false; }
            using (var d = new Sonda(tokenBersaglio(), host, port))
                if (!d.AttendiPronta()) { Console.WriteLine("  FALLITA: tolto il bando, non rientra"); return false; }
            Console.WriteLine("  OK: bandito fuori e respinto con il motivo; tolto il bando rientra");
            return true;
        }
        finally { a.Admin($"unban {Bersaglio}"); a.Attendi(() => false, 0.5); }
    }

    /// Un bandito che prova a entrare: respinto, col codice del bando e non
    /// con "credenziali rifiutate".
    static bool BanditoRespinto(string token, string host, int port)
    {
        using var c = new Sonda(token, host, port);
        c.Attendi(() => c.ErroreIngresso != null || c.Pronta, 10);
        Console.WriteLine($"  il bandito riprova: entrato {c.Pronta}, motivo {c.ErroreIngresso?.ToString() ?? "nessuno"}");
        if (c.Pronta) { Console.WriteLine("  FALLITA: il bandito e' rientrato"); return false; }
        if (c.ErroreIngresso != ErrorCode.Banned) { Console.WriteLine("  FALLITA: respinto senza dire che e' bandito"); return false; }
        return true;
    }

    /// Prima del riavvio: il bersaglio, che non e' collegato, viene silenziato
    /// e bandito per nome. Dopo il riavvio i due provvedimenti devono esserci
    /// ancora.
    public static bool ModerazionePrepara(string tokenAdmin, string host, int port)
    {
        using var a = new Sonda(tokenAdmin, host, port);
        if (!a.AttendiPronta()) { Console.WriteLine("  FALLITA: l'amministratore non e' entrato in gioco"); return false; }
        bool ok = Risposta(a, $"mute {Bersaglio} 30", $"silenziato: {Bersaglio}")
                & Risposta(a, $"ban {Bersaglio}", $"bandito: {Bersaglio}");
        if (!ok) Console.WriteLine("  FALLITA: silenzio o bando di chi non e' collegato non confermato");
        else Console.WriteLine($"  {Bersaglio}, non collegato, silenziato e bandito per nome");
        return ok;
    }

    public static bool ModerazioneVerifica(string tokenAdmin, Func<string> tokenBersaglio, string host, int port)
    {
        using var a = new Sonda(tokenAdmin, host, port);
        if (!a.AttendiPronta()) { Console.WriteLine("  FALLITA: l'amministratore non e' entrato in gioco"); return false; }
        try
        {
            if (!BanditoRespinto(tokenBersaglio(), host, port))
            { Console.WriteLine("  FALLITA: il bando non ha superato il riavvio"); return false; }

            if (!Risposta(a, $"unban {Bersaglio}", $"bando tolto: {Bersaglio}"))
            { Console.WriteLine("  FALLITA: nessuna conferma della revoca"); return false; }
            using var b = new Sonda(tokenBersaglio(), host, port);
            if (!b.AttendiPronta()) { Console.WriteLine("  FALLITA: tolto il bando, non rientra"); return false; }
            if (!Avvicina(a, b)) { Console.WriteLine("  FALLITA: l'amministratore non e' arrivato vicino"); return false; }
            if (Arriva(b, a, "riavvio")) { Console.WriteLine("  FALLITA: il silenzio non ha superato il riavvio"); return false; }
            Console.WriteLine("  OK: bando e silenzio hanno superato il riavvio");
            return true;
        }
        finally
        {
            a.Admin($"unban {Bersaglio}");
            a.Admin($"unmute {Bersaglio}");
            a.Attendi(() => false, 0.5);
        }
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

    // ---- protocollo delle abilita (A14a) ---------------------------------------
    //
    // Solo il protocollo: i numeri delle abilita devono dire famiglia, classe e
    // grado giusti, nelle quantita decise (decisioni/abilita.md); i messaggi
    // nuovi devono fare andata e ritorno; il server deve riconoscere i comandi
    // nuovi e dire di no, non "riuscito" come per un tipo sconosciuto.

    public static bool ProtocolloAbilita(string token, string host, int port)
    {
        Console.WriteLine("--- protocollo delle abilita: numeri, messaggi, comandi rifiutati");
        bool ok = true;
        void Male(string m) { Console.WriteLine("  FALLITA: " + m); ok = false; }

        var tutte = Enum.GetValues<AbilityId>().Where(a => a != AbilityId.None).ToArray();
        Console.WriteLine($"  abilita nel protocollo: {tutte.Length}");
        if (tutte.Length != 175) Male($"ne servono 175 (4 armi da 13, 6 classi da 19, Mago 9), sono {tutte.Length}");

        // Quante per grado: attive base, Comuni, Rare, Epiche, Dono di Torak;
        // poi passive base, Comuni, Rare, Epiche.
        string Conta(IEnumerable<AbilityId> ab)
        {
            var att = ab.Where(a => !AbilityIds.IsPassive(a)).ToArray();
            var pas = ab.Where(a =>  AbilityIds.IsPassive(a)).ToArray();
            string g(AbilityId[] v, int n) => string.Join("/", Enumerable.Range(0, n)
                .Select(i => v.Count(a => (int)AbilityIds.GradeOf(a) == i)));
            return $"{g(att, 5)} + {g(pas, 4)}";
        }

        foreach (var f in new[] { WeaponFamily.Sword, WeaponFamily.Spear, WeaponFamily.Bow, WeaponFamily.Staff })
        {
            var ab = tutte.Where(a => AbilityIds.FamilyOf(a) == f).ToArray();
            string c = Conta(ab);
            Console.WriteLine($"  {f,-14} {c}");
            if (c != "4/3/3/2/1 + 0/0/0/0") Male($"{f}: atteso 4/3/3/2/1 + 0/0/0/0");
            if (ab.Any(a => AbilityIds.ClassOf(a) != ClassId.None)) Male($"{f}: un'abilita d'arma risulta di una classe");
        }
        foreach (var k in Enum.GetValues<ClassId>().Where(k => k != ClassId.None))
        {
            var ab = tutte.Where(a => AbilityIds.ClassOf(a) == k).ToArray();
            string c = Conta(ab);
            string atteso = k == ClassId.Mage ? "2/3/0/0/0 + 2/2/0/0" : "2/3/3/2/1 + 2/2/2/2";
            Console.WriteLine($"  {k,-14} {c}");
            if (c != atteso) Male($"{k}: atteso {atteso}");
            if (ab.Any(a => AbilityIds.FamilyOf(a) != WeaponFamily.None)) Male($"{k}: un'abilita di classe risulta d'arma");
        }
        if (tutte.Any(a => AbilityIds.FamilyOf(a) == WeaponFamily.None && AbilityIds.ClassOf(a) == ClassId.None))
            Male("c'e un'abilita che non e ne d'arma ne di classe");

        // Armature: nove pezzi, tre per tipo, testa busto gambe.
        var armi = Enum.GetValues<ItemType>().Where(AbilityIds.IsArmor).ToArray();
        string arm = string.Join(" ", armi.Select(t => $"{AbilityIds.ArmorKindOf(t)}.{AbilityIds.SlotOf(t)}"));
        Console.WriteLine($"  armature: {arm}");
        if (arm != "Leather.Head Leather.Body Leather.Legs Iron.Head Iron.Body Iron.Legs Bone.Head Bone.Body Bone.Legs")
            Male("tipi o slot delle armature sbagliati");
        if (AbilityIds.FamilyOf(ItemType.Staff) != WeaponFamily.Staff || AbilityIds.FamilyOf(ItemType.WoodJavelin) != WeaponFamily.None
            || AbilityIds.FamilyOf(ItemType.IronSword) != WeaponFamily.Sword || AbilityIds.IsArmor(ItemType.Shield))
            Male("famiglie delle armi sbagliate");

        // Andata e ritorno dei messaggi nuovi.
        var st = new AbilityStateMsg
        {
            Unlocked          = new[] { new AbilityProgress { Id = AbilityId.SwordLunge, Level = 2 } },
            WeaponSlots       = new[] { new WeaponSlotSet { Family = WeaponFamily.Sword,
                                         Abilities = new[] { AbilityId.SwordLunge, AbilityId.None, AbilityId.SwordParry } } },
            ClassActiveSlots  = new[] { AbilityId.WarriorChallenge, AbilityId.None, AbilityId.MageMagicBolt },
            ClassPassiveSlots = new[] { AbilityId.WarriorSturdy, AbilityId.MageArcane },
            MainClass = ClassId.Warrior, SecondaryClass = ClassId.Mage,
            Classes      = new[] { new ClassProgress { Class = ClassId.Warrior, Unlocked = true, Kills = 7, Points = 1 } },
            WeaponPoints = new[] { new WeaponPoints { Family = WeaponFamily.Sword, Grade = Grade.Rare, Points = 2, Kills = 13 } },
            Cooldowns    = new[] { new AbilityCooldown { Id = AbilityId.SwordLunge, MsLeft = 4500 } },
        };
        var st2 = MessagePack.MessagePackSerializer.Deserialize<AbilityStateMsg>(MessagePack.MessagePackSerializer.Serialize(st));
        bool andata = st2.Unlocked[0].Level == 2 && st2.WeaponSlots[0].Abilities[2] == AbilityId.SwordParry
                      && st2.ClassPassiveSlots[1] == AbilityId.MageArcane && st2.SecondaryClass == ClassId.Mage
                      && st2.WeaponPoints[0].Grade == Grade.Rare && st2.Cooldowns[0].MsLeft == 4500
                      && st2.Classes[0].Kills == 7;
        var eq = new EquipmentStateMsg { Worn = new[]
        {
            new InventoryItem { Type = ItemType.IronHelm, Count = 1, InstanceId = 42, Level = 3 },
            new InventoryItem(), new InventoryItem { Type = ItemType.BoneGreaves, Count = 1 },
        } };
        var eq2 = MessagePack.MessagePackSerializer.Deserialize<EquipmentStateMsg>(MessagePack.MessagePackSerializer.Serialize(eq));
        andata &= eq2.Worn.Length == 3 && eq2.Worn[0].Level == 3 && eq2.Worn[1].Type == ItemType.None
                  && eq2.Worn[2].Type == ItemType.BoneGreaves;
        var ps = MessagePack.MessagePackSerializer.Deserialize<PlayerStatsMsg>(MessagePack.MessagePackSerializer.Serialize(
            new PlayerStatsMsg { DefensePercent = 5, AttackSpeedPercent = 6, CooldownReductionPercent = -4, CombatRegenPerMille = 3 }));
        andata &= ps.DefensePercent == 5 && ps.AttackSpeedPercent == 6 && ps.CooldownReductionPercent == -4 && ps.CombatRegenPerMille == 3;
        Console.WriteLine($"  andata e ritorno dei messaggi: {(andata ? "si" : "no")}");
        if (!andata) Male("un messaggio nuovo non torna uguale");

        // Il server: i sei comandi nuovi si riconoscono e, per questa sonda che
        // non ha niente, si rifiutano.
        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }
        static byte[] P<T>(T v) => MessagePack.MessagePackSerializer.Serialize(v);
        var comandi = new (uint Id, CommandType Tipo, byte[] Contenuto)[]
        {
            (101, CommandType.UseAbility,     P(new UseAbilityCommand { Ability = AbilityId.SwordHeavySlash, Yaw = 1f })),
            (102, CommandType.SetAbilitySlot, P(new SetAbilitySlotCommand { Kind = AbilitySlotKind.Weapon, Family = WeaponFamily.Sword, Slot = 0, Ability = AbilityId.SwordParry })),
            // Da A14i la scelta della classe c'e' davvero: una secondaria senza
            // principale resta NotAllowed.
            (103, CommandType.ChooseClass,    P(new ChooseClassCommand { Class = ClassId.Warrior, Secondary = true })),
            (104, CommandType.Evolve,         P(new EvolveCommand { Into = ClassId.CrystalAdept })),
            (105, CommandType.SpendPoint,     P(new SpendPointCommand { Ability = AbilityId.SwordLunge })),
            (106, CommandType.Equip,          P(new EquipCommand { Slot = EquipSlot.Body, InstanceId = 0 })),
            // Contenuto rotto: dev'essere InvalidCommand, non NotAllowed.
            (107, CommandType.UseAbility,     new byte[] { 0xC1 }),
        };
        foreach (var c in comandi) s.Comando(c.Id, c.Tipo, c.Contenuto);
        s.Attendi(() => comandi.All(c => s.Esito(c.Id) != null), 8);
        foreach (var c in comandi)
        {
            var r = s.Esito(c.Id);
            // Da A14g1 uso e slot ci sono davvero: senza spada e senza abilita
            // sbloccate la sonda riceve "non usabile". Gli altri restano NotAllowed.
            var atteso = c.Id == 107 ? ErrorCode.InvalidCommand
                       : c.Id is 101 or 102 ? ErrorCode.AbilityNotUsable : ErrorCode.NotAllowed;
            string visto = r == null ? "nessuna risposta" : $"{(r.Success ? "riuscito" : "rifiutato")} {r.ErrorCode}";
            Console.WriteLine($"  {c.Tipo,-15}{(c.Id == 107 ? " (rotto)" : "        ")} → {visto}");
            if (r == null || r.Success || r.ErrorCode != atteso) Male($"{c.Tipo}: atteso rifiutato {atteso}");
        }

        if (ok) Console.WriteLine("  OK: numeri, messaggi e comandi del protocollo delle abilita come deciso");
        return ok;
    }

    // ---- catalogo e stato delle abilita (A14b) ---------------------------------
    //
    // Il catalogo del server deve avere una riga per ogni abilita del protocollo,
    // coi numeri che hanno senso; lo stato del giocatore deve arrivare al client
    // all'ingresso e a ogni cambio, e chi lo carica deve scartare cio che non ha
    // senso. Che superi un riavvio lo controlla la prova di persistenza.

    /// Lo stato fisso di "abilita esempio" dopo la pulizia, come lo scrive il
    /// server: sette voci scartate, il livello 9 diventato 3.
    public const string AbilitaEsempioPulita =
        "sbloccate=101:3,102:2,201:1,1000:1,1001:1,1050:1,1300:1,1301:1 arma=Sword:102/-/101 " +
        "attivi=1000/1300/- passivi=-/- principale=Warrior secondaria=Mage " +
        "classi=Warrior:1:7:1,Mage:0:3:0 punti=Sword.Rare:2:13";

    /// La riga senza le ricariche, che col passare del tempo finiscono.
    public static string SenzaRicariche(string riga)
    {
        if (riga == null) return null;
        int i = riga.IndexOf(" ricariche=", StringComparison.Ordinal);
        return i < 0 ? riga : riga[..i];
    }

    public static string Ricariche(string riga)
    {
        if (riga == null) return "";
        int i = riga.IndexOf(" ricariche=", StringComparison.Ordinal);
        return i < 0 ? "" : riga[(i + " ricariche=".Length)..];
    }

    public static bool CatalogoEStatoAbilita(string token, string host, int port)
    {
        Console.WriteLine("--- catalogo e stato delle abilita: ogni abilita ha la sua riga, lo stato arriva e si pulisce");
        bool ok = true;
        void Male(string m) { Console.WriteLine("  FALLITA: " + m); ok = false; }

        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }

        // All'ingresso arriva lo stato, qualunque sia.
        bool arrivato = s.Attendi(() => s.AbilitaRicevute > 0, 6);
        Console.WriteLine($"  stato delle abilita all'ingresso: {(arrivato ? "arrivato" : "non arrivato")}");
        if (!arrivato) Male("all'ingresso il server non manda lo stato delle abilita");

        // Il catalogo: 175 righe, 123 attive (4 armi da 13, 6 classi da 11,
        // Mago 5) e 52 passive, 6 uniche e 4 sull'oggetto, nessun problema.
        var cat = s.ChiediAdmin("abilita catalogo", "catalogo abilita: ");
        Console.WriteLine($"  catalogo: {cat ?? "(nessuna risposta)"}");
        if (cat != "totale=175 attive=123 passive=52 uniche=6 sull'oggetto=4 problemi=0")
            Male("il catalogo non torna col protocollo");

        // Lo stato d'esempio: dopo la pulizia dev'essere esattamente questo.
        int prima = s.AbilitaRicevute;
        var riga = s.AbilitaInRiga("esempio");
        Console.WriteLine($"  esempio: {riga ?? "(nessuna risposta)"}");
        if (riga == null || !riga.StartsWith("scartate=7 "))
            Male("le voci senza senso scartate non sono sette");
        else if (SenzaRicariche(riga["scartate=7 ".Length..]) != AbilitaEsempioPulita)
            Male("lo stato pulito non e' quello atteso: " + AbilitaEsempioPulita);
        if (Ricariche(riga) != "102,112") Male($"ricariche attese 102,112, sono {Ricariche(riga)}");

        // Il cambio arriva anche al client, nella stessa forma.
        if (!s.Attendi(() => s.AbilitaRicevute > prima, 6)) Male("dopo il cambio lo stato non e' arrivato al client");
        else
        {
            var m = s.Abilita;
            var affondo = m.Cooldowns.FirstOrDefault(c => c.Id == AbilityId.SwordLunge);
            bool forma = m.Unlocked.Length == 8
                         && m.Unlocked.Any(u => u.Id == AbilityId.SwordParry && u.Level == 3)
                         && m.WeaponSlots.Length == 1 && m.WeaponSlots[0].Family == WeaponFamily.Sword
                         && m.WeaponSlots[0].Abilities.SequenceEqual(new[] { AbilityId.SwordLunge, AbilityId.None, AbilityId.SwordParry })
                         && m.ClassActiveSlots.SequenceEqual(new[] { AbilityId.WarriorChallenge, AbilityId.MageMagicBolt, AbilityId.None })
                         && m.ClassPassiveSlots.All(a => a == AbilityId.None)
                         && m.MainClass == ClassId.Warrior && m.SecondaryClass == ClassId.Mage
                         && m.WeaponPoints.Length == 1 && m.WeaponPoints[0].Grade == Grade.Rare && m.WeaponPoints[0].Points == 2
                         // Dieci minuti chiesti, ricarica vera 10 secondi: non di piu',
                         // salvo un tick di arrotondamento (33 ms).
                         && affondo != null && affondo.MsLeft > 0 && affondo.MsLeft <= 10_000 + 34;
            Console.WriteLine($"  messaggio al client: {m.Unlocked.Length} sbloccate, ricarica dell'Affondo {affondo?.MsLeft ?? 0} ms, forma {(forma ? "giusta" : "sbagliata")}");
            if (!forma) Male("il messaggio al client non e' lo stato pulito");
        }

        // Rileggendolo, lo stato e' rimasto quello.
        var ancora = s.AbilitaInRiga();
        if (SenzaRicariche(ancora) != AbilitaEsempioPulita) Male($"riletto, lo stato e' cambiato: {ancora}");

        if (ok) Console.WriteLine("  OK: catalogo completo, stato mandato al client e ripulito al caricamento");
        return ok;
    }

    // ---- comandi di prova delle abilita (A14c) ---------------------------------
    //
    // I comandi con cui Tommaso prova armi e abilita senza fare il percorso
    // (decisioni/abilita.md, sezione 10): ognuno deve fare quello che dice,
    // rifiutare quello che non ha senso (l'abilita che sta sull'oggetto, il
    // grado Dono di Torak, un'evoluzione scelta senza evolvere), lasciare uno
    // stato pulito e mandarlo al client.

    public static bool ComandiDiProvaAbilita(string token, string host, int port)
    {
        Console.WriteLine("--- comandi di prova delle abilita: dai, sblocca, livello, classe, evolvi, punti, ricarica");
        bool ok = true;
        void Male(string m) { Console.WriteLine("  FALLITA: " + m); ok = false; }

        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }

        // Ogni comando risponde con il suo nome davanti; "errore" se non ha fatto niente.
        string Chiedi(string comando, string testa)
        {
            var r = s.ChiediAdmin(comando, testa + ": ");
            Console.WriteLine($"  {comando} -> {r ?? "(nessuna risposta)"}");
            return r ?? "(nessuna risposta)";
        }
        void Atteso(string comando, string testa, string atteso)
        {
            var r = Chiedi(comando, testa);
            if (r != atteso) Male($"'{comando}' doveva rispondere '{atteso}'");
        }
        void Rifiutato(string comando, string testa)
        {
            if (!Chiedi(comando, testa).StartsWith("errore")) Male($"'{comando}' doveva essere rifiutato");
        }
        void Stato(string atteso, string quando)
        {
            var r = SenzaRicariche(s.AbilitaInRiga());
            if (r != atteso) Male($"{quando}: lo stato e' [{r}], atteso [{atteso}]");
        }

        // ricarica, e il cambio di classe che svuota gli slot della classe
        // lasciata: si parte dallo stato d'esempio, che ha due ricariche e
        // uno slot del Guerriero.
        s.AbilitaInRiga("esempio");
        Atteso("ricarica", "ricarica", "azzerate 2");
        if (Ricariche(s.AbilitaInRiga()) != "") Male("dopo 'ricarica' restano ricariche in corso");
        Atteso("classe cacciatore", "classe", "principale=Hunter secondaria=Mage slot svuotati=1");
        Stato("sbloccate=101:3,102:2,201:1,1000:1,1001:1,1050:1,1300:1,1301:1 arma=Sword:102/-/101 " +
              "attivi=-/1300/- passivi=-/- principale=Hunter secondaria=Mage " +
              "classi=Warrior:1:7:1,Hunter:0:0:0,Mage:0:3:0 punti=Sword.Rare:2:13", "dopo il cambio di classe");

        // azzera e sblocca.
        Atteso("abilita azzera", "abilita azzera", "ok");
        const string Vuoto = "sbloccate= arma= attivi=-/-/- passivi=-/- principale=None secondaria=None classi= punti=";
        Stato(Vuoto, "dopo 'abilita azzera'");
        Atteso("abilita sblocca spada", "abilita sblocca", "sbloccate 12 nuove, saltate 1, in tutto 12");
        Rifiutato("abilita sblocca 140", "abilita sblocca");            // Tempesta di lame, sull'oggetto
        Rifiutato("abilita sblocca nienteditale", "abilita sblocca");
        Atteso("abilita sblocca guerriero", "abilita sblocca", "sbloccate 18 nuove, saltate 1, in tutto 30");
        Atteso("abilita sblocca 1040", "abilita sblocca", "sbloccate 1 nuove, saltate 0, in tutto 31"); // l'unica, solo per numero
        // Tutte: 175 meno le 4 sull'oggetto e le 6 uniche, piu' l'unica gia' presa.
        Atteso("abilita sblocca tutte", "abilita sblocca", "sbloccate 135 nuove, saltate 10, in tutto 166");

        // livello, per nome e per numero.
        Atteso("abilita livello Affondo 3", "abilita livello", "102 Affondo al livello 3");
        Atteso("abilita livello 1310 2", "abilita livello", "1310 Scudo magico al livello 2");
        Rifiutato("abilita livello 102 4", "abilita livello");
        Rifiutato("abilita livello 240 1", "abilita livello");           // Lancia del cielo, sull'oggetto

        // classe, sblocca, punti.
        Rifiutato("classe adepto", "classe");                            // un'evoluzione si raggiunge evolvendo
        Rifiutato("classe guerriero secondaria guerriero", "classe");
        Atteso("classe guerriero secondaria mago", "classe", "principale=Warrior secondaria=Mage slot svuotati=0");
        Atteso("classe sblocca", "classe", "sbloccate Warrior e Mage, abilita base nuove=0");
        Atteso("punti classe 4", "punti", "punti di classe Warrior=4");
        Atteso("punti arma raro 2 lancia", "punti", "punti d'arma Rare=2 per Spear");
        Rifiutato("punti arma leggendario 2 lancia", "punti");
        var r = SenzaRicariche(s.AbilitaInRiga());
        if (r == null || !r.Contains(" principale=Warrior secondaria=Mage classi=Warrior:1:0:4,Mage:1:0:0 punti=Spear.Rare:2:0"))
            Male($"classi e punti non sono quelli chiesti: {r}");

        // evolvi: l'evoluzione diventa la principale; dopo, ci si puo' tornare
        // col comando classe.
        Atteso("evolvi accolito", "evolvi", "principale=EchoAcolyte secondaria=Mage abilita base nuove=0 slot svuotati=0");
        Rifiutato("evolvi guerriero", "evolvi");
        Atteso("classe mago", "classe", "principale=Mage secondaria=None slot svuotati=0");
        Atteso("classe accolito", "classe", "principale=EchoAcolyte secondaria=None slot svuotati=0");

        // Il client riceve lo stato dopo un cambio.
        int prima = s.AbilitaRicevute;
        Atteso("classe cacciatore secondaria accolito", "classe", "principale=Hunter secondaria=EchoAcolyte slot svuotati=0");
        if (!s.Attendi(() => s.AbilitaRicevute > prima && s.Abilita?.MainClass == ClassId.Hunter
                             && s.Abilita.SecondaryClass == ClassId.EchoAcolyte, 6))
            Male("dopo il cambio di classe lo stato non e' arrivato al client");
        else if (s.Abilita.Unlocked.Length != 166 || !s.Abilita.Unlocked.Any(u => u.Id == AbilityId.SwordLunge && u.Level == 3))
            Male($"al client sono arrivate {s.Abilita.Unlocked.Length} sbloccate, non 166 con l'Affondo al 3");

        // dai: esemplari col grado.
        ulong Id(string risposta) =>
            ulong.TryParse(risposta.Split("id=").LastOrDefault()?.Split(',')[0], out var n) ? n : 0;
        byte? Grado(ulong id)
        {
            byte g = 0;
            bool c = s.Attendi(() => { lock (s.Zaino) return s.GradiIstanze.TryGetValue(id, out g); }, 5);
            return c ? g : null;
        }
        var spada = Chiedi("dai IronSword raro", "dai");
        if (!spada.StartsWith("1x IronSword Rare id=")) Male("'dai IronSword raro' non ha dato una spada di ferro rara");
        else if (Grado(Id(spada)) != (byte)Grade.Rare) Male("la spada data non e' arrivata nello zaino come Rara");
        var scudi = Chiedi("dai Shield epico 2", "dai");
        if (!scudi.StartsWith("2x Shield Epic id=")) Male("'dai Shield epico 2' non ha dato due scudi epici");
        else if (Grado(Id(scudi)) != (byte)Grade.Epic) Male("lo scudo dato non e' arrivato nello zaino come Epico");
        Rifiutato("dai IronSword torak", "dai");                          // uno al mondo: passa dal registro
        Rifiutato("dai Wood comune", "dai");                              // non ha gradi
        // Le armature sono esemplari da A14e1: ora si danno.
        var cappuccio = Chiedi("dai LeatherHood comune", "dai");
        if (!cappuccio.StartsWith("1x LeatherHood Common id=")) Male("'dai LeatherHood comune' non ha dato un cappuccio di cuoio Comune");
        else if (Grado(Id(cappuccio)) != (byte)Grade.Common) Male("il cappuccio dato non e' arrivato nello zaino come Comune");
        Rifiutato("dai IronSword leggendario", "dai");

        // Si lascia l'account pulito per chi viene dopo.
        s.ChiediAdmin("abilita azzera", "abilita azzera: ");
        s.Admin("clearinv");

        if (ok) Console.WriteLine("  OK: i comandi di prova fanno quello che dicono e rifiutano quello che non ha senso");
        return ok;
    }

    // ---- bastone e regola dello scudo (A14d) -----------------------------------
    //
    // Il bastone e' un'arma vera: ricetta di 3 legno e 1 cuoio senza stazione,
    // un esemplare con usura, si puo' dare col suo grado. Lo scudo nella mano
    // libera vale solo con spada o bastone, o a mani vuote: con lancia, arco,
    // giavellotto o attrezzi resta appeso (decisioni/abilita.md sezione 3).
    public static bool BastoneEScudo(string token, string host, int port)
    {
        Console.WriteLine("--- bastone e scudo: il bastone si crea senza stazione, lo scudo solo con spada o bastone");
        bool ok = true;
        void Male(string m) { Console.WriteLine("  FALLITA: " + m); ok = false; }

        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }
        if (!s.Attendi(() => s.Ricette.Length > 0, 10)) { Console.WriteLine("  FALLITA: il catalogo non e' arrivato"); return false; }

        // La ricetta, come la vede il client.
        int iStaff = s.IndiceRicetta("Staff");
        if (iStaff < 0) { Console.WriteLine("  FALLITA: nel catalogo non c'e' il bastone"); return false; }
        var ric = s.Ricette[iStaff];
        string ing = string.Join("+", ric.Ingredients.OrderBy(g => g.Type)
            .Select(g => $"{(g.Count <= 0 ? 1 : g.Count)}x{g.Type}"));
        Console.WriteLine($"  Staff = {ing}, stazioni: banco={ric.RequiresWorkbench} costruttore={ric.RequiresConstructorTable} fornace={ric.RequiresFurnace}");
        if (ric.ResultType != ItemType.Staff || ric.Category != CraftCategory.Weapon)
            Male($"la ricetta 'Staff' da {ric.ResultType} ({ric.Category}), non un bastone fra le armi");
        if (ing != "3xWood+1xLeather") Male($"ingredienti {ing}, attesi 3 legno e 1 cuoio");
        if (ric.RequiresWorkbench || ric.RequiresConstructorTable || ric.RequiresFurnace)
            Male("il bastone non deve chiedere stazioni");

        // Crearlo davvero, lontano da ogni stazione.
        const float CX = -140f, CZ = 250f;
        s.Admin("setfaction 3");
        s.Admin($"tp {CX.ToString(Inv)} {CZ.ToString(Inv)}");
        s.Admin("clearinv");
        s.Admin("give Wood 3");
        s.Admin("give Leather 1");
        if (!s.Attendi(() => s.Quanti(ItemType.Wood) == 3 && s.Quanti(ItemType.Leather) == 1 &&
                             Dist(s.X, s.Z, CX, CZ) < 2f, 12))
        {
            Console.WriteLine("  FALLITA: preparazione non riuscita");
            return false;
        }
        s.ChiediRicetta(iStaff, CX, CZ);
        bool creato = s.Attendi(() => s.Quanti(ItemType.Staff) >= 1 && s.Quanti(ItemType.Wood) == 0 &&
                                      s.Quanti(ItemType.Leather) == 0, 15);
        // Il messaggio che chiude la creazione porta il conteggio, non
        // l'identita' dell'esemplare: l'esemplare si vede con "dai" qui sotto.
        Console.WriteLine($"  creato: bastoni={s.Quanti(ItemType.Staff)} legno={s.Quanti(ItemType.Wood)} cuoio={s.Quanti(ItemType.Leather)}");
        if (!creato) Male("il bastone non e' stato creato, o gli ingredienti non sono stati consumati");

        // Col suo grado, dal comando di prova.
        var dato = s.ChiediAdmin("dai Staff comune", "dai: ");
        Console.WriteLine($"  dai Staff comune -> {dato ?? "(nessuna risposta)"}");
        if (dato == null || !dato.StartsWith("1x Staff Common id=")) Male("'dai Staff comune' non ha dato un bastone Comune");
        else
        {
            ulong idDato = ulong.TryParse(dato.Split("id=").Last(), out var n) ? n : 0;
            (ItemType Tipo, int Usura) es = default;
            byte grado = 0;
            bool arrivato = s.Attendi(() => { lock (s.Zaino) return s.Istanze.TryGetValue(idDato, out es) && s.GradiIstanze.TryGetValue(idDato, out grado); }, 5);
            Console.WriteLine($"  esemplare {idDato}: {es.Tipo} usura={es.Usura} grado={grado}");
            if (!arrivato || es.Tipo != ItemType.Staff || es.Usura != 60 || grado != (byte)Grade.Common)
                Male("il bastone dato non e' arrivato come esemplare Comune con usura 60");
        }

        // La regola dello scudo, arma per arma, nella posizione "arma 1 e mano
        // libera"; e a mani vuote nella posizione 4.
        s.Admin("give Shield 1");
        if (!s.Attendi(() => s.Quanti(ItemType.Shield) >= 1, 8)) { Male("lo scudo non e' arrivato"); }
        var casi = new (ItemType Arma, bool ConScudo)[]
        {
            (ItemType.StoneSword, true), (ItemType.IronSword, true), (ItemType.Staff, true),
            (ItemType.WoodSpear, false), (ItemType.IronSpear, false), (ItemType.Bow, false),
            (ItemType.WoodJavelin, false), (ItemType.Axe, false), (ItemType.RudimentaryKnife, false),
        };
        string Mano(ItemType arma, byte posizione)
        {
            // Tre comandi per arma, dieci armi di fila: senza una pausa si
            // arriva a 30 comandi in due terzi di secondo, e il server scarta
            // in silenzio dal 31esimo nella stessa finestra (MaxCommandsPerSecond).
            // E' quello che e' successo al giro 1 di A14d con la posizione 4,
            // non il numero del comando (P3, riprodotto il 6 novembre).
            s.Attendi(() => false, 0.15);
            s.Dotazione(arma, ItemType.None, SideItemType.Shield);
            s.Posizione(posizione);
            CommandResultMsg esito = null;
            s.Attendi(() => (esito = s.Esito(112)) != null, 4);
            if (esito == null || !esito.Success)
                Console.WriteLine($"  cambio di posizione {posizione}: {(esito == null ? "nessun esito" : $"rifiutato {esito.ErrorCode}")}");
            string atteso = $"arma1={arma} arma2=None fianco=Shield posizione={posizione} ";
            string r = null;
            s.Attendi(() => (r = s.ChiediAdmin("dotazione", "dotazione: ")) != null && r.StartsWith(atteso), 6);
            Console.WriteLine($"  {arma,-16} posizione {posizione}: {r ?? "(nessuna risposta)"}");
            if (r == null || !r.StartsWith(atteso)) return null;
            return r.Split(' ').FirstOrDefault(p => p.StartsWith("mano="))?.Substring(5);
        }
        foreach (var (arma, conScudo) in casi)
        {
            var mano = Mano(arma, 1);
            string attesa = conScudo ? "Shield" : "None";
            if (mano != attesa) Male($"con {arma} la mano libera e' '{mano ?? "?"}', attesa {attesa}");
        }
        if (Mano(ItemType.Staff, 4) != "Shield") Male("a mani vuote (posizione 4) lo scudo deve restare");

        // Si lascia l'account pulito per chi viene dopo.
        s.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        s.Posizione(0);
        s.Admin("clearinv");

        if (ok) Console.WriteLine("  OK: il bastone si crea e si da, lo scudo vale solo con spada, bastone o a mani vuote");
        return ok;
    }

    // ---- armature (A14e1) --------------------------------------------------------
    //
    // Nove pezzi (cuoio, ferro, osso; testa, busto, gambe), ricette al banco da
    // lavoro, esemplari con grado e usura. Si indossano col comando Equip: il
    // pezzo esce dallo zaino, quello che c'era nello slot ci torna. I bonus
    // vanno nelle statistiche mandate al client, col moltiplicatore del grado
    // (decisioni/abilita.md sezione 4). Morendo l'armatura segue lo zaino.
    // Che le statistiche cambino davvero la partita lo prova "statistiche in
    // partita" (A14e2).
    public static bool Armature(string token, string host, int port)
    {
        Console.WriteLine("--- armature: si creano al banco, si indossano, danno bonus col grado");
        bool ok = true;
        void Male(string m) { Console.WriteLine("  FALLITA: " + m); ok = false; }

        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }
        if (!s.Attendi(() => s.Ricette.Length > 0 && s.StatisticheRicevute > 0 && s.IndossatiRicevuti > 0, 10))
        { Console.WriteLine("  FALLITA: all'ingresso non sono arrivati catalogo, statistiche e armatura indossata"); return false; }

        // Da zero: niente indosso, zaino vuoto.
        foreach (var slot in new[] { EquipSlot.Head, EquipSlot.Body, EquipSlot.Legs })
        { s.Togli(slot); s.Attendi(() => s.Esito(113) != null, 3); }
        s.Admin("clearinv");
        s.Attendi(() => false, 0.5);
        if (!s.Attendi(() => s.Indossati?.Worn.Length == 3 && s.Indossati.Worn.All(w => w.Type == ItemType.None), 5))
        { Console.WriteLine("  FALLITA: non sono riuscito a togliermi tutto"); return false; }

        // 1. il catalogo.
        var attese = new Dictionary<ItemType, string>
        {
            [ItemType.LeatherHood] = "3xLeather", [ItemType.LeatherBoots] = "4xLeather", [ItemType.LeatherJacket] = "6xLeather",
            [ItemType.IronHelm] = "2xIronIngot+1xLeather", [ItemType.IronGreaves] = "3xIronIngot+1xLeather", [ItemType.IronCuirass] = "4xIronIngot+2xLeather",
            [ItemType.BoneHood] = "3xBone+1xLeather", [ItemType.BoneGreaves] = "4xBone+1xLeather", [ItemType.BoneCuirass] = "6xBone+2xLeather",
        };
        foreach (var (tipo, ingAtt) in attese)
        {
            var ric = s.Ricette.FirstOrDefault(r => r.ResultType == tipo);
            if (ric == null) { Male($"nel catalogo non c'e' {tipo}"); continue; }
            string ing = string.Join("+", ric.Ingredients.OrderBy(g => g.Type).Select(g => $"{(g.Count <= 0 ? 1 : g.Count)}x{g.Type}"));
            if (ing != ingAtt) Male($"{tipo}: ingredienti {ing}, attesi {ingAtt}");
            if (!ric.RequiresWorkbench || ric.RequiresConstructorTable || ric.RequiresFurnace)
                Male($"{tipo}: deve chiedere il banco da lavoro e basta");
            if (ric.Category != CraftCategory.Weapon) Male($"{tipo}: categoria {ric.Category}, attesa Weapon");
        }
        Console.WriteLine($"  catalogo: {attese.Count(a => s.Ricette.Any(r => r.ResultType == a.Key))}/9 armature");

        // 2. creare: lontano dal banco no, accanto si', e gli ingredienti si consumano.
        int iHood = s.Ricette.ToList().FindIndex(r => r.ResultType == ItemType.LeatherHood);
        const float LX = -140f, LZ = 250f;   // lontano da ogni stazione (vedi bastone e scudo)
        const float BX = -140f, BZ = 280f;   // qui si mette un banco
        s.Admin("setfaction 3");
        s.Admin($"tp {LX.ToString(Inv)} {LZ.ToString(Inv)}");
        s.Admin("give Leather 3");
        if (!s.Attendi(() => s.Quanti(ItemType.Leather) == 3 && Dist(s.X, s.Z, LX, LZ) < 2f, 10))
        { Console.WriteLine("  FALLITA: preparazione non riuscita"); return false; }
        s.ChiediRicetta(iHood, LX, LZ);
        s.Attendi(() => s.UltimoCraft != null, 6);
        Console.WriteLine($"  lontano dal banco: {(s.UltimoCraft == null ? "nessuna risposta" : $"Success={s.UltimoCraft.Success} \"{s.UltimoCraft.ResultName}\"")}");
        if (s.UltimoCraft == null || s.UltimoCraft.Success) Male("lontano dal banco il cappuccio non si deve poter creare");

        s.Admin($"build Workbench {(BX + 2f).ToString(Inv)} {BZ.ToString(Inv)}");
        s.Admin($"tp {BX.ToString(Inv)} {BZ.ToString(Inv)}");
        s.Attendi(() => Dist(s.X, s.Z, BX, BZ) < 2f, 6);
        s.Attendi(() => false, 0.5);
        s.ChiediRicetta(iHood, BX, BZ);
        bool creato = s.Attendi(() => s.Quanti(ItemType.LeatherHood) >= 1 && s.Quanti(ItemType.Leather) == 0, 15);
        Console.WriteLine($"  accanto al banco: cappucci={s.Quanti(ItemType.LeatherHood)} cuoio={s.Quanti(ItemType.Leather)}");
        if (!creato) Male("accanto al banco il cappuccio non e' stato creato, o il cuoio non e' stato consumato");

        // 3. esemplari col grado, dal comando di prova.
        ulong Dai(string riga, ItemType tipo, Grade grado)
        {
            s.Attendi(() => false, 0.15);
            var r = s.ChiediAdmin(riga, "dai: ");
            ulong id = r != null && ulong.TryParse(r.Split("id=").Last(), out var n) ? n : 0;
            byte g = 0;
            (ItemType Tipo, int Usura) es = default;
            bool arrivato = id != 0 && s.Attendi(() => { lock (s.Zaino) return s.Istanze.TryGetValue(id, out es) && s.GradiIstanze.TryGetValue(id, out g); }, 5);
            Console.WriteLine($"  {riga} -> {r ?? "(nessuna risposta)"} usura={es.Usura}");
            if (!arrivato || es.Tipo != tipo || g != (byte)grado) { Male($"'{riga}' non ha dato un {tipo} {grado}"); return 0; }
            return id;
        }
        ulong hood    = Dai("dai LeatherHood base", ItemType.LeatherHood, Grade.Base);
        ulong jacket  = Dai("dai LeatherJacket comune", ItemType.LeatherJacket, Grade.Common);
        ulong greaves = Dai("dai BoneGreaves epico", ItemType.BoneGreaves, Grade.Epic);
        ulong helm    = Dai("dai IronHelm raro", ItemType.IronHelm, Grade.Rare);
        ulong staff   = Dai("dai Staff base", ItemType.Staff, Grade.Base);
        if (!ok) return false;

        // Le statistiche di partenza: tutto quello che cambia, cambia da qui.
        var b = s.Statistiche;
        int hpPct = b.Modifiers.Where(m => m.Stat == StatKind.MaxHp).Sum(m => m.Percent);
        Console.WriteLine($"  di partenza: vita={b.MaxHp} danno={b.DamagePercent} movimento={b.MoveSpeedPercent} " +
                          $"attacco={b.AttackSpeedPercent} ricariche={b.CooldownReductionPercent} rigenerazione={b.CombatRegenPerMille}");

        bool Equip(ulong id, EquipSlot? togli = null)
        {
            s.Attendi(() => false, 0.15);
            if (togli is EquipSlot sl) s.Togli(sl); else s.Indossa(id);
            CommandResultMsg e = null;
            s.Attendi(() => (e = s.Esito(113)) != null, 5);
            return e != null && e.Success;
        }
        string Indosso() => s.Indossati == null ? "?" : string.Join(",", s.Indossati.Worn.Select(w => w.Type == ItemType.None ? "-" : $"{w.Type}:{w.InstanceId}"));
        bool NelloZaino(ulong id) { lock (s.Zaino) return s.Istanze.ContainsKey(id); }
        void Statistiche(string quando, int vita, int danno, int movimento, int attacco, int ricariche, int rigenerazione)
        {
            int vitaAtt = Math.Max(10, (int)Math.Round(100 * (1 + (hpPct + vita) / 100.0)));
            bool giuste = s.Attendi(() =>
            {
                var t = s.Statistiche;
                return t.MaxHp == vitaAtt && t.DamagePercent == b.DamagePercent + danno
                    && t.MoveSpeedPercent == b.MoveSpeedPercent + movimento
                    && t.AttackSpeedPercent == b.AttackSpeedPercent + attacco
                    && t.CooldownReductionPercent == b.CooldownReductionPercent + ricariche
                    && t.CombatRegenPerMille == b.CombatRegenPerMille + rigenerazione;
            }, 5);
            var t = s.Statistiche;
            Console.WriteLine($"  {quando}: indosso [{Indosso()}] vita={t.MaxHp} danno={t.DamagePercent} movimento={t.MoveSpeedPercent} " +
                              $"attacco={t.AttackSpeedPercent} ricariche={t.CooldownReductionPercent} rigenerazione={t.CombatRegenPerMille}");
            if (!giuste) Male($"{quando}: attese vita {vitaAtt}, danno +{danno}, movimento {movimento:+0;-0}, attacco +{attacco}, ricariche +{ricariche}, rigenerazione +{rigenerazione}");
        }

        // 4. indossare. Cuoio Base in testa (+2 movimento e attacco), cuoio
        // Comune sul busto (+3 x1,5), osso Epico alle gambe (+2 x3 danno e
        // ricariche). Movimento e attacco 2 + 4,5 = 6,5 -> 7.
        if (!Equip(hood) || !Equip(jacket) || !Equip(greaves)) Male("indossare tre pezzi dello zaino e' stato rifiutato");
        if (NelloZaino(hood) || NelloZaino(jacket) || NelloZaino(greaves)) Male("un pezzo indossato e' rimasto nello zaino");
        if (Indosso() != $"LeatherHood:{hood},LeatherJacket:{jacket},BoneGreaves:{greaves}") Male($"indossati [{Indosso()}], non i tre pezzi nei loro slot");
        Statistiche("cuoio base, cuoio comune, osso epico", 0, 6, 7, 7, 6, 0);

        // Il ferro Raro in testa al posto del cuoio: il cuoio torna nello
        // zaino, lo stesso esemplare. Vita +4 x2, rigenerazione 1 x2, e -2 di
        // movimento che non sale col grado: movimento 4,5 - 2 = 2,5 -> 3.
        if (!Equip(helm)) Male("indossare l'elmo sopra il cappuccio e' stato rifiutato");
        if (!s.Attendi(() => NelloZaino(hood), 5)) Male("il cappuccio tolto dalla testa non e' tornato nello zaino");
        Statistiche("ferro raro al posto del cuoio", 8, 6, 3, 5, 6, 2);

        // Togliere le gambe.
        if (!Equip(0, EquipSlot.Legs)) Male("togliere i gambali e' stato rifiutato");
        if (!s.Attendi(() => NelloZaino(greaves), 5)) Male("i gambali tolti non sono tornati nello zaino");
        else if (!s.GradiIstanze.TryGetValue(greaves, out var gg) || gg != (byte)Grade.Epic) Male("i gambali sono tornati senza il loro grado");
        Statistiche("tolti i gambali", 8, 0, 3, 5, 0, 2);

        // Quello che non si fa.
        if (Equip(staff)) Male("un bastone non si indossa");
        if (Equip(987654321)) Male("un esemplare che non c'e' non si indossa");
        if (Equip(0, EquipSlot.Legs)) Male("togliere da uno slot vuoto deve essere rifiutato");

        // Il comando admin.
        var riga = s.ChiediAdmin("armatura", "armatura: ");
        Console.WriteLine($"  armatura -> {riga ?? "(nessuna risposta)"}");
        if (riga == null || !riga.StartsWith($"testa=IronHelm:Rare:{helm}:") || !riga.Contains($" busto=LeatherJacket:Common:{jacket}:")
            || !riga.Contains(" gambe=vuoto ") || !riga.EndsWith("bonus=MaxHp+8,MoveSpeed+3,AttackSpeed+5,CombatRegen+2"))
            Male("il comando 'armatura' non dice quello che si indossa");

        // 5. morendo l'armatura segue lo zaino: non resta indosso, e lo zaino si perde.
        int prima = s.IndossatiRicevuti;
        s.Admin("kill");
        bool svestito = s.Attendi(() => s.IndossatiRicevuti > prima && s.Indossati.Worn.All(w => w.Type == ItemType.None), 8);
        s.Attendi(() => false, 1.0);
        Console.WriteLine($"  dopo la morte: indosso [{Indosso()}], elmo nello zaino={NelloZaino(helm)}, giacca={NelloZaino(jacket)}");
        if (!svestito) Male("morendo l'armatura e' rimasta indosso");
        if (NelloZaino(helm) || NelloZaino(jacket)) Male("morendo l'armatura e' tornata nello zaino invece di perdersi con lui");
        Statistiche("dopo la morte", 0, 0, 0, 0, 0, 0);

        // Si lascia l'account pulito per chi viene dopo.
        s.Attendi(() => false, 4);
        s.Admin("clearinv");

        if (ok) Console.WriteLine("  OK: le armature si creano al banco, si indossano e si tolgono, danno i bonus del loro grado");
        return ok;
    }

    // ---- statistiche in partita (A14e2) ----------------------------------------------
    //
    // Le statistiche dell'armatura (e dei rituali) ora cambiano la partita:
    // vita massima, danno, velocita d'attacco, rigenerazione in combattimento.
    // Si misurano sui fatti, con due sonde di fazioni diverse: la vita vista
    // nell'istantanea, i colpi che una da all'altra. Il comando "statistiche"
    // dice i numeri che il server usa, per confrontarli.
    //
    // Numeri attesi (decisioni/abilita.md sezione 4, grado Epico x3): tre
    // pezzi di ferro, vita +42% e rigenerazione 9 millesimi al secondo; tre
    // d'osso, danno +21%; tre di cuoio, velocita d'attacco +21%.
    public static bool StatisticheInPartita(string tokenA, string tokenB, string host, int port)
    {
        Console.WriteLine("--- statistiche in partita: l'armatura cambia vita, danno, colpi, rigenerazione e passo");
        bool ok = true;
        void Male(string m) { Console.WriteLine("  FALLITA: " + m); ok = false; }

        using var a = new Sonda(tokenA, host, port);
        using var b = new Sonda(tokenB, host, port);
        if (!a.AttendiPronta() || !b.AttendiPronta()) { Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco"); return false; }
        if (!a.Attendi(() => a.Statistiche != null && a.Indossati != null, 10))
        { Console.WriteLine("  FALLITA: all'ingresso non sono arrivate statistiche e armatura"); return false; }

        Dictionary<string, string> Stat()
        {
            a.Attendi(() => false, 0.15);
            var r = a.ChiediAdmin("statistiche", "statistiche: ");
            var d = new Dictionary<string, string>();
            if (r == null) return d;
            foreach (var p in r.Split(' ')) { int i = p.IndexOf('='); if (i > 0) d[p[..i]] = p[(i + 1)..]; }
            d["riga"] = r;
            return d;
        }
        int VitaMax(Dictionary<string, string> d) => d.TryGetValue("vita", out var v) && int.TryParse(v.Split('/').Last(), out int n) ? n : -1;
        double Molt(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) && double.TryParse(v.TrimStart('x'), System.Globalization.NumberStyles.Float, Inv, out double x) ? x : -1;
        int Pausa(Dictionary<string, string> d) => d.TryGetValue("pausa", out var v) && int.TryParse(v.Split("->").Last(), out int n) ? n : -1;
        float VitaB() => a.Stato(b.Id).Vita;
        float VitaA() => a.Stato(a.Id).Vita;
        // Dopo "sethp" su B si aspetta che A veda B a vita piena, ascoltando
        // tutte e due: aspettando solo su B, A non legge la rete e la vita di B
        // che vede resta quella vecchia (giro 1: un colpo da 27 misurato 5).
        void Piena()
        {
            a.Attendi(() => { b.Attendi(() => true, 0.01); return VitaB() >= 100f; }, 3);
            a.Attendi(() => { b.Attendi(() => true, 0.01); return false; }, 0.3);
        }

        ulong Dai(string riga)
        {
            a.Attendi(() => false, 0.15);
            var r = a.ChiediAdmin(riga, "dai: ");
            ulong id = r != null && ulong.TryParse(r.Split("id=").Last(), out var n) ? n : 0;
            bool arrivato = id != 0 && a.Attendi(() => { lock (a.Zaino) return a.Istanze.ContainsKey(id); }, 5);
            if (!arrivato) Male($"'{riga}' non ha dato il pezzo ({r ?? "nessuna risposta"})");
            return id;
        }
        bool Equip(ulong id, EquipSlot? togli = null)
        {
            a.Attendi(() => false, 0.15);
            if (togli is EquipSlot sl) a.Togli(sl); else a.Indossa(id);
            CommandResultMsg e = null;
            a.Attendi(() => (e = a.Esito(113)) != null, 5);
            return e != null && e.Success;
        }
        void SvestiA()
        {
            foreach (var slot in new[] { EquipSlot.Head, EquipSlot.Body, EquipSlot.Legs })
                if (a.Indossati.Worn[(int)slot].Type != ItemType.None) Equip(0, slot);
        }

        // Le due sonde una accanto all'altra, lontane da tutto, in fazioni diverse.
        const float PX = -150f, PZ = 250f;
        a.Admin("setfaction 1");
        b.Admin("setfaction 2");
        SvestiA();
        a.Admin("clearinv");
        b.Admin("clearinv");
        a.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        a.Posizione(0);
        a.Admin($"tp {PX.ToString(Inv)} {PZ.ToString(Inv)}");
        b.Admin($"tp {(PX + 1f).ToString(Inv)} {PZ.ToString(Inv)}");
        if (!a.Attendi(() => Dist(a.X, a.Z, PX, PZ) < 2f, 8) || !b.Attendi(() => Dist(b.X, b.Z, PX + 1f, PZ) < 2f, 8))
        { Console.WriteLine("  FALLITA: le sonde non sono arrivate al punto della prova"); return false; }
        a.Cammina(0f, 0f, 0.1);
        b.Cammina(0f, 0f, 0.1);

        var s0 = Stat();
        int vita0 = VitaMax(s0);
        double danno0 = Molt(s0, "danno"), attacco0 = Molt(s0, "attacco");
        Console.WriteLine($"  di partenza: {(s0.TryGetValue("riga", out var r0) ? r0 : "(nessuna risposta)")}");
        if (vita0 <= 0 || danno0 <= 0 || attacco0 <= 0) { Console.WriteLine("  FALLITA: il comando 'statistiche' non risponde"); return false; }

        // 1. vita massima: col ferro Epico addosso si sale oltre la vita di
        //    prima, e l'istantanea lo vede.
        ulong elmo = Dai("dai IronHelm epico"), corazza = Dai("dai IronCuirass epico"), gambali = Dai("dai IronGreaves epico");
        if (!ok) return false;
        if (!Equip(elmo) || !Equip(corazza) || !Equip(gambali)) Male("indossare il ferro e' stato rifiutato");
        var sF = Stat();
        int vitaF = VitaMax(sF);
        Console.WriteLine($"  col ferro epico: {(sF.TryGetValue("riga", out var rF) ? rF : "?")}");
        if (sF.GetValueOrDefault("rigenerazione") != "9") Male("col ferro epico la rigenerazione deve essere 9 millesimi al secondo");
        if (vitaF <= vita0) Male($"col ferro la vita massima non e' salita ({vita0} -> {vitaF})");
        a.Admin("sethp 1000");
        bool piena = a.Attendi(() => VitaA() == vitaF, 4);
        Console.WriteLine($"  'sethp 1000' col ferro: vita vista {VitaA()}, massima {vitaF}");
        if (!piena) Male($"la vita non arriva alla massima nuova ({vitaF})");

        // 2. rigenerazione in combattimento: senza mangiare, circa 1,3 punti
        //    al secondo su 142. In cinque secondi almeno quattro punti.
        //    Le sonde nascono con la rigenerazione ferma (P4): prima si guarda
        //    che ferma lo sia davvero, poi la si accende per la misura e la si
        //    spegne di nuovo, per le parti dopo che leggono la vita esatta.
        if (a.ChiediAdmin("rigenerazione", "rigenerazione: ") != "no") Male("la sonda non nasce con la rigenerazione ferma");
        a.Admin("sethp 50");
        a.Attendi(() => VitaA() == 50f, 3);
        float ferma0 = VitaA();
        a.Attendi(() => false, 3.2);
        float ferma1 = VitaA();
        Console.WriteLine($"  rigenerazione ferma: vita {ferma0} -> {ferma1} in 3 secondi col ferro");
        if (ferma1 != ferma0) Male("con la rigenerazione ferma la vita e' salita lo stesso");
        if (a.ChiediAdmin("rigenerazione si", "rigenerazione: ") != "si") Male("'rigenerazione si' non accende la rigenerazione");
        float da = VitaA();
        a.Attendi(() => false, 5.2);
        float poi = VitaA();
        if (a.ChiediAdmin("rigenerazione no", "rigenerazione: ") != "no") Male("'rigenerazione no' non ferma la rigenerazione");
        Console.WriteLine($"  rigenerazione: vita {da} -> {poi} in 5 secondi (attesi circa {vitaF * 9 / 1000.0 * 5:F1} punti)");
        if (poi - da < 4f || poi - da > 9f) Male("la rigenerazione del ferro non ha reso quello che deve");

        // 3. togliendo la corazza la vita massima scende, e la vita con lei.
        a.Admin("sethp 1000");
        a.Attendi(() => VitaA() == vitaF, 3);
        if (!Equip(0, EquipSlot.Body)) Male("togliere la corazza e' stato rifiutato");
        var sT = Stat();
        int vitaT = VitaMax(sT);
        bool scesa = a.Attendi(() => VitaA() == vitaT, 3);
        Console.WriteLine($"  tolta la corazza: vita massima {vitaF} -> {vitaT}, vita vista {VitaA()}");
        if (vitaT >= vitaF || !scesa) Male("tolta la corazza la vita non e' scesa alla massima nuova");
        SvestiA();

        // 4. danno: un colpo di spada di pietra (22, senza scudo) prima e dopo
        //    l'osso Epico. La differenza e quella del moltiplicatore.
        a.Admin("give StoneSword 1");
        a.Attendi(() => a.Quanti(ItemType.StoneSword) >= 1, 5);
        a.Dotazione(ItemType.StoneSword, ItemType.None, SideItemType.None);
        a.Posizione(0);
        a.Attendi(() => false, 0.4);
        float UnColpo()
        {
            b.Admin("sethp 1000");
            Piena();
            float prima = VitaB();
            a.Colpisci(0.2);
            a.Attendi(() => VitaB() < prima, 2);
            a.Attendi(() => false, 0.4);
            return prima - VitaB();
        }
        float colpo0 = UnColpo();
        ulong cOsso = Dai("dai BoneHood epico"), kOsso = Dai("dai BoneCuirass epico"), gOsso = Dai("dai BoneGreaves epico");
        if (!Equip(cOsso) || !Equip(kOsso) || !Equip(gOsso)) Male("indossare l'osso e' stato rifiutato");
        var sO = Stat();
        double dannoO = Molt(sO, "danno");
        float colpoO = UnColpo();
        int attesa = (int)Math.Round(22 * dannoO, MidpointRounding.AwayFromZero) - (int)Math.Round(22 * danno0, MidpointRounding.AwayFromZero);
        Console.WriteLine($"  danno: x{danno0:F2} -> x{dannoO:F2}, colpo di spada {colpo0} -> {colpoO} (differenza attesa {attesa})");
        if (Math.Abs(dannoO - danno0 - 0.21) > 0.011) Male("con tre pezzi d'osso epici il danno deve salire del 21%");
        if (colpo0 <= 0f || colpoO - colpo0 != attesa) Male("il colpo di spada non e' cresciuto come il moltiplicatore del danno");
        SvestiA();

        // 5. velocita d'attacco: a mani nude (pausa 20 tick) per sei secondi,
        //    prima e col cuoio Epico (+21%, pausa 17). Si contano i colpi dalla
        //    vita persa da B, colpo per colpo dello stesso valore.
        a.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        a.Posizione(0);
        a.Attendi(() => false, 0.4);
        int Colpi(double secondi)
        {
            b.Admin("sethp 1000");
            Piena();
            float prima = VitaB();
            a.Colpisci(0.2);   // un colpo solo, per sapere quanto vale
            a.Attendi(() => VitaB() < prima, 2);
            a.Attendi(() => false, 0.8);
            float uno = prima - VitaB();
            b.Admin("sethp 1000");
            Piena();
            prima = VitaB();
            a.Colpisci(secondi);
            a.Attendi(() => false, 0.5);
            float persa = prima - VitaB();
            return uno > 0f ? (int)Math.Round(persa / uno) : -1;
        }
        var sN = Stat();
        int colpiN = Colpi(6.0);
        ulong cCuoio = Dai("dai LeatherHood epico"), kCuoio = Dai("dai LeatherJacket epico"), gCuoio = Dai("dai LeatherBoots epico");
        if (!Equip(cCuoio) || !Equip(kCuoio) || !Equip(gCuoio)) Male("indossare il cuoio e' stato rifiutato");
        var sC = Stat();
        int colpiC = Colpi(6.0);
        Console.WriteLine($"  velocita d'attacco: x{Molt(sN, "attacco"):F2} pausa {Pausa(sN)} -> x{Molt(sC, "attacco"):F2} pausa {Pausa(sC)}; " +
                          $"colpi in 6 secondi {colpiN} -> {colpiC}");
        if (Math.Abs(Molt(sC, "attacco") - Molt(sN, "attacco") - 0.21) > 0.011) Male("con tre pezzi di cuoio epici la velocita d'attacco deve salire del 21%");
        if (Pausa(sC) >= Pausa(sN)) Male("col cuoio la pausa fra due colpi non si e' accorciata");
        if (colpiN <= 0 || colpiC <= colpiN) Male("col cuoio non si danno piu' colpi nello stesso tempo");

        // 6. velocita di movimento (A14e3): quattro secondi di cammino verso
        //    nord, misurati sulle istantanee, e ritorno al punto di partenza.
        //    Col cuoio Epico addosso +21%, senza niente il passo di base, col
        //    ferro Epico -6% (il malus del ferro non sale col grado). Il tempo
        //    del cammino lo decide la sonda, quindi le distanze ballano di un
        //    tick o due: si confrontano i rapporti con un margine.
        //    Si cammina sullo spawn della fazione 1, terra di sicuro: il punto
        //    della prova e' in mare (giro 1: 4 metri a nuoto invece di 20).
        double Passo(Dictionary<string, string> d) => Molt(d, "passo");
        const float CX = -400f, CZ = -400f;
        float Cammino()
        {
            a.Admin($"tp {CX.ToString(Inv)} {CZ.ToString(Inv)}");
            a.Attendi(() => Dist(a.X, a.Z, CX, CZ) < 2f, 5);
            a.Attendi(() => false, 0.3);
            float x0 = a.X, z0 = a.Z;
            a.Cammina(0f, 1f, 4.0);
            a.Attendi(() => false, 0.4);
            return Dist(a.X, a.Z, x0, z0);
        }
        var sMC = Stat();
        float mCuoio = Cammino();
        SvestiA();
        var sMN = Stat();
        float mNudo = Cammino();
        if (!Equip(elmo) || !Equip(corazza) || !Equip(gambali)) Male("indossare di nuovo il ferro e' stato rifiutato");
        var sMF = Stat();
        float mFerro = Cammino();
        double rCuoio = mNudo > 0f ? mCuoio / mNudo : 0, rFerro = mNudo > 0f ? mFerro / mNudo : 0;
        Console.WriteLine($"  movimento: nudo x{Molt(sMN, "movimento"):F2} passo {Passo(sMN):F2} -> {mNudo:F1} m; " +
                          $"cuoio x{Molt(sMC, "movimento"):F2} -> {mCuoio:F1} m (x{rCuoio:F2}); " +
                          $"ferro x{Molt(sMF, "movimento"):F2} -> {mFerro:F1} m (x{rFerro:F2}) in 4 secondi");
        if (Math.Abs(Molt(sMC, "movimento") - Molt(sMN, "movimento") - 0.21) > 0.011) Male("con tre pezzi di cuoio epici il movimento deve salire del 21%");
        if (Math.Abs(Molt(sMF, "movimento") - Molt(sMN, "movimento") + 0.06) > 0.011) Male("con tre pezzi di ferro il movimento deve scendere del 6%");
        if (mNudo < 15f || mNudo > 25f) Male($"in 4 secondi a passo pieno si fanno circa 20 metri, non {mNudo:F1}");
        if (rCuoio < 1.12 || rCuoio > 1.30) Male("col cuoio epico non si cammina il 21% piu' lontano");
        if (rFerro < 0.86 || rFerro > 0.99) Male("col ferro non si cammina un po' meno lontano");
        a.Admin($"tp {PX.ToString(Inv)} {PZ.ToString(Inv)}");
        a.Attendi(() => Dist(a.X, a.Z, PX, PZ) < 2f, 5);

        // Si lascia tutto pulito.
        SvestiA();
        a.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        a.Admin("clearinv");
        a.Admin("sethp 1000");
        b.Admin("sethp 1000");
        a.Attendi(() => false, 0.5);

        if (ok) Console.WriteLine("  OK: vita massima, rigenerazione, danno, velocita d'attacco e passo dell'armatura contano in partita");
        return ok;
    }

    // ---- usura che rende meno (A14f) ---------------------------------------------------
    //
    // Sopra la meta dell'usura un oggetto rende pieno; sotto, danno dell'arma e
    // bonus dell'armatura calano in linea retta fino al 70% vicino a zero
    // (decisioni/abilita.md sezione 5). Il comando "usura N" porta armi e
    // armatura di chi lo chiede a N% della loro usura massima. I colpi
    // ricevuti consumano un uso di ogni pezzo indossato.
    //
    // Numeri attesi con la spada di pietra senza scudo (22) e la sua usura 55:
    // al 60% (33) colpo pieno 22; al 25% (13) resa 0,84 e colpo 19; a 1 resa
    // 0,71 e colpo 16. Tre pezzi d'osso Epici al 25%: danno +21% -> +18%.
    static double ResaAttesa(int dur, int max)
    {
        double f = Math.Clamp((double)dur / max, 0, 1);
        return f >= 0.5 ? 1.0 : 0.7 + 0.3 * (f / 0.5);
    }

    public static bool UsuraCheRendeMeno(string tokenA, string tokenB, string host, int port)
    {
        Console.WriteLine("--- usura che rende meno: sotto la meta l'arma colpisce meno e l'armatura da meno");
        bool ok = true;
        void Male(string m) { Console.WriteLine("  FALLITA: " + m); ok = false; }

        using var a = new Sonda(tokenA, host, port);
        using var b = new Sonda(tokenB, host, port);
        if (!a.AttendiPronta() || !b.AttendiPronta()) { Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco"); return false; }
        if (!a.Attendi(() => a.Indossati != null, 10) || !b.Attendi(() => b.Indossati != null, 10))
        { Console.WriteLine("  FALLITA: all'ingresso non e' arrivata l'armatura"); return false; }

        Dictionary<string, string> Righe(Sonda s, string comando, string testa)
        {
            s.Attendi(() => false, 0.15);
            var r = s.ChiediAdmin(comando, testa);
            var d = new Dictionary<string, string>();
            if (r == null) return d;
            foreach (var p in r.Split(' ')) { int i = p.IndexOf('='); if (i > 0) d[p[..i]] = p[(i + 1)..]; }
            d["riga"] = r;
            return d;
        }
        // "StoneSword=13/55:x0.84" -> (13, 55)
        (int Dur, int Max) Usura(Dictionary<string, string> d, string tipo)
        {
            if (!d.TryGetValue(tipo, out var v)) return (-1, -1);
            var parti = v.Split(':')[0].Split('/');
            return parti.Length == 2 && int.TryParse(parti[0], out int u) && int.TryParse(parti[1], out int m) ? (u, m) : (-1, -1);
        }
        double Molt(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) && double.TryParse(v.TrimStart('x'), System.Globalization.NumberStyles.Float, Inv, out double x) ? x : -1;
        float VitaB() => a.Stato(b.Id).Vita;
        void Piena()
        {
            a.Attendi(() => { b.Attendi(() => true, 0.01); return VitaB() >= 100f; }, 3);
            a.Attendi(() => { b.Attendi(() => true, 0.01); return false; }, 0.3);
        }
        float UnColpo()
        {
            b.Admin("sethp 1000");
            Piena();
            float prima = VitaB();
            a.Colpisci(0.2);
            a.Attendi(() => { b.Attendi(() => true, 0.01); return VitaB() < prima; }, 2);
            a.Attendi(() => { b.Attendi(() => true, 0.01); return false; }, 0.4);
            return prima - VitaB();
        }
        ulong Dai(Sonda s, string riga)
        {
            s.Attendi(() => false, 0.15);
            var r = s.ChiediAdmin(riga, "dai: ");
            ulong id = r != null && ulong.TryParse(r.Split("id=").Last(), out var n) ? n : 0;
            bool arrivato = id != 0 && s.Attendi(() => { lock (s.Zaino) return s.Istanze.ContainsKey(id); }, 5);
            if (!arrivato) Male($"'{riga}' non ha dato il pezzo ({r ?? "nessuna risposta"})");
            return id;
        }
        bool Equip(Sonda s, ulong id, EquipSlot? togli = null)
        {
            s.Attendi(() => false, 0.15);
            if (togli is EquipSlot sl) s.Togli(sl); else s.Indossa(id);
            CommandResultMsg e = null;
            s.Attendi(() => (e = s.Esito(113)) != null, 5);
            return e != null && e.Success;
        }
        void Svesti(Sonda s)
        {
            foreach (var slot in new[] { EquipSlot.Head, EquipSlot.Body, EquipSlot.Legs })
                if (s.Indossati.Worn[(int)slot].Type != ItemType.None) Equip(s, 0, slot);
        }

        // Le due sonde una accanto all'altra, lontane da tutto, in fazioni diverse.
        const float PX = -150f, PZ = 250f;
        a.Admin("setfaction 1");
        b.Admin("setfaction 2");
        Svesti(a); Svesti(b);
        a.Admin("clearinv");
        b.Admin("clearinv");
        a.Admin($"tp {PX.ToString(Inv)} {PZ.ToString(Inv)}");
        b.Admin($"tp {(PX + 1f).ToString(Inv)} {PZ.ToString(Inv)}");
        if (!a.Attendi(() => Dist(a.X, a.Z, PX, PZ) < 2f, 8) || !b.Attendi(() => Dist(b.X, b.Z, PX + 1f, PZ) < 2f, 8))
        { Console.WriteLine("  FALLITA: le sonde non sono arrivate al punto della prova"); return false; }
        a.Cammina(0f, 0f, 0.1);
        b.Cammina(0f, 0f, 0.1);

        a.Admin("give StoneSword 1");
        a.Attendi(() => a.Quanti(ItemType.StoneSword) >= 1, 5);
        a.Dotazione(ItemType.StoneSword, ItemType.None, SideItemType.None);
        a.Posizione(0);
        a.Attendi(() => false, 0.4);

        // 1. l'arma: piena, al 60% ancora piena, al 25% e a 1 meno.
        float colpoPieno = UnColpo();
        double danno0 = Molt(Righe(a, "statistiche", "statistiche: "), "danno");
        Console.WriteLine($"  spada nuova: colpo {colpoPieno} (danno x{danno0:F2})");
        if (colpoPieno <= 0f || danno0 <= 0) { Console.WriteLine("  FALLITA: il colpo di prova non e' arrivato"); return false; }
        foreach (int perc in new[] { 60, 25, 0 })
        {
            var u = Righe(a, $"usura {perc}", "usura: ");
            var (dur, max) = Usura(u, "StoneSword");
            double resaServer = Molt(Righe(a, "statistiche", "statistiche: "), "resa");
            double resa = ResaAttesa(dur, max);
            float colpo = UnColpo();
            int atteso = Math.Max(1, (int)Math.Round(22 * danno0 * resa, MidpointRounding.AwayFromZero));
            Console.WriteLine($"  'usura {perc}': spada {dur}/{max}, resa x{resaServer:F2} (attesa x{resa:F2}), colpo {colpo} (atteso {atteso})");
            if (dur < 0) { Male($"'usura {perc}' non dice l'usura della spada ({u.GetValueOrDefault("riga", "nessuna risposta")})"); continue; }
            if (Math.Abs(resaServer - resa) > 0.011) Male($"al {perc}% la resa della spada deve essere x{resa:F2}");
            if (colpo != atteso) Male($"al {perc}% il colpo di spada deve fare {atteso}");
        }

        // 2. l'armatura: tre pezzi d'osso Epici danno +21% di danno; al 25%
        //    della loro usura +18% (21 x 0,85 = 17,85). La spada torna nuova.
        a.Admin("usura 100");
        ulong cOsso = Dai(a, "dai BoneHood epico"), kOsso = Dai(a, "dai BoneCuirass epico"), gOsso = Dai(a, "dai BoneGreaves epico");
        if (!ok) return false;
        if (!Equip(a, cOsso) || !Equip(a, kOsso) || !Equip(a, gOsso)) Male("indossare l'osso e' stato rifiutato");
        var arm0 = Righe(a, "armatura", "armatura: ");
        double dannoOsso = Molt(Righe(a, "statistiche", "statistiche: "), "danno");
        a.Admin("usura 25");
        var arm1 = Righe(a, "armatura", "armatura: ");
        double dannoLogoro = Molt(Righe(a, "statistiche", "statistiche: "), "danno");
        Console.WriteLine($"  osso epico nuovo: {arm0.GetValueOrDefault("bonus", "?")} danno x{dannoOsso:F2}; " +
                          $"al 25%: {arm1.GetValueOrDefault("bonus", "?")} danno x{dannoLogoro:F2}");
        if (!arm0.GetValueOrDefault("bonus", "").Contains("Damage+21")) Male("tre pezzi d'osso epici nuovi devono dare Damage+21");
        if (!arm1.GetValueOrDefault("bonus", "").Contains("Damage+18")) Male("tre pezzi d'osso epici al 25% devono dare Damage+18");
        if (Math.Abs(dannoOsso - danno0 - 0.21) > 0.011 || Math.Abs(dannoLogoro - danno0 - 0.18) > 0.011)
            Male("il moltiplicatore del danno non segue i bonus dell'armatura logora");
        // il colpo con spada e osso al 25%: un arrotondamento solo, alla fine.
        var uS = Usura(Righe(a, "usura", "usura: "), "StoneSword");
        float colpoLogoro = UnColpo();
        int attesoLogoro = Math.Max(1, (int)Math.Round(22 * dannoLogoro * ResaAttesa(uS.Dur, uS.Max), MidpointRounding.AwayFromZero));
        Console.WriteLine($"  spada e osso al 25%: colpo {colpoLogoro} (atteso {attesoLogoro})");
        if (colpoLogoro != attesoLogoro) Male($"con spada e osso al 25% il colpo deve fare {attesoLogoro}");
        a.Admin("usura 100");
        var arm2 = Righe(a, "armatura", "armatura: ");
        if (!arm2.GetValueOrDefault("bonus", "").Contains("Damage+21")) Male("riportata l'usura piena, l'osso deve tornare a Damage+21");
        Svesti(a);

        // 3. un colpo ricevuto consuma un uso di ogni pezzo indossato.
        ulong elmo = Dai(b, "dai IronHelm comune"), gamb = Dai(b, "dai LeatherBoots comune");
        if (!ok) return false;
        if (!Equip(b, elmo) || !Equip(b, gamb)) Male("indossare elmo e stivali su B e' stato rifiutato");
        var ub0 = Righe(b, "usura", "usura: ");
        UnColpo();
        var ub1 = Righe(b, "usura", "usura: ");
        var (e0, _) = Usura(ub0, "IronHelm"); var (e1, _) = Usura(ub1, "IronHelm");
        var (s0, _) = Usura(ub0, "LeatherBoots"); var (s1, _) = Usura(ub1, "LeatherBoots");
        Console.WriteLine($"  un colpo ricevuto: elmo {e0} -> {e1}, stivali {s0} -> {s1}");
        if (e0 <= 0 || e1 != e0 - 1 || s0 <= 0 || s1 != s0 - 1) Male("un colpo ricevuto deve consumare un uso di ogni pezzo indossato");

        // Si lascia tutto pulito.
        Svesti(a); Svesti(b);
        a.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        a.Admin("clearinv");
        b.Admin("clearinv");
        a.Admin("sethp 1000");
        b.Admin("sethp 1000");
        a.Attendi(() => false, 0.5);

        if (ok) Console.WriteLine("  OK: sotto la meta dell'usura arma e armatura rendono meno, fino al 70%, e i colpi ricevuti consumano l'armatura");
        return ok;
    }

    // ---- abilita della spada (A14g1) -------------------------------------------
    //
    // Il comando UseAbility nel tick, coi suoi controlli (sbloccata, nello
    // slot, famiglia e grado dell'arma, ricarica), la ricarica accorciata dal
    // livello e dall'osso, SetAbilitySlot, e le quattro base della spada:
    // Fendente pesante (+50%), Taglio largo (tutti davanti entro 2,5 m), Affondo
    // (4 m avanti e colpo), Parata (il prossimo colpo da davanti non fa niente).
    // Tre sonde: A con la spada in fazione 1, B e C in fazione 2.

    public static bool AbilitaDellaSpada(string tokenA, string tokenB, string tokenC, string host, int port)
    {
        Console.WriteLine("--- abilita della spada: uso, slot, ricariche e le quattro base");
        bool ok = true;
        void Male(string m) { Console.WriteLine("  FALLITA: " + m); ok = false; }

        using var a = new Sonda(tokenA, host, port);
        using var b = new Sonda(tokenB, host, port);
        using var c = new Sonda(tokenC, host, port);
        if (!a.AttendiPronta() || !b.AttendiPronta() || !c.AttendiPronta())
        { Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco"); return false; }
        a.Attendi(() => a.Indossati != null, 10);

        static byte[] P<T>(T v) => MessagePack.MessagePackSerializer.Serialize(v);
        bool Aspetta(Func<bool> cosa, double secondi) =>
            a.Attendi(() => { b.Attendi(() => true, 0.01); c.Attendi(() => true, 0.01); return cosa(); }, secondi);
        void Pausa(double secondi) => Aspetta(() => false, secondi);
        static string Visto(CommandResultMsg r) => r == null ? "nessuna risposta" : r.Success ? "riuscito" : "rifiutato " + r.ErrorCode;

        CommandResultMsg Usa(Sonda s, AbilityId id, float yaw)
        {
            Pausa(0.15);
            s.Comando(114, CommandType.UseAbility, P(new UseAbilityCommand { Ability = id, Yaw = yaw }));
            CommandResultMsg r = null;
            Aspetta(() => (r = s.Esito(114)) != null, 5);
            return r;
        }
        CommandResultMsg Slot(Sonda s, WeaponFamily f, byte slot, AbilityId id)
        {
            Pausa(0.15);
            s.Comando(115, CommandType.SetAbilitySlot, P(new SetAbilitySlotCommand
                { Kind = AbilitySlotKind.Weapon, Family = f, Slot = slot, Ability = id }));
            CommandResultMsg r = null;
            Aspetta(() => (r = s.Esito(115)) != null, 5);
            return r;
        }
        void Atteso(string cosa, CommandResultMsg r, ErrorCode atteso)
        {
            bool giusto = r != null && (atteso == ErrorCode.None ? r.Success : !r.Success && r.ErrorCode == atteso);
            Console.WriteLine($"  {cosa}: {Visto(r)}{(giusto ? "" : $" (atteso {(atteso == ErrorCode.None ? "riuscito" : atteso.ToString())})")}");
            if (!giusto) Male($"{cosa}: atteso {(atteso == ErrorCode.None ? "riuscito" : "rifiutato " + atteso)}");
        }
        string Stato(Sonda s) { Pausa(0.15); return SenzaRicariche(s.AbilitaInRiga()) ?? "(nessuna risposta)"; }
        float Vita(Sonda chi) => a.Stato(chi.Id).Vita;
        void Piena(params Sonda[] chi)
        {
            foreach (var s in chi) s.Admin("sethp 1000");
            Aspetta(() => chi.All(s => Vita(s) >= 100f), 3);
            Pausa(0.3);
        }
        /// Quanto ha perso chi, aspettando che il colpo si veda.
        float Perso(Sonda chi, float prima, double secondi = 1.5)
        {
            Aspetta(() => Vita(chi) < prima, secondi);
            Pausa(0.3);
            return prima - Vita(chi);
        }
        /// I millisecondi di ricarica che lo stato mandato dice per id.
        int MsRicarica(AbilityId id)
        {
            var st = a.Abilita;
            return st?.Cooldowns?.FirstOrDefault(x => x.Id == id)?.MsLeft ?? -1;
        }
        double Danno()
        {
            Pausa(0.15);
            var r = a.ChiediAdmin("statistiche", "statistiche: ") ?? "";
            foreach (var p in r.Split(' '))
                if (p.StartsWith("danno=") && double.TryParse(p[6..].TrimStart('x'), System.Globalization.NumberStyles.Float, Inv, out double x)) return x;
            return -1;
        }
        void Tp(Sonda s, float x, float z)
        {
            s.Admin($"tp {x.ToString(Inv)} {z.ToString(Inv)}");
            Aspetta(() => Dist(s.X, s.Z, x, z) < 0.5f, 6);
            s.Cammina(0f, 0f, 0.1);
        }
        bool Equip(ulong id, EquipSlot? togli = null)
        {
            Pausa(0.15);
            if (togli is EquipSlot sl) a.Togli(sl); else a.Indossa(id);
            CommandResultMsg e = null;
            Aspetta(() => (e = a.Esito(113)) != null, 5);
            return e != null && e.Success;
        }
        void Svesti()
        {
            if (a.Indossati == null) return;
            foreach (var sl in new[] { EquipSlot.Head, EquipSlot.Body, EquipSlot.Legs })
                if (a.Indossati.Worn[(int)sl].Type != ItemType.None) Equip(0, sl);
        }
        ulong Dai(string riga)
        {
            Pausa(0.15);
            var r = a.ChiediAdmin(riga, "dai: ");
            ulong id = r != null && ulong.TryParse(r.Split("id=").Last(), out var n) ? n : 0;
            if (id == 0 || !Aspetta(() => { lock (a.Zaino) return a.Istanze.ContainsKey(id); }, 5))
                Male($"'{riga}' non ha dato il pezzo ({r ?? "nessuna risposta"})");
            return id;
        }

        // Lontano da tutto: lo stesso punto delle prove dell'armatura. C sta
        // lontano finche' non serve, cosi' il colpo singolo prende sempre B.
        const float PX = -150f, PZ = 250f;
        a.Admin("setfaction 1"); b.Admin("setfaction 2"); c.Admin("setfaction 2");
        Svesti();
        foreach (var s in new[] { a, b, c }) { s.Admin("clearinv"); s.Admin("abilita azzera"); }
        a.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        b.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        Tp(a, PX, PZ); Tp(b, PX + 1f, PZ); Tp(c, PX - 8f, PZ);
        a.Admin("give StoneSword 1");
        Aspetta(() => a.Quanti(ItemType.StoneSword) >= 1 || a.Istanze.Values.Any(v => v.Tipo == ItemType.StoneSword), 5);
        a.Dotazione(ItemType.StoneSword, ItemType.None, SideItemType.None);
        a.Posizione(0);
        Pausa(0.4);
        Piena(b, c);

        // 1. i rifiuti, e le quattro base che si sbloccano alla prima prova.
        Atteso("Ferita (Comune) con una spada Base", Usa(a, AbilityId.SwordWound, 90f), ErrorCode.AbilityNotUsable);
        string st1 = Stato(a);
        Console.WriteLine($"  stato dopo il primo uso: {st1}");
        if (!st1.StartsWith("sbloccate=100:1,101:1,102:1,103:1 arma=Sword:100/101/102 "))
            Male("alla prima prova con la spada si devono sbloccare le quattro base, le prime tre negli slot");
        Atteso("Lancio (lancia) con la spada", Usa(a, AbilityId.SpearThrow, 90f), ErrorCode.AbilityNotUsable);
        Atteso("Taglio largo fuori dagli slot", Usa(a, AbilityId.SwordWideCut, 90f), ErrorCode.AbilityNotUsable);
        if (Vita(b) < 100f) Male("un'abilita rifiutata ha fatto danno");

        // 2. gli slot.
        Atteso("Taglio largo nello slot 0", Slot(a, WeaponFamily.Sword, 0, AbilityId.SwordWideCut), ErrorCode.None);
        Atteso("Fendente nello slot 1", Slot(a, WeaponFamily.Sword, 1, AbilityId.SwordHeavySlash), ErrorCode.None);
        Atteso("Parata negli slot della lancia", Slot(a, WeaponFamily.Spear, 0, AbilityId.SwordParry), ErrorCode.AbilityNotUsable);
        Atteso("slot numero 5", Slot(a, WeaponFamily.Sword, 5, AbilityId.SwordParry), ErrorCode.InvalidCommand);
        Atteso("Fendente nello slot 2 (lascia l'1)", Slot(a, WeaponFamily.Sword, 2, AbilityId.SwordHeavySlash), ErrorCode.None);
        Atteso("Affondo nello slot 1", Slot(a, WeaponFamily.Sword, 1, AbilityId.SwordLunge), ErrorCode.None);
        string st2 = Stato(a);
        Console.WriteLine($"  slot: {st2}");
        if (!st2.Contains(" arma=Sword:103/102/100 ")) Male("gli slot della spada devono essere 103/102/100");
        bool statoArrivato = Aspetta(() => a.Abilita?.WeaponSlots?.Any(w => w.Family == WeaponFamily.Sword
            && w.Abilities.SequenceEqual(new[] { AbilityId.SwordWideCut, AbilityId.SwordLunge, AbilityId.SwordHeavySlash })) == true, 3);
        if (!statoArrivato) Male("lo stato mandato al client non ha gli slot nuovi");

        // 3. Fendente pesante: +50%, 22 -> 33; poi in ricarica 6 s.
        Piena(b);
        float prima = Vita(b);
        Atteso("Fendente pesante su B", Usa(a, AbilityId.SwordHeavySlash, 90f), ErrorCode.None);
        float f1 = Perso(b, prima);
        int ms1 = -1; Aspetta(() => (ms1 = MsRicarica(AbilityId.SwordHeavySlash)) > 0, 2);
        Console.WriteLine($"  Fendente livello 1: B perde {f1} (atteso 33), ricarica {ms1} ms (attesi 6000)");
        if (f1 != 33f) Male("il Fendente pesante deve fare 33 (22 +50%)");
        if (Math.Abs(ms1 - 6000) > 100) Male("la ricarica del Fendente al livello 1 deve essere 6 s");
        Atteso("Fendente ancora in ricarica", Usa(a, AbilityId.SwordHeavySlash, 90f), ErrorCode.AbilityOnCooldown);

        // 4. livello 3: +50% x1,3 = +65%, 22 -> 36; ricarica -20%, 4,8 s.
        Pausa(0.15); a.Admin("abilita livello 100 3"); Pausa(0.15); a.Admin("ricarica");
        Piena(b); prima = Vita(b);
        Atteso("Fendente livello 3", Usa(a, AbilityId.SwordHeavySlash, 90f), ErrorCode.None);
        float f3 = Perso(b, prima);
        int ms3 = -1; Aspetta(() => (ms3 = MsRicarica(AbilityId.SwordHeavySlash)) is > 0 and < 5500, 2);
        Console.WriteLine($"  Fendente livello 3: B perde {f3} (atteso 36), ricarica {ms3} ms (attesi 4800)");
        if (f3 != 36f) Male("il Fendente al livello 3 deve fare 36 (22 +65%)");
        if (Math.Abs(ms3 - 4800) > 100) Male("la ricarica del Fendente al livello 3 deve essere 4,8 s");

        // 5. l'osso Epico: danno +21% e ricariche -21%. Livello 1: 6 x 0,79 = 4,74 s.
        Pausa(0.15); a.Admin("abilita livello 100 1"); Pausa(0.15); a.Admin("ricarica");
        ulong o1 = Dai("dai BoneHood epico"), o2 = Dai("dai BoneCuirass epico"), o3 = Dai("dai BoneGreaves epico");
        if (!Equip(o1) || !Equip(o2) || !Equip(o3)) Male("indossare l'osso e' stato rifiutato");
        bool cdr = Aspetta(() => a.Statistiche?.CooldownReductionPercent == 21, 3);
        double molt = Danno();
        Piena(b); prima = Vita(b);
        Atteso("Fendente con l'osso", Usa(a, AbilityId.SwordHeavySlash, 90f), ErrorCode.None);
        float fo = Perso(b, prima);
        int mso = -1; Aspetta(() => (mso = MsRicarica(AbilityId.SwordHeavySlash)) is > 0 and < 5500, 2);
        int attesoOsso = (int)Math.Round(22 * molt * 1.5, MidpointRounding.AwayFromZero);
        Console.WriteLine($"  con l'osso epico (ricariche {a.Statistiche?.CooldownReductionPercent}%, danno x{molt:F2}): " +
                          $"B perde {fo} (atteso {attesoOsso}), ricarica {mso} ms (attesi 4740)");
        if (!cdr) Male("tre pezzi d'osso epici devono dare ricariche -21%");
        if (fo != attesoOsso) Male($"il Fendente con l'osso deve fare {attesoOsso}");
        if (Math.Abs(mso - 4740) > 100) Male("con l'osso epico la ricarica del Fendente deve essere 4,74 s");
        Svesti();
        Aspetta(() => a.Statistiche?.CooldownReductionPercent == 0, 3);

        // 6. Taglio largo: B davanti a 1 m e C dietro a 1 m. Solo B, 22.
        Tp(c, PX - 1f, PZ);
        Piena(b, c);
        float pb = Vita(b), pc = Vita(c);
        Atteso("Taglio largo verso B", Usa(a, AbilityId.SwordWideCut, 90f), ErrorCode.None);
        float tb = Perso(b, pb), tc = pc - Vita(c);
        Console.WriteLine($"  Taglio largo: B davanti perde {tb} (atteso 22), C dietro perde {tc} (atteso 0)");
        if (tb != 22f) Male("il Taglio largo deve fare 22 a chi sta davanti");
        if (tc != 0f) Male("il Taglio largo non deve prendere chi sta dietro");
        // ...e girandosi prende C e non B.
        Pausa(0.15); a.Admin("ricarica");
        Piena(b, c); pb = Vita(b); pc = Vita(c);
        Atteso("Taglio largo verso C", Usa(a, AbilityId.SwordWideCut, 270f), ErrorCode.None);
        tc = Perso(c, pc); tb = pb - Vita(b);
        Console.WriteLine($"  Taglio largo girato: C perde {tc} (atteso 22), B perde {tb} (atteso 0)");
        if (tc != 22f || tb != 0f) Male("il Taglio largo girato deve prendere C e non B");

        // 7. Affondo: B a 5 m, A scatta di 4 m e lo colpisce. C resta dietro.
        Tp(c, PX - 8f, PZ);
        Tp(b, PX + 5f, PZ);
        Piena(b); pb = Vita(b);
        Atteso("Affondo verso B", Usa(a, AbilityId.SwordLunge, 90f), ErrorCode.None);
        float lb = Perso(b, pb);
        Aspetta(() => Dist(a.X, a.Z, PX + 4f, PZ) < 0.3f, 2);
        Console.WriteLine($"  Affondo: A arriva a ({a.X:F2}, {a.Z:F2}) (attesa {PX + 4f}), B perde {lb} (atteso 22)");
        if (Dist(a.X, a.Z, PX + 4f, PZ) >= 0.3f) Male("l'Affondo deve portare A 4 m avanti");
        if (lb != 22f) Male("l'Affondo deve colpire B per 22");

        // 8. Parata: B con la spada para il primo colpo di A, non il secondo; e
        //    non para un colpo da dietro.
        b.Admin("give StoneSword 1");
        Aspetta(() => b.Istanze.Values.Any(v => v.Tipo == ItemType.StoneSword), 5);
        b.Dotazione(ItemType.StoneSword, ItemType.None, SideItemType.None);
        b.Posizione(0);
        Pausa(0.4);
        Piena(b); pb = Vita(b);
        Atteso("Parata di B verso A", Usa(b, AbilityId.SwordParry, 270f), ErrorCode.None);
        a.Colpisci(0.2);
        Pausa(0.2);
        float p1 = pb - Vita(b);
        float dopoParata = Vita(b);   // prima del secondo colpo: Colpisci aspetta mezzo secondo
        a.Colpisci(0.2);
        float p2 = Perso(b, dopoParata);
        Console.WriteLine($"  Parata: primo colpo di A toglie {p1} (atteso 0), il secondo {p2} (atteso 22)");
        if (p1 != 0f) Male("la Parata deve fermare il primo colpo da davanti");
        if (p2 != 22f) Male("la Parata ferma un colpo solo: il secondo deve fare 22");
        Pausa(0.15); b.Admin("ricarica");
        Piena(b); pb = Vita(b);
        Atteso("Parata di B girato di spalle", Usa(b, AbilityId.SwordParry, 90f), ErrorCode.None);
        a.Colpisci(0.2);
        float p3 = Perso(b, pb);
        Console.WriteLine($"  Parata di spalle: il colpo di A toglie {p3} (atteso 22)");
        if (p3 != 22f) Male("la Parata non deve fermare un colpo da dietro");

        // Si lascia tutto pulito.
        foreach (var s in new[] { a, b, c }) { s.Admin("abilita azzera"); s.Admin("clearinv"); s.Admin("sethp 1000"); }
        a.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        b.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        Pausa(0.5);

        if (ok) Console.WriteLine("  OK: abilita della spada usate nel tick, coi controlli, gli slot e le ricariche; Fendente, Taglio largo, Affondo e Parata fanno cio' che devono");
        return ok;
    }

    // ---- effetti a tempo (A14g1b) ----------------------------------------------
    //
    // Gli effetti che le abilita lasceranno sui bersagli, messi col comando di
    // prova "effetto": stordimento (fermo, niente colpi ne abilita),
    // immobilita (fermo ma colpisce), rallentamento (vale il piu' forte),
    // danno nel tempo (un colpo al secondo, uccide come il colpo di chi l'ha
    // inflitto), statistica a tempo (entra nei totali e va al client), danno
    // subito a tempo; e su una creatura stordimento, rallentamento e danno nel
    // tempo che uccide e da' il bottino a chi l'ha inflitto. A in fazione 1 con
    // la spada di pietra (22 a colpo), B in fazione 2.

    public static bool EffettiATempo(string tokenA, string tokenB, string host, int port)
    {
        Console.WriteLine("--- effetti a tempo: stordimento, immobilita, rallentamento, danno nel tempo, statistiche e danno subito");
        bool ok = true;
        void Male(string m) { Console.WriteLine("  FALLITA: " + m); ok = false; }

        using var a = new Sonda(tokenA, host, port);
        using var b = new Sonda(tokenB, host, port);
        if (!a.AttendiPronta() || !b.AttendiPronta())
        { Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco"); return false; }

        // Lontano da tutto, come le prove dell'armatura (e' mare: per misurare
        // il passo si va a terra, vedi Passo).
        const float PX = -150f, PZ = 250f;
        static byte[] P<T>(T v) => MessagePack.MessagePackSerializer.Serialize(v);
        bool Aspetta(Func<bool> cosa, double secondi) =>
            a.Attendi(() => { b.Attendi(() => true, 0.01); return cosa(); }, secondi);
        void Pausa(double secondi) => Aspetta(() => false, secondi);
        float Vita(Sonda chi) => a.Stato(chi.Id).Vita;
        void Tp(Sonda s, float x, float z)
        {
            s.Admin($"tp {x.ToString(Inv)} {z.ToString(Inv)}");
            Aspetta(() => Dist(s.X, s.Z, x, z) < 0.5f, 6);
            s.Cammina(0f, 0f, 0.1);
        }
        void Piena(params Sonda[] chi)
        {
            foreach (var s in chi) s.Admin("sethp 1000");
            Aspetta(() => chi.All(s => Vita(s) >= 100f), 3);
            Pausa(0.3);
        }
        float Perso(Sonda chi, float prima, double secondi = 1.5)
        {
            Aspetta(() => Vita(chi) < prima, secondi);
            Pausa(0.3);
            return prima - Vita(chi);
        }
        string Effetto(Sonda s, string riga) { Pausa(0.15); return s.ChiediAdmin("effetto " + riga, "effetto: ") ?? "(nessuna risposta)"; }
        static string Campo(string riga, string nome)
        {
            int i = riga.IndexOf(nome + "=", StringComparison.Ordinal);
            if (i < 0) return null;
            int f = riga.IndexOf(' ', i);
            return f < 0 ? riga[(i + nome.Length + 1)..] : riga[(i + nome.Length + 1)..f];
        }
        static (float X, float Z) Pos(string riga)
        {
            var p = Campo(riga, "pos")?.Split(',');
            return p != null && p.Length == 2 && float.TryParse(p[0], System.Globalization.NumberStyles.Float, Inv, out var x)
                   && float.TryParse(p[1], System.Globalization.NumberStyles.Float, Inv, out var z) ? (x, z) : (float.NaN, float.NaN);
        }
        /// Quanto si sposta s camminando verso +Z per un secondo. Si cammina
        /// sullo spawn della fazione 1, terra di sicuro (il punto della prova
        /// e' in mare: si nuoterebbe a 1 m/s), poi si torna al punto.
        float Passo(Sonda s)
        {
            Tp(s, -400f, -400f);
            float x0 = s.X, z0 = s.Z;
            s.Cammina(0f, 1f, 1.0);
            Pausa(0.3);
            float d = Dist(s.X, s.Z, x0, z0);
            Tp(s, PX, PZ);
            return d;
        }
        CommandResultMsg Usa(Sonda s, AbilityId id, float yaw)
        {
            Pausa(0.15);
            s.Comando(116, CommandType.UseAbility, P(new UseAbilityCommand { Ability = id, Yaw = yaw }));
            CommandResultMsg r = null;
            Aspetta(() => (r = s.Esito(116)) != null, 5);
            return r;
        }

        a.Admin("setfaction 1"); b.Admin("setfaction 2");
        foreach (var s in new[] { a, b }) { s.Admin("clearinv"); s.Admin("abilita azzera"); s.Admin("effetto azzera"); }
        b.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        Tp(a, PX, PZ); Tp(b, PX + 1f, PZ);
        a.Admin("give StoneSword 1");
        Aspetta(() => a.Istanze.Values.Any(v => v.Tipo == ItemType.StoneSword), 5);
        a.Dotazione(ItemType.StoneSword, ItemType.None, SideItemType.None);
        a.Posizione(0);
        Pausa(0.4);
        Piena(a, b);

        // 0. il passo normale, come riferimento (avanti e indietro).
        float normale = Passo(a);
        Console.WriteLine($"  passo normale in un secondo: {normale:F2} m");
        if (normale < 3f) Male("il passo normale misurato e' troppo corto per la prova");

        // 1. stordimento: fermo, niente colpi ne abilita; dopo, tutto torna.
        // 6 s: abilita, colpo e passo (col viaggio a terra) ci devono stare dentro tutti e tre (con
        // 2,5 s il Fendente arrivava a stordimento finito).
        string st = Effetto(a, "stordisci 6");
        Console.WriteLine($"  stordisci: {st}");
        if (!st.StartsWith("ok ") || !st.Contains("stordito")) Male("'effetto stordisci' non ha stordito A");
        var usoStordito = Usa(a, AbilityId.SwordHeavySlash, 90f);
        float prima = Vita(b);
        a.Colpisci(0.3);
        Pausa(0.3);
        float colpoStordito = prima - Vita(b);
        float fermo = Passo(a);
        Console.WriteLine($"  da stordito: passo {fermo:F2} m (atteso 0), colpo su B {colpoStordito} (atteso 0), " +
                          $"Fendente {(usoStordito == null ? "nessuna risposta" : usoStordito.Success ? "riuscito" : "rifiutato " + usoStordito.ErrorCode)} (atteso AbilityNotUsable)");
        if (fermo > 0.2f) Male("chi e' stordito non deve muoversi");
        if (colpoStordito != 0f) Male("chi e' stordito non deve colpire");
        if (usoStordito == null || usoStordito.Success || usoStordito.ErrorCode != ErrorCode.AbilityNotUsable)
            Male("chi e' stordito non deve usare abilita");
        Aspetta(() => !(Effetto(a, "stato")).Contains("stordito"), 7);
        Tp(a, PX, PZ);
        prima = Vita(b);
        a.Colpisci(0.2);
        float colpoDopo = Perso(b, prima);
        Console.WriteLine($"  finito lo stordimento: colpo su B {colpoDopo} (atteso 22)");
        if (colpoDopo != 22f) Male("finito lo stordimento il colpo deve tornare a 22");

        // 2. immobilita: fermo ma colpisce.
        Piena(b);
        st = Effetto(a, "immobile 4");
        float immobile = Passo(a);
        prima = Vita(b);
        a.Colpisci(0.2);
        float colpoImmobile = Perso(b, prima);
        Console.WriteLine($"  immobile: {st}; passo {immobile:F2} m (atteso 0), colpo su B {colpoImmobile} (atteso 22)");
        if (immobile > 0.2f) Male("chi e' immobile non deve muoversi");
        if (colpoImmobile != 22f) Male("chi e' immobile deve poter colpire");
        Aspetta(() => !(Effetto(a, "stato")).Contains("immobile"), 5);
        Tp(a, PX, PZ);

        // 3. rallentamento: 50% e 30% insieme, vale il 50%.
        Effetto(a, "rallenta 30 4");
        st = Effetto(a, "rallenta 50 4");
        float lento = Passo(a);
        double rapporto = lento / normale;
        Console.WriteLine($"  rallentato 50% e 30%: {st}; passo {lento:F2} m, {rapporto:P0} del normale (atteso circa 50%)");
        if (rapporto < 0.35 || rapporto > 0.65) Male("con un rallentamento del 50% e uno del 30% il passo deve essere circa la meta'");
        Effetto(a, "azzera");
        Tp(a, PX, PZ);

        // 4. danno nel tempo da A su B: 30 in 6 s, 5 al secondo.
        Piena(b);
        prima = Vita(b);
        st = Effetto(a, $"sanguina 30 6 {b.Id}");
        Pausa(1.4);
        float dopoUno = prima - Vita(b);
        Aspetta(() => prima - Vita(b) >= 30f, 7);
        Pausa(1.3);
        float dopoTutto = prima - Vita(b);
        string stB = Effetto(a, $"stato {b.Id}");
        Console.WriteLine($"  sanguina 30 in 6 s: {st}; dopo un secondo e poco B ha perso {dopoUno} (atteso 5), alla fine {dopoTutto} (atteso 30); poi: {stB}");
        if (dopoUno != 5f) Male("il danno nel tempo deve togliere 5 al primo secondo");
        if (dopoTutto != 30f) Male("il danno nel tempo deve togliere 30 in tutto, non di piu'");
        if (!stB.Contains("effetti=nessuno")) Male("finito il danno nel tempo non deve restare niente");

        // 5. un danno nel tempo uccide come il colpo di chi l'ha inflitto, e la
        //    morte toglie gli effetti.
        b.Admin("sethp 8");
        Aspetta(() => Vita(b) is > 0f and <= 8f, 3);
        Effetto(a, $"stordisci 10 {b.Id}");
        Effetto(a, $"sanguina 20 2 {b.Id}");
        bool morto = Aspetta(() => a.Stato(b.Id).Morto, 4);
        Console.WriteLine($"  B con 8 di vita e 20 da perdere in 2 s: {(morto ? "morto" : "vivo")} (atteso morto)");
        if (!morto) Male("il danno nel tempo deve poter uccidere");
        // Rinasce allo stendardo, magari fuori dalla vista di A: si guarda con
        // gli occhi di B.
        Aspetta(() => { var v = b.Stato(b.Id); return !v.Morto && v.Vita > 0f; }, 6);
        Pausa(0.5);
        stB = Effetto(a, $"stato {b.Id}");
        Console.WriteLine($"  B rinato: {stB}");
        if (!stB.Contains("effetti=nessuno")) Male("morendo gli effetti devono andare via (lo stordimento di 10 s compreso)");
        Tp(b, PX + 1f, PZ);

        // 6. statistica a tempo: danno +15% per 2 s, 22 -> 25, al client
        //    nell'elenco dei modificatori; poi torna 22.
        Piena(b);
        st = Effetto(a, "stat Damage 15 2");
        bool alClient = Aspetta(() => a.Statistiche?.DamagePercent == 15
            && a.Statistiche.Modifiers?.Any(m => m.Origin == "prova" && m.Stat == StatKind.Damage && m.SecondsLeft is >= 1 and <= 2) == true, 3);
        prima = Vita(b);
        a.Colpisci(0.2);
        float conBuff = Perso(b, prima);
        bool scaduto = Aspetta(() => a.Statistiche?.DamagePercent == 0
            && a.Statistiche.Modifiers?.Any(m => m.Origin == "prova") != true, 4);
        Pausa(0.3);
        prima = Vita(b);
        a.Colpisci(0.2);
        float senzaBuff = Perso(b, prima);
        Console.WriteLine($"  danno +15% per 2 s: {st}; al client {(alClient ? "si" : "no")}, colpo {conBuff} (atteso 25), " +
                          $"scaduto {(scaduto ? "si" : "no")}, colpo dopo {senzaBuff} (atteso 22)");
        if (!alClient) Male("la statistica a tempo deve arrivare al client, nei totali e nell'elenco con i secondi");
        if (conBuff != 25f) Male("con danno +15% la spada di pietra deve fare 25");
        if (!scaduto) Male("la statistica a tempo deve scadere e il client saperlo");
        if (senzaBuff != 22f) Male("scaduta la statistica il colpo deve tornare a 22");

        // 7. danno subito a tempo: B +50% per 3 s, 22 -> 33; azzerato, 22.
        Piena(b);
        st = Effetto(b, "subito 50 3");
        prima = Vita(b);
        a.Colpisci(0.2);
        float subito = Perso(b, prima);
        Effetto(b, "azzera");
        Pausa(0.3);
        prima = Vita(b);
        a.Colpisci(0.2);
        float azzerato = Perso(b, prima);
        Console.WriteLine($"  B prende +50%: {st}; colpo {subito} (atteso 33), dopo 'azzera' {azzerato} (atteso 22)");
        if (subito != 33f) Male("con danno subito +50% il colpo da 22 deve fare 33");
        if (azzerato != 22f) Male("'effetto azzera' deve togliere il danno subito");

        // 8. su una creatura: un lupo stordito non si muove e non morde; un
        //    lupo rallentato del 90% quasi non si muove; un danno nel tempo
        //    inflitto da A lo uccide e il bottino va ad A.
        Tp(b, PX + 40f, PZ);
        Piena(a);
        a.Admin("spawn wolf");
        Pausa(0.5);
        string lupo = Effetto(a, "stato vicino");
        string idLupo = Campo(lupo, "id");
        Console.WriteLine($"  il lupo: {lupo}");
        if (Campo(lupo, "tipo") != "Wolf" || idLupo == null) { Male("il lupo fatto nascere non si trova"); idLupo = "0"; }
        a.ChiediAdmin("segnoprova no", "segno di prova: ");
        st = Effetto(a, $"stordisci 3 {idLupo}");
        var l0 = Pos(Effetto(a, $"stato {idLupo}"));
        prima = Vita(a);
        Pausa(2.0);
        var l1 = Pos(Effetto(a, $"stato {idLupo}"));
        float morsoStordito = prima - Vita(a);
        float spostoStordito = Dist(l0.X, l0.Z, l1.X, l1.Z);
        Console.WriteLine($"  lupo stordito 3 s: {st}; in 2 s si sposta {spostoStordito:F2} m (atteso 0), A perde {morsoStordito} (atteso 0)");
        if (!(spostoStordito < 0.05f)) Male("un lupo stordito non deve muoversi");
        if (morsoStordito != 0f) Male("un lupo stordito non deve mordere");
        bool morde = Aspetta(() => Vita(a) < prima, 5);
        Console.WriteLine($"  finito lo stordimento il lupo morde: {(morde ? "si" : "no")}");
        if (!morde) Male("finito lo stordimento il lupo deve tornare a mordere");

        // rallentato: A si allontana di 10 m, il lupo la insegue.
        Piena(a);
        Tp(a, l1.X + 10f, l1.Z);
        Effetto(a, $"rallenta 90 3 {idLupo}");
        var r0 = Pos(Effetto(a, $"stato {idLupo}"));
        Pausa(1.0);
        var r1 = Pos(Effetto(a, $"stato {idLupo}"));
        float spostoLento = Dist(r0.X, r0.Z, r1.X, r1.Z);
        Aspetta(() => !Effetto(a, $"stato {idLupo}").Contains("rallentato"), 4);
        Tp(a, r1.X + 10f, r1.Z);
        var n0 = Pos(Effetto(a, $"stato {idLupo}"));
        Pausa(1.0);
        var n1 = Pos(Effetto(a, $"stato {idLupo}"));
        float spostoNormale = Dist(n0.X, n0.Z, n1.X, n1.Z);
        Console.WriteLine($"  lupo rallentato del 90%: in un secondo circa {spostoLento:F2} m; senza rallentamento {spostoNormale:F2} m");
        if (!(spostoLento < 1.5f)) Male("un lupo rallentato del 90% deve muoversi appena");
        if (!(spostoNormale > 2.5f)) Male("finito il rallentamento il lupo deve tornare al suo passo");

        // danno nel tempo che uccide: il bottino del lupo va ad A.
        a.ChiediAdmin("segnoprova si", "segno di prova: ");
        int pelliPrima = a.Quanti(ItemType.RawHide);
        Effetto(a, $"sanguina 1000 2 {idLupo}");
        bool lupoMorto = Aspetta(() => Effetto(a, $"stato {idLupo}").StartsWith("errore"), 4);
        bool pelli = Aspetta(() => a.Quanti(ItemType.RawHide) > pelliPrima, 3);
        Console.WriteLine($"  lupo con 1000 da perdere: {(lupoMorto ? "morto" : "vivo")} (atteso morto), pelli ad A {pelliPrima} -> {a.Quanti(ItemType.RawHide)}");
        if (!lupoMorto) Male("il danno nel tempo deve poter uccidere una creatura");
        if (!pelli) Male("il bottino della creatura uccisa dal danno nel tempo deve andare a chi l'ha inflitto");
        if (idLupo != "0") a.Admin($"remove {idLupo}");   // vivo o morto, non resta nel mondo di prova

        // Si lascia tutto pulito.
        foreach (var s in new[] { a, b }) { s.Admin("effetto azzera"); s.Admin("abilita azzera"); s.Admin("clearinv"); s.Admin("sethp 1000"); }
        a.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        Pausa(0.5);

        if (ok) Console.WriteLine("  OK: stordimento, immobilita, rallentamento, danno nel tempo, statistiche e danno subito a tempo fanno cio' che devono, su giocatori e creature");
        return ok;
    }

    // ---- il resto della spada (A14g2) ------------------------------------------
    //
    // Le tre Comuni, le tre Rare e le due Epiche della spada, con una spada di
    // pietra Epica (22 a colpo, il grado non cambia il danno): Grido di
    // battaglia (+15% danno), Ferita (colpo e sanguinamento del 30%), Colpo di
    // pomo (colpo e stordimento), Spinta (3 m indietro e rallentato del 50%),
    // Vortice (tre colpi a tutti attorno), Contrattacco (chi colpisce prende
    // il doppio), Esecuzione (solo sotto il 30% di vita, triplo danno), Lama
    // assetata (cura del 25% del danno inflitto). A in fazione 1, B e C in 2.

    public static bool RestoDellaSpada(string tokenA, string tokenB, string tokenC, string host, int port)
    {
        Console.WriteLine("--- resto della spada: Comuni, Rare ed Epiche");
        bool ok = true;
        void Male(string m) { Console.WriteLine("  FALLITA: " + m); ok = false; }

        using var a = new Sonda(tokenA, host, port);
        using var b = new Sonda(tokenB, host, port);
        using var c = new Sonda(tokenC, host, port);
        if (!a.AttendiPronta() || !b.AttendiPronta() || !c.AttendiPronta())
        { Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco"); return false; }

        const float PX = -150f, PZ = 250f;   // lo stesso punto delle altre prove della spada
        static byte[] P<T>(T v) => MessagePack.MessagePackSerializer.Serialize(v);
        bool Aspetta(Func<bool> cosa, double secondi) =>
            a.Attendi(() => { b.Attendi(() => true, 0.01); c.Attendi(() => true, 0.01); return cosa(); }, secondi);
        void Pausa(double secondi) => Aspetta(() => false, secondi);
        static string Visto(CommandResultMsg r) => r == null ? "nessuna risposta" : r.Success ? "riuscito" : "rifiutato " + r.ErrorCode;
        float Vita(Sonda chi) => a.Stato(chi.Id).Vita;
        void Tp(Sonda s, float x, float z)
        {
            s.Admin($"tp {x.ToString(Inv)} {z.ToString(Inv)}");
            Aspetta(() => Dist(s.X, s.Z, x, z) < 0.5f, 6);
            s.Cammina(0f, 0f, 0.1);
        }
        void Piena(params Sonda[] chi)
        {
            foreach (var s in chi) s.Admin("sethp 1000");
            Aspetta(() => chi.All(s => Vita(s) >= 100f), 3);
            Pausa(0.3);
        }
        float Perso(Sonda chi, float prima, double secondi = 1.5)
        {
            Aspetta(() => Vita(chi) < prima, secondi);
            Pausa(0.3);
            return prima - Vita(chi);
        }
        string Effetto(Sonda s, string riga) { Pausa(0.15); return s.ChiediAdmin("effetto " + riga, "effetto: ") ?? "(nessuna risposta)"; }
        static (float X, float Z) Pos(string riga)
        {
            int i = riga.IndexOf("pos=", StringComparison.Ordinal);
            if (i < 0) return (float.NaN, float.NaN);
            int f = riga.IndexOf(' ', i);
            var p = (f < 0 ? riga[(i + 4)..] : riga[(i + 4)..f]).Split(',');
            return p.Length == 2 && float.TryParse(p[0], System.Globalization.NumberStyles.Float, Inv, out var x)
                   && float.TryParse(p[1], System.Globalization.NumberStyles.Float, Inv, out var z) ? (x, z) : (float.NaN, float.NaN);
        }
        CommandResultMsg Usa(Sonda s, AbilityId id, float yaw)
        {
            Pausa(0.15);
            s.Comando(117, CommandType.UseAbility, P(new UseAbilityCommand { Ability = id, Yaw = yaw }));
            CommandResultMsg r = null;
            Aspetta(() => (r = s.Esito(117)) != null, 5);
            return r;
        }
        /// Mette id nello slot 0 della spada di s, azzera le ricariche, e lo usa.
        CommandResultMsg Prova(Sonda s, AbilityId id, float yaw)
        {
            Pausa(0.15);
            s.Comando(118, CommandType.SetAbilitySlot, P(new SetAbilitySlotCommand
                { Kind = AbilitySlotKind.Weapon, Family = WeaponFamily.Sword, Slot = 0, Ability = id }));
            CommandResultMsg r = null;
            Aspetta(() => (r = s.Esito(118)) != null, 5);
            if (r == null || !r.Success) Male($"{id} nello slot: {Visto(r)}");
            Pausa(0.15); s.Admin("ricarica");
            return Usa(s, id, yaw);
        }
        void Atteso(string cosa, CommandResultMsg r, ErrorCode atteso)
        {
            bool giusto = r != null && (atteso == ErrorCode.None ? r.Success : !r.Success && r.ErrorCode == atteso);
            Console.WriteLine($"  {cosa}: {Visto(r)}{(giusto ? "" : $" (atteso {(atteso == ErrorCode.None ? "riuscito" : atteso.ToString())})")}");
            if (!giusto) Male($"{cosa}: atteso {(atteso == ErrorCode.None ? "riuscito" : "rifiutato " + atteso)}");
        }
        void Spada(Sonda s, string grado)
        {
            Pausa(0.15);
            var r = s.ChiediAdmin($"dai StoneSword {grado}", "dai: ");
            ulong id = r != null && ulong.TryParse(r.Split("id=").Last(), out var n) ? n : 0;
            if (id == 0 || !Aspetta(() => { lock (s.Zaino) return s.Istanze.ContainsKey(id); }, 5))
                Male($"'dai StoneSword {grado}' non ha dato la spada ({r ?? "nessuna risposta"})");
            s.Dotazione(ItemType.StoneSword, ItemType.None, SideItemType.None);
            s.Posizione(0);
            Pausa(0.15); s.Admin("abilita sblocca spada");
            Pausa(0.4);
        }

        a.Admin("setfaction 1"); b.Admin("setfaction 2"); c.Admin("setfaction 2");
        foreach (var s in new[] { a, b, c }) { s.Admin("clearinv"); s.Admin("abilita azzera"); s.Admin("effetto azzera"); }
        b.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        c.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        Tp(a, PX, PZ); Tp(b, PX + 1f, PZ); Tp(c, PX - 8f, PZ);
        Spada(a, "epico");
        Piena(a, b, c);

        // 1. Grido di battaglia: +15% danno per 8 s, il colpo normale 22 -> 25.
        Atteso("Grido di battaglia", Prova(a, AbilityId.SwordBattleCry, 90f), ErrorCode.None);
        bool grido = Aspetta(() => a.Statistiche?.DamagePercent == 15, 3);
        float prima = Vita(b);
        a.Colpisci(0.2);
        float conGrido = Perso(b, prima);
        Console.WriteLine($"  Grido: danno al client {(grido ? "+15%" : "non arrivato")}, colpo su B {conGrido} (atteso 25)");
        if (!grido) Male("il Grido di battaglia deve dare +15% danno, e il client saperlo");
        if (conGrido != 25f) Male("col Grido di battaglia la spada di pietra deve fare 25");
        Effetto(a, "azzera");
        Aspetta(() => a.Statistiche?.DamagePercent == 0, 3);

        // 2. Ferita: 22 subito, poi il 30% di 22 (7) in 6 s.
        Piena(b);
        prima = Vita(b);
        Atteso("Ferita su B", Prova(a, AbilityId.SwordWound, 90f), ErrorCode.None);
        float ferita = Perso(b, prima, 1.0);
        string stB = Effetto(a, $"stato {b.Id}");
        Aspetta(() => prima - Vita(b) >= 29f, 8);
        Pausa(1.3);
        float feritaTutto = prima - Vita(b);
        Console.WriteLine($"  Ferita: B perde {ferita} subito (atteso 22), {feritaTutto} in tutto (atteso 29); {stB}");
        if (ferita != 22f) Male("la Ferita deve colpire per 22");
        if (!stB.Contains("Ferita")) Male("la Ferita deve far sanguinare");
        if (feritaTutto != 29f) Male("la Ferita deve togliere 22 + 7 di sanguinamento");

        // 3. Colpo di pomo: 22 e B stordito.
        Piena(b);
        prima = Vita(b);
        Atteso("Colpo di pomo su B", Prova(a, AbilityId.SwordPommelStrike, 90f), ErrorCode.None);
        float pomo = Perso(b, prima, 1.0);
        stB = Effetto(a, $"stato {b.Id}");
        Console.WriteLine($"  Colpo di pomo: B perde {pomo} (atteso 22); {stB}");
        if (pomo != 22f) Male("il Colpo di pomo deve colpire per 22");
        if (!stB.Contains("stordito")) Male("il Colpo di pomo deve stordire");
        Effetto(a, $"azzera {b.Id}");

        // 4. Spinta: B da 1 m a 4 m, rallentato del 50%, senza danno.
        Tp(b, PX + 1f, PZ);
        Piena(b);
        prima = Vita(b);
        Atteso("Spinta su B", Prova(a, AbilityId.SwordShove, 90f), ErrorCode.None);
        Pausa(0.3);
        stB = Effetto(a, $"stato {b.Id}");
        var (sx, sz) = Pos(stB);
        Console.WriteLine($"  Spinta: B a ({sx:F2}, {sz:F2}) (atteso {PX + 4f}, {PZ}), perde {prima - Vita(b)} (atteso 0); {stB}");
        if (float.IsNaN(sx) || Dist(sx, sz, PX + 4f, PZ) > 0.4f) Male("la Spinta deve portare B 3 m piu' in la'");
        if (!stB.Contains("rallentato 50%")) Male("la Spinta deve rallentare del 50%");
        if (Vita(b) != prima) Male("la Spinta non deve fare danno");
        Effetto(a, $"azzera {b.Id}");

        // 5. Vortice: tre colpi da 22 a B davanti e a C dietro, entro 3 m.
        Tp(b, PX + 1f, PZ); Tp(c, PX - 1.5f, PZ);
        Piena(b, c);
        float pb = Vita(b), pc = Vita(c);
        Atteso("Vortice", Prova(a, AbilityId.SwordWhirlwind, 90f), ErrorCode.None);
        Aspetta(() => pb - Vita(b) >= 66f && pc - Vita(c) >= 66f, 3);
        Pausa(0.6);
        float vb = pb - Vita(b), vc = pc - Vita(c);
        Console.WriteLine($"  Vortice: B perde {vb}, C perde {vc} (attesi 66 e 66)");
        if (vb != 66f || vc != 66f) Male("il Vortice deve dare tre colpi da 22 a tutti entro 3 m");
        Tp(c, PX - 8f, PZ);

        // 6. Contrattacco di B: A colpisce B per 22 e perde 44; finito, niente.
        Spada(b, "raro");
        Piena(a, b);
        Atteso("Contrattacco di B", Prova(b, AbilityId.SwordRiposte, 270f), ErrorCode.None);
        float pa = Vita(a); pb = Vita(b);
        a.Colpisci(0.2);
        float cb = Perso(b, pb, 1.0), ca = Perso(a, pa, 1.0);
        Console.WriteLine($"  Contrattacco: B perde {cb} (atteso 22), A perde {ca} (atteso 44)");
        if (cb != 22f) Male("col Contrattacco B prende lo stesso il colpo");
        if (ca != 44f) Male("col Contrattacco chi colpisce deve prendere il doppio");
        Pausa(2.2);
        Piena(a, b);
        pa = Vita(a); pb = Vita(b);
        a.Colpisci(0.2);
        cb = Perso(b, pb, 1.0); ca = pa - Vita(a);
        Console.WriteLine($"  dopo 2 s: B perde {cb} (atteso 22), A perde {ca} (atteso 0)");
        if (ca != 0f) Male("finito il Contrattacco non si rimanda piu' niente");
        b.Admin("abilita azzera"); b.Admin("clearinv");
        b.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        Pausa(0.3);

        // 7. Esecuzione: B a 50 no; B a 25 si', triplo danno (66), con danno
        //    subito -75% perche' non muoia: 17.
        Piena(b);
        b.Admin("sethp 50");
        Aspetta(() => Vita(b) is > 0f and <= 50f, 3);
        Atteso("Esecuzione su B a 50", Prova(a, AbilityId.SwordExecution, 90f), ErrorCode.AbilityNotUsable);
        b.Admin("sethp 25");
        Aspetta(() => Vita(b) is > 0f and <= 25f, 3);
        Effetto(b, "subito -75 6");
        prima = Vita(b);
        Atteso("Esecuzione su B a 25", Prova(a, AbilityId.SwordExecution, 90f), ErrorCode.None);
        float es = Perso(b, prima, 1.0);
        Console.WriteLine($"  Esecuzione: B a 25 perde {es} (atteso 17: 66 con -75%)");
        if (es != 17f) Male("l'Esecuzione deve fare il triplo del danno sotto il 30% di vita");
        Effetto(b, "azzera");

        // 8. Lama assetata: A a 50, colpisce B per 22 e torna a 56.
        Piena(b);
        a.Admin("sethp 50");
        Aspetta(() => Vita(a) is > 0f and <= 50f, 3);
        Atteso("Lama assetata", Prova(a, AbilityId.SwordThirstyBlade, 90f), ErrorCode.None);
        pa = Vita(a); pb = Vita(b);
        a.Colpisci(0.2);
        float lb = Perso(b, pb, 1.0);
        Aspetta(() => Vita(a) > pa, 1.5);
        Pausa(0.2);
        float la = Vita(a) - pa;
        Console.WriteLine($"  Lama assetata: B perde {lb} (atteso 22), A guadagna {la} (atteso 6)");
        if (lb != 22f || la != 6f) Male("con la Lama assetata un colpo da 22 deve curare 6");

        // Si lascia tutto pulito.
        foreach (var s in new[] { a, b, c }) { s.Admin("effetto azzera"); s.Admin("abilita azzera"); s.Admin("clearinv"); s.Admin("sethp 1000"); }
        a.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        Pausa(0.5);

        if (ok) Console.WriteLine("  OK: Grido di battaglia, Ferita, Colpo di pomo, Spinta, Vortice, Contrattacco, Esecuzione e Lama assetata fanno cio' che devono");
        return ok;
    }

    // ---- la lancia (A14g3) -----------------------------------------------------
    //
    // Le dodici abilita della lancia, con una lancia di legno Epica (20 a
    // colpo, portata 2,5 m), sguardo verso +X. A in fazione 1, B e C in 2; per
    // la Falange C passa per un momento in fazione 1.

    public static bool Lancia(string tokenA, string tokenB, string tokenC, string host, int port)
    {
        Console.WriteLine("--- lancia: base, Comuni, Rare ed Epiche");
        bool ok = true;
        void Male(string m) { Console.WriteLine("  FALLITA: " + m); ok = false; }

        using var a = new Sonda(tokenA, host, port);
        using var b = new Sonda(tokenB, host, port);
        using var c = new Sonda(tokenC, host, port);
        if (!a.AttendiPronta() || !b.AttendiPronta() || !c.AttendiPronta())
        { Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco"); return false; }

        const float PX = -150f, PZ = 250f;   // lo stesso punto delle prove della spada
        static byte[] P<T>(T v) => MessagePack.MessagePackSerializer.Serialize(v);
        bool Aspetta(Func<bool> cosa, double secondi) =>
            a.Attendi(() => { b.Attendi(() => true, 0.01); c.Attendi(() => true, 0.01); return cosa(); }, secondi);
        void Pausa(double secondi) => Aspetta(() => false, secondi);
        static string Visto(CommandResultMsg r) => r == null ? "nessuna risposta" : r.Success ? "riuscito" : "rifiutato " + r.ErrorCode;
        float Vita(Sonda chi) => a.Stato(chi.Id).Vita;
        void Tp(Sonda s, float x, float z)
        {
            s.Admin($"tp {x.ToString(Inv)} {z.ToString(Inv)}");
            Aspetta(() => Dist(s.X, s.Z, x, z) < 0.5f, 6);
            s.Cammina(0f, 0f, 0.1);
        }
        void Piena(params Sonda[] chi)
        {
            foreach (var s in chi) s.Admin("sethp 1000");
            Aspetta(() => chi.All(s => Vita(s) >= 100f), 3);
            Pausa(0.3);
        }
        float Perso(Sonda chi, float prima, double secondi = 1.5)
        {
            Aspetta(() => Vita(chi) < prima, secondi);
            Pausa(0.3);
            return prima - Vita(chi);
        }
        string Effetto(Sonda s, string riga) { Pausa(0.15); return s.ChiediAdmin("effetto " + riga, "effetto: ") ?? "(nessuna risposta)"; }
        static (float X, float Z) Pos(string riga)
        {
            int i = riga.IndexOf("pos=", StringComparison.Ordinal);
            if (i < 0) return (float.NaN, float.NaN);
            int f = riga.IndexOf(' ', i);
            var p = (f < 0 ? riga[(i + 4)..] : riga[(i + 4)..f]).Split(',');
            return p.Length == 2 && float.TryParse(p[0], System.Globalization.NumberStyles.Float, Inv, out var x)
                   && float.TryParse(p[1], System.Globalization.NumberStyles.Float, Inv, out var z) ? (x, z) : (float.NaN, float.NaN);
        }
        CommandResultMsg Prova(Sonda s, AbilityId id, float yaw = 90f)
        {
            Pausa(0.15);
            s.Comando(118, CommandType.SetAbilitySlot, P(new SetAbilitySlotCommand
                { Kind = AbilitySlotKind.Weapon, Family = WeaponFamily.Spear, Slot = 0, Ability = id }));
            CommandResultMsg r = null;
            Aspetta(() => (r = s.Esito(118)) != null, 5);
            if (r == null || !r.Success) Male($"{id} nello slot: {Visto(r)}");
            Pausa(0.15); s.Admin("ricarica");
            Pausa(0.15);
            s.Comando(117, CommandType.UseAbility, P(new UseAbilityCommand { Ability = id, Yaw = yaw }));
            r = null;
            Aspetta(() => (r = s.Esito(117)) != null, 5);
            return r;
        }
        void Atteso(string cosa, CommandResultMsg r, ErrorCode atteso)
        {
            bool giusto = r != null && (atteso == ErrorCode.None ? r.Success : !r.Success && r.ErrorCode == atteso);
            Console.WriteLine($"  {cosa}: {Visto(r)}{(giusto ? "" : $" (atteso {(atteso == ErrorCode.None ? "riuscito" : atteso.ToString())})")}");
            if (!giusto) Male($"{cosa}: atteso {(atteso == ErrorCode.None ? "riuscito" : "rifiutato " + atteso)}");
        }
        void Pulisci(params Sonda[] chi) { foreach (var s in chi) Effetto(a, $"azzera {s.Id}"); }
        void Controlla(string cosa, float visto, float atteso)
        {
            Console.WriteLine($"  {cosa}: {visto} (atteso {atteso})");
            if (visto != atteso) Male($"{cosa}: {visto} invece di {atteso}");
        }

        a.Admin("setfaction 1"); b.Admin("setfaction 2"); c.Admin("setfaction 2");
        foreach (var s in new[] { a, b, c }) { s.Admin("clearinv"); s.Admin("abilita azzera"); s.Admin("effetto azzera"); }
        b.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        c.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        Tp(a, PX, PZ); Tp(b, PX + 6f, PZ); Tp(c, PX - 20f, PZ);
        {
            Pausa(0.15);
            var r = a.ChiediAdmin("dai WoodSpear epico", "dai: ");
            ulong id = r != null && ulong.TryParse(r.Split("id=").Last(), out var n) ? n : 0;
            if (id == 0 || !Aspetta(() => { lock (a.Zaino) return a.Istanze.ContainsKey(id); }, 5))
                Male($"'dai WoodSpear epico' non ha dato la lancia ({r ?? "nessuna risposta"})");
            a.Dotazione(ItemType.WoodSpear, ItemType.None, SideItemType.None);
            a.Posizione(0);
            Pausa(0.15); a.Admin("abilita sblocca lancia");
            Pausa(0.4);
        }
        Piena(a, b, c);

        // 1. Stoccata lunga: B a 6 m no, a 3,5 m (oltre la portata della lancia) 20.
        Atteso("Stoccata lunga su B a 6 m", Prova(a, AbilityId.SpearLongThrust), ErrorCode.AbilityNotUsable);
        Tp(b, PX + 3.5f, PZ);
        float prima = Vita(b);
        Atteso("Stoccata lunga su B a 3,5 m", Prova(a, AbilityId.SpearLongThrust), ErrorCode.None);
        Controlla("Stoccata lunga, B perde", Perso(b, prima, 1.0), 20f);

        // 2. Lancio: B a 10 m, 150% = 30.
        Tp(b, PX + 10f, PZ);
        prima = Vita(b);
        Atteso("Lancio su B a 10 m", Prova(a, AbilityId.SpearThrow), ErrorCode.None);
        Controlla("Lancio, B perde", Perso(b, prima, 1.0), 30f);

        // 3. Balzo indietro: A da PX a PX - 4.
        Atteso("Balzo indietro", Prova(a, AbilityId.SpearBackstep), ErrorCode.None);
        Pausa(0.2);
        var (ax, az) = Pos(Effetto(a, "stato"));
        Console.WriteLine($"  Balzo indietro: A a ({ax:F2}, {az:F2}) (atteso {PX - 4f}, {PZ})");
        if (float.IsNaN(ax) || Dist(ax, az, PX - 4f, PZ) > 0.4f) Male("il Balzo indietro deve portare A 4 m indietro");
        Tp(a, PX, PZ);

        // 4. Tenere a distanza: B a 1,5 m prende 10 (meta' di 20) e va oltre i 2 m.
        Tp(b, PX + 1.5f, PZ);
        Piena(b);
        prima = Vita(b);
        Atteso("Tenere a distanza", Prova(a, AbilityId.SpearKeepAtBay), ErrorCode.None);
        float tenere = Perso(b, prima, 1.0);
        var (bx, bz) = Pos(Effetto(a, $"stato {b.Id}"));
        Controlla("Tenere a distanza, B perde", tenere, 10f);
        Console.WriteLine($"  Tenere a distanza: B a ({bx:F2}, {bz:F2}) (atteso {PX + 3.5f}, {PZ})");
        if (float.IsNaN(bx) || Dist(bx, bz, PX + 3.5f, PZ) > 0.4f) Male("Tenere a distanza deve spingere B di 2 m");
        Pausa(3.2);

        // 5. Perforare: B a 1,5 m e C dietro a 3 m, 20 a tutti e due.
        Tp(b, PX + 1.5f, PZ); Tp(c, PX + 3f, PZ);
        Piena(b, c);
        float pb = Vita(b), pc = Vita(c);
        Atteso("Perforare", Prova(a, AbilityId.SpearPierce), ErrorCode.None);
        Aspetta(() => Vita(b) < pb && Vita(c) < pc, 1.5);
        Pausa(0.3);
        Controlla("Perforare, B perde", pb - Vita(b), 20f);
        Controlla("Perforare, C dietro perde", pc - Vita(c), 20f);
        Tp(c, PX - 20f, PZ);

        // 6. Inchiodare: B perde 20 ed e' rallentato del 70%.
        Piena(b);
        prima = Vita(b);
        Atteso("Inchiodare", Prova(a, AbilityId.SpearPinDown), ErrorCode.None);
        Controlla("Inchiodare, B perde", Perso(b, prima, 1.0), 20f);
        string stB = Effetto(a, $"stato {b.Id}");
        Console.WriteLine($"  Inchiodare: {stB}");
        if (!stB.Contains("rallentato 70%")) Male("Inchiodare deve rallentare del 70%");
        Pulisci(b);

        // 7. Carica: B a 6 m, A corre fino a 1,5 m da lui, B perde 40 ed e' stordito.
        Tp(b, PX + 6f, PZ);
        Piena(b);
        prima = Vita(b);
        Atteso("Carica", Prova(a, AbilityId.SpearCharge), ErrorCode.None);
        Controlla("Carica, B perde", Perso(b, prima, 1.0), 40f);
        stB = Effetto(a, $"stato {b.Id}");
        (ax, az) = Pos(Effetto(a, "stato"));
        Console.WriteLine($"  Carica: A a ({ax:F2}, {az:F2}) (atteso fra {PX + 4.2f} e {PX + 4.8f}); {stB}");
        if (float.IsNaN(ax) || ax < PX + 4.2f || ax > PX + 4.8f) Male("la Carica deve fermarsi a 1,5 m da B");
        if (!stB.Contains("stordito")) Male("la Carica deve stordire chi prende");
        Pulisci(b);
        Tp(a, PX, PZ);

        // 8. Raffica: tre stoccate da 20 su B.
        Tp(b, PX + 1.5f, PZ);
        Piena(b);
        prima = Vita(b);
        Atteso("Raffica", Prova(a, AbilityId.SpearFlurry), ErrorCode.None);
        Aspetta(() => prima - Vita(b) >= 60f, 3);
        Pausa(0.5);
        Controlla("Raffica, B perde", prima - Vita(b), 60f);

        // 9. Spazzata: B davanti e C dietro a 3 m, 20 e stordimento a tutti e due.
        Tp(c, PX - 3f, PZ);
        Piena(b, c);
        pb = Vita(b); pc = Vita(c);
        Atteso("Spazzata", Prova(a, AbilityId.SpearSweep), ErrorCode.None);
        Aspetta(() => Vita(b) < pb && Vita(c) < pc, 1.5);
        Pausa(0.1);
        string stC = Effetto(a, $"stato {c.Id}");
        stB = Effetto(a, $"stato {b.Id}");
        Controlla("Spazzata, B perde", pb - Vita(b), 20f);
        Controlla("Spazzata, C perde", pc - Vita(c), 20f);
        if (!stB.Contains("stordito") || !stC.Contains("stordito")) Male($"la Spazzata deve stordire tutti e due ({stB} / {stC})");
        Pulisci(b, c);

        // 10. Lancio passante: B a 5 m e C a 12 m sulla linea, 20 a tutti e due.
        Tp(b, PX + 5f, PZ); Tp(c, PX + 12f, PZ);
        Piena(b, c);
        pb = Vita(b); pc = Vita(c);
        Atteso("Lancio passante", Prova(a, AbilityId.SpearPiercingThrow), ErrorCode.None);
        Aspetta(() => Vita(b) < pb && Vita(c) < pc, 1.5);
        Pausa(0.3);
        Controlla("Lancio passante, B perde", pb - Vita(b), 20f);
        Controlla("Lancio passante, C perde", pc - Vita(c), 20f);
        Tp(c, PX - 20f, PZ);

        // 11. Impalare: B perde 20, immobile, poi 8 di sanguinamento (40%) in 3 s.
        Tp(b, PX + 1.5f, PZ);
        Piena(b);
        prima = Vita(b);
        Atteso("Impalare", Prova(a, AbilityId.SpearImpale), ErrorCode.None);
        float impala = Perso(b, prima, 1.0);
        stB = Effetto(a, $"stato {b.Id}");
        Aspetta(() => prima - Vita(b) >= 28f, 5);
        Pausa(1.3);
        Controlla("Impalare, B perde subito", impala, 20f);
        Controlla("Impalare, B perde in tutto", prima - Vita(b), 28f);
        Console.WriteLine($"  Impalare: {stB}");
        if (!stB.Contains("immobile")) Male("Impalare deve immobilizzare");
        Pulisci(b);

        // 12. Falange: A e C (passata in fazione 1, a 3 m) a -25% danno subito,
        //     B nemico a 1,5 m no.
        c.Admin("setfaction 1");
        Tp(c, PX - 3f, PZ);
        Pausa(0.3);
        Atteso("Falange", Prova(a, AbilityId.SpearPhalanx), ErrorCode.None);
        string stA = Effetto(a, "stato");
        stC = Effetto(a, $"stato {c.Id}");
        stB = Effetto(a, $"stato {b.Id}");
        Console.WriteLine($"  Falange: A {stA}; C {stC}; B {stB}");
        if (!stA.Contains("danno subito -25%") || !stC.Contains("danno subito -25%")) Male("la Falange deve dare -25% danno subito a chi la usa e agli alleati vicini");
        if (stB.Contains("Falange")) Male("la Falange non deve andare ai nemici");

        // Si lascia tutto pulito.
        c.Admin("setfaction 2");
        foreach (var s in new[] { a, b, c }) { s.Admin("effetto azzera"); s.Admin("abilita azzera"); s.Admin("clearinv"); s.Admin("sethp 1000"); }
        a.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        Pausa(0.5);

        if (ok) Console.WriteLine("  OK: le dodici abilita della lancia fanno cio' che devono");
        return ok;
    }

    // ---- l'arco (A14g4) --------------------------------------------------------
    //
    // Le dodici abilita dell'arco, con un arco Epico, sguardo verso +X. Una
    // freccia d'abilita vale il tiro pieno di oggi: 40. A in fazione 1, B e C
    // in 2. Ogni freccia tirata consuma una freccia vera dello zaino di A.

    public static bool Arco(string tokenA, string tokenB, string tokenC, string host, int port)
    {
        Console.WriteLine("--- arco: base, Comuni, Rare ed Epiche");
        bool ok = true;
        void Male(string m) { Console.WriteLine("  FALLITA: " + m); ok = false; }

        using var a = new Sonda(tokenA, host, port);
        using var b = new Sonda(tokenB, host, port);
        using var c = new Sonda(tokenC, host, port);
        if (!a.AttendiPronta() || !b.AttendiPronta() || !c.AttendiPronta())
        { Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco"); return false; }

        const float PX = -150f, PZ = 250f;   // lo stesso punto delle prove della spada
        static byte[] P<T>(T v) => MessagePack.MessagePackSerializer.Serialize(v);
        bool Aspetta(Func<bool> cosa, double secondi) =>
            a.Attendi(() => { b.Attendi(() => true, 0.01); c.Attendi(() => true, 0.01); return cosa(); }, secondi);
        void Pausa(double secondi) => Aspetta(() => false, secondi);
        static string Visto(CommandResultMsg r) => r == null ? "nessuna risposta" : r.Success ? "riuscito" : "rifiutato " + r.ErrorCode;
        float Vita(Sonda chi) => a.Stato(chi.Id).Vita;
        void Tp(Sonda s, float x, float z)
        {
            s.Admin($"tp {x.ToString(Inv)} {z.ToString(Inv)}");
            Aspetta(() => Dist(s.X, s.Z, x, z) < 0.5f, 6);
            s.Cammina(0f, 0f, 0.1);
        }
        void Piena(params Sonda[] chi)
        {
            foreach (var s in chi) s.Admin("sethp 1000");
            Aspetta(() => chi.All(s => Vita(s) >= 100f), 3);
            Pausa(0.3);
        }
        float Perso(Sonda chi, float prima, double secondi = 1.5)
        {
            Aspetta(() => Vita(chi) < prima, secondi);
            Pausa(0.3);
            return prima - Vita(chi);
        }
        string Effetto(Sonda s, string riga) { Pausa(0.15); return s.ChiediAdmin("effetto " + riga, "effetto: ") ?? "(nessuna risposta)"; }
        static (float X, float Z) Pos(string riga)
        {
            int i = riga.IndexOf("pos=", StringComparison.Ordinal);
            if (i < 0) return (float.NaN, float.NaN);
            int f = riga.IndexOf(' ', i);
            var p = (f < 0 ? riga[(i + 4)..] : riga[(i + 4)..f]).Split(',');
            return p.Length == 2 && float.TryParse(p[0], System.Globalization.NumberStyles.Float, Inv, out var x)
                   && float.TryParse(p[1], System.Globalization.NumberStyles.Float, Inv, out var z) ? (x, z) : (float.NaN, float.NaN);
        }
        CommandResultMsg Prova(Sonda s, AbilityId id, float yaw = 90f)
        {
            Pausa(0.15);
            s.Comando(118, CommandType.SetAbilitySlot, P(new SetAbilitySlotCommand
                { Kind = AbilitySlotKind.Weapon, Family = WeaponFamily.Bow, Slot = 0, Ability = id }));
            CommandResultMsg r = null;
            Aspetta(() => (r = s.Esito(118)) != null, 5);
            if (r == null || !r.Success) Male($"{id} nello slot: {Visto(r)}");
            Pausa(0.15); s.Admin("ricarica");
            Pausa(0.15);
            s.Comando(117, CommandType.UseAbility, P(new UseAbilityCommand { Ability = id, Yaw = yaw }));
            r = null;
            Aspetta(() => (r = s.Esito(117)) != null, 5);
            return r;
        }
        void Atteso(string cosa, CommandResultMsg r, ErrorCode atteso)
        {
            bool giusto = r != null && (atteso == ErrorCode.None ? r.Success : !r.Success && r.ErrorCode == atteso);
            Console.WriteLine($"  {cosa}: {Visto(r)}{(giusto ? "" : $" (atteso {(atteso == ErrorCode.None ? "riuscito" : atteso.ToString())})")}");
            if (!giusto) Male($"{cosa}: atteso {(atteso == ErrorCode.None ? "riuscito" : "rifiutato " + atteso)}");
        }
        void Pulisci(params Sonda[] chi) { foreach (var s in chi) Effetto(a, $"azzera {s.Id}"); }
        void Controlla(string cosa, float visto, float atteso)
        {
            Console.WriteLine($"  {cosa}: {visto} (atteso {atteso})");
            if (visto != atteso) Male($"{cosa}: {visto} invece di {atteso}");
        }
        int Frecce() { Pausa(0.2); return a.Quanti(ItemType.Arrow); }

        a.Admin("setfaction 1"); b.Admin("setfaction 2"); c.Admin("setfaction 2");
        foreach (var s in new[] { a, b, c }) { s.Admin("clearinv"); s.Admin("abilita azzera"); s.Admin("effetto azzera"); }
        b.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        c.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        Tp(a, PX, PZ); Tp(b, PX + 10f, PZ); Tp(c, PX - 20f, PZ);
        {
            Pausa(0.15);
            var r = a.ChiediAdmin("dai Bow epico", "dai: ");
            ulong id = r != null && ulong.TryParse(r.Split("id=").Last(), out var n) ? n : 0;
            if (id == 0 || !Aspetta(() => { lock (a.Zaino) return a.Istanze.ContainsKey(id); }, 5))
                Male($"'dai Bow epico' non ha dato l'arco ({r ?? "nessuna risposta"})");
            a.Dotazione(ItemType.Bow, ItemType.None, SideItemType.None);
            a.Posizione(0);
            Pausa(0.15); a.Admin("abilita sblocca arco");
            Pausa(0.4);
        }
        Piena(a, b, c);

        // 1. Tiro mirato: senza frecce non parte; con le frecce 64 (+60%) e una freccia in meno.
        Atteso("Tiro mirato senza frecce", Prova(a, AbilityId.BowAimedShot), ErrorCode.AbilityNotUsable);
        a.Admin("give Arrow 60");
        Aspetta(() => a.Quanti(ItemType.Arrow) == 60, 3);
        int frecce = Frecce();
        float prima = Vita(b);
        Atteso("Tiro mirato su B a 10 m", Prova(a, AbilityId.BowAimedShot), ErrorCode.None);
        Controlla("Tiro mirato, B perde", Perso(b, prima, 1.0), 64f);
        Controlla("Tiro mirato, frecce usate", frecce - Frecce(), 1f);

        // 2. Doppia freccia: due da 40.
        Piena(b);
        prima = Vita(b);
        Atteso("Doppia freccia", Prova(a, AbilityId.BowDoubleArrow), ErrorCode.None);
        Aspetta(() => prima - Vita(b) >= 80f, 2);
        Pausa(0.4);
        Controlla("Doppia freccia, B perde", prima - Vita(b), 80f);

        // 3. Freccia azzoppante: 40 e rallentato del 40%.
        Piena(b);
        prima = Vita(b);
        Atteso("Freccia azzoppante", Prova(a, AbilityId.BowCripplingArrow), ErrorCode.None);
        Controlla("Freccia azzoppante, B perde", Perso(b, prima, 1.0), 40f);
        string stB = Effetto(a, $"stato {b.Id}");
        Console.WriteLine($"  Freccia azzoppante: {stB}");
        if (!stB.Contains("rallentato 40%")) Male("la Freccia azzoppante deve rallentare del 40%");
        Pulisci(b);

        // 4. Freccia infuocata: 40 subito, poi 12 (30%) in 5 s.
        Piena(b);
        prima = Vita(b);
        Atteso("Freccia infuocata", Prova(a, AbilityId.BowFireArrow), ErrorCode.None);
        float subito = Perso(b, prima, 1.0);
        Aspetta(() => prima - Vita(b) >= 52f, 7);
        Pausa(1.3);
        Controlla("Freccia infuocata, B perde subito", subito, 40f);
        Controlla("Freccia infuocata, B perde in tutto", prima - Vita(b), 52f);
        Pulisci(b);

        // 5. Ventaglio: B dritto a 5 m e C a 5 m, 30 gradi a destra (verso -Z):
        //    una freccia ciascuno, 40 a tutti e due, cinque frecce usate.
        Tp(b, PX + 5f, PZ); Tp(c, PX + 4.33f, PZ - 2.5f);
        Piena(b, c);
        float pb = Vita(b), pc = Vita(c);
        frecce = Frecce();
        Atteso("Ventaglio", Prova(a, AbilityId.BowArrowFan), ErrorCode.None);
        Aspetta(() => Vita(b) < pb && Vita(c) < pc, 1.5);
        Pausa(0.3);
        Controlla("Ventaglio, B perde", pb - Vita(b), 40f);
        Controlla("Ventaglio, C perde", pc - Vita(c), 40f);
        Controlla("Ventaglio, frecce usate", frecce - Frecce(), 5f);
        Tp(c, PX - 20f, PZ);

        // 6. Freccia stordente: 40 e stordito.
        Tp(b, PX + 10f, PZ);
        Piena(b);
        prima = Vita(b);
        Atteso("Freccia stordente", Prova(a, AbilityId.BowStunningArrow), ErrorCode.None);
        Controlla("Freccia stordente, B perde", Perso(b, prima, 1.0), 40f);
        stB = Effetto(a, $"stato {b.Id}");
        Console.WriteLine($"  Freccia stordente: {stB}");
        if (!stB.Contains("stordito")) Male("la Freccia stordente deve stordire");
        Pulisci(b);

        // 7. Pioggia di frecce: B a 15 m, C a 17 m; sei ondate da 10 a tutti e
        //    due, sei frecce usate.
        Tp(b, PX + 15f, PZ); Tp(c, PX + 17f, PZ);
        Piena(b, c);
        pb = Vita(b); pc = Vita(c);
        frecce = Frecce();
        Atteso("Pioggia di frecce", Prova(a, AbilityId.BowArrowRain), ErrorCode.None);
        Aspetta(() => pb - Vita(b) >= 60f && pc - Vita(c) >= 60f, 5);
        Pausa(0.8);
        Controlla("Pioggia di frecce, B perde", pb - Vita(b), 60f);
        Controlla("Pioggia di frecce, C perde", pc - Vita(c), 60f);
        Controlla("Pioggia di frecce, frecce usate", frecce - Frecce(), 6f);
        Tp(c, PX - 20f, PZ);

        // 8. Tiro in fuga: B a 3 m; A salta 5 m indietro e B prende 40.
        Tp(b, PX + 3f, PZ);
        Piena(b);
        prima = Vita(b);
        Atteso("Tiro in fuga", Prova(a, AbilityId.BowRetreatShot), ErrorCode.None);
        Controlla("Tiro in fuga, B perde", Perso(b, prima, 1.0), 40f);
        var (ax, az) = Pos(Effetto(a, "stato"));
        Console.WriteLine($"  Tiro in fuga: A a ({ax:F2}, {az:F2}) (atteso {PX - 5f}, {PZ})");
        if (float.IsNaN(ax) || Dist(ax, az, PX - 5f, PZ) > 0.4f) Male("il Tiro in fuga deve portare A 5 m indietro");
        Tp(a, PX, PZ);

        // 9. Freccia perforante: B a 10 m e C a 30 m sulla linea, 80 a tutti e due.
        Tp(b, PX + 10f, PZ); Tp(c, PX + 30f, PZ);
        Piena(b, c);
        pb = Vita(b); pc = Vita(c);
        Atteso("Freccia perforante", Prova(a, AbilityId.BowPiercingArrow), ErrorCode.None);
        Aspetta(() => Vita(b) < pb && Vita(c) < pc, 1.5);
        Pausa(0.3);
        Controlla("Freccia perforante, B perde", pb - Vita(b), 80f);
        Controlla("Freccia perforante, C perde", pc - Vita(c), 80f);
        Tp(c, PX - 20f, PZ);

        // 10. Marchio del cacciatore: niente danno, B a +25% danno subito.
        Piena(b);
        prima = Vita(b);
        Atteso("Marchio del cacciatore", Prova(a, AbilityId.BowHuntersMark), ErrorCode.None);
        stB = Effetto(a, $"stato {b.Id}");
        Console.WriteLine($"  Marchio del cacciatore: {stB}");
        if (!stB.Contains("danno subito +25%")) Male("il Marchio deve dare +25% di danno subito");
        Controlla("Marchio del cacciatore, B perde", prima - Vita(b), 0f);
        Pulisci(b);

        // 11. Tiro rapido: senza, premere l'attacco con l'arco non tira niente
        //     (si tira rilasciando la carica); con, ogni 3/4 di secondo un tiro
        //     pieno da 40: due in 1,3 s.
        Piena(b);
        prima = Vita(b);
        a.Colpisci(1.0);
        Pausa(0.3);
        Controlla("Senza Tiro rapido, premere l'attacco: B perde", prima - Vita(b), 0f);
        frecce = Frecce();
        Atteso("Tiro rapido", Prova(a, AbilityId.BowRapidFire), ErrorCode.None);
        a.Colpisci(1.3);
        Pausa(0.3);
        int tirate = frecce - Frecce();
        Controlla("Tiro rapido, frecce tirate in 1,3 s", tirate, 2f);
        Controlla("Tiro rapido, B perde", prima - Vita(b), 80f);
        Pausa(3.0);   // il Tiro rapido finisce

        // 12. Occhio di falco: B a 25 m, oltre la gittata: l'Azzoppante non
        //     parte; con l'Occhio (+50% gittata, +10% danno) parte e fa 44.
        Tp(b, PX + 25f, PZ);
        Piena(b);
        Atteso("Freccia azzoppante su B a 25 m", Prova(a, AbilityId.BowCripplingArrow), ErrorCode.AbilityNotUsable);
        Atteso("Occhio di falco", Prova(a, AbilityId.BowHawkEye), ErrorCode.None);
        prima = Vita(b);
        Atteso("Freccia azzoppante su B a 25 m con l'Occhio", Prova(a, AbilityId.BowCripplingArrow), ErrorCode.None);
        Controlla("Con l'Occhio di falco, B perde", Perso(b, prima, 1.0), 44f);

        // Si lascia tutto pulito.
        foreach (var s in new[] { a, b, c }) { s.Admin("effetto azzera"); s.Admin("abilita azzera"); s.Admin("clearinv"); s.Admin("sethp 1000"); }
        a.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        Pausa(0.5);

        if (ok) Console.WriteLine("  OK: le dodici abilita dell'arco fanno cio' che devono");
        return ok;
    }

    // ---- il bastone (A14g5) ----------------------------------------------------
    //
    // Le dodici abilita del bastone, con un bastone Epico (12 a colpo, portata
    // 2,5 m), sguardo verso +X. A in fazione 1, B e C in 2; per il Vento alle
    // spalle C passa per un momento in fazione 1. B colpisce a mani nude per
    // provare Schivata e Danza.

    public static bool Bastone(string tokenA, string tokenB, string tokenC, string host, int port)
    {
        Console.WriteLine("--- bastone: base, Comuni, Rare ed Epiche");
        bool ok = true;
        void Male(string m) { Console.WriteLine("  FALLITA: " + m); ok = false; }

        using var a = new Sonda(tokenA, host, port);
        using var b = new Sonda(tokenB, host, port);
        using var c = new Sonda(tokenC, host, port);
        if (!a.AttendiPronta() || !b.AttendiPronta() || !c.AttendiPronta())
        { Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco"); return false; }

        const float PX = -150f, PZ = 250f;   // lo stesso punto delle prove della spada
        static byte[] P<T>(T v) => MessagePack.MessagePackSerializer.Serialize(v);
        bool Aspetta(Func<bool> cosa, double secondi) =>
            a.Attendi(() => { b.Attendi(() => true, 0.01); c.Attendi(() => true, 0.01); return cosa(); }, secondi);
        void Pausa(double secondi) => Aspetta(() => false, secondi);
        static string Visto(CommandResultMsg r) => r == null ? "nessuna risposta" : r.Success ? "riuscito" : "rifiutato " + r.ErrorCode;
        float Vita(Sonda chi) => a.Stato(chi.Id).Vita;
        void Tp(Sonda s, float x, float z)
        {
            s.Admin($"tp {x.ToString(Inv)} {z.ToString(Inv)}");
            Aspetta(() => Dist(s.X, s.Z, x, z) < 0.5f, 6);
            s.Cammina(0f, 0f, 0.1);
        }
        void Piena(params Sonda[] chi)
        {
            foreach (var s in chi) s.Admin("sethp 1000");
            Aspetta(() => chi.All(s => Vita(s) >= 100f), 3);
            Pausa(0.3);
        }
        float Perso(Sonda chi, float prima, double secondi = 1.5)
        {
            Aspetta(() => Vita(chi) < prima, secondi);
            Pausa(0.3);
            return prima - Vita(chi);
        }
        string Effetto(Sonda s, string riga) { Pausa(0.15); return s.ChiediAdmin("effetto " + riga, "effetto: ") ?? "(nessuna risposta)"; }
        static (float X, float Z) Pos(string riga)
        {
            int i = riga.IndexOf("pos=", StringComparison.Ordinal);
            if (i < 0) return (float.NaN, float.NaN);
            int f = riga.IndexOf(' ', i);
            var p = (f < 0 ? riga[(i + 4)..] : riga[(i + 4)..f]).Split(',');
            return p.Length == 2 && float.TryParse(p[0], System.Globalization.NumberStyles.Float, Inv, out var x)
                   && float.TryParse(p[1], System.Globalization.NumberStyles.Float, Inv, out var z) ? (x, z) : (float.NaN, float.NaN);
        }
        (float X, float Z) Dove(Sonda s) => Pos(Effetto(a, s == a ? "stato" : $"stato {s.Id}"));
        void Vicino(string cosa, Sonda s, float x, float z, float tolleranza = 0.4f)
        {
            var (vx, vz) = Dove(s);
            Console.WriteLine($"  {cosa}: ({vx:F2}, {vz:F2}) (atteso {x}, {z})");
            if (float.IsNaN(vx) || Dist(vx, vz, x, z) > tolleranza) Male($"{cosa}: ({vx:F2}, {vz:F2}) invece di ({x}, {z})");
        }
        CommandResultMsg Prova(Sonda s, AbilityId id, float yaw = 90f)
        {
            Pausa(0.15);
            s.Comando(118, CommandType.SetAbilitySlot, P(new SetAbilitySlotCommand
                { Kind = AbilitySlotKind.Weapon, Family = WeaponFamily.Staff, Slot = 0, Ability = id }));
            CommandResultMsg r = null;
            Aspetta(() => (r = s.Esito(118)) != null, 5);
            if (r == null || !r.Success) Male($"{id} nello slot: {Visto(r)}");
            Pausa(0.15); s.Admin("ricarica");
            Pausa(0.15);
            s.Comando(117, CommandType.UseAbility, P(new UseAbilityCommand { Ability = id, Yaw = yaw }));
            r = null;
            Aspetta(() => (r = s.Esito(117)) != null, 5);
            return r;
        }
        void Atteso(string cosa, CommandResultMsg r, ErrorCode atteso)
        {
            bool giusto = r != null && (atteso == ErrorCode.None ? r.Success : !r.Success && r.ErrorCode == atteso);
            Console.WriteLine($"  {cosa}: {Visto(r)}{(giusto ? "" : $" (atteso {(atteso == ErrorCode.None ? "riuscito" : atteso.ToString())})")}");
            if (!giusto) Male($"{cosa}: atteso {(atteso == ErrorCode.None ? "riuscito" : "rifiutato " + atteso)}");
        }
        void Pulisci(params Sonda[] chi) { foreach (var s in chi) Effetto(a, $"azzera {s.Id}"); }
        void Controlla(string cosa, float visto, float atteso)
        {
            Console.WriteLine($"  {cosa}: {visto} (atteso {atteso})");
            if (visto != atteso) Male($"{cosa}: {visto} invece di {atteso}");
        }
        // B tira un pugno ad A (il colpo normale, a mani nude) e dice quanto ha tolto.
        float Pugno()
        {
            float prima = Vita(a);
            b.Colpisci(0.1);
            return Perso(a, prima, 0.6);
        }

        a.Admin("setfaction 1"); b.Admin("setfaction 2"); c.Admin("setfaction 2");
        foreach (var s in new[] { a, b, c }) { s.Admin("clearinv"); s.Admin("abilita azzera"); s.Admin("effetto azzera"); }
        b.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        c.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        Tp(a, PX, PZ); Tp(b, PX + 1.5f, PZ); Tp(c, PX - 1.5f, PZ);
        {
            Pausa(0.15);
            var r = a.ChiediAdmin("dai Staff epico", "dai: ");
            ulong id = r != null && ulong.TryParse(r.Split("id=").Last(), out var n) ? n : 0;
            if (id == 0 || !Aspetta(() => { lock (a.Zaino) return a.Istanze.ContainsKey(id); }, 5))
                Male($"'dai Staff epico' non ha dato il bastone ({r ?? "nessuna risposta"})");
            a.Dotazione(ItemType.Staff, ItemType.None, SideItemType.None);
            a.Posizione(0);
            Pausa(0.15); a.Admin("abilita sblocca bastone");
            Pausa(0.4);
        }
        Piena(a, b, c);

        // 1. Colpo rotante: B davanti e C dietro a 1,5 m, 12 a tutti e due e
        //    spinti via di 1 m.
        float pb = Vita(b), pc = Vita(c);
        Atteso("Colpo rotante", Prova(a, AbilityId.StaffSpinStrike), ErrorCode.None);
        Aspetta(() => Vita(b) < pb && Vita(c) < pc, 1.5);
        Pausa(0.3);
        Controlla("Colpo rotante, B perde", pb - Vita(b), 12f);
        Controlla("Colpo rotante, C perde", pc - Vita(c), 12f);
        Vicino("Colpo rotante, B spinto a", b, PX + 2.5f, PZ);
        Vicino("Colpo rotante, C spinto a", c, PX - 2.5f, PZ);
        Tp(c, PX - 20f, PZ);

        // 2. Sgambetto: senza nessuno a portata non parte; B a 1,5 m perde 12
        //    ed e' stordito.
        Tp(b, PX + 6f, PZ);
        Atteso("Sgambetto su B a 6 m", Prova(a, AbilityId.StaffTrip), ErrorCode.AbilityNotUsable);
        Tp(b, PX + 1.5f, PZ);
        Piena(b);
        float prima = Vita(b);
        Atteso("Sgambetto su B a 1,5 m", Prova(a, AbilityId.StaffTrip), ErrorCode.None);
        Controlla("Sgambetto, B perde", Perso(b, prima, 1.0), 12f);
        string stB = Effetto(a, $"stato {b.Id}");
        Console.WriteLine($"  Sgambetto: {stB}");
        if (!stB.Contains("stordito")) Male("lo Sgambetto deve stordire");
        Pulisci(b);

        // 3. Scatto: A 6 m avanti. Salto con l'asta: 8 m avanti.
        Tp(b, PX + 1.5f, PZ + 10f);
        Atteso("Scatto", Prova(a, AbilityId.StaffDash), ErrorCode.None);
        Pausa(0.2);
        Vicino("Scatto, A a", a, PX + 6f, PZ);
        Tp(a, PX, PZ);
        Atteso("Salto con l'asta", Prova(a, AbilityId.StaffPoleVault), ErrorCode.None);
        Pausa(0.2);
        Vicino("Salto con l'asta, A a", a, PX + 8f, PZ);
        Tp(a, PX, PZ);

        // 4. Passo leggero: A +30% velocita'. Vento alle spalle: A e C (passata
        //    in fazione 1, a 3 m) +20%, B nemico a 1,5 m no.
        Atteso("Passo leggero", Prova(a, AbilityId.StaffLightStep), ErrorCode.None);
        string stA = Effetto(a, "stato");
        Console.WriteLine($"  Passo leggero: {stA}");
        if (!stA.Contains("MoveSpeed +30%")) Male("il Passo leggero deve dare +30% di velocita'");
        Pulisci(a);
        c.Admin("setfaction 1");
        Tp(c, PX - 3f, PZ); Tp(b, PX + 1.5f, PZ);
        Pausa(0.3);
        Atteso("Vento alle spalle", Prova(a, AbilityId.StaffTailwind), ErrorCode.None);
        stA = Effetto(a, "stato");
        string stC = Effetto(a, $"stato {c.Id}");
        stB = Effetto(a, $"stato {b.Id}");
        Console.WriteLine($"  Vento alle spalle: A {stA}; C {stC}; B {stB}");
        if (!stA.Contains("MoveSpeed +20%") || !stC.Contains("MoveSpeed +20%")) Male("il Vento alle spalle deve dare +20% di velocita' a chi lo usa e agli alleati vicini");
        if (stB.Contains("Vento")) Male("il Vento alle spalle non deve andare ai nemici");
        c.Admin("setfaction 2");
        Pulisci(a, b, c);
        Tp(c, PX - 20f, PZ);

        // 5. Schivata: sguardo verso +X, A salta 3 m a destra (verso -Z); B le
        //    sta gia' a un metro dal punto d'arrivo e tira un pugno: manca. Dopo
        //    il secondo il pugno prende.
        Tp(b, PX + 1f, PZ - 3f);
        Piena(a);
        Atteso("Schivata", Prova(a, AbilityId.StaffDodge), ErrorCode.None);
        float schivato = Pugno();
        Vicino("Schivata, A a", a, PX, PZ - 3f);
        Controlla("Schivata, il pugno di B entro un secondo toglie", schivato, 0f);
        Pausa(0.6);
        float preso = Pugno();
        Console.WriteLine($"  Dopo la Schivata il pugno di B toglie {preso}");
        if (preso <= 0f) Male("finita la Schivata il pugno deve prendere");

        // 6. Danza: con l'esito fissato a "manca" il pugno non toglie niente,
        //    a "prende" si'. Il 50% vero non lo prova nessuno: e' un dado.
        Piena(a);
        Atteso("Danza", Prova(a, AbilityId.StaffDance), ErrorCode.None);
        string danza = a.ChiediAdmin("danza si", "danza: ") ?? "(nessuna risposta)";
        if (!danza.Contains("si")) Male($"'danza si' non risponde ({danza})");
        Pausa(0.5);
        Controlla("Danza che manca, il pugno di B toglie", Pugno(), 0f);
        a.ChiediAdmin("danza no", "danza: ");
        Pausa(0.5);
        preso = Pugno();
        Console.WriteLine($"  Danza che prende, il pugno di B toglie {preso}");
        if (preso <= 0f) Male("con la Danza a 'prende' il pugno deve prendere");
        a.ChiediAdmin("danza caso", "danza: ");
        Pausa(3.5);   // la Danza finisce

        // 7. Rimbalzo: B a 4 m e C a 7 m, 12 a tutti e due; A finisce accanto a C.
        Tp(a, PX, PZ); Tp(b, PX + 4f, PZ); Tp(c, PX + 7f, PZ);
        Piena(b, c);
        pb = Vita(b); pc = Vita(c);
        Atteso("Rimbalzo", Prova(a, AbilityId.StaffRicochet), ErrorCode.None);
        Aspetta(() => Vita(b) < pb && Vita(c) < pc, 1.5);
        Pausa(0.3);
        Controlla("Rimbalzo, B perde", pb - Vita(b), 12f);
        Controlla("Rimbalzo, C perde", pc - Vita(c), 12f);
        Vicino("Rimbalzo, A finisce a", a, PX + 6f, PZ, 0.6f);
        Tp(c, PX - 20f, PZ);

        // 8. Folata: B a 2 m davanti e C a 3 m dietro, spinti via di 6 m, senza danno.
        Tp(a, PX, PZ); Tp(b, PX + 2f, PZ); Tp(c, PX - 3f, PZ);
        Piena(b, c);
        pb = Vita(b); pc = Vita(c);
        Atteso("Folata", Prova(a, AbilityId.StaffGust), ErrorCode.None);
        Pausa(0.3);
        Vicino("Folata, B spinto a", b, PX + 8f, PZ);
        Vicino("Folata, C spinto a", c, PX - 9f, PZ);
        Controlla("Folata, B perde", pb - Vita(b), 0f);
        Tp(c, PX - 20f, PZ);

        // 9. Passo d'ombra: nessuno davanti, non parte; B a 10 m: A arriva a un
        //    metro dietro di lui, e il colpo dopo fa 24 (doppio); quello dopo 12.
        Tp(b, PX, PZ + 10f);
        Atteso("Passo d'ombra senza nessuno davanti", Prova(a, AbilityId.StaffShadowStep), ErrorCode.AbilityNotUsable);
        Tp(b, PX + 10f, PZ);
        Piena(b);
        Atteso("Passo d'ombra", Prova(a, AbilityId.StaffShadowStep), ErrorCode.None);
        Pausa(0.2);
        Vicino("Passo d'ombra, A a", a, PX + 11f, PZ);
        prima = Vita(b);
        a.Colpisci(0.1);
        Controlla("Dopo il Passo d'ombra il colpo toglie", Perso(b, prima, 1.0), 24f);
        Pausa(0.6);
        prima = Vita(b);
        a.Colpisci(0.1);
        Controlla("Il colpo dopo ancora toglie", Perso(b, prima, 1.0), 12f);

        // 10. Ritorno: A ferma 5,5 s, poi lo Scatto di 6 m; il Ritorno la riporta
        //     dov'era 5 s prima.
        Tp(b, PX + 1.5f, PZ + 10f);
        Tp(a, PX, PZ);
        Pausa(5.5);
        Atteso("Scatto prima del Ritorno", Prova(a, AbilityId.StaffDash), ErrorCode.None);
        Pausa(0.2);
        Vicino("Scatto, A a", a, PX + 6f, PZ);
        Atteso("Ritorno", Prova(a, AbilityId.StaffRecall), ErrorCode.None);
        Pausa(0.2);
        Vicino("Ritorno, A torna a", a, PX, PZ);

        // Si lascia tutto pulito.
        foreach (var s in new[] { a, b, c }) { s.Admin("effetto azzera"); s.Admin("abilita azzera"); s.Admin("clearinv"); s.Admin("sethp 1000"); }
        a.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        Pausa(0.5);

        if (ok) Console.WriteLine("  OK: le dodici abilita del bastone fanno cio' che devono");
        return ok;
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
        // 40 + 25 della carne, esatti: le sonde nascono con la rigenerazione
        // ferma (P4), che prima poteva cadere fra "sethp 40" e il pasto e dare
        // 66 (P2, log a14c-giro-1).
        bool curaDaFerito     = daFerito.Success && daFerito.Message == "+25 vita"
                                && daFerito.Hp == 65;
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

    // ---- segno di prova --------------------------------------------------------
    //
    // Sul server delle prove ogni sonda nasce col segno di prova: lupi e
    // cinghiali normali non la vedono. Prima le belve dell'isola centrale
    // uccidevano le sonde a caso, e le prove fallivano per i lupi invece che
    // per la regola (backlog P1 e P1b). Qui si controlla che il segno ci sia e
    // che funzioni, e che togliendolo un lupo torni ad attaccare: altrimenti
    // le prove che vogliono una belva vera (B1, prima A12) non potrebbero farlo.

    public static bool SegnoDiProva(string token, string host, int port)
    {
        Console.WriteLine("--- segno di prova: le belve normali non vedono le sonde, finche' il segno c'e'");

        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }

        float Vita() { lock (s.Mondo) return s.Mondo.TryGetValue(s.Id, out var e) ? e.Extra0 : -1f; }

        const float PX = 310f, PZ = -290f;
        s.Admin($"tp {PX.ToString(Inv)} {PZ.ToString(Inv)}");
        if (!s.Attendi(() => Dist(s.X, s.Z, PX, PZ) < 2f, 8))
        { Console.WriteLine("  FALLITA: teletrasporto non arrivato"); return false; }
        s.Cammina(0f, 0f, 0.1);

        string segno = s.ChiediAdmin("segnoprova", "segno di prova: ");
        if (segno != "si") { Console.WriteLine($"  FALLITA: la sonda non nasce col segno (risposta: {segno ?? "nessuna"})"); return false; }

        s.Admin("sethp 1000");
        s.Attendi(() => false, 0.5);
        float piena = Vita();

        s.Admin("spawn wolf");
        EntityState lupo = null;
        s.Attendi(() =>
        {
            lupo = s.PiuVicina(EntityType.Wolf, s.X + 2f, s.Z);
            return lupo != null && Dist(lupo.X, lupo.Z, s.X + 2f, s.Z) < 1f;
        }, 5);
        if (lupo == null || Dist(lupo.X, lupo.Z, s.X + 2f, s.Z) >= 1f)
        { Console.WriteLine("  FALLITA: il lupo d'ufficio non e' comparso accanto alla sonda"); return false; }
        ulong idLupo = lupo.EntityId;

        bool ok = true;
        try
        {
            // Col segno: dieci secondi accanto a un lupo, nessun morso. Il lupo
            // vaga: ogni due secondi la sonda gli si rimette a un metro, se no
            // "nessun morso" passerebbe anche perche' se n'e' andato.
            float minima = piena, lontano = 0f;
            for (int i = 0; i < 5; i++)
            {
                EntityState l;
                lock (s.Mondo) s.Mondo.TryGetValue(idLupo, out l);
                if (l != null)
                {
                    lontano = MathF.Max(lontano, Dist(l.X, l.Z, s.X, s.Z));
                    s.Admin($"tp {(l.X + 1f).ToString(Inv)} {l.Z.ToString(Inv)}");
                }
                s.Attendi(() => { minima = MathF.Min(minima, Vita()); return false; }, 2);
            }
            Console.WriteLine($"  col segno: vita {piena} -> minima {minima} in 10 secondi accanto al lupo " +
                              $"(al piu' {lontano:F1} m prima di riavvicinarsi)");
            if (minima < piena) { Console.WriteLine("  FALLITA: il lupo ha morso una sonda col segno"); ok = false; }

            // Senza segno: il lupo deve attaccare. In quei dieci secondi il lupo
            // ha vagato per la sua tana (fino a 32 metri, oltre i 15 della
            // caccia): la sonda gli si rimette accanto (giro 2 del 5 novembre).
            if (s.ChiediAdmin("segnoprova no", "segno di prova: ") != "no")
            { Console.WriteLine("  FALLITA: il segno non si toglie"); return false; }
            EntityState oraLupo;
            lock (s.Mondo) s.Mondo.TryGetValue(idLupo, out oraLupo);
            if (oraLupo != null)
            {
                Console.WriteLine($"  il lupo ora e' a {Dist(oraLupo.X, oraLupo.Z, s.X, s.Z):F1} m: la sonda gli va accanto");
                s.Admin($"tp {(oraLupo.X + 1f).ToString(Inv)} {oraLupo.Z.ToString(Inv)}");
            }
            bool morsa = s.Attendi(() => Vita() < piena, 10);
            Console.WriteLine($"  senza segno: vita {Vita()}");
            if (!morsa) { Console.WriteLine("  FALLITA: senza segno il lupo non ha attaccato"); ok = false; }
        }
        finally
        {
            s.ChiediAdmin("segnoprova si", "segno di prova: ");
            s.Admin($"remove {idLupo}");
            s.Admin("sethp 1000");
            s.Attendi(() => false, 0.5);
        }

        if (ok) Console.WriteLine("  OK: col segno il lupo lascia stare la sonda, senza la morde");
        return ok;
    }

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

    // ---- avvisi sugli unici --------------------------------------------------
    //
    // Decisione di Tommaso (5 novembre, backlog A13): quando un unico si
    // ottiene, si perde, passa di mano o va distrutto, tutti i collegati lo
    // trovano nel registro. Due sonde: chi lo ottiene e lo butta, e l'altra che
    // lo raccoglie e lo distrugge; ognuna deve ricevere ogni avviso, anche
    // quelli che non la riguardano. In fondo l'unico distrutto si riottiene: e
    // la promessa scritta nell'avviso.
    //
    // Gli unici si danno e si buttano col comando admin "unico": il rituale li
    // da a caso, e la morte li butta a terra o li distrugge a caso.

    public static bool AvvisiSugliUnici(string token, string tokenAltro, string host, int port)
    {
        Console.WriteLine("--- avvisi sugli unici: ottenuto, perso, passato di mano, distrutto, a tutti");

        using var s = new Sonda(token, host, port);
        using var o = new Sonda(tokenAltro, host, port);
        if (!s.AttendiPronta() || !o.AttendiPronta())
        { Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco"); return false; }

        const float PX = 360f, PZ = -300f;
        s.Admin($"tp {PX.ToString(Inv)} {PZ.ToString(Inv)}");
        o.Admin($"tp {(PX + 12f).ToString(Inv)} {PZ.ToString(Inv)}");
        if (!s.Attendi(() => { o.Poll(); return Dist(s.X, s.Z, PX, PZ) < 2f && Dist(o.X, o.Z, PX + 12f, PZ) < 2f; }, 8))
        { Console.WriteLine("  FALLITA: teletrasporto non arrivato"); return false; }
        s.Cammina(0f, 0f, 0.1);
        s.Admin("clearinv");
        o.Admin("clearinv");
        s.Attendi(() => { o.Poll(); return false; }, 0.5);

        int Conta(Sonda x, WorldEventKind k) { lock (x.Eventi) return x.Eventi.Count(e => e.Kind == k); }
        bool ok = true;

        // I nomi con cui il server chiama le due sonde (backlog A13b): negli
        // avvisi "ottenuto" e "passato di mano" ci va quello di chi lo ottiene,
        // e mai quello di chi l'aveva prima.
        string nomeS = s.ChiediAdmin("unico chi", "unico: ");
        string nomeO = o.ChiediAdmin("unico chi", "unico: ");
        if (string.IsNullOrWhiteSpace(nomeS) || string.IsNullOrWhiteSpace(nomeO) || nomeS == nomeO)
        { Console.WriteLine($"  FALLITA: nomi delle sonde non validi ('{nomeS}', '{nomeO}')"); return false; }
        Console.WriteLine($"  sonde: '{nomeS}' e '{nomeO}'");

        // Aspetta che il conteggio di quel tipo cresca per tutte e due, e dice
        // il testo arrivato. Con <deve> il testo deve contenere quel nome; ogni
        // nome di <maiNomi> non deve esserci (se non e' parte del nome atteso).
        bool ArrivatoATutti(string passo, WorldEventKind k, int primaS, int primaO,
                            string deve = null, params string[] maiNomi)
        {
            bool arrivato = s.Attendi(() => { o.Poll(); return Conta(s, k) > primaS && Conta(o, k) > primaO; }, 6);
            string testo, testoO;
            lock (s.Eventi) testo = s.Eventi.LastOrDefault(e => e.Kind == k)?.Text ?? "(niente)";
            lock (o.Eventi) testoO = o.Eventi.LastOrDefault(e => e.Kind == k)?.Text ?? "(niente)";
            Console.WriteLine($"  {passo}: {k} a chi lo fa={Conta(s, k) > primaS}, all'altro={Conta(o, k) > primaO} — \"{testo}\"");
            if (!arrivato) { Console.WriteLine($"  FALLITA ({passo}): l'avviso non e' arrivato a tutti"); return false; }
            bool bene = true;
            if (testo != testoO)
            { Console.WriteLine($"  FALLITA ({passo}): le due sonde hanno letto testi diversi (\"{testoO}\")"); bene = false; }
            if (deve != null && !testo.Contains(deve))
            { Console.WriteLine($"  FALLITA ({passo}): manca il nome di chi lo ottiene, '{deve}'"); bene = false; }
            foreach (var n in maiNomi)
                if ((deve == null || !deve.Contains(n)) && testo.Contains(n))
                { Console.WriteLine($"  FALLITA ({passo}): c'e' il nome '{n}', che non ci deve essere"); bene = false; }
            return bene;
        }

        // 1. ottenuto
        int os0 = Conta(s, WorldEventKind.UniqueObtained), oo0 = Conta(o, WorldEventKind.UniqueObtained);
        var dato = s.ChiediAdmin("unico dai", "unico: ");
        if (dato == null || dato == "nessuno")
        { Console.WriteLine($"  FALLITA: nessun unico libero da dare (risposta '{dato}')"); return false; }
        string id = dato.Split(' ')[0];
        Console.WriteLine($"  dato l'unico {dato}");
        ok &= ArrivatoATutti("ottenuto", WorldEventKind.UniqueObtained, os0, oo0, nomeS, nomeO);
        string diS = s.ChiediAdmin($"unico stato {id}", "unico: ");

        // 2. perso: a terra, a quattro metri dalla sonda
        int ls0 = Conta(s, WorldEventKind.UniqueLost), lo0 = Conta(o, WorldEventKind.UniqueLost);
        var aTerra = s.ChiediAdmin("unico butta", "unico: ");
        var pezzi = aTerra?.Split(' ');
        // "a terra <entita> <x> <z>"
        if (pezzi == null || pezzi.Length < 5 || pezzi[0] != "a")
        { Console.WriteLine($"  FALLITA: l'unico non e' stato buttato (risposta '{aTerra}')"); return false; }
        float bx = float.Parse(pezzi[3], Inv), bz = float.Parse(pezzi[4], Inv);
        ok &= ArrivatoATutti("perso", WorldEventKind.UniqueLost, ls0, lo0, null, nomeS, nomeO);

        // 3. passato di mano: l'altra sonda ci passa sopra e lo raccoglie
        int hs0 = Conta(s, WorldEventKind.UniqueChangedHands), ho0 = Conta(o, WorldEventKind.UniqueChangedHands);
        o.Admin($"tp {bx.ToString(Inv)} {bz.ToString(Inv)}");
        ok &= ArrivatoATutti("passato di mano", WorldEventKind.UniqueChangedHands, hs0, ho0, nomeO, nomeS);
        string diO = o.ChiediAdmin($"unico stato {id}", "unico: ");
        if (diO == null || diO == "libero" || diO == diS)
        {
            Console.WriteLine($"  FALLITA: nel registro l'unico non e' passato all'altra (prima '{diS}', ora '{diO}')");
            ok = false;
        }

        // 4. distrutto: l'altra si svuota lo zaino
        int ds0 = Conta(s, WorldEventKind.UniqueDestroyed), do0 = Conta(o, WorldEventKind.UniqueDestroyed);
        o.Admin("clearinv");
        ok &= ArrivatoATutti("distrutto", WorldEventKind.UniqueDestroyed, ds0, do0, null, nomeS, nomeO);
        string dopo = s.ChiediAdmin($"unico stato {id}", "unico: ");
        if (dopo != "libero")
        { Console.WriteLine($"  FALLITA: distrutto ma non e' tornato libero ('{dopo}')"); ok = false; }

        // 5. e ora si riottiene, proprio quello
        int rs0 = Conta(s, WorldEventKind.UniqueObtained), ro0 = Conta(o, WorldEventKind.UniqueObtained);
        var ridato = s.ChiediAdmin($"unico dai {id}", "unico: ");
        if (ridato == null || !ridato.StartsWith(id + " "))
        { Console.WriteLine($"  FALLITA: l'unico distrutto non si riottiene (risposta '{ridato}')"); ok = false; }
        else ok &= ArrivatoATutti("riottenuto", WorldEventKind.UniqueObtained, rs0, ro0, nomeS, nomeO);

        // Pulizia: le prove dopo trovano il catalogo libero come all'inizio.
        s.Admin("clearinv");
        s.Attendi(() => { o.Poll(); return false; }, 0.5);

        if (ok) Console.WriteLine("  OK: ogni passaggio dell'unico arriva a tutti col nome giusto, e il distrutto torna disponibile");
        return ok;
    }

    // ---- a terra contro le creature ---------------------------------------------
    //
    // Decisione di Tommaso (5 novembre, backlog B1, che annulla A12): contro
    // qualunque creatura non si muore, si va a terra 30 secondi e ci si rialza
    // con meta' vita dove si e' caduti. Lo zaino resta sempre: anche sull'isola
    // centrale, anche per l'orda e per gli Echi, dove con A12 si perdeva. Il
    // costo e' il 10% della durabilita' massima di cio' che si impugna: la
    // spada di pietra (55 usi) ne perde 6, lo scudo (80) ne perde 8.
    //
    // Quattro cadute, ognuna in un punto suo, come la prova di A12 che
    // sostituisce. Nella prima c'e' anche un'altra sonda accanto: il lupo,
    // mandata a terra la prima, passa a lei. E nella prima si aspetta che la
    // sonda si rialzi da sola; nelle altre la si rialza d'ufficio.
    //
    // La sonda ha un punto di vita, cosi' va a terra al primo colpo. Il segno di
    // prova si toglie solo per le belve normali: l'orda e gli Echi lo ignorano.

    public static bool ATerraControLeCreature(string token, string tokenAltro, string host, int port)
    {
        Console.WriteLine("--- a terra contro le creature: niente morte, zaino intatto, usura, e ci si rialza");

        using var s = new Sonda(token, host, port);
        using var o = new Sonda(tokenAltro, host, port);
        if (!s.AttendiPronta() || !o.AttendiPronta())
        { Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco"); return false; }

        bool ok = true;
        ok &= ATerraPerUnaCreatura(s, o,    "lupo fuori dall'isola",          320f, -270f, "lupo");
        ok &= ATerraPerUnaCreatura(s, null, "lupo sull'isola centrale",       -25f,  -35f, "lupo");
        ok &= ATerraPerUnaCreatura(s, null, "Eco dell'orda fuori dall'isola", 335f, -285f, "orda");
        ok &= ATerraPerUnaCreatura(s, null, "Echi fuori dall'isola",          305f, -275f, "echi");
        if (ok) Console.WriteLine("  OK: contro le creature si va a terra, lo zaino resta, si paga in usura e ci si rialza");
        return ok;
    }

    /// Con <paramref name="altra"/> si prova anche che la belva passa a lei e
    /// che ci si rialza da soli dopo 30 secondi.
    static bool ATerraPerUnaCreatura(Sonda s, Sonda altra, string nome, float px, float pz, string chi)
    {
        void Gira() => altra?.Poll();

        s.Admin($"tp {px.ToString(Inv)} {pz.ToString(Inv)}");
        altra?.Admin($"tp {(px - 5f).ToString(Inv)} {pz.ToString(Inv)}");
        if (!s.Attendi(() => { Gira(); return Dist(s.X, s.Z, px, pz) < 2f && (altra == null || Dist(altra.X, altra.Z, px - 5f, pz) < 2f); }, 8))
        { Console.WriteLine($"  FALLITA ({nome}): teletrasporto non arrivato"); return false; }
        s.Cammina(0f, 0f, 0.1);
        altra?.Cammina(0f, 0f, 0.1);
        s.Admin("clearinv");
        s.Admin("give Wood 20");
        s.Admin("give StoneSword 1");
        s.Admin("give Shield 1");
        if (!s.Attendi(() => s.Quanti(ItemType.Wood) == 20 && s.Quanti(ItemType.Shield) == 1 && s.Quanti(ItemType.StoneSword) == 1, 8))
        { Console.WriteLine($"  FALLITA ({nome}): preparazione dello zaino non riuscita (legno {s.Quanti(ItemType.Wood)})"); return false; }
        s.Dotazione(ItemType.StoneSword, ItemType.None, SideItemType.Shield);
        s.Attendi(() => false, 0.3);
        string usuraPrima = s.ChiediAdmin("aterra usura", "a terra: ");
        if (usuraPrima != "usura StoneSword=55/55 Shield=80/80")
        { Console.WriteLine($"  FALLITA ({nome}): dotazione non pronta ('{usuraPrima}')"); return false; }
        s.Admin("sethp 1");
        s.Attendi(() => { Gira(); return false; }, 0.5);
        float cadeX = s.X, cadeZ = s.Z;

        ulong idBelva = 0;
        if (chi == "lupo" && s.ChiediAdmin("segnoprova no", "segno di prova: ") != "no")
        { Console.WriteLine($"  FALLITA ({nome}): il segno non si toglie"); return false; }

        bool ok = true;
        try
        {
            if (chi == "echi") s.Admin("echi evoca Common");
            else
            {
                // L'orda del santuario e' fatta di Echi (A1a4a2): "spawn orda"
                // fa nascere un Eco dell'orda.
                s.Admin(chi == "orda" ? "spawn orda" : "spawn wolf");
                var tipo = chi == "orda" ? EntityType.Echo : EntityType.Wolf;
                EntityState lupo = null;
                s.Attendi(() =>
                {
                    Gira();
                    lupo = s.PiuVicina(tipo, px + 2f, pz);
                    return lupo != null && Dist(lupo.X, lupo.Z, px + 2f, pz) < 1.5f;
                }, 5);
                // Chi e' dell'orda caccia subito: puo' essere gia' a qualche
                // metro quando la sonda lo vede.
                if (lupo != null && Dist(lupo.X, lupo.Z, px, pz) < 6f) idBelva = lupo.EntityId;
            }

            bool caduta = s.Attendi(() => { Gira(); return s.Stato(s.Id).ATerra; }, 20);
            var st = s.Stato(s.Id);
            if (!caduta)
            {
                Console.WriteLine($"  FALLITA ({nome}): la sonda non e' andata a terra in 20 secondi (vita {st.Vita}, morta {st.Morto})");
                return false;
            }
            // Un attimo per l'usura (si toglie a fine tick) e per i messaggi.
            s.Attendi(() => { Gira(); return false; }, 1.0);
            st = s.Stato(s.Id);
            string risposta = s.ChiediAdmin("aterra", "a terra: ");
            string usura = s.ChiediAdmin("aterra usura", "a terra: ");
            int legno = s.Quanti(ItemType.Wood);
            float spostata = Dist(s.X, s.Z, cadeX, cadeZ);
            Console.WriteLine($"  {nome}: a terra={st.ATerra}, morta={st.Morto}, vita {st.Vita}, secondi {st.Secondi} " +
                              $"(server: '{risposta}'), legno 20 -> {legno}, {usura}, spostata di {spostata:F1} m");
            if (st.Morto || st.Vita != 0f) { Console.WriteLine($"  FALLITA ({nome}): doveva essere a terra con zero vita, non morta"); ok = false; }
            if (st.Secondi < 25f || st.Secondi > 30f) { Console.WriteLine($"  FALLITA ({nome}): i secondi al rialzarsi non tornano"); ok = false; }
            if (legno != 20 || s.Quanti(ItemType.StoneSword) != 1 || s.Quanti(ItemType.Shield) != 1)
            { Console.WriteLine($"  FALLITA ({nome}): lo zaino doveva restare intatto"); ok = false; }
            if (usura != "usura StoneSword=49/55 Shield=72/80")
            { Console.WriteLine($"  FALLITA ({nome}): l'usura doveva essere il 10% del massimo (spada 49, scudo 72)"); ok = false; }
            if (spostata > 1.5f) { Console.WriteLine($"  FALLITA ({nome}): chi e' a terra non resta dove e' caduto"); ok = false; }

            if (altra != null)
            {
                // La belva passa all'altra sonda, appena le si toglie il segno.
                altra.ChiediAdmin("segnoprova no", "segno di prova: ");
                float vitaAltra = Vita(altra, altra.Id);
                bool morsa = s.Attendi(() => { Gira(); return Vita(altra, altra.Id) < vitaAltra; }, 12);
                var dopo = s.Stato(s.Id);
                Console.WriteLine($"  il lupo passa all'altra: morsa={morsa} (vita {vitaAltra} -> {Vita(altra, altra.Id)}); " +
                                  $"la prima resta a terra={dopo.ATerra} con vita {dopo.Vita}");
                if (!morsa) { Console.WriteLine($"  FALLITA ({nome}): il lupo non e' passato a chi e' in piedi"); ok = false; }
                if (!dopo.ATerra || dopo.Vita != 0f) { Console.WriteLine($"  FALLITA ({nome}): chi e' a terra e' stato toccato"); ok = false; }
                if (idBelva != 0) { s.Admin($"remove {idBelva}"); idBelva = 0; }
                altra.ChiediAdmin("segnoprova si", "segno di prova: ");
                altra.Admin("aterra rialza");
                altra.Admin("sethp 1000");

                // E ci si rialza da soli, dove si era caduti, con meta' vita.
                bool rialzata = s.Attendi(() => { Gira(); return !s.Stato(s.Id).ATerra; }, 35);
                s.Attendi(() => { Gira(); return false; }, 0.5);
                var su = s.Stato(s.Id);
                float spostataDopo = Dist(s.X, s.Z, cadeX, cadeZ);
                Console.WriteLine($"  dopo mezzo minuto: rialzata={rialzata}, vita {su.Vita}, morta={su.Morto}, " +
                                  $"spostata di {spostataDopo:F1} m, legno {s.Quanti(ItemType.Wood)}");
                // Si rialza a 50 esatti: con la rigenerazione ferma delle sonde
                // (P4) non cade piu' il punto in piu' del giro 1 di A14d (P3).
                if (!rialzata || su.Vita != 50f || su.Morto)
                { Console.WriteLine($"  FALLITA ({nome}): dopo 30 secondi doveva rialzarsi con 50 di vita"); ok = false; }
                if (spostataDopo > 1.5f) { Console.WriteLine($"  FALLITA ({nome}): non si e' rialzata dove era caduta"); ok = false; }
            }
        }
        finally
        {
            if (chi == "lupo") s.ChiediAdmin("segnoprova si", "segno di prova: ");
            if (idBelva != 0) s.Admin($"remove {idBelva}");
            // Un Eco dell'orda rimasto in vita terrebbe il santuario a riposo.
            if (chi == "orda") s.Admin("santuario orda uccidi");
            if (chi == "echi") s.Admin("echi uccidi");
            s.Attendi(() => { Gira(); return false; }, 0.3);
            s.Admin("aterra rialza");
            s.Admin("sethp 1000");
            s.Attendi(() => { Gira(); return false; }, 0.5);
        }
        return ok;
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
    // fazioni davanti e 2 cristalli alti fra loro. Quando si richiude si
    // sveglia il Custode, e finche' vive non si offre (B4); battuto lui si apre
    // una finestra per offrire, e alla sua fine contano solo chi e' dentro e
    // quello che ha offerto. Sotto il 25% la terra si ripara, sopra i
    // cristalli si perdono. Dopo, un'ora di riposo; mezz'ora se vince il
    // Custode (tutti a terra), tre ore se e' battuto e nessuno offre.
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
        // Le belve dell'isola centrale non si scacciano piu: le sonde hanno il
        // segno di prova e lupi e cinghiali normali non le vedono (backlog P1b).
        // Prima uno di loro ne uccideva una davanti all'ingresso, e lei
        // rinasceva allo stendardo a 210 metri (backlog P1).
        return arrivate;
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

    /// Rimette in piedi chi e' a terra (comando admin "aterra rialza", B1):
    /// prima si aspettavano i tre secondi della rinascita, ora a terra si
    /// resta mezzo minuto.
    static void RialzaTutte(Sonda[] ps)
    {
        foreach (var p in ps) p.Admin("aterra rialza");
        Aggiorna(ps);
        ps[0].Attendi(() => false, 0.5);
    }

    /// Finche' il Custode vive il centro del mondo rifiuta le offerte (B4).
    static bool RifiutaFincheVive(Sonda s, string quando)
    {
        s.Rituale(CentroDelMondo, CrystalType.Precursor);
        bool risposto = s.Attendi(() => s.UltimoRituale != null, 4);
        var r = s.UltimoRituale;
        Console.WriteLine($"  offerta {quando}: accettata={r?.Accepted}, \"{r?.Refusal}\"");
        if (risposto && r.Accepted == false && r.Refusal?.Contains("Custode") == true) return true;
        Console.WriteLine($"  FALLITA: {quando} il centro del mondo doveva rifiutare l'offerta per il Custode");
        return false;
    }

    /// <summary>
    /// Il Custode indebolito d'ufficio a 1 di vita, e la sonda lo finisce a
    /// pugni: gli si mette addosso, cosi' e' lui il piu' vicino a portata e non
    /// una sonda dell'altra fazione. Vero se e' morto.
    /// </summary>
    static bool BattiIlCustode(Sonda s)
    {
        StatoCustode(s, "vita 1");
        var fine = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < fine)
        {
            var st = StatoCustode(s);
            if (st.GetValueOrDefault("stato") == "assente") return true;
            if (!float.TryParse(st.GetValueOrDefault("x"), System.Globalization.NumberStyles.Float, Inv, out var kx) ||
                !float.TryParse(st.GetValueOrDefault("z"), System.Globalization.NumberStyles.Float, Inv, out var kz))
                return false;
            s.Admin("aterra rialza");
            s.Admin($"tp {kx.ToString(Inv)} {kz.ToString(Inv)}");
            s.Attendi(() => false, 0.2);
            s.Colpisci(1.0);
        }
        return StatoCustode(s).GetValueOrDefault("stato") == "assente";
    }

    /// Il santuario riposa, per quasi i secondi attesi (B4: un'ora, mezz'ora,
    /// tre ore).
    static bool RiposaPer(Sonda s, int secondi, string quando)
    {
        var st = s.StatoSantuario();
        int resta = Sonda.Numero(st, "riposo");
        Console.WriteLine($"  riposo {quando}: stato {st.GetValueOrDefault("stato")}, {resta} s (attesi {secondi})");
        if (st.GetValueOrDefault("stato") == "Resting" && resta <= secondi && resta >= secondi - 60) return true;
        Console.WriteLine($"  FALLITA: {quando} il santuario doveva riposare {secondi} secondi");
        return false;
    }

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

            // --- il Custode (B3): con l'ingresso aperto dorme
            var custodeAperto = StatoCustode(capo);
            Console.WriteLine($"  il Custode con l'ingresso aperto: {Sonda.Descrivi(custodeAperto)}");
            if (custodeAperto.GetValueOrDefault("stato") != "dorme")
            { Console.WriteLine("  FALLITA: con l'ingresso aperto il Custode non dorme"); ok = false; }

            // --- finche' il Custode vive il centro del mondo non accetta offerte
            // (B4): con l'ingresso aperto dorme, e rifiuta lo stesso.
            if (!RifiutaFincheVive(capo, "con l'ingresso aperto")) ok = false;

            capo.Admin("santuario chiudi");
            // Alla chiusura il Custode si sveglia, con la fascia della terra al
            // 20% (dal 5% al 25%: 400 di vita per giocatore, danno 35) e la
            // vita per gli undici dentro.
            var custodeChiuso = StatoCustode(capo);
            var lotta = capo.StatoSantuario();
            Console.WriteLine($"  il Custode alla chiusura, in undici dentro: {Sonda.Descrivi(custodeChiuso)}; santuario {lotta.GetValueOrDefault("stato")}");
            if (custodeChiuso.GetValueOrDefault("stato") != "sveglio" || Sonda.Numero(custodeChiuso, "fascia") != 1
                || Sonda.Numero(custodeChiuso, "piena") != 4400 || Sonda.Numero(custodeChiuso, "danno") != 35)
            { Console.WriteLine("  FALLITA: alla chiusura il Custode doveva svegliarsi con fascia 1, vita 4400 e danno 35"); ok = false; }
            if (lotta.GetValueOrDefault("stato") != "Fight")
            { Console.WriteLine("  FALLITA: alla chiusura il santuario non e' in lotta col Custode"); ok = false; }
            if (!RifiutaFincheVive(capo, "col Custode sveglio")) ok = false;

            // --- le sonde lo battono: indebolito d'ufficio, il capo lo finisce a
            // pugni. Si apre la finestra per offrire.
            bool battuto = BattiIlCustode(capo);
            var finestra = capo.StatoSantuario();
            Console.WriteLine($"  Custode battuto a pugni={battuto}; santuario {finestra.GetValueOrDefault("stato")}, " +
                              $"offerte per {finestra.GetValueOrDefault("offerte")} s");
            if (!battuto) { Console.WriteLine("  FALLITA: la sonda non e' riuscita a battere il Custode indebolito"); ok = false; }
            if (finestra.GetValueOrDefault("stato") != "Offering" || Sonda.Numero(finestra, "offerte") is < 55 or > 60)
            { Console.WriteLine("  FALLITA: battuto il Custode non si e' aperta la finestra di un minuto per offrire"); ok = false; }
            // Chi il Custode ha mandato a terra non potrebbe offrire.
            RialzaTutte(ps);

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

            // Finisce la finestra: il rituale si compie con chi e' dentro.
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
            if (!RiposaPer(capo, 3600, "dopo la riparazione")) ok = false;

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
            // Chi il Custode ha mandato a terra non camminerebbe fino al muro.
            RialzaTutte(ps);
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
            capo.Admin("santuario chiudi");
            StatoCustode(capo, "uccidi");   // battuto: si apre la finestra
            RialzaTutte(ps);
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

            // --- l'orda (Tommaso, A1a3c2b): dieci dentro fanno 50 piccoli (il
            // minimo), piu' un mini boss per il Precursore e uno per l'Epico.
            // Prima uccide chi e' dentro, poi si sparge; finche' vive il
            // santuario riposa; i mini boss lasciano cadere i loro cristalli.
            // E' fatta di Echi (A1a4a2): 25 Eco e 25 Vaganti, un Divoratore
            // per l'Epico e un Araldo per il Precursore.
            var orda = capo.StatoSantuario();
            Console.WriteLine($"  l'orda appena sprigionata: {Sonda.Descrivi(orda)}");
            if (orda.GetValueOrDefault("orda") != "50" || orda.GetValueOrDefault("capi") != "2"
                || orda.GetValueOrDefault("in attesa") != "2")
            { Console.WriteLine("  FALLITA: l'orda non ha 50 piccoli e 2 mini boss"); ok = false; }
            if (orda.GetValueOrDefault("eco") != "25" || orda.GetValueOrDefault("vaganti") != "25"
                || orda.GetValueOrDefault("divoratori") != "1" || orda.GetValueOrDefault("araldi") != "1")
            { Console.WriteLine("  FALLITA: l'orda non e' di 25 Eco, 25 Vaganti, un Divoratore e un Araldo"); ok = false; }

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
            // Contro l'orda non si muore, si va a terra per 30 secondi (B1): le
            // prove dopo vogliono le sonde in piedi, e le si rialza d'ufficio.
            RialzaTutte(ps);

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
            if (!nove) { Console.WriteLine("  FALLITA: non si e' aperto per i nove"); ok = false; }

            // Anche qui il Custode si e' svegliato, coi nove e la terra al 100%:
            // 600 per giocatore.
            var custodeNove = StatoCustode(capo);
            Console.WriteLine($"  il Custode, in nove: {Sonda.Descrivi(custodeNove)}");
            if (custodeNove.GetValueOrDefault("stato") != "sveglio" || Sonda.Numero(custodeNove, "piena") != 5400)
            { Console.WriteLine("  FALLITA: in nove con la terra al 100% il Custode doveva svegliarsi con vita 5400"); ok = false; }

            // --- vince il Custode (B4): tutti e nove a terra nello stesso
            // momento. Fuori tutti, in piedi e con lo zaino intatto, il Custode a
            // vita piena, e mezz'ora di riposo.
            foreach (var p in ps.Take(9)) p.Admin("aterra cadi");
            capo.Attendi(() => false, 2.0);
            Aggiorna(ps);
            var vinto = StatoCustode(capo);
            int fuori = ps.Take(9).Count(p => !Dentro(p) && Dist(p.X, p.Z, IngressoXZ, IngressoXZ) < 3f);
            int inPiedi = ps.Take(9).Count(p => !p.Stato(p.Id).ATerra);
            Console.WriteLine($"  tutti e nove a terra: fuori davanti all'ingresso {fuori}/9, in piedi {inPiedi}/9, " +
                              $"Precursore {capo.Cristalli(CrystalType.Precursor)}, Epico {epico.Cristalli(CrystalType.Epic)}; " +
                              $"custode {Sonda.Descrivi(vinto)}");
            if (fuori != 9 || inPiedi != 9)
            { Console.WriteLine("  FALLITA: vinto il Custode, il santuario non ha rimesso fuori tutti in piedi"); ok = false; }
            if (capo.Cristalli(CrystalType.Precursor) != 1 || epico.Cristalli(CrystalType.Epic) != 1)
            { Console.WriteLine("  FALLITA: vinto il Custode, qualcuno ha perso un cristallo"); ok = false; }
            if (vinto.GetValueOrDefault("stato") != "dorme" || Sonda.Numero(vinto, "vita") != Sonda.Numero(vinto, "piena"))
            { Console.WriteLine("  FALLITA: vinto, il Custode non dorme a vita piena"); ok = false; }
            if (!RiposaPer(capo, 1800, "dopo la vittoria del Custode")) ok = false;

            // --- battuto, ma nessuno offre: tre ore di riposo, nessun cristallo perso
            capo.Admin("santuario riposo");
            RialzaTutte(ps);
            bool noveDiNuovo = ApriInNove();
            capo.Admin("santuario chiudi");
            StatoCustode(capo, "uccidi");
            capo.UltimoRituale = null;
            capo.Admin("santuario chiudi");
            capo.Attendi(() => capo.UltimoRituale != null, 3);
            Console.WriteLine($"  in nove, battuto senza offerte: entrati={noveDiNuovo}, Precursore {capo.Cristalli(CrystalType.Precursor)}, " +
                              $"Epico {epico.Cristalli(CrystalType.Epic)}, risposta \"{capo.UltimoRituale?.Refusal}\"");
            if (!noveDiNuovo) { Console.WriteLine("  FALLITA: finito il riposo non si e' riaperto per i nove"); ok = false; }
            if (capo.Cristalli(CrystalType.Precursor) != 1 || epico.Cristalli(CrystalType.Epic) != 1)
            { Console.WriteLine("  FALLITA: senza offerte qualcuno ha perso un cristallo"); ok = false; }
            if (capo.UltimoRituale?.Refusal?.Contains("nessuno ha offerto") != true)
            { Console.WriteLine("  FALLITA: battuto senza offerte, il santuario non l'ha detto"); ok = false; }
            if (!RiposaPer(capo, 3 * 3600, "battuto senza offerte")) ok = false;

            // --- ora si offre: rituale incompleto.
            // Nessun Echo d'altre prove in giro: si contano quelli del rito.
            capo.Admin("santuario riposo");
            capo.StatoEchi("uccidi");
            RialzaTutte(ps);
            bool noveAncora = ApriInNove();
            capo.Admin("santuario chiudi");
            StatoCustode(capo, "uccidi");
            RialzaTutte(ps);
            capo.Rituale(CentroDelMondo, CrystalType.Precursor);
            epico.Rituale(CentroDelMondo, CrystalType.Epic);
            capo.Attendi(() => capo.UltimoRituale?.Accepted == true, 4);
            epico.Attendi(() => epico.UltimoRituale?.Accepted == true, 4);
            capo.Admin("santuario chiudi");
            capo.Attendi(() => capo.Cristalli(CrystalType.Precursor) == 0, 6);
            epico.Attendi(() => epico.Cristalli(CrystalType.Epic) == 0, 3);
            capo.Attendi(() => capo.UltimoRituale?.Bane != null, 3);
            Console.WriteLine($"  in nove, con le offerte: riaperto={noveAncora}, Precursore {capo.Cristalli(CrystalType.Precursor)}, " +
                              $"Epico {epico.Cristalli(CrystalType.Epic)}, chunk piu' arido {ChunkPiuArido(capo)}/255, " +
                              $"risposta \"{capo.UltimoRituale?.Bane}\"");
            if (!noveAncora) { Console.WriteLine("  FALLITA: finito il riposo non si e' riaperto per i nove"); ok = false; }
            if (!RiposaPer(capo, 3600, "dopo il rituale incompleto")) ok = false;
            if (capo.Cristalli(CrystalType.Precursor) != 0 || epico.Cristalli(CrystalType.Epic) != 0)
            { Console.WriteLine("  FALLITA: nel rituale incompleto i cristalli offerti non si sono persi"); ok = false; }
            if (ChunkPiuArido(capo) != 255)
            { Console.WriteLine("  FALLITA: il rituale incompleto ha cambiato la terra"); ok = false; }
            if (capo.UltimoRituale?.Bane?.Contains("incompleto") != true)
            { Console.WriteLine("  FALLITA: il rituale incompleto non ha risposto"); ok = false; }

            // Gli Echi del rito incompleto (proposta A1a4, punto 8): uno
            // schieramento per cristallo offerto, di casa dentro il santuario.
            // Epico: 6 Eco, 2 Vaganti, un Divoratore; Precursore: 6 Eco, 2
            // Vaganti, un Araldo. Nessuno porta il cristallo: e' gia' tornato in
            // circolo. Non sono l'orda: il santuario non ne conta nessuno.
            var echiRito = capo.StatoEchi();
            var ordaRito = capo.StatoSantuario();
            Console.WriteLine($"  echi del rito incompleto: {Sonda.Descrivi(echiRito)}; orda {ordaRito.GetValueOrDefault("orda")}, " +
                              $"capi {ordaRito.GetValueOrDefault("capi")}");
            if (Sonda.Numero(echiRito, "eco") != 12 || Sonda.Numero(echiRito, "vaganti") != 4
                || Sonda.Numero(echiRito, "divoratori") != 1 || Sonda.Numero(echiRito, "araldi") != 1)
            { Console.WriteLine("  FALLITA: il rito incompleto non ha chiamato gli Echi di un Epico e di un Precursore"); ok = false; }
            if (Sonda.Numero(echiRito, "di casa nel santuario") != 18)
            { Console.WriteLine("  FALLITA: gli Echi del rito incompleto non sono di casa nel santuario"); ok = false; }
            if (Sonda.Numero(echiRito, "con cristallo") != 0)
            { Console.WriteLine("  FALLITA: gli Echi del rito incompleto portano un cristallo che era gia' perso"); ok = false; }
            if (ordaRito.GetValueOrDefault("orda") != "0" || ordaRito.GetValueOrDefault("capi") != "0")
            { Console.WriteLine("  FALLITA: il rito incompleto ha sprigionato l'orda"); ok = false; }
            var echiUccisi = capo.StatoEchi("uccidi");
            // Il confronto e' con un attimo prima, non con prima del rito: e'
            // rimasto cosi' da quando chi moriva per gli Echi lasciava cadere lo
            // zaino (A12, annullato da B1).
            if (Sonda.Numero(echiUccisi, "cristalli alti a terra") != Sonda.Numero(echiRito, "cristalli alti a terra"))
            { Console.WriteLine($"  FALLITA: uccisi gli Echi del rito incompleto, e' caduto un cristallo: {Sonda.Descrivi(echiUccisi)}"); ok = false; }
            // Chi e' andato a terra per gli Echi si rialza d'ufficio (B1).
            RialzaTutte(ps);

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

    // ---- 15b2. il Custode -------------------------------------------------------
    //
    // Decisioni di Tommaso (backlog B3, decisioni/boss.md): il Custode c'e
    // sempre dentro il santuario e dorme; si sveglia quando l'ingresso si
    // richiude (lo prova "santuario"); quattro fasce secondo la vitalita, e la
    // vita per giocatore dentro al risveglio. Qui, con una sonda sola e il
    // comando admin "custode": le fasce, che addormentato non attacca, che
    // sveglio manda a terra chi e dentro e poi torna a dormire, e che battuto
    // ne nasce un altro.

    static Dictionary<string, string> StatoCustode(Sonda s, string argomenti = "")
    {
        var r = s.ChiediAdmin(("custode " + argomenti).Trim(), "custode: ");
        var stato = new Dictionary<string, string>();
        if (r == null) return stato;
        foreach (var voce in r.Split(", "))
        {
            int sp = voce.LastIndexOf(' ');
            if (sp > 0) stato[voce[..sp]] = voce[(sp + 1)..];
        }
        return stato;
    }

    public static bool Custode(string token, string host, int port)
    {
        Console.WriteLine("--- il Custode: dorme nel santuario, quattro fasce, vita per chi e' dentro, a terra chi perde");

        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }

        bool ok = true;
        try
        {
            s.Admin("santuario angolo 0");
            s.Admin("santuario riposo");
            s.Admin("setvitality 1");
            s.Admin($"tp {IngressoXZ.ToString(Inv)} {IngressoXZ.ToString(Inv)}");
            s.Attendi(() => Dist(s.X, s.Z, IngressoXZ, IngressoXZ) < 2f, 6);
            s.Attendi(() => false, 1.5);   // il secondo del santuario: lo sposta al centro

            var st = StatoCustode(s);
            float dalCentro = st.TryGetValue("dal centro", out var dc) && float.TryParse(dc, System.Globalization.NumberStyles.Float, Inv, out var f) ? f : -1f;
            EntityState visto;
            lock (s.Mondo) visto = s.Mondo.Values.FirstOrDefault(e => e.Type == EntityType.Keeper);
            Console.WriteLine($"  all'inizio: {Sonda.Descrivi(st)}; visto dal client: " +
                              (visto == null ? "no" : $"vita {visto.Extra0}, piena {visto.Extra1}, sveglio {visto.Extra2}, fascia {visto.Extra3}, " +
                               $"a {Dist(visto.X, visto.Z, CentroXZ, CentroXZ):F1} m dal centro"));
            if (st.GetValueOrDefault("stato") != "dorme" || dalCentro < 0f || dalCentro > 0.5f)
            { Console.WriteLine("  FALLITA: il Custode non dorme al centro del santuario"); ok = false; }
            if (visto == null || visto.Extra2 != 0f || visto.Extra3 != 2)
            { Console.WriteLine("  FALLITA: il client non vede il Custode addormentato con la fascia della terra viva"); ok = false; }

            // --- le quattro fasce, svegliato d'ufficio "come se" dentro fossero in tre
            var fasce = new (string Vit, int Fascia, int PerGiocatore, int Danno)[]
            {
                ("0.02",  0, 250, 25),   // carestia
                ("0.2",   1, 400, 35),   // dal 5% al 25%
                ("0.625", 2, 600, 50),   // sopra il 25%
                ("1.2",   3, 800, 60),   // era rigogliosa
            };
            foreach (var (vit, fascia, perG, danno) in fasce)
            {
                s.Admin($"setvitality {vit}");
                s.Attendi(() => false, 0.3);
                var sv = StatoCustode(s, "sveglia 3");
                Console.WriteLine($"  terra a {vit}, svegliato in tre: {Sonda.Descrivi(sv)}");
                if (sv.GetValueOrDefault("stato") != "sveglio" || Sonda.Numero(sv, "fascia") != fascia
                    || Sonda.Numero(sv, "piena") != 3 * perG || Sonda.Numero(sv, "vita") != 3 * perG
                    || Sonda.Numero(sv, "danno") != danno)
                { Console.WriteLine($"  FALLITA: terra a {vit}: atteso fascia {fascia}, vita {3 * perG}, danno {danno}"); ok = false; }
                var dorme = StatoCustode(s, "dormi");
                if (dorme.GetValueOrDefault("stato") != "dorme")
                { Console.WriteLine("  FALLITA: il Custode non torna a dormire d'ufficio"); ok = false; }
            }
            s.Admin("setvitality 1");
            s.Attendi(() => false, 1.2);

            // --- addormentato non attacca: tre secondi accanto, vita intatta
            float vx = CentroXZ + 1.5f * Diag, vz = CentroXZ + 1.5f * Diag;
            s.Admin("sethp 100");
            s.Admin($"tp {vx.ToString(Inv)} {vz.ToString(Inv)}");
            s.Attendi(() => Dist(s.X, s.Z, vx, vz) < 1f, 6);
            s.Cammina(0f, 0f, 0.1);
            s.Attendi(() => false, 3.0);
            var accanto = s.Stato(s.Id);
            Console.WriteLine($"  tre secondi accanto al Custode che dorme: vita {accanto.Vita}, a terra {accanto.ATerra}, dentro {Dentro(s)}");
            if (!Dentro(s)) { Console.WriteLine("  FALLITA: la sonda non e' dentro il santuario"); ok = false; }
            if (accanto.Vita != 100f || accanto.ATerra) { Console.WriteLine("  FALLITA: il Custode che dorme ha attaccato"); ok = false; }

            // --- sveglio: la vita e' per chi e' dentro (una sonda), e la manda a terra
            var sveglio = StatoCustode(s, "sveglia");
            Console.WriteLine($"  svegliato con la sonda dentro: {Sonda.Descrivi(sveglio)}");
            if (Sonda.Numero(sveglio, "piena") != 600 || Sonda.Numero(sveglio, "fascia") != 2)
            { Console.WriteLine("  FALLITA: con una sonda dentro e la terra al 100% la vita doveva essere 600"); ok = false; }
            bool caduta = s.Attendi(() => s.Stato(s.Id).ATerra, 12);
            var giu = s.Stato(s.Id);
            Console.WriteLine($"  contro il Custode sveglio: a terra={giu.ATerra}, morta={giu.Morto}, vita {giu.Vita}");
            if (!caduta || giu.Morto) { Console.WriteLine("  FALLITA: contro il Custode la sonda doveva andare a terra, non morire"); ok = false; }

            // --- nessuno in piedi dentro: torna a dormire, a vita piena
            s.Attendi(() => false, 1.5);
            var riposa = StatoCustode(s);
            Console.WriteLine($"  con la sonda a terra: {Sonda.Descrivi(riposa)}");
            if (riposa.GetValueOrDefault("stato") != "dorme" || Sonda.Numero(riposa, "vita") != Sonda.Numero(riposa, "piena"))
            { Console.WriteLine("  FALLITA: con nessuno in piedi dentro il Custode non torna a dormire a vita piena"); ok = false; }
            s.Admin("aterra rialza");
            s.Admin("sethp 100");
            s.Attendi(() => false, 2.0);
            if (s.Stato(s.Id).Vita != 100f) { Console.WriteLine("  FALLITA: rialzata accanto al Custode che dorme, e' stata colpita"); ok = false; }

            // --- battuto: se ne va, e ne nasce un altro addormentato
            string idPrima = StatoCustode(s).GetValueOrDefault("id");
            StatoCustode(s, "sveglia");
            var morto = StatoCustode(s, "uccidi");
            s.Attendi(() => false, 1.5);
            var nuovo = StatoCustode(s);
            Console.WriteLine($"  ucciso d'ufficio: subito '{Sonda.Descrivi(morto)}', dopo un secondo {Sonda.Descrivi(nuovo)}");
            if (morto.GetValueOrDefault("stato") != "assente") { Console.WriteLine("  FALLITA: ucciso, il Custode e' ancora li'"); ok = false; }
            if (nuovo.GetValueOrDefault("stato") != "dorme" || nuovo.GetValueOrDefault("id") == idPrima)
            { Console.WriteLine("  FALLITA: battuto il Custode, non ne e' nato un altro addormentato"); ok = false; }
        }
        finally
        {
            s.Admin("custode dormi");
            s.Admin("setvitality 1");
            s.Admin("aterra rialza");
            s.Admin("tp 30 30");
            s.Admin("sethp 1000");
            s.Attendi(() => false, 0.5);
        }
        if (ok) Console.WriteLine("  OK: il Custode dorme nel santuario, ha quattro fasce, vita per chi e' dentro, e chi perde va a terra");
        return ok;
    }

    // ---- 15b3. le mosse del Custode ---------------------------------------------
    //
    // Backlog B5 (decisioni/boss.md): il colpo a terra ogni 10 secondi prende
    // tutti entro 6 metri; la carica va su chi gli ha fatto piu' danno. Qui due
    // sonde, una a 4,5 metri dal centro e una a 7 dall'altra parte, con la terra
    // al 20% (danno 35: la vita di una sonda arriva al massimo a 100). Le mosse
    // si chiedono d'ufficio ("custode colpo", "custode carica"), poi si guarda
    // che arrivino anche da sole al loro ritmo.

    static ulong IdNumero(Dictionary<string, string> stato, string chiave) =>
        ulong.TryParse(stato.GetValueOrDefault(chiave), out var v) ? v : 0UL;

    static void MettiIn(Sonda s, float x, float z)
    {
        // Curata prima di arrivare: accanto al Custode, con poca vita, andrebbe
        // a terra prima del "sethp" (giro 3: i pugni dopo andavano a vuoto).
        s.Admin("aterra rialza");
        s.Admin("sethp 100");
        s.Admin($"tp {x.ToString(Inv)} {z.ToString(Inv)}");
        s.Attendi(() => Dist(s.X, s.Z, x, z) < 1f, 6);
        s.Cammina(0f, 0f, 0.1);
        s.Admin("sethp 100");
    }

    public static bool MosseDelCustode(string tokenA, string tokenB, string host, int port)
    {
        Console.WriteLine("--- mosse del Custode: il colpo a terra prende chi e' vicino e non chi e' lontano, la carica va su chi picchia di piu'");

        using var a = new Sonda(tokenA, host, port);
        using var b = new Sonda(tokenB, host, port);
        if (!a.AttendiPronta() || !b.AttendiPronta()) { Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco"); return false; }

        bool ok = true;
        float vicinoXZ  = CentroXZ + 4.5f * Diag;   // 4,5 m dal centro
        float lontanoXZ = CentroXZ - 7f * Diag;     // 7 m dall'altra parte, dentro il muro
        try
        {
            a.Admin("santuario angolo 0");
            a.Admin("santuario riposo");
            a.Admin("setvitality 0.2");
            a.Admin("custode dormi");
            a.Attendi(() => false, 1.5);   // il secondo del santuario: lo sposta al centro
            MettiIn(a, vicinoXZ, vicinoXZ);
            MettiIn(b, lontanoXZ, lontanoXZ);
            a.Attendi(() => false, 0.5);
            if (!Dentro(a) || !Dentro(b)) { Console.WriteLine("  FALLITA: le sonde non sono dentro il santuario"); ok = false; }

            // --- colpo a terra: prende la vicina, non la lontana
            var sv = StatoCustode(a, "sveglia 2");
            Console.WriteLine($"  svegliato in due, terra al 20%: {Sonda.Descrivi(sv)}");
            if (sv.GetValueOrDefault("stato") != "sveglio" || Sonda.Numero(sv, "danno") != 35
                || Sonda.Numero(sv, "colpi a terra") != 0 || Sonda.Numero(sv, "prossimo colpo") < 9)
            { Console.WriteLine("  FALLITA: al risveglio il Custode doveva avere danno 35, nessun colpo a terra e il prossimo fra 10 secondi"); ok = false; }
            // La vita si legge prima e dopo: dopo "sethp" lo stato visto puo'
            // arrivare in ritardo (giro 2: la lontana letta a 74 senza colpi).
            a.Attendi(() => a.Stato(a.Id).Vita == 100f && b.Stato(b.Id).Vita == 100f, 2);
            float vaPrima = a.Stato(a.Id).Vita, vbPrima = b.Stato(b.Id).Vita;
            var colpo = StatoCustode(a, "colpo");
            a.Attendi(() => false, 0.4);
            var va = a.Stato(a.Id); var vb = b.Stato(b.Id);
            Console.WriteLine($"  colpo a terra d'ufficio: colpi {Sonda.Numero(colpo, "colpi a terra")}; " +
                              $"vicina (4,5 m) vita {vaPrima} -> {va.Vita}, lontana (7 m) vita {vbPrima} -> {vb.Vita}");
            if (Sonda.Numero(colpo, "colpi a terra") != 1) { Console.WriteLine("  FALLITA: il colpo a terra non e' partito"); ok = false; }
            // Il colpo toglie 35: con la rigenerazione ferma delle sonde (P4)
            // nessun punto rigenerato nei 0,4 secondi dopo (P3).
            if (va.Vita > vaPrima - 35f) { Console.WriteLine("  FALLITA: il colpo a terra non ha preso la sonda a 4,5 metri"); ok = false; }
            if (vb.Vita < vbPrima) { Console.WriteLine("  FALLITA: il colpo a terra ha preso la sonda a 7 metri"); ok = false; }

            // --- carica: la sonda A lo picchia, poi va lontano; la B gli sta
            // addosso, ed e' la piu' vicina. La carica va su A.
            var pos = StatoCustode(a);
            float kx = float.Parse(pos.GetValueOrDefault("x") ?? "0", Inv), kz = float.Parse(pos.GetValueOrDefault("z") ?? "0", Inv);
            MettiIn(a, kx, kz);
            a.Colpisci(1.0);
            if (a.Stato(a.Id).ATerra) { MettiIn(a, kx, kz); a.Colpisci(1.0); }
            var picchiato = StatoCustode(a);
            Console.WriteLine($"  dopo un secondo di pugni: vita {Sonda.Numero(picchiato, "vita")}/{Sonda.Numero(picchiato, "piena")}, " +
                              $"piu' danno {IdNumero(picchiato, "piu danno")} (A e' {a.Id})");
            if (IdNumero(picchiato, "piu danno") != a.Id) { Console.WriteLine("  FALLITA: il Custode non ricorda chi l'ha picchiato"); ok = false; }
            pos = StatoCustode(a);
            kx = float.Parse(pos.GetValueOrDefault("x") ?? "0", Inv); kz = float.Parse(pos.GetValueOrDefault("z") ?? "0", Inv);
            MettiIn(b, kx + 1f * Diag, kz + 1f * Diag);
            MettiIn(a, lontanoXZ, lontanoXZ);
            float vitaPrima = a.Stato(a.Id).Vita;
            lock (a.Mondo) a.BitDelCustodeVisti = 0;
            var carica = StatoCustode(a, "carica");
            Console.WriteLine($"  carica d'ufficio: {Sonda.Descrivi(carica)}");
            if (IdNumero(carica, "carica") != a.Id) { Console.WriteLine("  FALLITA: la carica non e' andata su chi l'ha picchiato di piu'"); ok = false; }
            bool presa = a.Attendi(() => a.Stato(a.Id).Vita < vitaPrima, 3);
            var arrivato = StatoCustode(a);
            float dA = Dist(float.Parse(arrivato.GetValueOrDefault("x") ?? "0", Inv), float.Parse(arrivato.GetValueOrDefault("z") ?? "0", Inv), a.X, a.Z);
            Console.WriteLine($"  dopo la carica: A vita {vitaPrima} -> {a.Stato(a.Id).Vita}, Custode a {dA:F1} m da A, B vita {b.Stato(b.Id).Vita}");
            if (!presa || dA > 3.5f) { Console.WriteLine("  FALLITA: la carica non ha raggiunto e colpito chi l'ha picchiato"); ok = false; }
            // B7: il client deve sapere che carica (bit Charging nei Flags).
            uint bitCarica; lock (a.Mondo) bitCarica = a.BitDelCustodeVisti;
            Console.WriteLine($"  bit visti dal client durante la carica: {bitCarica} (carica {((bitCarica & KeeperState.Charging) != 0 ? "si" : "no")})");
            if ((bitCarica & KeeperState.Charging) == 0) { Console.WriteLine("  FALLITA: il client non ha visto il segno della carica"); ok = false; }

            // --- da sole, al loro ritmo: in 18 secondi un altro colpo a terra
            // (ogni 10) e un'altra carica (ogni 12). Le sonde si curano e si
            // rialzano, e A torna lontano.
            MettiIn(b, CentroXZ, CentroXZ);
            MettiIn(a, lontanoXZ, lontanoXZ);
            var fine = DateTime.UtcNow.AddSeconds(18);
            Dictionary<string, string> ritmo = StatoCustode(a);
            lock (a.Mondo) a.BitDelCustodeVisti = 0;
            while (DateTime.UtcNow < fine)
            {
                ritmo = StatoCustode(a);
                if (Sonda.Numero(ritmo, "colpi a terra") >= 2 && Sonda.Numero(ritmo, "cariche") >= 2) break;
                foreach (var p in new[] { a, b }) { p.Admin("aterra rialza"); p.Admin("sethp 100"); }
                // Sotto i 4 metri non carica: se il Custode e' arrivato addosso
                // ad A, A va dall'altra parte, e B resta al centro, la piu' vicina.
                if (IdNumero(ritmo, "carica") == 0 &&
                    float.TryParse(ritmo.GetValueOrDefault("x"), System.Globalization.NumberStyles.Float, Inv, out var rx) &&
                    float.TryParse(ritmo.GetValueOrDefault("z"), System.Globalization.NumberStyles.Float, Inv, out var rz) &&
                    Dist(rx, rz, a.X, a.Z) < 5f)
                {
                    float altro = Dist(rx, rz, lontanoXZ, lontanoXZ) > Dist(rx, rz, CentroXZ + 7f * Diag, CentroXZ + 7f * Diag)
                        ? lontanoXZ : CentroXZ + 7f * Diag;
                    MettiIn(b, CentroXZ, CentroXZ);
                    MettiIn(a, altro, altro);
                }
                a.Attendi(() => false, 0.5);
            }
            Console.WriteLine($"  al loro ritmo: colpi a terra {Sonda.Numero(ritmo, "colpi a terra")}, cariche {Sonda.Numero(ritmo, "cariche")}");
            if (Sonda.Numero(ritmo, "colpi a terra") < 2) { Console.WriteLine("  FALLITA: il colpo a terra non torna da solo ogni 10 secondi"); ok = false; }
            if (Sonda.Numero(ritmo, "cariche") < 2) { Console.WriteLine("  FALLITA: la carica non torna da sola"); ok = false; }
            // B7: il colpo al suo ritmo si prepara per un secondo, e il client
            // lo vede (bit SlamWindup): e quello che gli fa disegnare il cerchio.
            uint bitRitmo; lock (a.Mondo) bitRitmo = a.BitDelCustodeVisti;
            Console.WriteLine($"  bit visti dal client al loro ritmo: {bitRitmo} (preparazione {((bitRitmo & KeeperState.SlamWindup) != 0 ? "si" : "no")})");
            if ((bitRitmo & KeeperState.SlamWindup) == 0) { Console.WriteLine("  FALLITA: il client non ha visto la preparazione del colpo a terra"); ok = false; }

            // --- tornato a dormire, le mosse ripartono da zero
            var dorme = StatoCustode(a, "dormi");
            if (Sonda.Numero(dorme, "colpi a terra") != 0 || IdNumero(dorme, "piu danno") != 0)
            { Console.WriteLine("  FALLITA: addormentato, il Custode ricorda ancora mosse e danni"); ok = false; }
        }
        finally
        {
            a.Admin("custode dormi");
            a.Admin("setvitality 1");
            foreach (var p in new[] { a, b }) { p.Admin("aterra rialza"); p.Admin("tp 30 30"); p.Admin("sethp 1000"); }
            a.Attendi(() => false, 0.5);
        }
        if (ok) Console.WriteLine("  OK: il colpo a terra prende chi e' entro 6 metri e non chi e' lontano, la carica va su chi picchia di piu'");
        return ok;
    }

    // ---- 15b4. il bottino del Custode ---------------------------------------------
    //
    // Backlog B6 (decisioni/boss.md): battuto, il Custode lascia a terra dentro
    // il santuario un bottino che cresce quando la vitalita cala; mai cristalli
    // Precursore; in era rigogliosa non raddoppia. E a tutti i collegati arriva
    // una voce nel registro con le fazioni che l'hanno vinto, senza nomi.
    //
    // Una sonda dentro, a 7 metri dal centro (le pile cadono fra 2,5 e 5 metri
    // dal Custode: non le raccoglie passando), l'altra fuori, lontana. Il
    // Custode si batte d'ufficio ("custode uccidi") nelle quattro fasce; dopo
    // ogni bottino le pile si tolgono con "remove", per non lasciarle alle
    // prove che vengono dopo. Quantita attese: quelle di Keeper.Loot, tutte
    // provvisorie: se si tarano, qui si cambiano insieme.

    /// Toglie le pile che "pile" vede, e ricomincia finche' ne compaiono: il
    /// server manda solo le 32 entita' piu' vicine, e togliendo quelle in vista
    /// si fa posto a quelle che erano nascoste. Restituisce quante ne ha tolte.
    static int SvuotaSantuario(Sonda s, Func<List<EntityState>> pile)
    {
        int tolte = 0;
        for (int giro = 0; giro < 8; giro++)
        {
            var viste = pile();
            if (viste.Count == 0)
            {
                // Mezzo secondo per lasciar arrivare quelle che erano fuori
                // dal tetto: se non ne arrivano, il santuario e' vuoto.
                s.Attendi(() => false, 0.5);
                if (pile().Count == 0) break;
                continue;
            }
            foreach (var p in viste) s.Admin($"remove {p.EntityId}");
            tolte += viste.Count;
            var ids = viste.Select(p => p.EntityId).ToHashSet();
            s.Attendi(() => !pile().Any(e => ids.Contains(e.EntityId)), 5);
        }
        return tolte;
    }

    public static bool BottinoDelCustode(string tokenA, string tokenB, string host, int port)
    {
        Console.WriteLine("--- bottino del Custode: cresce quando la terra e' povera, mai Precursori, non raddoppia, e il mondo sa chi l'ha vinto");

        using var a = new Sonda(tokenA, host, port);
        using var b = new Sonda(tokenB, host, port);
        if (!a.AttendiPronta() || !b.AttendiPronta()) { Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco"); return false; }

        bool ok = true;
        float dentroXZ = CentroXZ - 7f * Diag;
        string nomeA = a.ChiediAdmin("unico chi", "unico: ");
        string nomeB = b.ChiediAdmin("unico chi", "unico: ");
        // Cristalli per grado (sottotipo 1..4) e risorse per tipo.
        const int Comune = 1, Raro = 2, Epico = 3, Precursore = 4;
        var fasce = new (string Vit, int Fascia, string Nome, int Legno, int Comuni, int Rari, int EpiciMin, int EpiciMax)[]
        {
            ("0.02",  0, "carestia",       80, 0, 5, 2, 3),
            ("0.2",   1, "terra povera",   40, 5, 3, 1, 1),
            ("0.625", 2, "terra viva",     20, 3, 0, 0, 0),
            ("1.2",   3, "era rigogliosa", 20, 3, 0, 0, 0),   // come la terra viva: niente raddoppio
        };

        List<EntityState> PileDentro()
        {
            lock (a.Mondo) return a.Mondo.Values
                .Where(e => e.Type == EntityType.DroppedItem && Dist(e.X, e.Z, CentroXZ, CentroXZ) <= 9f)
                .ToList();
        }

        try
        {
            a.Admin("santuario angolo 0");
            a.Admin("santuario riposo");
            a.Admin("custode dormi");
            MettiIn(a, dentroXZ, dentroXZ);
            b.Admin("aterra rialza");
            b.Admin("tp 30 30");
            b.Attendi(() => Dist(b.X, b.Z, 30f, 30f) < 2f, 6);
            a.Attendi(() => false, 1.5);
            if (!Dentro(a)) { Console.WriteLine("  FALLITA: la sonda non e' dentro il santuario"); ok = false; }

            // Pile gia' a terra nel santuario (di prove di prima): si tolgono
            // prima di cominciare. Prima si mettevano solo da parte, ma erano
            // una ventina e il server manda solo le 32 entita' piu' vicine: le
            // pile nuove non ci stavano tutte, una restava nascosta e veniva
            // contata nella fascia dopo (backlog P2, log a14c-giro-2: carestia
            // senza il legno 80, terra viva con legno 100 = 80 + 20).
            int vecchie = SvuotaSantuario(a, PileDentro);
            if (vecchie > 0) Console.WriteLine($"  pile gia' a terra nel santuario prima della prova: {vecchie}, tolte");
            var giaLi = PileDentro().Select(e => e.EntityId).ToHashSet();
            if (giaLi.Count > 0) Console.WriteLine($"  attenzione: {giaLi.Count} pile vecchie non si tolgono, non contano");

            foreach (var (vit, fascia, nome, legno, comuni, rari, epMin, epMax) in fasce)
            {
                a.Admin($"setvitality {vit}");
                a.Admin("santuario riposo");
                a.Attendi(() => Sonda.Numero(StatoCustode(a), "fascia") == fascia, 5);
                var prima = StatoCustode(a);
                if (prima.GetValueOrDefault("stato") != "dorme" || Sonda.Numero(prima, "fascia") != fascia)
                { Console.WriteLine($"  FALLITA: terra a {vit}: il Custode non dorme con la fascia {fascia} ({Sonda.Descrivi(prima)})"); ok = false; continue; }

                int voceA0, voceB0;
                lock (a.Eventi) voceA0 = a.Eventi.Count(e => e.Kind == WorldEventKind.KeeperDefeated);
                lock (b.Eventi) voceB0 = b.Eventi.Count(e => e.Kind == WorldEventKind.KeeperDefeated);

                StatoCustode(a, "uccidi");
                a.Attendi(() => PileDentro().Count(e => !giaLi.Contains(e.EntityId)) >= 4, 5);
                a.Attendi(() => false, 0.7);   // tutte le pile nello stesso snapshot, ma con margine
                var nuove = PileDentro().Where(e => !giaLi.Contains(e.EntityId)).ToList();

                int Quanti(ItemType t, int sub = 0) => nuove.Where(e => e.Extra1 == (int)t && e.Extra3 == sub).Sum(e => (int)e.Extra2);
                int c = Quanti(ItemType.Crystal, Comune), r = Quanti(ItemType.Crystal, Raro),
                    ep = Quanti(ItemType.Crystal, Epico), pr = Quanti(ItemType.Crystal, Precursore);
                int w = Quanti(ItemType.Wood);
                float lontana = nuove.Count == 0 ? 0f : nuove.Max(e => Dist(e.X, e.Z, CentroXZ, CentroXZ));
                Console.WriteLine($"  {nome} (terra a {vit}): {nuove.Count} pile, legno {w}, pietra {Quanti(ItemType.Stone)}, " +
                                  $"cristalli comuni {c}, rari {r}, epici {ep}, precursori {pr}; la piu' lontana a {lontana:F1} m dal centro");

                if (w != legno || c != comuni || r != rari || ep < epMin || ep > epMax)
                { Console.WriteLine($"  FALLITA: {nome}: atteso legno {legno}, comuni {comuni}, rari {rari}, epici {epMin}-{epMax}"); ok = false; }
                if (pr != 0) { Console.WriteLine($"  FALLITA: {nome}: il Custode ha lasciato cristalli Precursore"); ok = false; }
                if (lontana > 8f) { Console.WriteLine($"  FALLITA: {nome}: una pila e' caduta fuori dal santuario"); ok = false; }

                // La voce nel registro, a chi e' dentro e a chi e' lontano. Ogni
                // sonda legge i suoi messaggi solo mentre aspetta: si aspettano
                // tutte e due (giro 1: aspettava solo la prima, e la seconda
                // sembrava non averla ricevuta).
                bool arrivata =
                    a.Attendi(() => { lock (a.Eventi) return a.Eventi.Count(e => e.Kind == WorldEventKind.KeeperDefeated) > voceA0; }, 5)
                  & b.Attendi(() => { lock (b.Eventi) return b.Eventi.Count(e => e.Kind == WorldEventKind.KeeperDefeated) > voceB0; }, 5);
                string testoA, testoB;
                lock (a.Eventi) testoA = a.Eventi.LastOrDefault(e => e.Kind == WorldEventKind.KeeperDefeated)?.Text ?? "(niente)";
                lock (b.Eventi) testoB = b.Eventi.LastOrDefault(e => e.Kind == WorldEventKind.KeeperDefeated)?.Text ?? "(niente)";
                if (fascia == 0) Console.WriteLine($"  voce nel registro: \"{testoA}\"");
                if (!arrivata || testoA != testoB || !testoA.Contains("fazione"))
                { Console.WriteLine($"  FALLITA: {nome}: la voce del Custode battuto non e' arrivata a tutti e due (dentro: \"{testoA}\", fuori: \"{testoB}\")"); ok = false; }
                if ((!string.IsNullOrWhiteSpace(nomeA) && testoA.Contains(nomeA)) || (!string.IsNullOrWhiteSpace(nomeB) && testoA.Contains(nomeB)))
                { Console.WriteLine($"  FALLITA: {nome}: la voce dice il nome di un giocatore"); ok = false; }

                // Via le pile, comprese quelle che fossero rimaste nascoste,
                // perche' non finiscano nel conto della fascia dopo.
                SvuotaSantuario(a, () => PileDentro().Where(e => !giaLi.Contains(e.EntityId)).ToList());
                if (PileDentro().Any(e => !giaLi.Contains(e.EntityId)))
                    Console.WriteLine("  attenzione: alcune pile del bottino sono ancora a terra");
            }
        }
        finally
        {
            // Anche le pile lasciate nel santuario dalle prove di prima (il
            // Custode battuto in "santuario" e "custode"): il mondo di prova
            // resta pulito per chi viene dopo.
            foreach (var p in PileDentro()) a.Admin($"remove {p.EntityId}");
            a.Admin("santuario riposo");
            a.Admin("custode dormi");
            a.Admin("setvitality 1");
            a.Admin("tp 30 30");
            a.Admin("sethp 1000");
            b.Admin("sethp 1000");
            a.Attendi(() => false, 1.2);
        }
        if (ok) Console.WriteLine("  OK: il bottino cresce quando la terra e' povera, mai Precursori, non raddoppia, e la voce arriva a tutti senza nomi");
        return ok;
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
        // Una volta, nel primo giro di A5, i nodi fuori dall'isola centrale non
        // sono comparsi, e il log di quel giro non c'e' piu'. Come per la
        // trappola: si scrive dov'era la sonda e cosa vedeva, e si ripianta una
        // volta (backlog P1b). I nodi piantati due volte si tolgono tutti alla
        // fine, con Ripulisci.
        (EntityState Ferro, EntityState Pietra) Pianta(float nx, float nz)
        {
            EntityState f = null, p = null;
            bool Giusto(EntityState e, float x, float z) => e != null && Dist(e.X, e.Z, x, z) < 1f;
            for (int tentativo = 1; tentativo <= 2; tentativo++)
            {
                s.Admin($"tp {nx.ToString(Inv)} {nz.ToString(Inv)}");
                if (!s.Attendi(() => Dist(s.X, s.Z, nx, nz) < 2f, 8))
                    Console.WriteLine($"  (nodi, tentativo {tentativo}: la sonda e' a ({s.X:F1}, {s.Z:F1}), non a ({nx}, {nz}))");
                if (!Giusto(f, nx, nz + 1f)) s.Admin($"build IronNode {nx.ToString(Inv)} {(nz + 1f).ToString(Inv)}");
                if (!Giusto(p, nx + 1f, nz)) s.Admin($"build StoneNode {(nx + 1f).ToString(Inv)} {nz.ToString(Inv)}");
                s.Attendi(() => Giusto(f = s.PiuVicina(EntityType.IronNode, nx, nz + 1f), nx, nz + 1f) &&
                                Giusto(p = s.PiuVicina(EntityType.StoneNode, nx + 1f, nz), nx + 1f, nz), 12);
                if (Giusto(f, nx, nz + 1f) && Giusto(p, nx + 1f, nz)) break;
                int viste; lock (s.Mondo) viste = s.Mondo.Count;
                Console.WriteLine($"  (nodi, tentativo {tentativo}: ferro {(Giusto(f, nx, nz + 1f) ? "visto" : "non visto")}, " +
                                  $"pietra {(Giusto(p, nx + 1f, nz) ? "vista" : "non vista")}; " +
                                  $"sonda a ({s.X:F1}, {s.Z:F1}), {viste} entita' in vista)");
            }
            if (Giusto(f, nx, nz + 1f)) piantati.Add(f.EntityId); else f = null;
            if (Giusto(p, nx + 1f, nz)) piantati.Add(p.EntityId); else p = null;
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
    // spegnere si sprigiona un'orda d'ufficio (50 piccoli e un mini boss Epico, tutti Echi);
    // dopo, deve esserci ancora, sparsa, col suo mini boss. La sonda sta lontana
    // dall'isola centrale: l'orda non deve poterla raggiungere.

    public static bool OrdaPrepara(string token, string host, int port)
    {
        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda dell'orda non e' entrata in gioco"); return false; }
        var stato = s.StatoSantuario("orda 1 1 0");
        Console.WriteLine($"  orda prima del riavvio: {Sonda.Descrivi(stato)}");
        if (stato.GetValueOrDefault("orda") != "50" || stato.GetValueOrDefault("capi") != "1"
            || stato.GetValueOrDefault("eco") != "25" || stato.GetValueOrDefault("vaganti") != "25"
            || stato.GetValueOrDefault("divoratori") != "1")
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
        // Echi dal A1a4a2: 25 Eco, 25 Vaganti e il Divoratore dell'Epico.
        bool ok = stato.GetValueOrDefault("orda") == "50" && stato.GetValueOrDefault("capi") == "1"
                  && stato.GetValueOrDefault("eco") == "25" && stato.GetValueOrDefault("vaganti") == "25"
                  && stato.GetValueOrDefault("divoratori") == "1"
                  && stato.GetValueOrDefault("sparsa") == "1" && stato.GetValueOrDefault("in attesa") == "1"
                  && stato.GetValueOrDefault("stato") == "Resting";
        if (!ok) Console.WriteLine("  FALLITA: l'orda non e' sopravvissuta al riavvio com'era");
        // Si toglie comunque: il mondo di prova riparte pulito al prossimo giro,
        // ma chi lo guarda dopo non deve trovarci 50 Echi.
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
        // L'armatura indossata non sta nello zaino (A14e1): prima si toglie,
        // cosi' clearinv porta via anche quella del giro di prima.
        foreach (var slot in new[] { EquipSlot.Head, EquipSlot.Body, EquipSlot.Legs })
        { s.Togli(slot); s.Attendi(() => s.Esito(113) != null, 3); }
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

        // Le abilita (A14b): lo stato d'esempio, con una ricarica di 30 secondi
        // (il Grido di battaglia) che deve esserci ancora al rientro.
        var abilita = s.AbilitaInRiga("esempio");
        if (abilita == null || !abilita.StartsWith("scartate=7 ")
            || SenzaRicariche(abilita["scartate=7 ".Length..]) != AbilitaEsempioPulita)
        {
            Console.WriteLine($"  FALLITA in preparazione: lo stato delle abilita non si carica ({abilita ?? "nessuna risposta"})");
            return false;
        }

        // L'armatura indossata (A14e1): un elmo di ferro Raro e una corazza
        // d'osso Epica, che al rientro devono essere ancora indosso, gli
        // stessi esemplari col loro grado, e non nello zaino.
        foreach (var riga in new[] { "dai IronHelm raro", "dai BoneCuirass epico" })
        {
            var r = s.ChiediAdmin(riga, "dai: ");
            ulong id = r != null && ulong.TryParse(r.Split("id=").Last(), out var n) ? n : 0;
            s.Attendi(() => { lock (s.Zaino) return s.Istanze.ContainsKey(id); }, 5);
            s.Indossa(id);
            CommandResultMsg e = null;
            if (id == 0 || !s.Attendi(() => (e = s.Esito(113)) != null, 5) || !e.Success)
            {
                Console.WriteLine($"  FALLITA in preparazione: '{riga}' e poi indossarlo non e' riuscito ({r ?? "nessuna risposta"})");
                return false;
            }
        }
        s.Attendi(() => s.Indossati != null && s.Indossati.Worn.Count(w => w.Type != ItemType.None) == 2, 5);
        string indossati = IndossatiInRiga(s);

        File.WriteAllLines(Lasciato, righe.Append(registro).Append(rituali.ToString(Inv))
                                          .Append(AbilitaEsempioPulita).Append(indossati));

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
        string abilitaAtt = atteso.Length > 7 ? atteso[7] : "";
        string indossatiAtt = atteso.Length > 8 ? atteso[8] : "";

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

        // Le abilita (A14b): arrivate all'ingresso col salvataggio, e uguali.
        // La ricarica del Grido di battaglia (30 s) deve esserci ancora: il
        // tempo a server spento non conta. Quella dell'Affondo (10 s) puo'
        // essere gia' finita.
        var abilita = s.AbilitaInRiga();
        var msg = s.Abilita;
        Console.WriteLine($"  {quando}: abilita [{abilita}], all'ingresso {msg?.Unlocked.Length ?? -1} sbloccate");
        if (SenzaRicariche(abilita) != abilitaAtt)
        { Console.WriteLine($"  FALLITA: lo stato delle abilita non e' tornato uguale (era [{abilitaAtt}])"); ok = false; }
        if (!Ricariche(abilita).Split(',').Contains("112"))
        { Console.WriteLine("  FALLITA: la ricarica del Grido di battaglia non ha superato l'uscita"); ok = false; }
        if (msg == null || msg.Unlocked.Length != 8)
        { Console.WriteLine("  FALLITA: all'ingresso non e' arrivato lo stato delle abilita salvato"); ok = false; }

        // L'armatura indossata (A14e1): stessi pezzi negli stessi slot, e
        // nessuno di loro anche nello zaino.
        s.Attendi(() => s.IndossatiRicevuti > 0, 6);
        string indossati = IndossatiInRiga(s);
        bool doppi;
        lock (s.Zaino) doppi = s.Indossati != null && s.Indossati.Worn.Any(w => w.InstanceId != 0 && s.Istanze.ContainsKey(w.InstanceId));
        Console.WriteLine($"  {quando}: indossati [{indossati}] (erano [{indossatiAtt}])");
        if (indossati != indossatiAtt)
        { Console.WriteLine("  FALLITA: l'armatura indossata non e' tornata uguale"); ok = false; }
        if (doppi)
        { Console.WriteLine("  FALLITA: un pezzo indossato e' anche nello zaino"); ok = false; }

        // Il Custode non si salva: rinasce addormentato al centro del
        // santuario (B3). Dopo un riavvio deve esserci lo stesso.
        var custode = StatoCustode(s);
        Console.WriteLine($"  {quando}: custode {Sonda.Descrivi(custode)}");
        if (custode.GetValueOrDefault("stato") != "dorme")
        { Console.WriteLine("  FALLITA: il Custode non c'e', o non dorme"); ok = false; }

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

    /// L'armatura indossata in una riga: per slot tipo, id, grado e usura, "-"
    /// se vuoto; "?" se il server non l'ha mandata.
    static string IndossatiInRiga(Sonda s) =>
        s.Indossati == null ? "?" : string.Join(",", s.Indossati.Worn.Select(w =>
            w.Type == ItemType.None ? "-" : $"{w.Type}:{w.InstanceId}:{w.Level}:{w.Durability}"));

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

    // ---- passiva dello scudo e Pietraviva (A14h) --------------------------------
    //
    // Decisioni/abilita.md sezione 3: con lo scudo nella mano libera, quando
    // la vita scende sotto il 25% parte una barriera per 8 s (Comune 10%, Raro
    // 15%, Epico 20%, Dono di Torak 25% della vita massima; Base niente),
    // ricarica 60 s. Pietraviva in piu: il colpo che porterebbe a zero lascia
    // a 1, immortali per 3 s, con il 15% di vita recuperato; ricarica 3 minuti.
    // I colpi sono quelli del comando di prova "scudo colpo", che passa per la
    // stessa strada dei colpi veri (DannoRicevuto).
    public static bool PassivaDelloScudo(string token, string host, int port)
    {
        Console.WriteLine("--- passiva dello scudo: barriera sotto il 25%, Pietraviva non fa morire");
        bool ok = true;
        void Male(string m) { Console.WriteLine("  FALLITA: " + m); ok = false; }

        using var s = new Sonda(token, host, port);
        if (!s.AttendiPronta()) { Console.WriteLine("  FALLITA: la sonda non e' entrata in gioco"); return false; }

        const float PX = -145f, PZ = 255f;   // lontano da tutto, come le prove dell'armatura
        void Pausa(double secondi) => s.Attendi(() => false, secondi);
        string Scudo(string riga = "")
        {
            Pausa(0.15);
            return s.ChiediAdmin(("scudo " + riga).Trim(), "scudo: ") ?? "(nessuna risposta)";
        }
        static string Campo(string riga, string nome)
        {
            int i = riga.IndexOf(nome + "=", StringComparison.Ordinal);
            if (i < 0) return null;
            int f = riga.IndexOf(' ', i);
            return f < 0 ? riga[(i + nome.Length + 1)..] : riga[(i + nome.Length + 1)..f];
        }
        (int Vita, int Max) Vita(string riga)
        {
            var v = Campo(riga, "vita")?.Split('/');
            return v != null && v.Length == 2 && int.TryParse(v[0], out int a) && int.TryParse(v[1], out int b) ? (a, b) : (-1, -1);
        }
        int Preso(string riga) =>
            riga.StartsWith("preso ") && int.TryParse(riga.Split(' ')[1], out int p) ? p : -1;
        int PuntiBarriera(string riga)
        {
            var b = Campo(riga, "barriera");
            return b == null || b == "no" ? 0 : int.TryParse(b, out int n) ? n : -1;
        }
        // Ogni risposta finisce nel resoconto della prova.
        string Riga(string r) { Console.WriteLine("    " + r); return r; }

        // Scudo e spada, la spada in mano con lo scudo (posizione 1).
        bool Arma(string scudo)
        {
            s.Admin("clearinv");
            Pausa(0.3);
            if (scudo == "pietraviva")
            {
                var u = s.ChiediAdmin("unico dai 5", "unico: ");
                Console.WriteLine($"  unico dai 5 -> {u ?? "(nessuna risposta)"}");
                if (u == null || !u.StartsWith("5 ")) { Male("lo scudo unico (5) non e' libero o non e' arrivato"); return false; }
            }
            else
            {
                var d = s.ChiediAdmin($"dai Shield {scudo}", "dai: ");
                if (d == null || !d.StartsWith("1x Shield ")) { Male($"'dai Shield {scudo}' non ha dato lo scudo ({d})"); return false; }
            }
            var w = s.ChiediAdmin("dai StoneSword base", "dai: ");
            if (w == null || !w.StartsWith("1x StoneSword ")) { Male($"la spada non e' arrivata ({w})"); return false; }
            Pausa(0.2);
            s.Dotazione(ItemType.StoneSword, ItemType.None, SideItemType.Shield);
            Pausa(0.2);
            s.Posizione(1);
            string r = null;
            s.Attendi(() => (r = s.ChiediAdmin("dotazione", "dotazione: ")) != null && r.Contains("mano=Shield"), 6);
            if (r == null || !r.Contains("mano=Shield")) { Male($"lo scudo non e' nella mano libera ({r})"); return false; }
            s.Admin("ricarica");
            s.Admin("effetto azzera");
            s.Admin("sethp 1000");
            Pausa(0.3);
            return true;
        }

        s.Admin("setfaction 3");
        s.Admin($"tp {PX.ToString(Inv)} {PZ.ToString(Inv)}");
        s.Attendi(() => Dist(s.X, s.Z, PX, PZ) < 2f, 8);

        // 1. Scudo Raro: sopra il 25% niente, sotto la barriera del 15%.
        if (Arma("raro"))
        {
            var r = Riga(Scudo());
            var (vita, max) = Vita(r);
            if (Campo(r, "scudo") != "Rare" || max <= 0) Male($"stato dello scudo Raro sbagliato: {r}");
            int attesa = (int)Math.Round(max * 0.15, MidpointRounding.AwayFromZero);

            r = Riga(Scudo($"colpo {vita - (int)(max * 0.40)}"));   // al 40%
            if (PuntiBarriera(r) != 0) Male("al 40% della vita la barriera non deve partire");

            r = Riga(Scudo($"colpo {Vita(r).Vita - (int)(max * 0.20)}"));   // al 20%
            int dopo = Vita(r).Vita;
            if (PuntiBarriera(r) != attesa) Male($"sotto il 25% la barriera deve valere {attesa} (15% di {max})");
            if (Campo(r, "ricarica") == "pronta") Male("partita la barriera, la passiva deve andare in ricarica");

            r = Riga(Scudo("colpo 5"));
            if (Preso(r) != 0 || Vita(r).Vita != dopo || PuntiBarriera(r) != attesa - 5)
                Male("un colpo di 5 deve finire tutto nella barriera");

            r = Riga(Scudo($"colpo {attesa}"));
            if (Preso(r) != 5 || PuntiBarriera(r) != 0)
                Male($"un colpo di {attesa} con {attesa - 5} di barriera deve passare per 5 e romperla");

            // In ricarica non riparte.
            s.Admin("sethp 1000"); Pausa(0.3);
            r = Riga(Scudo($"colpo {(int)(max * 0.85)}"));
            if (PuntiBarriera(r) != 0) Male("in ricarica la barriera non deve ripartire");

            // Con lo scudo appeso (posizione 0, la spada da sola) niente.
            s.Posizione(0); Pausa(0.3);
            s.Admin("ricarica"); s.Admin("sethp 1000"); Pausa(0.3);
            r = Riga(Scudo($"colpo {(int)(max * 0.85)}"));
            if (Campo(r, "scudo") != "nessuno" || PuntiBarriera(r) != 0) Male("con lo scudo fuori dalla mano la passiva non deve valere");
        }

        // 2. Scudo Base: blocca come prima, nessuna barriera.
        if (Arma("base"))
        {
            var r = Riga(Scudo());
            int max = Vita(r).Max;
            r = Riga(Scudo($"colpo {(int)(max * 0.85)}"));
            if (Campo(r, "scudo") != "Base" || PuntiBarriera(r) != 0) Male("lo scudo Base non deve dare barriere");
        }

        // 3. Pietraviva: il colpo mortale lascia a 1, 3 s immortali, poi il 15%.
        if (Arma("pietraviva"))
        {
            var r = Riga(Scudo());
            int max = Vita(r).Max;
            if (Campo(r, "scudo") != "Torak" || !r.Contains(" pietraviva ")) Male($"Pietraviva non riconosciuto: {r}");

            r = Riga(Scudo($"colpo {max * 10}"));
            if (Vita(r).Vita != 1 || Campo(r, "immortale") == "no") Male("il colpo mortale con Pietraviva deve lasciare a 1, immortale");
            if (PuntiBarriera(r) != (int)Math.Round(max * 0.25, MidpointRounding.AwayFromZero))
                Male("a 1 di vita deve partire anche la barriera del 25%");
            if (Campo(r, "pietraviva") == "pronta") Male("Pietraviva deve andare in ricarica");

            r = Riga(Scudo($"colpo {max * 10}"));
            if (Preso(r) != 0 || Vita(r).Vita != 1) Male("da immortale nessun colpo deve togliere vita");
            var aTerra = s.ChiediAdmin("aterra", "a terra: ");
            if (aTerra != "no") Male($"con Pietraviva non si va a terra ({aTerra})");

            Pausa(3.6);
            r = Riga(Scudo());
            int curata = 1 + (int)Math.Round(max * 0.15, MidpointRounding.AwayFromZero);
            if (Vita(r).Vita < curata) Male($"dopo 3 s la vita deve essere almeno {curata} (1 piu' il 15%)");
            if (Campo(r, "immortale") != "no") Male("dopo 3 s l'immortalita' deve finire");

            // In ricarica Pietraviva non regge piu': si va a terra.
            r = Riga(Scudo($"colpo {max * 10}"));
            aTerra = s.ChiediAdmin("aterra", "a terra: ");
            if (aTerra == null || !aTerra.StartsWith("si")) Male($"con Pietraviva in ricarica il colpo mortale deve mandare a terra ({aTerra})");
            s.Admin("aterra rialza");
        }

        // Si lascia pulito: lo scudo unico torna libero con lo zaino vuoto.
        s.Posizione(0);
        s.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        s.Admin("clearinv");
        s.Admin("ricarica");
        s.Admin("effetto azzera");
        s.Admin("sethp 1000");
        Pausa(0.5);

        if (ok) Console.WriteLine("  OK: la barriera parte sotto il 25% secondo il grado, Pietraviva tiene a 1 e cura");
        return ok;
    }

    // ---- classi: scelta, sblocco, slot, cambio, passive (A14i) ----------------
    //
    // Decisioni/abilita.md sezione 6: si sceglie una classe principale; dopo 25
    // uccisioni di mob si sblocca con le sue 2 attive e 2 passive base, che
    // vanno negli slot vuoti; la secondaria dev'essere gia sbloccata e da' al
    // piu' uno slot attivo e uno passivo; cambiando classe gli slot della
    // classe lasciata si svuotano ma quello che si e' sbloccato resta. Le
    // passive di statistica contano: Robusto +10% vita massima (col livello),
    // Mano pesante +8% danno in mischia. A in fazione 1 col Guerriero, B in 2
    // fa da bersaglio. La 25esima uccisione e' un lupo vero, ucciso da un
    // danno nel tempo di A: le prime 24 le fissa "classe uccisioni".
    public static bool Classi(string tokenA, string tokenB, string host, int port)
    {
        Console.WriteLine("--- classi: scelta, sblocco a 25 uccisioni, slot, secondaria, cambio, passive");
        bool ok = true;
        void Male(string m) { Console.WriteLine("  FALLITA: " + m); ok = false; }

        using var a = new Sonda(tokenA, host, port);
        using var b = new Sonda(tokenB, host, port);
        if (!a.AttendiPronta() || !b.AttendiPronta())
        { Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco"); return false; }

        const float PX = -155f, PZ = 245f;   // lontano da tutto, come le altre prove delle abilita
        static byte[] P<T>(T v) => MessagePack.MessagePackSerializer.Serialize(v);
        bool Aspetta(Func<bool> cosa, double secondi) =>
            a.Attendi(() => { b.Attendi(() => true, 0.01); return cosa(); }, secondi);
        void Pausa(double secondi) => Aspetta(() => false, secondi);
        float Vita(Sonda chi) => a.Stato(chi.Id).Vita;
        void Tp(Sonda s, float x, float z)
        {
            s.Admin($"tp {x.ToString(Inv)} {z.ToString(Inv)}");
            Aspetta(() => Dist(s.X, s.Z, x, z) < 0.5f, 6);
            s.Cammina(0f, 0f, 0.1);
        }
        void Piena(params Sonda[] chi)
        {
            foreach (var s in chi) s.Admin("sethp 1000");
            Aspetta(() => chi.All(s => Vita(s) >= 100f), 3);
            Pausa(0.3);
        }
        // Un colpo di A su B: quanto perde B.
        float Colpo()
        {
            Piena(b);
            float prima = Vita(b);
            a.Colpisci(0.2);
            Aspetta(() => Vita(b) < prima, 1.5);
            Pausa(0.3);
            return prima - Vita(b);
        }
        uint idComando = 130;
        CommandResultMsg Manda(CommandType tipo, byte[] contenuto)
        {
            uint id = idComando++;
            Pausa(0.15);
            a.Comando(id, tipo, contenuto);
            CommandResultMsg r = null;
            Aspetta(() => (r = a.Esito(id)) != null, 5);
            Pausa(0.2);   // lo stato delle abilita arriva subito dopo l'esito
            return r;
        }
        void Atteso(string cosa, CommandResultMsg r, ErrorCode atteso)
        {
            string visto = r == null ? "nessuna risposta" : r.Success ? "riuscito" : $"rifiutato {r.ErrorCode}";
            Console.WriteLine($"  {cosa}: {visto}");
            bool va = r != null && (atteso == ErrorCode.None ? r.Success : !r.Success && r.ErrorCode == atteso);
            if (!va) Male($"{cosa}: atteso {(atteso == ErrorCode.None ? "riuscito" : "rifiutato " + atteso)}");
        }
        CommandResultMsg Scegli(ClassId c, bool secondaria = false) =>
            Manda(CommandType.ChooseClass, P(new ChooseClassCommand { Class = c, Secondary = secondaria }));
        CommandResultMsg Slot(AbilitySlotKind tipo, byte slot, AbilityId ab) =>
            Manda(CommandType.SetAbilitySlot, P(new SetAbilitySlotCommand { Kind = tipo, Slot = slot, Ability = ab }));
        string Stato() { Pausa(0.15); return a.ChiediAdmin("classe stato", "classe: ") ?? "(nessuna risposta)"; }
        // La vita massima secondo le statistiche mandate al client, dopo che
        // ne e' arrivata una nuova (o subito, se non deve cambiare).
        int VitaMax(int ricevuteDopo = -1)
        {
            if (ricevuteDopo >= 0) Aspetta(() => a.StatisticheRicevute > ricevuteDopo, 4);
            Pausa(0.3);
            return a.Statistiche?.MaxHp ?? -1;
        }
        string Slots() =>
            a.Abilita == null ? "(nessuno stato)"
            : $"attivi={string.Join("/", a.Abilita.ClassActiveSlots.Select(x => (int)x))} passivi={string.Join("/", a.Abilita.ClassPassiveSlots.Select(x => (int)x))}";

        a.Admin("setfaction 1"); b.Admin("setfaction 2");
        foreach (var s in new[] { a, b }) { s.Admin("clearinv"); s.Admin("abilita azzera"); s.Admin("effetto azzera"); }
        b.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        Tp(a, PX, PZ); Tp(b, PX + 1f, PZ);
        a.Admin("give StoneSword 1");
        Aspetta(() => a.Istanze.Values.Any(v => v.Tipo == ItemType.StoneSword), 5);
        a.Dotazione(ItemType.StoneSword, ItemType.None, SideItemType.None);
        a.Posizione(0);
        Pausa(0.4);
        Piena(a, b);
        int vitaBase = VitaMax();
        float colpoBase = Colpo();
        Console.WriteLine($"  senza classe: vita massima {vitaBase}, colpo di spada {colpoBase}");
        if (vitaBase <= 0) Male("le statistiche di A non sono arrivate");

        // 1. scelte rifiutate: secondaria senza principale, evoluzione,
        //    secondaria non sbloccata, secondaria uguale alla principale.
        Atteso("secondaria senza principale", Scegli(ClassId.Warrior, true), ErrorCode.NotAllowed);
        Atteso("Adepto dei Cristalli come principale", Scegli(ClassId.CrystalAdept), ErrorCode.NotAllowed);
        Atteso("principale Guerriero", Scegli(ClassId.Warrior), ErrorCode.None);
        Atteso("secondaria Cacciatore non sbloccata", Scegli(ClassId.Hunter, true), ErrorCode.NotAllowed);
        Atteso("secondaria uguale alla principale", Scegli(ClassId.Warrior, true), ErrorCode.NotAllowed);
        var st = Stato();
        Console.WriteLine($"  classe stato: {st}");
        if (st != "principale=Warrior:0/25 secondaria=-") Male("dopo la scelta il Guerriero deve essere a 0 uccisioni su 25");
        if (!Aspetta(() => a.HaLetto("abbatti ancora 25 creature"), 3)) Male("la scelta della classe non e' stata detta in chat");
        Atteso("Sfida prima dello sblocco", Manda(CommandType.UseAbility, P(new UseAbilityCommand { Ability = AbilityId.WarriorChallenge })),
               ErrorCode.AbilityNotUsable);

        // 2. la 25esima uccisione: un lupo ucciso da un danno nel tempo di A.
        Console.WriteLine($"  classe uccisioni 24 -> {a.ChiediAdmin("classe uccisioni 24", "classe: ")}");
        int statPrima = a.StatisticheRicevute;
        // B si allontana: "effetto stato vicino" deve trovare il lupo, non lei.
        Tp(b, PX + 40f, PZ);
        a.Admin("spawn wolf");
        Pausa(0.5);
        string lupo = a.ChiediAdmin("effetto stato vicino", "effetto: ") ?? "";
        string idLupo = lupo.Split(' ').FirstOrDefault(p => p.StartsWith("id="))?[3..];
        if (!lupo.Contains("tipo=Wolf") || idLupo == null) { Male($"il lupo fatto nascere non si trova ({lupo})"); idLupo = "0"; }
        a.ChiediAdmin($"effetto sanguina 1000 2 {idLupo}", "effetto: ");
        bool sbloccato = Aspetta(() => a.Abilita?.Classes?.Any(c => c.Class == ClassId.Warrior && c.Unlocked) == true, 6);
        if (idLupo != "0") a.Admin($"remove {idLupo}");
        Tp(b, PX + 1f, PZ);   // B torna accanto ad A, per i colpi
        st = Stato();
        Console.WriteLine($"  dopo il lupo: {st}; {Slots()}");
        if (!sbloccato || st != "principale=Warrior:sbloccata secondaria=-") Male("alla 25esima uccisione il Guerriero deve sbloccarsi");
        if (!Aspetta(() => a.HaLetto("Guerriero sbloccato"), 3)) Male("lo sblocco non e' stato detto in chat");
        var sb = a.Abilita?.Unlocked?.Select(u => u.Id).ToHashSet() ?? new HashSet<AbilityId>();
        if (!new[] { AbilityId.WarriorChallenge, AbilityId.WarriorToughSkin, AbilityId.WarriorSturdy, AbilityId.WarriorHeavyHand }.All(sb.Contains))
            Male("allo sblocco servono le 2 attive e le 2 passive base del Guerriero");
        if (Slots() != "attivi=1000/1001/0 passivi=1050/1051") Male("le abilita base devono andare negli slot vuoti");

        // 3. passive: Robusto +10% vita massima, al livello 3 +13%; Mano
        //    pesante +8% in mischia (22 -> 24).
        int vitaRobusto = VitaMax(statPrima);
        float colpoMano = Colpo();
        Console.WriteLine($"  con Robusto e Mano pesante: vita massima {vitaRobusto} (attesa {vitaBase + 10}), colpo {colpoMano} (atteso 24)");
        if (vitaRobusto != vitaBase + 10) Male("Robusto deve dare +10% di vita massima");
        if (colpoBase != 22f) Male($"il colpo di spada senza classe doveva fare 22, non {colpoBase}");
        if (colpoMano != 24f) Male("Mano pesante deve dare +8% al colpo di spada");
        statPrima = a.StatisticheRicevute;
        a.ChiediAdmin("abilita livello 1050 3", "abilita livello: ");
        int vitaL3 = VitaMax(statPrima);
        Console.WriteLine($"  Robusto al livello 3: vita massima {vitaL3} (attesa {vitaBase + 13})");
        if (vitaL3 != vitaBase + 13) Male("Robusto al livello 3 deve dare +13% (10% piu' due volte il 15%)");
        // tolta dallo slot, la passiva non vale piu'.
        Atteso("svuotare lo slot di Mano pesante", Slot(AbilitySlotKind.ClassPassive, 1, AbilityId.None), ErrorCode.None);
        float colpoSenza = Colpo();
        Console.WriteLine($"  senza Mano pesante nello slot: colpo {colpoSenza} (atteso {colpoBase})");
        if (colpoSenza != colpoBase) Male("fuori dallo slot Mano pesante non deve valere");

        // 4. cambio di classe: gli slot del Guerriero si svuotano, quello che
        //    e' sbloccato resta; il Guerriero sbloccato puo' fare da secondaria,
        //    con un solo slot attivo e uno passivo.
        statPrima = a.StatisticheRicevute;
        Atteso("principale Cacciatore", Scegli(ClassId.Hunter), ErrorCode.None);
        int vitaCacciatore = VitaMax(statPrima);
        st = Stato();
        Console.WriteLine($"  col Cacciatore: {st}; {Slots()}; vita massima {vitaCacciatore}");
        if (st != "principale=Hunter:0/25 secondaria=-") Male("il Cacciatore appena scelto deve essere a 0 su 25");
        if (Slots() != "attivi=0/0/0 passivi=0/0") Male("cambiando classe gli slot del Guerriero devono svuotarsi");
        if (vitaCacciatore != vitaBase) Male("senza Robusto nello slot la vita massima deve tornare quella di prima");
        if (a.Abilita?.Classes?.Any(c => c.Class == ClassId.Warrior && c.Unlocked) != true
            || a.Abilita?.Unlocked?.Any(u => u.Id == AbilityId.WarriorSturdy && u.Level == 3) != true)
            Male("cambiando classe il Guerriero deve restare sbloccato, con Robusto al livello 3");
        Atteso("Robusto nello slot senza il Guerriero", Slot(AbilitySlotKind.ClassPassive, 0, AbilityId.WarriorSturdy), ErrorCode.AbilityNotUsable);
        Atteso("secondaria Guerriero, gia sbloccato", Scegli(ClassId.Warrior, true), ErrorCode.None);
        statPrima = a.StatisticheRicevute;
        Atteso("Robusto dalla secondaria", Slot(AbilitySlotKind.ClassPassive, 0, AbilityId.WarriorSturdy), ErrorCode.None);
        int vitaSecondaria = VitaMax(statPrima);
        Atteso("una seconda passiva dalla secondaria", Slot(AbilitySlotKind.ClassPassive, 1, AbilityId.WarriorHeavyHand), ErrorCode.AbilityNotUsable);
        Atteso("Sfida dalla secondaria", Slot(AbilitySlotKind.ClassActive, 0, AbilityId.WarriorChallenge), ErrorCode.None);
        Atteso("una seconda attiva dalla secondaria", Slot(AbilitySlotKind.ClassActive, 1, AbilityId.WarriorToughSkin), ErrorCode.AbilityNotUsable);
        Console.WriteLine($"  Robusto dalla secondaria: vita massima {vitaSecondaria} (attesa {vitaBase + 13}); {Slots()}");
        if (vitaSecondaria != vitaBase + 13) Male("Robusto dalla secondaria deve valere come dalla principale");
        // un'attiva di classe senza effetto (arrivano con A14j) non va in ricarica.
        Atteso("Sfida, effetto non ancora fatto", Manda(CommandType.UseAbility, P(new UseAbilityCommand { Ability = AbilityId.WarriorChallenge })),
               ErrorCode.AbilityNotUsable);
        if (a.Abilita?.Cooldowns?.Length > 0) Male("un'attiva senza effetto non deve andare in ricarica");
        Atteso("togliere la secondaria", Scegli(ClassId.None, true), ErrorCode.None);
        if (Slots() != "attivi=0/0/0 passivi=0/0") Male("tolta la secondaria i suoi slot devono svuotarsi");

        // Si lascia pulito.
        foreach (var s in new[] { a, b }) { s.Admin("effetto azzera"); s.Admin("abilita azzera"); s.Admin("clearinv"); s.Admin("sethp 1000"); }
        a.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        Pausa(0.5);

        if (ok) Console.WriteLine("  OK: classi scelte, sbloccate a 25 uccisioni, slot e secondaria con le loro regole, passive di statistica applicate");
        return ok;
    }

    /// <summary>
    /// A14j1a: le attive del Guerriero. A e il Guerriero (fazione 1, spada di
    /// pietra), B il nemico (fazione 2, spada di pietra: il suo colpo fa 22),
    /// C l'alleata di A (fazione 1, a mani nude). Sfida e Provocazione non
    /// hanno ancora effetto (A14j1c, A14j2).
    /// </summary>
    public static bool Guerriero(string tokenA, string tokenB, string tokenC, string host, int port)
    {
        Console.WriteLine("--- guerriero: le attive (senza Sfida e Provocazione)");
        bool ok = true;
        void Male(string m) { Console.WriteLine("  FALLITA: " + m); ok = false; }

        using var a = new Sonda(tokenA, host, port);
        using var b = new Sonda(tokenB, host, port);
        using var c = new Sonda(tokenC, host, port);
        if (!a.AttendiPronta() || !b.AttendiPronta() || !c.AttendiPronta())
        { Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco"); return false; }

        const float PX = -150f, PZ = 250f;   // lo stesso punto delle prove delle armi
        static byte[] P<T>(T v) => MessagePack.MessagePackSerializer.Serialize(v);
        bool Aspetta(Func<bool> cosa, double secondi) =>
            a.Attendi(() => { b.Attendi(() => true, 0.01); c.Attendi(() => true, 0.01); return cosa(); }, secondi);
        void Pausa(double secondi) => Aspetta(() => false, secondi);
        static string Visto(CommandResultMsg r) => r == null ? "nessuna risposta" : r.Success ? "riuscito" : "rifiutato " + r.ErrorCode;
        float Vita(Sonda chi) => a.Stato(chi.Id).Vita;
        void Tp(Sonda s, float x, float z)
        {
            s.Admin($"tp {x.ToString(Inv)} {z.ToString(Inv)}");
            Aspetta(() => Dist(s.X, s.Z, x, z) < 0.5f, 6);
            s.Cammina(0f, 0f, 0.1);
        }
        void Piena(params Sonda[] chi)
        {
            foreach (var s in chi) s.Admin("sethp 1000");
            Aspetta(() => chi.All(s => Vita(s) >= 100f), 3);
            Pausa(0.3);
        }
        string Effetto(Sonda s, string riga) { Pausa(0.15); return s.ChiediAdmin("effetto " + riga, "effetto: ") ?? "(nessuna risposta)"; }
        string StatoDi(Sonda s) => Effetto(a, s == a ? "stato" : $"stato {s.Id}");
        static (float X, float Z) Pos(string riga)
        {
            int i = riga.IndexOf("pos=", StringComparison.Ordinal);
            if (i < 0) return (float.NaN, float.NaN);
            int f = riga.IndexOf(' ', i);
            var p = (f < 0 ? riga[(i + 4)..] : riga[(i + 4)..f]).Split(',');
            return p.Length == 2 && float.TryParse(p[0], System.Globalization.NumberStyles.Float, Inv, out var x)
                   && float.TryParse(p[1], System.Globalization.NumberStyles.Float, Inv, out var z) ? (x, z) : (float.NaN, float.NaN);
        }
        uint idComando = 140;
        CommandResultMsg Prova(AbilityId id, float yaw = 90f)
        {
            uint idSlot = idComando++, idUso = idComando++;
            Pausa(0.15);
            a.Comando(idSlot, CommandType.SetAbilitySlot, P(new SetAbilitySlotCommand
                { Kind = AbilitySlotKind.ClassActive, Slot = 0, Ability = id }));
            CommandResultMsg r = null;
            Aspetta(() => (r = a.Esito(idSlot)) != null, 5);
            if (r == null || !r.Success) Male($"{id} nello slot: {Visto(r)}");
            Pausa(0.15); a.Admin("ricarica");
            Pausa(0.15);
            a.Comando(idUso, CommandType.UseAbility, P(new UseAbilityCommand { Ability = id, Yaw = yaw }));
            r = null;
            Aspetta(() => (r = a.Esito(idUso)) != null, 5);
            Pausa(0.2);
            return r;
        }
        void Atteso(string cosa, CommandResultMsg r, ErrorCode atteso)
        {
            bool giusto = r != null && (atteso == ErrorCode.None ? r.Success : !r.Success && r.ErrorCode == atteso);
            Console.WriteLine($"  {cosa}: {Visto(r)}{(giusto ? "" : $" (atteso {(atteso == ErrorCode.None ? "riuscito" : atteso.ToString())})")}");
            if (!giusto) Male($"{cosa}: atteso {(atteso == ErrorCode.None ? "riuscito" : "rifiutato " + atteso)}");
        }
        void Pulisci(params Sonda[] chi) { foreach (var s in chi) Effetto(a, $"azzera {s.Id}"); }
        void Controlla(string cosa, float visto, float atteso)
        {
            Console.WriteLine($"  {cosa}: {visto} (atteso {atteso})");
            if (visto != atteso) Male($"{cosa}: {visto} invece di {atteso}");
        }
        void Contiene(string cosa, string riga, string pezzo, bool deve = true)
        {
            Console.WriteLine($"  {cosa}: {riga}");
            if (riga.Contains(pezzo) != deve) Male($"{cosa}: {(deve ? "manca" : "non doveva esserci")} '{pezzo}'");
        }
        // Un colpo di spada di B sul piu vicino: quanto perde chi.
        float ColpoDiB(Sonda chi, params Sonda[] anche)
        {
            Piena(new[] { chi }.Concat(anche).ToArray());
            float prima = Vita(chi);
            b.Colpisci(0.1);
            Aspetta(() => Vita(chi) < prima, 1.5);
            Pausa(0.3);
            return prima - Vita(chi);
        }

        a.Admin("setfaction 1"); b.Admin("setfaction 2"); c.Admin("setfaction 1");
        foreach (var s in new[] { a, b, c }) { s.Admin("clearinv"); s.Admin("abilita azzera"); s.Admin("effetto azzera"); }
        c.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        foreach (var s in new[] { a, b })
        {
            s.Admin("give StoneSword 1");
            Aspetta(() => s.Istanze.Values.Any(v => v.Tipo == ItemType.StoneSword), 5);
            s.Dotazione(ItemType.StoneSword, ItemType.None, SideItemType.None);
            s.Posizione(0);
        }
        Tp(a, PX, PZ); Tp(b, PX + 1.5f, PZ); Tp(c, PX - 30f, PZ);
        Pausa(0.15); Console.WriteLine($"  {a.ChiediAdmin("classe guerriero", "classe: ")}");
        Pausa(0.15); Console.WriteLine($"  {a.ChiediAdmin("classe sblocca", "classe: ")}");
        Pausa(0.15); a.Admin("abilita sblocca guerriero");
        Pausa(0.4);
        Piena(a, b, c);

        // 0. Sfida non ha ancora effetto: non parte e non va in ricarica.
        Atteso("Sfida, effetto non ancora fatto", Prova(AbilityId.WarriorChallenge), ErrorCode.AbilityNotUsable);

        // 1. Pelle dura: -30% danno subito per 4 s; il colpo di B da 22 a 15.
        float base_ = ColpoDiB(a);
        Controlla("colpo di B su A senza niente", base_, 22f);
        Atteso("Pelle dura", Prova(AbilityId.WarriorToughSkin), ErrorCode.None);
        Contiene("Pelle dura, effetti di A", StatoDi(a), "danno subito -30%");
        Controlla("colpo di B con Pelle dura", ColpoDiB(a), 15f);
        Pulisci(a);

        // 2. Furia: +30% danno e velocita d'attacco, +15% danno subito (22 -> 25).
        Atteso("Furia", Prova(AbilityId.WarriorFury), ErrorCode.None);
        var furia = StatoDi(a);
        Contiene("Furia, effetti di A", furia, "Damage +30%");
        Contiene("Furia, effetti di A", furia, "AttackSpeed +30%");
        Contiene("Furia, effetti di A", furia, "danno subito +15%");
        Controlla("colpo di B con la Furia", ColpoDiB(a), 25f);
        Pulisci(a);

        // 3. Spallata: a 6 m non parte; a 1,5 m B e stordito, senza danno.
        Tp(b, PX + 6f, PZ);
        Atteso("Spallata senza nessuno a portata", Prova(AbilityId.WarriorShoulderBash), ErrorCode.AbilityNotUsable);
        Tp(b, PX + 1.5f, PZ);
        Piena(b);
        float vb = Vita(b);
        Atteso("Spallata", Prova(AbilityId.WarriorShoulderBash), ErrorCode.None);
        Contiene("Spallata, effetti di B", StatoDi(b), "stordito");
        Controlla("Spallata, B perde", vb - Vita(b), 0f);
        Pulisci(b);

        // 4. Resistere: per 5 s niente stordimento ne rallentamento.
        Atteso("Resistere", Prova(AbilityId.WarriorEndure), ErrorCode.None);
        Contiene("Resistere, stordire A", Effetto(a, "stordisci 2"), "non si stordisce");
        Effetto(a, "rallenta 50 2");
        var resiste = StatoDi(a);
        Contiene("Resistere, effetti di A", resiste, "immune");
        Contiene("Resistere, effetti di A", resiste, "rallentato", deve: false);
        Pulisci(a);

        // 5. Secondo fiato: a vita piena non parte; da 50 cura 20 in 5 s.
        Piena(a);
        Atteso("Secondo fiato a vita piena", Prova(AbilityId.WarriorSecondWind), ErrorCode.AbilityNotUsable);
        a.Admin("sethp 50");
        Aspetta(() => Vita(a) == 50f, 3);
        Atteso("Secondo fiato", Prova(AbilityId.WarriorSecondWind), ErrorCode.None);
        Contiene("Secondo fiato, effetti di A", StatoDi(a), "cura nel tempo 20 in 5 colpi");
        Aspetta(() => Vita(a) >= 70f, 7);
        Pausa(1.2);
        Controlla("Secondo fiato, vita di A dopo 5 s", Vita(a), 70f);
        Pulisci(a);

        // 6. Balzo: 8 m avanti; B a un metro dall'atterraggio rallenta del 30%.
        Tp(b, PX + 9f, PZ);
        Atteso("Balzo", Prova(AbilityId.WarriorLeap), ErrorCode.None);
        var (ax, az) = Pos(StatoDi(a));
        Console.WriteLine($"  Balzo, A atterra a ({ax:F2}, {az:F2}) (atteso {PX + 8f}, {PZ})");
        if (float.IsNaN(ax) || Dist(ax, az, PX + 8f, PZ) > 0.4f) Male("il Balzo deve portare A 8 m avanti");
        Contiene("Balzo, effetti di B", StatoDi(b), "rallentato 30%");
        Pulisci(a, b);

        // 7. Grido di guerra: +15% danno ad A e a C alleata, non a B.
        Tp(a, PX, PZ); Tp(b, PX + 1.5f, PZ); Tp(c, PX - 1.5f, PZ);
        Atteso("Grido di guerra", Prova(AbilityId.WarriorWarCry), ErrorCode.None);
        Contiene("Grido di guerra, effetti di A", StatoDi(a), "Damage +15%");
        Contiene("Grido di guerra, effetti di C", StatoDi(c), "Damage +15%");
        Contiene("Grido di guerra, effetti di B", StatoDi(b), "Damage", deve: false);
        Pulisci(a, b, c);

        // 8. Proteggere: B colpisce C; meta del colpo (11 di 22) va ad A.
        Tp(b, PX - 3f, PZ);   // C e la piu vicina a B, A sta a 3 m
        Controlla("colpo di B su C senza Proteggere", ColpoDiB(c, a), 22f);
        Atteso("Proteggere", Prova(AbilityId.WarriorProtect), ErrorCode.None);
        Piena(a, c);
        float va = Vita(a), vc = Vita(c);
        b.Colpisci(0.1);
        Aspetta(() => Vita(c) < vc, 1.5);
        Pausa(0.3);
        Controlla("Proteggere, C perde", vc - Vita(c), 11f);
        Controlla("Proteggere, A perde", va - Vita(a), 11f);
        // Il danno preso al posto di altri non porta A sotto 1 (Proteggere di
        // nuovo: i 6 s di prima possono essere finiti).
        Atteso("Proteggere di nuovo", Prova(AbilityId.WarriorProtect), ErrorCode.None);
        a.Admin("sethp 5");
        Aspetta(() => Vita(a) == 5f, 3);
        Piena(c);
        b.Colpisci(0.1);
        Pausa(0.5);
        var dopo = a.Stato(a.Id);
        Console.WriteLine($"  Proteggere con A a 5: vita di A {dopo.Vita}, a terra {dopo.ATerra}");
        if (dopo.Vita != 1f || dopo.ATerra || dopo.Morto) Male("chi protegge non deve scendere sotto 1 per i colpi presi al posto di altri");

        // Si lascia pulito.
        foreach (var s in new[] { a, b, c }) { s.Admin("effetto azzera"); s.Admin("abilita azzera"); s.Admin("clearinv"); s.Admin("sethp 1000"); }
        a.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        b.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        Pausa(0.5);

        if (ok) Console.WriteLine("  OK: Pelle dura, Furia, Spallata, Resistere, Secondo fiato, Balzo, Grido di guerra e Proteggere fanno quello che dice il catalogo");
        return ok;
    }


    /// <summary>
    /// A14j1b: le passive del Guerriero con un effetto loro. A e il Guerriero
    /// (fazione 1, spada di pietra), B il nemico (fazione 2, spada di pietra):
    /// un colpo di spada fa 22 in tutte e due le direzioni. Le passive si
    /// mettono una alla volta nel primo slot passivo, il secondo resta vuoto
    /// (cosi Robusto e Mano pesante non cambiano i conti). Livello 1.
    /// </summary>
    public static bool PassiveDelGuerriero(string tokenA, string tokenB, string host, int port)
    {
        Console.WriteLine("--- passive del guerriero: Sangue caldo, Inarrestabile, Sete di sangue, Bastione, Massacro");
        bool ok = true;
        void Male(string m) { Console.WriteLine("  FALLITA: " + m); ok = false; }

        using var a = new Sonda(tokenA, host, port);
        using var b = new Sonda(tokenB, host, port);
        if (!a.AttendiPronta() || !b.AttendiPronta())
        { Console.WriteLine("  FALLITA: le sonde non sono entrate in gioco"); return false; }

        const float PX = -150f, PZ = 250f;   // lo stesso punto delle prove delle armi
        static byte[] P<T>(T v) => MessagePack.MessagePackSerializer.Serialize(v);
        bool Aspetta(Func<bool> cosa, double secondi) =>
            a.Attendi(() => { b.Attendi(() => true, 0.01); return cosa(); }, secondi);
        void Pausa(double secondi) => Aspetta(() => false, secondi);
        static string Visto(CommandResultMsg r) => r == null ? "nessuna risposta" : r.Success ? "riuscito" : "rifiutato " + r.ErrorCode;
        float Vita(Sonda chi) => a.Stato(chi.Id).Vita;
        void Tp(Sonda s, float x, float z)
        {
            s.Admin($"tp {x.ToString(Inv)} {z.ToString(Inv)}");
            Aspetta(() => Dist(s.X, s.Z, x, z) < 0.5f, 6);
            s.Cammina(0f, 0f, 0.1);
        }
        void Imposta(Sonda s, int hp)
        {
            s.Admin($"sethp {hp}");
            Aspetta(() => Vita(s) == Math.Min(hp, 100), 3);
            Pausa(0.3);
        }
        string Effetto(string riga) { Pausa(0.15); return a.ChiediAdmin("effetto " + riga, "effetto: ") ?? "(nessuna risposta)"; }
        uint idComando = 200;
        void Passiva(AbilityId id)
        {
            foreach (var (slot, ab) in new[] { ((byte)0, id), ((byte)1, AbilityId.None) })
            {
                uint n = idComando++;
                Pausa(0.15);
                a.Comando(n, CommandType.SetAbilitySlot, P(new SetAbilitySlotCommand
                    { Kind = AbilitySlotKind.ClassPassive, Slot = slot, Ability = ab }));
                CommandResultMsg r = null;
                Aspetta(() => (r = a.Esito(n)) != null, 5);
                if (r == null || !r.Success) Male($"{(ab == AbilityId.None ? "svuotare" : ab.ToString())} nello slot passivo {slot}: {Visto(r)}");
            }
            Pausa(0.3);
        }
        void Controlla(string cosa, float visto, float atteso)
        {
            Console.WriteLine($"  {cosa}: {visto} (atteso {atteso})");
            if (visto != atteso) Male($"{cosa}: {visto} invece di {atteso}");
        }
        void Contiene(string cosa, string riga, string pezzo)
        {
            Console.WriteLine($"  {cosa}: {riga}");
            if (!riga.Contains(pezzo)) Male($"{cosa}: manca '{pezzo}'");
        }
        // Un colpo di spada di chi colpisce sul piu vicino: quanto perde chi.
        float Colpo(Sonda da, Sonda chi)
        {
            float prima = Vita(chi);
            da.Colpisci(0.1);
            Aspetta(() => Vita(chi) < prima, 1.5);
            Pausa(0.3);
            return prima - Vita(chi);
        }

        a.Admin("setfaction 1"); b.Admin("setfaction 2");
        foreach (var s in new[] { a, b })
        {
            s.Admin("clearinv"); s.Admin("abilita azzera"); s.Admin("effetto azzera");
            s.Admin("give StoneSword 1");
            Aspetta(() => s.Istanze.Values.Any(v => v.Tipo == ItemType.StoneSword), 5);
            s.Dotazione(ItemType.StoneSword, ItemType.None, SideItemType.None);
            s.Posizione(0);
        }
        Tp(a, PX, PZ); Tp(b, PX + 1.5f, PZ);
        Pausa(0.15); Console.WriteLine($"  {a.ChiediAdmin("classe guerriero", "classe: ")}");
        Pausa(0.15); Console.WriteLine($"  {a.ChiediAdmin("classe sblocca", "classe: ")}");
        Pausa(0.15); a.Admin("abilita sblocca guerriero");
        Pausa(0.4);

        // 1. Bastione: a vita piena il colpo di B fa 22; sotto meta 18 (-20%).
        Passiva(AbilityId.WarriorBulwark);
        Imposta(a, 1000); Imposta(b, 1000);
        Controlla("Bastione, colpo di B su A a vita piena", Colpo(b, a), 22f);
        Imposta(a, 40);
        Controlla("Bastione, colpo di B su A a 40 di vita", Colpo(b, a), 18f);

        // 2. Sete di sangue: a vita piena il colpo di A fa 22; a 40 (60% che
        //    manca, +12%) fa 25.
        Passiva(AbilityId.WarriorBloodlust);
        Imposta(a, 1000); Imposta(b, 1000);
        Controlla("Sete di sangue, colpo di A a vita piena", Colpo(a, b), 22f);
        Imposta(a, 40); Imposta(b, 1000);
        Controlla("Sete di sangue, colpo di A a 40 di vita", Colpo(a, b), 25f);

        // 3. Sangue caldo: a 50, un colpo dato cura 1 (1% di 100).
        Passiva(AbilityId.WarriorHotBlood);
        Imposta(a, 50); Imposta(b, 1000);
        Controlla("Sangue caldo, colpo di A", Colpo(a, b), 22f);
        Controlla("Sangue caldo, vita di A dopo il colpo", Vita(a), 51f);

        // 4. Inarrestabile: senza, lo stordimento di 2 s dura 2; con, 1,2 (-40%).
        Passiva(AbilityId.None);
        Contiene("stordimento senza Inarrestabile", Effetto("stordisci 2"), "stordito 2.0s");
        Effetto("azzera");
        Passiva(AbilityId.WarriorUnstoppable);
        Contiene("stordimento con Inarrestabile", Effetto("stordisci 2"), "stordito 1.2s");
        Effetto("azzera");
        Contiene("rallentamento con Inarrestabile", Effetto("rallenta 50 2"), "rallentato 50% 1.2s");
        Effetto("azzera");

        // 5. Massacro: A a 50 uccide B (a 1 di vita): +20% velocita d'attacco
        //    per 5 s e 10 di vita.
        Passiva(AbilityId.WarriorMassacre);
        Imposta(a, 50); Imposta(b, 1);
        a.Colpisci(0.1);
        Aspetta(() => b.Stato(b.Id).Morto || Vita(b) <= 0f, 2);
        Pausa(0.3);
        Controlla("Massacro, vita di A dopo l'uccisione", Vita(a), 60f);
        Contiene("Massacro, effetti di A", Effetto("stato"), "AttackSpeed +20%");
        Aspetta(() => !b.Stato(b.Id).Morto && Vita(b) > 0f, 6);

        // Si lascia pulito.
        foreach (var s in new[] { a, b }) { s.Admin("effetto azzera"); s.Admin("abilita azzera"); s.Admin("clearinv"); s.Admin("sethp 1000"); }
        a.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        b.Dotazione(ItemType.None, ItemType.None, SideItemType.None);
        Pausa(0.5);

        if (ok) Console.WriteLine("  OK: Bastione, Sete di sangue, Sangue caldo, Inarrestabile e Massacro fanno quello che dice il catalogo");
        return ok;
    }
    static readonly System.Globalization.CultureInfo Inv =
        System.Globalization.CultureInfo.InvariantCulture;
}
