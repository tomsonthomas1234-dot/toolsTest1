CREATE TABLE IF NOT EXISTS world_snapshots (
  id BIGSERIAL PRIMARY KEY,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  tick INT NOT NULL,
  object_key TEXT NOT NULL,
  byte_len INT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_world_snapshots_created_at
  ON world_snapshots (created_at DESC);
