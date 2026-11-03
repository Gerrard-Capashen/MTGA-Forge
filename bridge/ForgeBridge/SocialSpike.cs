using System;
using System.Collections.Generic;
using HasbroGo.Social.Models;
using MTGA.Social;
using Wizards.Arena.Promises;

#pragma warning disable CS0067 // events the real manager raises; the spike raises only the ones it needs

namespace ForgeBridge
{
    /// <summary>
    /// Stage 2 spike of docs/online-identity-friends.md: an <see cref="ISocialManager"/> with a FAKE friend list, to see which parts of the
    /// client's Friends / chat / challenge UI work without the Hasbro Go SDK and which calls they make (everything is logged as
    /// "social: ..."). Enabled with FORGEBRIDGE_SOCIAL=spike (the launcher's -SocialSpike also switches the server's social kill switches off).
    /// The real implementation (friends from the coordinator) replaces the fake data and keeps this shape.
    /// </summary>
    public sealed class SpikeSocialManager : ISocialManager, IDisposable
    {
        public static bool Enabled => string.Equals(Environment.GetEnvironmentVariable("FORGEBRIDGE_SOCIAL"), "spike", StringComparison.OrdinalIgnoreCase);

        private SocialEntity _local;
        private ChatManager _chat;
        private bool _connected;

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
                    _local.SetPresence(PresenceStatus.Available);
                }
                return _local;
            }
        }

        /// <summary>Made on first use: the real one needs the ChallengeDataProvider, which is in the Pantry only after the startup.</summary>
        public ChatManager ChatManager
        {
            get
            {
                if (_chat == null)
                {
                    try { _chat = new ChatManager(this); _chat.Init(); Log.Info("social: ChatManager created"); }
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

        public SpikeSocialManager()
        {
            AddFriend("Alice#11111", "acc-alice", "persona-alice", PresenceStatus.Available);
            AddFriend("Bob#22222", "acc-bob", "persona-bob", PresenceStatus.Busy);
            AddFriend("Carol#33333", "acc-carol", "persona-carol", PresenceStatus.Offline);
            Log.Info("social: SpikeSocialManager created with " + Friends.Count + " fake friends");
        }

        private void AddFriend(string fullName, string socialId, string playerId, PresenceStatus status)
        {
            var f = new SocialEntity(fullName, socialId, playerId);
            f.SetPresence(status);
            Friends.Add(f);
        }

        private static void Trace(string what) { Log.Info("social: " + what); }

        public void SocialShutdown() { Trace("SocialShutdown"); }
        public void Connect() { Connect(PresenceStatus.Available); }

        public void Connect(PresenceStatus connectStatus)
        {
            Trace("Connect " + connectStatus);
            _connected = true;
            LocalPlayer.SetPresence(connectStatus);
            ConnectionChanged?.Invoke(true);
            FriendsChanged?.Invoke();
        }

        public void Disconnect(bool shutdownStyle) { Trace("Disconnect " + shutdownStyle); _connected = false; ConnectionChanged?.Invoke(false); }
        public void Reconnect() { Trace("Reconnect"); Connect(); }
        public void SendDirectMessage(string friendFullName, string message) { Trace("SendDirectMessage to " + friendFullName + ": " + message); }
        public void SendGameMessage(string playerId, string message) { Trace("SendGameMessage to " + playerId + " (" + (message ?? "").Length + " chars)"); }

        public void SetUserPresenceStatus(PresenceStatus presenceStatus)
        {
            Trace("SetUserPresenceStatus " + presenceStatus);
            var old = LocalPlayer.Status;
            LocalPlayer.SetPresence(presenceStatus);
            LocalPresenceStatusChanged?.Invoke(old, presenceStatus);
        }

        public void RefreshFriendsData() { Trace("RefreshFriendsData"); FriendsChanged?.Invoke(); }
        public void AcceptFriendInviteIncoming(SocialEntity potentialFriend) { Trace("AcceptFriendInviteIncoming " + potentialFriend?.FullName); }
        public void AcceptFriendInviteIncoming(Invite invite) { Trace("AcceptFriendInviteIncoming (invite)"); }
        public void DeclineFriendInviteIncoming(Invite invite) { Trace("DeclineFriendInviteIncoming"); }
        public void ToggleAutoDeclineFriendInviteIncoming(bool shouldDecline) { Trace("ToggleAutoDecline " + shouldDecline); DeclineIncomingFriendRequests = shouldDecline; }
        public void SubmitFriendInviteOutgoing(string potentialFriend) { Trace("SubmitFriendInviteOutgoing " + potentialFriend); }
        public void RevokeFriendInviteOutgoing(Invite invite) { Trace("RevokeFriendInviteOutgoing"); }

        public void RemoveFriend(SocialEntity friend)
        {
            Trace("RemoveFriend " + friend?.FullName);
            if (friend != null && Friends.Remove(friend)) { FriendRemoved?.Invoke(friend); FriendsChanged?.Invoke(); }
        }

        public bool CheckIfAlreadyFriends(string playerId) { return Friends.Exists(f => f.PlayerId == playerId); }
        public bool CheckIfAlreadyFriendInvited(string playerId) { return false; }
        public void BlockByDisplayName(string fullDisplayName) { Trace("BlockByDisplayName " + fullDisplayName); }
        public void AddBlock(SocialEntity userToBeBlocked) { Trace("AddBlock " + userToBeBlocked?.FullName); }
        public void RemoveBlock(Block block) { Trace("RemoveBlock"); }
        public void ShowEnteringQueueWithOutgoingChallengeMessage(Action onOkAction) { Trace("ShowEnteringQueueWithOutgoingChallengeMessage"); onOkAction?.Invoke(); }

        public Promise<string> GetPlayerIdFromFullPlayerName(string fullPlayerName)
        {
            var f = Friends.Find(x => x.FullName == fullPlayerName);
            Trace("GetPlayerIdFromFullPlayerName " + fullPlayerName + " -> " + (f?.PlayerId ?? "(unknown)"));
            return f != null ? new SimplePromise<string>(f.PlayerId) : (Promise<string>)new OpenPromise<string>();
        }

        public Promise<string> GetAccountIdFromFullPlayerName(string fullPlayerName)
        {
            var f = Friends.Find(x => x.FullName == fullPlayerName);
            Trace("GetAccountIdFromFullPlayerName " + fullPlayerName + " -> " + (f?.SocialId ?? "(unknown)"));
            return f != null ? new SimplePromise<string>(f.SocialId) : (Promise<string>)new OpenPromise<string>();
        }

        public void ForwardNotificationAlert(SocialMessage socialMessage) { Trace("ForwardNotificationAlert"); }
        public void BIMessage_ChatWindowOpened() { Trace("chat window opened"); }
        public void BIMessage_DirectMessageRecieved(Conversation conversation) { }
        public void BIMessage_DirectMessageSent(Conversation conversation) { }
        public void BIMessage_FriendsListOpened() { Trace("friends list opened"); }
        public void BIMessage_NotificationBubbleShown(SocialMessage notification) { }
        public void Dispose() { }
    }
}
