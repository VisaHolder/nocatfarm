using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// The Telegram bot listening as well as talking: commands from your own chat.
///
/// /status is a styled summary (the same boxed-header, // section, ◆ look as the owner's site bot); /console turns
/// the chat into the console, where everything typed runs as a command; and any console command works with a /
/// in front - /start new, /pause kylro, /update accept. Only the connected chat is obeyed - anyone else who finds
/// the bot is ignored - and the two that can't be undone from a phone (exit, remove) ask for "confirm" first.
///
/// The same listener finds the chat in the first place - but only through the private link nocat.farm hands out
/// (t.me/yourbot?start=CODE), never "whoever messages first": with commands on, the connected chat controls every
/// account, and a bot's username is public. It long-polls getUpdates, which Telegram refuses while a webhook owns
/// the bot - that case is said once, plainly, instead of waiting for ever.
/// </summary>
public static partial class Notifier {
	private static BotManager? _mgr;

	/// <summary>Long polls hold a request open ~25s, longer than the sending client's timeout.</summary>
	private static readonly HttpClient PollHttp = new() { Timeout = TimeSpan.FromSeconds(40) };

	private static long _offset;
	private static string _pollingToken = "";
	private static bool _consoleMode;
	private static string _menuSetFor = "";
	private static readonly HashSet<string> _hinted = [];
	private static string _pollerWarnedFor = "";

	/// <summary>
	/// The secret in the connect link. Pressing Start on t.me/yourbot?start=CODE sends "/start CODE", which is the only
	/// message that connects an unconnected bot. New every run, so an old link (or an old message in the backlog)
	/// can't connect anything.
	/// </summary>
	private static readonly string PairCode = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

	/// <summary>The link that connects your chat - null until there's a working bot to link to, or once connected.</summary>
	public static string? TelegramConnectLink =>
		HasTelegramToken && (_botName.Length > 1) && (G.TelegramChatId.Length == 0) ? $"https://t.me/{_botName[1..]}?start={PairCode}" : null;

	public static bool TelegramConnected => HasTelegramToken && (G.TelegramChatId.Length > 0);

