-- Match results: each seat reports its own outcome (match_reports); once both are in, `matches` holds the verdict.
-- 'verified' = the two reports are complementary (win/loss, loss/win, draw/draw, same pool), 'disputed' = they are not.
-- With one report only (the other seat dropped or never reported): 'forfeit' = the reporter claims the win and the relay room saw the
-- opponent leave for good after a minimum match length, 'conceded' = the reporter admits the loss (the winner is not known),
-- 'void' = nothing usable after PENDING_TTL. Rank may use verified only; forfeit and conceded are weaker evidence (see results.ts).
CREATE TABLE match_reports (
  match_id  TEXT NOT NULL,
  role      TEXT NOT NULL,            -- 'host' | 'guest'
  player_id TEXT NOT NULL,
  outcome   TEXT NOT NULL,            -- 'win' | 'loss' | 'draw'
  pool      TEXT NOT NULL,            -- the queue / event name the match was paired in
  created   INTEGER NOT NULL,
  PRIMARY KEY (match_id, role)
);
CREATE INDEX match_reports_player ON match_reports (player_id, created);

CREATE TABLE matches (
  match_id TEXT PRIMARY KEY,
  pool     TEXT NOT NULL,
  host_id  TEXT,                      -- NULL for a seat that never reported
  guest_id TEXT,
  winner   TEXT,                      -- 'host' | 'guest' | 'draw' (NULL when disputed, conceded or void)
  status   TEXT NOT NULL,             -- 'verified' | 'disputed' | 'forfeit' | 'conceded' | 'void'
  resolved INTEGER NOT NULL
);
CREATE INDEX matches_host ON matches (host_id, resolved);
CREATE INDEX matches_guest ON matches (guest_id, resolved);

-- What the relay room itself saw (the one witness a client cannot forge): when both seats were in, and who left first.
CREATE TABLE match_rooms (
  match_id  TEXT PRIMARY KEY,
  started   INTEGER NOT NULL,         -- both seats connected
  left_role TEXT,                     -- first seat to drop
  left_at   INTEGER,
  rejoined  INTEGER                   -- set when that seat came back (reserved for reconnect; the room does not allow it yet)
);
