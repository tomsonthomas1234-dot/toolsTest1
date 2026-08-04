// Imposta la password di un account, senza farla passare da nessun'altra parte.
//
//   Password.exe <nome>
//
// La password si digita qui e finisce solo in due posti: la memoria di questo
// processo per il tempo di calcolarne l'hash, e la colonna password_hash come
// PBKDF2. Non compare a schermo, non finisce nella cronologia della shell, non
// viene scritta in nessun log.
//
// L'hash lo calcola Precursori.Shared.Auth.Passwords, la stessa del gioco: una
// copia separata di PBKDF2 qui dentro continuerebbe a funzionare anche dopo un
// cambio di parametri dall'altra parte, producendo hash che il gioco non sa piu
// leggere.
using System.Text.Json;
using Npgsql;
using Precursori.Shared.Auth;

if (args.Length < 1)
{
    Console.WriteLine("Uso: Password <nome account>");
    return 2;
}
string nome = args[0];

// La stringa di connessione si legge da dove la legge il server, cosi non
// esistono due verita su dove sia il database.
string appsettings = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory,
    "..", "..", "..", "..", "..", "bootstrap", "02_gameserver", "src",
    "Precursori.GameServer", "appsettings.json"));

string conn;
try
{
    using var doc = JsonDocument.Parse(File.ReadAllText(appsettings));
    conn = doc.RootElement.GetProperty("Persistence").GetProperty("PostgresConnString").GetString()!;
}
catch (Exception ex)
{
    Console.WriteLine($"Non trovo la configurazione del database ({appsettings}): {ex.Message}");
    return 2;
}

Console.WriteLine($"Account: {nome}");
string p1 = LeggiNascosta("Nuova password: ");
if (p1.Length < 8)
{
    Console.WriteLine("Troppo corta: almeno 8 caratteri.");
    return 1;
}
string p2 = LeggiNascosta("Ripetila:       ");
if (p1 != p2)
{
    Console.WriteLine("Le due password non coincidono. Non ho cambiato niente.");
    return 1;
}

string hash = Passwords.Hash(p1);

await using var db = new NpgsqlConnection(conn);
await db.OpenAsync();

await using var cmd = new NpgsqlCommand(@"
    INSERT INTO accounts (name, password_hash)
    VALUES (@nome, @hash)
    ON CONFLICT (name) DO UPDATE SET password_hash = EXCLUDED.password_hash
    RETURNING id, (xmax = 0) AS creato;", db);
cmd.Parameters.AddWithValue("nome", nome);
cmd.Parameters.AddWithValue("hash", hash);

try
{
    await using var r = await cmd.ExecuteReaderAsync();
    if (await r.ReadAsync())
        Console.WriteLine(r.GetBoolean(1)
            ? $"Creato l'account '{nome}' (id {r.GetInt64(0)}) con la password data."
            : $"Password dell'account '{nome}' (id {r.GetInt64(0)}) reimpostata.");
}
catch (PostgresException ex) when (ex.SqlState == "42P10")
{
    // Nessun vincolo di unicita sul nome: si ripiega su un aggiornamento
    // esplicito, che e cio che serve quando l'account esiste gia.
    await using var upd = new NpgsqlCommand(
        "UPDATE accounts SET password_hash = @hash WHERE name = @nome RETURNING id;", db);
    upd.Parameters.AddWithValue("nome", nome);
    upd.Parameters.AddWithValue("hash", hash);
    var id = await upd.ExecuteScalarAsync();
    if (id == null) { Console.WriteLine($"Nessun account chiamato '{nome}'."); return 1; }
    Console.WriteLine($"Password dell'account '{nome}' (id {id}) reimpostata.");
}

Console.WriteLine("Ora puoi entrare dal gioco con questo nome e questa password.");
return 0;

// Lettura senza eco. Il tasto di ritorno non si stampa mai, nemmeno come
// asterischi: il numero di asterischi e gia un'informazione sulla lunghezza.
static string LeggiNascosta(string invito)
{
    Console.Write(invito);
    var sb = new System.Text.StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
        if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
        if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
    }
    return sb.ToString();
}
