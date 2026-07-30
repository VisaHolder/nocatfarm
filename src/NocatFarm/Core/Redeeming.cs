using System.Globalization;
using SteamKit2;
using SteamKit2.Internal;

namespace NocatFarm.Core;

/// <summary>What Steam said when a key was handed to it.</summary>
public readonly record struct RedeemResult(EPurchaseResultDetail Detail, string Message, List<uint> Packages) {
	public bool Ok => Detail == EPurchaseResultDetail.NoDetail;

	/// <summary>
	/// Worth passing to a different account.
	///
	/// The distinction matters: "already owned" or "region locked" means the key is fine and this account is the
	/// wrong one, so somewhere else should try. "Bad activation code" means the key itself is dead and passing it
	/// around just burns Steam's per-account failure allowance - which is what gets an account locked out of
	/// redeeming entirely for an hour.
	/// </summary>
	public bool WorthAnotherAccount => Detail is EPurchaseResultDetail.AlreadyPurchased
		or EPurchaseResultDetail.RestrictedCountry
		or EPurchaseResultDetail.DoesNotOwnRequiredApp
		or EPurchaseResultDetail.CannotRedeemCodeFromClient

		// These two say nothing about the key - they say this ACCOUNT can't answer right now, which is the
		// clearest possible reason to hand the key to the next one. Stopping the walk here meant one account
		// sitting in Steam's hour-long activation cooldown blocked every other account from trying.
		or EPurchaseResultDetail.RateLimited
		or EPurchaseResultDetail.Timeout;
}

/// <summary>
/// Product keys.
///
/// Steam counts failed activations per account and locks an account out of redeeming for an hour once it has
/// seen too many, so the interesting part is not sending the key - it is knowing when NOT to. A key that came
/// back "already owned" is a good key on the wrong account and should move on to the next one; a key that came
/// back "bad code" is dead, and trying it on five more accounts is how all five get locked out.
/// </summary>
public static class Redeeming {
	/// <summary>Roughly what a Steam key looks like, so obvious rubbish never costs an activation attempt.</summary>
	public static bool LooksLikeKey(string key) {
		if (string.IsNullOrWhiteSpace(key)) {
			return false;
		}

		key = key.Trim();

		// The usual shapes: AAAAA-BBBBB-CCCCC, the longer 5x5, and the unhyphenated 15-30 char run.
		int dashes = key.Count(static c => c == '-');
		int alnum = key.Count(char.IsLetterOrDigit);

		return (alnum >= 15) && (alnum <= 30) && (dashes <= 4) && key.All(static c => char.IsLetterOrDigit(c) || (c == '-'));
	}

	public static async Task<RedeemResult> RedeemAsync(Bot bot, string key, CancellationToken ct = default) {
		if (!bot.IsOnline) {
			return new RedeemResult(EPurchaseResultDetail.Timeout, "not logged in", []);
		}

		ClientMsgProtobuf<CMsgClientRegisterKey> request = new(EMsg.ClientRegisterKey) {
			SourceJobID = bot.Client.GetNextJobID(),
			Body = { key = key.Trim() }
		};

		TaskCompletionSource<RedeemResult> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

		void OnPurchase(SteamApps.PurchaseResponseCallback cb) {
			if (cb.JobID != request.SourceJobID) {
				return;
			}

			List<uint> packages = [];
			string name = "";

			try {
				// The receipt is a key-value blob; the packages are under "lineitems".
				KeyValue items = cb.PurchaseReceiptInfo["lineitems"];

				foreach (KeyValue item in items.Children) {
					uint packageId = item["PackageID"].AsUnsignedInteger();

					if (packageId == 0) {
						packageId = item["packageID"].AsUnsignedInteger();
					}

					if (packageId != 0) {
						packages.Add(packageId);
					}

					string itemName = item["ItemDescription"].AsString() ?? "";

					if (!string.IsNullOrEmpty(itemName) && (name.Length == 0)) {
						name = itemName;
					}
				}
			} catch {
				// A receipt we can't read doesn't change whether it worked.
			}

			answer.TrySetResult(new RedeemResult(cb.PurchaseResultDetail, Describe(cb.PurchaseResultDetail, name), packages));
		}

		using IDisposable subscription = bot.SubscribeCallback<SteamApps.PurchaseResponseCallback>(OnPurchase);
		bot.Client.Send(request);

		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(TimeSpan.FromSeconds(30));

		try {
			await using (timeout.Token.Register(() => answer.TrySetResult(new RedeemResult(EPurchaseResultDetail.Timeout, "Steam didn't answer", []))).ConfigureAwait(false)) {
				return await answer.Task.ConfigureAwait(false);
			}
		} catch (Exception e) {
			return new RedeemResult(EPurchaseResultDetail.Timeout, e.Message, []);
		}
	}

	/// <summary>Steam's result codes in words, because "EPurchaseResultDetail.RateLimited" helps nobody.</summary>
	private static string Describe(EPurchaseResultDetail detail, string itemName) => detail switch {
		EPurchaseResultDetail.NoDetail => string.IsNullOrEmpty(itemName) ? "activated" : $"activated - {itemName}",
		EPurchaseResultDetail.AlreadyPurchased => "this account already owns it",
		EPurchaseResultDetail.BadActivationCode => "that isn't a valid key",
		EPurchaseResultDetail.DuplicateActivationCode => "that key has already been used",
		EPurchaseResultDetail.RestrictedCountry => "the key is locked to another region",
		EPurchaseResultDetail.RateLimited => "Steam has rate-limited this account's activations - it needs about an hour",
		EPurchaseResultDetail.DoesNotOwnRequiredApp => "this account doesn't own the base game the key needs",
		EPurchaseResultDetail.Timeout => "Steam didn't answer in time",
		EPurchaseResultDetail.CancelledByUser => "cancelled",
		_ => detail.ToString()
	};

	/// <summary>
	/// Hand a key to whichever account can actually use it.
	///
	/// Stops the moment one activates it, and stops dead on a bad or used key rather than working through the
	/// whole list - each account that tries a dead key spends one of its own activation attempts on it.
	/// </summary>
	public static async Task<string> RedeemAcrossAsync(IEnumerable<Bot> bots, string key, CancellationToken ct = default) {
		if (!LooksLikeKey(key)) {
			return $"'{key}' doesn't look like a Steam key";
		}

		List<string> lines = [];

		foreach (Bot bot in bots.Where(static b => b.IsOnline)) {
			RedeemResult result = await RedeemAsync(bot, key, ct).ConfigureAwait(false);
			lines.Add($"  {bot.Name,-12} {result.Message}");

			if (result.Ok) {
				if (result.Packages.Count > 0) {
					lines.Add($"               package(s) {string.Join(", ", result.Packages.Select(static p => p.ToString(CultureInfo.InvariantCulture)))}");
				}

				break;
			}

			if (!result.WorthAnotherAccount) {
				break;   // the key is the problem, not the account
			}

			await Task.Delay(Rng.Seconds(3, 8), ct).ConfigureAwait(false);
		}

		return lines.Count == 0 ? "no account is logged in" : $"{key}\n{string.Join('\n', lines)}";
	}
}
