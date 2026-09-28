using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Sends the events you picked to a Discord channel (a webhook) and/or a Telegram chat (your own bot). Commands come
/// in from Telegram (TelegramCommands.cs) and from your own Discord bot (DiscordBot.cs).
///
/// It listens to the same events the tray pop-ups do (<see cref="Log.Published"/>), each tagged with what it's
/// about, and keeps only the kinds switched on under Settings, Notifications. Busy moments are bundled: events land
/// in a queue and go out a few seconds after the first one, grouped by kind and account - three drops in a minute
/// are one message, not three pings.
///
/// Telegram needs a chat to post into. Rather than send people hunting for a chat ID, it watches the bot for the
/// first message anyone sends it and takes that chat, then says hello there.
///
/// Its own problems (a dead webhook, a bad token) are said once in the log as warnings - never as published
/// events, which would feed straight back into this and loop.
/// </summary>
public static partial class Notifier {
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

	/// <summary>Where the bot's picture comes from - the app icon, from the repo.</summary>
	private const string Avatar = "https://raw.githubusercontent.com/VisaHolder/nocatfarm/main/assets/icon.png";

	/// <summary>How long after the first event a batch goes out, so a burst becomes one message.</summary>
	private static readonly TimeSpan BatchWindow = TimeSpan.FromSeconds(8);

	private static readonly ConcurrentQueue<(Topic Topic, string Source, string Text, DateTime At)> Queue = new();
	private static CancellationTokenSource? _stop;
	private static Task? _loop;
	private static Task? _poll;

	// Each problem said once, and again only after the setting changes.
	private static string _discordWarnedFor = "";
	private static string _telegramWarnedFor = "";
	private static DateTime _nextDiscover = DateTime.MinValue;
	private static string _checkedToken = "";

	/// <summary>The bot's @name, from getMe - so "open @yourbot and press Start" can name it.</summary>
	private static string _botName = "";

	/// <summary>
	/// The bot already hands its messages to a webhook (another program - a website, another bot framework), so
	/// Telegram won't let anything else read them and the chat can't be found by itself. Said once per token.
	/// </summary>
	private static bool _webhookBusy;
	private static string _webhookWarnedFor = "";

	private static GlobalConfig G => Live.Global;
	private static bool HasDiscord => G.DiscordWebhookUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
	private static bool HasTelegramToken => G.TelegramBotToken.Contains(':', StringComparison.Ordinal);
	private static bool HasTelegram => HasTelegramToken && (G.TelegramChatId.Length > 0);

	public static void Start(BotManager mgr) {
		_mgr = mgr;
		Log.Published += OnPublished;
		_stop = new CancellationTokenSource();
		_loop = Task.Run(() => LoopAsync(_stop.Token));
		_poll = Task.Run(() => PollLoopAsync(_stop.Token));
		_discord = Task.Run(() => DiscordLoopAsync(_stop.Token));
	}

