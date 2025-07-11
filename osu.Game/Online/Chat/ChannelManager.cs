﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics.Containers;
using osu.Framework.Logging;
using osu.Framework.Threading;
using osu.Game.Database;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Overlays.Chat.Listing;

namespace osu.Game.Online.Chat
{
    /// <summary>
    /// Manages everything channel related
    /// </summary>
    public partial class ChannelManager : CompositeComponent, IChannelPostTarget
    {
        /// <summary>
        /// The channels the player joins on startup
        /// </summary>
        private readonly string[] defaultChannels =
        {
            @"#lazer",
            @"#osu",
            @"#lobby"
        };

        private readonly BindableList<Channel> availableChannels = new BindableList<Channel>();
        private readonly BindableList<Channel> joinedChannels = new BindableList<Channel>();

        /// <summary>
        /// Keeps a stack of recently closed channels
        /// </summary>
        private readonly List<ClosedChannel> closedChannels = new List<ClosedChannel>();

        // For efficiency purposes, this constant bounds the number of closed channels we store.
        // This number is somewhat arbitrary; future developers are free to modify it.
        // Must be a positive number.
        private const int closed_channels_max_size = 50;

        /// <summary>
        /// The currently opened channel
        /// </summary>
        public Bindable<Channel> CurrentChannel { get; } = new Bindable<Channel>();

        /// <summary>
        /// The Channels the player has joined
        /// </summary>
        public IBindableList<Channel> JoinedChannels => joinedChannels;

        /// <summary>
        /// The channels available for the player to join
        /// </summary>
        public IBindableList<Channel> AvailableChannels => availableChannels;

        private readonly IAPIProvider api;

        [Resolved]
        private UserLookupCache users { get; set; }

        private readonly IBindable<APIState> apiState = new Bindable<APIState>();
        private ScheduledDelegate scheduledAck;

        private IChatClient chatClient = null!;
        private long? lastSilenceMessageId;
        private uint? lastSilenceId;

        public ChannelManager(IAPIProvider api)
        {
            this.api = api;

            CurrentChannel.ValueChanged += currentChannelChanged;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            chatClient = api.GetChatClient();
            chatClient.ChannelJoined += ch => Schedule(() => joinChannel(ch));
            chatClient.ChannelParted += ch => Schedule(() => leaveChannel(getChannel(ch), false));
            chatClient.NewMessages += msgs => Schedule(() => addMessages(msgs));
            chatClient.PresenceReceived += () => Schedule(initializeChannels);
            chatClient.RequestPresence();

            apiState.BindTo(api.State);
            apiState.BindValueChanged(_ => SendAck(), true);
        }

        /// <summary>
        /// Opens a channel or switches to the channel if already opened.
        /// </summary>
        /// <exception cref="ChannelNotFoundException">If the name of the specifed channel was not found this exception will be thrown.</exception>
        /// <param name="name"></param>
        public void OpenChannel(string name)
        {
            ArgumentNullException.ThrowIfNull(name);

            CurrentChannel.Value = AvailableChannels.FirstOrDefault(c => c.Name == name) ?? throw new ChannelNotFoundException(name);
        }

        /// <summary>
        /// Opens a new private channel.
        /// </summary>
        /// <param name="user">The user the private channel is opened with.</param>
        public void OpenPrivateChannel(APIUser user)
        {
            ArgumentNullException.ThrowIfNull(user);

            if (user.Id == api.LocalUser.Value.Id)
                return;

            CurrentChannel.Value = JoinedChannels.FirstOrDefault(c => c.Type == ChannelType.PM && c.Users.Count == 1 && c.Users.Any(u => u.Id == user.Id))
                                   ?? JoinChannel(new Channel(user));
        }

        private void currentChannelChanged(ValueChangedEvent<Channel> channel)
        {
            bool isSelectorChannel = channel.NewValue is ChannelListing.ChannelListingChannel;

            if (!isSelectorChannel)
                JoinChannel(channel.NewValue);

            Logger.Log($"Current channel changed to {channel.NewValue}");
        }

        /// <summary>
        /// Ensure we run post actions in sequence, once at a time.
        /// </summary>
        private readonly Queue<Action> postQueue = new Queue<Action>();

