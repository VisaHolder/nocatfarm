using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
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
/// What it shows is built only from the accounts picked in "Show these accounts" - by default the robot
/// accounts, never a human-mode account, whose whole point is looking like a person.
/// </summary>
public static class DiscordPresence {
	/// <summary>nocat.farm's application in the Discord Developer Portal: its name and logo are what Discord shows.</summary>
	private const string AppId = "1553951357894008872";

	private const string Site = "https://github.com/VisaHolder/nocatfarm";

	private static BotManager? _mgr;
	private static NamedPipeClientStream? _pipe;
	private static string _lastSent = "";
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

				if ((_pipe is not { IsConnected: true }) && !await ConnectAsync().ConfigureAwait(false)) {
					await Task.Delay(60_000).ConfigureAwait(false);

					continue;
				}

				object? activity = Card();
				string json = JsonSerializer.Serialize(activity);

				// Discord allows a handful of updates a minute; only send what changed (and now and then anyway, so a
				// Discord restart that kept the pipe picks it back up).
				if ((json != _lastSent) || (DateTime.UtcNow - _sentAt > TimeSpan.FromMinutes(10))) {
					await SendAsync(1, new { cmd = "SET_ACTIVITY", args = new { pid = Environment.ProcessId, activity }, nonce = Guid.NewGuid().ToString() }).ConfigureAwait(false);
					await ReadAsync().ConfigureAwait(false);
					_lastSent = json;
					_sentAt = DateTime.UtcNow;
				}

				await Task.Delay(15_000).ConfigureAwait(false);
			} catch (Exception e) {
				// Discord closed or restarted: start over on the next pass.
				Log.Debug(new Said("discord: {0}", e.Message));
				Disconnect();
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

		// The names line leaves out the featured account - it's already the picture.
		Bot[] named = [.. shown.Where(b => b != featured)];
		string state = G.DiscordShowNames && (named.Length > 0) ? Names(named) : today;

		// What clicking does is said on hover - Discord gives a picture no other hint that it's a link.
		Dictionary<string, object> assets = new() {
			["large_image"] = "logo",
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

	private static void Disconnect() {
		if (_pipe == null) {
			return;
		}

		try {
			// Take the card down straight away rather than when Discord notices the pipe is gone.
			if (_pipe.IsConnected) {
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
		byte[] header = new byte[8];
		await _pipe!.ReadExactlyAsync(header).ConfigureAwait(false);
		int op = BinaryPrimitives.ReadInt32LittleEndian(header);
		byte[] body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4))];
		await _pipe.ReadExactlyAsync(body).ConfigureAwait(false);
		string json = Encoding.UTF8.GetString(body);

		// 2 = Discord closing the connection, usually a bad application ID.
		if (op == 2) {
			throw new IOException(json);
		}

		return json;
	}
}
