using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NocatFarm.Config;
using SteamKit2;
using SteamKit2.Internal;

namespace NocatFarm.Core;

/// <summary>
/// <c>nocatFarm --steam-selftest</c>: the real network path, on this machine, without an account.
///
/// Built for the release tests, which run it on Windows, Linux (x64, arm64, Docker) and the Mac (arm64, x64). It uses
/// Bot's own pieces - the SteamConfiguration an account connects with (transport, timeout, proxy from the settings in
/// --path) and the games-played message SetPlaying sends - and signs in ANONYMOUSLY, so no account, password or token
/// is ever involved. Then it announces games the way an account's first announce after logon does (an empty message,
/// two seconds, then the custom name first and the real games after it), stays connected for 20 seconds, asks Steam
/// something to prove the session is still being served, clears the games and logs off.
///
/// What anonymous can't show: Steam doesn't keep a playing session for an anonymous user, so it never echoes the game
/// back (no PlayingSessionState, no persona) and no playtime is counted. What it does show: the connection, the
/// sign-in, the message encoded and accepted without Steam dropping the connection, heartbeats, and a clean log-off.
///
/// Exits 0 when everything passed, 1 when anything didn't. Retries a flaky CM and stays under two minutes.
/// </summary>
public static class SteamSelfTest {
	/// <summary>The custom name sent: the one the owner uses, emoji and all, so the UTF-8 path is exercised for real.</summary>
	internal const string Label = "\U0001F480nocat.lol\U0001F480";

	/// <summary>Free games anyone could idle: Counter-Strike 2 and Team Fortress 2.</summary>
	internal static readonly List<uint> Apps = [730, 440];

	private static readonly TimeSpan Budget = TimeSpan.FromSeconds(110);
	private static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(20);
	private const int Attempts = 3;

	/// <summary>The device an account announces with unless it's set otherwise.</summary>
	private static readonly int Device = new BotConfig().GameDevice;

	public static async Task<int> RunAsync() {
		Stopwatch clock = Stopwatch.StartNew();
		using CancellationTokenSource deadline = new(Budget);
		CancellationToken ct = deadline.Token;
		int fails = 0;
		Lock said = new();

		// Called from both transports' threads at once.
		void Say(bool ok, string what) {
			lock (said) {
				fails += ok ? 0 : 1;
				Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}  [{clock.Elapsed.TotalSeconds:0.0}s]");
			}
		}

		// The settings in --path: an account connects with the proxy, transport and timeout set there, so this does too.
		// A folder with none gets the defaults - what a fresh install connects with - and no file is made for them.
		try {
			Live.Global = File.Exists(ConfigStore.GlobalPath) ? ConfigStore.LoadGlobal() : new GlobalConfig();
		} catch (Exception e) {
			Console.WriteLine($"note  settings not read ({e.GetType().Name}) - using the defaults");
			Live.Global = new GlobalConfig();
		}

		GlobalConfig g = Live.Global;
		int chosen = g.SteamProtocol;

		// Both transports "Connect using" offers, each built by Bot exactly as an account's would be. They go through
		// different code on every OS - WebSocket is TLS on port 443 (SChannel, OpenSSL or Apple's stack), TCP is Steam's
		// own encrypted channel (RSA + AES from the OS crypto library) - and the default, "either", ends up on one of them.
		g.SteamProtocol = 1;
		SteamConfiguration webSocket = Bot.BuildSteamConfiguration(new BotConfig());
		g.SteamProtocol = 2;
		SteamConfiguration tcp = Bot.BuildSteamConfiguration(new BotConfig());
		g.SteamProtocol = chosen;
		SteamConfiguration steamConfig = Bot.BuildSteamConfiguration(new BotConfig());

		Console.WriteLine($"nocat.farm {Build.Version} Steam self-test - {RuntimeInformation.OSDescription.Trim()} {RuntimeInformation.OSArchitecture}, "
			+ $".NET {Environment.Version}, SteamKit2 {typeof(SteamClient).Assembly.GetName().Version?.ToString(3)}");
		Console.WriteLine($"      transport set: {steamConfig.ProtocolTypes} (websocket and tcp both tested), connection timeout {steamConfig.ConnectionTimeout.TotalSeconds:0}s, "
			+ $"proxy {(string.IsNullOrWhiteSpace(g.WebProxy) ? "none" : "yes")}, time zone {TimeZoneInfo.Local.Id}");

