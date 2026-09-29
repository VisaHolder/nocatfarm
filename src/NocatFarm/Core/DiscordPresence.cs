using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// "Playing nocat.farm" on your Discord profile while nocat.farm is open - the same card a game gets.
///
/// Talks to the Discord app on this PC over its local pipe (discord-ipc-N): a handshake with nocat.farm's
/// Discord application ID, then SET_ACTIVITY whenever what it would show changes. Nothing goes over the
/// internet from here, and there is no token - Discord shows it for whoever is signed into the Discord app.
/// With Discord closed it quietly tries again every minute.
///
/// On Windows that is a named pipe; on Linux Discord listens on a unix socket of the same name in the runtime
/// folder ($XDG_RUNTIME_DIR, or the temp folder), with the Flatpak and Snap builds each in a folder of their own.
/// A server or a container simply has none of them, so there it finds nothing and keeps quiet.
///
/// What it shows is built only from the accounts picked in "Show these accounts" - by default the robot
/// accounts, never a human-mode account, whose whole point is looking like a person.
/// </summary>
public static class DiscordPresence {
	/// <summary>nocat.farm's application in the Discord Developer Portal: its name and logo are what Discord shows.</summary>
	private const string AppId = "1553951357894008872";

	private const string Site = "https://github.com/VisaHolder/nocatfarm";

	/// <summary>The logo with the n filling up with purple, as a GIF - Discord plays GIFs from a web address, not from the
	/// app's uploaded pictures. Made by tools/make-logo-gif.py; the same file is assets/logo-liquid.gif.</summary>
	private const string AnimatedLogo = "https://nocat.lol/nocatfarm/logo.gif";

	private static BotManager? _mgr;
	/// <summary>The connection: a named pipe on Windows, a unix socket elsewhere. Both speak the same frames.</summary>
	private static Stream? _pipe;

	private static bool Connected => _pipe switch {
		NamedPipeClientStream p => p.IsConnected,
		NetworkStream s => s.Socket.Connected,
		_ => false
	};
	private static string _lastSent = "";

	// Whether the log has said the card is up, so it says so once per connection and once when it comes down.
	private static bool _shown;

	// The last reason Discord gave for not showing the card, so the same one isn't logged every few minutes.
	private static string? _lastRefusal;

