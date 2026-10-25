-- Rank (src/rank.ts). Everything is keyed by season so a reset later is a new season number.
-- pool = the ladder: 'constructed' for every RankedPlay_<Format> queue (the client shows ONE constructed rank), 'limited' later.

-- Who sat in a ranked match, written by the queue when it pairs two signed-in players: the only record of the account behind a seat
-- that never reports (a forfeit's leaver) and the only proof that a match was paired in a ranked queue (a report's pool is the client's word).
CREATE TABLE match_seats (
  match_id  TEXT NOT NULL,
  role      TEXT NOT NULL,            -- 'host' | 'guest'
  player_id TEXT NOT NULL,
  queue     TEXT NOT NULL,            -- RankedPlay_Standard ...
  pool      TEXT NOT NULL,            -- constructed
  created   INTEGER NOT NULL,
  PRIMARY KEY (match_id, role)
);

-- class: 0 Bronze .. 4 Diamond, 5 Mythic. level: 4 (lowest) .. 1 within a class (Mythic: 1). step: filled pips of the level.
-- rating: an Elo number every player has from the first ranked match; it orders the Mythic leaderboard (and allows pairing by rating later).
-- version: bumped by every change; a rating event is written in the same batch only when the version it read is still current.
CREATE TABLE ratings (
  player_id TEXT NOT NULL,
  pool      TEXT NOT NULL,
  season    INTEGER NOT NULL,
  class     INTEGER NOT NULL DEFAULT 0,
  level     INTEGER NOT NULL DEFAULT 4,
  step      INTEGER NOT NULL DEFAULT 0,
  wins      INTEGER NOT NULL DEFAULT 0,
  losses    INTEGER NOT NULL DEFAULT 0,
  draws     INTEGER NOT NULL DEFAULT 0,
  rating    REAL NOT NULL DEFAULT 1500,
  mythic_at INTEGER,                  -- when the player reached Mythic (ties on the leaderboard: earlier first)
  version   INTEGER NOT NULL DEFAULT 0,
  last_match TEXT,                    -- the match of the last change (with version: proof that THIS batch's update ran)
  updated   INTEGER NOT NULL,
  PRIMARY KEY (player_id, pool, season)
);
CREATE INDEX ratings_board ON ratings (pool, season, class, rating);

-- One row per (match, player): applying a match is idempotent. reason: 'applied' | 'short' (match under the minimum length) | 'pair-cap'
-- (too many results against the same opponent today); only 'applied' moves pips or the rating.
CREATE TABLE rating_events (
  match_id    TEXT NOT NULL,
  player_id   TEXT NOT NULL,
  opponent_id TEXT NOT NULL,
  pool        TEXT NOT NULL,
  season      INTEGER NOT NULL,
  queue       TEXT NOT NULL,
  result      TEXT NOT NULL,          -- 'win' | 'loss' | 'draw'
  status      TEXT NOT NULL,          -- the match verdict that applied it: verified | forfeit | conceded
  reason      TEXT NOT NULL,
  old_class   INTEGER NOT NULL, old_level INTEGER NOT NULL, old_step INTEGER NOT NULL,
  new_class   INTEGER NOT NULL, new_level INTEGER NOT NULL, new_step INTEGER NOT NULL,
  protected   INTEGER NOT NULL DEFAULT 0,   -- a loss that cost nothing (Bronze, or the floor of a class)
  old_rating  REAL NOT NULL,
  new_rating  REAL NOT NULL,
  seq         INTEGER NOT NULL,       -- the ratings row's version after this event (NOT NULL is what makes a stale write fail)
  created     INTEGER NOT NULL,
  PRIMARY KEY (match_id, player_id)
);
CREATE INDEX rating_events_pair ON rating_events (player_id, opponent_id, created);
