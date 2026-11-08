using NocatFarm.Plugins;

namespace NocatFarm.StressTest;

// Plugins that misbehave on purpose, for tools/release-tests/stress.ps1 (a hidden test copy, no Steam accounts). Each one is
// something a badly written add-on could really do. None of them may take nocat.farm down, hold it still, or make it grow
// without end. What can't be guarded against from inside the process - a Thread or Timer callback that throws, an async void
// started from the plugin's own background thread that throws, a real stack overflow, Environment.Exit - is left out: those
// end any .NET program, and PLUGINS.md says so.

/// <summary>Throws straight away in OnLoadAsync.</summary>
public sealed class ThrowsOnLoad : INocatPlugin {
	public string Name => "stress-throws";
	public string Version => "1.0";
	public Task OnLoadAsync(IPluginHost host, CancellationToken ct) => throw new InvalidOperationException("stress: thrown on load");
}

/// <summary>Blocks its OnLoadAsync for a minute before any await - the start must go on after 15 seconds.</summary>
public sealed class BlocksOnLoad : INocatPlugin {
	public string Name => "stress-blocks";
	public string Version => "1.0";

	public Task OnLoadAsync(IPluginHost host, CancellationToken ct) {
		Thread.Sleep(TimeSpan.FromMinutes(1));
		return Task.CompletedTask;
	}
}

/// <summary>Never finishes loading.</summary>
public sealed class NeverLoads : INocatPlugin {
	public string Name => "stress-never";
	public string Version => "1.0";
	public Task OnLoadAsync(IPluginHost host, CancellationToken ct) => new TaskCompletionSource().Task;
}

/// <summary>Loads, then floods: the log, notifications, settings, events that block, async voids that throw, unobserved
/// tasks that fail, comment posts in a tight loop, a page that's enormous, commands that never answer.</summary>
public sealed class Flood : INocatPlugin {
	private IPluginHost _host = null!;
	private readonly CancellationTokenSource _stop = new();
	private int _ticks;

	public string Name => "stress-flood";
	public string Version => "1.0";

	public Task OnLoadAsync(IPluginHost host, CancellationToken ct) {
		_host = host;

		for (int i = 0; i < 2000; i++) {
			host.AddSetting(new PluginSetting($"Setting{i}", $"Setting {i}", new string('h', 3000), i % 2 == 0 ? PluginSettingKind.Text : PluginSettingKind.Secret, "x"));
			host.AddCommand(i == 0 ? "stress" : $"stress{i}", "stress", "a stress command", Command);
		}

		host.AddCommand("stress-never", "", "", static _ => new TaskCompletionSource<string>().Task);
		host.AddCommand("stress-block", "", "", static _ => {
			Thread.Sleep(5000);
			return Task.FromResult("late");
		});
		host.AddCommand("stress-huge", "", "", static _ => Task.FromResult(new string('x', 5_000_000)));
		host.AddCommand("stress-throw", "", "", static _ => throw new InvalidOperationException("stress: a command that throws"));
		host.AddCommand("stress-void", "", "", _ => {
			Void();
			return Task.FromResult("ok");
		});

		host.AddPage("stress", view => view switch {
			"throw" => throw new InvalidOperationException("stress: a page that throws"),
			"block" => Blocked(),
			"null" => null!,
			_ => Task.FromResult(new PluginPage([.. Enumerable.Range(0, 400).Select(static i => i == 0
				? PluginBlock.Table(["a", "b"], [.. Enumerable.Range(0, 50_000).Select(static r => new PluginRow([r.ToString(), new string('r', 50)], new PluginButton("go", "stress")))])
				: PluginBlock.Line(new string('p', 8000)))]))
		});

		host.AccountOnline += static _ => Thread.Sleep(Timeout.Infinite);
		host.AccountOnline += static async _ => {
			await Task.Yield();
			throw new InvalidOperationException("stress: async handler after its await");
		};

		// Background loops of its own, on the thread pool, each cut off on unload.
		_ = Task.Run(() => LoopAsync(_stop.Token));
		Void();

		return Task.CompletedTask;
	}

	private async Task LoopAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			try {
				await Task.Delay(50, ct).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				return;
			}

			int tick = Interlocked.Increment(ref _ticks);

			// The log and notifications, far past any sane rate.
			for (int i = 0; i < 20; i++) {
				_host.Log($"stress log line {tick}.{i} " + new string('z', tick % 7 == 0 ? 50_000 : 10));
			}

			_host.Notify($"stress notification {tick}");

			// A comment post from an account that isn't there, in parallel, every tick.
			await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => _host.PostProfileCommentAsync("nobody", 76561198000000002, "+rep stress", ct))).ConfigureAwait(false);
			_ = _host.CanComment("nobody");

			// A setting saved, a state saved - over and over.
			_host.SetSetting($"Setting{tick % 50}", $"value {tick}");

			if (tick % 20 == 0) {
				await _host.SaveStateAsync($"{{\"tick\":{tick},\"pad\":\"{new string('s', 10_000)}\"}}").ConfigureAwait(false);
				_ = await _host.LoadStateAsync().ConfigureAwait(false);
			}

			// A task nobody waits for, failing - the app's UnobservedTaskException is said and it goes on.
			if (tick % 40 == 0) {
				_ = Task.Run(static () => throw new InvalidOperationException("stress: unobserved"));
				GC.Collect();
			}

			_ = _host.Accounts.Count;
			_ = await _host.RunCommandAsync("status").ConfigureAwait(false);
		}
	}

	private static async void Void() {
		await Task.Delay(5).ConfigureAwait(false);
		throw new InvalidOperationException("stress: an async void after its await");
	}

	private static Task<PluginPage> Blocked() {
		Thread.Sleep(5000);
		return Task.FromResult(new PluginPage([]));
	}

	private static Task<string> Command(string[] words) => Task.FromResult(string.Join(" ", words));

	public Task OnUnloadAsync() {
		_stop.Cancel();
		Thread.Sleep(Timeout.Infinite);   // and never lets go - closing must not wait on it for more than a few seconds
		return Task.CompletedTask;
	}
}
