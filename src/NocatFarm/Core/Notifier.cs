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

	// Each problem said once, and again only after the setting changes.
	private static string _discordWarnedFor = "";
	private static string _telegramWarnedFor = "";
	private static DateTime _nextDiscover = DateTime.MinValue;
	private static string _checkedToken = "";

	/// <summary>The bot's @name, from getMe - so "open @yourbot and press Start" can name it.</summary>
	private static string _botName = "";
	private static DateTime _nextTokenCheck = DateTime.MinValue;

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
		CancellationToken ct = _stop.Token;
		Background.Loop(new Said("Notifications"), () => LoopAsync(ct));
		Background.Loop(new Said("Telegram commands"), () => PollLoopAsync(ct), "telegram");
		Background.Loop(new Said("Discord bot"), () => DiscordLoopAsync(ct), "discord");
	}

	/// <summary>On the way out: whatever is still waiting goes now (a few seconds at most), so the last events aren't lost.</summary>
	public static async Task StopAsync() {
		Log.Published -= OnPublished;

		try {
			await FlushAsync(force: true, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
		} catch (Exception e) {
			// best effort - but the last events never arriving is worth a line
			Log.Failed("notifications: sending the last batch on the way out", e);
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

				// Until Telegram has answered for this token: one failed look (a blip, a timeout) used to be the only look,
				// and without the bot's name there's no Connect Telegram button until the next start.
				if (HasTelegramToken && (G.TelegramBotToken != _checkedToken) && (DateTime.UtcNow >= _nextTokenCheck)) {
					string token = G.TelegramBotToken;

					if (await CheckTelegramTokenAsync(ct).ConfigureAwait(false)) {
						_checkedToken = token;
					} else {
						_nextTokenCheck = DateTime.UtcNow.AddMinutes(1);
					}
				}

				await FlushAsync(force: false, ct).ConfigureAwait(false);
				Log.Recovered("notify:loop");
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				return;
			} catch (Exception e) {
				// Once a second: the same failure is said once (an hour apart at most), and a real bug gets its stack.
				if (Log.DebugOnChange("notify:loop", $"notifications: {Log.Describe(e)}") && e is not (HttpRequestException or TaskCanceledException)) {
					Log.StackToFile(e);
				}
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

	internal sealed record Block(Topic Topic, string Source, List<string> Lines);

	/// <summary>What a kind of event is called: "Cards", "Needs you", "Daily summary".</summary>
	public static string Label(Topic topic) => Look(topic).Label.ToString();

	private static (Said Label, int Colour) Look(Topic topic) => topic switch {
		Topic.Cards => (new Said("Cards"), 0xE0A800),
		Topic.FreeStuff => (new Said("Free stuff"), 0x2ECC71),
		Topic.Trades => (new Said("Trades"), 0x3498DB),
		Topic.Problems => (new Said("Needs you"), 0xE74C3C),
		Topic.Updates => (new Said("Update"), 0x8B5CF6),
		Topic.Installs => (new Said("Installing"), 0x8B5CF6),
		Topic.Summary => (new Said("Daily summary"), 0xC8C8C8),
		Topic.Social => (new Said("Friends & comments"), 0x1ABC9C),
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
		foreach (List<object> chunk in EmbedChunks(blocks)) {
			object payload = new {
				username = "nocat.farm",
				avatar_url = Avatar,
				embeds = chunk.ToArray()
			};

			(bool ok, string why) = await PostDiscordAsync(payload, ct).ConfigureAwait(false);

			if (!ok) {
				return (false, why);
			}
		}

		return (true, "");
	}

	/// <summary>Discord's limits for one message: ten embeds, and 6000 characters across all of them.</summary>
	private const int EmbedsPerMessage = 10;
	private const int CharsPerMessage = 5800;   // under 6000, with room for Discord counting differently from us

	/// <summary>
	/// The blocks as embeds, split into messages Discord will take. Split on the count alone, a busy batch - several
	/// accounts' blocks of fifteen lines, or the daily summary with a couple of others - went over the 6000 characters
	/// Discord allows in one message, and the whole message and every one after it was refused.
	/// </summary>
	private static List<List<object>> EmbedChunks(IEnumerable<Block> blocks) {
		List<List<object>> chunks = [];
		List<object> current = [];
		int size = 0;
		string footer = "nocat.farm " + Build.Version;

		foreach (Block b in blocks) {
			(_, int colour) = Look(b.Topic);
			// Escaped: a line can be a stranger's words - a profile comment "[free case](https://...)" came up as a link
			// with any text they liked on it. Cut before the code block is closed, so a long summary keeps its closing ```.
			string description = b.Topic == Topic.Summary
				? "```\n" + Fit(string.Join('\n', b.Lines).Replace("```", "`​``", StringComparison.Ordinal), 3900) + "\n```"
				: Fit(string.Join('\n', Capped(b, 15).Select(static l => "◆ " + Md(l))), 4000);
			string title = Title(b);
			int length = title.Length + description.Length + footer.Length;

			if ((current.Count > 0) && ((current.Count >= EmbedsPerMessage) || (size + length > CharsPerMessage))) {
				chunks.Add(current);
				current = [];
				size = 0;
			}

			current.Add(new {
				title,
				description,
				color = colour,
				footer = new { text = footer },
				timestamp = DateTime.UtcNow.ToString("o")
			});
			size += length;
		}

		if (current.Count > 0) {
			chunks.Add(current);
		}

		return chunks;
	}

	private static async Task<(bool Ok, string Why)> PostDiscordAsync(object payload, CancellationToken ct) {
		string url = G.DiscordWebhookUrl;

		for (int attempt = 0; attempt < 2; attempt++) {
			using StringContent content = new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
			(HttpResponseMessage? sent, string error) = await TryPostAsync(url, content, ct).ConfigureAwait(false);

			if (sent == null) {
				if (attempt == 0) {
					await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

					continue;
				}

				return Unreachable(new Said("couldn't reach Discord ({0})", error), url, ref _discordWarnedFor, "discord");
			}

			using HttpResponseMessage r = sent;

			if (r.IsSuccessStatusCode) {
				_discordWarnedFor = "";
				Log.Recovered("notify:discord");

				return (true, "");
			}

			string body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

			// The warning below is said once per webhook; the detail - every different refusal - goes in the file.
			Log.DebugOnChange("notify:discord", $"discord webhook: HTTP {(int) r.StatusCode} from {Log.Where(r.RequestMessage?.RequestUri)}{ErrorField(body, "message")}", "discord");

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
				? new Said("webhook link dead (HTTP {0}) - copy it again from the channel", (int) r.StatusCode).ToString()
				: new Said("Discord refused the message (HTTP {0})", (int) r.StatusCode).ToString();

			if (_discordWarnedFor != url) {
				_discordWarnedFor = url;
				Log.Warn(new Said("notifications: {0}", why), "discord");
			}

			return (false, why);
		}

		return (false, new Said("Discord kept saying slow down").ToString());
	}

	/// <summary>
	/// One POST, or null and why when no answer came at all - no network, or nothing back within 20s. Thrown, that took
	/// the whole batch with it (Telegram's copy too, when only Discord was down), said so only in Debug, and left the
	/// test button blank.
	/// </summary>
	private static async Task<(HttpResponseMessage? Response, string Error)> TryPostAsync(string url, HttpContent content, CancellationToken ct) {
		try {
			return (await Http.PostAsync(url, content, ct).ConfigureAwait(false), "");
		} catch (Exception e) when ((e is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested) {
			// The callers put this in a warning - scrubbed, as the address here can carry the bot token or webhook secret.
			return (null, Log.Scrub(e.Message));
		}
	}

	/// <summary>
	/// For the log: the reason Telegram ("description") or Discord ("message") gave for turning something down,
	/// scrubbed and short - never the raw body.
	/// </summary>
	private static string ErrorField(string body, string field) {
		try {
			using JsonDocument d = JsonDocument.Parse(body);

			if ((d.RootElement.ValueKind == JsonValueKind.Object) && d.RootElement.TryGetProperty(field, out JsonElement v) && (v.ValueKind == JsonValueKind.String)) {
				string said = Log.Scrub(v.GetString());

				return ": " + (said.Length > 150 ? said[..150] : said);
			}
		} catch (JsonException) {
			// not JSON (a proxy's error page) - nothing worth quoting
		}

		return "";
	}

	/// <summary>A Telegram call for the log: the method only, as the bot token rides in the address itself.</summary>
	private static string TelegramWhere(string method) => "api.telegram.org/bot[hidden]/" + method;

	/// <summary>Said once until a message gets through again, not once per batch.</summary>
	private static (bool Ok, string Why) Unreachable(Said why, string key, ref string warnedFor, string source) {
		if (warnedFor != key) {
			warnedFor = key;
			Log.Warn(new Said("notifications: {0}", why), source);
		}

		return (false, why.ToString());
	}

	// ── Telegram ────────────────────────────────────────────────────────────
	private static string Html(string s) => WebUtility.HtmlEncode(s);

	private static async Task<(bool Ok, string Why)> SendTelegramAsync(List<Block> blocks, CancellationToken ct) {
		List<string> parts = [.. blocks.Select(TelegramPart)];

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

			message.Append(message.Length > 0 ? "\n\n" : "").Append(part);
		}

		return message.Length > 0 ? await PostTelegramAsync(message.ToString(), ct).ConfigureAwait(false) : (true, "");
	}

	/// <summary>The most of <paramref name="text"/> that fits in <paramref name="max"/> characters, with "…" when cut.</summary>
	internal static string Fit(string text, int max) => text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";

	/// <summary>
	/// One block as Telegram HTML: "// CARDS · kylro" then "◆ card dropped in Rust - 2 to go" - the owner's site bot's look,
	/// no emoji. Never over 3900 characters, and cut a whole line at a time, before encoding.
	/// </summary>
	/// <remarks>
	/// The finished HTML used to be cut at 4000, which took the closing &lt;/pre&gt; off a big fleet's daily summary (or
	/// halved an &amp;amp;) - Telegram turns the whole message down as HTML it can't read, so none of it arrived.
	/// </remarks>
	internal static string TelegramPart(Block b) {
		bool summary = b.Topic == Topic.Summary;
		string head = $"<code>// {Html(Look(b.Topic).Label.ToString().ToUpperInvariant())}</code>"
			+ (!summary && (Account(b) is { } a) ? " · <b>" + Html(a) + "</b>" : "") + "\n";
		int room = 3900 - head.Length - (summary ? "<pre></pre>".Length : 0);
		StringBuilder body = new();

		foreach (string line in summary ? b.Lines : Capped(b, 15).Select(static l => "◆ " + l)) {
			string encoded = Html(line);
			int left = room - body.Length - (body.Length > 0 ? 1 : 0);

			if (encoded.Length > left) {
				// A line of its own too long for the message is cut short; otherwise it stops at the last whole line.
				if (body.Length == 0) {
					string cut = line;

					while ((cut.Length > 0) && (Html(cut).Length + 1 > left)) {
						cut = cut[..(cut.Length * 9 / 10)];
					}

					body.Append(Html(cut)).Append('…');
				} else if (left >= 2) {
					body.Append("\n…");
				}

				break;
			}

			body.Append(body.Length > 0 ? "\n" : "").Append(encoded);
		}

		return summary ? $"{head}<pre>{body}</pre>" : head + body;
	}

	private static async Task<(bool Ok, string Why)> PostTelegramAsync(string html, CancellationToken ct) {
		string token = G.TelegramBotToken;
		object payload = new { chat_id = G.TelegramChatId, text = html, parse_mode = "HTML", disable_web_page_preview = true };

		for (int attempt = 0; attempt < 2; attempt++) {
			using StringContent content = new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
			(HttpResponseMessage? sent, string error) = await TryPostAsync($"https://api.telegram.org/bot{token}/sendMessage", content, ct).ConfigureAwait(false);

			if (sent == null) {
				if (attempt == 0) {
					await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

					continue;
				}

				return Unreachable(new Said("couldn't reach Telegram ({0})", error), token + G.TelegramChatId, ref _telegramWarnedFor, "telegram");
			}

			using HttpResponseMessage r = sent;
			string body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

			if (r.IsSuccessStatusCode) {
				_telegramWarnedFor = "";
				Log.Recovered("notify:telegram");

				return (true, "");
			}

			// The warning below is said once per bot and chat; the detail - every different refusal - goes in the file.
			Log.DebugOnChange("notify:telegram", $"telegram: HTTP {(int) r.StatusCode} from {TelegramWhere("sendMessage")}{ErrorField(body, "description")}", "telegram");

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
				? new Said("bot token rejected - copy it again from @BotFather").ToString()
				: body.Contains("chat not found", StringComparison.OrdinalIgnoreCase)
					? new Said("chat not found - message your bot, then clear \"Telegram chat\"").ToString()
					: new Said("Telegram refused the message (HTTP {0})", (int) r.StatusCode).ToString();

			if (_telegramWarnedFor != token + G.TelegramChatId) {
				_telegramWarnedFor = token + G.TelegramChatId;
				Log.Warn(new Said("notifications: {0}", why), "telegram");
			}

			return (false, why);
		}

		return (false, new Said("Telegram kept saying slow down").ToString());
	}

	/// <summary>
	/// A new token: say at once if it's no good, rather than when the first card drops. True once Telegram has given
	/// an answer either way; false when it couldn't be asked, so it's asked again.
	/// </summary>
	private static async Task<bool> CheckTelegramTokenAsync(CancellationToken ct) {
		HttpResponseMessage r;

		try {
			r = await Http.GetAsync($"https://api.telegram.org/bot{G.TelegramBotToken}/getMe", ct).ConfigureAwait(false);
		} catch (Exception e) when ((e is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested) {
			// Asked again every minute until it answers: an outage is said once, not sixty times an hour.
			Log.DebugOnChange("telegram:getMe", $"notifications: couldn't reach Telegram to check the bot ({Log.Describe(e)}) - trying again in a minute", "telegram");

			return false;
		}

		using HttpResponseMessage _ = r;
		Log.Recovered("telegram:getMe");

		if (r.StatusCode == HttpStatusCode.Unauthorized) {
			Log.Warn("Telegram token rejected - copy it again from @BotFather", "telegram");

			return true;
		}

		if (!r.IsSuccessStatusCode) {
			string body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
			Log.DebugOnChange("telegram:getMe", $"notifications: checking the bot got HTTP {(int) r.StatusCode} from {TelegramWhere("getMe")}{ErrorField(body, "description")} - trying again in a minute", "telegram");

			return false;
		}

		try {
			using JsonDocument d = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
			_botName = d.RootElement.GetProperty("result").TryGetProperty("username", out JsonElement u) ? "@" + u.GetString() : "";
		} catch (Exception e) {
			// no name: no Connect Telegram button - say why
			Log.Failed("notifications: reading the bot's name from Telegram's getMe", e, "telegram");
			_botName = "";
		}

		if (TelegramConnectLink is { } link) {
			Log.Info(new Said("Telegram bot {0} found - press Connect Telegram in Settings", _botName), "telegram");
			Log.Info(new Said("or open {0} and press Start", link), "telegram");
		}

		return true;
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
