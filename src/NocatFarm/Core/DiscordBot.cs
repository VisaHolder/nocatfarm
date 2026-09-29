using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Your own Discord bot taking / commands - the Discord twin of the Telegram commands, next to the webhook, which
/// still only posts notifications.
///
/// It keeps one connection open to Discord's Gateway (a websocket), says it's alive every so often the way Discord
/// asks, and picks the same session back up after a dropped connection instead of starting over. The commands are
/// registered twice: for private chats with the bot (global), and for each server the bot is in, where they show up
/// straight away. Registered only when they changed, so a restart doesn't resend them.
///
/// Only one Discord account is obeyed: the one that sent /connect with the one-time code the dashboard shows. Before
/// that only /connect works, and anyone else gets a private "not allowed". Replies in a server are private too
/// (only the owner sees them) - they can hold Steam Guard codes. The two that can't be undone from a phone (exit and
/// remove) need "confirm" on the end, exactly as on Telegram.
/// </summary>
public static partial class Notifier {
	private const string DiscordApi = "https://discord.com/api/v10";

	/// <summary>How long a /connect code from the dashboard works for.</summary>
	public const int DiscordCodeMinutes = 10;

	/// <summary>A reply only the person who ran the command sees.</summary>
	private const int Ephemeral = 64;

	/// <summary>No link previews under the replies - a dashboard address doesn't need a big box. (The edited first
	/// message can't take it, so links there go in &lt;&gt;, which does the same.)</summary>
	private const int SuppressEmbeds = 4;

	/// <summary>A Discord message holds 2000 characters.</summary>
	internal const int DiscordMessageLimit = 2000;

	// The session, kept across reconnects so a dropped connection resumes instead of starting over (and doesn't
	// use up one of the day's limited fresh starts).
	private static string _dcToken = "";
	private static string _dcSessionId = "";
	private static string _dcResumeUrl = "";
	private static long _dcSeq;
	private static string _dcAppId = "";
	private static string _dcBotName = "";
	private static volatile bool _dcOnline;
	private static string _dcBadToken = "";
	private static string _dcGreetedFor = "";
	private static string _dcOwnerName = "";
	private static string _dcOwnerNameFor = "";   // the owner ID that name belongs to
	private static Said _dcProblem;

	/// <summary>The command set last registered per place (the app, or the app in one server), so an unchanged set isn't sent again.</summary>
	private static readonly ConcurrentDictionary<string, string> DiscordRegistered = new();
	private static readonly SemaphoreSlim DiscordRegisterGate = new(1, 1);

	private static readonly Lock DiscordCodeGate = new();
	private static string _dcCode = "";
	private static DateTime _dcCodeUntil = DateTime.MinValue;
	private static int _dcCodeMisses;

	/// <summary>The token as pasted, without a "Bot " copied along with it.</summary>
	private static string DiscordToken {
		get {
			string t = G.DiscordBotToken.Trim();

			return t.StartsWith("Bot ", StringComparison.OrdinalIgnoreCase) ? t[4..].Trim() : t;
		}
	}

	public static bool HasDiscordBot => DiscordToken.Length > 0;
	public static bool DiscordBotOnline => _dcOnline && HasDiscordBot;
	public static string DiscordBotName => DiscordBotOnline ? _dcBotName : "";
	public static bool DiscordConnected => HasDiscordBot && (G.DiscordOwnerId.Length > 0);

	/// <summary>Who the bot obeys: their name once they've used it this run, their Discord ID before that.</summary>
	public static string DiscordOwner => (_dcOwnerName.Length > 0) && (_dcOwnerNameFor == G.DiscordOwnerId) ? _dcOwnerName : G.DiscordOwnerId;

	/// <summary>Why the bot isn't online, when there's a reason worth showing (a bad token, say).</summary>
	public static string DiscordBotProblem => HasDiscordBot && !_dcOnline ? _dcProblem.ToString() : "";

	/// <summary>The link that adds the bot to a server - with its commands. Only once the bot is online, as that's when its ID is known.</summary>
	public static string? DiscordInviteUrl => DiscordBotOnline && (_dcAppId.Length > 0)
		? $"https://discord.com/oauth2/authorize?client_id={_dcAppId}&scope=bot%20applications.commands&permissions=0"
		: null;

	/// <summary>For 'notify': the Discord bot in one line.</summary>
	public static string DiscordBotState() {
		if (!HasDiscordBot) {
			return new Said("Discord bot: not set up").ToString();
		}

		if (!G.DiscordCommands) {
			return new Said("Discord bot: off (Take commands from Discord)").ToString();
		}

		if (!_dcOnline) {
			return (_dcProblem.IsEmpty ? new Said("Discord bot: connecting...") : new Said("Discord bot: {0}", _dcProblem)).ToString();
		}

		return (DiscordConnected
			? new Said("Discord bot: online as {0}, connected to {1}", _dcBotName, DiscordOwner)
			: new Said("Discord bot: online as {0} - press Connect Discord in Settings, Notifications to link your Discord account", _dcBotName)).ToString();
	}