        /// <summary>
        /// Posts a message to the currently opened channel.
        /// </summary>
        /// <param name="text">The message text that is going to be posted</param>
        /// <param name="isAction">Is true if the message is an action, e.g.: user is currently eating </param>
        /// <param name="target">An optional target channel. If null, <see cref="CurrentChannel"/> will be used.</param>
        public void PostMessage(string text, bool isAction = false, Channel target = null)
        {
            target ??= CurrentChannel.Value;

            if (target == null)
                return;

            void dequeueAndRun()
            {
                if (postQueue.Count > 0)
                    postQueue.Dequeue().Invoke();
            }

            postQueue.Enqueue(() =>
            {
                if (!api.IsLoggedIn)
                {
                    target.AddNewMessages(new ErrorMessage("Please sign in to participate in chat!"));
                    return;
                }

                var message = new LocalEchoMessage
                {
                    Sender = api.LocalUser.Value,
                    Timestamp = DateTimeOffset.Now,
                    ChannelId = target.Id,
                    IsAction = isAction,
                    Content = text,
                    Uuid = Guid.NewGuid().ToString()
                };

                target.AddLocalEcho(message);

                // if this is a PM and the first message, we need to do a special request to create the PM channel
                if (target.Type == ChannelType.PM && target.Id == 0)
                {
                    var createNewPrivateMessageRequest = new CreateNewPrivateMessageRequest(target.Users.First(), message);

                    createNewPrivateMessageRequest.Success += _ => dequeueAndRun();
                    createNewPrivateMessageRequest.Failure += exception =>
                    {
                        handlePostException(exception);
                        target.ReplaceMessage(message, null);
                        dequeueAndRun();
                    };

                    api.Queue(createNewPrivateMessageRequest);
                    return;
                }

                var req = new PostMessageRequest(message);

                req.Success += m => dequeueAndRun();
                req.Failure += exception =>
                {
                    handlePostException(exception);
                    target.ReplaceMessage(message, null);
                    dequeueAndRun();
                };

                api.Queue(req);
            });

            // always run if the queue is empty
            if (postQueue.Count == 1)
                dequeueAndRun();
        }

        private static void handlePostException(Exception exception)
        {
            if (exception is APIException apiException)
                Logger.Log(apiException.Message, level: LogLevel.Important);
            else
                Logger.Error(exception, "Posting message failed.");
        }

        /// <summary>
        /// Posts a command locally. Commands like /help will result in a help message written in the current channel.
        /// </summary>
        /// <param name="text">the text containing the command identifier and command parameters.</param>
        /// <param name="target">An optional target channel. If null, <see cref="CurrentChannel"/> will be used.</param>
        public void PostCommand(string text, Channel target = null)
        {
            target ??= CurrentChannel.Value;

            if (target == null)
                return;

            string[] parameters = text.Split(' ', 2);
            string command = parameters[0];
            string content = parameters.Length == 2 ? parameters[1] : string.Empty;

            switch (command.ToLowerInvariant())
            {
                case "np":
                    AddInternal(new NowPlayingCommand(target));
                    break;

                case "me":
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        target.AddNewMessages(new ErrorMessage("Usage: /me [action]"));
                        break;
                    }

                    PostMessage(content, true, target);
                    break;

                case "join":
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        target.AddNewMessages(new ErrorMessage("Usage: /join [channel]"));
                        break;
                    }

                    var channel = availableChannels.FirstOrDefault(c => c.Name == content || c.Name == $"#{content}");

                    if (channel == null)
                    {
                        target.AddNewMessages(new ErrorMessage($"Channel '{content}' not found."));
                        break;
                    }

                    JoinChannel(channel);
                    break;

                case "chat":
                case "msg":
                case "query":
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        target.AddNewMessages(new ErrorMessage($"Usage: /{command} [user]"));
                        break;
                    }

                    // Check if the user has joined the requested channel already.
                    // This uses the channel name for comparison as the PM user's username is unavailable after a restart.
                    var privateChannel = JoinedChannels.FirstOrDefault(c => c.Type == ChannelType.PM && c.Users.Count == 1 && c.Name.Equals(content, StringComparison.OrdinalIgnoreCase));