	/// <summary>On the way out: whatever is still waiting goes now (a few seconds at most), so the last events aren't lost.</summary>
	public static async Task StopAsync() {
		Log.Published -= OnPublished;

		try {
			await FlushAsync(force: true, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
		} catch {
			// best effort
		}

		_stop?.Cancel();
	}

	/// <summary>Is this kind of event switched on?</summary>
	public static bool Wanted(Topic topic) => topic switch {
		Topic.Cards => G.SendCardDrops,
		Topic.FreeStuff => G.SendFreeStuff,
		Topic.Trades => G.SendTrades,
		Topic.Problems => G.SendProblems,
		Topic.Updates => G.SendUpdates,
		Topic.Installs => G.SendInstalls,
		Topic.Summary => G.SendDailySummary,
		Topic.Social => G.SendComments,
		Topic.Achievements => G.SendAchievements,
		Topic.Rep4Rep => G.SendRep4Rep,
		_ => false
	};

	private static void OnPublished(Topic topic, string source, string text) {
		if (!Wanted(topic) || (!HasDiscord && !HasTelegramToken)) {
			return;
		}

		Queue.Enqueue((topic, source, text, DateTime.UtcNow));

		// Something upstream gone wild must not build an endless backlog.
		while (Queue.Count > 300) {
			Queue.TryDequeue(out _);
		}
	}

	private static async Task LoopAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			try {
				await Task.Delay(1000, ct).ConfigureAwait(false);

				if (HasTelegramToken && (G.TelegramBotToken != _checkedToken)) {
					_checkedToken = G.TelegramBotToken;
					await CheckTelegramTokenAsync(ct).ConfigureAwait(false);
				}

				await FlushAsync(force: false, ct).ConfigureAwait(false);
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				return;
			} catch (Exception e) {
				Log.Debug(new Said("notifications: {0}", e.Message));
			}
		}
	}

	private static async Task FlushAsync(bool force, CancellationToken ct) {
		if (!Queue.TryPeek(out var first) || (!force && (DateTime.UtcNow - first.At < BatchWindow))) {
			return;
		}

		List<(Topic Topic, string Source, string Text, DateTime At)> batch = [];

		while (Queue.TryDequeue(out var item)) {
			batch.Add(item);
		}

		// One block per kind and account, in the order they first happened.
		List<Block> blocks = [.. batch
			.GroupBy(static e => (e.Topic, e.Source))
			.Select(static g => new Block(g.Key.Topic, g.Key.Source, [.. g.Select(static e => e.Text)]))];

		if (HasDiscord) {
			await SendDiscordAsync(blocks, ct).ConfigureAwait(false);
		}

		if (HasTelegram) {
			await SendTelegramAsync(blocks, ct).ConfigureAwait(false);
		}
	}

	private sealed record Block(Topic Topic, string Source, List<string> Lines);

	private static (Said Label, int Colour) Look(Topic topic) => topic switch {
		Topic.Cards => (new Said("Cards"), 0xE0A800),
		Topic.FreeStuff => (new Said("Free stuff"), 0x2ECC71),
		Topic.Trades => (new Said("Trades"), 0x3498DB),
		Topic.Problems => (new Said("Needs you"), 0xE74C3C),
		Topic.Updates => (new Said("Update"), 0x8B5CF6),
		Topic.Installs => (new Said("Installing"), 0x8B5CF6),
		Topic.Summary => (new Said("Daily summary"), 0xC8C8C8),
		Topic.Social => (new Said("Profile comment"), 0x1ABC9C),
		Topic.Achievements => (new Said("Achievements"), 0xF1C40F),
		Topic.Rep4Rep => (new Said("rep4rep"), 0x95A5A6),
		_ => (new Said("nocat.farm"), 0xC8C8C8)
	};

	/// <summary>The account an event came from - none for the app's own events.</summary>
	private static string? Account(Block b) =>
		(b.Source.Length > 0) && (b.Source != "nocat.farm") && (b.Source != "report") ? b.Source : null;

	/// <summary>"Cards · kylro" - the account where there is one; the app's own events just say what they are.</summary>
	private static string Title(Block b) {
		(Said label, _) = Look(b.Topic);

		return Account(b) is { } a ? $"{label} · {a}" : label.ToString();
	}

	/// <summary>The lines, capped so one runaway burst stays one readable message.</summary>
	private static List<string> Capped(Block b, int max) {
		if (b.Lines.Count <= max) {
			return b.Lines;
		}

		return [.. b.Lines.Take(max - 1), new Said("...and {0} more", b.Lines.Count - (max - 1)).ToString()];
	}

	// ── Discord ─────────────────────────────────────────────────────────────
	private static async Task<(bool Ok, string Why)> SendDiscordAsync(List<Block> blocks, CancellationToken ct) {
		// Ten embeds a message is Discord's limit.
		foreach (Block[] chunk in blocks.Chunk(10)) {
			object payload = new {
				username = "nocat.farm",
				avatar_url = Avatar,
				embeds = chunk.Select(static b => {
					(_, int colour) = Look(b.Topic);
					string body = b.Topic == Topic.Summary
						? "```\n" + string.Join('\n', b.Lines) + "\n```"
						: string.Join('\n', Capped(b, 15).Select(static l => "◆ " + l));

					return new {
						title = Title(b),
						description = body.Length > 4000 ? body[..4000] + "…" : body,
						color = colour,
						footer = new { text = "nocat.farm " + Build.Version },
						timestamp = DateTime.UtcNow.ToString("o")
					};
				}).ToArray()
			};

			(bool ok, string why) = await PostDiscordAsync(payload, ct).ConfigureAwait(false);

			if (!ok) {
				return (false, why);
			}
		}

		return (true, "");
	}

	private static async Task<(bool Ok, string Why)> PostDiscordAsync(object payload, CancellationToken ct) {
		string url = G.DiscordWebhookUrl;

		for (int attempt = 0; attempt < 2; attempt++) {
			using StringContent content = new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
			using HttpResponseMessage r = await Http.PostAsync(url, content, ct).ConfigureAwait(false);

			if (r.IsSuccessStatusCode) {
				_discordWarnedFor = "";

				return (true, "");
			}

			string body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

			// Too many too fast: Discord says how long to wait. Once, then give up on this batch.
			if ((r.StatusCode == HttpStatusCode.TooManyRequests) && (attempt == 0)) {
				double wait = 2;

				try {
					using JsonDocument d = JsonDocument.Parse(body);

					if (d.RootElement.TryGetProperty("retry_after", out JsonElement ra)) {
						wait = Math.Clamp(ra.GetDouble(), 0.5, 30);
					}
				} catch {
					// the default wait
				}

				await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);

				continue;
			}

			string why = r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized
				? new Said("the Discord webhook link doesn't work any more (HTTP {0}) - copy it again from the channel's Integrations settings", (int) r.StatusCode).ToString()
				: new Said("Discord refused the message (HTTP {0})", (int) r.StatusCode).ToString();

			if (_discordWarnedFor != url) {
				_discordWarnedFor = url;
				Log.Warn(new Said("notifications: {0}", why), "discord");
			}

			return (false, why);
		}

		return (false, new Said("Discord kept saying slow down").ToString());
	}

	// ── Telegram ────────────────────────────────────────────────────────────
	private static string Html(string s) => WebUtility.HtmlEncode(s);

	private static async Task<(bool Ok, string Why)> SendTelegramAsync(List<Block> blocks, CancellationToken ct) {
		// "// CARDS · kylro" then "◆ card dropped in Rust - 2 to go" - the owner's site bot's look, no emoji.
		List<string> parts = [.. blocks.Select(static b => b.Topic == Topic.Summary
			? $"<code>// {Html(Look(b.Topic).Label.ToString().ToUpperInvariant())}</code>\n<pre>{Html(string.Join('\n', b.Lines))}</pre>"
			: $"<code>// {Html(Look(b.Topic).Label.ToString().ToUpperInvariant())}</code>{(Account(b) is { } a ? " · <b>" + Html(a) + "</b>" : "")}\n"
				+ string.Join('\n', Capped(b, 15).Select(static l => "◆ " + Html(l))))];

		// A Telegram message holds 4096 characters; several blocks share one until it's full.
		StringBuilder message = new();

		foreach (string part in parts) {
			if ((message.Length > 0) && (message.Length + part.Length + 2 > 4000)) {
				(bool ok, string why) = await PostTelegramAsync(message.ToString(), ct).ConfigureAwait(false);

				if (!ok) {
					return (false, why);
				}

				message.Clear();
			}

			message.Append(message.Length > 0 ? "\n\n" : "").Append(part.Length > 4000 ? part[..4000] : part);
		}

		return message.Length > 0 ? await PostTelegramAsync(message.ToString(), ct).ConfigureAwait(false) : (true, "");
	}

	private static async Task<(bool Ok, string Why)> PostTelegramAsync(string html, CancellationToken ct) {
		string token = G.TelegramBotToken;
		object payload = new { chat_id = G.TelegramChatId, text = html, parse_mode = "HTML", disable_web_page_preview = true };

		for (int attempt = 0; attempt < 2; attempt++) {
			using StringContent content = new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
			using HttpResponseMessage r = await Http.PostAsync($"https://api.telegram.org/bot{token}/sendMessage", content, ct).ConfigureAwait(false);
			string body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

			if (r.IsSuccessStatusCode) {
				_telegramWarnedFor = "";

				return (true, "");
			}

			if ((r.StatusCode == HttpStatusCode.TooManyRequests) && (attempt == 0)) {
				int wait = 3;

				try {
					using JsonDocument d = JsonDocument.Parse(body);

					if (d.RootElement.TryGetProperty("parameters", out JsonElement p) && p.TryGetProperty("retry_after", out JsonElement ra)) {
						wait = Math.Clamp(ra.GetInt32(), 1, 30);
					}
				} catch {
					// the default wait
				}

				await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);

				continue;
			}

			string why = r.StatusCode == HttpStatusCode.Unauthorized
				? new Said("the Telegram bot token doesn't work - copy it again from @BotFather").ToString()
				: body.Contains("chat not found", StringComparison.OrdinalIgnoreCase)
					? new Said("Telegram can't find that chat - send your bot a message, clear \"Telegram chat\" in Settings and it finds it again").ToString()
					: new Said("Telegram refused the message (HTTP {0})", (int) r.StatusCode).ToString();

			if (_telegramWarnedFor != token + G.TelegramChatId) {
				_telegramWarnedFor = token + G.TelegramChatId;
				Log.Warn(new Said("notifications: {0}", why), "telegram");
			}

			return (false, why);
		}

		return (false, new Said("Telegram kept saying slow down").ToString());
	}

	/// <summary>A new token: say at once if it's no good, rather than when the first card drops.</summary>
	private static async Task CheckTelegramTokenAsync(CancellationToken ct) {
		using HttpResponseMessage r = await Http.GetAsync($"https://api.telegram.org/bot{G.TelegramBotToken}/getMe", ct).ConfigureAwait(false);

		if (r.StatusCode == HttpStatusCode.Unauthorized) {
			Log.Warn("notifications: the Telegram bot token doesn't work - copy it again from @BotFather", "telegram");

			return;
		}

		if (r.IsSuccessStatusCode) {
			try {
				using JsonDocument d = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
				_botName = d.RootElement.GetProperty("result").TryGetProperty("username", out JsonElement u) ? "@" + u.GetString() : "";
			} catch {
				_botName = "";
			}

			if (TelegramConnectLink is { } link) {
				Log.Info(new Said("notifications: Telegram bot {0} found - to connect, open {1} and press Start (or press Connect Telegram in Settings, Notifications)", _botName, link), "telegram");
			}
		}
	}

	/// <summary>
	/// The test button: one message to each place that's set up, right now, whatever is switched on. Says per place
	/// whether it worked, and if not, why.
	/// </summary>
	public static async Task<List<string>> TestAsync(CancellationToken ct = default) {
		List<string> result = [];
		List<string> picked = [.. Enum.GetValues<Topic>().Where(Wanted).Select(static t => Look(t).Label.ToString())];
		string chosen = picked.Count > 0 ? string.Join(", ", picked) : new Said("nothing yet - pick below").ToString();
		string hello = new Said("This is what nocat.farm notifications look like. You'll get: {0}", chosen).ToString();

		if (!HasDiscord && !HasTelegramToken) {
			result.Add(new Said("nothing is set up yet - paste a Discord webhook link or a Telegram bot token first").ToString());

			return result;
		}

		if (HasDiscord) {
			(bool ok, string why) = await PostDiscordAsync(new {
				username = "nocat.farm",
				avatar_url = Avatar,
				embeds = new[] {
					new { title = new Said("nocat.farm is connected").ToString(), description = hello, color = 0x8B5CF6,
						footer = new { text = "nocat.farm " + Build.Version }, timestamp = DateTime.UtcNow.ToString("o") }
				}
			}, ct).ConfigureAwait(false);
			result.Add(ok ? new Said("Discord: sent - check the channel").ToString() : new Said("Discord: didn't work - {0}", why).ToString());
		}

		if (HasTelegramToken) {

			if (G.TelegramChatId.Length == 0) {
				result.Add(_webhookBusy
					? new Said("Telegram: this bot is already connected to something else (a webhook), so its messages can't be read - make a new bot with @BotFather just for nocat.farm, or put your chat ID in \"Telegram chat\" (Show advanced)").ToString()
					: new Said("Telegram: {0} works but isn't connected yet - press Connect Telegram, press Start in Telegram, then test again", _botName.Length > 0 ? _botName : new Said("your bot").ToString()).ToString());
			} else {
				(bool ok, string why) = await PostTelegramAsync($"{Header()}\n<b>{Html(new Said("nocat.farm is connected").ToString())}</b>\n{Html(hello)}", ct).ConfigureAwait(false);
				result.Add(ok ? new Said("Telegram: sent - check the chat").ToString() : new Said("Telegram: didn't work - {0}", why).ToString());
			}
		}

		return result;
	}
}