	private static async Task PollLoopAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			try {
				// Nothing to listen for: no bot, commands off with a chat already set, or a webhook in the way.
				bool finding = G.TelegramChatId.Length == 0;

				if (!HasTelegramToken || (!finding && !G.TelegramCommands) || (_webhookBusy && (DateTime.UtcNow < _nextDiscover))) {
					await Task.Delay(3000, ct).ConfigureAwait(false);

					continue;
				}

				string token = G.TelegramBotToken;

				if (_pollingToken != token) {
					_pollingToken = token;
					_offset = 0;
				}

				if (!finding && (_menuSetFor != token)) {
					_menuSetFor = token;
					await SetMenuAsync(token, ct).ConfigureAwait(false);
				}

				using HttpResponseMessage r = await PollHttp.GetAsync(
					$"https://api.telegram.org/bot{token}/getUpdates?timeout=25&offset={_offset}&allowed_updates=%5B%22message%22%5D", ct).ConfigureAwait(false);

				// 409 means one of two things. A webhook is set: another program owns this bot's incoming messages for
				// good - sending still works, so a chat ID typed in by hand does too, but listening never will. Or
				// another program is long-polling the same bot right now (often a second copy of nocat.farm): Telegram
				// serves one reader at a time, so back off and try again. Each is said once, plainly.
				if (r.StatusCode == HttpStatusCode.Conflict) {
					string body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

					if (body.Contains("getUpdates request", StringComparison.OrdinalIgnoreCase)) {
						if (_pollerWarnedFor != token) {
							_pollerWarnedFor = token;
							Log.Warn(new Said("notifications: another program is reading this Telegram bot's messages right now (maybe a second copy of nocat.farm) - only one can, so commands may not arrive until it stops"), "telegram");
						}

						await Task.Delay(30_000, ct).ConfigureAwait(false);

						continue;
					}

					_webhookBusy = true;
					_nextDiscover = DateTime.UtcNow.AddMinutes(5);

					if (_webhookWarnedFor != token) {
						_webhookWarnedFor = token;
						Log.Warn(new Said("notifications: this Telegram bot is already connected to something else (a webhook), so nocat.farm can't read its messages - make a new bot with @BotFather just for nocat.farm, or put your chat ID in \"Telegram chat\""), "telegram");
					}

					continue;
				}

				_webhookBusy = false;

				if (!r.IsSuccessStatusCode) {
					await Task.Delay(10_000, ct).ConfigureAwait(false);

					continue;
				}

				using JsonDocument d = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

				foreach (JsonElement u in d.RootElement.GetProperty("result").EnumerateArray()) {
					_offset = u.GetProperty("update_id").GetInt64() + 1;

					if (!u.TryGetProperty("message", out JsonElement msg) || !msg.TryGetProperty("chat", out JsonElement chat)) {
						continue;
					}

					string chatId = chat.GetProperty("id").GetRawText();
					string text = msg.TryGetProperty("text", out JsonElement t) ? t.GetString() ?? "" : "";

					// Sent while nocat.farm was closed: skipped, so a "/stop all" from yesterday doesn't fire this
					// morning. Anything sent since it opened - even while the accounts are still starting - counts.
					if (msg.TryGetProperty("date", out JsonElement sent) && (DateTimeOffset.FromUnixTimeSeconds(sent.GetInt64()) < StartedAt)) {
						continue;
					}

					if (G.TelegramChatId.Length == 0) {
						bool isPrivate = chat.TryGetProperty("type", out JsonElement ty) && (ty.GetString() == "private");

						if (isPrivate && text.Contains(PairCode, StringComparison.Ordinal)) {
							await ConnectAsync(chatId, chat, ct).ConfigureAwait(false);
						} else if (isPrivate && _hinted.Add(chatId)) {
							// Probably the owner, pressing Start from BotFather's link instead of nocat.farm's.
							await ReplyAsync(chatId, Html(new Said("Not connected yet. In nocat.farm, open Settings, Notifications and press Connect Telegram - it brings you back here with the right link.").ToString()), ct).ConfigureAwait(false);
						}

						continue;
					}

					if (chatId != G.TelegramChatId) {
						Log.Debug("telegram: ignored a message from a chat that isn't the connected one", "telegram");

						continue;
					}

					if (G.TelegramCommands && (text.Length > 0)) {
						await HandleAsync(text, ct).ConfigureAwait(false);
					}
				}
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				return;
			} catch (Exception e) {
				Log.Debug(new Said("telegram: {0}", e.Message), "telegram");

				try {
					await Task.Delay(5000, ct).ConfigureAwait(false);
				} catch (OperationCanceledException) {
					return;
				}
			}
		}
	}

	/// <summary>When nocat.farm opened, give or take a few seconds for Telegram's clock.</summary>
	private static readonly DateTimeOffset StartedAt = Process.GetCurrentProcess().StartTime.ToUniversalTime().AddSeconds(-5);

	/// <summary>A one-off answer to a chat that isn't the connected one (so PostTelegramAsync, which goes there, won't do).</summary>
	private static async Task ReplyAsync(string chatId, string html, CancellationToken ct) {
		object payload = new { chat_id = chatId, text = html, parse_mode = "HTML" };
		using StringContent content = new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

		try {
			using HttpResponseMessage r = await Http.PostAsync($"https://api.telegram.org/bot{G.TelegramBotToken}/sendMessage", content, ct).ConfigureAwait(false);
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) {
			// only a hint
		}
	}

	/// <summary>The chat that pressed Start on the connect link becomes the one notifications go to and commands come from.</summary>
	private static async Task ConnectAsync(string chatId, JsonElement chat, CancellationToken ct) {
		string name = chat.TryGetProperty("title", out JsonElement t) ? t.GetString() ?? ""
			: chat.TryGetProperty("first_name", out JsonElement f) ? f.GetString() ?? "" : "";

		G.TelegramChatId = chatId;
		ConfigStore.SaveGlobal(G);
		Log.Good(new Said("notifications: Telegram connected - they'll go to {0}", name.Length > 0 ? name : chatId), "telegram");

		await PostTelegramAsync($"{Header()}\n<b>{Html(new Said("Connected.").ToString())}</b> "
			+ Html(new Said("Notifications you picked will arrive here. Change what gets sent under Settings, Notifications.").ToString())
			+ (G.TelegramCommands ? "\n\n" + Html(new Said("Send /status for a summary, /help for the commands, or /console to type commands like in the window.").ToString()) : ""), ct).ConfigureAwait(false);

		_menuSetFor = "";   // the command menu goes up on the next loop
	}

	/// <summary>The "/" menu in the chat: the everyday commands, so they're a tap away.</summary>
	private static async Task SetMenuAsync(string token, CancellationToken ct) {
		(string Command, Said Description)[] menu = [
			("status", new Said("What every account is doing")),
			("console", new Said("Type commands like in the window")),
			("cards", new Said("Cards left to farm")),
			("human", new Said("What human mode is doing today")),
			("offers", new Said("Live trade offers")),
			("confirmations", new Said("What's waiting to be confirmed")),
			("2fa", new Said("Steam Guard codes")),
			("stats", new Said("Cards and comments by hour")),
			("dashboard", new Said("Open the dashboard on your phone")),
			("update", new Said("Check for an update")),
			("help", new Said("Every command"))
		];

		object payload = new { commands = menu.Select(static m => new { command = m.Command, description = m.Description.ToString() }).ToArray() };
		using StringContent content = new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

		try {
			using HttpResponseMessage r = await Http.PostAsync($"https://api.telegram.org/bot{token}/setMyCommands", content, ct).ConfigureAwait(false);
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) {
			// cosmetic - the commands work without the menu
		}
	}

	private static async Task HandleAsync(string text, CancellationToken ct) {
		string trimmed = text.Trim();

		// Everything the chat sends shows in the log (the window, the dashboard and the log file), the way a command
		// typed in the window does - so what was done from a phone is never invisible at the PC. A secret's value
		// ('/set new SteamPassword ...') is masked, or the log file would hold the password in plain text.
		Log.Info(new Said("> " + Commands.ForLog(trimmed)), "telegram");
		bool slash = trimmed.StartsWith('/');
		string line = slash ? trimmed[1..] : trimmed;
		int space = line.IndexOf(' ');
		string first = (space < 0 ? line : line[..space]).ToLowerInvariant();
		string rest = space < 0 ? "" : line[(space + 1)..].Trim();

		// "/status@mybot" when commands are picked from the menu in a group
		if (first.IndexOf('@') is int at and >= 0) {
			first = first[..at];
		}

		if (slash) {
			switch (first) {
				case "start" or "help" when rest.Length == 0:
					await PostTelegramAsync(HelpHtml(), ct).ConfigureAwait(false);

					return;
				// The connect link pressed again (or an old one): Telegram sends "/start <code>" - that's not an account.
				case "start" when (rest.Length == 16) && rest.All(Uri.IsHexDigit) && (_mgr?.Get(rest) is null):
					await PostTelegramAsync(Html(new Said("Already connected. Send /status for a summary, /help for the commands, or /console to type commands like in the window.").ToString()), ct).ConfigureAwait(false);

					return;
				case "status" when rest.Length == 0:
					await PostTelegramAsync(StatusHtml(), ct).ConfigureAwait(false);

					return;
				// Links to tap, not a code block to copy from.
				case "dashboard" or "web" or "link" when rest.Length == 0:
					await PostTelegramAsync(DashboardHtml(), ct).ConfigureAwait(false);

					return;
				case "console":
					_consoleMode = !_consoleMode;
					await PostTelegramAsync(Html(_consoleMode
						? new Said("Console mode on - type commands like in the window, no / needed. Send /console again to leave.").ToString()
						: new Said("Console mode off.").ToString()), ct).ConfigureAwait(false);

					return;
			}
		} else if (!_consoleMode) {
			await PostTelegramAsync(Html(new Said("Send /status for a summary, /help for the commands, or /console to type commands like in the window.").ToString()), ct).ConfigureAwait(false);

			return;
		}

		// The two that can't be undone from a phone: shutting nocat.farm down (nothing can start it again from here)
		// and deleting an account. Both need "confirm" on the end. Guarded by the command the word REACHES, so an
		// alias ('delete') can't walk past a check written for one spelling ('remove').
		(string? needs, rest) = ConfirmGuard(first, rest);

		if (needs != null) {
			string typed = $"/{first} {rest}".Trim();
			await PostTelegramAsync(Html(needs == "remove"
				? new Said("That deletes the account and its saved login. Send {0} confirm to really do it.", typed).ToString()
				: new Said("That closes nocat.farm, and it can't be started again from Telegram. Send {0} confirm to really do it.", typed).ToString()), ct).ConfigureAwait(false);

			return;
		}

		if (_mgr == null) {
			return;
		}

		string output;

		try {
			output = await Commands.RunAsync(_mgr, $"{first} {rest}".Trim()).ConfigureAwait(false);
		} catch (Exception e) {
			output = new Said("that command failed: {0}", e.Message).ToString();
		}

		if (string.IsNullOrWhiteSpace(output)) {
			output = new Said("done").ToString();
		}

		foreach (string outLine in output.Replace("\r\n", "\n").Split('\n')) {
			if (outLine.Length > 0) {
				Log.Info(new Said("  " + outLine), "telegram");
			}
		}

		// A code block keeps the console's columns lined up; long replies go as several messages.
		foreach (string chunk in Chunks(output, 3500)) {
			await PostTelegramAsync($"<pre>{Html(chunk)}</pre>", ct).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// The confirm guard, shared by Telegram and Discord. Says which command still needs "confirm" on the end (exit or
	/// remove - null when it may run), and what's left once a "confirm" on the end is taken off. Checked on the
	/// command the word REACHES, so an alias ('delete', 'quit') can't walk past it.
	/// </summary>
	internal static (string? Needs, string Args) ConfirmGuard(string first, string rest) {
		string canonical = Commands.Resolve(first)?.Name ?? first;

		if (canonical is not ("exit" or "remove")) {
			return (null, rest);
		}

		return rest.EndsWith("confirm", StringComparison.OrdinalIgnoreCase) ? (null, rest[..^"confirm".Length].Trim()) : (canonical, rest);
	}

	private static IEnumerable<string> Chunks(string text, int size) {
		StringBuilder part = new();

		foreach (string l in text.Replace("\r", "").Split('\n')) {
			if ((part.Length > 0) && (part.Length + l.Length + 1 > size)) {
				yield return part.ToString();
				part.Clear();
			}

			part.Append(part.Length > 0 ? "\n" : "").Append(l.Length > size ? l[..size] : l);
		}

		if (part.Length > 0) {
			yield return part.ToString();
		}
	}

	/// <summary>The boxed header every styled message starts with.</summary>
	private static string Header() => $"<pre>◆ NOCAT.FARM · v{Html(Build.Version)} ◆</pre>";

	private static string Row(Said label, string value) => $"◆ {Html(label.ToString())}: <b>{Html(value)}</b>";

	private static string Section(Said name) => $"<code>// {Html(name.ToString().ToUpperInvariant())}</code>";

	/// <summary>The dashboard's links, tappable: on the same wifi, from anywhere, and on the PC itself.</summary>
	private static string DashboardHtml() {
		DashboardLinks.Links l = DashboardLinks.For(G);
		StringBuilder sb = new();
		static string A(string url) => $"<a href=\"{Html(url)}\">{Html(url)}</a>";

		sb.AppendLine(Section(new Said("Dashboard")));

		if (l.OpenAtHome && (l.Home.Count > 0)) {
			sb.AppendLine($"◆ {Html(new Said("On your phone, on the same wifi:").ToString())} {A(l.Home[0])}");
		} else {
			sb.AppendLine($"◆ {Html(new Said("Not open to other devices yet. On the PC: Settings, Dashboard, Show advanced - set a Dashboard password and put 0.0.0.0 in Listen on, then restart.").ToString())}");
		}

		if (l.FirewallBlocks) {
			sb.AppendLine($"◆ {Html(new Said("Windows Firewall is blocking other devices - on the PC, open the dashboard's Open on your phone and press Allow through Windows Firewall.").ToString())}");
		}

		sb.AppendLine(l.Outside != null
			? $"◆ {Html(new Said("From anywhere:").ToString())} {A(l.Outside)}{(l.OpenAtHome ? "" : " " + Html(new Said("(works once it's open to other devices)").ToString()))}"
			: $"◆ {Html(new Said("From outside your home: not set up (Public address, in the same place).").ToString())}");
		sb.AppendLine($"◆ {Html(new Said("On the PC itself:").ToString())} {A(l.Local)}");

		return sb.ToString();
	}

	/// <summary>What /status says, as plain values - Telegram and Discord each dress it in their own look.</summary>
	private sealed record StatusView(DateTime Now, List<(string Name, string State)> Accounts, int Cards, int Comments, bool ShowComments, string Uptime, string? NewVersion);

	private static StatusView StatusData() {
		List<(string Name, string State)> accounts = [];

		foreach (Bot b in _mgr?.All ?? []) {
			List<string> bits = [Loc.T(Commands.StateWord(b))];

			if (b.IsOnline && !string.IsNullOrWhiteSpace(b.Playing)) {
				bits.Add(b.Playing);
			}

			if (b.CardsRemaining > 0) {
				bits.Add(new Said("{0} cards left", b.CardsRemaining).ToString());
			}

			accounts.Add((b.Name, string.Join(" · ", bits)));
		}

		(int cards, int comments) = Stats.Totals(24);
		TimeSpan up = DateTime.Now - Process.GetCurrentProcess().StartTime;
		string uptime = up.TotalDays >= 1 ? $"{(int) up.TotalDays}d {up.Hours}h {up.Minutes}m" : $"{up.Hours}h {up.Minutes}m";

		return new StatusView(DateTime.Now, accounts, cards, comments, G.Rep4RepEnabled, uptime, UpdateCheck.Available);
	}

	/// <summary>/status: every account in a line, the last 24 hours, and the app itself - no emoji, the site bot's look.</summary>
	private static string StatusHtml() {
		StatusView v = StatusData();
		StringBuilder sb = new();

		sb.AppendLine(Header());
		sb.AppendLine($"▸ <b>{v.Now:yyyy-MM-dd} · {v.Now:HH:mm}</b>");
		sb.AppendLine();
		sb.AppendLine(Section(new Said("Accounts")));

		foreach ((string name, string state) in v.Accounts) {
			sb.AppendLine($"◆ {Html(name)}: <b>{Html(state)}</b>");
		}

		sb.AppendLine();
		sb.AppendLine(Section(new Said("Last 24h")));
		sb.AppendLine(Row(new Said("Cards"), v.Cards.ToString(System.Globalization.CultureInfo.InvariantCulture)));

		if (v.ShowComments) {
			sb.AppendLine(Row(new Said("Comments"), v.Comments.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		}

		sb.AppendLine();
		sb.AppendLine(Section(new Said("System")));
		sb.AppendLine(Row(new Said("Uptime"), v.Uptime));
		sb.AppendLine(Row(new Said("Version"), v.NewVersion is { } nv
			? (SelfUpdate.Supported ? new Said("{0} - {1} is out, send /update accept", Build.Version, nv) : new Said("{0} - {1} is out, send /update to see how to install it", Build.Version, nv)).ToString()
			: Build.Version));

		return sb.ToString().TrimEnd();
	}

	/// <summary>
	/// /help: every command, grouped, with what goes after it - the owner's site bot's layout. Built from the real
	/// command list, so it can't go out of date. Telegram makes each /command tappable.
	/// </summary>
	private static string HelpHtml() {
		StringBuilder sb = new();
		sb.AppendLine(Header());
		sb.AppendLine($"<b>{Html(new Said("Telegram").ToString())}:</b>");
		sb.AppendLine($"/status - {Html(new Said("what every account is doing").ToString())}");
		sb.AppendLine($"/console - {Html(new Said("type commands like in the window").ToString())}");
		sb.AppendLine($"/help - {Html(new Said("this list").ToString())}");

		// Window-only commands (the mini panel, the dashboard theme) mean nothing from a phone.
		string[] skip = ["mini", "theme", "tutorial", "help"];

		foreach (IGrouping<string, CommandDef> group in Commands.All.Where(c => !skip.Contains(c.Name)).GroupBy(static c => c.Group)) {
			if ((group.Key == Commands.GroupRep4Rep) && !G.Rep4RepEnabled) {
				continue;
			}

			sb.AppendLine();
			sb.AppendLine($"<b>{Html(GroupName(group.Key).ToString())}:</b>");

			foreach (CommandDef c in group) {
				sb.AppendLine($"/{c.Name}{(c.Args.Length > 0 ? " " + Html(c.Args) : "")}");
			}
		}

		sb.AppendLine();
		sb.Append(Html(new Said("Only this chat is listened to. exit and remove ask you to confirm first.").ToString()));

		return sb.ToString();
	}

	private static Said GroupName(string group) => group switch {
		Commands.GroupAccounts => new Said("Accounts"),
		Commands.GroupPlaying => new Said("Playing"),
		Commands.GroupCards => new Said("Trading cards"),
		Commands.GroupTrades => new Said("Trades & items"),
		Commands.GroupGuard => new Said("Steam Guard"),
		Commands.GroupAchievements => new Said("Achievements"),
		Commands.GroupFree => new Said("Free stuff & keys"),
		Commands.GroupInfo => new Said("Profile & info"),
		Commands.GroupRep4Rep => new Said("rep4rep"),
		Commands.GroupSettings => new Said("Settings"),
		_ => new Said("The app")
	};
}