		// ── the web: HTTPS + TLS on this OS ─────────────────────────────────
		// Both at once, capped at 30s together, so the Steam half keeps most of the budget.
		using CancellationTokenSource webCap = CancellationTokenSource.CreateLinkedTokenSource(ct);
		webCap.CancelAfter(TimeSpan.FromSeconds(30));
		Task<(bool, string)> directory = DirectoryAsync(steamConfig, webCap.Token);
		Task<(bool, string)> store = StoreAsync(webCap.Token);
		(bool dirOk, string dirSaid) = await directory.ConfigureAwait(false);
		Say(dirOk, dirSaid);
		(bool storeOk, string storeSaid) = await store.ConfigureAwait(false);
		Say(storeOk, storeSaid);

		// ── Steam itself, over both transports at once ─────────────────────
		await Task.WhenAll(
			Task.Run(() => SessionAsync(webSocket, "websocket", Say, ct), CancellationToken.None),
			Task.Run(() => SessionAsync(tcp, "tcp", Say, ct), CancellationToken.None)).ConfigureAwait(false);

		Console.WriteLine(fails == 0
			? $"all passed - the Steam connection and idling path work on this machine ({clock.Elapsed.TotalSeconds:0}s)"
			: $"{fails} failed");

		return fails == 0 ? 0 : 1;
	}

	/// <summary>The CM list from Steam's Web API, over HTTPS - what SteamKit fetches before an account's first connect.</summary>
	private static async Task<(bool, string)> DirectoryAsync(SteamConfiguration config, CancellationToken ct) {
		string last = "";

		for (int attempt = 1; (attempt <= 2) && !ct.IsCancellationRequested; attempt++) {
			try {
				IReadOnlyCollection<SteamKit2.Discovery.ServerRecord> servers = await SteamDirectory.LoadAsync(config, 20, ct).ConfigureAwait(false);

				if (servers.Count > 0) {
					return (true, $"web: Steam's server list came over HTTPS from {config.WebAPIBaseAddress.Host} - {servers.Count} servers"
						+ $" ({servers.Count(static s => s.ProtocolTypes.HasFlag(ProtocolTypes.WebSocket))} websocket, {servers.Count(static s => s.ProtocolTypes.HasFlag(ProtocolTypes.Tcp))} tcp)");
				}

				last = "an empty list";
			} catch (Exception e) {
				last = Describe(e);
			}

			await Pause(attempt, ct).ConfigureAwait(false);
		}

		return (false, $"web: Steam's server list didn't come over HTTPS from {config.WebAPIBaseAddress.Host}: {last}");
	}

	/// <summary>The store lookup GameNames makes - the app's own HttpClient, TLS and all.</summary>
	private static async Task<(bool, string)> StoreAsync(CancellationToken ct) {
		string last = "";

		for (int attempt = 1; (attempt <= 3) && !ct.IsCancellationRequested; attempt++) {
			try {
				GameNames.StoreAnswer answer = await GameNames.AskStoreAsync(Apps[0], ct).ConfigureAwait(false);

				if ((answer.Status == 200) && (answer.Name?.Contains("Counter-Strike", StringComparison.Ordinal) == true)) {
					return (true, $"web: the store answered over HTTPS ({answer.From?.Host}) - app {Apps[0]} is \"{answer.Name}\"");
				}

				last = answer.Status == 200 ? $"HTTP 200 but no name (\"{answer.Name}\")" : $"HTTP {answer.Status}";

				// A refusal still came back over TLS, so HTTPS itself is proven - but the name wasn't, so it's retried
				// and then failed: a store that won't answer this machine is exactly what this is here to catch.
			} catch (Exception e) {
				last = Describe(e);
			}

			await Pause(attempt, ct).ConfigureAwait(false);
		}

		return (false, $"web: the store lookup GameNames makes didn't work: {last}");
	}

	/// <summary>Connect, sign in anonymously, announce, hold, prove it's still served, clear and log off.</summary>
	private static async Task SessionAsync(SteamConfiguration config, string transport, Action<bool, string> said, CancellationToken ct) {
		string lastProblem = "";
		int ran = 0;
		void say(bool ok, string what) => said(ok, $"[{transport}] {what}");

		for (int attempt = 1; attempt <= Attempts; attempt++) {
			if (ct.IsCancellationRequested) {
				break;
			}

			// Blocking on purpose: one session's callbacks are pumped on this thread, so nothing in it can race.
			using Session session = new(config);
			ran = attempt;
			SessionResult result = session.Run(attempt, say, ct);

			// Out of time part way: said once, as that. The steps after it used to run on a spent clock and each print a
			// line - a PASS for "still connected 20s later" after no wait at all, FAILs for answers never waited for.
			if (result.OutOfTime) {
				say(false, $"steam: out of time ({Budget.TotalSeconds:0}s in all) {result.Problem} - attempt {attempt} of {Attempts}");

				return;
			}

			if (result.Retry && (attempt < Attempts) && !ct.IsCancellationRequested) {
				Console.WriteLine($"retry  [{transport}] attempt {attempt}: {result.Problem} - trying again");
				lastProblem = result.Problem;
				await Pause(attempt, ct).ConfigureAwait(false);

				continue;
			}

			if (result.Problem.Length > 0) {
				say(false, $"steam: {result.Problem} (attempt {attempt} of {Attempts})");
			}

			return;
		}

		// How many really ran: "after 3 attempts" was said when the time ran out after one.
		say(false, ran == 0 ? $"steam: out of time ({Budget.TotalSeconds:0}s in all) before a first attempt"
			: $"steam: no session after {ran} attempt(s) - {(lastProblem.Length > 0 ? lastProblem + ", then out of time" : "out of time")}");
	}

	/// <param name="OutOfTime">The time budget ran out part way; <paramref name="Problem"/> says where.</param>
	private sealed record SessionResult(bool Retry, string Problem, bool OutOfTime = false);

	/// <summary>One try: its own client, its own callbacks, pumped on this thread so nothing races.</summary>
	private sealed class Session(SteamConfiguration config) : IDisposable {
		private readonly SteamClient _client = new(config);
		private readonly Listener _net = new();
		private CallbackManager? _cb;

		private bool _connected;
		private SteamClient.DisconnectedCallback? _disconnected;
		private SteamUser.LoggedOnCallback? _loggedOn;
		private SteamUser.LoggedOffCallback? _loggedOff;
		private int _playingState;

		public SessionResult Run(int attempt, Action<bool, string> say, CancellationToken ct) {

			// The client an account gets: Bot's configuration, and the handlers Bot adds, so whatever Steam sends is
			// parsed by the same code it would be for an account.
			_client.AddHandler(new NocatHandler());
			_client.AddHandler(new UserStatsHandler());
			_client.DebugNetworkListener = _net;
			_cb = new CallbackManager(_client);
			_cb.Subscribe<SteamClient.ConnectedCallback>(_ => _connected = true);
			_cb.Subscribe<SteamClient.DisconnectedCallback>(d => _disconnected = d);
			_cb.Subscribe<SteamUser.LoggedOnCallback>(l => _loggedOn = l);
			_cb.Subscribe<SteamUser.LoggedOffCallback>(l => _loggedOff = l);
			_cb.Subscribe<SteamUser.PlayingSessionStateCallback>(_ => _playingState++);

			SteamUser user = _client.GetHandler<SteamUser>()!;
			SteamApps apps = _client.GetHandler<SteamApps>()!;

			// ── connect ──
			_client.Connect();

			if (!Pump(() => _connected || (_disconnected != null), TimeSpan.FromSeconds(25), ct) || !_connected) {
				if (ct.IsCancellationRequested && (_disconnected == null)) {
					return OutOfTime("while connecting");
				}

				return new SessionResult(true, _disconnected != null ? "the connection was refused or dropped" : "no connection within 25s");
			}

			string where = _client.CurrentEndPoint switch {
				System.Net.DnsEndPoint dns => $"{dns.Host}:{dns.Port}",
				{ } other => other.ToString() ?? "?",
				null => "?"
			};
			say(true, $"steam: connected to {where} (attempt {attempt})");

			// ── anonymous sign-in ──
			user.LogOnAnonymous(new SteamUser.AnonymousLogOnDetails { ClientLanguage = "english" });

			if (!Pump(() => (_loggedOn != null) || (_disconnected != null), TimeSpan.FromSeconds(20), ct) || (_loggedOn == null)) {
				if (ct.IsCancellationRequested && (_disconnected == null)) {
					return OutOfTime("while signing in");
				}

				return new SessionResult(true, $"no sign-in answer from {where}");
			}

			if (_loggedOn.Result != EResult.OK) {
				// TryAnotherCM / ServiceUnavailable are Steam saying "not this server, not now".
				bool transient = _loggedOn.Result is EResult.TryAnotherCM or EResult.ServiceUnavailable or EResult.Busy or EResult.Timeout;

				return new SessionResult(transient, $"anonymous sign-in refused: {_loggedOn.Result}");
			}

			say(true, $"steam: signed in anonymously as {_client.SteamID?.Render()} (cell {_loggedOn.CellID}, heartbeat every {_loggedOn.OutOfGameSecsPerHeartbeat}s)");

			// ── announce, the way an account's first announce after logon goes (SetPlaying's relaunch path) ──
			_client.Send(Bot.BuildGamesPlayed(null, []));
			Pump(() => _disconnected != null, TimeSpan.FromSeconds(2), ct);

			if (_disconnected != null) {
				return new SessionResult(true, "the connection dropped straight after the sign-in");
			}

			if (ct.IsCancellationRequested) {
				return OutOfTime("before the games-played message", user);
			}

			ClientMsgProtobuf<CMsgClientGamesPlayed> games = Bot.BuildGamesPlayed(Label, Apps, Device);
			CMsgClientGamesPlayed.GamePlayed first = games.Body.games_played[0];
			bool shape = (games.Body.games_played.Count == Apps.Count + 1) && (first.game_id == SteamIds.ShortcutGameId) && (first.game_extra_info == Label)
				&& games.Body.games_played.Skip(1).Select(static p => (uint) p.game_id).SequenceEqual(Apps);
			byte[] wire = games.Serialize();
			bool utf8 = Contains(wire, Encoding.UTF8.GetBytes(Label));
			int sentBefore = _net.Sent(EMsg.ClientGamesPlayedWithDataBlob);

			_client.Send(games);

			say(shape && utf8, $"games-played: {games.Body.games_played.Count} entries sent ({wire.Length} bytes) - custom name \"{Label}\" first"
				+ $" as UTF-8 {Convert.ToHexString(Encoding.UTF8.GetBytes(Label)[..4])}..., then {string.Join(", ", Apps)}"
				+ (shape ? "" : " - WRONG SHAPE") + (utf8 ? "" : " - the name isn't UTF-8 on the wire"));

			// ── hold: nothing drops us ──
			int heartbeatsBefore = _net.Sent(EMsg.ClientHeartBeat);
			int packetsBefore = _net.Received;
			Pump(() => (_disconnected != null) || (_loggedOff != null), HoldFor, ct);

			if ((_disconnected != null) || (_loggedOff != null)) {
				string why = _loggedOff != null ? $"Steam logged it off ({_loggedOff.Result})" : "the connection dropped";

				// The one outcome that could be the message itself: say so, and retry to tell a flaky CM from a real refusal.
				return new SessionResult(true, $"{why} within {HoldFor.TotalSeconds:0}s of the games-played message");
			}

			// Cut short, it didn't hold for the 20 seconds it's about to say it did.
			if (ct.IsCancellationRequested) {
				return OutOfTime($"while holding the connection for {HoldFor.TotalSeconds:0}s", user);
			}

			bool gamesOut = _net.Sent(EMsg.ClientGamesPlayedWithDataBlob) - sentBefore == 1;
			int heartbeats = _net.Sent(EMsg.ClientHeartBeat) - heartbeatsBefore;
			say(gamesOut && _client.IsConnected, $"steam: still connected and signed in {HoldFor.TotalSeconds:0}s later - {heartbeats} heartbeat(s) sent, "
				+ $"{_net.Received - packetsBefore} packet(s) from Steam in that time, playing-state echoes: {_playingState}"
				+ " (none expected - Steam keeps no playing session for an anonymous user)");

			// ── still being served: ask it something ──
			bool answered = false;
			string info = "";

			try {
				Task<AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet> job = apps.PICSGetProductInfo(new SteamApps.PICSRequest(Apps[1]), null, false).ToTask();
				Pump(() => job.IsCompleted || (_disconnected != null), TimeSpan.FromSeconds(15), ct);

				if (job.IsCompletedSuccessfully && job.Result.Results is { } results) {
					answered = results.Any(static r => r.Apps.ContainsKey(Apps[1]) || r.UnknownApps.Contains(Apps[1]));
					info = answered ? $"product info for app {Apps[1]}" : "an answer without the app in it";
				} else {
					info = job.IsFaulted ? Describe(job.Exception!.GetBaseException()) : "no answer within 15s";
				}
			} catch (Exception e) {
				info = Describe(e);
			}

			if (!answered && ct.IsCancellationRequested) {
				return OutOfTime("waiting for Steam to answer a question", user);
			}

			say(answered, $"steam: still answering after the games-played message - {info}");

			// ── stand down and log off, the way an account stops ──
			// On a clock of its own: this is a check, not a wait, and it has to get its 15 seconds even when the budget
			// runs out on the way here - on the budget's, a clean log-off at the last second read as "never closed".
			bool steamClosed = LogOff(user);

			say(_disconnected != null, _disconnected == null ? "steam: logged off, but the connection never closed"
				: $"steam: games cleared, logged off and disconnected cleanly ({(steamClosed ? "Steam closed it after the log-off" : "closed from this side")};"
					+ $" {_net.Received} packet(s) from Steam in all: {_net.ReceivedKinds()})");

			return new SessionResult(false, "");
		}

		/// <summary>
		/// Games cleared, logged off, and the connection closed - by Steam after the log-off, or from this side if it doesn't
		/// within 10 seconds. Its own short clock, never the budget's. True when Steam closed it.
		/// </summary>
		private bool LogOff(SteamUser user) {
			using CancellationTokenSource own = new(TimeSpan.FromSeconds(20));

			_client.Send(Bot.BuildGamesPlayed(null, []));
			user.LogOff();
			Pump(() => _disconnected != null, TimeSpan.FromSeconds(10), own.Token);
			bool steamClosed = _disconnected != null;

			if (!steamClosed) {
				_client.Disconnect();
				Pump(() => _disconnected != null, TimeSpan.FromSeconds(5), own.Token);
			}

			return steamClosed;
		}

		/// <summary>
		/// The time budget ran out <paramref name="where"/>. Signed in by then, it still logs off - quietly, on its own clock:
		/// what's being reported is the time, not the log-off.
		/// </summary>
		private SessionResult OutOfTime(string where, SteamUser? signedIn = null) {
			if (signedIn != null) {
				LogOff(signedIn);
			}

			return new SessionResult(false, where, OutOfTime: true);
		}

		/// <summary>Run callbacks until <paramref name="done"/> or the time is up. True if it came true.</summary>
		private bool Pump(Func<bool> done, TimeSpan max, CancellationToken ct) {
			Stopwatch waited = Stopwatch.StartNew();

			while (!done()) {
				if ((waited.Elapsed >= max) || ct.IsCancellationRequested) {
					return false;
				}

				_cb!.RunWaitCallbacks(TimeSpan.FromMilliseconds(200));
			}

			return true;
		}

		public void Dispose() {
			try {
				if (_client.IsConnected) {
					_client.Disconnect();
				}
			} catch {
				// going anyway
			}
		}
	}

	/// <summary>Counts what goes out and comes in - the same hook Bot's NetLog uses for connection liveness.</summary>
	private sealed class Listener : IDebugNetworkListener {
		private readonly ConcurrentDictionary<EMsg, int> _out = new();
		private readonly ConcurrentDictionary<EMsg, int> _in = new();
		private int _received;

		public int Received => Volatile.Read(ref _received);

		public int Sent(EMsg msg) => _out.TryGetValue(msg, out int n) ? n : 0;

		public string ReceivedKinds() => _in.IsEmpty ? "none"
			: string.Join(", ", _in.OrderByDescending(static kv => kv.Value).Take(4).Select(static kv => $"{kv.Key} x{kv.Value}"));

		public void OnIncomingNetworkMessage(EMsg msgType, byte[] data) {
			Interlocked.Increment(ref _received);
			_in.AddOrUpdate(msgType, 1, static (_, n) => n + 1);
		}

		public void OnOutgoingNetworkMessage(EMsg msgType, byte[] data) => _out.AddOrUpdate(msgType, 1, static (_, n) => n + 1);
	}

	private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

	private static Task Pause(int attempt, CancellationToken ct) =>
		Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

	private static string Describe(Exception e) => e switch {
		HttpRequestException h when h.InnerException is { } inner => $"{h.Message} ({inner.GetType().Name}: {inner.Message})",
		_ => $"{e.GetType().Name}: {e.Message}"
	};
}