	/// <summary>Discord's reason for refusing a SET_ACTIVITY, from its reply ({"evt":"ERROR","data":{"message":...}}), or null.</summary>
	internal static string? Refusal(string reply) {
		try {
			using JsonDocument doc = JsonDocument.Parse(reply);

			if (doc.RootElement.TryGetProperty("evt", out JsonElement evt) && (evt.GetString() == "ERROR")) {
				return doc.RootElement.TryGetProperty("data", out JsonElement data) && data.TryGetProperty("message", out JsonElement m)
					? m.GetString() ?? "no reason given"
					: "no reason given";
			}
		} catch (JsonException) {
			// Not an answer we can read - treat it as accepted, as before.
		}

		return null;
	}
	private static DateTime _sentAt = DateTime.MinValue;
	private static readonly long StartedUnix = new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime()).ToUnixTimeSeconds();

	private static GlobalConfig G => Live.Global;

	public static void Start(BotManager mgr) {
		_mgr = mgr;

		if (AppId.Length == 0) {
			return;
		}

		_ = Task.Run(LoopAsync);
	}

	private static async Task LoopAsync() {
		while (true) {
			try {
				if (!G.DiscordPresence) {
					Disconnect();
					await Task.Delay(5000).ConfigureAwait(false);

					continue;
				}

				if (!Connected && !await ConnectAsync().ConfigureAwait(false)) {
					await Task.Delay(60_000).ConfigureAwait(false);

					continue;
				}

				object? activity = Card();
				string json = JsonSerializer.Serialize(activity);

				// Discord allows a handful of updates a minute; only send what changed - and every few minutes anyway, so a
				// card Discord dropped on its side (it can, after the PC sleeps or Discord reloads, with the pipe still
				// open) comes back within minutes instead of staying gone.
				if ((json != _lastSent) || (DateTime.UtcNow - _sentAt > TimeSpan.FromMinutes(3))) {
					await SendAsync(1, new { cmd = "SET_ACTIVITY", args = new { pid = Environment.ProcessId, activity }, nonce = Guid.NewGuid().ToString() }).ConfigureAwait(false);
					string reply = await ReadAsync().ConfigureAwait(false);
					_sentAt = DateTime.UtcNow;

					// Discord answers every card - and says so when it won't show one. Ignoring that answer meant a
					// refused card looked, from here, exactly like a card on your profile.
					if (Refusal(reply) is { } why) {
						if (why != _lastRefusal) {
							_lastRefusal = why;
							Log.Warn(new Said("Discord didn't show the card: {0}", why), "discord");
						}

						_lastSent = "";   // try again on a later pass, not every fifteen seconds (the 3-minute resend)

						await Task.Delay(15_000).ConfigureAwait(false);

						continue;
					}

					_lastRefusal = null;
					_lastSent = json;

					if (!_shown && (activity != null)) {
						_shown = true;
						Log.Info(new Said("Discord profile now shows Playing nocat.farm"), "discord");
					}
				}

				await Task.Delay(15_000).ConfigureAwait(false);
			} catch (Exception e) {
				// Discord closed or restarted (an update does it): start over on the next pass - quietly. "Rich Presence
				// off/on" in the log is for when you switch it; Discord blinking isn't news, and it read as if you had.
				Log.Debug(new Said("Discord closed the connection ({0}) - trying again in a minute", e.Message), "discord");
				Disconnect(quietly: true);
				await Task.Delay(60_000).ConfigureAwait(false);
			}
		}
	}

	/// <summary>
	/// The card. Null clears it (nothing picked to show).
	///
	///   nocat.farm
	///   Farming cards · 12 left          what the shown accounts are doing
	///   kylro · old  (2 of 3)            their Steam names, and how many are signed in
	///   5:12:00 elapsed                  since nocat.farm was opened
	///   [ Get nocat.farm ] [ reap. on Steam ]
	///
	/// The logo links to GitHub and says the version and today's cards on hover; the first shown account's avatar
	/// sits in its corner. Every part but the first two lines has its own switch.
	/// </summary>
	private static object? Card() {
		// The featured account is the face of the card (the avatar); the lines are about the farm.
		Bot? featured = G.DiscordFeatured.Trim() is { Length: > 0 } f ? _mgr?.Get(f) : null;
		Bot[] shown = [.. Picked().OrderBy(static b => b.IsOnline ? 0 : 1)];

		if ((shown.Length == 0) && (featured == null)) {
			return null;
		}

		Bot[] online = [.. shown.Where(static b => b.IsOnline)];
		int cardsLeft = online.Sum(static b => b.CardsRemaining);
		int cardsToday = Stats.Recent(24).Count(e => (e.Kind == Stats.KindCard) && shown.Any(b => string.Equals(b.Name, e.Bot, StringComparison.OrdinalIgnoreCase)));

		string details = online.Length == 0 ? new Said("Resting").ToString()
			: cardsLeft > 0 ? new Said("Farming cards · {0} left", cardsLeft).ToString()
			: new Said("Idling games").ToString();

		string today = new Said("{0} cards today", cardsToday).ToString();

		// With names off, the line counts what Second line picks - hours over every shown account, like Steam's
		// "hrs past 2 weeks".
		string counted = G.DiscordSecondLine switch {
			1 => new Said("{0} hrs past week", Hours(History.MinutesOver(7, shown.Select(static b => b.Name)))).ToString(),
			2 => new Said("{0} hrs past month", Hours(History.MinutesOver(30, shown.Select(static b => b.Name)))).ToString(),
			_ => today
		};

		// The names line leaves out the featured account - it's already the picture.
		Bot[] named = [.. shown.Where(b => b != featured)];
		string state = G.DiscordShowNames && (named.Length > 0) ? Names(named) : counted;

		// What clicking does is said on hover - Discord gives a picture no other hint that it's a link.
		Dictionary<string, object> assets = new() {
			["large_image"] = AnimatedLogo,
			["large_text"] = new Said("nocat.farm {0} · {1} · click to open it on GitHub", NocatFarm.Build.Version, today).ToString(),
			["large_url"] = Site
		};

		Bot lead = featured ?? shown.FirstOrDefault(static b => b.AvatarUrl.Length > 0) ?? shown[0];

		if (G.DiscordShowAvatar && (lead.AvatarUrl.Length > 0)) {
			assets["small_image"] = lead.AvatarUrl;
			assets["small_text"] = new Said("{0} · click for the Steam profile", Display(lead)).ToString();
			assets["small_url"] = Profile(lead);
		}

		// Both lines are links too, on top of the two buttons: Discord hides your own buttons from you, but not these.
		// The first goes to GitHub, the names to the first named account's Steam profile.
		Dictionary<string, object> card = new() {
			["type"] = 0,
			["details"] = details,
			["details_url"] = Site,
			["state"] = state,
			["assets"] = assets
		};

		if (G.DiscordShowNames && named.FirstOrDefault() is { SteamId: not 0 } first) {
			card["state_url"] = Profile(first);
		}

		if (G.DiscordShowCounter) {
			card["party"] = new { id = "nocatfarm", size = new[] { online.Length, shown.Length } };
		}

		if (G.DiscordShowTimer) {
			card["timestamps"] = new { start = StartedUnix };
		}

		object[] buttons = [.. new[] { G.DiscordButton1, G.DiscordButton2 }.Select(Button).OfType<object>()];

		if (buttons.Length > 0) {
			card["buttons"] = buttons;
		}

		return card;
	}

	/// <summary>"kylro · old", or "kylro · old · +2" for a long list - Discord cuts the line off anyway.</summary>
	private static string Names(Bot[] shown) {
		string[] names = [.. shown.Select(Display)];

		return names.Length <= 3 ? string.Join(" · ", names) : string.Join(" · ", names.Take(2)) + $" · +{names.Length - 2}";
	}

	/// <summary>The Steam name people know the account by; the nocat.farm name until Steam has said it.</summary>
	private static string Display(Bot b) => b.SteamName.Length > 0 ? b.SteamName : b.Name;

	private static string Profile(Bot b) => b.SteamId != 0 ? $"https://steamcommunity.com/profiles/{b.SteamId}" : Site;

	/// <summary>One button from its setting: "github", an account name, "Label | https://...", or nothing.</summary>
	private static object? Button(string setting) {
		string v = setting.Trim();

		if (v.Length == 0) {
			return null;
		}

		if (v.Equals("github", StringComparison.OrdinalIgnoreCase)) {
			return new { label = new Said("Get nocat.farm").ToString(), url = Site };
		}

		int bar = v.IndexOf('|');

		if (bar > 0) {
			string label = v[..bar].Trim();
			string url = v[(bar + 1)..].Trim();

			return (label.Length > 0) && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
				? new { label = Clip(label), url }
				: null;
		}

		if (_mgr?.Get(v) is { SteamId: not 0 } b) {
			return new { label = Clip(new Said("{0} on Steam", Display(b)).ToString()), url = Profile(b) };
		}

		return null;
	}

	/// <summary>Discord refuses a button label over 32 characters - and the whole card with it.</summary>
	private static string Clip(string label) => label.Length <= 32 ? label : label[..31] + "…";

	/// <summary>"Show these accounts": names, or "all"; empty means every account that isn't in human mode.</summary>
	/// <summary>Minutes as hours the way Steam writes them: 4.5 under ten, then whole hours with a thousands comma.</summary>
	internal static string Hours(double minutes) {
		double h = Math.Max(0, minutes) / 60;

		return h < 10 ? h.ToString("0.#", CultureInfo.InvariantCulture) : Math.Round(h).ToString("N0", CultureInfo.InvariantCulture);
	}

	private static IEnumerable<Bot> Picked() {
		IEnumerable<Bot> all = _mgr?.All ?? [];
		string list = G.DiscordPresenceAccounts.Trim();

		if (list.Length == 0) {
			return all.Where(static b => !b.Cfg.LegitMode);
		}

		if (list.Equals("all", StringComparison.OrdinalIgnoreCase)) {
			return all;
		}

		HashSet<string> names = new(list.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

		return all.Where(b => names.Contains(b.Name));
	}

	private static async Task<bool> ConnectAsync() {
		if (!OperatingSystem.IsWindows()) {
			return await ConnectUnixAsync().ConfigureAwait(false);
		}

		for (int i = 0; i < 10; i++) {
			NamedPipeClientStream pipe = new(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);

			try {
				await pipe.ConnectAsync(500).ConfigureAwait(false);
				_pipe = pipe;
				await SendAsync(0, new { v = 1, client_id = AppId }).ConfigureAwait(false);
				await ReadAsync().ConfigureAwait(false);   // READY
				_lastSent = "";

				return true;
			} catch (Exception e) when (e is TimeoutException or IOException) {
				await pipe.DisposeAsync().ConfigureAwait(false);
				_pipe = null;
			}
		}

		return false;
	}

	/// <summary>
	/// Where the Linux Discord apps put their socket: the runtime folder first (where every current build puts it),
	/// then the temp folders, each plain and then under the Flatpak and Snap sub-folders.
	/// </summary>
	internal static IEnumerable<string> UnixSocketFolders() {
		string?[] roots = [
			Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
			Environment.GetEnvironmentVariable("TMPDIR"),
			Environment.GetEnvironmentVariable("TMP"),
			Environment.GetEnvironmentVariable("TEMP"),
			"/tmp"
		];

		HashSet<string> seen = new(StringComparer.Ordinal);

		foreach (string? root in roots) {
			if (string.IsNullOrWhiteSpace(root)) {
				continue;
			}

			foreach (string dir in new[] { root, Path.Combine(root, "app", "com.discordapp.Discord"), Path.Combine(root, "snap.discord") }) {
				if (seen.Add(dir)) {
					yield return dir;
				}
			}
		}
	}

	private static async Task<bool> ConnectUnixAsync() {
		foreach (string dir in UnixSocketFolders()) {
			for (int i = 0; i < 10; i++) {
				string path = Path.Combine(dir, $"discord-ipc-{i}");

				if (!File.Exists(path)) {
					continue;   // a socket shows up as a file; no point dialling what isn't there
				}

				if (await ConnectSocketAsync(path).ConfigureAwait(false)) {
					return true;
				}
			}
		}

		return false;
	}

	/// <summary>One socket: connect, handshake, READY. Separate so it can be pointed at any path.</summary>
	internal static async Task<bool> ConnectSocketAsync(string path) {
		Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

		try {
			using (CancellationTokenSource timeout = new(500)) {
				await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token).ConfigureAwait(false);
			}

			_pipe = new NetworkStream(socket, ownsSocket: true);
			await SendAsync(0, new { v = 1, client_id = AppId }).ConfigureAwait(false);
			await ReadAsync().ConfigureAwait(false);   // READY
			_lastSent = "";

			return true;
		} catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or TimeoutException) {
			// a stale socket left by a Discord that has since closed, or one that isn't Discord's
			if (_pipe != null) {
				await _pipe.DisposeAsync().ConfigureAwait(false);
				_pipe = null;
			} else {
				socket.Dispose();
			}

			return false;
		}
	}

	private static void Disconnect(bool quietly = false) {
		if (_pipe == null) {
			return;
		}

		if (_shown && !quietly) {
			_shown = false;
			Log.Info(new Said("Discord Rich Presence off"), "discord");
		}

		try {
			// Take the card down straight away rather than when Discord notices the pipe is gone.
			if (Connected) {
				SendAsync(1, new { cmd = "SET_ACTIVITY", args = new { pid = Environment.ProcessId, activity = (object?) null }, nonce = Guid.NewGuid().ToString() }).Wait(2000);
			}
		} catch {
			// going anyway
		}

		_pipe.Dispose();
		_pipe = null;
		_lastSent = "";
	}

	/// <summary>Called at shutdown, so the card goes when nocat.farm does.</summary>
	public static void Stop() => Disconnect();

	/// <summary>A frame: opcode and length (little-endian int32s), then the JSON.</summary>
	private static async Task SendAsync(int op, object payload) {
		byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
		byte[] frame = new byte[8 + body.Length];
		BinaryPrimitives.WriteInt32LittleEndian(frame, op);
		BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
		body.CopyTo(frame, 8);
		await _pipe!.WriteAsync(frame).ConfigureAwait(false);
		await _pipe.FlushAsync().ConfigureAwait(false);
	}

	private static async Task<string> ReadAsync() {
		// Five seconds, then give up. Discord answers in milliseconds; a pipe that never answers - a hung Discord, or
		// something else sitting on the name - used to hold the whole card loop on this read forever. A timeout
		// is reported as one, so the connect loop moves on to the next pipe and the card loop starts over.
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
		byte[] header = new byte[8];
		byte[] body;

		try {
			await _pipe!.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
			body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4))];
			await _pipe.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
		} catch (OperationCanceledException) when (timeout.IsCancellationRequested) {
			throw new TimeoutException("Discord didn't answer within 5s");
		}

		int op = BinaryPrimitives.ReadInt32LittleEndian(header);
		string json = Encoding.UTF8.GetString(body);

		// 2 = Discord closing the connection, usually a bad application ID.
		if (op == 2) {
			throw new IOException(json);
		}

		return json;
	}
}
