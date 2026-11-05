# Precursori su un server Linux

Questa cartella mette in piedi tutto quello che serve per giocare da fuori:
game server, web API, Postgres e MinIO, ognuno nel suo container. Redis non
c'è: oggi né il server né la web API lo usano.

| file | cosa fa |
|---|---|
| `docker-compose.yml` | lo stack di produzione |
| `.env.esempio` | i valori da scegliere: password, segreto, amministratore |
| `Dockerfile.gameserver`, `Dockerfile.webapi` | come si costruiscono i due programmi |
| `sql/` | le tabelle create al primo avvio (copia di `bootstrap/00_infra/sql`) |
| `docker-compose.prove.yml` | solo per `regole-docker.ps1`, mai in produzione |

È separato da `bootstrap/00_infra/docker-compose.yml`, quello di sviluppo:
lì le password sono scritte nel file e le porte dei database sono aperte a
tutti. Qui nessuna password ha un valore di ripiego, i database non sono
raggiungibili da fuori e il game server parte con `Server:ProductionChecks`
acceso, quindi si rifiuta di aprire col segreto di sviluppo.

## Passo passo

Su una macchina Linux appena noleggiata (Ubuntu o Debian), da un utente con
`sudo`.

### 1. Docker

Dalla guida ufficiale (docs.docker.com, "Install Docker Engine" per la propria
distribuzione), installare **Docker Engine** e il plugin **compose**. Poi
controllare:

```bash
docker --version
docker compose version
```

### 2. Il codice, con le stesse cartelle del PC

Il server e la web API puntano a `shared` per percorso relativo: i repository
vanno messi negli stessi posti che hanno in `C:\precursori`. Il client non
serve.

```bash
sudo mkdir -p /opt/precursori && sudo chown $USER /opt/precursori
cd /opt/precursori
git clone https://github.com/tomsonthomas1234-dot/sharedTest1.git bootstrap/01_shared/src/Precursori.Shared
git clone https://github.com/tomsonthomas1234-dot/serverTest1     bootstrap/02_gameserver/src/Precursori.GameServer
git clone https://github.com/tomsonthomas1234-dot/webapiTest1.git bootstrap/05_webapi/src/Precursori.WebApi
git clone https://github.com/tomsonthomas1234-dot/toolsTest1.git  tools
```

Se i repository sono privati, git chiede di autenticarsi: serve un token di
GitHub con accesso in sola lettura, o una chiave di deploy per ciascuno.

### 3. I valori di produzione

```bash
cd /opt/precursori/tools/produzione
cp .env.esempio .env
chmod 600 .env
nano .env
```

Riempire ogni riga. Per password e segreto:

```bash
openssl rand -hex 32
```

Il file `.env` non si committa mai (è in `.gitignore`) e resta solo su quella
macchina. Tenerne una copia in un posto sicuro: senza `POSTGRES_PASSWORD` e
`MINIO_PASSWORD` non si rientra nei dati.

### 4. Accendere

```bash
docker compose --env-file .env up -d --build
```

La prima volta scarica le immagini e compila: qualche minuto. Poi:

```bash
docker compose ps
docker compose logs -f gameserver
```

Il server è pronto quando nel log compare `UDP started on port 27015`. Se
invece compare `ProductionChecks e acceso ma la configurazione e ancora da
sviluppo`, il messaggio dice cosa manca.

I container ripartono da soli dopo un crash o un riavvio della macchina
(`restart: unless-stopped`).

### 5. Aprire le porte

Solo queste due, nient'altro:

- **27015 UDP** — il gioco
- **5080 TCP** — accesso e registrazione (web API)

Sia nel firewall della macchina (per esempio `sudo ufw allow 27015/udp` e
`sudo ufw allow 5080/tcp`) sia, se c'è, nel pannello del fornitore.
Attenzione: Docker apre le porte pubblicate anche scavalcando `ufw`. Per
questo nel `docker-compose.yml` sono pubblicate solo queste due.

Controllo da fuori, dal PC:

```
curl http://<indirizzo-del-server>:5080/health
```

### 6. Aggiornare

```bash
cd /opt/precursori
for r in bootstrap/01_shared/src/Precursori.Shared bootstrap/02_gameserver/src/Precursori.GameServer bootstrap/05_webapi/src/Precursori.WebApi tools; do git -C $r pull --ff-only; done
cd tools/produzione
docker compose --env-file .env up -d --build
```

Ricostruisce solo quello che è cambiato e riavvia i container toccati.

## Da sapere

- **Il mondo si salva a intervalli, non allo spegnimento.** Un riavvio perde
  quello che è successo dopo l'ultimo salvataggio periodico. Il
  `stop_grace_period` di 30 secondi lascia solo finire un salvataggio già
  partito.
- **Backup:** non ancora. È il compito A4. I dati stanno nei volumi Docker
  `precursori_pg_data` e `precursori_minio_data`: `docker compose down -v`
  **li cancella**, non usarlo mai qui.
- **MinIO** usa il suo utente principale anche come chiave del game server.
  Per una alpha chiusa basta; più avanti conviene un utente dedicato con
  accesso al solo secchio `precursori`.
- **Il traffico della web API è in chiaro (HTTP).** Le password viaggiano
  leggibili fino al server. Prima di aprire a chi non si conosce serve HTTPS
  (un proxy come Caddy davanti alla porta 5080, con un dominio).
- **Il segreto (`AUTH_SECRET`)** è lo stesso per web API e game server.
  Cambiarlo fa scadere tutti gli accessi.

## Provarlo su questo PC

```
& C:\precursori\tools\regole-docker.ps1
```

Costruisce e accende lo stesso stack come progetto Docker a parte, con
password generate a caso, fa girare le prove di `regole.ps1` contro quello
(compreso un riavvio del container e il rifiuto del segreto di sviluppo), poi
spegne e cancella tutto. Non tocca i container di sviluppo né il mondo vero.
