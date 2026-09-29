using NocatFarm.Config;
using SteamKit2;

namespace NocatFarm.Core;

/// <summary>
/// A tap on every packet SteamKit sends and receives. It does two jobs:
///
///   1. Connection liveness. Every INCOMING packet stamps the bot's last-packet time. A logged-in Steam
///      connection receives server traffic constantly (hundreds of messages a minute), so this going quiet is
///      a reliable sign the socket has died silently - a "half-open" connection that never raised a disconnect.
///      The heartbeat watches that timestamp and reconnects when it goes stale. This replaced a probe that
///      pinged the friends/profile service about the account's OWN id every minute, which is the same class of
///      self-directed friends call that used to sign the account owner out of chat. Liveness needs no such call.
///
///   2. Diagnostics. When the environment variable <c>NOCATFARM_NETLOG</c> is set it also writes one line per
///      message - time, direction, EMsg - to <c>netlog-&lt;bot&gt;.txt</c>, for comparing wire behaviour against
///      another client. Off by default; only the cheap timestamp stamp runs in normal operation.
/// </summary>
internal sealed class NetLog : IDebugNetworkListener {
	private readonly Bot _bot;
	private readonly string? _path;   // null unless NOCATFARM_NETLOG is set - then also written to
	private readonly Lock _gate = new();

	public NetLog(Bot bot) {
		_bot = bot;

		if (System.Environment.GetEnvironmentVariable("NOCATFARM_NETLOG") == "1") {
			_path = System.IO.Path.Combine(ConfigStore.Root, $"netlog-{bot.Name}.txt");

			try {
				System.IO.File.WriteAllText(_path, $"# netlog for {bot.Name}\n");
			} catch (System.Exception e) {
				// diagnostic only - but it was asked for, so say why there isn't one
				Log.Failed($"couldn't start the packet log {System.IO.Path.GetFileName(_path)}", e, bot.Name);
			}
		}
	}

	public void OnIncomingNetworkMessage(EMsg msgType, byte[] data) {
		_bot.NoteIncomingPacket();   // liveness - always, even when not file-logging

		if (_path != null) {
			Write("<= in ", msgType, data.Length);
		}
	}

	public void OnOutgoingNetworkMessage(EMsg msgType, byte[] data) {
		if (_path != null) {
			Write("=> OUT", msgType, data.Length);
		}
	}

	private void Write(string dir, EMsg msg, int len) {
		try {
			lock (_gate) {
				System.IO.File.AppendAllText(_path!, $"{System.DateTime.Now:HH:mm:ss.fff} {dir} {msg} ({len}b)\n");
			}
		} catch (System.Exception e) {
			// diagnostic only - and this runs per packet, so each distinct failure is said once
			Log.DebugOnChange($"netlog:{_bot.Name}", $"couldn't write the packet log: {Log.Describe(e)}", _bot.Name);
		}
	}
}
