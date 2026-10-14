using System.Globalization;

namespace NocatFarm.Core;

/// <summary>
/// What one look at spare items or booster packs did, told as ONE message - Discord/Telegram's "Spare items &amp; packs"
/// and one pop-up - when the look ends: "14 item(s) into 1,080 gems, 1 listed - you get 0.13 CAD" and the first few items.
/// The log keeps its line for each item as it happens (<see cref="Log.Earned"/>).
/// </summary>
/// <remarks>
/// Each item used to be told on its own: about 20 Telegram messages in 10 minutes. A human-mode look spread over an hour
/// is still one message, sent when it ends.
/// </remarks>
public sealed class ItemsNote(string account) {
	/// <summary>The items named in the message; the rest are "...and N more".</summary>
	public const int Shown = 10;

	private readonly List<Said> _lines = [];
	private int _gemItems, _listed, _packs, _youGetCents, _currency;
	private long _gems;
	private bool _moneyKnown = true;

	/// <summary>Whether there's anything to tell.</summary>
	public bool Any => (_gemItems + _listed + _packs) > 0;

	public void Gems(SpareItem item, int gems) {
		_gemItems++;
		_gems += gems;
		_lines.Add(new Said("{0} ({1}): {2} gems", item.Name, GameNames.Of(item.Game), gems));
	}

	/// <param name="price">What it went up for, as the market writes it ("0.15 CAD").</param>
	/// <param name="cents">What you get, in the wallet's currency - null when not known.</param>
	public void Listed(SpareItem item, string price, (int YouGetCents, int Currency)? cents) {
		_listed++;

		if (cents is { } c) {
			_youGetCents += c.YouGetCents;
			_currency = c.Currency;
		} else {
			_moneyKnown = false;
		}

		_lines.Add(new Said("{0} ({1}): listed for {2}", item.Name, GameNames.Of(item.Game), price));
	}

	public void Opened(string pack, IReadOnlyList<string> cards) {
		_packs++;
		_lines.Add(cards.Count > 0 ? new Said("{0}: {1}", pack, string.Join(", ", cards)) : new Said("{0}", pack));
	}

	/// <summary>The first line: what the look did, in numbers.</summary>
	public Said Head() {
		List<Said> parts = [];

		if (_gemItems > 0) {
			parts.Add(new Said("{0} item(s) into {1} gems", _gemItems, _gems.ToString("N0", CultureInfo.InvariantCulture)));
		}

		if (_listed > 0) {
			parts.Add(_moneyKnown && (_currency > 0) ? new Said("{0} listed - you get {1}", _listed, Spare.Money(_youGetCents, _currency)) : new Said("{0} listed", _listed));
		}

		if (_packs > 0) {
			parts.Add(new Said("opened {0} pack(s)", _packs));
		}

		Said head = parts.Count > 0 ? parts[0] : new Said("");

		foreach (Said p in parts.Skip(1)) {
			head = new Said("{0}, {1}", head, p);
		}

		return head;
	}

	/// <summary>The whole message: the numbers, then the first <see cref="Shown"/> items, one a line.</summary>
	public Said Message() {
		Said text = Head();

		foreach (Said line in _lines.Take(Shown)) {
			text = new Said("{0}\n· {1}", text, line);
		}

		if (_lines.Count > Shown) {
			text = new Said("{0}\n· {1}", text, new Said("...and {0} more", _lines.Count - Shown));
		}

		return text;
	}

	/// <summary>Told once: the pop-up gets the numbers, Discord/Telegram the message. Nothing when nothing happened.</summary>
	public void Send() {
		if (!Any) {
			return;
		}

		Log.Notify?.Invoke(NotifyKind.Earning, account, Head().ToString());
		Log.Publish(Topic.Items, account, Message());
	}
}