	/// <summary>A fresh one-time code for /connect - the Connect Discord button shows it. Any earlier code stops working.</summary>
	public static string NewDiscordCode() {
		// No 0/O or 1/I/L, so it can't be misread off the screen.
		const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
		char[] code = new char[8];

		for (int i = 0; i < code.Length; i++) {
			code[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
		}

		lock (DiscordCodeGate) {
			_dcCode = new string(code);
			_dcCodeUntil = DateTime.UtcNow.AddMinutes(DiscordCodeMinutes);
			_dcCodeMisses = 0;

			return _dcCode;
		}
	}

	/// <summary>Does this match the dashboard's code? A match uses it up, and so do five misses - so it can't be guessed.</summary>
	internal static bool UseDiscordCode(string typed) {
		byte[] given = Encoding.UTF8.GetBytes(typed.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant());

		lock (DiscordCodeGate) {
			if ((_dcCode.Length == 0) || (DateTime.UtcNow > _dcCodeUntil)) {
				return false;
			}

			bool match = CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(_dcCode));

			if (match || (++_dcCodeMisses >= 5)) {
				_dcCode = "";
			}

			return match;
		}
	}

	internal enum DiscordAsk {
		Run,
		Connect,
		NotConnected,
		NotAllowed
	}

	/// <summary>
	/// Who may do what: /connect is open to anyone (it needs the dashboard's code), everything else only to the
	/// connected owner - and before there is one, nothing else at all.
	/// </summary>
	internal static DiscordAsk DiscordGate(string command, string userId, string ownerId) =>
		command == "connect" ? DiscordAsk.Connect
		: ownerId.Length == 0 ? DiscordAsk.NotConnected
		: (userId.Length > 0) && (userId == ownerId) ? DiscordAsk.Run
		: DiscordAsk.NotAllowed;

	/// <summary>
	/// The "/" menu. Discord wants names of 1-32 lowercase letters, numbers, - or _, and descriptions of at most 100
	/// characters. The global set is for private chats with the bot; each server gets its own copy (it shows up
	/// there at once), so the global one leaves servers out - with both, every command would show twice.
	/// </summary>
	internal static List<Dictionary<string, object>> DiscordCommandSet(bool global) {
		Said args = new("What goes after it, like an account name");
		(string Name, Said Description)[] everyday = [
			("status", new Said("What every account is doing")),
			("dashboard", new Said("Open the dashboard on your phone")),
			("cards", new Said("Cards left to farm")),
			("human", new Said("What human mode is doing today")),
			("offers", new Said("Live trade offers")),
			("confirmations", new Said("What's waiting to be confirmed")),
			("2fa", new Said("Steam Guard codes")),
			("stats", new Said("Cards and comments by hour")),
			("anywhere", new Said("Open the dashboard from anywhere: on, off, or the link")),
			("screen", new Said("Turn the PC's screens off: /screen off")),
			("update", new Said("Check for an update")),
			("help", new Said("Every command"))
		];

		List<Dictionary<string, object>> set = [.. everyday.Select(c => Command(c.Name, c.Description, "args", args, false))];
		set.Add(Command("nocat", new Said("Run any console command, like pause kylro 30"), "command", new Said("The command, typed like in the window"), true));
		set.Add(Command("connect", new Said("Connect your Discord account to nocat.farm"), "code", new Said("The code Connect Discord shows in nocat.farm"), true));

		return set;

		Dictionary<string, object> Command(string name, Said description, string option, Said optionDescription, bool required) {
			Dictionary<string, object> c = new() {
				["name"] = name,
				["type"] = 1,
				["description"] = Fit(description),
				["options"] = new object[] { new Dictionary<string, object> { ["type"] = 3, ["name"] = option, ["description"] = Fit(optionDescription), ["required"] = required } }
			};

			if (global) {
				// 1 = a private chat with the bot, 2 = a group chat; 0 (servers) comes from each server's own copy.
				c["contexts"] = new[] { 1, 2 };
				c["integration_types"] = new[] { 0 };
			} else {
				// In a server, only its admins see them in the menu - it's your server, and nobody else could use them anyway.
				c["default_member_permissions"] = "0";
			}

			return c;
		}

		static string Fit(Said s) {
			string text = s.ToString();

			return text.Length <= 100 ? text : text[..99] + "…";
		}
	}

	/// <summary>Console output as Discord messages: a code block keeps the columns lined up, each one under Discord's limit.</summary>
	internal static List<string> DiscordBlocks(string output) {
		// ``` inside the output would end the block early; a zero-width space between the backticks keeps it whole.
		// Every pair of backticks broken, so no run of any length can close the code block early.
		string safe = output.Replace("``", "`​`", StringComparison.Ordinal);

		return [.. ChatChunks(safe, DiscordMessageLimit - 100).Select(static c => "```text\n" + c + "\n```")];
	}

	/// <summary>How long Discord asked to wait after a "slow down": its retry_after, else the Retry-After header, never silly.</summary>
	internal static TimeSpan DiscordRetryAfter(string body, TimeSpan? header) {
		double wait = header?.TotalSeconds ?? 2;

		try {
			using JsonDocument d = JsonDocument.Parse(body);

			if (d.RootElement.TryGetProperty("retry_after", out JsonElement ra) && (ra.ValueKind == JsonValueKind.Number)) {
				wait = ra.GetDouble();
			}
		} catch (JsonException) {
			// the header, or the default
		}

		return TimeSpan.FromSeconds(Math.Clamp(wait, 0.5, 60));
	}

	private static bool DiscordWanted(string token) => (token.Length > 0) && G.DiscordCommands && (token != _dcBadToken);

	private static long? DiscordSeq() => Interlocked.Read(ref _dcSeq) is > 0 and var s ? s : null;

