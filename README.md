# Strumenti di Precursori

Script e banchi di prova per il server di gioco, il web API e il client Unity.
Tutti si invocano **sempre allo stesso modo, senza argomenti**: è una scelta, non
una limitazione. Un comando che cambia forma a ogni uso costringe ad autorizzarlo
ogni volta, e "consenti sempre" non serve a niente.

## Uso quotidiano

| comando | cosa fa |
|---|---|
| `build.ps1` | ferma il server, compila `shared`, game server e web API, e se Unity è chiuso lancia `BatchVerify` sul client |
| `run.ps1` | avvia web API e game server veri, fermando prima le istanze vere già attive; non fa niente se stanno girando le prove |
| `ship.ps1` | committa e pusha i repository che hanno modifiche **e** un messaggio pronto in `msg/` |

`ship.ps1` legge i messaggi da `msg/<repo>.txt` e cancella il file a commit
riuscito: un messaggio esiste solo fra il momento in cui lo si scrive e quello in
cui parte.

### `run.ps1` e le prove

Server vero e server delle prove usano le stesse porte, quindi non possono
girare insieme. `regole.ps1` e `bench.ps1` scrivono, finché girano,
`regole.lock` o `bench.lock` con il loro PID: se `run.ps1` ne trova uno vivo
dice chi sta girando e non avvia niente. Un lucchetto rimasto da un giro
interrotto si riconosce (il PID è morto o è di un altro processo) e viene tolto.
In più i processi delle prove portano `--Prove=regole` o `--Prove=bench` nella
riga di comando, e `run.ps1` ferma solo quelli senza.

Le prove, invece, fermano anche il server vero e non lo riaccendono: finito un
giro, per tornare a giocare si lancia `run.ps1`.

### Perché il server avviato da un agente si spegneva

Gli agenti lanciano i comandi da una shell che sta dentro un *job* di Windows.
Un processo avviato con `Start-Process` resta in quel job: quando il comando
viene fermato, o la sessione dell'agente si chiude, Windows chiude il job e
uccide anche il server, senza errori nei log. Provato il 4 novembre: un figlio
di `Start-Process` è morto allo stop del comando che lo aveva lanciato, uno
creato tramite WMI (`Win32_Process.Create`) è rimasto vivo. Se il comando
finisce da solo, invece, il figlio sopravvive: per questo succedeva solo a volte.

Per questo `run.ps1` avvia web API e server tramite WMI, con l'uscita rediretta
nei soliti `logs/server.log` e `logs/webapi.log`. Un processo creato così non
eredita le variabili d'ambiente di chi lancia: il server vero si configura solo
dai suoi file, quindi va bene. Le prove continuano a usare `Start-Process`,
perché devono passare variabili d'ambiente al server e devono morire con chi le
lancia.

## Prove

| comando | cosa misura |
|---|---|
| `regole.ps1` | che il server **dica di no quando deve e ricordi quando deve** — nove controlli |
| `bench.ps1` | quanto costa il server sotto carico: 200 client, snapshot interi contro differenziali |
| `ispeziona.ps1` | cosa c'è costruito nel mondo vero, e chi possiede quali settori. Non modifica nulla |

`regole.ps1` gira su **un mondo separato**, azzerato a ogni giro. Serve a due
cose: non sporcare la partita vera — è già successo, uno stendardo di prova aveva
rivendicato un settore — e restare ripetibile, perché una prova che eredita lo
stato della precedente passa o fallisce a seconda di quante volte l'hai lanciata.

`bench.ps1` misura le due modalità **di fila nella stessa sessione**. Due misure
prese in momenti diversi, con la macchina in stati diversi, non si confrontano: è
proprio questo che aveva reso sbagliata la prima conclusione sui differenziali.

## Amministrazione

`password.ps1 [nome]` imposta la password di un account. Va lanciato **da una
persona**: la password si digita e basta, non passa da argomenti — finirebbe
nella cronologia della shell — non compare a schermo e non finisce in nessun log.

## Se PowerShell rifiuta di eseguirli

Senza cambiare le impostazioni del sistema, per una singola esecuzione:

```
powershell -ExecutionPolicy Bypass -File C:\precursori\tools\build.ps1
```

I due progetti C# (`loadtest`, `password`) si possono anche lanciare
direttamente dal loro eseguibile, senza passare dagli script.
