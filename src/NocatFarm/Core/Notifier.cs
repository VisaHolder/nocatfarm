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

		// Then what is already on its way: a command's reply - the "/exit confirm" that closed the app is one, on Telegram
		// and on Discord - and a batch the loop had just taken off the queue. Cancelled straight away they were cut off
		// mid-send: the reply never came and the batch was gone. A few seconds at most - a long command (a '/start all'
		// waiting out the sign-in gaps) doesn't hold closing up.
		await SettleAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

		_stop?.Cancel();
	}

	/// <summary>Batches being sent right now (the loop's, and the last one on the way out).</summary>
	private static int _sending;

	/// <summary>
	/// Waits, up to <paramref name="within"/>, for every batch being sent and every chat command being answered to finish.
	/// True when everything did.
	/// </summary>
	internal static async Task<bool> SettleAsync(TimeSpan within) {
		DateTime until = DateTime.UtcNow + within;

		while ((Volatile.Read(ref _sending) > 0) || (TelegramLane.Pending > 0) || (DiscordLane.Pending > 0)) {
			if (DateTime.UtcNow >= until) {
				return false;
			}

			await Task.Delay(25).ConfigureAwait(false);
		}

		return true;
	}

	/// <summary>
	/// Commands from the Telegram chat, in the order they were sent. Each still runs on its own - see CommandLane -
	/// so a long one doesn't stop the next being read.
	/// </summary>
	internal static readonly CommandLane TelegramLane = new(CommandLane.DetachAfter);

	/// <summary>The same for the Discord bot's / commands.</summary>
	internal static readonly CommandLane DiscordLane = new(CommandLane.DetachAfter);

	/// <summary>Is this kind of event switched on?</summary>
	public static bool Wanted(Topic topic) => topic switch {
		Topic.Cards => G.SendCardDrops,
		Topic.FreeStuff => G.SendFreeStuff,
		Topic.Trades => G.SendTrades,
		Topic.Problems => G.SendProblems,
		Topic.Updates => G.SendUpdates,
		Topic.Installs => G.SendInstalls,
		Topic.Summary => G.SendDailySummary,
		Topic.Weekly => G.WeeklyReport,   // switching the weekly report on is asking for it - it has no separate "send" switch
		Topic.Social => G.SendComments,
		Topic.Achievements => G.SendAchievements,
		Topic.Rep4Rep => G.SendRep4Rep,
		Topic.Security => G.SendSignIns,
		Topic.BreakIn => G.SendBreakIns,
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

		// Counted from before the batch comes off the queue until it has gone, so the way out (SettleAsync) waits for
		// it instead of cancelling it half-sent.
		Interlocked.Increment(ref _sending);

		try {
			await SendBatchAsync(ct).ConfigureAwait(false);
		} finally {
			Interlocked.Decrement(ref _sending);
		}
	}

	private static async Task SendBatchAsync(CancellationToken ct) {
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
		Topic.Weekly => (new Said("Weekly report"), 0xC8C8C8),
		Topic.Social => (new Said("Friends & comments"), 0x1ABC9C),
		Topic.Achievements => (new Said("Achievements"), 0xF1C40F),
		Topic.Rep4Rep => (new Said("rep4rep"), 0x95A5A6),
		Topic.Security => (new Said("Dashboard sign-ins"), 0xE74C3C),
		Topic.BreakIn => (new Said("Break-in attempts"), 0xE74C3C),
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
			// with any text they liked on it. A summary is a card, drawn in the site bot's look (CardMarkdown).
			string description = Cards(b) is { } cards
				? string.Join("\n\n", cards.Select(c => CardMarkdown(c, 4000 / cards.Count)))
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
	/// <summary>
	/// Text for Telegram's HTML: only &amp;, &lt; and &gt; - all it asks to be escaped. WebUtility's encoder turned "·" into
	/// "&amp;#183;" and "'" into "&amp;#39;" as well, which made every summary line several characters longer than what shows,
	/// and the summary was cut far sooner than Telegram's limit needed.
	/// </summary>
	private static string Html(string s) => s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

	/// <summary>How long a piece of Telegram HTML is as Telegram counts it - the text that shows, tags gone and every entity one character.</summary>
	internal static int Shown(string html) => WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", "")).Length;

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
	/// no emoji. A summary is a whole card (<see cref="CardHtml"/>). Never over 3900 characters, and cut a whole line at a
	/// time, before encoding.
	/// </summary>
	/// <remarks>
	/// The finished HTML used to be cut at 4000, which took the closing tag off a big fleet's daily summary (or halved an
	/// &amp;amp;) - Telegram turns the whole message down as HTML it can't read, so none of it arrived.
	/// </remarks>
	internal static string TelegramPart(Block b) {
		if (Cards(b) is { } cards) {
			return string.Join("\n\n", cards.Select(c => CardHtml(c, 3900 / cards.Count)));
		}

		string head = $"<code>// {Html(Look(b.Topic).Label.ToString().ToUpperInvariant())}</code>"
			+ (Account(b) is { } a ? " · <b>" + Html(a) + "</b>" : "") + "\n";
		int room = 3900 - head.Length;
		StringBuilder body = new();

		foreach (string line in Capped(b, 15).Select(static l => "◆ " + l)) {
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

		return head + body;
	}

	// ── the daily and weekly summaries: a card, in the owner's site-bot look ──
	/// <summary>A summary block's cards - null when it isn't a summary, or a line in it isn't a card (then it goes as plain lines).</summary>
	private static List<ReportCard>? Cards(Block b) {
		if (b.Topic is not (Topic.Summary or Topic.Weekly)) {
			return null;
		}

		List<ReportCard> cards = [];

		foreach (string line in b.Lines) {
			if (ReportCard.Read(line) is not { } card) {
				return null;
			}

			cards.Add(card);
		}

		return cards.Count > 0 ? cards : null;
	}

	/// <summary>
	/// A summary as Telegram HTML, the site bot's layout - no table, no code block around the figures:
	/// <code>
	/// ◆  NOCAT.FARM · v1.6.6  ◆                    (boxed)
	/// ▸ <b>Daily summary · 2026-10-01</b> · 05:00 NDT
	///
	/// // ACCOUNTS  ·  last 24h                    (code-coloured)
	/// ◈ kylro: <b>268h played · 8 comments</b>
	/// ◈ old: off
	/// </code>
	/// Every name and value escaped (an account name with &lt; or &amp; in it is just text), and never over
	/// <paramref name="max"/> characters as Telegram counts them (<see cref="Shown"/>) - see <see cref="FitCard"/>.
	/// </summary>
	/// <remarks>The title is in the date line: without it a daily and a weekly summary looked the same on a phone.</remarks>
	internal static string CardHtml(ReportCard card, int max) => FitCard(card, max,
		[Header(), $"▸ <b>{Titled(card, Html)}</b> · {Html(card.Time)}"],
		s => $"<code>// {Html(s.Name.ToString().ToUpperInvariant())}{(s.Note.IsEmpty ? "" : "  ·  " + Html(s.Note.ToString()))}</code>",
		r => {
			string value = Html(r.Value.ToString()) + (r.Alert ? " !" : "");

			return $"◈ {Html(r.Label.ToString())}: {(r.Strong ? $"<b>{value}</b>" : value)}";
		},
		Html, Shown);

	/// <summary>The same card in Discord's markdown: the boxed header as a code block, headings as inline code, figures in bold.</summary>
	internal static string CardMarkdown(ReportCard card, int max) => FitCard(card, max,
		[MdHeader(), $"▸ **{Titled(card, Md)}** · {Md(card.Time)}"],
		// Inside `...` nothing is escaped, so a backtick in a translated heading would end it early - it can't be in one.
		s => $"`// {(s.Name.ToString().ToUpperInvariant() + (s.Note.IsEmpty ? "" : "  ·  " + s.Note)).Replace('`', '\'')}`",
		r => {
			string value = Md(r.Value.ToString()) + (r.Alert ? " !" : "");

			return $"◈ {Md(r.Label.ToString())}: {(r.Strong ? $"**{value}**" : value)}";
		},
		Md, static s => s.Length);

	/// <summary>"Daily summary · 2026-10-01", escaped - or just the date for a card with no title.</summary>
	private static string Titled(ReportCard card, Func<string, string> escape) =>
		card.Title.IsEmpty ? escape(card.Date.ToString()) : $"{escape(card.Title.ToString())} · {escape(card.Date.ToString())}";

	/// <summary>
	/// The card's lines, never over <paramref name="max"/> by <paramref name="measure"/>. Too long, the longest section
	/// gives up rows from its end - all but its last, the "All accounts" line - and says how many with "…and 312 more".
	/// Every other section stays whole, so what needs a look is never what's cut.
	/// </summary>
	/// <remarks>
	/// It used to keep whole lines from the top until the room ran out. With ATTENTION after the accounts, a fleet of
	/// about fifty filled the message and the one line that wanted a look - a ban, an account that can't sign in - was
	/// the part left out. The room was counted on the HTML too, where "·" took six characters, not on what Telegram counts.
	/// </remarks>
	private static string FitCard(ReportCard card, int max, List<string> head, Func<ReportCard.Section, string> heading,
		Func<ReportCard.Row, string> row, Func<string, string> escape, Func<string, int> measure) {
		List<(string Heading, List<string> Rows)> sections = [.. card.Sections.Select(s => (heading(s), s.Rows.Select(row).ToList()))];

		string Draw(int cut, int keep) {
			List<string> lines = [.. head];

			for (int i = 0; i < sections.Count; i++) {
				lines.Add("");
				lines.Add(sections[i].Heading);
				List<string> rows = sections[i].Rows;

				if ((i != cut) || (keep >= rows.Count - 1)) {
					lines.AddRange(rows);

					continue;
				}

				lines.AddRange(rows.Take(keep));
				lines.Add(escape(new Said("…and {0} more", rows.Count - 1 - keep).ToString()));
				lines.Add(rows[^1]);
			}

			return string.Join('\n', lines);
		}

		string whole = Draw(-1, 0);

		if ((measure(whole) <= max) || (sections.Count == 0)) {
			return whole;
		}

		int longest = sections.Select(static (s, i) => (s.Rows.Count, i)).Max().i;

		// As many of its rows as fit: the most whose drawing is in, found by halving.
		int lo = 0, hi = sections[longest].Rows.Count - 2;

		if ((hi < 0) || (measure(Draw(longest, 0)) > max)) {
			return Fitted(whole.Split('\n'), max, measure);
		}

		while (lo < hi) {
			int mid = (lo + hi + 1) / 2;

			if (measure(Draw(longest, mid)) <= max) {
				lo = mid;
			} else {
				hi = mid - 1;
			}
		}

		return Draw(longest, lo);
	}

	/// <summary>As many whole lines as fit in <paramref name="max"/> by <paramref name="measure"/>, with "…" when some were left out.</summary>
	private static string Fitted(IEnumerable<string> lines, int max, Func<string, int> measure) {
		StringBuilder sb = new();
		int size = 0;

		foreach (string line in lines) {
			int more = (sb.Length > 0 ? 1 : 0) + measure(line);

			if (size + more > max - 2) {
				sb.Append("\n…");

				break;
			}

			sb.Append(sb.Length > 0 ? "\n" : "").Append(line);
			size += more;
		}

		return sb.ToString();
	}

	/// <summary>Whether a private Telegram chat is connected - somewhere only the owner reads, for a sign-in code.</summary>
	public static bool CanSendPrivately => HasTelegram;

	/// <summary>One message straight to the Telegram chat, now rather than in the next batch - the dashboard's sign-in code.</summary>
	public static async Task<bool> SendPrivateNowAsync(string text, CancellationToken ct) =>
		HasTelegram && (await PostTelegramAsync(WebUtility.HtmlEncode(text), ct).ConfigureAwait(false)).Ok;

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

/// <summary>
/// Chat commands from one place - the Telegram chat, or the Discord bot - run in the order they were sent.
/// </summary>
/// <remarks>
/// Each command used to get a task of its own the moment it arrived, so two sent a second apart ran side by side and
/// finished in any order: "/pause kylro" then "/resume kylro" could end paused, and "/console" then a plain line could
/// read console mode before the toggle had happened. Now each takes a place in the line as it arrives
/// (<see cref="Take"/>), and runs once the one before it has finished.
///
/// Not a strict queue, though. Some commands genuinely take minutes - '/start all' waits out every sign-in gap - and a
/// '/stop all' sent after one must not wait for it. So a command that has been going longer than
/// <see cref="DetachAfter"/> lets the next one start and carries on by itself: quick ones keep their order, a slow one
/// doesn't hold the rest up.
/// </remarks>
public sealed class CommandLane {
	/// <summary>How long a command holds the next one back before it is left to finish on its own.</summary>
	public static readonly TimeSpan DetachAfter = TimeSpan.FromSeconds(3);

	private readonly TimeSpan _detachAfter;
	private readonly Lock _gate = new();
	private Task _tail = Task.CompletedTask;
	private int _pending;

	public CommandLane(TimeSpan detachAfter) => _detachAfter = detachAfter;

	/// <summary>Commands taken and not finished yet - the way out waits (a little) for these to answer.</summary>
	public int Pending => Volatile.Read(ref _pending);

	/// <summary>A place in the line - taken as the message arrives, on the thread reading them, so in the order sent.</summary>
	public Slot Take() {
		TaskCompletionSource over = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Task before;

		lock (_gate) {
			before = _tail;
			_tail = over.Task;
		}

		Interlocked.Increment(ref _pending);

		return new Slot(this, before, over);
	}

	/// <summary>Take a place and run <paramref name="job"/> in its turn. Returns once the job has finished.</summary>
	public async Task RunAsync(Func<Task> job) {
		Slot slot = Take();

		try {
			await slot.WaitTurnAsync().ConfigureAwait(false);
			await job().ConfigureAwait(false);
		} finally {
			slot.Done();
		}
	}

	/// <summary>One command's place in the line.</summary>
	public sealed class Slot {
		private readonly CommandLane _lane;
		private readonly Task _before;
		private readonly TaskCompletionSource _over;
		private int _done;

		internal Slot(CommandLane lane, Task before, TaskCompletionSource over) {
			_lane = lane;
			_before = before;
			_over = over;
		}

		/// <summary>
		/// Wait for the command before this one to finish (or to have gone on longer than the lane waits), then it's this
		/// one's turn. From here the next one waits for this one the same way.
		/// </summary>
		public async Task WaitTurnAsync() {
			await _before.ConfigureAwait(false);

			// Still going after DetachAfter: the next one may start; this carries on by itself.
			_ = Task.Delay(_lane._detachAfter).ContinueWith(_ => _over.TrySetResult(), CancellationToken.None,
				TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
		}

		/// <summary>
		/// Finished - answered, or turned away without running. Always called, once (a second call does nothing). One
		/// that finishes before its turn came still keeps the next behind the ones before it.
		/// </summary>
		public void Done() {
			if (Interlocked.Exchange(ref _done, 1) != 0) {
				return;
			}

			Interlocked.Decrement(ref _lane._pending);
			_before.ContinueWith(_ => _over.TrySetResult(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
		}
	}
}