	private static void ForgetDiscordSession() {
		_dcSessionId = "";
		_dcResumeUrl = "";
		Interlocked.Exchange(ref _dcSeq, 0);
	}

	// ── the connection ──────────────────────────────────────────────────────
	private static async Task DiscordLoopAsync(CancellationToken ct) {
		int failures = 0;

		while (!ct.IsCancellationRequested) {
			string token = DiscordToken;
			TimeSpan wait;

			try {
				// No bot, commands off, or a token Discord already turned down: nothing to connect. Looked at again
				// every few seconds, so pasting a token or flipping the switch takes effect without a restart.
				if (!DiscordWanted(token)) {
					_dcOnline = false;
					await Task.Delay(3000, ct).ConfigureAwait(false);

					continue;
				}

				if (_dcToken != token) {
					// Another bot: nothing of the old one's session, name or commands carries over.
					_dcToken = token;
					ForgetDiscordSession();
					_dcAppId = "";
					_dcBotName = "";
					_dcProblem = default;
					DiscordRegistered.Clear();
				}

				(bool wasOnline, TimeSpan? hold) = await DiscordSessionAsync(token, ct).ConfigureAwait(false);
				failures = wasOnline ? 0 : failures + 1;

				if (wasOnline) {
					Log.Recovered("discord:bot");
				}

				wait = hold ?? (failures == 0 ? Rng.Seconds(1, 5) : SteamMaintenance.Grown(failures, TimeSpan.FromSeconds(2)));
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				return;
			} catch (Exception e) {
				failures++;
				wait = SteamMaintenance.Grown(failures, TimeSpan.FromSeconds(2));

				// Retried through an outage: the same failure is said once (an hour apart at most), and a real bug gets its stack.
				if (Log.DebugOnChange("discord:bot", $"Discord bot: connecting failed: {Log.Describe(e)}", "discord")
					&& e is not (HttpRequestException or WebSocketException or TaskCanceledException or OperationCanceledException)) {
					Log.StackToFile(e, "discord");
				}
			}

			_dcOnline = false;

			// Waited in short steps, so a new token or the switch turned off doesn't sit out a five-minute backoff.
			DateTime until = DateTime.UtcNow + wait;

			try {
				while ((DateTime.UtcNow < until) && (DiscordToken == token) && DiscordWanted(token)) {
					await Task.Delay(1000, ct).ConfigureAwait(false);
				}
			} catch (OperationCanceledException) {
				return;
			}
		}
	}

	/// <summary>
	/// One connection, from open to closed. Says whether it got as far as online (a later drop then reconnects
	/// quickly, instead of backing off), and how long to wait first when Discord said so.
	/// </summary>
	private static async Task<(bool WasOnline, TimeSpan? Hold)> DiscordSessionAsync(string token, CancellationToken ct) {
		bool resuming = (_dcSessionId.Length > 0) && (_dcResumeUrl.Length > 0);
		string url = _dcResumeUrl;

		if (!resuming) {
			(string? found, TimeSpan? hold) = await DiscordGatewayUrlAsync(token, ct).ConfigureAwait(false);

			if (found == null) {
				return (false, hold);
			}

			url = found;
		}

		using CancellationTokenSource conn = CancellationTokenSource.CreateLinkedTokenSource(ct);
		using ClientWebSocket ws = new();

		using (CancellationTokenSource connecting = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
			connecting.CancelAfter(TimeSpan.FromSeconds(20));
			await ws.ConnectAsync(new Uri(url.TrimEnd('/') + "/?v=10&encoding=json"), connecting.Token).ConfigureAwait(false);
		}

		DiscordLink link = new(ws);
		bool wasOnline = false;
		Task? pump = null;

		try {
			while (!conn.IsCancellationRequested) {
				string? text = await DiscordReceiveAsync(ws, conn.Token).ConfigureAwait(false);

				if (text == null) {
					break;
				}

				using JsonDocument doc = JsonDocument.Parse(text);
				JsonElement root = doc.RootElement;

				if (root.TryGetProperty("s", out JsonElement s) && (s.ValueKind == JsonValueKind.Number)) {
					Interlocked.Exchange(ref _dcSeq, s.GetInt64());
				}

				switch (root.GetProperty("op").GetInt32()) {
					// HELLO: start beating, then say who we are - or carry on where the last connection stopped.
					case 10:
						int interval = root.GetProperty("d").GetProperty("heartbeat_interval").GetInt32();
						pump = Task.Run(() => DiscordPumpAsync(link, interval, token, conn), CancellationToken.None);

						object hello = resuming
							? new { op = 6, d = new { token, session_id = _dcSessionId, seq = DiscordSeq() } }
							: new {
								op = 2,
								d = new {
									token,
									intents = 1,   // GUILDS: enough for the servers to arrive, and not a privileged one
									properties = new Dictionary<string, string> { ["os"] = OperatingSystem.IsWindows() ? "windows" : "linux", ["browser"] = "nocat.farm", ["device"] = "nocat.farm" }
								}
							};
						await link.SendAsync(hello, conn.Token).ConfigureAwait(false);

						break;
					case 11:
						link.Acked = true;

						break;
					// Discord asking for a beat now.
					case 1:
						await link.SendAsync(new { op = 1, d = DiscordSeq() }, conn.Token).ConfigureAwait(false);

						break;
					// RECONNECT: Discord moving us along - resume on a new connection.
					case 7:
						Log.Debug(new Said("discord bot: Discord asked for a reconnect"), "discord");

						return (true, Rng.Seconds(1, 3));
					// INVALID_SESSION: resume if it says we can, otherwise start fresh - after a short wait either way.
					case 9:
						if (!root.TryGetProperty("d", out JsonElement resumable) || (resumable.ValueKind != JsonValueKind.True)) {
							ForgetDiscordSession();
						}

						return (wasOnline, Rng.Seconds(1, 5));
					case 0:
						wasOnline |= OnDiscordDispatch(root, token, ct);

						break;
				}
			}
		} catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
			// the pump ended it: no answer to a heartbeat, or the token or the switch changed
		} catch (WebSocketException e) {
			Log.Failed("Discord bot: connection dropped", e, "discord");
		} finally {
			_dcOnline = false;
			await conn.CancelAsync().ConfigureAwait(false);

			if (pump != null) {
				try {
					await pump.ConfigureAwait(false);
				} catch (Exception e) {
					// it only beats - but anything it didn't expect is a bug worth a line
					Log.Failed("Discord bot: heartbeat", e, "discord");
				}
			}
		}

