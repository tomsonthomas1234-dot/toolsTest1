# Strumenti di Precursori

Script e banchi di prova per il server di gioco, il web API e il client Unity.
Tutti si invocano **sempre allo stesso modo, senza argomenti**: è una scelta, non
una limitazione. Un comando che cambia forma a ogni uso costringe ad autorizzarlo
ogni volta, e "consenti sempre" non serve a niente.

## Uso quotidiano

| comando | cosa fa |
|---|---|
| `build.ps1` | ferma il server, compila `shared`, game server e web API, e se Unity è chiuso lancia `BatchVerify` sul client |
| `run.ps1` | avvia web API e game server, fermando prima le istanze già attive |
| `ship.ps1` | committa e pusha i repository che hanno modifiche **e** un messaggio pronto in `msg/` |

`ship.ps1` legge i messaggi da `msg/<repo>.txt` e cancella il file a commit
riuscito: un messaggio esiste solo fra il momento in cui lo si scrive e quello in
cui parte.

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
