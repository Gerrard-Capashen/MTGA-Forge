using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using HasbroGo.Accounts.Models;
using HasbroGo.Social.Models;
using MTGA.Social;
using Newtonsoft.Json.Linq;
using Wizards.Arena.Promises;

#pragma warning disable CS0067 // events the real manager raises; the ones for chat and the social service come later

namespace ForgeBridge
{
    /// <summary>
    /// The client's <see cref="ISocialManager"/> backed by the coordinator's friends service (docs/online-identity-friends.md, stage 3).
    /// It never talks to the internet: it long-polls the local Forge server (<c>/social/state</c> on the art server's loopback port),
    /// mirrors that snapshot into the objects the Friends UI reads (friends with presence, invites, blocks) and raises the same events the
    /// Hasbro Go implementation did, and sends the player's actions (invite, accept, block, presence...) back as <c>/social/*</c> posts.
    /// Network work runs on background tasks; everything that touches the UI is queued to <see cref="Pump"/> (the main thread).
    /// Enabled with FORGEBRIDGE_SOCIAL=online (the launcher sets it whenever online play is on).
    /// </summary>
    public sealed class CoordinatorSocialManager : ISocialManager, IDisposable
    {
        public static bool Enabled => string.Equals(Environment.GetEnvironmentVariable("FORGEBRIDGE_SOCIAL"), "online", StringComparison.OrdinalIgnoreCase);

        private static readonly HttpClient http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(40) };
        private static readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();
        private static string Base => "http://127.0.0.1:" + CardArt.Port;

        /// <summary>Coroutine (started by LocalStartup): runs the queued work on the main thread.</summary>
        public static System.Collections.IEnumerator Pump()
        {
            while (true)
            {
                while (mainThread.TryDequeue(out var work))
                {
                    try { work(); } catch (Exception e) { Log.Error("social: " + e); }
                }
                yield return null;
            }
        }

        private SocialEntity _local;
        private ChatManager _chat;
        private bool _connected;
        private PresenceStatus _status = PresenceStatus.Available;
        private long _since;
        private volatile bool _stopping;
        private bool _first = true;
        private long _lastMessage;

        public bool IsSocialEnabled => true;
        public bool Connected => _connected;
        public bool DeclineIncomingFriendRequests { get; private set; }
        public List<SocialEntity> Friends { get; } = new List<SocialEntity>();
        public List<Invite> InvitesOutgoing { get; } = new List<Invite>();
        public List<Invite> InvitesIncoming { get; } = new List<Invite>();
        public List<Block> Blocks { get; } = new List<Block>();

        public SocialEntity LocalPlayer
        {
            get
            {
                if (_local == null)
                {
                    LocalProfile.Load();
                    _local = new SocialEntity(LocalProfile.DisplayName, "", LocalProfile.PlayerId);
                    _local.SetPresence(_status);
                }
                return _local;
            }
        }

        /// <summary>Made on first use: it needs the ChallengeDataProvider, which is in the Pantry only after the startup.</summary>
        public ChatManager ChatManager
        {
            get
            {
                if (_chat == null)
                {
                    try { _chat = new ChatManager(this); _chat.Init(); }
                    catch (Exception e) { Log.Error("social: ChatManager failed: " + e); }
                }
                return _chat;
            }
        }

        public event Action<bool> ConnectionChanged;
        public event Action<SocialEntity> FriendAdded;
        public event Action<SocialEntity> FriendRemoved;
        public event Action<SocialEntity> FriendPresenceChanged;
        public event Action FriendsChanged;
        public event Action<Invite> InviteOutgoingAdded;
        public event Action<Invite> InviteOutgoingRemoved;
        public event Action InvitesOutgoingChanged;
        public event Action<Invite> InviteIncomingAdded;
        public event Action<Invite> InviteIncomingRemoved;
        public event Action InvitesIncomingChanged;
        public event Action BlocksChanged;
        public event Action<SocialError> SocialException;
        public event Action<Exception> UnknownException;
        public event Action<bool> SocialEnabledChanged;
        public event Action<bool> ChatEnabledChanged;
        public event Action<MTGA.Social.DirectMessage> OnDirectMessage;
        public event Action<GameMessage> OnGameMessage;
        public event Action<PresenceStatus, PresenceStatus> LocalPresenceStatusChanged;

