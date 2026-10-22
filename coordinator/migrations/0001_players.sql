-- One row per player. The device key (ECDSA P-256, SPKI DER, base64) proves who is acting as player_id.
-- UNIQUE(name_lc, tag): many players may share a name, "Name#12345" is unique; the database, not a check-then-insert, decides.
CREATE TABLE players (
  player_id TEXT PRIMARY KEY,
  name      TEXT NOT NULL,
  name_lc   TEXT NOT NULL,
  tag       TEXT NOT NULL,
  pubkey    TEXT NOT NULL,
  created   INTEGER NOT NULL,
  last_seen INTEGER NOT NULL,
  status    TEXT NOT NULL DEFAULT 'ok',
  UNIQUE (name_lc, tag)
);

-- A name#tag a player gave up by renaming stays reserved for a while (no sniping of a recently used pair).
CREATE TABLE name_holds (
  name_lc TEXT NOT NULL,
  tag     TEXT NOT NULL,
  until   INTEGER NOT NULL,
  PRIMARY KEY (name_lc, tag)
);
