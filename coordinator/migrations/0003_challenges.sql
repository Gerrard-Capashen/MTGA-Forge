-- Who is in which open challenge (the state itself lives in the Challenge Durable Object): "my challenges" for a reconnect.
CREATE TABLE challenge_players (
  challenge_id TEXT NOT NULL,
  player_id    TEXT NOT NULL,
  created      INTEGER NOT NULL,
  PRIMARY KEY (challenge_id, player_id)
);
CREATE INDEX challenge_players_player ON challenge_players (player_id);
