-- Friends: one row per pair, a < b (the pair is the key whoever asked), status 'pending' (requester asked) or 'accepted'.
CREATE TABLE friendships (
  a         TEXT NOT NULL,
  b         TEXT NOT NULL,
  requester TEXT NOT NULL,
  status    TEXT NOT NULL,
  created   INTEGER NOT NULL,
  PRIMARY KEY (a, b),
  CHECK (a < b)
);
CREATE INDEX friendships_b ON friendships (b);

-- Blocks: the blocker never sees the blocked player's invites and the blocked player cannot find the blocker by name.
CREATE TABLE blocks (
  blocker TEXT NOT NULL,
  blocked TEXT NOT NULL,
  created INTEGER NOT NULL,
  PRIMARY KEY (blocker, blocked)
);
CREATE INDEX blocks_blocked ON blocks (blocked);