		ct.ThrowIfCancellationRequested();

		// Turned off or a new token: this session is over for good.
		if ((DiscordToken != token) || !G.DiscordCommands) {
			ForgetDiscordSession();

			return (wasOnline, TimeSpan.Zero);
		}

		int code = ws.CloseStatus is { } status ? (int) status : 0;

		switch (code) {
			// Authentication failed: the token is wrong. Waits for a new one rather than knocking again and again.
			case 4004:
				BadDiscordToken(token, new Said("token rejected - Developer Portal, Bot, Reset Token"));

				return (false, null);
			// Something about the bot itself that trying again won't fix.
			case >= 4010 and <= 4014:
				BadDiscordToken(token, new Said("refused (code {0}) - check the Developer Portal", code));

				return (false, null);
			// Not signed in yet, a lost place in the stream, or a session that ran out: start fresh.
			case 4003 or 4007 or 4009:
				ForgetDiscordSession();

				break;
		}

		if (code != 0) {
			Log.Debug(new Said("discord bot: Discord closed the connection ({0})", code), "discord");
		}

		return (wasOnline || link.Zombie, null);
	}

	private static void BadDiscordToken(string token, Said why) {
		_dcBadToken = token;
		_dcProblem = why;
		ForgetDiscordSession();
		Log.Warn(new Said("Discord bot: {0}", why), "discord");
	}

	/// <summary>Where to connect, from Discord - and whether the day's fresh starts are used up.</summary>
	private static async Task<(string? Url, TimeSpan? Hold)> DiscordGatewayUrlAsync(string token, CancellationToken ct) {
		(HttpStatusCode status, string body) = await DiscordApiAsync(HttpMethod.Get, "/gateway/bot", null, token, ct).ConfigureAwait(false);

		if (status == HttpStatusCode.Unauthorized) {
			BadDiscordToken(token, new Said("token rejected - Developer Portal, Bot, Reset Token"));

			return (null, null);
		}

		if (status != HttpStatusCode.OK) {
			// Asked again with a growing wait: the same answer is said once.
			Log.DebugOnChange("discord:bot", $"discord bot: finding the Gateway got HTTP {(int) status} from discord.com/api/v10/gateway/bot{ErrorField(body, "message")}", "discord");

			return (null, null);
		}

		using JsonDocument d = JsonDocument.Parse(body);

		if (d.RootElement.TryGetProperty("session_start_limit", out JsonElement limit)
			&& limit.TryGetProperty("remaining", out JsonElement remaining) && (remaining.GetInt32() <= 0)) {
			double ms = limit.TryGetProperty("reset_after", out JsonElement reset) ? reset.GetDouble() : 60_000;
			_dcProblem = new Said("too many connects today - retrying in {0} min", (int) Math.Ceiling(ms / 60_000));
			Log.Warn(new Said("Discord bot: {0}", _dcProblem), "discord");

			return (null, TimeSpan.FromMilliseconds(Math.Clamp(ms, 5_000, TimeSpan.FromDays(1).TotalMilliseconds)));
		}

		return (d.RootElement.GetProperty("url").GetString(), null);
	}

	/// <summary>One whole message off the socket (Discord may send it in pieces), or null once Discord closes it.</summary>
	private static async Task<string?> DiscordReceiveAsync(ClientWebSocket ws, CancellationToken ct) {
		byte[] buffer = new byte[16 * 1024];
		using MemoryStream all = new();

		while (true) {
			WebSocketReceiveResult r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);

			if (r.MessageType == WebSocketMessageType.Close) {
				return null;
			}

			all.Write(buffer, 0, r.Count);

			if (all.Length > 64 * 1024 * 1024) {
				throw new InvalidDataException("a Gateway message over 64 MB");
			}

			if (r.EndOfMessage) {
				return Encoding.UTF8.GetString(all.GetBuffer(), 0, (int) all.Length);
			}
		}
	}

	/// <summary>
	/// The heartbeat, and the watch on the settings. Beats every interval Discord gave (the first at a random point
	/// in the first one, as Discord asks); a beat that got no answer means a dead connection, so it ends it and the
	/// loop resumes on a new one.
	/// </summary>
	private static async Task DiscordPumpAsync(DiscordLink link, int intervalMs, string token, CancellationTokenSource conn) {
		DateTime next = DateTime.UtcNow.AddMilliseconds(intervalMs * Random.Shared.NextDouble());

		try {
			while (!conn.IsCancellationRequested) {
				await Task.Delay(1000, conn.Token).ConfigureAwait(false);

				if ((DiscordToken != token) || !G.DiscordCommands) {
					await conn.CancelAsync().ConfigureAwait(false);

					return;
				}

				if (DateTime.UtcNow < next) {
					continue;
				}

				if (!link.Acked) {
					link.Zombie = true;
					Log.Debug(new Said("discord bot: no answer to the last heartbeat - reconnecting"), "discord");
					await conn.CancelAsync().ConfigureAwait(false);

					return;
				}

				link.Acked = false;
				await link.SendAsync(new { op = 1, d = DiscordSeq() }, conn.Token).ConfigureAwait(false);
				next = DateTime.UtcNow.AddMilliseconds(intervalMs);
			}
		} catch (OperationCanceledException) {
			// the connection is over
		} catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException) {
			Log.Failed("Discord bot: sending a heartbeat - reconnecting", e, "discord");
			await conn.CancelAsync().ConfigureAwait(false);
		}
	}

	/// <summary>One open connection: sends go out one at a time (the socket allows no more), and whether the last beat was answered.</summary>
	private sealed class DiscordLink(ClientWebSocket ws) {
		private readonly SemaphoreSlim _sending = new(1, 1);

		public volatile bool Acked = true;
		public volatile bool Zombie;

		public async Task SendAsync(object payload, CancellationToken ct) {
			byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
			await _sending.WaitAsync(ct).ConfigureAwait(false);

			try {
				await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
			} finally {
				_sending.Release();
			}
		}
	}

	/// <summary>An event from Discord. True when it means we're online (READY, RESUMED).</summary>
	private static bool OnDiscordDispatch(JsonElement root, string token, CancellationToken ct) {
		string type = root.TryGetProperty("t", out JsonElement t) ? t.GetString() ?? "" : "";

		if (!root.TryGetProperty("d", out JsonElement d) || (d.ValueKind != JsonValueKind.Object)) {
			return false;
		}

		switch (type) {
			case "READY": {
				_dcSessionId = d.GetProperty("session_id").GetString() ?? "";
				_dcResumeUrl = d.TryGetProperty("resume_gateway_url", out JsonElement resume) ? resume.GetString() ?? "" : "";
				_dcAppId = d.GetProperty("application").GetProperty("id").GetString() ?? "";
				_dcBotName = d.GetProperty("user").GetProperty("username").GetString() ?? "";
				_dcOnline = true;
				_dcProblem = default;

				if (_dcGreetedFor != token) {
					_dcGreetedFor = token;
					Log.Good(new Said("Discord bot online as {0}", _dcBotName), "discord");

					if (G.DiscordOwnerId.Length == 0) {
						Log.Info(new Said("Discord bot: to link it, press Connect Discord in Settings"), "discord");
					}
				}

				string appId = _dcAppId;
				_ = Task.Run(() => RegisterDiscordCommandsAsync(token, appId, null, ct), CancellationToken.None);

				return true;
			}
			case "RESUMED":
				_dcOnline = true;

				return true;
			// Every server the bot is in arrives like this after connecting, and a new one when the bot is added to it.
			case "GUILD_CREATE": {
				if (d.TryGetProperty("id", out JsonElement id) && (id.GetString() is { Length: > 0 } guild) && (_dcAppId.Length > 0)) {
					string appId = _dcAppId;
					_ = Task.Run(() => RegisterDiscordCommandsAsync(token, appId, guild, ct), CancellationToken.None);
				}

				return false;
			}
			case "INTERACTION_CREATE": {
				// Off the read loop: a command can take a while, and the heartbeat answers must keep arriving.
				JsonElement copy = d.Clone();
				_ = Task.Run(() => OnDiscordInteractionAsync(copy, ct), CancellationToken.None);

				return false;
			}
		}

		return false;
	}

	/// <summary>Puts the "/" menu up - for private chats (no server) or one server - unless that exact set is already there.</summary>
	private static async Task RegisterDiscordCommandsAsync(string token, string appId, string? guild, CancellationToken ct) {
		string json = JsonSerializer.Serialize(DiscordCommandSet(global: guild == null));
		string key = guild == null ? appId : $"{appId}/{guild}";
		string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

		if (DiscordRegistered.TryGetValue(key, out string? had) && (had == hash)) {
			return;
		}

		try {
			await DiscordRegisterGate.WaitAsync(ct).ConfigureAwait(false);

			try {
				string path = guild == null ? $"/applications/{appId}/commands" : $"/applications/{appId}/guilds/{guild}/commands";
				(HttpStatusCode status, string body) = await DiscordApiAsync(HttpMethod.Put, path, json, token, ct).ConfigureAwait(false);

				if (status == HttpStatusCode.OK) {
					DiscordRegistered[key] = hash;
				} else if ((guild != null) && (status == HttpStatusCode.Forbidden)) {
					// Added to the server without the commands permission (an older invite link).
					Log.Warn(new Said("Discord bot: no / commands in a server - invite it again"), "discord");
				} else {
					Log.Debug($"discord bot: couldn't add the / commands (HTTP {(int) status} from discord.com/api/v10{path}){ErrorField(body, "message")}", "discord");
				}
			} finally {
				DiscordRegisterGate.Release();
			}
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) {
			Log.Failed("Discord bot: adding the / commands", e, "discord");
		} catch (OperationCanceledException) {
			// closing
		} catch (Exception e) {
			// Run as a fire-and-forget task: anything else would vanish unobserved.
			Log.Failed("Discord bot: adding the / commands", e, "discord");
			Log.StackToFile(e, "discord");
		}
	}

	/// <summary>Discord's REST API. Waits out a "slow down" the way Discord asks (a few times), then hands back what came.</summary>
	private static async Task<(HttpStatusCode Status, string Body)> DiscordApiAsync(HttpMethod method, string path, object? payload, string? token, CancellationToken ct) {
		string? json = payload switch {
			null => null,
			string s => s,
			_ => JsonSerializer.Serialize(payload)
		};

		for (int attempt = 0; ; attempt++) {
			using HttpRequestMessage req = new(method, DiscordApi + path);
			req.Headers.TryAddWithoutValidation("User-Agent", $"DiscordBot (https://github.com/VisaHolder/nocatfarm, {Build.Version})");

			// The interaction replies carry their own token in the address and need no bot token.
			if (token != null) {
				req.Headers.TryAddWithoutValidation("Authorization", "Bot " + token);
			}

			if (json != null) {
				req.Content = new StringContent(json, Encoding.UTF8, "application/json");
			}

			using HttpResponseMessage r = await Http.SendAsync(req, ct).ConfigureAwait(false);
			string body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

			if ((r.StatusCode == HttpStatusCode.TooManyRequests) && (attempt < 3)) {
				await Task.Delay(DiscordRetryAfter(body, r.Headers.RetryAfter?.Delta), ct).ConfigureAwait(false);

				continue;
			}

			return (r.StatusCode, body);
		}
	}

	// ── the commands ────────────────────────────────────────────────────────
	private static async Task OnDiscordInteractionAsync(JsonElement d, CancellationToken ct) {
		try {
			// 2 = a / command. Nothing else is registered, so nothing else should come.
			if (!d.TryGetProperty("type", out JsonElement type) || (type.GetInt32() != 2)) {
				return;
			}

			string id = Text(d, "id");
			string replyToken = Text(d, "token");
			string appId = Text(d, "application_id");

			// In a server the person is under member.user; in a private chat, under user.
			JsonElement user = d.TryGetProperty("member", out JsonElement member) && member.TryGetProperty("user", out JsonElement mu) ? mu
				: d.TryGetProperty("user", out JsonElement u) ? u : default;
			string userId = Text(user, "id");
			string userName = Text(user, "global_name");

			if (userName.Length == 0) {
				userName = Text(user, "username");
			}

			// Private in a server - a reply can hold Steam Guard codes. A private chat is private already, and keeps the history.
			bool inServer = d.TryGetProperty("guild_id", out JsonElement guild) && (guild.ValueKind == JsonValueKind.String);
			int flags = inServer ? Ephemeral : 0;

			JsonElement data = d.GetProperty("data");
			string name = Text(data, "name").ToLowerInvariant();
			Dictionary<string, string> options = [];

			if (data.TryGetProperty("options", out JsonElement list) && (list.ValueKind == JsonValueKind.Array)) {
				foreach (JsonElement o in list.EnumerateArray()) {
					options[Text(o, "name")] = o.TryGetProperty("value", out JsonElement v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText() : "";
				}
			}

			switch (DiscordGate(name, userId, G.DiscordOwnerId)) {
				case DiscordAsk.NotConnected:
					await DiscordReplyAsync(id, replyToken, new Said("Not connected yet. In nocat.farm, open Settings, Notifications, press Connect Discord and send /connect with the code it shows.").ToString(), Ephemeral, ct).ConfigureAwait(false);

					return;
				case DiscordAsk.NotAllowed:
					Log.Debug(new Said("discord bot: {0} isn't the connected Discord account - ignored", userName), "discord");
					await DiscordReplyAsync(id, replyToken, new Said("Only the owner of this nocat.farm can use its commands.").ToString(), Ephemeral, ct).ConfigureAwait(false);

					return;
				case DiscordAsk.Connect:
					await DiscordConnectAsync(id, replyToken, userId, userName, options.GetValueOrDefault("code", ""), flags, ct).ConfigureAwait(false);

					return;
			}

			_dcOwnerName = userName;

			_dcOwnerNameFor = userId;

			string extra = name == "nocat" ? "command" : "args";
			string given = options.GetValueOrDefault(extra, "").Trim();
			string line = name == "nocat" ? given.TrimStart('/') : $"{name} {given}".Trim();

			// Discord wants an answer within three seconds and some commands take longer: "thinking..." now, the
			// real answer in its place when it's ready.
			(HttpStatusCode status, string refused) = await DiscordApiAsync(HttpMethod.Post, $"/interactions/{id}/{replyToken}/callback", new { type = 5, data = new { flags } }, null, ct).ConfigureAwait(false);

			// The address carries the reply's own token, so it isn't written down - only what was being done.
			if ((int) status >= 300) {
				Log.Debug($"discord bot: answering /{name} got HTTP {(int) status}{ErrorField(refused, "message")}", "discord");

				return;
			}

			List<string> messages = await DiscordAnswerAsync(line, $"/{name} {extra}:", ct).ConfigureAwait(false);

			// A runaway answer (a long log) stops at fifteen messages rather than filling the chat.
			for (int i = 0; i < Math.Min(messages.Count, 15); i++) {
				(HttpStatusCode sent, string body) = i == 0
					? await DiscordApiAsync(HttpMethod.Patch, $"/webhooks/{appId}/{replyToken}/messages/@original", new { content = messages[0] }, null, ct).ConfigureAwait(false)
					: await DiscordApiAsync(HttpMethod.Post, $"/webhooks/{appId}/{replyToken}", new { content = messages[i], flags = flags | SuppressEmbeds }, null, ct).ConfigureAwait(false);

				// Otherwise the answer never shows and Discord is left "thinking..." with nothing in the log to say why.
				if ((int) sent >= 300) {
					Log.Debug($"discord bot: posting the answer to /{name} (part {i + 1}) got HTTP {(int) sent}{ErrorField(body, "message")}", "discord");
				}
			}
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			// closing
		} catch (Exception e) {
			Log.Failed("Discord bot: a / command", e, "discord");

			if (e is not (HttpRequestException or TaskCanceledException)) {
				Log.StackToFile(e, "discord");
			}
		}

		static string Text(JsonElement e, string name) =>
			(e.ValueKind == JsonValueKind.Object) && e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.String) ? v.GetString() ?? "" : "";
	}

	/// <summary>An answer straight away, no "thinking..." first - for the quick ones.</summary>
	private static async Task DiscordReplyAsync(string id, string replyToken, string content, int flags, CancellationToken ct) {
		(HttpStatusCode status, string body) = await DiscordApiAsync(HttpMethod.Post, $"/interactions/{id}/{replyToken}/callback",
			new { type = 4, data = new { content, flags = flags | SuppressEmbeds } }, null, ct).ConfigureAwait(false);

		// Not the address: it carries the reply's own token.
		if ((int) status >= 300) {
			Log.Debug($"discord bot: a quick reply got HTTP {(int) status}{ErrorField(body, "message")}", "discord");
		}
	}

	/// <summary>/connect with the dashboard's code: whoever sends it becomes the one Discord account the bot obeys.</summary>
	private static async Task DiscordConnectAsync(string id, string replyToken, string userId, string userName, string code, int flags, CancellationToken ct) {
		if ((userId.Length == 0) || !UseDiscordCode(code)) {
			Log.Debug(new Said("discord bot: a /connect with a wrong or old code from {0}", userName), "discord");
			await DiscordReplyAsync(id, replyToken, new Said("That code doesn't match. In nocat.farm, press Connect Discord for a new one - each code works once, for {0} minutes.", DiscordCodeMinutes).ToString(), Ephemeral, ct).ConfigureAwait(false);

			return;
		}

		G.DiscordOwnerId = userId;
		ConfigStore.SaveGlobal(G);
		_dcOwnerName = userName;
		_dcOwnerNameFor = userId;
		Log.Good(new Said("Discord connected - only {0} can use its commands", userName.Length > 0 ? userName : userId), "discord");

		await DiscordReplyAsync(id, replyToken, $"**{new Said("Connected.")}** {new Said("Send /status for a summary, /help for the commands, or /nocat to run any console command.")}", flags, ct).ConfigureAwait(false);
	}

	/// <summary>
	/// What a command answers, as Discord messages. The same as Telegram: /status, /dashboard and /help styled, the
	/// rest the console's own output in a code block, with the same confirm guard and the same log lines.
	/// </summary>
	private static async Task<List<string>> DiscordAnswerAsync(string line, string how, CancellationToken ct) {
		// Everything shows in the log, the way a command typed in the window does - secrets masked.
		Log.Info(new Said("> /" + Commands.ForLog(line)), "discord");

		int space = line.IndexOf(' ');
		string first = (space < 0 ? line : line[..space]).ToLowerInvariant();
		string rest = space < 0 ? "" : line[(space + 1)..].Trim();

		if (first.Length == 0) {
			return [new Said("Send /status for a summary, /help for the commands, or /nocat to run any console command.").ToString()];
		}

		if (rest.Length == 0) {
			switch (first) {
				case "status" or "s" or "bots":
					return [.. Chunks(StatusMarkdown(), DiscordMessageLimit)];
				case "dashboard" or "web" or "link":
					return [.. Chunks(DashboardMarkdown(), DiscordMessageLimit)];
				case "help" or "?" or "h":
					return [.. Chunks(HelpMarkdown(), DiscordMessageLimit)];
			}
		}

		(string? needs, rest) = ConfirmGuard(first, rest);

		if (needs != null) {
			string typed = $"{how} {first} {rest}".Trim();

			return [(needs == "remove"
				? new Said("That deletes the account and its saved login. Run {0} confirm to really do it.", typed)
				: new Said("That closes nocat.farm, and it can't be started again from Discord. Run {0} confirm to really do it.", typed)).ToString()];
		}

		if (_mgr == null) {
			return [];
		}

		string output;

		try {
			output = await Commands.RunAsync(_mgr, $"{first} {rest}".Trim()).WaitAsync(ct).ConfigureAwait(false);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			// A command throwing is a bug: the stack goes in the file. Which command is the "> /..." line above (secrets masked).
			Log.Failed("Discord bot: running the command", e, "discord");
			Log.StackToFile(e, "discord");
			output = new Said("that command failed: {0}", Log.Scrub(e.Message)).ToString();
		}

		if (string.IsNullOrWhiteSpace(output)) {
			output = new Said("done").ToString();
		}

		EchoToLog(output, "discord");

		return DiscordBlocks(output);
	}

	// ── styled answers: the Telegram look, in Discord's markdown ────────────
	/// <summary>Text that shows as typed - a name with * or _ in it doesn't turn bold or italic.</summary>
	private static string Md(string s) {
		StringBuilder sb = new(s.Length);

		foreach (char c in s) {
			if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>' or '[' or ']' or '(' or ')') {
				sb.Append('\\');
			}

			sb.Append(c);
		}

		return sb.ToString();
	}

	private static string MdHeader() => $"```\n◆ NOCAT.FARM · v{Build.Version} ◆\n```";

	private static string MdSection(Said name) => $"`// {name.ToString().ToUpperInvariant()}`";

	private static string MdRow(Said label, string value) => $"◆ {Md(label.ToString())}: **{Md(value)}**";

	private static string StatusMarkdown() {
		StatusView v = StatusData();
		StringBuilder sb = new();

		sb.AppendLine(MdHeader());
		sb.AppendLine($"▸ **{v.Now:yyyy-MM-dd} · {v.Now:HH:mm}**");
		sb.AppendLine();
		sb.AppendLine(MdSection(new Said("Accounts")));

		foreach ((string name, string state) in v.Accounts) {
			sb.AppendLine($"◆ {Md(name)}: **{Md(state)}**");
		}

		sb.AppendLine();
		sb.AppendLine(MdSection(new Said("Last 24h")));
		sb.AppendLine(MdRow(new Said("Cards"), v.Cards.ToString(System.Globalization.CultureInfo.InvariantCulture)));

		if (v.ShowComments) {
			sb.AppendLine(MdRow(new Said("Comments"), v.Comments.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		}

		sb.AppendLine();
		sb.AppendLine(MdSection(new Said("System")));
		sb.AppendLine(MdRow(new Said("Uptime"), v.Uptime));
		sb.AppendLine(MdRow(new Said("Version"), v.NewVersion is { } nv
			? (SelfUpdate.Supported ? new Said("{0} - {1} is out, run /update with accept", Build.Version, nv) : new Said("{0} - {1} is out, run /update to see how to install it", Build.Version, nv)).ToString()
			: Build.Version));

		return sb.ToString().TrimEnd();
	}

	/// <summary>The dashboard's links: on the same wifi, from anywhere, and on the PC itself. Discord makes them clickable.</summary>
	private static string DashboardMarkdown() {
		DashboardLinks.Links l = DashboardLinks.For(G);
		StringBuilder sb = new();

		sb.AppendLine(MdSection(new Said("Dashboard")));

		foreach (DashboardLinks.Row r in DashboardLinks.Rows(l, RemoteAccess.MinPasswordLength)) {
			string note = r.Note.IsEmpty ? "" : r.Link == null ? Md(r.Note.ToString()) : $"*({Md(r.Note.ToString())})*";
			sb.AppendLine($"◆ {Md(r.Label.ToString())} {(r.Link == null ? "" : $"<{r.Link}> ")}{note}".TrimEnd());
		}

		List<Said> todo = DashboardLinks.Todo(l);

		if (todo.Count > 0) {
			sb.AppendLine(Md(DashboardLinks.TodoHeading.ToString()));
			todo.ForEach(t => sb.AppendLine($"- {Md(t.ToString())}"));
		}

		sb.AppendLine(Md(DashboardLinks.ScanHint.ToString()));

		return sb.ToString().TrimEnd();
	}

	/// <summary>/help: the Discord commands, then every console command, grouped - built from the real list, like Telegram's.</summary>
	private static string HelpMarkdown() {
		StringBuilder sb = new();
		sb.AppendLine(MdHeader());
		sb.AppendLine($"**{new Said("Discord")}:**");
		sb.AppendLine($"/status - {Md(new Said("what every account is doing").ToString())}");
		sb.AppendLine($"/nocat - {Md(new Said("run any console command, like pause kylro 30").ToString())}");
		sb.AppendLine($"/help - {Md(new Said("this list").ToString())}");
		sb.AppendLine(Md(new Said("The others take what goes after them in args - /human args: week, /offers args: kylro.").ToString()));

		// Window-only commands (the mini panel, the dashboard theme, clearing a screen) mean nothing from a phone.
		string[] skip = ["mini", "theme", "tutorial", "help", "clear"];

		foreach (IGrouping<string, CommandDef> group in Commands.All.Where(c => !skip.Contains(c.Name)).GroupBy(static c => c.Group)) {
			if ((group.Key == Commands.GroupRep4Rep) && !G.Rep4RepEnabled) {
				continue;
			}

			sb.AppendLine();
			sb.AppendLine($"**{Md(GroupName(group.Key).ToString())}:**");

			foreach (CommandDef c in group) {
				sb.AppendLine($"`{c.Name}{(c.Args.Length > 0 ? " " + c.Args : "")}`");
			}
		}

		sb.AppendLine();
		sb.Append(Md(new Said("Run any of these with /nocat. Only your connected Discord account is obeyed. exit and remove ask you to confirm first.").ToString()));

		return sb.ToString();
	}
}