        public CoordinatorSocialManager()
        {
            Log.Info("social: coordinator friends, local server " + Base);
            Task.Run(PollLoop);
        }

        // ---------------------------------------------------------------------------------------------- reading the state

        private async Task PollLoop()
        {
            while (!_stopping)
            {
                try
                {
                    string text = await http.GetStringAsync(Base + "/social/state?since=" + _since + "&wait=25000").ConfigureAwait(false);
                    var o = JObject.Parse(text);
                    _since = (long)o["version"];
                    mainThread.Enqueue(() => Apply(o));
                }
                catch (Exception)
                {
                    if (!_stopping) await Task.Delay(2000).ConfigureAwait(false);     // the local server is not (yet) answering
                }
            }
        }

        private static PresenceStatus ToPresence(string s)
        {
            switch (s)
            {
                case "Available": return PresenceStatus.Available;
                case "Busy": return PresenceStatus.Busy;
                case "Away": return PresenceStatus.Away;
                default: return PresenceStatus.Offline;
            }
        }

        private static string FromPresence(PresenceStatus s)
        {
            switch (s)
            {
                case PresenceStatus.Available: return "Available";
                case PresenceStatus.Busy: return "Busy";
                case PresenceStatus.Away: return "Away";
                default: return "Offline";
            }
        }

        private static IEnumerable<JObject> Items(JObject o, string key) => ((o[key] as JArray) ?? new JArray()).OfType<JObject>();

        private static string Id(JObject o) => (string)o["playerId"];

        private static DateTime Created(JObject o) => o["created"] != null ? DateTimeOffset.FromUnixTimeMilliseconds((long)o["created"]).UtcDateTime : DateTime.UtcNow;

        /// <summary>Mirror one snapshot of the local server's state into the lists the UI reads and raise the events for what changed.</summary>
        private void Apply(JObject o)
        {
            // me
            var self = (JObject)o["self"];
            if (self != null && _local != null && (string)self["fullName"] != _local.FullName) _local = null;       // renamed
            PresenceStatus status = ToPresence((string)o["status"]);
            if (status != _status)
            {
                var old = _status;
                _status = status;
                LocalPlayer.SetPresence(status);
                LocalPresenceStatusChanged?.Invoke(old, status);
            }
            else LocalPlayer.SetPresence(status);

            // friends: keep the same object for the same player, report adds / removes / presence changes
            var oldFriends = Friends.ToDictionary(f => f.PlayerId);
            var friends = new List<SocialEntity>();
            var presenceChanged = new List<SocialEntity>();
            foreach (var j in Items(o, "friends"))
            {
                string id = Id(j);
                PresenceStatus p = ToPresence((string)j["status"]);
                if (oldFriends.TryGetValue(id, out var e))
                {
                    if (e.Status != p) { e.SetPresence(p); presenceChanged.Add(e); }
                }
                else
                {
                    e = new SocialEntity((string)j["fullName"], id, id);
                    e.SetPresence(p);
                }
                friends.Add(e);
            }
            var removed = oldFriends.Values.Where(f => !friends.Contains(f)).ToList();
            var added = friends.Where(f => !oldFriends.ContainsKey(f.PlayerId)).ToList();
            Friends.Clear();
            Friends.AddRange(friends);
            foreach (var f in removed) FriendRemoved?.Invoke(f);
            foreach (var f in added) FriendAdded?.Invoke(f);
            foreach (var f in presenceChanged) FriendPresenceChanged?.Invoke(f);
            if (_first || removed.Count + added.Count + presenceChanged.Count > 0) FriendsChanged?.Invoke();

            // invites
            ApplyInvites(o, "incoming", InvitesIncoming, j => new Invite(new IncomingFriendInvite(Id(j), Id(j), Created(j), Id(j), new DisplayName((string)j["fullName"]))),
                i => InviteIncomingAdded?.Invoke(i), i => InviteIncomingRemoved?.Invoke(i), () => InvitesIncomingChanged?.Invoke());
            ApplyInvites(o, "outgoing", InvitesOutgoing, j => new Invite(new OutgoingFriendInvite(Id(j), Created(j), Id(j), Id(j), new DisplayName((string)j["fullName"]), null)),
                i => InviteOutgoingAdded?.Invoke(i), i => InviteOutgoingRemoved?.Invoke(i), () => InvitesOutgoingChanged?.Invoke());

            // blocks
            var blockIds = Items(o, "blocks").Select(Id).ToList();
            bool blocksChanged = _first || blockIds.Count != Blocks.Count || blockIds.Any(id => Blocks.All(b => b.BlockedPlayer.PlayerId != id));
            if (blocksChanged)
            {
                Blocks.Clear();
                foreach (var j in Items(o, "blocks"))
                {
                    var e = new SocialEntity((string)j["fullName"], Id(j), Id(j));
                    e.SetAsBlocked(true);
                    Blocks.Add(new Block(e));
                }
                BlocksChanged?.Invoke();
            }

            // friend messages (the client's challenge invites and updates travel as opaque text between two clients)
            foreach (var j in Items(o, "messages"))
            {
                long seq = (long)j["seq"];
                if (seq <= _lastMessage) continue;
                _lastMessage = seq;
                if (_first) continue;
                string from = (string)j["from"], payload = (string)j["payload"] ?? "";
                if (payload.StartsWith(DmPrefix, StringComparison.Ordinal))
                {
                    var friend = Friends.Find(f => f.PlayerId == from);
                    OnDirectMessage?.Invoke(new MTGA.Social.DirectMessage { SenderPlayerId = from, SenderDisplayName = friend?.FullName ?? "", Message = payload.Substring(DmPrefix.Length) });
                }
                else OnGameMessage?.Invoke(new GameMessage(from, "", payload, null));
            }

            bool connected = (bool)o["connected"];
            if (connected != _connected || _first)
            {
                _connected = connected;
                ConnectionChanged?.Invoke(connected);
            }
            _first = false;
        }

