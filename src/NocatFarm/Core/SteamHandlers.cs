using SteamKit2;
using SteamKit2.Internal;

namespace NocatFarm.Core;

/// <summary>Steam pushed new inventory items - for a farming account that means a card dropped.</summary>
public sealed class ItemAnnouncementsCallback : CallbackMsg {
	public uint NewItems { get; }

	internal ItemAnnouncementsCallback(uint newItems) => NewItems = newItems;
}

/// <summary>Steam pushed a new profile comment. This is a PUSH - no polling, it lands the moment it is posted.</summary>
public sealed class CommentNotificationsCallback : CallbackMsg {
	public uint NewComments { get; }
	public uint NewOwnerComments { get; }

	internal CommentNotificationsCallback(uint newComments, uint newOwnerComments) {
		NewComments = newComments;
		NewOwnerComments = newOwnerComments;
	}
}

/// <summary>
/// The messages SteamKit doesn't surface itself but that matter here: item announcements (card drops - they let
/// farming react instantly instead of re-scraping on a timer) and comment notifications (someone commented on
/// this account's profile).
/// </summary>
public sealed class NocatHandler : ClientMsgHandler {
	public override void HandleMsg(IPacketMsg packetMsg) {
		ArgumentNullException.ThrowIfNull(packetMsg);

		switch (packetMsg.MsgType) {
			case EMsg.ClientItemAnnouncements: {
				ClientMsgProtobuf<CMsgClientItemAnnouncements> msg = new(packetMsg);
				Client?.PostCallback(new ItemAnnouncementsCallback(msg.Body.count_new_items));

				break;
			}
			case EMsg.ClientCommentNotifications: {
				ClientMsgProtobuf<CMsgClientCommentNotifications> msg = new(packetMsg);
				Client?.PostCallback(new CommentNotificationsCallback(msg.Body.count_new_comments, msg.Body.count_new_comments_owner));

				break;
			}
		}
	}

	/// <summary>Ask Steam to (re)send the item announcement state. Cheap, and it primes the push channel.</summary>
	public void RequestItemAnnouncements() =>
		Client?.Send(new ClientMsgProtobuf<CMsgClientRequestItemAnnouncements>(EMsg.ClientRequestItemAnnouncements));

	/// <summary>Same for comment notifications.</summary>
	public void RequestCommentNotifications() =>
		Client?.Send(new ClientMsgProtobuf<CMsgClientRequestCommentNotifications>(EMsg.ClientRequestCommentNotifications));

	/// <summary>
	/// Answer a Steam group invite. Groups don't go through the friend-request path - accepting one is its own
	/// message, and without this the invite sits in the notification list forever.
	/// </summary>
	public void AcknowledgeClanInvite(ulong clanId, bool accept) {
		ClientMsg<AcknowledgeClanInviteBody> msg = new() {
			Body = { ClanID = clanId, AcceptInvite = accept }
		};

		Client?.Send(msg);
	}
}

/// <summary>
/// The body of ClientAcknowledgeClanInvite, which SteamKit doesn't ship a definition for.
///
/// It is one of the handful of Steam messages that never made the move to protobufs - the wire format is still
/// just the two fields laid out back to back, so it is written by hand here rather than generated.
/// </summary>
public sealed class AcknowledgeClanInviteBody : ISteamSerializableMessage {
	public ulong ClanID { get; set; }
	public bool AcceptInvite { get; set; }

	EMsg ISteamSerializableMessage.GetEMsg() => EMsg.ClientAcknowledgeClanInvite;

	public void Serialize(Stream stream) {
		ArgumentNullException.ThrowIfNull(stream);

		using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, true);
		writer.Write(ClanID);
		writer.Write(AcceptInvite);
	}

	public void Deserialize(Stream stream) {
		ArgumentNullException.ThrowIfNull(stream);

		using BinaryReader reader = new(stream, System.Text.Encoding.UTF8, true);
		ClanID = reader.ReadUInt64();
		AcceptInvite = reader.ReadBoolean();
	}
}