                    if (privateChannel != null)
                    {
                        CurrentChannel.Value = privateChannel;
                        break;
                    }

                    var request = new GetUserRequest(content);
                    request.Success += OpenPrivateChannel;
                    request.Failure += e => target.AddNewMessages(
                        new ErrorMessage(e.InnerException?.Message == @"NotFound" ? $"User '{content}' was not found." : $"Could not fetch user '{content}'."));

                    api.Queue(request);
                    break;

                case "help":
                    target.AddNewMessages(new InfoMessage("Supported commands: /help, /me [action], /join [channel], /chat [user], /np"));
                    break;

                default:
                    target.AddNewMessages(new ErrorMessage($@"""/{command}"" is not supported! For a list of supported commands see /help"));
                    break;
            }
        }

        private void addMessages(List<Message> messages)
        {
            var channels = JoinedChannels.ToList();

            foreach (var group in messages.GroupBy(m => m.ChannelId))
                channels.Find(c => c.Id == group.Key)?.AddNewMessages(group.ToArray());

            lastSilenceMessageId ??= messages.LastOrDefault()?.Id;
        }

        private void initializeChannels()
        {
            // This request is self-retrying until it succeeds.
            // To avoid requests piling up when not logged in (ie. API is unavailable) exit early.
            if (!api.IsLoggedIn)
                return;

            var req = new ListChannelsRequest();

            bool joinDefaults = JoinedChannels.Count == 0;

            req.Success += channels =>
            {
                foreach (var channel in channels)
                {
                    var ch = getChannel(channel, addToAvailable: true);

                    // join any channels classified as "defaults"
                    if (joinDefaults && defaultChannels.Any(c => c.Equals(channel.Name, StringComparison.OrdinalIgnoreCase)))
                        joinChannel(ch);
                }
            };

            req.Failure += error =>
            {
                Logger.Error(error, "Fetching channel list failed");
                Scheduler.AddDelayed(initializeChannels, 60000);
            };

            api.Queue(req);
        }

        /// <summary>
        /// Fetches inital messages of a channel
        ///
        /// TODO: remove this when the API supports returning initial fetch messages for more than one channel by specifying the last message id per channel instead of one last message id globally.
        /// right now it caps out at 50 messages and therefore only returns one channel's worth of content.
        /// </summary>
        /// <param name="channel">The channel </param>
        private void fetchInitialMessages(Channel channel)
        {
            if (channel.Id <= 0 || channel.MessagesLoaded) return;

            var fetchInitialMsgReq = new GetMessagesRequest(channel);
            fetchInitialMsgReq.Success += messages =>
            {
                addMessages(messages);
                channel.MessagesLoaded = true; // this will mark the channel as having received messages even if there were none.
            };

            api.Queue(fetchInitialMsgReq);
        }

        /// <summary>
        /// Sends an acknowledgement request to the API.
        /// This marks the user as online to receive messages from public channels, while also returning a list of silenced users.
        /// It needs to be called at least once every 10 minutes to remain visibly marked as online.
        /// </summary>
        public void SendAck()
        {
            if (apiState.Value != APIState.Online)
                return;

            var req = new ChatAckRequest
            {
                SinceMessageId = lastSilenceMessageId,
                SinceSilenceId = lastSilenceId
            };

            req.Failure += _ => scheduleNextRequest();
            req.Success += ack =>
            {
                foreach (var silence in ack.Silences)
                {
                    foreach (var channel in JoinedChannels)
                        channel.RemoveMessagesFromUser(silence.UserId);
                    lastSilenceId = Math.Max(lastSilenceId ?? 0, silence.Id);
                }

                scheduleNextRequest();
            };

            api.Queue(req);

            void scheduleNextRequest()
            {
                scheduledAck?.Cancel();
                scheduledAck = Scheduler.AddDelayed(SendAck, 60000);
            }
        }

        /// <summary>
        /// Find an existing channel instance for the provided channel. Lookup is performed based on ID.
        /// The provided channel may be used if an existing instance is not found.
        /// </summary>
        /// <param name="lookup">A candidate channel to be used for lookup or permanently on lookup failure.</param>
        /// <param name="addToAvailable">Whether the channel should be added to <see cref="AvailableChannels"/> if not already.</param>
        /// <param name="addToJoined">Whether the channel should be added to <see cref="JoinedChannels"/> if not already.</param>
        /// <returns>The found channel.</returns>
        private Channel getChannel(Channel lookup, bool addToAvailable = false, bool addToJoined = false)
        {
            Channel found = null;

            bool lookupCondition(Channel ch)
            {
                if (ch.Id > 0 && lookup.Id > 0)
                    return ch.Id == lookup.Id;

                return ch.Name == lookup.Name;
            }

            var available = AvailableChannels.FirstOrDefault(lookupCondition);
            if (available != null)
                found = available;

            var joined = JoinedChannels.FirstOrDefault(lookupCondition);
            if (found == null && joined != null)
                found = joined;

            if (found == null)
            {
                found = lookup;

                // if we're using a channel object from the server, we want to remove ourselves from the users list.
                // this is because we check the first user in the channel to display a name/icon on tabs for now.
                var foundSelf = found.Users.FirstOrDefault(u => u.Id == api.LocalUser.Value.Id);
                if (foundSelf != null)
                    found.Users.Remove(foundSelf);
            }
            else
            {
                found.Id = lookup.Id;
                found.Name = lookup.Name;
                found.LastMessageId = Math.Max(found.LastMessageId ?? 0, lookup.LastMessageId ?? 0);
            }

            if (joined == null && addToJoined) joinedChannels.Add(found);
            if (available == null && addToAvailable) availableChannels.Add(found);

            return found;
        }

        /// <summary>
        /// Joins a channel if it has not already been joined. Must be called from the update thread.
        /// </summary>
        /// <param name="channel">The channel to join.</param>
        /// <returns>The joined channel. Note that this may not match the parameter channel as it is a backed object.</returns>
        public Channel JoinChannel(Channel channel) => joinChannel(channel, true);

        private Channel joinChannel(Channel channel, bool fetchInitialMessages = false)
        {
            if (channel == null) return null;

            channel = getChannel(channel, addToJoined: true);

            // ensure we are joined to the channel
            if (!channel.Joined.Value)
            {
                channel.Joined.Value = true;

                switch (channel.Type)
                {
                    case ChannelType.Multiplayer:
                        // join is implicit. happens when you join a multiplayer game.
                        // this will probably change in the future.
                        joinChannel(channel, fetchInitialMessages);
                        return channel;

                    case ChannelType.PM:
                        Logger.Log($"Attempting to join PM channel {channel}");

                        var createRequest = new CreateChannelRequest(channel);
                        createRequest.Failure += e =>
                        {
                            Logger.Log($"Failed to join PM channel {channel} ({e.Message})");
                        };
                        createRequest.Success += resChannel =>
                        {
                            Logger.Log($"Joined PM channel {channel} ({resChannel.ChannelID})");

                            if (resChannel.ChannelID.HasValue)
                            {
                                channel.Id = resChannel.ChannelID.Value;

                                addMessages(resChannel.RecentMessages);
                                channel.MessagesLoaded = true; // this will mark the channel as having received messages even if there were none.
                            }
                        };

                        api.Queue(createRequest);
                        break;

                    default:
                        Logger.Log($"Attempting to join public channel {channel}");

                        var req = new JoinChannelRequest(channel);
                        req.Success += () =>
                        {
                            Logger.Log($"Joined public channel {channel}");
                            joinChannel(channel, fetchInitialMessages);

                            // Required after joining public channels to mark the user as online in them.
                            // Todo: Temporary workaround for https://github.com/ppy/osu-web/issues/9602
                            SendAck();
                        };
                        req.Failure += e =>
                        {
                            Logger.Log($"Failed to join public channel {channel} ({e.Message})");
                            LeaveChannel(channel);
                        };
                        api.Queue(req);
                        return channel;
                }
            }
            else
            {
                if (fetchInitialMessages)
                    this.fetchInitialMessages(channel);
            }

            CurrentChannel.Value ??= channel;

            return channel;
        }

        /// <summary>
        /// Leave the specified channel. Can be called from any thread.
        /// </summary>
        /// <param name="channel">The channel to leave.</param>
        public void LeaveChannel(Channel channel) => Schedule(() => leaveChannel(channel, true));

        private void leaveChannel(Channel channel, bool sendLeaveRequest)
        {
            if (channel == null) return;

            if (channel == CurrentChannel.Value)
                CurrentChannel.Value = null;

            joinedChannels.Remove(channel);

            // Prevent the closedChannel list from exceeding the max size
            // by removing the oldest element
            if (closedChannels.Count >= closed_channels_max_size)
            {
                closedChannels.RemoveAt(0);
            }

            // For PM channels, we store the user ID; else, we store the channel ID
            closedChannels.Add(channel.Type == ChannelType.PM
                ? new ClosedChannel(ChannelType.PM, channel.Users.Single().Id)
                : new ClosedChannel(channel.Type, channel.Id));

            if (channel.Joined.Value)
            {
                if (sendLeaveRequest)
                    api.Queue(new LeaveChannelRequest(channel));
                channel.Joined.Value = false;
            }
        }

        /// <summary>
        /// Opens the most recently closed channel that has not already been reopened,
        /// Works similarly to reopening the last closed tab on a web browser.
        /// </summary>
        public void JoinLastClosedChannel()
        {
            // This loop could be eliminated if the join channel operation ensured that every channel joined
            // is removed from the closedChannels list, but it'd require a linear scan of closed channels on every join.
            // To keep the overhead of joining channels low, just lazily scan the list of closed channels locally.
            while (closedChannels.Count > 0)
            {
                ClosedChannel lastClosedChannel = closedChannels.Last();
                closedChannels.RemoveAt(closedChannels.Count - 1);

                // If the user has already joined the channel, try the next one
                if (joinedChannels.FirstOrDefault(lastClosedChannel.Matches) != null)
                    continue;

                Channel lastChannel = AvailableChannels.FirstOrDefault(lastClosedChannel.Matches);

                if (lastChannel != null)
                {
                    // Channel exists as an available channel, directly join it
                    CurrentChannel.Value = JoinChannel(lastChannel);
                }
                else if (lastClosedChannel.Type == ChannelType.PM)
                {
                    // Try to get user in order to open PM chat
                    users.GetUserAsync((int)lastClosedChannel.Id).ContinueWith(task =>
                    {
                        var user = task.GetResultSafely();

                        if (user != null)
                            Schedule(() => CurrentChannel.Value = JoinChannel(new Channel(user)));
                    });
                }

                return;
            }
        }

        /// <summary>
        /// Marks the <paramref name="channel"/> as read
        /// </summary>
        /// <param name="channel">The channel that will be marked as read</param>
        public void MarkChannelAsRead(Channel channel)
        {
            if (channel.LastMessageId == channel.LastReadId)
                return;

            var message = channel.Messages.FindLast(msg => !(msg is LocalMessage));

            if (message == null)
                return;

            var req = new MarkChannelAsReadRequest(channel, message);

            req.Success += () => channel.LastReadId = message.Id;
            req.Failure += e => Logger.Log($"Failed to mark channel {channel} up to '{message}' as read ({e.Message})", LoggingTarget.Network);

            api.Queue(req);
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (chatClient.IsNotNull())
                chatClient.Dispose();
        }
    }

    /// <summary>
    /// An exception thrown when a channel could not been found.
    /// </summary>
    public class ChannelNotFoundException : Exception
    {
        public ChannelNotFoundException(string channelName)
            : base($"A channel with the name {channelName} could not be found.")
        {
        }
    }

    /// <summary>
    /// Stores information about a closed channel
    /// </summary>
    public class ClosedChannel
    {
        public readonly ChannelType Type;
        public readonly long Id;

        public ClosedChannel(ChannelType type, long id)
        {
            Type = type;
            Id = id;
        }

        public bool Matches(Channel channel)
        {
            if (channel.Type != Type) return false;

            return Type == ChannelType.PM
                ? channel.Users.Single().Id == Id
                : channel.Id == Id;
        }
    }
}