        private void ApplyInvites(JObject o, string key, List<Invite> list, Func<JObject, Invite> make, Action<Invite> add, Action<Invite> remove, Action changed)
        {
            var now = Items(o, key).ToList();
            var gone = list.Where(i => now.All(j => Id(j) != i.PotentialFriend.PlayerId)).ToList();
            var fresh = now.Where(j => list.All(i => i.PotentialFriend.PlayerId != Id(j))).ToList();
            foreach (var i in gone) { list.Remove(i); remove(i); }
            foreach (var j in fresh) { var i = make(j); list.Add(i); add(i); }
            if (_first || gone.Count + fresh.Count > 0) changed();
        }

        // ---------------------------------------------------------------------------------------------- actions

        /// <summary>POST /social/&lt;action&gt; on a background task; the coordinator's refusal (already friends, no such player...) is logged like the SDK did.</summary>
        private static void Post(string action, JObject body, Action<JObject> done = null)
        {
            Task.Run(async () =>
            {
                JObject answer;
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, Base + "/social/" + action) { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
                    req.Headers.Add("X-Forge-Social", "1");
                    var resp = await http.SendAsync(req).ConfigureAwait(false);
                    answer = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                }
                catch (Exception e)
                {
                    answer = new JObject { ["error"] = e.Message };
                }
                mainThread.Enqueue(() =>
                {
                    if (answer["error"] != null) Log.Info("social: " + action + " refused: " + (string)answer["error"]);
                    done?.Invoke(answer);
                });
            });
        }

        private static JObject Who(string playerId) => new JObject { ["playerId"] = playerId };

        public void SocialShutdown() { _stopping = true; }
        public void Connect() { }
        public void Connect(PresenceStatus connectStatus) { }
        public void Disconnect(bool shutdownStyle) { }
        public void Reconnect() { }
        /// <summary>Chat messages travel as friend messages (the coordinator relays them to a friend who is online, 16 KB, 60 a minute), marked by this prefix.</summary>
        private const string DmPrefix = "\u0001dm\u0001";

        public void SendDirectMessage(string friendFullName, string message)
        {
            var friend = Friends.Find(f => f.FullName == friendFullName);
            if (friend == null) { Log.Info("social: direct message to " + friendFullName + " dropped: not a friend"); return; }
            if (friend.Status == PresenceStatus.Offline) Log.Info("social: " + friendFullName + " is offline, the message will not arrive");
            Post("game", new JObject { ["to"] = friend.PlayerId, ["payload"] = DmPrefix + message });
        }
        public void SendGameMessage(string playerId, string message)
        {
            Post("game", new JObject { ["to"] = playerId, ["payload"] = message });
        }

        public void SetUserPresenceStatus(PresenceStatus presenceStatus)
        {
            Post("status", new JObject { ["status"] = FromPresence(presenceStatus) });
        }

        public void RefreshFriendsData() { }
        public void AcceptFriendInviteIncoming(SocialEntity potentialFriend) { Post("accept", Who(potentialFriend.PlayerId)); }
        public void AcceptFriendInviteIncoming(Invite invite) { Post("accept", Who(invite.PotentialFriend.PlayerId)); }
        public void DeclineFriendInviteIncoming(Invite invite) { Post("decline", Who(invite.PotentialFriend.PlayerId)); }
        public void ToggleAutoDeclineFriendInviteIncoming(bool shouldDecline) { DeclineIncomingFriendRequests = shouldDecline; }
        public void RevokeFriendInviteOutgoing(Invite invite) { Post("revoke", Who(invite.PotentialFriend.PlayerId)); }
        public void RemoveFriend(SocialEntity friend) { Post("remove", Who(friend.PlayerId)); }

        public void SubmitFriendInviteOutgoing(string potentialFriend)
        {
            if (string.IsNullOrWhiteSpace(potentialFriend) || potentialFriend.Contains("@") || !potentialFriend.Contains("#")) return;    // Name#12345 only
            var blocked = Blocks.Find(b => b.BlockedPlayer.FullName == potentialFriend);
            if (blocked != null) RemoveBlock(blocked);
            Post("invite", new JObject { ["full"] = potentialFriend.Trim() });
        }

        public bool CheckIfAlreadyFriends(string playerId) { return Friends.Any(f => f.PlayerId == playerId); }
        public bool CheckIfAlreadyFriendInvited(string playerId) { return InvitesOutgoing.Any(i => i.PotentialFriend.PlayerId == playerId); }
        public void BlockByDisplayName(string fullDisplayName) { Post("block", new JObject { ["full"] = fullDisplayName }); }
        public void AddBlock(SocialEntity userToBeBlocked) { Post("block", Who(userToBeBlocked.PlayerId)); }
        public void RemoveBlock(Block block) { Post("unblock", Who(block.BlockedPlayer.PlayerId)); }
        public void ShowEnteringQueueWithOutgoingChallengeMessage(Action onOkAction) { onOkAction?.Invoke(); }

        public Promise<string> GetPlayerIdFromFullPlayerName(string fullPlayerName) { return Resolve(fullPlayerName); }
        public Promise<string> GetAccountIdFromFullPlayerName(string fullPlayerName) { return Resolve(fullPlayerName); }

        /// <summary>The player id (= account id) behind "Name#12345": a friend we know, else the coordinator is asked.</summary>
        private Promise<string> Resolve(string fullName)
        {
            var friend = Friends.Find(f => f.FullName == fullName);
            if (friend != null) return new SimplePromise<string>(friend.PlayerId);
            var promise = new OpenPromise<string>();
            Post("resolve", new JObject { ["full"] = fullName }, answer =>
            {
                if (answer["playerId"] != null) promise.SetResult((string)answer["playerId"]);
                else promise.SetResult(null);
            });
            return promise;
        }

        public void ForwardNotificationAlert(SocialMessage socialMessage) { }
        public void BIMessage_ChatWindowOpened() { }
        public void BIMessage_DirectMessageRecieved(Conversation conversation) { }
        public void BIMessage_DirectMessageSent(Conversation conversation) { }
        public void BIMessage_FriendsListOpened() { }
        public void BIMessage_NotificationBubbleShown(SocialMessage notification) { }
        public void Dispose() { _stopping = true; }
    }
}
