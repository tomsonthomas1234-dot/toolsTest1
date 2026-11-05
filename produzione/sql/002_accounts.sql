CREATE TABLE IF NOT EXISTS accounts (
  id BIGSERIAL PRIMARY KEY,
  name TEXT NOT NULL,
  password_hash TEXT NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  last_login_at TIMESTAMPTZ
);

-- Il nome e l'identita pubblica del giocatore e deve essere unico, ma il
-- confronto va fatto senza distinzione di maiuscole: "Torak" e "torak" devono
-- essere la stessa persona, altrimenti l'elenco degli amministratori e ogni
-- futura lista di permessi diventano aggirabili cambiando una lettera.
CREATE UNIQUE INDEX IF NOT EXISTS idx_accounts_name_lower
  ON accounts (lower(name));
